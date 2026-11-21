using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.EventSystems;
using static Together.MinigameMPManager;

namespace Together;

/// <summary>
/// 正骨小游戏骨骼位置预测插值、击打上报与防客户端提前结束限位补丁。
/// </summary>
[HarmonyPatch(typeof(DislocationMinigame), nameof(DislocationMinigame.CheckForHit))]
public static class DislocationMinigame_CheckForHit_MultiplayerPatch {
    public static bool just_received_new_packet = false;
    public static Vector2 lastreceived_bonepos = new Vector2(500f, 0f);
    public static Vector2 lastreceived_boneVelocity = Vector2.zero;

    public static Vector2 real_bonepos = new Vector2(500f, 0f);
    public static Vector2 boneVelocity = Vector2.zero;

    public static RectTransform bone => Minigame.game.spawnedMiniGame.GetChild(2).GetComponent<RectTransform>();

    private sealed class HitState {
        public bool CheckForHitSkipped;
        public Vector2 PreviousVelocity;
    }

    private static void LimitBoneLocationForMP() {
        if (Minigame.game?.currentMinigame is not DislocationMinigame dm) {
            return;
        }

        Vector2 pos = bone.anchoredPosition;
        Vector2 vel = dm.boneVelocity;
        DislocationMinigameSession.BonePositionTick(ref pos, ref vel);
        float newDislocationTimer = DislocationMinigameSession.CalculateDislocationTimer(pos, dm.limb);

        if (newDislocationTimer <= DislocationMinigameSession.dislocation_timer_minigame_end_treshholddddddddddd) {
            Vector2 finishSpot = DislocationMinigameSession.GetFinishSpot(dm.limb);
            Vector2 normal = pos - finishSpot;
            float magnitude = normal.magnitude;
            normal = magnitude > 1E-05f ? (normal / magnitude) : Vector2.zero;

            bone.anchoredPosition = finishSpot + normal * 3.01f;
            dm.boneVelocity -= normal * Vector2.Dot(dm.boneVelocity, normal);
        }
    }

    private static void Prefix(DislocationMinigame __instance, ref HitState __state) {
        if (!Net.IsRunning) {
            return;
        }

        Limb limb = __instance.limb;
        boneVelocity = __instance.boneVelocity;

        Body localBody = Util.GetLocalBodyNullable();
        if (!limb.IsBodyLocal() && limb.body.averagePain > 75f && localBody != null && localBody.averagePain < 75f) {
            limb.body.averagePain = 74f;
        }

        real_bonepos = bone.anchoredPosition;
        __state = new HitState {
            CheckForHitSkipped = limb.body.averagePain > 75f
                || boneVelocity.magnitude > 60f
                || !Minigame.game.handClicking
                || Minigame.game.handVelocity.magnitude < 4f,
            PreviousVelocity = boneVelocity,
        };
    }

    private static void Postfix(DislocationMinigame __instance, ref HitState __state) {
        if (!Net.IsRunning || __state == null) {
            return;
        }

        MinigameBase mgBase = MinigameBase.main;
        if (mgBase != null && mgBase.forceUngrab && mgBase.firstFrameForceUngrab && !__state.CheckForHitSkipped) {
            boneVelocity = __instance.boneVelocity;
            Vector2 diff = boneVelocity - __state.PreviousVelocity;

            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_DislocationMinigame_dislocatingtypeshit);
            writer.Put(diff);
            writer.Put(__instance.hasWrench);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

            if (Net.IsClient) {
                MinigameMPManager.client_dislocation_mg_ignoretime = Time.realtimeSinceStartupAsDouble + ClientMain.RTT_IN_SECONDS_with_margin + 1.0;
            }
        }

