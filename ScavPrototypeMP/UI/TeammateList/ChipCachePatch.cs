using HarmonyLib;

namespace Together.UI.TeammateList;

/// <summary>
/// 拦截个体与世界芯片状态变更，实时同步队友列表健康遥测可读性缓存。
/// </summary>
[HarmonyPatch(typeof(NetBody), nameof(NetBody.SetUnchipped))]
internal static class ChipCachePatch {
    private static void Postfix(NetBody __instance, bool unchippedVal) {
        if (__instance == null) {
            return;
        }
        TeammateListHost.NoteUnchipped(__instance.plr, unchippedVal);
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SetUnchipped))]
internal static class WorldUnchippedChipCachePatch {
    private static void Postfix(bool unchipped) {
        TeammateListHost.ViewerUnchipped = unchipped;
        TeammateListHost.Roster?.ResampleChipFlags();
    }
}
