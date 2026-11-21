using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using UnityEngine;

namespace Together;

#region FLUID_OUTSIDE_OF_HOST_VIEW

/// <summary>
/// 覆写流体管理器的单步模拟区间查询，使主机可环绕所有存活玩家所在区域轮转执行流体物理模拟。
/// </summary>
[HarmonyPatch(typeof(FluidManager), nameof(FluidManager.SimulationRangeIndex))]
public static class FluidManager_SimulationRangeIndex_MultiplayerPatch {
    public static bool forcenext_avaiable = false;
    public static (RangeI, RangeI) forcenext;

    static bool Prefix(ref (RangeI, RangeI) __result) {
        if (forcenext_avaiable) {
            forcenext_avaiable = false;
            __result = forcenext;
            return false;
        }
        return true;
    }
}

/// <summary>
/// 主机端驱动所有玩家周围的流体分区模拟，避免远离主机镜头的客机所处区域液体冻结。
/// </summary>
[HarmonyPatch(typeof(FluidManager), "FixedUpdate")]
public static class FluidManager_FixedUpdate_MultiplayerPatch {
    private static int simIndex = 0;

    static bool Prefix() {
        if (Net.IsRunning && NetBody.all_instances.Count > 0 && Net.IsServer && WorldGeneration.world != null) {
            HashSet<Vector2UInt8> vecs = new();

            foreach (var body in NetBody.all_instances) {
                if (body == null) {
                    continue;
                }
                Vector2 pos = (Vector2)WorldGeneration.world.WorldToBlockPos(body.transform.position) / (float)WorldChunkSync.FLUIDSIMULATIONZONESIZE;

                vecs.Add((Vector2UInt8)pos);
                int px = WorldChunkSync._CheckTheProxNum(pos.x);
                int py = WorldChunkSync._CheckTheProxNum(pos.y);
                vecs.Add((Vector2UInt8)new Vector2(pos.x + px, pos.y));
                vecs.Add((Vector2UInt8)new Vector2(pos.x, pos.y + py));
                vecs.Add((Vector2UInt8)new Vector2(pos.x + px, pos.y + py));
            }

            int maxFluidSimZonesDimension = WorldChunkSync.WORLDSIZE / WorldChunkSync.FLUIDSIMULATIONZONESIZE;

            foreach (var fsimchunk in vecs) {
                if (fsimchunk.x > maxFluidSimZonesDimension - 1 || fsimchunk.y > maxFluidSimZonesDimension - 1) {
                    continue;
                }

                Vector2Int blockpos = new Vector2Int(
                    fsimchunk.x * WorldChunkSync.FLUIDSIMULATIONZONESIZE,
                    fsimchunk.y * WorldChunkSync.FLUIDSIMULATIONZONESIZE);
                RangeI item = new RangeI(blockpos.x, blockpos.x + WorldChunkSync.FLUIDSIMULATIONZONESIZE);
                RangeI item2 = new RangeI(blockpos.y + simIndex, blockpos.y + 16 + simIndex);

                // 补齐顶部 16 格高度带，防止流体模拟遗漏区块上沿
                if (simIndex + 16 >= 112) {
                    item2.max += 16;
                }

                int maxW = (int)(WorldGeneration.world.width - 2);
                int maxH = (int)(WorldGeneration.world.height - 2);
                if (item.min < 1) item.min = 1;
                if (item2.min < 1) item2.min = 1;
                if (item.max < 1) item.max = 1;
                if (item2.max < 1) item2.max = 1;
                if (item.max > maxW) item.max = maxW;
                if (item2.max > maxH) item2.max = maxH;
                if (item.min > maxW) item.min = maxW;
                if (item2.min > maxH) item2.min = maxH;

                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext_avaiable = true;
                FluidManager_SimulationRangeIndex_MultiplayerPatch.forcenext = (item, item2);
                FluidManager.main.SimulationStep();
            }

            simIndex += 16;
            if (simIndex >= 112) {
                simIndex = 0;
            }
            return false;
        }
        return true;
    }
}

#endregion

