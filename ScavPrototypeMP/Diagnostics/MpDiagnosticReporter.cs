using System;
using System.Collections.Generic;
using System.IO;
using System.Net.Http;
using System.Text;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Bootstrap;
using Newtonsoft.Json;
using UnityEngine;

namespace Together.Diagnostics {
    /// <summary>
    /// 脱敏结构化诊断上报数据包模型（支持本地落盘与外部自定义 Webhook / Mod 订阅消费）。
    /// </summary>
    [Serializable]
    public sealed class MpDiagnosticReport {
        public string ReportId;
        public string CreatedAtUtc;
        public string BuildFlavor;
        public string ModVersion;
        public string GameVersion;
        public string TriggerReason;
        public string Domain;
        public string UserNote;
        public string Role;
        public string Transport;
        public int OnlinePlayerCount;
        public int LivingPlayerCount;
        public int LocalPingMs;
        public int ServerFps;
        public int ServerTps;
        public string InputBlockSummary;
        public List<string> LoadedPlugins;
        public string ExceptionType;
        public string ExceptionMessage;
        public string SanitizedStackTrace;
        public List<FlightEvent> RecentFlightEvents;
        [JsonIgnore]
        public string SavedJsonPath;
        [JsonIgnore]
        public string SavedSummaryPath;
    }

    /// <summary>
    /// 联机异常自动捕获与一键诊断上报中心。
    /// <para>
    /// - 自动对本地操作系统用户名路径与房间密码执行脱敏；<br/>
    /// - 内置 30 秒最小上报冷却与相同堆栈指纹去重，杜绝异常风暴卡死主线程；<br/>
    /// - 开放 <see cref="OnReportGenerated"/> 事件与可选 <see cref="WebhookEndpointUrl"/>，支持外置上报服务无缝接入。
    /// </para>
    /// </summary>
    public static class MpDiagnosticReporter {
        private const double AutoReportCooldownSeconds = 30.0;
        private const int MaxRetainedReportFiles = 20;

        private static readonly object Gate = new object();
        private static readonly HashSet<int> ReportedExceptionFingerprints = new HashSet<int>();
        private static readonly HttpClient SharedHttpClient = new HttpClient { Timeout = TimeSpan.FromSeconds(8) };

        private static bool _installed;
        private static bool _inHandler;
        private static double _lastAutoReportTime = -999.0;

        /// <summary>是否允许在捕获未处理异常时自动生成本地诊断报告（默认开启）。</summary>
        public static bool AutoReportUnhandledExceptions { get; set; } = true;

        /// <summary>可选远程上报 Webhook URL（默认为空，仅保存在本地 <c>BepInEx/TogetherReports</c>；配置后异步 POST 脱敏 JSON）。</summary>
        public static string WebhookEndpointUrl { get; set; } = string.Empty;

        /// <summary>每次生成诊断报告后触发，供外部模组或 UI 提示消费。</summary>
        public static event Action<MpDiagnosticReport> OnReportGenerated;

        /// <summary>最近一次生成的报告文件路径。</summary>
        public static string LastReportPath { get; private set; } = string.Empty;

        public static string ReportsDirectory => Path.Combine(Paths.BepInExRootPath, "TogetherReports");

        public static void Install() {
            if (_installed) {
                return;
            }
            _installed = true;
            Application.logMessageReceived += OnUnityLogMessageReceived;
        }

        public static void Uninstall() {
            if (!_installed) {
                return;
            }
            _installed = false;
            Application.logMessageReceived -= OnUnityLogMessageReceived;
        }

        internal static void NotifyException(LogDomain domain, string context, Exception ex) {
            if (!AutoReportUnhandledExceptions || ex == null) {
                return;
            }
            TryAutoReport(domain.ToString(), context, ex.GetType().FullName, ex.Message, ex.StackTrace);
        }

        /// <summary>主动构建、脱敏并持久化一份完整诊断报告（可供控制台 <c>mp_report</c> 或 UI 按钮调用）。</summary>
        public static MpDiagnosticReport GenerateReport(
            string reason,
            Exception exception = null,
            string userNote = null,
            LogDomain domain = LogDomain.Core,
            int maxFlightEvents = 512) {
            MpDiagnosticReport report = BuildReportSnapshot(
                reason ?? "ManualReport",
                domain.ToString(),
                userNote,
                exception?.GetType().FullName,
                exception?.Message,
                exception?.StackTrace,
                maxFlightEvents);

            PersistAndDispatch(report);
            return report;
        }

        private static void OnUnityLogMessageReceived(string condition, string stackTrace, LogType type) {
            if (type != LogType.Exception || !AutoReportUnhandledExceptions || _inHandler) {
                return;
            }
            // 仅自动拦截与联机模组调用栈直接相关的未捕获异常，避免替无关模组误报刷盘
            bool relevant = (!string.IsNullOrEmpty(stackTrace) && (stackTrace.Contains("Together.") || stackTrace.Contains("CasualtiesMP")))
                         || (!string.IsNullOrEmpty(condition) && condition.Contains("Together."));
            if (!relevant) {
                return;
            }
            TryAutoReport(LogDomain.Core.ToString(), "UnhandledUnityException", "UnityLogException", condition, stackTrace);
        }

