using Steamworks;

namespace Together {
    public partial class KSteam {

        // STEAM FRIENDS & OVERLAY CALLBACKS
        static Callback<PersonaStateChange_t> PersonaStateChange;
        static Callback<GameOverlayActivated_t> GameOverlayActivated;
        static Callback<GameServerChangeRequested_t> GameServerChangeRequested;
        static Callback<GameLobbyJoinRequested_t> GameLobbyJoinRequested;
        static Callback<AvatarImageLoaded_t> AvatarImageLoaded;
        static Callback<FriendRichPresenceUpdate_t> FriendRichPresenceUpdate;
        static Callback<GameRichPresenceJoinRequested_t> GameRichPresenceJoinRequested;
        static Callback<GameConnectedClanChatMsg_t> GameConnectedClanChatMsg;
        static Callback<GameConnectedChatJoin_t> GameConnectedChatJoin;
        static Callback<GameConnectedChatLeave_t> GameConnectedChatLeave;
        static Callback<GameConnectedFriendChatMsg_t> GameConnectedFriendChatMsg;
        static Callback<UnreadChatMessagesChanged_t> UnreadChatMessagesChanged;
        static Callback<OverlayBrowserProtocolNavigation_t> OverlayBrowserProtocolNavigation;
        static Callback<EquippedProfileItemsChanged_t> EquippedProfileItemsChanged;
        static CallResult<ClanOfficerListResponse_t> OnClanOfficerListResponseCallResult;
        static CallResult<DownloadClanActivityCountsResult_t> OnDownloadClanActivityCountsResultCallResult;
        static CallResult<JoinClanChatRoomCompletionResult_t> OnJoinClanChatRoomCompletionResultCallResult;
        static CallResult<FriendsGetFollowerCount_t> OnFriendsGetFollowerCountCallResult;
        static CallResult<FriendsIsFollowing_t> OnFriendsIsFollowingCallResult;
        static CallResult<FriendsEnumerateFollowingList_t> OnFriendsEnumerateFollowingListCallResult;
        static CallResult<EquippedProfileItems_t> OnEquippedProfileItemsCallResult;

