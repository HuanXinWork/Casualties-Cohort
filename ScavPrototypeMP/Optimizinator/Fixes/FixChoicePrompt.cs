using Together;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 在 <see cref="UIChoicePrompt"/> 绘制投票接受/拒绝按钮期间临时切换为细边框按钮皮肤，绘制完毕后还原默认小尺寸按钮皮肤。
/// </summary>
internal static class FixChoicePrompt {
    private static bool _sized;

    internal static void Begin() {
        _sized = false;
        if (UIChoicePrompt.prompt_show_progress == 0f || GUI.skin == null || GUI.skin.button == null) {
            return;
        }
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, false);
        _sized = true;
    }

    internal static void End() {
        if (!_sized || GUI.skin == null || GUI.skin.button == null) {
            return;
        }
        _sized = false;
        UIBullshit._GUI_SetButtonSkinTexture(GUI.skin.button, true);
    }
}
