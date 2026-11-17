using BepInEx;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 联机主菜单与局内侧边状态栏顶层控制器：负责主菜单开关状态、侧边玩家/网络质量面板及主菜单分页容器渲染。
/// </summary>
public static partial class UIMainMenu {
    public static bool is_client => Net.IsClient;
    public static bool is_server => Net.IsServer;
    public static bool is_dedicated_server => Net.IsDedicatedServer;

    public static bool SINK_KEYBOARD_INPUT = true;

    public class MenuTab {
        public string name;
        public Action<Rect> gui_func;
        public Action on_open;

        public object data1;
        public object data2;
        public object data3;
        public object data4;
        public object data5;
    }

    public class SettingsTab : MenuTab {
        internal static List<SettingsTab> tabs = new List<SettingsTab>();

        public Type settings_origin;
        public Action on_reset;

        public SettingsTab() {
            tabs.Add(this);
        }
    }

    public static float PANEL_SIZE_X = 350f;
    public static float PANEL_SIZE_Y = 750f;
    public static float MENU_SIZE_X = 1500.0f;
    public static float MENU_SIZE_Y = 800.0f;

    public static List<string> AllowedMods = new List<string> {
        "RuntimeUnityEditor",
    };

    // 注意：静态分页实例必须在 ALL_MENU_TABS 与 current_tab 之前按顺序初始化，避免跨 partial 文件静态构造顺序不确定
    public static MenuTab menutab_directconnect = new MenuTab {
        name = "mmtb_directconnect",
        on_open = () => {
            _COLOR_EDITING = null;
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = false;
            RestoreLastRulePreset();
        },
        gui_func = _GUI_RenderDirectConnectMenu,
    };

    public static MenuTab menutab_settings = new MenuTab {
        name = "mmtb_settings",
        gui_func = _GUI_RenderSettingsMenu,
    };

    public static MenuTab menutab_about = new MenuTab {
        name = "mmtb_about",
        gui_func = _GUI_RenderAboutMenu,
    };

    public static List<MenuTab> ALL_MENU_TABS = new List<MenuTab> {
        UIServerBrowser.menutab_serverbrowser,
        menutab_directconnect,
        menutab_settings,
    };

    public static MenuTab current_tab = menutab_directconnect;

    private static Vector2 _sidePanelPlayersScroll = Vector2.zero;

    public static float GetMenuUIScale() {
        return UIBullshit.uiScale;
    }

    public static bool IsOpen() {
        if (UIBullshit.main == null) {
            return false;
        }
        return mainmenu_open;
    }

    public static bool IsInSettings() {
        return IsOpen() && current_tab == menutab_settings;
    }

    /// <summary>同步主菜单联机入口按钮的交互状态与开合文案。</summary>
    internal static void ApplyMpMenuButton(bool open) {
        if (UIManager.MP_MENU_BUTTON == null) {
            return;
        }

        TextMeshProUGUI label = UIManager.MP_MENU_BUTTON.GetComponentInChildren<TextMeshProUGUI>();
        Button button = UIManager.MP_MENU_BUTTON.GetComponent<Button>();
        if (button != null) {
            button.interactable = true;
        }
        if (label != null) {
            label.text = Lang.Get(open ? "mainmenu_mainbutton_close" : "mainmenu_mainbutton");
        }
    }

    public static bool mainmenu_open { get; private set; }

    public static void SetOpen(bool open_or_nah) {
        if (open_or_nah != mainmenu_open) {
            if (open_or_nah) {
                Application.runInBackground = true;
                RestoreLastRulePreset();
                if (UIManager.runSettingsMenu != null) {
                    UIManager.runSettingsMenu.SetActive(false);
                    if (Con.IsConsoleOpen()) {
                        Con.con.ToggleActiveState();
                    }
                }
                Util.PlayUISound(PlayerCamera.UISoundType.Click);
            } else {
                Util.PlayUISound(PlayerCamera.UISoundType.Close);
            }
            ApplyMpMenuButton(open_or_nah);
            OnMenuTabChange();
        }
        if (open_or_nah) {
            Util.OpenBrightnessPanel(false);
        }
        mainmenu_open = open_or_nah;
    }

