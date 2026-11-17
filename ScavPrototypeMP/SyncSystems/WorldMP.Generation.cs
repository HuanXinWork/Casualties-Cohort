using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// <see cref="WorldMP"/> 的世界生成三阶段协程（种子协调、玩家出生点分配、收尾清理）及服务端世界就绪接收器。
/// </summary>
public partial class WorldMP {
    static float WORLDCLEAR_MAX_HALT_TIME = 0.04f;
    public static bool client_technically_finished_worldgen_now_just_waiting_for_server = false;

    internal static IEnumerator Patched_GenerateWorld(WorldGeneration world) {
        world.loadingObject.SetActive(value: true);
        world.generatingWorld = true;
        SharedMain.LocalWorldIsGenerating = true;
        WorldGeneration_ApplyLayerModifiers_MultiplayerPatch.sent_to_clients = false;
        ClientMain._last_reminderpack_while_generating_received = false;
        client_technically_finished_worldgen_now_just_waiting_for_server = false;

        int depth = world != null ? world.biomeDepth : 0;
        int seed = UnityEngine.Random.state.GetHashCode();

        if (Net.IsRunning) {
            Body_PlaceBody_MultiplayerPatch.has_spawn_location = false;

            try {
                WorldGeneration_GenerateWorld_MultiplayerPatch.InitializeSpecialResourcePrefabs();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, nameof(WorldGeneration_GenerateWorld_MultiplayerPatch.InitializeSpecialResourcePrefabs), ex);
            }

            SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;

            if (Net.IsClient) {
                ClientMain.ServerIsGeneratingWorld = true;
                world.loadingText.text = Lang.Get("worldgen_waitforserver_seed");
                ModLog.Info("CLIENT: Waiting for worldgen params...", LogDomain.World);

                while (WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used) {
                    ClientMain.ServerIsGeneratingWorld = true;
                    if (!Net.IsRunning) {
                        yield break;
                    }
                    SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
                    yield return null;
                }
                ModLog.Info("CLIENT: Received the worldgen params, starting world generation!", LogDomain.World);

                WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.Apply();
                WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;
                depth = world.biomeDepth;
                seed = WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate.GetHashCode();
            } else {
                // 服务端在快照 LastBeforeGenerationState 前优先向外部存档模组查询是否有固定世界种子覆写
                int defaultSeed = seed;
                seed = TogetherSaveWorldApi.ResolveServerWorldSeed(depth, seed);
                if (seed != defaultSeed
                    || (TogetherSaveWorldApi.ServerWorldSeedOverride != null
                        && TogetherSaveWorldApi.ResolveServerWorldSeed(depth, defaultSeed ^ 1) == seed)) {
                    UnityEngine.Random.InitState(seed);
                }

                WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams = new LastBeforeGenerationState();
                ServerMain.Server_AnnounceSeed(ServerMain.AllClientIdsExceptHost);

                // 钻探舱模式清空偏移使全员出生在舱内；普通下层挖掘保留玩家间横向相对偏移
                if (world.doPod) {
                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        if (item.Value?.ServerPlayerState != null) {
                            item.Value.ServerPlayerState.layer_transition_x_offset = 0;
                        }
                    }
                } else if (Multiplayer.rules.LayerFinishKeepXOffset) {
                    float leftx = 0f;
                    float rightx = 0f;
                    bool firstfound = false;
                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        var body = item.Key;
                        var plr = item.Value;
                        if (plr?.ServerPlayerState == null) {
                            continue;
                        }
                        if (body != null && body.alive) {
                            float n = Mathf.Clamp(body.transform.position.x, -world.width * 0.49f, world.width * 0.49f);
                            plr.ServerPlayerState.layer_transition_x_offset = n;
                            if (!firstfound) {
                                firstfound = true;
                                leftx = n;
                                rightx = n;
                            } else {
                                leftx = Mathf.Min(leftx, n);
                                rightx = Mathf.Max(rightx, n);
                            }
                        } else {
                            plr.ServerPlayerState.layer_transition_x_offset = 0;
                        }
                    }
                    float centerx = Mathf.Lerp(leftx, rightx, 0.5f);

                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        var body = item.Key;
                        var plr = item.Value;
                        if (body != null && body.alive && plr?.ServerPlayerState != null) {
                            plr.ServerPlayerState.layer_transition_x_offset = Mathf.Clamp(
                                plr.ServerPlayerState.layer_transition_x_offset - centerx,
                                -world.width * 0.49f,
                                world.width * 0.49f);
                            body.transform.position = new Vector2(plr.ServerPlayerState.layer_transition_x_offset, body.transform.position.y);
                        }
                    }
                }
            }

            foreach (var item in ScavPlayer.BodyToPlayerDict.Values) {
                item.pos = Vector2.zero;
                if (!item.IsLocal && item.body != null) {
                    item.netBody.SetNetIgnoreTime(1);
                }
                item.ServerPlayerState?.OnWorldgenStart();
            }
        } else {
            // 单机直通模式下同样支持外部模组世界种子覆写
            int defaultSeed = seed;
            seed = TogetherSaveWorldApi.ResolveServerWorldSeed(depth, seed);
            if (seed != defaultSeed
                || (TogetherSaveWorldApi.ServerWorldSeedOverride != null
                    && TogetherSaveWorldApi.ResolveServerWorldSeed(depth, defaultSeed ^ 1) == seed)) {
                UnityEngine.Random.InitState(seed);
            }
        }

        Multiplayer._InvokeOnSceneChangeOrWorldStartGenerate();
        TogetherSaveWorldApi.NotifyWorldGenStarted(depth, seed);

        ModLog.Info($"WorldGeneration.GenerateWorld (depth={depth})", LogDomain.World);
        yield return WorldGeneration_GenerateWorld_MultiplayerPatch.GenerateWorld(world);
    }

    internal static IEnumerator Patched_WorldPlacePlayer() {
        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Net.IsRunning && Net.IsClient) {
            ClientMain.ServerIsGeneratingWorld = true;
            ClientMain._last_reminderpack_while_generating_received = false;

            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_WorldPlacePlayer, WorldGeneration.unchipped, true);
            ModLog.Info("CLIENT: Waiting at WorldPlacePlayer for server spawn location.", LogDomain.World);

            world.loadingText.text = Lang.Get("worldgen_waitforserver_spawn");
            double asked = Time.realtimeSinceStartupAsDouble;
            while (Multiplayer.IsNetworkActiveAndIsClient() && !ClientMain._last_reminderpack_while_generating_received) {
                yield return new WaitForSecondsRealtime(0.25f);
                if (!Multiplayer.IsNetworkActiveAndIsClient()) {
                    yield break;
                }
                yield return new WaitForSecondsRealtime(0.25f);
                if (!Multiplayer.IsNetworkActiveAndIsClient()) {
                    yield break;
                }
                if (Time.realtimeSinceStartupAsDouble - asked >= 2.0) {
                    asked = Time.realtimeSinceStartupAsDouble;
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_WorldPlacePlayer, WorldGeneration.unchipped, true);
                    ModLog.Debug("CLIENT: Still waiting for spawn location, retrying request.", LogDomain.World);
                }
            }
            if (ClientMain._last_reminderpack_while_generating_received && ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
            }
            ModLog.Info("CLIENT: Received location for WorldPlacePlayer.", LogDomain.World);
        }

        yield return WorldGeneration_WorldPlacePlayer_MultiplayerPatch.WorldPlacePlayer(world);
        ModLog.Debug("WorldGeneration.WorldPlacePlayer finished", LogDomain.World);
    }

    static IEnumerator FinishWorldGen_ClientClearWorldObjects() {
        var items = FindObjectsOfType<Item>(true).Select(x => x.gameObject);
        var builds = FindObjectsOfType<BuildingEntity>(true).Select(x => x.gameObject);
        var allCandidates = items.Union(builds).ToList();

        world.loadingText.text = Lang.Get("worldgen_waitforserver_delete");
        double time = Time.realtimeSinceStartupAsDouble;
        for (int i = 0; i < allCandidates.Count; i++) {
            var go = allCandidates[i];
            if (go == null || NetObjectRegistry.ObjectShouldNotBeSynced(go) || NetObjectRegistry.IsRegistered(go)) {
                continue;
            }
            GameObject.Destroy(go);
            if (Time.realtimeSinceStartupAsDouble - time > WORLDCLEAR_MAX_HALT_TIME) {
                yield return null;
                time = Time.realtimeSinceStartupAsDouble;
            }
        }
    }

    internal static IEnumerator Patched_FinishWorldGeneration() {
        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Net.IsRunning) {
            foreach (var item in ScavPlayer.BodyToPlayerDict) {
                item.Value.ResetEntropy();
                item.Value.levelPlayTime = 0;
            }

            if (world.doPod) {
                foreach (var obj in ScavPlayer.BodyToPlayerDict) {
                    var body = obj.Key;
                    if (!obj.Value.IsLocal && body != null) {
                        body.hearingLoss += 15f;
                        body.hunger -= 10f;
                        body.thirst -= 15f;
                    }
                }
            }

            WorldChunkSync.server_chunksync_queue.Clear();
            if (Net.IsClient) {
                WorldChunkSync.singleton.timer_TilemapSync = 10f;
                WorldChunkSync.singleton.timer_TilemapFluidSync = 10f;
                ClientMain.ServerIsGeneratingWorld = true;
                client_technically_finished_worldgen_now_just_waiting_for_server = true;

                yield return FinishWorldGen_ClientClearWorldObjects();

                world.loadingText.text = Lang.Get("worldgen_waitforserver_delete");
                yield return FrameBuckets.ClientFlushWorldTilemaps(world);

                ClientMain.ServerIsGeneratingWorld = true;
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_FinishedWorldgen, WorldGeneration.unchipped, true);
                ModLog.Info("CLIENT: Finished worldgen, notifying server.", LogDomain.World);

                world.loadingText.text = Lang.Get("worldgen_waitforserver");
                while (Multiplayer.IsNetworkActiveAndIsClient() && (ClientMain.ServerIsGeneratingWorld || !ClientMain._last_reminderpack_while_generating_received)) {
                    WorldChunkSync.TilemapSyncUpdate();
                    yield return new WaitForSecondsRealtime(0.01f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3233f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3334f);
                    NetObjectRegistry._ObjectSyncUpdateLoopUniversal(false);
                    yield return new WaitForSecondsRealtime(0.3334f);
                }
                yield return new WaitForSecondsRealtime(1f);

                if (ClientMain._last_reminderpack_while_generating_received && ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                    lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
                }
            }
        }

