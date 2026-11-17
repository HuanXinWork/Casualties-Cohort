using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 在原版控制台完成命令表初始化后，追加联机覆写命令与模组专属命令。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterAllCommands))]
static class ConsoleScript_RegisterAllCommands_MultiplayerPatch {
    static void Postfix() {
        try {
            Con._RegisterMultiplayerConsoleCommands();
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, "MP console RegisterAllCommands Postfix", e);
        }
    }
}

/// <summary>
/// 原版 <see cref="ConsoleScript.RegisterAllCommands"/> 仅在 <c>Commands.Count == 0</c> 时执行；
/// 若前一次启动中途异常中断，后续场景切换时在此兜底确保联机命令注册齐全。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), "Start")]
static class ConsoleScript_Start_EnsureMpCommands_MultiplayerPatch {
    static void Postfix() {
        try {
            if (ConsoleScript.SearchExact("species") == null || ConsoleScript.SearchExact("telekinesis") == null) {
                Con._RegisterMultiplayerConsoleCommands();
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, "MP console Start ensure", e);
        }
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.RegisterPlayerDetails))]
static class ConsoleScript_RegisterPlayerDetails_MultiplayerPatch {
    static void Prefix(ConsoleScript __instance, ref bool __state) {
        __state = __instance.playerDetailsRegistered;
    }

    static void Postfix(ConsoleScript __instance, ref bool __state) {
        if (!__state) {
            Command command2 = ConsoleScript.SearchExact("setlimbfield");
            if (command2?.argAutofill != null && command2.argAutofill.ContainsKey(1) && command2.argAutofill[1] != null) {
                command2.argAutofill[1].Add("all");
            }
        }
    }
}

/// <summary>
/// 当客户端开启远程管理员控制台模式（<c>admin</c>）时，将非本地专属且未列入黑名单的命令转发至服务端执行。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.TryExecuteCommand))]
static class ConsoleScript_TryExecuteCommand_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string[] args, bool addToLog) {
        if (addToLog && Multiplayer.IsNetworkActiveAndIsClient() && Con.client_adminmode) {
            if (args.Length == 0 || string.IsNullOrEmpty(args[0])) {
                return false;
            }

            if (Con.localonly_commands.Contains(args[0])) {
                return true;
            }
            if (Con.banned_admin_commands.Contains(args[0])) {
                log.l($"{args[0]} is a banned admin command.");
                return false;
            }

            string rawCommand = string.Join(" ", args);
            log.l($"Sending command to server: \"{rawCommand}\"");
            Con.Client_ExecuteAdminCommand(rawCommand);

            __instance.AddCommandToLogAndClearInput();
            return false;
        }

        return true;
    }

    static void Postfix() {
    }
}

/// <summary>
/// 服务端控制台产生新日志时，自动压缩广播给当前在线的远程管理员客户端（过滤字体缺字刷屏）。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.LogToConsole))]
static class ConsoleScript_LogToConsole_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string text) {
        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            if (text.StartsWith("The character with Unicode value \\u")) {
                return true;
            }
            Con.Server_SendConsoleLog(text, Con.server_admins.Select(x => x.playerId).ToList());
        }
        return true;
    }
}

/// <summary>
/// 扩展原版坐标解析器，支持 <c>plr:&lt;name/@macro&gt;</c> 以及远程管理员上下文的 <c>cursor</c> / <c>player</c>。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ParsePosition))]
static class ConsoleScript_ParsePosition_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string s, ref Vector2 __result) {
        ScavPlayer plr = Con.GetCommandCallerPlayer();
        if (Con.TryGetTaggedPlayerName(s, out string plrname)) {
            NetBody foundthing = Con.GetBodyForCommand(plrname, plr);
            if (foundthing != null) {
                __result = foundthing.transform.position;
            } else {
                throw new Exception("\"" + s + "\" player not found.");
            }
            return false;
        }
        if (plr != null) {
            if (s == "cursor") {
                __result = plr.CursorWorldPos;
                return false;
            }
            if (s == "player") {
                __result = plr.pos;
                return false;
            }
        }

        return true;
    }
}

/// <summary>
/// 扩展原版布尔解析器，兼容 <c>0</c> / 非零整数形式的布尔参数。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ParseBool))]
static class ConsoleScript_ParseBool_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string s, ref bool __result) {
        string lowered = s.ToLowerInvariant();
        if (bool.TryParse(lowered, out __result)) {
            return false;
        }
        if (int.TryParse(lowered, out int n)) {
            __result = n != 0;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.ToggleActiveState))]
