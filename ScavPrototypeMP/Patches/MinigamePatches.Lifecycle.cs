using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using static BandageMinigame;
using static Together.MinigameMPManager;

namespace Together;

/// <summary>
/// 拦截小游戏开启生命周期，绑定实例级包扎闭包并向服务端注册协同小游戏会话。
/// </summary>
[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.StartMinigame))]
public static class MinigameBase_StartMinigame_MultiplayerPatch {
    public static byte selfharmminigame_last_cuts_count = 0;
    public static bool ignore_next_mgstart_cuz_its_not_me = false;
    public static Dictionary<string, OnBandageUse> known_bandages_onuse_lambdas = new Dictionary<string, OnBandageUse>();

    /// <summary>
    /// 按具体 <c>(item, limb)</c> 实例对保存包扎闭包，防止双人同时使用同 ID 绷带时效果与耐久串台。
    /// </summary>
    public static readonly Dictionary<(Item item, Limb limb), OnBandageUse> known_bandage_instance_lambdas =
        new Dictionary<(Item item, Limb limb), OnBandageUse>();

    private static void RegisterBandageLambda(Item item, Limb limb, OnBandageUse onUse) {
        if (onUse == null) {
            return;
        }
        if (known_bandage_instance_lambdas.Count > 16) {
            List<(Item item, Limb limb)> staleKeys = null;
            foreach (var kv in known_bandage_instance_lambdas) {
                if (kv.Key.item == null || kv.Key.limb == null) {
                    staleKeys ??= new List<(Item item, Limb limb)>();
                    staleKeys.Add(kv.Key);
                }
            }
            if (staleKeys != null) {
                for (int i = 0; i < staleKeys.Count; i++) {
                    known_bandage_instance_lambdas.Remove(staleKeys[i]);
                }
            }
        }
        if (item != null && limb != null) {
            known_bandage_instance_lambdas[(item, limb)] = onUse;
        }
        if (item != null && !string.IsNullOrEmpty(item.id)) {
            known_bandages_onuse_lambdas[item.id] = onUse;
        }
    }

    private static void ResetMinigameOverriderStates() {
        DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_bonepos = new Vector2(500f, 0f);
        DislocationMinigame_CheckForHit_MultiplayerPatch.lastreceived_boneVelocity = Vector2.zero;
        AmputationMinigame_PhysicsUpdate_MultiplayerPatch.last_cutProgress = 0f;
        MinigameMPManager.client_i_know_my_minigame_session = false;
        for (int i = 0; i < ShrapnelMinigameSession.client_shrapnel_owners.Count; i++) {
            ShrapnelMinigameSession.client_shrapnel_owners[i] = null;
        }
    }

    private static bool Prefix(MinigameBase __instance, Minigame minigame, Item item, ref bool __state) {
        __state = __instance.currentMinigame == null;

        AnyObjectNetId objNet = MinigameMPManager.GetMinigameTargetObjectNetId(minigame);
        bool isMedicalMinigame = objNet.TryGetLimb(out Limb limb);

        if (isMedicalMinigame && minigame is BandageMinigame bMini) {
            RegisterBandageLambda(item, limb, bMini.OnUse);
            BandageMinigame_DoBandageAction_MultiplayerPatch.bandagethingcounter = 14;
        }

        if (!Net.IsRunning) {
            return true;
        }
        if (__instance.currentMinigame != null) {
            return false;
        }

        if (ignore_next_mgstart_cuz_its_not_me) {
            ignore_next_mgstart_cuz_its_not_me = false;
            return false;
        }

#if FULLGAME
        if (minigame != null && minigame.GetType().Name == "CargoMinigame") {
            ResetMinigameOverriderStates();
            return true;
        }
#endif

        if (isMedicalMinigame) {
            bool isNonItemMedicalMinigame = minigame is ShrapnelMinigame or DislocationMinigame or CPRMinigame;
            if (isNonItemMedicalMinigame) {
                ShrapnelMinigame_Update_MultiplayerPatch.last_packet_was_a_change = false;
                PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_on_limb = limb;
            } else if (PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_on_limb == null) {
                Body healer = PlayerCamera_ApplyWoundItem_MultiplayerPatch.last_wounditem_user;
                bool thisScreen = healer != null && Util.IsBodyLocal(healer);
                if (minigame is not ProsthesisRepairMinigame || !thisScreen) {
                    return false;
                }
            }

            if (minigame is SyringeMinigame) {
                WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb = limb;
                WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount = 0f;
                SyringeMinigame_Update_MultiplayerPatch.last_fill_amount = item != null ? item.condition : 0f;
                SyringeMinigame_Update_MultiplayerPatch.last_was_different = false;
                SyringeMinigame_Update_MultiplayerPatch.is_start_or_end = true;
                SyringeMinigame_Update_MultiplayerPatch.fill_milestone_timer = SyringeMinigame_Update_MultiplayerPatch.SYRINGE_SEND_RATE;
            } else if (minigame is AmputationMinigame) {
                if (PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item != null) {
                    if (NetObjectRegistry.TryGetSyncInfo(PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item, out _)) {
                        AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item = PlayerCamera_ApplyWoundItem_MultiplayerPatch.applying_item;
                    } else {
                        NetObjectRegistry.AlertObjectNotRegistered(true);
                        return false;
                    }
                } else {
                    return false;
                }
            }
        }

        ResetMinigameOverriderStates();
        return true;
    }

