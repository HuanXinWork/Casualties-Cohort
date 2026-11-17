#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Newtonsoft.Json.Linq;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版联机角色选择与准备就绪门控协调器（大厅状态机、存档物种恢复、中途加入握手与网络协议分部）：
/// 新开局进入物种选择并等待全员 Ready [n/max] 后发车；读档继续则直接按存档物种跳过选人；无存档中途加入单人选定物种后直接空投。
/// </summary>
public static partial class CharacterSelectWait {
    public static bool Waiting { get; private set; }
    public static bool LocalConfirmed { get; private set; }
    public static int ReadyCount { get; private set; }
    public static int MaxCount { get; private set; }
    /// <summary>客机中途加入正在选定物种阶段（尚未进入 WaitLoad）。</summary>
    public static bool LateJoinPicking { get; private set; }

    const float ProceedDelaySeconds = 1f;

    static readonly Dictionary<knetid, byte> _selections = new Dictionary<knetid, byte>();
    static readonly HashSet<knetid> _pendingLateJoin = new HashSet<knetid>();
    static bool _pendingLoadSave;
    static bool _pendingTutorial;
    static bool _openedRunSettings;
    static bool _speciesLocked;
    static byte _lockedSpecies;
    static int _proceedToken;
    static bool _netHooks;

    public static bool SpeciesLocked => Waiting && _speciesLocked;

    static void EnsureNetHooks() {
        if (_netHooks) {
            return;
        }
        _netHooks = true;
        ScavPlayer.OnPlayerJoined += OnLobbyPlayerJoined;
        ScavPlayer.OnPlayerLeft += OnLobbyPlayerLeft;
    }

    static void OnLobbyPlayerJoined(ScavPlayer plr) {
        if (!Net.IsServer || !Waiting || plr == null) {
            return;
        }
        RefreshCountsFromLobby();
        if (!plr.IsHost) {
            SendWaitBeginTo(plr);
        }
        BroadcastProgress();
        CancelProceed();
    }

    static void OnLobbyPlayerLeft(ScavPlayer plr) {
        if (plr != null) {
            _pendingLateJoin.Remove(plr.playerId);
        }
        if (!Net.IsServer || !Waiting) {
            return;
        }
        if (plr != null) {
            _selections.Remove(plr.playerId);
        }
        RefreshCountsFromLobby();
        BroadcastProgress();
        if (ReadyCount >= MaxCount && MaxCount > 0 && ReadyCount > 0) {
            ScheduleProceed();
        } else {
            CancelProceed();
        }
    }

    public static void Server_MarkPendingLateJoin(knetid clientId) {
        if (!Net.IsServer) {
            return;
        }
        _pendingLateJoin.Add(clientId);
    }

    public static bool IsPendingLateJoin(knetid clientId) {
        return _pendingLateJoin.Contains(clientId);
    }

    public static bool TryResolveSavedSpecies(ScavPlayer plr, out byte chr) {
        if (TryReadSavedCharacter(plr, out chr)) {
            return true;
        }
        if (plr == null) {
            return false;
        }
        if (ServerMain.Server_LastPlayerStates.TryGetValue(plr.GetPersistentId(), out PlayerSavedState st)) {
            chr = (byte)CharacterSpawn.ClampChar(st.charType);
            return true;
        }
        chr = 0;
        return false;
    }

