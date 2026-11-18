using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 生物终端（<see cref="BioTerminalScript"/>）联机使用同步补丁。
/// </summary>
[HarmonyPatch(typeof(BioTerminalScript), nameof(BioTerminalScript.OnUse))]
public static class BioTerminalScript_OnUse_MultiplayerPatch {
    private const float DoorUnlockRadius = 6f;
    private const float RequiredBloodMl = 100f;

    public static void OnUseRewritten(Body user, BuildingEntity building) {
        bool drained = false;
        foreach (Item item in user.GetAllItemsThorough()) {
            if (item.TryGetComponent<WaterContainerItem>(out var container) && container.AmountOf("redblood") >= RequiredBloodMl) {
                drained = true;
                container.Drain(container.CalculateDrainSingleLiquid("redblood", RequiredBloodMl));
                break;
            }
        }
        if (drained) {
            building.Backgroundify();
            Sound.Play(VanillaAssetKeys.SoundBeep, building.transform.position);
            Collider2D[] hits = Physics2D.OverlapCircleAll(building.transform.position, DoorUnlockRadius);
            for (int i = 0; i < hits.Length; i++) {
                if (hits[i].TryGetComponent<BuildingEntity>(out var door) && door.id == "reinforceddoor") {
                    door.Backgroundify();
                }
            }
        }
    }

    static void Postfix(BioTerminalScript __instance) {
        if (!Net.IsRunning)
            return;
        var tracker = __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
        if (tracker.is_backgroundified && NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si)) {
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_BioTerminalScriptOpenShitheadBruh, si.syncId, true);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_BioTerminalScriptOpenShitheadBruh)]
    static void ReceiveBioTerminalOpen(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid terminalSyncId);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body) &&
            NetObjectRegistry.TryGetSyncInfo(terminalSyncId, out SyncInfo si) &&
            (clientId == 0 ||
             (user.body.conscious &&
              si.IsBuilding() &&
              si.go.TryGetComponent<TrackerBuildingEntityBackgroundified>(out var tr) &&
              !tr.is_backgroundified &&
              si.go.TryGetComponent<BioTerminalScript>(out _) &&
              KM.DistanceCheck(body, si.building, GameplayRanges.ServerExchangeToleranceDistance)))) {
            if (clientId != 0) {
                OnUseRewritten(body, si.building);
            }
            ServerMain.Server_AnnounceSound(si.go.transform.position, VanillaAssetKeys.SoundBeep, ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(si.go.transform.position, $"S: open BioTerminal {user}");
            return;
        }

        ModLog.Flow(LogDomain.World, "BioTerminal.Denied", $"client={clientId} syncId={terminalSyncId}");
    }
}

/// <summary>
/// 钻探舱（<see cref="DrillPod"/>）修复联机同步补丁：统一交由服务端消耗修理包并切换工作状态，防止客户端单边消耗物品而房主未修复。
/// </summary>
[HarmonyPatch(typeof(DrillPod), nameof(DrillPod.OnUse))]
public static class DrillPod_OnUse_MultiplayerPatch {
    public static void OnUseRewritten(Body user, DrillPod pod) {
        if (user == null || pod == null)
            return;
        if (!user.FindByIdThorough("drillrepairkit", out var kit))
            return;

        UnityEngine.Object.Destroy(kit.gameObject);
        ForceRepairDrill(pod);
    }

    public static void ForceRepairDrill(DrillPod pod) {
        if (pod == null)
            return;

        pod.working = true;
        Sound.Play("drillpodrepair", pod.transform.position, twoDimensional: false, pitchShift: false);
        BuildingEntity be = pod.GetComponent<BuildingEntity>();
        if (be != null) {
            be.description = Locale.GetBuilding("drillpoddscfixed");
        }
        UsableObject usable = pod.GetComponent<UsableObject>();
        if (usable != null) {
            UnityEngine.Object.Destroy(usable);
        }
    }

    static bool Prefix(DrillPod __instance) {
        if (!Net.IsRunning)
            return true;
        if (!NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out SyncInfo si))
            return false;

