using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Core;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 兴奋剂状态（<see cref="Body.onHardStimulants"/>）、药物过量指示（<see cref="Body.overdoseIndex"/>）与脑控制反转（<see cref="Body.BrainControlReverse"/>）权威同步系统（对应 Issue #49）。
/// <para>
/// 设计约束与线协议兼容契约：
/// 1. 严禁修改 <c>NetmsgId</c> 或新增 <c>SyncVar</c>，全程复用 <see cref="CustomWireCommands.ChannelCustomCommand"/> 通道下发 <c>"stim_sync"</c>，旧版 <c>v5.5.3.0</c> 客户端未知命令直接安全忽略。
/// 2. <see cref="ClientMain.RunCustomCommand"/> 仅读取首个字符串做字典精确匹配而不会按空格切分，因此包结构必须先写入命令键 <c>"stim_sync"</c>，再写入四元组文本载荷 <c>"&lt;netId&gt; &lt;overdose&gt; &lt;onHard&gt; &lt;reverseDur&gt;"</c>。
/// 3. 原版仅在 <see cref="MoodleManager.UpdateMoodles"/> 中每 0.5s 对本地相机躯体递减 <c>overdoseIndex</c>，服务端远端玩家躯体无 <c>MoodleManager</c> 驱动会导致 OD 图标永久卡死；本系统在服务端 0.5s 采样后同步补偿远端躯体衰减。
/// 4. 原版客户端 <see cref="Body.HandlePeriodicChecks"/> 每 0.5s 会查询客户端本地空的 <c>CoUtils</c> 计时器并将 <c>onHardStimulants</c> 覆写回 <c>false</c>；客户端缓存服务端快照并在周期更新及 <c>MoodleManager</c> 刷新前强制对齐。
/// </para>
/// </summary>
internal sealed class StimulantSync : CasualtiesTogetherSingleton {
    internal const string CmdStimSync = "stim_sync";
    private const float ScanIntervalSeconds = 0.5f;
    private const int SnapshotPruneThreshold = 32;

    private struct ServerStimSnapshot {
        public int OverdoseDeci;
        public bool OnHard;
    }

    private struct ClientStimState {
        public int Overdose;
        public bool OnHard;
    }

    private static readonly Dictionary<ushort, ServerStimSnapshot> LastSentSnapshots = new Dictionary<ushort, ServerStimSnapshot>();
    private static readonly Dictionary<ushort, ClientStimState> ClientSyncedStates = new Dictionary<ushort, ClientStimState>();
    private static readonly List<ushort> StaleNetIds = new List<ushort>();
    private static readonly char[] SpaceSeparators = { ' ' };

    internal static bool AllowAuthorizedBrainReverse;
    private float _scanTimer;

    [CTPostInit]
    private static void Init() {
        ClientMain.ClientCustomCommandsDict[CmdStimSync] = OnClientReceiveStimSync;
        Multiplayer.OnSceneChangeOrWorldStartGenerate += ClearAllState;
    }

    internal static void ClearAllState() {
        LastSentSnapshots.Clear();
        ClientSyncedStates.Clear();
        StaleNetIds.Clear();
        AllowAuthorizedBrainReverse = false;
    }

    private void Update() {
        if (!Net.IsRunning || !Util.IsWorldGenerated()) {
            if (LastSentSnapshots.Count > 0 || ClientSyncedStates.Count > 0) {
                ClearAllState();
            }
            return;
        }

        if (Net.IsClient) {
            ApplyClientAuthoritativeStates();
            return;
        }

        if (!Net.IsServer) {
            return;
        }

        _scanTimer += Time.unscaledDeltaTime;
        if (_scanTimer < ScanIntervalSeconds) {
            return;
        }
        _scanTimer = 0f;
        ServerScanAndBroadcast();
    }

