using System;

namespace Together.Diagnostics {
    /// <summary>
    /// 联机子系统结构化日志与黑匣子追踪域位掩码（支持在 Debug 模式下按域单独开启或静音）。
    /// </summary>
    [Flags]
    public enum LogDomain : uint {
        None      = 0,
        Core      = 1u << 0,
        Transport = 1u << 1,
        Lobby     = 1u << 2,
        Movement  = 1u << 3,
        Item      = 1u << 4,
        Container = 1u << 5,
        Craft     = 1u << 6,
        Trader    = 1u << 7,
        Medical   = 1u << 8,
        Combat    = 1u << 9,
        Minigame  = 1u << 10,
        World     = 1u << 11,
        Story     = 1u << 12,
        UI        = 1u << 13,
        Security  = 1u << 14,
        Interop   = 1u << 15,
        Bootstrap = Core,
        ObjectSync= Item | World,
        All       = 0xFFFFFFFFu,
    }

    /// <summary>
    /// 实时标识当前阻断玩家移动或局内面板快捷键的所有激活源（用于快速定位“角色动不了/面板打不开”）。
    /// </summary>
    [Flags]
    public enum InputBlockFlags : ushort {
        None                 = 0,
        ConsoleOpen          = 1 << 0,
        ChatInputFocused     = 1 << 1,
        PauseMenuOpen        = 1 << 2,
        WoundViewOpen        = 1 << 3,
        RadialInventoryOpen  = 1 << 4,
        TradeMenuOpen        = 1 << 5,
        CraftingSearchFocus  = 1 << 6,
        MinigameActive       = 1 << 7,
        CutsceneActive       = 1 << 8,
        SelfDestructLocked   = 1 << 9,
        BodyDeadOrUnconscious= 1 << 10,
        ExternalModModal     = 1 << 11,
    }

    /// <summary>
    /// 环形黑匣子事件类别编码（纯值类型记录，零字符串分配）。
    /// </summary>
    public enum FlightEventKind : byte {
        None            = 0,
        SessionStart    = 1,
        SessionEnd      = 2,
        PlayerJoin      = 3,
        PlayerLeave     = 4,
        PacketSend      = 5,
        PacketReceive   = 6,
        PacketReject    = 7,
        CommitSubmit    = 8,
        CommitReceipt   = 9,
        CommitTimeout   = 10,
        PanelToggle     = 11,
        GuardDenied     = 12,
        Warning         = 13,
        Error           = 14,
        Exception       = 15,
    }
}