/// <summary>
/// 世界地形瓦片与流体区块增量对账同步核心（生命周期、坐标换算与 FNV-1a 区域哈希）。
/// 网络打包、节流轮询与数据包接收器见 <c>TilemapSync.Network.cs</c>。
/// </summary>
public partial class WorldChunkSync : CasualtiesTogetherSingleton {
    public static WorldChunkSync singleton;

    public static bool _TILE_SYNC_ENABLED = true;
    public static bool _FLUID_SYNC_ENABLED = true;

    public static int _fluid_chunksync_counter = 0;
    public static int _chunksync_counter = 0;

    private const float TilemapSyncFrequency = 1.061102f;
    private const float TilemapFluidSyncFrequency = 0.035067f;

    public float timer_TilemapSync = 0f;
    public float timer_TilemapFluidSync = 0f;

    public static WorldGeneration world => WorldGeneration.world;

    public const int SYNCCHUNK_TILESIZE = 32;
    public const int SYNCCHUNK_DIMENSION = 16 * 2;
    public const int SYNCREGION_SIZE = 8;
    public const int HALF_SYNCREGION_SIZE = SYNCREGION_SIZE / 2;

    public const int WORLDSIZE = 1024;
    public const int TILEID_BITSIZE = 6;
    public const int FLUIDID_BITSIZE = 3;

    public const int FLUIDSIMULATIONZONESIZE = 128;
    public const int FLUIDCHUNKSIZE = 64;
    public const int HALF_FLUIDCHUNKSIZE = FLUIDCHUNKSIZE / 2;

    public static int CHUNKSIZE => WorldGeneration.CHUNKSIZE;
    public static ushort[,] worldBlocks => WorldGeneration.world.worldBlocks;
    public static bool instantiatingWorld => WorldGeneration.world.instantiatingWorld;
    public static byte[,] fluid => FluidManager.main.fluid;

    public static uint[,] Server_ChunkHashes = new uint[SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE];

    public static readonly List<(knetid, float, Vector2UInt8)> server_chunksync_queue = new();

    void Awake() {
        singleton = this;
    }

    void Start() {
        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {
            Server_ChunkHashes = KrokV5Optimization.FixChunkHashes.Rent(SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE);
        };
    }

    void LateUpdate() {
        if (!Net.IsRunning || !Util.IsWorldGenerated()) {
            return;
        }

        WorldGeneration_SetBlock_MultiplayerPatch.FlushChangedBlocks();

        if (world.savePanel.activeSelf) {
            if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
                PlayerCamera.main.body.forceWalk = false;
            }

            if (Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
                world.savePanel.SetActive(value: false);
            } else {
                var plr = ScavPlayer.LocalPlayer;
                if (plr && !world.doingRegen && !world.generatingWorld && world.worldExists && !plr.finishedLayer) {
                    plr.finishedLayer = true;
                    // 房主已死亡且出口人数达标时由 DeadHostExitPanel 统一接管提示，抑制常规等待横幅避免重复提示
                    bool hostDown = ServerMain.HostBodyIsDead() && ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
                    if (!hostDown && ServerMain.GetNumberOfPlayersRequiredToFinishLayer() != 1) {
                        PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_friends"));
                    } else if (!hostDown) {
                        PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_host"));
                    }
                }

                if (Net.IsClient) {
                    // 原版每帧对站在出口的角色打开该面板，联机客户端始终隐藏本地原生面板
                    world.savePanel.SetActive(value: false);
                }
            }
        }

        if (Net.IsServer) {
            WorldGeneration_Update_DeadHostExitPanel.ApplyHostExitPanel(world);
        }

        if (Net.IsServer) {
            int maxDispatch = Mathf.Max(ScavPlayer.ClientIdToPlayerDict.Count / 2, 1);
            for (int i = 0; i < maxDispatch; i++) {
                if (server_chunksync_queue.Count <= 0) {
                    break;
                }
                var currentChunk = server_chunksync_queue[0];
                server_chunksync_queue.RemoveAt(0);
                if (ScavPlayer.TryGetPlayerFromClientId(currentChunk.Item1, out _)) {
                    Server_Sendchunk(currentChunk.Item3, currentChunk.Item1, false);
                }
            }

            Server_ChunkHashes = KrokV5Optimization.FixChunkHashes.Rent(SYNCCHUNK_TILESIZE, SYNCCHUNK_TILESIZE);
        }

