using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 按调用点与皮肤代数缓存 <see cref="GUIStyle"/> 拷贝副本，仅在皮肤重建时失效刷新。
/// </summary>
internal static class FixGuiStyle {
    internal static long Hit;
    internal static long New;
    internal static int SkinGen = 1;

    private sealed class StyleSlot {
        internal GUIStyle Style;
        internal GUIStyle Source;
        internal int Gen;
    }

    private static readonly Dictionary<int, StyleSlot> Styles = new Dictionary<int, StyleSlot>();

    // 动态 Transpiler 站点从 10000 起步，与源码显式站点号（1..999）隔离避免冲突
    private static int _nextSite = 10000;

    internal static string Evidence() => "styleHit=" + Hit + " styleNew=" + New;

    internal static void NoteSkinApplied() => SkinGen++;

    internal static void Arm(Harmony harmony) {
        ConstructorInfo style = AccessTools.Constructor(typeof(GUIStyle), new[] { typeof(GUIStyle) });
        ImguiScan.Arm(harmony, new[] { style }, AccessTools.Method(typeof(FixGuiStyle), nameof(Transpile)));
    }

    internal static GUIStyle Copy(GUIStyle source, int site) {
        if (source == null) {
            return new GUIStyle();
        }
        if (!Styles.TryGetValue(site, out StyleSlot slot) || slot == null) {
            slot = new StyleSlot();
            Styles[site] = slot;
        }
        if (slot.Style != null && slot.Gen == SkinGen && ReferenceEquals(slot.Source, source)) {
            GUIStyle style = slot.Style;
            if (style.richText != source.richText) {
                style.richText = source.richText;
            }
            if (style.fontSize != source.fontSize) {
                style.fontSize = source.fontSize;
            }
            if (style.wordWrap != source.wordWrap) {
                style.wordWrap = source.wordWrap;
            }
            Hit++;
            return style;
        }

        slot.Style = new GUIStyle(source);
        slot.Source = source;
        slot.Gen = SkinGen;
        New++;
        return slot.Style;
    }

    private static IEnumerable<CodeInstruction> Transpile(IEnumerable<CodeInstruction> instructions) {
        ConstructorInfo style = AccessTools.Constructor(typeof(GUIStyle), new[] { typeof(GUIStyle) });
        MethodInfo copy = AccessTools.Method(typeof(FixGuiStyle), nameof(Copy));

        foreach (CodeInstruction ins in instructions) {
            if (ins.opcode != OpCodes.Newobj || ins.operand is not ConstructorInfo ctor || ctor != style) {
                yield return ins;
                continue;
            }
            int site = _nextSite++;
            CodeInstruction id = new CodeInstruction(OpCodes.Ldc_I4, site);
            id.labels.AddRange(ins.labels);
            id.blocks.AddRange(ins.blocks);
            yield return id;
            yield return new CodeInstruction(OpCodes.Call, copy);
        }
    }
}
