using LiteNetLib;
using System;
using System.Collections.Generic;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class NetBody {
    /// <summary>
    /// 双人拥抱交互会话管理器（请求确认、冷却梯度情绪奖励与双人靠拢手臂姿态驱动）。
    /// </summary>
    static class HugSession {
        const float AcceptDistance = 5f;
        const float VisualDistance = GameplayRanges.PlayerInteractDistance;
        const float DurationSeconds = 3f;
        const float RequestCooldown = 1f;
        const double HardLockSeconds = 10.0;
        const double MoodCooldownSeconds = 600.0;
        const float MoodRampStart = 0.7f;
        const float MoodRampFloor = 0.01f;

        static readonly Dictionary<ushort, double> MoodReadyAt = new Dictionary<ushort, double>();
        static readonly Dictionary<ushort, ushort> PendingFrom = new Dictionary<ushort, ushort>();
        static readonly List<Pose> Poses = new List<Pose>();
        static int _velocityFrame = -1;
        static int _armFrame = -1;

        sealed class Pose {
            public Body A;
            public Body B;
            public double Ends;
        }

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict[CustomWireCommands.HugAsk] = (plr, cmd, args) => {
                if (args == null || args.Length < 2 || !ushort.TryParse(args[1], out ushort targetId)) {
                    return;
                }
                if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out ScavPlayer target)) {
                    return;
                }
                Ask(plr, target);
            };
            ClientMain.ClientCustomCommandsDict[CustomWireCommands.HugGo] = (cmd, reader) => {
                reader.Get(out ushort a);
                reader.Get(out ushort b);
                BeginLocal(a, b);
            };
        }

        public static void Request(NetBody self, NetBody other) {
            if (self == null || other == null || self.plr == null || other.plr == null || self == other) {
                return;
            }
            if (!Huggable(self.body) || !Huggable(other.body) || Tied(self, other)) {
                return;
            }
            if (!Near(self.body, other.body, AcceptDistance)) {
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.DoAlert(Lang.Get("hug_far"));
                }
                return;
            }
            if (Net.IsServer) {
                Ask(self.plr, other.plr);
                return;
            }
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(CustomWireCommands.HugAsk + " " + other.plr.playerId.id);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void Ask(ScavPlayer from, ScavPlayer target) {
            if (!Net.IsServer || from == null || target == null || from == target) {
                return;
            }
            if (from.netBody == null || target.netBody == null) {
                return;
            }
            if (!from.ServerPlayerState.Cooldown("hug", RequestCooldown)) {
                from.Server_DoAlertSingle("<key:hug_soon>");
                return;
            }
            if (!Huggable(from.body) || !Huggable(target.body) || Tied(from.netBody, target.netBody)) {
                return;
            }
            if (!Near(from.body, target.body, AcceptDistance)) {
                from.Server_DoAlertSingle("<key:hug_far>");
                return;
            }
            if (InPose(from.body) || InPose(target.body)) {
                from.Server_DoAlertSingle("<key:hug_busy>");
                return;
            }
            ushort targetId = target.playerId.id;
            PendingFrom[targetId] = from.playerId.id;
            ServerMain.PromptPlayer(target, "HUG", "<body:" + from.netBody.netId + ">", "<key:plrint_hug_ask>", (answer) => {
                if (!PendingFrom.TryGetValue(targetId, out ushort stillFrom) || stillFrom != from.playerId.id) {
                    return;
                }
                PendingFrom.Remove(targetId);
                if (answer != 0) {
                    from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId + "> <key:declined>.");
                    return;
                }
                Start(from, target);
            });
        }

        static void Start(ScavPlayer a, ScavPlayer b) {
            if (a == null || b == null || a.netBody == null || b.netBody == null) {
                return;
            }
            if (!Huggable(a.body) || !Huggable(b.body) || !Near(a.body, b.body, AcceptDistance)) {
                a.Server_DoAlertSingle("<key:hug_far>");
                b.Server_DoAlertSingle("<key:hug_far>");
                return;
            }
            if (InPose(a.body) || InPose(b.body)) {
                a.Server_DoAlertSingle("<key:hug_busy>");
                return;
            }
            ApplyMood(a, b);
            BeginLocal(a.playerId.id, b.playerId.id);
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(CustomWireCommands.HugGo);
            writer.Put(a.playerId.id);
            writer.Put(b.playerId.id);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        static void BeginLocal(ushort aId, ushort bId) {
            if (!ScavPlayer.TryGetPlayerFromClientId(aId, out ScavPlayer a) || a.body == null) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(bId, out ScavPlayer b) || b.body == null) {
                return;
            }
            Poses.Add(new Pose {
                A = a.body,
                B = b.body,
                Ends = Time.realtimeSinceStartupAsDouble + DurationSeconds,
            });
            if (a.body.talker != null) {
                List<string> lines = Locale.GetCharacter("traderhugsuccess", a.body.charType);
                if (lines != null && lines.Count > 0) {
                    Talker_Talk_MultiplayerPatch.Force(a.body.talker, lines, null, true, true);
                }
            }
        }

        public static void TickVelocity() {
            if (_velocityFrame == Time.frameCount) {
                return;
            }
            _velocityFrame = Time.frameCount;
            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = Poses.Count - 1; i >= 0; i--) {
                Pose pose = Poses[i];
                if (pose.A == null || pose.B == null || now >= pose.Ends || !Near(pose.A, pose.B, VisualDistance)) {
                    Poses.RemoveAt(i);
                    continue;
                }
                ApplyPose(pose.A, pose.B);
            }
        }

        public static void TickArms() {
            if (_armFrame == Time.frameCount) {
                return;
            }
            _armFrame = Time.frameCount;
            for (int i = 0; i < Poses.Count; i++) {
                Pose pose = Poses[i];
                if (pose.A == null || pose.B == null) {
                    continue;
                }
                ApplyArms(pose.A, pose.B);
            }
        }

        static void ApplyMood(ScavPlayer a, ScavPlayer b) {
            double now = Time.realtimeSinceStartupAsDouble;
            Reward(a.body, MoodScale(a.playerId.id, now));
            Reward(b.body, MoodScale(b.playerId.id, now));
            MoodReadyAt[a.playerId.id] = now;
            MoodReadyAt[b.playerId.id] = now;
            if (a.netBody != null) {
                MedicalSync.Server_QueueSendCharacterHealth(a.netBody, true);
            }
            if (b.netBody != null) {
                MedicalSync.Server_QueueSendCharacterHealth(b.netBody, true);
            }
        }

        static float MoodScale(ushort id, double now) {
            if (!MoodReadyAt.TryGetValue(id, out double started)) {
                return 1f;
            }
            double elapsed = Math.Max(0.0, now - started);
            if (elapsed >= MoodCooldownSeconds) {
                MoodReadyAt.Remove(id);
                return 1f;
            }
            if (elapsed < HardLockSeconds) {
                return 0f;
            }
            float progress = Mathf.Clamp01((float)(elapsed / MoodCooldownSeconds));
            if (progress < MoodRampStart) {
                return 0f;
            }
            if (progress >= 1f) {
                return 1f;
            }
            float ramp = Mathf.Clamp01((progress - MoodRampStart) / (1f - MoodRampStart));
            return Mathf.Lerp(MoodRampFloor, 1f, Mathf.Pow(ramp, 1.6f));
        }

        static void Reward(Body body, float scale) {
            if (body == null || scale <= 0f) {
                return;
            }
            float delta = UnityEngine.Random.Range(1, 6) * scale;
            body.happiness = Mathf.Clamp(body.happiness + delta, -100f, 100f);
        }

        static bool Huggable(Body body) => body != null && body.alive && body.conscious;

        static bool Tied(NetBody a, NetBody b) {
            return a.piggybacking_on == b || b.piggybacking_on == a || a.carrying_person == b || b.carrying_person == a;
        }

        static bool Near(Body a, Body b, float max) {
            if (a == null || b == null) {
                return false;
            }
            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude <= max * max;
        }

        static bool InPose(Body body) {
            for (int i = 0; i < Poses.Count; i++) {
                Pose pose = Poses[i];
                if (pose.A == body || pose.B == body) {
                    return true;
                }
            }
            return false;
        }

        static void ApplyPose(Body a, Body b) {
            Vector2 posA = a.transform.position;
            Vector2 posB = b.transform.position;
            Vector2 direction = posB - posA;
            if (direction.sqrMagnitude < 0.001f) {
                direction = Vector2.right;
            }
            Vector2 normal = direction.normalized;
            Vector2 center = (posA + posB) * 0.5f;
            Vector2 targetA = center - normal * 0.42f;
            Vector2 targetB = center + normal * 0.42f;
            targetA.y = posA.y;
            targetB.y = posB.y;
            a.SetVelocity((targetA - posA) * 7.5f);
            b.SetVelocity((targetB - posB) * 7.5f);
            a.eyeCloseTime = Mathf.Max(a.eyeCloseTime, 0.2f);
            b.eyeCloseTime = Mathf.Max(b.eyeCloseTime, 0.2f);
            a.targetLookPos = new Vector2(posB.x, posB.y + 1f);
            b.targetLookPos = new Vector2(posA.x, posA.y + 1f);
        }

        static void ApplyArms(Body a, Body b) {
            Vector2 direction = b.transform.position - a.transform.position;
            if (direction.sqrMagnitude < 0.001f) {
                direction = Vector2.right;
            }
            Vector2 normal = direction.normalized;
            Vector2 side = new Vector2(-normal.y, normal.x);
            Vector2 aTarget = (Vector2)b.transform.position - normal * 0.18f;
            Vector2 bTarget = (Vector2)a.transform.position + normal * 0.18f;
            ApplyArmPair(a, aTarget + side * 0.22f, aTarget - side * 0.22f);
            ApplyArmPair(b, bTarget - side * 0.22f, bTarget + side * 0.22f);
        }

        static void ApplyArmPair(Body body, Vector2 upperTarget, Vector2 lowerTarget) {
            if (body.limbs == null || body.limbs.Length == 0) {
                return;
            }
            Limb leftForearm = LimbAt(body, (int)LimbIndex.PrimaryForearm);
            Limb leftHand = LimbAt(body, (int)LimbIndex.PrimaryHand);
            Limb rightForearm = LimbAt(body, (int)LimbIndex.SecondaryForearm);
            Limb rightHand = LimbAt(body, (int)LimbIndex.SecondaryHand);
            PullLimb(leftForearm, upperTarget);
            Vector2 leftFrom = leftForearm != null ? (Vector2)leftForearm.transform.position : upperTarget;
            PullLimb(leftHand, Vector2.Lerp(leftFrom, upperTarget, 0.8f));
            PullLimb(rightForearm, lowerTarget);
            Vector2 rightFrom = rightForearm != null ? (Vector2)rightForearm.transform.position : lowerTarget;
            PullLimb(rightHand, Vector2.Lerp(rightFrom, lowerTarget, 0.8f));
        }

        static Limb LimbAt(Body body, int index) {
            if (!AnatomyConstants.IsValidLimbIndex(body, index)) {
                return null;
            }
            Limb limb = body.limbs[index];
            return limb.dismembered ? null : limb;
        }

        static void PullLimb(Limb limb, Vector2 target) {
            if (limb == null) {
                return;
            }
            Vector2 next = Vector2.Lerp(limb.transform.position, target, 0.82f);
            limb.transform.position = new Vector3(next.x, next.y, limb.transform.position.z);
        }
    }
}