static class ConsoleScript_ToggleActiveState_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance) {
        return true;
    }
}

/// <summary>
/// 联机运行期间抑制原版 <see cref="ConsoleScript.Alert"/> 强制弹出控制台抢夺输入焦点，仅记录日志。
/// </summary>
[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.Alert))]
static class ConsoleScript_Alert_NoAutoOpen_MultiplayerPatch {
    static bool Prefix(ConsoleScript __instance, string message) {
        if (!Net.IsRunning) {
            return true;
        }
        if (__instance != null) {
            __instance.LogToConsole(message);
        }
        return false;
    }
}

/// <summary>
/// 联机控制台命令核心管理器（按职责拆分为多个 <c>partial</c> 模块文件）。
/// </summary>
public partial class Con : CasualtiesTogetherSingleton {
    public static ConsoleScript con => ConsoleScript.instance;

    public static bool _DEV_CHATSPY = false;
    public static float _DEV_ZOOM = 1f;

    public static bool _DEV_HIDEHUD {
        get {
            if (PlayerCamera.main == null) return false;
            return !PlayerCamera.main.mainCanvas.gameObject.activeSelf;
        }
        set {
            if (PlayerCamera.main == null) return;
            PlayerCamera.main.mainCanvas.gameObject.SetActive(!value);
        }
    }

    public static bool _DEV_GODMODE = false;

    public static bool _DEV_FREECAM {
        get {
            if (PlayerCamera.main == null) {
                return false;
            }
            return PlayerCamera.main.isFreecam;
        }
        set {
            if (PlayerCamera.main == null) {
                return;
            }
            PlayerCamera.main.isFreecam = value;
        }
    }

    public static bool _DEV_NO_VISION_EFFECT = false;

    internal static bool _DEV_AUDIO_ONLY_ON_FOCUS = false;
    internal static float _DEV_LAST_VOLUME = 1f;

    public static bool _DEV_TELEKINESIS = false;
    public static GameObject _DEV_TELEKINESIS_OBJECT = null;

    static ScavPlayer CommandCallerPlayer = null;
    static bool _mpConsoleCommandsRegistered;

    public static bool IsConsoleOpen() {
        return (bool)ConsoleScript.instance && ConsoleScript.instance.active;
    }

    public static void OpenConsole() {
        ConsoleScript.CheckForConsole();
        if (!Con.con.active) {
            Con.con.ToggleActiveState();
        }
    }