    private static void Postfix(MinigameBase __instance, Minigame minigame, ref bool __state) {
        if (!Net.IsRunning || !__instance.currentMinigame || Net.IsDedicatedServer) {
            return;
        }

#if FULLGAME
        if (minigame != null && minigame.GetType().Name == "CargoMinigame") {
            return;
        }
#endif

        ScavPlayer localPlr = ScavPlayer.LocalPlayer;
        if (localPlr == null) {
            return;
        }
        localPlr.minigame_current_type = MinigameMPManager.GetMinigameTypeId(minigame.GetType());

        AnyObjectNetId objNet = MinigameMPManager.GetMinigameTargetObjectNetId(minigame);
        if (!__state) {
            return;
        }

        if (minigame is KeypadMinigame kp && Net.IsClient) {
            kp.match = "-- LOADING --";
        }

        knetid currentItemSyncId = 0;
        if (MinigameBase.main.currentItem != null) {
            if (NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out SyncInfo curItemSi)) {
                currentItemSyncId = curItemSi.syncId;
            } else {
                NetObjectRegistry.AlertObjectNotRegistered(true);
            }
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_MinigameStart);
        writer.Put(localPlr.minigame_current_type);
        writer.Put(currentItemSyncId);
        writer.Put(objNet);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }
}

/// <summary>
/// 拦截小游戏结束并向服务端提交结算结果（自残、撬锁、断针、正骨、截肢）。
/// </summary>
[HarmonyPatch(typeof(MinigameBase), nameof(MinigameBase.EndMinigame))]
public static class MinigameBase_EndMinigame_MultiplayerPatch {
    private static bool Prefix(MinigameBase __instance) {
        MinigameMPManager.Client_DeleteAllRemoteHands();
        MinigameMPManager.client_i_know_my_minigame_session = false;

        if (Net.IsRunning) {
            try {
                if (__instance.currentMinigame != null) {
                    if (__instance.currentMinigame is SelfHarmMinigame sharm) {
                        Body harmBody = SelfHarmMultiplayerSync.selfHarmMinigameBody;
                        if (harmBody == null || Util.IsBodyLocal(harmBody)) {
                            byte resultType = (byte)(sharm.cutsDone == 5 ? 0 : (sharm.isEye ? 1 : 2));
                            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SelfHarmMinigameMinigameEnd, resultType, true);
                        }
                    } else if (Net.IsClient) {
                        if (__instance.currentMinigame is LockpingMinigame lmini) {
                            if (lmini.lockProgress >= 1f && lmini.toDestroy != null && lmini.toDestroy.GetHealth() == 0f
                                && NetObjectRegistry.TryGetSyncInfoOrRegister(lmini.toDestroy.gameObject, out SyncInfo si)) {
                                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_LockpickingMinigame_Finish_success, si.syncId, true);
                            }
                        } else if (__instance.currentMinigame is SyringeMinigame) {
                            Limb l = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;
                            NetBody pb = l != null && l.body != null ? l.body.GetComponent<NetBody>() : null;
                            if (l != null && pb != null && pb.syncBody != null && l.shrapnel == 1 && l.pain > 20f
                                && Minigame.game != null && Minigame.game.currentItem != null
                                && NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem.gameObject, out SyncInfo si)) {
                                SyringeMinigame_Update_MultiplayerPatch.Client_SendCurrentInjectedAmount(true);
                                Multiplayer.Client_SendSimpleMessageToServer(
                                    (byte)NetmsgId.SERVER_SyringMinigame_BrokeNeedle,
                                    si.syncId,
                                    pb.netId,
                                    Util.GetLimbIndex(l),
                                    true);
                            }
                        } else if (__instance.currentMinigame is DislocationMinigame dmini) {
                            if (dmini.limb != null && !dmini.limb.dislocated) {
                                NetDataWriter writer = Net.CreateWriter(NetmsgId.DislocationFixSuccess);
                                writer.Put(new LimbNetId(dmini.limb));
                                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
                            }
                        } else if (__instance.currentMinigame is AmputationMinigame amini) {
                            if (amini.cutProgress >= 1f && amini.limb != null
                                && NetObjectRegistry.TryGetSyncInfo(AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item, out SyncInfo si)) {
                                NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_AmputationMinigame_success);
                                writer.Put(si.syncId);
                                writer.Put(new LimbNetId(amini.limb));
                                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                            }
                            AmputationMinigame_PhysicsUpdate_MultiplayerPatch.cutter_item = null;
                        }
                    }
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Medical, "MinigameBase.EndMinigame", ex);
            }

#if FULLGAME
            if (__instance.currentMinigame != null && __instance.currentMinigame.GetType().Name == "CargoMinigame") {
                return true;
            }
