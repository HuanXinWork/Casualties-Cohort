using System.Runtime.CompilerServices;

namespace Together.MpConstants {
    /// <summary>
    /// 物品父容器同步字段 <c>container_data1</c> 的线上字节编码规范（严格对齐原版 v5.5.x 线协议：<c>0=地面, 1=容器, 100=穿戴, 200+slot=随身槽位</c>）。
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
        /// <summary>穿戴在角色装备栏上（线上编码值固定为 100）。</summary>
        Wearable = 100,
    }

    /// <summary>
    /// 统一收口全工程所有容器状态字节的判断与槽位编解码逻辑（<c>0=World, 1=Container, 100=Wearable, 200+slot=BodySlot</c>）。
    /// </summary>
    public static class ContainerEncoding {
        /// <summary>地面世界散落态编码值。</summary>
        public const byte WorldByte = 0;

        /// <summary>背包/箱子容器内部态编码值。</summary>
        public const byte ContainerByte = 1;

        /// <summary>医疗/特殊依附态编码值。</summary>
        public const byte MedicalByte = 2;

        /// <summary>角色穿戴装备态编码值（原版 v5.5.x 线协议固定为 100）。</summary>
        public const byte WearableByte = 100;

        /// <summary>兼容别名：穿戴态基准值（100）。</summary>
        public const byte WearableOffset = WearableByte;

        /// <summary>角色随身物品栏槽位起始编码值（原版 v5.5.x 线协议：实际槽位下标 = <c>data - 200</c>）。</summary>
        public const byte BodySlotOffset = 200;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsLooseInWorld(byte data) => data == WorldByte;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsInsideContainer(byte data) => data == ContainerByte;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsWornOnBody(byte data) => data == WearableByte;

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsHeldInBodySlot(byte data) => data >= BodySlotOffset;

        /// <summary>判断物品是否附着在角色躯体上（穿戴 100 或随身槽位 &gt;= 200）。</summary>
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static bool IsAttachedToBody(byte data) => data == WearableByte || data >= BodySlotOffset;

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
        public static byte EncodeWearable() => WearableByte;
    }
}
