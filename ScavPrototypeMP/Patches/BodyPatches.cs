using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 角色躯体核心行为同步补丁（洗脑、技能等级溢出保护、喘息、跳跃、周期检查、出生摆位、回光返照、锻炼、血液循环与小睡）。
/// 自残同步（<see cref="SelfHarmMultiplayerSync"/>）、面部死亡定格与多人睡眠速度同步见 <c>BodyPatches.SelfHarmAndVisuals.cs</c>。
/// </summary>
[HarmonyPatch(typeof(MindwipeScript), nameof(MindwipeScript.Start))]
public static class MindwipeScript_Start_MultiplayerPatch {
    static bool Prefix(MindwipeScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        Body body = __instance.GetComponent<Body>();
        if (body.IsBodyLocal()) {
            if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.TryGetNetBody(out NetBody nb)) {
                nb.SetNetHealthSyncIgnoreTime(1f);
            }
            return true;
        }
        if (__instance.active) {
            return false;
        }
        __instance.StartCoroutine("WipeRoutine");
        return false;
    }
}

[HarmonyPatch(typeof(MindwipeScript), nameof(MindwipeScript.WipeRoutine))]
public static class MindwipeScript_WipeRoutine_MultiplayerPatch {
    class MindwipeMPTracker : MonoBehaviour {
        public GameObject go;

        void Update() {
            if (!TryGetComponent<MindwipeScript>(out _)) {
                if (go != null) {
                    Destroy(go);
                }
                if (this != null) {
                    Destroy(this);
                }
            }
        }
    }

    public static IEnumerator Copypasted_WipeRoutine(MindwipeScript __instance) {
        yield return new WaitForSeconds(2f);
        Body body = __instance.GetComponent<Body>();
        float timer = 0f;
        while (timer < 3f) {
            body.Ragdoll();
            body.shock = 20f;
            body.hearingLoss += Time.deltaTime * 50f;
            if (body.IsBodyLocal()) {
                PlayerCamera.main.bonusAbber += Time.deltaTime * 8f;
            }

            foreach (Limb obj in body.limbs) {
                obj.rb.angularVelocity += Random.Range(-400f, 400f);
                obj.rb.velocity += Random.insideUnitCircle * 2f;
                obj.pain += Time.deltaTime * 100f;
            }
            timer += Time.deltaTime;
            yield return null;
        }

        for (int i = 0; i < body.limbs.Length; i++) {
            body.limbs[i].pain = 0f;
        }
        body.consciousness = 0f;
        body.energy = 0f;
        body.sleeping = true;
        body.hearingLoss = 80f;
        body.strokeAmount = 0f;
        if (body.alive) {
            body.brainHealth += 50f;
        }
        body.skills.INT = 0;
        body.skills.expINT = 0f;
        body.skills.UpdateExpBoundaries();

        if (body.IsBodyLocal()) {
            if (Net.IsClient) {
                foreach (Recipe recipe in Recipes.recipes) {
                    recipe.hasMadeBefore = false;
                }
            }

            MusicManager.main.StopSong();
            GameObject vign = GameObject.Instantiate(Resources.Load("Special/MindwipeVignette"), PlayerCamera.main.mainCanvas.transform) as GameObject;
            if (vign != null) {
                vign.transform.SetAsFirstSibling();
                vign.AddComponent<MindwipeMPTracker>().go = vign;
            }
        }
        __instance.active = true;
    }

    static bool Prefix(MindwipeScript __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = Copypasted_WipeRoutine(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(Skills), nameof(Skills.CheckForLevelUp))]
public static class Skills_CheckForLevelUp_MultiplayerPatch {
    static void Prefix(ref float xp) {
        if (xp >= int.MaxValue - 1f) {
            xp = int.MaxValue - 100;
        } else if (xp < 0f) {
            xp = int.MaxValue / 2;
        }
    }

