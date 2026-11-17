using CasualtiesTogetherUtils;
using LiteNetLib;
using System.Collections.Generic;
using System.Linq;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class NetBody {
    /// <summary>
    /// Dune 物种啃食队友交互会话（请求确认、昏迷免授权判定、饥饿自动啃食与音效台词同步）。
    /// </summary>
    static class DuneBiteSession {
        const float AskDistance = 5f;
        const float HealthPanelRangeMult = 2f;
        const float RequestCooldown = 0.1f;
        const float AutoHungerMax = 10f;
        const float AutoChance = 0.05f;
        const float HungerGain = 35f;
        const float UnconsciousAmpChance = 0.01f;

        static readonly string[] BiteLines = {
            "Mmmm.. m..",
            "Th-ank...",
            "Fri-end tas-ty....",
        };

        static readonly Dictionary<ushort, ushort> PendingFrom = new Dictionary<ushort, ushort>();

        [CTPostInit]
        static void Init() {
            ServerMain.ServerCustomCommandsDict[CustomWireCommands.DuneBiteAsk] = (plr, cmd, args) => {
                if (args == null || args.Length < 2 || !ushort.TryParse(args[1], out ushort targetId)) {
                    return;
                }
                if (!ScavPlayer.TryGetPlayerFromClientId(targetId, out ScavPlayer target)) {
                    return;
                }
                bool seenUnconscious = args.Length >= 3 && args[2] == "1";
                Ask(plr, target, seenUnconscious);
            };
            ClientMain.ClientCustomCommandsDict[CustomWireCommands.DuneBiteGo] = (cmd, reader) => {
                reader.Get(out ushort duneId);
                reader.Get(out ushort victimId);
                PlayLocalFx(duneId, victimId);
            };
        }

        public static void TryKeybindBite() {
            if (!Net.IsRunning || !Util.IsWorldGenerated()) {
                return;
            }
            ScavPlayer me = ScavPlayer.LocalPlayer;
            if (me == null || me.netBody == null || !IsDune(me.body)) {
                return;
            }
            if (Con.IsConsoleOpen() || (PlayerCamera.main != null && PlayerCamera.main.radialOpen)) {
                return;
            }
            NetBody target = PlayerCamera_HandlePlayerHover_MultiplayerPatch.hoveredPlayerBody;
            if (target == null && UIInGame.interaction_menu_target_body != null) {
                target = UIInGame.interaction_menu_target_body;
            }
            if (target == null || target == me.netBody || !target.IsPlayer) {
                return;
            }
            Request(me.netBody, target);
        }

        public static void Request(NetBody self, NetBody other) {
            if (self == null || other == null || self.plr == null || other.plr == null || self == other) {
                return;
            }
            if (!IsDune(self.body) || !Biteable(other.body)) {
                return;
            }
            if (!Near(self.body, other.body, AskDistance)) {
                if (PlayerCamera.main != null) {
                    PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                }
                return;
            }
            if (Net.IsServer) {
                Ask(self.plr, other.plr, CannotConsent(other.body));
                return;
            }
            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            string ko = CannotConsent(other.body) ? " 1" : " 0";
            writer.Put(CustomWireCommands.DuneBiteAsk + " " + other.plr.playerId.id + ko);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }

        static void Ask(ScavPlayer from, ScavPlayer target, bool seenUnconscious = false) {
            if (!Net.IsServer || from == null || target == null || from == target) {
                return;
            }
            if (from.netBody == null || target.netBody == null || from.ServerPlayerState == null) {
                return;
            }
            if (!from.ServerPlayerState.Cooldown("dune_bite", RequestCooldown)) {
                from.Server_DoAlertSingle("<key:hug_soon>");
                return;
            }
            if (!IsDune(from.body) || !Biteable(target.body)) {
                return;
            }
            if (!Near(from.body, target.body, AskDistance * 1.2f)) {
                from.Server_DoAlertSingle("<key:interact_obstructed>");
                return;
            }
            if (seenUnconscious || CannotConsent(target.body)) {
                ApplyBite(from.netBody, target.netBody);
                return;
            }
            ushort targetId = target.playerId.id;
            PendingFrom[targetId] = from.playerId.id;
            ServerMain.PromptPlayer(target, "DUNE_BITE",
                "<body:" + from.netBody.netId + ">",
                "<key:plrint_bite_ask>",
                (answer) => {
                    if (!PendingFrom.TryGetValue(targetId, out ushort stillFrom) || stillFrom != from.playerId.id) {
                        return;
                    }
                    PendingFrom.Remove(targetId);
                    if (CannotConsent(target.body)) {
                        if (!IsDune(from.body) || !Biteable(target.body) || !Near(from.body, target.body, AskDistance * 1.4f)) {
                            from.Server_DoAlertSingle("<key:interact_obstructed>");
                            return;
                        }
                        ApplyBite(from.netBody, target.netBody);
                        return;
                    }
                    if (answer != 0) {
                        from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId + "> <key:declined>.");
                        return;
                    }
                    if (!IsDune(from.body) || !Biteable(target.body) || !Near(from.body, target.body, AskDistance * 1.4f)) {
                        from.Server_DoAlertSingle("<key:interact_obstructed>");
                        return;
                    }
                    ApplyBite(from.netBody, target.netBody);
                    from.Server_DoAlertSingle("<bodyc:" + target.netBody.netId + "> <key:accepted>.");
                });
        }

        public static void ServerTickAuto() {
            if (!Net.IsServer || !Util.IsWorldGenerated()) {
                return;
            }
            float range = GameplayRanges.PlayerInteractDistance * HealthPanelRangeMult;
            float rangeSqr = range * range;
            foreach (ScavPlayer dunePlr in ScavPlayer.AllLivingPlayers) {
                if (dunePlr == null || dunePlr.netBody == null || dunePlr.body == null) {
                    continue;
                }
                if (!IsDune(dunePlr.body) || dunePlr.body.hunger > AutoHungerMax) {
                    continue;
                }
                if (UnityEngine.Random.value >= AutoChance) {
                    continue;
                }
                NetBody nearest = null;
                float nearestSqr = float.PositiveInfinity;
                Vector2 dunePos = dunePlr.body.transform.position;
                foreach (ScavPlayer other in ScavPlayer.AllLivingPlayers) {
                    if (other == null || other == dunePlr || other.netBody == null || other.body == null) {
                        continue;
                    }
                    if (!Biteable(other.body)) {
                        continue;
                    }
                    float distSqr = ((Vector2)other.body.transform.position - dunePos).sqrMagnitude;
                    if (distSqr <= rangeSqr && distSqr < nearestSqr) {
                        nearestSqr = distSqr;
                        nearest = other.netBody;
                    }
                }
                if (nearest != null) {
                    ApplyBite(dunePlr.netBody, nearest);
                }
            }
        }

        static void ApplyBite(NetBody dune, NetBody victim) {
            if (dune == null || victim == null || dune.body == null || victim.body == null) {
                return;
            }
            Body bitten = victim.body;
            List<Limb> limbs = bitten.limbs.Skip((int)LimbIndex.PrimaryUpperArm).Where(x => x != null && !x.dismembered).ToList();
            if (limbs.Count == 0) {
                return;
            }
            Limb limb = limbs.PickRandom();
            limb.muscleHealth -= UnityEngine.Random.Range(25f, 40f);
            limb.skinHealth -= UnityEngine.Random.Range(12f, 20f);
            limb.bleedAmount += UnityEngine.Random.Range(6f, 12f);
            limb.pain += UnityEngine.Random.Range(37f, 55f);
            bitten.adrenaline += 40f;

            if (!bitten.conscious && UnityEngine.Random.value < UnconsciousAmpChance) {
                List<Limb> extremities = bitten.limbs
                    .Where(x => x != null && !x.dismembered && !x.isAbdomen && !x.isHead && !x.isVital)
                    .ToList();
                if (extremities.Count > 0) {
                    extremities.PickRandom().Dismember();
                }
            }

            dune.body.hunger = Mathf.Clamp(dune.body.hunger + HungerGain, -50f, 125f);

            victim.OnDamageEvent(new BodyDamageEvent(dune, victim, null, 30f));
            MedicalSync.ForceHealth(victim);
            MedicalSync.ForceHealth(dune);

            ushort duneId = dune.plr != null ? dune.plr.playerId.id : (ushort)0;
            ushort victimId = victim.plr != null ? victim.plr.playerId.id : (ushort)0;
            PlayLocalFx(duneId, victimId);

            LiteNetLib.Utils.NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(CustomWireCommands.DuneBiteGo);
            writer.Put(duneId);
            writer.Put(victimId);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
        }

        static void PlayLocalFx(ushort duneId, ushort victimId) {
            if (!ScavPlayer.TryGetPlayerFromClientId(duneId, out ScavPlayer dunePlr) || dunePlr.body == null) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(victimId, out ScavPlayer victimPlr) || victimPlr.body == null) {
                return;
            }
            Body duneBody = dunePlr.body;
            Body victimBody = victimPlr.body;
            victimBody.DoGoreSound();
            Sound.Play("eatFlesh", victimBody.transform.position);
            if (duneBody.talker != null) {
                string line = BiteLines[UnityEngine.Random.Range(0, BiteLines.Length)];
                Talker_Talk_MultiplayerPatch.Force(duneBody.talker, new List<string> { line }, null, true, true);
            }
        }

        static bool IsDune(Body body) => body != null && body.alive && body.charType == 2;

        internal static bool CannotConsent(Body body) {
            if (body == null || !body.alive) {
                return true;
            }
            return body.sleeping || !body.conscious || body.consciousness <= 30f;
        }

        static bool Biteable(Body body) {
            return body != null
                && body.alive
                && body.limbs != null
                && body.limbs.Skip((int)LimbIndex.PrimaryUpperArm).Any(x => x != null && !x.dismembered);
        }

        static bool Near(Body a, Body b, float max) {
            if (a == null || b == null) {
                return false;
            }
            Vector2 delta = (Vector2)a.transform.position - (Vector2)b.transform.position;
            return delta.sqrMagnitude <= max * max;
        }
    }
}
