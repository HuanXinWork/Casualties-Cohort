using BepInEx.Configuration;
using BepInEx.Logging;
using CasualtiesTogetherUtils;
using Together.Diagnostics;
using UnityEngine;

namespace Together.UI.TeammateList;

/// <summary>
/// 局内队友状态列表子系统静态宿主：管理快捷键配置、观察者芯片状态与每帧更新/绘制入口。
/// </summary>
internal static class TeammateListHost {
    internal static ManualLogSource Log;
    internal static Roster Roster;
    internal static ConfigEntry<KeyboardShortcut> CollapseKey;
    internal static bool ViewerUnchipped;

    internal static void Init(Plugin plugin) {
        Log = Plugin.log;
        CollapseKey = plugin.Config.Bind(
            "TeammateList",
            "CollapseKey",
            new KeyboardShortcut(KeyCode.LeftAlt),
            "Key that collapses and opens the teammate list. A modifier can be included, for example LeftControl + F8.");
        Roster = new Roster();
        Bind.EnsureRow();
        RefreshViewerChip();
        ModLog.Info($"TeammateList integrated collapse={CollapseKey.Value}", LogDomain.UI);
    }

    internal static void Tick() {
        if (Roster == null || !Multiplayer.IsNetworkActiveAndIsInGame()) {
            return;
        }
        Roster.Tick();
    }

    internal static void Draw() {
        if (Roster == null || !Multiplayer.IsNetworkActiveAndIsInGame()) {
            return;
        }
        Roster.Draw();
    }

    internal static void RefreshViewerChip() {
        bool next = Util.IsUnchipped();
        ScavPlayer local = ScavPlayer.LocalPlayer;
        if (local != null) {
            next = local.IsUnchipped();
        }
        ViewerUnchipped = next;
    }

    internal static void NoteUnchipped(ScavPlayer plr, bool unchipped) {
        if (plr == null) {
            return;
        }
        if (plr == ScavPlayer.LocalPlayer || plr.IsLocal) {
            ViewerUnchipped = unchipped;
        }
        Roster?.NoteTargetUnchipped(plr, unchipped);
    }

    internal static void NoteLayerReady() {
        RefreshViewerChip();
        Roster?.ResampleChipFlags();
    }
}