    static void Postfix(Skills __instance, ref bool __result) {
        if (!Net.IsRunning) {
            return;
        }
        foreach (Body item in ScavPlayer.BodyToPlayerDict.Keys) {
            if (item.skills == __instance) {
                if (!Util.IsBodyLocal(item)) {
                    __result = false;
                }
                break;
            }
        }
    }
}

[HarmonyPatch(typeof(Skills), nameof(Skills.UpdateExpBoundaries))]
static class Skills_UpdateExpBoundaries_MultiplayerPatch {
    static void Postfix(Skills __instance) {
        if (__instance.maxSTR < 0) __instance.maxSTR = int.MaxValue;
        if (__instance.maxINT < 0) __instance.maxINT = int.MaxValue;
        if (__instance.maxRES < 0) __instance.maxRES = int.MaxValue;
    }
}

[HarmonyPatch(typeof(PantSound), nameof(PantSound.Bark))]
static class PantSound_Bark_MultiplayerPatch {
    static void Postfix(PantSound __instance) {
        if (Net.IsRunning && __instance.body != null && __instance.body.TryGetNetBody(out NetBody nb)) {
            nb.lastTimeBark = Time.realtimeSinceStartupAsDouble;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Jump))]
static class Body_Jump_MultiplayerPatch {
    struct PreJumpState {
        public bool lastWantsJumpInput;
    }

    static bool Prefix(Body __instance, ref PreJumpState __state) {
        __state.lastWantsJumpInput = PlayerCamera.main.wantsJumpInput;
        if (__instance.TryGetComponent(out NetBody nb)) {
            nb.StopPiggyback();
        }
        return true;
    }

    static void Postfix(Body __instance, ref PreJumpState __state) {
        if (__instance.IsBodyLocal()) {
            if (!PlayerCamera.main.wantsJumpInput && Net.IsRunning && Net.IsClient && Util.IsWorldGenerated()) {
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerJump, false);
            }
        } else {
            PlayerCamera.main.wantsJumpInput = __state.lastWantsJumpInput;
        }
        if (Net.IsServer && __instance.TryGetComponent(out NetBody nb)) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerJumpRelay);
            writer.Put(nb.netId);
            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.GetListOfClientIdsExceptThisAndHost(nb));
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HandlePeriodicChecks))]
public static class Body_HandlePeriodicChecks_MultiplayerPatch {
    struct PeriodicState {
        public bool halfMinuteTick;
        public bool suppressedBrainFx;
        public bool savedForceNoMovement;
    }

    static void Prefix(Body __instance, ref PeriodicState __state) {
        __state = new PeriodicState {
            halfMinuteTick = __instance.halfMinuteCheckTime + Time.deltaTime > 30f,
        };

        if (!Net.IsRunning || !__state.halfMinuteTick || __instance.brainHealth >= 95f) {
            return;
        }

        if (Util.IsBodyLocal(__instance)) {
            if (__instance.consciousness > 20f
                && UnityEngine.Random.value > __instance.brainHealth * 0.01f
                && !PlayerCamera.main.lastStandPanel.activeSelf
                && !UIInGame.SpectatorModeActive) {
                PlayerCamera.main.ForceFlashBrain();
            }
            return;
        }

        __state.savedForceNoMovement = __instance.forceNoMovement;
        __instance.forceNoMovement = true;
        __state.suppressedBrainFx = true;
    }

    static void Postfix(Body __instance, ref PeriodicState __state) {
        if (__state.suppressedBrainFx) {
            __instance.forceNoMovement = __state.savedForceNoMovement;
        }
        if (!Net.IsRunning || !__state.halfMinuteTick || Util.IsBodyLocal(__instance)) {
            return;
        }
        if (PlayerCamera.main != null && PlayerCamera.main.threatMusicTime == 8f) {
            PlayerCamera.main.threatMusicTime = 0f;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.PlaceBody))]
public static class Body_PlaceBody_MultiplayerPatch {
    public static bool has_spawn_location = false;
    public static Vector2 SpawnLocation = new(0f, 500f);

    static Vector2 ogcampos;
    static Vector2 ogbodypos;

    public static bool ShouldKeepLayerXOffset() {
        if (!Multiplayer.rules.LayerFinishKeepXOffset) {
            return false;
        }
        if (WorldGeneration.world != null && (WorldGeneration.world.doPod || WorldGeneration.world.biomeDepth == 10)) {
            return false;
        }
        return true;
    }

    static bool Prefix(Body __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        ogbodypos = __instance.transform.position;
        ogcampos = PlayerCamera.main.transform.position;

        bool ascent = WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 10;
        if ((ShouldKeepLayerXOffset() || ascent) && ScavPlayer.TryGetLocalNetBody(out NetBody pb) && Net.IsServer && ScavPlayer.LocalPlayer.IsAlive()) {
            float x = ascent ? 0f : ScavPlayer.LocalPlayer.ServerPlayerState.layer_transition_x_offset;
            pb.SetBodyPosition(Util.PlaceBody_FindSpawnLocation(x, pb.body));
            return false;
        }
        return true;
    }

