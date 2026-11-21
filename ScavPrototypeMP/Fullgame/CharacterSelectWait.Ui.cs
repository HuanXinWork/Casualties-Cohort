#if FULLGAME
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using TMPro;
using Together.Diagnostics;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 正式版联机角色选择与准备就绪门控协调器（主菜单 <see cref="PreRunScript"/> UI 挂钩、按钮文本覆写与面板显隐分部）。
/// </summary>
public static partial class CharacterSelectWait {
    struct ReadyButton {
        public Button button;
        public TextMeshProUGUI label;
        public string originalText;
    }

    static readonly List<ReadyButton> _readyButtons = new List<ReadyButton>();
    static readonly List<GameObject> _hiddenRunRulePanels = new List<GameObject>();
    static bool _uiHooks;
    static Button _charSelectStart;
    static Button _presetsNext;
    static Button _charSelectBack;

    public static void EnsureUiHooks(PreRunScript prs) {
        if (prs == null || _uiHooks) {
            return;
        }
        _uiHooks = true;

        Transform root = prs.runSettingsScreen != null ? prs.runSettingsScreen.transform : prs.transform;
        Transform presets = FindDeepChild(root, "Presets");
        Transform charSelect = FindCharSelectRoot(prs) ?? FindDeepChild(root, "CharSelect");

        if (presets != null) {
            Transform nextTf = FindDeepChild(presets, "Start");
            if (nextTf != null) {
                _presetsNext = nextTf.GetComponent<Button>();
                if (_presetsNext != null) {
                    _presetsNext.onClick.AddListener(OnPresetsNextClicked);
                }
            }
        }

        if (charSelect != null) {
            Transform startTf = FindDeepChild(charSelect, "Start");
            Transform bottom = FindDeepChild(charSelect, "Bottom");
            if (bottom != null) {
                Transform bottomStart = FindDeepChild(bottom, "Start");
                if (bottomStart != null) {
                    startTf = bottomStart;
                }
            }
            if (startTf != null) {
                _charSelectStart = startTf.GetComponent<Button>();
            }
            Transform backTf = FindDeepChild(charSelect, "Back");
            if (backTf != null) {
                _charSelectBack = backTf.GetComponent<Button>();
                if (_charSelectBack != null) {
                    _charSelectBack.onClick.AddListener(OnCharSelectBackClicked);
                }
            }
        }

        ModLog.Info(LogDomain.UI, $"CharacterSelectWait UI hooks: next={_presetsNext != null} start={_charSelectStart != null} back={_charSelectBack != null}");
    }

    public static void TickUi() {
        if (!Net.IsRunning || !Util.IsInMainMenu()) {
            return;
        }
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        EnsureUiHooks(prs);

        if (LateJoinPicking) {
            ApplyLocalWaitUi();
            ForceCharSelectReadyVisual(prs);
            return;
        }

        // 原版返回按钮可在未经过我们监听的情况下隐藏物种选择页，必须及时中止大厅准备门控以免后续进图漏生成玩家
        if (Net.IsServer && Waiting && Util.IsInMainMenu() && !IsSpeciesSelectPageVisible(prs)) {
            AbortLobbyGate();
            return;
        }

        if (Net.IsServer && !Waiting && IsSpeciesSelectPageVisible(prs)) {
            OpenLobbyGate();
            return;
        }

        if (!Waiting) {
            RestoreCharSelectStartStock(prs);
            return;
        }

        EnsureHostLobbyGate();
        SetCharBrowseInteractable(prs, !_speciesLocked);
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        ForceCharSelectReadyVisual(prs);
    }

