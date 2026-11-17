using System;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 玩家镜头拖拽交互、外部模组模态输入阻塞锁、主副手切换、商人招募复活、自毁序列与状态图标越界防护。
/// 网络接收器分离至 <see cref="PlayerCameraNetworkReceivers"/> 以避免 <c>Harmony003</c> 分析器误判。
/// </summary>
internal static class PlayerCameraNetworkReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_DrainWaterContainer)]
    static void Server_DrainWaterContainer(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid waterContainerSyncId);
        reader.Get(out float newTotal);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out var body)
            && ItemSync.TryGetItemSyncInfo(waterContainerSyncId, out var si)
            && si.IsLiquidContainer()
            && Util.IsFinite(newTotal)
            && ItemSync.CheckIfBodyReachThisItem(si, body)) {
            var lq = si.liquidcontainer;
            float curTotal = lq.CurrentTotal;
            if (!lq.item.IsInWater() && newTotal >= 0f && curTotal > newTotal) {
                lq.Drain(lq.CalculateDrain(curTotal - newTotal));
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_SwitchHands)]
    static void Server_PlayerCamera_SwitchHands(knetid clientId, NetDataReader reader) {
        reader.Get(out ushort handSlot);
        if (handSlot > AnatomyConstants.SecondaryHandSlot) {
            ModLog.Error($"Bogus handSlot={handSlot} in SwitchHands from client={clientId}", LogDomain.Security);
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.PacketReject, clientId.id, detailCode: (byte)NetmsgId.SERVER_PlayerCamera_SwitchHands);
            return;
        }

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && plr.IsAlive()) {
            plr.body.handSlot = handSlot;
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(plr.body.transform.position, $"S: PlayerCamera_SwitchHands {handSlot}");
            }

            var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerCamera_SwitchHandsRelay);
            writer.Put(clientId);
            writer.Put((byte)handSlot);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerCamera_SwitchHandsRelay)]
    static void Client_PlayerCamera_SwitchHands(knetid _, NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out byte handSlot);

        if (ScavPlayer.TryGetPlayerFromClientId(who, out var plr) && plr.body && !plr.IsLocal) {
            plr.body.handSlot = handSlot;
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(plr.body.transform.position, $"C: PlayerCamera_SwitchHandsRelay {handSlot}");
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderRecruit)]
    static void ServerReceiver_RecruitPressed(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderSyncId);
        if (TraderSync.Server_TraderInteractionCheck(clientId, traderSyncId, "MP_RECRUIT", out ScavPlayer plr, out SyncInfo traderSi, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Recruit, plr.body, traderSi);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(traderSyncId, out SyncInfo missed) && missed.trader != null) {
            TraderCommit.PublishTo(missed.trader, clientId);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_selfdestruct)]
    static void ServerReceiver_selfdestruct(knetid clientId, NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)
            && pb.alive
            && plr.ServerPlayerState.Cooldown("selfdestruct", 6f)) {
            log.devevent($"Selfdestruct {pb}", plr.pos, true);
            WorldGeneration.world.StartCoroutine(PlayerCamera_StartSelfDestruct_MultiplayerPatch.SelfDestructSequence(pb));
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.UpdateWearables))]
public static class PlayerCamera_UpdateWearables_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance, ref Body __state) {
        if (__instance.body == null) {
            __instance.body = Util.GetLocalBody();
        }
        __state = __instance.body;
        if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
            __instance.body = InvButton_get_body_MultiplayerPatch.focused_body;
        }
    }

    static void Postfix(PlayerCamera __instance, ref Body __state) {
        __instance.body = __state;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformRadialAction))]
public static class PlayerCamera_TryPerformRadialAction_MultiplayerPatch {
    static bool Prefix(ref bool __result) {
        if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleWhileDragging))]
static class PlayerCamera_HandleWhileDragging_MultiplayerPatch {
    static bool draining_liquid = false;

    static void Prefix(PlayerCamera __instance, ref bool __state) {
        __state = __instance.radialOpen;
    }

    static void Postfix(PlayerCamera __instance, ref bool __state, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning) {
            return;
        }

        var body = __instance.body;

