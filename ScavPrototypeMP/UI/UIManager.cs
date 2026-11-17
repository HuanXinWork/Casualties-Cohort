using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using TMPro;
using Together.Api;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 当控制台或联机主菜单开启时，强制置位原版主菜单按钮的遮罩标记以阻止鼠标悬停穿透。
/// </summary>
[HarmonyPatch(typeof(AdaptiveButton), nameof(AdaptiveButton.overlayActive), MethodType.Getter)]
public static class AdaptiveButton_overlayActive_MultiplayerPatch {
#if !RELEASE
    private static bool _getterWorkingAnnounced;

    private static void Prefix() {
        if (!_getterWorkingAnnounced) {
            _getterWorkingAnnounced = true;
            ModLog.Debug($"AdaptiveButton.overlayActive Getter 补丁已生效: {new StackTrace()}", LogDomain.UI);
        }
    }
#endif

    public static void Postfix(AdaptiveButton __instance, ref bool __result) {
        if (Con.IsConsoleOpen() || UIMainMenu.IsOpen()) {
            __result = true;
        }
    }
}

/// <summary>
/// uGUI <see cref="RectTransform"/> 锚点与边距快捷布局扩展方法。
/// </summary>
public static class UIUtility {
    public static void SetAnchors(this RectTransform rt, Vector2 anchorMin, Vector2 anchorMax, Vector2? pivot = null) {
        rt.anchorMin = anchorMin;
        rt.anchorMax = anchorMax;
        if (pivot.HasValue) {
            rt.pivot = pivot.Value;
        }
    }

    public static void SetStretch(this RectTransform rt, float left = 0, float right = 0, float top = 0, float bottom = 0) {
        rt.anchorMin = new Vector2(0, 0);
        rt.anchorMax = new Vector2(1, 1);
        rt.offsetMin = new Vector2(left, bottom);
        rt.offsetMax = new Vector2(-right, -top);
    }

    public static void SetSizeAndPos(this RectTransform rt, Vector2 sizeDelta, Vector2 anchoredPosition) {
        rt.sizeDelta = sizeDelta;
        rt.anchoredPosition = anchoredPosition;
    }
}

/// <summary>
/// 全局持久化 uGUI 画布、原版控件模板抓取与主菜单联机入口按钮管理器。
/// </summary>
public class UIManager : CasualtiesTogetherSingleton {
    private static UIManager main;

    public UIManager() {
        main = this;
    }

    private static readonly string[] SpritesToFind = {
        "uiBlock",
        "uiBlockMini",
        "uiBlockNano",
        "uiBlockSmall",
        "UICheckMark",
        "uicursor",
        "uicursorpressed",
        "UIElement8px",
        "UIFoldoutClosed",
        "UIFoldoutOpened",
        "uigradientblack",
        "uiPoint",
        "userbarfill",
        "whitesquare",
    };

    public static Dictionary<string, Sprite> ui_sprites = new();
    public static Sprite uiBlockSmall;
    public static Sprite uiBlockNano;

    public static GameObject template_button;
    public static GameObject template_label;

    public static GameObject runSettingsMenu;

    public static GameObject MP_MENU_BUTTON;
    public static GameObject MP_LINK_BUTTON;

    public static GameObject modPauseTextObj;
    public static GameObject modCanvasObj;
    public static Canvas SeparateCanvas;
    public static Vector2 ReferenceResolution = new Vector2(1920, 1080);

    public static PreRunScript PreRunScript => PreRunScript.instance;
    public static Canvas GameCanvas;

    private static readonly bool CreateLinkButton = false;

    // 首次初始化可能在 PreRunScript.Awake 之前触发，使用短周期重试确保主菜单联机按钮可靠挂载
    private float _nextMenuButtonTry;

    public static bool IsServerGameplayPaused => !Util.IsInPauseMenuOrMainMenu() && Mathf.Abs(Time.timeScale) < 0.00501f;

    public static VerticalLayoutGroup AddVerticalLayout(in GameObject go, int spacing = 4, RectOffset padding = null, bool controlWidth = true, bool controlHeight = false) {
        VerticalLayoutGroup vlg = go.AddComponent<VerticalLayoutGroup>();
        vlg.spacing = spacing;
        vlg.padding = padding ?? new RectOffset(4, 4, 4, 4);
        vlg.childControlWidth = controlWidth;
        vlg.childControlHeight = controlHeight;
        vlg.childForceExpandWidth = controlWidth;
        vlg.childForceExpandHeight = false;
        return vlg;
    }

