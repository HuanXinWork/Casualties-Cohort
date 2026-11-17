using CasualtiesTogetherUtils;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Experimental.Rendering.Universal;

namespace Together;

public partial class Con {
    /// <summary>
    /// 注册官方诊断上报排障命令（<c>mp_debug_hud</c>/<c>mp_report</c>/<c>mp_log_domain</c>）以及客户端本地显示、音频与开发调试命令。
    /// </summary>
    private static void RegisterDiagnosticsAndClientCommands(ConsoleScript con) {
        // 1. 局内状态透视窗与输入阻塞源实时诊断命令
        RegisterCommand_LocalOnly(new Command(
            "mp_debug_hud",
            "MP - Toggle diagnostics overlay HUD and print active input block flags.",
            (string[] splited) => {
                MpDebugOverlay.Visible = splited.Length > 1
                    ? ParseBool01(splited[1])
                    : !MpDebugOverlay.Visible;
                InputBlockFlags blocks = MpDebugOverlay.EvaluateInputBlocks();
                con.LogToConsole($"[Diagnostics] MpDebugOverlay.Visible = {MpDebugOverlay.Visible} | InputBlocks = {blocks}");
            },
            null,
            ("bool", "optional, toggle if omitted")));

        // 2. 一键脱敏诊断报告生成命令（同时输出 JSON 与人类可读 TXT 路径）
        RegisterCommand_LocalOnly(new Command(
            "mp_report",
            "MP - Generate sanitized JSON + TXT diagnostic report immediately.",
            (string[] splited) => {
                string argText = splited.Length > 1 ? string.Join(" ", splited.Skip(1)) : null;
                MpDiagnosticReport report = MpDiagnosticReporter.GenerateReport("ConsoleManualReport", userNote: argText);
                con.LogToConsole($"[Diagnostics] Report #{report.ReportId} saved:\n  TXT : {report.SavedSummaryPath}\n  JSON: {report.SavedJsonPath}");
            },
            null,
            ("string note", "optional user note attached to report")));

        // 3. 结构化子系统日志域查看与开关命令
        List<string> domainAutofill = new List<string> { "all", "none", "console" };
        foreach (string name in Enum.GetNames(typeof(LogDomain))) {
            if (!string.Equals(name, nameof(LogDomain.None), StringComparison.OrdinalIgnoreCase)
                && !string.Equals(name, nameof(LogDomain.All), StringComparison.OrdinalIgnoreCase)) {
                domainAutofill.Add(name);
            }
        }

        RegisterCommand_LocalOnly(new Command(
            "mp_log_domain",
            "MP - Inspect or toggle ModLog.EnabledDebugDomains per subsystem.",
            (string[] splited) => {
                if (splited.Length < 2) {
                    var sb = new StringBuilder();
                    sb.AppendLine($"[Diagnostics] Build={(ModLog.IsDebugBuild ? "DEBUG" : "RELEASE")} | ShowInConsole={ModLog.ShowInConsole} | Mask={ModLog.EnabledDebugDomains}");
                    foreach (LogDomain dom in Enum.GetValues(typeof(LogDomain))) {
                        if (dom == LogDomain.None || dom == LogDomain.All) {
                            continue;
                        }
                        bool on = (ModLog.EnabledDebugDomains & dom) != 0;
                        sb.AppendLine($"  - {dom,-12}: {(on ? "ON" : "OFF")}");
                    }
                    con.LogToConsole(sb.ToString().TrimEnd());
                    return;
                }

                string token = splited[1].Trim();
                if (string.Equals(token, "all", StringComparison.OrdinalIgnoreCase)) {
                    ModLog.EnabledDebugDomains = LogDomain.All;
                    con.LogToConsole("[Diagnostics] ModLog.EnabledDebugDomains = All");
                    return;
                }
                if (string.Equals(token, "none", StringComparison.OrdinalIgnoreCase)) {
                    ModLog.EnabledDebugDomains = LogDomain.None;
                    con.LogToConsole("[Diagnostics] ModLog.EnabledDebugDomains = None");
                    return;
                }
                if (string.Equals(token, "console", StringComparison.OrdinalIgnoreCase)) {
                    ModLog.ShowInConsole = splited.Length > 2 ? ParseBool01(splited[2]) : !ModLog.ShowInConsole;
                    con.LogToConsole($"[Diagnostics] ModLog.ShowInConsole = {ModLog.ShowInConsole}");
                    return;
                }

                if (!Enum.TryParse<LogDomain>(token, ignoreCase: true, out LogDomain targetDomain)
                    || targetDomain == LogDomain.None
                    || targetDomain == LogDomain.All) {
                    throw new Exception($"Unknown log domain \"{token}\". Valid domains: {string.Join(", ", domainAutofill)}");
                }

                bool enable = splited.Length > 2
                    ? ParseBool01(splited[2])
                    : (ModLog.EnabledDebugDomains & targetDomain) == 0;

                if (enable) {
                    ModLog.EnabledDebugDomains |= targetDomain;
                } else {
                    ModLog.EnabledDebugDomains &= ~targetDomain;
                }
                con.LogToConsole($"[Diagnostics] LogDomain.{targetDomain} = {(enable ? "ON" : "OFF")} | Active={ModLog.EnabledDebugDomains}");
            },
            new Dictionary<int, List<string>> { { 0, domainAutofill } },
            ("string domain", "Subsystem name / all / none / console"),
            ("bool state", "optional, enable/disable or omit to toggle")));

        RegisterCommand(new Command("chatspy", "MP- force to show and log all chat messages", (string[] splited) => {
            Chat._DEV_LOG_CHAT = splited.Length < 2 ? !Chat._DEV_LOG_CHAT : ParseBool01(splited[1]);
            _DEV_CHATSPY = Chat._DEV_LOG_CHAT;

            if (!CanCheat()) {
                _DEV_CHATSPY = false;
                log.l($"_DEV_LOG_CHAT = {Chat._DEV_LOG_CHAT}");
            } else {
                log.l($"_DEV_CHATSPY = {_DEV_CHATSPY} ");
            }
        }, null));

        RegisterCommand_LocalOnly(new Command("zoom", "MP - zoom camera", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);
            con.CheckForWorld();

            if (!float.TryParse(splited[1], NumberStyles.Float, CultureInfo.InvariantCulture, out float target)) {
                ConsoleScript.instance.LogToConsole("Invalid float value for zoom.");
                return;
            }
            if (target < 0.001f) {
                throw new ArgumentOutOfRangeException(nameof(target), "Zoom can't be <= 0.");
            }
            if (Mathf.Abs(target) < 1f && !CanCheat()) {
                target = 1f;
            }

            Camera.main.orthographicSize = (Camera.main.orthographicSize * _DEV_ZOOM) / target;
            PixelPerfectCamera ppc = PlayerCamera.main.GetComponent<PixelPerfectCamera>();
            ppc.assetsPPU = (int)(target * 8f);

            _DEV_ZOOM = target;
            ConsoleScript.instance.LogToConsole($"zoom {_DEV_ZOOM} orthographicSize={Camera.main.orthographicSize} PPU={ppc.assetsPPU}");
        }, null, ("float zoom", "the bigger the number, the closer you see")));

        RegisterCommand_LocalOnly(new Command("spectate", "MP - spectator mode", (string[] splited) => {
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !UIInGame.SpectatorModeActive;
            if (target) {
                ConFailIfCheatsDisabled();
                UIInGame.StartSpectatorMode();

                if (splited.Length > 2) {
                    GameObject obj = GetObjectOrBodyForCommand(splited[2]);
                    if (obj != null) {
                        UIInGame.Spectator_SetTarget(obj.transform);
                    }
                }
            } else {
                UIInGame.StopSpectatorMode();
            }
            con.LogToConsole($"Spectator mode: {UIInGame.SpectatorModeActive}");
        }, null));

        RegisterCommand(new Command("mpopenmenu", "MP", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !UIMainMenu.mainmenu_open;
            UIMainMenu.SetOpen(target);
            ConsoleScript.instance.LogToConsole($"UIMainMenu.SetOpen({target});");
        }, null));

        RegisterCommand_LocalOnly(new Command("novisioneffect", "MP", (string[] splited) => {
            ref bool val = ref _DEV_NO_VISION_EFFECT;
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !val;
            if (target) {
                ConFailIfCheatsDisabled();
            }
            val = target;
            ConsoleScript.instance.LogToConsole($"novisioneffect {val}");
        }, null));

        RegisterCommand_LocalOnly(new Command("telekinesis", "MP dev", (string[] splited) => {
            ref bool val = ref _DEV_TELEKINESIS;
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !val;
            if (target) {
                ConFailIfNetworkIsRunningAndIsClient();
                ConFailIfCheatsDisabled();
            }
            val = target;
            ConsoleScript.instance.LogToConsole($"telekinesis {val}");
        }, null));

        RegisterCommand_LocalOnly(new Command("musicvolume", "MP", (string[] splited) => {
            float layer = splited.Length > 1 ? con.ParseFloat(splited[1]) : 0f;
            MusicManager_Start_MultiplayerPatch.musicvolume = layer;
            ConsoleScript.instance.LogToConsole($"custom_music_volume {MusicManager_Start_MultiplayerPatch.musicvolume}");
        }, null, ("float", "volume level 0-1")));

        RegisterCommand_LocalOnly(new Command("audio-only-on-focus", "MP - vc test", (string[] splited) => {
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !_DEV_AUDIO_ONLY_ON_FOCUS;
            if (_DEV_AUDIO_ONLY_ON_FOCUS != target) {
                if (target) {
                    _DEV_LAST_VOLUME = AudioListener.volume;
                } else {
                    AudioListener.volume = _DEV_LAST_VOLUME;
                }
            }
            _DEV_AUDIO_ONLY_ON_FOCUS = target;
            ConsoleScript.instance.LogToConsole($"audio_only_on_focus {_DEV_AUDIO_ONLY_ON_FOCUS}");
        }, null));

        RegisterCommand_LocalOnly(new Command("vclistenervolume-individual", "MP - mute or change volume of a specific player, local", (string[] splited) => {
            con.CheckArgumentCount(splited, 2);
            string plridentifier = splited[1];
            float newvolume = Mathf.Max(0f, con.ParseFloat(splited[2]));

            try {
                if (TryGetPlayerFromPartialName(plridentifier, out ScavPlayer plr, null)) {
                    plr.vc_output.custom_volume_set = newvolume;
                    con.LogToConsole($"Set vc volume {plr} to {newvolume}");
                } else {
                    con.LogToConsole($"Player does not exist: {plridentifier}");
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "vclistenervolume-individual", e);
            }
        }, null, ("player", ""), ("float", "volume level 0-1")));

        RegisterCommand_LocalOnly(new Command("win32setwindowtitle", "MP dev", (string[] splited) => {
            string msg = string.Join(" ", splited.Skip(1));
            win32windowtitlechanger.SetTitle(msg);
            con.LogToConsole($"win32windowtitlechanger.SetTitle(\"{msg}\")");
        }, null, ("string", "message")));

        RegisterCommand(new Command("loadcustommusic", "MP-SERVER- force reload all custom music right now", (string[] splited) => {
            ConFailIfNetworkIsRunningAndIsClient();
            MP3PlayerServerAudioStreamer.Server_ForceLoadMusic();
        }, null));

        RegisterCommand_LocalOnly(new Command("printposition", "MP", (string[] splited) => {
            con.CheckArgumentCount(splited, 1);
            Vector2 pos = con.ParsePosition(splited[1]);
            log.l($"pos {splited[1]} ->\t {pos}");
        }, null, ("position", "")));

        RegisterCommand_LocalOnly(new Command("mpdumplocale", "MP - dump locale of multiplayer mod", (string[] splited) => {
            var text = new StringBuilder("// KROKOSHA CO-OP MOD LOCALE DUMP:\n");
            if (splited.Length > 1) {
                string lang = splited[1];
                text.AppendLine("\n// " + lang.ToSafeString());
                var lan = new Language();
                var copy = new Dictionary<string, string>(Lang.dict[lang]);
                foreach (var i in copy) {
                    lan.other[Lang.LocalPrefixTag + i.Key] = i.Value;
                }
                text.Append(JsonConvert.SerializeObject(lan, Formatting.Indented));
            } else {
                foreach (var item in Lang.dict) {
                    var lan = new Language();
                    var copy = new Dictionary<string, string>(item.Value);
                    foreach (var i in copy) {
                        lan.other[Lang.LocalPrefixTag + i.Key] = i.Value;
                    }
                    text.AppendLine("\n// " + item.Key);
                    text.Append(JsonConvert.SerializeObject(lan, Formatting.Indented));
                }
            }
            text.AppendLine("\n// END\n// COPY IT FROM LOG FILE INSTEAD OF CONSOLE BECAUSE IT CLEARS FORMATTING HERE");
            log.l(text.ToString());
        }, null));

        RegisterCommand_LocalOnly(new Command("mpverbose", "MP dev - Enable verbose logging and debug screens", (string[] splited) => {
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !Multiplayer.verbose;
            DebugMenuSettings.is_verbose = target;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = target;
            DebugMenuSettings._DEV_ENABLE_STEAM_LOG = target;
            log.l($"DEV : verbose = {Multiplayer.verbose} ");
        }, null));

