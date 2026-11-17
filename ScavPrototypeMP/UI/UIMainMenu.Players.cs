using CasualtiesTogetherUtils;
using KrokV5Optimization;
using Steamworks;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机主菜单连接页左侧详细玩家列表、房主踢人/封禁/禁言管理、名牌改色与单人语音音量调节面板。
/// </summary>
public static partial class UIMainMenu {
    private static readonly string[] _CONNECTIONSECONDPANEL_TOOLBAR_STRS = new string[] {
        "Players",
        "Rules"
    };

    internal static int _CONNECTIONSECONDPANEL_TOOLBAR = 1;

    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = false;
    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton = false;
    public static bool ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben = false;

    private static Vector2 _SCROLL_FULLPLRLIST = Vector2.zero;
    private static ScavPlayer _COLOR_EDITING = null;
    private static string _COLOR_EDITING_STR = "FFFFFF";

    private static void _GUI__ConnectionSecondPanel(Rect r2) {
        int ogFontSize = GUI.skin.label.fontSize;
        TextAnchor ogLabelAlign = GUI.skin.label.alignment;
        try {
            GUI.skin.label.fontSize = (int)(ogFontSize * 1.4f);
            GUI.skin.label.alignment = TextAnchor.UpperCenter;

            _CONNECTIONSECONDPANEL_TOOLBAR_STRS[0] = Lang.Get("mmct_plrs");
            _CONNECTIONSECONDPANEL_TOOLBAR_STRS[1] = Lang.Get("mmct_rules");

            _CONNECTIONSECONDPANEL_TOOLBAR = GUILayout.Toolbar(_CONNECTIONSECONDPANEL_TOOLBAR, _CONNECTIONSECONDPANEL_TOOLBAR_STRS);

            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.alignment = TextAnchor.UpperLeft;

            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
            _GUI__ConnectionSecondPanel_ContentRender(r2);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI__ConnectionSecondPanel", ex);
        } finally {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
            GUI.skin.label.alignment = ogLabelAlign;
            GUI.skin.label.fontSize = ogFontSize;
        }
    }