        if (__instance.radialOpen) {
            if (InvButton_get_body_MultiplayerPatch.focused_body != null) {
                Vector2 realCurPos = Camera.main.WorldToScreenPoint(InvButton_get_body_MultiplayerPatch.focused_body.transform.position);
                InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = Vector2.Lerp(
                    InvButton_get_body_MultiplayerPatch.focused_body_position_smooth,
                    realCurPos,
                    5f * Time.deltaTime);
                __instance.radialMenu.transform.position = InvButton_get_body_MultiplayerPatch.focused_body_position_smooth;
                if (InvButton_get_body_MultiplayerPatch.focused_body.TryGetNetBody(out var npc)) {
                    __instance.radialText.text = npc.bodyName;
                }
                __instance.radialCircle.enabled = false;

                if (!KM.DistanceCheck(
                        InvButton_get_body_MultiplayerPatch.focused_body.transform.position,
                        PlayerCamera.main.body.transform.position,
                        SharedMain.max_player_interaction_distance * 2f)) {
                    __instance.radialOpen = false;
                    InvButton_get_body_MultiplayerPatch.focused_body = null;
                    __instance.radialCircle.enabled = true;
                }
            } else {
                __instance.radialCircle.enabled = true;
                InvButton_get_body_MultiplayerPatch.focused_body_position_smooth = __instance.radialMenu.transform.position;
            }
        }

        if (!__state && __instance.radialOpen && !Util.IsInWoundView()) {
            Component collider2D = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), LayerMask.GetMask("Body", "Limb"));
            if (collider2D != null) {
                Body targetBody;
                if (collider2D.TryGetComponent<Limb>(out var limb)) {
                    targetBody = limb.body;
                } else if (collider2D.TryGetComponent<Body>(out var hitBody)) {
                    targetBody = hitBody;
                } else {
                    return;
                }

                if (!targetBody.IsBodyLocal() && targetBody.TryGetNetBody(out var nb)) {
                    __instance.radialOpen = false;
                    if (UIInGame.interaction_menu_target_body == null) {
                        UIInGame.StartPlayerInteractionMenu(nb);
                    }
                } else if (UIInGame.interaction_menu_target_body != null) {
                    // 光标位于自身躯体上开启轮盘背包时，关闭远端玩家交互菜单并保持轮盘开启
                    UIInGame.StopPlayerInteractionMenu();
                }
            }
        } else {
            bool currentlyDraining = false;
            var dragItem = __instance.dragItem;

            foreach (RaycastResult uiCast in uiCasts) {
                if (uiCast.gameObject == __instance.liquidDrainObject) {
                    if ((bool)dragItem && !dragItem.IsInWater()) {
                        if (ItemSync.TryGetSyncInfo(dragItem, out var si)) {
                            si.SetIgnoreTimeForRoundTrip();
                        }
                        WaterContainerItem component = dragItem.GetComponent<WaterContainerItem>();
                        currentlyDraining = component.CurrentTotal > 0f;
                    }
                    break;
                }
            }

            if (draining_liquid && !currentlyDraining) {
                if (Net.IsClient && __instance.dragItem && ItemSync.TryGetSyncInfo(__instance.dragItem, out var si) && si.IsLiquidContainer()) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_DrainWaterContainer, si.syncId, si.liquidcontainer.CurrentTotal, true);
                }
            }
            draining_liquid = currentlyDraining;
        }

        if (UIInGame.interaction_menu_target_body != null && __instance.dragItem != null) {
            if ((Mathf.Abs(Camera.main.WorldToScreenPoint(UIInGame.interaction_menu_target_body.body.transform.position).x - Input.mousePosition.x) > 500f * PlayerCamera.uiScale
                    && !__instance.tradeMenu.activeSelf)
                || body.consciousness < 20f) {
                UIInGame.StopPlayerInteractionMenu();
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPickupFromUI))]
static class PlayerCamera_TryPickupFromUI_MultiplayerPatch {
    static void Postfix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return;
        }

        var dragItem = __instance.dragItem;
        if (dragItem == null) {
            return;
        }

        if (!NetObjectRegistry.IsRegistered(dragItem.gameObject) && NetObjectRegistry.ObjectShouldBeSynced(dragItem.gameObject)) {
            NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(dragItem.gameObject);
            ModLog.Warning($"Tried to drag unregistered UI item {dragItem?.id}", LogDomain.Item);
            __instance.dragItem = null;
            NetObjectRegistry.AlertObjectNotRegistered(true);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleInput))]
public static class PlayerCamera_HandleInput_MultiplayerPatch {
    static bool Prefix() {
        if (Net.IsRunning && PlayerCamera.main?.body != null && ServerMain.DidHeFinishTheLayer(PlayerCamera.main.body)) {
            PlayerCamera.main.body.forceWalk = false;
        }

        // 聊天框聚焦或外部模组（如 SkinSync / CuSaveManager）模态窗口激活时阻断局内快捷键与移动输入
        return !Chat.CHAT_textbox_input_focused && !TogetherPanelApi.IsAnyExternalInputBlockerActive();
    }

