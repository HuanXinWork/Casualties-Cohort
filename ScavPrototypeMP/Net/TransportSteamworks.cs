using CasualtiesTogetherUtils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEngine;
using static Together.KSteam;

namespace Together {

    public partial class TransportSteamworks : NetTransportBase {

        public TransportSteamworks() {
            hostLobbyBucket = (byte)UnityEngine.Random.Range(0, KSteam.MAX_LOBBY_BUCKETS);

            const int defaultBufferSizeLimit = 524288;
            int sendBufferSizeBytes = defaultBufferSizeLimit * 2;

            // SetConfigValue 调用完成后立即释放 Pinned GCHandle，防止进程生命周期内永久钉死托管堆
            GCHandle handle = GCHandle.Alloc(sendBufferSizeBytes, GCHandleType.Pinned);
            try {
                SteamNetworkingUtils.SetConfigValue(
                    Steamworks.ESteamNetworkingConfigValue.k_ESteamNetworkingConfig_SendBufferSize,
                    Steamworks.ESteamNetworkingConfigScope.k_ESteamNetworkingConfig_Global,
                    IntPtr.Zero,
                    Steamworks.ESteamNetworkingConfigDataType.k_ESteamNetworkingConfig_Int32,
                    handle.AddrOfPinnedObject()
                );
            } finally {
                handle.Free();
            }
        }

        public bool is_steamserver = false;
        public int DefaultTimeoutMS = 30000;
        public bool i_am_owner_of_this_lobby => KSteam.CURRENT_LOBBY.ownerID == KSteam.GetLocalUserSteamID();

        public bool IsInitialized {
            get {
                try {
                    if (is_steamserver) {
                        InteropHelp.TestIfAvailableGameServer();
                    } else {
                        InteropHelp.TestIfAvailableClient();
                    }
                    return true;
                } catch (Exception) {
                    // Steamworks 原生客户端/服务端接口未就绪或已销毁时按布尔探测返回 false
                    return false;
                }
            }
        }

        private void OnConnectionStatusChanged(SteamNetConnectionStatusChangedCallback_t pCallback) {
            try {
                // dedicated browser-ping virtual port - never treat as a game client
                if (BrowserSteamPing.HandleTransportCallback(pCallback)) {
                    return;
                }

#if !RELEASE
                KSteam.steamtestlog($"{this.GetType().Name}.OnConnectionStatusChanged [" + SteamNetConnectionStatusChangedCallback_t.k_iCallback + " - SteamNetConnectionStatusChangedCallback] - " + pCallback.m_hConn + " -- " + pCallback.m_info + " -- " + pCallback.m_eOldState);
#endif

                if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connecting) {
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: - connection request from " + pCallback.m_info.m_identityRemote.GetSteamID64());
                    }

                    if (Net.IsServer) {
                        if (connectionMapping.Count > Multiplayer.PLAYER_COUNT_LIMIT) {
#if !RELEASE
                            if (log.verbose) {
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Rejecting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");
                            }
#endif
                            if (is_steamserver) {
                                _ = SteamGameServerNetworkingSockets.CloseConnection(pCallback.m_hConn, 0, "MP max players", false);
                            } else {
                                _ = SteamNetworkingSockets.CloseConnection(pCallback.m_hConn, 0, "MP max players", false);
                            }
                            return;
                        }

                        EResult res = is_steamserver
                            ? SteamGameServerNetworkingSockets.AcceptConnection(pCallback.m_hConn)
                            : SteamNetworkingSockets.AcceptConnection(pCallback.m_hConn);

                        if (res == EResult.k_EResultOK) {
                            if (Net.IsServer) {
                                if (log.verbose) {
                                    KSteam.steamtestlog($"OnConnectionStatusChanged: Accepting connection {pCallback.m_info.m_identityRemote.GetSteamID64()}");
                                }

                                var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();

                                if (!connectionMapping.ContainsKey(clientId)) {
                                    SteamConnectionData nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                                    nCon.connection = pCallback.m_hConn;
                                    connectionMapping.Add(clientId, nCon);
                                }
                            } else if (log.verbose) {
                                KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: this is not a server");
                            }
                        } else if (log.verbose) {
                            KSteam.steamtestlog($"OnConnectionStatusChanged: Connection {pCallback.m_info.m_identityRemote.GetSteamID64()} could not be accepted: {res}");
                        }
                    }
                } else if (pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_Connected) {
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: connection request to " + pCallback.m_info.m_identityRemote.GetSteamID64() + " was accepted!");
                    }