    public static HorizontalLayoutGroup AddHorizontalLayout(in GameObject go, int spacing = 4, RectOffset padding = null, bool controlWidth = false, bool controlHeight = true) {
        HorizontalLayoutGroup hlg = go.AddComponent<HorizontalLayoutGroup>();
        hlg.spacing = spacing;
        hlg.padding = padding ?? new RectOffset(4, 4, 4, 4);
        hlg.childControlWidth = controlWidth;
        hlg.childControlHeight = controlHeight;
        hlg.childForceExpandWidth = false;
        hlg.childForceExpandHeight = controlHeight;
        return hlg;
    }

    public static LayoutElement SetPreferredSize(in GameObject go, float width = -1, float height = -1) {
        LayoutElement le = go.GetOrAddComponent<LayoutElement>();
        if (width >= 0) {
            le.preferredWidth = width;
        }
        if (height >= 0) {
            le.preferredHeight = height;
        }
        return le;
    }

    public static void CreateBlock(in Component parent, in string name, out GameObject go, out RectTransform rt, out Image image) {
        go = new GameObject("UI_BLOCK_" + name);
        if (parent != null) {
            go.transform.SetParent(parent.transform, false);
        }
        if (template_button != null) {
            go.layer = template_button.layer;
        }

        rt = go.AddComponent<RectTransform>();
        image = go.AddComponent<Image>();
        image.type = Image.Type.Sliced;
        image.sprite = uiBlockNano;
    }

    public static GameObject CreateBlock(in Component parent, in string name, Vector2 size, float alpha = 1f) {
        CreateBlock(parent, name, out GameObject go, out RectTransform rt, out Image image);
        rt.sizeDelta = size;
        image.color = image.color.WithAlpha(alpha);
        return go;
    }

    public static void CreateButton(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject button_go, out TextMeshProUGUI text, out RectTransform rt, out Button button)
        => CreateButton(parent, label, tooltip, tooltip_desc, out button_go, out text, out rt, out button, out _);

    public static void CreateButton(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject button_go, out TextMeshProUGUI text, out RectTransform rt, out Button button, out Image image) {
        button_go = GameObject.Instantiate(template_button.gameObject, parent != null ? parent.transform : null, false);
        button_go.SetActive(true);
        button_go.name = "UI_BUTTON_" + label;

        text = button_go.GetComponentInChildren<TextMeshProUGUI>();
        rt = button_go.GetComponent<RectTransform>();
        button = button_go.GetComponent<Button>();
        image = button_go.GetComponent<Image>();

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }

    public static Button CreateButton(in Component parent, in string label, Action onClick, in string tooltip = null, in string tooltip_desc = null) {
        CreateButton(parent, label, tooltip, tooltip_desc, out _, out _, out _, out Button button);
        if (onClick != null) {
            button.onClick.AddListener(() => onClick());
        }
        return button;
    }

    public static void CreateLabelWithBackground(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt)
        => CreateLabelWithBackground(parent, label, tooltip, tooltip_desc, out go, out text, out rt, out _);

    public static void CreateLabelWithBackground(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt, out Image image) {
        go = GameObject.Instantiate(template_button.gameObject, parent != null ? parent.transform : null, false);
        go.SetActive(true);
        go.name = "UI_LABELBG_" + label;

        text = go.GetComponentInChildren<TextMeshProUGUI>();
        rt = go.GetComponent<RectTransform>();
        image = go.GetComponent<Image>();
        Button button = go.GetComponent<Button>();
        GameObject.Destroy(button);

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }

    public static void CreateLabel(in Component parent, in string label, in string tooltip, in string tooltip_desc, out GameObject go, out TextMeshProUGUI text, out RectTransform rt) {
        go = GameObject.Instantiate(template_label.gameObject, parent != null ? parent.transform : null, false);
        go.SetActive(true);
        go.name = "UI_LABEL_" + label;

        text = go.GetComponent<TextMeshProUGUI>();
        rt = go.GetComponent<RectTransform>();

        SetUITooltip(text, label, tooltip, tooltip_desc);
    }

