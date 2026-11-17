using Steamworks;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 服务器浏览器专用 Steam Relay P2P 真实延迟（RTT）短连接探测器。
/// 主机在专用虚拟端口 <see cref="VirtualPort"/> 监听并在握手后立即断开；浏览器侧连接读取 <c>m_nPing</c> 后即刻回收句柄。
/// </summary>
internal static class BrowserSteamPing {
    public const int VirtualPort = 13;
    public const int MaxConcurrent = 6;
    private const float ProbeTimeoutSeconds = 4f;

    public static HSteamListenSocket HostListen = HSteamListenSocket.Invalid;

    private static Callback<SteamNetConnectionStatusChangedCallback_t> _callback;
    private static readonly SteamNetworkingConfigValue_t[] EmptyOptions = Array.Empty<SteamNetworkingConfigValue_t>();
    private static readonly Dictionary<HSteamNetConnection, Probe> Probes = new Dictionary<HSteamNetConnection, Probe>();
    private static readonly HashSet<ulong> InFlightOwners = new HashSet<ulong>();

    private sealed class Probe {
        public NetPublicServerInfo Info;
        public ulong OwnerId;
        public HSteamNetConnection Conn;
        public float StartedAt;
        public bool GotPing;
    }

    public static int InFlightCount => Probes.Count;

    public static void EnsureCallbacks() {
        if (!KSteam.Loaded || _callback != null) {
            return;
        }
        _callback = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
    }

    public static void EnsureHostListen() {
        if (!KSteam.Loaded) {
            return;
        }
        EnsureCallbacks();
        SteamNetworkingUtils.InitRelayNetworkAccess();
        if (HostListen != HSteamListenSocket.Invalid) {
            return;
        }
        HostListen = SteamNetworkingSockets.CreateListenSocketP2P(VirtualPort, 0, EmptyOptions);
        if (HostListen == HSteamListenSocket.Invalid) {
            ModLog.Warning("[BrowserPing] 创建主机测速监听端口失败", LogDomain.Transport);
        }
    }

    public static void CloseHostListen() {
        if (HostListen == HSteamListenSocket.Invalid) {
            return;
        }
        SteamNetworkingSockets.CloseListenSocket(HostListen);
        HostListen = HSteamListenSocket.Invalid;
    }

    public static bool IsHostPingListen(HSteamListenSocket listen) {
        return HostListen != HSteamListenSocket.Invalid && listen == HostListen;
    }

    public static bool IsProbeConnection(HSteamNetConnection conn) {
        return Probes.ContainsKey(conn);
    }

    /// <summary>
    /// 供 <see cref="TransportSteamworks"/> 分流拦截测速连接回调，防止测速短连接被误当作正式玩家连接处理。
    /// </summary>
    public static bool HandleTransportCallback(SteamNetConnectionStatusChangedCallback_t cb) {
        if (IsHostPingListen(cb.m_info.m_hListenSocket)) {
            HandleHostSide(cb);
            return true;
        }
        if (IsProbeConnection(cb.m_hConn)) {
            HandleProbeSide(cb);
            return true;
        }
        return false;
    }

    public static bool TryBeginProbe(NetPublicServerInfo info) {
        if (info == null || info.steam_lobby_info == null || !KSteam.Loaded) {
            return false;
        }
        if (Probes.Count >= MaxConcurrent) {
            return false;
        }

        CSteamID owner = info.steam_lobby_info.ownerID;
        if (!owner.IsValid() || owner.GetEAccountType() != EAccountType.k_EAccountTypeIndividual) {
            return false;
        }
        ulong ownerId = owner.m_SteamID;
        if (ownerId == 0 || InFlightOwners.Contains(ownerId)) {
            return false;
        }
        if (KSteam.GetLocalUserSteamID().m_SteamID == ownerId) {
            info.pingMs = 0;
            return true;
        }

        EnsureCallbacks();
        SteamNetworkingUtils.InitRelayNetworkAccess();

        SteamNetworkingIdentity identity = new SteamNetworkingIdentity();
        identity.SetSteamID(owner);
        HSteamNetConnection conn = SteamNetworkingSockets.ConnectP2P(ref identity, VirtualPort, 0, EmptyOptions);
        if (conn == HSteamNetConnection.Invalid) {
            return false;
        }

        Probes[conn] = new Probe {
            Info = info,
            OwnerId = ownerId,
            Conn = conn,
            StartedAt = Time.realtimeSinceStartup,
        };
        InFlightOwners.Add(ownerId);
        return true;
    }

