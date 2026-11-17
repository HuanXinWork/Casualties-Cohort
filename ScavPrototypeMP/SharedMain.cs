using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together {
    /// <summary>
    /// 主客端共用的世界模拟范围裁剪、存活玩家列表维护、离散惩罚规则判定与每秒帧率性能上报组件。
    /// </summary>
    public class SharedMain : MonoBehaviour {
        public static float max_player_interaction_distance = 9f;
        public static float max_player_interaction_distance_sq => max_player_interaction_distance * max_player_interaction_distance;

        public static bool LocalWorldIsGenerating = false;
        public static bool LocalWorldIsGenerated = false;
        public static bool IsInCutscene = false;
        public static double LastWorldgenFinishTime = 0;

        // 注意：字段名 CHUNKS_VISIBLE_TO_PLAYERS 被 FixSimRange 通过反射读取，严禁重命名
        static bool[,] CHUNKS_VISIBLE_TO_PLAYERS = new bool[16, 16];

        public static bool CheckIfDispersionPunishmentProtocolRuleIsActive() {
            return (Time.realtimeSinceStartupAsDouble - LastWorldgenFinishTime) > (Net.IsClient ? 10f : 20f)
                && ScavPlayer.AllLivingPlayers.Count > 0
                && Multiplayer.rules.PlayerScatterDisallowed
                && Multiplayer.rules.ScatterMinGroupSize > 0;
        }

        public static bool CheckIfShouldActivateDispersionPunishmentProtocol(ScavPlayer plr, out float max_recorded_dist, out int count_plrs_in_range, out int required_count_plrs_in_range) {
            count_plrs_in_range = ScavPlayer.AllLivingPlayers.Count;
            required_count_plrs_in_range = 0;
            max_recorded_dist = 0f;

            // 防护角色重生成或断线瞬间的空引用异常
            if (plr == null || plr.body == null || !plr.IsAlive() || plr.levelPlayTime < 20f) {
                return false;
            }
            if (count_plrs_in_range <= 1) {
                return false;
            }

            List<ScavPlayer> plrs = new List<ScavPlayer>(ScavPlayer.AllLivingPlayers);
            plrs.RemoveAll(x => x == null || x.body == null || x.levelPlayTime < 20f);
            if (Net.IsServer) {
                plrs.RemoveAll(x => x.ServerPlayerState == null || !x.ServerPlayerState.is_loaded_in);
            }
            if (Multiplayer.rules.Teams) {
                plrs.RemoveAll(x => !x.IsInSameTeamAs(plr));
            }
            if (plrs.Count <= 1) {
                return false;
            }

            required_count_plrs_in_range = Math.Max(1, (int)Mathf.Ceil(plrs.Count * Multiplayer.rules.ScatterMinGroupSize * 0.01f));

            float presqr = Util.MetersToTiles(Multiplayer.rules.ScatterPunishDistance);
            presqr *= presqr;
            Vector3 pos = plr.body.transform.position;
            count_plrs_in_range = 0;
            foreach (ScavPlayer x in ScavPlayer.AllLivingPlayers) {
                if (x == null || x.body == null) {
                    continue;
                }
                float d = KM.DistanceSqr(x.body.transform.position, pos);
                if (d < presqr) {
                    count_plrs_in_range++;
                    if (d > max_recorded_dist) {
                        max_recorded_dist = d;
                    }
                }
            }
            max_recorded_dist = Mathf.Sqrt(max_recorded_dist);

            return count_plrs_in_range < required_count_plrs_in_range;
        }

        public static void CreatePlayerCharacters() {
            if (!Util.IsInWorld()) {
                return;
            }

            foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                try {
#if FULLGAME
                    if (plr != null && CharacterSelectWait.IsPendingLateJoin(plr.playerId)) {
                        continue;
                    }
#endif
                    plr?.CreateCharacter();
                } catch (Exception e) {
                    ModLog.Exception(LogDomain.Bootstrap, "CreatePlayerCharacters", e);
                }
            }
        }

        public static void ForceUpdatePlayerLists() {
            ScavPlayer.AllLivingPlayers.Clear();
            ScavPlayer.AllDeadPlayers.Clear();

            foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
                if (plr == null || plr.body == null) {
                    continue;
                }
                if (plr.body.alive) {
                    ScavPlayer.AllLivingPlayers.Add(plr);
                } else {
                    ScavPlayer.AllDeadPlayers.Add(plr);
                }
            }
        }

        public static bool WorldInSimRange(Vector2 pos) {
            if (SimKeep.Covers(pos)) {
                return true;
            }

            bool stamped = false;
            if (!KrokV5Optimization.FixSimRange.Range(pos, ref stamped)) {
                return stamped;
            }

            bool inSimRange = false;
            Vector2Int blockpos = WorldGeneration.world.WorldToBlockPos(pos);
            TilemapRenderer renderer = WorldGeneration.world.GetClosestChunkRenderer(blockpos);
            if (renderer != null) {
                inSimRange = renderer.enabled;
            }

            if (!Net.IsRunning) {
                return inSimRange;
            }
            if (Net.IsServer && !inSimRange) {
                return PositionIsInSimulatedChunk_BlockPos(blockpos);
            }
            return inSimRange;
        }

        public static bool PositionIsInSimulatedChunk(Vector2 pos) {
            return PositionIsInSimulatedChunk_BlockPos(WorldGeneration.world.WorldToBlockPos(pos));
        }

        public static bool PositionIsInSimulatedChunk_BlockPos(Vector2Int blockpos) {
            Vector2Int chunkpos = WorldGeneration.world.BlockToChunkPos(blockpos);

            int ymax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1) - 1;
            int xmax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0) - 1;

            chunkpos.x = Mathf.Clamp(chunkpos.x, 0, xmax_m1);
            chunkpos.y = Mathf.Clamp(chunkpos.y, 0, ymax_m1);

            return CHUNKS_VISIBLE_TO_PLAYERS[chunkpos.x, chunkpos.y];
        }

        private const int VISIBLE_CHUNK_RANGE_HALF = 2;

        private static void _PlayerVisibleChunksAroundPos(Vector2 pos) {
            float chunksize_inv = 1f / WorldGeneration.CHUNKSIZE;
            int half_chunkWidth = (int)(WorldGeneration.world.chunkWidth * 0.5f);
            int half_chunkHeight = (int)(WorldGeneration.world.chunkHeight * 0.5f);

            int ymax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1) - 1;
            int xmax_m1 = CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0) - 1;

            int num1 = Mathf.Clamp((int)(pos.x * chunksize_inv) - VISIBLE_CHUNK_RANGE_HALF + half_chunkWidth, 0, xmax_m1);
            int num2 = Mathf.Clamp((int)(pos.x * chunksize_inv) + VISIBLE_CHUNK_RANGE_HALF + 1 + half_chunkWidth, 0, xmax_m1);
            int num3 = Mathf.Clamp((int)(pos.y * chunksize_inv) - VISIBLE_CHUNK_RANGE_HALF + half_chunkHeight, 0, ymax_m1);
            int num4 = Mathf.Clamp((int)(pos.y * chunksize_inv) + VISIBLE_CHUNK_RANGE_HALF + 1 + half_chunkHeight, 0, ymax_m1);

            for (int y = num3; y <= num4; y++) {
                for (int x = num1; x <= num2; x++) {
                    CHUNKS_VISIBLE_TO_PLAYERS[x, y] = true;
                }
            }
        }

        private static void _UpdatePlayerVisibleChunks() {
            if (!KrokV5Optimization.FixSimRange.Rebuild()) {
                return;
            }

            for (int y = 0; y < CHUNKS_VISIBLE_TO_PLAYERS.GetLength(1); y++) {
                for (int x = 0; x < CHUNKS_VISIBLE_TO_PLAYERS.GetLength(0); x++) {
                    CHUNKS_VISIBLE_TO_PLAYERS[x, y] = false;
                }
            }

            if (!LocalWorldIsGenerated) {
                return;
            }
            _PlayerVisibleChunksAroundPos(WorldGeneration.world.mainCam.transform.position);
            if (!Net.IsRunning || Net.IsClient) {
                return;
            }

            foreach (KeyValuePair<Body, ScavPlayer> bp in ScavPlayer.BodyToPlayerDict) {
                if (bp.Value != null) {
                    _PlayerVisibleChunksAroundPos(bp.Value.pos);
                }
            }
        }

        internal static float timer_SendPerformanceReport = 0f;

        private void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Shared);
            LocalWorldIsGenerated = Util.IsWorldGenerated();
            LocalWorldIsGenerating = Util.IsGeneratingWorld();
            IsInCutscene = Util.IsInWorld() && Traverse.Create(Util.world).Field("cutsceneActive").GetValue<bool>();

            ForceUpdatePlayerLists();
            _UpdatePlayerVisibleChunks();

            if (!Net.IsConnected) {
                return;
            }

            timer_SendPerformanceReport += ClientMain.AdaptiveSyncTimerDelta;
            if (timer_SendPerformanceReport > 1f) {
                timer_SendPerformanceReport = 0f;
                UpdateSendPerformanceReport();
            }
        }

        private static void UpdateSendPerformanceReport() {
            if (Net.IsClient) {
                NetDataWriter writer = Net.CreateWriter(NetmsgId.CurrentPerformanceReport_fromclient);
                writer.Put(ServerMain.CURRENT_FPS);
                writer.Put(ServerMain.CURRENT_TPS);
                Net.Client_Send(DeliveryMethod.Unreliable, writer);
            } else {
                NetDataWriter writer = Net.CreateWriter(NetmsgId.CurrentPerformanceReport);
                writer.Put(ServerMain.CURRENT_FPS);
                writer.Put(ServerMain.CURRENT_TPS);
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
            }
        }

        [ClientReceiver((byte)NetmsgId.CurrentPerformanceReport, true)]
        private static void ClientReceiver__CurrentPerformanceReport(knetid _, NetDataReader reader) {
            reader.Get(out ClientMain.SERVER_FPS);
            reader.Get(out ClientMain.SERVER_TPS);
        }

        [ServerReceiver((byte)NetmsgId.CurrentPerformanceReport_fromclient)]
        private static void ServerReceiver__CurrentPerformanceReport_fromclient(knetid clientId, NetDataReader reader) {
            if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) && plr.ServerPlayerState != null) {
                reader.Get(out plr.ServerPlayerState.fps);
                reader.Get(out plr.ServerPlayerState.tps);
            }
        }
    }
}
