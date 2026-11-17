using CasualtiesTogetherUtils;
using HarmonyLib;
using MonoMod.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.SceneManagement;
using Color = UnityEngine.Color;

namespace Together;

/// <summary>
/// Multiplayer 的首次启动引导、反射接收器/单例扫描、程序集哈希计算与场景切换回调分部类。
/// </summary>
public partial class Multiplayer {
    internal static List<Component> Singletons = new();
    internal static List<Type> WorldSingletons = new();

    internal static void _FIRST_INIT() {
        FULL_VERSION_TAG = Application.version + "_MPv" + Plugin.MOD_VERSION;

#if !RELEASE
        log.l("Mod list: " + String.Join(", ", GetBepinPlugins()));
#endif

        try {
            foreach (var x in CoopKeybinds.as_dict) {
                Util.RegisterKeybind(x.Key, x.Value);
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Bootstrap, "FirstInit -> RegisterKeybind", e);
        }

        {
            var colors = new Color[] {
                new Color(1f, 0.5f, 0f),
                new Color(1f, 0.4f, 0f),
                new Color(1f, 0.6f, 0.2f),
                new Color(0.5f, 0f, 1f),
                new Color(0.6f, 0.2f, 0.8f),
                new Color(0.4f, 0f, 0.6f),
                new Color(1f, 0.4f, 0.7f),
                new Color(1f, 0.6f, 0.8f),
                new Color(0.9f, 0.3f, 0.5f),
                new Color(0.5f, 1f, 0.5f),
                new Color(0.2f, 0.8f, 0.2f),
                new Color(0.1f, 0.5f, 0.1f),
                new Color(0.3f, 0.7f, 0.3f),
                new Color(0.4f, 0.7f, 1f),
                new Color(0.2f, 0.4f, 0.8f),
                new Color(0.5f, 0.8f, 1f),
                new Color(0.1f, 0.3f, 0.6f),
                new Color(0f, 0.5f, 0.5f),
                new Color(0f, 0.7f, 0.7f),
                new Color(1f, 0.9f, 0.3f),
                new Color(1f, 0.8f, 0f),
                new Color(0.9f, 0.7f, 0.1f),
                new Color(0.6f, 0.3f, 0f),
                new Color(0.4f, 0.2f, 0.1f),
                new Color(0.7f, 0.5f, 0.3f),
                new Color(0f, 1f, 0.6f),
                new Color(1f, 0f, 0.6f),
                new Color(0.6f, 0f, 0f),
                new Color(0.8f, 0.2f, 0.2f),
                new Color(0.6f, 0.8f, 0.2f),
            };

            ScavPlayer.NAMETAG_DEFAULT_COLORS = ScavPlayer.NAMETAG_DEFAULT_COLORS.AddRangeToArray(colors);
            colors.ShuffleRandom();

            ScavPlayer.NAMETAG_DEFAULT_COLORS_RANDOMIZED = (new Color[] {
                Color.red,
                Color.green,
                Color.blue,
                Color.cyan,
                Color.yellow,
                Color.magenta,
                Color.white
            }).AddRangeToArray(colors);
        }

        try {
            if (!Util.IsInWorld()) {
                UIManager.OnMainMenuLoad();
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Bootstrap, "FirstInit -> UIManager.OnMainMenuLoad", e);
        }

        if (CoUtils_instance_MultiplayerPatch.original_local_instance == null) {
            CoUtils.instance.DurationOf("calling_this_just_to_trigger_the_getter");
        }

        OnSceneChangeOrWorldStartGenerate += _MpOnSceneChangeOrWorldStartGenerate;

        SceneManager.sceneLoaded += OnSceneLoaded;
        SceneManager.sceneUnloaded += OnSceneUnLoaded;

        Singletons.Add(Plugin.s.AddComponent<DebugHelp>());
        Singletons.Add(Plugin.s.AddComponent<SharedMain>());
        Singletons.Add(Plugin.s.AddComponent<ClientMain>());
        Singletons.Add(Plugin.s.AddComponent<ServerMain>());

        var my_assembly = Assembly.GetAssembly(typeof(CasualtiesTogetherSingleton));
        var my_types = my_assembly.GetTypesSafely();

        foreach (Type type in my_types) {
            try {
                UIMainMenu.GetSettingsFromStaticClass(type);

                if (type.IsClass && !type.IsAbstract) {
                    if (type.IsSubclassOf(typeof(CasualtiesTogetherSingleton))) {
#if DEBUG
                        Plugin.log.LogInfo($"DEV: Found Singleton to add: {type.Name}");
#endif
                        Singletons.Add(Plugin.s.AddComponent(type));
                    }
                    if (type.IsSubclassOf(typeof(CasualtiesTogetherSingletonWorldOnly))) {
#if DEBUG
                        Plugin.log.LogInfo($"DEV: Found World-Only Singleton to add: {type.Name}");
#endif
                        WorldSingletons.Add(type);
                    }
                }

                MethodInfo[] methods = type.GetMethods(BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.Instance | BindingFlags.Static);
                foreach (MethodInfo methodInfo in methods) {
                    try {
                        var netReceiverAttribute = methodInfo.GetAttribute<NetworkMessageReceiverAttribute>(true);
                        if (netReceiverAttribute != null) {
                            all_network_receivers_from_attributes.Add(
                                netReceiverAttribute,
                                methodInfo.CreateDelegate<HandleMessageDelegate>()
                            );
                        } else {
                            var initAttribute = methodInfo.GetAttribute<CTPostInitAttribute>(true);
                            if (initAttribute != null) {
                                all_postinit_from_attributes.Add(methodInfo.CreateDelegate<Action>());
                            }
                        }
                    } catch (Exception e) {
                        ModLog.Exception(LogDomain.Bootstrap, $"FirstInit -> method scanning : {methodInfo}", e);
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, $"FirstInit -> class scanning loop: TYPE:{type}", e);
            }
        }

        Util.DelayCallLambda(0.1f, () => {
            if (!win32windowtitlechanger.changed_the_title) {
                win32windowtitlechanger.SetTitle(_PickGameWindowTitle());
            }
        });

        Singletons.Reverse();

        CalculateModHash();

        foreach (var postInit in all_postinit_from_attributes) {
            try {
                postInit.DynamicInvoke();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, $"FirstInit -> PostInit: {postInit.GetMethodInfo()}", e);
            }
        }
    }

