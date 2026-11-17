using CasualtiesTogetherUtils;
using Steamworks;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using static Together.KSteam;

namespace Together;

/// <summary>
/// 服务器浏览器数据管理分部：P2P 延迟轮询采样、大厅过滤（含终局层防剧透与敏感词过滤）、多级排序与列表刷新。
/// </summary>
public static partial class UIServerBrowser {
    private const float PingSampleIntervalFast = 0.2f;
    private const float PingSampleIntervalSlow = 1f;

    // 终局爬升层（10）与实验室层（11）禁止出现在公开大厅列表，防止剧透或中途乱入破坏终局状态机
    public const int AscentLayerDepth = 10;
    public const int LabLayerDepth = 11;

    private static float _pingSampleAccum;
    private static int _pingSampleIndex;
    private static bool _pingFirstPassDone;
    private static int _pingSortFingerprint = int.MinValue;

    internal static readonly Dictionary<ulong, NetPublicServerInfo> CurServerList_ButOnlySteamLobbies = new();
    internal static List<NetPublicServerInfo> CurServerList = new();
    internal static List<NetPublicServerInfo> CurFilteredServerList = new();
    public static int CurServerCount => CurServerList.Count;
    private static int TotalPlayerCount;

    private static bool SearchPatternIsLobbyId;
    private static ulong SearchPatternLobbyId;

    private static void ResetPingSamplerFast() {
        _pingSampleIndex = 0;
        _pingSampleAccum = 0f;
        _pingFirstPassDone = false;
        _pingSortFingerprint = int.MinValue;
        BrowserSteamPing.CancelAll();
    }

    private static int ComputePingSortFingerprint() {
        int known = 0;
        int sum = 0;
        for (int i = 0; i < CurFilteredServerList.Count; i++) {
            NetPublicServerInfo info = CurFilteredServerList[i];
            if (info == null || !info.HasPing) {
                continue;
            }
            known++;
            sum += info.pingMs;
        }
        return (known * 397) ^ sum;
    }

    private static void TickPingSampler() {
        if (!KSteam.Loaded) {
            return;
        }

        BrowserSteamPing.Tick();

        int fingerprint = ComputePingSortFingerprint();
        if (fingerprint != _pingSortFingerprint) {
            _pingSortFingerprint = fingerprint;
            if (!_manualSortActive) {
                SortServerListByJoinability();
            }
        }

        float interval = _pingFirstPassDone ? PingSampleIntervalSlow : PingSampleIntervalFast;
        _pingSampleAccum += Time.unscaledDeltaTime;
        if (_pingSampleAccum < interval) {
            return;
        }
        _pingSampleAccum = 0f;

        int count = CurFilteredServerList.Count;
        if (count <= 0) {
            _pingSampleIndex = 0;
            return;
        }

        for (int attempt = 0; attempt < count; attempt++) {
            if (_pingSampleIndex >= count) {
                _pingSampleIndex = 0;
                if (!_pingFirstPassDone) {
                    _pingFirstPassDone = true;
                    return;
                }
            }

            NetPublicServerInfo info = CurFilteredServerList[_pingSampleIndex++];
            if (BrowserSteamPing.TryBeginProbe(info)) {
                return;
            }
            if (BrowserSteamPing.InFlightCount >= BrowserSteamPing.MaxConcurrent) {
                _pingSampleIndex = Math.Max(0, _pingSampleIndex - 1);
                return;
            }
        }
    }

    public static void AddServerToTheList(NetPublicServerInfo info) {
        if (info == null) {
            return;
        }

        ulong id = info.steam_lobby_info != null ? info.steam_lobby_info.lobby_steamID.m_SteamID : 0;

        if (info.steam_lobby_info != null) {
            if (CurServerList_ButOnlySteamLobbies.ContainsKey(id)) {
                ModLog.Debug($"[LobbyBrowser] 忽略重复大厅 id={id} name=\"{info.name}\"", LogDomain.Lobby);
                return;
            }
            CurServerList_ButOnlySteamLobbies[id] = info;

            foreach (ulong friendId in KSteam.Friends) {
                if (info.steam_lobby_info.members.ContainsKey((CSteamID)friendId) && !info.steamfriends.Contains(friendId)) {
                    info.steamfriends.Add(friendId);
                    info.friends_tooltip ??= Lang.Get("mmsb_steam_lobbyfriend");
                    info.friends_tooltip += '\n' + SteamFriends.GetFriendPersonaName((CSteamID)friendId);
                }
            }
        }

        TotalPlayerCount += info.plr_count;
        CurServerList.Add(info);

        if (TestLobbySearchFilters(info, out string filterDrop)) {
            bool isJoinable = info.IsJoinable();
            if (isJoinable) {
                CurFilteredServerList.Insert(0, info);
            } else {
                CurFilteredServerList.Add(info);
            }
            ModLog.Debug($"[LobbyBrowser] 显示大厅 id={id} name=\"{info.name}\" ver=\"{info.version}\" joinable={isJoinable} filtered={CurFilteredServerList.Count}/{CurServerList.Count}", LogDomain.Lobby);
        } else {
            ModLog.Debug($"[LobbyBrowser] 过滤隐藏 id={id} name=\"{info.name}\" ver=\"{info.version}\" reason={filterDrop}", LogDomain.Lobby);
        }
    }

    public static bool TestLobbySearchFilters(NetPublicServerInfo info) {
        return TestLobbySearchFilters(info, out _);
    }

