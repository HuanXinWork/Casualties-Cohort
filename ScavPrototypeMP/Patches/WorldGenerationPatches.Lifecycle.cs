using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 地热层（depth 8）顶部出生带防护补丁与世界生成各阶段协程接管补丁。
/// </summary>
[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateWorld))]
static class WorldGeneration_UpdateWorld_GeothermalSpawnRow_MultiplayerPatch {
    static void Prefix(WorldGeneration __instance) {
        if (!Net.IsRunning || __instance == null || __instance.biomeDepth != 8 || !__instance.generatingWorld) {
            return;
        }
        ScrubHotRock(__instance);
    }

    internal static bool InSpawnBand(in Vector2 worldPos) {
        WorldGeneration world = WorldGeneration.world;
        if (!Net.IsRunning || world == null || world.biomeDepth != 8) {
            return false;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        return worldPos.y > (float)world.halfHeight - chunk;
    }

    static void ScrubHotRock(WorldGeneration world) {
        ushort[,] blocks = world.worldBlocks;
        if (blocks == null) {
            return;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        int w = blocks.GetLength(0);
        int h = blocks.GetLength(1);
        int y0 = Mathf.Max(0, h - chunk);
        for (int x = 0; x < w; x++) {
            for (int y = y0; y < h; y++) {
                if (blocks[x, y] == 36) {
                    blocks[x, y] = 37;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.PlaceLiquids))]
static class WorldGeneration_PlaceLiquids_GeothermalSpawnRow_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, float perChunk, byte type, int maxFill) {
        if (!Net.IsRunning || __instance == null || __instance.biomeDepth != 8 || type != 6 || FluidManager.main == null) {
            return true;
        }
        int chunk = WorldGeneration.CHUNKSIZE;
        if (chunk < 8) {
            chunk = 32;
        }
        float num = (float)(__instance.chunkWidth * __instance.chunkHeight) * perChunk;
        float ceiling = (float)__instance.halfHeight - chunk;
        for (int i = 0; (float)i < num; i++) {
            Vector2 pos = new Vector2(
                UnityEngine.Random.Range(0L - (long)__instance.halfWidth, __instance.halfWidth),
                UnityEngine.Random.Range(0L - (long)__instance.halfHeight, ceiling));
            FluidManager.main.StartFill(__instance.WorldToBlockPos(pos), type, maxFill);
        }
        return false;
    }
}

[HarmonyPatch(typeof(GeyserScript), "Start")]
static class GeyserScript_Start_GeothermalSpawnRow_MultiplayerPatch {
    static void Postfix(GeyserScript __instance) {
        if (!Net.IsRunning || __instance == null) {
            return;
        }
        if (WorldGeneration_UpdateWorld_GeothermalSpawnRow_MultiplayerPatch.InSpawnBand(__instance.transform.position)) {
            UnityEngine.Object.Destroy(__instance.gameObject);
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), "GeothermalLavaCool")]
static class WorldGeneration_GeothermalLavaCool_AllPlayers_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning || !Net.IsServer) {
            return true;
        }
        __result = CoolLavaNearAllPlayers(__instance);
        return false;
    }

    static IEnumerator CoolLavaNearAllPlayers(WorldGeneration world) {
        while (world != null && !world.generatingWorld) {
            int passes = world.forceRain ? 5 : 1;
            for (int i = 0; i < passes; i++) {
                Body body = PickCoolBody();
                if (body == null) {
                    continue;
                }
                Vector2 pos = (Vector2)body.transform.position + UnityEngine.Random.insideUnitCircle * 16f;
                if (FluidManager.main != null && FluidManager.main.GetLiquid(pos) == 6) {
                    world.SetBlock(pos, 37);
                    FluidManager.main.SetLiquid(pos, 0);
                    if (WorldChunkSync.singleton != null) {
                        WorldChunkSync.singleton.timer_TilemapFluidSync = 10f;
                    }
                }
            }
            yield return new WaitForSeconds(0.066f);
        }
    }

    static Body PickCoolBody() {
        List<ScavPlayer> living = ScavPlayer.AllLivingPlayers;
        if (living != null && living.Count > 0) {
            int start = UnityEngine.Random.Range(0, living.Count);
            for (int i = 0; i < living.Count; i++) {
                ScavPlayer plr = living[(start + i) % living.Count];
                if (plr?.body != null) {
                    return plr.body;
                }
            }
        }
        return PlayerCamera.main != null ? PlayerCamera.main.body : null;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.FinishWorldGeneration))]
