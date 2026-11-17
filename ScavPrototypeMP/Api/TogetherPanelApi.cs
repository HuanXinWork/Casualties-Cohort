using System;
using System.Collections.Generic;
using Together.Diagnostics;
using UnityEngine;

namespace Together.Api {
    /// <summary>
    /// 面向外部模组（<c>SkinSync</c>、<c>CuSaveManager</c> 等）开放的官方 UI 面板、输入阻塞锁与角色视图生命周期接口。
    /// </summary>
    public static class TogetherPanelApi {
        public sealed class ExternalSettingsTab {
            public string Id { get; internal set; }
            public Func<string> TitleProvider { get; internal set; }
            public Action DrawImGui { get; internal set; }
            public Func<string> StatusProvider { get; internal set; }
            public Action OnReset { get; internal set; }
            public string Title => TitleProvider != null ? (TitleProvider() ?? Id) : Id;
            public string Status => StatusProvider != null ? (StatusProvider() ?? string.Empty) : string.Empty;
        }

        public sealed class ExternalPlayerAction {
            public string Id { get; internal set; }
            public Func<Body, string> LabelProvider { get; internal set; }
            public Func<Body, bool> CanExecute { get; internal set; }
            public Action<Body> Execute { get; internal set; }
        }

        private static readonly List<ExternalSettingsTab> SettingsTabs = new List<ExternalSettingsTab>();
        private static readonly Dictionary<string, Func<bool>> InputBlockers = new Dictionary<string, Func<bool>>(StringComparer.OrdinalIgnoreCase);
        private static readonly List<ExternalPlayerAction> PlayerActions = new List<ExternalPlayerAction>();

        /// <summary>主菜单多人按钮创建或刷新就绪时触发，参数为按钮的 <see cref="RectTransform"/>（供外部模组对齐并排挂载入口按钮）。</summary>
        public static event Action<RectTransform> OnMainMenuMpButtonReady;

        /// <summary>健康面板（<see cref="WoundView"/>）绑定或切换观察目标躯体及物种贴图后触发（供 <c>SkinSync</c> 应用自定义伤势与肌肉贴图）。</summary>
        public static event Action<WoundView, Body, int> OnWoundViewTargetApplied;

        /// <summary>联机角色躯体首次初始化完成或同 <c>netId</c> 换物种重建网格（<c>RebuildBodyMesh</c>）完毕后触发（参数：body, netBodyId, species, isRebuild）。</summary>
        public static event Action<Body, ushort, int, bool> OnBodyReadyOrRebuilt;

        public static IReadOnlyList<ExternalSettingsTab> RegisteredSettingsTabs => SettingsTabs;
        public static IReadOnlyList<ExternalPlayerAction> RegisteredPlayerActions => PlayerActions;

        /// <summary>
        /// 注册一个嵌入在 Together 设置窗口内的外部模组配置页签（签名兼容 <c>CuSaveManager.ExternalTabRegistry</c> 风格）。
        /// </summary>
        public static void RegisterSettingsTab(
            string id,
            Func<string> titleProvider,
            Action drawImGui,
            Func<string> statusProvider = null,
            Action onReset = null) {
            if (string.IsNullOrWhiteSpace(id) || titleProvider == null || drawImGui == null) {
                return;
            }
            for (int i = 0; i < SettingsTabs.Count; i++) {
                if (string.Equals(SettingsTabs[i].Id, id, StringComparison.OrdinalIgnoreCase)) {
                    SettingsTabs[i].TitleProvider = titleProvider;
                    SettingsTabs[i].DrawImGui = drawImGui;
                    SettingsTabs[i].StatusProvider = statusProvider;
                    SettingsTabs[i].OnReset = onReset;
                    return;
                }
            }
            SettingsTabs.Add(new ExternalSettingsTab {
                Id = id,
                TitleProvider = titleProvider,
                DrawImGui = drawImGui,
                StatusProvider = statusProvider,
                OnReset = onReset,
            });
            ModLog.Debug("已注册外部设置分页: " + id, LogDomain.Interop);
        }

