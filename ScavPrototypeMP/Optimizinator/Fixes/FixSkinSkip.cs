using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 当屏幕分辨率、UI 缩放与强制重算标记未变化且无菜单/投票弹窗打开时，跳过每遍 IMGUI 的皮肤纹理与边距重算。
/// </summary>
internal static class FixSkinSkip {
    internal static long Skip;
    internal static long Run;

    private static float _lastScale = float.NaN;
    private static int _lastW = -1;
    private static int _lastH = -1;
    private static bool _stamped;

    internal static string Evidence() => "skinSkip=" + Skip + " skinRun=" + Run;

    internal static bool Allow() {
        if (OptimizerMain.CacheImguiStyles != null && !OptimizerMain.CacheImguiStyles.Value) {
            return true;
        }

        bool recalc = UIBullshit._recalculate_skin;
        float scale = UIBullshit.uiScale;
        int w = Screen.width;
        int h = Screen.height;
        bool changed = float.IsNaN(_lastScale)
            || Mathf.Abs(scale - _lastScale) > 0.0001f
            || w != _lastW
            || h != _lastH
            || recalc
            || !_stamped;
        bool prompt = UIChoicePrompt.prompt_show_progress != 0f;
        bool menu = prompt || UIBullshit.IsAnyMenuOpen() || UIMainMenu.IsOpen() || UIMainMenu.mainmenu_open;

        if (changed || menu) {
            _lastScale = scale;
            _lastW = w;
            _lastH = h;
            _stamped = true;
            FixGuiStyle.NoteSkinApplied();
            Run++;
            return true;
        }

        Skip++;
        return false;
    }
}