        static void OnPersonaStateChange(PersonaStateChange_t pCallback) {
            var changes = (EPersonaChange)pCallback.m_nChangeFlags;
            var steamID = (CSteamID)pCallback.m_ulSteamID;

            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                if (changes.HasFlag(EPersonaChange.k_EPersonaChangeName) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNickname) || changes.HasFlag(EPersonaChange.k_EPersonaChangeNameFirstSet)) {
                    KSteam.GetSteamUsername(steamID.m_SteamID, true);

                    if (tsteam.SteamIDToNetPlayerDict.TryGetValue(pCallback.m_ulSteamID, out ScavPlayer plr)) {
                        if (!plr.nameIsCustom) {
                            plr.ApplyNameAndColor(steamID.ToString(), plr.playerColor);
                        }
                    }
                }
                if (changes.HasFlag(EPersonaChange.k_EPersonaChangeRelationshipChanged)) {
                    LoadFriendList();
                }
            }
        }

        static void OnGameOverlayActivated(GameOverlayActivated_t pCallback) {
            steamtestlog("[" + GameOverlayActivated_t.k_iCallback + " - GameOverlayActivated] - " + pCallback.m_bActive + " -- " + pCallback.m_bUserInitiated + " -- " + pCallback.m_nAppID + " -- " + pCallback.m_dwOverlayPID);
            IS_IN_STEAMOVERLAY = pCallback.m_bActive != 0;
        }

        static void OnGameServerChangeRequested(GameServerChangeRequested_t pCallback) {
            steamtestlog("[" + GameServerChangeRequested_t.k_iCallback + " - GameServerChangeRequested] - " + pCallback.m_rgchServer + " -- " + pCallback.m_rgchPassword);
        }

        static void OnGameLobbyJoinRequested(GameLobbyJoinRequested_t pCallback) {
            steamtestlog("[" + GameLobbyJoinRequested_t.k_iCallback + " - GameLobbyJoinRequested] - " + pCallback.m_steamIDLobby + " -- " + pCallback.m_steamIDFriend);

            if (Multiplayer.IsNetworkActiveOrIsInGame()) {
                Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_joinrequest_in_game"));
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("steam_joinrequest"), pCallback.m_steamIDLobby));
                TransportSteamworks.OnWantToJoinLobby(pCallback.m_steamIDLobby.m_SteamID);
            }
        }

        static void OnAvatarImageLoaded(AvatarImageLoaded_t pCallback) {
            var steamID = (CSteamID)pCallback.m_steamID;

            steamtestlog("[" + AvatarImageLoaded_t.k_iCallback + " - AvatarImageLoaded] - " + pCallback.m_steamID + " -- " + pCallback.m_iImage + " -- " + pCallback.m_iWide + " -- " + pCallback.m_iTall);

            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                if (tsteam.SteamIDToNetPlayerDict.TryGetValue((ulong)steamID, out ScavPlayer plr)) {
                    LoadPlayerProfilePics(plr);
                }
            }
        }

        static void OnClanOfficerListResponse(ClanOfficerListResponse_t pCallback, bool bIOFailure) {
            steamtestlog("[" + ClanOfficerListResponse_t.k_iCallback + " - ClanOfficerListResponse] - " + pCallback.m_steamIDClan + " -- " + pCallback.m_cOfficers + " -- " + pCallback.m_bSuccess);
        }

        static void OnFriendRichPresenceUpdate(FriendRichPresenceUpdate_t pCallback) {
        }

        static void OnGameRichPresenceJoinRequested(GameRichPresenceJoinRequested_t pCallback) {
            steamtestlog("[" + GameRichPresenceJoinRequested_t.k_iCallback + " - GameRichPresenceJoinRequested] - " + pCallback.m_steamIDFriend + " -- " + pCallback.m_rgchConnect);
        }

        static void OnGameConnectedClanChatMsg(GameConnectedClanChatMsg_t pCallback) {
            steamtestlog("[" + GameConnectedClanChatMsg_t.k_iCallback + " - GameConnectedClanChatMsg] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser + " -- " + pCallback.m_iMessageID);

            int ret = SteamFriends.GetClanChatMessage(pCallback.m_steamIDClanChat, pCallback.m_iMessageID, out string Text, 2048, out EChatEntryType _, out CSteamID Chatter);
            steamtestlog(ret + " " + Chatter + ": " + Text);
        }

        static void OnGameConnectedChatJoin(GameConnectedChatJoin_t pCallback) {
            steamtestlog("[" + GameConnectedChatJoin_t.k_iCallback + " - GameConnectedChatJoin] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser);
        }

        static void OnGameConnectedChatLeave(GameConnectedChatLeave_t pCallback) {
            steamtestlog("[" + GameConnectedChatLeave_t.k_iCallback + " - GameConnectedChatLeave] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_steamIDUser + " -- " + pCallback.m_bKicked + " -- " + pCallback.m_bDropped);
        }

        static void OnDownloadClanActivityCountsResult(DownloadClanActivityCountsResult_t pCallback, bool bIOFailure) {
            steamtestlog("[" + DownloadClanActivityCountsResult_t.k_iCallback + " - DownloadClanActivityCountsResult] - " + pCallback.m_bSuccess);
        }

        static void OnJoinClanChatRoomCompletionResult(JoinClanChatRoomCompletionResult_t pCallback, bool bIOFailure) {
            steamtestlog("[" + JoinClanChatRoomCompletionResult_t.k_iCallback + " - JoinClanChatRoomCompletionResult] - " + pCallback.m_steamIDClanChat + " -- " + pCallback.m_eChatRoomEnterResponse);
        }

        static void OnGameConnectedFriendChatMsg(GameConnectedFriendChatMsg_t pCallback) {
            steamtestlog("[" + GameConnectedFriendChatMsg_t.k_iCallback + " - GameConnectedFriendChatMsg] - " + pCallback.m_steamIDUser + " -- " + pCallback.m_iMessageID);

            int ret = SteamFriends.GetFriendMessage(pCallback.m_steamIDUser, pCallback.m_iMessageID, out string Text, 2048, out EChatEntryType _);
            steamtestlog(ret + " " + pCallback.m_steamIDUser + ": " + Text);
        }

        static void OnFriendsGetFollowerCount(FriendsGetFollowerCount_t pCallback, bool bIOFailure) {
            steamtestlog("[" + FriendsGetFollowerCount_t.k_iCallback + " - FriendsGetFollowerCount] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_nCount);
        }

        static void OnFriendsIsFollowing(FriendsIsFollowing_t pCallback, bool bIOFailure) {
            steamtestlog("[" + FriendsIsFollowing_t.k_iCallback + " - FriendsIsFollowing] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_bIsFollowing);

            if (pCallback.m_bIsFollowing) {
                Following.Add(pCallback.m_steamID.m_SteamID);
            }
        }

        static void OnFriendsEnumerateFollowingList(FriendsEnumerateFollowingList_t pCallback, bool bIOFailure) {
            steamtestlog("[" + FriendsEnumerateFollowingList_t.k_iCallback + " - FriendsEnumerateFollowingList] - " + pCallback.m_eResult + " -- " + pCallback.m_rgSteamID + " -- " + pCallback.m_nResultsReturned + " -- " + pCallback.m_nTotalResultCount);
        }

        static void OnUnreadChatMessagesChanged(UnreadChatMessagesChanged_t pCallback) {
            steamtestlog("[" + UnreadChatMessagesChanged_t.k_iCallback + " - UnreadChatMessagesChanged]");
        }

        static void OnOverlayBrowserProtocolNavigation(OverlayBrowserProtocolNavigation_t pCallback) {
            steamtestlog("[" + OverlayBrowserProtocolNavigation_t.k_iCallback + " - OverlayBrowserProtocolNavigation] - " + pCallback.rgchURI);
        }

        static void OnEquippedProfileItemsChanged(EquippedProfileItemsChanged_t pCallback) {
            steamtestlog("[" + EquippedProfileItemsChanged_t.k_iCallback + " - EquippedProfileItemsChanged] - " + pCallback.m_steamID);
        }

        static void OnEquippedProfileItems(EquippedProfileItems_t pCallback, bool bIOFailure) {
            steamtestlog("[" + EquippedProfileItems_t.k_iCallback + " - EquippedProfileItems] - " + pCallback.m_eResult + " -- " + pCallback.m_steamID + " -- " + pCallback.m_bHasAnimatedAvatar + " -- " + pCallback.m_bHasAvatarFrame + " -- " + pCallback.m_bHasProfileModifier + " -- " + pCallback.m_bHasProfileBackground + " -- " + pCallback.m_bHasMiniProfileBackground);
        }

        // called only once after steamworks dll has loaded
        public static void Init() {
            if (!KSteam.Loaded) {
                return;
            }

            { // bind all callbacks
                FavoritesListChanged = Callback<FavoritesListChanged_t>.Create(OnFavoritesListChanged);
                LobbyInvite = Callback<LobbyInvite_t>.Create(OnLobbyInvite);
                LobbyEnter = Callback<LobbyEnter_t>.Create(OnLobbyEnter);
                LobbyDataUpdate = Callback<LobbyDataUpdate_t>.Create(OnLobbyDataUpdate);
                LobbyChatUpdate = Callback<LobbyChatUpdate_t>.Create(OnLobbyChatUpdate);
                LobbyChatMsg = Callback<LobbyChatMsg_t>.Create(OnLobbyChatMsg);
                LobbyGameCreated = Callback<LobbyGameCreated_t>.Create(OnLobbyGameCreated);
                LobbyKicked = Callback<LobbyKicked_t>.Create(OnLobbyKicked);
                FavoritesListAccountsUpdated = Callback<FavoritesListAccountsUpdated_t>.Create(OnFavoritesListAccountsUpdated);
                JoinPartyCallback = Callback<JoinPartyCallback_t>.Create(OnJoinPartyCallback);
                CreateBeaconCallback = Callback<CreateBeaconCallback_t>.Create(OnCreateBeaconCallback);
                ReservationNotificationCallback = Callback<ReservationNotificationCallback_t>.Create(OnReservationNotificationCallback);
                ChangeNumOpenSlotsCallback = Callback<ChangeNumOpenSlotsCallback_t>.Create(OnChangeNumOpenSlotsCallback);
                AvailableBeaconLocationsUpdated = Callback<AvailableBeaconLocationsUpdated_t>.Create(OnAvailableBeaconLocationsUpdated);
                ActiveBeaconsUpdated = Callback<ActiveBeaconsUpdated_t>.Create(OnActiveBeaconsUpdated);
                OnLobbyEnterCallResult = CallResult<LobbyEnter_t>.Create(OnLobbyEnter_APIDispatch);
                OnLobbyMatchListCallResult = CallResult<LobbyMatchList_t>.Create(OnLobbyMatchList);
                OnLobbyCreatedCallResult = CallResult<LobbyCreated_t>.Create(OnLobbyCreated);

                PersonaStateChange = Callback<PersonaStateChange_t>.Create(OnPersonaStateChange);
                GameOverlayActivated = Callback<GameOverlayActivated_t>.Create(OnGameOverlayActivated);
                GameServerChangeRequested = Callback<GameServerChangeRequested_t>.Create(OnGameServerChangeRequested);
                GameLobbyJoinRequested = Callback<GameLobbyJoinRequested_t>.Create(OnGameLobbyJoinRequested);
                AvatarImageLoaded = Callback<AvatarImageLoaded_t>.Create(OnAvatarImageLoaded);
                FriendRichPresenceUpdate = Callback<FriendRichPresenceUpdate_t>.Create(OnFriendRichPresenceUpdate);
                GameRichPresenceJoinRequested = Callback<GameRichPresenceJoinRequested_t>.Create(OnGameRichPresenceJoinRequested);
                GameConnectedClanChatMsg = Callback<GameConnectedClanChatMsg_t>.Create(OnGameConnectedClanChatMsg);
                GameConnectedChatJoin = Callback<GameConnectedChatJoin_t>.Create(OnGameConnectedChatJoin);
                GameConnectedChatLeave = Callback<GameConnectedChatLeave_t>.Create(OnGameConnectedChatLeave);
                GameConnectedFriendChatMsg = Callback<GameConnectedFriendChatMsg_t>.Create(OnGameConnectedFriendChatMsg);
                UnreadChatMessagesChanged = Callback<UnreadChatMessagesChanged_t>.Create(OnUnreadChatMessagesChanged);
                OverlayBrowserProtocolNavigation = Callback<OverlayBrowserProtocolNavigation_t>.Create(OnOverlayBrowserProtocolNavigation);
                EquippedProfileItemsChanged = Callback<EquippedProfileItemsChanged_t>.Create(OnEquippedProfileItemsChanged);
                OnClanOfficerListResponseCallResult = CallResult<ClanOfficerListResponse_t>.Create(OnClanOfficerListResponse);
                OnDownloadClanActivityCountsResultCallResult = CallResult<DownloadClanActivityCountsResult_t>.Create(OnDownloadClanActivityCountsResult);
                OnJoinClanChatRoomCompletionResultCallResult = CallResult<JoinClanChatRoomCompletionResult_t>.Create(OnJoinClanChatRoomCompletionResult);
                OnFriendsGetFollowerCountCallResult = CallResult<FriendsGetFollowerCount_t>.Create(OnFriendsGetFollowerCount);
                OnFriendsIsFollowingCallResult = CallResult<FriendsIsFollowing_t>.Create(OnFriendsIsFollowing);
                OnFriendsEnumerateFollowingListCallResult = CallResult<FriendsEnumerateFollowingList_t>.Create(OnFriendsEnumerateFollowingList);
                OnEquippedProfileItemsCallResult = CallResult<EquippedProfileItems_t>.Create(OnEquippedProfileItems);
            }

            SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
            if (pszCommandLine == null) {
                pszCommandLine = "";
            }
            steamtestlog("[SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024)] - " + pszCommandLine);

            LoadFriendList();
            UpdateRichPresence();
        }
    }
}
