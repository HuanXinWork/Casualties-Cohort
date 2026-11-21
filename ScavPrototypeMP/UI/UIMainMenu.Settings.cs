using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using TMPro;
using Together.Api;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 联机主菜单设置中心（含外部模组 <see cref="TogetherPanelApi.RegisteredSettingsTabs"/> 动态扩展页签、一键导出诊断报告按钮）与关于致谢面板。
/// </summary>
public static partial class UIMainMenu {
    // 注意：内置 SettingsTab 静态实例必须在 cur_settings_tab 与 ALL_SETTINGS 之前按顺序初始化
    internal static SettingsTab _general_setting_tab = new SettingsTab {
        name = "mmst_general",
        gui_func = _GUI___SettingsGeneral,
        on_open = () => { },
        on_reset = () => { },
        settings_origin = typeof(Amogus)
    };

    internal static SettingsTab _gui_setting_tab = new SettingsTab {
        name = "mmst_gui",
        gui_func = _GUI___SettingsGUI,
        settings_origin = typeof(UIBullshit)
    };

    internal static SettingsTab _language_setting_tab = new SettingsTab {
        name = "mmst_language",
        gui_func = _GUI___SettingsLanguage,
        on_reset = () => {
            SetSettingsLanguage("");
            Lang.SaveSelectedLocale();
            languageSettingsDirty = !Lang.IsSelectedLocaleSaved;
        },
    };

    internal static SettingsTab _voip_setting_tab = new SettingsTab {
        name = "mmst_voip",
        gui_func = _GUI___SettingsVOIP,
        on_open = () => { _VOIP_SETTINGS_DROPDOWNTEST = false; },
        on_reset = () => { Voicechat.SetMicrophone(Voicechat.NOMIC); },
        settings_origin = typeof(Voicechat)
    };

    internal static SettingsTab _chat_setting_tab = new SettingsTab {
        name = "mmst_chat",
        gui_func = _GUI___SettingsChat,
        settings_origin = typeof(Chat)
    };

    internal static SettingsTab _debug_setting_tab = new SettingsTab {
        name = "mmst_debug",
        gui_func = _GUI___SettingsDebug,
        settings_origin = typeof(DebugMenuSettings)
    };

    internal static SettingsTab _bugreport_setting_tab = null;

    public static SettingsTab cur_settings_tab = _general_setting_tab;

    public static List<SettingsTab> ALL_SETTINGS = new List<SettingsTab> {
        _general_setting_tab,
        _chat_setting_tab,
        _voip_setting_tab,
        _gui_setting_tab,
        _language_setting_tab,
    };

    private static bool languageSettingsDirty = true;

    private static void SetSettingsLanguage(string locale) {
        Lang.SetSelectedLocale(locale);
        languageSettingsDirty = !Lang.IsSelectedLocaleSaved;
        GUILayout_DropdownMenu.CloseAll();

        // IMGUI 文本在下一帧重绘时自动更新，主菜单的 TMP 入口按钮需显式刷新
        if (UIManager.MP_MENU_BUTTON != null) {
            TextMeshProUGUI text = UIManager.MP_MENU_BUTTON.GetComponentInChildren<TextMeshProUGUI>();
            if (text != null) {
                text.text = Lang.Get(IsOpen() ? "mainmenu_mainbutton_close" : "mainmenu_mainbutton");
            }
        }
    }

