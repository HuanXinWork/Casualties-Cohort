using System;
using System.Diagnostics;
using System.Reflection;
using BepInEx.Logging;

namespace Together.Diagnostics {
    /// <summary>
    /// Debug / Release 双模统一日志门面（对齐 <c>SkinSync</c> 与 <c>CuSaveManager</c> 的 <c>ModLog</c> 规范）。
    /// <para>
    /// - <b>Release 构建</b>：所有标记 <see cref="ConditionalAttribute"/>("DEBUG") 的 <see cref="Debug"/> 与 <see cref="Flow"/> 调用在编译期连同实参表达式一并抹除（零 CPU / 零 GC）；仅输出精简关键信息与告警错误。<br/>
    /// - <b>Debug 构建</b>：启用全链路分域诊断，绕过 BepInEx 默认屏蔽 <c>LogDebug</c> 的限制写入 <c>LogInfo</c>，并支持按 <see cref="EnabledDebugDomains"/> 实时过滤。
    /// </para>
    /// </summary>
    public static class ModLog {
        private const string Prefix = "[Together] ";

        private static ManualLogSource _sink;
        private static MethodInfo _consoleLog;
        private static bool _consoleResolved;

        /// <summary>是否将日志同步镜像打印到游戏内 <see cref="ConsoleScript"/> 控制台。</summary>
        public static bool ShowInConsole { get; set; }

        /// <summary>Debug 模式下允许输出的日志域掩码（默认全开，可通过控制台命令按子系统动态开关）。</summary>
        public static LogDomain EnabledDebugDomains { get; set; } = LogDomain.All;

        /// <summary>当前构建是否为编译期 Debug 版本。</summary>
        public static bool IsDebugBuild =>
#if DEBUG
            true;
#else
            false;
#endif

        public static void Init(ManualLogSource sink) {
            _sink = sink;
        }

        public static void Info(string message, LogDomain domain = LogDomain.Core) {
            string formatted = FormatDomainPrefix(domain) + message;
            _sink?.LogInfo(formatted);
            WriteToConsole(formatted, null);
        }

        public static void Warning(string message, LogDomain domain = LogDomain.Core) {
            string formatted = FormatDomainPrefix(domain) + message;
            _sink?.LogWarning(formatted);
            WriteToConsole(formatted, "yellow");
            MpFlightRecorder.Record(domain, FlightEventKind.Warning);
        }

        public static void Error(string message, LogDomain domain = LogDomain.Core) {
            string formatted = FormatDomainPrefix(domain) + message;
            _sink?.LogError(formatted);
            WriteToConsole(formatted, "red");
            MpFlightRecorder.Record(domain, FlightEventKind.Error);
        }

        public static void Exception(LogDomain domain, string context, Exception ex) {
            string msg = ex == null
                ? $"{context}: (null exception)"
                : $"{context}: {ex.GetType().Name}: {ex.Message}\n{ex.StackTrace}";
            Error(msg, domain);
            MpFlightRecorder.Record(domain, FlightEventKind.Exception, detailCode: ex != null ? ex.HResult : 0);
            MpDiagnosticReporter.NotifyException(domain, context, ex);
        }

        /// <summary>Debug 构建专属诊断日志；Release 编译期自动抹除调用点与参数求值。</summary>
        [Conditional("DEBUG")]
        public static void Debug(string message, LogDomain domain = LogDomain.Core) {
#if DEBUG
            if ((EnabledDebugDomains & domain) == 0) {
                return;
            }
            string text = Prefix + "[DBG]" + FormatDomainPrefix(domain) + message;
            _sink?.LogInfo(text);
            WriteToConsole(text, "grey");
#endif
        }

        /// <summary>Debug 构建专属分步流程追踪日志；Release 编译期自动抹除。</summary>
        [Conditional("DEBUG")]
        public static void Flow(LogDomain domain, string step, string message) {
#if DEBUG
            if ((EnabledDebugDomains & domain) == 0) {
                return;
            }
            string text = Prefix + "[DBG][" + domain + "] " + step + " " + message;
            _sink?.LogInfo(text);
            WriteToConsole(text, "grey");
#endif
        }

        private static string FormatDomainPrefix(LogDomain domain) {
            return domain == LogDomain.None || domain == LogDomain.Core ? string.Empty : "[" + domain + "] ";
        }

        private static void WriteToConsole(string message, string color) {
            if (!ShowInConsole) {
                return;
            }
            try {
                ConsoleScript instance = ConsoleScript.instance;
                if (instance == null || !ResolveConsoleLog()) {
                    return;
                }
                string text = string.IsNullOrEmpty(color)
                    ? Prefix + message
                    : $"<color={color}>{Prefix}{message}</color>";
                _consoleLog.Invoke(instance, new object[] { text });
            } catch {
                // 控制台自身异常不再递归写日志
            }
        }

        private static bool ResolveConsoleLog() {
            if (_consoleResolved) {
                return _consoleLog != null;
            }
            _consoleResolved = true;
            _consoleLog = typeof(ConsoleScript).GetMethod(
                "LogToConsole",
                BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public,
                null,
                new[] { typeof(string) },
                null);
            return _consoleLog != null;
        }
    }
}