    static void Postfix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }

        if (__instance.IsBodyLocal()) {
            if (Net.IsClient) {
                __instance.transform.position = ogbodypos;
            } else {
                has_spawn_location = true;
                Vector2 podSpawn = __instance.transform.position;
                bool packTogether = WorldGeneration.world != null && (WorldGeneration.world.doPod || WorldGeneration.world.biomeDepth == 10);
                foreach (NetBody item in NetBody.all_instances) {
                    if (item.IsPlayer && !item.IsBodyLocal()) {
                        item.plr.ResetEntropy();
                        item.SetBodyPosition(packTogether ? podSpawn : new Vector2(item.transform.position.x, podSpawn.y));
                    }
                }
            }
            SpawnLocation = __instance.transform.position;
        } else {
            ogcampos = PlayerCamera.main.transform.position;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.TryLastStand))]
public static class Body_TryLastStand_MultiplayerPatch {
    static bool force = false;

    public static void ForceLastStand(this Body b) {
        force = true;
        b.lastHappiness[9] = 100f;
        b.TryLastStand();
        force = false;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> codes = new(instructions);
        bool didIt = false;

        for (int i = 0; i < codes.Count - 6; i++) {
            if (codes[i].opcode == OpCodes.Ldsfld &&
                codes[i + 1].opcode == OpCodes.Ldsfld &&
                codes[i + 2].opcode == OpCodes.Callvirt &&
                codes[i + 2].operand is MethodInfo method &&
                method.Name == nameof(PlayerCamera.LastStandSequence) &&
                codes[i + 3].opcode == OpCodes.Callvirt &&
                codes[i + 3].operand is MethodInfo method2 &&
                method2.Name == "StartCoroutine" &&
                codes[i + 4].opcode == OpCodes.Pop) {
                CodeInstruction ci = new(OpCodes.Nop);
                ci.MoveLabelsFrom(codes[i]);
                codes.RemoveRange(i, 5);
                codes.Insert(i, ci);
                i += 1;
                didIt = true;
            }
        }

        if (!didIt) {
            ModLog.Error("Body_TryLastStand_MultiplayerPatch transpiler: pattern not found", LogDomain.Medical);
        }
        return codes;
    }

    static bool Prefix(Body __instance, ref bool __state) {
        __state = !__instance.succesfullyRolledLastStand && Util.IsBodyLocal(__instance);
        if (Net.IsRunning) {
            if (!Multiplayer.rules.LastStandAllowed) {
                return false;
            }
            if (!Multiplayer.IsNetworkActiveAndIsServer() && !force) {
                return false;
            }
            __instance.GetCoUtilsInstance().SelectForMPOverride();
        }
        force = false;
        return true;
    }

    static void Postfix(Body __instance, ref bool __state) {
        CoUtilsMultiplayer.DeselectMPOverride();

        if (__state && __instance.succesfullyRolledLastStand) {
            ModLog.Info($"{ScavPlayer.LocalPlayer?.playerName} rolled Last Stand successfully.", LogDomain.Medical);
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.LastStandSequence());
        } else if (Multiplayer.IsNetworkActiveAndIsServer() && __instance.TryGetNetPlayer(out ScavPlayer plr)) {
            ModLog.Info($"Server rolled Last Stand for {plr.playerName}: {__instance.succesfullyRolledLastStand}", LogDomain.Medical);
            MedicalSync.Server_SendCharacterHealth(plr.netBody);
        }
    }
}

