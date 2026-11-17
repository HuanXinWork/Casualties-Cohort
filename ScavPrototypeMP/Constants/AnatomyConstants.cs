using System.Runtime.CompilerServices;

namespace Together.MpConstants {
    /// <summary>
    /// 原版 <see cref="Body.limbs"/> 15 个肢体槽位的标准解剖学下标枚举（严格对齐游戏 v1.0.2 预制体数组顺序）。
    /// </summary>
    public enum LimbIndex : byte {
        Head = 0,
        UpperTorso = 1,
        LowerTorso = 2,
        PrimaryUpperArm = 3,
        PrimaryForearm = 4,
        PrimaryHand = 5,
        SecondaryUpperArm = 6,
        SecondaryForearm = 7,
        SecondaryHand = 8,
        FrontThigh = 9,
        FrontCalf = 10,
        FrontFoot = 11,
        BackThigh = 12,
        BackCalf = 13,
        BackFoot = 14,
    }

    /// <summary>
    /// 角色肢体数量、分组与生理边界常量，替代散落的 <c>.limbs[0]</c>、<c>.limbs[5]</c>、<c>15</c> 等硬编码数字。
    /// </summary>
    public static class AnatomyConstants {
        /// <summary>原版角色标准肢体总数。</summary>
        public const int StandardLimbCount = 15;

        /// <summary>主手槽位下标（对应 <see cref="LimbIndex.PrimaryHand"/>）。</summary>
        public const int PrimaryHandSlot = 0;

        /// <summary>副手槽位下标（对应 <see cref="LimbIndex.SecondaryHand"/>）。</summary>
        public const int SecondaryHandSlot = 1;

        /// <summary>商人维持交易所需的最低建筑生命值阈值（低于或等于此值视为重伤关闭交易）。</summary>
        public const float TraderMinAliveBuildingHealth = 200f;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsValidLimbIndex(Body body, int index) {
            return body != null && body.limbs != null && (uint)index < (uint)body.limbs.Length && body.limbs[index] != null;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static Limb GetHandLimbForSlot(Body body, int handSlot) {
            if (body == null || body.limbs == null) {
                return null;
            }
            int idx = handSlot == PrimaryHandSlot ? (int)LimbIndex.PrimaryHand : (int)LimbIndex.SecondaryHand;
            return (uint)idx < (uint)body.limbs.Length ? body.limbs[idx] : null;
        }
    }
}
