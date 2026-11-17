using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TMPro;
using UnityEngine;

namespace Together;

public partial class MinigameMPManager {
    private static readonly FieldInfo ShrapnelObjectsField = AccessTools.Field(typeof(ShrapnelMinigame), "objects");
    private static readonly FieldInfo ShrapnelCurrentlyHeldField = AccessTools.Field(typeof(ShrapnelMinigame), "currentlyHeld");

    public class MinigameSession {
        public bool minigame_ended = false;
        public ushort session_id;
        public byte minigame_type;
        public float maxdist = 19f;
        public bool object_required = false;
        public GameObject obj;
        public readonly HashSet<ScavPlayer> involved_players = new HashSet<ScavPlayer>();
        public readonly HashSet<ScavPlayer> all_players_that_ever_were_involved = new HashSet<ScavPlayer>();

        public IReadOnlyList<knetid> InvolvedPlayersClientIds(bool ignorehost = false) {
            List<knetid> ids = involved_players.Select(p => p.playerId).ToList();
            if (ignorehost) {
                ids.Remove(Net.SERVER_CLIENTID);
            }
            return ids;
        }

        public virtual void Init() { }
        public virtual void Update() { }
        public virtual void FixedUpdate() { }
        public virtual void OnPlayerJoin(ScavPlayer plr) { }
    }

    public abstract class _LimbMedicalMinigameSession : MinigameSession {
        public Limb limb => obj != null ? obj.GetComponent<Limb>() : null;

        public override void Init() {
            object_required = true;
            maxdist = SharedMain.max_player_interaction_distance * 1.7f;
        }
    }

    public class ShrapnelMinigameSession : _LimbMedicalMinigameSession {
        public readonly List<Vector2> shrapnel_locations = new List<Vector2> {
            new Vector2(-248f, -316.85f),
            new Vector2(-143f, -316.85f),
            new Vector2(0f, -316.85004f),
            new Vector2(116f, -316.85004f),
            new Vector2(208f, -316.85f),
        };

        public readonly List<(double, ScavPlayer)> last_shrapnel_movement = new List<(double, ScavPlayer)>();
        private float _syncTimer = 0f;

        public static List<ScavPlayer> client_shrapnel_owners = new List<ScavPlayer> { null, null, null, null, null };

        public override void Init() {
            base.Init();
            int missingCount = shrapnel_locations.Count - limb.shrapnel;
            for (int i = 0; i < shrapnel_locations.Count; i++) {
                Vector2 pos = shrapnel_locations[i] + Random.insideUnitCircle * 20f;
                if (missingCount > 0) {
                    missingCount--;
                    pos += Vector2.up * 2500f;
                }
                shrapnel_locations[i] = pos;
                last_shrapnel_movement.Add((0.0, null));
            }
        }

        public override void FixedUpdate() {
            if (limb == null) {
                return;
            }
            _syncTimer -= Time.fixedDeltaTime;
            if (_syncTimer > 0f) {
                return;
            }
            _syncTimer = 0.065f;

            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameSchrapnelLocations);
            writer.Put(new LimbNetId(limb));
            writer.Put((ushort)shrapnel_locations.Count);