    public static void Server_CompleteLateJoin(knetid clientId, byte character) {
        if (!Net.IsServer) {
            return;
        }
        if (!_pendingLateJoin.Remove(clientId)) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) || plr == null) {
            return;
        }
        plr.selectedCharacter = (byte)CharacterSpawn.ClampChar(character);
        plr.CreateCharacter();

        NetDataWriter writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);
        writer.Put(ServerMain.LastStartGameAnnouncementPacket);
        writer.Put(WorldMP.CompileRunSettings(), true);
        AchievementBreakSync.WriteAnnouncementFlag(writer);
        Net.Server_SendToClientsVeryReliable(writer, new List<knetid> { clientId });
        ServerMain.Server_AnnounceSeed(new List<knetid> { clientId });
        Multiplayer.DoMultiplayerStatusMessageLog($"{plr.playerName} joining as species {plr.selectedCharacter}...");
    }

    /// <summary>将全房间玩家选定物种列表打包写入开局公告，避免中途加入者加载界面全部回退为本地物种立绘。</summary>
    public static void WriteSpeciesRoster(NetDataWriter writer) {
        if (writer == null) {
            return;
        }
        List<knetid> ids = new List<knetid>();
        List<byte> chrs = new List<byte>();
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null) {
                continue;
            }
            ids.Add(plr.playerId);
            chrs.Add((byte)CharacterSpawn.ClampChar(plr.selectedCharacter));
        }
        int n = Mathf.Min(ids.Count, 255);
        writer.Put((byte)n);
        for (int i = 0; i < n; i++) {
            writer.Put(ids[i]);
            writer.Put(chrs[i]);
        }
    }

    public static void ReadSpeciesRoster(NetDataReader reader) {
        if (reader == null || reader.EndOfData) {
            return;
        }
        reader.Get(out byte count);
        for (int i = 0; i < count; i++) {
            if (reader.EndOfData) {
                break;
            }
            reader.Get(out knetid id);
            reader.Get(out byte chr);
            chr = (byte)CharacterSpawn.ClampChar(chr);
            if (ScavPlayer.TryGetPlayerFromClientId(id, out ScavPlayer plr) && plr != null) {
                plr.selectedCharacter = chr;
            }
        }
    }

    public static void Client_EnterLateJoinPick() {
        EnsureNetHooks();
        CancelProceed();
        LateJoinPicking = true;
        Waiting = true;
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 1;
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
            EnsureUiHooks(PreRunScript.instance);
        }
        RestoreReadyButtons();
        ApplyLocalWaitUi();
        ForceCharSelectReadyVisual(PreRunScript.instance);
        Multiplayer.DoMultiplayerStatusMessageLog("Pick a species, then Join...");
    }

    public static bool ShouldBlockStartGameAnnouncement() {
        return LateJoinPicking && !LocalConfirmed;
    }

    static void OnPresetsNextClicked() {
        if (!Net.IsRunning) {
            return;
        }
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to pick a run...");
            return;
        }
        if (!Net.IsServer) {
            return;
        }
        if (!Waiting) {
            OpenLobbyGate();
        }
    }

    public static void HostBeginTutorialPick() {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        if (Waiting && _pendingTutorial) {
            return;
        }
        OpenLobbyGate(tutorial: true);
        if (UIMainMenu.IsOpen()) {
            UIMainMenu.SetOpen(false);
        }
    }

    static void OnCharSelectBackClicked() {
        if (!Waiting || LateJoinPicking) {
            return;
        }
        Reset();
    }

    public static void EnsureHostLobbyGate() {
        EnsureNetHooks();
        if (!Net.IsRunning || !Net.IsServer || !Waiting || !Util.IsInMainMenu()) {
            return;
        }
        int max = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        if (max != MaxCount) {
            RefreshCountsFromLobby();
            BroadcastProgress();
        }
    }

    static void OpenLobbyGate(bool tutorial = false) {
        EnsureNetHooks();
        CancelProceed();
        RestoreReadyButtons();
        _pendingLoadSave = false;
        _pendingTutorial = tutorial;
        _speciesLocked = false;
        _selections.Clear();
        LocalConfirmed = false;
        Waiting = true;
        ReadyCount = 0;
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        ApplyLocalWaitUi();
        BroadcastWaitBegin();
        BroadcastProgress();
        Multiplayer.DoMultiplayerStatusMessageLog(tutorial ? "Pick a character, then start the tutorial." : "Ready up to start...");
    }

    public static void EnforceLockedSpecies() {
        if (!_speciesLocked) {
            return;
        }
        PreRunScript.characterSelected = _lockedSpecies;
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = _lockedSpecies;
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
        }
    }

    static void HostContinueDirectly() {
        EnsureNetHooks();
        CancelProceed();
        RestoreReadyButtons();
        RestoreRunRulePanels();
        SetCharBrowseInteractable(PreRunScript.instance, true);
        LateJoinPicking = false;
        LocalConfirmed = false;
        _speciesLocked = false;
        _pendingLoadSave = true;
        Waiting = true;
        SeedSavedCharacters();
        _selections.Clear();
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null) {
                continue;
            }
            byte chr = (byte)CharacterSpawn.ClampChar(plr.selectedCharacter);
            plr.selectedCharacter = chr;
            _selections[plr.playerId] = chr;
        }
        ReadyCount = _selections.Count;
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        HostProceed();
    }

    public static void HostBeginNewRun(bool loadSave) {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        if (loadSave) {
            HostContinueDirectly();
            return;
        }
        if (Waiting) {
            return;
        }
        OpenLobbyGate();
    }

    public static void HostBeginLoadRun() {
        HostContinueDirectly();
    }

    static void SeedSavedCharacters() {
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (TryReadSavedCharacter(plr, out byte chr)) {
                plr.selectedCharacter = chr;
                if (plr.IsLocal) {
                    PreRunScript.characterSelected = chr;
                }
            }
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
        }
    }

    public static bool TryReadSavedCharacter(ScavPlayer plr, out byte chr) {
        chr = 0;
        if (plr == null || SavesystemPatch.PreserveSave) {
            return false;
        }
        try {
            string folder = Path.Combine(SavesystemPatch.mpsavefolder, plr.GetPersistentId());
            string path = Path.Combine(folder, "save.sv");
            if (!File.Exists(path)) {
                return false;
            }
            JObject jObject = JObject.Parse(SaveSystem.Unzip(File.ReadAllBytes(path)));
            if (jObject["character"] != null) {
                chr = (byte)CharacterSpawn.ClampChar((int)jObject["character"]);
                return true;
            }
            if (jObject["body"] != null && jObject["body"]["charType"] != null) {
                chr = (byte)CharacterSpawn.ClampChar((int)jObject["body"]["charType"]);
                return true;
            }
        } catch (Exception e) {
            ModLog.Warn(LogDomain.Lobby, $"TryReadSavedCharacter failed for {plr}: {e.Message}");
        }
        return false;
    }

    public static void Reset() {
        CancelProceed();
        Waiting = false;
        LateJoinPicking = false;
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 0;
        _pendingLoadSave = false;
        _pendingTutorial = false;
        _speciesLocked = false;
        _selections.Clear();
        RestoreReadyButtons();
        RestoreRunRulePanels();
        HideCharSelect();
        if (_openedRunSettings) {
            PreRunScript prs = PreRunScript.instance;
            if (prs != null && prs.runSettingsScreen != null) {
                prs.runSettingsScreen.SetActive(false);
            }
            _openedRunSettings = false;
        }
        SetCharBrowseInteractable(PreRunScript.instance, true);
    }

    public static void OnStartOrLoadClicked(bool loadSave) {
        if (!Net.IsRunning) {
            return;
        }
        if (loadSave) {
            if (Net.IsClient) {
                Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to continue...");
                return;
            }
            if (!Net.IsServer) {
                return;
            }
            HostContinueDirectly();
            return;
        }
        if (Waiting) {
            if (LateJoinPicking) {
                SetLocalReady(true);
                return;
            }
            ToggleLocalReady();
            return;
        }
        if (Net.IsClient) {
            Multiplayer.DoMultiplayerStatusMessageLog("Waiting for host to pick a run...");
            return;
        }
        if (!Net.IsServer) {
            return;
        }
        OpenLobbyGate();
    }

    public static void OnLocalCharacterBrowsed(int character) {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        if (_speciesLocked) {
            EnforceLockedSpecies();
            return;
        }
        byte ch = (byte)CharacterSpawn.ClampChar(character);
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = ch;
        }
        PreRunScript.characterSelected = ch;
        SendOrRecordReady(LocalConfirmed, ch);
    }

    public static void ToggleLocalReady() {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        SetLocalReady(!LocalConfirmed);
    }

    public static void SetLocalReady(bool ready) {
        if (!Net.IsRunning || !Waiting) {
            return;
        }
        if (_speciesLocked) {
            EnforceLockedSpecies();
        }
        byte ch = (byte)CharacterSpawn.ClampChar(PreRunScript.characterSelected);
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = ch;
        }
        PreRunScript.characterSelected = ch;
        SendOrRecordReady(ready, ch);
        LocalConfirmed = ready;
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    static void SendOrRecordReady(bool ready, byte ch) {
        if (Net.IsServer) {
            ScavPlayer host = ScavPlayer.LocalPlayer;
            if (host == null || !ScavPlayer.ClientIdToPlayerDict.ContainsKey(host.playerId)) {
                ModLog.Warn(LogDomain.Lobby, $"[CharSelect] host ready failed: local={host != null} id={(host != null ? host.playerId.ToString() : "null")} lobby={ScavPlayer.ClientIdToPlayerDict.Count}");
                return;
            }
            Server_SetReady(host.playerId, ready, ch);
        } else {
            ClientMain.Client_SendCharacterReady(ready, ch);
        }
    }

    public static void Client_EnterWait(int ready, int max, int suggestedChar, bool speciesLocked = false) {
        EnsureNetHooks();
        CancelProceed();
        LateJoinPicking = false;
        Waiting = true;
        LocalConfirmed = false;
        ReadyCount = ready;
        MaxCount = Mathf.Max(1, max);
        int chr = CharacterSpawn.ClampChar(suggestedChar);
        _pendingLoadSave = speciesLocked;
        _speciesLocked = speciesLocked;
        _lockedSpecies = (byte)chr;
        PreRunScript.characterSelected = chr;
        if (ScavPlayer.LocalPlayer != null) {
            ScavPlayer.LocalPlayer.selectedCharacter = (byte)chr;
        }
        if (PreRunScript.instance != null) {
            Traverse.Create(PreRunScript.instance).Method("UpdateCharacterText").GetValue();
            EnsureUiHooks(PreRunScript.instance);
        }
        RestoreReadyButtons();
        ApplyLocalWaitUi();
    }

    public static void Client_UpdateProgress(int ready, int max) {
        ReadyCount = ready;
        MaxCount = Mathf.Max(1, max);
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    public static void Server_SetReady(knetid clientId, bool ready, byte character) {
        if (!Net.IsServer || !Waiting) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr)) {
            return;
        }
        character = (byte)CharacterSpawn.ClampChar(character);
        if (_pendingLoadSave && TryReadSavedCharacter(plr, out byte savedChr)) {
            character = savedChr;
        }
        plr.selectedCharacter = character;
        if (plr.body != null) {
            NetBody nb = plr.netBody;
            if (nb != null && nb.syncBody != null) {
                CharacterSpawn.ApplySelectedToSyncBody(nb.syncBody, plr);
            }
            CharacterSpawn.ServerRebuildBodyIfSpeciesMismatch(plr);
        }
        if (ready) {
            _selections[clientId] = character;
        } else {
            _selections.Remove(clientId);
        }
        RefreshCountsFromLobby();
        BroadcastProgress();
        if (_pendingTutorial && plr.IsHost && ready) {
            HostProceed();
            return;
        }
        if (ReadyCount >= MaxCount && MaxCount > 0) {
            ScheduleProceed();
        } else {
            CancelProceed();
        }
    }

    static void RefreshCountsFromLobby() {
        MaxCount = Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count);
        List<knetid> stale = null;
        foreach (KeyValuePair<knetid, byte> kv in _selections) {
            if (!ScavPlayer.ClientIdToPlayerDict.ContainsKey(kv.Key)) {
                stale ??= new List<knetid>();
                stale.Add(kv.Key);
            }
        }
        if (stale != null) {
            for (int i = 0; i < stale.Count; i++) {
                _selections.Remove(stale[i]);
            }
        }
        ReadyCount = _selections.Count;
    }

    static void ScheduleProceed() {
        CancelProceed();
        int token = _proceedToken;
        Util.StartCoroutine(ProceedAfterDelay(token));
        Multiplayer.DoMultiplayerStatusMessageLog("All ready - starting...");
    }

    static void CancelProceed() {
        _proceedToken++;
    }

    static IEnumerator ProceedAfterDelay(int token) {
        float t = 0f;
        while (t < ProceedDelaySeconds) {
            if (token != _proceedToken || !Waiting || !Net.IsServer) {
                yield break;
            }
            if (ReadyCount < MaxCount || MaxCount <= 0) {
                yield break;
            }
            t += Time.unscaledDeltaTime;
            yield return null;
        }
        if (token != _proceedToken || !Waiting || !Net.IsServer) {
            yield break;
        }
        if (ReadyCount < MaxCount || MaxCount <= 0) {
            yield break;
        }
        HostProceed();
    }

    static void HostProceed() {
        if (!Net.IsServer) {
            return;
        }
        bool loadSave = _pendingLoadSave;
        bool tutorial = _pendingTutorial;
        CancelProceed();
        Waiting = false;
        RestoreReadyButtons();
        RestoreRunRulePanels();
        Multiplayer.DoMultiplayerStatusMessageLog(tutorial ? "Starting tutorial..." : (loadSave ? "Continuing saved run..." : "Starting new run..."));

        foreach (KeyValuePair<knetid, byte> kv in _selections) {
            if (!ScavPlayer.TryGetPlayerFromClientId(kv.Key, out ScavPlayer plr) || plr == null) {
                continue;
            }
            byte chr = (byte)CharacterSpawn.ClampChar(kv.Value);
            plr.selectedCharacter = chr;
            if (plr.IsLocal) {
                PreRunScript.characterSelected = chr;
            }
            if (plr.body != null) {
                NetBody nb = plr.netBody;
                if (nb != null && nb.syncBody != null) {
                    CharacterSpawn.ApplySelectedToSyncBody(nb.syncBody, plr);
                }
                CharacterSpawn.ServerRebuildBodyIfSpeciesMismatch(plr);
            }
        }

        if (tutorial) {
            SaveSystem.loadedRun = false;
            SavesystemPatch.PendingLoadMpSave = false;
            WorldMP.LoadVanillaTutorialWorld();
        } else if (loadSave) {
            SaveSystem.loadedRun = true;
            SavesystemPatch.PendingLoadMpSave = true;
            WorldMP.LoadVanillaGeneratedWorld(true);
        } else {
            SaveSystem.loadedRun = false;
            SavesystemPatch.PendingLoadMpSave = false;
            SavesystemPatch.DeleteMPSave();
            if (PreRunScript.instance != null) {
                WorldGeneration.runSettings = PreRunScript.instance.runSettings;
            }
            WorldMP.LoadVanillaGeneratedWorld(false);
        }
        _selections.Clear();
        LocalConfirmed = false;
        ReadyCount = 0;
        MaxCount = 0;
        _pendingLoadSave = false;
        _pendingTutorial = false;
        _speciesLocked = false;
    }

    static void BroadcastWaitBegin() {
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null || plr.IsHost) {
                continue;
            }
            SendWaitBeginTo(plr);
        }
    }

    static void SendWaitBeginTo(ScavPlayer plr) {
        if (plr == null) {
            return;
        }
        byte saved = 0;
        bool lockSpecies = _pendingLoadSave && TryReadSavedCharacter(plr, out saved);
        int suggested = lockSpecies ? saved : plr.selectedCharacter;
        if (lockSpecies) {
            plr.selectedCharacter = saved;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CharSelectWait);
        writer.Put(ReadyCount);
        writer.Put(MaxCount);
        writer.Put(suggested);
        writer.Put(lockSpecies);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: new knetid[] { plr.playerId });
    }

    static void BroadcastProgress() {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CharSelectProgress);
        writer.Put(ReadyCount);
        writer.Put(MaxCount);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        ForceCharSelectReadyVisual(PreRunScript.instance);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SelectChar)]
    static void Server_SelectChar(knetid clientId, NetDataReader reader) {
        bool ready = true;
        int ch;
        if (reader.AvailableBytes >= 5) {
            reader.Get(out ready);
            reader.Get(out ch);
        } else {
            reader.Get(out ch);
        }
        byte chr = (byte)Mathf.Clamp(ch, 0, 255);
        if (_pendingLateJoin.Contains(clientId)) {
            if (ready) {
                Server_CompleteLateJoin(clientId, chr);
            }
            return;
        }
        Server_SetReady(clientId, ready, chr);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CharSelectWait)]
    static void Client_CharSelectWait(knetid _, NetDataReader reader) {
        reader.Get(out int ready);
        reader.Get(out int max);
        int suggested = 0;
        if (reader.AvailableBytes >= 4) {
            reader.Get(out suggested);
        }
        bool speciesLocked = false;
        if (reader.AvailableBytes >= 1) {
            reader.Get(out speciesLocked);
        }
        Client_EnterWait(ready, max, suggested, speciesLocked);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CharSelectProgress)]
    static void Client_CharSelectProgress(knetid _, NetDataReader reader) {
        reader.Get(out int ready);
        reader.Get(out int max);
        Client_UpdateProgress(ready, max);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_LateJoinCharSelect)]
    static void Client_LateJoinCharSelect(knetid _, NetDataReader reader) {
        Client_EnterLateJoinPick();
    }
}
#endif
