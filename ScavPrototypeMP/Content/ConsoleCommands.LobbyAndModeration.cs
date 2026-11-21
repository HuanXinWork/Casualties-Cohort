using CasualtiesTogetherUtils;
using LiteNetLib;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Reflection;
using System.Text;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class Con {
    /// <summary>
    /// 注册房间连接、Steam 大厅、投票管理、踢出/封禁、禁言、规则与管理员权限相关命令。
    /// </summary>
    private static void RegisterLobbyAndModerationCommands(ConsoleScript con) {
        RegisterCommand_LocalOnly(new Command("vote", "MP- Call a vote.", (string[] splited) => {
            string voteCmd = string.Join(" ", splited);
            var writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(voteCmd);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }, null, ("type", "kick/mutevc/mutetc"), ("player", "")));

        RegisterCommand(new Command("createvote", "MP-SERVER- Call a custom vote.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 5);

            int argcount = 1;
            string toRun = splited[argcount++];
            float timeout = con.ParseFloat(splited[argcount++]);
            float percent = Mathf.Clamp(con.ParseFloat(splited[argcount++]), -100f, 100f) / 100f;

            string title = Lang.MarkMsgAsLocaleKey("vote_default_title");
            string text = Lang.MarkMsgAsLocaleKey("vote_default_text");
            string tt = splited.ElementAtOrDefault(argcount++);
            string te = splited.ElementAtOrDefault(argcount++);
            if (tt != null) {
                title = tt.Replace('_', ' ');
            }
            if (te != null) {
                text = te.Replace('_', ' ');
            }

            VoteSystem.Server_AnnounceVote(title, text, timeout, (HashSet<ScavPlayer> voted_yes, HashSet<ScavPlayer> voted_no, HashSet<ScavPlayer> voted_ignore) => {
                float total = voted_yes.Count + voted_no.Count + voted_ignore.Count;
                if (total > 0f) {
                    float perc = percent >= 0f ? (voted_yes.Count / total) : (voted_no.Count / total);
                    Chat.Server_ChatAnnouncement($"<key:vote_result> {Mathf.RoundToInt(perc * 100f)}%/{Mathf.RoundToInt(percent * 100f)}%");
                    if (perc >= Mathf.Abs(percent)) {
                        con.RunCommandString(toRun);
                    }
                }
            });
        }, null,
            ("string command", "Command to run if vote is accepted."),
            ("float time", "Time for vote. (Default: 10)"),
            ("float percent", "Percentage of votes required. (Default: 50)"),
            ("string", "Title"),
            ("string", "Text")));

        RegisterCommand(new Command("kick", "MP-SERVER- Forcefully disconnect player.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 1);

            string reason = splited.Length > 2
                ? Lang.FormatMsg("kick_reason_custom", string.Join(" ", splited.Skip(2)))
                : Lang.MarkMsgAsLocaleKey("kick_reason_default");
            string plrname = splited[1];
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    Plugin.log.LogInfo($"Server entered command to kick: {plr}");
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);

            if (res.Item1) {
                log.l($"Kicked: {plrname} - {Lang.ParseTaggedString(reason)}");
            } else {
                log.l($"Failed to kick: {plrname} {res.Item2}");
            }
        }, null, ("player", ""), ("string reason", "optional")));

        RegisterCommand(new Command("ban", "MP-SERVER- Ban player.", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 1);
            string plrname = splited[1];
            string reason = splited.Length > 2
                ? Lang.FormatMsg("ban_reason_custom", string.Join(" ", splited.Skip(2)))
                : Lang.MarkMsgAsLocaleKey("ban_reason_default");

            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    Plugin.log.LogInfo($"Server entered command to ban: {plr}");
                    BanList.Add("NULL", plr.playerName, plr.SteamId);
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);

            if (res.Item1) {
                log.l($"Banned: {plrname} - {Lang.ParseTaggedString(reason)}");
            } else if (ulong.TryParse(plrname, out ulong parsed_as_steamid) && parsed_as_steamid != 0 && plrname.StartsWith("7656119")) {
                BanList.Add("NULL", "", parsed_as_steamid);
                log.l($"Banned Steam ID: {parsed_as_steamid} ");
            } else {
                log.l($"Failed to Ban: {plrname} {res.Item2}");
            }
        }, null, ("player", ""), ("string reason", "optional")));

        RegisterCommand(new Command("ipban", "MP-SERVER- IP Ban player. (Non-steam only)", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 1);
            string plrname = splited[1];

            if (!(Net.Transport is TransportLiteNetLib lnl)) {
                throw new Exception("Only available for direct IP connection mode.");
            }

            string reason = splited.Length > 2
                ? Lang.FormatMsg("ban_reason_custom", string.Join(" ", splited.Skip(2)))
                : Lang.MarkMsgAsLocaleKey("ban_reason_default");
            var res = PerformActionOnPlayersByName(plrname, (ScavPlayer plr) => {
                if (!plr.IsLocal) {
                    NetPeer peer = lnl.ClientIdToPeerDict[plr.playerId];
                    if (peer == null) {
                        throw new Exception($"{plr} does not have net peer object ????");
                    }
                    Plugin.log.LogInfo($"Server entered command to IP ban: {plr}");
                    BanList.Add(peer.Address.ToString(), plr.playerName, plr.SteamId);
                    plr.Server_Kick(reason);
                    if (plrname != "@a") {
                        plrname = plr.playerName;
                    }
                }
            }, false, GetCommandCallerPlayer(), false, false);

            if (res.Item1) {
                log.l($"Banned: {plrname} - {Lang.ParseTaggedString(reason)}");
            } else if (Net.TryParseIPPORT(plrname, out string realip, out _)) {
                BanList.Add(realip, "", 0);
                log.l($"Banned IP: {realip} ");
            } else {
                log.l($"Failed to Ban: {plrname} {res.Item2}");
            }
        }, null, ("player", ""), ("string reason", "optional")));

        RegisterCommand(new Command("banlist", "MP- Show all players you've banned. Local.", (string[] splited) => {
            log.l($"BANLIST: {BanList.Instance.filePath}\n{string.Join("\n", BanList.Entries)} ");
        }, null));

        RegisterCommand(new Command("unban", "MP- Remove a player from your ban list.", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);
            string plrname = splited[1];

            BanList.BanEntry found = null;
            if (ulong.TryParse(plrname, out ulong parsed_as_steamid) && parsed_as_steamid != 0) {
                found = BanList.Entries.FirstOrDefault(x => x.steamid == parsed_as_steamid);
            }
            found ??= BanList.Entries.FirstOrDefault(x => x.ip == plrname);
            found ??= BanList.Entries.FirstOrDefault(x => x.name == plrname);

            if (found != null) {
                BanList.Remove(found);
                log.l("Unbanned: " + found);
            } else {
                log.l($"\"{plrname}\" was not found in your ban list.");
            }
        }, null, ("user", "SteamID or IP or Name")));

        RegisterCommand(new Command("say", "MP - say something in chat", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            string msg = string.Join(" ", splited.Skip(1));
            Chat.SendChatMessage(msg, true);
            ConsoleScript.instance.LogToConsole("Chat message sent: " + msg);
        }, null, ("string", "message")));

        RegisterCommand_BannedAdmin(new Command("adminpriv-password", "MP-SERVER- set admin password (ANYONE CAN USE THIS PASSWORD)", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);
            server_admin_password = splited[1];
            log.l("Set server admin password.");
        }, null, ("password", "choose strong password")));

        RegisterCommand_BannedAdmin(new Command("adminpriv", "MP-SERVER- give or remove admin privileges", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 2);

            string action = splited[1].ToLowerInvariant();
            string plridentifier = splited[2];

            if (TryGetPlayerFromPartialName(plridentifier, out ScavPlayer plr, null)) {
                if (plr.IsLocal) {
                    log.l("Local player already has this privilege.");
                    return;
                }
                if (action == "add") {
                    plr.ServerPlayerState.admin_privilege = true;
                    removed_server_admins.Remove(plr.GetPersistentId());
                    log.l($"Gave admin privilege to: {plr}");
                } else if (action == "remove") {
                    plr.ServerPlayerState.admin_privilege = false;
                    server_admins.Remove(plr);
                    removed_server_admins.Add(plr.GetPersistentId());
                    log.l($"Removed admin privilege from: {plr}");

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_DenyAdminMode);
                    writer.Put(false);
                    Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
                } else {
                    con.LogToConsole($"{action} -> it should be \"add\" or  \"remove\"");
                }
            } else {
                con.LogToConsole($"{plridentifier} -> player does not exist");
            }
        }, new Dictionary<int, List<string>> { { 0, new List<string> { "add", "remove" } } },
            ("action", ""),
            ("player", "")));

        RegisterCommand_LocalOnly(new Command("admin", "MP-CLIENT- toggle admin mode, run commands on the server and see its log", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsServer();

            string pass = splited.Length > 1 ? splited[1] : "";
            client_adminmode = !client_adminmode;
            client_isadmin = client_isadmin || client_adminmode;

            log.l($"Requesting admin console: {client_adminmode}");

            var writer = Net.CreateWriter(NetmsgId.SERVER_Console_ActivateAdminMode);
            writer.Put(client_adminmode);
            writer.Put(pass);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }, null, ("password", "optional (u dont need password if ur whitelisted)")));

        RegisterCommand(new Command("setplrname", "MP-SERVER- force rename a player", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            ConFailIfNetworkNotRunning();
            con.CheckArgumentCount(splited, 2);

            string plridentifier = splited[1];
            string newname = splited[2];
            try {
                var res = PerformActionOnPlayersByName(plridentifier, (ScavPlayer plr) => {
                    con.LogToConsole($"Renamed {plr} to {newname}");
                    plr.nameIsCustom = true;
                    plr.ApplyNameAndColor(newname, plr.playerColor);
                }, true, GetCommandCallerPlayer(), false, false);

                if (!res.Item1) {
                    con.LogToConsole("Rename error: " + res.Item2);
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Lobby, "setplrname", e);
            }
        }, null, ("player", ""), ("string", "new name")));

        RegisterCommand(new Command("setplrcolor", "MP-SERVER- force recolor a player", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 2);

            string plridentifier = splited[1];
            Color newcolor = TryGetPlayerFromPartialName(splited[2], out ScavPlayer colorPlr, null)
                ? colorPlr.playerColor
                : con.ParseColor(splited[2]);

            if (Net.IsRunning) {
                try {
                    var res = PerformActionOnPlayersByName(plridentifier, (ScavPlayer plr) => {
                        con.LogToConsole($"Recolored {plr} to {newcolor}");
                        plr.ApplyNameAndColor(plr.playerName, newcolor);
                    }, true, GetCommandCallerPlayer(), false, false);

                    if (!res.Item1) {
                        con.LogToConsole("Recolor error: " + res.Item2);
                    }
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Lobby, "setplrcolor", e);
                }
            } else {
                UIMainMenu.SetInputColor(newcolor);
                con.LogToConsole($"Set local color to {UIMainMenu.LAST_VALID_INPUT_COLOR}");
            }
        }, null, ("player", ""), ("color", "new color")));

        RegisterCommand(new Command("servermute", "MP-SERVER- mute player chat or voice", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 2);

            bool istc = false;
            bool isvc = false;
            string type = splited[1].ToLowerInvariant();
            string plrname = splited[2];
            bool has_target = false;
            bool target = true;
            if (splited.Length > 3) {
                has_target = true;
                target = con.ParseBool(splited[3]);
            }
            if (type == "tc") {
                istc = true;
            } else if (type == "vc") {
                isvc = true;
            } else if (type == "all") {
                isvc = true;
                istc = true;
                has_target = true;
            } else {
                throw new Exception($"Unknown chat type: {type}");
            }

            var res = PerformActionOnPlayersByName(plrname, (plr) => {
                if (isvc) {
                    plr.server_mute_vc = has_target ? target : !plr.server_mute_vc;
                    log.l($"{plr}.mute_vc = {plr.server_mute_vc}");
                }
                if (istc) {
                    plr.server_mute_tc = has_target ? target : !plr.server_mute_tc;
                    log.l($"{plr}.mute_tc = {plr.server_mute_tc}");
                }
            }, true, GetCommandCallerPlayer(), false, false);

            if (!res.Item1) {
                con.LogToConsole("servermute fail: " + res.Item2);
            }
        }, null, ("type", "tc/vc/all"), ("player", ""), ("bool", "optional")));

        RegisterCommand(new Command("serverpause", "MP - Pause time.", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            con.CheckForWorld();
            ConFailIfNetworkIsRunningAndIsClient();

            bool doPause = splited.Length > 1 ? con.ParseBool(splited[1]) : (Time.timeScale != 0f);
            doPause = !doPause;

            PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Paused);
            Time.timeScale = doPause.ToByte();
        }, null, ("bool", "1 pause, 0 continue")));

        RegisterCommand_LocalOnly(new Command("maxplayers", "MP - SERVER - Set player limit", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 1);
            Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp(con.ParseInt(splited[1]), 0, 251);
            con.LogToConsole($"Set player count limit to: {Multiplayer.PLAYER_COUNT_LIMIT}");
        }, null, ("int", "")));

        RegisterCommand(new Command("tomainmenu", "MP - go to main menu", (string[] splited) => {
            con.CheckForWorld();
            PlayerCamera.main.ToMainMenu();
        }, null));

        RegisterCommand_LocalOnly(new Command("currentrules", "MP - show all multiplayer game rules", (string[] splited) => {
            var sb = new StringBuilder("Krokosha Co-op mod game rules:\n");
            foreach (FieldInfo fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                sb.AppendLine($"rule {fld.Name} [{fld.FieldType.Name}] // current: {fld.GetValue(Multiplayer.rules)}");
            }
            con.LogToConsole(sb.ToString());
        }, null));

        RegisterCommand(new Command("resetrules", "MP - reset all multiplayer game rules to default values", (string[] splited) => {
            Multiplayer.rules = new MultiplayerGameRules();
            con.LogToConsole("Reset all rules to default.");
        }, null));

        RegisterCommand(new Command("rule", "MP - set multiplayer game rule", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            con.CheckArgumentCount(splited, 2);

            string rulename = splited[1];
            string rulevalstr = splited[2];
            foreach (FieldInfo fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                if (fld.Name == rulename) {
                    try {
                        object typedValue = fld.FieldType == typeof(bool)
                            ? ParseBool01(rulevalstr)
                            : Convert.ChangeType(rulevalstr, fld.FieldType, CultureInfo.InvariantCulture);
                        TypedReference rulesref = __makeref(Multiplayer.rules);
                        fld.SetValueDirect(rulesref, typedValue);
                        Multiplayer.ApplyGameRules();
                        con.LogToConsole($"Succesfully set rule '{fld.Name}' to '{fld.GetValue(Multiplayer.rules)}'.");
                        return;
                    } catch (Exception ex) {
                        con.LogToConsole($"Failed to set rule '{fld.Name}': {ex.Message}");
                        return;
                    }
                }
            }
            con.LogToConsole($"Rule '{rulename}' does not exist!");
        }, new Dictionary<int, List<string>> { { 0, typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance).Select(x => x.Name).ToList() } },
            ("string rule", "Rule Name"),
            ("value", "New value")));

        RegisterCommand(new Command("setlocalname", "MP - Set your multiplayer username.", delegate (string[] args) {
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            if (PlayerCamera.main) {
                throw new Exception("Name can only be set in main menu.");
            }
            ConFailIfNetworkAlreadyRunning();
            string name = Multiplayer.SanitizeTextInput(args[1]);
            con.LogToConsole("Set your username to: " + name);
            UIMainMenu.USERINPUT_NAME = name;
            Multiplayer.ExplicitSessionName = true;
        }, null, ("string name", "Your new name")));

        RegisterCommand(new Command("setlocalpassword", "MP - Set your multiplayer password.", delegate (string[] args) {
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            if (PlayerCamera.main) {
                throw new Exception("Password can only be set in main menu.");
            }
            ConFailIfNetworkAlreadyRunning();
            string newpass = args[1];
            con.LogToConsole("Set your password to: " + newpass);
            Multiplayer.INPUT_PASSWORD = newpass;
        }, null, ("string password", "")));

        RegisterCommand(new Command("setservername", "MP - Set your server name.", delegate (string[] args) {
            ConFailIfNetworkIsRunningAndIsClient();
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            string name = string.Join(" ", args.Skip(1));
            Multiplayer.SetServerName(name);
            con.LogToConsole("Set server name to: " + Net.CurrentServerInfo.name);
        }, null, ("string name", "New name")));

        RegisterCommand(new Command("setserverdesc", "MP - Set your server description.", delegate (string[] args) {
            ConFailIfNetworkIsRunningAndIsClient();
            ConsoleScript.instance.CheckArgumentCount(args, 1);
            string desc = string.Join(" ", args.Skip(1));
            Multiplayer.SetServerDescription(desc);
            con.LogToConsole("Set server desc to: " + Net.CurrentServerInfo.description);
        }, null, ("string name", "New description")));

        RegisterCommand_LocalOnly(new Command("joinlobby", "MP - Client Connect to an active steam lobby.", delegate (string[] args) {
            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            ulong lobbyId = ulong.Parse(args[1]);
            TransportSteamworks.OnWantToJoinLobby(lobbyId);
            con.LogToConsole("Starting client and attempting join: " + lobbyId);
        }, null, ("SteamId", "")));

        RegisterCommand_LocalOnly(new Command("hostlobby", "MP - Host steam lobby.", delegate (string[] args) {
            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            con.CheckArgumentCount(args, 2);

            int lobbytype = con.ParseInt(args[1]);
            Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED = con.ParseBool(args[2]);
            ELobbyType type = KSteam.NumToLobbyType(lobbytype);
            Net.NetType netType = Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED ? Net.NetType.DedicatedServer : Net.NetType.Host;

            Multiplayer.DoMultiplayerStatusMessageLog(string.Format(Lang.Get("mmct_hosting_steam_lobby"), netType, type));
            TransportSteamworks.OnWantToHostLobby(netType, type);
        }, null, ("int", "Lobby type (2 = public, 1 = friends only, 0 = private)"), ("bool", "dedicated")));

        RegisterCommand_LocalOnly(new Command("getlobbyinfo", "MP - Log info about your steam lobby.", delegate (string[] args) {
            ConFailIfNotInSteamLobby();
            log.l("LOBBY INFO:\n" + KSteam.CURRENT_LOBBY.DebugReadableDump());
        }, null));

        RegisterCommand_LocalOnly(new Command("dumplobbysearch", "MP - Dump all lobbies shown in server browser.", delegate (string[] args) {
            bool filtered = con.ParseBool(args[1]);
            var text = new StringBuilder("// LOBBY SEARCH DUMP:\n");
            foreach (var item in (filtered ? UIServerBrowser.CurFilteredServerList : UIServerBrowser.CurServerList)) {
                if (item.steam_lobby_info != null) {
                    text.AppendLine(item.steam_lobby_info.DebugReadableDump());
                }
            }
            text.AppendLine("===============");
            log.l(text.ToString());
        }, null, ("bool", "filtered")));

        RegisterCommand_LocalOnly(new Command("refreshlobbies", "MP - Worldwide steam lobby browser refresh.", delegate (string[] args) {
            KSteam.userselected_lobbyDistanceFilter = ELobbyDistanceFilter.k_ELobbyDistanceFilterWorldwide;
            UIServerBrowser.SortHideIncompatible = false;
            UIServerBrowser.ReloadList();
            log.warn($"[LobbyBrowser] CMD-REFRESH worldwide myVer=\"{Multiplayer.FULL_VERSION_TAG}\"");
            con.LogToConsole("Refreshing steam lobbies (worldwide)...");
        }, null));

        RegisterCommand_LocalOnly(new Command("connect", "MP - Client Connect to an active server.", delegate (string[] args) {
            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            if (args.Length > 1) {
                UIMainMenu.USERINPUT_IPPORT = args[1];
            }
            con.LogToConsole("Starting client and attempting connection: " + UIMainMenu.USERINPUT_IPPORT);
            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Client);
        }, null, ("string ip:port", "optional, IP and PORT of target server")));

        RegisterCommand_LocalOnly(new Command("starthost", "MP - Host the game.", delegate (string[] args) {
            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            if (args.Length > 1) {
                UIMainMenu.USERINPUT_IPPORT = ushort.TryParse(args[1], out _) ? ("0.0.0.0:" + args[1]) : args[1];
            }
            con.LogToConsole("Starting Host: " + UIMainMenu.USERINPUT_IPPORT);
            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.Host);
        }, null, ("int port", "")));

        RegisterCommand_LocalOnly(new Command("startserver", "MP - Start a dedicated server. (you'll have no player character)", delegate (string[] args) {
            ConFailIfNetworkAlreadyRunning();
            ConFailIfNotInMainMenu();
            if (args.Length > 1) {
                UIMainMenu.USERINPUT_IPPORT = ushort.TryParse(args[1], out _) ? ("0.0.0.0:" + args[1]) : args[1];
            }
            con.LogToConsole("Starting Server: " + UIMainMenu.USERINPUT_IPPORT);
            TransportLiteNetLib.OnWantToConnect(UIMainMenu.USERINPUT_IPPORT, Net.NetType.DedicatedServer);
        }, null, ("int port", "")));

        RegisterCommand_LocalOnly(new Command("disconnect", "MP - Disconnect from server or shutdown server.", delegate (string[] args) {
            ConFailIfNetworkNotRunning();
            log.l("Disconnect.");
            Multiplayer.ShutdownNetwork();
        }, null));
    }
}