#if FULLGAME
        RegisterCrystalDiagnosticCommands();
#endif

        RegisterCommand_LocalOnly(new Command("syncinfo", "MP dev - Show debug sync info on objects. (DEPRECATED)", (string[] splited) => {
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING;
            NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = target;
            log.l($"NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING = {NetObjectRegistry._DEV_ENABLE_SYNCINFO_SNITCHING} ");
        }, null));

        RegisterCommand(new Command("syncreset", "MP dev - Unregister all objects.", (string[] splited) => {
            if (Net.IsClient) {
                return;
            }
            ConFailIfNetworkNotRunning();

            if (Net.Transport != null && Net.Transport is TransportLiteNetLib tlnl) {
                tlnl.NetManager.Statistics.Reset();
            }

            bool deleteInventory = splited.Length > 1 && con.ParseBool(splited[1]);
            if (deleteInventory) {
                NetObjectRegistry.UnregisterAllObjects();
                log.l("NetObjectRegistry.UnregisterAllObjects();");
            } else {
                NetObjectRegistry.UnregisterAllObjectsButKeepInventory();
                log.l("NetObjectRegistry.UnregisterAllObjectsButKeepInventory();");
            }
        }, null, ("bool", "1 keep inventory, 0 for simple")));

        RegisterCommand(new Command("syncregisterall", "MP dev - Register all objects in the world. (IT LAGS !!!!!)", (string[] splited) => {
            ConFailIfNetworkNotRunning();
            var objects = GameObject.FindObjectsOfType<Item>().Select(x => x.gameObject)
                .Union(GameObject.FindObjectsOfType<BuildingEntity>().Select(x => x.gameObject));
            foreach (GameObject item in objects) {
                if (NetObjectRegistry.ObjectShouldBeSynced(item)) {
                    NetObjectRegistry.NewGO(item);
                }
            }
            log.l("called NetObjectRegistry.NewGO on everything");
        }, null));

        RegisterCommand_LocalOnly(new Command("chatboxpos", "MP - set position and size of chat box (everything is a scale in float (0-1)) (OBSOLETE)", (string[] splited) => {
            if (splited.Length == 1) {
                Chat.SETTING_posX = 1f;
                Chat.SETTING_posY = 1f;
                Chat.SETTING_sizeX = 1f;
                Chat.SETTING_sizeY = 1f;
                Chat.SETTING_scale = 1f;
                con.LogToConsole("Chatbox: Reset chatbox location.");
                return;
            }

            con.CheckArgumentCount(splited, 5);
            Chat.SETTING_posX = Mathf.Clamp01(con.ParseFloat(splited[1]));
            Chat.SETTING_posY = Mathf.Clamp01(con.ParseFloat(splited[2]));
            Chat.SETTING_sizeX = con.ParseFloat(splited[3]);
            Chat.SETTING_sizeY = con.ParseFloat(splited[4]);
            Chat.SETTING_scale = con.ParseFloat(splited[5]);

            con.LogToConsole("Chatbox: SETTING NEW PARAMETERS:");
            con.LogToConsole($"Chatbox: setting_posX  = {(Chat.SETTING_posX * 100f)}%");
            con.LogToConsole($"Chatbox: setting_posY  = {(Chat.SETTING_posY * 100f)}%");
            con.LogToConsole($"Chatbox: setting_sizeX = {(Chat.SETTING_sizeX * 100f)}%");
            con.LogToConsole($"Chatbox: setting_sizeY = {(Chat.SETTING_sizeY * 100f)}%");
            con.LogToConsole($"Chatbox: setting_scale = {(Chat.SETTING_scale * 100f)}%");
        }, null, ("float", "X pos"), ("float", "Y pos"), ("float", "X size"), ("float", "Y size"), ("float", "scale")));

        RegisterCommand_LocalOnly(new Command("chatpreview", "MP dev - toggle chat text preview (OBSOLETE)", (string[] splited) => {
            ref bool val = ref Chat._DEV_ENABLE_PREVIEW;
            bool target = splited.Length > 1 ? ParseBool01(splited[1]) : !val;
            val = target;
            con.LogToConsole($"Chat._DEV_ENABLE_PREVIEW = {val}");
        }, null, ("bool", "")));

        RegisterCommand_LocalOnly(new Command("screensize", "MP dev - set game window resolution", (string[] splited) => {
            Screen.SetResolution(width: con.ParseInt(splited[1]), height: con.ParseInt(splited[2]), fullscreenMode: FullScreenMode.Windowed);
        }, null));
    }

