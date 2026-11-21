using System;
using System.Collections;
using System.IO;
using System.Reflection;
using System.Text;
using TMPro;
using UnityEngine;

namespace Together;

public static class FontUtils {
    public static string ReplaceMissingCharacters(TMP_FontAsset font, string input) {
        if (font == null || string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new StringBuilder(input.Length);

        foreach (char c in input) {
            // 必须显式传入 tryAddCharacter: true，触发 AtlasPopulationMode.Dynamic 的 OS 动态回退 TMP_FontAsset 按需烘焙首次出现的 CJK/西里尔字形
            if (font.HasCharacter(c, true, true))
                result.Append(c);
            else
                result.Append('?');
        }

        return result.ToString();
    }

    public static string ReplaceMissingCharacters(Font font, string input) {
        if (font == null || string.IsNullOrEmpty(input))
            return input;

        StringBuilder result = new StringBuilder(input.Length);

        font.RequestCharactersInTexture(input, font.fontSize, FontStyle.Normal);

        foreach (char c in input) {
            CharacterInfo info;
            if (font.GetCharacterInfo(c, out info, font.fontSize)) {
                result.Append(c);
            } else {
                result.Append('?');
            }
        }

        return result.ToString();
    }

    public static bool HasCharacter(Font font, char c) {
        if (font == null) return false;

        CharacterInfo info;
        font.RequestCharactersInTexture(c.ToString(), font.fontSize, FontStyle.Normal);

        return font.GetCharacterInfo(c, out info, font.fontSize);
    }
}
