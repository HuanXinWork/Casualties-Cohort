using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Newtonsoft.Json.Linq;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机存档路径重定向、世界进度预读、IL 路径替换器以及世界地形/流体二维数组序列化辅助类。
/// </summary>
public static partial class SavesystemPatch {
    public static string savedatapathreplacement = "";

    // 地图调试与教学关置为 true，避免覆盖真实联机存档 mp_save
    internal static bool PreserveSave;

    // 调试地图启用 DebugWorldSaveSystem 后，防止被读取存档时的旧规则覆盖关闭
    internal static bool DebugWorldUsesSave;

    // 严格保留字段名与静态签名，供 TogetherSaveWorldApi 与外部模组（如 CuSaveManager）反射绑定
    internal static bool PendingLoadMpSave;

    const string _mpsavefolder_name = "mp_save";
    public static string mpsavefolder => Path.Combine(Application.persistentDataPath, _mpsavefolder_name);
    public static string vanillasavefolder => Application.persistentDataPath;
    public const string mpsaveheaderfile = "mp_rules.json";

    internal struct MpWorldPeek {
        public int biomeDepth;
        public int totalTraveled;
        public float lootRarity;
        public float trapRarity;
        public bool midlevel;
        public bool ok;
    }

    /// <summary>
    /// 在生成世界前预读联机存档的层级深度与倍率配置（层中检查点存储原始 biomeDepth，过关存档存储 depth + 1）。
    /// </summary>
    internal static bool TryPeekMpWorldState(out MpWorldPeek peek) {
        peek = default;
        try {
            bool midlevel = false;
            string rulesPath = Path.Combine(mpsavefolder, mpsaveheaderfile);
            if (File.Exists(rulesPath)) {
                JObject rules = JObject.Parse(File.ReadAllText(rulesPath));
                if (rules.TryGetValue("LEVEL", out var lvl) && lvl != null && lvl.Type != JTokenType.Null)
                    midlevel = lvl.Value<bool>();
            }

            string savePath = Path.Combine(mpsavefolder, "save.sv");
            if (!File.Exists(savePath) && Directory.Exists(mpsavefolder)) {
                foreach (string dir in Directory.GetDirectories(mpsavefolder)) {
                    string candidate = Path.Combine(dir, "save.sv");
                    if (File.Exists(candidate)) {
                        savePath = candidate;
                        break;
                    }
                }
            }
            if (!File.Exists(savePath))
                return false;

            JObject root = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(savePath)));
            if (root["biome"] == null)
                return false;

            int stored = (int)root["biome"];
            int depth = midlevel ? stored : Math.Max(0, stored - 1);
            if (WorldGeneration.world != null && WorldGeneration.world.amountOfLayers > 0)
                depth = Mathf.Clamp(depth, 0, WorldGeneration.world.amountOfLayers - 1);

