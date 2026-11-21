using CasualtiesTogetherUtils;
using KrokV5Optimization;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Linq;
using Together.Diagnostics;
using UnityEngine;
using static Together.UIMainMenu;

namespace Together;

/// <summary>
/// 多人大厅服务器浏览器：IMGUI 列表视图、排序顶栏、筛选侧栏与缓存图标加载。
/// 数据过滤、延迟探测调度与排序逻辑见 <c>UIServerBrowser.Filtering.cs</c>。
/// </summary>
public static partial class UIServerBrowser {
    public static MenuTab menutab_serverbrowser = new MenuTab {
        name = "mmtb_serverbrowser",
        on_open = () => {
            if (KSteam.Loaded) {
                SteamNetworkingUtils.InitRelayNetworkAccess();
                BrowserSteamPing.EnsureCallbacks();
            }
            if (CurServerList.Count == 0) {
                ReloadList();
            }
            ResetPingSamplerFast();
            _manualSortActive = false;
        },
        gui_func = _GUI_RenderServerBrowser,
    };

    public static string _LOBBY_SEARCH_FILTER = "";
    private static Vector2 _serverBrowserMenuScroll = Vector2.zero;

    public static bool SortHideIncompatible;
    public static bool SortHidePasswordProtected = true;
    public static bool HideDescriptions;

    private static bool _sortFlipAlpha;
    private static bool _sortFlipCount;
    private static bool _sortFlipLayer;
    private static bool _sortFlipPing;
    private static bool _manualSortActive;

    public static int _LOBBY_ENTRY_HEIGHT = 90;
    public static int _LOBBY_ERROR_HEIGHT = 30;

    private static readonly Dictionary<string, Sprite> ResourceSprites = new();

    // Layout 阶段快照本帧待绘大厅行与控件布尔形态，Repaint 阶段复用以防 Ping 排序或 Steam 回调在两阶段之间改变控件数量抛 IMGUI 布局异常
    private struct BrowserRowShape {
        public bool badVersion;
        public bool friendIcon;
        public bool locked;
        public bool steam;
        public bool customMode;
        public bool listPlayers;
        public bool listMods;
        public bool showDesc;
        public bool showLayer;
        public bool showMood;
        public string mood;
    }

    private static NetPublicServerInfo[] _browserRows = Array.Empty<NetPublicServerInfo>();
    private static BrowserRowShape[] _rowShapes = Array.Empty<BrowserRowShape>();

    private static void CaptureBrowserRows() {
        int n = CurFilteredServerList.Count;
        var rows = new List<NetPublicServerInfo>(n);
        var shapes = new List<BrowserRowShape>(n);
        string ver = Multiplayer.FULL_VERSION_TAG;
        bool hideDesc = HideDescriptions;
        for (int i = 0; i < n; i++) {
            NetPublicServerInfo lobby = CurFilteredServerList[i];
            if (lobby == null) {
                continue;
            }
            BrowserRowShape shape = new BrowserRowShape();
#if RELEASE
            shape.badVersion = lobby.version != ver;
#endif
            shape.friendIcon = lobby.friends_tooltip != null;
            shape.locked = lobby.haspassword;
            shape.steam = lobby.steam_lobby_info != null;
            shape.listPlayers = lobby.plr_count > 0 && shape.steam;
            shape.listMods = lobby.modlist != null && lobby.modlist.Length > 0;
            shape.showDesc = !hideDesc && !string.IsNullOrWhiteSpace(lobby.description);
            shape.showLayer = lobby.cur_layer != -1;
            int clayer1 = lobby.cur_layer + 1;
            bool hasTitle = Locale.currentLang != null && Locale.currentLang.other != null && Locale.currentLang.other.TryGetValue("layertitle" + clayer1, out _);
            if (hasTitle && shape.showLayer && lobby.gamemode != "tutorial") {
                shape.customMode = lobby.gamemode != "debug_world" && lobby.gamemode != "loading" && lobby.gamemode != "vanilla";
            }
            if (shape.showLayer && Locale.currentLang != null && Locale.currentLang.other != null) {
                int num = Mathf.RoundToInt(lobby.avg_happiness * 0.1f);
                if (Locale.currentLang.other.TryGetValue("moodrange" + num.ToString(), out string moodRange)) {
                    shape.showMood = true;
                    shape.mood = moodRange;
                }
            }
            rows.Add(lobby);
            shapes.Add(shape);
        }
        _browserRows = rows.ToArray();
        _rowShapes = shapes.ToArray();
    }

