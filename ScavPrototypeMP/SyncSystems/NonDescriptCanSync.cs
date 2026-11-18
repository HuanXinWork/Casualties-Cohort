using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Text;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 无名罐头（<see cref="NonDescriptCan"/>）权威属性与内部随机液体列表同步器（PR #48 零线协议破坏重制版）。
/// <para>
/// 设计依据与线协议契约：
/// 1. 原版 <c>NonDescriptCan.Start()</c> 在主客机各自独立随机生成 5 项营养/疾病属性与 1~4 种液体，导致客机预测食用效果与服务端结算严重脱节。
/// 2. 严禁向 <see cref="SyncInfo"/> 新增任何 <c>SyncVar</c> / <c>SyncList</c>（会破坏 Steam 原版 <c>v5.5.3.0</c> 的 LiteEntitySystem 反射字段表）；
///    由于罐头属性在生成后只读不变，全部走既有 <c>CustomCommand</c> 命名通道（<c>nondesc_req</c> / <c>nondesc_sync</c>），旧版 <c>v5.5.3.0</c> 会安全忽略未知命令键。
/// 3. 所有数值均强制以 <see cref="CultureInfo.InvariantCulture"/> 编解码，规避俄语/欧系语言环境下逗号小数点导致的解析失败。
/// </para>
/// </summary>
internal static class NonDescriptCanSync {
    private const string CmdRequest = "nondesc_req";
    private const string CmdSync = "nondesc_sync";
    private const string EmptyLiquidsToken = "none";
    private const float ClientRequestCooldownSeconds = 1.5f;
    private const float PendingPayloadTtlSeconds = 3.0f;
    private const int MaxTrackedClientEntries = 256;
    private const int MaxPendingPayloads = 64;

    private struct ClientCanTrack {
        public NonDescriptCan Can;
        public ushort SyncId;
        public bool Synced;
        public float NextRequestUnscaledTime;
    }

    private struct ParsedCanPayload {
        public ushort SyncId;
        public float Water;
        public float Fat;
        public float Food;
        public float Happy;
        public float Sick;
        public List<(string Id, float Weight)> Liquids;
        public float ExpiresAtUnscaledTime;
    }

    private static readonly Dictionary<int, ClientCanTrack> ClientTracks = new();
    private static readonly Dictionary<ushort, ParsedCanPayload> PendingPayloads = new();
    private static readonly List<int> StaleTrackKeys = new();
    private static readonly List<ushort> StalePendingKeys = new();

    [CTPostInit]
    private static void Init() {
        ServerMain.ServerCustomCommandsDict[CmdRequest] = HandleServerRequest;
        ClientMain.ClientCustomCommandsDict[CmdSync] = HandleClientSync;
        Multiplayer.OnSceneChangeOrWorldStartGenerate -= ClearAll;
        Multiplayer.OnSceneChangeOrWorldStartGenerate += ClearAll;
    }

    /// <summary>
    /// 切层生成新世界或跨服重连切换场景时清空全部客户端追踪表与暂存载荷，防止 3s TTL 窗口内新世界复用相同 <c>syncId</c> 误命中旧局暂存数据。
    /// </summary>
    private static void ClearAll() {
        ClientTracks.Clear();
        PendingPayloads.Clear();
        StaleTrackKeys.Clear();
        StalePendingKeys.Clear();
    }

    internal static bool IsClientSynced(NonDescriptCan can) {
        if (can == null) {
            return false;
        }
        return ClientTracks.TryGetValue(can.GetInstanceID(), out ClientCanTrack track)
            && track.Can != null
            && track.Synced;
    }

