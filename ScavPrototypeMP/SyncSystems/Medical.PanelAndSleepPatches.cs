using System;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Api;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using UnityEngine.UI;
using static WoundView;

namespace Together;

/// <summary>
/// 拦截健康面板小睡按钮并向服务端上报睡眠动作。
/// </summary>
[HarmonyPatch(typeof(WoundView), nameof(WoundView.TakeANap))]
public static class WoundView_TakeANap_MultiplayerPatch {
    private static void Prefix() {
        if (PlayerCamera.main == null || PlayerCamera.main.body == null || !PlayerCamera.main.body.canTakeNap) {
            return;
        }
        ScavPlayer plr = ScavPlayer.LocalPlayer;
        if (plr != null && plr.TryGetNetBody(out NetBody nb)) {
            nb.SetNetHealthSyncIgnoreTime(2f);
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerTakeANap, true);
        }
    }
}

/// <summary>
/// 睡眠与苏醒网络接收器（独立于 <c>[HarmonyPatch]</c> 类以规避值类型参数 <c>Harmony003</c> 告警）。
/// </summary>
internal static class MedicalSleepSync {
    [ServerReceiver((byte)NetmsgId.SERVER_PlayerTakeANap)]
    private static void Server_PlayerTakeANap(knetid clientId, NetDataReader reader) {
        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AwakeInWorld, LogDomain.Medical, (byte)NetmsgId.SERVER_PlayerTakeANap, out VerifiedCaller caller)) {
            return;
        }
        if (caller.NetBody.syncBody == null) {
            return;
        }

        if (!Util.IsBodyLocal(caller.Body)) {
            PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
        }
        caller.Body.TakeANap();
        PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(caller.Body.transform.position, $"S: PlayerTakeANap {caller.Player}");
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerTakeANap_Relay);
        writer.Put(caller.NetBody.netId);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerTakeANap_Relay)]
    private static void Client_PlayerTakeANap_Relay(knetid _, NetDataReader reader) {
        reader.Get(out knetid whoNetId);
        if (!NetBody.TryGetNetBodyFromId(whoNetId, out NetBody nb) || nb.body == null) {
            return;
        }

        if (!nb.body.sleeping) {
            if (!Util.IsBodyLocal(nb.body)) {
                PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = true;
            }
            nb.body.TakeANap();
            PlayerCamera_ToggleWoundView_MultiplayerPatch.IgnoreNext = false;
        }

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(nb.position, $"C: PlayerTakeANap_Relay {nb}");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerWakeTheFuckUpSamurai)]
    private static void Server_IWannaWakeUp(knetid clientId, NetDataReader reader) {
        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AliveInWorld, LogDomain.Medical, (byte)NetmsgId.SERVER_PlayerWakeTheFuckUpSamurai, out VerifiedCaller caller)) {
            return;
        }
        caller.Body.WakeUp();
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(caller.Body.transform.position, $"S: Wake up {caller.Player}");
        }
    }
}

/// <summary>
/// 健康面板开关与目标躯体切换补丁，支持队友检视、CPR 按钮挂载、观战检视及外部模组贴图事件分发。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ToggleWoundView))]
public static class PlayerCamera_ToggleWoundView_MultiplayerPatch {
    public static bool IgnoreNext = false;
    public static GameObject CPRButton = null;
    public static Body body_to_open = null;

