using System.Collections.Generic;
using System.Globalization;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using UnityEngine.EventSystems;
using static BandageMinigame;
using static Together.MinigameMPManager;

namespace Together;

/// <summary>
/// 注射器推注进度聚合发送补丁。
/// </summary>
[HarmonyPatch(typeof(SyringeMinigame), nameof(SyringeMinigame.Update))]
public static class SyringeMinigame_Update_MultiplayerPatch {
    public static float last_fill_amount = 0f;
    public static bool last_was_different = false;
    public static bool is_start_or_end = false;

    public const float SYRINGE_SEND_RATE = 0.5f;
    public static float fill_milestone_timer = SYRINGE_SEND_RATE;

    public static void Client_SendCurrentInjectedAmount(bool forceSound) {
        Limb limb = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;
        if (limb == null || limb.body == null || Minigame.game?.currentItem == null) {
            return;
        }
        if (!ItemSync.TryGetSyncInfo(Minigame.game.currentItem, out SyncInfo si)
            || WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount <= 0f) {
            return;
        }

        fill_milestone_timer = SYRINGE_SEND_RATE;

        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_SyringeMinigameInjectShit);
        writer.Put(si.syncId);
        writer.Put(WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount);
        writer.Put(ScavPlayer.GetClientIdFromBody(limb.body));
        writer.Put((byte)Util.GetLimbIndex(limb));
        writer.Put(is_start_or_end || forceSound);
        is_start_or_end = false;

        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb_amount = 0f;
    }

    public static void Postfix(SyringeMinigame __instance) {
        if (!Net.IsRunning) {
            return;
        }

        bool isDifferent = last_fill_amount != __instance.syringeFill.fillAmount;
        Limb limb = WaterContainerItem_Inject_MultiplayerPatch.listen_to_limb;

        if (isDifferent && limb != null && limb.TryGetNetBody(out NetBody pb)) {
            pb.SetNetHealthSyncIgnoreTime(0.1f);
        }

        if (Minigame.game.currentItem == null) {
            return;
        }

        bool itemSynced = ItemSync.TryGetSyncInfo(Minigame.game.currentItem, out SyncInfo si);
        bool justStoppedInjecting = !isDifferent && last_was_different;
        if ((justStoppedInjecting || fill_milestone_timer <= 0f) && itemSynced) {
            Client_SendCurrentInjectedAmount(justStoppedInjecting);
        } else if (isDifferent) {
            if (itemSynced) {
                si.SetIgnoreTimeForRoundTrip(0.1f);
            }
            fill_milestone_timer -= Time.deltaTime;
        } else {
            is_start_or_end = true;
            fill_milestone_timer = SYRINGE_SEND_RATE;
        }

        last_was_different = isDifferent;
        last_fill_amount = __instance.syringeFill.fillAmount;
    }
}

/// <summary>
/// 统计本地注射器推注毫升数并标记容器往返同步抑制窗口。
/// </summary>
[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.Inject))]
public static class WaterContainerItem_Inject_MultiplayerPatch {
    public static Limb listen_to_limb = null;
    public static float listen_to_limb_amount = 0f;

    private static bool Prefix(WaterContainerItem __instance, Limb limb, float amount) {
        if (!Net.IsRunning) {
            return true;
        }

        limb.GetCoUtilsInstance().SelectForMPOverride();
        if (limb == listen_to_limb) {
            listen_to_limb_amount += amount;
        }
        if (NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out SyncInfo si)) {
            si.SetIgnoreTimeForRoundTrip();
        }
        return true;
    }
}

/// <summary>
/// 绷带/敷料缠绕小游戏单步动作上报补丁。
/// </summary>
[HarmonyPatch(typeof(BandageMinigame), nameof(BandageMinigame.DoBandageAction))]
public static class BandageMinigame_DoBandageAction_MultiplayerPatch {
    public static int bandagethingcounter = 0;

