using HarmonyLib;
using System;
using System.Collections;
using System.Collections.Generic;
using System.Globalization;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using TMPro;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace CasualtiesTogetherUtils;


public static class PlayerPrefsExtended {
    public static bool GetBool(string key, bool defaultValue = false) {
        return PlayerPrefs.GetInt(key, defaultValue.ToInt32()) != 0;
    }
    public static void SetBool(string key, bool value) {
        PlayerPrefs.SetInt(key, value.ToInt32());
    }
}


#region UTIL_PATCHES

[HarmonyPatch(typeof(Settings), nameof(Settings.DefaultSettings))]
internal static class Settings_DefaultSettings_Patch {
    public static Dictionary<string, KeyCode> custom_binds = new();
    static void Postfix(ref List<Setting> __result) {
        foreach (var keybind in custom_binds)
            if (!__result.Any(x => x.name == keybind.Key)) {
                __result.Add(new SettingKeybind {
                    name = keybind.Key,
                    value = keybind.Value,
                    apply = delegate {
                    },
                    category = Setting.SettingCategory.Input
                });
            }

    }
}
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Start))]
internal static class SettingsMenu_Start_Patch {
    public static bool forceOpenCustomTab;
    static bool Prefix(SettingsMenu __instance) {
        if (SettingsMenu_Start_Patch.forceOpenCustomTab) {
            SettingsMenu_Start_Patch.forceOpenCustomTab = false;
            return false;
        }
        return true;
    }
}
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.SelectTab), new Type[] { typeof(Setting.SettingCategory) })]
internal static class SettingsMenu_SelectTab_Patch {
    internal static int currentSelectedTab;
    static bool Prefix(SettingsMenu __instance, Setting.SettingCategory category) {
        currentSelectedTab = (int)category;
        return true;
    }
}

#endregion


public static class Util_UnityExtensions {


    public static TMP_Text SetAlpha(this TMP_Text img, float alpha) {
        if (img != null)
            img.color = img.color.WithAlpha(alpha);
        return img;
    }
    public static Image SetAlpha(this Image img, float alpha) {
        if (img != null) {
            img.color = img.color.WithAlpha(alpha);
        }
        return img;
    }

    public static Rect GetScreenRect(this RectTransform rt, Camera camera = null) {
        var corners = new Vector3[4];
        rt.GetWorldCorners(corners);

        var min = RectTransformUtility.WorldToScreenPoint(camera, corners[0]);
        var max = RectTransformUtility.WorldToScreenPoint(camera, corners[2]);

        return Rect.MinMaxRect(min.x, min.y, max.x, max.y);
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParent(this Transform t, Component p, bool worldPositionStays = true) => t.SetParent(p.transform, worldPositionStays);
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static void SetParent(this Transform t, GameObject p, bool worldPositionStays = true) => t.SetParent(p.transform, worldPositionStays);
    public static void DestroyGameObject(this UnityEngine.Object uo) {
        if (uo != null)
            GameObject.Destroy(uo.GameObject());
    }
    public static void DestroyGameObjectImmediate(this UnityEngine.Object uo) {
        if (uo != null)
            GameObject.DestroyImmediate(uo.GameObject());
    }

    public static Component GetOrAddComponent(this UnityEngine.Object uo, Type componentType) {
        var go = uo.GameObject();
        if (go == null)
            throw new NotSupportedException(uo.GetType().ToString());
        return go.GetComponent(componentType) ?? go.AddComponent(componentType);
    }

    public static Transform Transform(this UnityEngine.Object uo) {
        if (uo != null) {
            if (uo is GameObject)
                return ((GameObject)uo).transform;
            if (uo is Component)
                return ((Component)uo).transform;
        }
        return null;
    }
}

public static class Util_MiscSystemExtensions {

    public static void AddIfNotPresent<T>(this List<T> list, T value) {
        if (!list.Contains(value))
            list.Add(value);
    }

    public static T PickRandom<T>(this IEnumerable<T> source) {
        return source.ElementAt(UnityEngine.Random.Range(0, source.Count()));
    }

    public static float NextFloat(this System.Random r, float min, float max) {
        return (float)(r.NextDouble() * (max - min) + min);
    }
    public static bool IsSimpleType(this Type type) {
        return type.IsPrimitive
        || type == typeof(string);
    }

