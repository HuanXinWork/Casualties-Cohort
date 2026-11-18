using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;
using Together.Api;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 拦截原版 <see cref="SaveSystem.HasSave"/>，按单人/联机上下文重定向存档检测目录。
/// </summary>
[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.HasSave))]
public static class SaveSystem_HasSave_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);

    internal static bool force = false;

    private static bool Prefix(ref bool __result) {
        if (!Net.IsRunning && !force) {
            SavesystemPatch.PreserveSave = false;
            SavesystemPatch.savedatapathreplacement = Application.persistentDataPath;
            return true;
        }
        if (force) {
            __result = true;
            force = false;
            return false;
        }
        // 原版主菜单仅检测 save.sv，联机目录下包含分玩家子目录时同样视为存在存档
        string path = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
        SavesystemPatch.savedatapathreplacement = path;
        if (path == SavesystemPatch.vanillasavefolder) {
            __result = SavesystemPatch.HasVanillaSaveFile();
        } else {
            __result = SavesystemPatch.HasAnySaveFile();
        }
        return false;
    }
}

/// <summary>
/// 拦截原版 <see cref="SaveSystem.SaveGame"/>，由服务端按持久 ID 逐玩家落盘档案、保存联机规则头并写入外部模组侧车分段。
/// </summary>
[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.SaveGame))]
public static class SaveSystem_SaveGame_MultiplayerPatch {
    public static WorldGeneration world => WorldGeneration.world;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);

    static bool force = false;
    static bool wrote;

    /// <summary>
    /// 单玩家检查点直写入口：直接针对已选定目录保存指定躯体，跳过全局联机遍历前缀。
    /// </summary>
    internal static void WriteForced(Body body) {
        var camera = PlayerCamera.main;
        Body previous = camera != null ? camera.body : null;
        force = true;
        try {
            if (camera != null)
                camera.body = body;
            SaveSystem.SaveGame();
        } finally {
            force = false;
            if (camera != null)
                camera.body = previous;
        }
    }

    static bool Prefix() {
        wrote = false;
        if (!Net.IsRunning && !force) {
            SavesystemPatch.PreserveSave = false;
            SavesystemPatch.savedatapathreplacement = Application.persistentDataPath;
            return true;
        }
        // 教学关或地图调试模式禁止覆盖真实 mp_save
        if (SavesystemPatch.PreserveSave && !force)
            return false;

        if (!force) {
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.mpsavefolder;
            Body originalLocalBody = Util.GetLocalBody();

            try {
                if (Multiplayer.IsNetworkActiveAndIsServer()) {
                    if (!Directory.Exists(SavesystemPatch.mpsavefolder)) {
                        Directory.CreateDirectory(SavesystemPatch.mpsavefolder);
                    }

                    bool midlevel = !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();

                    try {
                        foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                            SavesystemPatch.savedatapathreplacement = Path.Combine(SavesystemPatch.mpsavefolder, bodyplr.Value.GetPersistentId());

                            try {
                                try {
                                    if (!midlevel && !bodyplr.Key.alive && Multiplayer.rules.CanReviveOnNextLevel()) {
                                        bodyplr.Value.Server_RespawnCharacter(originalLocalBody.transform.position, !midlevel);
                                    }
                                    bodyplr.Value.ResetEntropy();
                                } catch (Exception ex) {
                                    ModLog.Exception(LogDomain.World, $"SaveGame.PreparePlayer({bodyplr.Value})", ex);
                                }

                                if (!Directory.Exists(SavesystemPatch.savedatapathreplacement)) {
                                    Directory.CreateDirectory(SavesystemPatch.savedatapathreplacement);
                                }

                                force = true;
                                PlayerCamera.main.body = bodyplr.Key;
                                SaveSystem.SaveGame();
                            } catch (Exception ex) {
                                ModLog.Exception(LogDomain.World, $"SaveGame.SavePlayer({bodyplr.Value})", ex);
                            }
                        }

                        PlayerCamera.main.body = originalLocalBody;
                        SavesystemPatch.savedatapathreplacement = SavesystemPatch.mpsavefolder;
                        force = false;
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.World, "SaveGame.SaveAllPlayers", ex);
                    }

                    // 保存联机存档头 mp_rules.json
                    {
                        var dict = new Dictionary<string, object> {
                            ["hello"] = "Multiplayer Mod created by Krokosha666",
                            ["MPVERSION"] = Plugin.MOD_VERSION,
                            ["GAMEVERSION"] = Application.version,
                            ["SAVEID"] = WoundView.view.cInfo[2],
                            ["LEVEL"] = midlevel,
                            ["RULES"] = Multiplayer.rules,
                            ["MODIFIER"] = midlevel ? (WorldMP.GetLayerModifier()?.modifierIndex ?? -1) : -1,
                            ["PLRPOS"] = ScavPlayer.BodyToPlayerDict.ToDictionary(
                                x => x.Value.GetPersistentId(),
                                x => (x.Key.transform.position.x, x.Key.transform.position.y))
                        };
#if FULLGAME
                        dict[AchievementBreakSync.SaveHeaderKey] = SteamManager.disableAchievements;
                        dict["PLRCHARS"] = ScavPlayer.BodyToPlayerDict.ToDictionary(
                            x => x.Value.GetPersistentId(),
                            x => x.Key != null ? x.Key.charType : (int)x.Value.selectedCharacter);
#endif
                        string serialized = JsonConvert.SerializeObject(dict, Formatting.Indented);
                        string rulesPath = Path.Combine(SavesystemPatch.mpsavefolder, SavesystemPatch.mpsaveheaderfile);
                        File.WriteAllText(rulesPath, serialized);
                    }

#if !RELEASE
                    if (midlevel) {
                        try {
                            File.WriteAllBytes(
                                Path.Combine(SavesystemPatch.mpsavefolder, "world_tiles.bin"),
                                SavesystemPatch.SerializeWorldBlocks());
                            File.WriteAllBytes(
                                Path.Combine(SavesystemPatch.mpsavefolder, "world_fluid.bin"),
                                SavesystemPatch.SerializeWorldFluids());
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.World, "SaveGame.SaveWorldBinary", ex);
                        }
                    }
#endif
                    // 联机主存档目录写入完成后，触发 TogetherSaveWorldApi 侧车分段导出与完成事件
                    TogetherSaveWorldApi.WriteModSaveSections(SavesystemPatch.mpsavefolder);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "SaveGame_MultiplayerPatch.Prefix", ex);
            }

            SaveSystem_HasSave_MultiplayerPatch.force = false;
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetPersistentDataPathToSave();
            force = false;
            PlayerCamera.main.body = originalLocalBody;
        }

        ModLog.Flow(LogDomain.World, "SaveGame", $"Saving to {SavesystemPatch.savedatapathreplacement}");
        wrote = true;
        return true;
    }

    // 原版存档只写入全局共享配方表，联机按玩家独立记录已合成配方奖励列表
    static void Postfix() {
        if (!wrote || PlayerCamera.main == null || PlayerCamera.main.body == null)
            return;
        if (!PlayerCamera.main.body.TryGetNetBody(out NetBody nb) || nb == null || !nb.IsPlayer)
            return;
        PlayerCheckpoint.StampCrafted(SavesystemPatch.savedatapathreplacement, nb.RecipesHasCrafterBefore);
    }
}

