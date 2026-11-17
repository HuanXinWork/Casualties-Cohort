using System;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Text;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 仅在世界场景驻留的周期性状态快照组件（每 60 秒保存一次服务端已加载玩家的位置快照）。
/// </summary>
class TrackerWorld : CasualtiesTogetherSingletonWorldOnly {
    void Start() {
        InvokeRepeating(nameof(Update_60s), 1f, 60f);
    }

    void LateUpdate() {
#if !RELEASE
        var world = WorldGeneration.world;
        if (world != null && world.genRects != null) {
            foreach (var item in world.genRects) {
                if (item != null && item.TryGetComponent(out Image img)) {
                    img.color = new Color(img.color.r, img.color.g, img.color.b, 0.02f);
                }
            }
        }
#endif
    }

    void Update_60s() {
        if (Net.IsServer && SharedMain.LocalWorldIsGenerated) {
            SaveLocationSnapshots();
        }
    }

    internal void SaveLocationSnapshots() {
        foreach (var plr in ScavPlayer.AllPlayers) {
            if (plr?.ServerPlayerState == null || !plr.ServerPlayerState.is_loaded_in) {
                continue;
            }
            plr.ServerPlayerState.SaveLocationSnapshot();
        }
    }
}

/// <summary>
/// 联机世界参数、开局配置同步、场景切换与环境背景管理核心。
/// 世界生成生命周期协程与网络接收器见 <c>WorldMP.Generation.cs</c>。
/// </summary>
public partial class WorldMP : CasualtiesTogetherSingleton {
    public static event Action OnWorldgenFinish;

    /// <summary>
    /// 开局后锁定芯片模式（unchipped），忽略大厅在局内运行期间的后续切换。
    /// </summary>
    public static class RunUnchippedLock {
        public static bool Locked;
        public static bool GlobalUnchipped;
        internal static bool ApplyingSynced;

        public static void LockForRun() {
            if (!Net.IsRunning) {
                return;
            }
            Locked = true;
            GlobalUnchipped = WorldGeneration.world != null
                ? WorldGeneration.unchipped
                : (bool)runSettings.GetValueOrDefault("unchipped", false);
            if (WorldGeneration.runSettings != null) {
                WorldGeneration.runSettings["unchipped"] = GlobalUnchipped;
            }
            if (PreRunScript.instance != null && PreRunScript.instance.runSettings != null) {
                PreRunScript.instance.runSettings["unchipped"] = GlobalUnchipped;
            }
        }

        public static void Clear() {
            Locked = false;
        }

        public static bool BlocksLocalChange(bool want) {
            if (!Locked || !Net.IsRunning) {
                return false;
            }
            if (ApplyingSynced || Net.IsServer) {
                return false;
            }
            if (Multiplayer.rules.UnchippedIsIndividual) {
                return Util.IsInWorld();
            }
            return want != GlobalUnchipped;
        }
    }

    public static Dictionary<string, object> GetNormalRunSettings() {
        Dictionary<string, object> dict = new();
        foreach (KeyValuePair<string, object> presetValue in RunSettings.GetPreset("normal").presetValues) {
            dict.Add(presetValue.Key, presetValue.Value);
        }
        return dict;
    }

    public static Dictionary<string, object> runSettings {
        get {
            Dictionary<string, object> prerunset = PreRunScript.instance != null ? PreRunScript.instance.runSettings : null;
            if (Util.IsInMainMenu()) {
                return prerunset ?? GetNormalRunSettings();
            }
            return WorldGeneration.runSettings ?? prerunset ?? GetNormalRunSettings();
        }
    }

    public struct RunPrefs : INetSerializeByMemcpy {
        public bool tutorial;
        public bool unchipped;

        public RunPrefs ReadPrefs() {
            tutorial = PlayerPrefsExtended.GetBool("tutorial");
            if (runSettings != null) {
                unchipped = (bool)runSettings["unchipped"];
            }
            return this;
        }

        public void ApplyPrefs(bool abide_rules = true, bool apply_tutorial = true) {
            if (apply_tutorial) {
                PlayerPrefsExtended.SetBool("tutorial", tutorial);
            }
            if (abide_rules && !rules.UnchippedIsIndividual && runSettings != null) {
                runSettings["unchipped"] = unchipped;
            }
        }
    }

    static Dictionary<string, object> SettingTupleListToDict(List<(string, string)> tupleList) {
        if (tupleList == null) {
            throw new ArgumentNullException(nameof(tupleList));
        }

        Dictionary<string, object> dict = new();
        foreach ((string settingKey, string valueStr) in tupleList) {
            List<RunSetting> matches = RunSettings.settingTypes.Where(x => x.name == settingKey).ToList();
            if (matches.Count == 0) {
                continue;
            }

            RunSetting first = matches[0];
            if (first is RunSettingFloat) {
                dict.Add(settingKey, float.Parse(valueStr, CultureInfo.InvariantCulture));
            } else if (first is RunSettingBool) {
                dict.Add(settingKey, bool.Parse(valueStr));
            } else if (first is RunSettingDropdown) {
                dict.Add(settingKey, int.Parse(valueStr));
            }
        }
        return dict;
    }

