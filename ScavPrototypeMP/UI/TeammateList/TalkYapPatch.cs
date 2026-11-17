using HarmonyLib;
using System;
using System.Collections.Generic;

namespace Together.UI.TeammateList;

/// <summary>
/// 拦截角色说话事件，将最新台词实时推送至对应队友卡片底栏显示。
/// </summary>
[HarmonyPatch(typeof(Talker), "Talk", new Type[] {
    typeof(List<string>),
    typeof(Limb),
    typeof(bool),
    typeof(bool)
})]
internal static class TalkYapPatch {
    private static void Postfix(Talker __instance) {
        if (__instance == null || TeammateListHost.Roster == null) {
            return;
        }
        TeammateListHost.Roster.NoteYap(__instance.body, Roster.CurrentLine(__instance));
    }
}