    public static void WoundView_SetTargetBody(Body b) {
        WoundView wv = WoundView.view;
        if (wv == null || b == null) {
            return;
        }
        wv.body = b;

        bool lookingAtSomeoneElse = !Util.IsBodyLocal(wv.body);
        if (wv.napbutton != null) {
            wv.napbutton.gameObject.SetActive(!lookingAtSomeoneElse);
        }

        Transform workoutButton = wv.transform.Find("WorkoutButton");
        if (workoutButton != null) {
            if (CPRButton == null) {
                try {
                    CPRButton = UnityEngine.Object.Instantiate(workoutButton.gameObject, workoutButton.transform.parent, false);
                    CPRButton.SetActive(true);
                    CPRButton.name = "BUTTON_MP_CPR";

                    RectTransform rt = CPRButton.GetComponent<RectTransform>();
                    UITooltip tooltip = CPRButton.GetComponent<UITooltip>();
                    tooltip.skipLocale = true;
                    tooltip.tipName = Lang.Get("wv_cpr");
                    tooltip.tipDesc = Lang.Get("wv_cprdesc");

                    rt.SetSiblingIndex(workoutButton.GetSiblingIndex());
                    Image img = CPRButton.GetComponent<Image>();
                    img.sprite = CoopModAssets.cpr_button_icon;

                    Button button = CPRButton.GetComponent<Button>();
                    button.onClick.RemoveAllListeners();
                    button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                    button.onClick.AddListener(() => {
                        if (CPRHandler.CheckIfBodyIsNotOkAndNeedCPR(WoundView.view.body)
                            && CPRHandler.IsCPRBeingPerformedOnThisBody_IsMeOrNobody(WoundView.view.body)) {
                            ModLog.Debug("开启 CPR 心肺复苏小游戏", LogDomain.Medical);
                            if (MinigameBase.main.currentMinigame is ManualDefibMinigame) {
                                MinigameBase.main.EndMinigame();
                            }
                            MinigameBase.main.StartMinigame(new CPRMinigame(WoundView.view.body), null);
                        }
                    });
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.Medical, "CPRButtonCreation", ex);
                }
            }

            workoutButton.gameObject.SetActive(!lookingAtSomeoneElse);
            Button workoutBtnComp = workoutButton.GetComponent<Button>();
            if (workoutBtnComp != null) {
                workoutBtnComp.interactable = !lookingAtSomeoneElse;
            }
        } else {
            ModLog.Error("WoundView 中未找到 WorkoutButton 节点", LogDomain.Medical);
        }

        if (CPRButton != null) {
            CPRButton.SetActive(lookingAtSomeoneElse);
            Button cprBtnComp = CPRButton.GetComponent<Button>();
            if (cprBtnComp != null) {
                cprBtnComp.interactable = lookingAtSomeoneElse;
            }
        } else {
            ModLog.Error("未能初始化健康面板 CPRButton", LogDomain.Medical);
        }

        SetWoundviewSpeciesSprite(wv, b);

        if (lookingAtSomeoneElse) {
            if (b.TryGetNetBody(out NetBody nb)) {
                wv.nameText.text = nb.bodyName;
                wv.bodyStatText.text = WoundViewName(nb);
            } else {
                ModLog.Error($"检视非本地躯体时未找到对应 NetBody: {wv.body}", LogDomain.Medical);
                wv.nameText.text = GetSpeciesName(b);
                wv.bodyStatText.text = "ERROR";
            }
        } else {
            wv.nameText.text = GetSpeciesName(b);
            wv.SetCharDetails(wv.cInfo[0], wv.cInfo[1], wv.cInfo[2], wv.cInfo[3]);
        }

