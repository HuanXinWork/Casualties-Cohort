using System;
using System.Collections.Generic;
using System.Runtime.CompilerServices;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 开局菜单与世界地形运行时破坏同步补丁（开局放行闸门、爆炸坑方块同步、地震与方块耐久广播）。
/// 地热层安全保护与世界生成反转补丁见 <c>WorldGenerationPatches.Lifecycle.cs</c>。
/// </summary>
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartRun))]
public static class PreRunScript_StartRun_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (!Net.IsRunning) {
            if (TogetherSaveWorldApi.ShouldBypassSingleplayerBlock()) {
                return true;
            }
            Multiplayer.DoMultiplayerStatusMessageLog(Multiplayer.CANT_PLAY_SP_MSG);
            return false;
        }

#if FULLGAME
        CharacterSelectWait.OnStartOrLoadClicked(loadSave: false);
        return false;
#else
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("You can't start the game, server can.");
            return false;
        }
        SaveSystem.loadedRun = false;
        SavesystemPatch.DeleteMPSave();
        WorldGeneration.runSettings = __instance.runSettings;
        WorldMP.LoadVanillaGeneratedWorld(false);
        return false;
#endif
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.LoadRun))]
public static class PreRunScript_LoadRun_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (!Net.IsRunning) {
            if (TogetherSaveWorldApi.ShouldBypassSingleplayerBlock()) {
                return true;
            }
            Multiplayer.DoMultiplayerStatusMessageLog(Multiplayer.CANT_PLAY_SP_MSG);
            return false;
        }

#if FULLGAME
        CharacterSelectWait.OnStartOrLoadClicked(loadSave: true);
        return false;
#else
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("You can't start the game, server can.");
            return false;
        }
        SaveSystem.loadedRun = true;
        WorldMP.LoadVanillaGeneratedWorld(true);
        return false;
#endif
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.StartTutorial))]
public static class PreRunScript_StartTutorial_MultiplayerPatch {
    static bool Prefix(PreRunScript __instance) {
        if (Net.IsRunning) {
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog("You can't start the game, server can.");
                return false;
            }
#if FULLGAME
            CharacterSelectWait.HostBeginTutorialPick();
#else
            WorldMP.LoadVanillaTutorialWorld();
#endif
            return false;
        }
        return true;
    }
}

/// <summary>
/// 追踪局内圆形挖坑（如 <see cref="WorldGeneration.CreateExplosion"/> 与怪物冲撞破墙）：
/// 旧逻辑将 <c>doing_autoUpdateChunk</c> 置为 <c>true</c> 并在 <c>QueueBlockChange</c> 中跳过，导致爆炸边缘随机破坏的方块与怪物破墙完全漏同步；
/// 现改为在局内 <c>autoUpdateChunk</c> 期间将变更路由至可靠方块同步队列。
/// </summary>
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateBlockCircle))]
public static class WorldGenerationGenerateBlockCirclePatch {
    /// <summary>保留原公开静态字段供外部兼容读取（不再用于屏蔽局内方块变更入队）。</summary>
    public static bool doing_autoUpdateChunk = false;
    internal static int runtimeAutoUpdateDepth = 0;

    public static bool Prefix(bool autoUpdateChunk = false) {
        doing_autoUpdateChunk = false;
        if (autoUpdateChunk && SharedMain.LocalWorldIsGenerated) {
            runtimeAutoUpdateDepth++;
        }
        return true;
    }

    public static void Finalizer(Exception __exception) {
        doing_autoUpdateChunk = false;
        if (runtimeAutoUpdateDepth > 0) {
            runtimeAutoUpdateDepth--;
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DistributeMiniBarrels))]
public static class WorldGeneration_DistributeMiniBarrels_WorldgenPatch {
    public static bool Prefix() {
        if (Multiplayer.IsNetworkActiveAndIsClient() || WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateOres))]