public static class WorldGeneration_FinishWorldGeneration_MultiplayerPatch {
    [HarmonyReversePatch]
    public static IEnumerator FinishWorldGeneration(object instance) {
        throw new NotImplementedException();
    }

    public static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = WorldMP.Patched_FinishWorldGeneration();
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.WorldPlacePlayer))]
public static class WorldGeneration_WorldPlacePlayer_MultiplayerPatch {
    [HarmonyReversePatch]
    public static IEnumerator WorldPlacePlayer(object instance) {
        throw new NotImplementedException();
    }

    public static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = WorldMP.Patched_WorldPlacePlayer();
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.IncreaseDepthByLayer))]
static class WorldGeneration_IncreaseDepthByLayer_MultiplayerPatch {
    static void Postfix() {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        foreach (var body in ScavPlayer.BodyToPlayerDict.Keys) {
            if (body == null || body.skills == null) {
                continue;
            }
            if (!body.IsBodyLocal()) {
                body.skills.AddExp(2, 80f);
            }
            if (body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.GenerateWorld))]
public static class WorldGeneration_GenerateWorld_MultiplayerPatch {
    [HarmonyReversePatch]
    public static IEnumerator GenerateWorld(object instance) {
        throw new NotImplementedException();
    }

    public static LastBeforeGenerationState firstworldgenparams;
    public static bool client_firstworldgenparams_are_used = true;

    private static readonly string[] RegisteredStructurePrefabPaths = {
        "Lifepod",
        "BioContainer",
        "Structures/SteelBridge",
        "Structures/CratePod",
        "Structures/MiniPod",
        "Structures/MedicalBuilding",
        "Structures/BrickLoot",
        "Structures/LongCorridor",
        "Structures/RestSpot",
        "Structures/BlueHeartCont",
        "Structures/CrystalSpawnPlatform",
        "Special/TutorialStructure",
        "Special/EndLab",
    };

    public static void InitializeSpecialResourcePrefabs() {
        var ticks = (GameObject)Resources.Load("CaveTicks");
        if (ticks != null) {
            ticks.GetOrAddComponent<TrackerStaticCaveTicks>();
        }

        RegisterClimbablePrefab("Special/sandvinerope", TrackerClimbableEntity.sandvine_new_id);
        RegisterClimbablePrefab("Special/mushroomrope", TrackerClimbableEntity.mushroomrope_new_id);

#if FULLGAME
        RegisterClimbablePrefab("Special/veinloop", TrackerClimbableEntity.veinloop_new_id);
        RegisterClimbablePrefab(TrackerClimbableEntity.spicevine_id, null);
        RegisterClimbablePrefab(TrackerClimbableEntity.spicevineceiling_id, TrackerClimbableEntity.spicevineceiling_id);
#endif

        var climbingropeextended = (GameObject)Resources.Load("climbingropeextended");
        climbingropeextended?.GetOrAddComponent<TrackerClimbableEntity>();

        var ladder = (GameObject)Resources.Load("ladder");
        ladder?.GetOrAddComponent<TrackerClimbableEntity>();

        var defibrack = (GameObject)Resources.Load("Special/defibrack");
        if (defibrack != null && defibrack.TryGetComponent<BuildingEntity>(out var defibBuilding)) {
            defibBuilding.id = "Special/defibrack";
        }

        foreach (string path in RegisteredStructurePrefabPaths) {
            try {
                GameObject basObj = Resources.Load<GameObject>(path);
                if (basObj == null) {
                    ModLog.Warning($"Force-register backgrounds: missing prefab {path}", LogDomain.World);
                    continue;
                }
                foreach (Transform ch in basObj.transform) {
                    ScavMPBuildingSynchronizer.RegisterNonUniqueIdBg(ch, false);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "RegisterStructureBg(" + path + ")", ex);
            }
        }

        var heavyDrill = Resources.Load<GameObject>("heavydrill");
        var audio = heavyDrill != null ? heavyDrill.GetComponent<AudioSource>() : null;
        if (audio != null) {
            audio.spatialBlend = 1f;
            audio.volume *= 0.6f;
            audio.maxDistance = 150f;
            audio.minDistance = 30f;
            audio.rolloffMode = AudioRolloffMode.Linear;
        }

        foreach (var item in NetObjectRegistry.lowpriority_objects_resourceids) {
            try {
                GameObject basObj = Resources.Load<GameObject>(item);
                basObj?.GetOrAddComponent<ItemDespawnerIfUntouched>();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "AttachDespawner(" + item + ")", ex);
            }
        }
    }