    public static TextMeshProUGUI CreateLabel(in Component parent, in string label, in string tooltip = null, in string tooltip_desc = null) {
        CreateLabel(parent, label, tooltip, tooltip_desc, out _, out TextMeshProUGUI text, out _);
        return text;
    }

    public static void SetUITooltip(in TextMeshProUGUI text, in string label, in string tooltip, in string tooltip_desc, bool translate = true) {
        text.text = translate ? Lang.GetNoError(label) : label;

        if (string.IsNullOrEmpty(tooltip) && string.IsNullOrEmpty(tooltip_desc)) {
            if (text.TryGetComponent(out UITooltip existingTip)) {
                UITooltip.Destroy(existingTip);
            }
        } else {
            UITooltip tt = text.GetOrAddComponent<UITooltip>();
            tt.skipLocale = true;
            if (string.IsNullOrEmpty(tooltip)) {
                tt.tipName = "";
                tt.localeName = "";
            } else {
                tt.tipName = translate ? Lang.GetNoError(tooltip) : tooltip;
                tt.localeName = Lang.LocalPrefixTag + tooltip;
            }
            if (string.IsNullOrEmpty(tooltip_desc)) {
                tt.tipDesc = "";
                tt.localeDesc = "";
            } else {
                tt.tipDesc = translate ? Lang.GetNoError(tooltip_desc) : tooltip_desc;
                tt.localeDesc = Lang.LocalPrefixTag + tooltip_desc;
            }
        }
        if (text.TryGetComponent(out UILocalizer uil)) {
            Component.Destroy(uil);
        }
    }

    public static RectTransform CreateRect(in Component parent, in string name = "rect") {
        GameObject go = new GameObject(name, typeof(RectTransform));
        go.transform.SetParent(parent.transform, false);
        return go.GetComponent<RectTransform>();
    }

    public static void ScrapeAssets() {
        try {
            Sprite[] sprites = Resources.FindObjectsOfTypeAll<Sprite>();
            foreach (string spriteName in SpritesToFind) {
                Sprite spr = sprites.FirstOrDefault(f => f.name == spriteName);
                if (spr == null) {
                    spr = sprites.FirstOrDefault(f => f.name.Contains(spriteName));
                    if (spr == null) {
                        ModLog.Debug($"UI精灵未找到: \"{spriteName}\"", LogDomain.UI);
                        continue;
                    }
                    ModLog.Debug($"UI精灵模糊匹配命中: \"{spriteName}\" -> \"{spr.name}\"", LogDomain.UI);
                } else {
                    ModLog.Debug($"UI精灵精确命中: \"{spriteName}\"", LogDomain.UI);
                }
                ui_sprites[spriteName] = spr;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.UI, "UIManager.ScrapeAssets", ex);
        }

        if (ui_sprites.TryGetValue("uiBlockSmall", out Sprite smallSprite) || ui_sprites.TryGetValue("uiBlock", out smallSprite)) {
            uiBlockSmall = smallSprite;
        }
        if (ui_sprites.TryGetValue("uiBlockNano", out Sprite nanoSprite)) {
            uiBlockNano = nanoSprite;
        }

        GetPrerunScriptAndCanvas();
        if (PreRunScript != null) {
            Transform menuBg = PreRunScript.transform.Find("RunSettings");
            if (menuBg != null) {
                runSettingsMenu = menuBg.gameObject;
            }

            if (template_button == null) {
                try {
                    Transform cancel = PreRunScript.transform.Find("RunSettings/Presets/Cancel");
                    if (cancel != null) {
                        template_button = GameObject.Instantiate(cancel.gameObject, modCanvasObj.transform, false);
                        template_button.name = "MAIN_BUTTON";
                        Button button = template_button.GetComponent<Button>();
                        TMP_Text text = button.GetComponentInChildren<TMP_Text>(true);
                        text.color = Color.white;
                        template_label = text.gameObject;
                        button.onClick.RemoveAllListeners();
                        button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                        RectTransform rt = button.GetComponent<RectTransform>();
                        rt.anchoredPosition = Vector2.zero;
                        rt.anchorMin = Vector2.zero;
                        rt.anchorMax = Vector2.one;
                        rt.offsetMin = Vector2.zero;
                        rt.offsetMax = Vector2.zero;
                        template_button.SetActive(false);
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.UI, "UIManager.CloneTemplateButton", ex);
                }
            }
        }
    }

