using BepInEx;
using BepInEx.Logging;
using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Runtime.InteropServices;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together {

    [BepInProcess("CasualtiesUnknown.exe")]
    [BepInPlugin(MyPluginInfo.PLUGIN_GUID, MyPluginInfo.PLUGIN_NAME, MyPluginInfo.PLUGIN_VERSION)]
    public partial class Plugin : BaseUnityPlugin {
        internal static ManualLogSource log;
        internal static new ManualLogSource Logger => log;
        internal const string FORCEDISABLE_PLAYERPREF_KEY = "CasualtiesMP_FORCE_DISABLE_MP_MOD";
        public static Plugin main { get; private set; }

        internal Harmony harmony;

        public static readonly string[] TARGET_GAME_VERSION = new string[] {
            #if FULLGAME
            "1.0.2",
            #else
            "7.0.1",
            #endif
        };
        public static readonly string MOD_VERSION = BuildInfo.MOD_VERSION;
        public static string FULL_MOD_VERSION { get; internal set; }

        Plugin() {
            FULL_MOD_VERSION = BuildInfo.MOD_VERSION;
        }

#if DEBUG
        public readonly static bool IS_DEBUG_BUILD = true;
#else
        public readonly static bool IS_DEBUG_BUILD = false;
#endif
#if TESTING
        public readonly static bool IS_TESTING_BUILD = true;
#else
        public readonly static bool IS_TESTING_BUILD = false;
#endif
#if RELEASE
        public readonly static bool IS_RELEASE_BUILD = true;
#else
        public readonly static bool IS_RELEASE_BUILD = false;
#endif
#if FULLGAME
        public readonly static bool IS_FULLGAME_BUILD = true;
#else
        public readonly static bool IS_FULLGAME_BUILD = false;
#endif

        public static GameObject s;

        public readonly static bool OG_RUNINGACKGROUND = false;

        public static bool SUCCESFULLY_INITIALIZED { get; private set; }
        public static bool FORCE_DISABLE_MP_MOD { get; private set; }
        public static bool FORCE_NO_STEAM { get; private set; }
        public static bool AnyCmdArgsPassed { get; private set; }

        public static event Action OnInitFinish;

        internal class LoadErrorNotifier : MonoBehaviour {
            public static string errorstr = "";
            void Start() {
                KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER.ON_CLICK_WILL_ENABLE_THE_MOD = false;
                this.GetOrAddComponent<KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER>();
            }
            void OnGUI() {
                if (GameObject.FindObjectOfType<Body>()) {
                    return;
                }
                GUI.Label(new Rect(20, 20, Screen.width, Screen.height), $"CO-OP MOD v{Plugin.FULL_MOD_VERSION} failed to load!!!!!! \n\n{errorstr}");
            }
        }

        public static bool IsExeSameAsInSteam() {
            if (!KSteam.Loaded) {
                return false;
            }

            string installdir = KSteam.GetAppInstallDir();

            if (installdir.IsNullOrWhiteSpace()) {
                return false;
            }

            string exePath = Process.GetCurrentProcess().MainModule.FileName;
            bool contains = exePath.Contains(installdir);

            return contains;
        }

        static bool TryStartFromSteam() {
            if (!KSteam.Loaded) {
                return false;
            }

            if (KSteam.STEAM_APPID == KSteam.CONST_STEAMAPPID_CASUALTIESUNKNOWN_DEMO
                || KSteam.STEAM_APPID == KSteam.CONST_STEAMAPPID_CASUALTIESUNKNOWN_PLAYTEST
            ) {
                if (!IsExeSameAsInSteam()) {
                    return false;
                }

                Application.Quit();
                Process.Start(new ProcessStartInfo {
                    FileName = $"steam://run/{KSteam.STEAM_APPID}",
                    UseShellExecute = true
                });
                return true;
            }
            return false;
        }

        public static void RestartGame() {
            try {
#if !DONT_RESTART_IF_RESTART_BULLSHIT
                try {
                    if (TryStartFromSteam()) {
                        Together.log.l("Relaunching the game through steam!");
                        return;
                    }
                } catch (System.Exception e) {
                    Together.log.error("STEAM RESTART FAILED " + e.ToString());
                }
#endif
            } catch (System.Exception e3) {
                UnityEngine.Debug.LogError("GAME RESTART FAILED:" + e3.ToString());
            }

            Application.Quit();
        }

        private void Awake() {
            main = this;
            s = this.gameObject;

            SUCCESFULLY_INITIALIZED = false;

#if !RELEASE
            Application.runInBackground = true;
            DebugMenuSettings.is_verbose = true;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
            Chat._DEV_LOG_CHAT = true;
#endif

            // to prevent it from being deleted by unity, same as the HideManagerGameObject in config, but i do it forcefully
            gameObject.hideFlags = HideFlags.HideAndDontSave;

            log = base.Logger;
            ModLog.Init(Logger);

            try {
                Awake_Stage1(); // that later calls stage 2
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, nameof(Awake_Stage1), e);
            }
            OnInitFinish?.Invoke();
        }

        void Awake_Stage1() {
            try {
                FORCE_DISABLE_MP_MOD = PlayerPrefsExtended.GetBool(FORCEDISABLE_PLAYERPREF_KEY, FORCE_DISABLE_MP_MOD);
            } catch (Exception e) {
                log.LogError("PlayerPrefs: FORCE_DISABLE_MP_MOD: LOADING: " + e.ToString());
            }
            CheckCommandlineArgsForThisMod();

            try {
                PlayerPrefsExtended.SetBool(FORCEDISABLE_PLAYERPREF_KEY, FORCE_DISABLE_MP_MOD);
                PlayerPrefs.Save();
                PlayerPrefsExtended.SetBool("KrokoshaCasualtiesMP_FORCE_DISABLE_MP_MOD", true);
            } catch (Exception e) {
                log.LogError("PlayerPrefs: FORCE_DISABLE_MP_MOD: SAVING: " + e.ToString());
            }

            try {
                Lang.InternalLoadLocale();
                Lang.InitializeLanguageConfig(Config);
            } catch (Exception e) {
                log.LogError("Lang.InternalLoadLocale FATAL: " + e.ToString());
            }

            if (FORCE_DISABLE_MP_MOD) {
                log.LogInfo("Multiplayer Mod Self-Destruct activated.");
                try {
                    this.AddComponent<KrokoshaCasualtiesMP_DISABLED_STATE.KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER>();
                } catch (Exception e) {
                    log.LogError("MP-SWITCH BUTTON CREATION: " + e.ToString());
                }
                try {
                    Destroy(this);
                } catch (Exception e) {
                    log.LogError("FORCE_DISABLE_MP_MOD: !!! KILLING MYSELF FAILED !!!: " + e.ToString());
                }
                return;
            }

            try {
                Awake_Stage2();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, nameof(Awake_Stage2), e);
                LoadErrorNotifier.errorstr += "\n" + e.ToString();
                AppendNonAsciiPathHintIfNeeded();
            }

            // Issue #16 兜底防线：只要存在 Harmony 补丁失败或阶段初始化未完成，确保挂载 LoadErrorNotifier 显露启动错误提示
            if (_harmonyPatchFailed || (!SUCCESFULLY_INITIALIZED && !string.IsNullOrWhiteSpace(LoadErrorNotifier.errorstr))) {
                this.GetOrAddComponent<LoadErrorNotifier>();
                Application.runInBackground = false;
            }
        }

        void _PickUsername() {
            try {
                if (KSteam.Loaded) {
                    string steamName = Multiplayer.SanitizeUnicodeTextInput(KSteam.GetLocalUsername());
                    if (!string.IsNullOrEmpty(steamName)) {
                        UIMainMenu.USERINPUT_NAME = steamName;
                        return;
                    }
                }
            } catch (Exception e) {
                log.LogError("_PickName: " + e.ToString());
            }
            UIMainMenu.USERINPUT_NAME = "Player" + UnityEngine.Random.Range(0, 9999).ToString();
        }

        void Awake_Stage2() {
            // 初始化结构化双模日志门面与脱敏异常自动上报中心
            ModLog.Init(Logger);
            MpDiagnosticReporter.Install();

#if PLAYTEST
            DebugMenuSettings.is_verbose = true;
            DebugMenuSettings._DEV_VISUALISE_NET_EVENTS = true;
            Chat._DEV_LOG_CHAT = true;
#endif

            harmony = new Harmony(MyPluginInfo.PLUGIN_GUID);
            OptimizerMain.Log = Logger;

            if (!TARGET_GAME_VERSION.Contains(Application.version)) {
                LoadErrorNotifier.errorstr += $"Mismatching version detected!";

                Together.log.error(LoadErrorNotifier.errorstr);

                LoadErrorNotifier.errorstr += "\n";
            }

            try {
                DoHarmonyPatchAll(harmony);
                if (_harmonyPatchFailed) {
                    this.GetOrAddComponent<LoadErrorNotifier>();
                    Application.runInBackground = false;
                    return;
                }
            } catch (Exception e) {
                Together.log.error($"Plugin {MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load! Make sure you have correct version!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();
                AppendNonAsciiPathHintIfNeeded();

                this.GetOrAddComponent<LoadErrorNotifier>();
                Application.runInBackground = false;

                return;
            }
            Application.runInBackground = true;

            log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} version {Plugin.FULL_MOD_VERSION} is loaded!");
            log.LogInfo($"Game version: \"{Application.version}\"");

            KSteam.CheckSteam();

            UIMainMenu._USE_STEAM_MENU = KSteam.Loaded;

            if (!plrname_override) {
                _PickUsername();
            }

#if !RELEASE
            Multiplayer.INPUT_PASSWORD = "AMOGUS";
#endif

            try {
                CoopModAssets.LoadAssets();
            } catch (Exception e) {
                Together.log.error($"{MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load ASSETS!!!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();

                this.GetOrAddComponent<LoadErrorNotifier>();
                return;
            }

            log.LogInfo($"Plugin {MyPluginInfo.PLUGIN_GUID} assets loaded!");

            Multiplayer mainsingleton = this.AddComponent<Multiplayer>();

            try {
                Multiplayer._FIRST_INIT();
                log.LogInfo($"Created multiplayer singleton GameObject!");
            } catch (Exception e) {
                Together.log.error($"{MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load!\n\n{e}\n");
                LoadErrorNotifier.errorstr += "\n" + e.ToString();

                this.GetOrAddComponent<LoadErrorNotifier>();
                Component.Destroy(mainsingleton);
                return;
            }

            SUCCESFULLY_INITIALIZED = true;

            GlobalDark_Awake_MultiplayerPatch.SetBetaBuildMPText();

            try {
                OptimizerMain.Install();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "OptimizerMain.Install", e);
            }

            try {
                FindTheBugInator.Install();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "FindTheBugInator.Install", e);
            }

            try {
                UI.TeammateList.TeammateListHost.Init(this);
            } catch (Exception e) {
                ModLog.Exception(LogDomain.UI, "TeammateListHost.Init", e);
            }

            base.StartCoroutine(DelayedDelayedTestGameStartImmidiate());
        }

        private bool _harmonyPatchFailed;

        private static void AppendNonAsciiPathHintIfNeeded() {
            string exePath = BepInEx.Paths.ExecutablePath;
            if (!string.IsNullOrEmpty(exePath) && exePath.Any(c => c > 127)) {
                string hint = Lang.Get("loaderror_possible_nonascii_path");
                if (LoadErrorNotifier.errorstr == null || !LoadErrorNotifier.errorstr.Contains(exePath, StringComparison.Ordinal)) {
                    LoadErrorNotifier.errorstr += $"\n\n{hint}\n{exePath}\n";
                }
            }
        }

        /// <summary>
        /// 逐类应用 Harmony 补丁并按致命阈值分级处置：
        /// 非 ASCII 游戏路径下的补丁异常（Issue #16 典型症状）或大面积补丁崩溃（&gt;= 10 组）触发熔断并挂载 <see cref="LoadErrorNotifier"/>；
        /// 纯 ASCII 路径下的极个别非致命兼容补丁失配仅记录详细错误日志，不阻断模组主流程初始化。
        /// </summary>
        void DoHarmonyPatchAll(Harmony harmony) {
            _harmonyPatchFailed = false;
            List<Type> patchTypes = GetHarmonyPatchesInNamespaces("Together", "CasualtiesMultiplayerAPI", "CasualtiesTogetherUtils");

            int failedCount = 0;
            Type firstFailedType = null;
            Exception firstException = null;

            foreach (Type patch in patchTypes) {
                try {
                    harmony.PatchAll(patch);
                } catch (Exception ex) {
                    failedCount++;
                    if (firstException == null) {
                        firstFailedType = patch;
                        firstException = ex;
                    }
                    ModLog.Exception(LogDomain.Bootstrap, $"DoHarmonyPatchAll({patch.FullName})", ex);
                }
            }

            if (failedCount > 0) {
                string summary = $"Harmony failed to apply {failedCount}/{patchTypes.Count} patch group(s). First failure in {firstFailedType?.FullName}:\n{firstException}";
                bool hasNonAsciiExePath = !string.IsNullOrEmpty(BepInEx.Paths.ExecutablePath) && BepInEx.Paths.ExecutablePath.Any(c => c > 127);
                if (hasNonAsciiExePath || failedCount >= 10) {
                    _harmonyPatchFailed = true;
                    Together.log.error($"Plugin {MyPluginInfo.PLUGIN_GUID} {Plugin.FULL_MOD_VERSION} failed to load! Make sure you have correct version!\n\n{summary}\n");
                    LoadErrorNotifier.errorstr += "\n" + summary;
                    AppendNonAsciiPathHintIfNeeded();
                } else {
                    log.LogError($"[Harmony] {summary} (non-fatal under ASCII path; continuing mod initialization)");
                }
            } else {
                log.LogInfo($"Harmony successfully applied {patchTypes.Count}/{patchTypes.Count} patch group(s).");
            }
        }

        public static List<Type> GetHarmonyPatchesInNamespaces(params string[] namespaces) {
            return Assembly.GetExecutingAssembly()
                .GetTypesSafely()
                .Where(t =>
                    t.Namespace != null &&
                    namespaces.Any(ns =>
                        t.Namespace == ns ||
                        t.Namespace.StartsWith(ns + ".", StringComparison.Ordinal)) &&
                    t.IsDefined(typeof(HarmonyPatch), false))
                .ToList();
        }

        void MultiplayerUnpatch() {
            MpDiagnosticReporter.Uninstall();
            if (harmony != null) {
                try {
                    harmony.UnpatchSelf();
                    harmony = null;
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Core, nameof(MultiplayerUnpatch), e);
                }
            }
            if (Multiplayer.Singletons != null) {
                foreach (Component item in Multiplayer.Singletons) {
                    if (item != null) {
                        Component.Destroy(item);
                    }
                }
                Multiplayer.Singletons.Clear();
            } else {
                Multiplayer.Singletons = new();
            }
            if (this.TryGetComponent(out Multiplayer mp)) {
                Component.Destroy(mp);
            }
        }

        public static new void StartCoroutine(IEnumerator f) {
            s.GetComponent<Multiplayer>().StartCoroutine(f);
        }

        private void Start() {
            Invoke(nameof(DelayedCoroutineToSkipScavIntro), 0.5f);
            log.LogInfo($"Invoked main {MyPluginInfo.PLUGIN_GUID} function, 1s...");
        }

        private void OnDestroy() {
            MpDiagnosticReporter.Uninstall();
            try {
                OptimizerMain.Shutdown();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "Plugin.OnDestroy.OptimizerShutdown", e);
            }
            try {
                Net.ShutdownReset();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Transport, "Plugin.OnDestroy.NetShutdown", e);
            }
            Together.log.l("Plugin.OnDestroy()");
        }

        public static void SkipMainMenuIntro() {
            try {
                Plugin.SkipWarningScreen();
                ScrollableText.ForceClose();
            } catch (Exception e) {
                Together.log.error(e);
            }
        }

        public static void SkipWarningScreen() {
            GameObject o = GameObject.Find("Canvas/Warning");
            if (o != null && o.activeSelf) {
                Plugin.log.LogInfo("Removing warning screen! ");
                o.SetActive(false);
            }
        }

        private void DelayedCoroutineToSkipScavIntro() {
#if !RELEASE
            SkipWarningScreen();
#endif
        }

        private void Update() {
            using (FindTheBugInator.Time(FindTheBugInator.Plugin)) {
                TitleScreenFpsCap.Tick();
                PlayerCheckpoint.Tick();
                CraftCommit.Tick();
                MoveCommit.Tick();
                TorchWet.Tick();
            }
            using (FindTheBugInator.Time(FindTheBugInator.Chunks)) {
                ChunkSleep.Tick();
                BagSleep.Tick();
                HeaterMultiplayerRewrite.Tick();
            }
            using (FindTheBugInator.Time(FindTheBugInator.Opt)) {
                OptimizerMain.Tick();
            }

            using (FindTheBugInator.Time(FindTheBugInator.Roster)) {
                UI.TeammateList.TeammateListHost.Tick();
            }
#if FULLGAME
            using (FindTheBugInator.Time(FindTheBugInator.Crystals)) {
                CrystalAuthority.ClientTickBlankResync();
                ConcurrentEndings.TickLocalAscent();
                BlueHeartMp.Server_EnsureAllMissingCargoOnAscent();
            }
#endif
        }

        private void FixedUpdate() {
            using (FindTheBugInator.Time(FindTheBugInator.FixedStep)) {
                BagSleep.WriteHost();
                FallCatch.Tick();
            }
        }

        private void LateUpdate() {
            using (FindTheBugInator.Time(FindTheBugInator.Late)) {
                OptimizerMain.LateTick();
                ChunkSleep.LateFly();
            }
        }
    }

    public static class win32windowtitlechanger {
        public static bool changed_the_title = false;
        static IntPtr hwnd = IntPtr.Zero;

        public static void SetTitle(string newname) {
            if (hwnd == IntPtr.Zero) {
                hwnd = Process.GetCurrentProcess().MainWindowHandle;
            }
            if (hwnd == IntPtr.Zero) {
#if TESTING
                log.error($"ERROR FAILED TO CHANGE THE WINDOWTEXT, WINDOW NOT FOUND!!!! WHAT !!!?!?: {Process.GetCurrentProcess()}");
#endif
            } else {
#if TESTING
                log.l($"win32windowtitlechanger: Set name to: {newname}");
#endif
                SetWindowText(hwnd, newname);
                changed_the_title = true;
            }
        }

        [DllImport("user32.dll", EntryPoint = "SetWindowText", CharSet = CharSet.Unicode)]
        static extern bool SetWindowText(System.IntPtr hwnd, System.String lpString);
    }
}