    public static bool Prefix(BandageMinigame __instance) {
        if (Net.IsRunning && __instance.limb != null && __instance.limb.body != null && Minigame.game?.currentItem != null) {
            Limb l = __instance.limb;
            NetBody pb = l.body.GetComponent<NetBody>();
            if (pb != null && pb.syncBody != null
                && NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem.gameObject, out SyncInfo si)) {
                Multiplayer.Client_SendSimpleMessageToServer(
                    (byte)NetmsgId.SERVER_BandageMinigame_DoBandageAction,
                    si.syncId,
                    pb.netId,
                    Util.GetLimbIndex(l),
                    true);
                si.SetIgnoreTimeForRoundTrip();
            }
        }
        return true;
    }
}

#if FULLGAME
/// <summary>
/// 记录每种义肢修补耗材对应的最大破洞修补次数，并提供原版耗材服务端权威常量解析与过期缓存清理（对应 PR #60/#58）。
/// </summary>
[HarmonyPatch(typeof(Item), nameof(Item.TryProsthesisRepair))]
public static class ProsthesisRepair_RememberPatchCount {
    private const int PruneThreshold = 256;
    private const int MaxModdedHoleCountClamp = 64;

    public static readonly Dictionary<ushort, int> PatchCount = new Dictionary<ushort, int>();
    private static readonly List<ushort> StaleKeys = new List<ushort>();

    private static void Prefix(Item item, int holeCount) {
        if (item == null || holeCount < 1) {
            return;
        }
        PruneStaleEntriesIfNeeded();
        if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) && si != null) {
            PatchCount[si.syncId.id] = holeCount;
        }
    }

    /// <summary>
    /// 对照原版 <c>Item.cs</c> 全部 11 种义肢修补耗材 ID 返回权威 <c>holeCount</c>，防恶意客机伪造高数值零损耗或反向刷耐久。
    /// </summary>
    public static bool TryGetVanillaHoleCount(string itemId, out int holeCount) {
        holeCount = itemId switch {
            "scrapmetal" or "scrapcube" => 16,
            "scrappanel" => 2,
            "scraptube" => 6,
            "woodscraps" or "woodcube" => 10,
            "woodpanel" => 1,
            "stick" => 4,
            "titaniumslab" => 50,
            "titaniumsheet" => 10,
            "titaniumrod" => 25,
            _ => 0,
        };
        return holeCount > 0;
    }

    public static int ResolveClientHoleCountHint(Item item, ushort syncId) {
        if (PatchCount.TryGetValue(syncId, out int cached) && cached >= 1) {
            return cached;
        }
        if (item != null && TryGetVanillaHoleCount(item.id, out int vanillaCount)) {
            return vanillaCount;
        }
        return 0;
    }

    /// <summary>
    /// 服务端权威解析义肢修补耗材的 <c>holeCount</c>：
    /// 1. 已知原版耗材强制返回服务端权威常量（忽略客机提示）；
    /// 2. 服务端已记录 <see cref="PatchCount"/> 的物品优先取缓存值；
    /// 3. 仅未知第三方模组耗材因 <c>ReliableUnordered</c> 乱序或读档恢复未命中缓存时，回退采用 <c>[1, 64]</c> 截断后的客机提示（上限 64 确保 <c>1/holeCount &gt; 0.0015f</c> 严格正向扣减耐久）。
    /// </summary>
    public static int ResolveAuthoritativeHoleCount(Item item, int clientHint) {
        PruneStaleEntriesIfNeeded();
        if (item == null) {
            return 0;
        }
        if (TryGetVanillaHoleCount(item.id, out int vanillaCount)) {
            return vanillaCount;
        }
        if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si)
            && si != null
            && PatchCount.TryGetValue(si.syncId.id, out int cachedCount)
            && cachedCount >= 1) {
            return cachedCount;
        }
        if (clientHint >= 1) {
            return Mathf.Clamp(clientHint, 1, MaxModdedHoleCountClamp);
        }
        return 0;
    }

    public static void PruneStaleEntriesIfNeeded() {
        if (PatchCount.Count <= PruneThreshold) {
            return;
        }
        StaleKeys.Clear();
        foreach (ushort id in PatchCount.Keys) {
            if (!NetObjectRegistry.TryGetSyncInfo(id, out SyncInfo si) || si == null || si.IsDestroyed || si.go == null) {
                StaleKeys.Add(id);
            }
        }
        for (int i = 0; i < StaleKeys.Count; i++) {
            PatchCount.Remove(StaleKeys[i]);
        }
        StaleKeys.Clear();
    }
}

