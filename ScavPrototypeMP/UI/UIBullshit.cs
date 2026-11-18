using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together {
    /// <summary>
    /// 全局 IMGUI 顶层渲染调度器、网络质量周期采样、缩放换算、光标遮挡检测与悬浮提示框单例。
    /// 皮肤构建、纹理缩放变暗缓存与输入字体固定逻辑见 <c>UIBullshit.Skin.cs</c>。
    /// </summary>
    public partial class UIBullshit : CasualtiesTogetherSingleton {
        public static UIBullshit main { get; private set; }

        internal ECGVisualizer egg;

        public static string net_debug_msg = "";

        private static Vector2 last_screensize = Vector2.zero;

        public static Canvas canvas {
            get {
                if (Util.IsInMainMenu()) {
                    return PreRunScript.instance.GetComponent<Canvas>();
                }
                return PlayerCamera.main.mainCanvas;
            }
        }

        [SettingDeclarerThingyBool("setting_ui_discordrpc_allowjoin")]
        public static bool DISCORD_ALLOWJOINBUTTON = true;

        [SettingDeclarerThingyBool("setting_ui_discordrpc_onlyask")]
        public static bool DISCORD_ONLYASKTOJOINBUTTON = false;

        [SettingDeclarerThingyBool("setting_ui_talker_species_font")]
        public static bool USE_TALKER_SPECIES_FONT = true;

        // 关闭时保持物种专属说话字体，位于联机界面设置分页
        [SettingDeclarerThingyBool("setting_ui_defaulttalkerfont")]
        public static bool USE_DEFAULT_TALKER_FONT = false;

        [SettingDeclarerThingyBool("setting_ui_usegamefont")]
        public static bool USE_GAME_FONT = true;

        public static bool USE_NEW_UI = false;

        public static float CUSTOM_UI_SCALE_FOR_UIBULLSHIT = 1f;
        public static float DEFAULT_RETROGAME_FONTSIZE = 20f;
        public static float THING_FOR_UI_SCALE = 3f;

        public static float uiScale = 1f;
        public static float uiScaleMultiplier = 1f;
        public static float uiScaleForMostUI = 1f;
        public static int uiBlockSmallBorderSize = 3;
        public static int uiBlockNanoBorderSize = 2;

        private static int TOTAL_INTERACTIVE_UI = 0;
        private static int TOTAL_FOCUSED_INTERACTIVE_UI = 0;
        public static bool IS_CURSOR_OVERLAPPING = false;

        private static bool _gui_tooltip = false;
        private static string _gui_tooltip_name = "";
        private static string _gui_tooltip_desc = "";

        public static bool _reset_gui_textures_next_frame = false;

#if !RELEASE
        private static float _debugSliderValue = 0f;
        private static bool _debugRelativeScale = true;
        private static bool _renderDebugOverlay = false;
#endif

        private static readonly float TOOLTIP_SCALE = 0.78f;

        private void Awake() {
            if (main != null) {
                throw new InvalidOperationException("Duplicate UIBullshit singleton instance detected.");
            }
            main = this;
        }

        private void Start() {
            WorldMP.OnWorldgenFinish += WorldgenPatches_OnWorldgenFinish;
            InvokeRepeating(nameof(SecondUpdate), 0.5f, 1f);
        }

        private void OnDestroy() {
            WorldMP.OnWorldgenFinish -= WorldgenPatches_OnWorldgenFinish;
            IS_CURSOR_OVERLAPPING = false;
        }

        private void WorldgenPatches_OnWorldgenFinish() {
            // 世界生成完成后强制下一帧重算分辨率与缩放纹理
            last_screensize = Vector2.zero;
        }

        private void SecondUpdate() {
            net_debug_msg = Net.GetDebugStatsString();

            float avgPing = 0f;
            float packetStability = 0f;
            if (ScavPlayer.ClientIdToPlayerDict.Count > 0) {
                foreach (ScavPlayer item in ScavPlayer.AllPlayers) {
                    avgPing += (float)(item.ping * 1000.0);
                }
                avgPing /= ScavPlayer.ClientIdToPlayerDict.Count;
            }

            int totalStatuses = 0;
            if (Net.IsServer) {
                if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                    ulong me = KSteam.GetLocalUserSteamID().m_SteamID;
                    foreach (KeyValuePair<ulong, TransportSteamworks.SteamConnectionData> item in tsteam.connectionMapping) {
                        if (item.Key == me) {
                            continue;
                        }
                        packetStability += item.Value.connectionStatus.m_flConnectionQualityLocal;
                        totalStatuses += 1;
                    }
                } else if (Net.Transport is TransportLiteNetLib tlnl) {
                    List<NetPeer> peers = new List<NetPeer>();
                    tlnl.NetManager.GetConnectedPeers(peers);
                    foreach (NetPeer item in peers) {
                        float lossRatio = (float)item.Statistics.PacketLossPercent * 0.01f;
                        packetStability += Mathf.Clamp01(1f - lossRatio);
                        totalStatuses += 1;
                    }
                }
            }

            packetStability = totalStatuses > 0 ? (packetStability / totalStatuses) : 1f;

            ServerMain.AVERAGE_PING = avgPing;
            if (Net.IsServer) {
                ServerMain.AVG_CONNECTION_QUALITY = (byte)Mathf.Clamp(packetStability * 100f, 0f, 100f);
                if (Net.IsClient) {
                    ServerMain.AVG_CONNECTION_QUALITY = ClientMain.MY_CONNECTION_QUALITY;
                } else {
                    ClientMain.MY_CONNECTION_QUALITY = ServerMain.AVG_CONNECTION_QUALITY;
                }
            } else {
                ServerMain.AVG_CONNECTION_QUALITY = ClientMain.MY_CONNECTION_QUALITY;
            }

            Application.runInBackground = Net.IsRunning || !Util.IsWorldGenerated() || UIMainMenu.IsOpen();
        }

        private void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Ui);

            if (egg != null) {
                egg.timeToUpdate += Time.unscaledDeltaTime;
            }

            if (IS_CURSOR_OVERLAPPING && GlobalDark.main != null) {
                GlobalDark.main.SetTooltip(("", ""));
            }

            try {
                Canvas activeCanvas = canvas;
                if (activeCanvas != null) {
                    uiScale = activeCanvas.scaleFactor;
                    uiScaleMultiplier = uiScale * CUSTOM_UI_SCALE_FOR_UIBULLSHIT;
                    uiScaleForMostUI = uiScaleMultiplier * THING_FOR_UI_SCALE;
                    uiBlockSmallBorderSize = Mathf.CeilToInt(uiScaleForMostUI * 4f);
                    uiBlockNanoBorderSize = Mathf.CeilToInt(uiScaleForMostUI * 2f);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIBullshit.Update.ScaleCalc", ex);
            }

            if (Input.GetKeyDown(KeyBinds.GetBind("pause"))) {
                if (PauseHandler.main != null && PauseHandler.main.disallowPause && !UIMainMenu.IsOpen()) {
                    UIMainMenu.SetOpen(true);
                } else {
                    UIMainMenu.SetOpen(false);
                }
            }

            if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
                return;
            }

            if (PlayerCamera.main != null) {
                PlayerCamera.main.GetOrAddComponent<UIInGame>();
            }
        }

        private void LateUpdate() {
            // 使用 Unicode 清洗替代纯 ASCII 正则，避免每帧将玩家输入或 Steam 读取的中文/俄文昵称清空
            UIMainMenu.USERINPUT_NAME = Multiplayer.SanitizeUnicodeTextInput(UIMainMenu.USERINPUT_NAME);

            if (_reset_gui_textures_next_frame) {
                _reset_gui_textures_next_frame = false;
                ResizeGUITextures();
            }
            if (last_screensize != Util.GetResolution()) {
                _reset_gui_textures_next_frame = true;
            }

            IS_CURSOR_OVERLAPPING = TOTAL_FOCUSED_INTERACTIVE_UI > 0;
            TOTAL_INTERACTIVE_UI = 0;
            TOTAL_FOCUSED_INTERACTIVE_UI = 0;
        }

        public static Vector2 ScaleSize(in float x, in float y) {
            return new Vector2(x, y) * uiScale;
        }

        public static Rect ScaleRect(float x, float y, float width, float height, bool check_overlap = false) {
            x *= uiScale;
            y *= uiScale;
            width *= uiScale;
            height *= uiScale;
            return check_overlap ? ConstructorCheckCursorOverlap(x, y, width, height) : new Rect(x, y, width, height);
        }

        public static Rect ScaleRectButNotPos(in float x, in float y, float width, float height, bool check_overlap = false) {
            width *= uiScale;
            height *= uiScale;
            return check_overlap ? ConstructorCheckCursorOverlap(x, y, width, height) : new Rect(x, y, width, height);
        }

        public static Vector2 GetScreenSize() {
            return new Vector2(Screen.width, Screen.height);
        }

        public static Vector2 GetHalfScreenSize() {
            return GetScreenSize() * 0.5f;
        }

        public static bool CheckCursorOverlap(in Rect rect) {
            TOTAL_INTERACTIVE_UI += 1;
            if (IsCursorInGUIRect(rect)) {
                IS_CURSOR_OVERLAPPING = true;
                TOTAL_FOCUSED_INTERACTIVE_UI += 1;
                return true;
            }
            return false;
        }

        public static Rect HollowCheckCursorOverlap(in Rect rect) {
            CheckCursorOverlap(rect);
            return rect;
        }

        public static Rect ConstructorCheckCursorOverlap(in float x, in float y, in float width, in float height) {
            Rect rect = new Rect(x, y, width, height);
            CheckCursorOverlap(rect);
            return rect;
        }

        public static Vector2 GetCursorPosInGUI() {
            return new Vector2(Input.mousePosition.x, Screen.height - Input.mousePosition.y);
        }

        public static bool IsCursorInGUIRect(in Rect area) {
            return area.Contains(GetCursorPosInGUI());
        }

        public static void _GUI_SetTooltip(in string name, in string desc) {
            _gui_tooltip = true;
            _gui_tooltip_name = name;
            _gui_tooltip_desc = desc;
        }

        public static void _GUI_BiggerLabel(in string txt, float multiplier) {
            int ogFontSize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = (int)(ogFontSize * multiplier);
            try {
                GUIContent content = FixGuiContent.Content(txt, 1);
                Vector2 size = GUI.skin.label.CalcSize(content);
                GUILayout.Label(content, GUILayout.Height(size.y));
            } finally {
                GUI.skin.label.fontSize = ogFontSize;
            }
        }

        public static bool _GUI_FocusableButton(in Rect rect, in string text, out bool focused, GUIStyle style = null) {
            bool result = style == null ? GUI.Button(rect, text) : GUI.Button(rect, text, style);
            focused = IsCursorInGUIRect(rect);

            TOTAL_INTERACTIVE_UI += 1;
            if (focused) {
                IS_CURSOR_OVERLAPPING = true;
                TOTAL_FOCUSED_INTERACTIVE_UI += 1;
            }
            return result;
        }

        public static void _GUI_9SlicePanel(in Rect rect, in float brightness, in float alpha, bool check_overlap, Texture2D tex = null) {
            Texture2D ogBackground = GUI.skin.box.normal.background;

            tex ??= unscaled_uiBlockSmall;
            if (tex != null) {
                GUI.skin.box.normal.background = GetChangedTex(tex, brightness, alpha);

                if (tex == unscaled_uiBlockSmall) {
                    int border = uiBlockSmallBorderSize;
                    GUI.skin.box.border = new RectOffset(border, border, border, border);
                } else if (tex == unscaled_uiBlockNano) {
                    int border = uiBlockNanoBorderSize;
                    GUI.skin.box.border = new RectOffset(border, border, border, border);
                }
            }
            GUI.Box(rect, "");
            GUI.skin.box.normal.background = ogBackground;

            if (check_overlap) {
                CheckCursorOverlap(rect);
            }
        }

        public static bool IsAnyMenuOpen() {
            return Util.IsInPauseMenuOrMainMenu() || UIMainMenu.IsOpen();
        }

        private void OnGUI() {
            using (FindTheBugInator.Time(FindTheBugInator.Ui)) {
                _GUI_DoTheFullGUI();
            }
        }

        private void RenderAllMenus() {
            try {
                if (!Con.IsConsoleOpen()) {
                    try {
                        UIMainMenu._GUI_DrawSideMenuWithPlayerList();
                        UIMainMenu._GUI_DrawMPMainMenu();
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.UI, "UIBullshit.RenderAllMenus.MainMenu", ex);
                    }
                }

                Chat._GUI_DrawChat();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIBullshit.RenderAllMenus.Chat", ex);
            }

            if (UIInGame.main != null) {
                UIInGame.main._GUI_DoInGameUI();
            }
        }

        private void _GUI_DoTheFullGUI() {
            try {
                try {
                    bool chrome = WantsMpChrome();
                    if (chrome) {
                        _GUI_CaptureOGUITextures();
                        _GUI_DoTexturesForMPUserInterface();
                    }

                    try {
                        GUI.enabled = !GUILayout_DropdownMenu.isOpen;
                        try {
                            RenderAllMenus();
                        } finally {
                            GUI.enabled = true;
                        }

                        GUILayout_DropdownMenu.Draw();

                        if (!Con.IsConsoleOpen()) {
                            try {
                                UIChoicePrompt.instance._GUI_RenderUIChoicePrompt();
                            } catch (Exception ex) {
                                ModLog.Exception(LogDomain.UI, "UIBullshit._GUI_RenderUIChoicePrompt", ex);
                            }
                        }

                        RenderFloatingTooltip();
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.UI, "UIBullshit._GUI_DoTheFullGUI.MainRender", ex);
                    } finally {
                        if (chrome) {
                            _GUI_ResetToOGUITextures();
                        }
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, "UIBullshit._GUI_DoTheFullGUI.Chrome", ex);
                }

                if (Con.IsConsoleOpen()) {
                    return;
                }

#if !RELEASE
                try {
                    _GUI_CaptureOGUITextures();
                    _GUI_DoTexturesForMPUserInterface();

                    float sc = _debugRelativeScale ? uiScale : 1f;
                    if (_renderDebugOverlay) {
                        Vector2 pos = (Vector2)Util.GetCursorPos();
                        pos = new Vector2(pos.x, Screen.height - pos.y);
                        _GUI_9SlicePanel(new Rect(pos.x + 5f, pos.y + 5f, 150f * sc, 40f * sc), 1f, 0.6980392f, false);

                        if (GUI.Button(new Rect(Screen.width - 200f * sc, 0f, 200f * sc, 50f * sc), "Make UI relative")) {
                            _debugRelativeScale = !_debugRelativeScale;
                        }
                        GUI.Label(new Rect(Screen.width - 300f * sc, 0f, 100f * sc, 50f * sc), "Lorem");

                        float prevScaleFactor = THING_FOR_UI_SCALE;
                        THING_FOR_UI_SCALE = GUI.HorizontalSlider(new Rect(Screen.width - 500f * sc, 90f * sc, 200f * sc, 20f * sc), prevScaleFactor, 0.1f, 5f);
                        if (!Mathf.Approximately(prevScaleFactor, THING_FOR_UI_SCALE)) {
                            ResizeGUITextures();
                        }

                        float prevSlider = _debugSliderValue;
                        _debugSliderValue = GUI.HorizontalSlider(new Rect(Screen.width - 500f * sc, 110f * sc, 200f * sc, 20f * sc), prevSlider, 0f, 10f);
                        if (!Mathf.Approximately(prevSlider, _debugSliderValue)) {
                            ResizeGUITextures();
                        }

                        GUI.Label(
                            new Rect(Screen.width - 500f * sc, 130f * sc, 500f * sc, 400f * sc),
                            $"scaleFactor: {canvas.scaleFactor}\nuiScaleMultiplier: {uiScaleMultiplier}\nuiScaleForMostUI: {uiScaleForMostUI}\nTHING_FOR_UI_SCALE: {THING_FOR_UI_SCALE}\nRETROGAME_FONTSIZE: {DEFAULT_RETROGAME_FONTSIZE}\nOVERLAP: {IS_CURSOR_OVERLAPPING}");

                        if (GUI.Button(new Rect(Screen.width - 500f * sc, 40f * sc, 200f * sc, 50f * sc), "Reset textures")) {
                            ResizeGUITextures();
                        }

                        _GUI_9SlicePanel(new Rect(Screen.width - 700f * sc, 40f * sc, 200f * sc, 100f * sc), 1f, 0.6980392f, false);
                    }
                    _renderDebugOverlay = GUI.Toggle(ConstructorCheckCursorOverlap(Screen.width - 500f * sc, 0f, 200f * sc, 40f * sc), _renderDebugOverlay, "RENDER DEBUG UI");
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, "UIBullshit.DebugOverlay", ex);
                } finally {
                    _GUI_ResetToOGUITextures();
                }
