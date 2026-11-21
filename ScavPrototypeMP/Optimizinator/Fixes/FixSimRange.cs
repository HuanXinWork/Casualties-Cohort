using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using Together;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace KrokV5Optimization;

/// <summary>
/// 原版模拟网格默认硬编码为 16x16 且蜘蛛、抓人花与 <see cref="IKHandle"/> 仅依赖房主相机区块渲染器开关。
/// 联机下按实际地图尺寸动态扩容网格，每位玩家标记自身周围 <see cref="WindowRadius"/> 区块并集作为模拟判据，
/// 并在房主端同步点亮客机周围区块渲染器以驱动 <see cref="ChunkScript.Update"/>；类与补丁方法保持 <c>public</c> 防止 Mono DynamicMethod JIT 权限崩溃。
/// </summary>
public static class FixSimRange {
    internal static long Grids;

    // 覆盖客机周围 4 格区块（9x9 窗口），匹配原版相机视窗外扩范围
    public const int WindowRadius = 4;

    static FieldInfo _table;
    static bool _gateMiss;

    // this file's counters on the 10s report.
    internal static string Evidence() => "simGrid=" + Grids;

    // 原版初始网格仅 16x16，深层大地图越界会被跳过导致远处刚体休眠冻结，按当前世界区块宽高动态对齐容量
    static void Fit(WorldGeneration world) {
        if (world == null || !world.worldExists)
            return;
        int w = (int)world.chunkWidth;
        int h = (int)world.chunkHeight;
        if (w < 1 || h < 1)
            return;
        if (_table == null)
            _table = AccessTools.Field(typeof(SharedMain), "CHUNKS_VISIBLE_TO_PLAYERS");
        if (_table == null)
            return;
        bool[,] have = _table.GetValue(null) as bool[,];
        if (have != null && have.GetLength(0) == w && have.GetLength(1) == h)
            return;
        _table.SetValue(null, new bool[w, h]);
    }

    // false skips stock _UpdatePlayerVisibleChunks. we clear the table and stamp the window per player ourselves.
    public static bool Rebuild() {
        WorldGeneration world = WorldGeneration.world;
        Fit(world);
        bool[,] table = Table();
        if (table == null)
            return true;
        int width = table.GetLength(0);
        int height = table.GetLength(1);
        for (int y = 0; y < height; y++) {
            for (int x = 0; x < width; x++)
                table[x, y] = false;
        }
        if (world == null || !world.worldExists)
            return false;
        int players = 0;
        foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Value == null)
                continue;
            Mark(table, world, pair.Value.pos);
            players++;
        }
        // menu or a tick with no bodies yet. stamp the camera so the local world still simulates.
        if (players == 0 && Camera.main != null)
            Mark(table, world, Camera.main.transform.position);
        // thrown stuff, a fired hook, and a lit stick of dynamite stamp the same window a player would.
        SimKeep.Stamp(table, world);
        Grids++;
        return false;
    }

    // in mp, ignore the host renderer. offline returns true and stock keeps its own test.
    public static bool Range(Vector2 pos, ref bool __result) {
        if (!Net.IsRunning)
            return true;
        if (WorldGeneration.world == null) {
            __result = false;
            return false;
        }
        __result = SharedMain.PositionIsInSimulatedChunk(pos);
        return false;
    }

    // first get_enabled in the spider update was "is my chunk renderer on". replace that with WorldInSimRange.
    [HarmonyPatch(typeof(SpiderHandler), "Update")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Spider(IEnumerable<CodeInstruction> instructions) {
        return Gate(instructions, "SpiderHandler.Update");
    }

    // same renderer check as the spider.
    [HarmonyPatch(typeof(GrabberPlant), "Update")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Plant(IEnumerable<CodeInstruction> instructions) {
        return Gate(instructions, "GrabberPlant.Update");
    }

    // PR #66 & v5.5.4：保留原版区块渲染器布尔值并按位或叠加 WorldInSimRange，防止房主视距外客机旁生物移动导致触手/蛛腿顶点停更拉长，同时完整保留原指令标签与异常块。
    [HarmonyPatch(typeof(IKHandle), "Update")]
    [HarmonyTranspiler]
    public static IEnumerable<CodeInstruction> Handle(IEnumerable<CodeInstruction> instructions) {
        return GateEither(instructions, "IKHandle.Update");
    }

    // CHUNKS_VISIBLE_TO_PLAYERS is the bool grid stock already consults.
    static bool[,] Table() {
        if (_table == null)
            _table = AccessTools.Field(typeof(SharedMain), "CHUNKS_VISIBLE_TO_PLAYERS");
        return _table != null ? _table.GetValue(null) as bool[,] : null;
    }

    // chunk under pos, plus WindowRadius each way. out of range indexes are skipped.
    public static void Mark(bool[,] table, WorldGeneration world, Vector2 pos) {
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int maxX = table.GetLength(0) - 1;
        int maxY = table.GetLength(1) - 1;
        for (int y = chunk.y - WindowRadius; y <= chunk.y + WindowRadius; y++) {
            if ((uint)y > (uint)maxY)
                continue;
            for (int x = chunk.x - WindowRadius; x <= chunk.x + WindowRadius; x++) {
                if ((uint)x > (uint)maxX)
                    continue;
                table[x, y] = true;
            }
        }
    }

    // pop the renderer, push WorldInSimRange(transform.position). only the first get_enabled is the chunk check.
    static IEnumerable<CodeInstruction> Gate(IEnumerable<CodeInstruction> instructions, string name) {
        var list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++) {
            if (list[i].opcode != OpCodes.Callvirt || list[i].operand is not MethodInfo method)
                continue;
            if (method.Name != "get_enabled")
                continue;
            var swap = new CodeInstruction(OpCodes.Pop);
            if (list[i].labels != null)
                swap.labels.AddRange(list[i].labels);
            if (list[i].blocks != null)
                swap.blocks.AddRange(list[i].blocks);
            list[i] = swap;
            list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Component), "transform")));
            list.Insert(i + 3, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Transform), "position")));
            list.Insert(i + 4, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Vector2), "op_Implicit", new[] { typeof(Vector3) })));
            list.Insert(i + 5, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))));
            return list;
        }
        if (!_gateMiss) {
            _gateMiss = true;
            OptimizerMain.Log.LogWarning("[KrokV5Opt] sim range gate missed " + name);
        }
        return list;
    }

    // leave the chunk renderer bool, then OR WorldInSimRange. only the first Renderer.get_enabled.
    static IEnumerable<CodeInstruction> GateEither(IEnumerable<CodeInstruction> instructions, string name) {
        List<CodeInstruction> list = new List<CodeInstruction>(instructions);
        for (int i = 0; i < list.Count; i++) {
            if (list[i].opcode != OpCodes.Callvirt || list[i].operand is not MethodInfo method)
                continue;
            if (method.Name != "get_enabled" || method.DeclaringType != typeof(Renderer))
                continue;
            list.Insert(i + 1, new CodeInstruction(OpCodes.Ldarg_0));
            list.Insert(i + 2, new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Component), "transform")));
            list.Insert(i + 3, new CodeInstruction(OpCodes.Callvirt, AccessTools.PropertyGetter(typeof(Transform), "position")));
            list.Insert(i + 4, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Vector2), "op_Implicit", new[] { typeof(Vector3) })));
            list.Insert(i + 5, new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))));
            list.Insert(i + 6, new CodeInstruction(OpCodes.Or));
            return list;
        }
        if (!_gateMiss) {
            _gateMiss = true;
            OptimizerMain.Log.LogWarning("[KrokV5Opt] sim range gate missed " + name);
        }
        return list;
    }
}