            double now = Time.realtimeSinceStartupAsDouble;
            for (int i = 0; i < shrapnel_locations.Count; i++) {
                Vector2 pos = shrapnel_locations[i];
                (double expireAt, ScavPlayer holder) ownership = last_shrapnel_movement[i];

                writer.Put(pos);
                if (ownership.holder == null) {
                    writer.Put(false);
                } else {
                    writer.Put(true);
                    writer.Put(ownership.holder.playerId);
                }

                if (ownership.expireAt < now && ownership.holder != null) {
                    ownership.holder = null;
                    last_shrapnel_movement[i] = ownership;
                }
            }

            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: InvolvedPlayersClientIds());
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameSchrapnelLocations, false)]
        private static void ClientReceiver_MinigameSchrapnelLocations(knetid senderId, NetDataReader reader) {
            ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;

            reader.Get(out LimbNetId netLimb);
            reader.Get(out ushort shrapCount);

            if (MinigameBase.main == null || MinigameBase.main.currentMinigame is not ShrapnelMinigame sm
                || !netLimb.TryGetNetBodyAndLimb(out _, out _)) {
                return;
            }

            var objects = ShrapnelObjectsField?.GetValue(sm) as List<RectTransform>;
            var currentlyHeld = ShrapnelCurrentlyHeldField?.GetValue(sm) as RectTransform;
            if (objects == null) {
                return;
            }

            for (int i = 0; i < shrapCount && i < objects.Count && i < client_shrapnel_owners.Count; i++) {
                reader.Get(out Vector2 loc);
                RectTransform shrapObj = objects[i];
                if (currentlyHeld != shrapObj && shrapObj != null) {
                    float jump = Vector2.Distance(shrapObj.anchoredPosition, loc);
                    if (jump > 120f) {
                        NoteSlow("shrapnel snap " + i + " by " + Mathf.RoundToInt(jump));
                    }
                    if (shrapObj.anchoredPosition != loc) {
                        ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = true;
                    }
                    shrapObj.anchoredPosition = loc;
                }

                reader.Get(out bool hasOwner);
                if (hasOwner) {
                    reader.Get(out knetid ownerId);
                    client_shrapnel_owners[i] = ScavPlayer.GetPlayerFromClientId(ownerId);
                } else {
                    client_shrapnel_owners[i] = null;
                }
            }
        }

        [ServerReceiver((byte)NetmsgId.SERVER_ShrapnelMinigame_MovingShrapnel)]
        private static void ServerReceiver_ShrapnelMinigame_MovingShrapnel(knetid clientId, NetDataReader reader) {
            reader.Get(out byte shrapIndex);
            reader.Get(out Vector2 newPos);

            if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out _) || !Util.IsFinite(newPos)) {
                return;
            }

            MinigameSession sess = GetSessionThisPlayerIsInvolvedIn(plr);
            KM.v2clampSquare(ref newPos, 524f);

            if (sess is ShrapnelMinigameSession sms && shrapIndex < sms.shrapnel_locations.Count && sms.limb != null) {
                (double expireAt, ScavPlayer holder) lastOwnership = sms.last_shrapnel_movement[shrapIndex];
                double now = Time.realtimeSinceStartupAsDouble;
                if (lastOwnership.holder == plr || lastOwnership.expireAt < now) {
                    lastOwnership.expireAt = now + 0.15f;
                    lastOwnership.holder = plr;
                    sms.last_shrapnel_movement[shrapIndex] = lastOwnership;

                    Vector2 oldPos = sms.shrapnel_locations[shrapIndex];
                    if (newPos.y <= 35f) {
                        newPos = new Vector2(oldPos.x, newPos.y);
                    }
                    if (newPos.y < -364f) {
                        newPos = new Vector2(newPos.x, -364f);
                    }
                    sms.shrapnel_locations[shrapIndex] = newPos;

                    int remaining = 0;
                    for (int i = 0; i < sms.shrapnel_locations.Count; i++) {
                        if (sms.shrapnel_locations[i].y < 34f) {
                            remaining++;
                        }
                    }

                    // 先对比弹片数变化再赋值，确保非最后一颗弹片取出时也立即推送血量快照
                    bool shrapnelChanged = sms.limb.shrapnel != remaining;
                    sms.limb.shrapnel = remaining;

                    if (shrapnelChanged && sms.limb.TryGetNetBody(out NetBody nb)) {
                        MedicalSync.Server_QueueSendCharacterHealth(nb);
                    }
                    if (remaining == 0) {
                        Server_EndMinigame(sess, true);
                    }
                } else {
                    NoteSlow("shrapnel move dropped, held by " + lastOwnership.holder);
                }
            } else {
                NoteSlow(sess == null ? "shrapnel move dropped, no session" : "shrapnel move dropped, bad session or index " + shrapIndex);
            }
        }
    }

    public class DislocationMinigameSession : _LimbMedicalMinigameSession {
        public Vector2 bonepos;
        public Vector2 boneVelocity;

        public const float dislocation_timer_minigame_end_treshholddddddddddd = 3f;
        private static readonly float DislocationTimerTolerance = 1.4f;
        private float _syncTimer = 0f;

        public Vector2 FinishSpot => GetFinishSpot(limb);

        public static Vector2 GetFinishSpot(Limb limb) {
            return new Vector2(375f, 54.6f);
        }

        public static float CalculateDislocationTimer(Vector2 boneAnchoredPosition, Limb limb) {
            return Mathf.Clamp(Vector2.Distance(boneAnchoredPosition, GetFinishSpot(limb)) * 0.2f, 0f, 100f);
        }

        public static void BonePositionTick(ref Vector2 boneAnchoredPosition, ref Vector2 boneVelocity) {
            boneAnchoredPosition += boneVelocity * Time.fixedDeltaTime * 3.5f;
            boneVelocity = Vector2.Lerp(boneVelocity, Vector2.zero, Time.fixedDeltaTime * 3.5f);
            boneAnchoredPosition = new Vector2(
                Mathf.Clamp(boneAnchoredPosition.x, -500f, 900f),
                Mathf.Clamp(boneAnchoredPosition.y, -500f, 500f));
        }

        public override void Init() {
            base.Init();
            bonepos = FinishSpot + DislocationMinigame.GetRandomPointOnRightQuarter() * (limb.dislocationTimer * 5f);
        }

        public override void FixedUpdate() {
            if (limb == null) {
                return;
            }
            _syncTimer -= Time.fixedDeltaTime;

            BonePositionTick(ref bonepos, ref boneVelocity);
            limb.dislocationTimer = CalculateDislocationTimer(bonepos, limb);
            if (limb.dislocationTimer < dislocation_timer_minigame_end_treshholddddddddddd + DislocationTimerTolerance) {
                limb.UnDislocate();
                limb.dislocationTimer = 0f;
                MedicalSync.Server_QueueSendCharacterHealth(limb.GetNetBody(), true);
                Server_EndMinigame(this, false);
                obj = null;
            } else if (_syncTimer <= 0f) {
                _syncTimer = 0.06234456f;
                SendBone(InvolvedPlayersClientIds(), false);
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameDislocationBonepos, false)]
        private static void ClientReceiver_MinigameDislocationBonepos(knetid _, NetDataReader reader) {
            ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;

            reader.Get(out LimbNetId _);
            reader.Get(out Vector2 bonePos);
            reader.Get(out Vector2 boneVel);
            bool hard = reader.AvailableBytes > 0 && reader.GetBool();

            if (MinigameBase.main?.currentMinigame is DislocationMinigame dm) {
                DislocationMinigame_CheckForHit_MultiplayerPatch.just_received_new_packet = true;
                DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_bonepos = bonePos;
                DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_boneVelocity = boneVel;
                if (hard) {
                    client_dislocation_mg_ignoretime = 0;
                    dm.boneVelocity = boneVel;
                    Transform root = Minigame.game != null ? Minigame.game.spawnedMiniGame : null;
                    if (root != null && root.childCount > 2 && root.GetChild(2) is RectTransform rt) {
                        rt.anchoredPosition = bonePos;
                    }
                    Note("dislocation deny, bone pulled to host");
                }
            }
        }

        internal void SendBone(IReadOnlyList<knetid> who, bool hard) {
            if (limb == null || who == null || who.Count == 0) {
                return;
            }

            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameDislocationBonepos);
            writer.Put(new LimbNetId(limb));
            writer.Put(bonepos);
            writer.Put(boneVelocity);
            if (hard) {
                writer.Put(true);
            }

            Net.Server_SendToClients(hard ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: who);
        }

        internal void DenyBone(ScavPlayer plr) {
            if (plr == null) {
                return;
            }
            SendBone(new List<knetid> { plr.playerId }, true);
        }
    }

    public abstract class _UnlockingMinigameSession : MinigameSession {
        public BuildingEntity building => obj != null ? obj.GetComponent<BuildingEntity>() : null;
        public Openable openable => obj != null ? obj.GetComponent<Openable>() : null;

        public override void Init() {
            object_required = true;
            if (obj != null && obj.TryGetComponent<UsableObject>(out UsableObject uo)) {
                maxdist = KeypadMinigame_CheckCasts_MultiplayerPatch.GetUsabilityDistanceMP(uo) * 1.1f;
            }
        }
    }

    public class KeypadMinigameSession : _UnlockingMinigameSession {
        public string match { get; protected set; }
        public string current_input = string.Empty;

        public override void Init() {
            base.Init();
            if (string.IsNullOrEmpty(openable.code)) {
                openable.code = KeypadMinigame.GenerateCode();
            }
            match = openable.code;
        }

        public override void OnPlayerJoin(ScavPlayer plr) {
            if (!plr.IsLocal) {
                Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_MinigameKeypadCode, plr.playerId, match, true);
            }
            AnnounceCurrentInput();
        }

        public void AnnounceCurrentInput() {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameKeypadCurrentInput);
            writer.Put(current_input, true);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: InvolvedPlayersClientIds());
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameKeypadCurrentInput, false)]
        private static void ClientReceiver_MinigameKeypadCurrentInput(knetid _, NetDataReader reader) {
            reader.Get(out string code, true);
            if (MinigameBase.main != null && MinigameBase.main.currentMinigame is KeypadMinigame kp) {
                kp.current = code;
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_MinigameKeypadCode)]
        private static void ClientReceiver_MinigameKeypadCode(knetid _, NetDataReader reader) {
            client_i_know_my_minigame_session = true;
            reader.Get(out string code, true);

            if (MinigameBase.main != null && MinigameBase.main.currentMinigame is KeypadMinigame kp) {
                kp.match = code;
                if (kp.toDestroy != null && kp.toDestroy.TryGetComponent<Openable>(out Openable openable)) {
                    openable.code = code;
                }
                if (Minigame.game != null && Minigame.game.spawnedMiniGame != null) {
                    TextMeshProUGUI matchText = Minigame.game.spawnedMiniGame.GetChild(0).GetChild(0).GetComponent<TextMeshProUGUI>();
                    if (matchText != null) {
                        matchText.text = KeypadMinigame.AddDashes(code);
                    }
                }
            }
        }
    }
}