/// <summary>
/// 客户端完成义肢贴片或扳直修复时向服务端发送 <see cref="CustomWireCommands.ProsthesisPatch"/> 同步指令（追加第 6 参数兼容旧版 v5.5.3.0 房主）。
/// </summary>
[HarmonyPatch(typeof(ProsthesisRepairMinigame), nameof(ProsthesisRepairMinigame.Update))]
public static class ProsthesisRepairMinigame_Update_MultiplayerPatch {
    private static readonly FieldInfo RepairLimbField = AccessTools.Field(typeof(ProsthesisRepairMinigame), "limb");

    public struct BeforeState {
        public float Muscle;
        public float Bone;
        public bool Broken;
        public bool Ok;
    }

    private static void Prefix(out BeforeState __state) {
        __state = default;
        if (!Net.IsRunning || Net.IsServer || Minigame.game == null || !Minigame.game.handStoppedClicking) {
            return;
        }
        if (Minigame.game.currentMinigame is not ProsthesisRepairMinigame repair) {
            return;
        }
        Limb limb = RepairLimbField?.GetValue(repair) as Limb;
        if (limb == null) {
            return;
        }
        __state.Ok = true;
        __state.Muscle = limb.muscleHealth;
        __state.Bone = limb.boneHealTimer;
        __state.Broken = limb.broken;
    }

    private static void Postfix(ref BeforeState __state) {
        if (!__state.Ok || Minigame.game == null || Minigame.game.currentItem == null) {
            return;
        }
        if (Minigame.game.currentMinigame is not ProsthesisRepairMinigame repair) {
            return;
        }
        Limb limb = RepairLimbField?.GetValue(repair) as Limb;
        if (limb == null || limb.body == null) {
            return;
        }

        int flags = 0;
        if (limb.muscleHealth > __state.Muscle + 0.01f) {
            flags |= 1;
        }
        if (__state.Broken && (!limb.broken || limb.boneHealTimer < __state.Bone - 0.01f)) {
            flags |= 2;
        }
        if (flags == 0) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem.gameObject, out SyncInfo si) || si == null) {
            return;
        }
        if (!limb.body.TryGetNetBody(out NetBody nb) || nb.syncBody == null) {
            return;
        }

        si.SetIgnoreTimeForRoundTrip();
        int holeHint = ProsthesisRepair_RememberPatchCount.ResolveClientHoleCountHint(Minigame.game.currentItem, si.syncId.id);
        string msg = holeHint > 0
            ? string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4} {5}", CustomWireCommands.ProsthesisPatch, si.syncId.id, nb.netId.id, Util.GetLimbIndex(limb), flags, holeHint)
            : string.Format(CultureInfo.InvariantCulture, "{0} {1} {2} {3} {4}", CustomWireCommands.ProsthesisPatch, si.syncId.id, nb.netId.id, Util.GetLimbIndex(limb), flags);

        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(msg);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }
}
#endif

/// <summary>
/// 手摇充电器小游戏电量与体力消耗同步补丁。
/// </summary>
[HarmonyPatch(typeof(HandCrankMinigame), nameof(HandCrankMinigame.PhysicsUpdate))]
public static class HandCrankMinigame_PhysicsUpdate_MultiplayerPatch {
    public static float crankProgress = 0f;
    public const float CRANK_SEND_RATE = 5f;
    public const float CRANK_CHARGE_AMOUNT = 3.3E-05f;
    public const float CRANK_STAMINA_USE = 0.015f;