        si.SetIgnoreTimeForRoundTrip();
        si.AwaitServerAck();
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_DrillPodRepairTypeShit, si.syncId, true);
        return false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_DrillPodRepairTypeShit)]
    static void ReceiveDrillPodRepair(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid podSyncId);
        using var _ack = UseAck.After(clientId, podSyncId);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body) &&
            NetObjectRegistry.TryGetSyncInfo(podSyncId, out SyncInfo si) &&
            si.go != null &&
            si.go.TryGetComponent(out DrillPod pod) &&
            !pod.working &&
            (clientId == 0 ||
             (user != null && user.body != null && user.body.conscious &&
              si.IsBuilding() &&
              KM.DistanceCheck(body, si.building, GameplayRanges.ServerExchangeToleranceDistance)))) {
            OnUseRewritten(body, pod);
            if (!pod.working) {
                ForceRepairDrill(pod);
            }
            ServerMain.Server_AnnounceSound(si.go.transform.position, "drillpodrepair", ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(si.go.transform.position, $"S: DrillPod Repair {user}");
            }
            return;
        }

        ModLog.Flow(LogDomain.World, "DrillPodRepair.Denied", $"client={clientId} syncId={podSyncId}");
    }
}

/// <summary>
/// 救生舱加热器与淋浴按钮（<see cref="LifepodButton"/>）联机状态广播补丁。
/// </summary>
[HarmonyPatch(typeof(LifepodButton), nameof(LifepodButton.OnUse))]
public static class LifepodButton_OnUse_MultiplayerPatch {
    static bool Prefix(LifepodButton __instance) {
        if (!Net.IsRunning)
            return true;

        BuildingEntity parentBuilding = __instance.transform.parent.GetComponent<BuildingEntity>();
        if (parentBuilding && !NetObjectRegistry.TryGetSyncInfoOrRegister(parentBuilding.gameObject, out _)) {
            ModLog.Warning($"Attempted to use unregistered lifepod button {parentBuilding.id} at {(Vector2)parentBuilding.transform.position}, aborting.", LogDomain.World);
            NetObjectRegistry.AlertObjectNotRegistered(true);
            return false;
        }
        return true;
    }

    static void Postfix(LifepodButton __instance) {
        if (!Net.IsRunning)
            return;

        BuildingEntity parentBuilding = __instance.transform.parent.GetComponent<BuildingEntity>();
        if (parentBuilding && NetObjectRegistry.TryGetSyncInfo(parentBuilding.gameObject, out var si)) {
            ModLog.Flow(LogDomain.World, "LifepodButton.Send", $"type={__instance.type} si={si}");
            Multiplayer.Client_SendSimpleMessageToServer(
                (byte)NetmsgId.SERVER_LifepodButtonActivate,
                si.syncId,
                (byte)__instance.type,
                (byte)__instance.controller.heatState,
                true);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_LifepodButtonActivate)]
    static void ReceiveLifepodButtonActivate(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid lifepodSyncId);
        reader.Get(out byte buttonType);
        reader.Get(out byte heatState);