#if !RELEASE
        if (aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
            world.loadingText.text = "Loading world tiles...";
            yield return null;
            if (aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
                SavesystemPatch.DeserializeWorldBlocks(aaaaaaaaaaaaaaaaaaaaaaaaaaa);
            }
            world.loadingText.text = "Loading world fluids...";
            yield return null;
            if (ccccccccccccccccccccccccccc != null) {
                SavesystemPatch.DeserializeWorldFluids(ccccccccccccccccccccccccccc);
            }
            world.loadingText.text = "Loading world objects...";
            yield return null;
            if (bbbbbbbbbbbbbbbbbbbbbbbbbbb != null) {
                try {
                    var uncompressed = Util.Decompress(bbbbbbbbbbbbbbbbbbbbbbbbbbb);
                    using var stream = new MemoryStream(uncompressed);
                    using var reader = new BinaryReader(stream);
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.World, "LoadDebugWorldObjects", ex);
                }
            }
            yield return null;
        }
#endif

        if (Net.IsRunning) {
            if (Net.IsServer) {
                foreach (var plr in ScavPlayer.AllPlayers) {
                    plr.ServerPlayerState.OnWorldGenerate();
                }
                WorldChunkSync.FluidTilemapSyncUpdate();
            }
            WorldChunkSync.TilemapSyncUpdate();
        }

        if (Util.TryGetLocalBody(out var localBodyBefore) && !localBodyBefore.alive) {
            UIInGame.StartSpectatorMode();
        }

