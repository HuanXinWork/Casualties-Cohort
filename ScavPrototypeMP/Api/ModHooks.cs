using System;
using Together.Api;
using Together.Diagnostics;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 面向外部存档/世界控制模组（如 <c>CuSaveManager</c> 的 <c>TogetherModHooksBridge</c>）开放的轻量静态门面。
    /// <para>
    /// 设计约束：仅暴露基础 BCL 与 Unity 原生类型，不暴露任何 <c>LiteNetLib</c> 或内部同步类型；
    /// 视图与面板钩子统一由 <see cref="TogetherPanelApi"/> 承载，避免双通道同时订阅产生重复回调。
    /// </para>
    /// </summary>
    public static class ModHooks {
        public const int ApiVersion = 1;

        /// <summary>当联机模组已加载但当前处于未开房单机状态（<c>!Net.IsRunning</c>）时，是否原生放行单人模式。</summary>
        public static bool AllowSingleplayerPassthrough { get; set; }

        /// <summary>服务端生成世界前可选查询的自定义固定世界种子提供器（返回非空非零值时生效）。</summary>
        public static Func<int?> CustomWorldSeedProvider { get; set; }

        /// <summary>服务端或客户端世界生成种子确认并对齐进度后触发（参数：appliedSeed, isHost）。</summary>
        public static event Action<int, bool> OnWorldSeedApplied;

        /// <summary>是否在联机读档时启用精确存档坐标恢复。</summary>
        public static bool RestoreSavedPositionsOnLoad { get; set; }

        /// <summary>按 (playerId, persistentId, body) 解析玩家出生点覆写坐标；返回非空坐标时跳过默认随机出生。</summary>
        public static Func<ushort, string, Body, Vector2?> SpawnPositionOverride { get; set; }

        /// <summary>服务端触发全员阵亡自动退回主菜单（<c>AutoExitWhenAllDied</c>）前的拦截守卫；返回 <c>true</c> 时暂缓退房。</summary>
        public static Func<bool> ShouldBlockAutoExitOnAllDead { get; set; }

        internal static int LastAppliedCustomWorldSeed;
        internal static int PendingClientCustomWorldSeed;

        internal static int? QueryCustomWorldSeed() {
            Func<int?> provider = CustomWorldSeedProvider;
            if (provider == null) {
                return null;
            }
            try {
                return provider.Invoke();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(CustomWorldSeedProvider), ex);
                return null;
            }
        }

        internal static void NotifyWorldSeedApplied(int seed, bool isHost) {
            Action<int, bool> handlers = OnWorldSeedApplied;
            if (handlers == null) {
                return;
            }
            foreach (Delegate d in handlers.GetInvocationList()) {
                try {
                    ((Action<int, bool>)d).Invoke(seed, isHost);
                } catch (Exception ex) {
                    ModLog.Exception(LogDomain.Interop, nameof(OnWorldSeedApplied), ex);
                }
            }
        }

        internal static bool TryResolveSpawnOverride(ScavPlayer plr, Body body, out Vector2 targetPos) {
            targetPos = default;
            if (!RestoreSavedPositionsOnLoad) {
                return false;
            }
            Func<ushort, string, Body, Vector2?> callback = SpawnPositionOverride;
            if (callback == null || plr == null || body == null) {
                return false;
            }
            try {
                Vector2? resolved = callback.Invoke(plr.playerId.id, plr.GetPersistentId(), body);
                if (resolved.HasValue) {
                    targetPos = resolved.Value;
                    return true;
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(SpawnPositionOverride), ex);
            }
            return false;
        }

        internal static bool QueryBlockAutoExitOnAllDead() {
            Func<bool> guard = ShouldBlockAutoExitOnAllDead;
            if (guard == null) {
                return false;
            }
            try {
                return guard.Invoke();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(ShouldBlockAutoExitOnAllDead), ex);
                return false;
            }
        }
    }
}