    private static void ServerScanAndBroadcast() {
        bool hasRemoteClients = ServerMain.AllClientIdsExceptHost.Count > 0;

        for (int i = 0; i < NetBody.all_instances.Count; i++) {
            NetBody nb = NetBody.all_instances[i];
            if (nb == null || !nb.IsPlayer || nb.body == null || nb.syncBody == null) {
                continue;
            }

            ushort netId = nb.netId.id;
            float overdoseVal = nb.body.alive ? Mathf.Max(0, nb.body.overdoseIndex) : 0f;
            int overdoseDeci = Mathf.RoundToInt(overdoseVal * 10f);
            bool onHard = nb.body.alive && nb.body.onHardStimulants;

            // 补偿服务端非本地躯体缺失 MoodleManager.UpdateMoodles 每 0.5s 递减的机制，使过量停止后主客机均能自然消除 OD
            if (!nb.IsBodyLocal() && nb.body.overdoseIndex > 0) {
                nb.body.overdoseIndex--;
            }

            if (LastSentSnapshots.TryGetValue(netId, out ServerStimSnapshot prev)) {
                if (prev.OverdoseDeci == overdoseDeci && prev.OnHard == onHard) {
                    continue;
                }
            } else if (overdoseDeci == 0 && !onHard) {
                LastSentSnapshots[netId] = default;
                continue;
            }

            LastSentSnapshots[netId] = new ServerStimSnapshot {
                OverdoseDeci = overdoseDeci,
                OnHard = onHard,
            };

            if (hasRemoteClients) {
                ServerBroadcastStimSync(netId, overdoseVal, onHard, 0f);
            }
        }

        if (LastSentSnapshots.Count > SnapshotPruneThreshold) {
            StaleNetIds.Clear();
            foreach (ushort id in LastSentSnapshots.Keys) {
                if (!NetBody.TryGetNetBodyFromId(id, out NetBody activeNb) || activeNb == null || activeNb.syncBody == null) {
                    StaleNetIds.Add(id);
                }
            }
            for (int i = 0; i < StaleNetIds.Count; i++) {
                LastSentSnapshots.Remove(StaleNetIds[i]);
            }
            StaleNetIds.Clear();
        }
    }

    internal static void ServerTriggerRemoteBrainReverse(NetBody nb) {
        if (nb == null || nb.body == null || nb.syncBody == null || ServerMain.AllClientIdsExceptHost.Count == 0) {
            return;
        }

        ushort netId = nb.netId.id;
        float overdoseVal = nb.body.alive ? Mathf.Max(0, nb.body.overdoseIndex) : 0f;
        bool onHard = nb.body.alive && nb.body.onHardStimulants;

        LastSentSnapshots[netId] = new ServerStimSnapshot {
            OverdoseDeci = Mathf.RoundToInt(overdoseVal * 10f),
            OnHard = onHard,
        };

        ServerBroadcastStimSync(netId, overdoseVal, onHard, 1f);
    }