#endif
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIBullshit.OnGUI", ex);
            }

            if (!Con.IsConsoleOpen()) {
                _GUI_DrawVerboseWoundview();
            }
        }

        private void RenderFloatingTooltip() {
            if (!_gui_tooltip) {
                return;
            }
            _gui_tooltip = false;

            int ogFontSize = GUI.skin.label.fontSize;
            bool ogRichText = GUI.skin.label.richText;
            bool ogWordWrap = GUI.skin.label.wordWrap;
            Color ogColor = GUI.color;

            try {
                GUI.skin.label.fontSize = (int)(ogFontSize * TOOLTIP_SCALE);
                GUI.skin.label.richText = true;

                Vector2 cornerPos = GetCursorPosInGUI() + Vector2.one * 10f;

                if (!string.IsNullOrWhiteSpace(_gui_tooltip_name)) {
                    GUI.skin.label.wordWrap = false;
                    GUIContent content = FixGuiContent.Content(_gui_tooltip_name, 2);
                    Vector2 size = GUI.skin.label.CalcSize(content);
                    Rect rect = new Rect(cornerPos.x, cornerPos.y, size.x, size.y);
                    Rect rectWider = new Rect(rect.x, rect.y, rect.width + size.y * 0.1f, rect.height);

                    GUI.color = Color.black;
                    GUI.DrawTexture(rectWider, WorldMP.whiteSquare.texture);
                    GUI.color = Color.white;
                    GUI.Label(rect, content);

                    cornerPos.y += size.y;
                }

                if (!string.IsNullOrWhiteSpace(_gui_tooltip_desc)) {
                    GUI.skin.label.wordWrap = true;
                    GUIContent content = FixGuiContent.Content(_gui_tooltip_desc, 3);
                    Vector2 size = GUI.skin.label.CalcSize(content);
                    Rect rect = new Rect(cornerPos.x, cornerPos.y, size.x, size.y);
                    Rect rectWider = new Rect(rect.x, rect.y, rect.width + size.y * 0.1f, rect.height);

                    GUI.color = Color.black;
                    GUI.DrawTexture(rectWider, WorldMP.whiteSquare.texture);
                    GUI.color = new Color(0.9f, 0.9f, 0.9f);
                    GUI.Label(rect, content);
                }
            } finally {
                GUI.skin.label.wordWrap = ogWordWrap;
                GUI.skin.label.fontSize = ogFontSize;
                GUI.skin.label.richText = ogRichText;
                GUI.color = ogColor;
            }
        }

        public static void _GUI_DrawVerboseWoundview() {
            if (!Util.IsInWoundView()) {
                return;
            }
            if (MinigameBase.main.currentMinigame != null && MinigameBase.main.currentMinigame is CPRMinigame) {
                return;
            }

            WoundView wv = WoundView.view;
            if (wv == null || wv.body == null) {
                return;
            }

            if (log.verbose) {
                int ogFont = GUI.skin.label.fontSize;
                GUI.skin.label.fontSize = 10;
                try {
                    for (int k = 0; k < wv.body.limbs.Length; k++) {
                        Limb limb = wv.body.limbs[k];
                        Image limbImage = wv.limbImages[k];
                        Vector3 pos = limbImage.rectTransform.position;
                        pos = new Vector2(pos.x, Screen.height - pos.y);

                        GUI.Label(new Rect(pos.x, pos.y, 200f, 20f), $"{limb.name} id:{k}");
                        GUI.Label(new Rect(pos.x, pos.y + 12f, 200f, 20f), $"totalForce: {Math.Round(limb.totalForce, 2)}");
                        GUI.Label(new Rect(pos.x, pos.y + 24f, 200f, 20f), $"shra: {limb.shrapnel}");
                        GUI.Label(new Rect(pos.x, pos.y + 36f, 200f, 20f), $"totalBleed: {limb.totalBleedAmount}");
                        GUI.Label(new Rect(pos.x, pos.y + 48f, 200f, 20f), $"inf: {limb.infectionAmount}");
                    }
                } finally {
                    GUI.skin.label.fontSize = ogFont;
                }
            }

            if (!Util.IsBodyLocal(wv.body)) {
                float maxKeepDistance = Mathf.Max(SharedMain.max_player_interaction_distance, GameplayRanges.PlayerInteractDistance) * 2f;
                if (!UIInGame.SpectatorModeActive && !KM.DistanceCheck(PlayerCamera.main.body, wv.body, maxKeepDistance)) {
                    PlayerCamera.main.ToggleWoundView();
                }
            }
        }
    }
}