    private static void _GUI___SettingsLanguage(Rect r) {
        string[] locales = Lang.AvailableLocales;
        string[] choices = new string[locales.Length + 1];
        choices[0] = Lang.Get("mmst_language_follow_game");
        for (int i = 0; i < locales.Length; i++) {
            choices[i + 1] = Lang.GetLocaleDisplayName(locales[i]);
        }

        int selected = Array.IndexOf(locales, Lang.SelectedLocale) + 1;
        float labelWidth = r.width * SettingLabelWidthRatio;
        float fieldSpacing = Mathf.Max(GUI.skin.label.margin.right, GUI.skin.button.margin.left);
        float dropdownWidth = Mathf.Max(1f, r.width - labelWidth - fieldSpacing);
        float contentWidth = Mathf.Max(1f, r.width - 8f * GetMenuUIScale());

        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        GUILayout.BeginHorizontal(GUILayout.Width(r.width), GUILayout.ExpandWidth(false));
        {
            GUILayout.Label(Lang.Get("mmst_language"), GUILayout.Width(labelWidth));
            int choice = GUILayout_DropdownMenu.Dropdown(selected, choices, GUILayout.Width(dropdownWidth), GUILayout.ExpandWidth(false));
            if (choice != selected) {
                SetSettingsLanguage(choice == 0 ? "" : locales[choice - 1]);
            }
        }
        GUILayout.EndHorizontal();

        bool wordWrap = GUI.skin.label.wordWrap;
        TextAnchor alignment = GUI.skin.label.alignment;
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.skin.label.wordWrap = true;
        GUILayout.Space(10f * GetMenuUIScale());

        // IMGUI 自动换行标签必须显式指定高度，否则多行中文说明会被裁切
        GUIStyle descriptionStyle = new GUIStyle(GUI.skin.label) {
            wordWrap = true,
            alignment = TextAnchor.UpperLeft,
        };
        GUIContent descriptionContent = FixGuiContent.Content(Lang.Get("mmst_language_desc"), 7001);
        float descriptionHeight = Mathf.Max(1f, descriptionStyle.CalcHeight(descriptionContent, contentWidth));
        GUILayout.Label(descriptionContent, descriptionStyle, GUILayout.Width(contentWidth), GUILayout.Height(descriptionHeight));

        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        GUILayout.Label(
            Lang.Get(languageSettingsDirty ? "mmst_language_unsaved" : "mmst_language_saved"),
            GUILayout.Width(contentWidth),
            GUILayout.ExpandWidth(false));
        GUI.skin.label.wordWrap = wordWrap;
        GUI.skin.label.alignment = alignment;
    }

    /// <summary>当前激活的外部模组设置分页（为 null 时表示当前展示内置 <see cref="cur_settings_tab"/>）。</summary>
    private static TogetherPanelApi.ExternalSettingsTab _activeExternalSettingsTab = null;
    private static SettingsTab _lastObservedBuiltinTab = _general_setting_tab;

    private static readonly float MicVolumePreviewPixHeight = 20f;
    private static readonly float SettingLabelWidthRatio = 0.38f;
    internal static int last_writeX = 0;
    internal static bool writeX_flipflop = false;
    private static bool _VOIP_SETTINGS_DROPDOWNTEST = false;

    public static Action DrawUpdateButton = () => { };
    public static Action BuildUpdateButton = () => { };

    internal static Dictionary<Type, List<FieldInfo>> _known_settings = new();

    private static Vector2 _SETTINGSMENUSCROLL = Vector2.zero;
    private static string _lastDiagnosticReportNotice = "";