    private static void Prefix(HandCrankMinigame __instance) {
        if (!Net.IsRunning || !__instance.held) {
            return;
        }

        float z = __instance.crank.localEulerAngles.z;
        float newAngle = HandCrankMinigame.GetAngleFromVector(Minigame.game.handPos.normalized);
        crankProgress += Mathf.Abs(Mathf.DeltaAngle(newAngle, z));

        if (crankProgress > CRANK_SEND_RATE
            && NetObjectRegistry.TryGetSyncInfo(Minigame.game.currentItem, out SyncInfo si)
            && Net.IsClient) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_HandCrankMinigame);
            writer.Put(si.syncId);
            writer.Put(crankProgress);
            crankProgress = 0f;
            Net.Client_Send(DeliveryMethod.Unreliable, writer);
        }
    }
}

/// <summary>
/// 修正手动除颤仪电量估算区间始终按本地操控者智力计算。
/// </summary>
[HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.GetEstimationRange))]
internal static class ManualDefibMinigame_GetEstimationRange_MultiplayerPatch {
    private static bool Prefix(ref float __result) {
        if (!Net.IsRunning) {
            return true;
        }
        Body localBody = Util.GetLocalBodyNullable();
        float range = localBody != null ? (70f - localBody.skills.INTFrom10 * 5f) : 70f;
        __result = Mathf.Max(5f, range);
        return false;
    }
}

/// <summary>
/// 联机下屏蔽密码锁彩蛋爆炸，避免未同步单边自爆摧毁地形；单机下保留原版彩蛋。
/// </summary>
[HarmonyPatch(typeof(KeypadMinigame), nameof(KeypadMinigame.CheckForMegalovania))]
internal static class KeypadMinigame_CheckForMegalovania_MultiplayerPatch {
    private static bool Prefix() {
        return !Net.IsRunning;
    }
}

/// <summary>
/// 撬锁卡死断爪/扣耐久事件上报补丁。
/// </summary>
[HarmonyPatch(typeof(LockpingMinigame), nameof(LockpingMinigame.Update))]
public static class LockpingMinigame_Update_MultiplayerPatch {
    private static readonly FieldInfo TimeWasStuckField = AccessTools.Field(typeof(LockpingMinigame), "timeWasStuck");

    private static void Prefix(LockpingMinigame __instance) {
        if (!Net.IsRunning) {
            return;
        }

        if (__instance.clickingInside
            && __instance.lockProgress >= __instance.MaxTurnProgress()
            && __instance.lockProgress < 1f
            && TimeWasStuckField != null) {
            float timeWasStuck = (float)TimeWasStuckField.GetValue(__instance);
            if (timeWasStuck + Time.deltaTime > 0.5f) {
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_LockpingMinigame_breakclaw, false);
            }
        }

        float maxProgress = 1f - (Time.deltaTime * (0.66f + (__instance.pickLevel + 1) * 0.065f * 1.1f));
        if (__instance.lockProgress > maxProgress && __instance.toDestroy != null) {
            NetObjectRegistry.TryGetSyncInfoOrRegister(__instance.toDestroy, out _);
        }
    }
}

/// <summary>
/// 密码锁按键点击上报与防本地双重输入覆盖补丁。
/// </summary>
[HarmonyPatch(typeof(KeypadMinigame), nameof(KeypadMinigame.CheckCasts))]
public static class KeypadMinigame_CheckCasts_MultiplayerPatch {
    private static readonly FieldInfo ClearButtonField = AccessTools.Field(typeof(KeypadMinigame), "clearButton");

    public static float GetUsabilityDistanceMP(UsableObject uo) {
        return 10f * uo.rangeMultiplier * 1.4f;
    }

    public static bool CheckIfCloseEnoughForMinigame(UsableObject uo, NetBody pb) {
        if (uo == null || pb == null) {
            return false;
        }
        return KM.DistanceCheck(uo.transform.position, pb.pos, GetUsabilityDistanceMP(uo));
    }

    private static bool Prefix(KeypadMinigame __instance, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) {
            return true;
        }

