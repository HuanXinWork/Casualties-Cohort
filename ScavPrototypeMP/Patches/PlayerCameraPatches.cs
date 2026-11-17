using System;
using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using TMPro;
using Together.Api;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 玩家主镜头悬停提示、观战深度计、屏幕后处理着色器、返回主菜单保护闸门与时间倍率同步补丁。
/// 拖拽交互、外部模态输入阻塞锁、换手、商人招募与自毁流程补丁见 <c>PlayerCameraPatches.Interaction.cs</c>。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandlePlayerHover))]
public static class PlayerCamera_HandlePlayerHover_MultiplayerPatch {
    public static bool is_hovering_over_another_player = false;
    public static NetBody hoveredPlayerBody;

    public static void Postfix() {
        if (!Net.IsRunning || Camera.main == null) {
            return;
        }

        is_hovering_over_another_player = false;
        hoveredPlayerBody = null;

        Vector2 mouseWorld = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Component hit = Physics2D.OverlapPoint(mouseWorld, LayerMask.GetMask("Limb"))?.GetComponent<Limb>()?.body;
        if (!hit) {
            hit = Physics2D.OverlapPoint(mouseWorld, LayerMask.GetMask("Body"));
        }
        if (hit && hit.TryGetComponent<NetBody>(out var npc) && !Util.IsBodyLocal(npc.body)) {
            string mouseOverName = "";
            string mouseOverDesc = "";
            is_hovering_over_another_player = true;
            if (npc.IsPlayer) {
                hoveredPlayerBody = npc;
            }
            npc.DoMouseHoverTooltip(ref mouseOverName, ref mouseOverDesc);
            GlobalDark.main.SetTooltip((mouseOverName, mouseOverDesc));
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ItemHoverDescription))]
static class PlayerCamera_ItemHoverDescription_MultiplayerPatch {
    public static void Postfix(Item item, ref (string, string) __result) {
        ClientMain.DoVerboseObjInfo(item, ref __result.Item1, ref __result.Item2);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.DoAlert))]
static class PlayerCamera_DoAlert_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance) {
        if (__instance != null && __instance.alertText != null) {
            __instance.alertText.richText = true;
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleItemHover))]
static class PlayerCamera_HandleItemHover_MultiplayerPatch {
    public static Component last_focused_thing = null;

    internal static void EnsureHoveredEntityRegistered(Component item) {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated() && item != null) {
            if (last_focused_thing != item && NetObjectRegistry.ObjectShouldBeSynced(item.gameObject)) {
                NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out _);
            }
            last_focused_thing = item;
        }
    }

    static void Postfix(Item item) {
        EnsureHoveredEntityRegistered(item);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBuildingHover))]
static class PlayerCamera_HandleBuildingHover_MultiplayerPatch {
    static void Postfix(BuildingEntity build) {
        PlayerCamera_HandleItemHover_MultiplayerPatch.EnsureHoveredEntityRegistered(build);
        ClientMain.DoVerboseObjInfo(build, ref GlobalDark.main.tooltipText.name, ref GlobalDark.main.tooltipText.desc);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBlockHover))]
static class PlayerCamera_HandleBlockHover_MultiplayerPatch {
    static void Postfix() {
        if (!log.verbose || Camera.main == null || WorldGeneration.world == null || FluidManager.main == null) {
            return;
        }
        Vector2 worldPoint = Camera.main.ScreenToWorldPoint(Input.mousePosition);
        Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(worldPoint);
        ushort blockId = WorldGeneration.world.GetBlock(blockPos);
        BlockInfo blockInfo = WorldGeneration.world.GetBlockInfo(blockId);
        byte fluidId = FluidManager.main.GetLiquid(blockPos.x, blockPos.y);

        GlobalDark.main.tooltipText.desc = $"{GlobalDark.main.tooltipText.desc}\nBLOCKPOS: {blockPos}\nBLOCK ID: {blockId}\nSLEEPQUAL: {blockInfo.sleep}";
        if (fluidId != 0) {
            GlobalDark.main.tooltipText.desc += $"\nFLUID ID: {fluidId}";
        }
    }
}

