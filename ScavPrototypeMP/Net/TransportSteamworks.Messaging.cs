using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Runtime.InteropServices;
using Unity.VisualScripting;
using UnityEngine;
using static Together.KSteam;

namespace Together {

    public partial class TransportSteamworks {

        void _Update_PollMessages() {
            // receive only what this frame will dispatch. Steam leaves the rest queued.
            int cap = Net.IsClient ? ClientPollCap : HostPollCap;
            if (_pollPtrs == null || _pollPtrs.Length < cap) {
                _pollPtrs = new IntPtr[cap];
            }

            _pollConnections.Clear();
            foreach (SteamConnectionData connectionData in connectionMapping.Values) {
                _pollConnections.Add(connectionData);
            }

            int remaining = cap;
            for (int c = 0; c < _pollConnections.Count; c++) {
                if (Net.IsClient && remaining <= 0) {
                    break;
                }

                SteamConnectionData connectionData = _pollConnections[c];
                int take = Net.IsClient ? remaining : cap;
                int messageCount = is_steamserver
                    ? SteamGameServerNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take)
                    : SteamNetworkingSockets.ReceiveMessagesOnConnection(connectionData.connection, _pollPtrs, take);
                if (messageCount <= 0) {
                    continue;
                }

                if (messageCount > take) {
                    messageCount = take;
                }

                for (int i = 0; i < messageCount; i++) {
                    SteamNetworkingMessage_t data = Marshal.PtrToStructure<SteamNetworkingMessage_t>(_pollPtrs[i]);
                    FindTheBugInator.NoteSteamIn(data.m_cbSize);
                    byte[] buffer = new byte[data.m_cbSize];
                    Marshal.Copy(data.m_pData, buffer, 0, data.m_cbSize);
                    SteamNetworkingMessage_t.Release(_pollPtrs[i]);

                    try {
                        _K_OnReceiveNetworkingMessage(connectionData, buffer);
                    } catch (Exception e) {
                        Plugin.Logger.LogError($"{this.GetType().Name}._K_OnReceiveNetworkingMessage\n ERROR:\n{e.ToString()}\nCURRENT LOBBY:{(KSteam.CURRENT_LOBBY == null ? "NULL_LOBBY" : KSteam.CURRENT_LOBBY.DebugReadableDump())}\nconnectionData: {connectionData}");
                    }
                }

                if (Net.IsClient) {
                    remaining -= messageCount;
                }
            }
        }

        void _Server_NewLobbyMember(NetDataReader reader, ulong steamID) {
            bool privileged_user = KnownPersons.IsSteamUserPrivileged(steamID);

            bool validated = Net.ValidateNewClientHandshake(reader, false, privileged_user, out Multiplayer.IntroductionPlayerInfo userInfo, out string reject_reason);
            if (validated && !privileged_user && BanList.IsBanned(steamID)) {
                validated = false;
                reject_reason = "Banned";
            }
            if (!validated) {
#if TESTING
                steamtestlog($"Rejected player {BetterToStringFromSteamID(steamID)} for {reject_reason}\ndata:\n{reader.RawData.ToHexString()}", true);
#else
                steamtestlog($"Rejected player {BetterToStringFromSteamID(steamID)} for {reject_reason}");
#endif

                KickMember((CSteamID)steamID, reject_reason);

                Util.DelayCallLambda(0.4f, () => {
                    RemoveSteamUser(steamID);
                });
                return;
            }

            string joined = userInfo.userName;
            if (string.IsNullOrEmpty(joined) || joined == "EMPTY_STRING") {
                joined = steamID.ToString();
            }
            BasePlayer playerEntity = BasePlayer.ServerCreatePlayer(joined, steamID, userInfo.color, false);

            try {
                SteamIDToNetPlayerDict[steamID] = playerEntity.player;
                SteamIDToClientIDDict[steamID] = playerEntity.player.playerId;
            } catch (Exception e) {
                log.error($"Steam: Player creation: {e}");
            }

            log.l($"{this.GetType().Name}: Accepted connection for: {playerEntity.player}  color:{userInfo.color}");
        }

        void _K_OnReceiveNetworkingMessage(SteamConnectionData connection, byte[] data) {
            var steamID = connection.id.m_SteamID;
            NetDataReader reader = new NetDataReader(data);

            if (i_am_owner_of_this_lobby) {
                if (!KSteam.CURRENT_LOBBY.members.TryGetValue(connection.id, out LobbyMember _member)) {
                    _member = new(connection.id);
                    KSteam.CURRENT_LOBBY.members[connection.id] = _member;
                }
                _member.server_last_data_receive_time = Time.realtimeSinceStartupAsDouble;

                if (!SteamIDToClientIDDict.TryGet(steamID, out var clientID)) { // new player !
                    _Server_NewLobbyMember(reader, steamID);
                    return;
                }
                Net.InvokeServerMessage(clientID, reader);
            } else {
                if (connection.id != KSteam.CURRENT_LOBBY.ownerID) {
#if !RELEASE
                    log.error($"RECEIVING MESSAGES NOT FROM A LOBBY HOST !!!!!! connection.id:{connection.id}");
#endif
                    return;
                }
                Net.InvokeClientMessage(Net.SERVER_CLIENTID, reader);
            }
        }

