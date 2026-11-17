namespace Together.MpConstants {
    /// <summary>
    /// 全局交互距离、物理扫描半径与同步视距单点真相常量表（主客机校验统一引用同一常量，消除双端半径不一致导致的静默拒绝）。
    /// </summary>
    public static class GameplayRanges {
        /// <summary>常规近身玩家交互最大距离（推人、拥抱、背负、查看伤势等）。</summary>
        public const float PlayerInteractDistance = 6f;
        public const float PlayerInteractDistanceSqr = PlayerInteractDistance * PlayerInteractDistance;

        /// <summary>客户端维持商人交易界面开启的最大允许距离。</summary>
        public const float TraderMenuKeepOpenDistance = 8f;
        public const float TraderMenuKeepOpenDistanceSqr = TraderMenuKeepOpenDistance * TraderMenuKeepOpenDistance;

        /// <summary>合成系统自动搜索周围地面可用材料圆的半径。</summary>
        public const float CraftGroundScanRadius = 10f;
        public const float CraftGroundScanRadiusSqr = CraftGroundScanRadius * CraftGroundScanRadius;

        /// <summary>物体近距高频更新判定半径。</summary>
        public const float ObjectNearbyRadius = 12f;
        public const float ObjectNearbyRadiusSqr = ObjectNearbyRadius * ObjectNearbyRadius;

        /// <summary>服务端近战攻击对建筑造成伤害的最大合法距离校验。</summary>
        public const float MeleeBuildingMaxServerDistance = 15f;
        public const float MeleeBuildingMaxServerDistanceSqr = MeleeBuildingMaxServerDistance * MeleeBuildingMaxServerDistance;

        /// <summary>服务端递交物品（GiveItem）与商人购买请求的最大容差校验距离（兼顾网络 RTT 位移余量）。</summary>
        public const float ServerExchangeToleranceDistance = 20f;
        public const float ServerExchangeToleranceDistanceSqr = ServerExchangeToleranceDistance * ServerExchangeToleranceDistance;

        /// <summary>远端实体低频发包预算分界距离。</summary>
        public const float SendBudgetFarDistance = 36f;
        public const float SendBudgetFarDistanceSqr = SendBudgetFarDistance * SendBudgetFarDistance;

        /// <summary>玩家布娃娃 SyncGroup3 订阅半径。</summary>
        public const float RagdollSyncGroupRadius = 40f;
        public const float RagdollSyncGroupRadiusSqr = RagdollSyncGroupRadius * RagdollSyncGroupRadius;

        /// <summary>世界物体活跃模拟与网络注册保持半径。</summary>
        public const float SimulationKeepRadius = 64f;
        public const float SimulationKeepRadiusSqr = SimulationKeepRadius * SimulationKeepRadius;
    }
}
