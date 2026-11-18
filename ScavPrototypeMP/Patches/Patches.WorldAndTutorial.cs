using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 钻探舱人数校验、躯体存档恢复与碰撞忽略、飞船舱门清理、教学关卡同步与无芯片层修正器补丁。
/// </summary>
[HarmonyPatch(typeof(DrillPod), "Update")]
public static class DrillPod_Update_MultiplayerPatch {
    public class DrillPodMultiplayerRewrite : MonoBehaviour {
        public bool working => drill.working;
        public DrillPod drill => GetComponent<DrillPod>();
        internal float timeHeld = 0f;

        void Update() {
            if (!Net.IsRunning || didTeleport || Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
                return;
            }

            bool w = working;
            var localBody = Util.GetLocalBody();
            bool localStarting = BodyIsStartingIt(drill, localBody);

            if (w && localStarting) {
                timeHeld += Time.deltaTime;
                PlayerCamera.main.bonusActionBarTime = timeHeld * 0.2f;

                if (timeHeld > 5f && !didTeleport) {
                    if (!CheckIfEveryoneIsInTheDrill(drill, out var current, out var required)) {
                        ModLog.Debug($"DrillPod: not enough players inside ({current}/{required})", LogDomain.World);
                        Util.DoAlert(string.Format(Lang.Get("drillpod_not_enough_plrs"), current, required));
                        timeHeld = 0f;
                    }
                }
            } else if (PlayerCamera.main.bonusActionBarTime > 0f) {
                PlayerCamera.main.bonusActionBarTime = 0f;
            }

            if (w && Net.IsServer) {
                bool canStartDrill = CheckIfEveryoneIsInTheDrill(drill, out _, out var required);
                if (!canStartDrill) {
                    timeHeld = 0f;
                }

                foreach (var bodysci in ScavPlayer.BodyToPlayerDict) {
                    var body = bodysci.Key;
                    if (w && !didTeleport && BodyIsStartingIt(drill, body) && canStartDrill) {
                        timeHeld += Time.deltaTime;
                        if (timeHeld > 5f && !didTeleport) {
                            didTeleport = true;
                            WorldGeneration.world.doPod = true;

                            string str = required == 1
                                ? $"<key:plr_started_drillpod> <plr:{bodysci.Value.playerId}>"
                                : "<key:all_started_drillpod>";

                            Chat.Server_ChatAnnouncement(str);
                            Util.DoAlert(Lang.ParseTaggedString(str));

                            WorldGeneration.world.StartCoroutine("RegenerateWorld", true);
                            Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
                        }
                    }
                }
            }
        }
    }

    internal static bool didTeleport = false;

    const float InsideX = 2.25f;
    const float InsideY = 7.5f;

    public static bool BodyIsInside(DrillPod drill, Body body) {
        if (drill == null || body == null) {
            return false;
        }
        return Mathf.Abs(body.transform.position.x - drill.transform.position.x) < InsideX
            && Mathf.Abs(body.transform.position.y - drill.transform.position.y) < InsideY;
    }

    public static bool BodyIsStartingIt(DrillPod drill, Body body) {
        return BodyIsInside(drill, body) && body != null && body.crouching;
    }

    public static bool CheckIfEveryoneIsInTheDrill(DrillPod drill, out int current, out int required) {
        current = 0;
        int eligible = 0;
        foreach (ScavPlayer item in ScavPlayer.AllLivingPlayers) {
            if (item == null || item.body == null) {
                continue;
            }
            if (Net.IsServer && (item.ServerPlayerState == null || !item.ServerPlayerState.is_loaded_in)) {
                continue;
            }
            eligible++;
            if (BodyIsInside(drill, item.body)) {
                current++;
            }
        }
        if (eligible <= 0) {
            required = 1;
            return false;
        }
        float scale = Multiplayer.rules.LayerFinishPlrPercent * 0.01f;
        required = Mathf.Clamp(Mathf.CeilToInt(eligible * scale), 1, eligible);
        return current >= required;
    }

    public static bool Prefix(DrillPod __instance) {
        if (Net.IsRunning) {
            __instance.GetOrAddComponent<DrillPodMultiplayerRewrite>();
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Awake))]
public static class Body_Awake_MultiplayerPatch {
    public static Vector2 origColSize = new(1.6f, 5.0f);

    static void Postfix(Body __instance) {
        origColSize = __instance.origColSize;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Start))]
public static class Body_Start_MultiplayerPatch {
    public static string BodyToString(this Body b) {
        if (b == null) {
            return "BODY(NULL)";
        }
        if (b.TryGetNetBody(out var nb)) {
            return nb.ToString();
        }
        return $"BODY({b.transform.parent?.name}->{b})";
    }