    static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        var body = Util.GetLocalBody();
        if (body == null || !body.alive) {
            return;
        }

        if (Input.GetKeyDown(KeyBinds.GetBind("pause"))) {
            UIInGame.StopPlayerInteractionMenu();
        }
        if (Input.GetKeyDown(KeyBinds.GetBind("bark")) && body.eatTime == 0.5f) {
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerBark, true);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SwitchHands))]
public static class PlayerCamera_SwitchHands_MultiplayerPatch {
    public static void Postfix() {
        if (Net.IsRunning && PlayerCamera.main?.body != null) {
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_SwitchHands, (ushort)PlayerCamera.main.body.handSlot, true);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.FlashBrain))]
public static class PlayerCamera_FlashBrain_MultiplayerPatch {
    public static bool force = false;

    public static void ForceFlashBrain(this PlayerCamera pc) {
        force = true;
        pc.StartCoroutine(pc.FlashBrain());
    }

    static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!force) {
            __result = new List<object>().GetEnumerator();
            return false;
        }
        force = false;
        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToggleTradeMenu))]
static class PlayerCamera_ToggleTradeMenu_MultiplayerPatch {
    public static GameObject RECRUITButton = null;
    public static int RANDOM_ITEMS_TO_GIVE_AFTER_RESPAWN = 3;

    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        var ogMoveButton = __instance.traderMoveButon;
        if (ogMoveButton != null && RECRUITButton == null) {
            try {
                RECRUITButton = GameObject.Instantiate(ogMoveButton.gameObject, ogMoveButton.transform.parent, false);
                RECRUITButton.SetActive(true);
                RECRUITButton.name = "BUTTON_MP_recruit";

                var rt = RECRUITButton.GetComponent<RectTransform>();
                var offset = new Vector2(rt.sizeDelta.x + 15f, 0f);
                rt.offsetMax += offset;
                rt.offsetMin += offset;

                rt.SetSiblingIndex(ogMoveButton.transform.GetSiblingIndex());
                var img = RECRUITButton.transform.GetChild(0).GetComponent<Image>();
                img.sprite = CoopModAssets.recruit;
                var button = RECRUITButton.GetComponent<Button>();
                button.onClick.RemoveAllListeners();
                button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                button.onClick.AddListener(() => {
                    if (PlayerCamera.main.currentTrader != null
                        && NetObjectRegistry.TryGetSyncInfoOrRegister(PlayerCamera.main.currentTrader.build, out var si)) {
                        if (Net.IsServer) {
                            TraderCommit.Submit(TraderCommit.Op.Recruit, PlayerCamera.main.body, si);
                        } else {
                            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_TraderRecruit);
                            writer.Put(si.syncId);
                            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                        }
                        PlayerCamera.main.ToggleTradeMenu();
                    }
                });
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Trader, "RECRUITButtonCreation", ex);
            }
        }
        return true;
    }

    public static List<ScavPlayer> GetPlayersEligibleForRespawn() {
        return new List<ScavPlayer>(ScavPlayer.AllDeadPlayers);
    }

    public static ScavPlayer Server_SelectMostSuitablePlayerToRespawn() {
        var candidates = GetPlayersEligibleForRespawn();
        if (candidates.Count == 0) {
            return null;
        }
        candidates.Sort((a, b) => {
            if (!a.IsAltTab && b.IsAltTab) {
                return -1;
            }
            if (a.ServerPlayerState.last_cursormove > b.ServerPlayerState.last_cursormove) {
                return -1;
            }
            if (a.ServerPlayerState.last_cursormove == b.ServerPlayerState.last_cursormove) {
                return 0;
            }
            return 1;
        });
        return candidates[0];
    }

    internal static void PerformRecruit(SyncInfo traderSi) {
        if (traderSi == null || traderSi.go == null || traderSi.IsDestroyed || !Multiplayer.rules.CanReviveFromTrader()) {
            return;
        }
        TrackerTrader traderTracker = traderSi.go.GetOrAddComponent<TrackerTrader>();
        ScavPlayer availablePlr = Server_SelectMostSuitablePlayerToRespawn();
        if (availablePlr == null || !traderTracker.CanBeRecruited_ForRespawn()) {
            return;
        }
        ModLog.Info($"Recruiting {traderSi} to respawn {availablePlr}", LogDomain.Trader);

        availablePlr.Server_RespawnCharacter(traderSi.transformPosition + Vector2.up * 1.5f, true);
        int giftCount = Math.Min(traderTracker.og.items.Count, RANDOM_ITEMS_TO_GIVE_AFTER_RESPAWN);
        for (int i = 0; i < giftCount; i++) {
            int randIndex = UnityEngine.Random.Range(0, traderTracker.og.items.Count);
            TraderItem item = traderTracker.og.items[randIndex];
            traderTracker.og.items.RemoveAt(randIndex);
            GameObject spawned = Utils.Create(item.id, traderSi.go.transform.position, 0f);
            if (spawned.TryGetComponent(out AmmoScript component) && component.itemType == AmmoScript.AmmoItemType.Magazine) {
                component.rounds = UnityEngine.Random.Range((int)(component.maxRounds * 0.5f), component.maxRounds);
            }
            availablePlr.body.AutoPickUpItem(spawned.GetComponent<Item>());
        }

        GameObject.Destroy(traderSi.go);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshTraderInventories))]