        timer_TilemapSync += ClientMain.AdaptiveSyncTimerDelta;
        timer_TilemapFluidSync += ClientMain.AdaptiveSyncTimerDelta;

        if (timer_TilemapSync > TilemapSyncFrequency) {
            timer_TilemapSync = 0f;
            TilemapSyncUpdate();
        }
        if (timer_TilemapFluidSync > TilemapFluidSyncFrequency) {
            timer_TilemapFluidSync = 0f;
            FluidTilemapSyncUpdate();
        }
    }

    /// <summary>
    /// 计算指定方块矩形区域的 64-bit FNV-1a 折叠哈希，并在低 10 位混入非空方块计数以降低碰撞率。
    /// </summary>
    public static uint HashRegion(in ushort[,] arr, int xmin, int xmax, int ymin, int ymax) {
        const ulong fnvOffset = 1469598103934665603UL;
        const ulong fnvPrime = 1099511628211UL;

        ulong hash = fnvOffset;
        ushort counter = 0;

        for (int y = ymin; y < ymax; y++) {
            for (int x = xmin; x < xmax; x++) {
                ushort v = arr[x, y];
                if (v != 0) {
                    counter++;
                }

                hash ^= (byte)v;
                hash *= fnvPrime;

                hash ^= (byte)(v >> 8);
                hash *= fnvPrime;
            }
        }

        uint folded = (uint)(hash ^ (hash >> 32));
        return (folded << 10) | counter;
    }

    public static Vector2UInt8 WorldPosToSyncchunkCoordinate(Vector2 pos) {
        return BlockPosToSyncchunkCoordinate(WorldGeneration.world.WorldToBlockPos(pos));
    }

    public static Vector2UInt8 WorldPosToSyncchunkCoordinateSafe(Vector2 pos) {
        return BlockPosToSyncchunkCoordinateSafe(WorldGeneration.world.WorldToBlockPos(pos));
    }

    public static Vector2UInt8 BlockPosToSyncchunkCoordinate(Vector2Int blockpos) {
        return new Vector2UInt8((byte)(blockpos.x / SYNCCHUNK_TILESIZE), (byte)(blockpos.y / SYNCCHUNK_TILESIZE));
    }

    public static Vector2UInt8 BlockPosToSyncchunkCoordinateSafe(Vector2Int blockpos) {
        Vector2Int cornerpos = new Vector2Int(
            blockpos.x / SYNCCHUNK_TILESIZE,
            blockpos.y / SYNCCHUNK_TILESIZE);
        cornerpos.Clamp(
            new Vector2Int(0, 0),
            new Vector2Int(SYNCCHUNK_DIMENSION - 1, SYNCCHUNK_DIMENSION - 1));
        return (Vector2UInt8)cornerpos;
    }

    public static int _CheckTheProxNum(float n) {
        float d = n - Mathf.Floor(n);
        if (d < 0.4f) {
            return -1;
        }
        if (d > 0.6f) {
            return 1;
        }
        return 0;
    }

    byte[,] GetChunkData(Vector2Int chunk) {
        Vector2Int realchunkpos = chunk * CHUNKSIZE;
        byte[,] chunkdata = new byte[CHUNKSIZE, CHUNKSIZE];
        for (int i = 0; i < CHUNKSIZE; i++) {
            for (int j = 0; j < CHUNKSIZE; j++) {
                chunkdata[i, j] = (byte)worldBlocks[i + realchunkpos.x, j + realchunkpos.y];
            }
        }
        return chunkdata;
    }

    byte[,] GetFluidChunkData(Vector2Int lefttopcorner) {
        byte[,] chunkdata = new byte[FLUIDCHUNKSIZE, FLUIDCHUNKSIZE];
        for (int i = 0; i < FLUIDCHUNKSIZE; i++) {
            for (int j = 0; j < FLUIDCHUNKSIZE; j++) {
                chunkdata[i, j] = fluid[i + lefttopcorner.x, j + lefttopcorner.y];
            }
        }
        return chunkdata;
    }
}
