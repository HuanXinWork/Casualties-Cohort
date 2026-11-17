using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// <see cref="WorldChunkSync"/> 的网络区块压缩打包、周期广播对账与收包解码部分。
/// </summary>
public partial class WorldChunkSync {
    private const int MaxSyncQueueSortInsert = 100;
    private const int MaxSyncQueueHardCap = 200;

    public static void Server_Sendchunk(Vector2UInt8 syncchunk_coordinate, knetid plrId, bool reliable = false) {
        if (!ScavPlayer.TryGetPlayerFromClientId(plrId, out var plr)) {
            return;
        }
        Server_Sendchunk(syncchunk_coordinate, plr, reliable);
    }

    public static void Server_Sendchunk(Vector2UInt8 syncchunk_coordinate, ScavPlayer plr, bool reliable = false) {
        if (plr == null) {
            return;
        }
        if (!KrokV5Optimization.FixChunkBlock.AllowStock(syncchunk_coordinate, plr, reliable)) {
            return;
        }

        var tiles = worldBlocks;
        if (tiles == null) {
            return;
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_WorldTilemapChunk);
        writer.Put(syncchunk_coordinate.x);
        writer.Put(syncchunk_coordinate.y);

        int blockposX = syncchunk_coordinate.x * SYNCCHUNK_TILESIZE;
        int blockposY = syncchunk_coordinate.y * SYNCCHUNK_TILESIZE;

        using var outputstream = new MemoryStream();
        using (var deflateStream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            using var inputstream = new MemoryStream();
            for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                    inputstream.WriteByte((byte)tiles[x + blockposX, y + blockposY]);
                }
            }
            byte[] raw = inputstream.ToArray();
            deflateStream.Write(raw, 0, raw.Length);
        }
        writer.PutBytesWithLength(outputstream.ToArray());

        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, plr.playerId);

        if (plr.ServerPlayerState?.known_chunks != null) {
            plr.ServerPlayerState.known_chunks[syncchunk_coordinate.x, syncchunk_coordinate.y] = true;
        }
    }

    public static NetDataWriter _Server_PackFluidChunk(in Vector2UInt8 syncchunk_coordinate) {
        if (KrokV5Optimization.FixChunkFluid.TryReplace(syncchunk_coordinate, out NetDataWriter packed)) {
            return packed;
        }

        int blockposX = syncchunk_coordinate.x * SYNCCHUNK_TILESIZE;
        int blockposY = syncchunk_coordinate.y * SYNCCHUNK_TILESIZE;

        var writer = Net.CreateWriter(NetmsgId.CLIENT_WorldFluidTilemapChunk);
        writer.Put(syncchunk_coordinate.x);
        writer.Put(syncchunk_coordinate.y);

        var f = fluid;
        using var outputstream = new MemoryStream();
        using (var deflateStream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            using var inputstream = new MemoryStream();
            for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
                for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                    inputstream.WriteByte(f[x + blockposX, y + blockposY]);
                }
            }
            byte[] raw = inputstream.ToArray();
            deflateStream.Write(raw, 0, raw.Length);
        }
        writer.PutBytesWithLength(outputstream.ToArray());
        return writer;
    }

    public static void Server_SendFluidChunk(Vector2UInt8 syncchunk_coordinate, ScavPlayer plr, bool reliable = false) {
        if (plr == null || fluid == null) {
            return;
        }
        var writer = _Server_PackFluidChunk(syncchunk_coordinate);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, plr.playerId);
    }

    internal static void FluidTilemapSyncUpdate() {
        if (!Net.IsRunning || !_FLUID_SYNC_ENABLED) {
            return;
        }
        // 世界生成切换期间 fluid 数组正在重建，此时跳过打包避免空引用或越界
        if (WorldGeneration.world == null || WorldGeneration.world.generatingWorld || FluidManager.main == null || fluid == null) {
            return;
        }
        if (Net.IsClient) {
            return;
        }

        var plrBlockPositions = new List<Vector2Int>();
        foreach (var plr in ScavPlayer.AllPlayers) {
            if (plr == null || plr.IsLocal || !plr.body || !plr.body.alive) {
                continue;
            }

            Vector2 plrEdgePos = plr.pos - new Vector2(FLUIDCHUNKSIZE * 0.5f, FLUIDCHUNKSIZE * 0.5f);
            plrBlockPositions.Add(WorldGeneration.world.WorldToBlockPos(plrEdgePos));

            if (!plr.body.inWater && plr.body.liquidSlipTime <= 0 && plr.body.liquidRagdollBar >= 1 && plr.body.liquidDrinkTime <= 0) {
                Vector2Int bpUnder = WorldGeneration.world.WorldToBlockPos(plrEdgePos + new Vector2(0f, -FLUIDCHUNKSIZE));
                plrBlockPositions.Add(bpUnder);
            }
        }

        if (plrBlockPositions.Count <= 0) {
            return;
        }

        HashSet<Vector2UInt8> syncchunks = new();
        foreach (var chunk in plrBlockPositions) {
            Vector2Int p = new Vector2Int(chunk.x / SYNCCHUNK_TILESIZE, chunk.y / SYNCCHUNK_TILESIZE);
            for (int y = 0; y < 3; y++) {
                for (int x = 0; x < 3; x++) {
                    Vector2Int np = p + new Vector2Int(x, y);
                    np.Clamp(new Vector2Int(0, 0), new Vector2Int(SYNCCHUNK_DIMENSION - 1, SYNCCHUNK_DIMENSION - 1));
                    syncchunks.Add((Vector2UInt8)np);
                }
            }
        }

        if (_fluid_chunksync_counter >= syncchunks.Count) {
            _fluid_chunksync_counter = 0;
        }

        Vector2UInt8 curCenterPos = syncchunks.ElementAt(_fluid_chunksync_counter);
        _fluid_chunksync_counter++;

        var writer = _Server_PackFluidChunk(curCenterPos);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    internal static void TilemapSyncUpdate() {
        if (!Net.IsRunning || !_TILE_SYNC_ENABLED || world == null || worldBlocks == null) {
            return;
        }

        if (Net.IsClient) {
            Vector2 refPos = (PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.alive)
                ? (Vector2)PlayerCamera.main.body.transform.position
                : (Camera.main != null ? (Vector2)Camera.main.transform.position : Vector2.zero);

            Vector2UInt8 localChunkPos = WorldPosToSyncchunkCoordinateSafe(refPos);
            Vector2Int cornerpos = new Vector2Int(
                localChunkPos.x - HALF_SYNCREGION_SIZE,
                localChunkPos.y - HALF_SYNCREGION_SIZE - 1);
            cornerpos.Clamp(
                new Vector2Int(0, 0),
                new Vector2Int(SYNCCHUNK_DIMENSION - SYNCREGION_SIZE, SYNCCHUNK_DIMENSION - SYNCREGION_SIZE));

            var writer = Net.CreateWriter(NetmsgId.SERVER_HeyUhhMyChunksAreLikeThisBruh);
            writer.Put((byte)cornerpos.x);
            writer.Put((byte)cornerpos.y);

            var tiles = worldBlocks;
            for (int y = 0; y < SYNCREGION_SIZE; y++) {
                for (int x = 0; x < SYNCREGION_SIZE; x++) {
                    int blockposX = (cornerpos.x + x) * SYNCCHUNK_TILESIZE;
                    int blockposY = (cornerpos.y + y) * SYNCCHUNK_TILESIZE;
                    uint hash = HashRegion(
                        tiles,
                        blockposX,
                        blockposX + SYNCCHUNK_TILESIZE,
                        blockposY,
                        blockposY + SYNCCHUNK_TILESIZE);
                    writer.Put(hash);
                }
            }

            Net.Client_Send(DeliveryMethod.Unreliable, writer);
        } else {
            foreach (var plr in ServerMain.AllPlayersExceptHost) {
                Server_CheckPlrPosChunk(plr);
            }
        }
    }

    public static void Server_CheckPlrPosChunk(ScavPlayer plr) {
        if (plr == null || plr.ServerPlayerState?.known_chunks == null || world == null) {
            return;
        }

        Vector2UInt8 pos = WorldPosToSyncchunkCoordinateSafe(plr.pos - new Vector2(SYNCCHUNK_TILESIZE * 0.5f, 0f));
        for (int y = Mathf.Max(0, pos.y - 2); y < Mathf.Min(pos.y + 1, SYNCCHUNK_DIMENSION); y++) {
            for (int x = pos.x; x < Mathf.Min(pos.x + 2, SYNCCHUNK_DIMENSION); x++) {
                if (!plr.ServerPlayerState.known_chunks[x, y]) {
                    var coord = new Vector2UInt8(x, y);
                    Server_Sendchunk(coord, plr, false);
                    Server_SendFluidChunk(coord, plr, false);
                }
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_WorldFluidTilemapChunk)]
    static void ClientReceiver_WorldFluidTilemapChunk(knetid _, NetDataReader reader) {
        if (FluidManager.main == null || fluid == null) {
            return;
        }

        reader.Get(out byte cornersyncchunkX);
        reader.Get(out byte cornersyncchunkY);
        if (cornersyncchunkX >= SYNCCHUNK_DIMENSION || cornersyncchunkY >= SYNCCHUNK_DIMENSION) {
            return;
        }

        Vector2Int blockorigin = new Vector2Int(cornersyncchunkX, cornersyncchunkY) * SYNCCHUNK_TILESIZE;
        var f = fluid;

        reader.Get(out byte[] compressedChunkArray);
        byte[] chunkarray = Util.DecompressDeflate(compressedChunkArray);
        if (chunkarray == null || chunkarray.Length < SYNCCHUNK_TILESIZE * SYNCCHUNK_TILESIZE) {
            return;
        }

        int i = 0;
        for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
            for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                f[x + blockorigin.x, y + blockorigin.y] = chunkarray[i++];
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_WorldTilemapChunk)]
    static void ClientReceiver_WorldTilemapChunk(knetid _, NetDataReader reader) {
        if (world == null || instantiatingWorld) {
            return;
        }
        var tiles = worldBlocks;
        if (tiles == null) {
            return;
        }

        reader.Get(out byte cornersyncchunkX);
        reader.Get(out byte cornersyncchunkY);
        if (cornersyncchunkX >= SYNCCHUNK_DIMENSION || cornersyncchunkY >= SYNCCHUNK_DIMENSION) {
            return;
        }

        Vector2Int blockorigin = new Vector2Int(cornersyncchunkX, cornersyncchunkY) * SYNCCHUNK_TILESIZE;

        reader.Get(out byte[] compressedChunkArray);
        byte[] chunkarray = Util.DecompressDeflate(compressedChunkArray);
        if (chunkarray == null || chunkarray.Length < SYNCCHUNK_TILESIZE * SYNCCHUNK_TILESIZE) {
            return;
        }

        int i = 0;
        for (int y = 0; y < SYNCCHUNK_TILESIZE; y++) {
            for (int x = 0; x < SYNCCHUNK_TILESIZE; x++) {
                tiles[x + blockorigin.x, y + blockorigin.y] = chunkarray[i++];
            }
        }

        WorldGeneration.world.UpdateChunkClosest(blockorigin);
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(WorldGeneration.world.BlockToWorldPos(blockorigin), $"C: Received chunk data {cornersyncchunkX},{cornersyncchunkY}");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_HeyUhhMyChunksAreLikeThisBruh)]
    static void Server_HeyUhhMyChunksAreLikeThisBruh(knetid clientId, NetDataReader reader) {
        if (!Util.IsWorldGenerated()) {
            return;
        }
        var tiles = worldBlocks;
        if (tiles == null) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null) {
            return;
        }
        if (server_chunksync_queue.Count > MaxSyncQueueHardCap) {
            return;
        }

        reader.Get(out byte cornersyncchunkX);
        reader.Get(out byte cornersyncchunkY);

        Vector2Int cornerPos = new Vector2Int(cornersyncchunkX, cornersyncchunkY);
        cornerPos.Clamp(
            new Vector2Int(0, 0),
            new Vector2Int(SYNCCHUNK_DIMENSION - SYNCREGION_SIZE, SYNCCHUNK_DIMENSION - SYNCREGION_SIZE));
        if (cornerPos.x != cornersyncchunkX || cornerPos.y != cornersyncchunkY) {
            ModLog.Warning($"ChunksHash: {plr} 上报了越界的区块坐标 ({cornersyncchunkX},{cornersyncchunkY})", LogDomain.World);
            MpFlightRecorder.Record(LogDomain.World, FlightEventKind.PacketReject, clientId.id, detailCode: (byte)NetmsgId.SERVER_HeyUhhMyChunksAreLikeThisBruh);
            return;
        }

        Vector2Int plrposBlockspace = WorldGeneration.world.WorldToBlockPos(plr.pos);

        for (int y = 0; y < SYNCREGION_SIZE; y++) {
            for (int x = 0; x < SYNCREGION_SIZE; x++) {
                reader.Get(out uint hash);

                int chunkposX = cornerPos.x + x;
                int chunkposY = cornerPos.y + y;
                int blockposX = chunkposX * SYNCCHUNK_TILESIZE;
                int blockposY = chunkposY * SYNCCHUNK_TILESIZE;

                uint myHash = Server_ChunkHashes[chunkposX, chunkposY];
                if (myHash == 0) {
                    Server_ChunkHashes[chunkposX, chunkposY] = myHash = HashRegion(
                        tiles,
                        blockposX,
                        blockposX + SYNCCHUNK_TILESIZE,
                        blockposY,
                        blockposY + SYNCCHUNK_TILESIZE);
                }

                if (hash != myHash) {
                    Vector2 blockposvec2 = new Vector2(blockposX, blockposY);
                    float dist = KM.DistanceSqr(blockposvec2, plrposBlockspace);
                    var chunkPos = new Vector2UInt8(chunkposX, chunkposY);

                    Server_QueueChunkToSync(plr.playerId, dist, chunkPos);

                    if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                        DebugHelp.OnNetEvent(
                            WorldGeneration.world.BlockToWorldPos(new Vector2Int(blockposX, blockposY)),
                            $"S: {plr} chunk hash mismatch {cornersyncchunkX},{cornersyncchunkY}");
                    }
                }
            }
        }
    }

    static void Server_QueueChunkToSync(knetid plrId, float dist, Vector2UInt8 chunkPos) {
        var tupl = (plrId, dist, chunkPos);
        if (server_chunksync_queue.Any(x => x.Item1 == plrId && x.Item3 == chunkPos)) {
            return;
        }

        int i;
        for (i = 0; i < server_chunksync_queue.Count; i++) {
            if (server_chunksync_queue[i].Item2 < dist) {
                continue;
            }
            server_chunksync_queue.Insert(i, tupl);
            return;
        }
        if (i < MaxSyncQueueSortInsert) {
            server_chunksync_queue.Add(tupl);
        }
    }
}