    public static bool CanCheat() {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            return Multiplayer.rules.AllowClientCheatCommands;
        }
        return Multiplayer.rules.sv_cheats;
    }

    public static string CheatWarningText() {
        if (!Net.IsRunning) {
            return "huh";
        }
        if (!Multiplayer.rules.sv_cheats) {
            return "Server rule 'sv_cheats' is disabled";
        }
        if (!Multiplayer.rules.AllowClientCheatCommands) {
            return "Server rule 'AllowClientCheatCommands' is disabled";
        }
        return "hello";
    }

    public static void ConFailIfNetworkAlreadyRunning() {
        if (Net.IsRunning) {
            throw new Exception("Network system is already running!");
        }
    }

    public static void ConFailIfNetworkNotRunning() {
        if (!Net.IsRunning) {
            throw new Exception("Network system is not running!");
        }
    }

    public static void ConFailIfNotInSteamLobby() {
        if (!Net.IsRunning) {
            throw new Exception("Network system is not running!");
        }
        if (!Net.TryGetSteamTransport(out _)) {
            throw new Exception("You can do that only in a Steam lobby!");
        }
    }

    public static void ConFailIfNotConnected() {
        if (!Net.IsConnected) {
            throw new Exception("Network system is not running or not connected!");
        }
    }

    public static void ConFailIfNoLocalPlayer() {
        if (ScavPlayer.LocalPlayer == null) {
            throw new Exception("Local player not found!");
        }
    }

    public static void LogToConsole(string text) {
        con.LogToConsole(text);
    }

    public static void ConFailIfCheatsDisabled() {
        if (!CanCheat()) {
            throw new Exception("Cheats disabled. " + CheatWarningText());
        }
    }

    public static void ConFailIfNetworkIsRunningAndIsClient() {
        if (Net.IsRunning && Net.IsClient) {
            throw new Exception("Client can't do that.");
        }
    }

    public static void ConFailIfNetworkIsRunningAndIsServer() {
        if (Net.IsRunning && Net.IsServer) {
            throw new Exception("Server can't do that.");
        }
    }

    public static void ConFailIfNotInMainMenu() {
        if (!Util.IsInMainMenu()) {
            throw new Exception("This can only be done in main menu.");
        }
    }

    public static bool TryGetTaggedPlayerName(string s, out string name) {
        if (s.TryTrimStart("player:", out name, true)) {
            return true;
        }
        if (s.TryTrimStart("plr:", out name, true)) {
            return true;
        }
        name = null;
        return false;
    }

    public static bool ParseBool01(string str, bool enabledisable = true) {
        str = str.ToLowerInvariant();
        if (enabledisable) {
            if (str == "enable") {
                return true;
            }
            if (str == "disable") {
                return false;
            }
        }

        if (bool.TryParse(str, out bool result)) {
            return result;
        }
        if (int.TryParse(str, out int n)) {
            return n != 0;
        }

        throw new Exception("\"" + str + "\" is not a valid boolean value! (true/false)");
    }

    public static void UnNoclip() {
        if (ConsoleScript.instance.noClip) {
            PlayerCamera.main.body.rb.simulated = true;
            ConsoleScript.instance.noClip = false;
        }
    }

    public static Command RegisterCommandModern(
        string name,
        string desc,
        Action<string[], Command> action,
        params (string type, string typeDesc, string fallback, List<string> autofill)[] args) {
        var argAutofill = new Dictionary<int, List<string>>();
        var argDesc = new List<(string t, string d)>();

        for (int i = 0; i < args.Length; i++) {
            var arg = args[i];
            if (arg.autofill != null) {
                argAutofill[i] = arg.autofill;
            }
            string typeStr = arg.type;
            string typeDesc = arg.typeDesc;
            if (arg.fallback != null) {
                typeDesc += $" (default: {arg.fallback})";
            }
            argDesc.Add((typeStr, typeDesc));
        }

        var defaultArgs = args.ToList();
        defaultArgs.Insert(0, (name, name, name, null));

        Command c = null;
        c = new Command(
            name,
            desc,
            (string[] splited) => {
                var dargs = defaultArgs.Select(x => x.fallback).ToList();
                for (int i = 0; i < dargs.Count; i++) {
                    var argInfo = defaultArgs[i];
                    if (i < splited.Length) {
                        dargs[i] = splited[i];
                    }
                    if (dargs[i] == null) {
                        throw new Exception($"Command \"{name}\": Argument {i}: Expected {argInfo.type} ({argInfo.typeDesc}), got null.");
                    }
                }
                action(dargs.ToArray(), c);
            },
            argAutofill,
            argDesc.ToArray());

        return RegisterCommand(c);
    }

    public static Command RegisterCommand(Command comm) {
        if (comm == null || string.IsNullOrEmpty(comm.name)) {
            return comm;
        }
        if (ConsoleScript.instance == null) {
            Util.CallLambdaWhen(() => ConsoleScript.instance != null, () => {
                RegisterCommand(comm);
            });
        } else {
            Command existing = ConsoleScript.SearchExact(comm.name);
            if (existing == null) {
                ConsoleScript.Commands.Add(comm);
            }
        }
        return comm;
    }

    public static Command RegisterCommand_LocalOnly(Command comm) {
        RegisterCommand(comm);
        localonly_commands.Add(comm.name);
        return comm;
    }

    public static Command RegisterCommand_BannedAdmin(Command comm) {
        RegisterCommand(comm);
        banned_admin_commands.Add(comm.name);
        return comm;
    }

    public static GameObject SpawnThingOnPlayer(string resourceid, Body body = null, bool give_it_to_em = false, GameObject container = null) {
        if (Net.IsDedicatedServer && body == null && ScavPlayer.BodyToPlayerDict.Count > 0) {
            body = ScavPlayer.BodyToPlayerDict.First().Key;
        }
        body ??= PlayerCamera.main.body;

        UnityEngine.Object resource = Resources.Load(resourceid);
        if (!resource) {
            Plugin.log.LogError($"Unknown resource: {resourceid}");
            return null;
        }

        var t = UnityEngine.Object.Instantiate(
            resource,
            (Vector2)body.transform.position + UnityEngine.Random.insideUnitCircle,
            Quaternion.Euler(0f, 0f, 0f)) as GameObject;

        if (t.TryGetComponent<AmmoScript>(out var ammo)) {
            ammo.rounds = ammo.maxRounds;
        }
        if (t.TryGetComponent<GunScript>(out var gun)) {
            gun.roundsInMag = gun.magCapacity;
            if (gun.feedType == GunScript.FeedType.Mag) {
                gun.hasMag = true;
            }
            gun.roundInChamber = GunScript.RoundInChamber.Round;
        }
        if (give_it_to_em && t.TryGetComponent<Item>(out var item)) {
            if (container && container.TryGetComponent<Container>(out var cntr)) {
                cntr.LoadItem(item);
            } else {
                body.ForceAutoPickUpItem(item);
            }
        }
        return t;
    }

    public static void DelayRunCommand(float delay_sec, string cmd) {
        Plugin.StartCoroutine(_DelayRunCommandCoroutine(delay_sec, cmd));
    }

    static IEnumerator _DelayRunCommandCoroutine(float delay_sec, string cmd) {
        yield return new WaitForSecondsRealtime(delay_sec);
        RunCommand(cmd);
    }

    public static void RunCommand(string cmd, bool addToLog = false) {
        try {
            string[] args = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
            ConsoleScript.instance.TryExecuteCommand(args, addToLog);
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, $"RunCommand {cmd}", e);
        }
    }

    public static void RunCommandUnsafe(string cmd, bool addToLog = false) {
        string[] args = cmd.Trim().Split(' ', StringSplitOptions.RemoveEmptyEntries);
        ConsoleScript.instance.TryExecuteCommand(args, addToLog);
    }

    public static bool CanFreecam() {
        if (Util.IsInMainMenu()) {
            return false;
        }
        if (CanCheat()) {
            return true;
        }
        if (Multiplayer.rules.SpectateWhileUnconscious) {
            if (!PlayerCamera.main.body.conscious) {
                return true;
            }
        } else if (!PlayerCamera.main.body.alive) {
            return true;
        }
        return false;
    }

    /// <summary>
    /// 清理世界内残留弹壳/碎屑低优物品、地形裂纹、怪物与爆炸焦痕。
    /// </summary>
    public static void CleanWorld() {
        foreach (var item in WorldGeneration.world.blockDamages) {
            item.damage = 99999999999f;
            item.UpdateSprite();
        }
        WorldGeneration.world.blockDamages.Clear();

        foreach (var item in GameObject.FindObjectsOfType<SpiderHandler>()) {
            GameObject.Destroy(item.gameObject);
        }
        // 仅销毁属于低优先级垃圾列表（如弹壳 casing）的世界散落物品，避免误删正常装备
        foreach (var item in GameObject.FindObjectsOfType<Item>()) {
            if (item != null && NetObjectRegistry.lowpriority_objects_resourceids.Contains(item.id)) {
                GameObject.Destroy(item.gameObject);
            }
        }
        foreach (var item in GameObject.FindObjectsOfType<SpriteRenderer>()) {
            if (item.gameObject.name.Contains("blastmark") && item.sortingOrder == -9050) {
                GameObject.Destroy(item.gameObject);
            }
        }
    }

    public static ScavPlayer GetCommandCallerPlayer() {
        if (CommandCallerPlayer == null) {
            return ScavPlayer.LocalPlayer;
        }
        return CommandCallerPlayer;
    }

    public static Vector2 GetCommandCallerCursorPos() {
        ScavPlayer caller = GetCommandCallerPlayer();
        if (caller != null) {
            return caller.CursorWorldPos;
        }
        return Util.GetCursorWorldPos();
    }