        if (__instance.toDestroy == null
            || ScavPlayer.LocalPlayer == null
            || !CheckIfCloseEnoughForMinigame(__instance.toDestroy.GetComponent<UsableObject>(), ScavPlayer.LocalPlayer.netBody)) {
            MinigameBase.main.EndMinigame();
            return false;
        }

        if (NetObjectRegistry.TryGetSyncInfo(__instance.toDestroy.gameObject, out SyncInfo si)) {
            int keyNumber = -1;
            GameObject clearBtn = ClearButtonField?.GetValue(__instance) as GameObject;

            for (int i = 0; i < uiCasts.Count; i++) {
                GameObject hitObj = uiCasts[i].gameObject;
                if (__instance.inputButtons.Contains(hitObj)) {
                    int.TryParse(hitObj.transform.GetChild(0).GetComponent<TextMeshProUGUI>().text, out keyNumber);
                    break;
                }
                if (hitObj == clearBtn) {
                    keyNumber = 10;
                    break;
                }
            }

            if (keyNumber != -1) {
                NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_KeypadMinigame_Keypress);
                writer.Put(si.syncId);
                writer.Put((byte)keyNumber);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }

        return true;
    }

    private static void Postfix(KeypadMinigame __instance) {
        if (ScavPlayer.LocalPlayer != null
            && MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(ScavPlayer.LocalPlayer) is KeypadMinigameSession kpSession) {
            __instance.current = kpSession.current_input;
        }
    }
}

/// <summary>
/// 医疗耗材（注射器、绷带、义肢修补）与机关设备（手摇发电、撬锁、密码锁）小游戏网络接收器。
/// </summary>
internal static class ItemsAndDevicesMinigameReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_SyringeMinigameInjectShit)]
    private static void Server_SyringeInject(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syringeSyncId);
        reader.Get(out float ml);
        reader.Get(out knetid targetClientId);
        reader.Get(out byte limbId);
        reader.Get(out bool doSound);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, targetClientId, limbId, syringeSyncId, out ScavPlayer healer, out NetBody target, out SyncInfo si)
            && Util.IsFinite(ml)
            && si.IsLiquidContainer()) {
            if (!healer.IsLocal) {
                target.GetCoUtilsInstance().SelectForMPOverride();
                si.liquidcontainer.Inject(target.body.limbs[limbId], ml);
            }
            if (doSound) {
                ServerMain.Server_AnnounceSound(target.body.transform.position, "syringe", ServerMain.GetListOfClientIdsExceptThis(clientId));
            }
            return;
        }

        if (healer != null) {
            healer.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_syringeinject"));
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healer);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_BandageMinigame_DoBandageAction)]
    private static void Server_DoBandageAction(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid bandageId);
        reader.Get(out knetid targetPlr);
        reader.Get(out byte limbId);

        if (MedicalSync.Server_CheckCanHealerUseItemForLimb(clientId, targetPlr, limbId, bandageId, out ScavPlayer healerPlr, out NetBody targetNb, out SyncInfo itemSi)) {
            Limb limb = targetNb.body.limbs[limbId];
            if (MinigameBase_StartMinigame_MultiplayerPatch.known_bandage_instance_lambdas.TryGetValue((itemSi.item, limb), out OnBandageUse onUseLambda)
                || MinigameBase_StartMinigame_MultiplayerPatch.known_bandages_onuse_lambdas.TryGetValue(itemSi.item.id, out onUseLambda)) {
                if (!healerPlr.IsLocal) {
                    healerPlr.GetCoUtilsInstance().SelectForMPOverride();
                    onUseLambda.Invoke(1f / 18f);
                    if (itemSi.item != null && itemSi.item.condition < 0f) {
                        itemSi.item.condition = 0f;
                    }
                }

                BandageMinigame_DoBandageAction_MultiplayerPatch.bandagethingcounter++;
                if (BandageMinigame_DoBandageAction_MultiplayerPatch.bandagethingcounter > 14) {
                    if (healerPlr.netBody != targetNb) {
                        targetNb.body.happiness += 0.2f;
                    }
                    ServerMain.Server_AnnounceSound(targetNb.pos, "bandage", ServerMain.GetListOfClientIdsExceptThis(clientId));
                    BandageMinigame_DoBandageAction_MultiplayerPatch.bandagethingcounter = 0;
                }
                return;
            }

            ModLog.Error($"服务端未找到对应绷带闭包定义: itemId={itemSi.item.id}, sync={itemSi}", LogDomain.Medical);
            healerPlr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("serverdeny_bandage1"));
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healerPlr);
            return;
        }

        if (healerPlr != null) {
            MinigameMPManager.Server_ForceEndMinigameForPlayer(healerPlr);
        }
    }

