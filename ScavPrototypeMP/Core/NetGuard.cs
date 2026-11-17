using System;
using Together.MpConstants;
using Together.Diagnostics;
using UnityEngine;

namespace Together.Core {
    /// <summary>
    /// 服务端 <c>[ServerReceiver]</c> 声明式校验策略标志。
    /// </summary>
    [Flags]
    public enum CallerPolicy : byte {
        None             = 0,
        RequireWorld     = 1 << 0,
        RequireBody      = 1 << 1,
        RequireAlive     = 1 << 2,
        RequireConscious = 1 << 3,

        /// <summary>标准局内实体操作要求：世界已生成 + 角色存在且存活。</summary>
        AliveInWorld     = RequireWorld | RequireBody | RequireAlive,

        /// <summary>标准主动交互要求：世界已生成 + 角色存在、存活且清醒。</summary>
        AwakeInWorld     = RequireWorld | RequireBody | RequireAlive | RequireConscious,
    }

    /// <summary>
    /// 通过校验后的服务端请求调用方上下文快照。
    /// </summary>
    public readonly struct VerifiedCaller {
        public readonly knetid ClientId;
        public readonly ScavPlayer Player;
        public readonly NetBody NetBody;
        public readonly Body Body;

        public VerifiedCaller(knetid clientId, ScavPlayer player, NetBody netBody, Body body) {
            ClientId = clientId;
            Player = player;
            NetBody = netBody;
            Body = body;
        }
    }

    /// <summary>
    /// 统一收口服务端数据包接收器的合法性、存活/清醒状态与交互距离校验，消除散落重复判断并自动向黑匣子记录拒绝原因。
    /// </summary>
    public static class NetGuard {
        public static bool TryVerifyCaller(
            knetid clientId,
            CallerPolicy policy,
            LogDomain domain,
            byte msgId,
            out VerifiedCaller caller) {
            caller = default;

            if (!Net.IsServer) {
                return false;
            }
            if ((policy & CallerPolicy.RequireWorld) != 0 && !CasualtiesTogetherUtils.Util.IsWorldGenerated()) {
                MpFlightRecorder.Record(domain, FlightEventKind.GuardDenied, clientId.id, detailCode: msgId);
                return false;
            }
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null) {
                MpFlightRecorder.Record(domain, FlightEventKind.GuardDenied, clientId.id, detailCode: msgId);
                return false;
            }

            NetBody nb = plr.netBody;
            Body body = nb != null ? nb.body : plr.body;

            if ((policy & CallerPolicy.RequireBody) != 0 && (nb == null || body == null)) {
                MpFlightRecorder.Record(domain, FlightEventKind.GuardDenied, clientId.id, detailCode: msgId);
                return false;
            }
            if ((policy & CallerPolicy.RequireAlive) != 0 && (body == null || !body.alive)) {
                MpFlightRecorder.Record(domain, FlightEventKind.GuardDenied, clientId.id, detailCode: msgId);
                return false;
            }
            if ((policy & CallerPolicy.RequireConscious) != 0 && (body == null || !body.conscious)) {
                MpFlightRecorder.Record(domain, FlightEventKind.GuardDenied, clientId.id, detailCode: msgId);
                return false;
            }

            caller = new VerifiedCaller(clientId, plr, nb, body);
            return true;
        }

        public static bool IsWithinDistance(in VerifiedCaller caller, Vector2 worldTarget, float maxDistance) {
            if (caller.Body == null || !CasualtiesTogetherUtils.Util.IsFinite(worldTarget.x) || !CasualtiesTogetherUtils.Util.IsFinite(worldTarget.y)) {
                return false;
            }
            float dx = caller.Body.transform.position.x - worldTarget.x;
            float dy = caller.Body.transform.position.y - worldTarget.y;
            return (dx * dx + dy * dy) <= (maxDistance * maxDistance);
        }
    }
}
