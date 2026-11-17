using System;
using System.Collections.Generic;
using ICSharpCode.SharpZipLib.Checksum;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using Together.Diagnostics;
using UnityEngine;

namespace Together.Api {
    /// <summary>
    /// 面向外部模组（如 <c>SkinSync</c>、<c>CuSaveManager</c>）开放的网络会话查询、玩家名册快照与任意大小自动分片可靠通道接口。
    /// <para>分片通道底层复用原版已有的 <c>CustomMessage</c> 路由，与未修改的原版 <c>v5.5.x</c> 节点 100% 保持线上协议兼容。</para>
    /// </summary>
    public static class TogetherNetworkApi {
        private sealed class LargeChannelHandlers {
            public Action<ushort, byte[]> OnServer;
            public Action<byte[]> OnClient;
        }

        private sealed class InboundTransfer {
            public string SubChannel;
            public int TotalBytes;
            public ushort TotalChunks;
            public uint ExpectedCrc32;
            public byte[] Buffer;
            public bool[] ReceivedMask;
            public int ReceivedChunks;
            public float UpdatedAt;
        }

        private static readonly Dictionary<string, LargeChannelHandlers> LargeHandlers = new Dictionary<string, LargeChannelHandlers>(StringComparer.Ordinal);
        private static readonly Dictionary<ulong, InboundTransfer> PendingTransfers = new Dictionary<ulong, InboundTransfer>();
        private static readonly List<ulong> ExpiredTransferKeys = new List<ulong>();

        private static uint _nextTransferId = 1;
        private static bool _chunkChannelRegistered;

        public static event Action OnSessionStarted;
        public static event Action OnSessionEnded;
        public static event Action<MpPlayerSnapshot> OnPlayerJoined;
        public static event Action<MpPlayerSnapshot> OnPlayerLeft;

        public static MpSessionSnapshot GetSessionSnapshot() {
            bool running = Net.IsRunning;
            bool steam = running && Net.IsRunningSteam;
            string transport = !running ? string.Empty : (steam ? "Steam" : "Direct");
            return new MpSessionSnapshot(
                Plugin.SUCCESFULLY_INITIALIZED && !Plugin.FORCE_DISABLE_MP_MOD,
                running,
                running && Net.IsServer,
                running && Net.IsClient,
                running && Net.IsDedicatedServer,
                steam,
                transport);
        }

        public static MpNetworkMetrics GetNetworkMetrics() {
            int online = ScavPlayer.ClientIdToPlayerDict != null ? ScavPlayer.ClientIdToPlayerDict.Count : 0;
            int living = ScavPlayer.AllLivingPlayers != null ? ScavPlayer.AllLivingPlayers.Count : 0;
            int dead = ScavPlayer.AllDeadPlayers != null
                ? ScavPlayer.AllDeadPlayers.Count
                : Math.Max(0, online - living);
            return new MpNetworkMetrics(
                ClientMain.LOCAL_PING,
                ClientMain.SERVER_FPS,
                ClientMain.SERVER_TPS,
                Net.IsRunning ? Net.ServerTick : (ushort)0,
                online,
                living,
                dead);
        }

        public static bool TryGetLocalPlayer(out MpPlayerSnapshot snapshot) {
            if (ScavPlayer.LocalPlayer == null) {
                snapshot = MpPlayerSnapshot.None;
                return false;
            }
            snapshot = BuildPlayerSnapshot(ScavPlayer.LocalPlayer);
            return snapshot.IsValid;
        }

        public static bool TryGetPlayer(ushort clientId, out MpPlayerSnapshot snapshot) {
            if (ScavPlayer.TryGetPlayerFromClientId(new knetid(clientId), out ScavPlayer plr) && plr != null) {
                snapshot = BuildPlayerSnapshot(plr);
                return snapshot.IsValid;
            }
            snapshot = MpPlayerSnapshot.None;
            return false;
        }

