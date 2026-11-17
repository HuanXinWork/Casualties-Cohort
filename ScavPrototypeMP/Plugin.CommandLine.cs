using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Net;
using System.Reflection;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace Together {
    public partial class Plugin {
        public static List<string> commands_to_run = new();
        public static string test_scenario = null;
        public static bool dump_game_ids { get; private set; }
        public static bool start_network { get; private set; }
        public static bool do_immidiate_start { get; private set; }
        public static int startworld { get; private set; }
        public static bool plrname_override { get; private set; }
        public static bool rules_override { get; private set; }
        static string current_rule_to_set;

        public static bool cmd_start__steam_lobby = false;
        public static string cmd_start__steam_lobby_type = "public";
        public static bool cmd_start__is_client = false;
        public static bool cmd_start__is_dedicated_server = false;
        public static bool cmd_autobrowser = false;
        public static float cmd_autobrowser_delay = 5f;
        // after browser refresh, join first joinable lobby whose name contains this substring
        public static string cmd_autojoin_name = null;

        public static bool dowindowposthing { get; private set; }
#if !RELEASE
        public static bool starterkit { get; private set; }
#endif

        static void CheckCommandlineArgsForThisMod() {
#if TESTING
            log.LogInfo("Checking args...");
#endif

            startworld = 0;

            string[] args = System.Environment.GetCommandLineArgs();
            for (int i = 0; i < args.Length; i++) {
                if (args[i].TryTrimStart("--ksmulti-", out string t)) {
                    args[i] = "--mp-" + t;
                }
            }

            dump_game_ids = false;

            Func<string, string, bool> gggg = (string arg, string thing) => {
                return arg == "--" + thing
                    || arg == "-" + thing
                    || arg == thing;
            };

            for (int i = 0; i < args.Length; i++) {
                string arg = args[i];

                if (arg == ("--mp-disable") ||
                    gggg(arg, "nomp") ||
                    gggg(arg, "no-mp") ||
                    gggg(arg, "nomultiplayer") ||
                    gggg(arg, "no-multiplayer")
                ) {
                    FORCE_DISABLE_MP_MOD = true;
                    string msg = $"CMD: Force-Disable multiplayer";
                    log.LogInfo(msg);
                    try {
                        Util.CallLambdaWhen(() => { return Con.con != null; }, () => { Con.con.LogToConsole(msg); }, 1f);
                    } catch (Exception e) {
                        ModLog.Exception(LogDomain.Core, "CheckCommandlineArgs.ForceDisableLog", e);
                    }
                    return;
                }

                if (arg == ("--mp-starthost")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = false;
                }
                if (arg == ("--mp-startclient")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = true;
                }
                if (arg == ("--mp-startserver")) {
                    start_network = true;
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = true;

                    Multiplayer.rules.AutoContinue = true;
                }
                if (arg == ("--mp-sethost")) {
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = false;
                }
                if (arg == ("--mp-setclient")) {
                    Plugin.cmd_start__is_client = true;
                }
                if (arg == ("--mp-setserver")) {
                    Plugin.cmd_start__is_client = false;
                    Plugin.cmd_start__is_dedicated_server = true;

                    Multiplayer.rules.AutoContinue = true;
                }

                if (arg == ("--dump-game-ids")) {
                    dump_game_ids = true;
                }

                if (arg == ("--mp-startnetwork")) {
                    start_network = true;
                }
                if (arg == ("--mp-immidiate-start")) {
                    do_immidiate_start = true;
                }
                if (arg == ("--mp-setstarttutorial")) {
                    startworld = 1;
                }
                if (arg == ("--mp-setstartdebug")) {
                    startworld = 2;
                }
                if (arg == ("--mp-verbose")) {
                    DebugMenuSettings.is_verbose = true;
                    DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
                }
                if (arg == ("--mpdev-sort-windowpos")) {
                    dowindowposthing = true;
                }
#if !RELEASE
                if (arg == ("--kmultidev-starterkit")) {
                    starterkit = true;
                }
#endif
#if TESTING
                if (arg == ("--kmultidev-setwindownamefromusername")) {
                    win32windowtitlechanger.SetTitle(Application.productName + UIMainMenu.USERINPUT_NAME);
                }
#endif
                if (arg == ("--mp-ip-port")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];

                    UIMainMenu.USERINPUT_IPPORT = arg;
                }
                if (arg == ("--mp-setname")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];

                    UIMainMenu.USERINPUT_NAME = Multiplayer.SanitizeTextInput(arg);
                    plrname_override = true;
                    log.LogInfo($"CMD: set name to: {UIMainMenu.USERINPUT_NAME}");
                }
                if (arg == ("--mp-setpass")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];

                    AnyCmdArgsPassed = true;
                    Multiplayer.INPUT_PASSWORD = arg;
                    log.LogInfo($"CMD: set password to: {Multiplayer.INPUT_PASSWORD}");
                }
                if (arg == ("--mp-setrule")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];
                    current_rule_to_set = arg;
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];

                    rules_override = true;

                    AnyCmdArgsPassed = true;