#if FULLGAME
    private static void RegisterCrystalDiagnosticCommands() {
        RegisterCommand_LocalOnly(new Command("dumpcrystals", "MP - dump crystal size/color/effects fingerprints", (string[] splited) => {
            int limit = 32;
            if (splited.Length > 1) {
                int.TryParse(splited[1], out limit);
                if (limit < 1) {
                    limit = 1;
                }
            }
            string side = Net.IsServer ? (Net.IsClient ? "listen" : "host") : "client";
            CrystalBehaviour[] crystals = UnityEngine.Object.FindObjectsOfType<CrystalBehaviour>();
            var lines = new StringBuilder();
            lines.AppendLine($"[CRYSTALDUMP] begin side={side} count={crystals.Length} limit={limit}");
            var rows = new List<(float dist, string line, bool special)>();
            Vector2 origin = PlayerCamera.main != null && PlayerCamera.main.body != null
                ? (Vector2)PlayerCamera.main.body.transform.position
                : Vector2.zero;

            for (int i = 0; i < crystals.Length; i++) {
                CrystalBehaviour c = crystals[i];
                if (c == null) {
                    continue;
                }
                knetid syncId = default;
                bool hasSync = NetObjectRegistry.TryGetSyncInfo(c.gameObject, out var si) && si != null;
                if (hasSync) {
                    syncId = si.syncId;
                }
                var effects = CrystalAuthority.GetEffects(c);
                int effectCount = effects != null ? effects.Count : 0;
                var effectNames = new List<string>();
                if (effects != null) {
                    for (int e = 0; e < effects.Count; e++) {
                        if (effects[e] != null) {
                            effectNames.Add(effects[e].GetType().Name);
                        }
                    }
                }
                Color32 sc = c.sprite != null ? (Color32)c.sprite.color : (Color32)Color.white;
                Color32 lc = c.light != null ? (Color32)c.light.color : sc;
                float intensity = c.light != null ? c.light.intensity : -1f;
                bool volume = c.light != null && c.light.volumeIntensityEnabled;
                float vol = volume ? c.light.volumeIntensity : 0f;
                bool heater = c.GetComponent<Heater>() != null;
                int fireKids = 0;
                int distortKids = 0;
                for (int ch = 0; ch < c.transform.childCount; ch++) {
                    string n = c.transform.GetChild(ch).name;
                    if (n.Contains("CrystalFire")) {
                        fireKids++;
                    }
                    if (n.Contains("CrystalDistort")) {
                        distortKids++;
                    }
                }
                Vector2 pos = c.transform.position;
                float dist = Vector2.Distance(origin, pos);
                string line =
                    $"[CRYSTALDUMP] side={side} sync={(hasSync ? syncId.ToString() : "none")} pos={pos.x:F1},{pos.y:F1} scaleX={c.transform.localScale.x:F2} size={c.crystalSize:F3} " +
                    $"spr=#{sc.r:X2}{sc.g:X2}{sc.b:X2}a{sc.a} light=#{lc.r:X2}{lc.g:X2}{lc.b:X2} i={intensity:F2} vol={(volume ? 1 : 0)}:{vol:F2} " +
                    $"heater={(heater ? 1 : 0)} fire={fireKids} distort={distortKids} effects={effectCount}:[{string.Join(",", effectNames)}]";
                rows.Add((dist, line, effectCount > 0));
            }

            rows.Sort((a, b) => a.dist.CompareTo(b.dist));
            int special = 0;
            int written = 0;
            for (int i = 0; i < rows.Count && written < limit; i++) {
                lines.AppendLine(rows[i].line);
                if (rows[i].special) {
                    special++;
                }
                written++;
            }
            lines.AppendLine($"[CRYSTALDUMP] end side={side} written={written} special={special}");
            string text = lines.ToString();
            log.l(text, false);
            try {
                string dir = Path.Combine(Application.persistentDataPath, "crystal_sync");
                Directory.CreateDirectory(dir);
                string path = Path.Combine(dir, $"dump_{side}.txt");
                File.WriteAllText(path, text);
                log.l($"[CRYSTALDUMP] end side={side} written={written} special={special} file={path}", false);
                log.l($"[CRYSTALDUMP] wrote {written} crystals -> {path}");
            } catch (Exception e) {
                log.warn($"[CRYSTALDUMP] file write failed: {e.Message}");
            }
        }, null));

        RegisterCommand_LocalOnly(new Command("comparecrystals", "MP - compare crystal_sync dump_host/dump_client", (string[] splited) => {
            string dir = Path.Combine(Application.persistentDataPath, "crystal_sync");
            string hostPath = Path.Combine(dir, "dump_host.txt");
            string listenPath = Path.Combine(dir, "dump_listen.txt");
            string clientPath = Path.Combine(dir, "dump_client.txt");
            if (!File.Exists(hostPath) && File.Exists(listenPath)) {
                hostPath = listenPath;
            }
            if (!File.Exists(hostPath) || !File.Exists(clientPath)) {
                log.warn($"[CRYSTALDUMP] compare missing files host={File.Exists(hostPath)} client={File.Exists(clientPath)} dir={dir}");
                return;
            }
            var lineRe = new Regex(
                @"sync=(?<sync>\S+)\s+pos=(?<px>[-\d\.]+),(?<py>[-\d\.]+)\s+scaleX=(?<sx>[-\d\.]+)\s+size=(?<size>[-\d\.]+)\s+spr=(?<spr>#\w+)\s+light=(?<light>#\w+)\s+i=(?<i>[-\d\.]+).*?fire=(?<fire>\d+)\s+distort=(?<distort>\d+)\s+effects=(?<effects>\d+:\[[^\]]*\])");
            const float MatchRadius = 1.5f;

            List<(float x, float y, string fp, string label, float intensity, string spr)> Parse(string path) {
                var list = new List<(float x, float y, string fp, string label, float intensity, string spr)>();
                foreach (string raw in File.ReadAllLines(path)) {
                    Match m = lineRe.Match(raw);
                    if (!m.Success) continue;
                    if (!float.TryParse(m.Groups["px"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float px)) continue;
                    if (!float.TryParse(m.Groups["py"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float py)) continue;
                    float.TryParse(m.Groups["i"].Value, NumberStyles.Float, CultureInfo.InvariantCulture, out float intensity);
                    string spr = m.Groups["spr"].Value;
                    string fp = $"spr={spr} size={m.Groups["size"].Value} fire={m.Groups["fire"].Value} distort={m.Groups["distort"].Value} effects={m.Groups["effects"].Value}";
                    string label = Mathf.RoundToInt(px * 10f) + "_" + Mathf.RoundToInt(py * 10f);
                    list.Add((px, py, fp, label, intensity, spr));
                }
                return list;
            }

            var host = Parse(hostPath);
            var client = Parse(clientPath);
            int matched = 0;
            int mismatched = 0;
            int hostOnly = 0;
            int clientOnly = 0;
            int clientOnlyBarren = 0;
            var report = new StringBuilder();
            report.AppendLine($"[CRYSTALDUMP] compare begin host={host.Count} client={client.Count} (by pos+-{MatchRadius:0.#})");
            bool[] hostUsed = new bool[host.Count];
            bool[] clientUsed = new bool[client.Count];
            float r2 = MatchRadius * MatchRadius;

            for (int hi = 0; hi < host.Count; hi++) {
                var h = host[hi];
                int best = -1;
                float bestD2 = float.MaxValue;
                for (int ci = 0; ci < client.Count; ci++) {
                    if (clientUsed[ci] || h.fp != client[ci].fp) continue;
                    float dx = h.x - client[ci].x;
                    float dy = h.y - client[ci].y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= r2 && d2 < bestD2) {
                        bestD2 = d2;
                        best = ci;
                    }
                }
                if (best >= 0) {
                    hostUsed[hi] = true;
                    clientUsed[best] = true;
                    matched++;
                }
            }

            for (int hi = 0; hi < host.Count; hi++) {
                if (hostUsed[hi]) continue;
                var h = host[hi];
                int best = -1;
                float bestD2 = float.MaxValue;
                for (int ci = 0; ci < client.Count; ci++) {
                    if (clientUsed[ci]) continue;
                    float dx = h.x - client[ci].x;
                    float dy = h.y - client[ci].y;
                    float d2 = dx * dx + dy * dy;
                    if (d2 <= r2 && d2 < bestD2) {
                        bestD2 = d2;
                        best = ci;
                    }
                }
                if (best < 0) {
                    hostOnly++;
                    continue;
                }
                hostUsed[hi] = true;
                clientUsed[best] = true;
                mismatched++;
                report.AppendLine($"[CRYSTALDUMP] MISMATCH pos={h.label}~{client[best].label} host=[{h.fp}] client=[{client[best].fp}]");
            }

            for (int ci = 0; ci < client.Count; ci++) {
                if (clientUsed[ci]) continue;
                bool barren = client[ci].fp.Contains("effects=0:[]")
                    || (client[ci].spr.StartsWith("#FFFFFF", StringComparison.OrdinalIgnoreCase) && client[ci].intensity < 0f);
                if (barren) {
                    clientOnlyBarren++;
                    report.AppendLine($"[CRYSTALDUMP] CLIENT_ONLY_BARREN pos={client[ci].label} [{client[ci].fp}]");
                } else {
                    clientOnly++;
                    report.AppendLine($"[CRYSTALDUMP] CLIENT_ONLY pos={client[ci].label} [{client[ci].fp}]");
                }
            }

            bool pass = mismatched == 0 && matched >= 8 && clientOnly == 0;
            string summary = $"[CRYSTALDUMP] compare end matched={matched} mismatched={mismatched} hostOnly={hostOnly} clientOnly={clientOnly} barren={clientOnlyBarren} PASS={(pass ? 1 : 0)}";
            report.AppendLine(summary);
            string text = report.ToString();
            log.l(text, false);
            log.l(summary);
            try {
                File.WriteAllText(Path.Combine(dir, "compare.txt"), text);
            } catch (Exception e) {
                log.warn($"[CRYSTALDUMP] compare write failed: {e.Message}");
            }
        }, null));
    }
#endif
}