    static void CalculateModHash() {
        try {
            var assemblycsharppath = Path.Combine(Application.dataPath, "Managed/Assembly-CSharp.dll");
            if (File.Exists(assemblycsharppath)) {
                GAME_FILES_HASH = Util.GetFileHash(assemblycsharppath);
            } else {
                DoMultiplayerStatusMessageError("GAME HASH CALCULATION: Assembly-CSharp.dll DOES NOT EXIST????");
            }

            var asm = Assembly.GetExecutingAssembly();
            var mpmodassemblypath = asm.Location;
            if (File.Exists(mpmodassemblypath)) {
                GAME_FILES_HASH += Util.GetFileHash(mpmodassemblypath);
            } else {
                DoMultiplayerStatusMessageError($"GAME HASH CALCULATION: {asm.FullName} DOES NOT EXIST????");
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Bootstrap, "GAME HASH CALCULATION", e);
        }

        if (FULL_VERSION_TAG.EndsWith("fork")) {
            var str = ((ushort)GAME_FILES_HASH).ToString("X");
            FULL_VERSION_TAG += str;
            Plugin.FULL_MOD_VERSION += str;
        }

        GlobalDark_Awake_MultiplayerPatch.SetBetaBuildMPText();
    }

    public static void OnSceneLoaded(Scene a, LoadSceneMode b) {
#if DEBUG
        Plugin.log.LogWarning("DEV: SCENE LOADED");
#endif
        ServerMain._ded_server_switch_counter = -10;
        WoundView_UpdateView_MultiplayerPatch.OG_showInfection = null;
        ServerMain.Server_LastPlayerStates.Clear();

        var is_world = a.name == "SampleScene";
        if (!is_world) {
            SavesystemPatch.PreserveSave = false;
        }

        if (Net.IsRunning) {
            try {
                PreRunScript.didIntro = true;
                if (ScrollableText.textObj != null) {
                    UnityEngine.Object.Destroy(ScrollableText.textObj.gameObject);
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, "remove the intro yapping", e);
            }

            if (PlayerCamera.main?.body != null && !Net.IsDedicatedServer) {
                ScavPlayer.BodyToPlayerDict[PlayerCamera.main.body] = ScavPlayer.LocalPlayer;
            }

            if (is_world) {
                try {
                    WorldGeneration.world.loadingObject.GetOrAddComponent<LoadingScreenSpecimenDuplicator>();
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Bootstrap, "LoadingScreenCreatureDuplicator", e);
                }

                ClientMain._InitializePlayerCharacter();
            }
        }