static class WorldGeneration_GenerateOres_MultiplayerPatch {
    static bool Prefix() {
        // 矿石生成会推进随机数序列，紧随其后的结构生成依赖相同的 Random 状态，因此主客机均须执行
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Start))]
static class WorldGeneration_Start_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance) {
        if (Multiplayer.WorldSingletons != null) {
            foreach (var type in Multiplayer.WorldSingletons) {
                try {
                    __instance.GetOrAddComponent(type);
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.World, "AttachWorldSingleton(" + type?.Name + ")", ex);
                }
            }
        }
        if (Net.IsRunning && Net.IsServer && !SavesystemPatch.PreserveSave
            && (SaveSystem.loadedRun || SavesystemPatch.PendingLoadMpSave)
            && SavesystemPatch.TryPeekMpWorldState(out var peek)) {
            SavesystemPatch.ApplyPeekedWorldState(__instance, peek);
            SaveSystem.loadedRun = true;
            ModLog.Info($"Continue/load Start: debugStartDepth={peek.biomeDepth} midlevel={peek.midlevel} traveled={peek.totalTraveled}", LogDomain.World);
        }
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlockNoUpdate))]
static class WorldGenerationSetBlockNoUpdatePatch {
    static bool Prefix(WorldGeneration __instance, ref Vector2Int pos, ushort block) {
        WorldGeneration_SetBlock_MultiplayerPatch.QueueBlockChange(__instance, in pos, block, true);
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetBlock), new[] { typeof(Vector2Int), typeof(ushort) })]
public static class WorldGeneration_SetBlock_MultiplayerPatch {
    public struct ChangedBlockInfo : INetSerializeByMemcpy {
        public Vector2Int16 pos;
        public ushort block;

        public ChangedBlockInfo(in Vector2Int16 p, in ushort b) {
            pos = p;
            block = b;
        }

        public BlockInfo GetBlockInfo() {
            return WorldGeneration.world.GetBlockInfo(block);
        }

        public static implicit operator BlockInfo(ChangedBlockInfo b) {
            return b.GetBlockInfo();
        }
    }

    private const int MaxChangedBlocksQueue = 96;
    private const int MaxReliableChangedBlocksQueue = 2048;
    private const int ReliableFlushChunkSize = 128;

    public static readonly List<ChangedBlockInfo> changedBlocks = new(MaxChangedBlocksQueue);
    public static readonly List<ChangedBlockInfo> reliableChangedBlocks = new(256);

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void QueueBlockChange(in WorldGeneration __instance, in Vector2Int pos, in ushort block, in bool IsNoUpdate) {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || !SharedMain.LocalWorldIsGenerated) {
            return;
        }

        var b = new ChangedBlockInfo((Vector2Int16)pos, block);
        // 冷却黑曜石/熔岩壳（37）以及局内爆炸/怪物挖坑（GenerateBlockCircle autoUpdateChunk=true）必须走可靠通道，避免丢包或超出 96 格被截断导致残砖失步
        if (block == 37 || WorldGenerationGenerateBlockCirclePatch.runtimeAutoUpdateDepth > 0) {
            if (reliableChangedBlocks.Count < MaxReliableChangedBlocksQueue) {
                reliableChangedBlocks.Add(b);
            }
        } else if (changedBlocks.Count < MaxChangedBlocksQueue) {
            changedBlocks.Add(b);
        }
    }

    static readonly NetDataWriter writer = Net.CreateWriter(NetmsgId.SetBlock);
    static readonly NetDataWriter reliableWriter = Net.CreateWriter(NetmsgId.SetBlock);

    public static void FlushChangedBlocks() {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || !SharedMain.LocalWorldIsGenerated || !WorldChunkSync._TILE_SYNC_ENABLED) {
            changedBlocks.Clear();
            reliableChangedBlocks.Clear();
            return;
        }
        if (changedBlocks.Count > 0) {
            int n = changedBlocks.Count;
            for (int i = 0; i < n; i++) {
                writer.Put(changedBlocks[i]);
            }
            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
            writer.ResetKeepMsgId();
            changedBlocks.Clear();
        }
        if (reliableChangedBlocks.Count > 0) {
            int total = reliableChangedBlocks.Count;
            int offset = 0;
            while (offset < total) {
                int end = Mathf.Min(offset + ReliableFlushChunkSize, total);
                for (int i = offset; i < end; i++) {
                    reliableWriter.Put(reliableChangedBlocks[i]);
                }
                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, reliableWriter, clientIds: ServerMain.AllClientIdsExceptHost);
                reliableWriter.ResetKeepMsgId();
                offset = end;
            }
            reliableChangedBlocks.Clear();
        }
    }

    static bool Prefix(WorldGeneration __instance, Vector2Int pos, ushort block) {
        QueueBlockChange(__instance, pos, block, false);
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.CreateExplosion))]
public static class WorldGeneration_CreateExplosion_MultiplayerPatch {
    public static event Action<ExplosionParams> OnExplosion;