    public static string CompileRunSettings() {
        string input = JsonConvert.SerializeObject(SaveSystem.DicToTupleList(runSettings), Formatting.None, new JsonSerializerSettings {
            ReferenceLoopHandling = ReferenceLoopHandling.Ignore,
            Formatting = Formatting.None
        });
        return Convert.ToBase64String(Util.Compress(Encoding.UTF8.GetBytes(input)));
    }

    public static Dictionary<string, object> ReadRunSettings(string base64) {
        Dictionary<string, object> dict = GetNormalRunSettings();
        try {
            string decoded = Encoding.UTF8.GetString(Util.Decompress(Convert.FromBase64String(base64)));
            JArray jArray = JArray.Parse(decoded);
            foreach (KeyValuePair<string, object> item in SettingTupleListToDict(jArray.ToObject<List<(string, string)>>())) {
                dict[item.Key] = item.Value;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(ReadRunSettings), ex);
        }
        return dict;
    }

    public static void ApplyRunSettings(in string base64) {
        ApplyRunSettings(ReadRunSettings(base64));
    }

    public static void ApplyRunSettings(Dictionary<string, object> newRunSettings) {
        var oldset = runSettings;
        if (PreRunScript.instance != null) {
            PreRunScript.instance.runSettings = newRunSettings;
            PreRunScript.instance.currentPreset = 6;
            PreRunScript.instance.UpdateAllSettingDisplays();
        }
        WorldGeneration.runSettings = newRunSettings;
        if (oldset != null && Net.IsClient && rules.UnchippedIsIndividual) {
            newRunSettings["unchipped"] = oldset.GetValueOrDefault("unchipped", false);
        }
    }

    public static WorldGeneration world => WorldGeneration.world;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;

    public static MultiplayerGameRules rules => Multiplayer.rules;
    public static bool verbose => Multiplayer.verbose;

    static bool mainmenu_is_screen_darkening = false;

    internal static byte[] aaaaaaaaaaaaaaaaaaaaaaaaaaa = null;
    internal static byte[] bbbbbbbbbbbbbbbbbbbbbbbbbbb = null;
    internal static byte[] ccccccccccccccccccccccccccc = null;

    internal static Sprite whiteSquare = null;

    public static bool earthquake_enabled;
    public static bool allow_continue_to_next_layers;

