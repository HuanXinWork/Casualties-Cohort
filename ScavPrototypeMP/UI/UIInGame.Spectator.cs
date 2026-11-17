using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System.Collections.Generic;
using System.Linq;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 局内旁观者模式与自由相机（Freecam）目标切换、事件跟随与控制栏 IMGUI 视图。
    /// </summary>
    public partial class UIInGame {
        public static bool SpectatorModeActive { get; private set; }
        public static Transform SpectatorCurrentTarget { get; private set; }
        public static Transform SpectatorLastTarget { get; private set; }
        internal static bool spectator_only_alive = true;

        private static int curspectatefolowing = 0;
        private static string curspectatefolowingname = "EROR";

        public static Vector2 _DEV_FREECAM_CURPOS {
            get => ConsoleScript.instance.freecamPos;
            set => ConsoleScript.instance.freecamPos = value;
        }

        private static ushort last_action_priority = 0;
        private static float last_action_cooldown = 0f;
        private static float last_action_cooldown_countdown = 0f;
        private static readonly bool action_spectator_active = true;

        public static void Spectator_OnAction(NetBody star, ushort priority, float cooldown, float max_viewing_distance = 50f) {
            if (!SpectatorModeActive || !action_spectator_active || star == null) {
                return;
            }
            float denom = Mathf.Max(0.001f, last_action_cooldown);
            float priorityFalloff = Mathf.Clamp((last_action_cooldown_countdown + last_action_cooldown * 0.5f) / denom, 0.5f, 1f);

            if (priority > last_action_priority || last_action_cooldown_countdown <= 0f || priority > last_action_priority * priorityFalloff) {
                last_action_priority = priority;
                last_action_cooldown = cooldown;
                last_action_cooldown_countdown = cooldown;

                if (!KM.DistanceCheck(star.pos, PlayerCamera.main.transform.position, max_viewing_distance)) {
                    Spectator_SetTarget(star);
                }
            }
        }

        private static void _GUI_DoSpectatorGui() {
            StopPlayerInteractionMenu();

            if (Util.IsInWoundView() || !Util.IsWorldGenerated()) {
                return;
            }

            int dir = 0;
            float uisc = UIBullshit.uiScale;

            float halfScreenX = Screen.width * 0.5f;
            float curY = Screen.height * 0.54f + 70f * uisc;

            float iteratorBtnWidth = 60f * uisc;
            float iteratorBtnHeight = 50f * uisc;

            float width = Mathf.Max(220f * uisc, GUI.skin.label.CalcSize(FixGuiContent.Content(curspectatefolowingname, 5)).x + 16f * uisc);
            float rowHeight = 45f * uisc;

            Rect nametagRect = new Rect(halfScreenX - width * 0.5f, curY, width, iteratorBtnHeight);
            UIBullshit._GUI_9SlicePanel(nametagRect, 1f, 0.8f, true, UIBullshit.unscaled_uiBlockNano);

            TextAnchor ogAlign = GUI.skin.label.alignment;
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUI.Label(nametagRect, curspectatefolowingname);
            GUI.skin.label.alignment = ogAlign;

            width += iteratorBtnWidth * 2f;

            if (GUI.Button(UIBullshit.ConstructorCheckCursorOverlap(halfScreenX - width * 0.5f, curY, iteratorBtnWidth, iteratorBtnHeight), "<")) {
                ServerMain._ded_server_switch_counter = -30;
                dir -= 1;
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            }
            if (GUI.Button(UIBullshit.ConstructorCheckCursorOverlap(halfScreenX + width * 0.5f - iteratorBtnWidth, curY, iteratorBtnWidth, iteratorBtnHeight), ">")) {
                ServerMain._ded_server_switch_counter = -30;
                dir += 1;
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            }

            curY += iteratorBtnHeight * 1.1f;

            spectator_only_alive = GUI.Toggle(new Rect(halfScreenX - width * 0.5f, curY, width, rowHeight), spectator_only_alive, Lang.Get("spectate_alive"));
            curY += rowHeight;

            Transform followTarget = Spectator.GetCameraFollow();
            if (followTarget != null && followTarget.TryGetComponent<Body>(out _) && !Util.IsInWoundView()) {
                if (GUI.Button(new Rect(halfScreenX - width * 0.5f, curY, width, rowHeight), Lang.Get("spectate_openwoundview"))) {
                    PlayerCamera.main.ToggleWoundView();
                    return;
                }
                curY += rowHeight;
            }
            if (!Con._DEV_FREECAM && Multiplayer.rules.AllowSpectatorFreecam) {
                if (GUI.Button(new Rect(halfScreenX - width * 0.5f, curY, width, rowHeight), Lang.Get("spectate_freecam"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    Util.DoAlert(Lang.Get("freecam_keybindalert"));
                    StartFreecamMode(true);
                    return;
                }
                curY += rowHeight;
            }
            if (GUI.Button(new Rect(halfScreenX - width * 0.5f, curY, width, rowHeight), Lang.Get("spectate_exit"))) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Close);
                StopSpectatorMode();
                return;
            }

            if (dir != 0) {
                IList<ScavPlayer> plrs = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList();
                int plrAmount = plrs.Count;
                for (int i = 0; i < plrAmount; i++) {
                    curspectatefolowing += dir;
                    if (curspectatefolowing < 0) {
                        curspectatefolowing = plrAmount - 1;
                    }
                    if (curspectatefolowing >= plrAmount) {
                        curspectatefolowing = 0;
                    }

                    ScavPlayer plr = plrs[curspectatefolowing];
                    if (plr.body != null && (!spectator_only_alive || plr.body.alive)) {
                        Spectator_SetTarget(plr.netBody);
                        break;
                    }
                }
            }
        }

        public static void StartFreecamMode(bool resetpos = true) {
            Con._DEV_FREECAM = true;

            if (resetpos) {
                _DEV_FREECAM_CURPOS = PlayerCamera.main.transform.position;
            }

            MoodleManager.main.body = Util.GetLocalBody();
            PlayerCamera.main.radialOpen = false;
        }

        public static void StartSpectatorMode() {
            Con._DEV_FREECAM = false;

            if (!SpectatorModeActive) {
                SpectatorModeActive = true;
                PlayerCamera.main.endScreen.gameObject.SetActive(false);

                if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.body != null) {
                    ScavPlayer plr = ScavPlayer.LocalPlayer;
                    curspectatefolowingname = plr.playerName;
                    curspectatefolowing = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList().IndexOf(plr);

                    if (Spectator.GetCameraFollow() == null) {
                        Spectator_SetTarget(plr.body);
                    }
                } else {
                    curspectatefolowingname = "NOBODY";
                    curspectatefolowing = 0;
                }
            }
        }

        public static void StopSpectatorMode() {
            ModLog.Debug("StopSpectatorMode", LogDomain.UI);

            if (Util.IsInMainMenu()) {
                Con._DEV_FREECAM = false;
                SpectatorModeActive = false;
                return;
            }

            Con._DEV_FREECAM = false;
            if (SpectatorModeActive && !Net.IsDedicatedServer) {
                SpectatorModeActive = false;

                if (PlayerCamera.main.didDeathScreen) {
                    PlayerCamera.main.endScreen.gameObject.SetActive(true);
                }

                Spectator.SetCameraFollow(null);
                MoodleManager.main.body = Util.GetLocalBody();
            }
        }

        public static void Spectator_SetTarget(Component obj, string custom_name = null) {
            if (!SpectatorModeActive || obj == null) {
                return;
            }

            if (custom_name != null) {
                curspectatefolowingname = custom_name;
            } else if (obj.TryGetComponent(out NetBody netBodyForName)) {
                curspectatefolowingname = netBodyForName.bodyName;
            } else {
                curspectatefolowingname = obj.name;
            }

            if (obj.TryGetComponent(out Body targetBody)) {
                MoodleManager.main.body = targetBody;
            }

            Spectator.SetCameraFollow(obj.transform);
            SpectatorCurrentTarget = obj.transform;
            SpectatorLastTarget = obj.transform;

            if (obj.TryGetComponent(out NetBody pb)) {
                if (WoundView.view != null && WoundView.view.gameObject.activeSelf) {
                    PlayerCamera_ToggleWoundView_MultiplayerPatch.WoundView_SetTargetBody(pb.body);
                    WoundView.view.UpdateView();
                }
                if (pb.IsPlayer) {
                    curspectatefolowing = ScavPlayer.BodyToPlayerDict.Values.AsReadOnlyList().IndexOf(pb.plr);
                }
            }
        }
    }
}