#if FULLGAME
        if (Net.IsRunning && Net.IsClient) {
            yield return CrystalAuthority.ClientLoadingScreenStep(world);
        }
#endif

        yield return WorldGeneration_FinishWorldGeneration_MultiplayerPatch.FinishWorldGeneration(world);
        ModLog.Info("WorldGeneration.FinishWorldGeneration completed", LogDomain.World);

        if (Util.TryGetLocalBody(out var localBodyAfter) && !localBodyAfter.alive) {
            UIInGame.StartSpectatorMode();
        }

        aaaaaaaaaaaaaaaaaaaaaaaaaaa = null;
        bbbbbbbbbbbbbbbbbbbbbbbbbbb = null;
        ccccccccccccccccccccccccccc = null;

        SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;
        ServerMain._ded_server_switch_counter = -20;

        if (Multiplayer.IsNetworkActiveAndIsClient() && ClientMain._last_reminderpack_while_generating_received) {
            if (ScavPlayer.TryGetLocalNetBody(out var lpb)) {
                ModLog.Debug($"CLIENT: Applying starter char state packet at {ClientMain._last_reminderpack_while_generating.Position}", LogDomain.World);
                lpb.OnReceiveSyncPacket(ClientMain._last_reminderpack_while_generating, true);
            } else {
                ModLog.Error($"CLIENT: Starter char state packet received without local body at {ClientMain._last_reminderpack_while_generating.Position}", LogDomain.World);
            }
        }

        if (Net.IsRunning) {
            Multiplayer.ApplyGameRules();
            ServerMain.ResetPlayersFinishedStatus();
            WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;

            DrillPod_Update_MultiplayerPatch.didTeleport = false;
#if FULLGAME
            BlueHeartMp.CargoUnlocked = false;
            BlueHeartMp.ClientCutsceneFromHost = false;
            BlueHeartMp.ResetCargoEnsureState();
#endif
            if (PlayerCamera.main != null) {
                PlayerCamera.main.bonusActionBarTime = 0f;
            }

            ServerMain._DEV_ENABLE_HP_SYNC = true;
            WorldChunkSync._TILE_SYNC_ENABLED = true;
            WorldChunkSync._FLUID_SYNC_ENABLED = true;

            TutorialHandler_Update_MarkiplierPatch.did_multiplayer_warning = false;
            TutorialHandler_Update_MarkiplierPatch.everyone_finish_time = 0f;

            foreach (var item in ScavPlayer.BodyToPlayerDict) {
                item.Value.ResetEntropy();
            }

            if (Net.IsServer) {
                if (Multiplayer.rules.CanReviveOnNextLevel()) {
                    bool didRespawnSomeone = false;
                    foreach (var item in ScavPlayer.BodyToPlayerDict) {
                        if (!item.Key.alive) {
                            didRespawnSomeone = true;
                            ModLog.Info($"Respawned {item.Value.playerName} on next layer", LogDomain.World);
                            item.Value.Server_RespawnCharacter(PlayerCamera.main.body.transform.position, true);
                        }
                    }
                    if (didRespawnSomeone) {
                        const string respawnAlertKey = "respawned_dead_plrs";
                        ModLog.Info(Lang.GetEN(respawnAlertKey), LogDomain.World);
                        PlayerCamera.main.StartCoroutine(PlayerCamera.main.DoAlertDelayed(Lang.Get(respawnAlertKey), important: false, 10f));
                    }
                }

                try {
                    foreach (var npc in NetBody.all_instances) {
                        if (npc?.body == null) {
                            continue;
                        }
                        foreach (var item in npc.body.GetAllItemsThorough()) {
                            if (item != null) {
                                NetObjectRegistry.NewGO(item.gameObject);
                            }
                        }
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.World, "ReRegisterPlayerInventories", ex);
                }
            } else {
                Con._DEV_CHATSPY = false;
            }

            RunUnchippedLock.LockForRun();

            var locplrbody = ScavPlayer.LocalPlayer?.body;
            if (locplrbody != null) {
                ScavPlayer.LocalPlayer.SetUnchipped(WorldGeneration.unchipped);
            }

            try {
                SharedMain.CreatePlayerCharacters();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, nameof(SharedMain.CreatePlayerCharacters), ex);
            }
        } else {
            Application.runInBackground = Plugin.OG_RUNINGACKGROUND;
        }

        try {
            OnWorldgenFinish?.Invoke();
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(OnWorldgenFinish), ex);
        }

        SharedMain.LocalWorldIsGenerating = false;
        TogetherSaveWorldApi.NotifyWorldGenFinished(world != null ? world.biomeDepth : 0);