    private static void _GUI___ConnectionSecondPanel_ContentRender_Top(Rect r) {
        GUILayout.BeginHorizontal();
        {
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben && KSteam.IS_IN_LOBBY) {
                if (GUILayout.Button(Lang.Get("mmct_hoststeamprofile"))) {
                    SteamFriends.ActivateGameOverlayToUser("steamid", (CSteamID)KSteam.CURRENT_LOBBY.ownerID);
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                }
                GUILayout.FlexibleSpace();
            }
            if (Con.CanExecuteAdminCommands()) {
                ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety = GUILayout.Toggle(
                    ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety,
                    Lang.Get("mmct_bankicksafety"));
            }
        }
        GUILayout.EndHorizontal();
    }

    private static void _GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList(Rect r) {
        if (!Net.IsRunning) {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUILayout.Label(Lang.Get("mmct_offline"));
        } else if (ScavPlayer.ClientIdToPlayerDict.Count > 0) {
            bool ogEnabled = GUI.enabled;
            foreach (ScavPlayer plr in new List<ScavPlayer>(ScavPlayer.AllPlayers)) {
                GUI.skin.label.alignment = TextAnchor.UpperLeft;
                GUILayout.BeginVertical(GUI.skin.box);
                try {
                    _GUI___DrawPlayerInList(r, plr);
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, $"UIMainMenu.RenderDetailedPlayer({plr})", ex);
                } finally {
                    GUI.enabled = ogEnabled;
                    GUILayout.EndVertical();
                }
            }
        } else {
            GUI.skin.label.alignment = TextAnchor.MiddleCenter;
            GUILayout.Label(Lang.Get("mmct_nobody"));
        }
    }

    private static void _GUI__ConnectionSecondPanel_ContentRender(Rect r2) {
        int ogFontSize = GUI.skin.label.fontSize;
        int ogFontSizeButton = GUI.skin.button.fontSize;

        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.skin.label.fontSize = (int)(ogFontSize * 0.85f);
        GUI.skin.button.fontSize = GUI.skin.label.fontSize;

        try {
            if (_CONNECTIONSECONDPANEL_TOOLBAR == 0) {
                GUI.skin.label.wordWrap = false;
                if (Net.IsRunning) {
                    _GUI___ConnectionSecondPanel_ContentRender_Top(r2);
                }

                _SCROLL_FULLPLRLIST = GUILayout.BeginScrollView(_SCROLL_FULLPLRLIST);
                try {
                    _GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList(r2);
                } finally {
                    GUILayout.EndScrollView();
                }
            } else if (_CONNECTIONSECONDPANEL_TOOLBAR == 1) {
                _GUI___ConnectionSecondPanel_ContentRender_RenderRulesList(r2);
            }
        } finally {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.button.fontSize = ogFontSizeButton;
        }
    }

    private static void _GUI___DrawPlayerInList(Rect r, ScavPlayer plr) {
        GUI.skin.label.alignment = TextAnchor.UpperLeft;
        GUI.color = Color.white;

        bool ogEnabled = GUI.enabled;
        Vector2 textSize = GUI.skin.label.CalcSize(FixGuiContent.Content(plr.playerName, 19));

        GUILayout.BeginHorizontal();
        {
            if (plr.IsHost) {
                GUILayout.Label(CoopModAssets.icon_crown.texture, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
            }
            foreach (Sprite item in plr.KnownUserTagIcons) {
                GUILayout.Label(item.texture, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
            }

            Texture2D avatar = plr.IconAnyLargeToSmall;
            if (avatar != null) {
                GUILayout.Label(avatar, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
            }

            GUI.color = plr.playerColor;
            GUILayout.Label(plr.playerName, GUILayout.Width(Mathf.Min(textSize.x, r.width * 0.5f)));
            GUI.color = Color.white;

            GUILayout.FlexibleSpace();

            if (!plr.IsLocal && Con.CanExecuteAdminCommands()) {
                if (GUI.enabled) {
                    GUI.enabled = ____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_ban_kick_safety;
                }

                if (GUILayout.Button(Lang.Get("mmct_plr_kick"))) {
                    Util.DelayCallLambda(0.001f, () => Con.ExecuteCommandAdminNoLog("kick id:" + plr.playerId));
                }
                if (GUILayout.Button(Lang.Get("mmct_plr_ban"))) {
                    Util.DelayCallLambda(0.001f, () => Con.ExecuteCommandAdminNoLog("ban id:" + plr.playerId));
                }
                if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_showLitenetLibBanButton
                    && GUILayout.Button(Lang.Get("mmct_plr_ipban"))) {
                    Util.DelayCallLambda(0.001f, () => Con.ExecuteCommandAdminNoLog("ipban id:" + plr.playerId));
                }

                GUI.enabled = ogEnabled;
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        {
            if (____GUI___ConnectionSecondPanel_ContentRender_RenderDetailedPlayerList_gaben
                && GUILayout.Button(Lang.Get("mmct_plr_steamprofile"))) {
                SteamFriends.ActivateGameOverlayToUser("steamid", (CSteamID)plr.SteamId);
            }

            GUILayout.Space(5f * GetMenuUIScale());

            if (Con.CanExecuteAdminCommands()) {
                if (_COLOR_EDITING == plr) {
                    Vector2 hexBoxSize = GUI.skin.label.CalcSize(FixGuiContent.Content("#FFFFFF", 20));
                    Color24 color = Color24.TryParseHex(_COLOR_EDITING_STR, out Color24 parsed) ? parsed : plr.playerColor;
                    GUI.color = color;

                    _COLOR_EDITING_STR = GUILayout.TextField(_COLOR_EDITING_STR, 7, GUILayout.Width(hexBoxSize.x));
                    if (_COLOR_EDITING_STR.Length > 6 && !_COLOR_EDITING_STR.StartsWith('#')) {
                        _COLOR_EDITING_STR = _COLOR_EDITING_STR.Substring(0, 6);
                    }
                    GUI.color = Color.white;

                    if (GUILayout.Button(Lang.Get("mm_apply"))) {
                        if (plr.playerColor != color) {
                            string cmd = $"setplrcolor id:{plr.playerId} {color.ToHex(true)}";
                            ModLog.Debug(cmd, LogDomain.UI);
                            Con.ExecuteCommandAdminNoLog(cmd);
                        }
                        _COLOR_EDITING = null;
                    }
                } else {
                    GUI.color = plr.playerColor;
                    if (GUILayout.Button(Lang.Get("mmct_plr_setcolor"))) {
                        _COLOR_EDITING_STR = plr.playerColor.ToHex(false);
                        _COLOR_EDITING = plr;
                    }
                    GUI.color = Color.white;
                }
            }
            GUILayout.FlexibleSpace();

            GUIContent muteLabelContent = FixGuiContent.Content(Lang.Get("mmct_plr_mutelabel"), 21);
            Vector2 muteLabelSize = GUI.skin.label.CalcSize(muteLabelContent);

            if (!plr.IsLocal && Con.CanExecuteAdminCommands()) {
                GUILayout.Label(muteLabelContent);
                if (GUILayout.Button(CoopModAssets.voicechaticon.texture, GUILayout.Width(muteLabelSize.y), GUILayout.Height(muteLabelSize.y))) {
                    Util.DelayCallLambda(0.001f, () => Con.ExecuteCommandAdminNoLog("servermute vc id:" + plr.playerId));
                }
                if (plr.server_mute_vc) {
                    Rect crossRect = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(crossRect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1f, Color.red, 0f, 0f);
                }

                if (GUILayout.Button(CoopModAssets.LoadSprite("speechbubble3.png").texture, GUILayout.Width(muteLabelSize.y), GUILayout.Height(muteLabelSize.y))) {
                    Util.DelayCallLambda(0.001f, () => Con.ExecuteCommandAdminNoLog("servermute tc id:" + plr.playerId));
                }
                if (plr.server_mute_tc) {
                    Rect crossRect = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(crossRect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1f, Color.red, 0f, 0f);
                }

                GUI.enabled = ogEnabled;
            } else if (plr.server_mute_vc || plr.server_mute_tc) {
                GUILayout.FlexibleSpace();
                GUILayout.Label(muteLabelContent);
                GUILayout.Label(CoopModAssets.voicechaticon.texture, GUILayout.Width(muteLabelSize.y), GUILayout.Height(muteLabelSize.y));
                if (plr.server_mute_vc) {
                    Rect crossRect = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(crossRect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1f, Color.red, 0f, 0f);
                }
                GUILayout.Label(CoopModAssets.LoadSprite("speechbubble3.png").texture, GUILayout.Width(muteLabelSize.y), GUILayout.Height(muteLabelSize.y));
                if (plr.server_mute_tc) {
                    Rect crossRect = GUILayoutUtility.GetLastRect();
                    GUI.DrawTexture(crossRect, CoopModAssets.cross.texture, ScaleMode.ScaleToFit, true, 1f, Color.red, 0f, 0f);
                }
                GUILayout.FlexibleSpace();
            }

            if (!plr.IsLocal && plr.vc_output != null) {
                GUILayout.Space(5f * GetMenuUIScale());
                GUI.skin.label.alignment = TextAnchor.UpperRight;
                GUILayout.Label(Lang.Get("mmct_plr_vc_vol"));
                plr.vc_output.custom_volume_set = GUILayout.HorizontalSlider(plr.vc_output.custom_volume_set, 0f, 2f, GUILayout.MaxWidth(r.width * 0.4f));
            }
        }
        GUILayout.EndHorizontal();

        GUILayout.BeginHorizontal();
        {
            GUI.skin.label.alignment = TextAnchor.UpperLeft;
            GUILayout.Label("ID: " + plr.playerId);
            GUILayout.Space(2f * GetMenuUIScale());
            GUILayout.Label(Lang.Get("mmct_plr_ping") + " " + plr.ping_as_ms);

            if (plr.ServerPlayerState != null) {
                GUILayout.Label($" TPS:{plr.ServerPlayerState.tps,3} FPS:{plr.ServerPlayerState.fps,3}");
            }
        }
        GUILayout.EndHorizontal();
    }
}
