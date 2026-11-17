using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 按调用点（site ID）缓存并复用 <see cref="GUIContent"/> 实例，消除 IMGUI Layout/Repaint 双遍每帧临时构造器分配。
/// </summary>
internal static class FixGuiContent {
    internal static long Rent;
    internal static long New;

    private static readonly Dictionary<int, GUIContent> Contents = new Dictionary<int, GUIContent>();

    // 动态 Transpiler 站点从 10000 起步，与源码中显式传入的静态站点号（1..999）彻底隔离避免碰撞
    private static int _nextSite = 10000;

    internal static string Evidence() => "guiRent=" + Rent + " guiNew=" + New;

    internal static void Arm(Harmony harmony) {
        ConstructorInfo content = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string) });
        ConstructorInfo contentTip = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string), typeof(string) });
        ImguiScan.Arm(harmony, new[] { content, contentTip }, AccessTools.Method(typeof(FixGuiContent), nameof(Transpile)));
    }

    internal static GUIContent Content(string text, int site) {
        if (!Contents.TryGetValue(site, out GUIContent content) || content == null) {
            content = new GUIContent();
            Contents[site] = content;
            New++;
        } else {
            Rent++;
        }
        content.text = text ?? "";
        content.image = null;
        content.tooltip = null;
        return content;
    }

    internal static GUIContent ContentTip(string text, string tip, int site) {
        GUIContent content = Content(text, site);
        content.tooltip = tip;
        return content;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
        ConstructorInfo content = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string) });
        ConstructorInfo contentTip = AccessTools.Constructor(typeof(GUIContent), new[] { typeof(string), typeof(string) });
        MethodInfo contentCall = AccessTools.Method(typeof(FixGuiContent), nameof(Content));
        MethodInfo tipCall = AccessTools.Method(typeof(FixGuiContent), nameof(ContentTip));

        foreach (CodeInstruction ins in instructions) {
            if (ins.opcode != OpCodes.Newobj || ins.operand is not ConstructorInfo ctor) {
                yield return ins;
                continue;
            }
            MethodInfo call = ctor == content ? contentCall : (ctor == contentTip ? tipCall : null);
            if (call == null) {
                yield return ins;
                continue;
            }

            // 将原 newobj 上的跳转标签与异常块迁移到压栈 site ID 的新指令上，保持分支目标合法
            int site = _nextSite++;
            CodeInstruction id = new CodeInstruction(OpCodes.Ldc_I4, site);
            id.labels.AddRange(ins.labels);
            id.blocks.AddRange(ins.blocks);
            yield return id;
            yield return new CodeInstruction(OpCodes.Call, call);
        }
    }
}
