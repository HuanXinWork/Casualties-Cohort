using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using TMPro;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 拦截原版设置菜单“恢复默认”按钮：当处于联机专属设置标签页时仅重置联机设置项。
/// </summary>
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.ResetToDefault))]
internal static class SettingsMenu_ResetToDefault_MultiplayerPatch {
    private static bool Prefix(SettingsMenu __instance) {
        if ((int)Util.GetSettingsPanelOpenedCategory() == SettingsMenu_Start_MultiplayerPatch.multiplayerSettingsMenuTabIndex) {
            foreach (UIMainMenu.SettingsTab settingsTab in UIMainMenu.SettingsTab.tabs) {
                if (settingsTab?.settings_origin != null) {
                    foreach (FieldInfo item in UIMainMenu.GetSettingsFromStaticClass(settingsTab.settings_origin)) {
                        SettingDeclarerThingyBaseAttribute attr = item.GetAttribute<SettingDeclarerThingyBaseAttribute>();
                        attr.Save(item, attr.default_value);
                    }
                }
                settingsTab?.on_reset?.Invoke();
                SettingsMenu_Start_MultiplayerPatch.OnMultiplayerTabClick();
            }
            return false;
        }
        return true;
    }
}

/// <summary>
/// 在原版 uGUI <see cref="SettingsMenu"/> 顶栏动态注入“Multiplayer”设置页签并生成反射绑定控件。
/// </summary>
[HarmonyPatch(typeof(SettingsMenu), nameof(SettingsMenu.Start))]
public static class SettingsMenu_Start_MultiplayerPatch {
    public static List<GameObject> spawnedSettings => SettingsMenu.instance.spawnedSettings;
    public static RectTransform content => SettingsMenu.instance.content;

    internal static int multiplayerSettingsMenuTabIndex = 100;
    private static Button multiplayerTabButton;
    private static RectTransform VCVizRectTransform;
    internal static Delegate bugreportFunctionCreator;
    internal static GameObject DeactivateButton;
    public static GameObject UpdateButton;
    public static TextMeshProUGUI UpdateStatusText;
    public static float contentHeight;

    private static void Postfix(SettingsMenu __instance) {
        Button template = __instance.buttons.LastOrDefault();
        if (template == null) {
            return;
        }

        RectTransform rt = template.transform as RectTransform;
        Vector2 lastPos = rt.anchoredPosition;
        float sizeReduce = (rt.sizeDelta.x + 3f) / (__instance.buttons.Count() + 1);
        Vector2 deltaVec = new Vector2(sizeReduce, 0f);

        for (int i = 0; i < __instance.buttons.Count(); i++) {
            RectTransform brt = __instance.buttons[i].transform as RectTransform;
            brt.sizeDelta -= deltaVec;
            brt.anchoredPosition -= deltaVec * (i + 0.5f);
        }

        Button inst = GameObject.Instantiate(template, template.transform.parent, false);
        inst.name = $"{MyPluginInfo.PLUGIN_GUID}_SettingsTab";
        multiplayerTabButton = inst;

        Button newButton = inst.GetComponent<Button>();
        RectTransform buttonRect = newButton.transform as RectTransform;
        __instance.buttons.Add(inst);
        multiplayerSettingsMenuTabIndex = __instance.buttons.Count();
        newButton.onClick.RemoveAllListeners();
        buttonRect.anchoredPosition = lastPos + deltaVec * 0.5f;

        TextMeshProUGUI label = newButton.GetComponentInChildren<TextMeshProUGUI>(true);
        UILocalizer uiLocalizer = newButton.GetComponentInChildren<UILocalizer>(true);
        if (label != null) {
            label.text = Lang.Get("multiplayer");
        }
        if (uiLocalizer != null) {
            UILocalizer.Destroy(uiLocalizer);
        }

        newButton.onClick.AddListener(OnMultiplayerTabClick);
    }

    public static void OnMultiplayerTabClick() {
        SettingsMenu.instance.SelectTab(multiplayerSettingsMenuTabIndex);
        BuildMPSettingsUI();
        if (multiplayerTabButton != null) {
            multiplayerTabButton.GetComponent<Image>().sprite = SettingsMenu.instance.buttonOpen;
        }
    }

