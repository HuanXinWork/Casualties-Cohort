using CasualtiesTogetherUtils;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 联机主菜单游戏规则分类浏览器、预设存取、搜索过滤与单项重置渲染器。
/// </summary>
public static partial class UIMainMenu {
    private static string _GUI____RenderRuleField_search = "";
    private const string LastRulePresetKey = "LastRulePreset";

    internal static int _GUI____RenderRuleField_PresetChosen = 0;
    internal static string _GUI____RenderRuleField_PresetName = "";
    private static float _GUI____RenderRuleField_last_max_width = 300f;
    private static float _GUI____RenderRuleField_cur_max_width = 0f;

    private static Vector2 _SCROLL_FULLRULELIST = Vector2.zero;

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
            if (GUILayout.Button(Lang.Get("mmct_rules_expand_all", true) ?? "Expand all", GUILayout.ExpandWidth(false))) {
                RuleCategories.SetAllExpanded(true);
            }
            if (GUILayout.Button(Lang.Get("mmct_rules_collapse_all", true) ?? "Collapse all", GUILayout.ExpandWidth(false))) {
                RuleCategories.SetAllExpanded(false);
            }
            GUILayout.FlexibleSpace();
            GUILayout.Label(Lang.Get("mmct_search"));
            _GUI____RenderRuleField_search = GUILayout.TextField(_GUI____RenderRuleField_search, GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.3f));
        }
        GUILayout.EndHorizontal();

        _SCROLL_FULLRULELIST = GUILayout.BeginScrollView(_SCROLL_FULLRULELIST);
        try {
            List<FieldInfo> ruleFields = typeof(MultiplayerGameRules).GetFields(BindingFlags.Instance | BindingFlags.Public).ToList();
            bool searching = !string.IsNullOrWhiteSpace(_GUI____RenderRuleField_search);
            var groups = RuleCategories.Group(ruleFields);
            if (searching) {
                groups = groups.Select(g => (g.category, fields: g.category.Title.ContainsInsensitive(_GUI____RenderRuleField_search)
                    ? g.fields
                    : g.fields.Where(f => f.Name.ContainsInsensitive(_GUI____RenderRuleField_search)
                        || (Lang.Get("rule_name_" + f.Name, true) ?? f.Name).ContainsInsensitive(_GUI____RenderRuleField_search)).ToList()))
                    .Where(g => g.fields.Count > 0)
                    .ToList();
            }

            if (groups.Count == 0) {
                GUI.skin.label.alignment = TextAnchor.UpperCenter;
                GUILayout.Label(Lang.Get("filter_found_nothing"));
            } else {
                _GUI____RenderRuleField_cur_max_width = 0f;
                foreach (var (category, fields) in groups) {
                    bool open = searching || RuleCategories.IsExpanded(category);
                    int changed = fields.Count(f => !RuleCategories.IsDefault(f, f.GetValue(Multiplayer.rules)));
                    string title = $"{(open ? "[-]" : "[+]")} {category.Title} ({fields.Count}"
                        + (changed > 0 ? $", {Lang.Get("mmct_rules_changed", true) ?? "changed"}: {changed})" : ")");
                    GUI.skin.label.alignment = TextAnchor.MiddleLeft;
                    if (GUILayout.Button(title) && !searching) {
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                        RuleCategories.SetExpanded(category, !open);
                    }
                    if (!open) {
                        continue;
                    }
                    foreach (FieldInfo fld in fields) {
                        GUILayout.BeginHorizontal();
                        try {
                            _GUI____RenderRuleField(fld, canEdit, r2);
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.UI, $"UIMainMenu.RenderRuleField({fld.Name})", ex);
                        } finally {
                            GUILayout.EndHorizontal();
                        }
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
                bool newValue = GUILayout.Toggle((bool)ogValue, "", GUILayout.ExpandWidth(false));
                fld.SetValueDirect(rulesRef, newValue);
                GUILayout.FlexibleSpace();
                GUILayout.Label(Lang.Get(newValue ? "mmct_rules_on" : "mmct_rules_off", true) ?? (newValue ? "on" : "off"), GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.1f));
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

            bool atDefault = RuleCategories.IsDefault(fld, fld.GetValue(Multiplayer.rules));
            bool ogEnabledReset = GUI.enabled;
            GUI.enabled = ogEnabledReset && !atDefault;
            if (GUILayout.Button(Lang.Get("mmct_rules_reset_one", true) ?? "R", GUILayout.ExpandWidth(false), GUILayout.Width(r2.width * 0.06f))) {
                fld.SetValueDirect(rulesRef, RuleCategories.DefaultValue(fld));
                didChange = true;
            }
            GUI.enabled = ogEnabledReset;

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
}