        double timeDiff = MinigameMPManager.client_dislocation_mg_ignoretime - Time.realtimeSinceStartupAsDouble;
        if (timeDiff > 0.0) {
            float listenScale = Mathf.Clamp01(1.0f - (float)timeDiff);
            if (just_received_new_packet) {
                float jump = Vector2.Distance(bone.anchoredPosition, lastreceived_bonepos);
                if (jump > 40f) {
                    MinigameMPManager.NoteSlow("dislocation snap by " + Mathf.RoundToInt(jump));
                }
                just_received_new_packet = false;
                bone.anchoredPosition = Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, listenScale);
            } else {
                bone.anchoredPosition = Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, listenScale * Time.deltaTime * 0.5f);
            }
            __instance.boneVelocity = Vector2.Lerp(__instance.boneVelocity, lastreceived_boneVelocity, listenScale * Time.deltaTime);
        } else {
            if (just_received_new_packet) {
                float jump = Vector2.Distance(bone.anchoredPosition, lastreceived_bonepos);
                if (jump > 40f) {
                    MinigameMPManager.NoteSlow("dislocation snap by " + Mathf.RoundToInt(jump));
                }
                just_received_new_packet = false;
                bone.anchoredPosition = lastreceived_bonepos;
                __instance.boneVelocity = lastreceived_boneVelocity;
            } else {
                bone.anchoredPosition = Vector2.Lerp(bone.anchoredPosition, lastreceived_bonepos, Time.deltaTime);
            }
        }

        DislocationMinigameSession.BonePositionTick(ref lastreceived_bonepos, ref lastreceived_boneVelocity);
        LimitBoneLocationForMP();
    }
}

/// <summary>
/// 取弹片小游戏多人夹持所有权互斥、弹片拖拽同步与远端音效插值补丁。
/// </summary>
[HarmonyPatch(typeof(ShrapnelMinigame), nameof(ShrapnelMinigame.Update))]
public static class ShrapnelMinigame_Update_MultiplayerPatch {
    private static readonly FieldInfo LimbField = AccessTools.Field(typeof(ShrapnelMinigame), "limb");
    private static readonly FieldInfo ObjectsField = AccessTools.Field(typeof(ShrapnelMinigame), "objects");
    private static readonly FieldInfo CurrentlyHeldField = AccessTools.Field(typeof(ShrapnelMinigame), "currentlyHeld");
    private static readonly FieldInfo HeldOffsetField = AccessTools.Field(typeof(ShrapnelMinigame), "heldOffset");

    public static bool last_packet_was_a_change = false;
    private static float _syncTimer = 0f;
    private static RectTransform _lastHeld = null;

    public static void Prefix(ShrapnelMinigame __instance, ref float __state) {
        if (!Net.IsRunning) {
            return;
        }

        Limb l = LimbField?.GetValue(__instance) as Limb;
        if (l == null) {
            return;
        }

        var objects = ObjectsField?.GetValue(__instance) as List<RectTransform>;
        var currentlyHeld = CurrentlyHeldField?.GetValue(__instance) as RectTransform;
        if (objects == null) {
            return;
        }

        bool forceSendImportant = false;
        if (currentlyHeld != null) {
            int idx = objects.IndexOf(currentlyHeld);
            ScavPlayer owner = idx >= 0 && idx < ShrapnelMinigameSession.client_shrapnel_owners.Count
                ? ShrapnelMinigameSession.client_shrapnel_owners[idx]
                : null;

            if (owner == null || owner == ScavPlayer.LocalPlayer) {
                _syncTimer -= Time.deltaTime;
                if (currentlyHeld.anchoredPosition.y < 35f && HeldOffsetField != null) {
                    Vector2 heldOffset = (Vector2)HeldOffsetField.GetValue(__instance);
                    if (Minigame.game.handPos.y + heldOffset.y > 35f) {
                        currentlyHeld.anchoredPosition = Minigame.game.handPos + heldOffset;
                        _syncTimer = 0f;
                        forceSendImportant = true;
                    }
                }
            } else {
                MinigameMPManager.NoteSlow("shrapnel ungrab, owner " + owner);
                currentlyHeld = null;
                Minigame.game.ForceUngrab();
            }
        } else if (_lastHeld != null) {
            _syncTimer = 0f;
            forceSendImportant = true;
        }

        if (_syncTimer <= 0f && currentlyHeld != null) {
            _syncTimer = 0.05f;
            if (l.TryGetNetBody(out _)) {
                byte shrapIndex = (byte)objects.IndexOf(currentlyHeld);
                Multiplayer.Client_SendSimpleMessageToServer(
                    (byte)NetmsgId.SERVER_ShrapnelMinigame_MovingShrapnel,
                    shrapIndex,
                    currentlyHeld.anchoredPosition,
                    forceSendImportant);
            }
        }

        AudioSource source = Minigame.game.spawnedMiniGame.GetComponent<AudioSource>();
        __state = source != null ? source.volume : 0f;
        _lastHeld = currentlyHeld;
    }

