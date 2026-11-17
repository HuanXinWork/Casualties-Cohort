using UnityEngine;

namespace Together.MpConstants {
    /// <summary>
    /// Unity 物理图层名称常量与惰性缓存掩码（消除热路径重复调用 <see cref="LayerMask.GetMask"/> 的字符串哈希开销）。
    /// </summary>
    public static class PhysicsLayers {
        public const string ItemLayerName = "Item";
        public const string GroundLayerName = "Ground";
        public const string BodyLayerName = "Body";
        public const string BuildingLayerName = "Building";

        private static int _itemMask;
        private static int _groundMask;
        private static int _groundLayerIndex = -1;
        private static bool _initialized;

        private static void EnsureInitialized() {
            if (_initialized) {
                return;
            }
            int item = LayerMask.GetMask(ItemLayerName);
            int ground = LayerMask.GetMask(GroundLayerName);
            int groundIndex = LayerMask.NameToLayer(GroundLayerName);
            if (item != 0 || ground != 0) {
                _itemMask = item;
                _groundMask = ground;
                _groundLayerIndex = groundIndex;
                _initialized = true;
            }
        }

        /// <summary>缓存的 <c>Ground</c> 图层索引值（0..31）。</summary>
        public static int GroundLayerIndex {
            get {
                EnsureInitialized();
                return _groundLayerIndex >= 0 ? _groundLayerIndex : LayerMask.NameToLayer(GroundLayerName);
            }
        }

        /// <summary>缓存的 <c>Item</c> 图层掩码。</summary>
        public static int ItemMask {
            get {
                EnsureInitialized();
                return _itemMask != 0 ? _itemMask : LayerMask.GetMask(ItemLayerName);
            }
        }

        /// <summary>缓存的 <c>Ground</c> 地形阻挡图层掩码。</summary>
        public static int GroundMask {
            get {
                EnsureInitialized();
                return _groundMask != 0 ? _groundMask : LayerMask.GetMask(GroundLayerName);
            }
        }
    }
}