    public static int CountOccurences(this string text, string substring) {
        int c = 0;
        int i = 0;
        while ((i = text.IndexOf(substring, i)) != -1) {
            c++;
            i += substring.Length;
        }
        return c;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static int ToInt32(this bool b) {
        return b ? 1 : 0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static byte ToByte(this bool b) {
        return b ? (byte)1 : (byte)0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBool(this byte b) {
        return b != (byte)0;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool ToBool(this int i) {
        return i != 0;
    }


    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Clamp(this Vector2 v2, in Vector2 a, in Vector2 b) {
        return new Vector2(Math.Clamp(v2.x, a.x, b.x), Math.Clamp(v2.y, a.y, b.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Clamp(this Vector2 v2, in float a, in float b) {
        return new Vector2(Math.Clamp(v2.x, a, b), Math.Clamp(v2.y, a, b));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Abs(this Vector2 v2) {
        return new Vector2(Math.Abs(v2.x), Math.Abs(v2.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Max(this Vector2 v2, in Vector2 other) {
        return new Vector2(Math.Max(v2.x, other.x), Math.Max(v2.y, other.y));
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static Vector2 Min(this Vector2 v2, in Vector2 other) {
        return new Vector2(Math.Min(v2.x, other.x), Math.Min(v2.y, other.y));
    }


    public static (IEnumerable<T> True, IEnumerable<T> False) Partition<T>(this IEnumerable<T> source, Func<T, bool> predicate) {
        var trueList = new List<T>();
        var falseList = new List<T>();

        foreach (var item in source) {
            if (predicate(item))
                trueList.Add(item);
            else
                falseList.Add(item);
        }

        return (trueList, falseList);
    }

    public static byte[] ToBytes(this BitArray bits) {
        int numBytes = (bits.Length + 7) / 8;
        byte[] bytes = new byte[numBytes];
        bits.CopyTo(bytes, 0);
        return bytes;
    }

    public static string ToStringInvariant(this object obj) {
        string aaa = null;
        var prev = CultureInfo.CurrentCulture;
        try {
            CultureInfo.CurrentCulture = CultureInfo.InvariantCulture;

            aaa = obj.ToString();
        } finally {
            CultureInfo.CurrentCulture = prev;
        }
        return aaa;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool IsInRange(this RangeF r, in float number) {
        return number >= r.min && number <= r.max;
    }
    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static float Center(this RangeF r) {
        return Mathf.LerpUnclamped(r.min, r.max, 0.5f);
    }
    public static RangeF Widen(this RangeF r, in float number) {
        var r1 = new RangeF(r.min, r.max);
        r1.min -= number;
        r1.max += number;
        return r1;
    }
    public static Rect ShrinkBorder(this Rect r, in float number) {
        var reeeee = new Rect(r);
        var n = Vector2.one * number;
        reeeee.min += n;
        reeeee.max -= n;
        return reeeee;
    }

    public static int StableHash(this string s) { // ai generated
        if (s == null)
            return 0;

        unchecked {
            const int fnvPrime = 16777619;
            int hash = (int)2166136261;

            byte[] bytes = Encoding.UTF8.GetBytes(s);

            foreach (byte b in bytes) {
                hash ^= b;
                hash *= fnvPrime;
            }
            return hash;
        }
    }
    public static byte[] GetUTF8Bytes(this string s) {
        return Encoding.UTF8.GetBytes(s);
    }
    public static string ToStringFromUTF8Bytes(this byte[] b) {
        return Encoding.UTF8.GetString(b);
    }
    public static byte[] ToByteArray(this string s) {
        var arr = new byte[s.Length];
        for (int i = 0; i < s.Length; i++) {
            arr[i] = (byte)s[i];
        }
        return arr;
    }
    public static string ToStringFromCharBytes(this byte[] b) {
        var arr = new StringBuilder(b.Length);
        foreach (var c in b) {
            arr.Append((char)c);
        }
        return arr.ToString();
    }


    // ai generated
    public static void ShuffleRandom<T>(this IList<T> array) {
        for (int i = array.Count - 1; i > 0; i--) {
            int randomIndex = UnityEngine.Random.Range(0, i + 1);

            // swap
            T temp = array[i];
            array[i] = array[randomIndex];
            array[randomIndex] = temp;
        }
    }

    public static bool TryTrimStart(this string source, string value, out string trimmed) {
        if (source.StartsWith(value)) {
            trimmed = source.Substring(value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }
    public static bool TryTrimStart(this string source, string value, out string trimmed, bool ignoreCase) {
        if (source.StartsWith(value, ignoreCase, CultureInfo.InvariantCulture)) {
            trimmed = source.Substring(value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }
    public static bool TryTrimEnd(this string source, string value, out string trimmed, bool ignoreCase) {
        if (source.EndsWith(value, ignoreCase, CultureInfo.InvariantCulture)) {
            trimmed = source.Substring(0, source.Length - value.Length);
            return true;
        }
        trimmed = source;
        return false;
    }

    public static Texture2D Clone(this Texture2D s) {
        var t = new Texture2D(s.width, s.height, s.format, s.mipmapCount > 1);
        t.SetPixels(s.GetPixels());
        t.Apply();
        return t;
    }
    public static Sprite Clone(this Sprite spr) {
        var t = spr.texture.Clone();
        return Sprite.Create(t, spr.rect, spr.pivot, spr.pixelsPerUnit);
    }
    public static void TextureBW(this Texture2D t) {
        var p = t.GetPixels();
        for (int i = 0; i < p.Length; i++) {
            p[i] = p[i].ColorBW();
        }
        t.SetPixels(p);
    }
    public static Color ColorBW(this Color t) {
        var avg = Mathf.Max(t.r, t.g, t.b);
        return new Color(avg, avg, avg, t.a);
    }

}

public static class Util_MiscCUExtensions {

    public static Dictionary<int, List<string>> GetArgAutofill(this Command com) {
        if (com.argAutofill == null) {
            com.argAutofill = new();
        }
        return com.argAutofill;
    }
    public static List<string> GetArgAutofillList(this Command com, int arg) {
        if (com.argAutofill == null) {
            com.argAutofill = new();
        }
        if (!com.argAutofill.TryGetValue(arg, out var result)) {
            result = new();
            com.argAutofill[arg] = result;
        }
        return result;
    }
    public static Vector2 GetPosition(this DamagingCrate crate) {
        return crate.transform.position;
    }
    public static Vector2 GetPosition(this Item item) {
        return item.transform.position;
    }

    public static T GetOrAddComponent<T>(this CrystalEffect ce) where T : Component {
        var uo = ce.crystal;
        return uo.GetComponent<T>() ?? uo.AddComponent<T>();
    }

}
