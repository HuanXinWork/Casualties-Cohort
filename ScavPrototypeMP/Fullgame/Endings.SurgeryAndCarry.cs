#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 终局手术台（<see cref="SurgicalDeviceBehavior"/>）与蓝色心脏摘除同步器：
/// 腹部麻醉达标的客户端在本地运行剖腹取物小游戏，完成后上报房主摘除服务端货物标记并为该玩家单独放行逃生门。
/// </summary>
public static class MacroFleshRebuilderMp {
    public static bool LocallyConsumed;
    public static bool CargoClearedLocally;
    public static bool EscapeGranted;
    public static bool EscapeStarted;
    static readonly HashSet<knetid> HeartAccepted = new HashSet<knetid>();
    private static readonly AccessTools.FieldRef<CosmeticDoor, bool> SurgeryDoorOpen = AccessTools.FieldRefAccess<CosmeticDoor, bool>("open");

    public static void ResetLayer() {
        LocallyConsumed = false;
        CargoClearedLocally = false;
        EscapeGranted = false;
        EscapeStarted = false;
        HeartAccepted.Clear();
    }

    public static bool HasRemovedHeart(knetid id) {
        return HeartAccepted.Contains(id);
    }

    /// <summary>
    /// 房主校验并受理玩家完成手术摘除蓝色心脏请求（仅限第 11 层终局实验室逃生阶段且存活的无芯片玩家）。
    /// </summary>
    public static void HostAcceptHeartRemoved(ScavPlayer plr) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        EndLabScript lab = EndLabScript.instance;
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11 || lab == null) {
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr.playerId.id);
            return;
        }
        if (!plr.IsLocal) {
            if (plr.body == null || !plr.body.alive || !plr.IsUnchipped() || (!ConcurrentEndings.ScientistDead && !lab.doingEscape)) {
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr.playerId.id);
                return;
            }
        }
        if (!HeartAccepted.Add(plr.playerId)) {
            return;
        }
        FindTheBugInator.Mark(CustomWireCommands.BlueHeartRemoved, plr.playerName ?? "?");
        if (plr.body != null) {
            EndingCarry.DropFor(plr.netBody);
        }
        if (plr.body != null) {
            if (plr.body.hasCargo) {
                plr.body.RemoveCargo();
            }
            if (plr.body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
        if (plr.IsLocal) {
            GrantLocalEscape();
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.BlueHeartRemoved);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    public static void GrantLocalEscape() {
        if (EscapeGranted) {
            OpenLocalSurgeryDoors(permanent: true);
            return;
        }
        EscapeGranted = true;
        if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null) {
            EndingCarry.DropFor(ScavPlayer.LocalPlayer.netBody);
        }
        EnsureSurgicalDeviceUsable();
        OpenLocalSurgeryDoors(permanent: true);
        EndLabScript lab = EndLabScript.instance;
        if (lab == null) {
            return;
        }
        lab.doTimer = false;
        if (lab.removedHeart != null) {
            lab.removedHeart.SetActive(true);
        }
        if (lab.thoughtUi != null) {
            lab.thoughtUi.text = Locale.GetOther("cargosurgery6");
        }
        if (MusicManager.main != null && lab.end2 != null) {
            MusicManager.main.PlaySong(lab.end2);
        }
        FindTheBugInator.Mark("surgery-exit");
    }

    public static void NoteLocalCargoRemoved(Body body) {
        if (body == null || !Util.IsBodyLocal(body)) {
            return;
        }
        CargoClearedLocally = true;
        LocallyConsumed = true;
        if (body.TryGetNetBody(out NetBody carrier)) {
            EndingCarry.DropFor(carrier);
        }
        EnsureSurgicalDeviceUsable();
        if (!Net.IsRunning) {
            GrantLocalEscape();
            return;
        }
        if (Net.IsServer) {
            if (ScavPlayer.LocalPlayer != null) {
                HostAcceptHeartRemoved(ScavPlayer.LocalPlayer);
            }
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.BlueHeartRemoved);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static bool BlocksCarry(NetBody carrier) {
        if (carrier == null) {
            return false;
        }
        bool local = carrier.IsLocal || (carrier.plr != null && carrier.plr.IsLocal);
        if (local) {
            return LocallyConsumed || CargoClearedLocally || EscapeGranted;
        }
        return Net.IsServer && carrier.plr != null && HeartAccepted.Contains(carrier.plr.playerId);
    }

    public static bool BlockCargoReembed(Body body) {
        if (!CargoClearedLocally || body == null || !Util.IsBodyLocal(body)) {
            return false;
        }
        return true;
    }

    public static void ConsumeLocalPrompt() {
        LocallyConsumed = true;
        EnsureSurgicalDeviceUsable();
    }

    public static void EnsureSurgicalDeviceUsable() {
        SurgicalDeviceBehavior[] devices = UnityEngine.Object.FindObjectsByType<SurgicalDeviceBehavior>(FindObjectsSortMode.None);
        for (int i = 0; i < devices.Length; i++) {
            SurgicalDeviceBehavior device = devices[i];
            if (device == null || device.GetComponent<UsableObject>() != null) {
                continue;
            }
            device.gameObject.AddComponent<UsableObject>();
        }
    }

    public static void OpenLocalSurgeryDoors(bool permanent = false) {
        EndLabScript[] labs = UnityEngine.Object.FindObjectsByType<EndLabScript>(FindObjectsSortMode.None);
        for (int i = 0; i < labs.Length; i++) {
            EndLabScript lab = labs[i];
            if (lab != null && lab.surgeryDoor != null) {
                ForceSurgeryDoorOpen(lab.surgeryDoor, permanent);
            }
        }
    }

    public static void ForceSurgeryDoorOpen(CosmeticDoor door, bool permanent) {
        if (door == null) {
            return;
        }
        door.forceDisable = false;
        if (!permanent) {
            return;
        }
        try {
            SurgeryDoorOpen(door) = true;
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.Story, $"MacroFleshRebuilderMp: failed to set CosmeticDoor.open: {ex.Message}");
        }
        Collider2D col = door.GetComponent<Collider2D>();
        if (col != null) {
            col.enabled = false;
        }
    }

    public static void KeepSurgeryDoorOpenDuringEscape(EndLabScript lab) {
        if (!EscapeGranted || lab == null || lab.surgeryDoor == null) {
            return;
        }
        ForceSurgeryDoorOpen(lab.surgeryDoor, permanent: true);
    }

    /// <summary>将注射在角色躯干 <see cref="CoUtils"/> 上的麻醉计时镜像回原版逃生协程轮询的全局本地单例。</summary>
    public static void MirrorAbdomenAnesthetic(EndLabScript lab) {
        if (lab == null || lab.canStartSurgery || !lab.doTimer) {
            return;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null) {
            return;
        }
        CoUtils onBody = body.GetComponent<CoUtils>();
        if (onBody == null) {
            return;
        }
        const string key = "localanestheticDownTorso";
        const int lowerTorsoIdx = (int)LimbIndex.LowerTorso;
        float need = ConcurrentEndings.SurgeryAnestheticDurationRequired;
        float left = onBody.DurationOf(key);
        if (left < need && body.limbs != null && body.limbs.Length > lowerTorsoIdx && body.limbs[lowerTorsoIdx] != null) {
            string named = "localanesthetic" + body.limbs[lowerTorsoIdx].name;
            if (named != key) {
                left = onBody.DurationOf(named);
            }
        }
        if (left < need) {
            return;
        }
        CoUtils local = CoUtilsMultiplayer.GetLocalCoUtilsInstance();
        if (local == null || local == onBody || local.DurationOf(key) >= need) {
            return;
        }
        local.DoTimedOp(key, () => { }, left);
    }

    public static void TryStartEscapeFromPit(Body body) {
        if (EscapeStarted || !EscapeGranted || body == null || body.hasCargo) {
            return;
        }
        if (WorldGeneration.world == null) {
            return;
        }
        Vector2 p = body.transform.position;
        if (p.y <= 270f || p.x >= 370f) {
            return;
        }
        OpenLocalSurgeryDoors(permanent: true);
        WorldGeneration.world.StartCoroutine(WorldGeneration.world.EscapeEnding());
    }
}

