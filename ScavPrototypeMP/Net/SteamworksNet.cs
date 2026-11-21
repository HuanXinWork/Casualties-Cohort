using BepInEx.Logging;
using CasualtiesTogetherUtils;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {

    // not static !! and is not ScavSingleton, cuz instance is created after CheckSteam runs succesfully
    public partial class KSteam : MonoBehaviour {
        protected static ManualLogSource log => Plugin.log;

        public static bool Loaded { get; private set; }

        public static KSteam singleton = null;

        public static bool IS_IN_LOBBY { get; private set; }
        public static bool IS_IN_STEAMOVERLAY { get; private set; }

        public static readonly string Filename_steam_appid_txt = "steam_appid.txt";

        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO = 4576510;
        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST = 4584420;
        public const uint CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL = 4576490;

        public static bool APP_IS_PIRATE { get; private set; }   // 480
        public static bool APP_IS_DEMO { get; private set; }     // 4576510
        public static bool APP_IS_PLAYTEST { get; private set; } // 4584420
        public static bool APP_IS_FULL { get; private set; }     // 4576490

        public static uint STEAM_APPID { get; private set; }

        public class LobbyMember {
            public CSteamID steamID;
            public double server_last_data_receive_time;
            public string disconnectreason = null;
            public Dictionary<string, string> metadata = new();
            public LobbyMember(ulong steamID) : this((CSteamID)steamID) {
            }
            public LobbyMember(CSteamID steamID) {
                this.steamID = steamID;
                server_last_data_receive_time = Time.realtimeSinceStartupAsDouble;
            }
        }

        public class Lobby {
            public bool locked;
            public CSteamID lobby_steamID;
            public CSteamID ownerID;
            public Dictionary<CSteamID, LobbyMember> members = new(); // THIS IS EMPTY IF USER IS NOT IN THE LOBBY
            public int memberlimit;
            // steam lobby keys are case-insensitive and often come back lowercased (coop_mod_version).
            public Dictionary<string, string> metadata = new(StringComparer.OrdinalIgnoreCase);

            public string DebugReadableDump() {
                if (this == null) {
                    return "NULL_LOBBY";
                }

                string s = $"===\nlobby_steamID: {lobby_steamID}\nownerID: {ownerID}\nlocked: {locked}\nmemberlimit: {memberlimit}\nmembers: {String.Join(", ", members.Keys)}\nmetadata:\n{String.Join("\n", metadata)}\n===";
                return s;
            }
        }

        public static List<Lobby> LOBBIES = new();
        public static Lobby CURRENT_LOBBY = new Lobby();

        // matchList can return lobby ids before metadata is local - RequestLobbyData fills them
        static readonly HashSet<ulong> _pendingBrowserLobbyData = new HashSet<ulong>();

        internal static Dictionary<ulong, NetPublicServerInfo> CurServerList_ButOnlySteamLobbies => UIServerBrowser.CurServerList_ButOnlySteamLobbies;

        public static void ClearPendingBrowserLobbyData() {
            _pendingBrowserLobbyData.Clear();
        }

        internal static void UpdateLobbyInfo(CSteamID steamIDLobby, ref Lobby outLobby) {
            outLobby.lobby_steamID = steamIDLobby;
            outLobby.ownerID = SteamMatchmaking.GetLobbyOwner(steamIDLobby);
            outLobby.memberlimit = SteamMatchmaking.GetLobbyMemberLimit(steamIDLobby);

            int nDataCount = SteamMatchmaking.GetLobbyDataCount(steamIDLobby);
            outLobby.metadata.Clear();
            for (int i = 0; i < nDataCount; ++i) {
                bool lobbyDataRet = SteamMatchmaking.GetLobbyDataByIndex(steamIDLobby, i, out var m_Key, Steamworks.Constants.k_nMaxLobbyKeyLength, out var m_Value, Steamworks.Constants.k_cubChatMetadataMax);

                if (m_Key.StartsWith(_OLDLOBBYDATAKEYPREFIX, StringComparison.Ordinal)) {
                    m_Key = m_Key.Substring(_OLDLOBBYDATAKEYPREFIX.Length);
                }

                outLobby.metadata[m_Key] = m_Value;
                if (!lobbyDataRet) {
                    Together.log.error("SteamMatchmaking.GetLobbyDataByIndex returned false.");
                    continue;
                }
            }

            // ONLY sync members for the lobby we're actually in.
            bool updatingCurrent = IS_IN_LOBBY && CURRENT_LOBBY.lobby_steamID == steamIDLobby;
            if (updatingCurrent || object.ReferenceEquals(outLobby, CURRENT_LOBBY)) {
                List<CSteamID> new_member_list = new List<CSteamID>();
                var membercount = SteamMatchmaking.GetNumLobbyMembers(steamIDLobby);
                for (int i = 0; i < membercount; ++i) {
                    var member = SteamMatchmaking.GetLobbyMemberByIndex(steamIDLobby, i);
                    new_member_list.Add(member);
                }

                List<CSteamID> newList = new_member_list;
                List<CSteamID> oldList = new List<CSteamID>(CURRENT_LOBBY.members.Select(x => (CSteamID)x.Key));

                IEnumerable<CSteamID> added = newList.Except(oldList);
                IEnumerable<CSteamID> removed = oldList.Except(newList);

                foreach (CSteamID sID in added) {
                    CURRENT_LOBBY.members.Add(sID, new LobbyMember(sID));
                }
                foreach (CSteamID sID in removed) {
                    CURRENT_LOBBY.members.Remove(sID);
                }
            }
        }

        static string _lastLobbyDropReason;

        static bool TryAddLobbyToBrowser(Lobby slobby) {
            _lastLobbyDropReason = null;
            NetPublicServerInfo l = Client_LobbyBrowser_ReadLobbyIntoServerInfos(slobby);
            ulong id = slobby != null ? slobby.lobby_steamID.m_SteamID : 0;
            int keyCount = slobby?.metadata != null ? slobby.metadata.Count : 0;
            if (l != null) {
                Together.log.warn($"[LobbyBrowser] ACCEPT id={id} name=\"{l.name}\" ver=\"{l.version}\" plr={l.plr_count}/{l.plr_max} joinable={l.IsJoinable()} keys={keyCount}");
                UIServerBrowser.AddServerToTheList(l);
                return true;
            }
            Together.log.warn($"[LobbyBrowser] DROP-PARSE id={id} reason={_lastLobbyDropReason ?? "unknown"} keys={keyCount}");
            return false;
        }

        static void RequestBrowserLobbyDataIfNeeded(Lobby slobby) {
            if (slobby == null) {
                return;
            }
            ulong id = slobby.lobby_steamID.m_SteamID;
            if (id == 0) {
                return;
            }
            if (!_pendingBrowserLobbyData.Add(id)) {
                Together.log.warn($"[LobbyBrowser] DATA-PENDING-ALREADY id={id}");
                return;
            }
            Together.log.warn($"[LobbyBrowser] REQUEST-DATA id={id} (metadata incomplete, waiting LobbyDataUpdate)");
            SteamMatchmaking.RequestLobbyData(slobby.lobby_steamID);
        }

        internal static void LeaveLobbyAndResetNet(CSteamID lobby_steamID) {
            IS_DOING_HostLobbyRecreatorLoop = false;
            CURRENT_LOBBY.lobby_steamID = lobby_steamID;
            LeaveLobbyAndResetNet(true);
        }

        internal static void LeaveLobbyAndResetNet(bool kill_transport = true) {
            if (Together.log.verbose) {
                Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID} - STACK: {new StackTrace()}");
            } else {
                Together.log.l($"STEAM: LeaveLobby called! lobbyID:{CURRENT_LOBBY.lobby_steamID}");
            }

            SteamMatchmaking.LeaveLobby((CSteamID)CURRENT_LOBBY.lobby_steamID);
            KSteam.IS_IN_LOBBY = false;
            CURRENT_LOBBY.lobby_steamID = CSteamID.Nil;

            if (kill_transport) {
                _ = Net.TryGetSteamTransport(out _);
                Net.ShutdownReset();
            }
        }

        public static ELobbyType NumToLobbyType(int num) {
            return num == 2 ? ELobbyType.k_ELobbyTypePublic : (num == 1 ? ELobbyType.k_ELobbyTypeFriendsOnly : ELobbyType.k_ELobbyTypePrivate);
        }
        public static int LobbyTypeToNum(ELobbyType num) {
            return num == ELobbyType.k_ELobbyTypePublic ? 2 : (num == ELobbyType.k_ELobbyTypeFriendsOnly ? 1 : 0);
        }

        public static bool IsSteamMaintenanceLikely() {
            return IsSteamMaintenanceLikely(DateTime.UtcNow);
        }

        public static bool IsSteamMaintenanceLikely(DateTime utcTime) {
            if (utcTime.Kind != DateTimeKind.Utc) {
                utcTime = DateTime.SpecifyKind(utcTime, DateTimeKind.Utc);
            }

            DayOfWeek day = utcTime.DayOfWeek;
            int hour = utcTime.Hour;

            if (day == DayOfWeek.Tuesday) {
                return hour >= 20;
            }

            if (day == DayOfWeek.Wednesday) {
                return hour < 2;
            }

            return false;
        }

        void Awake() {
            if (singleton != null) {
                Destroy(this);
                throw new Exception("kill yourself!");
            }
            singleton = this;
        }

        void Start() {
            Invoke(nameof(__CheckCommandLineForSteamArgs), 3);
            InvokeRepeating(nameof(Update10s), 5, 10);

            BindToEventsToCallSteamStuff();
        }

        void BindToEventsToCallSteamStuff() {
            Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {
                if (Util.IsInWorld()) {
                    SteamTimeline.SetTimelineTooltip(WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth], 0);
                } else {
                    SteamTimeline.ClearTimelineTooltip(0);
                }

                if (Net.TryGetSteamTransport(out _)) {
                    foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                        SteamFriends.SetPlayedWith((CSteamID)plr.SteamId);
                    }
                }
            };

            WorldMP.OnWorldgenFinish += () => {
                SteamTimeline.AddInstantaneousTimelineEvent(WorldGeneration.world.biomeTitles[WorldGeneration.world.biomeDepth], "", "steam_bookmark", 400);

                if (Net.IsRunningSteam && Net.IsServer) {
                    Server_UpdateLobbyData();
                }
            };

            WorldGeneration_CreateExplosion_MultiplayerPatch.OnExplosion += (eparam) => {
                if (KM.DistanceCheck(eparam.position, Camera.main.transform.position, 60)) {
                    SteamTimeline.AddInstantaneousTimelineEvent("Explosion", "", "steam_explosion", 10);
                }
            };

            NetBody.OnPlayerDeath += (plr) => {
                if (plr == null || plr.body == null) {
                    return;
                }

                if (Time.unscaledTimeAsDouble - plr.netBody.timeOfDeath > 1) {
                    SteamTimeline.AddInstantaneousTimelineEvent("Death", plr.playerName + " died.", "steam_death", 20);
                }
            };
        }

        static bool IS_DOING_HostLobbyRecreatorLoop = false;
        static IEnumerator HostLobbyRecreatorLoop() {
            double start = Time.realtimeSinceStartupAsDouble;
            while (IS_IN_LOBBY && Net.TryGetSteamTransport(out TransportSteamworks tsteam) && CURRENT_LOBBY.lobby_steamID == CSteamID.Nil) {
                yield return null;

                if ((Time.realtimeSinceStartupAsDouble - start) > 2) {
                    start = Time.realtimeSinceStartupAsDouble;

                    IS_DOING_HostLobbyRecreatorLoop = true;

                    tsteam.CreateLobby(KSteam.NumToLobbyType(UIMainMenu._STEAM_CHOSEN_LOBBYTYPE));

                    yield return new WaitForSeconds(1);
                }
            }
        }

        void Update10s() {
            if (Net.IsRunningSteam && Net.IsServer) {
                Server_UpdateLobbyData();
            }

            UpdateRichPresence();
        }

        public static string GetAppInstallDir() {
            _ = SteamApps.GetAppInstallDir(SteamUtils.GetAppID(), out string Folder, 260);
            if (Folder == null) {
                Folder = "";
            }
            return Folder;
        }

        private void __CheckCommandLineForSteamArgs() {
            if (!Loaded) {
                return;
            }

            string[] args = System.Environment.GetCommandLineArgs();

            {
                SteamApps.GetLaunchCommandLine(out var pszCommandLine, 1024);
                if (!string.IsNullOrEmpty(pszCommandLine)) {
                    args = pszCommandLine.Split(new char[] { ' ' }, options: StringSplitOptions.RemoveEmptyEntries);
                }
            }

            GetAppInstallDir();

            for (int i = 0; i < args.Length; i++) {
                string arg = args[i];

                try {
                    if (arg == ("+connect_lobby")) {
                        i++; if (i == args.Length) {
                            break;
                        }

                        arg = args[i];
                        if (ulong.TryParse(arg, out ulong newlobbysteamid)) {
                            if (!Net.IsRunning) {
                                KSteam.steamtestlog($"CMD: Will join to lobby {newlobbysteamid.ToString()}");
                                TransportSteamworks.OnWantToJoinLobby(newlobbysteamid);
                            }
                        }
                    }
                } catch (Exception e) {
                    Together.log.error($"STEAM: CMD: {e.ToString()}");
                }
            }
        }

        // this is called before creating this component, it checks if steamworks dll is present, if steam is working and everything is in order
        static void InitSteam() {
            try {
                string path = System.IO.Path.Combine(BepInEx.Paths.GameRootPath, Filename_steam_appid_txt);

                if (!File.Exists(path)) {
                    File.WriteAllText(path, (Util.GameIsFull() ?
                        CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL
                        : CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO
                        ).ToString());
                }
            } catch (Exception e) {
                ModLog.Debug($"Failed to get or create {Filename_steam_appid_txt}: {e.Message}", LogDomain.Transport);
            }

            if (Loaded || Plugin.FORCE_NO_STEAM) {
                return;
            }
            Loaded = false;

            if (!Packsize.Test()) {
                log.LogError($"Steamworks.NET: Packsize.Test() FAILED !!!");
                return;
            }
            if (!DllCheck.Test()) {
                log.LogError($"Steamworks.NET: DllCheck.Test() FAILED !!!");
                return;
            }

            try {
                var init_resultcode = SteamAPI.InitEx(out var outSteamErrMsg);
                switch (init_resultcode) {
                    case ESteamAPIInitResult.k_ESteamAPIInitResult_OK: {
                        break;
                    }
                    default: {
                        log.LogError($"Steamworks.NET: Init failed:\n\t{outSteamErrMsg}");
                        return;
                    }
                }
            } catch (Exception e) {
                Together.log.error($"Fatally failed to initialize Steam: {e}");
                return;
            }
            STEAM_APPID = SteamUtils.GetAppID().m_AppId;

            APP_IS_DEMO = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO;
            APP_IS_PLAYTEST = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST;
            APP_IS_FULL = STEAM_APPID == CONST_STEAMAPPID_CASUALTIESUNKNOWN_FULL;
            APP_IS_PIRATE = Util.GameIsFull() && !(APP_IS_PLAYTEST || APP_IS_FULL);

            Loaded = true;
            try {
                KSteam.Init();

                if (Plugin.s != null) {
                    Plugin.s.GetOrAddComponent<KSteam>();
#if FULLGAME
                } else if (SteamManager.instance != null) {
                    SteamManager.instance.GetOrAddComponent<KSteam>();
#endif
                } else {
                    GameObject.DontDestroyOnLoad(new GameObject("KSteam", typeof(KSteam)));
                }
            } catch (Exception e) {
                Multiplayer.DoMultiplayerStatusMessageError(string.Format(Lang.Get("steam_init_failed"), e.ToString()));
                Loaded = false;
                return;
            }

            if (Together.log.verbose) {
                Together.log.l($"Steamworks initialized: {KSteam.GetLocalUsername()} - {KSteam.GetLocalUserSteamID()}");
            }

            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("steam_initialized"));
        }

        public static bool CheckSteam() {
            try {
                InitSteam();
            } catch (Exception e) {
                log.LogError(e);
            }
            return Loaded;
        }

        public static HashSet<ulong> CoplayUsers = new();
        public static HashSet<ulong> Friends = new();
        public static HashSet<ulong> Following = new();

        static void LoadFriendList() {
            Friends.Clear();
            CoplayUsers.Clear();

            {
                int friendCount = SteamFriends.GetFriendCount(EFriendFlags.k_EFriendFlagImmediate);
                for (int i = 0; i < friendCount; i++) {
                    CSteamID friendId = SteamFriends.GetFriendByIndex(i, EFriendFlags.k_EFriendFlagImmediate);
                    Friends.Add(friendId.m_SteamID);
                }
            }

            {
                int coplayCount = SteamFriends.GetCoplayFriendCount();
                for (int i = 0; i < coplayCount; i++) {
                    CSteamID friendId = SteamFriends.GetCoplayFriend(i);
                    CoplayUsers.Add(friendId.m_SteamID);
                }
            }
        }

        private void Update() {
            if (Loaded) {
                using (FindTheBugInator.Time(FindTheBugInator.SteamCb)) {
                    SteamAPI.RunCallbacks();
                }
            }
        }

        protected void OnDestroy() {
            if (Loaded) {
                SteamAPI.Shutdown();
                Loaded = false;
            }
        }

        protected void OnEnable() {
            if (Loaded) {
                SteamClient.SetWarningMessageHook(new SteamAPIWarningMessageHook_t(KSteam.SteamAPIDebugTextHook));
            }
        }

        public static string GetLocalUsername() {
            return SteamFriends.GetPersonaName();
        }

        public static CSteamID GetLocalUserSteamID() {
            return SteamUser.GetSteamID();
        }

        [AOT.MonoPInvokeCallback(typeof(SteamAPIWarningMessageHook_t))]
        internal static void SteamAPIDebugTextHook(int nSeverity, System.Text.StringBuilder pchDebugText) {
            log.LogWarning(pchDebugText);
        }

        public static string GetSteamUsername(ulong steamuserid, bool force_reload_name = false) {
            if (steamuserid == 0) {
                return "[UNKNOWN]";
            }

            if (force_reload_name || _personaAsked.Add(steamuserid)) {
                SteamFriends.RequestUserInformation((CSteamID)steamuserid, true);
            }
            string name = SteamFriends.GetFriendPersonaName((CSteamID)steamuserid);
            if (string.IsNullOrEmpty(name) || name == "[unknown]") {
                return steamuserid.ToString();
            }
            return name;
        }

        // the name this machine should be listed under. steam persona, unless the player typed one.
        public static string SessionName() {
            if ((Plugin.plrname_override || Multiplayer.ExplicitSessionName) && !string.IsNullOrEmpty(UIMainMenu.USERINPUT_NAME)) {
                return UIMainMenu.USERINPUT_NAME;
            }
            if (Loaded) {
                string live = GetLocalUsername();
                if (!string.IsNullOrEmpty(live) && live != "[unknown]") {
                    string clean = Multiplayer.SanitizeTextInput(live);
                    if (clean.Length > 0) {
                        UIMainMenu.USERINPUT_NAME = clean;
                        return clean;
                    }
                }
            }
            return UIMainMenu.USERINPUT_NAME;
        }

        public static Texture2D SteamLoadImage(int imageId) {
            if (imageId == -1 || imageId == 0) {
                return null;
            }

            if (SteamImagesCache.TryGetValue(imageId, out Texture2D texture)) {
                return texture;
            }

            if (!SteamUtils.GetImageSize(imageId, out uint width, out uint height)) {
                return null;
            }

            byte[] imageData = new byte[width * height * 4];
            if (!SteamUtils.GetImageRGBA(imageId, imageData, (int)(width * height * 4))) {
                return null;
            }

            Color32[] pixels = new Color32[width * height];
            int rowSize = (int)width * 4;

            for (int y = 0; y < height; y++) {
                int srcRow = (int)(height - 1 - y) * rowSize;
                int dstRow = y * (int)width;

                for (int x = 0; x < width; x++) {
                    int i = srcRow + x * 4;
                    pixels[dstRow + x] = new Color32(
                        imageData[i],
                        imageData[i + 1],
                        imageData[i + 2],
                        imageData[i + 3]
                    );
                }
            }

            texture = new Texture2D((int)width, (int)height, TextureFormat.RGBA32, false, true);
            texture.SetPixels32(pixels);
            texture.Apply();

            SteamImagesCache[imageId] = texture;
            return texture;
        }

        public static void LoadPlayerProfilePics(ScavPlayer plr) {
            var steamid = (CSteamID)plr.SteamId;
            if (steamid.m_SteamID == 0) {
                return;
            }

            if (plr.IconLarge == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconLarge == null) {
                        plr.IconLarge = SteamLoadImage(SteamFriends.GetLargeFriendAvatar(steamid));
                        if (plr.IconLarge == null) {
                            return;
                        }

                        Sprite spr = plr.IconLargeSprite = Util.CreateSprite(plr.IconLarge);
                        if (plr.IconMediumSprite == null) {
                            plr.IconMediumSprite = spr;
                        }
                        if (plr.IconSmallSprite == null) {
                            plr.IconSmallSprite = spr;
                        }
                    }
                });
            }
            if (plr.IconMedium == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconMedium == null) {
                        plr.IconMedium = SteamLoadImage(SteamFriends.GetMediumFriendAvatar(steamid));
                        if (plr.IconMedium == null) {
                            return;
                        }

                        Sprite spr = plr.IconMediumSprite = Util.CreateSprite(plr.IconMedium);
                        if (plr.IconLargeSprite == null) {
                            plr.IconLargeSprite = spr;
                        }
                        if (plr.IconSmallSprite == null) {
                            plr.IconSmallSprite = spr;
                        }
                    }
                });
            }
            if (plr.IconSmall == null) {
                Util.DelayCallLambda(UnityEngine.Random.value, () => {
                    if (plr != null && plr.IconSmall == null) {
                        plr.IconSmall = SteamLoadImage(SteamFriends.GetSmallFriendAvatar(steamid));
                        if (plr.IconSmall == null) {
                            return;
                        }

                        Sprite spr = plr.IconSmallSprite = Util.CreateSprite(plr.IconSmall);
                        if (plr.IconLargeSprite == null) {
                            plr.IconLargeSprite = spr;
                        }
                        if (plr.IconMediumSprite == null) {
                            plr.IconMediumSprite = spr;
                        }
                    }
                });
            }
        }

        static Dictionary<int, Texture2D> SteamImagesCache = new();
        static readonly HashSet<ulong> _personaAsked = new HashSet<ulong>();

        public static CSteamID LobbyId => CURRENT_LOBBY.lobby_steamID;

        public static bool _DEV_ENABLE_STEAM_LOG => DebugMenuSettings._DEV_ENABLE_STEAM_LOG;
        internal static void steamtestlog(string what, bool forcelog = false) {
            if (!forcelog) {
                if (!_DEV_ENABLE_STEAM_LOG) {
                    return;
                }
            }
            try {
                what = "STEAM: " + what;
                Plugin.Logger.LogInfo(Together.log.do_timestamp(what));

                if (Con.con != null) {
                    Con.con.LogToConsole($"<color=#00ffffff>{Together.log.prefix1} {what}</color>");
                }
            } catch (Exception e) {
                Plugin.Logger.LogError("steamtestlog: " + e.ToString());
            }
        }
    }
}