[HarmonyPatch(typeof(GlobalDark), nameof(GlobalDark.Awake))]
internal static class GlobalDark_Awake_MultiplayerPatch {
    internal static void SetBetaBuildMPText() {
        if (GlobalDark.main == null || !Plugin.SUCCESFULLY_INITIALIZED) {
            return;
        }
        var betaBuild = GlobalDark.main.GetComponentsInChildren<TextMeshProUGUI>(true)
            .FirstOrDefault(text => text != null &&
                (text.text?.StartsWith("This is a beta build (", StringComparison.Ordinal) == true ||
                 text.text?.StartsWith("This is a modded beta build (", StringComparison.Ordinal) == true));
        if (betaBuild != null) {
            betaBuild.text = $"This is a beta build ({Application.version});  (Modified) (MP V{Plugin.FULL_MOD_VERSION})";
        }
    }

    static void Postfix() {
        SetBetaBuildMPText();
    }
}

[HarmonyPatch(typeof(GlobalDark), "Update")]
static class GlobalDark_Update_DontEatClicks {
    static void Postfix(GlobalDark __instance) {
        if (__instance != null && __instance.image != null) {
            __instance.image.raycastTarget = __instance.image.color.a >= 0.02f;
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.LateUpdate))]
public static class PlayerCamera_LateUpdate_TraderHold {
    private static void Postfix(PlayerCamera __instance) {
        TraderPinLog.Hold(__instance);
        UpdateSpectatorDepthMeter(__instance);
    }

    static void UpdateSpectatorDepthMeter(PlayerCamera camera) {
        if (!Net.IsRunning || camera == null) {
            return;
        }
        if (camera.body.alive && !(UIInGame.SpectatorModeActive || Con._DEV_FREECAM)) {
            return;
        }

        var world = WorldGeneration.world;
        var depthLine = camera.depthLine;
        if (world == null || depthLine == null || depthLine.parent == null || world.height <= 0) {
            return;
        }

        float depthUnits = (float)world.halfHeight - camera.transform.position.y;
        float meterHeight = depthLine.parent.GetComponent<RectTransform>().rect.size.y;
        depthLine.anchoredPosition = new Vector2(0f, -depthUnits / world.height * meterHeight);

        var depthLabel = depthLine.GetComponentInChildren<TextMeshProUGUI>();
        if (depthLabel != null) {
            float totalDepthMeters = depthUnits * 0.3f + world.totalTraveled;
            depthLabel.text = Util.UnitsDistance(totalDepthMeters);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.Update))]
public static class PlayerCamera_Update_MultiplayerPatch {
    static bool prev_radialOpen = false;

    static void Prefix(PlayerCamera __instance) {
        if (Net.IsRunning) {
            TraderPinLog.Hold(__instance);
            prev_radialOpen = __instance.radialOpen;
            TraderPinLog.Sample("update");
        }
    }