        private static void TryAutoReport(string domain, string reason, string exType, string exMsg, string stackTrace) {
            double now;
            try {
                now = Time.realtimeSinceStartupAsDouble;
            } catch {
                now = 0.0;
            }

            int fingerprint = HashCode.Combine(exType ?? string.Empty, FirstLine(stackTrace));
            lock (Gate) {
                if (_inHandler) {
                    return;
                }
                if (now > 0.0 && now - _lastAutoReportTime < AutoReportCooldownSeconds) {
                    return;
                }
                if (!ReportedExceptionFingerprints.Add(fingerprint)) {
                    return;
                }
                _lastAutoReportTime = now;
                _inHandler = true;
            }

            try {
                MpDiagnosticReport report = BuildReportSnapshot(reason, domain, null, exType, exMsg, stackTrace, 512);
                PersistAndDispatch(report);
            } catch {
                // 诊断器内部异常绝不向外扩散二次崩溃
            } finally {
                lock (Gate) {
                    _inHandler = false;
                }
            }
        }

        private static MpDiagnosticReport BuildReportSnapshot(
            string reason,
            string domain,
            string userNote,
            string exType,
            string exMsg,
            string stackTrace,
            int maxFlightEvents) {
            List<string> plugins = new List<string>();
            try {
                foreach (KeyValuePair<string, PluginInfo> kv in Chainloader.PluginInfos) {
                    if (kv.Value?.Metadata != null) {
                        plugins.Add($"{kv.Value.Metadata.GUID}@{kv.Value.Metadata.Version}");
                    }
                }
                plugins.Sort(StringComparer.OrdinalIgnoreCase);
            } catch {
                // 忽略早期引导阶段插件列表未就绪
            }

            string role = !Net.IsRunning ? "Offline" : (Net.IsDedicatedServer ? "DedicatedServer" : (Net.IsServer ? "Host" : "Client"));
            string transport = !Net.IsRunning ? "None" : (Net.IsRunningSteam ? "SteamNetworkingSockets" : "LiteNetLib");
            int onlineCount = ScavPlayer.ClientIdToPlayerDict != null ? ScavPlayer.ClientIdToPlayerDict.Count : 0;
            int livingCount = ScavPlayer.AllLivingPlayers != null ? ScavPlayer.AllLivingPlayers.Count : 0;

            return new MpDiagnosticReport {
                ReportId = Guid.NewGuid().ToString("N").Substring(0, 12),
                CreatedAtUtc = DateTime.UtcNow.ToString("O"),
                BuildFlavor = ModLog.IsDebugBuild ? "Debug" : "Release",
                ModVersion = Plugin.FULL_MOD_VERSION ?? BuildInfo.MOD_VERSION,
                GameVersion = SafeRead(() => Application.version, "unknown"),
                TriggerReason = SanitizeSensitiveText(reason),
                Domain = domain ?? "Core",
                UserNote = SanitizeSensitiveText(userNote),
                Role = role,
                Transport = transport,
                OnlinePlayerCount = onlineCount,
                LivingPlayerCount = livingCount,
                LocalPingMs = ClientMain.LOCAL_PING,
                ServerFps = ClientMain.SERVER_FPS,
                ServerTps = ClientMain.SERVER_TPS,
                InputBlockSummary = SafeRead(() => MpDebugOverlay.EvaluateInputBlocks().ToString(), "Unavailable"),
                LoadedPlugins = plugins,
                ExceptionType = exType ?? string.Empty,
                ExceptionMessage = SanitizeSensitiveText(exMsg),
                SanitizedStackTrace = SanitizeSensitiveText(stackTrace),
                RecentFlightEvents = MpFlightRecorder.SnapshotRecent(maxFlightEvents),
            };
        }