// 原版 UpdateChunkVisibility 仅遍历房主本地相机，补充开启客机周围窗口的区块渲染器以确保 ChunkScript.Update 正常推进
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateChunkVisibility))]
public static class UpdateChunkVisibility_ClientWindows_MultiplayerPatch {
    static HashSet<long> Now = new HashSet<long>();
    static HashSet<long> Prev = new HashSet<long>();

    public static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.generatingWorld)
            return;
        TilemapRenderer[,] chunks = __instance.renderChunks;
        if (chunks == null)
            return;

        Now.Clear();
        if (__instance.mainCam != null)
            Cover(__instance, __instance.mainCam.transform.position);
        foreach (KeyValuePair<Body, ScavPlayer> pair in ScavPlayer.BodyToPlayerDict) {
            if (pair.Value == null)
                continue;
            Cover(__instance, pair.Value.pos);
        }

        int maxX = chunks.GetLength(0);
        int maxY = chunks.GetLength(1);
        foreach (long key in Now)
            Set(chunks, key, maxX, maxY, true);
        foreach (long key in Prev) {
            if (!Now.Contains(key))
                Set(chunks, key, maxX, maxY, false);
        }

        HashSet<long> swap = Prev;
        Prev = Now;
        Now = swap;
    }

    static void Cover(WorldGeneration world, Vector2 pos) {
        Vector2Int chunk = world.BlockToChunkPos(world.WorldToBlockPos(pos));
        int maxX = world.renderChunks.GetLength(0) - 1;
        int maxY = world.renderChunks.GetLength(1) - 1;
        int radius = FixSimRange.WindowRadius;
        for (int y = chunk.y - radius; y <= chunk.y + radius; y++) {
            if ((uint)y > (uint)maxY)
                continue;
            for (int x = chunk.x - radius; x <= chunk.x + radius; x++) {
                if ((uint)x > (uint)maxX)
                    continue;
                Now.Add(((long)x << 32) | (uint)y);
            }
        }
    }

    static void Set(TilemapRenderer[,] chunks, long key, int maxX, int maxY, bool enabled) {
        int x = (int)(key >> 32);
        int y = (int)key;
        if ((uint)x >= (uint)maxX || (uint)y >= (uint)maxY)
            return;
        TilemapRenderer renderer = chunks[x, y];
        if (renderer != null && renderer.enabled != enabled)
            renderer.enabled = enabled;
    }
}