    public static Sprite GetResourceSprite(in string str) {
        if (string.IsNullOrEmpty(str)) {
            return null;
        }
        // 命中缓存时直接返回，避免列表每帧重绘重复执行 Resources.Load
        if (ResourceSprites.TryGetValue(str, out Sprite cached) && cached != null) {
            return cached;
        }
        try {
            Sprite loaded = Resources.Load<Sprite>(str);
            ResourceSprites[str] = loaded;
            return loaded;
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, nameof(GetResourceSprite), ex);
            ResourceSprites[str] = null;
            return null;
        }
    }

    private static void _GUI_RenderServerBrowser(Rect r) {
        if (Event.current == null || Event.current.type == EventType.Layout) {
            TickPingSampler();
            CaptureBrowserRows();
        }

        GUI.skin.label.alignment = TextAnchor.UpperLeft;
#if RELEASE
        if (!KSteam.Loaded) {
            UIBullshit._GUI_BiggerLabel("Steam not loaded", 1.4f);
            return;
        }
#endif

        float fieldNamesWidth = r.width * 0.12f;
        float fieldsWidth = r.width * 0.26f;
        float fieldHeight = 30 * GetMenuUIScale();

        GUILayout.BeginHorizontal();
        GUILayout.BeginVertical();
        {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
            GUILayout.BeginHorizontal();
            {
                GUI.skin.label.alignment = TextAnchor.MiddleRight;
                GUILayout.Label(Lang.Get("mmsb_sort"));
                GUI.skin.label.alignment = TextAnchor.MiddleLeft;

                if (GUILayout.Button(Lang.Get("mmsb_sort_ping"))) {
                    int mul = _sortFlipPing ? -1 : 1;
                    CurFilteredServerList.Sort((a, b) => ComparePing(a, b) * mul);
                    _sortFlipPing = !_sortFlipPing;
                    _manualSortActive = true;
                }
                if (GUILayout.Button(Lang.Get("mmsb_sort_plrcount"))) {
                    int mul = _sortFlipCount ? 1 : -1;
                    CurFilteredServerList.Sort((a, b) => a.plr_count.CompareTo(b.plr_count) * mul);
                    _sortFlipCount = !_sortFlipCount;
                    _manualSortActive = true;
                }
                if (GUILayout.Button(Lang.Get("mmsb_sort_layer"))) {
                    int mul = _sortFlipLayer ? -1 : 1;
                    CurFilteredServerList.Sort((a, b) => a.cur_layer.CompareTo(b.cur_layer) * mul);
                    _sortFlipLayer = !_sortFlipLayer;
                    _manualSortActive = true;
                }
                if (GUILayout.Button(Lang.Get("mmsb_sort_name"))) {
                    int mul = _sortFlipAlpha ? -1 : 1;
                    CurFilteredServerList.Sort((a, b) => string.Compare(a.name, b.name, StringComparison.Ordinal) * mul);
                    _sortFlipAlpha = !_sortFlipAlpha;
                    _manualSortActive = true;
                }

                GUILayout.FlexibleSpace();
                GUILayout.Label(string.Format(Lang.Get("mmsb_lobbyfounds"), CurServerList.Count, CurFilteredServerList.Count, TotalPlayerCount));
            }
            GUILayout.EndHorizontal();

            GUILayout.Space(10 * GetMenuUIScale());

            _serverBrowserMenuScroll = GUILayout.BeginScrollView(_serverBrowserMenuScroll, false, true);

            int ogFontSize = GUI.skin.label.fontSize;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            TextAnchor ogAlign = GUI.skin.label.alignment;
            GUI.skin.label.wordWrap = false;

            bool isPrivilegedBypass =
#if DEBUG
                true;
#else
                KSteam.Loaded && KnownPersons.IsSteamUserPrivileged(KSteam.GetLocalUserSteamID().m_SteamID);
#endif

            GUI.skin.label.alignment = TextAnchor.MiddleCenter;

            if (_browserRows.Length == 0) {
                GUILayout.Label(CurServerList.Count == 0 ? Lang.Get("mmsb_empty") : Lang.Get("filter_found_nothing"));
            } else {
                try {
                    GUI.skin.label.fontSize = ogFontSize;
                    int lobbyFontSize = ogFontSize;
                    bool ogEnabled = GUI.enabled;

                    for (int ri = 0; ri < _browserRows.Length; ri++) {
                        NetPublicServerInfo lobby = _browserRows[ri];
                        BrowserRowShape shape = _rowShapes[ri];
                        GUI.skin.label.alignment = TextAnchor.UpperLeft;
                        GUILayout.BeginVertical(GUI.skin.box);
                        int rowH = 0;
                        try {
                            RenderSingleLobbyCard(r, lobby, shape, lobbyFontSize, ogEnabled, isPrivilegedBypass, ref rowH);
                        } catch (Exception ex) {
                            while (rowH > 0) {
                                GUILayout.EndHorizontal();
                                rowH--;
                            }
                            ModLog.Exception(LogDomain.UI, $"UIServerBrowser.RenderLobbyCard({lobby})", ex);
                        }
                        GUI.enabled = ogEnabled;
                        GUILayout.EndVertical();
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, "UIServerBrowser.RenderList", ex);
                }
            }

            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.alignment = ogAlign;

            GUILayout.Label("");
            GUILayout.EndScrollView();
        }
        GUILayout.EndVertical();

        RenderFilterSidebar(r, fieldHeight, fieldNamesWidth, fieldsWidth);
        GUILayout.EndHorizontal();
    }

    private static void RenderSingleLobbyCard(Rect r, NetPublicServerInfo lobby, BrowserRowShape shape, int lobbyFontSize, bool ogEnabled, bool isPrivilegedBypass, ref int rowH) {
        bool isSteamLobby = shape.steam;
        GUI.skin.label.fontSize = lobbyFontSize;

#if RELEASE
        if (shape.badVersion) {
            GUI.enabled = false;
            GUILayout.BeginHorizontal();
            rowH++;
            {
                GUILayout.Space(10 * GetMenuUIScale());
                GUILayout.Label(lobby.name);
                GUI.skin.label.alignment = TextAnchor.UpperCenter;
                GUILayout.Label(lobby.plr_count + "/" + lobby.plr_max);
            }
            GUILayout.EndHorizontal();
            rowH--;

            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUI.skin.label.fontSize = (int)(GUI.skin.label.fontSize * 0.9f);
            GUILayout.Label(Lang.Get("mmsb_lobbybadver") + " - " + lobby.version);
            GUI.enabled = ogEnabled;
            return;
        }
#endif

        int layerDisplayIndex = lobby.cur_layer + 1;
        Sprite layerSprite = GetResourceSprite("BiomeIcon/" + layerDisplayIndex);

        GUIContent gameStateContent;
        bool shouldDrawLayerIcon = false;
        if (!Locale.currentLang.other.TryGetValue("layertitle" + layerDisplayIndex, out string localizedLayerTitle) || lobby.cur_layer == -1) {
            gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_inmenu"), 27);
        } else if (lobby.gamemode == "tutorial") {
            gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_loading"), 28);
        } else {
            shouldDrawLayerIcon = true;
            if (lobby.gamemode == "debug_world") {
                gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_debug_world"), 29);
            } else if (lobby.gamemode == "loading") {
                gameStateContent = FixGuiContent.Content(Lang.Get("mmsb_lobby_loading"), 30);
            } else if (lobby.gamemode == "vanilla") {
                gameStateContent = FixGuiContent.Content(localizedLayerTitle, 31);
            } else {
                gameStateContent = FixGuiContent.Content(lobby.gamemode, 32);
            }
        }

        GUILayout.BeginHorizontal();
        rowH++;
        {
            GUIContent nameContent = FixGuiContent.Content(lobby.name, 33);
            Vector2 nameSize = GUI.skin.label.CalcSize(nameContent);

            GUILayout.Space(10 * GetMenuUIScale());
            GUI.skin.label.richText = true;
            GUILayout.Label(nameContent);
            GUI.skin.label.richText = false;
            GUILayout.Space(10 * GetMenuUIScale());

            if (shape.friendIcon) {
                GUILayout.Label(CoopModAssets.happy.texture, GUILayout.Width(nameSize.y), GUILayout.Height(nameSize.y));
                if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                    UIBullshit._GUI_SetTooltip(lobby.friends_tooltip, "");
                }
            }

            if (shape.locked) {
                GUILayout.Label(CoopModAssets.@lock.texture, GUILayout.Width(nameSize.y), GUILayout.Height(nameSize.y));
                if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                    UIBullshit._GUI_SetTooltip(Lang.Get("mmsb_lobby_islocked"), "");
                }
            }

            if (isSteamLobby) {
                GUILayout.Label(CoopModAssets.icon_steam.texture, GUILayout.Width(nameSize.y), GUILayout.Height(nameSize.y));
            }

            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            GUILayout.Label(lobby.plr_count + "/" + lobby.plr_max);
            GUILayout.Label(FormatPing(lobby), GUILayout.Width(70 * GetMenuUIScale()));

            GUILayout.FlexibleSpace();
            if (shape.customMode) {
                GUILayout.Label(lobby.gamemode);
            }
            GUILayout.FlexibleSpace();

            GUILayout.Button(Lang.Get("mmsb_lobby_listrules"));
            DoTooltipOnLastGUIRectNoLang(lobby.rules_as_string);

            if (shape.listPlayers) {
                GUILayout.Button(Lang.Get("mmsb_lobby_listplrs"));
                if (lobby.steam_lobby_info != null) {
                    DoTooltipOnLastGUIRectNoLang(string.Join("\n", lobby.steam_lobby_info.members.Select(x => KSteam.GetSteamUsername(x.Key.m_SteamID))));
                }
            }
            if (shape.listMods) {
                GUILayout.Button(Lang.Get("mmsb_lobby_listmod"));
                if (lobby.modlist != null) {
                    DoTooltipOnLastGUIRectNoLang(string.Join("\n", lobby.modlist));
                }
            }
        }
        GUILayout.EndHorizontal();
        rowH--;

        if (shape.showDesc) {
            GUILayout.BeginHorizontal();
            rowH++;
            {
                GUI.skin.label.richText = true;
                GUI.skin.label.wordWrap = true;
                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                int prevDescFont = GUI.skin.label.fontSize;
                Color prevColor = GUI.color;
                GUI.color = Color.white * 0.9f;
                GUI.skin.label.fontSize = (int)(prevDescFont * 0.8f);

                GUIContent descContent = FixGuiContent.Content(lobby.description ?? "", 34);
                float descWidth = 800 * GetMenuUIScale();
                float descHeight = GUI.skin.label.CalcHeight(descContent, descWidth);
                GUILayout.Label(descContent, GUILayout.Width(descWidth), GUILayout.Height(descHeight));

                GUI.skin.label.fontSize = prevDescFont;
                GUI.color = prevColor;
                GUILayout.Space(10 * GetMenuUIScale());
                GUI.skin.label.richText = false;
                GUI.skin.label.wordWrap = false;
            }
            GUILayout.EndHorizontal();
            rowH--;
        }

        GUILayout.BeginHorizontal();
        rowH++;
        {
            GUI.enabled = ogEnabled && !Multiplayer.IsNetworkActiveOrIsInGame() && (lobby.IsJoinable() || isPrivilegedBypass);
            if (GUILayout.Button(Lang.Get(lobby.latejoinspectate ? "mmsb_lobbyjoinspectate" : "mmsb_lobbyjoin"), GUILayout.Width(r.width * 0.2f))) {
                if (isSteamLobby && lobby.steam_lobby_info != null) {
                    Multiplayer.DoMultiplayerStatusMessageLog($"STEAM: Join Lobby {lobby.steam_lobby_info.lobby_steamID}");
                    TransportSteamworks.OnWantToJoinLobby(lobby.steam_lobby_info.lobby_steamID.m_SteamID);
                    UIMainMenu.current_tab = UIMainMenu.menutab_directconnect;
                    UIMainMenu._DO_SERVER_CONFIG = false;
                } else {
                    ModLog.Warning("服务器浏览器暂不支持非 Steam IP 房间直接加入，请使用直连面板", LogDomain.UI);
                }
            }
            if (Net.IsRunning) {
                DoTooltipOnLastGUIRect("mmsb_lobbyjoin_alreadyjoined");
            } else if (lobby.midjoin_lock_active) {
                DoTooltipOnLastGUIRect("mmsb_lobbyjoin_nomidjoin");
            } else if (lobby.enforcemodlist_locked) {
                DoTooltipOnLastGUIRect("mmsb_lobbyjoin_wrongmods");
            }
            GUI.enabled = ogEnabled;

            float pad = 10 * GetMenuUIScale();
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Space(pad);
            GUILayout.Label(gameStateContent);

            if (shouldDrawLayerIcon) {
                Rect iconRect = GUILayoutUtility.GetLastRect();
                if (layerSprite != null) {
                    GUI.DrawTexture(iconRect, layerSprite.texture);
                }
                GUI.Label(iconRect, gameStateContent);
            }

            GUI.skin.label.fontSize = (int)(GUI.skin.label.fontSize * 0.9f);
            if (shape.showLayer) {
                GUI.skin.label.alignment = TextAnchor.MiddleCenter;
                if (shape.showMood) {
                    GUILayout.Label(Lang.Get("mmsb_lobby_avgmood") + shape.mood);
                }
                GUILayout.Label(Lang.Get("mmsb_lobby_alivecount") + (lobby.plr_living_count + "/" + lobby.plr_count));
            }

            GUILayout.Space(pad);
            GUI.skin.label.alignment = TextAnchor.LowerRight;
            GUILayout.Label("V" + lobby.version);
            GUILayout.Space(pad);
        }
        GUILayout.EndHorizontal();
        rowH--;
    }

    private static void RenderFilterSidebar(Rect r, float fieldHeight, float fieldNamesWidth, float fieldsWidth) {
        GUILayout.BeginVertical(GUI.skin.box, GUILayout.Width(190 * GetMenuUIScale()));
        {
            GUILayout.BeginHorizontal();
            {
                if (KSteam.Loaded) {
                    GUILayout.Label(Lang.Get("mmsb_steam_distfilter"));
                    UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
                    KSteam.userselected_lobbyDistanceFilter = (ELobbyDistanceFilter)GUILayout_DropdownMenu.Dropdown((int)KSteam.userselected_lobbyDistanceFilter, new[] {
                        Lang.Get("mmsb_steam_dist0"),
                        Lang.Get("mmsb_steam_dist1"),
                        Lang.Get("mmsb_steam_dist2"),
                        Lang.Get("mmsb_steam_dist3"),
                    });
                    UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUILayout.BeginHorizontal();
            {
                GUI.skin.label.alignment = TextAnchor.MiddleRight;
                GUILayout.Label(Lang.Get("mmct_search"));
                string newNameFilter = GUILayout.TextField(_LOBBY_SEARCH_FILTER, GUILayout.Width(fieldsWidth));
                if (newNameFilter != _LOBBY_SEARCH_FILTER) {
                    _LOBBY_SEARCH_FILTER = newNameFilter;
                    RefilterList();
                }
            }
            GUILayout.EndHorizontal();

            UIMainMenu._GUI___DirectConnect_DoPasswordField(r, fieldHeight, fieldNamesWidth * 0.9f, fieldsWidth * 0.5f);

            bool oldPass = SortHidePasswordProtected;
            SortHidePasswordProtected = GUILayout.Toggle(SortHidePasswordProtected, Lang.Get("mmsb_lobbyhidepass"));
            if (oldPass != SortHidePasswordProtected) {
                RefilterList();
            }

            bool oldIncomp = SortHideIncompatible;
            SortHideIncompatible = GUILayout.Toggle(SortHideIncompatible, Lang.Get("mmsb_lobbyhidever"));
            if (oldIncomp != SortHideIncompatible) {
                RefilterList();
            }

            HideDescriptions = GUILayout.Toggle(HideDescriptions, Lang.Get("mmsb_lobbyhidedesc"));

            GUILayout.Space(20);
            GUILayout.BeginHorizontal();
            {
                GUILayout.Space(20);
                if (GUILayout.Button("  " + Lang.Get("mmsb_refresh") + "  ")) {
                    Util.PlayUISound(PlayerCamera.UISoundType.Click);
                    ReloadList();
                }
                GUILayout.FlexibleSpace();
            }
            GUILayout.EndHorizontal();

            GUILayout.FlexibleSpace();
        }
        GUILayout.EndVertical();
    }
}
