using System;
using System.Runtime.CompilerServices;
using Together.Diagnostics;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 兼容既有调用点的日志门面（内部统一桥接至 <see cref="ModLog"/> 双模分域日志与 <see cref="MpFlightRecorder"/> 黑匣子）。
    /// </summary>
    internal class log {
        public static bool verbose => Multiplayer.verbose;
        public static ConsoleScript con => ConsoleScript.instance;
        public const string name = "MP";
        public const string prefix1 = name + ": ";

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        internal static string do_timestamp(string s) {
            return $"[{DateTime.UtcNow:HH:mm:ss.fff}] {s}";
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        private static string SanitizeLine(string s) {
            return string.IsNullOrEmpty(s) ? string.Empty : s.Replace('\r', ' ');
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void devevent(in string s, Vector2 eventpos, bool ignoreverbose = false) {
            devevent(s, eventpos, Color.cyan, ignoreverbose);
        }

        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        public static void devevent(string s, Vector2 eventpos, Color color, bool ignoreverbose) {
            string clean = SanitizeLine(s);
            ModLog.Debug(clean, LogDomain.Core);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(eventpos, clean, color);
            }

            if ((verbose || ignoreverbose) && con != null) {
                con.LogToConsole(prefix1 + clean);
            }
        }

        public static void l(params string[] s) => l(string.Join(' ', s), true);
        public static void l(string s) => l(s, true);

        public static void l(string s, bool console) {
            string clean = SanitizeLine(s);
#if DEBUG
            ModLog.Debug(clean, LogDomain.Core);
            if (console && con != null) {
                con.LogToConsole(prefix1 + clean);
            }
#else
            // Release 构建下仅当开启 verbose 时才输出常规跟踪日志，抑制稳态 I/O 噪音
            if (!verbose) {
                return;
            }
            ModLog.Info(clean, LogDomain.Core);
            if (console && con != null) {
                con.LogToConsole(prefix1 + clean);
            }
#endif
        }

        public static void warn(string s) => warn(s, true);

        public static void warn(string s, bool console) {
            string clean = SanitizeLine(s);
            ModLog.Warning(clean, LogDomain.Core);

            if (console && con != null) {
                con.LogToConsole($"<color=yellow>{name} WARN: {clean}</color>");
            }
        }

        public static void error_devevent(in string s, in Vector2 pos) {
            string clean = SanitizeLine(s);
            error(clean, true);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(pos, "ERROR: " + clean, Color.red);
            }
        }

        public static void error(Exception e) {
            if (e == null) {
                error("(null exception)", true);
                return;
            }
            ModLog.Exception(LogDomain.Core, "Together.log", e);
            if (con != null) {
                con.LogToConsole($"<color=red>{name} ERROR: {SanitizeLine(e.Message)}</color>");
            }
        }

        public static void error(string s) => error(s, true);

        public static void error(string s, bool console) {
            string clean = SanitizeLine(s);
            if (Plugin.log == null) {
                Debug.LogError(do_timestamp(clean));
                MpFlightRecorder.Record(LogDomain.Core, FlightEventKind.Error);
            } else {
                ModLog.Error(clean, LogDomain.Core);
            }

            if (console && con != null) {
                con.LogToConsole($"<color=red>{name} ERROR: {clean}</color>");
            }
        }

        public static void sus(string s, bool console = true) {
            string clean = "SUS: " + SanitizeLine(s);
            ModLog.Warning(clean, LogDomain.Security);
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied);

            if (console && con != null && Multiplayer.verbose) {
                con.LogToConsole($"<color=orange>{name}: {clean}</color>");
            }
        }

        public static void serverdeny(string s) {
            string clean = "SERVER DENY: " + SanitizeLine(s);
            ModLog.Warning(clean, LogDomain.Security);
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied);

            if (con != null && Multiplayer.verbose) {
                con.LogToConsole($"<color=yellow>{name}: {clean}</color>");
            }
        }

        public static void serverdeny(string s, Vector2 pos) {
            serverdeny(s);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(pos, "S: DENIED " + SanitizeLine(s), Color.red);
            }
        }
    }
}
