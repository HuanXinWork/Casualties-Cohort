using System;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.MpConstants;

namespace Together {
    /// <summary>
    /// 客户端物品/机关预测使用的服务端权威确认（Ack）机制（对应上游 PR #59 / #61 并补齐旧版房主向下兼容）。
    /// <para>
    /// 设计背景：高延迟或网络抖动下，固定估算的 <see cref="SyncInfo.SetIgnoreTimeForRoundTrip"/> 窗口容易在服务端处理回包前提前到期，
    /// 导致在途旧快照将已消耗物品耐久回滚（甚至允许二次使用）或闪烁机关状态。通过可靠通道单播 <c>use_ack</c>，客户端可持续锁定本地预测，
    /// 直至服务端明确裁决（接受或拒绝）完毕后再收敛为短尾缓冲。
    /// </para>
    /// <para>
    /// 三版本混连兼容不变量：连接未修改的 Steam 原版 <c>v5.5.3.0</c> 房主时，房主不会下发 <c>use_ack</c>；若直接启用最长 7.5s 锁窗口会导致客机每次交互卡死。
    /// 因此服务端在新客机加入时主动发送 <c>id == 0</c> 的握手探针（旧版客机对未知 <c>CustomCommand</c> 静默忽略），
    /// 仅收到探针或任意 <c>use_ack</c> 的客机才激活 <see cref="ServerSupportsUseAck"/>，否则自动无缝退回传统短 RTT 窗口。
    /// </para>
    /// </summary>
    internal static class UseAck {
        private static bool _serverSupportsUseAck;

        /// <summary>
        /// 当前连接的房主是否支持 <c>use_ack</c> 确认协议；断线或开启新会话时强制归零，防止跨房间状态残留。
        /// </summary>
        internal static bool ServerSupportsUseAck {
            get {
                if (!Net.IsRunning) {
                    _serverSupportsUseAck = false;
                    return false;
                }
                return _serverSupportsUseAck;
            }
            private set => _serverSupportsUseAck = value;
        }

        /// <summary>
        /// 服务端在请求裁决结束（无论接受或拒绝）时向发起请求的客机单播确认包。
        /// </summary>
        internal static void Send(knetid clientId, knetid item) {
            if (!Net.IsRunning || !Net.IsServer || item.id == 0) {
                return;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null || plr.IsLocal) {
                return;
            }
            SendRaw(plr.playerId, item);
        }

        /// <summary>
        /// 按 <see cref="Item"/> 实例查找 <see cref="SyncInfo"/> 并发送 <c>use_ack</c> 的便捷重载。
        /// </summary>
        internal static void Send(knetid clientId, Item item) {
            if (item != null && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) && si != null) {
                Send(clientId, si.syncId);
            }
        }

        /// <summary>
        /// 搭配 <c>using var _ack = UseAck.After(clientId, id);</c> 保证同步接收器的每条返回分支均发出确认包。
        /// 注意：含异步弹窗回调（如 <c>ConsentForFeeding</c>）的接收器严禁使用此作用域，必须在异步回调落定后手动调用 <see cref="Send(knetid, knetid)"/>。
        /// </summary>
        internal static Scope After(knetid clientId, knetid item) => new Scope(clientId, item);

        internal readonly struct Scope : IDisposable {
            private readonly knetid _client;
            private readonly knetid _item;

            internal Scope(knetid client, knetid item) {
                _client = client;
                _item = item;
            }

            public void Dispose() => Send(_client, _item);
        }

        private static void SendRaw(knetid targetPlayerId, knetid item) {
            NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(CustomWireCommands.UseAck);
            writer.Put(item);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, targetPlayerId);
        }

        private static void ResetSessionState() {
            _serverSupportsUseAck = false;
            CombatItemUseSync.ClearAllHeldStates();
        }

        private static void OnPlayerJoinedSendProbe(ScavPlayer plr) {
            if (!Net.IsRunning || !Net.IsServer || plr == null || plr.IsLocal) {
                return;
            }
            knetid targetId = plr.playerId;
            SendRaw(targetId, default);
            // 兼容延迟建立通道边缘窗口：0.25s 后幂等补发一次探针，确保客机进入世界前已完成能力协商
            Util.DelayCallLambda(0.25f, () => {
                if (Net.IsRunning && Net.IsServer && plr != null && !plr.IsLocal) {
                    SendRaw(targetId, default);
                }
            });
        }

        [CTPostInit]
        private static void Init() {
            TogetherNetworkApi.OnSessionStarted += ResetSessionState;
            TogetherNetworkApi.OnSessionEnded += ResetSessionState;
            ScavPlayer.OnPlayerJoined += OnPlayerJoinedSendProbe;

            ClientMain.ClientCustomCommandsDict[CustomWireCommands.UseAck] = (_, reader) => {
                ServerSupportsUseAck = true;
                if (reader.AvailableBytes < sizeof(ushort)) {
                    return;
                }
                reader.Get(out knetid item);
                if (item.id == 0) {
                    return;
                }
                if (NetObjectRegistry.TryGetSyncInfo(item, out SyncInfo si) && si != null) {
                    si.OnServerAck();
                } else {
                    CombatItemUseSync.ClearHeldState(item.id);
                }
            };
        }
    }
}