/// <summary>
/// 拦截原版 <see cref="SaveSystem.TryLoadGame"/>，恢复联机世界状态、逐玩家档案及外部模组自定义存档段。
/// </summary>
[HarmonyPatch(typeof(SaveSystem), nameof(SaveSystem.TryLoadGame))]
public static class SaveSystem_LoadGame_MultiplayerPatch {
    public static WorldGeneration world => WorldGeneration.world;

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        SavesystemPatch.StripSaveDelete(SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions));

    static bool force = false;
    internal static bool deleteVanillaAfter;

    /// <summary>
    /// 中途加入玩家单目录读档入口，跳过全局大厅遍历后缀。
    /// </summary>
    internal static void ForceLoad() {
        force = true;
        try {
            SaveSystem.TryLoadGame();
        } finally {
            force = false;
        }
    }

    static bool Prefix(ref bool __state) {
        deleteVanillaAfter = false;
        if (!Net.IsRunning && !force) {
            SavesystemPatch.PreserveSave = false;
            SavesystemPatch.savedatapathreplacement = Application.persistentDataPath;
            __state = SaveSystem.loadedRun;
            return true;
        }
        if (SavesystemPatch.PreserveSave && !force) {
            __state = false;
            SaveSystem.loadedRun = false;
            return false;
        }
        // 跨场景异步加载期间若 SaveSystem.loadedRun 被清空，依靠粘滞标志维持联机读档链路
        if (!force && SavesystemPatch.PendingLoadMpSave && Net.IsRunning && Net.IsServer) {
            SaveSystem.loadedRun = true;
        }
        __state = SaveSystem.loadedRun;
        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            return false;
        }

        if (!force) {
            Body originalLocalBody = Util.GetLocalBody();
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
            PlayerCamera.main.body = originalLocalBody;
        }

        if (!SaveSystem.loadedRun) {
            return false;
        }

        if (!force && Net.IsRunning) {
            SaveSystem.loadedRun = false;
            return true;
        }

        deleteVanillaAfter = !Net.IsRunning;
        ModLog.Flow(LogDomain.World, "LoadGame", $"Loading save from {SavesystemPatch.savedatapathreplacement}");
        return true;
    }

    static void Postfix(ref bool __state) {
        if (!Net.IsRunning && !force) {
            return;
        }
        if (force)
            PlayerCheckpoint.ReadCrafted(SavesystemPatch.savedatapathreplacement);

        if (deleteVanillaAfter) {
            deleteVanillaAfter = false;
            try {
                string vanilla = Path.Combine(SavesystemPatch.vanillasavefolder, "save.sv");
                if (File.Exists(vanilla))
                    File.Delete(vanilla);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "LoadGame.DeleteSoloSave", ex);
            }
        }

        if (!force) {
            SaveSystem.loadedRun = __state;
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.mpsavefolder;
            Body originalLocalBody = Util.GetLocalBody();

            try {
                string mpsavedir = SavesystemPatch.mpsavefolder;
                if (Multiplayer.IsNetworkActiveAndIsServer() && SaveSystem.loadedRun && Directory.Exists(mpsavedir)) {
                    Multiplayer.DoMultiplayerStatusMessageLog("Attempting to load a multiplayer save.");
                    if (SavesystemPatch.TryPeekMpWorldState(out var peek) && WorldGeneration.world != null) {
                        SavesystemPatch.ApplyPeekedWorldState(WorldGeneration.world, peek);
                        ModLog.Info($"Applied mp_save world peek: depth={peek.biomeDepth} midlevel={peek.midlevel} traveled={peek.totalTraveled}", LogDomain.World);
                    }

                    try {
                        string rulesPath = Path.Combine(mpsavedir, SavesystemPatch.mpsaveheaderfile);
                        if (!File.Exists(rulesPath)) {
                            throw new FileNotFoundException("Multiplayer rules file does not exist.", rulesPath);
                        }

                        bool midlevel;
                        {
                            string text = File.ReadAllText(rulesPath);
                            JObject jObject = JObject.Parse(text);
                            string mpVersion = (string)jObject.GetValue("MPVERSION");
                            string gameVersion = (string)jObject.GetValue("GAMEVERSION");
                            int saveId = (int)jObject.GetValue("SAVEID");
                            midlevel = (bool)jObject.GetValue("LEVEL");

                            if (midlevel) {
                                int modifier = (int)jObject.GetValue("MODIFIER");
                                if (modifier != -1) {
                                    world.StartCoroutine(Util._CoroutineCallLambdaWhen(Util.IsWorldGenerated, () => {
                                        var mod = LayerModifier.availableModifiers[modifier];
                                        mod.Initialize(world);
                                        mod.active = true;
                                    }));
                                }
                            }

                            if (mpVersion != Plugin.MOD_VERSION) {
                                ModLog.Warning($"LoadGame_MultiplayerPatch: Mismatching mod version! file:{mpVersion} current:{Plugin.MOD_VERSION}", LogDomain.World);
                            }
                            if (gameVersion != Application.version) {
                                ModLog.Warning($"LoadGame_MultiplayerPatch: Mismatching game version! file:{gameVersion} current:{Application.version}", LogDomain.World);
                            }
                            if (saveId != WoundView.view.cInfo[2]) {
                                ModLog.Flow(LogDomain.World, "LoadGame.SaveIdMismatch", $"header={saveId} save.sv={WoundView.view.cInfo[2]}");
                            }

                            if (jObject.TryGetValue("RULES", out var rulesToken)) {
                                Multiplayer.rules = rulesToken.ToObject<MultiplayerGameRules>();
                            }
#if FULLGAME
                            if (jObject.TryGetValue(AchievementBreakSync.SaveHeaderKey, out var cheatedToken)
                                && cheatedToken != null && cheatedToken.Type != JTokenType.Null
                                && cheatedToken.Value<bool>()) {
                                AchievementBreakSync.ApplyLocal(showAlert: true);
                            }
#endif
                        }

#if !RELEASE
                        if (midlevel) {
                            try {
                                string tilesPath = Path.Combine(mpsavedir, "world_tiles.bin");
                                if (File.Exists(tilesPath))
                                    WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa = File.ReadAllBytes(tilesPath);

                                string fluidsPath = Path.Combine(mpsavedir, "world_fluid.bin");
                                if (File.Exists(fluidsPath))
                                    WorldMP.ccccccccccccccccccccccccccc = File.ReadAllBytes(fluidsPath);

                                string objectsPath = Path.Combine(mpsavedir, "world_objects.bin");
                                if (File.Exists(objectsPath))
                                    WorldMP.bbbbbbbbbbbbbbbbbbbbbbbbbbb = File.ReadAllBytes(objectsPath);
                            } catch (Exception ex) {
                                ModLog.Exception(LogDomain.World, "LoadGame.ReadWorldBinary", ex);
                            }
                        }
#endif

                        try {
                            SharedMain.CreatePlayerCharacters();

                            foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                                string persistentId = bodyplr.Value.GetPersistentId();
                                SavesystemPatch.savedatapathreplacement = Path.Combine(mpsavedir, persistentId);

                                if (Directory.Exists(SavesystemPatch.savedatapathreplacement)) {
                                    try {
                                        SaveSystem_HasSave_MultiplayerPatch.force = true;
                                        SaveSystem.loadedRun = true;
                                        force = true;
                                        PlayerCamera.main.body = bodyplr.Key;
                                        SaveSystem.TryLoadGame();

                                        foreach (Item item in bodyplr.Key.GetAllItemsThorough()) {
                                            if (NetObjectRegistry.ObjectShouldBeSynced(item.gameObject)) {
                                                NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(item.gameObject);
                                            }
                                        }
                                        bodyplr.Value.ServerPlayerState.loaded_save = true;
                                    } catch (Exception ex) {
                                        ModLog.Exception(LogDomain.World, $"LoadGame.ReadPlayer({bodyplr.Value}, path={SavesystemPatch.savedatapathreplacement})", ex);
                                    }
                                } else {
                                    ModLog.Warning($"LoadGame_MultiplayerPatch: Save file not found for {bodyplr.Value}", LogDomain.World);
                                }
                            }

                            if (Con.IsConsoleOpen()) {
                                ConsoleScript.instance.ToggleActiveState();
                            }
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.World, "LoadGame.ReadAllPlayers", ex);
                        }

                        SaveSystem.loadedRun = true;
                        SaveSystem_HasSave_MultiplayerPatch.force = false;
                        SavesystemPatch.savedatapathreplacement = mpsavedir;
                        force = false;
                        PlayerCamera.main.body = originalLocalBody;
                        PlayerCheckpoint.ApplyLocalCraftMarkers();

                        try {
                            foreach (var bodyplr in ScavPlayer.BodyToPlayerDict) {
                                if (!bodyplr.Value.ServerPlayerState.loaded_save) {
                                    ModLog.Warning($"LoadGame_MultiplayerPatch: Could not find player to load: {bodyplr.Value}", LogDomain.World);
                                }
                            }
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.World, "LoadGame.VerifyLoadedPlayers", ex);
                        }

                        // 联机主存档与各玩家档案恢复完毕后，导入外部模组自定义存档段并触发完成事件
                        TogetherSaveWorldApi.ReadModSaveSections(mpsavedir);
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.World, "LoadGame.ReadMpSave", ex);
                    }

                    SaveSystem.loadedRun = true;
                    SavesystemPatch.PendingLoadMpSave = false;
                    SaveSystem_HasSave_MultiplayerPatch.force = false;
                    SavesystemPatch.savedatapathreplacement = SavesystemPatch.vanillasavefolder;
                    force = false;
                    PlayerCamera.main.body = originalLocalBody;
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "LoadGame_MultiplayerPatch.Postfix", ex);
            }

            SaveSystem_HasSave_MultiplayerPatch.force = false;
            SavesystemPatch.savedatapathreplacement = SavesystemPatch.vanillasavefolder;
            force = false;
            PlayerCamera.main.body = originalLocalBody;
        }
    }
}

/// <summary>
/// 拦截主菜单 <see cref="PreRunScript.Start"/>，同步更新存档目录替换路径以刷新继续游戏按钮状态。
/// </summary>
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.Start))]
public static class PreRunScript_Start_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) =>
        SavesystemPatch.TranspilerToReplaceTheApplicationPath(instructions);

    static void Prefix() {
        SavesystemPatch.savedatapathreplacement = SavesystemPatch.GetTheOnePersistentDataPathWithSaveFile();
    }
}
