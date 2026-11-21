using BepInEx;
using CasualtiesMultiplayerAPI;
using CasualtiesTogetherUtils;
using LiteNetLib.Utils;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using Color = UnityEngine.Color;

namespace Together;

public partial class Multiplayer : MonoBehaviour {

    public static ulong GAME_FILES_HASH = 0;
    public static bool ExplicitSessionName;

#if DEBUG
    public const bool IS_DEBUG_BUILD = true;
#else
    public const bool IS_DEBUG_BUILD = false;
#endif
#if RELEASETEST || PLAYTEST
    public const bool IS_TESTING_BUILD = true;
#else
    public const bool IS_TESTING_BUILD = false;
#endif
#if RELEASE
    public const bool IS_RELEASE_BUILD = true;
#else
    public const bool IS_RELEASE_BUILD = false;
#endif

    private void OnDestroy() {
        try {
            try {
                if (!Plugin.FORCE_DISABLE_MP_MOD) {
                    if (Plugin.s != null) {
                        Plugin.s.GetOrAddComponent<Plugin.LoadErrorNotifier>();
                    }
                    if (String.IsNullOrWhiteSpace(Plugin.LoadErrorNotifier.errorstr)) {
                        Plugin.LoadErrorNotifier.errorstr = "Multiplayer Singleton got deleted???";
                    }
                }

                Together.log.l("Multiplayer.OnDestroy()");
            } catch (System.Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, "Multiplayer.OnDestroy", e);
            }
        } catch (System.Exception) {
            // 应用退出阶段若日志接收器本身已销毁，忽略二次异常防止阻断退出
        }

        Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
    }

    private void OnApplicationQuit() {
        Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
    }

    public static bool verbose => DebugMenuSettings.is_verbose;
    public const int CONST_MAX_POSSIBLE_PLAYER_COUNT = 200;
    public const int MAX_NAME_LENGTH = 24;
    public const int LEGACY_ASCII_MAX_NAME_LENGTH = 16;

    /// <summary>
    /// 本地玩家的权威期望昵称（支持中文/西里尔等 Unicode 字符）。
    /// 避免直接调用 <c>KSteam.SessionName()</c> 导致含混合字符的 Steam 昵称被旧版 ASCII 正则截断污染 <c>UIMainMenu.USERINPUT_NAME</c>。
    /// </summary>
    public static string PLAYER_USERNAME {
        get {
            if ((Plugin.plrname_override || ExplicitSessionName) && !string.IsNullOrEmpty(UIMainMenu.USERINPUT_NAME)) {
                return SanitizeUnicodeTextInput(UIMainMenu.USERINPUT_NAME);
            }
            if (KSteam.Loaded) {
                string live = KSteam.GetLocalUsername();
                if (!string.IsNullOrEmpty(live) && live != "[unknown]") {
                    string clean = SanitizeUnicodeTextInput(live);
                    if (!string.IsNullOrEmpty(clean)) {
                        UIMainMenu.USERINPUT_NAME = clean;
                        return clean;
                    }
                }
            }
            string fallback = SanitizeUnicodeTextInput(UIMainMenu.USERINPUT_NAME);
            return !string.IsNullOrEmpty(fallback) ? fallback : "Player";
        }
    }

    internal static byte PLAYER_COUNT_LIMIT {
        get { return rules.PLAYER_COUNT_LIMIT; }
        set { rules.PLAYER_COUNT_LIMIT = value; }
    }

    public static string multiplayerStatusMessage = "Game initialized.";
    public static double lastMultiplayerStatusMessageChangeTime = 0;

    public static bool showMultiplayerMenu = true;

    public static string Server_GetJoinSecret() {
        if (SERVER_JOIN_SECRETS.Count > 0) {
            return SERVER_JOIN_SECRETS[SERVER_JOIN_SECRETS.Count - 1];
        }
        return "1";
    }

    public static List<string> SERVER_JOIN_SECRETS = new();
    public static string CLIENT_JOIN_SECRET = "0"; // used by Discord OnJoin and the approved OnJoinRequested
    public static string INPUT_PASSWORD = "";
    public static bool SERVER_TOGGLE_CHECK_GAME_HASH = true;
    public static bool SERVER_TOGGLE_ENFORCE_MODLIST = false;
    public static bool SERVER_TOGGLE_SHOULD_HOST_DEDICATED = false;

    public static MultiplayerGameRules rules = new MultiplayerGameRules();
    public static readonly JsonPresetsSaver<MultiplayerGameRules> RulesSaver = new("mp_rule_presets.json");

    public static bool DedicatedServerDisableVisuals = false;

    public static event Action OnSceneChangeOrWorldStartGenerate;
    internal static void _InvokeOnSceneChangeOrWorldStartGenerate() {
        OnSceneChangeOrWorldStartGenerate?.Invoke();
    }

    void Update2s() {
        while (SERVER_JOIN_SECRETS.Count < 4) {
            SERVER_JOIN_SECRETS.Add(Guid.NewGuid().ToString("N"));
        }
        SERVER_JOIN_SECRETS.Add(Guid.NewGuid().ToString("N"));

        while (SERVER_JOIN_SECRETS.Count > 12) {
            SERVER_JOIN_SECRETS.RemoveAt(0);
            SERVER_JOIN_SECRETS.RemoveAt(0);
        }
    }

    void Start() {
        InvokeRepeating(nameof(Update2s), 0.1f, 2);

        if (!Plugin.plrname_override) {
#if RELEASE
            {
                var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastPlayerName");
                if (!String.IsNullOrWhiteSpace(s)) {
                    UIMainMenu.USERINPUT_NAME = s;
                }
            }
            {
                var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastPlayerColor");
                if (!String.IsNullOrWhiteSpace(s) && ColorUtility.TryParseHtmlString(s, out var color)) {
                    UIMainMenu.SetInputColor(color);
                }
            }
#endif
        }

        if (String.IsNullOrEmpty(Net.MyServerInfo.name)) {
            Net.MyServerInfo.name = "Expedition " + UIMainMenu.USERINPUT_NAME;
#if RELEASE
            var s = PlayerPrefs.GetString("KrokoshaMultiplayer_LastMyLobbyName");
            if (!String.IsNullOrWhiteSpace(s)) {
                Net.MyServerInfo.name = s;
            }
#endif
        }

        if (!ScavPlayer.CheckIfPlrColorIsValid(UIMainMenu.LAST_VALID_INPUT_COLOR)) {
            log.l("Picked random color cuz its not picked.");
            UIMainMenu.SetInputColor(ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED[new System.Random().Next(0, ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED.Length)]);
        }
    }

    void Update() {
        Net.DoUpdate();

        if (ConsoleScript.instance?.active == true) {
            Con.UpdatePlayerListAutocomplete();
        }

#if !RELEASE
        if (Util.IsInWoundView()) {
            var limb = PlayerCamera.main.selectedLimb;
            if (limb != null) {
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad0)) {
                    var n = UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -10f : 10f;
                    limb.bleedAmount += n;
                    limb.skinHealth -= n;
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad1)) {
                    limb.shrapnel += UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -1 : 1;
                    if (limb.shrapnel < 0) {
                        limb.shrapnel = 0;
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad2)) {
                    if (limb.broken) {
                        limb.MendBone();
                    } else {
                        limb.BreakBone();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad3)) {
                    if (limb.dislocated) {
                        limb.UnDislocate();
                        limb.dislocationTimer = 0;
                    } else {
                        limb.Dislocate();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad4)) {
                    if (limb.dismembered) {
                        limb.dismembered = false;
                        limb.gameObject.SetActive(value: true);
                    } else {
                        limb.Dismember();
                    }
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
                if (UnityEngine.Input.GetKeyDown(KeyCode.Keypad5)) {
                    var n = UnityEngine.Input.GetKeyDown(KeyCode.LeftShift) ? -10f : 10f;
                    limb.infectionAmount += n;
                    PlayerCamera.main.woundView.GetComponent<WoundView>().UpdateView();
                }
            }
        }