        WoundView_AddImageToLimb_MultiplayerPatch.RefreshAllLimbImages();
    }

    private static string WoundViewName(NetBody nb) {
        string name = nb != null ? nb.playerName : null;
        if (!string.IsNullOrEmpty(name) && name != "NO_NAME_BODY") {
            return name;
        }
        if (nb != null && nb.IsPlayer && nb.plr != null) {
            return Lang.Get("wv_peerid") + nb.plr.playerId;
        }
        return Lang.Get("wv_peerid") + (nb != null && nb.syncBody != null ? nb.netId.ToString() : string.Empty);
    }

    private static string GetSpeciesName(Body body) {
        int chr = body != null ? Mathf.Clamp(body.charType, 0, 2) : 0;
        return chr switch {
            1 => Locale.GetOther("milky").ToUpper(),
            2 => Locale.GetOther("dune").ToUpper(),
            _ => Locale.GetOther("experiment").ToUpper(),
        };
    }

    /// <summary>
    /// 切换观察目标时同步刷新头部物种贴图，并通知 <see cref="TogetherPanelApi"/>（供 <c>SkinSync</c> 覆盖自定义伤势/肌肉贴图）。
    /// </summary>
    private static void SetWoundviewSpeciesSprite(WoundView wv, Body body) {
        if (wv == null || body == null) {
            return;
        }

        int chr = Mathf.Clamp(body.charType, 0, 2);
        if (wv.speciesText != null) {
            wv.speciesText.text = GetSpeciesName(body);
        }

        if (wv.headCharSprites != null && wv.headCharSprites.Length >= 6
            && wv.limbImages != null && wv.limbImages.Length > (int)LimbIndex.Head
            && wv.limbImages[(int)LimbIndex.Head] != null) {
            int outer = chr * 2;
            int inner = outer + 1;
            Image head = wv.limbImages[(int)LimbIndex.Head];
            if (wv.headCharSprites[outer] != null) {
                head.sprite = wv.headCharSprites[outer];
                head.SetNativeSize();
            }
            if (head.transform.childCount > 0) {
                Image muscle = head.transform.GetChild(0).GetComponent<Image>();
                if (muscle != null && wv.headCharSprites[inner] != null) {
                    muscle.sprite = wv.headCharSprites[inner];
                    muscle.SetNativeSize();
                }
            }
        }

        TogetherPanelApi.NotifyWoundViewTargetApplied(wv, chr);
    }

    public static bool IsOpen() {
        return WoundView.view != null && WoundView.view.gameObject.activeSelf;
    }

    public static bool HealthTargetGone() {
        WoundView view = WoundView.view;
        if (view == null) {
            return true;
        }
        Body b = view.body;
        if (b == null) {
            return true;
        }
        if (!Net.IsRunning) {
            return false;
        }
        if (!b.TryGetNetBody(out NetBody nb)) {
            return true;
        }
        return nb.syncBody == null;
    }

    public static void CloseHealthPanel(PlayerCamera cam) {
        body_to_open = null;
        if (cam == null) {
            return;
        }
        if (cam.woundView != null) {
            cam.woundView.SetActive(false);
        }
        if (cam.mainView != null) {
            cam.mainView.SetActive(true);
        }
        if (UIInGame.interaction_menu_target_body != null && PlayerCamera.main != null) {
            UIInGame.StopPlayerInteractionMenu();
        }
    }

    public static void OpenSpecificBody(Body b, bool sound = true) {
        if (b == null || PlayerCamera.main == null || WoundView.view == null) {
            return;
        }
        // 先收起轮盘背包，避免原版 ToggleWoundView 因 radialOpen 为真仅关背包就直接返回
        PlayerCamera.main.radialOpen = false;
        if (IsOpen()) {
            if (b == WoundView.view.body) {
                return;
            }
            WoundView_SetTargetBody(b);
            WoundView.view.UpdateView();
            WoundView.view.timeOpen = 0f;
            if (sound) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.HealthPanelOn);
            }
            return;
        }
        body_to_open = b;
        PlayerCamera.main.ToggleWoundView(sound);
    }

    public static bool Prefix(PlayerCamera __instance, bool sound) {
        WoundView_UpdateView_MultiplayerPatch.lastHealerList.Clear();
        WoundView_UpdateView_MultiplayerPatch.ClearHealerUI();

        if (IgnoreNext) {
            IgnoreNext = false;
            return false;
        }

        if (IsOpen() && HealthTargetGone()) {
            CloseHealthPanel(__instance);
            return false;
        }

        // 本地角色开着健康面板暴毙且尚未进入观战时强制关闭面板，避免原版因 !body.alive 直接返回导致面板锁死
        if (IsOpen() && (__instance.body == null || !__instance.body.alive) && !UIInGame.SpectatorModeActive) {
            CloseHealthPanel(__instance);
            if (sound) {
                __instance.PlayUISound(PlayerCamera.UISoundType.Close);
            }
            return false;
        }

        if (!IsOpen()) {
            if (Util.IsLocalBodyAlive() && CoopKeybinds.IsWoundViewSameAsOG() && Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.woundview))) {
                Body hoveredBody = Util.GetBodyOnPos(Util.GetCursorWorldPos());
                if (hoveredBody != null && !hoveredBody.IsBodyLocal()
                    && KM.DistanceCheck(Util.GetLocalBody(), hoveredBody, SharedMain.max_player_interaction_distance)) {
                    body_to_open = hoveredBody;
                }
            }

            if (body_to_open != null) {
                __instance.radialOpen = false;
            }

            if (body_to_open == null && UIInGame.SpectatorModeActive && Spectator.GetCameraFollow() != null
                && Spectator.GetCameraFollow().TryGetComponent(out Body targetBody)) {
                WoundView_SetTargetBody(targetBody);
            } else {
                WoundView_SetTargetBody(body_to_open ?? PlayerCamera.main.body);
            }
        }
        body_to_open = null;

        if (UIInGame.SpectatorModeActive && (__instance.body == null || !__instance.body.alive)) {
            GameObject view = WoundView.view.gameObject;
            if (sound) {
                __instance.PlayUISound(view.activeSelf ? PlayerCamera.UISoundType.Close : PlayerCamera.UISoundType.HealthPanelOn);
            }
            view.SetActive(!view.activeSelf);
            WoundView.view.UpdateView();
            WoundView.view.timeOpen = 0f;
            if (__instance.mainView != null) {
                __instance.mainView.SetActive(!view.activeSelf);
            }
            return false;
        }
        return true;
    }

    public static Exception Finalizer(PlayerCamera __instance, Exception __exception) {
        if (__instance != null && __instance.mainView != null && __instance.woundView != null) {
            __instance.mainView.SetActive(!__instance.woundView.activeSelf);
        }
        if (__exception != null) {
            ModLog.Exception(LogDomain.Medical, "ToggleWoundView.UpdateView", __exception);
        }
        return null;
    }
}

