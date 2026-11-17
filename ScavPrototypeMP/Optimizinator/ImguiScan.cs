using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using Together.Diagnostics;

namespace KrokV5Optimization;

/// <summary>
/// 扫描 <c>Together</c> 与 <c>CasualtiesTogetherUtils</c> 命名空间下的 IMGUI 方法，将高频临时对象构造器重写为站点池化调用。
/// </summary>
internal static class ImguiScan {
    internal static int Patched;

    internal static void Arm(Harmony harmony, ConstructorInfo[] wanted, MethodInfo transpile) {
        HashSet<ConstructorInfo> set = new HashSet<ConstructorInfo>(wanted);
        const BindingFlags flags = BindingFlags.Instance | BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic | BindingFlags.DeclaredOnly;

        foreach (Type type in typeof(UIBullshit).Assembly.GetTypes()) {
            if (type.Namespace != "Together" && type.Namespace != "CasualtiesTogetherUtils") {
                continue;
            }

            MethodInfo[] methods;
            try {
                methods = type.GetMethods(flags);
            } catch (Exception ex) {
                ModLog.Debug($"ImguiScan skipped GetMethods on {type.FullName}: {ex.Message}", LogDomain.UI);
                continue;
            }

            foreach (MethodInfo method in methods) {
                if (method.IsAbstract || method.ContainsGenericParameters || method.Name == "_GUI_DrawMPMainMenu") {
                    continue;
                }
                if (!Mentions(method, set)) {
                    continue;
                }
                try {
                    harmony.Patch(method, transpiler: new HarmonyMethod(transpile));
                    Patched++;
                } catch (Exception ex) {
                    ModLog.Warning($"[KrokV5Opt] imgui rewrite skipped {type.Name}.{method.Name}: {ex.Message}", LogDomain.UI);
                }
            }
        }
    }

    private static bool Mentions(MethodInfo method, HashSet<ConstructorInfo> wanted) {
        MethodBody body = method.GetMethodBody();
        byte[] il = body?.GetILAsByteArray();
        if (il == null) {
            return false;
        }

        for (int i = 0; i < il.Length - 4; i++) {
            // 0x73 = OpCodes.Newobj
            if (il[i] != 0x73) {
                continue;
            }
            int token = BitConverter.ToInt32(il, i + 1);
            try {
                if (method.Module.ResolveMethod(token) is ConstructorInfo ctor && wanted.Contains(ctor)) {
                    return true;
                }
            } catch (Exception ex) {
                ModLog.Debug($"ImguiScan token resolve skipped in {method.Name}@{i}: {ex.Message}", LogDomain.UI);
            }
        }
        return false;
    }
}