    private static string SafeGetExternalTabTitle(TogetherPanelApi.ExternalSettingsTab tab) {
        if (tab == null) {
            return string.Empty;
        }
        try {
            return tab.Title ?? tab.Id ?? string.Empty;
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Interop, $"ExternalSettingsTab.Title({tab.Id})", ex);
            return tab.Id ?? string.Empty;
        }
    }

    private static string SafeGetExternalTabStatus(TogetherPanelApi.ExternalSettingsTab tab) {
        if (tab == null) {
            return string.Empty;
        }
        try {
            return tab.Status ?? string.Empty;
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Interop, $"ExternalSettingsTab.Status({tab.Id})", ex);
            return string.Empty;
        }
    }

    private static void _GUI_RenderSettingsMenu(Rect r) {
        // 若外部代码直接给 cur_settings_tab 赋了新值，自动退出外部模组分页切回对应内置分页
        if (cur_settings_tab != _lastObservedBuiltinTab) {
            _activeExternalSettingsTab = null;
            _lastObservedBuiltinTab = cur_settings_tab;
        }

        IReadOnlyList<TogetherPanelApi.ExternalSettingsTab> extTabs = TogetherPanelApi.RegisteredSettingsTabs;
        if (_activeExternalSettingsTab != null) {
            bool stillRegistered = false;
            if (extTabs != null) {
                for (int i = 0; i < extTabs.Count; i++) {
                    if (ReferenceEquals(extTabs[i], _activeExternalSettingsTab)) {
                        stillRegistered = true;
                        break;
                    }
                }
            }
            if (!stillRegistered) {
                _activeExternalSettingsTab = null;
                cur_settings_tab ??= _general_setting_tab;
                _lastObservedBuiltinTab = cur_settings_tab;
            }
        }

        Rect choiceRect = new Rect(r) {
            xMax = Mathf.LerpUnclamped(r.xMax, r.xMin, 0.7f) - 50f * GetMenuUIScale(),
            x = 0f,
            y = 0f
        };
        UIBullshit._GUI_9SlicePanel(choiceRect, 1f, 0.5f, false, UIBullshit.unscaled_uiBlockNano);

        GUILayout.BeginHorizontal();
        {
            float submenuBtnWidth = choiceRect.width - UIBullshit.uiBlockNanoBorderSize * 2f;
            GUILayout.BeginVertical(GUILayout.Width(choiceRect.width));
            {
                GUILayout.Space(UIBullshit.uiBlockNanoBorderSize);

                void RenderBuiltinTabButton(SettingsTab tab) {
                    bool isSelected = _activeExternalSettingsTab == null && cur_settings_tab == tab;
                    GUI.backgroundColor = isSelected ? Color.white : Color.white * 0.8f;

                    if (GUILayout.Button(Lang.Get(tab.name), GUILayout.Width(submenuBtnWidth))) {
                        _activeExternalSettingsTab = null;
                        cur_settings_tab = tab;
                        _lastObservedBuiltinTab = tab;
                        cur_settings_tab.on_open?.Invoke();
                        OnMenuTabChange();
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }
                }

                foreach (SettingsTab tab in ALL_SETTINGS) {
                    RenderBuiltinTabButton(tab);
                }
#if RELEASE && !PLAYTEST
                if ((Net.IsRunning ? Con.CanCheat() : Multiplayer.rules.sv_cheats) || log.verbose)
#endif
                if (_debug_setting_tab != null) {
                    RenderBuiltinTabButton(_debug_setting_tab);
                }
                if (_bugreport_setting_tab != null) {
                    RenderBuiltinTabButton(_bugreport_setting_tab);
                }

                // 动态渲染外部模组通过 TogetherPanelApi 注册的扩展配置页签
                if (extTabs != null && extTabs.Count > 0) {
                    GUILayout.Space(6f * GetMenuUIScale());
                    for (int i = 0; i < extTabs.Count; i++) {
                        TogetherPanelApi.ExternalSettingsTab extTab = extTabs[i];
                        if (extTab == null) {
                            continue;
                        }
                        bool isSelected = ReferenceEquals(_activeExternalSettingsTab, extTab);
                        GUI.backgroundColor = isSelected ? Color.white : Color.white * 0.8f;

                        string extTitle = SafeGetExternalTabTitle(extTab);
                        if (GUILayout.Button(extTitle, GUILayout.Width(submenuBtnWidth))) {
                            _activeExternalSettingsTab = extTab;
                            OnMenuTabChange();
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    }
                }

                GUI.backgroundColor = Color.white;
            }
            GUILayout.EndVertical();

            Rect r2 = new Rect(r) {
                width = r.width - choiceRect.width - UIBullshit.uiBlockSmallBorderSize * 4.05f
            };

            _SETTINGSMENUSCROLL = GUILayout.BeginScrollView(_SETTINGSMENUSCROLL);
            GUILayout.BeginVertical(GUILayout.MaxWidth(r2.width), GUILayout.ExpandWidth(false), GUILayout.Width(r2.width));
            {
                int ogFontSize = GUI.skin.label.fontSize;
                TextAnchor ogAlign = GUI.skin.label.alignment;
                bool ogWordWrap = GUI.skin.label.wordWrap;
                GUI.skin.label.wordWrap = false;

                try {
                    if (_activeExternalSettingsTab != null) {
                        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
                        try {
                            GUI.skin.label.alignment = TextAnchor.UpperCenter;
                            UIBullshit._GUI_BiggerLabel(SafeGetExternalTabTitle(_activeExternalSettingsTab), 1.4f);

                            string statusText = SafeGetExternalTabStatus(_activeExternalSettingsTab);
                            if (!string.IsNullOrWhiteSpace(statusText)) {
                                GUI.skin.label.wordWrap = true;
                                GUILayout.Label(statusText);
                                GUI.skin.label.wordWrap = false;
                                GUILayout.Space(6f * GetMenuUIScale());
                            }

                            GUI.skin.label.alignment = TextAnchor.UpperLeft;
                            _activeExternalSettingsTab.DrawImGui?.Invoke();
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.Interop, $"ExternalSettingsTab.DrawImGui({_activeExternalSettingsTab.Id})", ex);
                        } finally {
                            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                        }
                    } else if (cur_settings_tab != null) {
                        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
                        try {
                            GUI.skin.label.alignment = TextAnchor.UpperCenter;
                            UIBullshit._GUI_BiggerLabel(Lang.Get(cur_settings_tab.name), 1.4f);
                            GUI.skin.label.alignment = TextAnchor.UpperLeft;

                            if (cur_settings_tab.settings_origin != null) {
                                __GUI____RenderFieldsWithThatAttribute(GetSettingsFromStaticClass(cur_settings_tab.settings_origin), r2);
                            }
                            cur_settings_tab.gui_func?.Invoke(r2);
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.UI, $"UIMainMenu.RenderSettingsTab({cur_settings_tab.name})", ex);
                        } finally {
                            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                        }
                    } else {
                        GUILayout.Label("Empty.");
                    }
                } finally {
                    GUI.skin.label.fontSize = ogFontSize;
                    GUI.skin.label.alignment = ogAlign;
                    GUI.skin.label.wordWrap = ogWordWrap;
                }
            }
            GUILayout.EndVertical();
            GUILayout.EndScrollView();
        }
        GUILayout.EndHorizontal();

        GUILayout.Space(10f * GetMenuUIScale());

        GUILayout.BeginHorizontal();
        {
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.Get("mmst_resettodefault"))) {
                if (_activeExternalSettingsTab != null) {
                    try {
                        _activeExternalSettingsTab.OnReset?.Invoke();
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.Interop, $"ExternalSettingsTab.OnReset({_activeExternalSettingsTab.Id})", ex);
                    }
                } else {
                    if (cur_settings_tab?.settings_origin != null) {
                        foreach (FieldInfo item in GetSettingsFromStaticClass(cur_settings_tab.settings_origin)) {
                            SettingDeclarerThingyBaseAttribute attr = item.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                            attr.Save(item, attr.default_value);
                        }
                    }
                    cur_settings_tab?.on_reset?.Invoke();
                }
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            DoTooltipOnLastGUIRect("mmst_resettodefault_desc");

            if (GUILayout.Button(Lang.Get("mmst_apply"))) {
                Lang.SaveSelectedLocale();
                languageSettingsDirty = !Lang.IsSelectedLocaleSaved;
                SettingsJsonThing.json.Save();
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
        }
        GUILayout.EndHorizontal();
    }

    public static void OnMenuTabChange() {
        GUILayout_DropdownMenu.CloseAll();
        Voicechat.ENABLE_LOCAL_LOOPBACK = false;
        ResetSettingsVOIPVisualiser();
    }

    public static void ResetSettingsVOIPVisualiser() {
        if (UIBullshit.main != null && UIBullshit.main.egg != null) {
            UnityEngine.Object.Destroy(UIBullshit.main.egg.gameObject);
            UIBullshit.main.egg = null;
        }
    }

    private static void _GUI___SettingsVOIP(Rect r) {
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        float halfWidth = r.width * SettingLabelWidthRatio;

        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        GUILayout.BeginHorizontal();
        {
            GUILayout.Label(Lang.Get("mmst_mike"), GUILayout.Width(halfWidth));
            if (_VOIP_SETTINGS_DROPDOWNTEST) {
                GUILayout.Label(Lang.Get("mmst_select_mike"), GUILayout.Width(halfWidth));
                if (GUILayout.Button(Lang.Get("mmst_mike_osdefault"))) {
                    _VOIP_SETTINGS_DROPDOWNTEST = false;
                    Voicechat.SetMicrophone(Voicechat.NOMIC);
                }
            } else if (GUILayout.Button(Voicechat.GetMic() ?? Lang.Get("mmst_mike_osdefault"))) {
                _VOIP_SETTINGS_DROPDOWNTEST = true;
            }
        }
        GUILayout.EndHorizontal();

        try {
            if (_VOIP_SETTINGS_DROPDOWNTEST) {
                for (int i = 0; i < Microphone.devices.Length; i++) {
                    string mic = Microphone.devices[i];
                    GUILayout.BeginHorizontal();
                    GUILayout.Space(halfWidth);
                    if (GUILayout.Button(mic)) {
                        _VOIP_SETTINGS_DROPDOWNTEST = false;
                        Voicechat.SetMicrophone(mic);
                    }
                    GUILayout.EndHorizontal();
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI___SettingsVOIP.DeviceDropdown", ex);
        }

        if (Voicechat.VoiceChatIsEnabledLocallyOnly()) {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
            GUILayout.BeginHorizontal();
            try {
                if (GUILayout.Button(Voicechat.ENABLE_LOCAL_LOOPBACK ? Lang.Get("mmst_mike_testing") : Lang.Get("mmst_mike_test"))) {
                    Voicechat.ENABLE_LOCAL_LOOPBACK = !Voicechat.ENABLE_LOCAL_LOOPBACK;
                }

                Rect rect = GUILayoutUtility.GetRect(FixGuiContent.Content("VOIP_PREVIEW", 22), GUI.skin.button, GUILayout.ExpandWidth(true));
                if (Event.current.type == EventType.Repaint && rect.height > 1f) {
                    Vector2 scaledRectSizeFloat = rect.size / UIBullshit.uiScale;
                    Vector2Int scaledRectSize = new Vector2Int(
                        Mathf.Max(1, (int)((scaledRectSizeFloat.x / scaledRectSizeFloat.y) * MicVolumePreviewPixHeight)),
                        (int)MicVolumePreviewPixHeight);

                    if (UIBullshit.main.egg == null || UIBullshit.main.egg.textureSize != scaledRectSize) {
                        ResetSettingsVOIPVisualiser();

                        GameObject go = new GameObject("VoiceVisualizer");
                        go.AddComponent<RawImage>().enabled = false;
                        UIBullshit.main.egg = go.AddComponent<ECGVisualizer>();
                        UIBullshit.main.egg.textureSize = scaledRectSize;
                        UIBullshit.main.egg.followBody = false;
                    }
                }
                if (UIBullshit.main.egg != null && UIBullshit.main.egg.tex != null) {
                    if (UIBullshit.main.egg.writeX != last_writeX) {
                        last_writeX = UIBullshit.main.egg.writeX;
                        UIBullshit.main.egg.writeHeight = 0f;
                        writeX_flipflop = !writeX_flipflop;
                    }
                    GUI.DrawTexture(rect, UIBullshit.main.egg.tex);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI___SettingsVOIP.LoopbackVisualizer", ex);
            } finally {
                GUILayout.EndHorizontal();
            }
        } else {
            GUILayout.Label(Lang.Get("mmst_mike_local_off"));
        }

        if (Voicechat.IsMicModePushToTalk() || Voicechat.IsMicModeToggleToTalk()) {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            if (GUILayout.Button(Lang.Get("mmst_vcbutton") + KeyBinds.GetBindName(CoopKeybinds.voicechat))) {
                SetOpen(false);
                Util.OpenSettingsPanel(true, Setting.SettingCategory.Input);
            }
            DoTooltipOnLastGUIRect("mmst_gotokeybind");
            GUILayout.EndHorizontal();
        }
        if (!Multiplayer.rules.VoicechatEnabled) {
            GUILayout.Label(Lang.Get("mmst_voip_rule_disabled") + "    ");
        }
    }

    private static void _GUI___SettingsGUI(Rect r) {
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
        GUI.skin.label.alignment = TextAnchor.UpperRight;
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmst_forcereloaduitex"))) {
            UIBullshit.ResizeGUITextures();
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        GUILayout.EndHorizontal();
    }

    private static void _GUI___SettingsDebug(Rect r) {
        foreach (KeyValuePair<knetid, SyncInfo> item in NetObjectRegistry.NetIdToSyncInfoDict) {
            GUILayout.BeginHorizontal();
            GUILayout.Label(item.Value.ToString());
            GUILayout.FlexibleSpace();
            if (Con.CanCheat() && item.Value.go != null) {
                if (GUILayout.Button("Destroy")) {
                    UnityEngine.Object.Destroy(item.Value.go);
                }
                if (GUILayout.Button($"TP: {item.Value.transformPosition}")) {
                    PlayerCamera.main.body.transform.position = item.Value.transformPosition;
                }
            }
            GUILayout.EndHorizontal();
        }
    }

    private static void _GUI___SettingsChat(Rect r) {
        Chat.ShowChat();
        GUILayout.BeginHorizontal();
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmst_chatbutton") + KeyBinds.GetBindName(CoopKeybinds.chat))) {
            SetOpen(false);
            Util.OpenSettingsPanel(true, Setting.SettingCategory.Input);
        }
        DoTooltipOnLastGUIRect("mmst_gotokeybind");
        GUILayout.EndHorizontal();
    }

    private static void _GUI___SettingsGeneral(Rect r) {
        bool ogEnabled = GUI.enabled;
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
        GUILayout.Space(20f * GetMenuUIScale());

        if (DrawUpdateButton != null) {
            try {
                DrawUpdateButton();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIMainMenu.DrawUpdateButton", ex);
            }
        }

        GUILayout.Space(16f * GetMenuUIScale());

        // 一键生成并导出脱敏诊断报告（供玩家排障反馈）
        GUILayout.BeginHorizontal();
        {
            if (GUILayout.Button("导出诊断报告 (Export Diagnostic Report)")) {
                MpDiagnosticReport report = MpDiagnosticReporter.GenerateReport("SettingsMenuButton", domain: LogDomain.UI);
                string savedHint = !string.IsNullOrEmpty(report.SavedSummaryPath)
                    ? MpDiagnosticReporter.SanitizeSensitiveText(report.SavedSummaryPath)
                    : report.ReportId;
                _lastDiagnosticReportNotice = $"诊断报告已保存 (Saved): {savedHint}";
                Multiplayer.DoMultiplayerStatusMessageLog(_lastDiagnosticReportNotice);
                if (PlayerCamera.main != null) {
                    Util.DoAlert(_lastDiagnosticReportNotice);
                }
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();

        if (!string.IsNullOrEmpty(_lastDiagnosticReportNotice)) {
            bool ogWrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;
            GUILayout.Label(_lastDiagnosticReportNotice);
            GUI.skin.label.wordWrap = ogWrap;
        }

        GUILayout.Space(16f * GetMenuUIScale());

        GUILayout.BeginHorizontal();
        {
            bool canDeactivateMp = !Net.IsRunning && !Util.IsInWorld();
            GUI.enabled = canDeactivateMp;
            if (GUILayout.Button(Lang.Get("mmst_button_deactivate_mp"))) {
                PlayerPrefsExtended.SetBool(Plugin.FORCEDISABLE_PLAYERPREF_KEY, true);
                PlayerPrefs.Save();
                Plugin.RestartGame();
            }
            GUI.enabled = ogEnabled;
            DoTooltipOnLastGUIRect(canDeactivateMp ? "mmst_button_deactivate_mp_desc" : "mmst_button_deactivate_mp_desc_disabled");
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    private static void __GUI____RenderFieldsWithThatAttribute(List<FieldInfo> flds, in Rect r) {
        float halfWidth = r.width * SettingLabelWidthRatio;
        foreach (FieldInfo f in flds) {
            if (!f.HasAttribute(typeof(SettingDeclarerThingyBaseAttribute))) {
                continue;
            }
            SettingDeclarerThingyBaseAttribute attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();
            string langKey = attr.n ?? ("setting_" + f.DeclaringType + "_" + f.Name);

            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(Lang.Get(langKey), GUILayout.Width(halfWidth));
                if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))
                    && Lang.TryGet(langKey + "_desc", out string descValue)) {
                    UIBullshit._GUI_SetTooltip("", descValue);
                }

                if (attr is SettingDeclarerThingyFloatAttribute fAttr) {
                    float val = (float)f.GetValue(null);
                    float newVal = GUILayout.HorizontalSlider(val, Mathf.Min(val, fAttr.mi), Mathf.Max(val, fAttr.ma));
                    GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                    GUILayout.Label(newVal.ToString("0.0") + fAttr.postfix, GUILayout.Width(r.width * 0.1f));
                    if (!Mathf.Approximately(val, newVal)) {
                        fAttr.Save(f, newVal);
                    }
                } else if (attr is SettingDeclarerThingyBoolAttribute bAttr) {
                    bool val = (bool)f.GetValue(null);
                    bool newVal = GUILayout.Toggle(val, "");
                    if (val != newVal) {
                        bAttr.Save(f, newVal);
#if FULLGAME
                        if (f.Name == nameof(UIBullshit.USE_DEFAULT_TALKER_FONT)) {
                            TalkerSpeciesFont.RefreshAll();
                        }
#endif
                    }
                } else if (attr is SettingDeclarerThingyChoiceAttribute cAttr) {
                    int val = f.GetValue(null).ConvertTo<int>();
                    string[] translatedChoices = cAttr.choices.Select(x => Lang.Get(x)).ToArray();
                    int newVal = GUILayout_DropdownMenu.Dropdown(val, translatedChoices);
                    if (val != newVal) {
                        cAttr.Save(f, newVal);
                    }
                } else {
                    GUILayout.Toggle(false, "ERROR UNKNOWN TYPE");
                }
            }
            GUILayout.EndHorizontal();
        }
    }

    public static List<FieldInfo> GetSettingsFromStaticClass(Type type) {
        if (!_known_settings.TryGetValue(type, out List<FieldInfo> fields)) {
            fields = type.GetFields(BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic).ToList();
            fields.RemoveAll(f => !f.HasAttribute(typeof(SettingDeclarerThingyBaseAttribute)));

            foreach (FieldInfo f in fields) {
                SettingDeclarerThingyBaseAttribute attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                attr.default_value = f.GetValue(null);
                attr.Get(f);
            }

            if (fields.Count > 0) {
                _known_settings[type] = fields;
            }
        }
        return fields;
    }
}