    private static void ServerBroadcastStimSync(ushort netId, float overdose, bool onHard, float reverseDur) {
        string payload = string.Format(
            CultureInfo.InvariantCulture,
            "{0} {1} {2:0.###} {3} {4:0.###}",
            CmdStimSync,
            netId,
            overdose,
            onHard ? 1 : 0,
            reverseDur);

        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CmdStimSync);
        writer.Put(payload);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    private static void OnClientReceiveStimSync(string _, NetDataReader reader) {
        if (reader == null || reader.AvailableBytes <= 0) {
            return;
        }

        string payload = reader.GetString();
        if (string.IsNullOrWhiteSpace(payload)) {
            return;
        }

        string[] parts = payload.Split(SpaceSeparators, StringSplitOptions.RemoveEmptyEntries);
        int offset = (parts.Length > 0 && parts[0] == CmdStimSync) ? 1 : 0;
        if (parts.Length < offset + 4) {
            return;
        }

        if (!ushort.TryParse(parts[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort netId)
            || !float.TryParse(parts[offset + 1], NumberStyles.Float, CultureInfo.InvariantCulture, out float overdoseFloat)
            || !int.TryParse(parts[offset + 2], NumberStyles.Integer, CultureInfo.InvariantCulture, out int onHardFlag)
            || !float.TryParse(parts[offset + 3], NumberStyles.Float, CultureInfo.InvariantCulture, out float reverseDur)) {
            return;
        }

        if (!Util.IsFinite(overdoseFloat) || !Util.IsFinite(reverseDur)) {
            return;
        }

        int overdoseInt = Mathf.Clamp(Mathf.RoundToInt(overdoseFloat), 0, 10);
        bool onHard = onHardFlag != 0;

        ClientSyncedStates[netId] = new ClientStimState {
            Overdose = overdoseInt,
            OnHard = onHard,
        };

        if (!NetBody.TryGetNetBodyFromId(netId, out NetBody nb) || nb == null || nb.body == null) {
            return;
        }

        bool wasOnHard = nb.body.onHardStimulants;
        bool wasOverdosing = nb.body.overdoseIndex > 0;

        nb.body.overdoseIndex = overdoseInt;
        nb.body.onHardStimulants = onHard;

        if (!nb.IsBodyLocal() || !nb.body.alive) {
            return;
        }

        // 状态跳变时立即驱动本地 MoodleManager 刷新，随后重申权威值以抵消 UpdateMoodles 末尾的 overdoseIndex--
        if ((wasOnHard != onHard || wasOverdosing != (overdoseInt > 0)) && MoodleManager.main != null) {
            MoodleManager.main.UpdateMoodles();
            nb.body.overdoseIndex = overdoseInt;
            nb.body.onHardStimulants = onHard;
        }

        if (reverseDur > 0f) {
            AllowAuthorizedBrainReverse = true;
            try {
                nb.body.StartCoroutine(nb.body.BrainControlReverse());
            } finally {
                AllowAuthorizedBrainReverse = false;
            }
        }
    }

    private static void ApplyClientAuthoritativeStates() {
        if (ClientSyncedStates.Count == 0) {
            return;
        }

        StaleNetIds.Clear();
        foreach (KeyValuePair<ushort, ClientStimState> pair in ClientSyncedStates) {
            if (!NetBody.TryGetNetBodyFromId(pair.Key, out NetBody nb) || nb == null || nb.body == null || nb.syncBody == null) {
                StaleNetIds.Add(pair.Key);
                continue;
            }
            ApplyStateToBody(nb.body, pair.Value);
        }

        for (int i = 0; i < StaleNetIds.Count; i++) {
            ClientSyncedStates.Remove(StaleNetIds[i]);
        }
        StaleNetIds.Clear();
    }

    internal static void ApplyClientLocalBeforeMoodles() {
        // 客机刚生成本地 NetBody 但 LiteEntitySystem SyncBody 尚未绑定的数帧窗口期内不可访问 localNb.netId
        if (!Net.IsRunning || !Net.IsClient || !NetBody.TryGetLocalNetBody(out NetBody localNb)
            || localNb == null || localNb.body == null || localNb.syncBody == null) {
            return;
        }
        if (ClientSyncedStates.TryGetValue(localNb.netId.id, out ClientStimState state)) {
            ApplyStateToBody(localNb.body, state);
        }
    }

    private static void ApplyStateToBody(Body body, ClientStimState state) {
        if (!body.alive) {
            body.overdoseIndex = 0;
            body.onHardStimulants = false;
            return;
        }
        body.onHardStimulants = state.OnHard;
        body.overdoseIndex = state.Overdose > 0 ? Mathf.Max(body.overdoseIndex, state.Overdose) : 0;
    }
}

/// <summary>
/// 拦截 <see cref="Body.BrainControlReverse"/>：
/// 服务端远端玩家触发时不在服务端翻转（防止二次取反客机上报的 <c>moveDir</c> 导致位移撕裂），改为通过 <c>"stim_sync"</c> 通知对应客机在本地执行；
/// 客户端仅放行经服务端指令授权的本地控制反转，未授权本地随机触发返回空枚举器（防止 Unity <c>StartCoroutine(string)</c> 因 <c>null</c> 返回值报错）。
/// </summary>
[HarmonyPatch(typeof(Body), nameof(Body.BrainControlReverse))]
internal static class Body_BrainControlReverse_MultiplayerPatch {
    private static IEnumerator EmptyCoroutine() {
        yield break;
    }

    private static bool Prefix(Body __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        if (__instance == null) {
            __result = EmptyCoroutine();
            return false;
        }

        if (Net.IsServer) {
            if (__instance.IsBodyLocal()) {
                return true;
            }
            if (__instance.TryGetNetBody(out NetBody nb) && nb.IsPlayer && nb.syncBody != null) {
                StimulantSync.ServerTriggerRemoteBrainReverse(nb);
            }
            __result = EmptyCoroutine();
            return false;
        }

        if (StimulantSync.AllowAuthorizedBrainReverse && __instance.IsBodyLocal()) {
            return true;
        }

        __result = EmptyCoroutine();
        return false;
    }
}

/// <summary>
/// 在客户端 <see cref="MoodleManager.UpdateMoodles"/> 构建图标列表前强制对齐本地角色的 <c>onHardStimulants</c> 与 <c>overdoseIndex</c>，
/// 消除同帧内 <see cref="Body.HandlePeriodicChecks"/> 先把 <c>onHardStimulants</c> 清零再刷新 Moodle 导致的闪烁。
/// </summary>
[HarmonyPatch(typeof(MoodleManager), nameof(MoodleManager.UpdateMoodles))]
internal static class MoodleManager_UpdateMoodles_StimulantSyncPatch {
    private static void Prefix() {
        if (Net.IsRunning && Net.IsClient) {
            StimulantSync.ApplyClientLocalBeforeMoodles();
        }
    }
}
