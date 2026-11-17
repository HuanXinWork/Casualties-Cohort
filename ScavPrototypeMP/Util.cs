using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Threading.Tasks;
using TMPro;
using Together;
using Together.MpConstants;
using UnityEngine;

namespace CasualtiesTogetherUtils;

public static partial class Util {

    public static WorldGeneration world => WorldGeneration.world;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;

    #region SOUND_STUFF

    public static AudioSource PlayWorldSoundOnScreenIfInRange(in string snd, in Vector2 pos, in float volume = 1, in float pitch = 1, in float range = 64) {
        if (KM.DistanceCheck(pos, Camera.main.transform.position, range)) {
            var s = Sound.Play(snd, pos, false, true, null, volume, pitch);
            if (s != null) {
                s.maxDistance = range;
                s.minDistance = range * 0.4f;
                s.rolloffMode = AudioRolloffMode.Linear;
                return s;
            }
        }
        return null;
    }

    #endregion

    #region LAMBDA_STUFF

    public static ulong GetFileHash(string filePath) {
        const ulong offsetBasis = 14695981039346656037;
        const ulong prime = 1099511628211;

        ulong hash = offsetBasis;
        byte[] buffer = new byte[8192];

        using (FileStream stream = File.OpenRead(filePath)) {
            int bytesRead;
            while ((bytesRead = stream.Read(buffer, 0, buffer.Length)) > 0) {
                for (int i = 0; i < bytesRead; i++) {
                    hash ^= buffer[i];
                    hash *= prime;
                }
            }
        }

        return hash;
    }


