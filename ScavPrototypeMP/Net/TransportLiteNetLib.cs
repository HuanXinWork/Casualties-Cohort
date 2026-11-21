using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Net;
using System.Net.Sockets;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {

    public interface NetTransportBase {
        public bool IsConnecting();
        public void Shutdown();
        public void Update();
        public void Kick(knetid clinetId, in string msg);
        public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid);
        public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds);
        public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data);
    }

    public class TransportLiteNetLib : NetTransportBase, INetEventListener {

        public readonly string ip = Net.IP_LOCALHOST_IPv4;
        public readonly ushort port = Net.DEFAULT_PORT;

        public NetManager NetManager;
        public NetPeer CLIENT_PEER { get; private set; }

        public TransportLiteNetLib(string IP, ushort PORT) {
            ip = IP;
            port = PORT;

            NetManager = new NetManager(this, null) {
                PingInterval = 500,
                DisconnectTimeout = 10000,
#if RELEASE
                MaxConnectAttempts = 30,
#else
                MaxConnectAttempts = 9999999,
#endif
                EnableStatistics = true,
            };
        }

        public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer, in knetid clientid) {
            if (ClientIdToPeerDict.TryGet(clientid, out NetPeer peer)) {
                peer.Send(writer, delivery);
            } else {
                if (log.verbose) {
                    log.error($"SERVER: TransportLiteNetLib.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
                }
            }
        }

        public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer, in IReadOnlyList<knetid> clientIds) {
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
            foreach (knetid cID in clientIds) {
                Server_SendTo(delivery, writer, cID);
            }
        }

        public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> writer) {
            CLIENT_PEER.Send(writer, delivery);
        }

        static bool _DoOnWantToConnect(string ipport_input, Net.NetType type) {
            try {
                if (!Multiplayer.IsValidPlayerName(UIMainMenu.USERINPUT_NAME, out string nameError)) {
                    Multiplayer.DoMultiplayerStatusMessageLog(nameError);
                    return false;
                }
                if (!Net.TryParseIPPORT(ipport_input, out string ip, out ushort port)) {
                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_invalid_ipport"));
                    return false;
                }
                if (type != Net.NetType.Client && !Net.VerifyMyServerName()) {
                    if (!NsfwContentFilter.LastCreateWasRejected) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_invalid_server_name"));
                    }
                    return false;
                }

                Net.TransportCreated(type);
                TransportLiteNetLib transport = new TransportLiteNetLib(ip, port);
                Net.Transport = transport;

                if (type == Net.NetType.Client) {
                    NetDataWriter hellopacket = Multiplayer.GetClientConnectIntroductionPacket();

                    bool success = transport.NetManager.Start();
                    if (!success) {
                        Net.ShutdownReset();
                        Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("net_lnl_client_start_failed"));
                        return false;
                    }

                    // 校验 NetManager.Connect 返回值：当 DNS 解析失败或端点不可达返回 null 时，立即重置连接状态并提示玩家，避免卡死在假连接态（Issue #2）
                    transport.CLIENT_PEER = transport.NetManager.Connect(ip, port, hellopacket);
                    if (transport.CLIENT_PEER == null) {
                        Net.ShutdownReset();
                        Multiplayer.DoMultiplayerStatusMessageError($"LiteNetLib: Failed to resolve or connect to \"{ip}:{port}\".");
                        return false;
                    }
                    return true;
                }

                if (type == Net.NetType.Host || type == Net.NetType.DedicatedServer) {
                    bool success = transport.NetManager.Start(port);
                    if (!success) {
                        Net.ShutdownReset();
                        Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("net_lnl_server_start_failed"));
                        return false;
                    }

                    Net.OnTransportConnected();
                    return true;
                }

                Multiplayer.DoMultiplayerStatusMessageError(Lang.Get("net_wanttoconnect_wtf"));
            } catch (Exception e) {
                Net.ShutdownReset();
                Multiplayer.DoMultiplayerStatusMessageError("TransportLiteNetLib.OnWantToConnect:\n" + e.Message);
                ModLog.Exception(LogDomain.Transport, nameof(_DoOnWantToConnect), e);
                return false;
            }
            return false;
        }

        public static bool OnWantToConnect(string ipport_input, Net.NetType type) {
            Net.ShutdownReset();

            if (_DoOnWantToConnect(ipport_input, type)) {
                Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("net_started"), type.ToString()));
                return true;
            } else {
                Net.ShutdownReset();
            }

            return false;
        }

        void INetEventListener.OnConnectionRequest(ConnectionRequest request) {
            NetDataReader reader = request.Data;

            bool is_rejected = !Net.ValidateNewClientHandshake(reader, true, false, out Multiplayer.IntroductionPlayerInfo userInfo, out string reject_reason);
            if (!is_rejected) {
                if (BanList.IsBannedIPName(request.RemoteEndPoint.Address.ToString(), userInfo.userName)) {
                    is_rejected = true;
                    reject_reason = "Banned";
                }
            }

            if (is_rejected) {
                NetDataWriter writer = new NetDataWriter();
                writer.Put(reject_reason);

                request.Reject(writer.Data, 0, writer.Length, false);
                return;
            }

            NetPeer newpeer = request.Accept();
#if !RELEASE
            if (log.verbose) {
                log.l($"{this.GetType().Name}: Accepted connection from: {newpeer}  name:{userInfo.userName}  color:{userInfo.color}");
            }
#endif

            BasePlayer playerEntity = BasePlayer.ServerCreatePlayer(userInfo.userName, 0, userInfo.color, false);

            ClientIdToPeerDict[playerEntity.OwnerId] = newpeer;

            newpeer.Tag = new TagPlayerInfo {
                intro = userInfo,
                plrId = playerEntity.OwnerId,
                netPlayer = playerEntity.serverNetPlayer,
                plr = playerEntity.player,
            };
        }

        void INetEventListener.OnNetworkError(IPEndPoint endPoint, SocketError socketError) {
            log.error($"{this.GetType().Name}.INetEventListener.OnNetworkError :IPEndPoint: {endPoint}, SocketError: {socketError}");
        }

        void INetEventListener.OnNetworkLatencyUpdate(NetPeer peer, int latency) {
            if (peer == CLIENT_PEER) {
                ClientMain.LOCAL_PING = latency;
                ClientMain.MY_CONNECTION_QUALITY = (byte)(Mathf.Clamp(100 - (int)peer.Statistics.PacketLossPercent, 0, 100));
            }

            if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe && klpe.plr != null) {
                klpe.plr.ping = (double)latency * 0.001;
            }
        }

        void INetEventListener.OnNetworkReceive(NetPeer peer, NetPacketReader reader, byte channelNumber, DeliveryMethod deliveryMethod) {
            if (Net.IsServer) {
                if (ClientIdToPeerDict.TryGet(peer, out knetid plrId)) {
                    Net.InvokeServerMessage(plrId, reader);
                }
            } else {
                Net.InvokeClientMessage(Net.SERVER_CLIENTID, reader);
            }
        }

        void INetEventListener.OnNetworkReceiveUnconnected(IPEndPoint remoteEndPoint, NetPacketReader reader, UnconnectedMessageType messageType) {
#if !RELEASE
            log.l($"{this.GetType().Name}.INetEventListener.OnNetworkReceiveUnconnected :IPEndPoint: {remoteEndPoint}, NetPacketReader: {reader}, UnconnectedMessageType: {messageType}");
#endif
        }

        void INetEventListener.OnPeerConnected(NetPeer peer) {
#if !RELEASE
            if (log.verbose) {
                log.l($"{this.GetType().Name}.INetEventListener.OnPeerConnected :peer: {peer}");
            }
#endif
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_connected"));
                Net.OnTransportConnected();
            }
        }

        void INetEventListener.OnPeerDisconnected(NetPeer peer, DisconnectInfo disconnectInfo) {
#if !RELEASE
            if (log.verbose) {
                log.l($"{this.GetType().Name}.INetEventListener.OnPeerDisconnected :peer: {peer}, DisconnectInfo: Reason: {disconnectInfo.Reason} SocketErrorCode: {disconnectInfo.SocketErrorCode} AdditionalData: {disconnectInfo.AdditionalData?.RawData?.ToHexString()}");
            }
#endif
            try {
                if (peer == CLIENT_PEER) {
                    Multiplayer.ShutdownNetwork();

                    if (disconnectInfo.Reason == DisconnectReason.UnknownHost) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_unknown_host"));
                    } else if (disconnectInfo.Reason == DisconnectReason.Timeout) {
                        Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("net_timed_out"));
                    } else if (disconnectInfo.Reason == DisconnectReason.ConnectionRejected) {
                        disconnectInfo.AdditionalData.TryGetString(out string str);
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("net_connection_rejected"), Lang.ParseDisconnectReason(str ?? "")));
                    } else if (disconnectInfo.Reason == DisconnectReason.DisconnectPeerCalled) {
                        disconnectInfo.AdditionalData.TryGetString(out string str);
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("net_disconnected"), Lang.ParseDisconnectReason(str ?? "")));
                    } else if (disconnectInfo.Reason == DisconnectReason.RemoteConnectionClose) {
                        disconnectInfo.AdditionalData.TryGetString(out string str);
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("net_disconnected"), Lang.ParseDisconnectReason(str ?? "")));
                    } else {
                        disconnectInfo.AdditionalData.TryGetString(out string str);
                        Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("net_disconnected_detail"), disconnectInfo.Reason, disconnectInfo.SocketErrorCode, Lang.ParseDisconnectReason(str ?? "")));
                    }
                }

                if (peer != null && peer.Tag != null && peer.Tag is TagPlayerInfo klpe) {
                    if (klpe.plrId != 0 && ClientIdToPeerDict.TryGet(klpe.plrId, out NetPeer currentPeer) &&
                        ReferenceEquals(currentPeer, peer)) {
                        Net.RemoveServerPlayer(klpe.netPlayer);
                    }

                    if (klpe.plr != null) {
                        ScavPlayer.Destroy(klpe.plr.gameObject);
                    }
                }
            } catch (Exception e) {
                log.error(e);
            }
            if (peer != null) {
                ClientIdToPeerDict.Remove(peer);
            }
        }

        public void OnMessageDelivered(NetPeer peer, object userData) {
            if (log.verbose) {
                log.l($"{this.GetType().Name}.IDeliveryEventListener.OnMessageDelivered :peer: {peer}, userData: {userData}");
            }
        }

        public void OnNtpResponse(NtpPacket packet) {
            if (log.verbose) {
                log.l($"{this.GetType().Name}.INtpEventListener.OnNtpResponse :NtpPacket: {packet}");
            }
        }

        public void OnPeerAddressChanged(NetPeer peer, IPEndPoint previousAddress) {
            if (log.verbose) {
                log.l($"{this.GetType().Name}.IPeerAddressChangedListener.OnPeerAddressChanged :peer: {peer}  previousAddress: {previousAddress}");
            }
        }

        bool NetTransportBase.IsConnecting() {
            if (CLIENT_PEER != null) {
                return CLIENT_PEER.ConnectionState != ConnectionState.Connected;
            }
            return false;
        }

        void NetTransportBase.Shutdown() {
            NetManager.Stop();
        }

        internal TwoWayDictionary<knetid, NetPeer> ClientIdToPeerDict = new();
        // 复用已连接对端缓冲列表，消除服务端每帧堆分配
        private readonly List<NetPeer> _connectedPeersCache = new();

        void NetTransportBase.Update() {
            if (Net.IsServer) {
                _connectedPeersCache.Clear();
                NetManager.GetConnectedPeers(_connectedPeersCache);
                foreach (NetPeer peer in _connectedPeersCache) {
                    try {
                        if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe) {
                            klpe.plr.ping = peer.Ping * 0.001f;
                        }
                    } catch (Exception e) {
                        log.error($"{nameof(TransportLiteNetLib)}: Player NetPeer bullshit happened {peer}\n{e.ToString()}");
                    }
                }
            }

            NetManager.PollEvents();
        }

        void NetTransportBase.Kick(knetid clientid, in string msg) {
            if (ClientIdToPeerDict.TryGet(clientid, out NetPeer peer)) {
                NetDataWriter writer = new NetDataWriter();
                writer.Put(msg);
                peer.Disconnect(writer);
            } else {
                log.warn($"Tried to kick a non-existant player, clientid:{clientid}  reason:{msg}");
            }
        }
    }
}