static class PlayerCamera_RefreshTraderInventories_MultiplayerPatch {
    public static GameObject RECRUITButton => PlayerCamera_ToggleTradeMenu_MultiplayerPatch.RECRUITButton;

    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        TraderPinLog.LogRefreshFault(__instance);

        if (RECRUITButton != null && __instance.currentTrader != null) {
            var tt = __instance.currentTrader.GetOrAddComponent<TrackerTrader>();
            bool isInteractable = tt.CanBeRecruited_ForRespawn();

            ScavPlayer availablePlr = null;
            if (Net.IsServer) {
                availablePlr = PlayerCamera_ToggleTradeMenu_MultiplayerPatch.Server_SelectMostSuitablePlayerToRespawn();
            } else {
                var list = PlayerCamera_ToggleTradeMenu_MultiplayerPatch.GetPlayersEligibleForRespawn();
                if (list.Count > 0) {
                    availablePlr = list[0];
                }
            }
            if (availablePlr == null) {
                isInteractable = false;
            }

            var tooltip = RECRUITButton.GetComponent<UITooltip>();
            tooltip.GetComponent<Button>().interactable = isInteractable;
            tooltip.skipLocale = true;
            tooltip.tipName = Lang.Get("trader_recruit");
            tooltip.tipDesc = isInteractable
                ? Lang.Get("trader_recruitdesc")
                : (availablePlr == null ? Lang.Get("trader_recruitdesc_no_plr") : Lang.Get("trader_recruitdesc_no_rep"));
        }

        if (RECRUITButton != null) {
            RECRUITButton.SetActive(Multiplayer.rules.CanReviveFromTrader());
        }
        return true;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.StartSelfDestruct))]
static class PlayerCamera_StartSelfDestruct_MultiplayerPatch {
    public static readonly float SelfDestructIdentifier = NetTimingConstants.SelfDestructDamageIdentifier;

    static void Postfix() {
        if (Net.IsRunning) {
            ClientRequestSuicide();
        }
    }

