namespace Together.MpConstants {
    /// <summary>
    /// 原版游戏常用音效键名、特效预制体名与本地化键常量，避免散落硬编码拼写错误。
    /// </summary>
    public static class VanillaAssetKeys {
        // 高频交互与医疗/合成音效
        public const string SoundCombine = "combine";
        public const string SoundUnlock = "unlock";
        public const string SoundBeep = "beep";
        public const string SoundGore = "gore";
        public const string SoundSelfDestruct = "selfdestruct1";
        public const string SoundProstheticApply = "prostheticapply";
        public const string SoundAnimalHitSweet = "animalhitsweet";

        // 常用世界生成与伤害特效预制体名
        public const string PrefabBloodExplosion = "BloodExplosion";
        public const string PrefabBulletCasing = "casing";
        public const string PrefabDroppings = "droppings";

        // 常用原版角色台词与提示键
        public const string TalkSuicide = "suicide";
        public const string TalkSelfDestruct = "selfdestruct";
        public const string TalkLoud = "loud";
    }

    /// <summary>
    /// 集中收口所有兼容原版 <c>v5.5.x</c> 线上协议的命名通道与 <c>CustomCommand</c> 协议键常量（严禁修改字符串字面值，保证 100% 线上字节兼容）。
    /// </summary>
    public static class CustomWireCommands {
        // NamedWriter / CustomReceiver 顶层通道名
        public const string ChannelCustomCommand = "CustomCommand";
        public const string ChannelFacePause = "FacePause";
        public const string ChannelCleanWorld = "cleanworld";

        /// <summary>Together.Api 预留给外部模组的大体积分块可靠通道前缀（走既有 CustomMessage 路由，与原版 v5.5.x 线协议完全兼容）。</summary>
        public const string ChannelApiLargePayload = "Together.Api.LargeChunk";

        // 社交互动与救援命令键
        public const string FallArm = "fall_arm";
        public const string FallHit = "fall_hit";
        public const string PatDone = "pat_done";
        public const string PatFx = "pat_fx";
        public const string HugAsk = "hug_ask";
        public const string HugGo = "hug_go";
        public const string DuneBiteAsk = "dune_bite_ask";
        public const string DuneBiteGo = "dune_bite_go";

        // 陷阱、环境与物品命令键
        public const string ColdPuffBlow = "coldpuff_blow";
        public const string TorchWet = "torch_wet";
        public const string CeilingGun = "ceiling_gun";
        public const string HealingTouch = "healing_touch";
        public const string UnstableArm = "unstable_arm";
        public const string BloodsuckerTouch = "bloodsucker_touch";
        public const string ProsthesisPatch = "prosthesis_patch";
        public const string LayerContinue = "layer_continue";
        public const string LayerSave = "layer_save";

        // 零线协议破坏扩展命令键（旧版 v5.5.3.0 对未知 CustomCommand 键静默忽略，保障三版本混连 100% 兼容）
        public const string UseAck = "use_ack";
        public const string NonDescSync = "nondesc_sync";
        public const string NonDescReq = "nondesc_req";
        public const string StimSync = "stim_sync";
        public const string UnicodeName = "unicode_name";

        // Fullgame 终局与剧情同步命令键
        public const string AscentGo = "ascent_go";
        public const string BlueHeartRemoved = "blueheartremoved";
        public const string CargoOff = "cargo_off";
        public const string EndingFlag = "ending_flag";
        public const string EndLabReady = "endlab_ready";
        public const string GuardPose = "guard_pose";
        public const string ScientistGo = "scientist_go";
        public const string ScientistHp = "scientist_hp";
        public const string ScientistReady = "scientist_ready";
        public const string SurgeryBags = "surgery_bags";
        public const string SurgeryTp = "surgery_tp";
    }
}