    public static void GetPrerunScriptAndCanvas() {
        if (PreRunScript != null) {
            GameCanvas = PreRunScript.GetComponent<Canvas>();
        }
    }

    private static void CreateModCanvas() {
        if (modCanvasObj != null) {
            return;
        }

        modCanvasObj = new GameObject("CasualtiesTogetherUICanvas");
        modCanvasObj.hideFlags = HideFlags.HideAndDontSave;
        modCanvasObj.layer = GameCanvas.gameObject.layer;

        SeparateCanvas = modCanvasObj.GetOrAddComponent<Canvas>();
        modCanvasObj.GetOrAddComponent<GraphicRaycaster>();
        RectTransform rt = modCanvasObj.GetOrAddComponent<RectTransform>();
        CanvasScaler scaler = modCanvasObj.GetOrAddComponent<CanvasScaler>();

        CanvasScaler ogScaler = GameCanvas.GetComponent<CanvasScaler>();
        scaler.referenceResolution = ogScaler.referenceResolution;
        ReferenceResolution = ogScaler.referenceResolution;
        scaler.fallbackScreenDPI = ogScaler.fallbackScreenDPI;
        scaler.dynamicPixelsPerUnit = ogScaler.dynamicPixelsPerUnit;
        scaler.referencePixelsPerUnit = ogScaler.referencePixelsPerUnit;
        scaler.physicalUnit = ogScaler.physicalUnit;
        scaler.scaleFactor = ogScaler.scaleFactor;
        scaler.screenMatchMode = ogScaler.screenMatchMode;
        scaler.uiScaleMode = ogScaler.uiScaleMode;

        RectTransform ogRt = GameCanvas.GetComponent<RectTransform>();
        rt.anchoredPosition = ogRt.anchoredPosition;
        rt.anchorMax = ogRt.anchorMax;
        rt.anchorMin = ogRt.anchorMin;
        rt.offsetMin = ogRt.offsetMin;
        rt.offsetMax = ogRt.offsetMax;
        rt.localScale = ogRt.localScale;

        SeparateCanvas.sortingOrder = GameCanvas.sortingOrder + 10;
        SeparateCanvas.renderMode = GameCanvas.renderMode;
        SeparateCanvas.scaleFactor = GameCanvas.scaleFactor;
        SeparateCanvas.additionalShaderChannels = GameCanvas.additionalShaderChannels;

        modCanvasObj.transform.SetSiblingIndex(Mathf.Max(0, GameCanvas.transform.GetSiblingIndex() - 2));
        GameObject.DontDestroyOnLoad(modCanvasObj);
    }