    private static void _GUI__PanelInfo() {
        string mode = Net.IsServer
            ? (Net.IsDedicatedServer ? Lang.Get("netmode_server") : Lang.Get("netmode_host"))
            : Lang.Get("netmode_client");
        GUILayout.Label("   " + Lang.Get("netmode") + mode);

        if (!Net.IsConnecting) {
            if (Net.IsServer) {
                GUILayout.Label("   " + Lang.Get("netpanel_avgping") + Math.Round(ServerMain.AVERAGE_PING));
            } else if (ScavPlayer.LocalPlayer != null) {
                GUILayout.Label("   " + Lang.Get("netpanel_ping") + ClientMain.LOCAL_PING);
            } else {
                GUILayout.Label(" ");
            }
        } else {
            GUILayout.Label(" ");
        }
    }

    private static void _GUI__PlrListDrawPlr(ScavPlayer plr) {
        float maxTagSizeX = PANEL_SIZE_X * GetMenuUIScale() * 0.9f;
        float curXOffset = GetMenuUIScale() * 2f;
        GUI.skin.label.normal.textColor = Color.white;

        GUILayout.BeginHorizontal(GUILayout.Width(maxTagSizeX));
        try {
            GUI.skin.label.wordWrap = false;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            string txt = $"{plr.playerId}: {plr.playerName}";
            GUIContent txtContent = FixGuiContent.Content(txt, 11);
            Vector2 textSize = GUI.skin.label.CalcSize(txtContent);

            foreach (Sprite item in plr.KnownUserTagIcons) {
                GUILayout.Label(item.texture, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
                curXOffset += textSize.y;
            }

            Texture2D profilePic = plr.IconAnySmallToLarge;
            if (profilePic != null) {
                GUILayout.Label(profilePic, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
                if (plr.body != null && !plr.body.alive && Multiplayer.rules.TrackPlayerDeaths) {
                    Rect crossRect = GUILayoutUtility.GetLastRect().ShrinkBorder(-1f * GetMenuUIScale());
                    GUI.DrawTexture(crossRect, CoopModAssets.cross.texture, ScaleMode.StretchToFill, true, 1f, Color.red, 0f, 0f);
                }
            }

            if (Voicechat.VCRULE_enabled && plr.vc_output != null && CoopModAssets.voicechaticon != null) {
                Color vcColor = plr.vc_output.CalculateVoiceChatIconColor(out float realCurVcVol);
                if (realCurVcVol > 0f || (plr.IsLocal && Voicechat.IS_RECORDING)) {
                    GUI.color = vcColor;
                    GUILayout.Label(CoopModAssets.voicechaticon.texture, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
                    GUI.color = Color.white;
                } else {
                    GUILayout.Space(textSize.y);
                }
                curXOffset += textSize.y;
            }

            GUI.skin.label.normal.textColor = plr.playerColor;

            string pingText = "ping: " + plr.ping_as_ms;
            Vector2 pingTextSize = GUI.skin.label.CalcSize(FixGuiContent.Content(" " + pingText, 12));

            GUILayout.Label(txt, GUILayout.MaxWidth(maxTagSizeX * 0.97f - (curXOffset + pingTextSize.x)));
            GUILayout.FlexibleSpace();
            GUI.skin.label.alignment = TextAnchor.UpperRight;
            GUILayout.Label(pingText);
        } finally {
            GUILayout.EndHorizontal();
        }
    }

    public static void _GUI_DrawSideMenuWithPlayerList() {
        if (!Net.IsRunning || !UIBullshit.IsAnyMenuOpen()) {
            return;
        }
        bool inGame = Util.IsInWorld();

        TextAnchor ogAlign = GUI.skin.label.alignment;
        int ogFontSize = GUI.skin.label.fontSize;
        bool ogStretchHeight = GUI.skin.label.stretchHeight;
        bool ogWordWrap = GUI.skin.label.wordWrap;
        RectOffset ogMargin = GUI.skin.label.margin;

        Rect mainRect = UIBullshit.ScaleRect(0f, 1080f * 0.5f - PANEL_SIZE_Y * 0.5f, PANEL_SIZE_X, PANEL_SIZE_Y, inGame);
        UIBullshit._GUI_9SlicePanel(mainRect, inGame ? 1f : 0.7f, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano);

        Rect contentRect = mainRect.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);
        GUILayout.BeginArea(contentRect);
        try {
            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            GUI.skin.label.wordWrap = false;
            GUI.skin.label.margin = new RectOffset(0, 0, 0, 0);
            GUI.skin.label.fontSize = (int)(ogFontSize * 1.3f);

            GUILayout.Label($"{Lang.Get("netpanel_title")} v{Plugin.FULL_MOD_VERSION}", GUILayout.Height(GUI.skin.label.fontSize * 1.75f));

            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            _GUI__PanelInfo();

            bool buttonsUsable = Util.IsInWorld() && !IsOpen();
            if (buttonsUsable) {
                if (GUILayout.Button(Lang.Get("netpanel_settings"))) {
                    if (mainmenu_open && Util.IsInWorld()) {
                        Util.OpenBrightnessPanel(true);
                        SetOpen(false);
                    } else {
                        SetOpen(!mainmenu_open);
                        if (mainmenu_open) {
                            current_tab = menutab_settings;
                        }
                    }
                }
            } else {
                GUILayout.Label(" ");
            }

            if (Net.IsConnected) {
                GUILayout.BeginVertical();
                GUI.skin.label.fontSize = (int)(ogFontSize * 0.9f);
                try {
                    GUILayout.BeginHorizontal();
                    try {
                        string connectedPlrs = Lang.Get("netpanel_connectedplrs") + ScavPlayer.ClientIdToPlayerDict.Count + "/" + Multiplayer.rules.PLAYER_COUNT_LIMIT;
                        if (buttonsUsable) {
                            if (GUILayout.Button(connectedPlrs)) {
                                SetOpen(true);
                                current_tab = menutab_directconnect;
                                _CONNECTIONSECONDPANEL_TOOLBAR = 0;
                            }
                        } else {
                            GUILayout.Label(connectedPlrs);
                        }
                    } finally {
                        GUILayout.EndHorizontal();
                    }

                    _sidePanelPlayersScroll = GUILayout.BeginScrollView(_sidePanelPlayersScroll, GUILayout.ExpandHeight(true));
                    try {
                        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
                            _GUI__PlrListDrawPlr(plr);
                        }
                    } finally {
                        GUI.skin.label.alignment = ogAlign;
                        GUILayout.EndScrollView();
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI_DrawSideMenuWithPlayerList.PlayerList", ex);
                } finally {
                    GUI.skin.label.normal.textColor = Color.white;
                    GUI.skin.label.fontSize = ogFontSize;
                    GUILayout.EndVertical();
                }
            }

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.FlexibleSpace();

            if (Time.realtimeSinceStartupAsDouble - Multiplayer.lastMultiplayerStatusMessageChangeTime < 10.0) {
                GUI.skin.label.wordWrap = true;
                GUI.skin.label.stretchHeight = true;
                GUI.skin.label.fontSize = (int)(ogFontSize * 0.75f);

                GUILayout.Label(Lang.Get("netpanel_laststatus") + "\n" + Multiplayer.multiplayerStatusMessage);

                GUI.skin.label.wordWrap = ogWordWrap;
                GUI.skin.label.stretchHeight = ogStretchHeight;
            }

            GUILayout.FlexibleSpace();

            try {
                float half = contentRect.width * 0.7f;
                GUI.skin.label.fontSize = ogFontSize;
                GUI.skin.label.wordWrap = false;
                GUI.skin.label.alignment = TextAnchor.UpperLeft;

                GUILayout.BeginHorizontal();
                GUILayout.Label($"PACKET STABILITY: {(Net.IsClient ? ClientMain.MY_CONNECTION_QUALITY.ToString() : ServerMain.AVG_CONNECTION_QUALITY.ToString())}%");
                if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                    UIBullshit._GUI_SetTooltip("", UIBullshit.net_debug_msg);
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal();
                GUILayout.Label("SERVER TPS: " + ClientMain.SERVER_TPS, GUILayout.Width(half));
                GUILayout.Label("FPS: " + ClientMain.SERVER_FPS);
                GUILayout.EndHorizontal();

                if (Net.IsClient) {
                    GUILayout.BeginHorizontal();
                    GUILayout.Label("LOCAL TPS: " + ServerMain.CURRENT_TPS, GUILayout.Width(half));
                    GUILayout.Label("FPS: " + ServerMain.CURRENT_FPS);
                    GUILayout.EndHorizontal();
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI_DrawSideMenuWithPlayerList.Footer", ex);
            }
        } finally {
            GUILayout.EndArea();
            GUI.skin.label.alignment = ogAlign;
            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.stretchHeight = ogStretchHeight;
            GUI.skin.label.wordWrap = ogWordWrap;
            GUI.skin.label.margin = ogMargin;
        }
    }

    /// <summary>
    /// 渲染联机主菜单顶层窗口。注意：<see cref="ImguiScan"/> 按方法名 <c>_GUI_DrawMPMainMenu</c> 识别此入口，严禁重命名。
    /// </summary>
    internal static void _GUI_DrawMPMainMenu() {
        if (!mainmenu_open) {
            return;
        }
        if (Util.IsInWorld() && PlayerCamera.main != null) {
            PlayerCamera.main.radialOpen = false;
            if (PlayerCamera.main.tradeMenu.activeSelf) {
                PlayerCamera.main.ToggleTradeMenu();
            }
            if (Util.IsInWoundView()) {
                PlayerCamera.main.ToggleWoundView();
            }
            PlayerCamera.main.craftingPanel.SetActive(false);
        }

        Vector2 scaled = new Vector2(MENU_SIZE_X, MENU_SIZE_Y) * GetMenuUIScale();
        if (scaled.x > Screen.width) {
            scaled *= Screen.width / scaled.x;
        }
        Rect mainPanelRect = new Rect(0f, 0f, scaled.x, scaled.y) {
            center = UIBullshit.GetHalfScreenSize()
        };
        UIBullshit._GUI_9SlicePanel(mainPanelRect, 1f, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano);
        Rect mainPanelContentRect = mainPanelRect.ShrinkBorder(UIBullshit.uiBlockSmallBorderSize);

        // 顶部标题栏与关闭按钮
        float topBarSizeY = GetMenuUIScale() * 56f;
        Rect topBar = new Rect(mainPanelRect) {
            yMin = mainPanelRect.yMin - topBarSizeY,
            yMax = mainPanelRect.yMin
        };
        UIBullshit._GUI_9SlicePanel(topBar, 1f, 0.6980392f, true, UIBullshit.unscaled_uiBlockNano);
        UIBullshit.CheckCursorOverlap(topBar);

        GUIStyle headerLabelStyle = FixGuiStyle.Copy(GUI.skin.label, 7);
        headerLabelStyle.alignment = TextAnchor.MiddleCenter;
        headerLabelStyle.fontSize = (int)(GUI.skin.label.fontSize * 1.5f);
        GUI.Label(topBar, Lang.Get("mainmenu_settings"), headerLabelStyle);

        GUIStyle closeButtonStyle = FixGuiStyle.Copy(GUI.skin.button, 8);
        Rect exitButtonRect = topBar.ShrinkBorder(UIBullshit.uiBlockSmallBorderSize);
        exitButtonRect.xMin = exitButtonRect.xMax - exitButtonRect.height;
        UIBullshit._GUI_SetButtonSkinTexture(closeButtonStyle, false);
        if (GUI.Button(exitButtonRect, "X", closeButtonStyle)) {
            SetOpen(false);
        }

        // 旧版模组残留与 Playtest 门禁检测
        List<string> metaNames = Multiplayer.GetBepinPlugins().Select(x => x.Metadata.Name).ToList();
        if (metaNames.Contains("Krokosha_MP_CU")) {
            RenderModConflictWarning(mainPanelContentRect, new[] {
                "Please delete old KrokMP folder.",
                "MP mod has been renamed after this update",
                "Go to CasualtiesUnknown/BepInEx/plugins/"
            });
            return;
        }

#if PLAYTEST
        metaNames.RemoveAll(x => !AllowedMods.Contains(x));
        if (metaNames.Count > 0) {
            RenderModConflictWarning(mainPanelContentRect, new[] {
                "You're playing a playtest build.",
                "Please delete all your other mods and restart",
                "(Or if you're a modder add Metadata.Name to Together.UIMainMenu.AllowedMods)",
                "Go to CasualtiesUnknown/BepInEx/plugins/"
            });
            return;
        }
#endif

        // 底部分页切换栏
        float tabsY = GetMenuUIScale() * 70f;
        Rect tabsPanel = mainPanelContentRect.ShrinkBorder(-1f * GetMenuUIScale());
        tabsPanel.yMin += mainPanelContentRect.height - tabsY;
        UIBullshit._GUI_9SlicePanel(tabsPanel, 0.5f, 0.6f, true, UIBullshit.unscaled_uiBlockNano);
        mainPanelContentRect.yMax = tabsPanel.yMin - UIBullshit.uiBlockNanoBorderSize;

        Rect horizontal = tabsPanel.ShrinkBorder(GetMenuUIScale() * 8f);
        GUILayout.BeginArea(horizontal);
        GUILayout.BeginHorizontal();
        try {
            float btnWidth = horizontal.width * (1f / (ALL_MENU_TABS.Count + 1.5f));
            int ogFontSize = GUI.skin.label.fontSize;
            GUI.skin.label.fontSize = (int)(ogFontSize * 1.1f);
            try {
                for (int i = 0; i < ALL_MENU_TABS.Count; i++) {
                    MenuTab tab = ALL_MENU_TABS[i];
                    GUI.backgroundColor = current_tab == tab ? Color.white : Color.white * 0.8f;
                    if (GUILayout.Button(Lang.Get(tab.name), GUILayout.MaxWidth(btnWidth), GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(true))) {
                        current_tab = tab;
                        current_tab.on_open?.Invoke();
                        OnMenuTabChange();
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }
                }

                GUILayout.FlexibleSpace();
                GUI.backgroundColor = current_tab == menutab_about ? Color.white : Color.white * 0.8f;
                if (GUILayout.Button(Lang.Get(menutab_about.name), GUILayout.MaxWidth(btnWidth), GUILayout.ExpandWidth(false), GUILayout.ExpandHeight(true))) {
                    current_tab = menutab_about;
                    current_tab.on_open?.Invoke();
                    OnMenuTabChange();
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                }
            } finally {
                GUI.skin.label.fontSize = ogFontSize;
                GUI.backgroundColor = Color.white;
            }
        } finally {
            GUILayout.EndHorizontal();
            GUILayout.EndArea();
        }

        // 当前活动分页内容区
        GUILayout.BeginArea(mainPanelContentRect);
        try {
            if (current_tab != null && current_tab.gui_func != null) {
                try {
                    current_tab.gui_func(mainPanelContentRect);
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, $"UIMainMenu.RenderTab({current_tab.name})", ex);
                }
            } else {
                int ogFontSize = GUI.skin.label.fontSize;
                GUI.skin.label.fontSize = ogFontSize * 2;
                GUILayout.Label("No tab selected.", GUILayout.ExpandHeight(true), GUILayout.ExpandWidth(true));
                GUI.skin.label.fontSize = ogFontSize;
            }
        } finally {
            GUILayout.EndArea();
        }

        if (!string.IsNullOrEmpty(_COLOR_ERRORS)) {
            _COLOR_ERRORS_RECT = GUIUtility.ScreenToGUIRect(_COLOR_ERRORS_RECT);
            GUI.color = Color.red;
            GUI.Label(_COLOR_ERRORS_RECT, _COLOR_ERRORS);
            GUI.color = Color.white;
            _COLOR_ERRORS = null;
        }
    }

    private static void RenderModConflictWarning(Rect area, string[] lines) {
        GUILayout.BeginArea(area);
        bool ogWordWrap = GUI.skin.label.wordWrap;
        int ogFontSize = GUI.skin.label.fontSize;
        try {
            GUI.skin.label.fontSize = (int)(ogFontSize * 1.5f);
            GUI.skin.label.wordWrap = true;

            Vector2 lineSize = GUI.skin.label.CalcSize(FixGuiContent.Content(lines[0], 9));
            for (int i = 0; i < lines.Length; i++) {
                GUILayout.Label(lines[i], GUILayout.Height(lineSize.y), GUILayout.ExpandWidth(true));
            }

            GUILayout.BeginHorizontal();
            if (GUILayout.Button("Take me there and close the game")) {
                PreRunScript.OpenInFileBrowser(Paths.PluginPath);
                Util.DelayCallLambda(1f, () => Application.Quit());
            }
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
            GUILayout.FlexibleSpace();
        } finally {
            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.wordWrap = ogWordWrap;
            GUILayout.EndArea();
        }
    }
}