        public static bool UnregisterSettingsTab(string id) {
            if (string.IsNullOrWhiteSpace(id)) {
                return false;
            }
            return SettingsTabs.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        /// <summary>
        /// 注册外部模组模态窗口输入阻塞查询委托。当返回 <c>true</c> 时，Together 自动屏蔽局内交互快捷键与移动输入。
        /// </summary>
        public static void RegisterExternalInputBlocker(string modId, Func<bool> isBlockingInput) {
            if (string.IsNullOrWhiteSpace(modId) || isBlockingInput == null) {
                return;
            }
            InputBlockers[modId] = isBlockingInput;
        }

        public static bool UnregisterExternalInputBlocker(string modId) {
            return !string.IsNullOrWhiteSpace(modId) && InputBlockers.Remove(modId);
        }

        public static bool IsAnyExternalInputBlockerActive() {
            if (InputBlockers.Count == 0) {
                return false;
            }
            foreach (KeyValuePair<string, Func<bool>> kv in InputBlockers) {
                try {
                    if (kv.Value != null && kv.Value()) {
                        return true;
                    }
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.Interop, "ExternalInputBlocker(" + kv.Key + ")", ex);
                }
            }
            return false;
        }

        /// <summary>尝试获取主菜单联机按钮的 <see cref="RectTransform"/> 以便并排布局按钮。</summary>
        public static bool TryGetMainMenuMpButtonRect(out RectTransform buttonRect) {
            buttonRect = UIManager.MP_MENU_BUTTON != null ? UIManager.MP_MENU_BUTTON.GetComponent<RectTransform>() : null;
            return buttonRect != null;
        }

        /// <summary>向局内右键队友弹出的玩家交互菜单注册自定义动作按钮。</summary>
        public static void RegisterPlayerInteraction(
            string id,
            Func<Body, string> labelProvider,
            Func<Body, bool> canExecute,
            Action<Body> onExecute) {
            if (string.IsNullOrWhiteSpace(id) || labelProvider == null || onExecute == null) {
                return;
            }
            for (int i = 0; i < PlayerActions.Count; i++) {
                if (string.Equals(PlayerActions[i].Id, id, StringComparison.OrdinalIgnoreCase)) {
                    PlayerActions[i].LabelProvider = labelProvider;
                    PlayerActions[i].CanExecute = canExecute;
                    PlayerActions[i].Execute = onExecute;
                    return;
                }
            }
            PlayerActions.Add(new ExternalPlayerAction {
                Id = id,
                LabelProvider = labelProvider,
                CanExecute = canExecute,
                Execute = onExecute,
            });
        }

        public static bool UnregisterPlayerInteraction(string id) {
            return !string.IsNullOrWhiteSpace(id)
                && PlayerActions.RemoveAll(x => string.Equals(x.Id, id, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        internal static void NotifyMainMenuMpButtonReady(GameObject buttonObj) {
            if (buttonObj == null || OnMainMenuMpButtonReady == null) {
                return;
            }
            RectTransform rt = buttonObj.GetComponent<RectTransform>();
            if (rt == null) {
                return;
            }
            try {
                OnMainMenuMpButtonReady.Invoke(rt);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(NotifyMainMenuMpButtonReady), ex);
            }
        }

        internal static void NotifyWoundViewTargetApplied(WoundView wv, int species) {
            if (wv == null || OnWoundViewTargetApplied == null) {
                return;
            }
            try {
                OnWoundViewTargetApplied.Invoke(wv, wv.body, species);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(NotifyWoundViewTargetApplied), ex);
            }
        }

        internal static void NotifyBodyReadyOrRebuilt(NetBody nb, int species, bool isRebuild) {
            if (nb == null || nb.body == null || OnBodyReadyOrRebuilt == null) {
                return;
            }
            ushort netId = nb.syncBody != null ? nb.syncBody.Id : (ushort)0;
            try {
                OnBodyReadyOrRebuilt.Invoke(nb.body, netId, species, isRebuild);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(NotifyBodyReadyOrRebuilt), ex);
            }
        }
    }
}
