using System;
using CasualtiesTogetherUtils;
using Together.Api;
using UnityEngine;

namespace Together.Diagnostics {
    /// <summary>
    /// 局内轻量状态透视与输入锁诊断面板（可通过控制台 <c>mp_debug_hud</c> 或 <see cref="Visible"/> 随时开关）。
    /// </summary>
    public static class MpDebugOverlay {
        public static bool Visible { get; set; }

        private static Rect _windowRect = new Rect(18f, 120f, 390f, 250f);

        /// <summary>实时计算当前阻塞本地角色移动或面板热键的全部激活原因位。</summary>
        public static InputBlockFlags EvaluateInputBlocks() {
            InputBlockFlags flags = InputBlockFlags.None;

            if (Con.IsConsoleOpen()) {
                flags |= InputBlockFlags.ConsoleOpen;
            }
            if (Chat.CHAT_textbox_input_focused) {
                flags |= InputBlockFlags.ChatInputFocused;
            }
            if (PauseHandler.paused) {
                flags |= InputBlockFlags.PauseMenuOpen;
            }
            if (TogetherPanelApi.IsAnyExternalInputBlockerActive()) {
                flags |= InputBlockFlags.ExternalModModal;
            }

            PlayerCamera cam = PlayerCamera.main;
            if (cam != null) {
                if (cam.woundView != null && cam.woundView.activeSelf) {
                    flags |= InputBlockFlags.WoundViewOpen;
                }
                if (cam.radialOpen) {
                    flags |= InputBlockFlags.RadialInventoryOpen;
                }
                if (cam.tradeMenu != null && cam.tradeMenu.activeSelf) {
                    flags |= InputBlockFlags.TradeMenuOpen;
                }
                if (cam.craftingPanel != null && cam.craftingPanel.activeSelf
                    && cam.recipeFilterField != null && cam.recipeFilterField.isFocused) {
                    flags |= InputBlockFlags.CraftingSearchFocus;
                }
                if (MinigameBase.main != null) {
                    flags |= InputBlockFlags.MinigameActive;
                }
                if (cam.selfDestructActive) {
                    flags |= InputBlockFlags.SelfDestructLocked;
                }
                if (cam.body == null || !cam.body.alive || !cam.body.conscious) {
                    flags |= InputBlockFlags.BodyDeadOrUnconscious;
                }
            }

            if (WorldGeneration.world != null && WorldGeneration.world.cutsceneActive) {
                flags |= InputBlockFlags.CutsceneActive;
            }

            return flags;
        }

        public static void DrawOnGui() {
            if (!Visible) {
                return;
            }
            _windowRect = GUILayout.Window(948271, _windowRect, DrawWindowContents, "Together Diagnostics (" + (ModLog.IsDebugBuild ? "DEBUG" : "RELEASE") + ")");
        }

        private static void DrawWindowContents(int windowId) {
            InputBlockFlags blocks = EvaluateInputBlocks();
            string role = !Net.IsRunning ? "Offline" : (Net.IsServer ? "Host/Server" : "Client");
            string transport = !Net.IsRunning ? "None" : (Net.IsRunningSteam ? "Steam" : "LiteNetLib");
            int online = ScavPlayer.ClientIdToPlayerDict != null ? ScavPlayer.ClientIdToPlayerDict.Count : 0;
            int living = ScavPlayer.AllLivingPlayers != null ? ScavPlayer.AllLivingPlayers.Count : 0;

            GUILayout.Label($"Session: {role} ({transport}) | Players: {living}/{online} alive");
            GUILayout.Label($"Net: Ping={ClientMain.LOCAL_PING}ms | SrvFPS={ClientMain.SERVER_FPS} | SrvTPS={ClientMain.SERVER_TPS} | Tick={(Net.IsRunning ? Net.ServerTick : (ushort)0)}");
            GUILayout.Label($"FlightRecorder: {MpFlightRecorder.TotalRecorded} events");
            GUILayout.Label($"InputLocks: {blocks}");

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("导出诊断报告 (Report)")) {
                MpDiagnosticReport rep = MpDiagnosticReporter.GenerateReport("OverlayManualButton");
                Util.DoAlert("诊断报告已导出: " + System.IO.Path.GetFileName(rep.SavedSummaryPath));
            }
            if (GUILayout.Button("关闭 (Close)")) {
                Visible = false;
            }
            GUILayout.EndHorizontal();

            GUI.DragWindow(new Rect(0f, 0f, 10000f, 24f));
        }
    }
}