    /// <summary>
    /// 客户端在首次绑定 <see cref="SyncInfo"/>、<c>Start()</c> 或 <c>Eat()</c> 前确保已同步服务端权威数值。
    /// </summary>
    internal static void EnsureClientSynced(NonDescriptCan can, knetid syncId) {
        if (!Net.IsRunning || Net.IsServer || can == null || syncId.id == 0) {
            return;
        }

        ushort rawId = syncId.id;
        float now = Time.unscaledTime;

        // 若服务端在 Start() 主动广播的 CustomCommand 比 LiteEntitySystem 实体创建早半拍到达，直接消费暂存载荷避免额外 RTT
        if (PendingPayloads.TryGetValue(rawId, out ParsedCanPayload pending)) {
            PendingPayloads.Remove(rawId);
            if (now <= pending.ExpiresAtUnscaledTime) {
                ApplyPayloadToCan(can, in pending);
                MarkClientSynced(can, rawId);
                return;
            }
        }

        int instanceId = can.GetInstanceID();
        if (ClientTracks.TryGetValue(instanceId, out ClientCanTrack track) && track.Can != null) {
            if (track.Synced && track.SyncId == rawId) {
                return;
            }
            if (track.SyncId == rawId && now < track.NextRequestUnscaledTime) {
                return;
            }
        }

        PruneClientTracksIfNeeded();
        ClientTracks[instanceId] = new ClientCanTrack {
            Can = can,
            SyncId = rawId,
            Synced = false,
            NextRequestUnscaledTime = now + ClientRequestCooldownSeconds,
        };

        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CmdRequest + " " + rawId.ToString(CultureInfo.InvariantCulture));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    internal static void OnCanStartPostfix(NonDescriptCan can) {
        if (!Net.IsRunning || can == null) {
            return;
        }

        if (Net.IsServer) {
            EnsureServerCanInitialized(can);
            if (ServerMain.AllClientIdsExceptHost.Count > 0
                && NetObjectRegistry.TryGetSyncInfo(can, out SyncInfo si)
                && si != null
                && !si.IsDestroyed
                && si.syncId.id != 0) {
                NetDataWriter writer = BuildSyncWriter(si.syncId.id, can);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
            }
            return;
        }

        if (NetObjectRegistry.TryGetSyncInfo(can, out SyncInfo clientSi)
            && clientSi != null
            && !clientSi.IsDestroyed
            && clientSi.syncId.id != 0) {
            EnsureClientSynced(can, clientSi.syncId);
        } else if (can.isActiveAndEnabled) {
            can.StartCoroutine(WaitForSyncInfoAndRequest(can));
        }
    }

    internal static bool OnCanEatPrefix(NonDescriptCan can) {
        if (can == null) {
            return false;
        }
        // 防御罐头在休眠容器内从未激活执行过 Start() 便被直接食用时，item/液体列表为 null 或仍保持 -1/-100 哨兵值
        if (can.item == null) {
            can.item = can.GetComponent<Item>();
        }
        if (!Net.IsRunning || Net.IsServer) {
            EnsureServerCanInitialized(can);
            return can.item != null;
        }

        if (NetObjectRegistry.TryGetSyncInfo(can, out SyncInfo si) && si != null && !si.IsDestroyed) {
            EnsureClientSynced(can, si.syncId);
        }
        if (!IsClientSynced(can)
            && (can.waterValue < 0f
                || can.fatValue < -90f
                || can.happinessValue < -90f
                || can.sicknessValue < -90f
                || can.foodValue < 0f)) {
            can.Start();
        }
        can.liquidIds ??= new List<string>();
        can.liquidWeights ??= new List<float>();
        return can.item != null;
    }

    private static IEnumerator WaitForSyncInfoAndRequest(NonDescriptCan can) {
        WaitForSecondsRealtime wait = new WaitForSecondsRealtime(0.2f);
        for (int attempt = 0; attempt < 25; attempt++) {
            if (!Net.IsRunning || Net.IsServer || can == null || IsClientSynced(can)) {
                yield break;
            }
            if (NetObjectRegistry.TryGetSyncInfo(can, out SyncInfo si)
                && si != null
                && !si.IsDestroyed
                && si.syncId.id != 0) {
                EnsureClientSynced(can, si.syncId);
                yield break;
            }
            yield return wait;
        }
    }