    private static void BuildMPSettingsUI() {
        contentHeight = 0f;
        ref float num = ref contentHeight;

        GameObject handler = new GameObject("MpSetHandler", typeof(MultiplayerSettingsHandler));
        handler.transform.SetParent(content.transform);
        spawnedSettings.Add(handler);

        const string gap = "     ";

        foreach (UIMainMenu.SettingsTab settingsTab in UIMainMenu.SettingsTab.tabs) {
            if (settingsTab == UIMainMenu._language_setting_tab) {
                continue;
            }
            List<FieldInfo> fields = UIMainMenu.GetSettingsFromStaticClass(settingsTab.settings_origin);

            CreateSettingButton(gap + Lang.Get(settingsTab.name), ref num, out GameObject headerObj, out Button headerBtn, out TextMeshProUGUI headerText);
            if (headerText != null) {
                headerText.fontSize += 10;
                headerText.horizontalAlignment = HorizontalAlignmentOptions.Left;
                headerText.color = Color.cyan;
                if (headerBtn != null) {
                    headerBtn.GetComponent<Image>().color = Color.cyan;
                }
            }
            if (headerObj != null) {
                headerObj.name = settingsTab.name;
            }
            if (headerBtn != null) {
                Button.Destroy(headerBtn);
            }

            CreateSettingFields(fields, ref num);

            if (settingsTab == UIMainMenu._chat_setting_tab) {
                Chat.ShowChat();
                CreateKeybindSetting(CoopKeybinds.chat, "mmst_chatkeybind", ref num);
            } else if (settingsTab == UIMainMenu._voip_setting_tab) {
                string curMic = Voicechat.GetMic();
                int micIndex = Array.IndexOf(Microphone.devices, curMic);
                if (micIndex == -1) {
                    micIndex = 0;
                }

                GameObject micRow = Utils.Create("Special/GameSettingDropdown", content);
                List<TMP_Dropdown.OptionData> options = new List<TMP_Dropdown.OptionData>();
                if (Microphone.devices.Length == 0) {
                    options.Add(new TMP_Dropdown.OptionData(Lang.Get("mmst_mike_osdefault")));
                } else {
                    foreach (string deviceName in Microphone.devices) {
                        options.Add(new TMP_Dropdown.OptionData(deviceName));
                    }
                }
                TMP_Dropdown dropDown = micRow.transform.GetChild(1).GetComponent<TMP_Dropdown>();
                dropDown.AddOptions(options);
                dropDown.SetValueWithoutNotify(micIndex);
                dropDown.onValueChanged.AddListener(i => {
                    string selectedMic = Microphone.devices.ElementAtOrDefault(i);
                    if (selectedMic != null) {
                        Voicechat.SetMicrophone(selectedMic);
                    }
                });
                RectTransform dropDownRect = dropDown.GetComponent<RectTransform>();
                dropDownRect.offsetMin = new Vector2(0, dropDownRect.offsetMin.y);
                dropDownRect.anchorMin = new Vector2(0.5f, dropDownRect.anchorMin.y);
                SetupSetting(micRow, Lang.Get("mmst_mike"), null, ref num);

                ScrollRect scrollRect = dropDownRect.GetComponentInChildren<ScrollRect>(true);
                if (scrollRect != null) {
                    scrollRect.vertical = true;
                    if (scrollRect.scrollSensitivity == 1) {
                        scrollRect.scrollSensitivity += 20;
                    }
                    if (scrollRect.viewport != null) {
                        scrollRect.viewport.sizeDelta += new Vector2(0, 400);
                    }
                }

                CreateKeybindSetting(CoopKeybinds.voicechat, "mmst_vckeybind", ref num);

                CreateSettingButton(gap + Lang.Get("mmst_mike_test"), ref num, out GameObject testBtnObj, out Button testBtn, out TextMeshProUGUI testBtnText);
                RawImage rawImage = null;
                if (testBtnText != null) {
                    testBtnText.horizontalAlignment = HorizontalAlignmentOptions.Left;
                }
                if (testBtn != null) {
                    testBtn.onClick.AddListener(() => {
                        Voicechat.ENABLE_LOCAL_LOOPBACK = !Voicechat.ENABLE_LOCAL_LOOPBACK;
                        testBtnText.text = gap + (Voicechat.ENABLE_LOCAL_LOOPBACK ? Lang.Get("mmst_mike_testing") : Lang.Get("mmst_mike_test"));
                        if (rawImage != null) {
                            rawImage.gameObject.SetActive(Voicechat.ENABLE_LOCAL_LOOPBACK);
                        }
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    });
                }
                testBtnObj.name = "mmst_mike_test";

                UIMainMenu.ResetSettingsVOIPVisualiser();
                GameObject vizObj = new GameObject("VoiceVisualizer");
                VCVizRectTransform = vizObj.AddComponent<RectTransform>();
                VCVizRectTransform.SetParent(testBtnObj.transform);
                VCVizRectTransform.SetStretch();
                VCVizRectTransform.anchorMin = new Vector2(0.5f, 0);
                rawImage = vizObj.AddComponent<RawImage>();
                rawImage.gameObject.SetActive(Voicechat.ENABLE_LOCAL_LOOPBACK);
                UIBullshit.main.egg = vizObj.AddComponent<ECGVisualizer>();
                Vector2 pixelSize = RectTransformUtility.PixelAdjustRect(VCVizRectTransform, VCVizRectTransform.GetComponentInParent<Canvas>()).size;
                UIBullshit.main.egg.textureSize = (pixelSize * 0.5f).ToInt();
                UIBullshit.main.egg.followBody = false;
            } else if (settingsTab == UIMainMenu._general_setting_tab) {
                CreateSettingButton("导出诊断报告 (Export Diagnostic Report)", ref num, out GameObject reportBtnObj, out Button reportBtn, out TextMeshProUGUI reportBtnText);
                if (reportBtn != null) {
                    reportBtn.onClick.AddListener(() => {
                        MpDiagnosticReport rep = MpDiagnosticReporter.GenerateReport("SettingsMenuButton", domain: LogDomain.UI);
                        string fileName = !string.IsNullOrEmpty(rep?.SavedSummaryPath) ? Path.GetFileName(rep.SavedSummaryPath) : (rep?.ReportId ?? "ok");
                        if (reportBtnText != null) {
                            reportBtnText.text = $"已保存诊断报告: {fileName}";
                        }
                        if (PlayerCamera.main != null) {
                            Util.DoAlert($"诊断报告已保存: {fileName}");
                        }
                        Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
                    });
                }
                if (reportBtnObj != null) {
                    RectTransform reportRect = reportBtnObj.GetComponent<RectTransform>();
                    reportRect.offsetMin += Vector2.one * 10f;
                    reportRect.offsetMax -= Vector2.one * 10f;
                }

                CreateSettingButton(Lang.Get("mmst_button_deactivate_mp"), ref num, out DeactivateButton, out Button deactivateBtn, out _);
                if (deactivateBtn != null) {
                    deactivateBtn.onClick.AddListener(() => {
                        PlayerPrefsExtended.SetBool(Plugin.FORCEDISABLE_PLAYERPREF_KEY, true);
                        PlayerPrefs.Save();
                        Plugin.RestartGame();
                    });
                }
                RectTransform deactivateRect = DeactivateButton.GetComponent<RectTransform>();
                deactivateRect.offsetMin += Vector2.one * 10f;
                deactivateRect.offsetMax -= Vector2.one * 10f;

                UIMainMenu.BuildUpdateButton?.Invoke();
            } else if (settingsTab == UIMainMenu._bugreport_setting_tab && bugreportFunctionCreator != null) {
                bugreportFunctionCreator.DynamicInvoke();
            }

            num += 60f;
        }

        SettingsMenu.instance.content.sizeDelta = new Vector2(SettingsMenu.instance.content.sizeDelta.x, num);
    }

