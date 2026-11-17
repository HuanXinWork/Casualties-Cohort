using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using System;
using System.Reflection;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 声明式设置字段反射绑定特性基类（自动映射到 <see cref="SettingsJsonThing"/> 持久化键值）。
/// </summary>
[AttributeUsage(AttributeTargets.Field)]
public abstract class SettingDeclarerThingyBaseAttribute : Attribute {
    public string n;
    public object default_value;
    public bool allow_saving = true;

    public string GetPrefKey(FieldInfo f) {
        return "KrokoshaMultiplayer_Setting_" + f.DeclaringType + "_" + f.Name;
    }

    public virtual void Save(FieldInfo f, object v) {
        throw new NotImplementedException("SettingDeclarerThingyBaseAttribute.Save must be overridden by subclasses.");
    }

    public virtual void Get(FieldInfo f) {
        throw new NotImplementedException("SettingDeclarerThingyBaseAttribute.Get must be overridden by subclasses.");
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyFloatAttribute : SettingDeclarerThingyBaseAttribute {
    public float mi;
    public float ma;
    public string postfix = "";

    public SettingDeclarerThingyFloatAttribute(float min, float max, string name) {
        mi = min;
        ma = max;
        n = name;
    }

    public override void Save(FieldInfo f, object v) {
        float newVal = (float)v;
        if (allow_saving) {
            SettingsJsonThing.json.SetFloat(GetPrefKey(f), newVal);
        }
        f.SetValue(null, newVal);
    }

    public override void Get(FieldInfo f) {
        if (!allow_saving) {
            return;
        }
        f.SetValue(null, SettingsJsonThing.json.GetFloat(GetPrefKey(f), (float)default_value));
    }

    public string GetDisplayString(FieldInfo f) {
        SettingDeclarerThingyFloatAttribute attr = f.GetAttribute<SettingDeclarerThingyFloatAttribute>();
        float v = (float)f.GetValue(null);
        return v.ToString("0.0") + attr.postfix;
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyBoolAttribute : SettingDeclarerThingyBaseAttribute {
    public SettingDeclarerThingyBoolAttribute(string name) {
        n = name;
    }

    public override void Save(FieldInfo f, object v) {
        bool newVal = (bool)v;
        if (allow_saving) {
            SettingsJsonThing.json.SetBool(GetPrefKey(f), newVal);
        }
        f.SetValue(null, newVal);
    }

    public override void Get(FieldInfo f) {
        if (!allow_saving) {
            return;
        }
        f.SetValue(null, SettingsJsonThing.json.GetBool(GetPrefKey(f), (bool)default_value));
    }
}

[AttributeUsage(AttributeTargets.Field)]
public class SettingDeclarerThingyChoiceAttribute : SettingDeclarerThingyBaseAttribute {
    public string[] choices;

    public SettingDeclarerThingyChoiceAttribute(string name, string[] choices) {
        n = name;
        this.choices = choices;
    }

    public override void Save(FieldInfo f, object v) {
        int newVal = Convert.ToInt32(v);
        if (allow_saving) {
            SettingsJsonThing.json.SetInt(GetPrefKey(f), newVal);
        }
        f.SetValue(null, Convert.ChangeType(newVal, f.FieldType));
    }

    public override void Get(FieldInfo f) {
        if (!allow_saving) {
            return;
        }
        int saved = SettingsJsonThing.json.GetInt(GetPrefKey(f), Convert.ToInt32(default_value));
        f.SetValue(null, Convert.ChangeType(saved, f.FieldType));
    }
}

/// <summary>通用设置分页空宿主占位类（保留原有公共类型名供反射绑定）。</summary>
public static class Amogus {
}

/// <summary>
/// 轻量 IMGUI 下拉菜单控件：在顶层绘制展开项并拦截底层点击。
/// </summary>
public static class GUILayout_DropdownMenu {
    public static bool isOpen => activeID != -1;

    private static int activeID = -1;
    private static Rect activeRect;
    private static string[] activeOptions;
    private static int clickedID = -1;
    private static int clickedNumToReturn = 0;
    private static int id_counter = 0;
    private static readonly float dropdownitemheight = 35f;

    public static void CloseAll() {
        clickedID = -1;
        activeID = -1;
    }

    public static int Dropdown(int selectedIndex, string[] options, params GUILayoutOption[] uILayoutOptions) {
        id_counter++;
        if (selectedIndex > 0 && selectedIndex >= options.Length) {
            selectedIndex = options.Length - 1;
        }

        GUIContent content;
        float maxWidth = 0f;
        if (options.Length == 0) {
            selectedIndex = 0;
            content = FixGuiContent.Content("[NONE]", 24);
        } else {
            content = FixGuiContent.Content(options[selectedIndex], 25);
            foreach (string item in options) {
                Vector2 itemSize = GUI.skin.button.CalcSize(FixGuiContent.Content(item, 26));
                maxWidth = Mathf.Max(maxWidth, itemSize.x);
            }
        }
        maxWidth = Mathf.Max(maxWidth, GUI.skin.button.CalcSize(content).x);

        if (uILayoutOptions.Length == 0) {
            uILayoutOptions = uILayoutOptions.AddToArray(GUILayout.MinWidth(maxWidth));
        }

        Rect buttonRect = GUILayoutUtility.GetRect(content, GUI.skin.button, uILayoutOptions);
        int result = selectedIndex;

        bool wasClicked = clickedID == id_counter;
        if (wasClicked || activeID == id_counter) {
            if (wasClicked) {
                clickedID = -1;
                result = clickedNumToReturn;
            }

            GUI.color = new Color(0f, 0f, 0f, 0f);
            bool oldEnabled = GUI.enabled;
            GUI.enabled = false;
            GUI.Button(buttonRect, content);
            GUI.enabled = oldEnabled;
            GUI.color = Color.white;
            return result;
        }

        if (GUI.Button(buttonRect, content)) {
            clickedID = -1;
            if (activeID == id_counter) {
                activeID = -1;
            } else {
                activeID = id_counter;
                activeRect = GUIUtility.GUIToScreenRect(buttonRect);
                activeOptions = options;
            }
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        return result;
    }

    public static void Draw() {
        id_counter = 0;
        if (!isOpen || activeOptions == null || activeOptions.Length == 0) {
            return;
        }

        try {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);

            float itemHeight = dropdownitemheight * UIBullshit.uiScale;
            Rect dropdownRect = new Rect(
                activeRect.x,
                activeRect.y,
                activeRect.width,
                activeOptions.Length * itemHeight);

            for (int i = 0; i < activeOptions.Length; i++) {
                Rect r = new Rect(
                    dropdownRect.x,
                    dropdownRect.y + i * itemHeight,
                    dropdownRect.width,
                    itemHeight);

                if (GUI.Button(r, activeOptions[i])) {
                    clickedID = activeID;
                    clickedNumToReturn = i;
                    activeID = -1;
                    Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                }
            }

            if (Event.current.type == EventType.MouseDown
                && !dropdownRect.Contains(Event.current.mousePosition)
                && !activeRect.Contains(Event.current.mousePosition)) {
                activeID = -1;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "GUILayout_DropdownMenu.Draw", ex);
        } finally {
            UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
        }
    }
}