    static void RegisterClimbablePrefab(string resourcePath, string overrideBuildingId) {
        var prefab = (GameObject)Resources.Load(resourcePath);
        if (prefab == null) {
            return;
        }
        var tracker = prefab.GetOrAddComponent<TrackerClimbableEntity>();
        var building = prefab.GetComponent<BuildingEntity>();
        if (building == null) {
            return;
        }
        if (string.IsNullOrEmpty(tracker.og_id)) {
            tracker.og_id = building.id;
        }
        if (!string.IsNullOrEmpty(overrideBuildingId)) {
            building.id = overrideBuildingId;
        }
    }

    public static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        // 单机直通模式下若无自定义世界种子覆写委托，直接放行原版 GenerateWorld 协程
        if (!Net.IsRunning && TogetherSaveWorldApi.ServerWorldSeedOverride == null) {
            return true;
        }
        __result = WorldMP.Patched_GenerateWorld(__instance);
        return false;
    }
}

/// <summary>
/// 房主死亡后允许抵达出口的存活客机弹出过关/保存面板并向服务端发起 <c>layer_continue</c> / <c>layer_save</c> 请求（对齐 v5.5.3）。
/// </summary>
internal static class LayerExitWhenHostDead {
    private static bool saveBusy;

    public static void Register() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.LayerContinue] = (plr, cmd, args) => {
            Accept(plr, save: false);
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.LayerSave] = (plr, cmd, args) => {
            Accept(plr, save: true);
        };
        Multiplayer.OnSceneChangeOrWorldStartGenerate += () => {
            saveBusy = false;
        };
    }

    public static bool ClientAsk(WorldGeneration world, bool save) {
        if (!ServerMain.HostBodyIsDead() || world == null) {
            return false;
        }
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            world.savePanel.SetActive(false);
            return true;
        }
#endif
        if (!ServerMain.LocalPlayerAtLayerExit() || !ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {
            PlayerCamera.main?.DoAlert(Lang.Get("layerfinish_not_enough_plrs"));
            world.savePanel.SetActive(false);
            return true;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(save ? CustomWireCommands.LayerSave : CustomWireCommands.LayerContinue);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        world.savePanel.SetActive(false);
        return true;
    }

    private static void Accept(ScavPlayer plr, bool save) {
        if (!Net.IsServer || plr == null || saveBusy || !ServerMain.HostBodyIsDead()
            || plr.body == null || !plr.body.alive || !ServerMain.DidHeFinishTheLayer(plr.body)) {
            return;
        }
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.doingRegen || world.generatingWorld || !world.worldExists) {
            return;
        }
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            return;
        }