            peek.biomeDepth = depth;
            peek.midlevel = midlevel;
            peek.totalTraveled = root["totalTraveled"] != null ? (int)root["totalTraveled"] : 0;
            peek.lootRarity = root["lootRarity"] != null ? (float)root["lootRarity"] : 1f;
            peek.trapRarity = root["trapRarity"] != null ? (float)root["trapRarity"] : 1f;
            peek.ok = true;
            return true;
        } catch (Exception ex) {
            ModLog.Warning($"TryPeekMpWorldState: {ex.Message}", LogDomain.World);
            return false;
        }
    }

    internal static void ApplyPeekedWorldState(WorldGeneration targetWorld, in MpWorldPeek peek) {
        if (targetWorld == null || !peek.ok)
            return;
        targetWorld.debugStartDepth = peek.biomeDepth;
        targetWorld.biomeDepth = peek.biomeDepth;
        if (peek.totalTraveled > 0)
            targetWorld.totalTraveled = peek.totalTraveled;
        if (peek.lootRarity > 0f)
            targetWorld.lootRarityMultiplier = peek.lootRarity;
        if (peek.trapRarity > 0f)
            targetWorld.trapRarityMultiplier = peek.trapRarity;
    }

    public static string ReplacementFor_Application_persistentDataPath() {
        if (!Net.IsRunning) {
            return Application.persistentDataPath;
        }
        return savedatapathreplacement;
    }

    public static bool HasAnySaveFile() {
        return HasVanillaSaveFile() || HasMultiplayerSaveFile();
    }

    public static bool HasOnlyVanillaSaveFile() {
        return HasVanillaSaveFile() && !HasMultiplayerSaveFile();
    }

    public static bool HasVanillaSaveFile() {
        return File.Exists(Path.Combine(vanillasavefolder, "save.sv"));
    }

    public static bool HasMultiplayerSaveFile() {
        if (File.Exists(Path.Combine(mpsavefolder, "save.sv")))
            return true;
        if (!Directory.Exists(mpsavefolder))
            return false;
        foreach (string dir in Directory.GetDirectories(mpsavefolder)) {
            if (File.Exists(Path.Combine(dir, "save.sv")))
                return true;
        }
        return false;
    }

    public static void UpdatePersistentDataPathCuzMultiplayer() {
        savedatapathreplacement = GetTheOnePersistentDataPathWithSaveFile();
        if (Util.IsInMainMenu()) {
            PreRunScript prs = PreRunScript.instance;
            if (prs != null) {
                prs.loadButton.interactable = SaveSystem.HasSave();
            }
        }
    }

    internal static string GetTheOnePersistentDataPathWithSaveFile() {
        if (Net.IsRunning && HasMultiplayerSaveFile()) {
            return mpsavefolder;
        }
        return Application.persistentDataPath;
    }

    internal static string GetPersistentDataPathToSave() {
        if (Net.IsRunning) {
            return mpsavefolder;
        }
        return Application.persistentDataPath;
    }

    internal static void DeleteMPSave() {
        PendingLoadMpSave = false;
        if (Directory.Exists(mpsavefolder)) {
            try {
                Directory.Delete(mpsavefolder, true);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "DeleteMPSave", ex);
            }
        }
    }

    internal static IEnumerable<CodeInstruction> TranspilerToReplaceTheApplicationPath(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);
        var replacementMethod = AccessTools.Method(typeof(SavesystemPatch), nameof(ReplacementFor_Application_persistentDataPath));
        for (int i = 0; i < codes.Count; i++) {
            CodeInstruction code = codes[i];
            if (code.ToString().Contains("get_persistentDataPath")) {
                code.operand = replacementMethod;
            }
        }
        return codes;
    }

    internal static void DeleteSaveIfSingleplayer(string path) {
        if (!Net.IsRunning && !string.IsNullOrEmpty(path) && File.Exists(path)) {
            File.Delete(path);
        }
    }

    internal static IEnumerable<CodeInstruction> StripSaveDelete(IEnumerable<CodeInstruction> instructions) {
        var delete = AccessTools.Method(typeof(File), nameof(File.Delete), new Type[] { typeof(string) });
        var gatedDelete = AccessTools.Method(typeof(SavesystemPatch), nameof(DeleteSaveIfSingleplayer));
        var codes = new List<CodeInstruction>(instructions);
        for (int i = 0; i < codes.Count; i++) {
            if (delete != null && codes[i].Calls(delete)) {
                codes[i].opcode = OpCodes.Call;
                codes[i].operand = gatedDelete;
            }
        }
        return codes;
    }

    public static WorldGeneration world => WorldGeneration.world;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static byte[,] fluid => FluidManager.main.fluid;

    /// <summary>
    /// 按原版 <c>worldBlocks[width, height]</c> 的真实水平跨度 <c>width</c> 执行行主序展平压缩（修正旧版误乘 <c>height</c> 导致的非方阵地图越界/错位缺陷）。
    /// </summary>
    public static byte[] SerializeWorldBlocks() {
        int width = (int)world.width;
        int height = (int)world.height;
        byte[] byteblocks = new byte[width * height];
        ushort[,] blocks = worldBlocks;

        for (int y = 0; y < height; y++) {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++) {
                byteblocks[rowOffset + x] = (byte)blocks[x, y];
            }
        }

        return Util.Compress(byteblocks);
    }

    /// <summary>
    /// 按行主序跨度 <c>width</c> 解压还原 <c>worldBlocks[width, height]</c> 并刷新世界网格。
    /// </summary>
    public static void DeserializeWorldBlocks(in byte[] mapdata) {
        try {
            byte[] mapblocks = Util.Decompress(mapdata);
            int width = (int)world.width;
            int height = (int)world.height;
            if (mapblocks == null || mapblocks.Length < width * height) {
                ModLog.Error($"DeserializeWorldBlocks size mismatch: got {mapblocks?.Length ?? 0}, expected {width * height}", LogDomain.World);
                return;
            }

            ushort[,] blocks = worldBlocks;
            for (int y = 0; y < height; y++) {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++) {
                    blocks[x, y] = mapblocks[rowOffset + x];
                }
            }

            world.UpdateWorld();
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(DeserializeWorldBlocks), ex);
        }
    }

    /// <summary>
    /// 按原版 <c>fluid[width, height]</c> 的真实水平跨度 <c>width</c> 执行行主序展平压缩。
    /// </summary>
    public static byte[] SerializeWorldFluids() {
        int width = (int)world.width;
        int height = (int)world.height;
        byte[] bytefluids = new byte[width * height];
        byte[,] f = fluid;

        for (int y = 0; y < height; y++) {
            int rowOffset = y * width;
            for (int x = 0; x < width; x++) {
                bytefluids[rowOffset + x] = f[x, y];
            }
        }

        return Util.Compress(bytefluids);
    }

    /// <summary>
    /// 按行主序跨度 <c>width</c> 解压还原 <c>fluid[width, height]</c> 并刷新世界流体表现。
    /// </summary>
    public static void DeserializeWorldFluids(in byte[] mapdata) {
        try {
            byte[] mapfluids = Util.Decompress(mapdata);
            int width = (int)world.width;
            int height = (int)world.height;
            if (mapfluids == null || mapfluids.Length < width * height) {
                ModLog.Error($"DeserializeWorldFluids size mismatch: got {mapfluids?.Length ?? 0}, expected {width * height}", LogDomain.World);
                return;
            }

            byte[,] f = fluid;
            for (int y = 0; y < height; y++) {
                int rowOffset = y * width;
                for (int x = 0; x < width; x++) {
                    f[x, y] = mapfluids[rowOffset + x];
                }
            }

            world.UpdateWorld();
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(DeserializeWorldFluids), ex);
        }
    }

#if !RELEASE
    public static byte[] SerializeWorldObjects() {
        return Array.Empty<byte>();
    }
#endif
}