#if !RELEASE
                    if (current_rule_to_set == "FriendlyFire") {
                        current_rule_to_set = "PVP";
                    }
#endif

                    FieldInfo field2 = typeof(MultiplayerGameRules).GetField(current_rule_to_set);
                    if (field2 == null) {
                        log.LogError($"CMD: UNKNOWN RULE: {current_rule_to_set} (attempt to set it to: {arg})");
                        continue;
                    }
                    object obj4;

                    try {
                        if (field2.FieldType == typeof(bool)) {
                            obj4 = Con.ParseBool01(arg);
                        } else {
                            obj4 = System.ComponentModel.TypeDescriptor.GetConverter(field2.FieldType).ConvertFromInvariantString(arg);
                        }
                    } catch (Exception e) {
                        log.LogError($"CMD: SET RULE, BUT VALUE IS INVALID:\nINPUT: {current_rule_to_set} = {arg}\n{e.Message}");
                        continue;
                    }

                    TypedReference rulesref = __makeref(Multiplayer.rules);
                    field2.SetValueDirect(rulesref, obj4);

                    log.LogInfo($"CMD: set rule: {current_rule_to_set} = {field2.GetValue(Multiplayer.rules)}");

                    continue;
                }
                if (arg == ("--mp-test")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];

                    test_scenario = arg;
                }
                if (arg == ("--mp-runcommand") || arg == "--ksm-con") {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];
                    AnyCmdArgsPassed = true;
                    commands_to_run.Add(arg);
                }
                if (arg == ("--mp-servername")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];
                    AnyCmdArgsPassed = true;
                    Net.MyServerInfo.name = arg;
                }
                if (arg == ("--mp-hoststeam")) {
                    cmd_start__steam_lobby = true;
                    start_network = true;

                    log.LogInfo($"CMD: Will start steam lobby");
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];
                    cmd_start__steam_lobby_type = arg;

                    log.LogInfo($"CMD: Will start steam lobby of type:{arg}");
                }
                if (arg == ("--mp-autobrowser")) {
                    cmd_autobrowser = true;
                    AnyCmdArgsPassed = true;
                    Application.runInBackground = true;
                    if (i + 1 < args.Length && float.TryParse(args[i + 1], System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out float delay)) {
                        i++;
                        cmd_autobrowser_delay = Mathf.Max(0f, delay);
                    }
                    log.LogInfo($"CMD: Will auto worldwide-refresh lobby browser after {cmd_autobrowser_delay}s");
                }
                if (arg == ("--mp-autojoin-name")) {
                    i++; if (i == args.Length) {
                        break;
                    }

                    arg = args[i];
                    cmd_autojoin_name = arg;
                    AnyCmdArgsPassed = true;
                    Plugin.cmd_start__is_client = true;
                    log.LogInfo($"CMD: Will autojoin lobby name containing: {cmd_autojoin_name}");
                }

                if (arg == "--mp-nosteam"
                    || gggg(arg, "nosteam")
                    || gggg(arg, "no-steam")
                ) {
                    FORCE_NO_STEAM = true;
                }
            }

            AnyCmdArgsPassed = AnyCmdArgsPassed || FORCE_NO_STEAM || start_network || startworld != 0 || do_immidiate_start || plrname_override || cmd_autobrowser;
            if (start_network) {
                Application.runInBackground = true;
                log.LogInfo($"CMD: Will start network.");
            }
            if (do_immidiate_start) {
                if (startworld != 0) {
                    log.LogInfo($"CMD: Will start immidiately in {startworld}.");
                } else {
                    log.LogInfo($"CMD: Will start immidiately.");
                }
            }
        }

        private IEnumerator DelayedDelayedTestGameStartImmidiate() {
            while (WorldGeneration.world == null && PreRunScript.instance == null) {
                yield return null;
            }

            ConsoleScript.CheckForConsole();
#if !RELEASE
            Con.RunCommand("musicvolume 0.011", true);
#endif

            log.LogInfo($"Found args... {start_network} {start_network} {Plugin.cmd_start__is_client} ");
#if !RELEASE
            if (dowindowposthing) {
                if (Plugin.cmd_start__is_client) {
                    Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(Screen.width, 0));
                }
            }
            if (dowindowposthing) {
                ScavPlayer.OnPlayerJoined += (plr) => {
                    if (Net.IsServer && Net.Transport is TransportLiteNetLib lnl) {
                        HashSet<ScavPlayer> plrs = new HashSet<ScavPlayer>();
                        {
                            List<NetPeer> peers = new List<NetPeer>();
                            lnl.NetManager.GetConnectedPeers(peers);
                            foreach (NetPeer peer in peers) {
                                try {
                                    if (peer.Tag != null && peer.Tag is TagPlayerInfo klpe && klpe.plr != null) {
                                        if (IPAddress.IsLoopback(peer.Address)) {
                                            plrs.Add(klpe.plr);
                                        }
                                    }
                                } catch (Exception e) {
                                    ModLog.Exception(LogDomain.Core, "SortWindowPos.PeerTag", e);
                                }
                            }
                        }
                        plrs.Remove(ScavPlayer.LocalPlayer);
                        Queue<ScavPlayer> queue = new Queue<ScavPlayer>(plrs);

                        bool fixt = false;
                        int localPlayersCount = 1 + plrs.Count();

                        if (localPlayersCount == 0) {
                            Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(0, 0));
                            Screen.SetResolution(1060, 600, false);
                            return;
                        }

                        int gridX = Mathf.Max(1, Mathf.CeilToInt(Mathf.Sqrt(localPlayersCount)));
                        int gridY = Mathf.Max(1, Mathf.CeilToInt((float)localPlayersCount / gridX));

                        int screenX = Mathf.Min(1060, Display.main.systemWidth / gridX);
                        int screenY = Mathf.Min(600, Display.main.systemHeight / gridY);

                        Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, new Vector2Int(0, 0));
                        Screen.SetResolution(screenX, screenY, false);

                        for (int y = 0; y < gridY; y++) {
                            for (int x = 0; x < gridX; x++) {
                                if (!fixt) {
                                    fixt = true;
                                    continue;
                                }
                                ScavPlayer plr2 = queue.Dequeue();

                                NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_SetWindowPos);
                                writer.Put(new Vector2Int(x * screenX, y * screenY));
                                writer.Put(new Vector2Int(screenX, screenY));
                                Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr2);
                            }
                        }
                    }
                };
            }