#endif

            NetDataWriter endWriter = Net.CreateWriter(NetmsgId.SERVER_MinigameEnd);
            endWriter.Put(true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, endWriter);
        }

        return true;
    }

    public static bool CanAmputate(this Limb limb) {
        if (limb == null || limb.body == null) {
            return false;
        }
        if (limb.body.IsDeadOrCriticallyDying() || Multiplayer.rules.AmputateHealthyPlayers) {
            return CombatStuff.CheckIfItsOkeyToDismemberThatLimb(limb);
        }

        return limb.infectionAmount > 60f
            && Array.IndexOf(limb.body.limbs, limb) > (int)LimbIndex.LowerTorso;
    }
}

/// <summary>
/// 小游戏结束结算网络接收器（独立于 <c>[HarmonyPatch]</c> 类以规避值类型参数 <c>Harmony003</c> 告警）。
/// </summary>
internal static class MinigameEndReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_SelfHarmMinigameMinigameEnd)]
    private static void Server_SelfHarmMinigameMinigameEnd(knetid clientId, NetDataReader reader) {
        reader.Get(out byte type);
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body)
            || !body.alive || body.totalHappiness >= -50f) {
            return;
        }

        Limb limb = null;
        SelfHarmMultiplayerSync.SelfHarm_Copypasted(type, body, ref limb);
        if (limb != null) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_SelfHarmRelay);
            writer.Put(type);
            writer.Put(new LimbNetId(limb));
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        }

        MedicalSync.Server_QueueSendCharacterHealth(plr.netBody, true);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_AmputationMinigame_success)]
    private static void Server_AmputationMinigame_success(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid itemSyncId);
        reader.Get(out LimbNetId netLimb);

        if (!MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, netLimb.bodyNetId, netLimb.limbId, itemSyncId, out ScavPlayer healer, out NetBody target, out SyncInfo si)
            || !si.CanBeUsedToAmputate()) {
            return;
        }

        Limb limb = netLimb.GetLimb();
        if (limb != null && limb.CanAmputate() && limb.muscleHealth < 70f) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(limb.transform.position, $"S: AmputationMinigame_success {healer} -> {target} {si}");
            }

            AmputationMinigame_Update_MultiplayerPatch.AmputationMinigame_FinishAmputateCopypasted(limb);
            target.OnDamageEvent(new BodyDamageEvent(healer.netBody, target, si.item, 100f));

            if (limb.isHead) {
                healer.body.happiness -= Mathf.Min(10f, Multiplayer.rules.PVPMoodDebuff * 10f);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.DislocationFixSuccess)]
    private static void Server_DislocationFixSuccess(knetid clientId, NetDataReader reader) {
        reader.Get(out LimbNetId netLimb);
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody healer)
            || !netLimb.TryGetNetBodyAndLimbSafe(out _, out Limb limb)) {
            return;
        }

        MinigameSession sess = MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(plr);
        if (sess is DislocationMinigameSession dms && dms.obj != null && dms.limb == limb
            && limb.dislocated && limb.dislocationTimer < 10f) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(limb.transform.position, $"S: DislocationFixSuccess {healer} -> {netLimb}");
            }
            limb.UnDislocate();
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SyringMinigame_BrokeNeedle)]
    private static void Server_SyringMinigame_BrokeNeedle(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid needleId);
        reader.Get(out knetid targetPlr);
        reader.Get(out byte limbId);

        if (!MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, targetPlr, limbId, needleId, out ScavPlayer plr, out NetBody targetNb, out SyncInfo si)) {
            return;
        }

        Limb limb = targetNb.body.limbs[limbId];
        if (limb.shrapnel == 1) {
            return;
        }

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(plr.body.transform.position, $"S: broke needle {plr.playerName} -> {targetNb.playerName} {si}");
        }

        if (!plr.IsLocal) {
            limb.shrapnel = Math.Max(limb.shrapnel, 1);
            limb.pain += 20f;
        }

        ServerMain.Server_AnnounceSound(targetNb.body.transform.position, "bullethit", ServerMain.GetListOfClientIdsExceptThis(clientId));
    }

    [ServerReceiver((byte)NetmsgId.SERVER_LockpickingMinigame_Finish_success)]
    private static void Server_LockpickingMinigame_Finish_success(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid buildingSyncId);
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb) || pb.body == null) {
            return;
        }

        if (pb.body.conscious
            && NetObjectRegistry.TryGetSyncInfo(buildingSyncId, out SyncInfo si)
            && si.go != null
            && si.go.TryGetComponent<Openable>(out Openable openable)
            && KM.DistanceCheck(pb.body, si.go.transform, GameplayRanges.ServerExchangeToleranceDistance)
            && !openable.isKeypad) {
            si.building.SetHealth(0f);
            ServerMain.Server_AnnounceSound(si.transformPosition, VanillaAssetKeys.SoundUnlock, ServerMain.AllClientIds);
        } else {
            MinigameMPManager.Note("lockpick denied");
            plr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_lockpick"));
        }
    }
}
