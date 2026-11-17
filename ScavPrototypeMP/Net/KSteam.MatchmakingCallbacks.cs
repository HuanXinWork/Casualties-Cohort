using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using Steamworks;
using System;
using Unity.VisualScripting;

namespace Together {
    public partial class KSteam {

        // STEAM MATCHMAKING & BEACON CALLBACKS
        internal static Callback<FavoritesListChanged_t> FavoritesListChanged;
        internal static Callback<LobbyInvite_t> LobbyInvite;
        internal static Callback<LobbyEnter_t> LobbyEnter;
        internal static Callback<LobbyDataUpdate_t> LobbyDataUpdate;
        internal static Callback<LobbyChatUpdate_t> LobbyChatUpdate;
        internal static Callback<LobbyChatMsg_t> LobbyChatMsg;
        internal static Callback<LobbyGameCreated_t> LobbyGameCreated;
        internal static Callback<LobbyKicked_t> LobbyKicked;
        internal static Callback<FavoritesListAccountsUpdated_t> FavoritesListAccountsUpdated;
        internal static Callback<JoinPartyCallback_t> JoinPartyCallback;
        internal static Callback<CreateBeaconCallback_t> CreateBeaconCallback;
        internal static Callback<ReservationNotificationCallback_t> ReservationNotificationCallback;
        internal static Callback<ChangeNumOpenSlotsCallback_t> ChangeNumOpenSlotsCallback;
        internal static Callback<AvailableBeaconLocationsUpdated_t> AvailableBeaconLocationsUpdated;
        internal static Callback<ActiveBeaconsUpdated_t> ActiveBeaconsUpdated;
        internal static CallResult<LobbyEnter_t> OnLobbyEnterCallResult;
        internal static CallResult<LobbyMatchList_t> OnLobbyMatchListCallResult;
        internal static CallResult<LobbyCreated_t> OnLobbyCreatedCallResult;

        static void OnFavoritesListChanged(FavoritesListChanged_t pCallback) {
            steamtestlog("[" + FavoritesListChanged_t.k_iCallback + " - FavoritesListChanged] - " + pCallback.m_nIP + " -- " + pCallback.m_nQueryPort + " -- " + pCallback.m_nConnPort + " -- " + pCallback.m_nAppID + " -- " + pCallback.m_nFlags + " -- " + pCallback.m_bAdd + " -- " + pCallback.m_unAccountId);
        }

        static void OnLobbyInvite(LobbyInvite_t pCallback) {
            steamtestlog("[" + LobbyInvite_t.k_iCallback + " - LobbyInvite] - " + pCallback.m_ulSteamIDUser + " -- " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulGameID);
        }

        // this gets called both on client and server
        static void OnLobbyEnter(LobbyEnter_t pCallback) {
            var enterresponse = (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse;

            steamtestlog("[" + LobbyEnter_t.k_iCallback + " - LobbyEnter] - " +
                pCallback.m_ulSteamIDLobby
                + " -- " + pCallback.m_rgfChatPermissions
                + " -- " + pCallback.m_bLocked
                + " -- " + enterresponse);

            if (!Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                IS_IN_LOBBY = false;
                if (Together.log.verbose && enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) {
                    Together.log.error("STEAM: Received OnLobbyEnter without a transport !!!");
                }
                return;
            }
            if (enterresponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
                var msg = enterresponse.ToString().TrimStart("k_EChatRoomEnterResponse");

                Multiplayer.DoMultiplayerStatusMessageLog($"OnLobbyEnter failed! response: {msg}");

                if (!IS_DOING_HostLobbyRecreatorLoop) {
                    IS_DOING_HostLobbyRecreatorLoop = false;
                    LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                    IS_IN_LOBBY = false;
                }
                return;
            }

            CURRENT_LOBBY.locked = Net.CurrentServerInfo.haspassword || pCallback.m_bLocked;
            UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);

            if (Net.IsServer) {
                if (tsteam.CreateServerSocket()) {
                    IS_DOING_HostLobbyRecreatorLoop = false;
#if TESTING
                    Multiplayer.DoMultiplayerStatusMessageLog("CreateServerSocket Success.");
#endif
                    CURRENT_LOBBY.locked = false;
                } else {
                    LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                    return;
                }
            }
        }

