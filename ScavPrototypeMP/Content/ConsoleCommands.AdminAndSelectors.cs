using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Globalization;
using System.Linq;
using System.Text;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

public partial class Con {
    /// <summary>
    /// 仅限本地执行的命令名集合（使用 <see cref="StringComparer.OrdinalIgnoreCase"/> 防止通过大写命令名绕过拦截在服主端执行）。
    /// </summary>
    public static HashSet<string> localonly_commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "clear",
        "noclip",
        "copylog",
        "volume",
        "musicvolume",
        "pixelate",
        "spectate",
        "fullbright",
        "unchipped",
        "echo",
        "bind",
        "addcustomcommand",
        "removecustomcommand",
        "framerate",
        "setconsoleheight",
        "setconsolecolor",
    };

    /// <summary>
    /// 严禁远程管理员在服务端代为执行的敏感命令集合（大小写不敏感匹配，彻底阻断 <c>ADMINPRIV</c>/<c>NOCLIP</c> 等大小写绕过）。
    /// </summary>
    public static HashSet<string> banned_admin_commands = new HashSet<string>(StringComparer.OrdinalIgnoreCase) {
        "noclip",
        "volume",
        "musicvolume",
        "adminpriv",
        "adminpriv-password",
        "pixelate",
        "spectate",
        "fullbright",
        "music",
        "echo",
        "bind",
        "addcustomcommand",
        "removecustomcommand",
        "framerate",
        "setconsoleheight",
        "setconsolecolor",
    };

    internal static HashSet<ScavPlayer> server_admins = new();
    internal static HashSet<string> removed_server_admins = new();
    internal static string server_admin_password = "";
    public static bool client_adminmode = false;
    public static bool client_isadmin = false;

    public static bool Server_CheckBannedCommand(in ScavPlayer plr, in string first_arg) {
        string com = first_arg?.Trim();
        if (string.IsNullOrEmpty(com)) {
            return true;
        }
        if (localonly_commands.Contains(com)) {
            log.sus($"{plr} tried to execute a local-only command: {com}");
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr != null ? plr.playerId.id : (ushort)0);
            return true;
        }
        if (banned_admin_commands.Contains(com)) {
            log.sus($"{plr} tried to execute a banned admin command: {com}");
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, plr != null ? plr.playerId.id : (ushort)0);
            return true;
        }
        return false;
    }

    static bool SanitizeClientAdminCommand(ScavPlayer plr, ref string[] args, Vector2 cursorpos) {
        Command realcomm = ConsoleScript.SearchExact(args[0]);
        if (realcomm != null) {
            try {
                if (string.Equals(realcomm.name, "repeat", StringComparison.OrdinalIgnoreCase)) {
                    if (args.Length <= 3) {
                        return false;
                    }
                    string commands = args[3];
                    foreach (string item in commands.Split(';')) {
                        string[] splited = item.Replace('_', ' ').Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                        if (splited.Length > 0 && Server_CheckBannedCommand(plr, splited[0])) {
                            return false;
                        }
                    }
                } else if (string.Equals(realcomm.name, "spawn", StringComparison.OrdinalIgnoreCase)) {
                    if (args.Length == 2) {
                        args = args.AddToArray("cursor");
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Security, $"SERVER: REQUEST FROM {plr} TO TryExecuteCommand", e);
                return false;
            }

            try {
                for (int i = 0; i < Math.Min(args.Length - 1, realcomm.argDescription.Length); i++) {
                    ref string arg = ref args[i + 1];
                    if (realcomm.argDescription[i].shortDesc.StartsWith("position")) {
                        if (arg == "cursor") {
                            arg = $"{cursorpos.x.ToString(CultureInfo.InvariantCulture)},{cursorpos.y.ToString(CultureInfo.InvariantCulture)}";
                        } else if (arg == "player") {
                            arg = $"plr:id:{plr.playerId}";
                        } else if (TryGetTaggedPlayerName(arg, out string realname)) {
                            if (realname == "@m") {
                                arg = $"plr:id:{plr.playerId}";
                            } else if (realname == "@c") {
                                NetBody onCursor = GetBodyForCommandOnCursor(cursorpos, true);
                                if (onCursor != null && onCursor.IsPlayer) {
                                    arg = $"plr:id:{onCursor.plr.playerId}";
                                }
                            }
                        }
                    } else if (realcomm.argDescription[i].shortDesc.StartsWith("player")) {
                        if (arg == "@m") {
                            arg = $"id:{plr.playerId}";
                        } else if (arg == "@c") {
                            NetBody onCursor = GetBodyForCommandOnCursor(cursorpos, true);
                            if (onCursor != null && onCursor.IsPlayer) {
                                arg = $"id:{onCursor.plr.playerId}";
                            }
                        }
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Security, $"SERVER: REQUEST FROM {plr} TO TryExecuteCommand", e);
                return false;
            }
        }
        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Console_TryExecuteCommand)]
    static void ServerReceiver_Console_TryExecuteCommand(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out string command);
        reader.Get(out Vector2 cursorpos);

        if (string.IsNullOrWhiteSpace(command)) {
            return;
        }

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) {
            if (server_admins.Contains(plr) || plr.ServerPlayerState.admin_privilege) {
                string[] args = command.Trim().Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries);
                if (args.Length == 0 || Server_CheckBannedCommand(plr, args[0])) {
                    return;
                }
                if (!SanitizeClientAdminCommand(plr, ref args, cursorpos)) {
                    return;
                }

                log.l($"[<color=red>ADMIN</color>] {plr} executes \"{string.Join(" ", args)}\"");

                try {
                    CommandCallerPlayer = plr;
                    CommandCallerPlayer.CursorWorldPos = cursorpos;
                    ConsoleScript.instance.TryExecuteCommand(args, false);
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Security, $"SERVER: REQUEST FROM {plr} TryExecuteCommand", e);
                } finally {
                    CommandCallerPlayer = null;
                }
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Console_ActivateAdminMode)]
    static void ServerReceiver_Console_ActivateAdminMode(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out bool activate);
        reader.Get(out string pass);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) {
            if (activate && !plr.IsLocal) {
                bool is_pass = !string.IsNullOrEmpty(server_admin_password) && pass == server_admin_password;

                if (!removed_server_admins.Contains(plr.GetPersistentId())
                    && (plr.ServerPlayerState.admin_privilege || is_pass || KnownPersons.IsSteamUserPrivileged(plr.SteamId))) {
                    server_admins.Add(plr);
                    string str = $"[<color=red>ADMIN</color>] {plr} <color=green><b>ENTERS</b></color> server console!";
                    if (is_pass) {
                        plr.ServerPlayerState.admin_privilege = true;
                        str += " (Entered correct password)";
                    }
                    log.l(str);
                } else {
                    MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, clientId.id, detailCode: (int)NetmsgId.SERVER_Console_ActivateAdminMode);
                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_DenyAdminMode);
                    writer.Put(false);
                    Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: plr.playerId);
                }
            } else {
                server_admins.Remove(plr);
                log.l($"[<color=red>ADMIN</color>] {plr} <color=red><b>EXITS</b></color> server console!");
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Console_DenyAdminMode)]
    static void ClientReceiver_Console_DenyAdminMode(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        client_adminmode = false;
        client_isadmin = false;
        log.l("Server denied server console access.");
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Console_Log)]
    static void ClientReceiver_Console_Log(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out byte[] compressed_string);
        byte[] uncompressed = Util.DecompressDeflate(compressed_string);
        string as_string = Encoding.UTF8.GetString(uncompressed);
        ConsoleScript.instance.LogToConsole("[<color=red>SERVER</color>] " + as_string);
    }

    public static void UpdatePlayerListAutocomplete() {
        List<string> player_colon_plrlist = new();
        List<string> macros_plus_plrlist = new();

        if (Net.IsRunning) {
            List<string> plrlist = ScavPlayer.AllPlayers.Select(x => x.playerName).ToList();

            player_colon_plrlist = new List<string> { "@c", "@r", "@m" };
            player_colon_plrlist.AddRange(plrlist);
            for (int i = 0; i < player_colon_plrlist.Count; i++) {
                player_colon_plrlist[i] = "plr:" + player_colon_plrlist[i];
            }

            var allmacros = new List<string> { "@c", "@a", "@o", "@r", "@m" };
            allmacros.AddRange(plrlist);
            macros_plus_plrlist = allmacros;
        }

        foreach (Command com in ConsoleScript.Commands) {
            for (int i = 0; i < com.argDescription.Length; i++) {
                if (com.argDescription[i].shortDesc.StartsWith("player")) {
                    com.GetArgAutofill()[i] = macros_plus_plrlist;
                }
                if (com.argDescription[i].shortDesc.StartsWith("position")) {
                    var og = new List<string> { "cursor", "player", "random", "#,#" };
                    og.AddRange(player_colon_plrlist);
                    com.GetArgAutofill()[i] = og;
                }
            }
        }
    }

    public static void Server_SendConsoleLog(in string text, ScavPlayer plr) => Server_SendConsoleLog(text, new List<knetid> { plr.playerId });

    public static void Server_SendConsoleLog(in string text, IReadOnlyList<knetid> targets = null) {
        targets ??= server_admins.Select(x => x.playerId).ToList();
        if (targets.Count > 0) {
            byte[] compressed = Array.Empty<byte>();
            try {
                compressed = Util.CompressDeflate(Encoding.UTF8.GetBytes(text));
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Console_Log);
                writer.PutBytesWithLength(compressed);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: targets);
            } catch (Exception e) {
                // 此处直写底层日志避免触发 LogToConsole 钩子无限递归
                Plugin.log.LogError($"ConsoleScript_LogToConsole_MultiplayerPatch compressed len:{compressed.Length}\n{e}\n{new StackTrace()}");
            }
        }
    }

    public static bool CanExecuteAdminCommands() {
        if (!Net.IsRunning || Net.IsServer) {
            return true;
        }
        return client_isadmin;
    }

    public static void ExecuteCommandAdminNoLog(in string command) {
        if (!Net.IsRunning || Net.IsServer) {
            con.TryExecuteCommand(command.Split(new char[] { ' ' }, StringSplitOptions.RemoveEmptyEntries), false);
        } else {
            Client_ExecuteAdminCommand(command);
        }
    }

    public static void Client_ExecuteAdminCommand(in string command) {
        var writer = Net.CreateWriter(NetmsgId.SERVER_Console_TryExecuteCommand);
        writer.Put(command);
        writer.Put(Util.GetCursorWorldPos());
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    public static List<ScavPlayer> GetPlayersForCommand(string plrname, ScavPlayer caller, bool onlyOne, bool allowMacros, List<ScavPlayer> toFilter = null) {
        toFilter ??= ScavPlayer.AllPlayers.ToList();
        var list = new List<ScavPlayer>();

        if (string.IsNullOrEmpty(plrname) || toFilter.Count == 0) {
            return list;
        }
        string loweredPlrname = plrname.ToLowerInvariant();

        if (plrname.StartsWith('@') && (allowMacros || loweredPlrname == "@c")) {
            if (!onlyOne) {
                if (loweredPlrname == "@a") {
                    return toFilter;
                }
                if (loweredPlrname == "@o") {
                    toFilter.Remove(caller);
                    return toFilter;
                }
            }

            if (loweredPlrname.StartsWith("@m") && caller != null && toFilter.Contains(caller)) {
                list.Add(caller);
                return list;
            }
            if (loweredPlrname.StartsWith("@r")) {
                int requiredCount = 1;
                if (!onlyOne) {
                    int colon = -1;
                    for (int i = 2; i < loweredPlrname.Length; i++) {
                        if (char.IsDigit(loweredPlrname[i])) {
                            colon = i;
                            break;
                        }
                    }
                    if (colon != -1) {
                        string countStr = loweredPlrname.Substring(colon);
                        if (int.TryParse(countStr, out int c) && c > 0) {
                            requiredCount = c;
                        }
                    }
                }
                requiredCount = Math.Min(requiredCount, toFilter.Count);
                toFilter.ShuffleRandom();
                return toFilter.GetRange(0, requiredCount);
            }
            if (loweredPlrname.StartsWith("@c")) {
                Vector2 cursorPos = caller != null ? caller.CursorWorldPos : Util.GetCursorWorldPos();
                NetBody person = GetBodyForCommandOnCursor(cursorPos, true);
                if (person != null && person.plr != null) {
                    list.Add(person.plr);
                }
                return list;
            }
            return list;
        }

        if (plrname.TryTrimStart("body:", out string bodyId, true)) {
            if (ushort.TryParse(bodyId, out ushort bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out NetBody nb) && nb.IsPlayer) {
                list.Add(nb.plr);
                return list;
            }
        } else if (!onlyOne && plrname.TryTrimStart("team:", out string team, true)) {
            if (!Color24.TryParseHex(team, out Color24 teamColor)) {
                if (TryGetPlayerFromPartialName(plrname, out ScavPlayer plr, toFilter)) {
                    teamColor = plr.playerColor;
                } else {
                    return list;
                }
            }
            return toFilter.Where(x => x.playerColor == teamColor).ToList();
        } else if (TryGetPlayerFromPartialName(plrname, out ScavPlayer plr, toFilter)) {
            list.Add(plr);
            return list;
        }

        return list;
    }

    public static bool TryGetPlayerFromPartialName(string partialname, out ScavPlayer player, List<ScavPlayer> toSearchIn = null) {
        toSearchIn ??= ScavPlayer.AllPlayers.ToList();

        if (partialname.StartsWith("id:")) {
            string supposed_id = partialname.Substring(3);
            if (ushort.TryParse(supposed_id, out ushort id)
                && ScavPlayer.TryGetPlayerFromClientId(id, out player)
                && toSearchIn.Contains(player)) {
                return true;
            }
        }
        if (partialname.StartsWith("765611") && ulong.TryParse(partialname, out ulong steamId)) {
            player = toSearchIn.FirstOrDefault(x => x.SteamId == steamId);
            if (player != null) {
                return true;
            }
        }

        foreach (ScavPlayer plr in toSearchIn) {
            if (plr.playerName == partialname) {
                player = plr;
                return true;
            }
        }
        foreach (ScavPlayer plr in toSearchIn) {
            if (plr.playerName.StartsWith(partialname, StringComparison.OrdinalIgnoreCase)) {
                player = plr;
                return true;
            }
        }
        player = null;
        return false;
    }

    public static NetBody GetBodyForCommandOnCursor(Vector2 cursorpos, bool onlyPlayers = false) {
        Body body = Util.GetBodyOnPos(cursorpos);
        NetBody person = null;
        if (body != null) {
            body.TryGetNetBody(out person);
        }
        var nearestperson = NetBody.GetNearestBody(cursorpos, true, false);
        person ??= nearestperson.Item1;

        if (person != null) {
            if (onlyPlayers && !person.IsPlayer) {
                return null;
            }
            return person;
        }
        return null;
    }

    public static NetBody GetBodyForCommand(string name, ScavPlayer caller) {
        if (string.IsNullOrEmpty(name)) {
            return null;
        }
        string lowered_plrname = name.ToLowerInvariant();
        lowered_plrname.TryTrimStart("body:", out string bodyId);

        List<ScavPlayer> players = GetPlayersForCommand(name, caller, true, true, ScavPlayer.BodyToPlayerDict.Values.ToList());
        if (players != null && players.Count > 0) {
            return players[0].netBody;
        }

        if (ushort.TryParse(bodyId, out ushort bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out NetBody nb)) {
            return nb;
        }

        return null;
    }

    public static Tuple<bool, string> PerformActionOnPlayersByName(string plrname, Action<ScavPlayer> func, bool allow_macros, ScavPlayer callerPlr, bool require_body, bool onlyOne) {
        if (string.IsNullOrEmpty(plrname)) {
            return Tuple.Create(false, "Empty name");
        }

        List<ScavPlayer> pool = require_body
            ? ScavPlayer.BodyToPlayerDict.Values.ToList()
            : ScavPlayer.AllPlayers.ToList();
        List<ScavPlayer> players = GetPlayersForCommand(plrname, callerPlr, onlyOne, allow_macros, pool);

        if (players.Count > 0) {
            foreach (ScavPlayer item in players) {
                func(item);
            }
            return Tuple.Create(true, "Success");
        }
        return Tuple.Create(false, "Player not found");
    }

    public static Tuple<bool, string> PerformActionOnBodiesByName(string plrname, Action<NetBody> func, bool allow_macros, bool only_players, ScavPlayer callerPlr, bool onlyOne) {
        if (string.IsNullOrEmpty(plrname)) {
            return Tuple.Create(false, "Empty name");
        }
        string lowered_plrname = plrname.ToLowerInvariant();
        lowered_plrname.TryTrimStart("body:", out string bodyId);

        List<ScavPlayer> players = GetPlayersForCommand(plrname, callerPlr, onlyOne, allow_macros, ScavPlayer.BodyToPlayerDict.Values.ToList());
        if (players.Count > 0) {
            foreach (ScavPlayer item in players) {
                func(item.netBody);
            }
            return Tuple.Create(true, "Success");
        }

        if (ushort.TryParse(bodyId, out ushort bodyNetId) && NetBody.TryGetNetBodyFromId(bodyNetId, out NetBody nb)) {
            func(nb);
            return Tuple.Create(true, "Found body");
        }

        if ((bodyId == "@m" || bodyId == "@r") && Util.TryGetLocalBody(out Body b) && b.TryGetNetBody(out NetBody netb)) {
            func(netb);
            return Tuple.Create(true, "Success");
        }
        return Tuple.Create(false, "Body not found");
    }

    public static SyncInfo GetSyncInfoForCommand(string objname) {
        GameObject obj = GetObjectForCommand(objname, true);
        if (obj == null) {
            return null;
        }
        return NetObjectRegistry.SyncRegistry[obj];
    }

    public static GameObject GetObjectForCommand(string objname, bool require_syncinfo, Func<GameObject, bool> condition = null) {
        condition ??= _ => true;
        GameObject go = null;
        objname = objname.Trim().ToLowerInvariant();
        if (!string.IsNullOrEmpty(objname)) {
            string[] split = objname.Split(':');
            if (split[0] == "syncid") {
                split = split.Skip(1).ToArray();
            }
            string firststring = split[0];
            if (ushort.TryParse(firststring, out ushort syncid)) {
                if (NetObjectRegistry.TryGetSyncInfo(syncid, out SyncInfo si)) {
                    go = si.go;
                }
            } else if (firststring == "@r" || firststring == "random") {
                IEnumerable<GameObject> array = UnityEngine.Object.FindObjectsOfType<GameObject>(includeInactive: true).Where(x => x.IsItemOrBuilding());
                if (require_syncinfo) {
                    array = array.Where(x => NetObjectRegistry.ObjectShouldBeSynced(x) && condition(x));
                }
                if (array.Any()) {
                    go = array.PickRandom();
                    if (require_syncinfo) {
                        NetObjectRegistry.TryGetSyncInfoOrRegister(go, out _);
                    }
                }
            } else if (firststring == "@c" || firststring == "cursor") {
                Vector2 cursorpos = GetCommandCallerCursorPos();
                Collider2D[] collider2Darray = Physics2D.OverlapPointAll(cursorpos);
                for (int i = 0; i < collider2Darray.Length; i++) {
                    GameObject gameObject = collider2Darray[i].gameObject;
                    if (gameObject.IsItemOrBuilding() && condition(gameObject) && (!require_syncinfo || NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject, out _))) {
                        go = gameObject;
                        break;
                    }
                }
            }
        }
        return go;
    }

    public static GameObject GetObjectOrBodyForCommand(string objname) {
        GameObject go = GetObjectForCommand(objname, false);
        if (go == null) {
            NetBody nb = GetBodyForCommand(objname, GetCommandCallerPlayer());
            if (nb == null && TryGetTaggedPlayerName(objname, out string plrname)) {
                nb = GetBodyForCommand(plrname, GetCommandCallerPlayer());
            }
            if (nb != null) {
                go = nb.gameObject;
            }
        }
        return go;
    }
}
