using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 动态系统 Unicode/CJK 字体回退挂载与三版本兼容 Unicode 昵称同步器（修复上游 Issue #40 / #35）。
    /// <para>
    /// <b>设计动机与原版契约：</b><br/>
    /// 1. 模组内置像素字体 <c>"Retro GamingPix"</c> 仅包含 ASCII 字形，<see cref="ScavPlayer.ApplyNameAndColor"/> 调用的
    /// <see cref="FontUtils.ReplaceMissingCharacters(TMP_FontAsset, string)"/> 内部执行 <c>font.HasCharacter(c, true)</c>（递归查询 <c>fallbackFontAssetTable</c>），
    /// 缺失回退表时会将所有中文/俄文/日韩文字符替换为 <c>'?'</c>。<br/>
    /// 2. Steam 原版 <c>v5.5.3.0</c> 房主在握手阶段（<see cref="Multiplayer.ValidateClientConnectIntroductionPacket"/>）强制执行纯 ASCII 清洗且要求 <c>Length &gt; 2</c>。
    /// 因此客户端握手包始终携带确定性 ASCII 安全名保证 100% 通过旧房主校验，入服后再通过既有 <c>CustomCommand</c> 通道上报 <c>"unicode_name"</c> 升级真实昵称（旧房主会静默忽略未知命令）。
    /// </para>
    /// </summary>
    public static class UnicodeFontFallback {
        public const string CommandUnicodeName = "unicode_name";

        // 按中日韩、全字符集与西里尔/拉丁扩展覆盖率排列的系统字体优先级列表
        private static readonly string[] PreferredOsFontNames = new string[] {
            "Microsoft YaHei",
            "Microsoft YaHei UI",
            "SimHei",
            "DengXian",
            "Malgun Gothic",
            "Meiryo",
            "Yu Gothic",
            "Noto Sans CJK SC",
            "Noto Sans CJK",
            "WenQuanYi Micro Hei",
            "Arial Unicode MS",
            "Segoe UI",
            "Arial",
        };

        private static TMP_FontAsset _cachedFallbackAsset;
        private static Font _cachedOsFont;
        private static bool _initializationAttempted;
        private static bool _globalFallbackRegistered;

        // 客户端当前传输层实例已上报过的 Unicode 昵称快照，保证同一连接会话幂等单次发送、断线重连自动重发
        private static NetTransportBase _lastSyncedTransport;
        private static string _lastSyncedUnicodeName;

        /// <summary>
        /// 确保动态 OS Unicode 回退字体资产已构建并幂等挂载到目标 <see cref="TMP_FontAsset"/>（及全局 TMP 回退链）。
        /// </summary>
        public static void EnsureInstalled(TMP_FontAsset targetFont) {
            try {
                TMP_FontAsset fallback = GetOrCreateFallbackAsset();
                if (fallback == null) {
                    return;
                }

                AttachFallbackToFont(targetFont, fallback);

                // 同步挂载到全局 TMP_Settings 与头顶名牌预制体字体，防止其他未显式指定 Retro GamingPix 的文本组件将非 ASCII 渲染为方块
                if (!_globalFallbackRegistered && TMP_Settings.instance != null) {
                    List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
                    if (globalFallbacks != null && !globalFallbacks.Contains(fallback)) {
                        globalFallbacks.Add(fallback);
                    }
                    _globalFallbackRegistered = true;
                }

                if (CharStatusVisuals.PREFAB_ExpTalkText != null) {
                    TMP_Text prefabTmp = CharStatusVisuals.PREFAB_ExpTalkText.GetComponent<TMP_Text>();
                    if (prefabTmp != null && prefabTmp.font != null) {
                        AttachFallbackToFont(prefabTmp.font, fallback);
                    }
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, nameof(EnsureInstalled), ex);
            }
        }

        private static void AttachFallbackToFont(TMP_FontAsset font, TMP_FontAsset fallback) {
            if (font == null || fallback == null || ReferenceEquals(font, fallback)) {
                return;
            }
            if (font.fallbackFontAssetTable == null) {
                font.fallbackFontAssetTable = new List<TMP_FontAsset>();
            }
            if (!font.fallbackFontAssetTable.Contains(fallback)) {
                font.fallbackFontAssetTable.Add(fallback);
            }
        }

        private static TMP_FontAsset GetOrCreateFallbackAsset() {
            if (_cachedFallbackAsset != null) {
                return _cachedFallbackAsset;
            }
            if (_initializationAttempted) {
                return null;
            }
            _initializationAttempted = true;

            try {
                string[] installedFonts = Font.GetOSInstalledFontNames();
                string[] matchedNames = MatchInstalledFontChain(installedFonts);

                _cachedOsFont = Font.CreateDynamicFontFromOSFont(matchedNames, 16);
                if (_cachedOsFont == null) {
                    ModLog.Warn(LogDomain.UI, "UnicodeFontFallback: Font.CreateDynamicFontFromOSFont returned null.");
                    return null;
                }
                _cachedOsFont.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_cachedOsFont);

                _cachedFallbackAsset = TMP_FontAsset.CreateFontAsset(_cachedOsFont);
                if (_cachedFallbackAsset == null) {
                    ModLog.Warn(LogDomain.UI, "UnicodeFontFallback: TMP_FontAsset.CreateFontAsset returned null.");
                    return null;
                }

                _cachedFallbackAsset.name = "CT_UnicodeDynamicFallback";
                _cachedFallbackAsset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                _cachedFallbackAsset.isMultiAtlasTexturesEnabled = true;
                _cachedFallbackAsset.hideFlags = HideFlags.HideAndDontSave;
                UnityEngine.Object.DontDestroyOnLoad(_cachedFallbackAsset);

                ModLog.Info(LogDomain.UI, $"UnicodeFontFallback: installed dynamic fallback font chain [{string.Join(", ", matchedNames.Take(4))}].");
                return _cachedFallbackAsset;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, nameof(GetOrCreateFallbackAsset), ex);
                return null;
            }
        }

        private static string[] MatchInstalledFontChain(string[] installedFonts) {
            if (installedFonts == null || installedFonts.Length == 0) {
                return PreferredOsFontNames;
            }

            List<string> result = new List<string>(PreferredOsFontNames.Length);
            HashSet<string> added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 第一轮精确忽略大小写匹配，第二轮前缀/包含匹配（兼容 Linux/macOS 带字重后缀的系统字体名）
            foreach (string candidate in PreferredOsFontNames) {
                for (int i = 0; i < installedFonts.Length; i++) {
                    string osName = installedFonts[i];
                    if (!string.IsNullOrEmpty(osName)
                        && string.Equals(osName, candidate, StringComparison.OrdinalIgnoreCase)
                        && added.Add(osName)) {
                        result.Add(osName);
                        break;
                    }
                }
            }

            foreach (string candidate in PreferredOsFontNames) {
                for (int i = 0; i < installedFonts.Length; i++) {
                    string osName = installedFonts[i];
                    if (!string.IsNullOrEmpty(osName)
                        && osName.IndexOf(candidate, StringComparison.OrdinalIgnoreCase) >= 0
                        && added.Add(osName)) {
                        result.Add(osName);
                        break;
                    }
                }
            }

            return result.Count > 0 ? result.ToArray() : PreferredOsFontNames;
        }

        /// <summary>
        /// 重置客户端 Unicode 昵称同步快照（在网络会话关闭重置时调用）。
        /// </summary>
        internal static void ResetClientSyncState() {
            _lastSyncedTransport = null;
            _lastSyncedUnicodeName = null;
        }

        /// <summary>
        /// 客机入服握手确认后按需向服务端发送一次 <c>"unicode_name"</c> 自定义命令升级真实 Unicode 昵称。
        /// </summary>
        public static void Client_TrySendUnicodeNameIfNeeded() {
            if (!Net.IsRunning || !Net.IsClient || Net.Transport == null || !Net.IsConnected) {
                return;
            }

            string desired = Multiplayer.SanitizeUnicodeTextInput(Multiplayer.PLAYER_USERNAME);
            if (!Multiplayer.IsValidPlayerName(desired, out _)) {
                return;
            }

            string handshakeSafe = Multiplayer.BuildAsciiHandshakeSafeName(desired);
            bool differsFromHandshake = !string.Equals(desired, handshakeSafe, StringComparison.Ordinal);
            bool differsFromServerSynced = ScavPlayer.LocalPlayer != null
                && ScavPlayer.LocalPlayer.BasePlayer != null
                && !string.Equals(ScavPlayer.LocalPlayer.BasePlayer.playerName.Value, desired, StringComparison.Ordinal);

            if (!differsFromHandshake && !differsFromServerSynced) {
                return;
            }

            if (ReferenceEquals(_lastSyncedTransport, Net.Transport)
                && string.Equals(_lastSyncedUnicodeName, desired, StringComparison.Ordinal)) {
                return;
            }

            try {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CommandUnicodeName + " " + desired);
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

                _lastSyncedTransport = Net.Transport;
                _lastSyncedUnicodeName = desired;
                ModLog.Debug(LogDomain.Lobby, $"Sent unicode_name upgrade '{desired}' (handshake fallback was '{handshakeSafe}').");
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Transport, nameof(Client_TrySendUnicodeNameIfNeeded), ex);
            }
        }

        [CTPostInit]
        internal static void RegisterUnicodeNameCommand() {
            ServerMain.ServerCustomCommandsDict[CommandUnicodeName] = (plr, cmd, args) => {
                if (!Net.IsServer || plr == null || string.IsNullOrEmpty(cmd)) {
                    return;
                }

                string trimmedCmd = cmd.Trim();
                if (trimmedCmd.Length <= CommandUnicodeName.Length) {
                    return;
                }

                string rawPayload = trimmedCmd.Substring(CommandUnicodeName.Length).Trim();
                string cleanName = Multiplayer.SanitizeUnicodeTextInput(rawPayload);
                if (!Multiplayer.IsValidPlayerName(cleanName, out _)) {
                    return;
                }

                // 直连模式下防止被按名字封禁的玩家通过 ASCII 握手替代名绕过封禁校验，并消解重名冲突
                if (Net.Transport is TransportLiteNetLib tlnl) {
                    if (tlnl.ClientIdToPeerDict.TryGet(plr.playerId, out NetPeer peer)
                        && peer != null
                        && BanList.IsBannedIPName(peer.Address.ToString(), cleanName)) {
                        plr.Server_Kick("Banned");
                        return;
                    }

                    bool nameTaken = ScavPlayer.ClientIdToPlayerDict.Values.Any(
                        other => other != null && other != plr && string.Equals(other.playerName, cleanName, StringComparison.Ordinal));
                    if (nameTaken) {
                        string suffix = "_" + plr.playerId;
                        int maxPrefixLen = Math.Max(1, Multiplayer.MAX_NAME_LENGTH - suffix.Length);
                        string prefix = cleanName.Length > maxPrefixLen ? cleanName.Substring(0, maxPrefixLen) : cleanName;
                        cleanName = prefix + suffix;
                    }
                }

                if (string.Equals(plr.playerName, cleanName, StringComparison.Ordinal)
                    && (plr.BasePlayer == null || string.Equals(plr.BasePlayer.playerName.Value, cleanName, StringComparison.Ordinal))) {
                    return;
                }

                plr.ApplyNameAndColor(cleanName, plr.playerColor);
            };
        }
    }
}