    public static void Postfix(ShrapnelMinigame __instance, ref float __state) {
        if (!Net.IsRunning) {
            return;
        }

        var currentlyHeld = CurrentlyHeldField?.GetValue(__instance) as RectTransform;
        if (currentlyHeld == null && Minigame.game?.spawnedMiniGame != null) {
            AudioSource source = Minigame.game.spawnedMiniGame.GetComponent<AudioSource>();
            if (source != null) {
                source.volume = Mathf.MoveTowards(__state, last_packet_was_a_change ? 1f : 0f, Time.deltaTime * 5f);
            }
        }
    }
}

/// <summary>
/// 取弹片手滑脱手撕裂伤同步补丁。
/// </summary>
[HarmonyPatch(typeof(ShrapnelMinigame), nameof(ShrapnelMinigame.BreakGrasp))]
public static class ShrapnelMinigame_BreakGrasp_MultiplayerPatch {
    private static readonly FieldInfo CurrentlyHeldField = AccessTools.Field(typeof(ShrapnelMinigame), "currentlyHeld");
    private static readonly FieldInfo LimbField = AccessTools.Field(typeof(ShrapnelMinigame), "limb");

    public static void Prefix(ShrapnelMinigame __instance) {
        if (!Net.IsRunning) {
            return;
        }

        var currentlyHeld = CurrentlyHeldField?.GetValue(__instance) as RectTransform;
        if (currentlyHeld != null) {
            Limb l = LimbField?.GetValue(__instance) as Limb;
            if (l != null && l.TryGetNetBody(out _)) {
                NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ShrapnelMinigame_BreakGrasp);
                writer.Put(new LimbNetId(l));
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }
    }
}

/// <summary>
/// 截肢小游戏完成判定与本地施救者肾上腺素隔离补丁。
/// </summary>
[HarmonyPatch(typeof(AmputationMinigame), nameof(AmputationMinigame.Update))]
internal static class AmputationMinigame_Update_MultiplayerPatch {
    public static void AmputationMinigame_FinishAmputateCopypasted(Limb limb) {
        if (Net.IsServer) {
            if (limb.body.IsDeadOrCriticallyDying() || limb.infectionAmount < 90f) {
                CombatStuff.BetterDismember(limb);
            } else {
                limb.Dismember();
            }
        }

        Limb[] connectedLimbs = limb.connectedLimbs;
        for (int i = 0; i < connectedLimbs.Length; i++) {
            Limb connected = connectedLimbs[i];
            connected.infected = false;
            connected.infectionAmount = 0f;
            connected.SetDisinfect(300f);
            connected.bleedAmount *= 0.5f;
        }
        limb.body.traumaAmount -= 20f;
    }

    private static bool Prefix(AmputationMinigame __instance, out float __state) {
        __state = 0f;
        if (!Net.IsRunning) {
            return true;
        }

        if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
            __state = PlayerCamera.main.body.adrenaline;
        }

        if (__instance.cutProgress >= 1f) {
            AmputationMinigame_FinishAmputateCopypasted(__instance.limb);
            Minigame.game.EndMinigame();
            return false;
        }
        return true;
    }

    private static void Postfix(float __state) {
        if (!Net.IsRunning || PlayerCamera.main == null || PlayerCamera.main.body == null) {
            return;
        }
        PlayerCamera.main.body.adrenaline = __state;
    }
}

/// <summary>
/// 截肢切割进度增量同步补丁。
/// </summary>
[HarmonyPatch(typeof(AmputationMinigame), nameof(AmputationMinigame.PhysicsUpdate))]
public static class AmputationMinigame_PhysicsUpdate_MultiplayerPatch {
    public static Item cutter_item;
    public static float last_cutProgress = 0f;

    public static void Prefix(AmputationMinigame __instance) {
        if (!Net.IsRunning) {
            return;
        }

        float scalarMuscleHealth = Mathf.Clamp01(__instance.limb.muscleHealth / 100f);
        __instance.cutProgress = Mathf.Max(__instance.cutProgress, 1f - (0.5f + scalarMuscleHealth * 0.5f));
    }

    public static void Postfix(AmputationMinigame __instance) {
        if (!Net.IsRunning) {
            return;
        }

        if (__instance.cutProgress > last_cutProgress + 0.08f) {
            last_cutProgress += 0.08f;
            if (NetObjectRegistry.TryGetSyncInfo(cutter_item, out SyncInfo si)) {
                __instance.limb.body.adrenaline = 100f;
                if (Net.IsClient) {
                    NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_AmputationMinigame_cutting);
                    writer.Put(si.syncId);
                    writer.Put(new LimbNetId(__instance.limb));
                    Net.Client_Send(DeliveryMethod.Unreliable, writer);
                }
            }
        }
    }
}