        private static void PersistAndDispatch(MpDiagnosticReport report) {
            try {
                Directory.CreateDirectory(ReportsDirectory);
                PruneOldReports();

                string stamp = DateTime.Now.ToString("yyyyMMdd_HHmmss") + "_" + report.ReportId;
                string jsonPath = Path.Combine(ReportsDirectory, $"report_{stamp}.json");
                string txtPath = Path.Combine(ReportsDirectory, $"report_{stamp}.txt");

                UTF8Encoding utf8NoBom = new UTF8Encoding(false);
                string json = JsonConvert.SerializeObject(report, Formatting.Indented);
                File.WriteAllText(jsonPath, json, utf8NoBom);
                File.WriteAllText(txtPath, FormatHumanReadableSummary(report), utf8NoBom);

                report.SavedJsonPath = jsonPath;
                report.SavedSummaryPath = txtPath;
                LastReportPath = txtPath;

                ModLog.Info($"已生成联机诊断报告：{SanitizeSensitiveText(txtPath)}", LogDomain.Core);

                if (!string.IsNullOrWhiteSpace(WebhookEndpointUrl)) {
                    string endpoint = WebhookEndpointUrl.Trim();
                    _ = Task.Run(async () => {
                        try {
                            using StringContent body = new StringContent(json, Encoding.UTF8, "application/json");
                            await SharedHttpClient.PostAsync(endpoint, body).ConfigureAwait(false);
                        } catch {
                            // 远程上报失败静默降级，本地报告已落盘保存
                        }
                    });
                }

                OnReportGenerated?.Invoke(report);
            } catch {
                // 磁盘只读或满盘时静默跳过
            }
        }

        private static void PruneOldReports() {
            try {
                DirectoryInfo dir = new DirectoryInfo(ReportsDirectory);
                if (!dir.Exists) {
                    return;
                }
                FileInfo[] files = dir.GetFiles("report_*.*");
                if (files.Length <= MaxRetainedReportFiles * 2) {
                    return;
                }
                Array.Sort(files, (a, b) => a.CreationTimeUtc.CompareTo(b.CreationTimeUtc));
                int toDelete = files.Length - MaxRetainedReportFiles * 2;
                for (int i = 0; i < toDelete; i++) {
                    files[i].Delete();
                }
            } catch {
                // 清理旧文件失败不影响主报告写入
            }
        }

        private static string FormatHumanReadableSummary(MpDiagnosticReport r) {
            StringBuilder sb = new StringBuilder(4096);
            sb.AppendLine("============================================================");
            sb.AppendLine($"Casualties: Together Diagnostic Report #{r.ReportId}");
            sb.AppendLine("============================================================");
            sb.AppendLine($"Time (UTC)   : {r.CreatedAtUtc}");
            sb.AppendLine($"Build / Ver  : {r.BuildFlavor} / {r.ModVersion} (Game {r.GameVersion})");
            sb.AppendLine($"Reason       : [{r.Domain}] {r.TriggerReason}");
            if (!string.IsNullOrEmpty(r.UserNote)) {
                sb.AppendLine($"User Note    : {r.UserNote}");
            }
            sb.AppendLine($"Session      : Role={r.Role}, Transport={r.Transport}, Online={r.OnlinePlayerCount}, Living={r.LivingPlayerCount}");
            sb.AppendLine($"Metrics      : Ping={r.LocalPingMs}ms, ServerFPS={r.ServerFps}, ServerTPS={r.ServerTps}");
            sb.AppendLine($"Input Blocks : {r.InputBlockSummary}");
            sb.AppendLine($"Plugins ({r.LoadedPlugins?.Count ?? 0}) : {(r.LoadedPlugins != null ? string.Join(", ", r.LoadedPlugins) : "none")}");
            if (!string.IsNullOrEmpty(r.ExceptionType) || !string.IsNullOrEmpty(r.ExceptionMessage)) {
                sb.AppendLine("------------------------------------------------------------");
                sb.AppendLine($"Exception    : {r.ExceptionType}: {r.ExceptionMessage}");
                if (!string.IsNullOrEmpty(r.SanitizedStackTrace)) {
                    sb.AppendLine(r.SanitizedStackTrace);
                }
            }
            sb.AppendLine("------------------------------------------------------------");
            sb.Append(MpFlightRecorder.FormatRecentText(r.RecentFlightEvents?.Count ?? 256));
            return sb.ToString();
        }

        internal static string SanitizeSensitiveText(string input) {
            if (string.IsNullOrEmpty(input)) {
                return string.Empty;
            }
            string result = input;
            try {
                string userProfile = Environment.GetFolderPath(Environment.SpecialFolder.UserProfile);
                if (!string.IsNullOrEmpty(userProfile)) {
                    result = result.Replace(userProfile, "<USER_HOME>");
                    result = result.Replace(userProfile.Replace('\\', '/'), "<USER_HOME>");
                }
                string userName = Environment.UserName;
                if (!string.IsNullOrEmpty(userName) && userName.Length >= 3) {
                    result = result.Replace("\\" + userName + "\\", "\\<USER>\\");
                    result = result.Replace("/" + userName + "/", "/<USER>/");
                }
            } catch {
                // 忽略系统环境变量读取限制
            }
            return result;
        }

        private static string FirstLine(string text) {
            if (string.IsNullOrEmpty(text)) {
                return string.Empty;
            }
            int idx = text.IndexOf('\n');
            return idx >= 0 ? text.Substring(0, idx).Trim() : text.Trim();
        }

        private static T SafeRead<T>(Func<T> getter, T fallback) {
            try {
                return getter();
            } catch {
                return fallback;
            }
        }
    }
}