/// <summary>
/// 联机规则下对夹板耐久损耗速率叠加 <c>AdditionalHealthDecay</c> 倍率。
/// </summary>
[HarmonyPatch(typeof(SplintLimb), nameof(SplintLimb.Start))]
internal static class SplintLimb_Start_MultiplayerPatch {
    private static void Postfix(SplintLimb __instance) {
        if (Net.IsRunning) {
            __instance.conditionLossMinute += __instance.conditionLossMinute * Multiplayer.rules.AdditionalHealthDecay;
        }
    }
}

/// <summary>
/// 为健康面板上的肢体附加贴图（绷带、夹板、止血带等）绑定归属躯体跟踪器，仅在检视对应角色且未断肢时显示。
/// </summary>
[HarmonyPatch(typeof(WoundView), nameof(WoundView.AddImageToLimb))]
public static class WoundView_AddImageToLimb_MultiplayerPatch {
    private sealed class WoundviewLimbImagePerPlayerTrackerComponent : MonoBehaviour {
        public Limb limb;
        public Body body => limb != null ? limb.body : null;
        public Image image => GetComponent<Image>();

        public void RefreshLimbImage() {
            Image img = image;
            if (img != null && WoundView.view != null) {
                img.enabled = limb != null && body == WoundView.view.body && !limb.dismembered;
            }
        }
    }

    public static void RefreshAllLimbImages() {
        if (WoundView.view == null) {
            return;
        }
        var trackers = WoundView.view.GetComponentsInChildren<WoundviewLimbImagePerPlayerTrackerComponent>();
        for (int i = 0; i < trackers.Length; i++) {
            trackers[i].RefreshLimbImage();
        }
    }

    private static void Prefix(WoundView __instance, Limb limb, out Body __state) {
        __state = __instance.body;
        if (limb != null) {
            __instance.body = limb.body;
        }
    }

    private static void Postfix(WoundView __instance, Image __result, Limb limb, Body __state) {
        __instance.body = __state;
        if (__result == null) {
            return;
        }
        var tracker = __result.gameObject.AddComponent<WoundviewLimbImagePerPlayerTrackerComponent>();
        tracker.limb = limb;
        tracker.RefreshLimbImage();
    }
}