#endif

            if (cmd_autobrowser || !string.IsNullOrEmpty(cmd_autojoin_name)) {
                Application.runInBackground = true;
                SkipWarningScreen();
                SkipMainMenuIntro();
                Together.log.warn($"[LobbyBrowser] AUTO-BROWSER waiting {cmd_autobrowser_delay}s then worldwide refresh");
                yield return new WaitForSeconds(cmd_autobrowser_delay);
                while (!KSteam.Loaded) {
                    yield return null;
                }
                KSteam.userselected_lobbyDistanceFilter = ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide;
                UIServerBrowser.SortHideIncompatible = false;
                UIServerBrowser.ReloadList();
                Together.log.warn($"[LobbyBrowser] AUTO-BROWSER refresh fired myVer=\"{Multiplayer.FULL_VERSION_TAG}\"");
                // give MatchList + LobbyDataUpdate a few seconds to land
                yield return new WaitForSeconds(8f);
                Together.log.warn($"[LobbyBrowser] AUTO-BROWSER done ui={UIServerBrowser.CurServerCount}/{UIServerBrowser.CurFilteredServerList.Count}");

                if (!string.IsNullOrEmpty(cmd_autojoin_name) && !Net.IsRunning) {
                    string needle = cmd_autojoin_name;
                    NetPublicServerInfo match = null;
                    foreach (NetPublicServerInfo lobby in UIServerBrowser.CurFilteredServerList) {
                        if (lobby == null || string.IsNullOrEmpty(lobby.name)) {
                            continue;
                        }
                        if (lobby.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) < 0) {
                            continue;
                        }
                        if (!lobby.IsJoinable()) {
                            Together.log.warn($"[LobbyBrowser] AUTO-JOIN skip non-joinable name=\"{lobby.name}\"");
                            continue;
                        }
                        match = lobby;
                        break;
                    }
                    if (match == null) {
                        // one more refresh pass
                        UIServerBrowser.ReloadList();
                        yield return new WaitForSeconds(8f);
                        foreach (NetPublicServerInfo lobby in UIServerBrowser.CurFilteredServerList) {
                            if (lobby != null && !string.IsNullOrEmpty(lobby.name)
                                && lobby.name.IndexOf(needle, StringComparison.OrdinalIgnoreCase) >= 0
                                && lobby.IsJoinable()) {
                                match = lobby;
                                break;
                            }
                        }
                    }
                    if (match != null && match.steam_lobby_info != null) {
                        ulong id = match.steam_lobby_info.lobby_steamID.m_SteamID;
                        Together.log.warn($"[LobbyBrowser] AUTO-JOIN name=\"{match.name}\" id={id}");
                        TransportSteamworks.OnWantToJoinLobby(id);
                        float joinWait = 0f;
                        while (!Net.IsRunning && joinWait < 60f) {
                            joinWait += 0.25f;
                            yield return new WaitForSeconds(0.25f);
                        }
                        Together.log.warn($"[LobbyBrowser] AUTO-JOIN done running={Net.IsRunning} after {joinWait:F1}s");
                    } else {
                        Together.log.error($"[LobbyBrowser] AUTO-JOIN failed — no joinable lobby containing \"{needle}\"");
                    }
                }
            }

            if (start_network) {
                Application.runInBackground = true;
                if (Plugin.cmd_start__steam_lobby) {
                    cmd_start__steam_lobby_type = cmd_start__steam_lobby_type.ToLower();
                    TransportSteamworks.OnWantToHostLobby(
                        Plugin.cmd_start__is_dedicated_server ? Net.NetType.DedicatedServer : Net.NetType.Host,
                        cmd_start__steam_lobby_type == "private" ? ELobbyType.k_ELobbyTypePrivate : (
                            cmd_start__steam_lobby_type == "friendsonly" ? ELobbyType.k_ELobbyTypeFriendsOnly :
                            ELobbyType.k_ELobbyTypePublic
                        )
                    );
                } else if (Plugin.cmd_start__is_client) {
                    while (!Net.IsRunning && Util.IsInMainMenu()) {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Client);
                        yield return new WaitForSeconds(0.1f);
                        while (Net.IsConnecting) {
                            yield return null;
                        }
                        if (Multiplayer.IsNetworkActiveAndIsServer() || Util.IsInWorld()) {// it's been cancelled
                            Together.log.l("Cancelled forced client connect.");
                            break;
                        }
                    }
                } else {
                    if (Plugin.cmd_start__is_dedicated_server) {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.DedicatedServer);
                    } else {
                        TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Host);
                    }
                    yield return new WaitForSeconds(1.0f);
                }
            }

            ConsoleScript.CheckForConsole();
            foreach (string item in commands_to_run) {
                try {
                    Together.log.l($"CMD: Running command \"{item}\"");
                    ConsoleScript.instance.RunCommandString(item);
                } catch (Exception e) {
                    Together.log.error($"CMD: Error while running command \"{item}\"\n{e}");
                }
            }

            if (do_immidiate_start) {
                SkipWarningScreen();

                if (do_immidiate_start && !(start_network && Plugin.cmd_start__is_client)) {
                    PreRunScript prs = PreRunScript.instance;
                    if (startworld != 0) {
                        if (startworld == 1) {
                            prs.StartTutorial();
                        } else if (startworld == 2) {
                            WorldMP.LoadDebugWorld();
                        } else {
                            SceneManager.LoadScene("SampleScene");

                            Util.CallLambdaWhen(() => { return WorldGeneration.world != null; }, () => {
                                WorldGeneration.world.biomeOverride = startworld == 1 ? WorldGeneration.OverrideSceneType.Tutorial : (startworld == 0 ? WorldGeneration.OverrideSceneType.None : WorldGeneration.OverrideSceneType.Debug);
                            });

                            if (Net.IsServer) {
                                ServerMain.Server_Announce_GAME_START();
                            }
                        }
                    } else {
                        prs.StartRun();
                    }

#if !RELEASE
                    if (starterkit) {
                        while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
                        yield return new WaitForSeconds(1.0f);
                        yield return Con.SpawnDevStarterTestKit();
                    }
#endif
                }
            }

#if TESTING
            while (!Util.IsInWorld() || WorldGeneration.world.generatingWorld) { yield return new WaitForSeconds(0.1f); }
            PlayerCamera.main.body.forceWalk = false; // fuck tutoril world
#endif

            if (test_scenario != null) {
                yield return DEV_RunTestScenario();
            }
        }

        [ClientReceiver((byte)NetmsgId.CLIENT_SetWindowPos)]
        static void ClientReceiver__SetWindowPos(knetid _, LiteNetLib.Utils.NetDataReader reader) {
            if (Net.Transport != null && Net.Transport is TransportLiteNetLib lnl && IPAddress.IsLoopback(IPAddress.Parse(lnl.ip)) && Together.log.verbose) {
            } else {
                return;
            }

            reader.Get(out Vector2Int pos);
            reader.Get(out Vector2Int size);

            Screen.MoveMainWindowTo(Screen.mainWindowDisplayInfo, pos);
            Screen.SetResolution(size.x, size.y, false);
        }
    }
}