internal static class WorkoutNetworkSync {
    internal static void ServerRelayStartExercise(knetid exerciser, Body.WorkoutType type) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_StartExcerciseRelay);
        writer.Put(exerciser);
        writer.Put((byte)type);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_StartExcercise)]
    static void ServerReceiver_StartExcercise(knetid clientId, NetDataReader reader) {
        reader.Get(out ushort rawType);
        Body.WorkoutType type = (Body.WorkoutType)rawType;

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body) && body.conscious) {
            body.StartCoroutine(body.DoWorkout(type));
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(body.transform.position, $"S: StartExcercise {plr} {type}");
            }
            ServerRelayStartExercise(clientId, type);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestStopExcercise)]
    static void Server_RequestStopExcercise(knetid clientId, NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body)) {
            Body_DoWorkout_MultiplayerPatch.ForceStopExcercising(body);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(body.transform.position, $"S: RequestStopExcercise {plr}");
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_StartExcerciseRelay)]
    static void Client_StartExcercise(knetid _, NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out byte rawType);
        Body.WorkoutType type = (Body.WorkoutType)rawType;

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out ScavPlayer plr, out Body body) && !plr.IsLocal) {
            body.StartCoroutine(body.DoWorkout(type));
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(body.transform.position, $"C: StartExcerciseRelay {plr} {type}");
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_StopExcercise)]
    static void Client_StopExcercise(knetid _, NetDataReader reader) {
        reader.Get(out knetid who);
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out ScavPlayer plr, out Body body)) {
            Body_DoWorkout_MultiplayerPatch.ForceStopExcercising(body);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(body.transform.position, $"C: StopExcercise {plr}");
            }
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DoWorkout))]
public static class Body_DoWorkout_MultiplayerPatch {
    [HarmonyReversePatch]
    public static IEnumerator DoWorkout(object instance, Body.WorkoutType type) {
        throw new NotImplementedException();
    }

    public static bool Prefix(Body __instance, Body.WorkoutType type, ref IEnumerator __result) {
        __result = Patched_DoWorkout(__instance, type);
        return false;
    }

    public static IEnumerator Patched_DoWorkout(object instance, Body.WorkoutType type) {
        Body body = (Body)instance;
        if (Net.IsRunning && Util.IsBodyLocal(body) && body.TryGetNetBody(out _)) {
            if (Net.IsClient) {
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_StartExcercise, (ushort)type, true);
            } else {
                WorkoutNetworkSync.ServerRelayStartExercise(Net.SERVER_CLIENTID, type);
            }
        }

        yield return DoWorkout(instance, type);

        if (Net.IsRunning && body.TryGetNetBody(out NetBody pb)) {
            if (Net.IsClient) {
                if (body.IsBodyLocal()) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopExcercise, true);
                }
            } else {
                Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_StopExcercise, pb.netId, true, false);
            }
        }
    }

    public static void ForceStopExcercising(Body body) {
        body.attackCooldown = Mathf.Max(body.attackCooldown, Time.deltaTime * 1.1f);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HandleCirculation))]
public static class Body_HandleCirculation_MultiplayerPatch {
    struct CirculationState {
        public float brainHealth;
    }

    static void Prefix(Body __instance, ref CirculationState __state) {
        __state.brainHealth = __instance.brainHealth;
        if (!Net.IsRunning) {
            return;
        }
        Sound_Play_MultiplayerPatch.is_the_fucking_updateheart_shit =
            !__instance.TryGetNetBody(out NetBody pb)
            || !((pb.IsLocal || (Util.IsInWoundView() && WoundView.view.body == __instance)) && __instance.alive);
    }

    static void Postfix(Body __instance, ref CirculationState __state) {
        Sound_Play_MultiplayerPatch.is_the_fucking_updateheart_shit = false;
        if (!Net.IsRunning || !__instance.TryGetComponent(out NetBody nb)) {
            return;
        }

        if (__instance.brainDying && __instance.brainHealth < __state.brainHealth) {
            var nearestMedic = ScavPlayer.GetNearestConsciousPlayerToThisBody(nb);
            if (nearestMedic.Item1 != null) {
                float timeDiff = (float)(Time.unscaledTimeAsDouble - nb._lasttime_nobraindamage_from_circulation);
                const float timeUntilStartDying = 15f;
                const float timeUntilFullyDying = 30f;

                float moveScale = timeDiff > timeUntilFullyDying
                    ? 1f
                    : (timeDiff < timeUntilStartDying ? 0f : timeDiff / timeUntilFullyDying);

                __instance.brainHealth = Mathf.Lerp(__state.brainHealth, __instance.brainHealth, moveScale);
            }
        } else {
            nb._lasttime_nobraindamage_from_circulation = Time.unscaledTimeAsDouble;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.canTakeNap), MethodType.Getter)]
public static class Body_get_canTakeNap_MultiplayerPatch {
    public static bool force = false;

    public static void Postfix(Body __instance, ref bool __result) {
        if (Net.IsRunning && !force) {
            if (Multiplayer.rules.DisableSleep) {
                __result = false;
            }
            if (!Util.IsBodyLocal(__instance)) {
                __result = true;
            }
        }
        force = false;
    }
}