#if FULLGAME
    [CTPostInit]
    private static void InitProsthesisPatchCommand() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.ProsthesisPatch] = (plr, cmd, args) => {
            if (plr == null || args == null || args.Length < 5) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort itemId)
                || !ushort.TryParse(args[2], out ushort bodyId)
                || !byte.TryParse(args[3], out byte limbId)
                || !int.TryParse(args[4], out int flags)) {
                return;
            }
            if ((flags & 3) == 0) {
                return;
            }
            if (!MedicalSync.Server_CheckCanHealerUseItemForLimb(plr.playerId, bodyId, limbId, itemId, out ScavPlayer healer, out NetBody target, out SyncInfo itemSi)) {
                return;
            }
            if (healer == null || healer.IsLocal || target == null || target.body == null || itemSi == null || itemSi.item == null) {
                return;
            }
            if (limbId >= target.body.limbs.Length) {
                return;
            }
            Limb limb = target.body.limbs[limbId];
            if (limb == null || limb.dismembered || limb.dislocated || !limb.HasProsthetic()) {
                return;
            }
            int clientHint = 0;
            if (args.Length >= 6) {
                int.TryParse(args[5], NumberStyles.Integer, CultureInfo.InvariantCulture, out clientHint);
            }
            int holeCount = ProsthesisRepair_RememberPatchCount.ResolveAuthoritativeHoleCount(itemSi.item, clientHint);
            if (holeCount < 1) {
                return;
            }

            int holes = limb.prostheticInfo.maxHoles;
            if (holes < 1) {
                return;
            }

            bool placed = false;
            float step = 100f / holes;
            if ((flags & 1) != 0 && limb.muscleHealth < 99.9f && itemSi.item.condition > 0.0001f) {
                limb.muscleHealth += step;
                SpendPatch(itemSi.item, holeCount);
                placed = true;
            }
            if ((flags & 2) != 0 && itemSi.item.condition > 0.0001f) {
                if (!limb.broken) {
                    limb.broken = true;
                    if (limb.boneHealTimer < 1f) {
                        limb.boneHealTimer = 100f;
                    }
                }
                limb.boneHealTimer -= step;
                if (limb.boneHealTimer < 5f) {
                    limb.MendBone();
                }
                SpendPatch(itemSi.item, holeCount);
                placed = true;
            }
            if (!placed) {
                return;
            }

            if (itemSi.item.condition <= 0f) {
                ProsthesisRepair_RememberPatchCount.PatchCount.Remove(itemId);
            }

            itemSi.WriteObjectIntoPacket();
            MedicalSync.Server_QueueSendCharacterHealth(target, true);
            ServerMain.Server_AnnounceSound(target.pos, "prosthesisrepair", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));
        };
    }

    private static void SpendPatch(Item item, int holeCount) {
        item.condition -= (1f / holeCount) - 0.0015f;
        if (item.condition < 0.01f) {
            item.condition = 0f;
        }
    }
