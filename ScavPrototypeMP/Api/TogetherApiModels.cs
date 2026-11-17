using System;
using UnityEngine;

namespace Together.Api {
    /// <summary>
    /// 面向外部模组暴露的只读联机会话状态快照。
    /// </summary>
    public readonly struct MpSessionSnapshot {
        public static readonly MpSessionSnapshot Offline = default;

        public readonly bool IsModInitialized;
        public readonly bool IsRunning;
        public readonly bool IsServer;
        public readonly bool IsClient;
        public readonly bool IsDedicatedServer;
        public readonly bool IsSteamTransport;
        public readonly string TransportName;

        public MpSessionSnapshot(
            bool isModInitialized,
            bool isRunning,
            bool isServer,
            bool isClient,
            bool isDedicatedServer,
            bool isSteamTransport,
            string transportName) {
            IsModInitialized = isModInitialized;
            IsRunning = isRunning;
            IsServer = isServer;
            IsClient = isClient;
            IsDedicatedServer = isDedicatedServer;
            IsSteamTransport = isSteamTransport;
            TransportName = transportName ?? string.Empty;
        }
    }

    /// <summary>
    /// 面向外部模组暴露的实时网络质量与房间人数统计指标。
    /// </summary>
    public readonly struct MpNetworkMetrics {
        public readonly int LocalPingMs;
        public readonly int ServerFps;
        public readonly int ServerTps;
        public readonly ushort ServerTick;
        public readonly int OnlinePlayerCount;
        public readonly int LivingPlayerCount;
        public readonly int DeadPlayerCount;

        public MpNetworkMetrics(
            int localPingMs,
            int serverFps,
            int serverTps,
            ushort serverTick,
            int onlinePlayerCount,
            int livingPlayerCount,
            int deadPlayerCount) {
            LocalPingMs = localPingMs;
            ServerFps = serverFps;
            ServerTps = serverTps;
            ServerTick = serverTick;
            OnlinePlayerCount = onlinePlayerCount;
            LivingPlayerCount = livingPlayerCount;
            DeadPlayerCount = deadPlayerCount;
        }
    }

    /// <summary>
    /// 面向外部模组（如 <c>SkinSync</c> / <c>CuSaveManager</c>）暴露的统一玩家身份与躯体状态快照。
    /// </summary>
    public readonly struct MpPlayerSnapshot : IEquatable<MpPlayerSnapshot> {
        public static readonly MpPlayerSnapshot None = default;

        public readonly bool IsValid;
        public readonly ushort ClientId;
        public readonly ushort NetBodyId;
        public readonly ulong SteamId;
        public readonly string PersistentId;
        public readonly string PlayerName;
        public readonly byte SelectedCharacter;
        public readonly bool IsLocal;
        public readonly bool IsAlive;
        public readonly Vector2 Position;
        public readonly Body Body;

        public MpPlayerSnapshot(
            ushort clientId,
            ushort netBodyId,
            ulong steamId,
            string persistentId,
            string playerName,
            byte selectedCharacter,
            bool isLocal,
            bool isAlive,
            Vector2 position,
            Body body) {
            IsValid = true;
            ClientId = clientId;
            NetBodyId = netBodyId;
            SteamId = steamId;
            PersistentId = persistentId ?? string.Empty;
            PlayerName = playerName ?? string.Empty;
            SelectedCharacter = selectedCharacter;
            IsLocal = isLocal;
            IsAlive = isAlive;
            Position = position;
            Body = body;
        }

        public bool Equals(MpPlayerSnapshot other) {
            return IsValid == other.IsValid && ClientId == other.ClientId && SteamId == other.SteamId;
        }

        public override bool Equals(object obj) {
            return obj is MpPlayerSnapshot other && Equals(other);
        }

        public override int GetHashCode() {
            return HashCode.Combine(IsValid, ClientId, SteamId);
        }
    }
}