    private static void CreateKeybindSetting(in string setting, in string text, ref float num) {
        SettingKeybind keybind = Settings.Get<SettingKeybind>(setting);
        GameObject g = Utils.Create("Special/GameSettingInput", content);
        Button button = g.transform.GetChild(1).GetComponent<Button>();
        TextMeshProUGUI txt = button.GetComponentInChildren<TextMeshProUGUI>();
        txt.text = keybind.GetDisplayString();
        button.onClick.AddListener(() => {
            button.StartCoroutine(SettingsMenu.instance.WaitForKeyPress(keybind, txt));
            Util.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        });
        SetupSetting(g, Lang.Get(text), null, ref num);
    }

    public static void CreateSettingButton(in string str, ref float num, out GameObject g, out Button button, out TextMeshProUGUI text) {
        g = Utils.Create("Special/GameSettingLanguage", content);
        if (g.TryGetComponent(out button)) {
            button.onClick.RemoveAllListeners();
        }
        text = g.transform.GetChild(0).GetComponent<TextMeshProUGUI>();
        SetupSetting(g, str, null, ref num);
    }

    private static void CreateSettingFields(List<FieldInfo> fields, ref float num) {
        foreach (FieldInfo f in fields) {
            SettingDeclarerThingyBaseAttribute attr = f.GetAttribute<SettingDeclarerThingyBaseAttribute>();
            if (attr == null) {
                continue;
            }

            string langKey = attr.n ?? ("setting_" + f.DeclaringType + "_" + f.Name);
            string descKey = langKey + "_desc";
            string setName = Lang.Get(langKey);
            Lang.TryGet(descKey, out string setDescription);

            GameObject g;
            if (attr is SettingDeclarerThingyFloatAttribute setFloat) {
                float val = (float)f.GetValue(null);
                g = Utils.Create("Special/GameSettingFloat", content);
                Slider slider = g.transform.GetChild(1).GetComponent<Slider>();
                slider.minValue = setFloat.mi;
                slider.maxValue = setFloat.ma;
                slider.SetValueWithoutNotify(val);
                g.transform.GetChild(2).GetComponent<TextMeshProUGUI>().text = setFloat.GetDisplayString(f);
                slider.onValueChanged.AddListener(x => {
                    setFloat.Save(f, x);
                    g.transform.GetChild(2).GetComponent<TextMeshProUGUI>().text = setFloat.GetDisplayString(f);
                });
            } else if (attr is SettingDeclarerThingyChoiceAttribute choiceAttr) {
                int val = f.GetValue(null).ConvertTo<int>();
                IEnumerable<string> translatedChoices = new List<string>(choiceAttr.choices).Select(x => Lang.Get(x));

                g = Utils.Create("Special/GameSettingDropdown", content);
                List<TMP_Dropdown.OptionData> list = new List<TMP_Dropdown.OptionData>();
                foreach (string choiceText in translatedChoices) {
                    list.Add(new TMP_Dropdown.OptionData(choiceText));
                }
                TMP_Dropdown dropdown = g.transform.GetChild(1).GetComponent<TMP_Dropdown>();
                dropdown.AddOptions(list);
                dropdown.SetValueWithoutNotify(val);
                dropdown.onValueChanged.AddListener(x => choiceAttr.Save(f, x));
            } else if (attr is SettingDeclarerThingyBoolAttribute boolAttr) {
                bool val = (bool)f.GetValue(null);
                g = Utils.Create("Special/GameSettingBool", content);
                Toggle toggle = g.transform.GetChild(1).GetComponent<Toggle>();
                toggle.SetIsOnWithoutNotify(val);
                toggle.onValueChanged.AddListener(x => boolAttr.Save(f, x));
            } else {
                ModLog.Warning($"未知的设置字段特性类型: {langKey}", LogDomain.UI);
                continue;
            }

            g.name = langKey;
            SetupSetting(g, setName, setDescription, ref num);
        }
    }