    public static bool TestLobbySearchFilters(NetPublicServerInfo info, out string dropReason) {
        dropReason = null;
        if (info == null) {
            dropReason = "null-info";
            return false;
        }
        if (info.cur_layer >= AscentLayerDepth) {
            dropReason = $"ascent-lab-layer-blacklist layer={info.cur_layer}";
            return false;
        }
        if (NsfwContentFilter.IsListingBlocked(info.name, info.description)) {
            dropReason = "nsfw-filter";
            return false;
        }
        if (!string.IsNullOrEmpty(_LOBBY_SEARCH_FILTER)) {
            bool matchesName = info.name != null && info.name.ContainsInsensitive(_LOBBY_SEARCH_FILTER);
            bool matchesId = SearchPatternIsLobbyId && info.steam_lobby_info != null && info.steam_lobby_info.lobby_steamID.m_SteamID == SearchPatternLobbyId;
            if (!matchesName && !matchesId) {
                dropReason = $"search-filter \"{_LOBBY_SEARCH_FILTER}\"";
                return false;
            }
        }
        if (SortHidePasswordProtected && info.haspassword) {
            dropReason = "hide-password";
            return false;
        }
        if (SortHideIncompatible && info.version != Multiplayer.FULL_VERSION_TAG) {
            dropReason = $"hide-incompatible lobbyVer=\"{info.version}\" myVer=\"{Multiplayer.FULL_VERSION_TAG}\"";
            return false;
        }
        return true;
    }

    public static void RefilterList() {
        List<NetPublicServerInfo> listClone = new List<NetPublicServerInfo>(CurServerList);
        ClearLists();
        foreach (NetPublicServerInfo item in listClone) {
            AddServerToTheList(item);
        }

        _pingSampleIndex = 0;
        _manualSortActive = false;
        _pingSortFingerprint = int.MinValue;
        SortServerListByJoinability();
    }

    private static int ComparePing(NetPublicServerInfo a, NetPublicServerInfo b) {
        int ap = a != null && a.HasPing ? a.pingMs : int.MaxValue;
        int bp = b != null && b.HasPing ? b.pingMs : int.MaxValue;
        return ap.CompareTo(bp);
    }

    private static string FormatPing(NetPublicServerInfo lobby) {
        if (lobby == null || !lobby.HasPing) {
            return "— ms";
        }
        return lobby.pingMs + " ms";
    }

    public static void SortServerListByJoinability() {
        CurFilteredServerList.Sort((a, b) => {
            int friendComp = (b.friends_tooltip != null).CompareTo(a.friends_tooltip != null);
            if (friendComp != 0) {
                return friendComp;
            }

            int pingComp = ComparePing(a, b);
            if (pingComp != 0) {
                return pingComp;
            }

            int verComp = (a.version != Multiplayer.FULL_VERSION_TAG).CompareTo(b.version != Multiplayer.FULL_VERSION_TAG);
            if (verComp != 0) {
                return verComp;
            }

            int joinComp = (!a.IsJoinable()).CompareTo(!b.IsJoinable());
            if (joinComp != 0) {
                return joinComp;
            }

            return (!a.haspassword).CompareTo(!b.haspassword);
        });
    }

    public static void ClearLists() {
        CurServerList_ButOnlySteamLobbies.Clear();
        CurServerList.Clear();
        CurFilteredServerList.Clear();
        TotalPlayerCount = 0;

        SearchPatternIsLobbyId = ulong.TryParse(_LOBBY_SEARCH_FILTER, out SearchPatternLobbyId);
        if (SearchPatternIsLobbyId) {
            SearchPatternIsLobbyId = ((CSteamID)SearchPatternLobbyId).IsLobby();
        }
    }

    public static void ReloadList() {
        _sortFlipAlpha = false;
        _sortFlipCount = false;
        _sortFlipLayer = false;
        _sortFlipPing = false;
        _manualSortActive = false;
        ResetPingSamplerFast();

        ClearLists();
        KSteam.ClearPendingBrowserLobbyData();

        if (KSteam.Loaded) {
            SteamNetworkingUtils.InitRelayNetworkAccess();
            KSteam.SearchLobbies(false);
        }

#if !RELEASE
        AddServerToTheList(new NetPublicServerInfo {
            name = "TEST DONT CLICK",
            description = "TEST DESCRIPTION ",
            plr_count = 67,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby()
        });
        AddServerToTheList(new NetPublicServerInfo {
            name = "1TEST <color=red>DONT</color> CLICK",
            description = Multiplayer.SanitizeRichTextAllowSimpleTags("Hello <b>bold</b> <i>italic</i> <u>underline</u> <s>strike</s> <color=red>red</color> <color=#FF0000>hex</color> <size=20>size</size> <size=999999>huge</size>"),
            plr_count = 13,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby { lobby_steamID = (CSteamID)1 }
        });
        AddServerToTheList(new NetPublicServerInfo {
            name = "2TEST DONT CLICK",
            description = Multiplayer.SanitizeRichTextAllowSimpleTags("TEST DESCRIPTION <size=500>WAY TOO BIG</size> <someDangerousTag>text</someDangerousTag>"),
            plr_count = 583,
            haspassword = true,
            version = Multiplayer.FULL_VERSION_TAG,
            steam_lobby_info = new Lobby { lobby_steamID = (CSteamID)2 }
        });
#endif
    }
}