#if !RELEASE
        Con.RunCommand("spawn glowplant -7,5");
        Con.RunCommand("spawn glowplant -13,5");
        Con.RunCommand("spawn glowplant -18,5");
        Con.RunCommand("spawn glowplant -23,5");
        Con.RunCommand("spawn foliagebag -27,5");
        Con.RunCommand("spawn foliagebag -10,5");
        Con.RunCommand("spawn trader1 6,5");
        Con.RunCommand("spawn dryfoliage -35,5 1 20");
        Con.RunCommand("spawn medcrate -35,10 20 1");
#endif
    }

    [ServerReceiver((byte)NetmsgId.SERVER_WorldPlacePlayer)]
    static void ServerReceiver_WorldPlacePlayer(knetid clientId, NetDataReader reader) {
        if (!Util.IsInWorld()) {
            return;
        }

        reader.Get(out bool plrIsUnchipped);
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) || plr == null) {
            return;
        }

        if (!plr.ServerPlayerState.did_give_spawn_location) {
            log.devevent($"SERVER: Received WorldPlacePlayer from {plr}", plr.pos, Color.red, true);
            plr.ResetEntropy();
            plr.unchipped = plrIsUnchipped;
            if (plr.body != null) {
                plr.SetUnchipped(plrIsUnchipped);
            }

            plr.ServerPlayerState.did_give_spawn_location = true;
            plr.StartCoroutine(ServerMain.GiveSpawnLocationToPlayer(clientId));

            if (WorldChunkSync.singleton != null) {
                WorldChunkSync.singleton.timer_TilemapSync += 0.05f;
                WorldChunkSync.singleton.timer_TilemapFluidSync += 0.05f;
            }
        } else if (plr.body != null && plr.netBody != null && Util.IsWorldGenerated() && Body_PlaceBody_MultiplayerPatch.has_spawn_location) {
            ModLog.Debug($"SERVER: Spawn already handed out, reminding {plr.netBody} at {plr.netBody.transform.position}", LogDomain.World);
            plr.netBody.Server_RemindPlayersCurrentState();
        } else if (!ServerMain.SpawnPlacementBusy(clientId)) {
            ModLog.Debug($"SERVER: Spawn flag set but {plr} still has no placement, retrying.", LogDomain.World);
            plr.StartCoroutine(ServerMain.GiveSpawnLocationToPlayer(clientId));
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_FinishedWorldgen)]
    static void ServerReceiver_FinishedWorldgen(knetid clientId, NetDataReader reader) {
        KrokV5Optimization.FixRejoinBaseline.Mark(clientId, out bool queueBaseline);
        try {
            if (!Util.IsInWorld()) {
                return;
            }

            reader.Get(out bool plrIsUnchipped);

            if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && plr != null && !plr.ServerPlayerState.finished_worldgen) {
                log.devevent($"SERVER: Received FinishedWorldgen from {plr}", plr.pos, Color.red, true);
                plr.ResetEntropy();

                plr.unchipped = plrIsUnchipped;
                if (plr.body != null) {
                    plr.SetUnchipped(plrIsUnchipped);
                }

                plr.ServerPlayerState.finished_worldgen = true;
                if (!plr.ServerPlayerState.did_give_spawn_location) {
                    plr.ServerPlayerState.did_give_spawn_location = true;
                    plr.StartCoroutine(ServerMain.GiveSpawnLocationToPlayer(clientId));
                }

                if (WorldChunkSync.singleton != null) {
                    WorldChunkSync.singleton.timer_TilemapSync = 10f;
                    WorldChunkSync.singleton.timer_TilemapFluidSync = 10f;
                }

                if (plr.LateJoin) {
                    foreach (LayerModifier layerModifier in LayerModifier.availableModifiers) {
                        if (layerModifier.active) {
#if FULLGAME
                            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Announce_LayerModifier);
                            writer.Put((ushort)layerModifier.modifierIndex);
                            writer.Put(Physics2D.gravity.y);
                            Net.Server_SendToClientsVeryReliable(writer, new List<knetid> { plr.playerId });
#else
                            Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_Announce_LayerModifier, plr.playerId, (knetid)layerModifier.modifierIndex, true);
#endif
                        }
                    }
                }