        // this gets called clientside!
        static void OnLobbyEnter_APIDispatch(LobbyEnter_t pCallback, bool bIOFailure) {
            try {
                steamtestlog("[" + LobbyEnter_t.k_iCallback + " - LobbyEnterAPIDispatch] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_rgfChatPermissions + " -- " + pCallback.m_bLocked + " -- " + (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse);

                if (bIOFailure) {
                    ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyEnterCallResult.Handle);
                    Multiplayer.DoMultiplayerStatusMessageError("STEAM: OnLobbyEnter IOFailure: " + reason);

                    IS_IN_LOBBY = false;
                    Net.ShutdownReset();
                    return;
                }

                var m_LobbyEnterResponse = (EChatRoomEnterResponse)pCallback.m_EChatRoomEnterResponse;
                var responseStr = m_LobbyEnterResponse.ToString().TrimStart("k_EChatRoomEnterResponse");

                if (m_LobbyEnterResponse != EChatRoomEnterResponse.k_EChatRoomEnterResponseSuccess) {
                    if (m_LobbyEnterResponse == EChatRoomEnterResponse.k_EChatRoomEnterResponseDoesntExist) {
                        Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Lobby Enter Rejected: Lobby does not exist!");
                    } else {
                        Multiplayer.DoMultiplayerStatusMessageError("STEAM: Lobby Enter Rejected: " + responseStr);
                    }

                    IS_IN_LOBBY = false;
                    Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                    Net.ShutdownReset();
                    return;
                }

                if (!Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                    LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                    IS_IN_LOBBY = false;
                    if (Together.log.verbose) {
                        Together.log.error("STEAM: Received OnLobbyEnter_APIDispatch without a transport !!!");
                    }
                    return;
                }

                CURRENT_LOBBY.locked = Net.CurrentServerInfo.haspassword || pCallback.m_bLocked;
                UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);

                IS_IN_LOBBY = true;

#if TESTING
                Multiplayer.DoMultiplayerStatusMessageLog($"Steam: OnLobbyEnter  lobby: {LobbyId}   ");
#else
                Multiplayer.DoMultiplayerStatusMessageLog($"Steam: OnLobbyEnter");
#endif
                UpdateRichPresence();

                if (Net.IsClient) {
                    if (tsteam.CreateClientSocket()) {
                        Multiplayer.DoMultiplayerStatusMessageLog("CreateClientSocket Success.");

                        Net.OnTransportConnected();

                        NetDataWriter hellopacket = Multiplayer.GetClientConnectIntroductionPacket();
                        tsteam.SendThroughSteamSocket(tsteam.serverUser.connection, new ArraySegment<byte>(hellopacket.Data), Steamworks.Constants.k_nSteamNetworkingSend_Reliable);
                    } else {
                        LeaveLobbyAndResetNet((CSteamID)pCallback.m_ulSteamIDLobby);
                        IS_IN_LOBBY = false;
                        return;
                    }
                }
            } catch (Exception e) {
                Together.log.error("STEAM: OnLobbyEnter_APIDispatch: " + e.ToString());
            }
        }