[HarmonyPatch(typeof(SurgicalDeviceBehavior), nameof(SurgicalDeviceBehavior.OnUse))]
static class SurgicalDeviceBehavior_OnUse_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        const int lowerTorsoIdx = (int)LimbIndex.LowerTorso;
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body == null || body.limbs == null || body.limbs.Length <= lowerTorsoIdx || body.limbs[lowerTorsoIdx] == null) {
            return false;
        }
        if (MacroFleshRebuilderMp.EscapeGranted || (MacroFleshRebuilderMp.CargoClearedLocally && !body.hasCargo)) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            }
            return false;
        }
        if (!body.hasCargo || !ConcurrentEndings.HasEnoughAbdomenAnesthetic()) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            }
            return false;
        }
        MacroFleshRebuilderMp.EnsureSurgicalDeviceUsable();
        if (MinigameBase.main != null) {
            MinigameBase.main.StartMinigame(new CargoMinigame(body.limbs[lowerTorsoIdx]), null);
        }
        return false;
    }

    static void Postfix() {
        if (!Net.IsRunning || MinigameBase.main == null || MinigameBase.main.currentMinigame is not CargoMinigame) {
            return;
        }
        MacroFleshRebuilderMp.ConsumeLocalPrompt();
    }
}

/// <summary>手术小游戏进行期间持续将受术下腹部疼痛与抽搐倍率归零，防止局部麻药中途衰减破坏 UI 操作。</summary>
[HarmonyPatch(typeof(CargoMinigame), nameof(CargoMinigame.Update))]
static class CargoMinigame_Update_NoPain_MultiplayerPatch {
    static void Postfix(CargoMinigame __instance) {
        if (!Net.IsRunning || __instance == null || __instance.limb == null) {
            return;
        }
        __instance.limb.pain = 0f;
        __instance.limb.forceMult = 0f;
    }
}

