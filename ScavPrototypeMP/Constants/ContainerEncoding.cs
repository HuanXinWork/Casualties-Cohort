using System.Runtime.CompilerServices;

namespace Together.MpConstants {
    /// <summary>
    /// 物品父容器同步字段 <c>container_data1</c> 的线上字节编码规范（与原版 v5.5.x 线协议 100% 字节级兼容）。
    /// </summary>
    public enum ItemParentKind : byte {
        /// <summary>散落在地面世界中（无父级容器或槽位）。</summary>
        World = 0,
        /// <summary>装在背包、箱子或其他 <see cref="Container"/> 组件内部。</summary>
        Container = 1,
        /// <summary>处于医疗操作或特殊挂载占用态。</summary>
        MedicalSpecial = 2,
        /// <summary>抓钩尖端等特殊依附态。</summary>
        GrabberSpecial = 3,
    }

    /// <summary>
    /// 统一收口全工程所有容器状态字节的判断与槽位编解码逻辑，彻底消灭散落的 <c>== 0</c>、<c>== 1</c>、<c>&gt;= 100</c>、<c>&gt;= 200</c> 裸魔数。
    /// </summary>
    public static class ContainerEncoding {
        /// <summary>地面世界散落态编码值。</summary>
        public const byte WorldByte = 0;

        /// <summary>背包/箱子容器内部态编码值。</summary>
        public const byte ContainerByte = 1;

        /// <summary>医疗/特殊依附态编码值。</summary>
        public const byte MedicalByte = 2;

        /// <summary>角色随身物品栏槽位起始编码值（实际槽位下标 = <c>data - BodySlotOffset</c>）。</summary>
        public const byte BodySlotOffset = 100;

        /// <summary>角色穿戴装备栏起始编码值（穿戴槽位下标 = <c>data - WearableOffset</c>）。</summary>
        public const byte WearableOffset = 200;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLooseInWorld(byte data) => data == WorldByte;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInsideContainer(byte data) => data == ContainerByte;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHeldInBodySlot(byte data) => data >= BodySlotOffset && data < WearableOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWornOnBody(byte data) => data >= WearableOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAttachedToBody(byte data) => data >= BodySlotOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeBodySlot(int slotIndex) => checked((byte)(BodySlotOffset + slotIndex));

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static int DecodeBodySlot(byte data) => data - BodySlotOffset;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool TryDecodeBodySlot(byte data, out int slotIndex) {
            if (IsHeldInBodySlot(data)) {
                slotIndex = data - BodySlotOffset;
                return true;
            }
            slotIndex = -1;
            return false;
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static byte EncodeWearable(int wearIndex = 0) => checked((byte)(WearableOffset + wearIndex));
    }
}