        SyncInfo si = null;
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body) &&
            NetObjectRegistry.TryGetSyncInfo(lifepodSyncId, out si) &&
            si.IsBuilding() &&
            KM.DistanceCheck(si.go.transform.position, body.transform.position, GameplayRanges.ServerExchangeToleranceDistance)) {
            LifepodController controller = si.go.GetComponentInChildren<LifepodController>();
            if (controller != null) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(si.go.transform.position, $"S: LifepodButtonActivate {buttonType}:{(buttonType == 0 ? "HEATER" : "SHOWER")} HEAT:{heatState}");

                if (buttonType != 0) {
                    if (controller.shower != null) {
                        controller.ActivateShower();
                    } else {
                        log.error_devevent($"LIFEPOD SHOWER BUTTON: {si} DOES NOT HAVE A SHOWER", si.transformPosition);
                    }
                }

                controller.heatState = Mathf.Max(-1, heatState - 1);
                if (controller.heater != null) {
                    controller.ToggleHeatState();
                } else {
                    log.error_devevent($"LIFEPOD HEAT TOGGLE: {si} DOES NOT HAVE A HEATER", si.transformPosition);
                }

                NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_LifepodButtonActivateRelay);
                writer.Put(lifepodSyncId);
                writer.Put(buttonType);
                writer.Put((byte)controller.heatState);
                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                return;
            }
        }

        log.serverdeny($"LifepodButton DENIED: client={clientId} user={user} si={si}");
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_LifepodButtonActivateRelay)]
    static void ReceiveLifepodButtonActivateRelay(knetid _, NetDataReader reader) {
        reader.Get(out knetid lifepodSyncId);
        reader.Get(out byte buttonType);
        reader.Get(out byte heatState);

        if (!NetObjectRegistry.TryGetSyncInfo(lifepodSyncId, out SyncInfo si) || !si.IsBuilding())
            return;

        LifepodController controller = si.go.GetComponentInChildren<LifepodController>();
        if (controller == null)
            return;

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
            DebugHelp.OnNetEvent(si.go.transform.position, $"C: LifepodButtonActivate {buttonType} {heatState}");

        if (buttonType != 0) {
            controller.ActivateShower();
        }
        controller.heatState = Mathf.Max(-1, heatState - 1);
        controller.ToggleHeatState();
    }
}

/// <summary>
/// 救生舱淋浴喷头（<see cref="LifepodShower"/>）多玩家清洁与消毒范围重写补丁。
/// </summary>
[HarmonyPatch(typeof(LifepodShower), nameof(LifepodShower.Update))]
public static class LifepodShower_Update_MultiplayerPatch {
    private const float ShowerActiveDuration = 3f;
    private const float ShowerScanRadius = 16f;
    private const float ShowerHalfWidth = 8f;

    static bool Prefix(LifepodShower __instance) {
        if (!Net.IsRunning)
            return true;

        if (Time.time - __instance.timeActive < ShowerActiveDuration) {
            foreach (NetBody plr in ScavPlayer.GetPlayerBodiesInRadius(__instance.transform.position, ShowerScanRadius)) {
                if (Mathf.Abs(__instance.transform.position.x - plr.transform.position.x) < ShowerHalfWidth
                    && __instance.transform.position.y > plr.transform.position.y) {
                    Body body = plr.body;
                    body.dirtyness -= Time.deltaTime * 16.7f;
                    body.snowAmount = Mathf.Min(1f, body.snowAmount + Time.deltaTime * 0.33f);
                    body.eyeScareTime = 0.5f;
                    Limb[] limbs = body.limbs;
                    for (int i = 0; i < limbs.Length; i++) {
                        limbs[i].SetDisinfect(120f);
                    }
                }
            }
        }
        return false;
    }
}

/// <summary>
/// 瞬开型容器建筑（<see cref="Openable"/>）联机开启请求同步补丁。
/// </summary>
[HarmonyPatch(typeof(Openable), nameof(Openable.OnUse))]
public static class Openable_OnUse_MultiplayerPatch {
    static void Postfix(Openable __instance) {
        if (!Net.IsRunning)
            return;

        bool synced = NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si);
        BuildingEntity building = __instance.GetComponent<BuildingEntity>();
        if (synced && building != null && building.GetHealth() == 0f) {
            si.SetIgnoreTimeForRoundTrip();
            si.AwaitServerAck();
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_OpenableOpenTypeShit, si.syncId, true);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_OpenableOpenTypeShit)]
    static void ReceiveOpenableUse(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid openableSyncId);
        using var _ack = UseAck.After(clientId, openableSyncId);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body) &&
            NetObjectRegistry.TryGetSyncInfo(openableSyncId, out SyncInfo si) &&
            si.go != null &&
            si.go.TryGetComponent<Openable>(out var openable) &&
            openable.instantOpen &&
            (clientId == 0 ||
             (user.body != null &&
              user.body.conscious &&
              si.IsBuilding() &&
              KM.DistanceCheck(body, si.building, GameplayRanges.ServerExchangeToleranceDistance)))) {
            if (clientId != 0) {
                openable.OnUse();
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(si.go.transform.position, $"S: Openable open {user}, si:{si}");
            return;
        }

        ModLog.Flow(LogDomain.World, "OpenableUse.Denied", $"client={clientId} syncId={openableSyncId}");
    }
}