[HarmonyPatch(typeof(EndLabScript), "Update")]
static class EndLabScript_Update_PitEnding_MultiplayerPatch {
    static void Postfix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        MacroFleshRebuilderMp.KeepSurgeryDoorOpenDuringEscape(__instance);
        if (PlayerCamera.main == null) {
            return;
        }
        MacroFleshRebuilderMp.MirrorAbdomenAnesthetic(__instance);
        MacroFleshRebuilderMp.TryStartEscapeFromPit(PlayerCamera.main.body);
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.EscapeEnding))]
static class WorldGeneration_EscapeEnding_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        if (MacroFleshRebuilderMp.EscapeStarted) {
            __result = Done();
            return false;
        }
        MacroFleshRebuilderMp.EscapeStarted = true;
        FindTheBugInator.Mark("escape-ending");
        return true;
    }

    static IEnumerator Done() {
        yield break;
    }
}

/// <summary>终局阶段背负限制规则：禁止背负即将引爆项圈的芯片玩家，也禁止已完成剖腹取心手术的玩家继续背人穿门。</summary>
public static class EndingCarry {
    public static bool Blocked(NetBody carried, NetBody carrier) {
        if (carried == null || carrier == null) {
            return false;
        }
        if (ConcurrentEndings.ScientistDead && IsChippedPlayer(carried)) {
            return true;
        }
        return MacroFleshRebuilderMp.BlocksCarry(carrier);
    }

    public static void DropIllegal() {
        List<NetBody> drop = null;
        foreach (NetBody nb in NetBody.all_instances) {
            if (nb == null || nb.piggybacking_on == null) {
                continue;
            }
            if (!Blocked(nb, nb.piggybacking_on)) {
                continue;
            }
            if (drop == null) {
                drop = new List<NetBody>();
            }
            drop.Add(nb);
        }
        if (drop == null) {
            return;
        }
        for (int i = 0; i < drop.Count; i++) {
            if (drop[i] != null && drop[i].piggybacking_on != null) {
                drop[i].StopPiggyback();
            }
        }
    }

    public static void DropFor(NetBody carrier) {
        if (carrier == null) {
            return;
        }
        NetBody carried = carrier.carrying_person;
        if (carried != null && carried.piggybacking_on != null) {
            carried.StopPiggyback();
        }
    }

    static bool IsChippedPlayer(NetBody nb) {
        if (nb == null || nb.plr == null) {
            return false;
        }
        return !nb.plr.IsUnchipped();
    }
}
#endif
