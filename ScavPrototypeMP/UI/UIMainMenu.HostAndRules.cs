using CasualtiesTogetherUtils;
using KrokV5Optimization;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机主菜单直连/Steam 建房面板、服务器配置项与游戏规则预设编辑器。
/// </summary>
public static partial class UIMainMenu {
    private static bool _UNHIDE_PASSWORD = false;
    private static bool _HIDE_IPPORT = false;
    private static string _COLOR_ERRORS = "";
    private static Rect _COLOR_ERRORS_RECT = default;

    public static string INPUT_COLORHEX = "#ffffff";
    public static Color24 LAST_VALID_INPUT_COLOR = Color24.black;

    internal static int _STEAM_CHOSEN_LOBBYTYPE = 2;
    internal static bool _DO_SERVER_CONFIG = false;
    internal static bool _USE_STEAM_MENU = true;

    public static string USERINPUT_IPPORT = Net.IP_LOCALHOST_IPv4 + ":" + Net.DEFAULT_PORT;
    public static string USERINPUT_NAME = "TEST_NAME";

    private static string _GUI____RenderRuleField_search = "";
    private const string LastRulePresetKey = "LastRulePreset";

    internal static int _GUI____RenderRuleField_PresetChosen = 0;
    internal static string _GUI____RenderRuleField_PresetName = "";
    private static float _GUI____RenderRuleField_last_max_width = 300f;
    private static float _GUI____RenderRuleField_cur_max_width = 0f;

    private static Vector2 _SCROLL_FULLRULELIST = Vector2.zero;

    public static void ParseInputColor(out bool is_valid) {
        if (INPUT_COLORHEX.Length > 1
            && Color24.TryParseHex(INPUT_COLORHEX, out Color24 c)
            && ScavPlayer.CheckIfPlrColorIsValid(c)) {
            is_valid = true;
            LAST_VALID_INPUT_COLOR = c;
            return;
        }
        is_valid = false;
    }

    public static void SetInputColor(in Color24 c) {
        INPUT_COLORHEX = ColorUtility.ToHtmlStringRGB(c);
        LAST_VALID_INPUT_COLOR = c;
    }