    public static void ClientRequestSuicide() {
        var writer = Net.CreateWriter(NetmsgId.SERVER_selfdestruct);
        writer.Put(true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

        if (NetBody.TryGetLocalNetBody(out var locnb)) {
            locnb.SetNetHealthSyncIgnoreTime(4f);
        }
    }

    internal static IEnumerator SelfDestructSequence(NetBody nb) {
        if (nb == null || nb.body == null) {
            yield break;
        }
        var body = nb.body;
        List<knetid> recipients = ServerMain.AllClientIds.ToList();
        if (nb.IsPlayer && nb.plr != null) {
            recipients.Remove(nb.plr.playerId);
        }

        bool lockedLocalCam = nb.IsBodyLocal() && PlayerCamera.main != null;
        if (lockedLocalCam) {
            PlayerCamera.main.selfDestructActive = true;
        }

        try {
            if (nb.unchipped) {
                body.GetComponent<SelfHarmer>().cooldownTime = 60f;
                body.happiness = -90f;
                body.talker.Talk(Locale.GetCharacter(VanillaAssetKeys.TalkSuicide), null, force: false, resetTalkTimer: true);
                yield return new WaitForSeconds(2f);
                if (nb == null || body == null) {
                    yield break;
                }
                if (Body.censorMood) {
                    body.TryStartFibrillation(true);
                    for (int i = 0; i < 5; i++) {
                        body.vomiter.Vomit();
                    }
                    yield return new WaitForSeconds(2f);
                    if (nb == null || body == null) {
                        yield break;
                    }
                } else {
                    var head = body.GetHead();
                    SelfHarmMultiplayerSync.SelfHarm_Copypasted(0, body, ref head);
                    yield return new WaitForSeconds(0.1f);
                    if (nb == null || body == null) {
                        yield break;
                    }
                    body.Ragdoll();
                    yield return new WaitForSeconds(0.5f);
                    if (nb == null || body == null) {
                        yield break;
                    }
                    body.Ragdoll();
                    body.consciousness *= 0.7f;
                    yield return new WaitForSeconds(1f);
                    if (nb == null || body == null) {
                        yield break;
                    }
                }
                body.brainHealth = 0f;
            } else {
                body.eyeScareTime = 10f;
                if (!nb.IsBodyLocal()) {
                    body.talker.Talk(Locale.GetCharacter(VanillaAssetKeys.TalkSelfDestruct), null, force: false, resetTalkTimer: true);
                }
                ServerMain.Server_AnnounceSound(body.GetPosition(), VanillaAssetKeys.SoundSelfDestruct, recipients);
                yield return new WaitForSeconds(3f);
                if (nb == null || body == null) {
                    yield break;
                }
                body.eyeCloseTime = 10f;
                yield return new WaitForSeconds(1.15f);
                if (nb == null || body == null) {
                    yield break;
                }

                Vector2 headPos = body.limbs[(int)LimbIndex.Head].transform.position;
                ServerMain.Server_AnnounceSound(body.GetPosition(), VanillaAssetKeys.SoundGore, recipients);
                ServerMain.Server_InstantiateInstance(headPos, 0f, VanillaAssetKeys.PrefabBloodExplosion, recipients);
                ServerMain.Server_InstantiateInstance(headPos, 0f, "Special/ExplosionParticle", recipients);

                nb.OnDamageEvent(new BodyDamageEvent(nb, nb, null, SelfDestructIdentifier));

                yield return new WaitForSeconds(0.02f);
                if (nb == null || body == null) {
                    yield break;
                }
                body.brainHealth = 0f;
                body.consciousness = 0f;
                body.heartRate = 0f;
                body.limbs[(int)LimbIndex.Head].muscleHealth = 0f;
                body.limbs[(int)LimbIndex.Head].skinHealth = 0f;
                body.limbs[(int)LimbIndex.Head].bleedAmount = 100f;
                body.Disfigure();
                body.RemoveEye();
                body.RemoveEye();
            }
        } finally {
            if (lockedLocalCam && PlayerCamera.main != null) {
                PlayerCamera.main.selfDestructActive = false;
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.SetIrradiateIntensity))]
public static class PlayerCamera_SetIrradiateIntensity_MultiplayerPatch {
    public static bool forceSetIrradiateIntensity = false;

    public static void RealSetIrradiateIntensity(float rad) {
        if (ScavPlayer.TryGetLocalNetBody(out var nb)) {
            RealSetIrradiateIntensity(nb, rad);
        }
    }

    public static void RealSetIrradiateIntensity(NetBody nb, float rad) {
        if (nb == null) {
            return;
        }
        nb.irradiateIntensity = Mathf.Max(nb.irradiateIntensity, rad);
        if (rad >= nb.irradiateIntensity) {
            nb.timeSinceRadUpdate = 0f;
        }
    }

    static bool Prefix() {
        return !Net.IsRunning;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleUnconsciousScreen))]
public static class vyosna_dawn_HandleUnconsciousScreen_Patch {
    private static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        List<CodeInstruction> list = new(instructions);
        List<int> occurrencesToRemove = new() { 0, 1 };
        int currentOccurrence = 0;
        int i = 0;
        while (i < list.Count && currentOccurrence < occurrencesToRemove.Count) {
            if (list[i].ToString().Contains(nameof(PlayerCamera.SetTimeScale))
                && occurrencesToRemove.Contains(currentOccurrence)
                && list[i].operand is MethodBase method) {
                list[i].opcode = OpCodes.Nop;
                list[i].operand = null;
                int goBackwards = method.GetParameters().Length + (method.IsStatic ? 0 : 1);
                for (int key = i; key >= i - goBackwards; key--) {
                    list[key].opcode = OpCodes.Nop;
                    list[key].operand = null;
                }
                currentOccurrence++;
            }
            i++;
        }
        return list;
    }
}

[HarmonyPatch(typeof(MoodleManager), nameof(MoodleManager.AddMoodle))]
static class MoodleManager_AddMoodle_Patch {
    static bool Prefix(MoodleManager __instance, ref int intensity) {
        if (__instance != null && __instance.backgroundIcons != null && __instance.backgroundIcons.Length > 0) {
            intensity = Mathf.Clamp(intensity, 0, __instance.backgroundIcons.Length - 1);
        }
        return true;
    }
}