#if FULLGAME
    public static bool TryParseSpeciesArg(string raw, out int chr) {
        chr = 0;
        if (string.IsNullOrWhiteSpace(raw)) {
            return false;
        }
        string s = raw.Trim().ToLowerInvariant();
        if (s is "expie" or "exp" or "experiment" or "0") {
            chr = 0;
            return true;
        }
        if (s is "milky" or "1") {
            chr = 1;
            return true;
        }
        if (s is "dune" or "2") {
            chr = 2;
            return true;
        }
        return false;
    }
#endif

    /// <summary>
    /// 联机控制台命令总注册入口，按领域分派给各职责子模块并执行容错隔离。
    /// </summary>
    internal static void _RegisterMultiplayerConsoleCommands() {
        ConsoleScript con = ConsoleScript.instance;

        try {
            RegisterVanillaCommandOverrides(con);
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, "MP console vanilla-command patch aborted (MP commands still register)", e);
        }

        try {
            WrapVanillaCheatAndServerCommands();
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, "MP console cheat-wrap aborted (MP commands still register)", e);
        }

        if (Plugin.dump_game_ids) {
            log.l($"all og commands: \n{string.Join("\n", ConsoleScript.Commands.Select(x => x.name).ToArray())}");
        }

        try {
            RegisterWorldAndGameplayCommands(con);
            RegisterLobbyAndModerationCommands(con);
            RegisterDiagnosticsAndClientCommands(con);
#if !RELEASE
            RegisterDevLegacyCommands(con);
#endif

            if (Plugin.s != null) {
                try {
                    Plugin.s.SendMessage("OnRegisterConsoleCommands", SendMessageOptions.DontRequireReceiver);
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Core, "OnRegisterConsoleCommands SendMessage", e);
                }
            }

            _mpConsoleCommandsRegistered = ConsoleScript.SearchExact("species") != null
                || ConsoleScript.SearchExact("telekinesis") != null
                || ConsoleScript.SearchExact("objdelete") != null;
            log.l($"Patched the console! mpRegistered={_mpConsoleCommandsRegistered} species={ConsoleScript.SearchExact("species") != null} telekinesis={ConsoleScript.SearchExact("telekinesis") != null} total={ConsoleScript.Commands.Count}");
            if (Plugin.s != null && Plugin.s.TryGetComponent(out Plugin.LoadErrorNotifier _)) {
                log.error("WrongVersionNotifier:\n" + Plugin.LoadErrorNotifier.errorstr);
            }

            UpdatePlayerListAutocomplete();
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Core, "MP console RegisterCommand block aborted", e);
        }
    }

    void Start() {
        ScavPlayer.OnPlayerLeft += (ScavPlayer plr) => {
            server_admins.Remove(plr);
        };
    }

    void Update() {
        if (_DEV_TELEKINESIS) {
            Vector2 cursorpos = Util.GetCursorWorldPos();
            if (UnityEngine.Input.GetKeyDown(KeyCode.Mouse1)) {
                _DEV_TELEKINESIS_OBJECT = null;
                Collider2D[] hits = Physics2D.OverlapPointAll(cursorpos);
                foreach (Collider2D obj in hits) {
                    Body b = obj.GetComponentInParent<Body>();
                    if (b != null) {
                        if (Util.IsBodyLocal(b)) {
                            continue;
                        }
                        if (b.TryGetComponent(out Limb l)) {
                            _DEV_TELEKINESIS_OBJECT = l.gameObject;
                        } else {
                            _DEV_TELEKINESIS_OBJECT = b.gameObject;
                        }
                    } else {
                        if (obj.TryGetComponent(out BuildingEntity _)) {
                            _DEV_TELEKINESIS_OBJECT = obj.gameObject;
                        }
                        if (obj.TryGetComponent(out Rigidbody2D rb)) {
                            _DEV_TELEKINESIS_OBJECT = rb.gameObject;
                        }
                    }
                    if (_DEV_TELEKINESIS_OBJECT != null) {
                        if (_DEV_TELEKINESIS_OBJECT.TryGetComponent<ChunkScript>(out _)) {
                            _DEV_TELEKINESIS_OBJECT = null;
                            continue;
                        }
                        break;
                    }
                }
                log.l("telekinesis click: " + _DEV_TELEKINESIS_OBJECT.ToSafeString());
            }
            if (UnityEngine.Input.GetKeyUp(KeyCode.Mouse1)) {
                _DEV_TELEKINESIS_OBJECT = null;
            }
        }
    }

    // 注意：Telekinesis.cs 通过 Harmony Prefix 拦截 Con.FixedUpdate，此字段与方法签名严禁改动。
    internal static Vector2 lastworldcursorpos = new Vector2();

    void FixedUpdate() {
        Vector2 cursorpos = Util.GetCursorWorldPos();

        if (_DEV_TELEKINESIS && !CanCheat()) {
            _DEV_TELEKINESIS = false;
            _DEV_TELEKINESIS_OBJECT = null;
        }

        lastworldcursorpos = cursorpos;
    }

    void LateUpdate() {
        if (_DEV_AUDIO_ONLY_ON_FOCUS) {
            if (Application.isFocused) {
                AudioListener.volume = _DEV_LAST_VOLUME;
            } else {
                if (AudioListener.volume > 0.0101f) {
                    _DEV_LAST_VOLUME = AudioListener.volume;
                }
                AudioListener.volume = 0.01f;
            }
        }
    }
}