namespace KrokoshaCasualtiesMP_DISABLED_STATE {
    internal class KrokoshaCasualtiesMP_DISABLED_BUTTON_HANDLER : MonoBehaviour {
        internal static bool ON_CLICK_WILL_ENABLE_THE_MOD = true;
        GameObject MP_LINK_BUTTON = null;
        public static Sprite mpmod_icon = null;
        void Awake() {
            try {
                mpmod_icon = Together.CoopModAssets.LoadSprite("mp.png");
            } catch (Exception ex) {
                doerror(ex.ToString());
            }
#if !DONT_RESTART_IF_RESTART_BULLSHIT
            try {
                Together.KSteam.CheckSteam();
            } catch (Exception ex) {
                doerror("Attempt to load steam" + ex.ToString());
            }
#endif
            Application.runInBackground = false;
        }

        void DoTheNormalButton() {
            if (MP_LINK_BUTTON == null) {
                PreRunScript prerun = PreRunScript.instance;
                if (prerun != null) {
                    try {
                        Transform credits = prerun.transform.Find("Credits");
                        Transform trans = prerun.transform.Find("Credits/TranslationRepo");
                        if (trans != null) {
                            MP_LINK_BUTTON = GameObject.Instantiate(trans.gameObject, prerun.transform, false).gameObject;
                            MP_LINK_BUTTON.name = "KROKMP_BUTTON_LINK";

                            RectTransform rt = MP_LINK_BUTTON.GetComponent<RectTransform>();
                            rt.anchoredPosition = new Vector2(50, rt.anchoredPosition.y);
                            rt.SetSiblingIndex(credits.GetSiblingIndex());

                            UITooltip tt = MP_LINK_BUTTON.GetComponent<UITooltip>();

                            string tool = ON_CLICK_WILL_ENABLE_THE_MOD ? "mainmenu_mp_button_tooltip_lastresort_enable_mod" : "mainmenu_mp_button_tooltip_lastresort_disable_mod";

                            tt.localeName = Together.Lang.LocalPrefixTag + tool;
                            tt.skipLocale = true;

                            tt.tipName = Together.Lang.Get(tool);

                            Image img = MP_LINK_BUTTON.GetComponent<Image>();
                            img.sprite = mpmod_icon;
                            img.color = Color.red;

                            Button button = MP_LINK_BUTTON.GetComponent<UnityEngine.UI.Button>();
                            button.onClick.RemoveAllListeners();
                            button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                            button.onClick.AddListener(() => {
                                PlayerPrefsExtended.SetBool(Together.Plugin.FORCEDISABLE_PLAYERPREF_KEY, !ON_CLICK_WILL_ENABLE_THE_MOD);

                                try {
                                    PlayerPrefs.Save();
                                    Together.log.l($"SET {Together.Plugin.FORCEDISABLE_PLAYERPREF_KEY} => {ON_CLICK_WILL_ENABLE_THE_MOD}");
                                } catch (System.Exception e) {
                                    doerror("PlayerPrefs.Save failed: " + e.Message);
                                }

                                Together.Plugin.RestartGame();
                            });
                        }
                    } catch (System.Exception e) {
                        doerror("DoTheNormalButton failure " + e.ToString());
                    }
                }
            }
        }
        void Update() {
            if (mpmod_icon == null) {
                return;
            }

            try {
                DoTheNormalButton();
            } catch (Exception ex) {
                doerror(ex.ToString());
            }
        }
        int errorcounter = 0;
        void doerror(string str) {
            if (errorcounter < 3) {
                errorcounter++;

                str = "KrokoshaCasualtiesMP_DISABLED: " + str + "\nFULL STACKTRACE:\n" + new StackTrace();

                if (Together.Plugin.log != null) {
                    Together.Plugin.log.LogError(str);
                } else {
                    print(str);
                }
            }
        }
    }
}