#endif
        if (ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {
            if (save) {
                saveBusy = true;
                world.SaveAndExit();
            } else {
                world.ContinueRun();
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.Update))]
internal static class WorldGeneration_Update_DeadHostExitPanel {
    private static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning || __instance == null || __instance.savePanel == null
            || !ServerMain.HostBodyIsDead() || __instance.doingRegen || __instance.generatingWorld || !__instance.worldExists) {
            return;
        }
        bool show = ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            show = false;
        }
#endif
        if (show && Net.IsClient && !ServerMain.LocalPlayerAtLayerExit()) {
            show = false;
        }
        if (__instance.savePanel.activeSelf != show) {
            __instance.savePanel.SetActive(show);
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SaveAndExit))]
public static class WorldGenerationSaveAndExitPatch {
    public static bool Prefix(WorldGeneration __instance) {
        if (Net.IsRunning) {
            if (PlayerCamera.main?.body != null) {
                PlayerCamera.main.body.forceWalk = false;
            }
            if (Net.IsClient) {
                if (LayerExitWhenHostDead.ClientAsk(__instance, save: true)) {
                    return false;
                }
                PlayerCamera.main?.DoAlert(Lang.Get("wait_for_host"));
                return false;
            }
        }
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.ContinueRun))]
public static class WorldGeneration_ContinueRun_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance) {
        ServerMain._ded_server_switch_counter = -20;

        if (!Net.IsRunning) {
            return true;
        }

        if (PlayerCamera.main?.body != null) {
            PlayerCamera.main.body.forceWalk = false;
        }
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            __instance.savePanel.SetActive(false);
            return false;
        }
#endif
        if (Net.IsClient) {
            if (LayerExitWhenHostDead.ClientAsk(__instance, save: false)) {
                return false;
            }
            PlayerCamera.main?.DoAlert(Lang.Get("wait_for_host"));
            return false;
        }

        if (!ServerMain.CheckIfEnoughPeopleAreAtLayerFinish()) {
            PlayerCamera.main?.DoAlert(Lang.Get("layerfinish_not_enough_plrs"));
            __instance.savePanel.SetActive(value: false);
            return false;
        }

        bool doingRegen = (bool)Traverse.Create(__instance).Field("doingRegen").GetValue();
        if (!doingRegen && !__instance.generatingWorld && __instance.worldExists) {
            Multiplayer.DoMultiplayerStatusMessageLog(Lang.Get("host_continue_run_status"));
            Chat.Server_ChatAnnouncement("<key:host_continue_run_announcement>");
            PlayerCheckpoint.WriteLayerTransition();

            __instance.savePanel.SetActive(value: false);
            __instance.StartCoroutine(__instance.RegenerateWorld());
        }
        return false;
    }
}

[HarmonyPatch(typeof(Heater), "OnWillRenderObject")]
public static class Heater_OnWillRenderObject_MultiplayerPatch {
    public static bool Prefix() {
        return !Net.IsRunning;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.UpdateAmbientLight))]
public static class WorldGeneration_UpdateAmbientLight_MultiplayerPatch {
    public static float GetRunSettingsAmbientLightIntensity() {
        float intensity = 0f;
        switch (WorldMP.runSettings.GetValueOrDefault("ambientlight", 1)) {
            case 1:
                intensity = 0.12f;
                break;
            case 2:
                intensity = 0.4f;
                break;
        }
        return intensity;
    }

    static void Postfix(WorldGeneration __instance) {
        if (!Net.IsRunning) {
            return;
        }
        var worldState = ClientMain.ServerWorldState;
        float intensity = worldState != null
            ? worldState.ambientLightIntensity.Value
            : GetRunSettingsAmbientLightIntensity();

        if (ConsoleScript.instance.fullBright
            || (!PlayerCamera_HandleScreenShaders_MultiplayerPatch.ShouldOverrideVisualsToDefault() && __instance.body.bothEyesGone)) {
            intensity = 0.7f;
        }
        __instance.ambientLight.intensity = intensity;
    }
}
