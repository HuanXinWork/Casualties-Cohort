#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 终局实验室（第 11 层）科学家演出门控与共享血池控制器：
/// 各端保留本地生成的科学家实体以匹配各自物种台词，待全员清除入场卡片且有人踏入房间（x &lt; 17）后统一放行对话；
/// 科学家生命值由房主权威维护并向客机广播。
/// </summary>
public static class ScientistGate {
    /// <summary>全员就绪且有人进入房间后置为 true，放行科学家对话协程。</summary>
    public static bool Go;
    /// <summary>本机科学家绑定的可受击建筑实体。</summary>
    public static BuildingEntity HealthBuilding;

    private const float MaxValidDamageDelta = 5000f;
    private const float MaxLabDamageReportDistance = 80f;

    static readonly HashSet<knetid> Ready = new();
    static bool _sentReady;
    static bool _hostSelfReady;
    static bool _applyingHp;

    public static void Reset() {
        Go = false;
        _sentReady = false;
        _hostSelfReady = false;
        Ready.Clear();
    }

    /// <summary>
    /// 为本机生成的科学家对象树打上忽略网络追踪标记，避免被通用实体同步覆盖，同时捕获其受击血量组件。
    /// </summary>
    public static void KeepLocal(EndLabScript lab) {
        if (lab == null) {
            return;
        }
        MarkChain(lab.scientist != null ? lab.scientist.gameObject : null, false);
        MarkChain(lab.scientistAnim != null ? lab.scientistAnim.gameObject : null, false);
        MarkChain(lab.scientistTalker != null ? lab.scientistTalker.gameObject : null, false);
        foreach (Transform t in lab.GetComponentsInChildren<Transform>(true)) {
            if (t != null && t.CompareTag("Scientist")) {
                MarkChain(t.gameObject, true);
            }
        }
    }

    static void MarkChain(GameObject go, bool claimHealth) {
        Transform t = go != null ? go.transform : null;
        while (t != null && t.GetComponent<EndLabScript>() == null) {
            if (t.GetComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>() == null) {
                t.gameObject.AddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
            }
            BuildingEntity building = t.GetComponent<BuildingEntity>();
            if (building != null) {
                if (claimHealth || HealthBuilding == null) {
                    HealthBuilding = building;
                }
                return;
            }
            t = t.parent;
        }
    }

