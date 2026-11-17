using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 防止局内 HUD 在同一帧同一 IMGUI 事件类型内重复绘制两遍（Layout 与 Repaint 属于不同事件类型，各自正常执行一次）。
/// </summary>
internal static class FixGuiLayout {
    internal static long Skip;

    private static int _frame = -1;
    private static int _event = int.MinValue;
    private static int _count;

    internal static string Evidence() => "guiHud=" + Skip;

    internal static bool Allow() {
        int frame = Time.frameCount;
        int kind = Event.current == null ? -1 : (int)Event.current.type;
        if (frame != _frame || kind != _event) {
            _frame = frame;
            _event = kind;
            _count = 0;
        }
        _count++;
        if (_count == 1) {
            return true;
        }
        Skip++;
        return false;
    }
}