    public static void Tick() {
        if (Probes.Count == 0) {
            return;
        }

        float now = Time.realtimeSinceStartup;
        List<HSteamNetConnection> done = null;
        foreach (KeyValuePair<HSteamNetConnection, Probe> kv in Probes) {
            Probe probe = kv.Value;
            TryReadPing(probe);
            if (probe.GotPing || now - probe.StartedAt >= ProbeTimeoutSeconds) {
                done ??= new List<HSteamNetConnection>();
                done.Add(kv.Key);
            }
        }

        if (done == null) {
            return;
        }
        for (int i = 0; i < done.Count; i++) {
            FinishProbe(done[i], keepPing: Probes.TryGetValue(done[i], out Probe p) && p.GotPing);
        }
    }

    public static void CancelAll() {
        List<HSteamNetConnection> keys = new List<HSteamNetConnection>(Probes.Keys);
        for (int i = 0; i < keys.Count; i++) {
            FinishProbe(keys[i], keepPing: false);
        }
    }

    private static void TryReadPing(Probe probe) {
        if (probe == null || probe.GotPing) {
            return;
        }
        SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
        SteamNetConnectionRealTimeLaneStatus_t lane = new SteamNetConnectionRealTimeLaneStatus_t();
        EResult result = SteamNetworkingSockets.GetConnectionRealTimeStatus(probe.Conn, ref status, 0, ref lane);
        if (result != EResult.k_EResultOK || status.m_nPing < 0) {
            return;
        }
        if (probe.Info != null) {
            probe.Info.pingMs = status.m_nPing;
        }
        probe.GotPing = true;
    }

    private static void HandleHostSide(SteamNetConnectionStatusChangedCallback_t cb) {
        ESteamNetworkingConnectionState state = cb.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) {
            SteamNetworkingSockets.AcceptConnection(cb.m_hConn);
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
            SteamNetworkingSockets.CloseConnection(cb.m_hConn, 0, "browser_ping", false);
        }
    }

    private static void HandleProbeSide(SteamNetConnectionStatusChangedCallback_t cb) {
        if (!Probes.TryGetValue(cb.m_hConn, out Probe probe)) {
            return;
        }
        ESteamNetworkingConnectionState state = cb.m_info.m_eState;
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_FindingRoute) {
            TryReadPing(probe);
            if (probe.GotPing) {
                FinishProbe(cb.m_hConn, keepPing: true);
            }
            return;
        }
        if (state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
            || state == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_None) {
            TryReadPing(probe);
            FinishProbe(cb.m_hConn, keepPing: probe.GotPing);
        }
    }

    private static void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t cb) {
        // 当正式 Steam 传输层运行时由 TransportSteamworks 转发回调，此处仅处理主菜单未联网浏览大厅场景
        if (Net.IsRunning && Net.TryGetSteamTransport(out _)) {
            return;
        }
        HandleTransportCallback(cb);
    }

    private static void FinishProbe(HSteamNetConnection conn, bool keepPing) {
        if (!Probes.TryGetValue(conn, out Probe probe)) {
            return;
        }
        Probes.Remove(conn);
        InFlightOwners.Remove(probe.OwnerId);
        SteamNetworkingSockets.CloseConnection(conn, 0, "browser_ping_done", false);
    }
}