    static void Postfix(PlayerCamera __instance) {
        if (__instance.mainView != null && !__instance.mainView.activeSelf) {
            bool panel = __instance.woundView != null && __instance.woundView.activeSelf;
            if (!panel || PlayerCamera_ToggleWoundView_MultiplayerPatch.HealthTargetGone()) {
                PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(__instance);
            }
        } else if (UIInGame.interaction_menu_target_body != null && UIInGame.interaction_menu_target_body.syncBody == null) {
            UIInGame.StopPlayerInteractionMenu();
        }

        if (Con._DEV_HIDEHUD) {
            GlobalDark.main?.SetTooltip(("", ""));
            __instance.hoverSquare.enabled = false;
        }

        if (!Net.IsRunning) {
            return;
        }

        if (!prev_radialOpen && __instance.radialOpen) {
            UIInGame.StopPlayerInteractionMenu();
            return;
        }

        if (Input.GetKeyDown(KeyBinds.GetBind("iteminteract"))) {
            if (UIInGame.interaction_menu_target_body != null
                && !UIInGame.interaction_menu_focused
                && !PlayerCamera_HandlePlayerHover_MultiplayerPatch.is_hovering_over_another_player) {
                UIInGame.StopPlayerInteractionMenu();
            }
        }

        if (Input.GetKeyDown(KeyCode.K)) {
            NetBody.TryDuneKeybindBite();
        }

        if (__instance.radialOpen) {
            if (Util.IsInWoundView()) {
                __instance.radialOpen = false;
            }
        } else {
            InvButton_get_body_MultiplayerPatch.focused_body = null;
        }

        try {
            var returnBtn = __instance.deathStats != null ? __instance.deathStats.Find("Return") : null;
            if (returnBtn != null) {
                bool showReturn = Net.IsServer;
#if FULLGAME
                if (showReturn && ConcurrentEndings.BlockHostReturn()) {
                    showReturn = false;
                }
#endif
                // 房主死亡但仍有存活队友时隐藏结算小票上的 Return 按钮，防止误点把存活队友强踢回主菜单
                if (showReturn && (__instance.body == null || !__instance.body.alive) && ScavPlayer.AllLivingPlayers.Count > 0) {
                    showReturn = false;
                }
                returnBtn.gameObject.SetActive(showReturn);
            }
        } catch (Exception ex) {
            ModLog.Debug("PlayerCamera deathStats Return check failed: " + ex.Message, LogDomain.UI);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleScreenShaders))]
public static class PlayerCamera_HandleScreenShaders_MultiplayerPatch {
    public static bool forceDefaultShaderForNextFrame = false;
    public static bool CURRENTLY_OVERRIDING_VISUALS = false;

    public static bool ShouldOverrideVisualsToDefault() {
        return UIInGame.SpectatorModeActive || forceDefaultShaderForNextFrame || Con._DEV_FREECAM || Con._DEV_NO_VISION_EFFECT;
    }