/// <summary>
/// 外科手术类小游戏（正骨、取弹片、截肢切割）服务端接收器。
/// </summary>
internal static class SurgeryMinigameReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_DislocationMinigame_dislocatingtypeshit)]
    private static void Server_DislocationHit(knetid clientId, NetDataReader reader) {
        reader.Get(out Vector2 newVel);
        reader.Get(out bool hasWrench);

        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body healerBody)
            || !healerBody.conscious || !Util.IsFinite(newVel)) {
            return;
        }

        if (MinigameMPManager.TryGetSessionThisPlayerIsInvolvedIn<DislocationMinigameSession>(plr, out DislocationMinigameSession dms)
            && dms.limb != null
            && dms.boneVelocity.magnitude <= 60f) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(plr.body.transform.position, $"S: dislocation hit: {newVel}");
            }

            dms.boneVelocity += Vector2.ClampMagnitude(newVel, 1300f);

            if (!plr.IsLocal) {
                if (hasWrench) {
                    dms.limb.pain += Random.Range(4f, 10f);
                } else {
                    dms.limb.pain += Random.Range(15f, 24f);
                    if (Random.value > 0.995f) {
                        dms.limb.BreakBone();
                    }
                }
            }

            ServerMain.Server_AnnounceSound(dms.limb.transform.position, "boneHit", ServerMain.GetListOfClientIdsExceptThis(clientId));
        } else if (MinigameMPManager.TryGetSessionThisPlayerIsInvolvedIn<DislocationMinigameSession>(plr, out DislocationMinigameSession fast)
                   && fast.boneVelocity.magnitude > 60f) {
            MinigameMPManager.NoteSlow("dislocation hit dropped by speed " + Mathf.RoundToInt(fast.boneVelocity.magnitude));
            fast.DenyBone(plr);
        } else {
            MinigameMPManager.NoteSlow("dislocation hit dropped, no session");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ShrapnelMinigame_BreakGrasp)]
    private static void Server_ShrapnelBreakGrasp(knetid clientId, NetDataReader reader) {
        reader.Get(out LimbNetId limbNetId);

        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body healerBody)
            || !healerBody.conscious
            || !limbNetId.TryGetNetBodyAndLimbSafe(out NetBody targetNb, out Limb limb)
            || !Util.DoFullInteractionCheck(healerBody, targetNb.body, false, SharedMain.max_player_interaction_distance * 1.5f)) {
            return;
        }

        if (limb.shrapnel > 0) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(plr.body.transform.position, $"S: failed shrapnel {plr} -> {limbNetId}");
            }

            if (!plr.IsLocal) {
                limb.skinHealth -= Random.Range(4f, 6f);
                limb.bleedAmount += Random.Range(0.4f, 1f);
                limb.pain += Random.Range(9f, 16f);
            }

            ServerMain.Server_AnnounceSound(targetNb.position, $"gore{Random.Range(1, 6)}", ServerMain.GetListOfClientIdsExceptThis(clientId));
            MedicalSync.Server_QueueSendCharacterHealth(targetNb);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_AmputationMinigame_cutting)]
    private static void Server_AmputationCutting(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid itemSyncId);
        reader.Get(out LimbNetId netLimb);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, netLimb.bodyNetId, netLimb.limbId, itemSyncId, out ScavPlayer healer, out NetBody target, out SyncInfo si)
            && si.CanBeUsedToAmputate()) {
            Limb limb = netLimb.GetLimb();
            if (limb != null && limb.CanAmputate()) {
                const float supposedProgress = 0.09f;
                const float timedProgress = supposedProgress * 100f;

                limb.body.adrenaline = Mathf.Max(limb.body.adrenaline, 60f);
                limb.pain += timedProgress * 0.5f;
                limb.skinHealth -= timedProgress;
                limb.muscleHealth -= timedProgress;
                limb.bleedAmount += timedProgress * 0.5f;

                BodyDamageEvent last = target.GetLastDamageEvent();
                if (last == null || last.attackerBody != healer.netBody) {
                    target.OnDamageEvent(new BodyDamageEvent(healer.netBody, target, si.item, supposedProgress));
                }
                return;
            }
        }

        if (healer != null && target != null) {
            healer.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_amputation"), false);
            ModLog.Warn($"服务端拒绝截肢切割增量: {healer} -> {target}", LogDomain.Medical);
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healer);
        }
    }
}
