using System.Diagnostics;

namespace Together.Diagnostics {
    /// <summary>
    /// 四阶段网络同步链路追踪器（对齐 <c>SkinSync.CommsTrace</c> 设计）。
    /// <para>字符串日志仅在 <c>DEBUG</c> 构建生效；值类型黑匣子打点可通过 <see cref="MpFlightRecorder"/> 在双构建零 GC 留痕。</para>
    /// </summary>
    public static class MpCommsTrace {
        [Conditional("DEBUG")]
        public static void ClientSend(LogDomain domain, byte msgId, uint syncId = 0, uint attemptId = 0, string detail = null) {
#if DEBUG
            MpFlightRecorder.Record(domain, FlightEventKind.PacketSend, syncId: syncId, attemptId: attemptId, detailCode: msgId);
            ModLog.Debug($"[Comms] 1-发送 本端->服务端：msgId={msgId}，syncId={syncId}，attempt={attemptId}" + Suffix(detail), domain);
#endif
        }

        [Conditional("DEBUG")]
        public static void SendSkipped(LogDomain domain, byte msgId, uint syncId, string reason) {
#if DEBUG
            ModLog.Debug($"[Comms] 1-发送 已跳过：msgId={msgId}，syncId={syncId}，原因={reason ?? "(无)"}", domain);
#endif
        }

        [Conditional("DEBUG")]
        public static void HostReceive(LogDomain domain, ushort callerId, byte msgId, uint syncId = 0, string detail = null) {
#if DEBUG
            MpFlightRecorder.Record(domain, FlightEventKind.PacketReceive, clientId: callerId, syncId: syncId, detailCode: msgId);
            ModLog.Debug($"[Comms] 2-主机接收 client={callerId}：msgId={msgId}，syncId={syncId}" + Suffix(detail), domain);
#endif
        }

        public static void HostRejected(LogDomain domain, ushort callerId, byte msgId, uint syncId, string reason) {
            MpFlightRecorder.Record(domain, FlightEventKind.PacketReject, clientId: callerId, syncId: syncId, detailCode: msgId);
#if DEBUG
            ModLog.Debug($"[Comms] 2-主机已拒绝 client={callerId}：msgId={msgId}，syncId={syncId}，原因={reason ?? "(未知)"}", domain);
#endif
        }

        [Conditional("DEBUG")]
        public static void HostBroadcast(LogDomain domain, byte msgId, int recipients, ushort excludedClientId = ushort.MaxValue, string detail = null) {
#if DEBUG
            string skip = excludedClientId == ushort.MaxValue ? "无" : excludedClientId.ToString();
            ModLog.Debug($"[Comms] 3-主机广播：msgId={msgId}，收件人数={recipients}，跳过={skip}" + Suffix(detail), domain);
#endif
        }

        [Conditional("DEBUG")]
        public static void HostUnicast(LogDomain domain, ushort targetClientId, byte msgId, uint syncId = 0, string detail = null) {
#if DEBUG
            ModLog.Debug($"[Comms] 3-主机单播 -> client={targetClientId}：msgId={msgId}，syncId={syncId}" + Suffix(detail), domain);
#endif
        }

        [Conditional("DEBUG")]
        public static void ClientReceive(LogDomain domain, byte msgId, uint syncId = 0, bool isLocalEcho = false, string detail = null) {
#if DEBUG
            MpFlightRecorder.Record(domain, FlightEventKind.PacketReceive, syncId: syncId, detailCode: msgId);
            ModLog.Debug($"[Comms] 4-客机接收：msgId={msgId}，syncId={syncId}，本地回显={(isLocalEcho ? "是" : "否")}" + Suffix(detail), domain);
#endif
        }

#if DEBUG
        private static string Suffix(string detail) {
            return string.IsNullOrEmpty(detail) ? string.Empty : "，" + detail;
        }
#endif
    }
}