        internal static string OneLine(string value) {
            if (value is null) {
                return "";
            }
            return value.Replace('\r', ' ').Replace('\n', ' ').Replace('"', '\'');
        }

        // socket state at the moment steam says they left, before we close it
        internal string DescribeLobbyLeftConnection(ulong steamID) {
            if (!connectionMapping.TryGetValue(steamID, out SteamConnectionData conndata)) {
                return "conn=none";
            }

            HSteamNetConnection conn = conndata.connection;
            string text = "conn=" + conn.m_HSteamNetConnection;

            try {
                SteamNetConnectionInfo_t info;
                bool gotInfo = is_steamserver
                    ? SteamGameServerNetworkingSockets.GetConnectionInfo(conn, out info)
                    : SteamNetworkingSockets.GetConnectionInfo(conn, out info);

                if (gotInfo) {
                    ESteamNetConnectionEnd endReason = (ESteamNetConnectionEnd)info.m_eEndReason;
                    string debug = OneLine(info.m_szEndDebug);
                    text += " state=" + info.m_eState + " (" + ((int)info.m_eState) + ")"
                        + " end=" + endReason + " (" + info.m_eEndReason + ")"
                        + " debug=\"" + debug + "\"";
                } else {
                    text += " info=false";
                }
            } catch (Exception e) {
                text += " infoErr=" + e.GetType().Name;
            }

            try {
                SteamNetConnectionRealTimeStatus_t status = new SteamNetConnectionRealTimeStatus_t();
                SteamNetConnectionRealTimeLaneStatus_t laneStatus = new SteamNetConnectionRealTimeLaneStatus_t();
                EResult result = is_steamserver
                    ? SteamGameServerNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus)
                    : SteamNetworkingSockets.GetConnectionRealTimeStatus(conn, ref status, 0, ref laneStatus);

                if (result == EResult.k_EResultOK) {
                    text += " rtState=" + status.m_eState
                        + " ping=" + status.m_nPing
                        + " pendingReliable=" + status.m_cbPendingReliable
                        + " pendingUnreliable=" + status.m_cbPendingUnreliable;
                } else {
                    text += " rt=" + result;
                }
            } catch (Exception e) {
                text += " rtErr=" + e.GetType().Name;
            }

            return text;
        }

        public int ConvertDeliveryMethodToSteamNetworkingSendFlag(in DeliveryMethod delivery) {
            int sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoDelay;
            switch (delivery) {
                case DeliveryMethod.ReliableUnordered:
                case DeliveryMethod.ReliableSequenced:
                case DeliveryMethod.ReliableOrdered:
                    sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_ReliableNoNagle;
                    break;
                case DeliveryMethod.Sequenced:
                    sendFlag = Steamworks.Constants.k_nSteamNetworkingSend_UnreliableNoNagle;
                    break;
            }
            return sendFlag;
        }

        public void SendThroughSteamSocket(ulong steamID, ReadOnlySpan<byte> data, DeliveryMethod delivery) => SendThroughSteamSocket(steamID, data, ConvertDeliveryMethodToSteamNetworkingSendFlag(delivery));

        public void SendThroughSteamSocket(ulong steamID, ReadOnlySpan<byte> data, int sendFlag) {
            if (!KSteam.CURRENT_LOBBY.lobby_steamID.IsValid()) {
#if !RELEASE || PRERELASE
                Plugin.Logger.LogWarning("SendThroughSteamSocket: Failed cuz lobby is non-existant (program is assuming lobby will get recreated soon)");
#endif
                return;
            }

            if (steamID == 0) {
                if (serverUser == null) {
                    return;
                }
                steamID = serverUser.id.m_SteamID;
            }

            if (connectionMapping.TryGetValue(steamID, out SteamConnectionData mappedconnection)) {
                SendThroughSteamSocket(mappedconnection.connection, data, sendFlag);
            } else {
#if RELEASE && !PLAYTEST
                if (log.verbose)
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}");
#else
                if (log.verbose) // DO NOT CHANGE THESE TO log.error !!! it causes stack overflow!!!
                {
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Trying to send to an unknown connection  {BetterToStringFromSteamID(steamID)}\n{new StackTrace()}");
                }
#endif
            }
        }

        double lastExceedWarn = 0;
        public unsafe void SendThroughSteamSocket(in HSteamNetConnection connection, ReadOnlySpan<byte> data, int sendFlag) {
            // 直接使用 fixed 钉住 ReadOnlySpan<byte> 传入 Steam 非托管发送缓冲，消除每包堆拷贝与 GCHandle 锁竞争
            EResult response;
            fixed (byte* ptr = data) {
                IntPtr pData = data.IsEmpty ? IntPtr.Zero : (IntPtr)ptr;
                if (is_steamserver) {
                    response = SteamGameServerNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data.Length, sendFlag, out long _);
                } else {
                    response = SteamNetworkingSockets.SendMessageToConnection(connection, pData, (uint)data.Length, sendFlag, out long _);
                }
            }

            if (response == EResult.k_EResultOK) {
                FindTheBugInator.NoteSteamOut(data.Length);
                return;
            }

            if (response == EResult.k_EResultNoConnection && !log.verbose) {
                return;
            }

            uint targetConnHandle = connection.m_HSteamNetConnection;
            SteamConnectionData foundsteamid = null;
            foreach (SteamConnectionData entry in connectionMapping.Values) {
                if (entry != null && entry.connection.m_HSteamNetConnection == targetConnHandle) {
                    foundsteamid = entry;
                    break;
                }
            }

            string msgid_as_str = "";