    static void Postfix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }
        var pb = __instance.GetComponent<NetBody>();
        string pid = "NULL";

        try {
            if (pb != null && pb.IsPlayer && Net.IsServer) {
                pid = pb.plr.GetPersistentId();

                if (!SavesystemPatch.PreserveSave && ServerMain.Server_LastPlayerStates.TryGetValue(pid, out var val)) {
                    int depth = WorldGeneration.world != null ? WorldGeneration.world.biomeDepth : val.savedonlayer;
                    if (val.savedonlayer != depth && pb.plr.LateJoin && PlayerCheckpoint.HasPlayerFile(pb.plr)) {
                        val.Discard();
                        PlayerCheckpoint.TryLoadBodyOnly(pb.plr);
                    } else {
                        val.Apply(__instance);
                        PlayerCheckpoint.RegisterBody(__instance);
                    }
                    ServerMain.Server_LastPlayerStates.Remove(pid);
                } else if (!SavesystemPatch.PreserveSave && pb.plr.LateJoin) {
                    PlayerCheckpoint.TryLoadBodyOnly(pb.plr);
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Core, $"Body_Start_MultiplayerPatch(pb={pb}, pid={pid})", ex);
        }

        int playerLayer = LayerMask.NameToLayer("Player");
        if (playerLayer >= 0) {
            Physics2D.IgnoreLayerCollision(playerLayer, playerLayer, true);
        } else {
            Physics2D.IgnoreLayerCollision(LayerMask.GetMask("Player"), LayerMask.GetMask("Player"), true);
        }

        foreach (var otherBody in GameObject.FindObjectsOfType<Body>()) {
            if (otherBody == __instance || __instance.transform.parent == null || otherBody.transform.parent == null) {
                continue;
            }
            var a = __instance.transform.parent.GetComponentsInChildren<Collider2D>();
            var b = otherBody.transform.parent.GetComponentsInChildren<Collider2D>();
            for (int i = 0; i < a.Length; i++) {
                for (int j = 0; j < b.Length; j++) {
                    Physics2D.IgnoreCollision(a[i], b[j], true);
                }
            }
        }
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.lineOfSightEnabled), MethodType.Getter)]
static class WorldGeneration_lineOfSightEnabled_MultiplayerPatch {
    static void Postfix(ref bool __result) {
        if (__result && PlayerCamera_HandleScreenShaders_MultiplayerPatch.CURRENTLY_OVERRIDING_VISUALS) {
            __result = false;
            PlayerCamera.main?.LOSHole?.GetComponent<VisionMask>()?.ClearMask();
        }
    }
}

[HarmonyPatch(typeof(FluidManager), nameof(FluidManager.DrinkLiquid))]
static class FluidManagerDrinkLiquidPatch {
    static void Postfix(Body body) {
        if (Net.IsRunning && body != null && Util.IsBodyLocal(body) && body.alive) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(body.transform.position, "C: PlayerDrinkLiquid");
            }
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerDrinkLiquid, true);
        }
    }
}

[HarmonyPatch(typeof(ShuttleStartOpen), nameof(ShuttleStartOpen.Start))]
static class ShuttleStartOpen_Start_MultiplayerPatch {
    static void Postfix(ShuttleStartOpen __instance) {
        if (!Net.IsRunning || ClientMain.ServerWorldState == null) {
            return;
        }
        if (Net.IsServer) {
            ClientMain.ServerWorldState.shuttleDoorPosition.Value = __instance.transform.position;
        } else {
            __instance.transform.position = ClientMain.ServerWorldState.shuttleDoorPosition.Value;
        }
    }
}

[HarmonyPatch(typeof(ShuttleStartOpen), nameof(ShuttleStartOpen.Update))]
static class ShuttleStartOpen_Update_MultiplayerPatch {
    static float timer = 0f;

    static void Postfix(ShuttleStartOpen __instance) {
        if (!Net.IsRunning) {
            timer = 0f;
            return;
        }
        if (Util.IsWorldGenerated() && Util.TryGetLocalBody(out var b)) {
            if (!KM.DistanceCheck(__instance.transform.position, b.transform.position, GameplayRanges.ObjectNearbyRadius)) {
                timer += Time.unscaledDeltaTime;
                if (timer > 5f) {
                    ModLog.Debug("ShuttleStartOpen auto-deleted because local player moved away.", LogDomain.World);
                    foreach (var item in __instance.doors) {
                        GameObject.Destroy(item);
                    }
                    GameObject.Destroy(__instance);
                }
            }
        } else {
            timer = 0f;
        }
    }
}

[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.WaitLoad))]
public static class ScavGameStartPatch {
    public static bool starting_game = false;

    static bool Prefix(PreRunScript __instance, ref IEnumerator __result) {
        if (Net.IsRunning) {
            if (starting_game) {
                Multiplayer.DoMultiplayerStatusMessageLog("Client is starting game by request of server.");
                starting_game = false;
                return true;
            }
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog("You can't start the game, server can.");
                starting_game = false;
                __result = new List<object>().GetEnumerator();
                return false;
            }

            starting_game = true;
            __instance.UpdateAllSettingDisplays();
            ServerMain.Server_Announce_GAME_START();
            Multiplayer.showMultiplayerMenu = false;
        } else {
            Multiplayer.showMultiplayerMenu = false;
        }

        starting_game = false;
        return true;
    }
}

internal static class TutorialSyncReceiver {
    [ClientReceiver((byte)NetmsgId.CLIENT_Tutorial_GlobalState)]
    static void ClientReceiver__Tutorial_GlobalState(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 handPos);
        reader.Get(out AnyObjectNetId grabObj);