    private static void HandleServerRequest(ScavPlayer plr, string cmd, string[] args) {
        if (!Net.IsRunning || !Net.IsServer || plr == null || plr.IsLocal || args == null || args.Length < 2) {
            return;
        }
        if (!ushort.TryParse(args[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort rawSyncId) || rawSyncId == 0) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfoLive((knetid)rawSyncId, out SyncInfo si)
            || si == null
            || si.IsDestroyed
            || si.go == null
            || !si.go.TryGetComponent(out NonDescriptCan can)
            || can == null) {
            return;
        }

        EnsureServerCanInitialized(can);
        NetDataWriter writer = BuildSyncWriter(rawSyncId, can);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    private static void HandleClientSync(string cmd, NetDataReader reader) {
        if (!Net.IsRunning || Net.IsServer) {
            return;
        }

        string payload = reader != null && reader.AvailableBytes > 0 ? reader.GetString() : cmd;
        if (!TryParsePayload(payload, out ParsedCanPayload parsed)) {
            return;
        }

        if (NetObjectRegistry.TryGetSyncInfoLive((knetid)parsed.SyncId, out SyncInfo si)
            && si != null
            && !si.IsDestroyed
            && si.go != null
            && si.go.TryGetComponent(out NonDescriptCan can)
            && can != null) {
            PendingPayloads.Remove(parsed.SyncId);
            ApplyPayloadToCan(can, in parsed);
            MarkClientSynced(can, parsed.SyncId);
            return;
        }

        PrunePendingPayloadsIfNeeded();
        parsed.ExpiresAtUnscaledTime = Time.unscaledTime + PendingPayloadTtlSeconds;
        PendingPayloads[parsed.SyncId] = parsed;
    }

    /// <summary>
    /// 若服务端罐头生成后立即被置入未激活容器（<c>Start()</c> 尚未执行），在打包前幂等触发原版 <c>Start()</c> 锁定权威随机值，
    /// 防止将未初始化的默认值（<c>-1 / -100</c>）下发给客机。
    /// </summary>
    private static void EnsureServerCanInitialized(NonDescriptCan can) {
        if (can.item == null) {
            can.item = can.GetComponent<Item>();
        }
        if (can.waterValue < 0f
            || can.fatValue < -90f
            || can.happinessValue < -90f
            || can.sicknessValue < -90f
            || can.foodValue < 0f
            || can.liquidIds == null) {
            can.Start();
        }
        can.liquidIds ??= new List<string>();
        can.liquidWeights ??= new List<float>();
    }

    private static NetDataWriter BuildSyncWriter(ushort syncId, NonDescriptCan can) {
        string payload = FormatSyncPayload(syncId, can);
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        // ClientMain.RunCustomCommand 读取首个字符串做精确字典键匹配（不按空格切分），故首段写命令键，次段写完整编码行
        writer.Put(CmdSync);
        writer.Put(payload);
        return writer;
    }

    private static string FormatSyncPayload(ushort syncId, NonDescriptCan can) {
        StringBuilder sb = new StringBuilder(128);
        sb.Append(CmdSync).Append(' ')
          .Append(syncId.ToString(CultureInfo.InvariantCulture)).Append(' ')
          .Append(can.waterValue.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
          .Append(can.fatValue.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
          .Append(can.foodValue.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
          .Append(can.happinessValue.ToString("R", CultureInfo.InvariantCulture)).Append(' ')
          .Append(can.sicknessValue.ToString("R", CultureInfo.InvariantCulture)).Append(' ');

        int pairCount = can.liquidIds != null && can.liquidWeights != null
            ? Mathf.Min(can.liquidIds.Count, can.liquidWeights.Count)
            : 0;
        int writtenPairs = 0;
        for (int i = 0; i < pairCount; i++) {
            string liqId = can.liquidIds[i];
            float weight = can.liquidWeights[i];
            if (string.IsNullOrEmpty(liqId) || !IsFiniteFloat(weight) || weight <= 0f) {
                continue;
            }
            if (writtenPairs > 0) {
                sb.Append(';');
            }
            sb.Append(liqId).Append(':').Append(weight.ToString("R", CultureInfo.InvariantCulture));
            writtenPairs++;
        }

        if (writtenPairs == 0) {
            sb.Append(EmptyLiquidsToken);
        }
        return sb.ToString();
    }

    private static bool TryParsePayload(string payload, out ParsedCanPayload result) {
        result = default;
        if (string.IsNullOrWhiteSpace(payload)) {
            return false;
        }

        string[] tokens = payload.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        int offset = tokens.Length > 0 && string.Equals(tokens[0], CmdSync, StringComparison.Ordinal) ? 1 : 0;
        if (tokens.Length < offset + 7) {
            return false;
        }

        if (!ushort.TryParse(tokens[offset], NumberStyles.Integer, CultureInfo.InvariantCulture, out ushort syncId)
            || syncId == 0
            || !TryParseFiniteFloat(tokens[offset + 1], out float water)
            || !TryParseFiniteFloat(tokens[offset + 2], out float fat)
            || !TryParseFiniteFloat(tokens[offset + 3], out float food)
            || !TryParseFiniteFloat(tokens[offset + 4], out float happy)
            || !TryParseFiniteFloat(tokens[offset + 5], out float sick)) {
            return false;
        }

        List<(string Id, float Weight)> liquids = new();
        string rawLiquids = tokens[offset + 6];
        if (!string.Equals(rawLiquids, EmptyLiquidsToken, StringComparison.OrdinalIgnoreCase)) {
            string[] pairs = rawLiquids.Split(';', StringSplitOptions.RemoveEmptyEntries);
            for (int i = 0; i < pairs.Length; i++) {
                string pair = pairs[i];
                int sep = pair.LastIndexOf(':');
                if (sep <= 0 || sep >= pair.Length - 1) {
                    continue;
                }
                string liqId = pair.Substring(0, sep);
                string weightText = pair.Substring(sep + 1);
                if (TryParseFiniteFloat(weightText, out float weight) && weight > 0f) {
                    liquids.Add((liqId, weight));
                }
            }
        }

        result = new ParsedCanPayload {
            SyncId = syncId,
            Water = water,
            Fat = fat,
            Food = food,
            Happy = happy,
            Sick = sick,
            Liquids = liquids,
        };
        return true;
    }

    private static void ApplyPayloadToCan(NonDescriptCan can, in ParsedCanPayload payload) {
        if (can.item == null) {
            can.item = can.GetComponent<Item>();
        }

        can.waterValue = payload.Water;
        can.fatValue = payload.Fat;
        can.foodValue = payload.Food;
        can.happinessValue = payload.Happy;
        can.sicknessValue = payload.Sick;

        can.liquidIds ??= new List<string>();
        can.liquidWeights ??= new List<float>();
        can.liquidIds.Clear();
        can.liquidWeights.Clear();

        if (payload.Liquids == null) {
            return;
        }

        for (int i = 0; i < payload.Liquids.Count; i++) {
            (string liqId, float weight) = payload.Liquids[i];
            // 严格校验客户端本地注册表包含该液体且回调非空，修复原上游 PR #48 遇到第三方未知液体抛 KeyNotFoundException 的崩溃
            if (Liquids.Registry != null
                && Liquids.Registry.ContainsKey(liqId)
                && Liquids.Registry[liqId]?.onDrink != null) {
                can.liquidIds.Add(liqId);
                can.liquidWeights.Add(weight);
            }
        }
    }

    private static void MarkClientSynced(NonDescriptCan can, ushort syncId) {
        PruneClientTracksIfNeeded();
        ClientTracks[can.GetInstanceID()] = new ClientCanTrack {
            Can = can,
            SyncId = syncId,
            Synced = true,
            NextRequestUnscaledTime = float.MaxValue,
        };
    }

    private static void PruneClientTracksIfNeeded() {
        if (ClientTracks.Count < MaxTrackedClientEntries) {
            return;
        }
        StaleTrackKeys.Clear();
        foreach (KeyValuePair<int, ClientCanTrack> pair in ClientTracks) {
            if (pair.Value.Can == null) {
                StaleTrackKeys.Add(pair.Key);
            }
        }
        for (int i = 0; i < StaleTrackKeys.Count; i++) {
            ClientTracks.Remove(StaleTrackKeys[i]);
        }
    }

    private static void PrunePendingPayloadsIfNeeded() {
        if (PendingPayloads.Count < MaxPendingPayloads) {
            return;
        }
        float now = Time.unscaledTime;
        StalePendingKeys.Clear();
        foreach (KeyValuePair<ushort, ParsedCanPayload> pair in PendingPayloads) {
            if (now > pair.Value.ExpiresAtUnscaledTime) {
                StalePendingKeys.Add(pair.Key);
            }
        }
        for (int i = 0; i < StalePendingKeys.Count; i++) {
            PendingPayloads.Remove(StalePendingKeys[i]);
        }
        if (PendingPayloads.Count >= MaxPendingPayloads) {
            PendingPayloads.Clear();
        }
    }

    private static bool TryParseFiniteFloat(string text, out float value) {
        if (float.TryParse(text, NumberStyles.Float, CultureInfo.InvariantCulture, out value)) {
            return IsFiniteFloat(value);
        }
        value = 0f;
        return false;
    }

    private static bool IsFiniteFloat(float value) {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
}

[HarmonyPatch(typeof(NonDescriptCan), "Start")]
internal static class NonDescriptCan_Start_MultiplayerPatch {
    private static bool Prefix(NonDescriptCan __instance) {
        if (!Net.IsRunning || Net.IsServer || __instance == null) {
            return true;
        }
        if (__instance.item == null) {
            __instance.item = __instance.GetComponent<Item>();
        }
        // 若客户端已在 Start() 触发前通过 SyncInfo.UpdateItem 收到了服务端权威数据（例如曾存放于休眠容器中或液体列表为空），
        // 跳过原版 Start() 避免 liquidIds.Count == 0 时被本地随机液体覆盖污染。
        return !NonDescriptCanSync.IsClientSynced(__instance);
    }

    private static void Postfix(NonDescriptCan __instance) {
        NonDescriptCanSync.OnCanStartPostfix(__instance);
    }
}

[HarmonyPatch(typeof(NonDescriptCan), nameof(NonDescriptCan.Eat))]
internal static class NonDescriptCan_Eat_MultiplayerPatch {
    private static bool Prefix(NonDescriptCan __instance) {
        return NonDescriptCanSync.OnCanEatPrefix(__instance);
    }
}
