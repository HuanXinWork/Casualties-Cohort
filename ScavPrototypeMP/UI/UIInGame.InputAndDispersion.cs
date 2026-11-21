using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using Together.Api;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 局内联机输入快捷键响应、外部模组模态阻塞锁校验与防分散惩罚告警更新。
    /// </summary>
    public partial class UIInGame {
        internal bool antidispersion_active = false;
        internal bool antidispersion_warned = false;
        internal float antidispersion_cur_distance = 0f;
        internal float antidispersion_cur_required_count = 0f;
        internal int antidispersion_cur_range_count = 0;

        private void Awake() {
            main = this;
        }

        private void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Ui);

            last_action_cooldown_countdown -= Time.deltaTime;

            OGInGameIHandlingBullshit();

            if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
                Destroy(this);
                return;
            }

            if (SpectatorModeActive && Input.GetMouseButtonDown(0)) {
                Vector2 cursorPos = Util.GetCursorWorldPos();
                Body bodyOnCursor = Util.GetBodyOnPos(cursorPos);
                if (bodyOnCursor != null
                    && !Con.IsConsoleOpen()
                    && !PlayerCamera.main.woundView.activeSelf
                    && !UIUtil.IsPointerOverUIElement()
                    && bodyOnCursor.TryGetComponent(out NetBody targetPb)) {
                    Spectator_SetTarget(targetPb);
                }
            }

            ScavPlayer localSci = ScavPlayer.LocalPlayer;
            if (localSci != null) {
                if (Util.TryGetLocalBody(out _) && interaction_menu_target_body != null) {
                    float maxDist = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance);
                    if (interaction_menu_target_body.body == null
                        || localSci.body == null
                        || !localSci.body.conscious
                        || !KM.DistanceCheck(PlayerCamera.main.body.transform.position, interaction_menu_target_body.body.transform.position, maxDist)
                        || Util.IsInWoundView()) {
                        StopPlayerInteractionMenu();
                    }
                }

                UI_Update_Antidispersion();
            }
        }

        private void LateUpdate() {
        }

        private void UI_Update_Antidispersion() {
            if (!SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
                return;
            }

            ScavPlayer localSci = ScavPlayer.LocalPlayer;
            if (localSci != null && localSci.IsAlive() && localSci.levelPlayTime > 3f) {
                antidispersion_active = SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(
                    localSci,
                    out antidispersion_cur_distance,
                    out int countPlrsInRange,
                    out int requiredCountPlrsInRange);

                antidispersion_cur_range_count = countPlrsInRange;
                antidispersion_cur_required_count = requiredCountPlrsInRange;

                if (antidispersion_warned) {
                    if (!antidispersion_active && antidispersion_cur_distance < GameplayRanges.CraftGroundScanRadius) {
                        antidispersion_warned = false;
                    }
                } else if (antidispersion_active) {
                    antidispersion_warned = true;
                    // 情绪衰减由主机权威计算，本地播放蜂鸣与震屏告警提示玩家靠拢队友
                    Util.DoAlert(Lang.Get("antidispersion_activated_alert"));
                    localSci.body.talker.Talk(Locale.GetCharacter(VanillaAssetKeys.TalkLoud));
                    Sound.Play(VanillaAssetKeys.SoundBeep, localSci.pos, true, false, localSci.body.transform, 0.5f, 2, true, true);
                    PlayerCamera.main.shaker.Shake(5f);
                }
            } else {
                antidispersion_active = false;
                antidispersion_cur_distance = 0f;
            }
        }

        private bool _UpdateCheckInteractKeybinds_CheckDistNShi(NetBody targetpb) {
            ScavPlayer locPlr = ScavPlayer.LocalPlayer;
            float maxDist = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance);
            if (Util.QuickRaycastInteractionCheck(locPlr.body.transform.position, targetpb.transform.position, true)) {
                if (KM.DistanceCheck(locPlr.body, targetpb, maxDist)) {
                    StopPlayerInteractionMenu();
                    return true;
                }
                PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
            } else {
                PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
            }
            return false;
        }

        private void _UpdateCheckInteractKeybinds(NetBody targetpb) {
            ScavPlayer locPlr = ScavPlayer.LocalPlayer;
            locPlr.TryGetNetBody(out NetBody locPb);
            bool targetIsCarryingMe = targetpb.piggybacking_on == locPb || targetpb.carrying_person == locPb;

            if (Multiplayer.rules.AllowPush && Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.push))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    locPlr.netBody.Push(targetpb);
                }
            } else if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.hug))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    locPlr.netBody.Hug(targetpb);
                }
            } else if (!CoopKeybinds.IsWoundViewSameAsOG() && Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.woundview))) {
                // 当联机伤势查看与原版同键位时由 ToggleWoundView.Prefix 原生处理光标下队友，避免跨帧残留 IgnoreNext 吞掉下一次关闭操作
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    Util.DoAlert(string.Format(Lang.Get("plrint_req_woundview"), targetpb.bodyName));
                    PlayerCamera_ToggleWoundView_MultiplayerPatch.OpenSpecificBody(targetpb.body);
                }
            } else if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.inventory))) {
                if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                    ClientMain._PLRINT_Inventory(targetpb);
                }
            } else if (!targetIsCarryingMe) {
                if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.piggyback))) {
                    if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                        ClientMain._PLRINT_Piggyback(targetpb);
                    }
                } else if (Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.carry))) {
                    bool blockedByPod = false;
#if FULLGAME
                    blockedByPod = locPb != null && MacroFleshRebuilderMp.BlocksCarry(locPb);
#endif
                    if (targetpb.CanBeCarriedBySomeone() && !blockedByPod) {
                        if (_UpdateCheckInteractKeybinds_CheckDistNShi(targetpb)) {
                            ClientMain._PLRINT_Carry(targetpb);
                        }
                    } else {
                        Util.DoAlert(Lang.Get("alert_cantcarry"));
                    }
                }
            }
        }

        private void OGInGameIHandlingBullshit() {
            Vector2 cursorPos = Util.GetCursorWorldPos();

            if (Util.TryGetLocalBody(out Body locBody)) {
                if (ConsoleScript.instance.fullBright && !Con.CanCheat()) {
                    ConsoleScript.instance.fullBright = false;
                }

                if (Con._DEV_FREECAM && !Con.CanFreecam()) {
                    Con._DEV_FREECAM = false;
                }

                if (Con._DEV_FREECAM) {
                    PlayerCamera_HandleScreenShaders_MultiplayerPatch.forceDefaultShaderForNextFrame = true;
                } else {
                    if (ConsoleScript.instance.noClip) {
                        if (!locBody.standing) {
                            locBody.ForceStand();
                        }
                        locBody.rb.velocity = Vector2.zero;

                        if (!Con.CanCheat()) {
                            Con.UnNoclip();
                        }
                    }

                    _DEV_FREECAM_CURPOS = (Vector2)PlayerCamera.main.body.transform.position;
                }

                if (!Con.CanCheat()) {
                    if (ConsoleScript.instance.fullBright) {
                        ConsoleScript.instance.fullBright = false;
                    }
                } else if (Con._DEV_GODMODE) {
                    locBody.ResetHealth();
                    if (Net.IsServer) {
                        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                            if (plr.body != null) {
                                plr.Server_HealCharacter();
                            }
                        }
                    }
                }
            }

            ScavPlayer locPlr = ScavPlayer.LocalPlayer;
            if (!Net.IsRunning || locPlr == null || !Util.IsWorldGenerated()) {
                return;
            }

            // 控制台、聊天打字、暂停菜单、联机主菜单、合成搜索框或外部模组模态窗口开启期间，屏蔽世界交互热键防止误触
            bool interactHotkeysBlocked = Con.IsConsoleOpen()
                || Chat.CHAT_textbox_input_focused
                || PauseHandler.paused
                || UIMainMenu.IsOpen()
                || HostTransferUI.IsModalOpen
                || TogetherPanelApi.IsAnyExternalInputBlockerActive()
                || (PlayerCamera.main != null
                    && PlayerCamera.main.craftingPanel != null
                    && PlayerCamera.main.craftingPanel.activeSelf
                    && PlayerCamera.main.recipeFilterField != null
                    && PlayerCamera.main.recipeFilterField.isFocused);

            if (!interactHotkeysBlocked && Input.GetKeyDown(KeyBinds.GetBind(CoopKeybinds.pointfingerat))) {
                Vector2 pos = (Vector2)Camera.main.ScreenToWorldPoint(Input.mousePosition);
                ModLog.Debug($"Sending PlayerPointFingerAt! {pos}", LogDomain.UI);

                bool isFast = (Time.realtimeSinceStartupAsDouble - locPlr.is_pointingfingeratTIME) < 0.2f;
                byte fastByte = (byte)(isFast ? 1 : 0);

                locPlr.PointFingerAt(pos, fastByte);

                NetDataWriter writer = Net.CreateWriter(NetmsgId.PlayerPointFingerAt);
                writer.Put(pos);
                writer.Put(fastByte);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            }

            locBody = locPlr.body;
            if (locBody != null && !interactHotkeysBlocked) {
                Body bodyOnCursor = Util.GetBodyOnPos(cursorPos);
                if (bodyOnCursor != null
                    && !PlayerCamera.main.woundView.activeSelf
                    && locPlr.IsConscious()
                    && !bodyOnCursor.IsBodyLocal()
                    && bodyOnCursor.TryGetComponent(out NetBody targetPb)) {
                    _UpdateCheckInteractKeybinds(targetPb);
                }
            }
        }
    }
}