    public static void CallLambdaWhenWorldGenerates(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhenWorldGenerated(func));
    }

    static IEnumerator _CoroutineCallLambdaWhenWorldGenerated(Delegate func) {
        while (!Util.IsWorldGenerated()) { yield return null; }

        func.DynamicInvoke();
    }

    public static void CallLambdaWhenWorldLoads(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(() => {
            return Util.IsInWorld() && worldBlocks != null;
        }, func));
    }
    public static void CallLambdaWhenWorldFirstGenerates(Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(Util.IsWorldGenerated, func));
    }

    public static void CallLambdaWhen(Func<bool> condition, Delegate func) {
        StartCoroutine(_CoroutineCallLambdaWhen(condition, func));
    }

    public static void CallLambdaWhen(Func<bool> condition, Delegate func, float check_repeat_time = 0.1f) {
        StartCoroutine(_CoroutineCallLambdaWhen(condition, func, check_repeat_time));
    }

    internal static IEnumerator _CoroutineCallLambdaWhen(Func<bool> condition, Delegate func) {
        while (!condition.Invoke()) { yield return null; }
        func.DynamicInvoke();
    }
    private static IEnumerator _CoroutineCallLambdaWhen(Func<bool> condition, Delegate func, float check_repeat_time) {
        while (!condition.Invoke()) { yield return new WaitForSecondsRealtime(check_repeat_time); }
        func.DynamicInvoke();
    }

    public static void DelayCallLambda(float delay_sec, Delegate func) {
        StartCoroutine(_DelayCallLambdaCoroutine(delay_sec, func));
    }

    internal static IEnumerator _DelayCallLambdaCoroutine(float delay_sec, Delegate func) {
        yield return new WaitForSecondsRealtime(delay_sec);

        func.DynamicInvoke();
    }


    private class CoroutineRunner : MonoBehaviour {
        static CoroutineRunner cr = null;
        public static CoroutineRunner get() {
            if (cr == null) {
                var go = new GameObject("KrokoshaCoroutineRunner");
                DontDestroyOnLoad(go);
                cr = go.AddComponent<CoroutineRunner>();
                go.hideFlags = HideFlags.HideAndDontSave;
            }
            return cr;
        }
        public static void run(IEnumerator f) => get().StartCoroutine(f);
    }


    public static void StartCoroutine(Func<IEnumerator> f) => CoroutineRunner.run(f());
    public static void StartCoroutine(IEnumerator f) => CoroutineRunner.run(f);


    public static Task StartCoroutineAsync(IEnumerator coroutine) {
        var tcs = new TaskCompletionSource<object>();
        CoroutineRunner.get().StartCoroutine(RunCoroutineForAsync(coroutine, tcs));
        return tcs.Task;
    }
    private static IEnumerator RunCoroutineForAsync(IEnumerator coroutine, TaskCompletionSource<object> tcs) {
        yield return coroutine;

        if (!tcs.Task.IsCompleted) {
            tcs.SetResult(null);
        }
    }

    #endregion

    #region GAME_SCENE_STATE

    public static bool IsWorldGenerated() {
        if (WorldGeneration.world && !WorldGeneration.world.generatingWorld) {
            return true;
        }
        return false;
    }
    public static bool IsWorldInstantiated() {
        if (WorldGeneration.world && !WorldGeneration.world.instantiatingWorld) {
            return true;
        }
        return false;
    }
    public static bool IsGeneratingWorld() {
        if (WorldGeneration.world) {
            return WorldGeneration.world.generatingWorld;
        }
        return false;
    }
    public static bool IsTutorialWorld() {
        if (WorldGeneration.world != null && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial) {
            return true;
        }
        return false;
    }
    public static bool IsInWorld() {
        if (WorldGeneration.world != null) {
            return true;
        }
        return false;
    }
    public static bool IsInMainMenu() {
        if (WorldGeneration.world == null) {// or (PreRunScript.instance != null)
            return true;
        }
        return false;
    }
    public static bool IsInPauseMenuOrMainMenu() {
        if (IsInWorld()) {
            if (PauseHandler.main != null) {
                if (PauseHandler.main.isPaused) {
                    return true;
                }
                return false;
            }
        }
        return true;
    }
    public static bool IsInPauseMenu() {
        if (PauseHandler.main != null) {
            if (PauseHandler.main.isPaused) {
                return true;
            }
            return false;
        }
        return false;
    }
    public static bool IsInGameAndPauseMenu() {
        if (IsInWorld()) {
            if (PauseHandler.main != null) {
                if (PauseHandler.main.isPaused) {
                    return true;
                }
                return false;
            }
        }
        return false;
    }

    [Obsolete("Use OpenPauseMenu")]
    public static void OpenBrightnessPanel(bool open_or_nah) => OpenPauseMenu(open_or_nah);
    public static void OpenPauseMenu(bool open_or_nah) {
        if (PauseHandler.main != null && PauseHandler.main.isPaused != open_or_nah) {
            PauseHandler.TogglePause();
        }
    }

    public static void OpenSettingsPanel(bool open_or_nah) {
        OpenSettingsPanel(open_or_nah, 0);
    }
    public static void OpenSettingsPanel(bool open_or_nah, Setting.SettingCategory category) {
        OpenSettingsPanel(open_or_nah, (int)category);
    }
    public static void OpenSettingsPanel(bool open_or_nah, int category) {
        if (open_or_nah) {
            if (SettingsMenu.instance == null) {
                SettingsMenu.OpenMenu(Util.IsInMainMenu() ? PreRunScript.instance.mainCanvas.transform : PauseHandler.main.transform);
            }
            SettingsMenu_Start_Patch.forceOpenCustomTab = true;
            if (SettingsMenu.instance.spawnedSettings == null) {
                SettingsMenu.instance.spawnedSettings = new List<GameObject>();
            }
            SettingsMenu.instance.SelectTab(category);
        } else {
            if (SettingsMenu.instance != null) {
                SettingsMenu.instance.Close();
            }
        }
    }
    public static bool IsSettingsOpen() {
        return SettingsMenu.instance != null;
    }
    public static Setting.SettingCategory GetSettingsPanelOpenedCategory() {
        return (Setting.SettingCategory)SettingsMenu_SelectTab_Patch.currentSelectedTab;
    }
    #endregion

    #region KEYBINDS

    public static void RegisterKeybind(string key, KeyCode code) {
        Settings_DefaultSettings_Patch.custom_binds[key] = code;
    }

    #endregion

    #region LOCAL_BODY_STUFF
    public static Vector2 GetLocalBodyPosition() {
        if (TryGetLocalBody(out var b)) {
            return b.transform.position;
        }
        return Camera.main.transform.position;
    }
    public static bool IsLocalBodyAlive() {
        if (TryGetLocalBody(out var body)) {
            return body.alive;
        }
        return false;
    }
    public static Body GetLocalBody() {
        return PlayerCamera.main.body;
    }
    public static Body GetLocalBodyNullable() {
        if (PlayerCamera.main == null) {
            return null;
        }
        return PlayerCamera.main.body;
    }
    public static bool TryGetLocalBody(out Body body) {
        if (PlayerCamera.main == null) {
            body = null;
            return false;
        }
        body = PlayerCamera.main.body;
        return body != null;
    }
    public static bool IsBodyLocal(Body body) {
        return PlayerCamera.main?.body == body;
    }
    public static bool IsBodyLocal(Limb limb) {
        return limb.body.IsBodyLocal();
    }
    #endregion

    public static bool IsUnchipped() {
        if (Net.IsRunning && WorldMP.RunUnchippedLock.Locked && !Multiplayer.rules.UnchippedIsIndividual) {
            return WorldMP.RunUnchippedLock.GlobalUnchipped;
        }
        if (Util.IsInWorld()) {
            return WorldGeneration.unchipped;
        }
        // main menu: PreRunScript.runSettings is often still null during Plugin.Awake
        var settings = WorldMP.runSettings;
        if (settings == null) {
            return false;
        }
        return (bool)settings.GetValueOrDefault("unchipped", false);
    }
    public static void SetUnchipped(bool unchipped) {
        if (WorldMP.RunUnchippedLock.BlocksLocalChange(unchipped)) {
            return;
        }
        if (IsInWorld()) {
            WorldGeneration.world.SetUnchipped(unchipped);
        } else {
            if (!Multiplayer.rules.UnchippedIsIndividual) {
                WorldMP.runSettings["unchipped"] = unchipped;
            }
        }
    }

    #region UI_UTILS

    public static string UnitsDistance(float meters) {
#if FULLGAME
        return $"{Mathf.RoundToInt((float)Units.ConvertDistance(meters))}{Units.DistanceUnit()}";
#else
        return $"{Mathf.RoundToInt(meters)}m";
#endif
    }


    public static void DoAlert(in string text, in bool important = false) => PlayerCamera.main.DoAlert(text, important);

    //OG: PlayerCamera.PlayUISound V6.0-pre8   but this one is static so it can be used in main menu
    static Dictionary<PlayerCamera.UISoundType, string> _PlayUISound_NAMES = new Dictionary<PlayerCamera.UISoundType, string> {
        { PlayerCamera.UISoundType.Click, "click"},
        { PlayerCamera.UISoundType.MiniClick, "miniClick"},
        { PlayerCamera.UISoundType.Close, "close"},
        { PlayerCamera.UISoundType.HealthPanelOn, "woundon"},
        { PlayerCamera.UISoundType.InventoryOpen, "menuOpen"},
        { PlayerCamera.UISoundType.InventoryClose, "menuClose"},
        { PlayerCamera.UISoundType.Deny, "warning"},
        { PlayerCamera.UISoundType.Time, "time"},
    };
    public static void PlayUISound(PlayerCamera.UISoundType t, float pitch = 1f, float volume = 1f) {
        var name = _PlayUISound_NAMES.GetValueSafe(t) ?? "warning";

        Sound_Play_MultiplayerPatch.force = true;
        Sound.Play(name, Vector2.zero, twoDimensional: true, pitchShift: false, null, volume, pitch, noReverb: true, ignoreMixer: true);
    }


    public static bool WoundViewAlertIsShown() {
        return IsInWoundView() && WoundView.view.criticalPanel.activeInHierarchy;
    }
    public static void WoundViewShowAlertText(in string msg) {
        if (IsInWoundView()) {
            WoundView.view.criticalPanel.SetActive(true);
            var txt = WoundView.view.criticalPanel.GetComponentInChildren<TextMeshProUGUI>(true);
            if ((bool)txt) {
                //OG: WoundView.UpdateView  V520dev1
                txt.enabled = Mathf.Sin(Time.unscaledTime * 20f) > 0f;
                txt.text = msg;
            }
        }
    }

    public static void ToggleWoundView() {
        ToggleWoundView(!IsInWoundView());
    }
    public static void ToggleWoundView(bool target, bool sound = true) {
        if (PlayerCamera.main != null) {
            if (target != IsInWoundView()) {
                PlayerCamera.main.ToggleWoundView(sound);
            }
        }
    }
    public static bool IsInWoundView() {
        return PlayerCamera.main != null && PlayerCamera.main.woundView.activeSelf;
    }
    public static bool IsInConsole() {
        return (bool)ConsoleScript.instance && ConsoleScript.instance.active;
    }
    public static bool IsInCraftingMenu() {
        return PlayerCamera.main != null && PlayerCamera.main.craftingPanel.activeSelf;
    }
    public static bool IsInTradingMenu() {
        return PlayerCamera.main != null && PlayerCamera.main.tradeMenu.activeSelf;
    }

    public static void CreatePickupLine(Vector2 from, Vector2 to) {
        //OG:  Body.DoPickupCheck V5t4
        GameObject obj = Utils.Create(("Special/PickupLine"), Vector2.zero, 0);
        obj.GetComponent<LineRenderer>().SetPosition(0, from);
        obj.GetComponent<LineRenderer>().SetPosition(1, to);
        UnityEngine.Object.Destroy(obj, 0.5f);
    }

    #endregion


    // used for Body, mostly server side（统一复用缓存的 PhysicsLayers.GroundMask，消除高频字符串哈希查找）

    public static bool QuickGroundLinecastCheck(in Vector2 from, in Vector2 to) {
        return Physics2D.Linecast(from, to, PhysicsLayers.GroundMask);
    }


    public static bool QuickRaycastInteractionCheck(Vector2 from, Vector2 to, bool do_effect) {
        int groundMask = PhysicsLayers.GroundMask;
        bool flag = Physics2D.Linecast(from, to, groundMask);
        if (flag && ((bool)Physics2D.OverlapPoint(from, groundMask) || (bool)Physics2D.OverlapPoint(to, groundMask))) {
            flag = false;
        }
        if (flag && do_effect) {
            CreatePickupLine(from, to);
        }
        return !flag;
    }


    public static bool AccurateRaycastInteractionCheck(Body who, Vector2 target, bool do_effect) {
        int groundMask = PhysicsLayers.GroundMask;
        bool flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target, groundMask);

        if (flag) {
            flag = Physics2D.Linecast(who.GetHead().transform.position, target, groundMask);
        }
        if (flag && do_effect) {
            CreatePickupLine(who.transform.position, target);
        }
        return !flag;
    }

    public static bool AccurateRaycastInteractionCheckObstruction(Body who, Body target, bool do_effect) {

        if (who == target) {
            return true;
        }

        int g = PhysicsLayers.GroundMask;

        bool flag = Physics2D.Linecast(who.GetHead().transform.position, target.GetHead().transform.position, g);

        if (flag) {
            flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target.GetLowerTorso().transform.position, g);
            if (flag) {
                flag = Physics2D.Linecast(who.GetHead().transform.position, target.GetLowerTorso().transform.position, g);
                if (flag)
                    flag = Physics2D.Linecast(who.GetLowerTorso().transform.position, target.GetHead().transform.position, g);
            }
        }
        if (flag && do_effect) {
            CreatePickupLine(who.transform.position, target.transform.position);
        }
        return !flag;
    }


    public static bool DoFullInteractionCheck(Body who, Body target, bool do_effect, float distance = 9f, bool check_obstruction = true) {
        if (who == target) {
            return true;
        }
        if (!KM.DistanceCheck(who.transform.position, target.transform.position, distance)) {
            return false;
        }
        if (check_obstruction && !AccurateRaycastInteractionCheckObstruction(who, target, do_effect)) {
            return false;
        }
        return true;
    }


    public static bool TryGetBodyFromObject(Component component, out Body body) {
        if (component != null && Utils.GetBody(component.gameObject, out body)) {
            return true;
        }
        body = null;
        return false;
    }
    public static bool TryGetBodyFromObject(GameObject gameObject, out Body body) {
        if (gameObject != null && Utils.GetBody(gameObject, out body)) {
            return true;
        }
        body = null;
        return false;
    }


    public static Body GetBodyOnPos(Vector2 pos) {
        var a = Physics2D.OverlapPointAll(pos);
        foreach (var item in a) {
            if (item.TryGetComponent(out Limb c)) {
                return c.body;
            } else if (item.TryGetComponent(out Body b)) {
                return b;
            }
        }
        return null;
    }


    public static Vector2 GetResolution() {
        return new Vector2(Screen.width, Screen.height);
    }
    public static Vector2 GetCursorWorldPos() {
        return Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition);
    }
    public static Vector2 GetCursorPos() {
        return (UnityEngine.Input.mousePosition);
    }

}