        public static bool TryGetPlayerByBody(Body body, out MpPlayerSnapshot snapshot) {
            if (body != null && ScavPlayer.BodyToPlayerDict != null && ScavPlayer.BodyToPlayerDict.TryGetValue(body, out ScavPlayer plr) && plr != null) {
                snapshot = BuildPlayerSnapshot(plr);
                return snapshot.IsValid;
            }
            snapshot = MpPlayerSnapshot.None;
            return false;
        }

        public static List<MpPlayerSnapshot> GetPlayers() {
            List<MpPlayerSnapshot> list = new List<MpPlayerSnapshot>();
            if (ScavPlayer.ClientIdToPlayerDict == null) {
                return list;
            }
            foreach (KeyValuePair<knetid, ScavPlayer> kv in ScavPlayer.ClientIdToPlayerDict) {
                if (kv.Value != null) {
                    list.Add(BuildPlayerSnapshot(kv.Value));
                }
            }
            return list;
        }

        /// <summary>
        /// 注册支持突破 <c>65,535</c> 字节限制的大负载自动分片可靠收发通道（例如 <c>SkinSync</c> 高清皮肤 ZIP 包同步）。
        /// </summary>
        public static bool RegisterLargeChannel(
            string subChannel,
            Action<ushort, byte[]> onServerReceive,
            Action<byte[]> onClientReceive) {
            if (string.IsNullOrWhiteSpace(subChannel) || (onServerReceive == null && onClientReceive == null)) {
                return false;
            }
            EnsureChunkTransportBound();
            if (!LargeHandlers.TryGetValue(subChannel, out LargeChannelHandlers entry)) {
                entry = new LargeChannelHandlers();
                LargeHandlers[subChannel] = entry;
            }
            if (onServerReceive != null) {
                entry.OnServer = onServerReceive;
            }
            if (onClientReceive != null) {
                entry.OnClient = onClientReceive;
            }
            return true;
        }

        public static bool SendLargeToServer(string subChannel, byte[] payload) {
            if (!Net.IsRunning || string.IsNullOrWhiteSpace(subChannel) || payload == null || payload.Length > NetTimingConstants.LargeChannelMaxTotalBytes) {
                return false;
            }
            EnsureChunkTransportBound();
            SendChunkedFrames(subChannel, payload, writer => Net.Client_Send(DeliveryMethod.ReliableOrdered, writer));
            return true;
        }

        public static bool SendLargeToClient(string subChannel, ushort targetClientId, byte[] payload) {
            if (!Net.IsRunning || !Net.IsServer || string.IsNullOrWhiteSpace(subChannel) || payload == null || payload.Length > NetTimingConstants.LargeChannelMaxTotalBytes) {
                return false;
            }
            EnsureChunkTransportBound();
            knetid target = new knetid(targetClientId);
            SendChunkedFrames(subChannel, payload, writer => Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, target));
            return true;
        }