                    var clientId = pCallback.m_info.m_identityRemote.GetSteamID64();

                    if (!connectionMapping.ContainsKey(clientId)) {
                        SteamConnectionData nCon = new SteamConnectionData(pCallback.m_info.m_identityRemote.GetSteamID());
                        nCon.connection = pCallback.m_hConn;
                        connectionMapping.Add(clientId, nCon);
                    } else {
                        connectionMapping[clientId].connection = pCallback.m_hConn;
                    }
                    return;
                } else if (
                    pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ClosedByPeer
                    || pCallback.m_info.m_eState == ESteamNetworkingConnectionState.k_ESteamNetworkingConnectionState_ProblemDetectedLocally
                ) {
                    var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();

                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");
                    }

                    if (client_steamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID) {
                        Multiplayer.ShutdownNetwork();
                        Multiplayer.DoMultiplayerStatusMessageLog($"Steam: Connection closed: {pCallback.m_info.m_eState.ToString().TrimStart("k_ESteamNetworkingConnectionState_")}");
                    } else {
                        ESteamNetConnectionEnd endReason = (ESteamNetConnectionEnd)pCallback.m_info.m_eEndReason;
                        string endDebug = OneLine(pCallback.m_info.m_szEndDebug);
                        string persona = SteamFriends.GetFriendPersonaName((CSteamID)client_steamId);
                        bool closeP2p = connectionMapping.ContainsKey(client_steamId);
                        ulong lobbyId = KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID;
                        int memberCount = SteamMatchmaking.GetNumLobbyMembers(KSteam.CURRENT_LOBBY.lobby_steamID);
                        log.l("[lobby-left] trigger=OnConnectionStatusChanged steam=" + client_steamId
                            + " name=\"" + OneLine(persona) + "\""
                            + " state=" + pCallback.m_info.m_eState + " (" + ((int)pCallback.m_info.m_eState) + ")"
                            + " old=" + pCallback.m_eOldState + " (" + ((int)pCallback.m_eOldState) + ")"
                            + " end=" + endReason + " (" + pCallback.m_info.m_eEndReason + ")"
                            + " debug=\"" + endDebug + "\""
                            + " lobby=" + lobbyId
                            + " members=" + memberCount
                            + " closeP2P=" + (closeP2p ? 1 : 0)
                            + " method=TransportSteamworks.RemoveSteamUser");
                        RemoveSteamUser(client_steamId);
                    }
                    return;
                } else {
                    var client_steamId = pCallback.m_info.m_identityRemote.GetSteamID64();
                    if (log.verbose) {
                        KSteam.steamtestlog($"OnConnectionStatusChanged: Connection closed for {BetterToStringFromSteamID(client_steamId)} state changed: {pCallback.m_info.m_eState}    lobby owner:{BetterToStringFromSteamID(KSteam.CURRENT_LOBBY.ownerID.m_SteamID)}");
                    }
                }
            } catch (Exception e) {
                Plugin.Logger.LogError("OnConnectionStatusChanged: " + e.ToString());
            }
        }

        internal class SteamConnectionData {
            internal SteamConnectionData(CSteamID steamId) {
                id = steamId;
            }

            internal SteamNetConnectionRealTimeStatus_t connectionStatus = new();
            internal CSteamID id;
            internal HSteamNetConnection connection;

            public override string ToString() {
                return $"SteamConnectionData(ID:{id}, connection:{connection})";
            }
        }

        private Callback<SteamNetConnectionStatusChangedCallback_t> onConnectionChange = null;
        public SteamNetworkingConfigValue_t[] options = new SteamNetworkingConfigValue_t[0];
        public HSteamListenSocket listenSocket { get; private set; }
        internal SteamConnectionData serverUser { get; private set; }
        internal readonly Dictionary<ulong, SteamConnectionData> connectionMapping = new Dictionary<ulong, SteamConnectionData>();

        const int ClientPollCap = 256;
        const int HostPollCap = 255;
        IntPtr[] _pollPtrs;
        readonly List<SteamConnectionData> _pollConnections = new List<SteamConnectionData>(4);

        public byte hostLobbyBucket;

        internal bool CreateServerSocket() {
            if (listenSocket != HSteamListenSocket.Invalid) {
                log.error($"{this.GetType().Name}.CreateServerSocket : Socket already exists !!!!!!!!! {listenSocket}");
                return false;
            }

            if (onConnectionChange == null) {
                if (is_steamserver) {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
                } else {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                }
            }

            if (options == null) {
                options = new SteamNetworkingConfigValue_t[0];
            }

            if (is_steamserver) {
                listenSocket = SteamGameServerNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
            } else {
                listenSocket = SteamNetworkingSockets.CreateListenSocketP2P(0, options.Length, options);
            }

            if (!is_steamserver) {
                BrowserSteamPing.EnsureHostListen();
            }

            if (log.verbose) {
                log.l(this.GetType().Name + "CreateServerSocket!");
            }

            return true;
        }

        internal bool CreateClientSocket() {
            if (listenSocket != HSteamListenSocket.Invalid) {
                log.error($"{this.GetType().Name}.CreateClientSocket : Socket already exists! {listenSocket}");
                return false;
            }

            if (onConnectionChange == null) {
                if (is_steamserver) {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.CreateGameServer(OnConnectionStatusChanged);
                } else {
                    onConnectionChange = Callback<SteamNetConnectionStatusChangedCallback_t>.Create(OnConnectionStatusChanged);
                }
            }

            var ConnectToSteamID = KSteam.CURRENT_LOBBY.ownerID;
            serverUser = new SteamConnectionData((CSteamID)(ConnectToSteamID));

            try {
                if (is_steamserver) {
                    SteamGameServerNetworkingUtils.InitRelayNetworkAccess();
                } else {
                    SteamNetworkingUtils.InitRelayNetworkAccess();
                }

                SteamNetworkingIdentity smi = new SteamNetworkingIdentity();
                smi.SetSteamID(serverUser.id);
                if (is_steamserver) {
                    serverUser.connection = SteamGameServerNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
                } else {
                    serverUser.connection = SteamNetworkingSockets.ConnectP2P(ref smi, 0, options.Length, options);
                }

                connectionMapping.Add((ulong)ConnectToSteamID, serverUser);
                return true;
            } catch (Exception ex) {
                log.error($"{this.GetType().Name}.CreateClientSocket : Client could not be started !! {ex.Message}");
                return false;
            }
        }

        internal bool CreateLobby(ELobbyType lobbyType) {
#if PLAYTEST
            lobbyType = ELobbyType.k_ELobbyTypePublic;
#endif
            if (Multiplayer.PLAYER_COUNT_LIMIT > Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT) {
                Multiplayer.PLAYER_COUNT_LIMIT = Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT;
            }

            KSteam.CURRENT_LOBBY.locked = false;
            SteamAPICall_t handle = SteamMatchmaking.CreateLobby(lobbyType, Multiplayer.PLAYER_COUNT_LIMIT);
            KSteam.OnLobbyCreatedCallResult.Set(handle);

            Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Creating Lobby...");

            if (log.verbose) {
                log.l(this.GetType().Name + ".CreateLobby");
            }

            return true;
        }

        public static bool OnWantToHostLobby(Net.NetType type, ELobbyType lobbyType) {
            Net.ShutdownReset();

            if (!KSteam.Loaded) {
                log.error($"Steam is not loaded!");
                return false;
            }

            if (type == Net.NetType.Client) {
                log.error($"what  {new StackTrace()}");
                return false;
            }

            if (!Net.VerifyMyServerName()) {
                if (!NsfwContentFilter.LastCreateWasRejected) {
                    Multiplayer.DoMultiplayerStatusMessageLog("Invalid Server name.");
                }
                return false;
            }

            try {
                UIMainMenu._STEAM_CHOSEN_LOBBYTYPE = KSteam.LobbyTypeToNum(lobbyType);
                Net.TransportCreated(type);
                TransportSteamworks t = new TransportSteamworks();
                Net.Transport = t;

                if (!t.IsInitialized) {
                    log.error($"STEAM: IsInitialized is false!");
                    Net.ShutdownReset();
                    return false;
                }

                if (t.CreateLobby(lobbyType)) {
                    return true;
                }
            } catch (Exception e) {
                log.error("TransportSteamworks.OnWantToHostLobby: " + e.ToString());
                Net.ShutdownReset();
            }

            return false;
        }

        public static bool OnWantToJoinLobby(ulong lobby_steamID) {
            Net.ShutdownReset();

            KSteam.CURRENT_LOBBY.locked = true;

            if (!KSteam.Loaded) {
                log.error($"Steam is not loaded!");
                return false;
            }
            Net.NetType type = Net.NetType.Client;

            try {
                Net.TransportCreated(type);
                TransportSteamworks t = new TransportSteamworks();
                Net.Transport = t;

                if (!t.IsInitialized) {
                    log.error($"STEAM: IsInitialized is false!");
                    Net.ShutdownReset();
                    return false;
                }

                KSteam.OnLobbyEnterCallResult.Set(SteamMatchmaking.JoinLobby(new CSteamID(lobby_steamID)));
                return true;
            } catch (Exception e) {
                log.error("TransportSteamworks.OnWantToConnect: " + e.ToString());
                Net.ShutdownReset();
            }

            return false;
        }

        public void Update() {
            if (!KSteam.Loaded && KSteam.CURRENT_LOBBY.lobby_steamID != CSteamID.Nil) {
                return;
            }

            UpdateTheIDDicts();
            _Update_PollMessages();

            one_second_timer += Time.unscaledDeltaTime;
            if (one_second_timer > 1) {
                one_second_timer = 0;
                UpdateOneSecond();
            }
        }
        float one_second_timer = -1;

        public SteamNetConnectionRealTimeStatus_t client_status = new();
        void UpdateOneSecond() {
            CheckLobbyMembersList();

            SteamNetConnectionRealTimeStatus_t status = new();
            SteamNetConnectionRealTimeLaneStatus_t laneStatus = new();
            if (Net.IsServer) {
                foreach (SteamConnectionData connectionData in connectionMapping.Values) {
                    var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(connectionData.connection, ref status, 0, ref laneStatus);
                    if (result == EResult.k_EResultOK) {
                        int ping_ms = status.m_nPing;

                        if (SteamIDToNetPlayerDict.TryGetValue(connectionData.id.m_SteamID, out ScavPlayer plr)) {
                            plr.ping = ((float)ping_ms) * 0.001f;
                        }

                        connectionData.connectionStatus = status;
                    }
                }
            } else {
                if (serverUser != null && serverUser.connection != HSteamNetConnection.Invalid) {
                    var result = SteamNetworkingSockets.GetConnectionRealTimeStatus(serverUser.connection, ref status, 0, ref laneStatus);
                    if (result == EResult.k_EResultOK) {
                        int ping_ms = status.m_nPing;

                        ClientMain.LOCAL_PING = ping_ms;
                        ClientMain.MY_CONNECTION_QUALITY = (byte)(Mathf.Clamp01(status.m_flConnectionQualityLocal) * 100);
                        client_status = status;
                        return;
                    }
                }
                client_status = new();
            }
        }

        void CheckLobbyMembersList() {
            if (!KSteam.IS_IN_LOBBY) {
                return;
            }

            float DefaultTimeoutInSeconds = (float)DefaultTimeoutMS * 0.001f;

            double curtime = Time.realtimeSinceStartupAsDouble;
            if (i_am_owner_of_this_lobby) {
                foreach (KeyValuePair<CSteamID, LobbyMember> item in KSteam.CURRENT_LOBBY.members) {
                    if (KSteam.GetLocalUserSteamID() == item.Key) {
                        item.Value.server_last_data_receive_time = curtime;
                        continue;
                    }

                    double lastdatareceived_time = curtime - item.Value.server_last_data_receive_time;

                    if (lastdatareceived_time > DefaultTimeoutInSeconds) {
                        ulong idleSteam = item.Key.m_SteamID;
                        string idleName = SteamFriends.GetFriendPersonaName(item.Key);
                        bool closeP2p = connectionMapping.ContainsKey(idleSteam);
                        int memberCount = SteamMatchmaking.GetNumLobbyMembers(KSteam.CURRENT_LOBBY.lobby_steamID);
                        string connSnap = DescribeLobbyLeftConnection(idleSteam);
                        log.l("[lobby-left] trigger=CheckLobbyMembersList timer=DefaultTimeoutMS"
                            + " limitMs=" + DefaultTimeoutMS
                            + " idleSec=" + lastdatareceived_time.ToString("0.0")
                            + " steam=" + idleSteam
                            + " name=\"" + OneLine(idleName) + "\""
                            + " lobby=" + KSteam.CURRENT_LOBBY.lobby_steamID.m_SteamID
                            + " members=" + memberCount
                            + " closeP2P=" + (closeP2p ? 1 : 0)
                            + " method=TransportSteamworks.RemoveSteamUser alsoKickChat=1 "
                            + connSnap);
                        item.Value.server_last_data_receive_time = curtime;

#if RELEASE
                        log.l($"Lobby member {BetterToStringFromSteamID(item.Key)} timed out!");
#else
                        log.l($"Lobby member {BetterToStringFromSteamID(item.Key)} timed out!  time:{lastdatareceived_time}");
#endif
                        KickMember(item.Key, "Timed out!");
                        RemoveSteamUser(item.Key.m_SteamID);
                    }
                }
            } else {
                foreach (KeyValuePair<CSteamID, LobbyMember> item in KSteam.CURRENT_LOBBY.members) {
                    item.Value.server_last_data_receive_time = curtime;
                }
            }
        }

        void UpdateTheIDDicts() {
            SteamIDToNetPlayerDict.Clear();
            SteamIDToClientIDDict.Clear();

            foreach (KeyValuePair<knetid, ScavPlayer> item in ScavPlayer.ClientIdToPlayerDict) {
                SteamIDToNetPlayerDict[item.Value.SteamId] = item.Value;
                SteamIDToClientIDDict[item.Value.SteamId] = item.Value.playerId;
                item.Value.IsHost = item.Value.SteamId == KSteam.CURRENT_LOBBY.ownerID.m_SteamID;
            }
        }

        public readonly Dictionary<ulong, ScavPlayer> SteamIDToNetPlayerDict = new();
        public readonly TwoWayDictionary<ulong, knetid> SteamIDToClientIDDict = new();

        internal void RemoveSteamUser(ulong steamID) {
#if TESTING
            KSteam.steamtestlog($"RemoveSteamUser:  {BetterToStringFromSteamID(steamID)} \n{new StackTrace()}");
#endif
            if (steamID == KSteam.GetLocalUserSteamID().m_SteamID) {
                return;
            }

            if (SteamIDToNetPlayerDict.TryGetValue(steamID, out ScavPlayer player) && player != null) {
                try {
                    GameObject.DestroyImmediate(player.gameObject);
                } catch (Exception e) {
                    Plugin.Logger.LogError(e.ToString());
                }
            }

            SteamIDToNetPlayerDict.Remove(steamID);
            SteamIDToClientIDDict.Remove(steamID);

            if (connectionMapping.TryGetValue(steamID, out SteamConnectionData conndata)) {
                if (is_steamserver) {
                    _ = SteamGameServerNetworkingSockets.CloseConnection(conndata.connection, 0, "disconnected", false);
                } else {
                    _ = SteamNetworkingSockets.CloseConnection(conndata.connection, 0, "disconnected", false);
                }

                connectionMapping.Remove(steamID);
#if TESTING
                log.warn($"OnLobbyMemberListChanged: removing {steamID} from connectionMapping ");
#endif
            }
        }

        public void Kick(knetid clientid, in string msg) {
            if (!Net.IsServer) {
                log.error($"Tried to kick {DebugHelp.GetPlayerFullDebugString(clientid)} as a non-host????");
                return;
            }

            if (SteamIDToClientIDDict.TryGet(clientid, out ulong steamid)) {
                KickMember((CSteamID)steamid, msg);
            }
            RemoveSteamUser(steamid);
        }

        internal void KickMember(CSteamID target, string reason = null) {
            if (SteamMatchmaking.GetLobbyOwner(LobbyId) != SteamUser.GetSteamID()) {
                log.error($"Tried to kick {BetterToStringFromSteamID(target)} as a non-host????");
                return;
            }

            if (reason == null && KSteam.CURRENT_LOBBY.members.TryGetValue(target, out LobbyMember member)) {
                reason = member.disconnectreason;
            }

            string msg = "KICK:" + target.m_SteamID.ToString();
            if (!String.IsNullOrEmpty(reason)) {
                msg += ":" + reason;
            }
            byte[] data = System.Text.Encoding.UTF8.GetBytes(msg);

            SteamMatchmaking.SendLobbyChatMsg(LobbyId, data, data.Length);
        }

        public void Shutdown() {
            Shutdown(true);
        }

        internal void CloseListenSocket() {
            if (listenSocket != HSteamListenSocket.Invalid) {
                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseListenSocket(listenSocket);
                } else {
                    SteamNetworkingSockets.CloseListenSocket(listenSocket);
                }

                listenSocket = HSteamListenSocket.Invalid;
            }
            BrowserSteamPing.CloseHostListen();
        }

        internal void Shutdown(bool reset_net) {
            CloseListenSocket();
            CloseP2PSessions();

            if (reset_net) {
                if (CURRENT_LOBBY.lobby_steamID != CSteamID.Nil || KSteam.IS_IN_LOBBY) {
                    KSteam.LeaveLobbyAndResetNet(false);
                }
            }
        }

        private void CloseConnectionOnServerUser() {
            if (serverUser != null) {
                if (connectionMapping.ContainsKey(serverUser.id.m_SteamID)) {
                    connectionMapping.Remove(serverUser.id.m_SteamID);
                }

                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
                } else {
                    SteamNetworkingSockets.CloseConnection(serverUser.connection, 0, "Disconnected", false);
                }
            }
            serverUser = null;
        }

        private void CloseP2PSessions() {
            CloseConnectionOnServerUser();

            foreach (SteamConnectionData user in connectionMapping.Values) {
                if (is_steamserver) {
                    SteamGameServerNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
                } else {
                    SteamNetworkingSockets.CloseConnection(user.connection, 0, "Disconnected", false);
                }
            }

            connectionMapping.Clear();
            if (log.verbose) {
                KSteam.steamtestlog($"{this.GetType().Name}.CloseP2PSessions - has Closed P2P Sessions With all Users");
            }

            if (onConnectionChange != null) {
                onConnectionChange.Dispose();
                onConnectionChange = null;
            }
        }

        bool NetTransportBase.IsConnecting() {
            if (Net.IsClient && ScavPlayer.LocalPlayer == null) {
                return true;
            }
            return !KSteam.IS_IN_LOBBY;
        }
    }
}
