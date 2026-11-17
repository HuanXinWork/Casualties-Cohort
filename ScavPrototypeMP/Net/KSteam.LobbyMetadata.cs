using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using MonoMod.Utils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using Unity.VisualScripting;

namespace Together {
    public partial class KSteam {

        public const string _OLDLOBBYDATAKEYPREFIX = "CASUALTIESUNKNOWN_KROKOSHA_MULTIPLAYER_";
        public const string _LOBBYDATAKEYPREFIX = "COOP_MOD_";
        public const string LOBBYDATAKEY_VERSION = _LOBBYDATAKEYPREFIX + "VERSION";
        public const string LOBBYDATAKEY_LOBBYNAME = _LOBBYDATAKEYPREFIX + "LOBBYNAME";
        public const string LOBBYDATAKEY_DESCRIPTION = _LOBBYDATAKEYPREFIX + "DESCRIPTION";
        public const string LOBBYDATAKEY_GAMEMODE = _LOBBYDATAKEYPREFIX + "GAMEMODE";
        public const string LOBBYDATAKEY_HASPASSWORD = _LOBBYDATAKEYPREFIX + "HASPASSWORD";
        public const string LOBBYDATAKEY_ISDEDICATED = _LOBBYDATAKEYPREFIX + "ISDEDICATED";
        public const string LOBBYDATAKEY_MIDJOIN_LOCKED = _LOBBYDATAKEYPREFIX + "LOCKED";
        public const string LOBBYDATAKEY_CURRENTLAYER = _LOBBYDATAKEYPREFIX + "CURRENTLAYER";
        public const string LOBBYDATAKEY_LIVINGCOUNT = _LOBBYDATAKEYPREFIX + "LIVINGCOUNT";
        public const string LOBBYDATAKEY_PLAYERCOUNT = _LOBBYDATAKEYPREFIX + "PLRCOUNT";
        public const string LOBBYDATAKEY_DEPTH = _LOBBYDATAKEYPREFIX + "DEPTH";
        public const string LOBBYDATAKEY_AVERAGEMOOD = _LOBBYDATAKEYPREFIX + "AVGMOOD";
        public const string LOBBYDATAKEY_EXTRADATA = _LOBBYDATAKEYPREFIX + "EXTRADATA";
        public const string LOBBYDATAKEY_RUNSETTINGS = _LOBBYDATAKEYPREFIX + "RUNSETTINGS";
        public const string LOBBYDATAKEY_MODS = _LOBBYDATAKEYPREFIX + "MODS";
        public const string LOBBYDATAKEY_LANGUAGE = _LOBBYDATAKEYPREFIX + "LANGUAGE";

        static string GetMyModListAsString() {
            return String.Join(":", Multiplayer.GetModGUIDs().Select(x => x.Replace(":", "")));
        }

        public static void Server_UpdateLobbyData() {
            Net.MyServerInfo.SetValues();
            CURRENT_LOBBY.locked = Net.MyServerInfo.haspassword;
            if (KSteam.NumToLobbyType(UIMainMenu._STEAM_CHOSEN_LOBBYTYPE) != ELobbyType.k_ELobbyTypePublic) {
                CURRENT_LOBBY.locked = true;
            }

            int bucket = 0;
            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                bucket = tsteam.hostLobbyBucket;
            }

