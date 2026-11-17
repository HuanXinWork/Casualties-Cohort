using System;
using LiteEntitySystem;
using LiteEntitySystem.Internal;
using LiteEntitySystem.Transport;
using LiteNetLib;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// LiteEntitySystem 内部日志桥接器，转发至统一诊断日志通道与局内控制台。
    /// </summary>
    public class LiteEntitySystemLogger : LiteEntitySystem.ILogger {
        public const string name = "LES";
        public const string prefix1 = name + ": ";
        public BepInEx.Logging.ManualLogSource Logger = BepInEx.Logging.Logger.CreateLogSource(name);

        public void Log(string log) {
            if (Together.log.verbose) {
                Logger.LogInfo(log);
                if (Together.log.con) {
                    Together.log.con.LogToConsole(prefix1 + log);
                }
            }
        }

        public void LogError(string log) {
            Logger.LogError(log);
            ModLog.Error(LogDomain.Transport, $"{name}: {log}");
            if (Together.log.con) {
                Together.log.con.LogToConsole($"<color=red>{name} ERROR: {log}</color>");
            }
        }

        public void LogWarning(string log) {
            if (Together.log.verbose) {
                Logger.LogWarning(log);
                if (Together.log.con) {
                    Together.log.con.LogToConsole($"<color=yellow>{name} WARN: {log}</color>");
                }
            }
        }
    }

    /// <summary>
    /// LiteEntitySystem 实体类型映射注册表及底层快照数据包收发入口。
    /// </summary>
    public sealed class ScavEntityTypesMap : EntityTypesMap {
        public ScavEntityTypesMap Register<TEntity>(ushort id, EntityConstructor<TEntity> constructor) where TEntity : InternalEntity {
            if (id == 0) {
                throw new ArgumentNullException(nameof(id));
            }
            EntityClassInfo<TEntity>.ClassId = id;
            RegisteredTypes.Add(typeof(TEntity), new RegisteredTypeInfo(id, id.ToString(), constructor));
            MaxId = Math.Max(MaxId, id);
            return this;
        }

        public static EntityTypesMap CreateTypesMap() {
            // 严格保持 ClassId 2..6 注册顺序以维持跨端线协议兼容
            ushort entityId = 2;
            return new ScavEntityTypesMap()
                .Register(entityId++, e => new BasePlayer(e))
                .Register(entityId++, e => new BasePlayerController(e))
                .Register(entityId++, e => new SyncBody(e))
                .Register(entityId++, e => new SyncInfo(e))
                .Register(entityId++, e => new ServerWorldStateSingleton(e));
        }

        [ServerReceiver((byte)NetmsgId.LiteEntitySystem)]
        static void ServerReceiver__LiteEntitySystem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
            NetPlayer plr = Net.ServerEntityManager.GetPlayer((byte)clientId);
            if (plr == null) {
                return;
            }

            // 直接将包含 1 字节 Header 的底层缓冲区切片以 ReadOnlySpan<byte> 零拷贝传入 LES，消除每包 GetRemainingBytes() 数组分配
            int startOffset = reader.Position - sizeof(byte);
            int totalLen = reader.AvailableBytes + sizeof(byte);
            if (reader.RawData != null && startOffset >= 0 && startOffset + totalLen <= reader.RawData.Length) {
                ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(reader.RawData, startOffset, totalLen);
                reader.SkipBytes(reader.AvailableBytes);
                _ = Net.ServerEntityManager.Deserialize(plr, span);
            } else {
                reader.SkipBytes(-sizeof(byte));
                byte[] bytes = reader.GetRemainingBytes();
                _ = Net.ServerEntityManager.Deserialize(plr, bytes);
            }
        }

        [ClientReceiver((byte)NetmsgId.LiteEntitySystem)]
        static void ClientReceiver__LiteEntitySystem(knetid senderId, LiteNetLib.Utils.NetDataReader reader) {
            // 零拷贝切片传入 ClientEntityManager.Deserialize，消除客户端每秒 30+ 次状态快照包的临时 byte[] 拷贝
            int startOffset = reader.Position - sizeof(byte);
            int totalLen = reader.AvailableBytes + sizeof(byte);
            if (reader.RawData != null && startOffset >= 0 && startOffset + totalLen <= reader.RawData.Length) {
                ReadOnlySpan<byte> span = new ReadOnlySpan<byte>(reader.RawData, startOffset, totalLen);
                reader.SkipBytes(reader.AvailableBytes);
                _ = Net.ClientEntityManager.Deserialize(span);
            } else {
                reader.SkipBytes(-sizeof(byte));
                byte[] bytes = reader.GetRemainingBytes();
                _ = Net.ClientEntityManager.Deserialize(bytes);
            }
        }
    }

    public struct BodyKnockbackPacket : INetSerializeByMemcpy {
        public Vector2 Velocity;
        public bool CloseUI;
    }

    public struct BodyTeleportPacket : INetSerializeByMemcpy {
        public Vector2 Position;
        public Vector2 Velocity;
        public float RagdollTime;
        public bool IsRight;
        public bool IsPiggyback;
        public knetid Piggyback;

        public BodyTeleportPacket(NetBody nb) {
            Position = nb.GetPosition();
            Velocity = nb.body.GetVelocity();
            RagdollTime = nb.body.shock;
            IsRight = nb.body.isRight;
            IsPiggyback = nb.IsPiggybacking();
            Piggyback = IsPiggyback ? nb.piggybacking_on.netId : default;
        }
    }

    /// <summary>
    /// 服务端面向单个远端客户端的 LiteEntitySystem 传输对端适配器。
    /// </summary>
    public class LESKrokNetServerPeer : AbstractNetPeer {
        internal knetid plrId = byte.MaxValue;

        public LESKrokNetServerPeer() {
        }

        public override void TriggerSend() {
            if (Net.Transport is TransportLiteNetLib tlnl) {
                tlnl.NetManager.TriggerUpdate();
            } else if (Net.Transport is TransportSteamworks tsteam) {
                tsteam.FlushNagle(plrId);
            }
        }

        public override void SendReliableOrdered(ReadOnlySpan<byte> data) {
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, data, plrId);
        }

        public override void SendUnreliable(ReadOnlySpan<byte> data) {
            Net.Server_SendTo(DeliveryMethod.Unreliable, data, plrId);
        }

        public override int GetMaxUnreliablePacketSize() => NetTimingConstants.UnreliableHealthBatchMaxBytes;

        public override string ToString() => $"{GetType().Name} - {DebugHelp.GetPlayerFullDebugString(plrId)}";
    }

    /// <summary>
    /// 客户端面向服务端房主的 LiteEntitySystem 传输对端适配器。
    /// </summary>
    public class LESKrokNetClientPeer : AbstractNetPeer {
        public LESKrokNetClientPeer() {
        }

        public override void TriggerSend() {
            if (Net.Transport is TransportLiteNetLib tlnl) {
                tlnl.NetManager.TriggerUpdate();
            } else if (Net.Transport is TransportSteamworks tsteam) {
                tsteam.FlushNagle();
            }
        }

        public override void SendReliableOrdered(ReadOnlySpan<byte> data) => Net.Client_Send(DeliveryMethod.ReliableOrdered, data);

        public override void SendUnreliable(ReadOnlySpan<byte> data) => Net.Client_Send(DeliveryMethod.Unreliable, data);

        public override int GetMaxUnreliablePacketSize() => NetTimingConstants.UnreliableHealthBatchMaxBytes;

        public override string ToString() => GetType().Name;
    }
}