        public static bool BroadcastLarge(string subChannel, byte[] payload, ushort? excludeClientId = null) {
            if (!Net.IsRunning || !Net.IsServer || string.IsNullOrWhiteSpace(subChannel) || payload == null || payload.Length > NetTimingConstants.LargeChannelMaxTotalBytes) {
                return false;
            }
            EnsureChunkTransportBound();
            List<knetid> recipients = new List<knetid>();
            foreach (knetid id in ServerMain.AllClientIds) {
                if (excludeClientId.HasValue && id.id == excludeClientId.Value) {
                    continue;
                }
                if (Net.IsServer && !Net.IsDedicatedServer && id == Net.SERVER_CLIENTID && excludeClientId.HasValue) {
                    continue;
                }
                recipients.Add(id);
            }
            if (recipients.Count == 0) {
                return false;
            }
            SendChunkedFrames(subChannel, payload, writer => Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, recipients));
            return true;
        }

        internal static MpPlayerSnapshot BuildPlayerSnapshot(ScavPlayer plr) {
            if (plr == null) {
                return MpPlayerSnapshot.None;
            }
            Body b = plr.body;
            NetBody nb = plr.netBody;
            ushort nbId = nb != null && nb.syncBody != null ? nb.syncBody.Id : (ushort)0;
            bool isLocal = ReferenceEquals(plr, ScavPlayer.LocalPlayer);
            bool alive = b != null && b.alive;
            Vector2 pos = b != null ? (Vector2)b.transform.position : Vector2.zero;
            return new MpPlayerSnapshot(
                plr.playerId.id,
                nbId,
                plr.SteamId,
                plr.GetPersistentId(),
                plr.playerName,
                plr.selectedCharacter,
                isLocal,
                alive,
                pos,
                b);
        }

        internal static void NotifySessionStarted() {
            MpFlightRecorder.Record(LogDomain.Lobby, FlightEventKind.SessionStart);
            try { OnSessionStarted?.Invoke(); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifySessionStarted), ex); }
        }

        internal static void NotifySessionEnded() {
            PendingTransfers.Clear();
            MpFlightRecorder.Record(LogDomain.Lobby, FlightEventKind.SessionEnd);
            try { OnSessionEnded?.Invoke(); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifySessionEnded), ex); }
        }

        internal static void NotifyPlayerJoined(ScavPlayer plr) {
            if (plr == null) return;
            MpFlightRecorder.Record(LogDomain.Lobby, FlightEventKind.PlayerJoin, plr.playerId.id);
            try { OnPlayerJoined?.Invoke(BuildPlayerSnapshot(plr)); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifyPlayerJoined), ex); }
        }

        internal static void NotifyPlayerLeft(ScavPlayer plr) {
            if (plr == null) return;
            MpFlightRecorder.Record(LogDomain.Lobby, FlightEventKind.PlayerLeave, plr.playerId.id);
            try { OnPlayerLeft?.Invoke(BuildPlayerSnapshot(plr)); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifyPlayerLeft), ex); }
        }

        private static void EnsureChunkTransportBound() {
            if (_chunkChannelRegistered) {
                return;
            }
            _chunkChannelRegistered = true;
            Multiplayer.RegisterCustomServerReceiver(CustomWireCommands.ChannelApiLargePayload, OnServerChunkReceived);
            Multiplayer.RegisterCustomClientReceiver(CustomWireCommands.ChannelApiLargePayload, OnClientChunkReceived);
        }

        private static void SendChunkedFrames(string subChannel, byte[] payload, Action<NetDataWriter> sendFrame) {
            uint transferId = _nextTransferId++;
            if (_nextTransferId == 0) {
                _nextTransferId = 1;
            }

            uint crc = ComputeCrc32(payload);
            int totalBytes = payload.Length;
            int chunkSize = NetTimingConstants.LargeChannelChunkBytes;
            ushort totalChunks = (ushort)Math.Max(1, (totalBytes + chunkSize - 1) / chunkSize);

            for (ushort idx = 0; idx < totalChunks; idx++) {
                int offset = idx * chunkSize;
                int len = Math.Min(chunkSize, totalBytes - offset);

                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelApiLargePayload);
                writer.Put(subChannel);
                writer.Put(transferId);
                writer.Put(idx);
                writer.Put(totalChunks);
                writer.Put(totalBytes);
                writer.Put(crc);
                writer.Put((ushort)len);
                if (len > 0) {
                    writer.Put(payload, offset, len);
                }
                sendFrame(writer);
            }
        }

        private static void OnServerChunkReceived(ScavPlayer sender, NetDataReader reader) {
            ushort senderId = sender != null ? sender.playerId.id : (ushort)0;
            if (TryReassembleChunk(senderId, reader, out string subChannel, out byte[] complete)) {
                if (LargeHandlers.TryGetValue(subChannel, out LargeChannelHandlers h) && h.OnServer != null) {
                    try {
                        h.OnServer(senderId, complete);
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.Interop, "LargeChannel.Server(" + subChannel + ")", ex);
                    }
                }
            }
        }

        private static void OnClientChunkReceived(NetDataReader reader) {
            if (TryReassembleChunk(ushort.MaxValue, reader, out string subChannel, out byte[] complete)) {
                if (LargeHandlers.TryGetValue(subChannel, out LargeChannelHandlers h) && h.OnClient != null) {
                    try {
                        h.OnClient(complete);
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.Interop, "LargeChannel.Client(" + subChannel + ")", ex);
                    }
                }
            }
        }

        private static bool TryReassembleChunk(ushort sourceKey, NetDataReader reader, out string subChannel, out byte[] completePayload) {
            subChannel = null;
            completePayload = null;
            PruneExpiredTransfers();

            try {
                subChannel = reader.GetString();
                uint transferId = reader.GetUInt();
                ushort chunkIdx = reader.GetUShort();
                ushort totalChunks = reader.GetUShort();
                int totalBytes = reader.GetInt();
                uint expectedCrc = reader.GetUInt();
                ushort sliceLen = reader.GetUShort();

                if (string.IsNullOrEmpty(subChannel)
                    || totalChunks == 0
                    || chunkIdx >= totalChunks
                    || totalBytes < 0
                    || totalBytes > NetTimingConstants.LargeChannelMaxTotalBytes
                    || sliceLen > NetTimingConstants.LargeChannelChunkBytes
                    || reader.AvailableBytes < sliceLen) {
                    return false;
                }

                ulong key = ((ulong)sourceKey << 32) | transferId;
                float now = Time.realtimeSinceStartup;
                if (!PendingTransfers.TryGetValue(key, out InboundTransfer state)) {
                    state = new InboundTransfer {
                        SubChannel = subChannel,
                        TotalBytes = totalBytes,
                        TotalChunks = totalChunks,
                        ExpectedCrc32 = expectedCrc,
                        Buffer = new byte[totalBytes],
                        ReceivedMask = new bool[totalChunks],
                        UpdatedAt = now,
                    };
                    PendingTransfers[key] = state;
                }

                int destOffset = chunkIdx * NetTimingConstants.LargeChannelChunkBytes;
                if (destOffset + sliceLen > state.Buffer.Length) {
                    PendingTransfers.Remove(key);
                    return false;
                }

                if (sliceLen > 0) {
                    Buffer.BlockCopy(reader.RawData, reader.Position, state.Buffer, destOffset, sliceLen);
                    reader.SkipBytes(sliceLen);
                }

                state.UpdatedAt = now;
                if (!state.ReceivedMask[chunkIdx]) {
                    state.ReceivedMask[chunkIdx] = true;
                    state.ReceivedChunks++;
                }

                if (state.ReceivedChunks < state.TotalChunks) {
                    return false;
                }

                PendingTransfers.Remove(key);
                if (ComputeCrc32(state.Buffer) != state.ExpectedCrc32) {
                    ModLog.Warning($"LargeChannel '{subChannel}' CRC32 校验不匹配，已丢弃损坏载荷。", LogDomain.Interop);
                    return false;
                }

                completePayload = state.Buffer;
                return true;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(TryReassembleChunk), ex);
                return false;
            }
        }

        private static void PruneExpiredTransfers() {
            if (PendingTransfers.Count == 0) {
                return;
            }
            float now = Time.realtimeSinceStartup;
            ExpiredTransferKeys.Clear();
            foreach (KeyValuePair<ulong, InboundTransfer> kv in PendingTransfers) {
                if (now - kv.Value.UpdatedAt > NetTimingConstants.LargeChannelSessionTimeoutSeconds) {
                    ExpiredTransferKeys.Add(kv.Key);
                }
            }
            for (int i = 0; i < ExpiredTransferKeys.Count; i++) {
                PendingTransfers.Remove(ExpiredTransferKeys[i]);
            }
            ExpiredTransferKeys.Clear();
        }

        private static uint ComputeCrc32(byte[] data) {
            Crc32 crc = new Crc32();
            if (data != null && data.Length > 0) {
                crc.Update(data);
            }
            return unchecked((uint)crc.Value);
        }
    }
}