#endif

    [ServerReceiver((byte)NetmsgId.SERVER_HandCrankMinigame)]
    private static void Server_HandCrankMinigame(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        reader.Get(out float crankProgress);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out NetBody pb)
            && pb.body != null
            && ItemSync.TryGetItemSyncInfo(syncId, out SyncInfo si)
            && ItemSync.CheckIfBodyReachThisItem(si, pb.body, GameplayRanges.ServerExchangeToleranceDistance, false)
            && crankProgress.IsFinite()) {
            if (si.item.IsHandcrank()) {
                si.item.battery.DrainCharge(-(crankProgress * HandCrankMinigame_PhysicsUpdate_MultiplayerPatch.CRANK_CHARGE_AMOUNT));
                pb.body.stamina -= crankProgress * HandCrankMinigame_PhysicsUpdate_MultiplayerPatch.CRANK_STAMINA_USE;
                si.item.condition = Mathf.Clamp01(si.item.condition);
            } else {
                MinigameMPManager.NoteSlow("hand crank dropped, not a crank");
            }
        } else {
            MinigameMPManager.NoteSlow("hand crank dropped, reach or sync");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_LockpingMinigame_breakclaw)]
    private static void Server_LockpickBreakClaw(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out Body lockpicker) || !lockpicker.conscious) {
            return;
        }

        ServerMain.Server_AnnounceSound(
            lockpicker.slots[lockpicker.handSlot].limb.transform.position,
            "gore2",
            ServerMain.GetListOfClientIdsExceptThis(clientId));

        lockpicker.FindByIdSurface("lockpickingkit", out Item kit);
        if (lockpicker.skills.INT < 10) {
            kit = null;
        }
        if (kit != null) {
            kit.SetCondition(kit.condition - 0.03f);
        } else {
            lockpicker.slots[lockpicker.handSlot].limb.pain += 20f;
            lockpicker.clawHealth -= 15f;
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_KeypadMinigame_Keypress)]
    private static void Server_KeypadKeypress(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        reader.Get(out byte keypadNum);

        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)
            || plr.body == null || !plr.body.conscious
            || !NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si)
            || !si.IsBuilding()
            || !si.go.TryGetComponent<UsableObject>(out UsableObject uo)
            || !si.go.TryGetComponent<Openable>(out Openable openable)
            || !openable.isKeypad) {
            return;
        }

        if (keypadNum > 10) {
            ModLog.Warn($"忽略异常密码锁按键编号: {plr} -> {keypadNum}", LogDomain.Medical);
            return;
        }

        if (MinigameMPManager.GetSessionThisPlayerIsInvolvedIn(plr) is not KeypadMinigameSession kpSess) {
            return;
        }
        if (!KeypadMinigame_CheckCasts_MultiplayerPatch.CheckIfCloseEnoughForMinigame(uo, plr.netBody)) {
            MinigameMPManager.Server_ForceEndMinigameForPlayer(plr);
            return;
        }

        if (keypadNum == 10) {
            keypadNum = 0;
            kpSess.current_input = string.Empty;
        } else {
            kpSess.current_input += keypadNum.ToString();
        }

        if (kpSess.current_input.Length > kpSess.match.Length) {
            kpSess.current_input = kpSess.current_input.Substring(0, kpSess.match.Length);
        } else {
            kpSess.AnnounceCurrentInput();
            if (kpSess.current_input == kpSess.match) {
                kpSess.building.SetHealth(0f);
            }
        }

        ServerMain.Server_AnnounceSound(
            plr.body.transform.position,
            VanillaAssetKeys.SoundBeep + keypadNum,
            ServerMain.GetListOfClientIdsExceptThis(clientId));
    }

#if DEBUG
    [CTPostInit]
    private static void RegisterDebugCommands() {
        Con.RegisterCommandModern("testcoutils", "DEBUG MP - Do a coutils test scenario", (split, comm) => {
            if (Net.IsServer) {
                Con.RunCommand("give @a bruisekit combatpen bruisekit combatpen bigpack pistol");
                foreach (NetBody item in NetBody.all_instances) {
                    item.body.GetLimb(Body.LimbNum.LegF).muscleHealth *= 0.4f;
                    item.body.GetLimb(Body.LimbNum.LegF).skinHealth *= 0.4f;
                }
            }
        });
    }
#endif
}