        static void OnLobbyDataUpdate(LobbyDataUpdate_t pCallback) {
            var lobbyId = (CSteamID)pCallback.m_ulSteamIDLobby;

            // browser RequestLobbyData result - don't overwrite CURRENT_LOBBY
            if (_pendingBrowserLobbyData.Remove(pCallback.m_ulSteamIDLobby)) {
                if (pCallback.m_bSuccess == 0) {
                    Together.log.warn($"[LobbyBrowser] DATA-FAIL id={pCallback.m_ulSteamIDLobby}");
                    return;
                }
                Lobby slobby = new Lobby();
                UpdateLobbyInfo(lobbyId, ref slobby);
                Together.log.warn($"[LobbyBrowser] DATA-OK id={pCallback.m_ulSteamIDLobby} keys={slobby.metadata.Count}");
                TryAddLobbyToBrowser(slobby);
                return;
            }

            if (!IS_IN_LOBBY || CURRENT_LOBBY.lobby_steamID != lobbyId) {
                return;
            }

            var ooo = CURRENT_LOBBY.ownerID;
            UpdateLobbyInfo(lobbyId, ref CURRENT_LOBBY);
            CURRENT_LOBBY.ownerID = ooo;
        }

        static void OnLobbyChatUpdate(LobbyChatUpdate_t pCallback) {
            try {
                var stateChange = (EChatMemberStateChange)pCallback.m_rgfChatMemberStateChange;
                if (pCallback.m_ulSteamIDUserChanged == pCallback.m_ulSteamIDMakingChange) {
                    steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + stateChange, true);
                } else {
                    steamtestlog("[" + LobbyChatUpdate_t.k_iCallback + " - LobbyChatUpdate] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUserChanged) + " -- " + pCallback.m_ulSteamIDMakingChange + " -- " + stateChange, true);
                }

                if (!IS_IN_LOBBY || !Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                    if (Multiplayer.verbose) {
                        Together.log.warn("Received OnLobbyChatUpdate while transport is not active, what?");
                    }

                    UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
                    return;
                }

                ulong changedUser = pCallback.m_ulSteamIDUserChanged;
                ulong makingChangeUser = pCallback.m_ulSteamIDMakingChange;

                bool joined = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeEntered) != 0;
                bool left = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeLeft) != 0;
                bool disconnected = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeDisconnected) != 0;
                bool kicked = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeKicked) != 0;
                bool banned = (stateChange & EChatMemberStateChange.k_EChatMemberStateChangeBanned) != 0;

                ulong localUser = SteamUser.GetSteamID().m_SteamID;
                bool isLocalUser = changedUser == localUser;

                if (joined && !isLocalUser) {
                    if (tsteam.i_am_owner_of_this_lobby && BanList.IsBanned(pCallback.m_ulSteamIDUserChanged)) {
                        tsteam.KickMember((CSteamID)pCallback.m_ulSteamIDUserChanged, "Banned!");
                        tsteam.RemoveSteamUser(pCallback.m_ulSteamIDUserChanged);
                    } else {
                        SteamFriends.RequestUserInformation((CSteamID)pCallback.m_ulSteamIDUserChanged, true);
                    }
                }

                if (left || disconnected || kicked || banned) {
                    if (isLocalUser && tsteam.i_am_owner_of_this_lobby) {
                        Multiplayer.DoMultiplayerStatusMessageLog($"I got removed from the lobby. But im the owner!\nAttempting to recreate lobby in a sec...");

                        CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;
                        tsteam.Shutdown(false);
                        Util.StartCoroutine(HostLobbyRecreatorLoop);
                        return;
                    }

                    if (isLocalUser) {
                        Multiplayer.DoMultiplayerStatusMessageLog($"I got removed from the lobby.");

                        if (Util.IsInWorld()) {
                            Multiplayer.showMultiplayerMenu = true;
                            PlayerCamera.main.ToMainMenu();
                        }
                        UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
                        LeaveLobbyAndResetNet(true);
                        return;
                    }

                    int rawFlags = (int)stateChange;
                    int memberCount = SteamMatchmaking.GetNumLobbyMembers((CSteamID)pCallback.m_ulSteamIDLobby);
                    string persona = SteamFriends.GetFriendPersonaName((CSteamID)changedUser);
                    bool closeP2p = tsteam.connectionMapping.ContainsKey(changedUser);
                    string connSnap = tsteam.DescribeLobbyLeftConnection(changedUser);
                    Together.log.l("[lobby-left] steam=" + changedUser
                        + " name=\"" + TransportSteamworks.OneLine(persona) + "\""
                        + " flags=" + stateChange + " (" + rawFlags + ")"
                        + " joined=" + (joined ? 1 : 0)
                        + " left=" + (left ? 1 : 0)
                        + " disconnected=" + (disconnected ? 1 : 0)
                        + " kicked=" + (kicked ? 1 : 0)
                        + " banned=" + (banned ? 1 : 0)
                        + " making=" + makingChangeUser
                        + " self=" + (changedUser == makingChangeUser ? 1 : 0)
                        + " lobby=" + pCallback.m_ulSteamIDLobby
                        + " members=" + memberCount
                        + " closeP2P=" + (closeP2p ? 1 : 0)
                        + " method=TransportSteamworks.RemoveSteamUser "
                        + connSnap);
                    tsteam.RemoveSteamUser(pCallback.m_ulSteamIDUserChanged);

                    if (Net.IsClient && pCallback.m_ulSteamIDUserChanged == CURRENT_LOBBY.ownerID.m_SteamID) {
                        Multiplayer.DoMultiplayerStatusMessageLog("LOBBY OWNER LEFT, LEAVING");

                        if (Util.IsInWorld()) {
                            Multiplayer.showMultiplayerMenu = true;
                            PlayerCamera.main.ToMainMenu();
                        }
                        LeaveLobbyAndResetNet(true);
                    }
                }
            } catch (Exception e) {
                Together.log.error("STEAM: LobbyChatUpdate: " + e.ToString());
            }
            UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);
        }

        static void OnLobbyChatMsg(LobbyChatMsg_t pCallback) {
            steamtestlog("[" + LobbyChatMsg_t.k_iCallback + " - LobbyChatMsg] - " + pCallback.m_ulSteamIDLobby + " -- " + TransportSteamworks.BetterToStringFromSteamID(pCallback.m_ulSteamIDUser) + " -- " + (EChatEntryType)pCallback.m_eChatEntryType + " -- " + pCallback.m_iChatID);

            byte[] data = new byte[4096];
            int ret = SteamMatchmaking.GetLobbyChatEntry((CSteamID)pCallback.m_ulSteamIDLobby, (int)pCallback.m_iChatID, out CSteamID SteamIDUser, data, data.Length, out EChatEntryType ChatEntryType);
            steamtestlog("GetLobbyChatEntry(" + (CSteamID)pCallback.m_ulSteamIDLobby + ", " + (int)pCallback.m_iChatID + ", out SteamIDUser, Data, Data.Length, out ChatEntryType) : " + ret + " -- " + SteamIDUser + " -- " + System.Text.Encoding.UTF8.GetString(data, 0, ret).Trim() + " -- " + ChatEntryType);

            string msg = System.Text.Encoding.UTF8.GetString(data, 0, ret);
            if (msg.StartsWith("KICK:")) {
                if (pCallback.m_ulSteamIDUser == CURRENT_LOBBY.ownerID.m_SteamID) {
                    string reason = "u got kicked for some reson";

                    msg = msg.Substring(5);
                    int colonloc = msg.IndexOf(':');
                    if (colonloc != -1) {
                        reason = msg.Substring(colonloc + 1);
                        msg = msg.Substring(0, colonloc);
                    }
                    ulong targetId = ulong.Parse(msg);

                    if (targetId == SteamUser.GetSteamID().m_SteamID) {
                        Multiplayer.DoMultiplayerStatusMessageLog("STEAM: Kicked: " + reason);
                        LeaveLobbyAndResetNet(true);
                    }
                }
            }
        }

        static void OnLobbyGameCreated(LobbyGameCreated_t pCallback) {
            steamtestlog("[" + LobbyGameCreated_t.k_iCallback + " - LobbyGameCreated] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulSteamIDGameServer + " -- " + pCallback.m_unIP + " -- " + pCallback.m_usPort);
        }

        static void OnLobbyMatchList(LobbyMatchList_t pCallback, bool bIOFailure) {
            LOBBIES.Clear();
            if (Together.log.verbose) {
                steamtestlog("[" + LobbyMatchList_t.k_iCallback + " - LobbyMatchList] - " + pCallback.m_nLobbiesMatching + $"  {(LobbySearch_BucketCounter == 0 ? "" : (LobbySearch_BucketCounter - 1).ToString())}");
            }

            if (bIOFailure) {
                ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyMatchListCallResult.Handle);
                Together.log.error("OnLobbyMatchList encountered an IOFailure due to: " + reason);
                return;
            }
            try {
                if (pCallback.m_nLobbiesMatching == 0) {
                    Multiplayer.DoMultiplayerStatusMessageLog("STEAM: No lobbies found, lol, game fell off");
                } else {
                    int added = 0;
                    int pendingData = 0;
                    for (int i = 0; i < pCallback.m_nLobbiesMatching; i++) {
                        Lobby slobby = new Lobby();

                        UpdateLobbyInfo(SteamMatchmaking.GetLobbyByIndex(i), ref slobby);
                        LOBBIES.Add(slobby);

                        if (TryAddLobbyToBrowser(slobby)) {
                            added++;
                        } else {
                            int before = _pendingBrowserLobbyData.Count;
                            RequestBrowserLobbyDataIfNeeded(slobby);
                            if (_pendingBrowserLobbyData.Count > before) {
                                pendingData++;
                            } else {
#if !RELEASE
                                log.LogWarning($"INCOMPATIBLE LOBBY INFO DUMP:\n{slobby.DebugReadableDump()}");
#endif
                            }
                        }
                    }
                    Together.log.warn($"[LobbyBrowser] MatchList steam={pCallback.m_nLobbiesMatching} accepted={added} request-data={pendingData} ui={UIServerBrowser.CurServerCount}/{UIServerBrowser.CurFilteredServerList.Count} myVer=\"{Multiplayer.FULL_VERSION_TAG}\"");
                }

                if (LobbySearch_BucketCounter == 0) {
                    SearchFriendLobbies();
                }

                if (UIServerBrowser.CurServerCount > 35 && LobbySearch_BucketCounter <= MAX_LOBBY_BUCKETS) {
                    Util.DelayCallLambda(0.2f, () => {
                        KSteam.SearchLobbies(true);
                        LobbySearch_BucketCounter++;
                    });
                }
            } catch (Exception e) {
                Together.log.error("STEAM: LobbyMatchList: " + e.ToString());
            }
        }

        static void OnLobbyKicked(LobbyKicked_t pCallback) {
            steamtestlog("[" + LobbyKicked_t.k_iCallback + " - LobbyKicked] - " + pCallback.m_ulSteamIDLobby + " -- " + pCallback.m_ulSteamIDAdmin + " -- " + pCallback.m_bKickedDueToDisconnect);
        }

        static void OnLobbyCreated(LobbyCreated_t pCallback, bool bIOFailure) {
            steamtestlog("[" + LobbyCreated_t.k_iCallback + " - LobbyCreated] - " + pCallback.m_eResult + " -- " + pCallback.m_ulSteamIDLobby);

            if (bIOFailure) {
                ESteamAPICallFailure reason = SteamUtils.GetAPICallFailureReason(OnLobbyCreatedCallResult.Handle);
                Multiplayer.DoMultiplayerStatusMessageError("STEAM: OnLobbyCreated encountered an IOFailure due to: " + reason);

                IS_IN_LOBBY = false;
                Net.ShutdownReset();
                Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                return;
            } else if (pCallback.m_eResult != EResult.k_EResultOK) {
                Util.PlayUISound(PlayerCamera.UISoundType.Deny);
                Net.ShutdownReset();
                IS_IN_LOBBY = false;
                if (pCallback.m_eResult == EResult.k_EResultNoConnection) {
                    Multiplayer.DoMultiplayerStatusMessageError($"Steam: Create Lobby FAILED: No internet connection.");
                    return;
                }
                Multiplayer.DoMultiplayerStatusMessageError($"Steam: Create Lobby FAILED: {pCallback.m_eResult}");
                return;
            }

            if (Net.Transport == null || (Net.Transport is not TransportSteamworks)) {
                Multiplayer.DoMultiplayerStatusMessageError($"CREATED LOBBY BUT THERES NO STEAMWORKS TRASNPORT WHAAAAT??!?!?!!??");

                IS_IN_LOBBY = false;
                Net.ShutdownReset();
                return;
            }

            UpdateLobbyInfo((CSteamID)pCallback.m_ulSteamIDLobby, ref CURRENT_LOBBY);

            IS_IN_LOBBY = true;

            int bucket = 0;
            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                bucket = tsteam.hostLobbyBucket;
            }

            Net.OnTransportConnected();

            if (ScavPlayer.LocalPlayer != null) {
                ScavPlayer.LocalPlayer.SteamId = GetLocalUserSteamID().m_SteamID;
                ScavPlayer.LocalPlayer.ApplyNameAndColor(ScavPlayer.LocalPlayer.SteamId.ToString(), UIMainMenu.LAST_VALID_INPUT_COLOR);

                tsteam.SteamIDToNetPlayerDict[ScavPlayer.LocalPlayer.SteamId] = ScavPlayer.LocalPlayer;
            }

            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG);
            SteamMatchmaking.SetLobbyData(LobbyId, "bucket", bucket.ToString());
            Util.DelayCallLambda(0.5f, () => {
                if (!IS_IN_LOBBY) {
                    return;
                }
                Server_UpdateLobbyData();
            });

            Multiplayer.DoMultiplayerStatusMessageLog($"STEAM: Lobby Created :3");
            UpdateRichPresence();
        }

        static void OnFavoritesListAccountsUpdated(FavoritesListAccountsUpdated_t pCallback) {
            steamtestlog("[" + FavoritesListAccountsUpdated_t.k_iCallback + " - FavoritesListAccountsUpdated] - " + pCallback.m_eResult);
        }

        static void OnJoinPartyCallback(JoinPartyCallback_t pCallback) {
            steamtestlog("[" + JoinPartyCallback_t.k_iCallback + " - JoinPartyCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ulBeaconID + " -- " + pCallback.m_SteamIDBeaconOwner + " -- " + pCallback.m_rgchConnectString);
        }

        static void OnCreateBeaconCallback(CreateBeaconCallback_t pCallback) {
            steamtestlog("[" + CreateBeaconCallback_t.k_iCallback + " - CreateBeaconCallback] - " + pCallback.m_eResult + " -- " + pCallback.m_ulBeaconID);
        }

        static void OnReservationNotificationCallback(ReservationNotificationCallback_t pCallback) {
            steamtestlog("[" + ReservationNotificationCallback_t.k_iCallback + " - ReservationNotificationCallback] - " + pCallback.m_ulBeaconID + " -- " + pCallback.m_steamIDJoiner);
        }

        static void OnChangeNumOpenSlotsCallback(ChangeNumOpenSlotsCallback_t pCallback) {
            steamtestlog("[" + ChangeNumOpenSlotsCallback_t.k_iCallback + " - ChangeNumOpenSlotsCallback] - " + pCallback.m_eResult);
        }

        static void OnAvailableBeaconLocationsUpdated(AvailableBeaconLocationsUpdated_t pCallback) {
            steamtestlog("[" + AvailableBeaconLocationsUpdated_t.k_iCallback + " - AvailableBeaconLocationsUpdated]");
        }

        static void OnActiveBeaconsUpdated(ActiveBeaconsUpdated_t pCallback) {
            steamtestlog("[" + ActiveBeaconsUpdated_t.k_iCallback + " - ActiveBeaconsUpdated]");
        }
    }
}
