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
    private static bool _specialPrefabsReady;

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

    // 重复改写已修改过的预制体资源存在引发底层崩溃的风险，统一通过带日志与异常捕获的辅助函数加载并配合 _specialPrefabsReady 保证单次幂等
    static GameObject LoadPrefab(string id) {
        log.l("[prefab] load " + id);
        try {
            GameObject go = Resources.Load<GameObject>(id);
            if (go == null) {
                log.warn("[prefab] missing " + id);
            }
            return go;
        } catch (Exception ex) {
            log.error("[prefab] load fail " + id + "\n" + ex);
            return null;
        }
    }

    static void PatchClimbable(string id, string newId) {
        GameObject go = LoadPrefab(id);
        if (go == null) {
            return;
        }
        try {
            TrackerClimbableEntity c = go.GetOrAddComponent<TrackerClimbableEntity>();
            BuildingEntity b = go.GetComponent<BuildingEntity>();
            if (b == null) {
                log.warn("[prefab] no building " + id);
                return;
            }
            if (string.IsNullOrEmpty(c.og_id)) {
                c.og_id = b.id;
            }
            if (newId != null) {
                b.id = newId;
            }
            log.l("[prefab] ok " + id);
        } catch (Exception ex) {
            log.error("[prefab] patch fail " + id + "\n" + ex);
        }
    }

    public static void InitializeSpecialResourcePrefabs() {
        if (_specialPrefabsReady) {
            log.l("[prefab] skip, already patched");
            return;
        }
        log.l("[prefab] begin");

        GameObject ticks = LoadPrefab("CaveTicks");
        if (ticks != null) {
            try {
                ticks.GetOrAddComponent<TrackerStaticCaveTicks>();
                log.l("[prefab] ok CaveTicks");
            } catch (Exception ex) {
                log.error("[prefab] patch fail CaveTicks\n" + ex);
            }
        }

        PatchClimbable("Special/sandvinerope", TrackerClimbableEntity.sandvine_new_id);
        PatchClimbable("Special/mushroomrope", TrackerClimbableEntity.mushroomrope_new_id);

#if FULLGAME
        PatchClimbable("Special/veinloop", TrackerClimbableEntity.veinloop_new_id);
        PatchClimbable(TrackerClimbableEntity.spicevine_id, null);
        PatchClimbable(TrackerClimbableEntity.spicevineceiling_id, TrackerClimbableEntity.spicevineceiling_id);
#endif

        GameObject climbingropeextended = LoadPrefab("climbingropeextended");
        if (climbingropeextended != null) {
            try {
                climbingropeextended.GetOrAddComponent<TrackerClimbableEntity>();
                log.l("[prefab] ok climbingropeextended");
            } catch (Exception ex) {
                log.error("[prefab] patch fail climbingropeextended\n" + ex);
            }
        }

        GameObject ladder = LoadPrefab("ladder");
        if (ladder != null) {
            try {
                ladder.GetOrAddComponent<TrackerClimbableEntity>();
                log.l("[prefab] ok ladder");
            } catch (Exception ex) {
                log.error("[prefab] patch fail ladder\n" + ex);
            }
        }

        GameObject defibrack = LoadPrefab("Special/defibrack");
        if (defibrack != null) {
            try {
                BuildingEntity rack = defibrack.GetComponent<BuildingEntity>();
                if (rack == null) {
                    log.warn("[prefab] no building Special/defibrack");
                } else {
                    rack.id = "Special/defibrack";
                    log.l("[prefab] ok Special/defibrack");
                }
            } catch (Exception ex) {
                log.error("[prefab] patch fail Special/defibrack\n" + ex);
            }
        }

        foreach (string path in RegisteredStructurePrefabPaths) {
            try {
                GameObject basObj = LoadPrefab(path);
                if (basObj == null) {
                    continue;
                }
                foreach (Transform ch in basObj.transform) {
                    ScavMPBuildingSynchronizer.RegisterNonUniqueIdBg(ch, false);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.World, "RegisterStructureBg(" + path + ")", ex);
            }
        }

#if FULLGAME
        try {
            GameObject heartCont = LoadPrefab("Structures/BlueHeartCont");
            if (heartCont != null) {
                BlueHeart[] hearts = heartCont.GetComponentsInChildren<BlueHeart>(true);
                for (int i = 0; i < hearts.Length; i++) {
                    if (hearts[i] == null) {
                        continue;
                    }
                    BuildingEntity heartBuild = hearts[i].GetComponent<BuildingEntity>();
                    if (heartBuild == null) {
                        heartBuild = hearts[i].GetComponentInParent<BuildingEntity>();
                    }
                    if (heartBuild == null) {
                        heartBuild = hearts[i].gameObject.AddComponent<BuildingEntity>();
                        heartBuild.id = "KMPSR_BlueHeart";
                    } else if (string.IsNullOrEmpty(heartBuild.id) || heartBuild.id == "background") {
                        heartBuild.id = "KMPSR_BlueHeart";
                    }
                    if (heartBuild.id == "KMPSR_BlueHeart") {
                        ScavMPBuildingSynchronizer.known_entities_with_nonunique_id[heartBuild.id] = heartBuild.gameObject;
                    }
                }
                log.l("[prefab] ok Structures/BlueHeartCont");
            }
        } catch (Exception ex) {
            log.error("[prefab] patch fail Structures/BlueHeartCont\n" + ex);
        }
#endif

        try {
            GameObject heavyDrill = LoadPrefab("heavydrill");
            AudioSource audio = heavyDrill != null ? heavyDrill.GetComponent<AudioSource>() : null;
            if (audio != null) {
                audio.spatialBlend = 1f;
                audio.volume *= 0.6f;
                audio.maxDistance = 150f;
                audio.minDistance = 30f;
                audio.rolloffMode = AudioRolloffMode.Linear;
                log.l("[prefab] ok heavydrill");
            }
        } catch (Exception ex) {
            log.error("[prefab] patch fail heavydrill\n" + ex);
        }

        foreach (var item in NetObjectRegistry.lowpriority_objects_resourceids) {
            GameObject basObj = null;
            try {
                basObj = LoadPrefab(item);
                if (basObj == null) {
                    continue;
                }
                basObj.GetOrAddComponent<ItemDespawnerIfUntouched>();
                log.l("[prefab] ok " + item);
            } catch (Exception ex) {
                log.error("[prefab] despawn fail " + item + " " + basObj + "\n" + ex);
            }
        }

        _specialPrefabsReady = true;
        log.l("[prefab] done");
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
/// 房主死亡后除房主自身常驻结算面板外，兼容允许已抵达出口的存活客机在全员达标时通过 <c>layer_continue</c> / <c>layer_save</c> 代理推进下一层或保存退出。
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
        if (!Net.IsRunning || __instance == null || __instance.savePanel == null) {
            return;
        }
        if (Net.IsClient) {
            // 房主死亡且本地存活客机已抵达出口、人数达标时保留面板供客机代理点击；其余情况一律按上游规则收起面板并给出对应等待提示
            bool allowDeadHostProxy = ServerMain.HostBodyIsDead()
                && !__instance.doingRegen && !__instance.generatingWorld && __instance.worldExists
                && ServerMain.LocalPlayerAtLayerExit() && ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
#if FULLGAME
            if (FullgameWorldCompat.IsAscentLayer()) {
                allowDeadHostProxy = false;
            }
#endif
            if (allowDeadHostProxy) {
                if (!__instance.savePanel.activeSelf) {
                    __instance.savePanel.SetActive(true);
                }
                if (PlayerCamera.main?.body != null) {
                    PlayerCamera.main.body.forceWalk = false;
                }
                return;
            }

            if (__instance.savePanel.activeSelf) {
                __instance.savePanel.SetActive(false);
                if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
                    PlayerCamera.main.body.forceWalk = false;
                    ScavPlayer plr = ScavPlayer.LocalPlayer;
                    if (plr != null && !__instance.doingRegen && !__instance.generatingWorld && __instance.worldExists && !plr.finishedLayer) {
                        plr.finishedLayer = true;
                        if (ServerMain.HostBodyIsDead() || ServerMain.GetNumberOfPlayersRequiredToFinishLayer() == 1) {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_host"));
                        } else {
                            PlayerCamera.main.DoAlert(Lang.Get("layerfinish_wait_for_friends"));
                        }
                    }
                }
            }
            return;
        }
        ApplyHostExitPanel(__instance);
    }

    // 帧末最终裁定房主端结算面板显隐，防止原版仅检查房主自身 y 坐标把全员达标的面板反复关掉（同时供 TilemapSync 调用）
    internal static void ApplyHostExitPanel(WorldGeneration world) {
        if (world == null || world.savePanel == null) {
            return;
        }
        if (world.doingRegen || world.generatingWorld || !world.worldExists) {
            return;
        }
        bool open = ServerMain.CheckIfEnoughPeopleAreAtLayerFinish();
#if FULLGAME
        if (FullgameWorldCompat.IsAscentLayer()) {
            open = false;
        }
#endif
        if (world.savePanel.activeSelf != open) {
            world.savePanel.SetActive(open);
        }
        if (open && PlayerCamera.main != null && PlayerCamera.main.body != null) {
            PlayerCamera.main.body.forceWalk = false;
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
                __instance.savePanel.SetActive(false);
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
            __instance.savePanel.SetActive(false);
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