#if FULLGAME
                BlueHeartMp.Server_EnsureCargoOnAscentLoad(plr);
#endif

                foreach (var item in NetObjectRegistry.GatherClosestObjectsToSync(plr.pos, plr.body, 64)) {
                    NetObjectRegistry.SafeNewGO(item);
                }
                if (plr.body != null && plr.body.TryGetNetBody(out NetBody crafted)) {
                    PlayerCheckpoint.SendCrafted(crafted);
                }
                TraderSync.SendTraderState(clientId);
            } else if (ScavPlayer.TryGetPlayerFromClientId(clientId, out plr) && plr != null) {
                TraderSync.SendTraderState(clientId);
            }
        } finally {
            KrokV5Optimization.FixRejoinBaseline.After(clientId, queueBaseline);
        }
    }
}

/// <summary>
/// 原版 <see cref="WorldGeneration.Clear"/> 只清理激活的物体；此处一并销毁被 <see cref="ChunkSleep"/> 休眠的非激活建筑防止跨层残留。
/// </summary>
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Clear))]
static class WorldGeneration_Clear_InactiveBuildings {
    static void Prefix() {
        BuildingEntity[] buildings = UnityEngine.Object.FindObjectsOfType<BuildingEntity>(true);
        for (int i = 0; i < buildings.Length; i++) {
            BuildingEntity building = buildings[i];
            if (building == null || building.gameObject.activeSelf) {
                continue;
            }
            UnityEngine.Object.Destroy(building.gameObject);
        }
    }
}