/// <summary>
/// 废铁吞噬机（<see cref="ScrapEaterScript"/>）联机投喂与开门同步补丁。
/// </summary>
[HarmonyPatch(typeof(ScrapEaterScript), nameof(ScrapEaterScript.OnUse))]
public static class ScrapEaterScript_OnUse_MultiplayerPatch {
    private const float AdjacentDoorRadius = 2f;

    static void Prefix(ScrapEaterScript __instance) {
        if (!Net.IsRunning || PlayerCamera.main == null || PlayerCamera.main.body == null)
            return;
        if (!PlayerCamera.main.body.FindByIdThorough("scrapmetal", out Item scrapItem))
            return;

        if (NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si)) {
            si.SetIgnoreTimeForRoundTrip();
            si.AwaitServerAck();
            // 原版 ScrapEaterScript.OnUse 会在客户端本地将 scrapItem.condition 归零；必须同步锁定该废铁的 SyncInfo 防在途快照回滚耐久（修复 PR #61 遗漏）
            if (scrapItem != null && NetObjectRegistry.TryGetSyncInfo(scrapItem.gameObject, out SyncInfo scrapSi) && scrapSi != null) {
                scrapSi.SetIgnoreTimeForRoundTrip();
                scrapSi.AwaitServerAck();
            }
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_ScrapEaterScriptUse, si.syncId, true);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ScrapEaterScriptUse)]
    static void ReceiveScrapEaterUse(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid eaterSyncId);
        knetid scrapSyncId = default;
        SyncInfo scrapSi = null;
        Item scrapItem = null;
        bool hasPlayer = ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer user, out Body body);
        if (hasPlayer && body != null && body.FindByIdThorough("scrapmetal", out scrapItem) && scrapItem != null
            && NetObjectRegistry.TryGetSyncInfo(scrapItem.gameObject, out scrapSi) && scrapSi != null) {
            scrapSyncId = scrapSi.syncId;
        }

        try {
            if (hasPlayer &&
                NetObjectRegistry.TryGetSyncInfo(eaterSyncId, out SyncInfo si) &&
                si.go != null &&
                si.go.TryGetComponent<ScrapEaterScript>(out var scrapEater) &&
                (clientId == 0 ||
                 (user.body != null &&
                  user.body.conscious &&
                  si.IsBuilding() &&
                  KM.DistanceCheck(body, si.building, GameplayRanges.ServerExchangeToleranceDistance)))) {
                if (scrapItem == null)
                    return;

                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(si.go.transform.position, $"S: ScrapEaterScript open {user}, si:{si}");

                if (clientId != 0) {
                    scrapEater.scrapAmount += scrapItem.condition;
                    scrapItem.condition = 0f;
                    if (scrapSi != null && !scrapSi.IsDestroyed) {
                        scrapSi.WriteObjectIntoPacket();
                    }
                    if (scrapEater.scrapAmount < ScrapEaterScript.target)
                        return;

                    scrapEater.build.Backgroundify();
                    Collider2D[] hits = Physics2D.OverlapCircleAll(scrapEater.transform.position, AdjacentDoorRadius);
                    for (int i = 0; i < hits.Length; i++) {
                        if (hits[i].TryGetComponent<BuildingEntity>(out var door) && door.id == "reinforceddoor") {
                            door.Backgroundify();
                        }
                    }
                }

                ServerMain.Server_AnnounceSound(si.go.transform.position, VanillaAssetKeys.SoundBeep, ServerMain.GetListOfClientIdsExceptThis(clientId));
                return;
            }

            ModLog.Flow(LogDomain.World, "ScrapEaterUse.Denied", $"client={clientId} syncId={eaterSyncId}");
        } finally {
            // 覆盖接受、未满额提前返回、缺废铁或距离/昏迷拒绝等全部返回路径，同时解除机关与废铁的客户端预测锁
            UseAck.Send(clientId, eaterSyncId);
            if (scrapSyncId.id != 0) {
                UseAck.Send(clientId, scrapSyncId);
            }
        }
    }
}
