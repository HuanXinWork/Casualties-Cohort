using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Newtonsoft.Json;
using Together.Diagnostics;
using UnityEngine;

namespace Together.Api {
    /// <summary>
    /// 面向存档管理模组（如 <c>CuSaveManager</c>）开放的单机直通闸门、世界种子覆写、自定义存档侧车段与玩家坐标批量恢复官方接口。
    /// </summary>
    public static class TogetherSaveWorldApi {
        private sealed class SaveSectionEntry {
            public string ModKey;
            public Func<string> ExportJson;
            public Action<string> ImportJson;
        }

        private static readonly List<SaveSectionEntry> SaveSections = new List<SaveSectionEntry>();

        /// <summary>
        /// 单机模式放行覆写委托：当联机模组已加载但当前未处于联机会话（<c>!Net.IsRunning</c>）时，
        /// 若此委托返回 <c>true</c>，Together 的 <c>PreRunScript.StartRun</c> 与 <c>LoadRun</c> 补丁将原生放行单人模式，无需外部模组反射拆补丁。
        /// </summary>
        public static Func<bool> AllowSingleplayerStartOverride { get; set; }

        /// <summary>
        /// 服务端生成新层级世界前的官方种子覆写提供器（输入目标层级 <c>depth</c>，返回非空 <c>int</c> 则覆写该层世界种子）。
        /// </summary>
        public static Func<int, int?> ServerWorldSeedOverride { get; set; }

        /// <summary>
        /// 房主在仍有存活队友（参数为存活玩家数）的情况下触发 <c>ToMainMenu</c> 前的拦截确认钩子；返回 <c>false</c> 则阻止本次退回主菜单。
        /// </summary>
        public static Func<int, bool> ConfirmHostExitWhenPlayersAlive { get; set; }

        /// <summary>世界开始生成层级时触发（参数：depth, seed）。</summary>
        public static event Action<int, int> OnWorldGenerationStarted;

        /// <summary>世界完成生成层级后触发（参数：depth）。</summary>
        public static event Action<int> OnWorldGenerationFinished;

        /// <summary>联机存档保存完成后触发（参数：存档目录路径）。</summary>
        public static event Action<string> OnMultiplayerSaveWritten;

        /// <summary>联机存档读取完成后触发（参数：存档目录路径）。</summary>
        public static event Action<string> OnMultiplayerSaveLoaded;

        /// <summary>读写 <see cref="SavesystemPatch.PendingLoadMpSave"/> 标志的官方强类型入口。</summary>
        public static bool PendingLoadMpSave {
            get => SavesystemPatch.PendingLoadMpSave;
            set => SavesystemPatch.PendingLoadMpSave = value;
        }

        /// <summary>调用联机原生继续游戏/读档入口。</summary>
        public static bool TryContinueMultiplayerGame(bool loadSave = true) {
            if (!Plugin.SUCCESFULLY_INITIALIZED || Plugin.FORCE_DISABLE_MP_MOD) {
                return false;
            }
            if (Net.IsRunning && !Net.IsServer) {
                return false;
            }
            try {
                WorldMP.LoadVanillaGeneratedWorld(loadSave);
                return true;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(TryContinueMultiplayerGame), ex);
                return false;
            }
        }

        /// <summary>服务端按玩家持久 ID 批量恢复角色坐标，返回成功传送的玩家人数。</summary>
        public static int RestorePlayersByPersistentId(IReadOnlyDictionary<string, Vector2> positions) {
            if (positions == null || positions.Count == 0 || !Net.IsRunning || !Net.IsServer || ScavPlayer.BodyToPlayerDict == null) {
                return 0;
            }
            int restored = 0;
            foreach (KeyValuePair<Body, ScavPlayer> kv in ScavPlayer.BodyToPlayerDict) {
                ScavPlayer plr = kv.Value;
                if (plr == null) {
                    continue;
                }
                string pid = plr.GetPersistentId();
                if (!string.IsNullOrEmpty(pid) && positions.TryGetValue(pid, out Vector2 targetPos)) {
                    plr.Server_TeleportCharacter(targetPos);
                    restored++;
                }
            }
            return restored;
        }

        /// <summary>
        /// 注册随联机存档一并读写的外部模组自定义 JSON 侧车分段（保存在存档目录的 <c>mp_mod_sections.json</c>，不污染原版二进制块结构）。
        /// </summary>
        public static void RegisterSaveSection(string modKey, Func<string> exportJson, Action<string> importJson) {
            if (string.IsNullOrWhiteSpace(modKey)) {
                return;
            }
            for (int i = 0; i < SaveSections.Count; i++) {
                if (string.Equals(SaveSections[i].ModKey, modKey, StringComparison.OrdinalIgnoreCase)) {
                    SaveSections[i].ExportJson = exportJson;
                    SaveSections[i].ImportJson = importJson;
                    return;
                }
            }
            SaveSections.Add(new SaveSectionEntry {
                ModKey = modKey,
                ExportJson = exportJson,
                ImportJson = importJson,
            });
        }