#endif
    }

    public static bool AdditionalApprovalCheck(ref NetDataReader reader, string plrname, ref string reject_reason) {
        if (reject_reason == null) {
            reject_reason = "<key:deny_connection_rejected>";
        }

        if (Util.IsInWorld() && !rules.LateJoinAllowed) {
            reject_reason = "<key:deny_game_in_progress>";
            return false;
        }

        return true;
    }

    public static string FULL_VERSION_TAG { get; private set; }

    public static void DoMultiplayerStatusMessageLog(string msg) {
        lastMultiplayerStatusMessageChangeTime = Time.realtimeSinceStartupAsDouble;
        multiplayerStatusMessage = msg;
        log.l("Status: " + msg);
    }

    public static void DoMultiplayerStatusMessageError(string msg) {
        lastMultiplayerStatusMessageChangeTime = Time.realtimeSinceStartupAsDouble;
        multiplayerStatusMessage = msg;
        ModLog.Error(LogDomain.Lobby, "Status ERROR: " + msg);
    }

    public static void SetServerName(string newname) {
        if (newname.Length > Net.MAX_SERVER_NAME_LENGTH) {
            newname = newname.Substring(0, Net.MAX_SERVER_NAME_LENGTH);
        }

        if (newname != Net.MyServerInfo.name) {
            newname = Multiplayer.SanitizeRichTextAllowSimpleTags(newname);
            if (newname == Net.MyServerInfo.name) return;

            Net.MyServerInfo.name = newname;
            if (!Net.IsRunning || Net.IsServer) {
                Net.CurrentServerInfo.name = newname;
            }

            if (IsNetworkActiveAndIsServer()) {
                ServerMain._Server_OnServerNameChange();
            }
        }
    }

    public static void SetServerDescription(string newdesc) {
        if (newdesc.Length > Net.MAX_SERVER_DESC_LENGTH) {
            newdesc = newdesc.Substring(0, Net.MAX_SERVER_DESC_LENGTH);
        }
        if (newdesc.CountIndices('\n') > Net.MAX_SERVER_DESC_NEWLINES) {
            newdesc = newdesc.Substring(0, newdesc.LastIndexOf('\n'));
        }

        if (newdesc != Net.MyServerInfo.description) {
            newdesc = Multiplayer.SanitizeRichTextAllowSimpleTags(newdesc);
            if (newdesc == Net.MyServerInfo.description) return;

            Net.MyServerInfo.description = newdesc;
            if (!Net.IsRunning || Net.IsServer) {
                Net.CurrentServerInfo.description = newdesc;
            }

            if (IsNetworkActiveAndIsServer()) {
                ServerMain._Server_OnServerNameChange();
            }
        }
    }

    public static string SanitizeTextInputAllowSpaces(string s) {
        if (string.IsNullOrEmpty(s)) {
            return string.Empty;
        }
        return Regex.Replace(s, "[^a-zA-Z0-9_#$()+=\\- ]", "").TrimStart();
    }

    /// <summary>
    /// 原版 <c>v5.5.3.0</c> 纯 ASCII 输入清洗契约（保留供跨版本握手安全名构建与协议兼容校验使用，严禁放宽字符集）。
    /// </summary>
    public static string SanitizeTextInput(string s) {
        if (string.IsNullOrEmpty(s)) {
            return string.Empty;
        }
        return Regex.Replace(s, "[^a-zA-Z0-9_#$()+=-]", "");
    }

    /// <summary>
    /// 清洗玩家昵称等单行无空格 Unicode 文本（Issue #40 / #35）：
    /// 剔除控制字符与富文本尖括号 <c>'&lt;'</c>、<c>'&gt;'</c>，允许全部 Unicode 字母/数字（中文、俄文、日韩文等）及常用安全符号，默认截断至 24 字符。
    /// </summary>
    public static string SanitizeUnicodeTextInput(string s, int maxLength = MAX_NAME_LENGTH) {
        return SanitizeUnicodeCore(s, allowSpaces: false, maxLength: maxLength);
    }

    /// <summary>
    /// 清洗允许空格的 Unicode 文本（如房间名）：额外保留空格并去除前导空格，按 <paramref name="maxLength"/> 截断。
    /// </summary>
    public static string SanitizeUnicodeTextInputAllowSpaces(string s, int maxLength = MAX_NAME_LENGTH) {
        return SanitizeUnicodeCore(s, allowSpaces: true, maxLength: maxLength);
    }

    private static string SanitizeUnicodeCore(string s, bool allowSpaces, int maxLength) {
        if (string.IsNullOrEmpty(s) || maxLength <= 0) {
            return string.Empty;
        }

        StringBuilder sb = new StringBuilder(Math.Min(s.Length, maxLength));
        for (int i = 0; i < s.Length; i++) {
            char c = s[i];
            if (char.IsControl(c) || c == '<' || c == '>') {
                continue;
            }
            if (allowSpaces && c == ' ') {
                if (sb.Length == 0) {
                    continue; // 等价于 TrimStart(' ')，确保截断前先剥离前导空格
                }
                sb.Append(c);
            } else if (IsAllowedUnicodeNameSymbol(c)) {
                sb.Append(c);
            }

            if (sb.Length >= maxLength) {
                break;
            }
        }
        return sb.ToString();
    }

    private static bool IsAllowedUnicodeNameSymbol(char c) {
        if (char.IsLetterOrDigit(c)) {
            return true;
        }
        return c == '_' || c == '#' || c == '$' || c == '(' || c == ')'
            || c == '+' || c == '=' || c == '-' || c == '.';
    }

    /// <summary>
    /// 判断字符串是否包含任意非 ASCII 字符（码点 &gt; 127）。
    /// </summary>
    public static bool ContainsNonAscii(string s) {
        if (string.IsNullOrEmpty(s)) {
            return false;
        }
        for (int i = 0; i < s.Length; i++) {
            if (s[i] > 127) {
                return true;
            }
        }
        return false;
    }

    /// <summary>
    /// 校验清洗前后的玩家昵称合法性：含非 ASCII 字符（如 1~2 字中文名）允许 <c>Length &gt;= 1</c>，纯 ASCII 仍保持原版 <c>Length &gt;= 3</c>。
    /// </summary>
    public static bool IsValidPlayerName(string name, out string denyReason) {
        if (string.IsNullOrEmpty(name)) {
            denyReason = Lang.Get("net_name_too_short");
            return false;
        }
        string sanitized = SanitizeUnicodeTextInput(name);
        if (!string.Equals(name, sanitized, StringComparison.Ordinal)) {
            denyReason = Lang.Get("net_invalid_name");
            return false;
        }
        int minLen = ContainsNonAscii(name) ? 1 : 3;
        if (name.Length < minLen) {
            denyReason = Lang.Get("net_name_too_short");
            return false;
        }
        denyReason = null;
        return true;
    }

    /// <summary>
    /// 构建 100% 兼容 Steam 原版 <c>v5.5.3.0</c> 房主握手校验（<c>SanitizeTextInput(name) == name &amp;&amp; Length &gt;= 3</c>）的确定性 ASCII 握手名。
    /// 若原始名字本身即为合法原版 ASCII 名（3..16 字符），原样返回；否则提取合法 ASCII 前缀拼接 <c>"Plr_"</c> 与 4 位无符号稳定哈希十六进制。
    /// </summary>
    public static string BuildAsciiHandshakeSafeName(string rawName) {
        string clean = SanitizeUnicodeTextInput(rawName);
        if (!string.IsNullOrEmpty(clean)
            && clean.Length >= 3
            && clean.Length <= LEGACY_ASCII_MAX_NAME_LENGTH
            && string.Equals(clean, SanitizeTextInput(clean), StringComparison.Ordinal)) {
            return clean;
        }

        string hashSource = !string.IsNullOrEmpty(clean) ? clean : (rawName ?? string.Empty);
        string asciiPrefix = SanitizeTextInput(hashSource);
        if (asciiPrefix.Length > 11) {
            asciiPrefix = asciiPrefix.Substring(0, 11);
        }

        uint stableHash = (uint)hashSource.StableHash();
        string hex4 = (stableHash & 0xFFFFu).ToString("X4");
        string safeName = string.IsNullOrEmpty(asciiPrefix)
            ? ("Plr_" + hex4)
            : (asciiPrefix + "_Plr_" + hex4);

        if (safeName.Length > MAX_NAME_LENGTH) {
            safeName = safeName.Substring(0, MAX_NAME_LENGTH);
        }
        return safeName;
    }

    public static string SanitizeRichText(string s) {
        StringBuilder b = new(s.Length);
        bool inside = false;
        foreach (char c in s) {
            if (c == '<') {
                inside = true;
            } else if (inside) {
                inside = c != '>';
            } else {
                b.Append(c);
            }
        }
        return b.ToString();
    }

    public static string SanitizeRichTextAllowSimpleTags(string s) {
        if (string.IsNullOrWhiteSpace(s))
            return "";

        StringBuilder b = new(s.Length);
        int i = 0;
        while (i < s.Length) {
            if (s[i] != '<') {
                b.Append(s[i++]);
                continue;
            }

            int end = s.IndexOf('>', i + 1);
            if (end == -1) {
                b.Append(s[i++]);
                continue;
            }

            string tag = s.Substring(i + 1, end - i - 1).Trim();
            if (tag is "b" or "/b" or "i" or "/i" or "s" or "/s") {
                b.Append('<').Append(tag).Append('>');
            } else if (tag.StartsWith("color=", StringComparison.OrdinalIgnoreCase)) {
                string value = tag.Substring(6).Trim();
                if (Regex.IsMatch(value, @"^(?:[a-zA-Z]+|#[0-9a-fA-F]{6}(?:[0-9a-fA-F]{2})?)$")) {
                    b.Append("<color=").Append(value).Append('>');
                }
            } else if (tag.Equals("/color", StringComparison.OrdinalIgnoreCase)) {
                b.Append("</color>");
            }

            i = end + 1;
        }

        return b.ToString();
    }

    public const string CANT_PLAY_SP_MSG = "You can't play singleplayer with MP mod active.\nTo play singleplayer go to \"MP Mod Menu > Settings > General > Deactivate Multiplayer Mod\"\nOr start an empty server.";

    public static void ShutdownNetwork() {
        UIInGame.StopSpectatorMode();
        GoBackToMainMenuIfImClientAndInGame();
        WorldMP.RunUnchippedLock.Clear();
        Net.ShutdownReset();
    }

    public static void _JustDisconnect() {
        UIInGame.StopSpectatorMode();
        WorldMP.RunUnchippedLock.Clear();
        Net.ShutdownReset();
    }

    readonly static float OG_dislocationHealSpeed = Limb.dislocationHealSpeed;
    readonly static float OG_boneHealSpeed = Limb.boneHealSpeed;

    public static void ApplyGameRules() {
        Limb.dislocationHealSpeed = OG_dislocationHealSpeed + OG_dislocationHealSpeed * rules.AdditionalHealthRegen;
        Limb.boneHealSpeed = OG_boneHealSpeed + OG_boneHealSpeed * rules.AdditionalHealthRegen;

        foreach (var fld in typeof(MultiplayerGameRules).GetFields()) {
            var ogvalue = fld.GetValue(Multiplayer.rules);

            if (fld.HasAttribute(typeof(MultiplayerRuleByteAttribute))) {
                var attr = fld.GetAttribute<MultiplayerRuleByteAttribute>();
                TypedReference rulesref = __makeref(Multiplayer.rules);

                byte p = (byte)ogvalue;
                if (p < attr.min) {
                    p = attr.min;
                    fld.SetValueDirect(rulesref, p);
                }
                if (p > attr.limit) {
                    p = attr.limit;
                    fld.SetValueDirect(rulesref, p);
                }
            }
        }

        // 钳制到有效采样率数组末位下标 Length - 1，消除索引越界崩溃
        if (Voicechat.STANDART_FREQUENCIES != null && Voicechat.STANDART_FREQUENCIES.Length > 0) {
            if (rules.VoicechatQuality >= Voicechat.STANDART_FREQUENCIES.Length) {
                rules.VoicechatQuality = (byte)(Voicechat.STANDART_FREQUENCIES.Length - 1);
            }
            Voicechat.SAMPLE_RATE = Voicechat.STANDART_FREQUENCIES[rules.VoicechatQuality];
        }

        if (Util.IsInWorld()) {
            try {
                var txt = PlayerCamera.main.timescaleText;
                if (txt != null) {
                    txt.transform.parent.gameObject.SetActive(rules.EnableTimeManipulation);
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.UI, "TimeManipulation UI switch ERROR", e);
            }
        }

        Plugin.s.SendMessage("KrokoshaSingletonEvent_OnRulesUpdate", SendMessageOptions.DontRequireReceiver);

        // 中途加入者会接收开局捕获的快照包，需保持其中规则字段与当前实时规则同步
        if (Net.IsServer) {
            ServerMain.StartGameAnnouncementPacket packet = ServerMain.LastStartGameAnnouncementPacket;
            packet.rules = rules;
            ServerMain.LastStartGameAnnouncementPacket = packet;
        }
    }

    public static bool IsNetworkActiveAndIsServer() => Net.IsRunning && Net.IsServer;
    public static bool IsNetworkActiveAndIsClient() => Net.IsRunning && Net.IsClient;
    public static bool IsNetworkActiveOrIsInGame() => Net.IsRunning || Util.IsInWorld();
    public static bool IsNetworkActiveAndIsInGame() => Net.IsRunning && Util.IsInWorld();
    public static bool IsNetworkActiveAndIsWorldGenerated() => Net.IsRunning && Util.IsWorldGenerated();

    public static void GoBackToMainMenuIfImClientAndInGame() {
        if (Net.IsClient && Util.IsInWorld()) {
            DoMultiplayerStatusMessageLog("Went back to main menu because disconnected.");
            Multiplayer.showMultiplayerMenu = true;
            PlayerCamera.main.ToMainMenu();
        }
    }

    public static void ResetNetworkManager() {
#if MP_GAMEMODES
        GamemodeManager.DeleteGamemode();
#endif
        GoBackToMainMenuIfImClientAndInGame();
        Net.ShutdownReset();
    }

    public delegate void HandleMessageDelegate(knetid senderClientId, LiteNetLib.Utils.NetDataReader messagePayload);

    internal static List<Delegate> all_postinit_from_attributes = new();
    internal static Dictionary<NetworkMessageReceiverAttribute, HandleMessageDelegate> all_network_receivers_from_attributes = new();

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static List<PluginInfo> GetBepinPlugins() {
        var l = BepInEx.Bootstrap.Chainloader.PluginInfos.Values.ToList();
        var toremove_guids = new string[] {
            MyPluginInfo.PLUGIN_GUID,
            "dannad.krmultiupdater",
        };
        l.RemoveAll(x => x.Metadata == null || toremove_guids.Contains(x.Metadata.GUID));
        return l;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static string[] GetModGUIDs() {
        return GetBepinPlugins().Select(x => x.Metadata.GUID).ToArray();
    }

    static void _MpOnSceneChangeOrWorldStartGenerate() {
        try {
            if (Util.IsInWorld()) {
                if (WorldGeneration.world != null) {
                    log.l($"SCENE CHANGE: Is In World,  Layer: {WorldGeneration.world.biomeDepth}");
                } else {
                    log.l("SCENE CHANGE: Is In World,  No world?");
                }
            } else {
                log.l("SCENE CHANGE: Is In Main Menu ");
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Bootstrap, "OnSceneChangeOrWorldStartGenerate Scene log", e);
        }

        foreach (var i in ScavPlayer.ClientIdToPlayerDict) {
            if (i.Value.ServerPlayerState != null) {
                i.Value.ServerPlayerState.OnSceneChangeOrWorldStartGenerate();
            }
            i.Value.LateJoin = false;
        }
        RadiationLine_Update_MultiplayerPatch.timeGone = 0;
        ServerMain._ded_server_switch_counter = -20;

        if (MoodleManager.main != null) {
            MoodleManager.main.body = Util.GetLocalBody();
        }

        if (Net.Transport is TransportLiteNetLib tlnl) {
            tlnl.NetManager.Statistics.Reset();
        }

        var state = ClientMain.ServerWorldState;
        if (state != null) {
            state.ambientLightIntensity.Value = WorldGeneration_UpdateAmbientLight_MultiplayerPatch.GetRunSettingsAmbientLightIntensity();
        }
    }
}