            SteamMatchmaking.SetLobbyMemberLimit(LobbyId, Multiplayer.rules.PLAYER_COUNT_LIMIT);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LOBBYNAME, Net.MyServerInfo.name);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_DESCRIPTION, Net.MyServerInfo.description);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_GAMEMODE, Net.MyServerInfo.gamemode);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_CURRENTLAYER, Net.MyServerInfo.cur_layer.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_AVERAGEMOOD, Net.MyServerInfo.avg_happiness.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_DEPTH, Net.MyServerInfo.cur_depth.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LIVINGCOUNT, Net.MyServerInfo.plr_living_count.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_PLAYERCOUNT, Net.MyServerInfo.plr_count.ToString());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_HASPASSWORD, String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD) ? "0" : "1");
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_ISDEDICATED, Net.IsDedicatedServer ? "1" : "0");
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_MIDJOIN_LOCKED, (Net.MyServerInfo.midjoin_lock_active) ? "1" : "0");
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_RUNSETTINGS, WorldMP.CompileRunSettings());
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_LANGUAGE, Locale.currentLangName);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_MODS, GetMyModListAsString());

            SteamMatchmaking.SetLobbyData(LobbyId, "bucket", bucket.ToString());

            SteamServerInfoInLobbyExtraData aaa = new SteamServerInfoInLobbyExtraData(Net.MyServerInfo);
            NetDataWriter writer = new NetDataWriter();
            writer.PutUnmanaged(aaa);
            writer.PutArray(ScavPlayer.AllPlayers.Select(x => x.SteamId).ToArray());
            writer.Put(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST);

            writer.CompressWriter(0);
            NetDataReader reader = new NetDataReader(writer);
            SteamMatchmaking.SetLobbyData(LobbyId, LOBBYDATAKEY_EXTRADATA, Convert.ToBase64String(reader.GetRemainingBytes()));
        }

        static readonly string[] _requiredLobbyMetaKeys = {
            LOBBYDATAKEY_VERSION,
            LOBBYDATAKEY_LOBBYNAME,
            LOBBYDATAKEY_GAMEMODE,
            LOBBYDATAKEY_CURRENTLAYER,
            LOBBYDATAKEY_DEPTH,
            LOBBYDATAKEY_LIVINGCOUNT,
            LOBBYDATAKEY_PLAYERCOUNT,
            LOBBYDATAKEY_AVERAGEMOOD,
            LOBBYDATAKEY_EXTRADATA,
            LOBBYDATAKEY_HASPASSWORD,
            LOBBYDATAKEY_ISDEDICATED,
            LOBBYDATAKEY_MIDJOIN_LOCKED,
        };

        public static NetPublicServerInfo Client_LobbyBrowser_ReadLobbyIntoServerInfos(KSteam.Lobby slobby) {
            try {
                if (slobby?.metadata == null || slobby.metadata.Count == 0) {
                    _lastLobbyDropReason = "no-metadata";
                    return null;
                }

                List<string> missing = new List<string>();
                for (int i = 0; i < _requiredLobbyMetaKeys.Length; i++) {
                    if (!slobby.metadata.ContainsKey(_requiredLobbyMetaKeys[i])) {
                        missing.Add(_requiredLobbyMetaKeys[i]);
                    }
                }
                if (missing.Count > 0) {
                    string have = string.Join(",", slobby.metadata.Keys);
                    _lastLobbyDropReason = "missing-keys:" + string.Join(",", missing) + " have=[" + have + "]";
                    return null;
                }

                string VERSION = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_VERSION, null) ?? slobby.metadata[LOBBYDATAKEY_VERSION.ToLower()];
                string LOBBYNAME = slobby.metadata[LOBBYDATAKEY_LOBBYNAME];
                string GAMEMODE = slobby.metadata[LOBBYDATAKEY_GAMEMODE];
                string CURRENTLAYER = slobby.metadata[LOBBYDATAKEY_CURRENTLAYER];
                string DEPTH = slobby.metadata[LOBBYDATAKEY_DEPTH];
                string LIVINGCOUNT = slobby.metadata[LOBBYDATAKEY_LIVINGCOUNT];
                string PLRCOUNT = slobby.metadata[LOBBYDATAKEY_PLAYERCOUNT];
                string AVERAGEMOOD = slobby.metadata[LOBBYDATAKEY_AVERAGEMOOD];
                string EXTRADATA = slobby.metadata[LOBBYDATAKEY_EXTRADATA];
                string haspassword = slobby.metadata[LOBBYDATAKEY_HASPASSWORD];
                string ISDEDICATED = slobby.metadata[LOBBYDATAKEY_ISDEDICATED];
                string LOCKED = slobby.metadata[LOBBYDATAKEY_MIDJOIN_LOCKED];
                string MODS = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_MODS, "");
                string lang = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_LANGUAGE, "EN");
                string DESCRIPTION = slobby.metadata.GetValueOrDefault(LOBBYDATAKEY_DESCRIPTION, "");

                if (lang == null || Multiplayer.SanitizeTextInputAllowSpaces(lang) != lang || lang.Length > 10) {
                    lang = "";
                }

                if (DESCRIPTION.Length > Net.MAX_SERVER_DESC_LENGTH || DESCRIPTION.CountIndices('\n') > Net.MAX_SERVER_DESC_NEWLINES) {
                    _lastLobbyDropReason = "desc-too-long";
                    return null;
                }
                if (LOBBYNAME.Length > Net.MAX_SERVER_NAME_LENGTH) {
                    _lastLobbyDropReason = "name-too-long";
                    return null;
                }
                if (VERSION.Length > Net.MAX_SERVER_NAME_LENGTH) {
                    _lastLobbyDropReason = "version-too-long";
                    return null;
                }
                if (GAMEMODE.Length > 12) {
                    GAMEMODE = "";
                }
                if (lang.Length > 8) {
                    lang = "";
                }
                if (Multiplayer.SanitizeRichTextAllowSimpleTags(LOBBYNAME) != LOBBYNAME) {
                    LOBBYNAME = $"Expedition {slobby.lobby_steamID}";
                }
                if (Multiplayer.SanitizeRichTextAllowSimpleTags(DESCRIPTION) != DESCRIPTION) {
                    DESCRIPTION = "";
                }

                NetPublicServerInfo l = new NetPublicServerInfo();
                l.steam_lobby_info = slobby;

                l.name = LOBBYNAME;
                l.description = DESCRIPTION;
                l.version = VERSION;
                l.haspassword = haspassword != "0";
                l.dedicated = ISDEDICATED != "0";
                l.midjoin_lock_active = LOCKED != "0";
                l.gamemode = GAMEMODE;
                l.cur_layer = int.Parse(CURRENTLAYER);
                l.avg_happiness = int.Parse(AVERAGEMOOD);
                l.cur_depth = int.Parse(DEPTH);
                l.lang = lang;
                l.pingMs = -1;

                l.plr_living_count = int.Parse(LIVINGCOUNT);
                l.plr_max = slobby.memberlimit;
                l.plr_count = int.Parse(PLRCOUNT);

                if (l.cur_layer > 4 && (Util.GameIsDemo() || (Util.GameIsFull() && !(APP_IS_PLAYTEST || APP_IS_FULL))) && CURRENT_LOBBY.lobby_steamID != slobby.lobby_steamID) {
                    _lastLobbyDropReason = $"demo-layer-filter layer={l.cur_layer}";
                    return null;
                }

                // ascent + end lab stay out of the search browser (stale listings can still slip through)
                if (l.cur_layer >= UIServerBrowser.AscentLayerDepth && CURRENT_LOBBY.lobby_steamID != slobby.lobby_steamID) {
                    _lastLobbyDropReason = $"ascent-lab-layer-blacklist layer={l.cur_layer}";
                    return null;
                }

                try {
                    byte[] extradata = Convert.FromBase64String(EXTRADATA);
                    NetDataReader reader = new NetDataReader(extradata);
                    reader = reader.DecompressReader();

                    reader.GetUnmanaged(out SteamServerInfoInLobbyExtraData ssi);
                    ssi.Read(l);

                    {
                        ulong[] steamusers = reader.GetULongArray();
                        foreach (ulong steamid in steamusers) {
                            if (!slobby.members.ContainsKey((CSteamID)steamid)) {
                                slobby.members[(CSteamID)steamid] = new(steamid);
                            }
                        }
                    }

                    bool modsenforce = reader.GetBool();
                    {
                        l.modlist = MODS.Split(':');
                        if (modsenforce) {
                            string[] mymods = GetMyModListAsString().Split(':');
                            l.enforcemodlist_locked = !(new HashSet<string>(mymods).SetEquals(l.modlist));
                        }
                    }
                } catch (Exception eeeee) {
                    // extradata optional-ish - still show lobby
                    Together.log.warn($"[LobbyBrowser] extradata-parse-fail id={slobby.lobby_steamID} ver=\"{l.version}\": {eeeee.Message}");
                }

                return l;
            } catch (Exception e) {
                _lastLobbyDropReason = "exception:" + e.GetType().Name + " " + e.Message;
                Together.log.warn($"[LobbyBrowser] DROP-EXCEPTION id={slobby?.lobby_steamID}: {e}");
            }
            return null;
        }

        public static void UpdateRichPresence() {
            if (Net.IsRunning) {
#if TESTING
                SteamFriends.SetRichPresence("status", "Co-op mod: " + (Util.IsInMainMenu() ? "sigma 1..2..3.. " : WorldGeneration.world.biomeDepth.ToString()));
                SteamFriends.SetRichPresence("gamestatus", "fart shot dead in florida ");
                SteamFriends.SetRichPresence("score", "67");
                SteamFriends.SetRichPresence("steam_display", "STEAMDISPLAY TESST ");
#endif
                SteamFriends.SetRichPresence("steam_player_group", LobbyId.ToString());
                SteamFriends.SetRichPresence("steam_player_group_size", ScavPlayer.ClientIdToPlayerDict.Count.ToString());
            } else {
                SteamFriends.SetRichPresence("steam_player_group", null);
                SteamFriends.SetRichPresence("steam_player_group_size", null);
                SteamFriends.ClearRichPresence();
            }
        }

        public const int MAX_LOBBY_BUCKETS = 20;
        public static int LobbySearch_BucketCounter = 0;

        public static void SearchLobbies(bool do_buckets) {
            if (!Loaded) {
                return;
            }

            if (Net.CurrentServerInfo.cur_layer <= 4 && STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO) {
                SteamMatchmaking.AddRequestLobbyListNumericalFilter(LOBBYDATAKEY_CURRENTLAYER, 4, ELobbyComparison.k_ELobbyComparisonEqualToOrLessThan);
            }

            if (do_buckets) {
                SteamMatchmaking.AddRequestLobbyListStringFilter("bucket", LobbySearch_BucketCounter.ToString(), ELobbyComparison.k_ELobbyComparisonEqual);
            } else {
                LobbySearch_BucketCounter = 0;

                if (UIServerBrowser.SortHidePasswordProtected) {
                    SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBYDATAKEY_HASPASSWORD, "0", ELobbyComparison.k_ELobbyComparisonEqual);
                }
                if (UIServerBrowser.SortHideIncompatible) {
                    SteamMatchmaking.AddRequestLobbyListStringFilter(LOBBYDATAKEY_VERSION, Multiplayer.FULL_VERSION_TAG, ELobbyComparison.k_ELobbyComparisonEqual);
                }
            }

            SteamMatchmaking.AddRequestLobbyListDistanceFilter(userselected_lobbyDistanceFilter);
            SteamMatchmaking.AddRequestLobbyListResultCountFilter((int)userselected_maxLobbies);
            SteamAPICall_t handle = SteamMatchmaking.RequestLobbyList();
            OnLobbyMatchListCallResult.Set(handle);
        }

        public static void SearchFriendLobbies() {
            LoadFriendList();

            foreach (ulong friendId in Friends) {
                if (SteamFriends.GetFriendGamePlayed((CSteamID)friendId, out FriendGameInfo_t gameInfo)) {
                    if (gameInfo.m_gameID.AppID().m_AppId == STEAM_APPID) {
#if TESTING
                        log.LogInfo($"FOUND FRIEND {SteamFriends.GetFriendPersonaName((CSteamID)friendId)} , LOBBY: {gameInfo.m_steamIDLobby} ");
#endif
                        if (gameInfo.m_steamIDLobby.IsValid()) {
                            if (!UIServerBrowser.CurServerList_ButOnlySteamLobbies.ContainsKey(gameInfo.m_steamIDLobby.m_SteamID)) {
                                Lobby slobby = new Lobby();
                                UpdateLobbyInfo(gameInfo.m_steamIDLobby, ref slobby);
                                if (!TryAddLobbyToBrowser(slobby)) {
                                    RequestBrowserLobbyDataIfNeeded(slobby);
                                }
                            }
                        }
                    }
                }
            }
        }

        internal static ELobbyDistanceFilter userselected_lobbyDistanceFilter = ELobbyDistanceFilter.k_ELobbyDistanceFilterDefault;
        static ushort userselected_maxLobbies = 100;
    }
}