        if (is_world) {
            if (!Net.IsRunningSteam) {
                Plugin.log.LogInfo("Local player username: " + UIMainMenu.USERINPUT_NAME);
            } else {
                Plugin.log.LogInfo("Local player username: " + KSteam.GetLocalUsername());
            }
            Plugin.log.LogInfo("Run settings: " + WorldMP.CompileRunSettings());

            if (Net.IsDedicatedServer) {
                UIInGame.StartSpectatorMode();
            } else {
                var pb = PlayerCamera.main.body.GetOrAddComponent<NetBody>();
                pb.BackupLocalPlayerInit();
                UIMainMenu.SetOpen(false);
            }
        } else {
            try {
                UIManager.OnMainMenuLoad();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Bootstrap, "OnSceneLoaded -> UIManager.OnMainMenuLoad", e);
            }
            WorldMP.ResetWorldParameters();
        }

        Multiplayer._InvokeOnSceneChangeOrWorldStartGenerate();
        ServerMain.Server_LastPlayerStates.Clear();
    }

    public static void OnSceneUnLoaded(Scene a) {
#if DEBUG
        Plugin.log.LogWarning("DEV: SCENE UNLOADED");
#endif
        NetObjectRegistry.UnregisterAllObjects();
    }

    public static bool IsFullgameOrMPDebugBuild() {
#if DEBUG
        return true;
#else
        return Util.GameIsFull();
#endif
    }

    static string _PickGameWindowTitle() {
        var r = new System.Random();
        var pickrandom = (string[] x) => x[r.Next(0, x.Length)];
        var gamename = Application.productName;
        if (r.NextDouble() > 0.2f) {
            gamename = "Casualties: Unknown";
            if (r.NextDouble() > 0.5f) {
                gamename = "Casualties Unknown";
                if (r.NextDouble() > 0.2f) {
                    gamename = pickrandom(new string[] {
                        "Casualties: Multiple",
                        "Casualties: Multiple",
                        "Casualties: Together",
                        "Casualties: Many",
                        "Casualties: Manyplayer",
                        "Casualties: Multiplayer",
                        "Casualties: Market Pliers",
                        "Casualties: Multi-Pliers",
                        "Casualties: Some",
                        "Casualties: Some Amount",
                        "Casualties: Co-op",
                    });
                }
            }
        }

        if (r.NextDouble() > 0.3) {
            gamename = gamename + ": " + pickrandom(new string[] {
                "Multiplayer",
                "Multiplied",
                "Co-op",
                "bluetooth support",
                "communism",
                "Manyplayer",
                "Die with friends",
                "death in poverty",
                "overdose with friends !!!",
                "fentanyl with friends !!!",
                "yipeee!!!  *falls off a cliff*",
                "furry suffering",
                "Just add multiplayer",
                "Krokosha's Multiplayer mod",
                "PEAK for masochists",
                "PEAK, but you go down",
                "thats a weird terraria",
                "Now with MULTIPLAYER",
                "The SALAD",
                "Cooperation behavioral research",
            });
        }
        return gamename;
    }
}