    public static void ForceCreateExplosionEffect(ExplosionParams param) {
        _forcerun_tempbool = true;
        try {
            var newparam = param.CloneViaSerialization();
            newparam.structuralDamage = 0f;
            newparam.muscleDamage = new RangeF(0f, 0f);
            newparam.skinDamage = new RangeF(0f, 0f);
            newparam.skinDamageChance = 0.0f;
            newparam.boneBreakChance = 0.0f;
            newparam.dislocationChance = 0.0f;
            newparam.disfigureChance = 0.0f;
            newparam.bleedChance = 0.0f;
            newparam.bleedAmount = new RangeF(0f, 0f);
            newparam.shrapnelChance = 0.0f;

            WorldGeneration.CreateExplosion(newparam);
        } finally {
            _forcerun_tempbool = false;
        }
    }

    public static ExplosionParams last_explosion_params = null;
    public static bool is_inside_CreateExplosion = false;
    static bool _forcerun_tempbool = false;
    static int hidRemote;

    static bool Prefix(ExplosionParams param) {
        is_inside_CreateExplosion = true;
        last_explosion_params = param;

        if (!Net.IsRunning || _forcerun_tempbool) {
            return true;
        }
        if (Net.IsClient) {
            return false;
        }

        TrapClaim.HostBoom(param);
        hidRemote++;
        TrapClaim.HideRemoteBodies();
        return true;
    }

    static void Postfix(ExplosionParams param) {
        if (hidRemote > 0) {
            TrapClaim.ShowRemoteBodies();
            hidRemote--;
            if (hidRemote == 0) {
                TrapClaim.PublishLocals(param);
            }
        }
        is_inside_CreateExplosion = false;

        try {
            OnExplosion?.Invoke(param);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(OnExplosion), ex);
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.RegenerateWorld))]
public static class RegenerateWorldPatch {
    public static void Prefix(WorldGeneration __instance) {
        try {
            Con.UnNoclip();

            if (Net.IsRunning) {
                if (Net.IsServer) {
                    var writer = Net.CreateWriter(NetmsgId.CLIENT_Announce_RegenerateWorld);
                    writer.Put((ushort)(__instance.doPod ? 1 : 0));
                    Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);

                    Multiplayer.DoMultiplayerStatusMessageLog("Announced RegenerateWorld.");

                    if (!Multiplayer.rules.RespawnKeepInventory) {
                        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
                            if (kv.Key != null && !kv.Key.alive) {
                                kv.Value?.Server_DropAllInventory();
                            }
                        }
                    }
                } else {
                    ClientMain.ServerIsGeneratingWorld = true;
                }
            }
            NetObjectRegistry.UnregisterAllObjectsButKeepInventory();
            SharedMain.LastWorldgenFinishTime = Time.realtimeSinceStartupAsDouble;