    public static void Tick() {
        if (!Net.IsRunning || Go || EndLabScript.instance == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
            return;
        }
        if (!_sentReady && LocalArrivalClear()) {
            _sentReady = true;
            if (Net.IsServer) {
                _hostSelfReady = true;
            } else {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CustomWireCommands.ScientistReady);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }
        }
        if (!Net.IsServer || !EveryoneReady()) {
            return;
        }
        if (!AnyoneInRoom()) {
            return;
        }
        Go = true;
        NetDataWriter go = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        go.Put(CustomWireCommands.ScientistGo);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, go, ServerMain.AllClientIdsExceptHost);
    }

    public static void MarkReady(knetid id) {
        Ready.Add(id);
    }

    public static void ReportDamage(float delta) {
        if (!Util.IsFinite(delta) || delta <= 0.05f || HealthBuilding == null || _applyingHp || ConcurrentEndings.ScientistDead) {
            return;
        }
        if (Net.IsServer) {
            BroadcastHealth(HealthBuilding.health);
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.ScientistHp + " " + delta.ToString("R", CultureInfo.InvariantCulture));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    /// <summary>
    /// 房主权威结算客机上报的科学家伤害差值并向全员同步最新生命值。
    /// </summary>
    public static void HostAcceptDamage(float delta) {
        if (!Net.IsServer || !Util.IsFinite(delta) || delta <= 0.05f || delta > MaxValidDamageDelta || HealthBuilding == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11 || EndLabScript.instance == null || ConcurrentEndings.ScientistDead) {
            return;
        }
        ApplyRaw(Mathf.Max(0f, HealthBuilding.health - delta));
        BroadcastHealth(HealthBuilding != null ? HealthBuilding.health : 0f);
    }

    public static void ApplyRemoteHealth(float value) {
        if (HealthBuilding == null || !Util.IsFinite(value)) {
            return;
        }
        ApplyRaw(value);
    }

    static void BroadcastHealth(float value) {
        if (!Net.IsServer) {
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.ScientistHp);
        writer.Put(value);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    static void ApplyRaw(float value) {
        if (HealthBuilding == null) {
            return;
        }
        _applyingHp = true;
        try {
            HealthBuilding.health = Mathf.Max(0f, value);
        } finally {
            _applyingHp = false;
        }
    }

    static bool LocalArrivalClear() {
        WorldGeneration world = WorldGeneration.world;
        PlayerCamera cam = PlayerCamera.main;
        if (world == null || cam == null || cam.body == null) {
            return false;
        }
        if (world.generatingWorld || world.biomeDepth != 11 || !world.worldExists) {
            return false;
        }
        if (ScrollableText.textExists || SharedMain.IsInCutscene || world.cutsceneActive) {
            return false;
        }
        Talker talker = cam.body.talker;
        if (talker != null && talker.text != null && !string.IsNullOrEmpty(talker.text.text) && !talker.FinishedTalking()) {
            return false;
        }
        try {
            float alert = Traverse.Create(cam).Field("alertTime").GetValue<float>();
            if (alert > 0f) {
                return false;
            }
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.Story, $"ScientistGate: failed to inspect PlayerCamera.alertTime: {ex.Message}");
        }
        return true;
    }

    static bool EveryoneReady() {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null && !_hostSelfReady) {
            return false;
        }
        foreach (ScavPlayer player in ScavPlayer.AllPlayers) {
            if (player == null || player.IsLocal) {
                continue;
            }
            if (!Ready.Contains(player.playerId)) {
                return false;
            }
        }
        return true;
    }

    static bool AnyoneInRoom() {
        foreach (ScavPlayer player in ScavPlayer.AllPlayers) {
            if (player == null || player.body == null || !player.body.alive) {
                continue;
            }
            if (player.body.transform.position.x < 17f) {
                return true;
            }
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        return local != null && local.alive && local.transform.position.x < 17f;
    }

    /// <summary>
    /// 原版协程在科学家开口前 yield 两次（等待世界生成、等待进入 x &lt; 17 并播放实验室主题曲）。
    /// 联机下允许本机提前推进到播放背景音乐阶段，实际台词则挂起等待房主广播 <c>scientist_go</c>。
    /// </summary>
    public static IEnumerator Drive(EndLabScript lab) {
        IEnumerator inner = EndLabScript_ScientistCutscene_Gate.Original(lab);
        if (!inner.MoveNext()) {
            yield break;
        }
        yield return inner.Current;
        if (lab == null) {
            yield break;
        }
        if (!inner.MoveNext()) {
            yield break;
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = 1f;
        }
        yield return new WaitUntil(() => Go || lab == null);
        if (lab == null) {
            yield break;
        }
        while (inner.MoveNext()) {
            yield return inner.Current;
        }
    }

    public static IEnumerator DeferredStart(EndLabScript lab) {
        yield return new WaitUntil(() => PlayerCamera.main != null && PlayerCamera.main.body != null && WorldGeneration.world != null);
        if (lab == null) {
            yield break;
        }
        KeepLocal(lab);
        PlayerCamera.main.blockItemUsage = true;
        PlayerCamera.main.body.forceWalk = true;
        lab.scientistCoroutine = lab.StartCoroutine(lab.ScientistCutscene());
        LampScript[] lamps = UnityEngine.Object.FindObjectsByType<LampScript>(FindObjectsSortMode.None);
        foreach (LampScript lamp in lamps) {
            lab.lights.Add(lamp);
        }
        GuardScript.anyGuardKilled = false;
        if (lab.injector != null) {
            lab.injector.DrainAll();
            lab.injector.AddLiquid("localanesthetic", 347f);
        }
        if (lab.surgicalDevice != null && lab.surgicalDevice.GetComponent<SurgicalDeviceBehavior>() == null) {
            lab.surgicalDevice.gameObject.AddComponent<SurgicalDeviceBehavior>();
        }
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.ScientistReady] = (plr, cmd, args) => {
            if (plr == null || WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
                return;
            }
            MarkReady(plr.playerId);
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.ScientistHp] = (plr, cmd, args) => {
            if (plr == null || args == null || args.Length < 2) {
                return;
            }
            // 严格验证调用者在终局实验室层存活且处于有效交互距离内，防止跨层伪造扣血包
            if (!NetGuard.TryVerifyCaller(plr.playerId, CallerPolicy.AliveInWorld, LogDomain.Story, 0, out VerifiedCaller caller)) {
                return;
            }
            if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11 || EndLabScript.instance == null || HealthBuilding == null) {
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr.playerId.id);
                return;
            }
            if (!NetGuard.IsWithinDistance(in caller, HealthBuilding.transform.position, MaxLabDamageReportDistance)) {
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr.playerId.id);
                return;
            }
            if (!float.TryParse(args[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float delta)
                || !Util.IsFinite(delta)
                || delta <= 0.05f
                || delta > MaxValidDamageDelta) {
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.PacketReject, plr.playerId.id);
                return;
            }
            HostAcceptDamage(delta);
        };
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.ScientistGo] = (cmd, reader) => {
            Go = true;
        };
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.ScientistHp] = (cmd, reader) => {
            ApplyRemoteHealth(reader.GetFloat());
        };
    }
}

[HarmonyPatch(typeof(EndLabScript), "Awake")]
static class EndLabScript_Awake_KeepScientist_MultiplayerPatch {
    static void Postfix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        ScientistGate.KeepLocal(__instance);
    }
}

[HarmonyPatch(typeof(EndLabScript), "Start")]
static class EndLabScript_Start_KeepScientist_MultiplayerPatch {
    static bool Prefix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        ScientistGate.KeepLocal(__instance);
        if (PlayerCamera.main == null || PlayerCamera.main.body == null) {
            __instance.StartCoroutine(ScientistGate.DeferredStart(__instance));
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(EndLabScript), "ScientistCutscene")]
static class EndLabScript_ScientistCutscene_Gate {
    [HarmonyReversePatch]
    public static IEnumerator Original(EndLabScript instance) {
        throw new NotImplementedException();
    }

    static bool Prefix(EndLabScript __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ScientistGate.Drive(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(EndLabScript), "Update")]
static class EndLabScript_Update_ScientistGate_MultiplayerPatch {
    static void Postfix() {
        ScientistGate.Tick();
    }
}

[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.health), MethodType.Setter)]
static class BuildingEntity_Health_ScientistShare_MultiplayerPatch {
    static void Prefix(BuildingEntity __instance, ref float value, ref float __state) {
        if (Net.IsRunning && __instance == ScientistGate.HealthBuilding && value < 0f) {
            value = 0f;
        }
        __state = (!Net.IsRunning || __instance != ScientistGate.HealthBuilding) ? float.NaN : __instance.health;
    }

    static void Postfix(BuildingEntity __instance, float __state) {
        if (float.IsNaN(__state)) {
            return;
        }
        ScientistGate.ReportDamage(__state - __instance.health);
    }
}
#endif