    static bool IsSpeciesSelectPageVisible(PreRunScript prs) {
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect == null || !charSelect.gameObject.activeInHierarchy) {
            return false;
        }
        Transform presets = null;
        if (prs.runSettingsScreen != null) {
            presets = FindDeepChild(prs.runSettingsScreen.transform, "Presets");
        }
        if (presets != null && presets.gameObject.activeInHierarchy) {
            return false;
        }
        Transform bottom = FindDeepChild(charSelect, "Bottom");
        return bottom != null && bottom.gameObject.activeInHierarchy;
    }

    static void ApplyLocalWaitUi() {
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        EnsureUiHooks(prs);
        ShowCharSelectHideRunRules(prs);
        if (prs.tutorialWarning != null) {
            prs.tutorialWarning.SetActive(false);
        }
        SetCharBrowseInteractable(prs, !_speciesLocked);
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        ForceCharSelectReadyVisual(prs);
    }

    static void SetCharBrowseInteractable(PreRunScript prs, bool interactable) {
        if (prs == null || prs.charImages == null) {
            return;
        }
        for (int i = 0; i < prs.charImages.Count; i++) {
            if (prs.charImages[i] == null) {
                continue;
            }
            Button btn = prs.charImages[i].GetComponent<Button>();
            if (btn == null) {
                continue;
            }
            if (!interactable) {
                btn.interactable = false;
                continue;
            }
            if (i != 0 && PlayerPrefs.GetInt("wonchar" + (i - 1)) == 0) {
                btn.interactable = false;
            } else {
                btn.interactable = true;
            }
        }
    }

    static Transform FindDeepChild(Transform parent, string name) {
        if (parent == null) {
            return null;
        }
        if (parent.name == name) {
            return parent;
        }
        for (int i = 0; i < parent.childCount; i++) {
            Transform found = FindDeepChild(parent.GetChild(i), name);
            if (found != null) {
                return found;
            }
        }
        return null;
    }

    static Transform FindCharSelectRoot(PreRunScript prs) {
        if (prs == null) {
            return null;
        }
        if (prs.charImages != null) {
            for (int i = 0; i < prs.charImages.Count; i++) {
                if (prs.charImages[i] == null) {
                    continue;
                }
                Transform t = prs.charImages[i].transform;
                while (t != null) {
                    if (t.name == "CharSelect") {
                        return t;
                    }
                    if (prs.runSettingsScreen != null && t == prs.runSettingsScreen.transform) {
                        break;
                    }
                    t = t.parent;
                }
            }
        }
        if (prs.charSelectName != null) {
            Transform t = prs.charSelectName.transform;
            while (t != null) {
                if (t.name == "CharSelect") {
                    return t;
                }
                t = t.parent;
            }
        }
        if (prs.runSettingsScreen != null) {
            return FindDeepChild(prs.runSettingsScreen.transform, "CharSelect");
        }
        return null;
    }

    static void HideCharSelect() {
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect != null) {
            charSelect.gameObject.SetActive(false);
        }
    }

    static void ShowCharSelectHideRunRules(PreRunScript prs) {
        if (prs.runSettingsScreen != null) {
            if (!prs.runSettingsScreen.activeSelf) {
                _openedRunSettings = true;
            }
            prs.runSettingsScreen.SetActive(true);
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect != null) {
            charSelect.gameObject.SetActive(true);
            Transform bottom = FindDeepChild(charSelect, "Bottom");
            if (bottom != null) {
                bottom.gameObject.SetActive(true);
            }
        }
        if (prs.runSettingsScreen == null) {
            return;
        }
        Transform root = prs.runSettingsScreen.transform;
        for (int i = 0; i < root.childCount; i++) {
            Transform child = root.GetChild(i);
            if (child == null) {
                continue;
            }
            if (charSelect != null && child == charSelect) {
                continue;
            }
            if (child.name != "Presets" && child.name != "CustomSettings" && child.name != "SelectOutline") {
                continue;
            }
            if (child.gameObject.activeSelf) {
                child.gameObject.SetActive(false);
                if (!_hiddenRunRulePanels.Contains(child.gameObject)) {
                    _hiddenRunRulePanels.Add(child.gameObject);
                }
            }
        }
    }

    static void RestoreRunRulePanels() {
        for (int i = 0; i < _hiddenRunRulePanels.Count; i++) {
            if (_hiddenRunRulePanels[i] != null) {
                _hiddenRunRulePanels[i].SetActive(true);
            }
        }
        _hiddenRunRulePanels.Clear();
    }

    static Button FindCharSelectStartButton(PreRunScript prs) {
        if (_charSelectStart != null) {
            return _charSelectStart;
        }
        Transform charSelect = FindCharSelectRoot(prs);
        if (charSelect == null) {
            return null;
        }
        Transform bottom = FindDeepChild(charSelect, "Bottom");
        if (bottom != null) {
            Transform startTf = FindDeepChild(bottom, "Start");
            if (startTf != null) {
                return startTf.GetComponent<Button>();
            }
        }
        Transform anyStart = FindDeepChild(charSelect, "Start");
        return anyStart != null ? anyStart.GetComponent<Button>() : null;
    }

    static void ForceCharSelectReadyVisual(PreRunScript prs) {
        if (prs == null) {
            return;
        }
        Button start = FindCharSelectStartButton(prs);
        if (start == null) {
            return;
        }
        if (!start.gameObject.activeSelf) {
            start.gameObject.SetActive(true);
        }
        if (start.transform.parent != null && !start.transform.parent.gameObject.activeSelf) {
            start.transform.parent.gameObject.SetActive(true);
        }

        RememberButton(start);
        start.enabled = true;
        start.interactable = true;

        Transform t = start.transform;
        while (t != null) {
            CanvasGroup cg = t.GetComponent<CanvasGroup>();
            if (cg != null) {
                cg.interactable = true;
                cg.blocksRaycasts = true;
                if (cg.alpha < 0.01f) {
                    cg.alpha = 1f;
                }
            }
            if (prs.runSettingsScreen != null && t == prs.runSettingsScreen.transform) {
                break;
            }
            t = t.parent;
        }

        string text = ReadyButtonText();
        TextMeshProUGUI[] labels = start.GetComponentsInChildren<TextMeshProUGUI>(true);
        for (int i = 0; i < labels.Length; i++) {
            if (labels[i] != null) {
                labels[i].text = text;
                labels[i].raycastTarget = false;
            }
        }
    }

    static void RestoreCharSelectStartStock(PreRunScript prs) {
        if (_readyButtons.Count > 0) {
            RestoreReadyButtons();
        }
        Button start = FindCharSelectStartButton(prs);
        if (start == null) {
            return;
        }
        start.enabled = true;
        start.interactable = true;
    }

    static void RememberButton(Button button) {
        if (button == null) {
            return;
        }
        for (int i = 0; i < _readyButtons.Count; i++) {
            if (_readyButtons[i].button == button) {
                return;
            }
        }
        TextMeshProUGUI label = button.GetComponentInChildren<TextMeshProUGUI>(true);
        _readyButtons.Add(new ReadyButton {
            button = button,
            label = label,
            originalText = label != null ? label.text : "Start",
        });
    }

    static string ReadyButtonText() {
        if (LateJoinPicking) {
            return LocalConfirmed ? "Joining..." : "Join";
        }
        if (_pendingTutorial) {
            return "Start";
        }
        if (LocalConfirmed) {
            return $"Ready [{ReadyCount}/{MaxCount}]";
        }
        return $"Ready up [{ReadyCount}/{MaxCount}]";
    }

    static void RestoreReadyButtons() {
        for (int i = 0; i < _readyButtons.Count; i++) {
            ReadyButton rb = _readyButtons[i];
            if (rb.label != null && rb.originalText != null) {
                rb.label.text = rb.originalText;
            }
            if (rb.button != null) {
                rb.button.enabled = true;
                rb.button.interactable = true;
            }
        }
        _readyButtons.Clear();
    }

    static void ReleaseMenuDim() {
        WorldMP.ReleaseTitleFade();
        PreRunScript prs = PreRunScript.instance;
        if (prs == null) {
            return;
        }
        if (prs.cover != null) {
            Color c = prs.cover.color;
            c.a = 0f;
            prs.cover.color = c;
            prs.cover.raycastTarget = false;
        }
        try {
            Traverse.Create(prs).Field("startedLoading").SetValue(false);
        } catch {
        }
    }
}

[HarmonyPatch(typeof(PreRunScript), "Start")]
static class PreRunScript_Start_CharSelectWaitHooksPatch {
    static void Postfix(PreRunScript __instance) {
        CharacterSelectWait.EnsureUiHooks(__instance);
    }
}

[HarmonyPatch(typeof(PreRunScript), "Update")]
static class PreRunScript_Update_CharSelectWaitPatch {
    static void Postfix(PreRunScript __instance) {
        CharacterSelectWait.EnsureUiHooks(__instance);
        CharacterSelectWait.TickUi();
    }
}
#endif