            foreach (var item in ScavPlayer.AllPlayers) {
                if (item.ServerPlayerState != null) {
                    item.ServerPlayerState.did_give_spawn_location = false;
                    item.ServerPlayerState.is_loaded_in = false;
                }
                item.ResetEntropy();
                item.levelPlayTime = 0;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(RegenerateWorldPatch), ex);
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateEntityAtPos))]
public static class WorldGeneration_GenerateEntityAtPos_MultiplayerPatch {
    public static void Prefix(GameObject basObj) {
        if (!Net.IsRunning || basObj == null) {
            return;
        }
        foreach (Transform ch in basObj.transform) {
            ScavMPBuildingSynchronizer.RegisterNonUniqueIdBg(ch, false);
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ApplyLayerModifiers))]
public static class WorldGeneration_ApplyLayerModifiers_MultiplayerPatch {
    public static bool sent_to_clients = false;

    public static bool Prefix(WorldGeneration __instance) {
        if (Net.IsRunning) {
            if (Net.IsClient || WorldMP.aaaaaaaaaaaaaaaaaaaaaaaaaaa != null) {
                return false;
            }
            __instance.generatingWorld = true;
        }
        return true;
    }

    public static void Postfix() {
        if (Net.IsRunning && Net.IsServer) {
            foreach (LayerModifier layerModifier in LayerModifier.availableModifiers) {
                if (layerModifier.active) {
                    ModLog.Info($"SERVER: Sending layer modifier index: {layerModifier.modifierIndex}", LogDomain.World);
#if FULLGAME
                    NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Announce_LayerModifier);
                    writer.Put((ushort)layerModifier.modifierIndex);
                    writer.Put(Physics2D.gravity.y);
                    Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
#else
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_Announce_LayerModifier, (knetid)layerModifier.modifierIndex, true, false);
#endif
                }
            }
        }
        sent_to_clients = true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Update))]
static class WorldGeneration_Update_EarthquakeBlocksPatch {
    private const float DiscReachSqr = 60f * 60f;
    static readonly List<Vector2> quake_centers = new();

    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.earthquakeIntensity <= 0f) {
            return;
        }
        float dt = Time.deltaTime;
        if (dt <= 0f) {
            return;
        }
        float gate = 16f * __instance.earthquakeIntensity;
        quake_centers.Clear();
        var cam = PlayerCamera.main;
        if (cam != null && cam.body != null) {
            quake_centers.Add(cam.body.transform.position);
        }
        foreach (var plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr == null || plr.IsLocal || plr.body == null || !plr.IsAlive()) {
                continue;
            }
            Vector2 center = plr.body.transform.position;
            if (DiscHits(in center)) {
                continue;
            }
            quake_centers.Add(center);
            if (UnityEngine.Random.value / dt >= gate) {
                continue;
            }
            Vector2 at = center + UnityEngine.Random.insideUnitCircle * UnityEngine.Random.Range(5f, 30f);
            __instance.SetBlock(__instance.WorldToBlockPos(at), 0);
        }
    }

    static bool DiscHits(in Vector2 center) {
        for (int i = 0; i < quake_centers.Count; i++) {
            if ((quake_centers[i] - center).sqrMagnitude <= DiscReachSqr) {
                return true;
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.DamageBlock), new Type[] { typeof(Vector2Int), typeof(float), typeof(bool), typeof(bool), typeof(bool) })]
public static class WorldGenerationDamageBlockAnnouncePatch {
    public static NetBody cur_damaging_netbody = null;

    public static void Postfix(WorldGeneration __instance, Vector2Int pos) {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }

        var blockDamage = __instance.GetBlockDamage(pos);
        if (blockDamage == null) {
            return;
        }

        var writer = Net.CreateWriter(NetmsgId.PlayerDamageBlockRelay);
        writer.Put(cur_damaging_netbody != null ? cur_damaging_netbody.netId : (knetid)0);
        writer.Put(pos);
        writer.Put(blockDamage.damage);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }
}