    void Awake() {
        ResetWorldParameters();

        Color white = Color.white;
        Texture2D texture = new Texture2D(2, 2, TextureFormat.RGBA32, false);
        texture.SetPixel(0, 0, white);
        texture.SetPixel(1, 0, white);
        texture.SetPixel(1, 1, white);
        texture.SetPixel(0, 1, white);
        texture.filterMode = FilterMode.Point;

        whiteSquare = Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f));
        SceneManager.sceneLoaded += OnSceneLoaded;
    }

    void OnSceneLoaded(Scene a, LoadSceneMode b) {
        aaaaaaaaaaaaaaaaaaaaaaaaaaa = null;
        bbbbbbbbbbbbbbbbbbbbbbbbbbb = null;
        ccccccccccccccccccccccccccc = null;
        if (a.name == "PreGen") {
            ReleaseTitleFade();
        }
    }

    /// <summary>
    /// 教程退出加载 PreGen 时解除 GlobalDark 黑幕锁与点击遮挡。
    /// </summary>
    internal static void ReleaseTitleFade() {
        GlobalDark dark = GlobalDark.main;
        if (dark == null) {
            return;
        }
        try {
            Traverse fields = Traverse.Create(dark);
            fields.Field("darkening").SetValue(false);
            fields.Field("stayDark").SetValue(false);
            fields.Field("speedMult").SetValue(1f);
        } catch (Exception ex) {
            ModLog.Debug("ReleaseTitleFade traverse fallback: " + ex.Message, LogDomain.World);
        }
        dark.SetSpeedMultiplier(1f);
        dark.SetStayDark(false);
        dark.SetDarkAmount(0f);
        if (dark.image != null) {
            dark.image.raycastTarget = false;
        }
    }

    void FixedUpdate() {
        if (Util.IsInWorld()) {
            if (mainmenu_is_screen_darkening) {
                mainmenu_is_screen_darkening = false;
            }
            if ((!earthquake_enabled && world.earthquakeTime <= 0)
                || world.biomeOverride == WorldGeneration.OverrideSceneType.Debug) {
                world.earthquakeTime = 0;
                world.earthquakeDelay = 1000f;
                world.earthquakeIntensity = 0;
            }
        }
    }

    public static LayerModifier GetLayerModifier() {
        foreach (var item in LayerModifier.availableModifiers) {
            if (item.active) {
                return item;
            }
        }
        return null;
    }

    public static void ResetWorldParameters() {
        earthquake_enabled = true;
        allow_continue_to_next_layers = true;
    }

    public static void ResetWorldParametersPeaceful() {
        ResetWorldParameters();
        earthquake_enabled = false;
        SetRadlinePlayerPrefs(false);
    }

    public static void SetRadlinePlayerPrefs(bool enabled) {
        PlayerPrefs.SetInt("radlinedisable", enabled ? 0 : 1);
    }

    public static void SetTutorialPlayerPrefs(bool enabled) {
        PlayerPrefs.SetInt("tutorial", enabled ? 1 : 0);
    }

    public static void LoadDebugWorld() {
        _CheckIfCanLoadAWorld();
        SetTutorialPlayerPrefs(false);
        SavesystemPatch.PreserveSave = true;
        SaveSystem.loadedRun = false;

        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            ServerMain.Server_Announce_GAME_START();
        }

        SceneManager.LoadScene("SampleScene");
        Util.CallLambdaWhen(
            () => WorldGeneration.world != null,
            () => { WorldGeneration.world.biomeOverride = WorldGeneration.OverrideSceneType.Debug; });
    }

    /// <summary>
    /// 加载原版生成的标准世界（<paramref name="loadsave"/> 为 <c>true</c> 时读取联机存档继续游戏）。
    /// 注意：对外公开方法签名与默认参数严禁变更（供 <c>TogetherSaveWorldApi</c> 及外部模组调用）。
    /// </summary>
    public static void LoadVanillaGeneratedWorld(bool loadsave = false) {
        _CheckIfCanLoadAWorld();
        SavesystemPatch.PreserveSave = false;
        ResetWorldParameters();
        SetRadlinePlayerPrefs(true);
        LoadWorldScene(loadsave);
    }

    public static void LoadVanillaTutorialWorld(bool fadeout = true) {
        _CheckIfCanLoadAWorld();
        SavesystemPatch.PreserveSave = true;
        SaveSystem.loadedRun = false;

        ResetWorldParameters();
        SetTutorialPlayerPrefs(true);
        SetRadlinePlayerPrefs(false);

        NetObjectRegistry.WipeSessionEntities();
        LoadWorldScene(false, fadeout);
    }

    static GameObject SKY_BACKGROUND = null;

    public static void CreateSkyBackground() {
        CreateSkyBackground(world.skyColors.Evaluate(UnityEngine.Random.value), UnityEngine.Random.Range(0f, 1f) < 0.3f ? 1f : 0f);
    }

    public static void CreateSkyBackground(Color skyColor, float rainIntensity) {
        SKY_BACKGROUND = Utils.Create("Special/wallholes", Vector2.zero, 0);

        var spr = SKY_BACKGROUND.GetComponent<SpriteRenderer>();
        spr.sprite = whiteSquare;
        spr.gameObject.transform.localScale *= 99999f;

        world.skyMaterial.SetColor("_TopColor", skyColor);
        world.skyMaterial.SetFloat("_RainIntensity", rainIntensity);

        var audio = SKY_BACKGROUND.GetComponent<AudioSource>();
        audio.maxDistance = 100000f;
        audio.spatialBlend = 0.001f;
        audio.volume *= 0.7f;

        SKY_BACKGROUND.name = "MP_SURFACE_BACKGROUND";

        if (Multiplayer.IsNetworkActiveAndIsServer()) {
            ServerMain.Server_SendWorldState();
        }
    }

    public static void RemoveSkyBackground() {
        if (SKY_BACKGROUND != null) {
            GameObject.Destroy(SKY_BACKGROUND);
            if (Multiplayer.IsNetworkActiveAndIsServer()) {
                ServerMain.Server_SendWorldState();
            }
        }
    }

    public static bool HasSkyBackground(out (Color, float) skyinfo) {
        if (SKY_BACKGROUND != null) {
            skyinfo = (
                world.skyMaterial.GetColor("_TopColor"),
                world.skyMaterial.GetFloat("_RainIntensity"));
            return true;
        }
        skyinfo = (default, 0f);
        return false;
    }

    public static bool HasSkyBackground() {
        return SKY_BACKGROUND != null;
    }

    public static void LoadWorldScene(bool loadsave = false, bool fadeout = true) {
        if (world != null) {
            throw new InvalidOperationException("World is already loaded.");
        }
        mainmenu_is_screen_darkening = true;
        SaveSystem.loadedRun = loadsave;

        if (fadeout) {
            var prs = PreRunScript.instance;
            if (prs) {
                try {
                    prs.StartCoroutine(prs.WaitLoad());
                } catch {
                    mainmenu_is_screen_darkening = false;
                    throw;
                }
            } else {
                mainmenu_is_screen_darkening = false;
                throw new InvalidOperationException("PreRunScript instance does not exist.");
            }
        } else {
            SceneManager.LoadScene("SampleScene");
        }
    }

    static void _CheckIfCanLoadAWorld() {
        if (Util.IsInWorld()) {
            throw new InvalidOperationException("World is already loaded.");
        }
        if (mainmenu_is_screen_darkening) {
            throw new InvalidOperationException("World is already loading.");
        }
    }
}