        public static bool UnregisterSaveSection(string modKey) {
            return !string.IsNullOrWhiteSpace(modKey)
                && SaveSections.RemoveAll(x => string.Equals(x.ModKey, modKey, StringComparison.OrdinalIgnoreCase)) > 0;
        }

        internal static bool ShouldBypassSingleplayerBlock() {
            if (Net.IsRunning || AllowSingleplayerStartOverride == null) {
                return false;
            }
            try {
                return AllowSingleplayerStartOverride.Invoke();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(ShouldBypassSingleplayerBlock), ex);
                return false;
            }
        }

        internal static int ResolveServerWorldSeed(int depth, int defaultSeed) {
            if (ServerWorldSeedOverride == null) {
                return defaultSeed;
            }
            try {
                int? custom = ServerWorldSeedOverride.Invoke(depth);
                return custom ?? defaultSeed;
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(ResolveServerWorldSeed), ex);
                return defaultSeed;
            }
        }

        internal static bool ShouldAllowHostExitToMainMenu(int livingCount) {
            if (livingCount <= 0 || ConfirmHostExitWhenPlayersAlive == null) {
                return true;
            }
            try {
                return ConfirmHostExitWhenPlayersAlive.Invoke(livingCount);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(ShouldAllowHostExitToMainMenu), ex);
                return true;
            }
        }

        internal static void NotifyWorldGenStarted(int depth, int seed) {
            try { OnWorldGenerationStarted?.Invoke(depth, seed); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifyWorldGenStarted), ex); }
        }

        internal static void NotifyWorldGenFinished(int depth) {
            try { OnWorldGenerationFinished?.Invoke(depth); } catch (Exception ex) { ModLog.Exception(LogDomain.Interop, nameof(NotifyWorldGenFinished), ex); }
        }

        internal static void WriteModSaveSections(string saveDirectory) {
            if (string.IsNullOrEmpty(saveDirectory)) {
                return;
            }
            try {
                if (SaveSections.Count > 0) {
                    Dictionary<string, string> payload = new Dictionary<string, string>(StringComparer.Ordinal);
                    for (int i = 0; i < SaveSections.Count; i++) {
                        SaveSectionEntry entry = SaveSections[i];
                        if (entry.ExportJson == null) continue;
                        try {
                            string json = entry.ExportJson();
                            if (!string.IsNullOrEmpty(json)) {
                                payload[entry.ModKey] = json;
                            }
                        } catch (Exception ex) {
                            ModLog.Exception(LogDomain.Interop, "ExportSaveSection(" + entry.ModKey + ")", ex);
                        }
                    }
                    if (payload.Count > 0) {
                        Directory.CreateDirectory(saveDirectory);
                        string filePath = Path.Combine(saveDirectory, "mp_mod_sections.json");
                        File.WriteAllText(filePath, JsonConvert.SerializeObject(payload, Formatting.Indented), new UTF8Encoding(false));
                    }
                }
                OnMultiplayerSaveWritten?.Invoke(saveDirectory);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(WriteModSaveSections), ex);
            }
        }

        internal static void ReadModSaveSections(string saveDirectory) {
            if (string.IsNullOrEmpty(saveDirectory)) {
                return;
            }
            try {
                string filePath = Path.Combine(saveDirectory, "mp_mod_sections.json");
                if (SaveSections.Count > 0 && File.Exists(filePath)) {
                    Dictionary<string, string> payload = JsonConvert.DeserializeObject<Dictionary<string, string>>(File.ReadAllText(filePath, Encoding.UTF8));
                    if (payload != null) {
                        for (int i = 0; i < SaveSections.Count; i++) {
                            SaveSectionEntry entry = SaveSections[i];
                            if (entry.ImportJson != null && payload.TryGetValue(entry.ModKey, out string json)) {
                                try {
                                    entry.ImportJson(json);
                                } catch (Exception ex) {
                                    ModLog.Exception(LogDomain.Interop, "ImportSaveSection(" + entry.ModKey + ")", ex);
                                }
                            }
                        }
                    }
                }
                OnMultiplayerSaveLoaded?.Invoke(saveDirectory);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Interop, nameof(ReadModSaveSections), ex);
            }
        }
    }
}