    public static void OnMainMenuLoad() {
        GetPrerunScriptAndCanvas();
        if (GameCanvas == null) {
            return;
        }

        CreateModCanvas();
        ScrapeAssets();

        if (CreateLinkButton && MP_LINK_BUTTON == null) {
            try {
                Transform credits = PreRunScript.transform.Find("Credits");
                Transform trans = PreRunScript.transform.Find("Credits/TranslationRepo");
                if (trans != null) {
                    MP_LINK_BUTTON = GameObject.Instantiate(trans.gameObject, PreRunScript.transform, false);
                    MP_LINK_BUTTON.name = "MAIN_BUTTON_LINK";

                    RectTransform rt = MP_LINK_BUTTON.GetComponent<RectTransform>();
                    rt.anchoredPosition = new Vector2(50, rt.anchoredPosition.y);
                    if (credits != null) {
                        rt.SetSiblingIndex(credits.GetSiblingIndex());
                    }

                    UITooltip tt = MP_LINK_BUTTON.GetComponent<UITooltip>();
                    tt.localeName = Lang.LocalPrefixTag + "mainmenu_mp_button_tooltip";
                    tt.skipLocale = true;
                    tt.tipName = Lang.Get("mainmenu_mp_button_tooltip");

                    Image img = MP_LINK_BUTTON.GetComponent<Image>();
                    img.sprite = CoopModAssets.mpmod_icon;
                    img.color = Color.green;

                    Button button = MP_LINK_BUTTON.GetComponent<Button>();
                    button.onClick.RemoveAllListeners();
                    button.onClick.SetPersistentListenerState(0, UnityEngine.Events.UnityEventCallState.Off);
                    button.onClick.AddListener(() => {
                        if (UIBullshit.main == null || (UIMainMenu.IsOpen() && UIMainMenu.current_tab == UIMainMenu.menutab_about) || Con.IsConsoleOpen()) {
                            PreRunScript.OpenURL(Constants.OG_DOWNLOAD_URL);
                        } else {
                            UIMainMenu.SetOpen(true);
                            UIMainMenu.current_tab = UIMainMenu.menutab_about;
                        }
                    });
                } else {
                    ModLog.Warning("未找到 Credits/TranslationRepo，跳过链接按钮创建", LogDomain.UI);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIManager.CreateLinkButton", ex);
            }
        }

        if (MP_MENU_BUTTON == null) {
            try {
                if (PreRunScript != null && template_button != null) {
                    Transform credits = PreRunScript.transform.Find("Credits");
                    RectTransform canvasRt = PreRunScript.GetComponent<RectTransform>();

                    CreateButton(PreRunScript, "mainmenu_mainbutton", null, null, out MP_MENU_BUTTON, out TextMeshProUGUI btnLabel, out RectTransform mpBtnRt, out Button mpBtn, out Image mpButtonBg);

#if !RELEASE
                    btnLabel.fontMaterial = new Material(btnLabel.fontMaterial) {
                        name = "Together_MainMenuButtonMat",
                        shader = CoopModAssets.coolshader
                    };
#endif

#if RELEASE
                    if (credits != null) {
                        mpBtnRt.SetSiblingIndex(credits.GetSiblingIndex());
                    } else
#endif
                    {
                        mpBtnRt.SetSiblingIndex(canvasRt.childCount);
                    }

                    mpBtnRt.pivot = new Vector2(0.5f, 1f);
                    const float halfWidth = 0.27f * 0.5f;
                    mpBtnRt.anchorMin = new Vector2(0.5f - halfWidth, 0.005f);
                    mpBtnRt.anchorMax = new Vector2(0.5f + halfWidth, 0.09f);
                    mpBtnRt.offsetMin = Vector2.zero;
                    mpBtnRt.offsetMax = Vector2.zero;

                    mpBtn.onClick.AddListener(() => {
                        ModLog.Debug("点击主菜单 Multiplayer 按钮", LogDomain.UI);
                        UIMainMenu.SetOpen(!UIMainMenu.mainmenu_open);
                        if (runSettingsMenu != null) {
                            runSettingsMenu.SetActive(false);
                        }
                    });

                    mpButtonBg.raycastPadding = Vector2.one * 4f;
                    mpButtonBg.sprite = uiBlockSmall;

                    ModLog.Debug("主菜单 MP_MENU_BUTTON 创建完毕", LogDomain.UI);
                    // 通知外部模组（SkinSync / CuSaveManager 等）获取联机主按钮锚点以并排挂载入口按钮
                    TogetherPanelApi.NotifyMainMenuMpButtonReady(MP_MENU_BUTTON);
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIManager.CreateMpMenuButton", ex);
            }
        } else {
            // 按钮已存在但发生主菜单刷新时，同样通知外部模组校准并排按钮锚点
            UIMainMenu.ApplyMpMenuButton(UIMainMenu.mainmenu_open);
            TogetherPanelApi.NotifyMainMenuMpButtonReady(MP_MENU_BUTTON);
        }

        if (main != null) {
            main.SendMessage("UIManager_OnMainMenuLoad", SendMessageOptions.DontRequireReceiver);
        }
    }

    private void Update() {
        if (modPauseTextObj != null) {
            modPauseTextObj.SetActive(IsServerGameplayPaused);
        }

        if (MP_MENU_BUTTON == null && !Util.IsInWorld() && Time.unscaledTime >= _nextMenuButtonTry) {
            _nextMenuButtonTry = Time.unscaledTime + 0.5f;
            try {
                OnMainMenuLoad();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, "UIManager.RetryOnMainMenuLoad", ex);
            }
        }
    }

    private void OnGUI() {
        // 全局顶层渲染诊断透视悬浮窗（支持主菜单、局内及控制台开启状态随时查看输入锁与网络指标）
        MpDebugOverlay.DrawOnGui();
    }
}