    public static void SetSettingNameAndTooltip(GameObject g, in string setName, in string setDesc) {
        g.transform.GetChild(0).GetComponent<TextMeshProUGUI>().text = setName;
        UITooltip uiTooltip = g.transform.GetChild(0).gameObject.GetOrAddComponent<UITooltip>();
        uiTooltip.skipLocale = true;
        uiTooltip.tipName = setName;
        if (setDesc != null) {
            uiTooltip.tipDesc = setDesc;
        }
    }

    public static void SetupSetting(GameObject g, in string setName, in string setDesc, ref float num) {
        SetSettingNameAndTooltip(g, setName, setDesc);
        RectTransform rt = g.GetComponent<RectTransform>();
        rt.anchoredPosition = new Vector2(0f, 0f - num - rt.sizeDelta.y * 0.5f);
        num += rt.sizeDelta.y;
        spawnedSettings.Add(g);
    }
}

internal class MultiplayerSettingsHandler : MonoBehaviour {
    private void Update() {
        if (UIBullshit.main != null && UIBullshit.main.egg != null && UIBullshit.main.egg.tex != null && UIBullshit.main.egg.gameObject.activeInHierarchy) {
            if (UIBullshit.main.egg.writeX != UIMainMenu.last_writeX) {
                UIMainMenu.last_writeX = UIBullshit.main.egg.writeX;
                UIBullshit.main.egg.writeHeight = 0;
                UIMainMenu.writeX_flipflop = !UIMainMenu.writeX_flipflop;
            }
        }

        if (SettingsMenu_Start_MultiplayerPatch.DeactivateButton != null && SettingsMenu_Start_MultiplayerPatch.DeactivateButton.activeInHierarchy) {
            bool canDeactivate = !Net.IsRunning && !Util.IsInWorld();
            Button button = SettingsMenu_Start_MultiplayerPatch.DeactivateButton.GetComponent<Button>();
            if (button != null) {
                button.interactable = canDeactivate;
            }

            UITooltip uiTooltip = SettingsMenu_Start_MultiplayerPatch.DeactivateButton.GetComponentInChildren<UITooltip>();
            if (uiTooltip != null) {
                uiTooltip.tipDesc = Lang.GetNoError(canDeactivate ? "mmst_button_deactivate_mp_desc" : "mmst_button_deactivate_mp_desc_disabled");
            }
        }
    }
}