    private static void _GUI___DirectConnect_DoSteamInfoo(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth) {
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));
        {
            GUILayout.Label(Lang.Get("mmct_steam_mypersona") + KSteam.GetLocalUsername());
            _GUI___DirectConnect_DoColorPickerThingy(Net.IsRunning);
        }
        GUILayout.EndHorizontal();
        GUI.skin.label.alignment = TextAnchor.UpperRight;

        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));
        {
            float dropdownWidth = r2.width * 0.45f;
            GUILayout.Label(Lang.Get("mmct_steam_lobbytype"));

            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            string[] lobbyTypes = new string[] {
                Lang.Get("mmct_steam_lobbytype_private"),
                Lang.Get("mmct_steam_lobbytype_friends"),
                Lang.Get("mmct_steam_lobbytype_public"),
            };

            if (Multiplayer.IsNetworkActiveAndIsClient()) {
                GUILayout.Label("  ");
            } else {
                int oldLobbyType = _STEAM_CHOSEN_LOBBYTYPE;
                _STEAM_CHOSEN_LOBBYTYPE = GUILayout_DropdownMenu.Dropdown(_STEAM_CHOSEN_LOBBYTYPE, lobbyTypes, GUILayout.Width(dropdownWidth));

                if (Net.IsRunning && oldLobbyType != _STEAM_CHOSEN_LOBBYTYPE) {
                    ELobbyType newType = KSteam.NumToLobbyType(_STEAM_CHOSEN_LOBBYTYPE);
#if PLAYTEST
                    newType = ELobbyType.k_ELobbyTypePublic;
#endif
                    if (SteamMatchmaking.SetLobbyType(KSteam.CURRENT_LOBBY.lobby_steamID, newType)) {
                        ModLog.Info($"STEAM: Changed lobby type to {newType}", LogDomain.Lobby);
                    } else {
                        _STEAM_CHOSEN_LOBBYTYPE = oldLobbyType;
                    }
                }
            }

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    private static void _GUI___DirectConnect_DoSteamConnectButtons(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth, float bigheight) {
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmct_steam_createlobby"), GUILayout.Height(bigheight))) {
            ELobbyType lobbyType = KSteam.NumToLobbyType(_STEAM_CHOSEN_LOBBYTYPE);
            Net.NetType netType = Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED ? Net.NetType.DedicatedServer : Net.NetType.Host;

            Multiplayer.DoMultiplayerStatusMessageLog($"User pressed Create Lobby! {lobbyType} -> {netType}");
            TransportSteamworks.OnWantToHostLobby(netType, lobbyType);
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        GUILayout.FlexibleSpace();
        if (GUILayout.Button(Lang.Get("mmct_steam_openfriendlist"), GUILayout.Height(bigheight))) {
            Multiplayer.DoMultiplayerStatusMessageLog("Open Friends list!");
            SteamFriends.ActivateGameOverlay("friends");
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        GUILayout.FlexibleSpace();
    }

    internal static void _GUI___DirectConnect_DoPasswordField(Rect r2, float fieldheight, float fieldnameswidth, float fieldswidth) {
        GUILayout.BeginHorizontal(GUILayout.Height(fieldheight));
        {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.Label(Lang.Get("mmct_passwordfield") + ":  ", GUILayout.Width(fieldnameswidth));
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            bool canEdit = !Net.IsRunning || Net.IsServer;
            if (_UNHIDE_PASSWORD) {
                GUILayout.Label(Multiplayer.INPUT_PASSWORD, GUILayout.Width(fieldswidth));
            } else if (canEdit) {
                Multiplayer.INPUT_PASSWORD = GUILayout.PasswordField(Multiplayer.INPUT_PASSWORD, '*', GUILayout.Width(fieldswidth));
            } else {
                GUILayout.Label(new string('*', Multiplayer.INPUT_PASSWORD.Length), GUILayout.Width(fieldswidth));
            }

            GUILayout.Button(Lang.Get("mmct_show"));
            Rect buttonRect = GUILayoutUtility.GetLastRect();
            if (Input.GetKey(KeyCode.Mouse0)) {
                buttonRect = GUIUtility.GUIToScreenRect(buttonRect);
                if (UIBullshit.IsCursorInGUIRect(buttonRect)) {
                    _UNHIDE_PASSWORD = true;
                }
            } else {
                _UNHIDE_PASSWORD = false;
            }

            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    private static void _GUI___DirectConnect_DoColorPickerThingy(bool textfields_are_readonly) {
        Vector2 minHexSize = GUI.skin.label.CalcSize(FixGuiContent.Content("#FFFFFF", 13));
        Vector2 curHexSize = GUI.skin.label.CalcSize(FixGuiContent.Content(INPUT_COLORHEX, 14));
        if (minHexSize.x < curHexSize.x) {
            minHexSize.x = curHexSize.x;
        }

        ParseInputColor(out bool isValid);
        GUI.color = LAST_VALID_INPUT_COLOR;

        if (!textfields_are_readonly) {
            if (GUILayout.Button("", GUILayout.Height(minHexSize.y), GUILayout.Width(minHexSize.y))) {
                List<Color> allColors = ScavPlayer.NAMETAG_DEFAULT_COLORS.ToList();
                if (Multiplayer.rules.Teams) {
                    foreach (KeyValuePair<knetid, ScavPlayer> c in ScavPlayer.ClientIdToPlayerDict) {
                        if (!allColors.Contains(c.Value.playerColor)) {
                            allColors.Add(c.Value.playerColor);
                        }
                    }
                }

                int curIndex = allColors.IndexOf(LAST_VALID_INPUT_COLOR);
                if (curIndex == -1) {
                    curIndex = UnityEngine.Random.Range(0, allColors.Count);
                }
                curIndex = (curIndex + 1) % allColors.Count;

                SetInputColor(allColors[curIndex]);
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            if (!isValid) {
                GUI.color = Color.white;
            }

            INPUT_COLORHEX = GUILayout.TextField(INPUT_COLORHEX, 7, GUILayout.Width(minHexSize.x));
            if (INPUT_COLORHEX.Length > 6 && !INPUT_COLORHEX.StartsWith('#')) {
                INPUT_COLORHEX = INPUT_COLORHEX.Substring(0, 6);
            }

            _COLOR_ERRORS_RECT = GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect());
            _COLOR_ERRORS_RECT.x = _COLOR_ERRORS_RECT.xMax;
            _COLOR_ERRORS_RECT.width *= 10f;

            if (!isValid) {
                if (Color24.TryParseHex(INPUT_COLORHEX, out Color24 parsed) && !ScavPlayer.CheckIfPlrColorIsValid(parsed)) {
                    _COLOR_ERRORS = Lang.Get("mmct_colortoodark");
                } else {
                    _COLOR_ERRORS = Lang.Get("mmct_colornotvalid");
                }
            }
        } else {
            GUILayout.Space(minHexSize.y);
            GUILayout.Label(ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.playerColor.ToHex(true) : INPUT_COLORHEX, GUILayout.Width(minHexSize.x));
        }

        GUI.color = Color.white;
    }

    public static void DoTooltipOnLastGUIRect(in string lang_key) {
        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
            UIBullshit._GUI_SetTooltip("", Lang.Get(lang_key));
        }
    }

    public static void DoTooltipOnLastGUIRectNoLang(in string text) {
        if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
            UIBullshit._GUI_SetTooltip("", text);
        }
    }

    private static void _GUI___DoServerConfig(Rect r2) {
        float bigHeight = 50f * GetMenuUIScale();
        TextAnchor ogLabelAlign = GUI.skin.label.alignment;

        GUI.skin.label.alignment = TextAnchor.UpperCenter;
        UIBullshit._GUI_BiggerLabel(Lang.Get("mmct_serveroptions"), 1.4f);
        GUI.skin.label.alignment = TextAnchor.UpperLeft;

        bool ogEnabled = GUI.enabled;
        if (Net.IsRunning) {
            GUI.enabled = false;
        }
        Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED, Lang.Get("mmct_sconfig_dedicated"));
        DoTooltipOnLastGUIRect(GUI.enabled ? "mmct_sconfig_dedicated_tooltip" : "mmct_sconfig_dedicated_disabled");
        GUI.enabled = ogEnabled;

        Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH, Lang.Get("mmct_sconfig_verifyhash"));
        DoTooltipOnLastGUIRect("mmct_sconfig_verifyhash_tooltip");
        Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST = GUILayout.Toggle(Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST, Lang.Get("mmct_sconfig_enforcemodlist"));
        DoTooltipOnLastGUIRect("mmct_sconfig_enforcemodlist_tooltip");

        GUILayout.BeginHorizontal();
        {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.Label(Lang.Get("mmct_sconfig_plrcount"));
            if (int.TryParse(GUILayout.TextField(Multiplayer.PLAYER_COUNT_LIMIT.ToString(), GUILayout.MinWidth(bigHeight)), out int newLimit)) {
                Multiplayer.PLAYER_COUNT_LIMIT = (byte)Mathf.Clamp(newLimit, 1, Multiplayer.CONST_MAX_POSSIBLE_PLAYER_COUNT);
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();

        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUILayout.Label(Lang.Get("mmct_sconfig_description") + ": ");

        GUILayout.BeginHorizontal();
        {
            bool ogWordWrap = GUI.skin.textField.wordWrap;
            GUI.skin.textField.wordWrap = true;

            float calcHeight = GUI.skin.textField.CalcHeight(FixGuiContent.Content(Net.MyServerInfo.description, 15), r2.width);
            string newDesc = GUILayout.TextArea(Net.MyServerInfo.description, GUILayout.Height(Mathf.Max(calcHeight, GetMenuUIScale() * 100f)));
            GUI.skin.textField.wordWrap = ogWordWrap;

            if (Net.MyServerInfo.description != newDesc) {
                Multiplayer.SetServerDescription(newDesc);
            }
        }
        GUILayout.EndHorizontal();

        GUI.skin.label.alignment = ogLabelAlign;

        GUILayout.FlexibleSpace();
        GUILayout.BeginHorizontal(GUILayout.Height(bigHeight));
        {
            GUILayout.Space(10f * GetMenuUIScale());
            if (GUILayout.Button(Lang.Get("back"), GUILayout.MinWidth(r2.width * 0.25f))) {
                _DO_SERVER_CONFIG = false;
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
        GUILayout.FlexibleSpace();
    }

    private static void _GUI__DirectConnect(Rect r2) {
        int ogFontSize = GUI.skin.label.fontSize;
        TextAnchor ogLabelAlign = GUI.skin.label.alignment;
        try {
            if (_DO_SERVER_CONFIG) {
                if (Net.IsConnected && Net.IsClient) {
                    _DO_SERVER_CONFIG = false;
                } else {
                    _GUI___DoServerConfig(r2);
                    return;
                }
            }

            float fieldNamesWidth = r2.width * 0.27f;
            float fieldsWidth = r2.width * 0.36f;
            float bigHeight = 50f * GetMenuUIScale();
            float fieldHeight = 30f * GetMenuUIScale();
            float ipPortFieldWidth = r2.width * 0.45f;

            int multiplayerMethodMenu = (KSteam.Loaded && (_USE_STEAM_MENU || Net.Transport is TransportSteamworks)) ? 1 : 0;

            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            GUILayout.BeginHorizontal();
            {
                bool drawSteamToggle = !Plugin.FORCE_NO_STEAM && !Net.IsRunning && KSteam.Loaded;
                if (drawSteamToggle) {
                    GUILayout.FlexibleSpace();
                }

                UIBullshit._GUI_BiggerLabel(_USE_STEAM_MENU ? Lang.Get("mmct_steamlobby") : Lang.Get("mmct_directconnecttitle"), 1.4f);

                GUI.skin.label.fontSize = ogFontSize;
                GUI.skin.label.alignment = TextAnchor.MiddleRight;

                if (drawSteamToggle) {
                    _USE_STEAM_MENU = GUILayout.Toggle(_USE_STEAM_MENU, Lang.Get("mmct_steammenutoggle"));
                }
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal(GUILayout.Height(fieldHeight));
            {
                GUILayout.Label(Lang.Get("mmct_sconfig_servername"), GUILayout.ExpandWidth(false));
                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                if (!Net.IsRunning || Net.IsServer) {
                    string newName = GUILayout.TextField(Net.MyServerInfo.name);
                    if (Net.MyServerInfo.name != newName) {
                        Multiplayer.SetServerName(newName);
                    }
                } else {
                    GUILayout.Label(Net.CurrentServerInfo.name);
                }
            }
            GUILayout.EndHorizontal();

            bool textfieldsReadOnly = Net.IsRunning;
            if (multiplayerMethodMenu == 0) {
                GUILayout.BeginHorizontal(GUILayout.Height(fieldHeight));
                {
                    GUILayout.Label(Lang.Get("mmct_ipportfield") + ":  ", GUILayout.Width(fieldNamesWidth));
                    GUI.skin.label.alignment = TextAnchor.UpperLeft;
                    if (textfieldsReadOnly) {
                        GUILayout.Label(_HIDE_IPPORT ? new string('*', USERINPUT_IPPORT.Length) : USERINPUT_IPPORT, GUILayout.Width(ipPortFieldWidth));
                    } else if (_HIDE_IPPORT) {
                        USERINPUT_IPPORT = GUILayout.PasswordField(USERINPUT_IPPORT, '*', GUILayout.Width(ipPortFieldWidth));
                    } else {
                        USERINPUT_IPPORT = GUILayout.TextField(USERINPUT_IPPORT, GUILayout.Width(ipPortFieldWidth));
                    }
                    _HIDE_IPPORT = GUILayout.Toggle(_HIDE_IPPORT, Lang.Get("mmct_hideip"));
                    GUILayout.FlexibleSpace();
                }
                GUILayout.EndHorizontal();

                GUILayout.BeginHorizontal(GUILayout.Height(fieldHeight));
                _GUI___DrawNameAndColorFields(fieldNamesWidth, fieldsWidth, textfieldsReadOnly);
                GUILayout.EndHorizontal();
            } else {
                _GUI___DirectConnect_DoSteamInfoo(r2, fieldHeight, fieldNamesWidth, fieldsWidth);
            }

            _GUI___DirectConnect_DoPasswordField(r2, fieldHeight, fieldNamesWidth, fieldsWidth);
            GUILayout.Space(20f * GetMenuUIScale());

            if (Net.IsRunning && Net.IsConnecting) {
                GUILayout.Label(Lang.Get("mmct_connecting"));
            } else {
                GUILayout.BeginHorizontal(GUILayout.Height(bigHeight));
                {
                    if (Net.IsServer) {
                        if (GUILayout.Button(Lang.Get("mmct_serveroptions"))) {
                            _DO_SERVER_CONFIG = true;
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (KSteam.IS_IN_LOBBY && KSteam.CURRENT_LOBBY.lobby_steamID.IsValid()) {
                        if (GUILayout.Button(Lang.Get("mmct_copylobbycode"))) {
                            GUIUtility.systemCopyBuffer = KSteam.CURRENT_LOBBY.lobby_steamID.ToString();
                        }
                        GUILayout.FlexibleSpace();
                    }

                    if (Net.IsServer && Net.IsConnected && Util.IsInMainMenu() && PreRunScript.instance != null) {
                        if (GUILayout.Button(Lang.Get("mmct_gotorunsettings"))) {
                            SetOpen(false);
                            PreRunScript.instance.runSettingsScreen.SetActive(true);
                        }
                    }
                }
                GUILayout.EndHorizontal();
                GUILayout.Space(15f * GetMenuUIScale());
            }

            GUILayout.BeginHorizontal(GUILayout.Height(bigHeight));
            {
                if (Net.IsRunning) {
                    GUILayout.FlexibleSpace();
                    string disconnectLabel = Net.IsClient
                        ? (Net.IsConnecting ? Lang.Get("mmct_disconnect_abort") : Lang.Get("mmct_disconnect"))
                        : Lang.Get("mmct_disconnect_server");
                    if (GUILayout.Button(disconnectLabel, GUILayout.MinWidth(bigHeight * 3f), GUILayout.Height(bigHeight))) {
                        Multiplayer.ShutdownNetwork();
                        Chat.LogMessage(Chat.NAMELABEL_LOCALSYSTEM, "Disconnected by the user.");
                        Multiplayer.DoMultiplayerStatusMessageLog("Disconnected by the user.");
                        Util.PlayUISound(PlayerCamera.UISoundType.Close);
                    }

                    if (Net.IsRunningSteam && KSteam.IS_IN_LOBBY && (!(KSteam.CURRENT_LOBBY.locked || Net.CurrentServerInfo.midjoin_lock_active) || Net.IsServer)) {
                        GUILayout.FlexibleSpace();
                        if (GUILayout.Button(Lang.Get("mmct_steam_invitefriends"), GUILayout.Height(bigHeight))) {
                            Multiplayer.DoMultiplayerStatusMessageLog("Open Friends Invite dialog!");
                            SteamFriends.ActivateGameOverlayInviteDialog(KSteam.CURRENT_LOBBY.lobby_steamID);
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    }
                    GUILayout.FlexibleSpace();
                } else if (Util.IsInWorld()) {
                    GUILayout.Label(Lang.Get("mmct_canthostinworld"));
                } else if (multiplayerMethodMenu == 0) {
                    if (Multiplayer.SERVER_TOGGLE_SHOULD_HOST_DEDICATED) {
                        if (GUILayout.Button(Lang.Get("mmct_startserver"), GUILayout.Height(bigHeight))) {
                            Multiplayer.DoMultiplayerStatusMessageLog("Start Server clicked!");
                            TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.DedicatedServer);
                            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        }
                    } else if (GUILayout.Button(Lang.Get("mmct_starthost"), GUILayout.Height(bigHeight))) {
                        Multiplayer.DoMultiplayerStatusMessageLog("Start Host clicked!");
                        TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.Host);
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }

                    if (Util.IsInMainMenu() && GUILayout.Button(Lang.Get("mmct_startclient"), GUILayout.Height(bigHeight))) {
                        Multiplayer.DoMultiplayerStatusMessageLog("Client Connect clicked!");
                        TransportLiteNetLib.OnWantToConnect(USERINPUT_IPPORT, Net.NetType.Client);
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    }
                } else {
                    _GUI___DirectConnect_DoSteamConnectButtons(r2, fieldHeight, fieldNamesWidth, fieldsWidth, bigHeight);
                }
            }
            GUILayout.EndHorizontal();

            bool ogWordWrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;
            try {
                GUIContent statusContent = FixGuiContent.Content(Lang.Get("mmct_last_status_msg") + "\n" + Multiplayer.multiplayerStatusMessage, 16);
                float textHeight = GUI.skin.label.CalcHeight(statusContent, r2.width);
                GUILayout.Label(statusContent, GUILayout.Height(textHeight));

                if (KSteam.Loaded && KSteam.IsSteamMaintenanceLikely()) {
                    GUILayout.Space(10f * GetMenuUIScale());
                    Color ogColor = GUI.color;
                    GUI.color = Color.red;
                    GUIContent maintContent = FixGuiContent.Content(Lang.Get("mmct_steammaintenance"), 17);
                    float maintHeight = GUI.skin.label.CalcHeight(maintContent, r2.width);
                    GUILayout.Label(maintContent, GUILayout.Height(maintHeight));
                    GUI.color = ogColor;
                }
            } finally {
                GUI.skin.label.wordWrap = ogWordWrap;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI__DirectConnect", ex);
        } finally {
            GUI.skin.label.alignment = ogLabelAlign;
            GUI.skin.label.fontSize = ogFontSize;
        }
    }

    private static void _GUI___DrawNameAndColorFields(float fieldnameswidth, float fieldswidth, bool textfields_are_readonly) {
        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        GUILayout.Label(Lang.Get("mmct_usernamefield") + ":  ", GUILayout.Width(fieldnameswidth));
        if (textfields_are_readonly) {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Label(USERINPUT_NAME, GUILayout.Width(fieldswidth));
        } else {
            string text = string.IsNullOrEmpty(USERINPUT_NAME) ? "" : USERINPUT_NAME;
            string edited = GUILayout.TextField(text, Multiplayer.MAX_NAME_LENGTH, GUILayout.Width(fieldswidth));
            if (edited != USERINPUT_NAME) {
                Multiplayer.ExplicitSessionName = true;
            }
            USERINPUT_NAME = edited;
        }
        _GUI___DirectConnect_DoColorPickerThingy(textfields_are_readonly);
        GUILayout.FlexibleSpace();
    }

    private static void _GUI___ConnectionSecondPanel_ContentRender_RenderRulesList(Rect r2) {
        bool canEdit = Con.CanExecuteAdminCommands();
        GUI.skin.label.alignment = TextAnchor.UpperRight;

        if (canEdit) {
            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(Lang.Get("mmct_presetname"));
                _GUI____RenderRuleField_PresetName = GUILayout.TextField(_GUI____RenderRuleField_PresetName, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.23f));

                bool ogEnabled = GUI.enabled;
                if (GUI.enabled) {
                    GUI.enabled = !string.IsNullOrWhiteSpace(_GUI____RenderRuleField_PresetName);
                }
                if (GUILayout.Button(Lang.Get("mmct_presetsave"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    Multiplayer.RulesSaver.SetData(_GUI____RenderRuleField_PresetName, Multiplayer.rules);
                    Multiplayer.RulesSaver.Save();

                    List<string> presets = Multiplayer.RulesSaver.GetPresets().ToList();
                    int foundIndex = presets.IndexOf(_GUI____RenderRuleField_PresetName);
                    if (foundIndex != -1) {
                        _GUI____RenderRuleField_PresetChosen = foundIndex + 1;
                        RememberRulePreset(_GUI____RenderRuleField_PresetName);
                    }
                }
                if (GUILayout.Button(Lang.Get("mmct_presetremove")) && Multiplayer.RulesSaver.HasKey(_GUI____RenderRuleField_PresetName)) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    string selectedPreset = Multiplayer.RulesSaver.GetPresets().ElementAtOrDefault(_GUI____RenderRuleField_PresetChosen - 1);
                    Multiplayer.RulesSaver.DeleteKey(_GUI____RenderRuleField_PresetName);
                    Multiplayer.RulesSaver.Save();
                    _GUI____RenderRuleField_PresetChosen = Multiplayer.RulesSaver.GetPresets().ToList().IndexOf(selectedPreset) + 1;
                    if (_GUI____RenderRuleField_PresetChosen == 0) {
                        _GUI____RenderRuleField_PresetName = "";
                        RememberRulePreset("");
                    }
                }
                GUI.enabled = ogEnabled;
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUILayout.Label(Lang.Get("mmct_presetselect"));
                List<string> arr = new List<string> { "[no preset]" };
                List<string> presets = Multiplayer.RulesSaver.GetPresets().ToList();
                arr.AddRange(presets);

                int prevPreset = _GUI____RenderRuleField_PresetChosen;
                _GUI____RenderRuleField_PresetChosen = GUILayout_DropdownMenu.Dropdown(_GUI____RenderRuleField_PresetChosen, arr.ToArray());

                int realPreset = _GUI____RenderRuleField_PresetChosen - 1;
                if (prevPreset != _GUI____RenderRuleField_PresetChosen) {
                    if (realPreset >= 0 && realPreset < presets.Count) {
                        _GUI____RenderRuleField_PresetName = presets[realPreset];
                        Multiplayer.rules = Multiplayer.RulesSaver.GetData(_GUI____RenderRuleField_PresetName);
                        Multiplayer.ApplyGameRules();
                    } else {
                        _GUI____RenderRuleField_PresetName = "";
                    }
                    RememberRulePreset(_GUI____RenderRuleField_PresetName);
                }

                GUILayout.FlexibleSpace();

                if (GUILayout.Button(Lang.Get("mmct_reset_rules"))) {
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    Con.ExecuteCommandAdminNoLog("resetrules");
                }
            }
            GUILayout.EndHorizontal();
        }

        GUILayout.BeginHorizontal();
        {
            GUILayout.FlexibleSpace();
            GUILayout.Label(Lang.Get("mmct_search"));
            _GUI____RenderRuleField_search = GUILayout.TextField(_GUI____RenderRuleField_search, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.3f));
        }
        GUILayout.EndHorizontal();

        _SCROLL_FULLRULELIST = GUILayout.BeginScrollView(_SCROLL_FULLRULELIST);
        try {
            List<FieldInfo> ruleFields = typeof(MultiplayerGameRules).GetFields(BindingFlags.Instance | BindingFlags.Public).ToList();
            if (!string.IsNullOrWhiteSpace(_GUI____RenderRuleField_search)) {
                ruleFields = ruleFields.Where(f => f.Name.ContainsInsensitive(_GUI____RenderRuleField_search)
                    || (Lang.Get("rule_name_" + f.Name, true) ?? f.Name).ContainsInsensitive(_GUI____RenderRuleField_search)).ToList();
            }

            if (ruleFields.Count == 0) {
                GUI.skin.label.alignment = TextAnchor.UpperCenter;
                GUILayout.Label(Lang.Get("filter_found_nothing"));
            } else {
                _GUI____RenderRuleField_cur_max_width = 0f;
                foreach (FieldInfo fld in ruleFields) {
                    GUILayout.BeginHorizontal();
                    try {
                        _GUI____RenderRuleField(fld, canEdit, r2);
                    } catch (Exception ex) {
                        ModLog.Exception(LogDomain.UI, $"UIMainMenu.RenderRuleField({fld.Name})", ex);
                    } finally {
                        GUILayout.EndHorizontal();
                    }
                }
                _GUI____RenderRuleField_last_max_width = Mathf.Min(r2.width * 0.5f, _GUI____RenderRuleField_cur_max_width);
            }
        } finally {
            GUILayout.EndScrollView();
        }
    }

    private static void RememberRulePreset(string presetName) {
        SettingsJsonThing.json.SetString(LastRulePresetKey, presetName);
        SettingsJsonThing.json.Save();
    }

    internal static void RestoreLastRulePreset() {
        if (Net.IsRunning) {
            return;
        }

        string presetName = SettingsJsonThing.json.GetString(LastRulePresetKey);
        int index = Multiplayer.RulesSaver.GetPresets().ToList().IndexOf(presetName);
        _GUI____RenderRuleField_PresetChosen = index + 1;
        _GUI____RenderRuleField_PresetName = index >= 0 ? presetName : "";
        if (index >= 0) {
            Multiplayer.rules = Multiplayer.RulesSaver.GetData(presetName);
            Multiplayer.ApplyGameRules();
        } else if (!string.IsNullOrEmpty(presetName)) {
            RememberRulePreset("");
        }
    }

    private static void _GUI____RenderRuleField(FieldInfo fld, bool can_edit, in Rect r2) {
        bool doTooltip;
        string ruleName = Lang.Get("rule_name_" + fld.Name, true) ?? fld.Name;
        GUI.skin.label.wordWrap = false;

        Vector2 nameSize = GUI.skin.label.CalcSize(FixGuiContent.Content(ruleName, 18));
        if (nameSize.x > _GUI____RenderRuleField_cur_max_width) {
            _GUI____RenderRuleField_cur_max_width = nameSize.x;
        }

        string ruleValueString = null;
        object ogValue = fld.GetValue(Multiplayer.rules);

        if (!can_edit) {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            string str = fld.FieldType == typeof(float)
                ? $"{ruleName}:  {Math.Round((float)ogValue, 1)}"
                : $"{ruleName}:  {ogValue}";
            GUILayout.Label(str);
            doTooltip = UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));
        } else {
            GUI.skin.label.alignment = TextAnchor.MiddleRight;
            GUILayout.Label(ruleName, GUILayout.Width(_GUI____RenderRuleField_last_max_width));
            doTooltip = UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));

            TypedReference rulesRef = __makeref(Multiplayer.rules);
            bool didChange = false;
            GUI.skin.label.alignment = TextAnchor.MiddleLeft;

            if (fld.FieldType == typeof(bool)) {
                bool newValue = GUILayout.Toggle((bool)ogValue, "");
                fld.SetValueDirect(rulesRef, newValue);
                didChange = newValue != (bool)ogValue;
            } else if (fld.HasAttribute(typeof(MultiplayerRuleByteAttribute))) {
                MultiplayerRuleByteAttribute attr = fld.GetAttribute<MultiplayerRuleByteAttribute>();
                float floatified = (byte)ogValue;
                float newValue = (float)Math.Round(GUILayout.HorizontalSlider(floatified, attr.min, attr.limit), 1);
                doTooltip = doTooltip || UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));

                fld.SetValueDirect(rulesRef, (byte)Mathf.RoundToInt(newValue));
                ruleValueString = Math.Round(newValue) + attr.postfix;
                GUILayout.Label(ruleValueString, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.1f));
                didChange = !Mathf.Approximately(newValue, floatified);
            } else if (fld.FieldType == typeof(float)) {
                float min = 0f;
                float max = 10f;
                MultiplayerRuleFloatAttribute attr = fld.GetAttribute<MultiplayerRuleFloatAttribute>();
                if (attr != null) {
                    min = attr.mi;
                    max = attr.ma;
                }
                float fOg = (float)ogValue;
                float newValue = (float)Math.Round(GUILayout.HorizontalSlider(fOg, Mathf.Min(fOg, min), Mathf.Max(fOg, max)), 1);
                doTooltip = doTooltip || UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()));

                fld.SetValueDirect(rulesRef, newValue);
                ruleValueString = attr != null ? (newValue.ToString("0.0") + attr.postfix) : newValue.ToString("0.0");
                GUILayout.Label(ruleValueString, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.1f));
                didChange = !Mathf.Approximately(newValue, fOg);
            } else if (fld.FieldType == typeof(int) || fld.FieldType == typeof(byte) || fld.FieldType == typeof(ushort)) {
                if (int.TryParse(GUILayout.TextField(ogValue.ToString()), out int parsedInt)) {
                    object typedVal = fld.FieldType == typeof(byte)
                        ? (object)(byte)Mathf.Clamp(parsedInt, byte.MinValue, byte.MaxValue)
                        : (fld.FieldType == typeof(ushort)
                            ? (object)(ushort)Mathf.Clamp(parsedInt, ushort.MinValue, ushort.MaxValue)
                            : parsedInt);
                    fld.SetValueDirect(rulesRef, typedVal);
                    didChange = !Equals(typedVal, ogValue);
                }
            } else {
                GUILayout.Label($"[{fld.FieldType.Name}]: {ogValue} ");
            }

            if (didChange) {
                if (Multiplayer.IsNetworkActiveAndIsClient()) {
                    if (Con.client_isadmin) {
                        object updated = fld.GetValue(Multiplayer.rules);
                        string cmd = $"rule {fld.Name} {updated.ToStringInvariant()}";
                        Multiplayer.DoMultiplayerStatusMessageLog($"Sending command: \"{cmd}\"");
                        Con.ExecuteCommandAdminNoLog(cmd);
                    }
                } else {
                    Multiplayer.ApplyGameRules();
                }
            }
        }

        if (doTooltip) {
            if (!Lang.TryGet("rule_desc_" + fld.Name, out string desc)) {
                desc = "";
            }
            if (!string.IsNullOrWhiteSpace(ruleValueString)) {
                desc = string.IsNullOrWhiteSpace(desc) ? ruleValueString : (desc + "\n" + ruleValueString);
            }
            if (!string.IsNullOrWhiteSpace(desc)) {
                UIBullshit._GUI_SetTooltip(fld.Name, desc);
            }
        }
    }

    private static void _GUI_RenderDirectConnectMenu(Rect r) {
        GUI.color = Color.white;
        float additionalHalfOffset = 200f * GetMenuUIScale();
        float halfBorderSize = UIBullshit.uiBlockSmallBorderSize * 0.5f;

        if (Net.Transport != null) {
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = Net.Transport is TransportLiteNetLib;
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = Net.Transport is TransportSteamworks;

            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben) {
                _USE_STEAM_MENU = true;
            }
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton) {
                _USE_STEAM_MENU = false;
            }
        } else {
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = false;
            ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = false;
        }

        Rect connectRect = new Rect(r) {
            xMax = r.center.x + additionalHalfOffset - halfBorderSize,
            x = 0f,
            y = 0f
        };
        UIBullshit._GUI_9SlicePanel(connectRect, 1f, 0.5f, false, UIBullshit.unscaled_uiBlockNano);
        connectRect = connectRect.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);
        GUILayout.BeginArea(connectRect);
        try {
            _GUI__ConnectionSecondPanel(connectRect);
        } finally {
            GUILayout.EndArea();
        }

        Rect secondHalf = new Rect(r) {
            x = 0f,
            y = 0f,
            xMin = r.center.x + additionalHalfOffset + halfBorderSize
        };
        UIBullshit._GUI_9SlicePanel(secondHalf, 1f, 0.5f, false, UIBullshit.unscaled_uiBlockNano);
        secondHalf = secondHalf.ShrinkBorder(UIBullshit.uiBlockNanoBorderSize);
        GUILayout.BeginArea(secondHalf);
        try {
            _GUI__DirectConnect(secondHalf);
        } finally {
            GUILayout.EndArea();
        }
    }
}