        if (TutorialHandler.main == null) {
            return;
        }
        TutorialHandler.main.handPos = handPos;
        if (grabObj.TryGetGameObject(out var go)) {
            if (TutorialHandler.main.grabInfo.grabbed != go.transform) {
                TutorialHandler.main.Grab(go.transform);
            }
        } else if (TutorialHandler.main.grabInfo.grabbed != null) {
            TutorialHandler.main.Ungrab();
        }
    }
}

[HarmonyPatch(typeof(TutorialHandler), nameof(TutorialHandler.Start))]
public static class TutorialHandler_Start_MarkiplierPatch {
    static void Postfix(TutorialHandler __instance) {
        if (Net.IsRunning) {
            Util.DelayCallLambda(1f, () => {
                Util.DoAlert("Multiplayer Tutorial is not done!\nsorry!", true);
            });
            __instance.StartCourse(typeof(SandboxCourse));
            __instance.courseSelectScreen.SetActive(value: false);
        }
    }
}

[HarmonyPatch(typeof(TutorialHandler), nameof(TutorialHandler.Update))]
public static class TutorialHandler_Update_MarkiplierPatch {
    internal static bool did_multiplayer_warning = false;
    internal static double everyone_finish_time = 0;
    internal static float server_sync_timer = 0f;

    public static void PreventFromDying_Stolen(Body body) {
        if (body == null || !body.alive) {
            return;
        }
        if (body.thirst < 11f) body.thirst = 11f;
        if (body.hunger < 11f) body.hunger = 11f;
        if (body.bloodPressure < 80f) body.bloodPressure = 80f;
        if (body.fibrillationProgress > 95f) body.fibrillationProgress = 95f;
        if (body.brainHealth < 96f) body.brainHealth = 96f;
        if (body.happiness < -30f) body.happiness = -30f;
    }

    static void Postfix(TutorialHandler __instance) {
#if !RELEASE
        if (Input.GetKeyDown(KeyCode.B)) {
            __instance.speakerTalker.Skip();
            if (Util.TryGetLocalBody(out var lb)) {
                lb.forceWalk = false;
            }
        }
#endif
        if (!Net.IsRunning) {
            return;
        }

        foreach (var item in ScavPlayer.BodyToPlayerDict.Keys) {
            PreventFromDying_Stolen(item);
        }

        if (Net.IsServer) {
            server_sync_timer += Time.unscaledDeltaTime;
            if (server_sync_timer > 0.05f) {
                server_sync_timer = 0f;
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Tutorial_GlobalState);
                writer.Put((Vector2)__instance.handPos);
                if (__instance.grabInfo.grabbed != null) {
                    writer.Put(AnyObjectNetId.GetNetIdFromGameObject(__instance.grabInfo.grabbed.gameObject));
                } else {
                    writer.Put(new AnyObjectNetId());
                }
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
            }
        }

        if (__instance.grabInfo.grabbed != null && __instance.grabInfo.grabbed.TryGetComponent(out NetBody nb)) {
            nb.StopPiggyback();
        }

#if !RELEASE
        __instance.speakerUI.text = "";
#endif

        if (!WorldGeneration.world.generatingWorld && Util.IsTutorialWorld() && PlayerCamera.main.body) {
            if (!did_multiplayer_warning) {
                PlayerCamera.main.DoAlertDelayed(Lang.Get("tutorial_mp_alert"), true, 4f);
                did_multiplayer_warning = true;
            }
            if (did_multiplayer_warning) {
                PlayerCamera.main.body.forceWalk = false;
            }

            PlayerCamera.main.body.consciousness = Mathf.Max(
                PlayerCamera.main.body.consciousness,
                10f + (Body.consciousnessRiseRate + 10f) * Time.deltaTime);

            if (everyone_finish_time != 0 && Time.realtimeSinceStartupAsDouble - everyone_finish_time > 2.0 && Net.IsServer) {
                ModLog.Info("TutorialHandler_Update: returning to main menu.", LogDomain.World);
                PlayerCamera.main.ToMainMenu();
            }
        }
    }
}

[HarmonyPatch(typeof(UnchippedModifier), nameof(UnchippedModifier.Initialize))]
internal static class UnchippedModifier_Initialize_MultiplayerPatch {
    internal static Dictionary<NetBody, bool> OriginalUnchippedStates = new();

    static void Prefix() {
        if (!Net.IsRunning) {
            return;
        }
        foreach (var nb in NetBody.all_instances) {
            OriginalUnchippedStates[nb] = nb.unchipped;
        }
    }
}

[HarmonyPatch(typeof(UnchippedModifier), nameof(UnchippedModifier.Disable))]
static class UnchippedModifier_Disable_MultiplayerPatch {
    static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates ??= new();
        try {
            foreach (var state in UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates) {
                if (state.Key != null) {
                    state.Key.SetUnchipped(state.Value);
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, nameof(UnchippedModifier_Disable_MultiplayerPatch), ex);
        }
        UnchippedModifier_Initialize_MultiplayerPatch.OriginalUnchippedStates.Clear();
    }
}