/// <summary>
/// 承载服务端暂停全屏遮罩提示文本的持久化 uGUI 容器。
/// </summary>
public class NewNewMultiplayerUI : CasualtiesTogetherSingleton {
    public static NewNewMultiplayerUI main { get; private set; }
    private static GameObject mainUI;

    private void Awake() {
        if (main != null) {
            throw new InvalidOperationException("Duplicate NewNewMultiplayerUI singleton instance.");
        }
        main = this;
    }

    private void UIManager_OnMainMenuLoad() {
#if !RELEASE
        try {
            Transform exp = PreRunScript.instance != null ? PreRunScript.instance.transform.Find("MenuBackground/Experiment") : null;
            if (exp != null && exp.TryGetComponent(out Image img)) {
                img.color = img.color.WithAlpha(0);
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "NewNewMultiplayerUI.HideBackgroundExperiment", ex);
        }
#endif
        if (UIManager.modCanvasObj != null && mainUI == null) {
            CreateMainUI();
        }
    }

    public static void CreateMainUI() {
        if (mainUI == null) {
            CreateTestUI();
            BuildPauseTextUI();
        }
    }

    private static void BuildPauseTextUI() {
        if (UIManager.modPauseTextObj == null) {
            UIManager.CreateLabelWithBackground(UIManager.modCanvasObj.transform, "server_pause", null, null, out UIManager.modPauseTextObj, out TextMeshProUGUI text, out RectTransform mainRT, out Image image);
            mainRT.pivot = Vector2.one * 0.5f;
            mainRT.anchorMin = new Vector2(-0.1f, -0.1f);
            mainRT.anchorMax = new Vector2(1.1f, 1.1f);
            text.raycastTarget = false;
            image.color = Color.black * 0.6f;
        }
    }

    private static void CreateTestUI() {
        // 预留给后续 uGUI 面板扩展的占位钩子
    }
}