    static void Postfix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            CURRENTLY_OVERRIDING_VISUALS = false;
            return;
        }

        CURRENTLY_OVERRIDING_VISUALS = ShouldOverrideVisualsToDefault();
        if (CURRENTLY_OVERRIDING_VISUALS) {
            forceDefaultShaderForNextFrame = false;
            __instance.blackAmount = 0f;

            __instance.consciousnessFade.color = new Color(0f, 0f, 0f, 0f);
            __instance.consciousnessVignette.color = new Color(0f, 0f, 0f, 0f);
            __instance.consciousnessECG.active = false;

            __instance.mainScreenPass.SetFloat("_Pain", 0f);
            __instance.mainScreenPass.SetFloat("_LowOxyAmt", 0f);
            __instance.mainScreenPass.SetFloat("_BloodAmount", 1f);
            __instance.mainScreenPass.SetFloat("_Stamina", 1f);
            __instance.mainScreenPass.SetFloat("_Consciousness", 1f);
            __instance.mainScreenPass.SetFloat("_Consiousness", 1f);
            __instance.mainScreenPass.SetFloat("_FrostAmount", 0f);
            __instance.mainScreenPass.SetFloat("_OverheatAmount", 0f);
            __instance.mainScreenPass.SetFloat("_BadFocusAmount", 0f);
            __instance.mainScreenPass.SetFloat("_InsanityFX", 0f);
            __instance.mainScreenPass.SetFloat("_BrightMult", 1f);
            __instance.mainScreenPass.SetVector("_PlayerPos", PlayerCamera.main.transform.position);
            __instance.mainScreenPass.SetFloat("_Dirtyness", 0f);
            __instance.mainScreenPass.SetFloat("_SaturationMult", 1f);
            if (__instance.mainScreenPass.HasFloat("_PosterizeSteps")) {
                __instance.mainScreenPass.SetFloat("_PosterizeSteps", 124f);
            }
            if (__instance.mainScreenPass.HasFloat("_NightVisAmount")) {
                __instance.mainScreenPass.SetFloat("_NightVisAmount", 0f);
            }

            __instance.blurPass.SetFloat("_BlurIntensity", 0f);
            __instance.blurPass.SetFloat("_DropletAmount", 0f);
            __instance.blurPass.SetFloat("_HungerIntensity", 0f);
            __instance.blurPass.SetFloat("_SicknessAmount", 0f);
            __instance.blurPass.SetFloat("_Insanity", 0f);
            __instance.blurPass.SetFloat("_WaterAmount", 0f);

            __instance.bonusPass.SetFloat("_Intensity", 0f);
            __instance.bonusPass.SetFloat("_Distort", 0f);
            __instance.bonusPass.SetFloat("_FlipScreen", 0f);

            __instance.damagePass.SetInt("_EyeDamage", 0);
            __instance.damagePass.SetFloat("_BrainDamage", 0f);
            __instance.damagePass.SetFloat("_Bloodloss", 0f);
            __instance.damagePass.SetVector("_Pos", PlayerCamera.main.transform.position);
            __instance.damagePass.SetFloat("_DirectionMult", 0f);
            __instance.damagePass.SetFloat("_RadAmount", 0f);

            WorldGeneration.world.fogMat.SetVector("_PlayerPos", PlayerCamera.main.transform.position);

            if (!__instance.body.alive) {
                PlayerCamera.main.endScreen.gameObject.SetActive(false);
            }

            WorldGeneration.world.soundMixerGroup.audioMixer.SetFloat("SoundCutoff", 22000f);
            __instance.underWaterSource.volume = 0f;
        } else if (!Util.IsUnchipped() || PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.forceSetIrradiateIntensity) {
            PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.forceSetIrradiateIntensity = false;
            if (NetBody.TryGetLocalNetBody(out var nb)) {
                __instance.damagePass.SetFloat("_RadAmount", Mathf.Clamp01(nb.irradiateIntensity));
                PlayerCamera.main.irradiateIntensity = nb.irradiateIntensity;
                PlayerCamera.main.timeSinceRadUpdate = nb.timeSinceRadUpdate;
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPickupFromWorld))]
static class PlayerCamera_UIOverrideTryPickupFromWorld_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance, ref Item __state) {
        __state = __instance.dragItem;
    }

    static void Postfix(PlayerCamera __instance, ref Item __state) {
        if (!Net.IsRunning) {
            return;
        }

        if (__instance.dragItem) {
            if (!NetObjectRegistry.IsRegistered(__instance.dragItem.gameObject)
                && NetObjectRegistry.ObjectShouldBeSynced(__instance.dragItem.gameObject)) {
                var unregisteredItem = __instance.dragItem;
                NetObjectRegistry.SafeDestroyObject(unregisteredItem.gameObject);

                if (__state == null) {
                    ModLog.Warning($"Tried to drag unregistered world item {unregisteredItem.name} at {unregisteredItem.transform.position}", LogDomain.Item);
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                }
                __instance.dragItem = null;
            }
        } else if (Camera.main != null) {
            Component collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Body", "Limb"));
            if (collider2D != null && Util.TryGetBodyFromObject(collider2D, out var targetBody)) {
                if (!targetBody.IsBodyLocal() && targetBody.TryGetNetBody(out var nb)) {
                    __instance.radialOpen = false;
                    if (UIInGame.interaction_menu_target_body != nb) {
                        UIInGame.StartPlayerInteractionMenu(nb);
                    } else {
                        UIInGame.StopPlayerInteractionMenu();
                    }
                }
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToMainMenu))]
static class PlayerCamera_ToMainMenu_MultiplayerPatch {
    static bool Prefix(PlayerCamera __instance) {
        // 房主在仍有存活队友时触发返回主菜单前，先询问外部模组（如 CuSaveManager）确认钩子，返回 false 则直接中止退回主菜单
        if (Net.IsRunning && Net.IsServer) {
            int livingCount = ScavPlayer.AllLivingPlayers.Count;
            if (livingCount > 0 && !TogetherSaveWorldApi.ShouldAllowHostExitToMainMenu(livingCount)) {
                return false;
            }
        }

#if FULLGAME
        ConcurrentEndings.KeepSpectating = false;
        ConcurrentEndings.SuppressDeathScreen = false;
#endif
        PlayerCamera_ToggleWoundView_MultiplayerPatch.CloseHealthPanel(__instance != null ? __instance : PlayerCamera.main);
        NetObjectRegistry.UnregisterAllObjectsButKeepInventory();
        Con.UnNoclip();

        if (Net.IsRunning) {
            UIInGame.StopSpectatorMode();

            if (Net.IsClient) {
                if (!Multiplayer.showMultiplayerMenu) {
                    Multiplayer.DoMultiplayerStatusMessageLog("Disconnecting because local player returned to main menu.");
                    Multiplayer._JustDisconnect();
                    Chat.LogMessage(Chat.NAMELABEL_LOCALSYSTEM, "Left the server.");
                }
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Announcing GoBackToMainMenu.");
                var writer = Net.CreateWriter(NetmsgId.CLIENT_GoBackToMainMenu);
                writer.Put(0);
                Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
                Chat.Server_ChatAnnouncement("<key:host_gotomainmenu>");
            }
        }

        Multiplayer.showMultiplayerMenu = true;
        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.OpenCraftScreen))]
