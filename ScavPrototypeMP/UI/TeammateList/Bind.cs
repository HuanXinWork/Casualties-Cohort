using BepInEx.Configuration;
using HarmonyLib;
using System.Collections.Generic;
using Together.Diagnostics;
using UnityEngine;

namespace Together.UI.TeammateList {
    /// <summary>
    /// 将队友列表折叠快捷键注入原版输入设置列表，并与 BepInEx <see cref="KeyboardShortcut"/> 配置双向同步。
    /// </summary>
    internal static class Bind {
        public const string Id = "teammatelist";

        [HarmonyPatch(typeof(Settings), nameof(Settings.DefaultSettings))]
        private static class DefaultSettingsPatch {
            private static void Postfix(List<Setting> __result) {
                Add(__result, applyNow: false);
            }
        }

        [HarmonyPatch(typeof(Settings), nameof(Settings.LoadSettings))]
        private static class LoadSettingsPatch {
            private static KeyboardShortcut _before;
            private static bool _haveBefore;

            private static void Prefix() {
                ConfigEntry<KeyboardShortcut> entry = TeammateListHost.CollapseKey;
                if (entry == null) {
                    return;
                }
                _before = entry.Value;
                _haveBefore = true;
            }

            private static void Postfix() {
                if (!_haveBefore) {
                    return;
                }
                _haveBefore = false;
                KeepConfig(_before);
            }
        }

        [HarmonyPatch(typeof(Settings), nameof(Settings.GetAllSettings))]
        private static class ReadSettingsPatch {
            private static void Postfix(List<Setting> __result) {
                Add(__result, applyNow: true);
            }
        }

        [HarmonyPatch(typeof(Locale), nameof(Locale.GetOther))]
        private static class LabelPatch {
            private static void Postfix(string str, ref string __result) {
                if (str == "gameset" + Id) {
                    __result = "Teammate list";
                }
            }
        }

        public static void EnsureRow() {
            Add(Settings.settings, applyNow: true);
        }

        public static string HideText() {
            return "Hide: " + ShortcutText(Current());
        }

        public static string OpenText(int digit) {
            return "Shift+" + digit;
        }

        public static bool Pressed() {
            ConfigEntry<KeyboardShortcut> entry = TeammateListHost.CollapseKey;
            KeyboardShortcut key = entry != null ? entry.Value : new KeyboardShortcut(KeyCode.LeftAlt);
            if (key.MainKey == KeyCode.None) {
                key = new KeyboardShortcut(KeyCode.LeftAlt);
            }
            if (!Input.GetKeyDown(key.MainKey)) {
                return false;
            }
            if (key.Modifiers == null) {
                return true;
            }
            foreach (KeyCode modifier in key.Modifiers) {
                if (!Input.GetKey(modifier)) {
                    return false;
                }
            }
            return true;
        }

        private static void Add(List<Setting> list, bool applyNow) {
            if (list == null) {
                return;
            }
            for (int i = 0; i < list.Count; i++) {
                Setting existing = list[i];
                if (existing != null && existing.name == Id) {
                    return;
                }
            }

            SettingKeybind row = new SettingKeybind {
                name = Id,
                value = ShownKey(),
                category = Setting.SettingCategory.Input
            };
            row.apply = delegate { Push(row); };
            list.Add(row);
            if (applyNow) {
                KeyBinds.CacheBind(row);
            }
            ModLog.Debug("Input row 'Teammate list' registered", LogDomain.UI);
        }

        private static KeyboardShortcut Current() {
            ConfigEntry<KeyboardShortcut> entry = TeammateListHost.CollapseKey;
            if (entry == null || entry.Value.MainKey == KeyCode.None) {
                return new KeyboardShortcut(KeyCode.LeftAlt);
            }
            return entry.Value;
        }

        private static string ShortcutText(KeyboardShortcut key) {
            string main = KeyText(key.MainKey);
            if (key.Modifiers == null) {
                return main;
            }
            string mods = "";
            foreach (KeyCode modifier in key.Modifiers) {
                if (modifier == KeyCode.None) {
                    continue;
                }
                if (mods.Length > 0) {
                    mods += "+";
                }
                mods += KeyText(modifier);
            }
            return mods.Length == 0 ? main : (mods + "+" + main);
        }

        private static string KeyText(KeyCode key) {
            if (key >= KeyCode.Alpha0 && key <= KeyCode.Alpha9) {
                return ((int)(key - KeyCode.Alpha0)).ToString();
            }
            if (key >= KeyCode.Keypad0 && key <= KeyCode.Keypad9) {
                return "Num" + (int)(key - KeyCode.Keypad0);
            }
            return key switch {
                KeyCode.LeftShift => "LShift",
                KeyCode.RightShift => "RShift",
                KeyCode.LeftAlt => "LAlt",
                KeyCode.RightAlt => "RAlt",
                KeyCode.LeftControl => "LCtrl",
                KeyCode.RightControl => "RCtrl",
                KeyCode.Mouse0 => "LMB",
                KeyCode.Mouse1 => "RMB",
                KeyCode.Mouse2 => "MMB",
                _ => key.ToString(),
            };
        }

        private static KeyCode ShownKey() {
            ConfigEntry<KeyboardShortcut> entry = TeammateListHost.CollapseKey;
            if (entry == null || entry.Value.MainKey == KeyCode.None) {
                return KeyCode.LeftAlt;
            }
            return entry.Value.MainKey;
        }

        private static void Push(SettingKeybind row) {
            ConfigEntry<KeyboardShortcut> entry = TeammateListHost.CollapseKey;
            if (entry == null || row == null) {
                return;
            }
            KeyCode key = row.value == KeyCode.None ? KeyCode.LeftAlt : row.value;
            if (entry.Value.MainKey == key && ModifierCount(entry.Value) == 0) {
                return;
            }
            entry.Value = new KeyboardShortcut(key);
        }

        private static void KeepConfig(KeyboardShortcut configured) {
            SettingKeybind row = Find(Settings.settings);
            if (row == null) {
                return;
            }
            bool untouched = configured.MainKey == KeyCode.LeftAlt && ModifierCount(configured) == 0;
            if (untouched && row.value != KeyCode.LeftAlt && row.value != KeyCode.None) {
                TeammateListHost.CollapseKey.Value = new KeyboardShortcut(row.value);
                return;
            }

            if (TeammateListHost.CollapseKey.Value.MainKey != configured.MainKey
                || ModifierCount(TeammateListHost.CollapseKey.Value) != ModifierCount(configured)) {
                TeammateListHost.CollapseKey.Value = configured;
            }
            KeyCode show = configured.MainKey == KeyCode.None ? KeyCode.LeftAlt : configured.MainKey;
            row.value = show;
            KeyBinds.CacheBind(row);
        }

        private static int ModifierCount(KeyboardShortcut key) {
            if (key.Modifiers == null) {
                return 0;
            }
            int count = 0;
            foreach (KeyCode modifier in key.Modifiers) {
                if (modifier != KeyCode.None) {
                    count++;
                }
            }
            return count;
        }

        private static SettingKeybind Find(List<Setting> list) {
            if (list == null) {
                return null;
            }
            for (int i = 0; i < list.Count; i++) {
                if (list[i] is SettingKeybind row && row.name == Id) {
                    return row;
                }
            }
            return null;
        }
    }
}