#if !(RELEASE && !PLAYTEST)
            if (!data.IsEmpty) {
                msgid_as_str = Net.ByteMsgIdToName(data[0]) + " :";
            }
#endif

            if (foundsteamid != null) {
                if (response == EResult.k_EResultLimitExceeded) {
                    if (Time.realtimeSinceStartupAsDouble - lastExceedWarn > 30) {
                        lastExceedWarn = Time.realtimeSinceStartupAsDouble;
                        Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)}");
                    }
                } else {
#if TESTING
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)} {new StackTrace()}");
#else
                    Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response} {BetterToStringFromSteamID(foundsteamid.id)}");
#endif
                }
            } else {
                Plugin.Logger.LogError($"{this.GetType().Name}.Send: Could not send: {msgid_as_str}{response}  UNKNOWN CONNECTION {connection}");
            }
        }

        public static string BetterToStringFromSteamID(CSteamID steamid) => BetterToStringFromSteamID(steamid.m_SteamID);
        public static string BetterToStringFromSteamID(ulong steamid) {
            string plrid = "";
            if (Net.TryGetSteamTransport(out TransportSteamworks ttttt) && ttttt.SteamIDToClientIDDict.TryGet(steamid, out knetid plrdknetid)) {
                plrid = $", plrid:{plrdknetid}";
            }
            string tagstr = "";
            {
                List<string> tags = new List<string>();
                if (KSteam.IS_IN_LOBBY && steamid == KSteam.CURRENT_LOBBY.ownerID.m_SteamID) {
                    tags.Add("OWNER");
                }
                if (steamid == KSteam.GetLocalUserSteamID().m_SteamID) {
                    tags.Add("LOCAL");
                }
                if (tags.Count > 0) {
                    tagstr = String.Join(", ", tags) + ", ";
                }
            }

            return $"MEMBER({tagstr}steamID:{steamid}, name:{SteamFriends.GetFriendPersonaName((CSteamID)steamid)}{plrid})";
        }

        public void Server_SendTo(in int steam_sendFlags, in NetDataWriter writer, in knetid clientid) {
            if (SteamIDToClientIDDict.TryGet(clientid, out ulong user_steamid)) {
                SendThroughSteamSocket(user_steamid, writer.AsReadOnlySpan(), steam_sendFlags);
            } else if (log.verbose) {
                Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
            }
        }

        public void Server_SendToClients(in int steam_sendFlags, in NetDataWriter writer, in IReadOnlyList<knetid> clientIds) {
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
            foreach (knetid cID in clientIds) {
                Server_SendTo(steam_sendFlags, writer, cID);
            }
        }

        public void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid) {
            if (SteamIDToClientIDDict.TryGet(clientid, out ulong user_steamid)) {
                SendThroughSteamSocket(user_steamid, data, delivery);
            } else if (log.verbose) {
                Plugin.Logger.LogError($"SERVER: TransportSteamworks.Server_SendTo: UNKNOWN PLAYER: {DebugHelp.GetPlayerFullDebugString(clientid)}\n{new StackTrace()}");
            }
        }

        public void Server_SendToClients(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in IReadOnlyList<knetid> clientIds) {
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
            foreach (knetid cID in clientIds) {
                Server_SendTo(delivery, data, cID);
            }
        }

        public void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) {
            SendThroughSteamSocket(KSteam.CURRENT_LOBBY.ownerID.m_SteamID, data, delivery);
        }

        public void FlushNagle() {
            // 客户端仅刷新对房主单连接一次，服务端遍历所有已连接客户端
            if (Net.IsClient) {
                if (serverUser != null && serverUser.connection != HSteamNetConnection.Invalid) {
                    SteamNetworkingSockets.FlushMessagesOnConnection(serverUser.connection);
                }
                return;
            }
            foreach (KeyValuePair<ulong, SteamConnectionData> item in connectionMapping) {
                if (item.Value != null && item.Value.connection != HSteamNetConnection.Invalid) {
                    SteamNetworkingSockets.FlushMessagesOnConnection(item.Value.connection);
                }
            }
        }

        public void FlushNagle(knetid plrId) {
            if (SteamIDToClientIDDict.TryGet(plrId, out ulong steamId) && connectionMapping.TryGetValue(steamId, out SteamConnectionData connection) && connection != null && connection.connection != HSteamNetConnection.Invalid) {
                SteamNetworkingSockets.FlushMessagesOnConnection(connection.connection);
            }
        }
    }
}
