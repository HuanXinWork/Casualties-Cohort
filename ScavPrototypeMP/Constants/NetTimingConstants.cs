namespace Together.MpConstants {
    /// <summary>
    /// 网络节流周期、事务重试与超时、抖动缓冲水位及包分片协议常量表。
    /// </summary>
    public static class NetTimingConstants {
        /// <summary>客户端移动或布娃娃状态下的角色位置发包最小间隔（~28.6 Hz）。</summary>
        public const float CharacterSyncMovingInterval = 0.035f;

        /// <summary>客户端站立原地转头/瞄准时的角色位置发包最小间隔（~15.4 Hz）。</summary>
        public const float CharacterSyncAimingInterval = 0.065f;

        /// <summary>客户端完全静止时的角色心跳同步间隔（10.0 Hz）。</summary>
        public const float CharacterSyncIdleInterval = 0.10f;

        /// <summary>客户端原地丢弃物品进入 poseHold 等待新坐标包的最大解冻超时（秒）。</summary>
        public const float DroppedItemPoseHoldTimeout = 0.25f;

        /// <summary>物品移动（MoveCommit）与合成（CraftCommit）未收到回执时的重发周期（秒）。</summary>
        public const float CommitResendIntervalSeconds = 2.0f;

        /// <summary>客户端在途事务（MoveCommit / CraftCommit）最大存活寿命（秒），超时自动释放锁防死锁。</summary>
        public const float CommitMaxLifetimeSeconds = 10.0f;

        /// <summary>递物确认弹窗（GiveItem Offer）在服务端的有效等待超时（秒）。</summary>
        public const float ItemOfferExpireSeconds = 15.0f;

        /// <summary>LiteEntitySystem 客户端就绪状态缓冲队列触发追赶裁剪的水位上限（~200ms @ 30Hz）。</summary>
        public const int EntityStateCatchUpKeepReady = 6;

        /// <summary>地面散落且处于视距外的非活跃物体客户端状态应用降频间隔（Tick 数）。</summary>
        public const int OffscreenLooseItemUpdateStrideTicks = 10;

        /// <summary>多目标健康/止痛药聚合不可靠包的单包软切分上限（字节）。</summary>
        public const int UnreliableHealthBatchMaxBytes = 1000;

        /// <summary>跨模组大负载自动分片通道（LargePayloadChannel）的单帧分片大小（16 KB，远低于 65535B 单包上限）。</summary>
        public const int LargeChannelChunkBytes = 16 * 1024;

        /// <summary>跨模组大负载自动分片通道允许的最大总载荷体积（8 MB，防止异常包耗尽内存）。</summary>
        public const int LargeChannelMaxTotalBytes = 8 * 1024 * 1024;

        /// <summary>跨模组大负载未完成分片重组会话的过期清理时间（秒）。</summary>
        public const float LargeChannelSessionTimeoutSeconds = 30.0f;

        /// <summary>角色主动自毁伤害事件唯一标识哨兵值（与原版协议约定保持一致）。</summary>
        public const float SelfDestructDamageIdentifier = 5434534f;
    }
}
