using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using UnityEngine;

namespace Together.Diagnostics {
    /// <summary>
    /// 单条黑匣子追踪事件（固定 32 字节值类型结构体，稳态写入零 GC 分配）。
    /// </summary>
    public struct FlightEvent {
        public long Sequence;
        public float RealtimeSeconds;
        public int UnityFrame;
        public ushort ServerTick;
        public ushort ClientId;
        public uint SyncId;
        public uint AttemptId;
        public int DetailCode;
        public LogDomain Domain;
        public FlightEventKind Kind;
    }

    /// <summary>
    /// 零稳态 GC 的内存环形黑匣子记录器（Debug 与 Release 均常驻极小固定数组，用于异常上报与偶发联机失步复盘）。
    /// </summary>
    public static class MpFlightRecorder {
        public const int Capacity = 2048;
        private const int Mask = Capacity - 1;

        private static readonly FlightEvent[] Buffer = new FlightEvent[Capacity];
        private static long _writeSeq;

        /// <summary>累计写入的事件总数。</summary>
        public static long TotalRecorded => Interlocked.Read(ref _writeSeq);

        public static void Record(
            LogDomain domain,
            FlightEventKind kind,
            ushort clientId = 0,
            uint syncId = 0,
            uint attemptId = 0,
            int detailCode = 0) {
            long seq = Interlocked.Increment(ref _writeSeq);
            int slot = (int)((seq - 1) & Mask);

            float now = 0f;
            int frame = 0;
            ushort srvTick = 0;
            try {
                now = Time.realtimeSinceStartup;
                frame = Time.frameCount;
                srvTick = Net.IsRunning ? Net.ServerTick : (ushort)0;
            } catch {
                // 允许在非主线程安全采样而不中断业务逻辑
            }

            Buffer[slot] = new FlightEvent {
                Sequence = seq,
                RealtimeSeconds = now,
                UnityFrame = frame,
                ServerTick = srvTick,
                ClientId = clientId,
                SyncId = syncId,
                AttemptId = attemptId,
                DetailCode = detailCode,
                Domain = domain,
                Kind = kind,
            };
        }

        public static void Clear() {
            Array.Clear(Buffer, 0, Buffer.Length);
            Interlocked.Exchange(ref _writeSeq, 0);
        }

        /// <summary>按时间升序提取最近缓存的全部有效事件快照。</summary>
        public static List<FlightEvent> SnapshotRecent(int maxCount = Capacity) {
            long head = Interlocked.Read(ref _writeSeq);
            int available = (int)Math.Min(head, Capacity);
            int count = Math.Min(available, Math.Max(0, maxCount));
            List<FlightEvent> list = new List<FlightEvent>(count);
            long startSeq = head - count + 1;
            for (long s = startSeq; s <= head; s++) {
                FlightEvent ev = Buffer[(int)((s - 1) & Mask)];
                if (ev.Sequence > 0 && ev.Kind != FlightEventKind.None) {
                    list.Add(ev);
                }
            }
            return list;
        }

        public static string FormatRecentText(int maxCount = 512) {
            List<FlightEvent> events = SnapshotRecent(maxCount);
            StringBuilder sb = new StringBuilder(events.Count * 80 + 128);
            sb.AppendLine($"=== Together Flight Recorder (showing {events.Count} / total {TotalRecorded}) ===");
            for (int i = 0; i < events.Count; i++) {
                FlightEvent e = events[i];
                sb.Append('#').Append(e.Sequence)
                  .Append(" [t=").Append(e.RealtimeSeconds.ToString("0.000"))
                  .Append("s f=").Append(e.UnityFrame)
                  .Append(" tick=").Append(e.ServerTick)
                  .Append("] ").Append(e.Domain)
                  .Append('/').Append(e.Kind)
                  .Append(" plr=").Append(e.ClientId)
                  .Append(" sync=").Append(e.SyncId)
                  .Append(" att=").Append(e.AttemptId)
                  .Append(" code=").Append(e.DetailCode)
                  .AppendLine();
            }
            return sb.ToString();
        }
    }
}
