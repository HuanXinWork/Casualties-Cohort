using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机主菜单“关于与致谢”分页渲染器。
/// </summary>
public static partial class UIMainMenu {
    private static Vector2 _ABOUTMENUSCROLL = Vector2.zero;
    private static string _lastCopiedLink = "";

    private static void _GUI_RenderAboutMenu(Rect r) {
        _ABOUTMENUSCROLL = GUILayout.BeginScrollView(_ABOUTMENUSCROLL);
        int ogFontSize = GUI.skin.label.fontSize;
        TextAnchor ogAlign = GUI.skin.label.alignment;
        GUI.skin.label.wordWrap = false;

        try {
            GUILayout.Space(10f * GetMenuUIScale());

            _GUI__AboutMenuLinkButton(Lang.Get("mmta_og_download"), Constants.OG_DOWNLOAD_URL, r);
            _GUI__AboutMenuLinkButton(Lang.Get("mmta_discord"), Constants.OG_MOD_DISCORD_URL, r);

            GUI.skin.label.alignment = TextAnchor.UpperCenter;
            GUILayout.Label("");
            UIBullshit._GUI_BiggerLabel(Lang.Get("mmta_credits"), 1.2f);

            try {
                _GUI__ABOUTMENU_FULL_CREDITS(r);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIMainMenu._GUI__ABOUTMENU_FULL_CREDITS", ex);
            }

            GUILayout.Label("");
            UIBullshit._GUI_BiggerLabel(Lang.Get("mmta_libraries"), 1.2f);

            _GUI__AboutMenuLinkButton("LiteNetLib", "https://github.com/RevenantX/LiteNetLib", r);
            _GUI__AboutMenuLinkButton("LiteEntitySystem", "https://github.com/RevenantX/LiteEntitySystem", r);
            _GUI__AboutMenuLinkButton("Steamworks.NET", "https://github.com/rlabrecque/Steamworks.NET", r);
            _GUI__AboutMenuLinkButton("OpusSharp", "https://github.com/AvionBlock/OpusSharp", r);
            _GUI__AboutMenuLinkButton("BepInEx", "https://github.com/BepInEx/BepInEx", r);

            GUILayout.Label("");
        } finally {
            GUI.skin.label.fontSize = ogFontSize;
            GUI.skin.label.alignment = ogAlign;
            GUILayout.EndScrollView();
        }
    }

    private static void _GUI__AboutMenuLinkButton(in string name, in string link, in Rect r) {
        GUI.skin.label.alignment = TextAnchor.MiddleRight;
        GUILayout.BeginHorizontal();
        {
            GUILayout.Label(name, GUILayout.Width(r.width * 0.5f));
            if (GUILayout.Button(Lang.Get("mmta_link_open"))) {
                Application.OpenURL(link);
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            if (UIBullshit.CheckCursorOverlap(GUIUtility.GUIToScreenRect(GUILayoutUtility.GetLastRect()))) {
                UIBullshit._GUI_SetTooltip("", link);
            }
            if (GUILayout.Button(Lang.Get("mmta_link_copy"))) {
                GUIUtility.systemCopyBuffer = link;
                _lastCopiedLink = link;
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
            }
            if (_lastCopiedLink == link) {
                GUILayout.Label(Lang.Get("mmta_link_copied"));
            }
            GUILayout.FlexibleSpace();
        }
        GUILayout.EndHorizontal();
    }

    internal static (Texture2D, string)[] Credits = new (Texture2D, string)[] {
        (null, "Creature - MP Mod Developer"),
        (null, "JyanSei - Optimization assistance"),
        (null, "dannad - Auto-updater module, Installer tool, C:T ds Staff"),
        (null, "Gary the cat - playtester, Bug report module, past servers, C:T ds Staff"),
        (null, "Todd - external mod \"ChangeSkin\", C:T ds Staff"),
        (null, "ItsVoidSK - playtester, some sprites"),
        (null, "dawn - TimeManipulation patch"),
        (null, "VtLoader - Bug fixes"),
        (null, "Jilu-tin - playtester"),
        (null, "NyaruQwQ - playtester"),
        (null, "X0men0X - playtester"),
        (null, "[Cider] - playtester"),
        (null, "avr - playtester"),
        (null, "gordocats - self-heal sprite"),
        (null, "notamoron - playtester"),
        (null, "Devactor - playtester"),
        (null, "Dictator - playtester"),
        (null, "Мрак 2.0 - playtester"),
        (null, "Dmonya - playtester"),
        (null, "Shiro - playtester"),
        (null, "Charu - playtester"),
        (null, "kipish - playtester"),
        (null, "Zero Ares IV - Supporter"),
        (null, "EthanTheProtogen - Supporter"),
        (null, "RoyMartini - Supporter"),
        (null, "Jazun - Supporter"),
        (null, "Alexx__ - Supporter"),
        (null, "vega - Supporter"),
        (null, "Zmechol1337 - Supporter"),
        (null, "plasterjello - Supporter"),
        (null, "Doctor Callico - Supporter"),
        (null, "vivikat - Supporter"),
        (null, "Ariyah - Supporter"),
        (null, "therealbudget - Supporter"),
        (null, "_stuck_ - Supporter"),
        (null, "ShaoAZ - Supporter"),
        (null, "JameisonAus - Supporter"),
        (null, "Kidi - Supporter"),
        (null, "BluBerryToaster - Supporter"),
        (null, "Katomou - Supporter"),
        (null, "Nemean_Lion732 - Supporter"),
        (null, "Locko2265 - Supporter"),
        (null, "Trenchburner - Supporter"),
        (null, "TheBoathead - Supporter"),
        (null, "Diesel - Supporter"),
        (null, "Mitirich - Supporter"),
        (null, "Krokosha666 - OG MP Mod Developer, Retired"),
        (null, "Orsoniks - Developer of Casualties: Unknown"),
        (null, "...and the rest of bug reporters, suggestions and regular players"),
    };

    private static void _GUI__ABOUTMENU_FULL_CREDITS(Rect r) {
        foreach ((Texture2D icon, string text) in Credits) {
            GUILayout.BeginHorizontal();
            GUILayout.FlexibleSpace();
            Vector2 textSize = GUI.skin.label.CalcSize(FixGuiContent.Content(text, 23));
            if (icon != null) {
                GUILayout.Label(icon, GUILayout.Width(textSize.y), GUILayout.Height(textSize.y));
                GUILayout.Space(textSize.y * 0.1f);
            }
            GUILayout.Label(text);
            GUILayout.FlexibleSpace();
            GUILayout.EndHorizontal();
        }
    }
}
