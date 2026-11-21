using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 动态系统 Unicode/CJK 字体回退挂载与三版本兼容 Unicode 昵称同步器（修复上游 Issue #40 / #35）。
    /// <para>
    /// <b>设计动机与引擎契约：</b><br/>
    /// 1. 模组内置像素字体 <c>"Retro GamingPix"</c> 仅包含 ASCII 字形，<see cref="ScavPlayer.ApplyNameAndColor"/> 调用的
    /// <see cref="FontUtils.ReplaceMissingCharacters(TMP_FontAsset, string)"/> 内部执行 <c>font.HasCharacter(c, true, true)</c>（递归查询 <c>fallbackFontAssetTable</c> 并按需动态烘焙），
    /// 缺失回退表时会将所有中文/俄文/日韩文字符替换为 <c>'?'</c>。<br/>
    /// 2. Unity 2022 中 <see cref="Font.CreateDynamicFontFromOSFont(string[], int)"/> 返回的内存句柄不含磁盘路径与内嵌 TTF 流，
    /// 传给 <see cref="TMP_FontAsset.CreateFontAsset(Font)"/> 会因 <c>FontEngine.LoadFontFace</c> 失败永远返回 <c>null</c>；
    /// 必须通过 <see cref="Font.GetPathsToOSFonts"/> 解析真实 <c>.ttf/.ttc/.otf</c> 文件路径并以 <c>new Font(fontFilePath)</c> 构造后创建动态多图集 <see cref="TMP_FontAsset"/>。<br/>
    /// 3. Steam 原版 <c>v5.5.3.0</c> 房主在握手阶段（<see cref="Multiplayer.ValidateClientConnectIntroductionPacket"/>）强制执行纯 ASCII 清洗且要求 <c>Length &gt; 2</c>。
    /// 因此客户端握手包始终携带确定性 ASCII 安全名保证 100% 通过旧房主校验，入服后再通过既有 <c>CustomCommand</c> 通道上报 <c>"unicode_name"</c> 升级真实昵称（旧房主会静默忽略未知命令）。
    /// </para>
    /// </summary>
    public static class UnicodeFontFallback {
        public const string CommandUnicodeName = "unicode_name";

        // 主 CJK 与全字符集字体文件主名优先级（忽略大小写；优先精确匹配常规体，避免误选粗体/扩展子集）
        private static readonly string[] PrimaryCjkFontStems = new string[] {
            "msyh",
            "simhei",
            "simsun",
            "deng",
            "malgun",
            "meiryo",
            "yugoth",
            "notosanscjk",
            "notosans",
            "wqy",
            "arialuni",
            "segoeui",
            "arial",
        };

        // 西里尔与通用拉丁扩展补充字体文件主名优先级（选用与主 CJK 不同的独立字体文件构成双层回退链）
        private static readonly string[] SecondaryUniversalFontStems = new string[] {
            "segoeui",
            "arial",
            "arialuni",
            "notosans",
            "msyh",
            "simhei",
            "simsun",
            "deng",
        };

        private static readonly List<TMP_FontAsset> _cachedFallbackAssets = new List<TMP_FontAsset>(2);
        private static readonly List<Font> _cachedOsFonts = new List<Font>(2);
        private static readonly List<TMP_FontAsset> _stockGlobalFallbacks = new List<TMP_FontAsset>(4);
        private static bool _initializationAttempted;
        private static bool _globalFallbackRegistered;

        // 客户端当前传输层实例已上报过的 Unicode 昵称快照，保证同一连接会话幂等单次发送、断线重连自动重发
        private static NetTransportBase _lastSyncedTransport;
        private static string _lastSyncedUnicodeName;

        /// <summary>
        /// 确保动态 OS Unicode 回退字体资产已构建并幂等挂载到目标 <see cref="TMP_FontAsset"/>、原版内置回退表及全局 TMP 回退链。
        /// </summary>
        public static void EnsureInstalled(TMP_FontAsset targetFont) {
            try {
                IReadOnlyList<TMP_FontAsset> fallbacks = GetOrCreateFallbackAssets();
                if (fallbacks.Count == 0 && _stockGlobalFallbacks.Count == 0) {
                    return;
                }

                AttachFallbackChainToFont(targetFont, fallbacks);

                if (TMP_Settings.instance != null) {
                    if (!_globalFallbackRegistered) {
                        List<TMP_FontAsset> globalFallbacks = TMP_Settings.fallbackFontAssets;
                        if (globalFallbacks != null) {
                            for (int i = 0; i < fallbacks.Count; i++) {
                                TMP_FontAsset fb = fallbacks[i];
                                if (fb != null && !globalFallbacks.Contains(fb)) {
                                    globalFallbacks.Add(fb);
                                }
                            }
                        }
                        _globalFallbackRegistered = true;
                    }

                    if (TMP_Settings.defaultFontAsset != null) {
                        AttachFallbackChainToFont(TMP_Settings.defaultFontAsset, fallbacks);
                    }
                }

                if (CharStatusVisuals.PREFAB_ExpTalkText != null) {
                    TMP_Text prefabTmp = CharStatusVisuals.PREFAB_ExpTalkText.GetComponent<TMP_Text>();
                    if (prefabTmp != null && prefabTmp.font != null) {
                        AttachFallbackChainToFont(prefabTmp.font, fallbacks);
                    }
                }
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, nameof(EnsureInstalled), ex);
            }
        }

        private static void AttachFallbackChainToFont(TMP_FontAsset font, IReadOnlyList<TMP_FontAsset> dynamicFallbacks) {
            if (font == null) {
                return;
            }
            for (int i = 0; i < dynamicFallbacks.Count; i++) {
                AttachFallbackToFont(font, dynamicFallbacks[i]);
            }
            // 将原版 TMP_Settings.fallbackFontAssets 已有的游戏内置多语言回退资产一并串入目标字体回退表
            for (int i = 0; i < _stockGlobalFallbacks.Count; i++) {
                AttachFallbackToFont(font, _stockGlobalFallbacks[i]);
            }
            if (TMP_Settings.instance != null && TMP_Settings.fallbackFontAssets != null) {
                List<TMP_FontAsset> currentGlobals = TMP_Settings.fallbackFontAssets;
                for (int i = 0; i < currentGlobals.Count; i++) {
                    AttachFallbackToFont(font, currentGlobals[i]);
                }
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

        private static IReadOnlyList<TMP_FontAsset> GetOrCreateFallbackAssets() {
            if (_cachedFallbackAssets.Count > 0 || _initializationAttempted) {
                return _cachedFallbackAssets;
            }
            _initializationAttempted = true;

            try {
                // 在向全局回退表注入自定义资产前，先快照原版内置回退资产
                if (TMP_Settings.instance != null && TMP_Settings.fallbackFontAssets != null) {
                    foreach (TMP_FontAsset stock in TMP_Settings.fallbackFontAssets) {
                        if (stock != null && !_stockGlobalFallbacks.Contains(stock)) {
                            _stockGlobalFallbacks.Add(stock);
                        }
                    }
                }

                string[] osFontPaths = Font.GetPathsToOSFonts();
                if (osFontPaths == null || osFontPaths.Length == 0) {
                    ModLog.Warn(LogDomain.UI, "UnicodeFontFallback: Font.GetPathsToOSFonts returned empty.");
                    return _cachedFallbackAssets;
                }

                HashSet<string> usedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                List<string> loadedFiles = new List<string>(2);

                List<string> cjkCandidates = OrderCandidateFontPaths(osFontPaths, PrimaryCjkFontStems);
                TMP_FontAsset primaryAsset = TryCreateDynamicFontAsset(cjkCandidates, usedPaths, "CT_UnicodeDynamicFallback_CJK", out string primaryPath);
                if (primaryAsset != null) {
                    _cachedFallbackAssets.Add(primaryAsset);
                    loadedFiles.Add(Path.GetFileName(primaryPath));
                }

                List<string> universalCandidates = OrderCandidateFontPaths(osFontPaths, SecondaryUniversalFontStems);
                TMP_FontAsset secondaryAsset = TryCreateDynamicFontAsset(universalCandidates, usedPaths, "CT_UnicodeDynamicFallback_Universal", out string secondaryPath);
                if (secondaryAsset != null) {
                    _cachedFallbackAssets.Add(secondaryAsset);
                    loadedFiles.Add(Path.GetFileName(secondaryPath));
                }

                if (_cachedFallbackAssets.Count == 0) {
                    ModLog.Warn(LogDomain.UI, "UnicodeFontFallback: unable to build any TMP_FontAsset from OS font paths.");
                } else {
                    ModLog.Info(LogDomain.UI, $"UnicodeFontFallback: installed {_cachedFallbackAssets.Count} dynamic OS fallback font asset(s) [{string.Join(", ", loadedFiles)}] (stockFallbacks={_stockGlobalFallbacks.Count}).");
                }

                return _cachedFallbackAssets;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.UI, nameof(GetOrCreateFallbackAssets), ex);
                return _cachedFallbackAssets;
            }
        }

        private static TMP_FontAsset TryCreateDynamicFontAsset(
            List<string> orderedPaths,
            HashSet<string> usedPaths,
            string assetName,
            out string selectedPath) {
            selectedPath = string.Empty;
            for (int i = 0; i < orderedPaths.Count; i++) {
                string fontPath = orderedPaths[i];
                if (usedPaths.Contains(fontPath)) {
                    continue;
                }

                Font osFont = null;
                try {
                    osFont = new Font(fontPath);
                    TMP_FontAsset asset = TMP_FontAsset.CreateFontAsset(osFont);
                    if (asset == null) {
                        UnityEngine.Object.Destroy(osFont);
                        continue;
                    }

                    osFont.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(osFont);
                    _cachedOsFonts.Add(osFont);

                    asset.name = assetName;
                    asset.atlasPopulationMode = AtlasPopulationMode.Dynamic;
                    asset.isMultiAtlasTexturesEnabled = true;
                    asset.hideFlags = HideFlags.HideAndDontSave;
                    UnityEngine.Object.DontDestroyOnLoad(asset);

                    usedPaths.Add(fontPath);
                    selectedPath = fontPath;
                    return asset;
                } catch {
                    if (osFont != null) {
                        UnityEngine.Object.Destroy(osFont);
                    }
                }
            }
            return null;
        }

        private static List<string> OrderCandidateFontPaths(string[] osFontPaths, string[] preferredStems) {
            List<string> validPaths = new List<string>(osFontPaths.Length);
            for (int i = 0; i < osFontPaths.Length; i++) {
                string p = osFontPaths[i];
                if (string.IsNullOrWhiteSpace(p)) {
                    continue;
                }
                string ext = Path.GetExtension(p);
                if (!string.Equals(ext, ".ttf", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ext, ".ttc", StringComparison.OrdinalIgnoreCase)
                    && !string.Equals(ext, ".otf", StringComparison.OrdinalIgnoreCase)) {
                    continue;
                }
                if (File.Exists(p)) {
                    validPaths.Add(p);
                }
            }

            List<string> ordered = new List<string>(preferredStems.Length * 2);
            HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

            // 第一轮精确匹配文件主名（如 msyh.ttc / simhei.ttf / segoeui.ttf），优先锁定常规字重而非粗斜体变体
            foreach (string stem in preferredStems) {
                for (int i = 0; i < validPaths.Count; i++) {
                    string p = validPaths[i];
                    string fileStem = Path.GetFileNameWithoutExtension(p);
                    if (string.Equals(fileStem, stem, StringComparison.OrdinalIgnoreCase) && seen.Add(p)) {
                        ordered.Add(p);
                    }
                }
            }

            // 第二轮包含匹配（兼容 NotoSansCJK-Regular.ttc / wqy-microhei.ttc 等跨平台文件名）
            foreach (string stem in preferredStems) {
                for (int i = 0; i < validPaths.Count; i++) {
                    string p = validPaths[i];
                    string fileStem = Path.GetFileNameWithoutExtension(p);
                    if (fileStem.IndexOf(stem, StringComparison.OrdinalIgnoreCase) >= 0 && seen.Add(p)) {
                        ordered.Add(p);
                    }
                }
            }

            return ordered;
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
                ModLog.Debug($"Sent unicode_name upgrade '{desired}' (handshake fallback was '{handshakeSafe}').", LogDomain.Lobby);
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

            // 启动阶段提前预热动态字体回退链并注册至全局 TMP_Settings，使主菜单与聊天面板从首帧起即支持 CJK/西里尔渲染
            EnsureInstalled(TMP_Settings.instance != null ? TMP_Settings.defaultFontAsset : null);
        }
    }
}