public static class PlayerCamera_OpenCraftScreen_MultiplayerPatch {
    public static bool Prefix(PlayerCamera __instance) {
        if (Net.IsRunning && !__instance.craftingPanel.activeSelf && UIInGame.SpectatorModeActive) {
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetTimeScale))]
public static class PlayerCamera_SetTimeScale_MultiplayerPatch {
    public static bool force = false;

    static bool Prefix(PlayerCamera __instance, PlayerCamera.SpeedType speed) {
        if (Net.IsRunning) {
            if (speed == PlayerCamera.SpeedType.Paused) {
                return false;
            }
            if (Net.IsClient && ClientMain.ServerWorldState != null && ClientMain.ServerWorldState.timeScale.Value == 0) {
                return false;
            }

            float num = 1f;
            if (Net.IsClient && ClientMain.ServerWorldState != null) {
                num = ClientMain.ServerWorldState.timeScale.Value;
            }

            bool lobbySleeping = ServerMain.CheckIfEveryoneIsSleeping() && !Multiplayer.rules.DisableSleep;
            if (lobbySleeping) {
                num = ServerMain.SleepEnergySpeedMult;
            }

            if (!lobbySleeping && (speed == PlayerCamera.SpeedType.UnconsciousFast || speed == PlayerCamera.SpeedType.DyingFast)) {
                Time.timeScale = num;
                if (__instance.curTimeScale == PlayerCamera.SpeedType.UnconsciousFast || __instance.curTimeScale == PlayerCamera.SpeedType.DyingFast) {
                    __instance.curTimeScale = PlayerCamera.SpeedType.Normal;
                }
                return false;
            }

            if (force) {
                force = false;
            } else if (Multiplayer.rules.DisableTimeManipulation) {
                Time.timeScale = num;
                return false;
            } else if (speed != __instance.curTimeScale) {
                if (ServerMain.CheckIfAnyoneIsMoving()) {
                    Time.timeScale = num;
                    return false;
                }
                if (Net.IsClient) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SetTimeScale, (ushort)speed, true);
                } else {
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_SetTimeScaleRelay, (ushort)speed, true);
                }
            }
        }
        return true;
    }
}
