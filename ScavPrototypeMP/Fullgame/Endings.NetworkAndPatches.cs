#if FULLGAME
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版联机多线并发结局（网络同步、第 10 层破表推进、大厅返回门控与结局 Harmony 补丁分部）。
/// </summary>
public static partial class ConcurrentEndings {
    /// <summary>第 10 层首个存活玩家抵达顶部出口时，由房主在全员机器上启动破表滚动文本。</summary>
    public static void Server_TryStartAscentCutscene() {
        if (!Net.IsServer || _ascentCutsceneStarted) {
            return;
        }
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.biomeDepth != 10 || world.doingRegen || world.generatingWorld || !world.worldExists) {
            return;
        }
        bool reached = false;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            if (FullgameWorldCompat.IsAtLayerExit(plr.body)) {
                reached = true;
                break;
            }
        }
        if (!reached) {
            return;
        }
        _ascentCutsceneStarted = true;
        FindTheBugInator.Mark("ascent-go");
        BeginAscentScroller();
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.AscentGo);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void TickLocalAscent() {
        if (!Net.IsRunning || Net.IsServer) {
            return;
        }
        if (!FullgameWorldCompat.IsAscentLayer()) {
            return;
        }
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null || !local.alive || !FullgameWorldCompat.IsAtLayerExit(local)) {
            return;
        }
        BeginAscentScroller();
    }

    public static void BeginAscentScroller() {
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.biomeDepth != 10) {
            return;
        }
        Traverse fields = Traverse.Create(world);
        Coroutine running = fields.Field("cutsceneCoroutine").GetValue<Coroutine>();
        if (running != null) {
            return;
        }
        Coroutine started = world.StartCoroutine(MpSurfaceBreachThenRequestLab(world));
        fields.Field("cutsceneCoroutine").SetValue(started);
    }

    public static void RequestAdvanceToEndLab() {
        if (!Net.IsRunning || WorldGeneration.world == null) {
            return;
        }
        if (Net.IsServer) {
            if (ScavPlayer.LocalPlayer != null) {
                MarkAscentDone(ScavPlayer.LocalPlayer.playerId);
            }
            Server_TryAdvanceToEndLab();
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.EndLabReady);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static void MarkAscentDone(knetid playerId) {
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10) {
            return;
        }
        _ascentDone.Add(playerId);
    }

    static bool AllLivingFinishedAscent() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            any = true;
            if (!_ascentDone.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    public static void Server_TryAdvanceToEndLab() {
        if (!Net.IsServer || WorldGeneration.world == null) {
            return;
        }
        var world = WorldGeneration.world;
        if (world.biomeDepth != 10) {
            return;
        }
        bool doingRegen = (bool)Traverse.Create(world).Field("doingRegen").GetValue();
        if (doingRegen || world.generatingWorld) {
            return;
        }
        if (!AllLivingFinishedAscent()) {
            FindTheBugInator.Mark("endlab-wait-ascent");
            Multiplayer.DoMultiplayerStatusMessageLog("ConcurrentEndings: waiting for every living player to finish the ascent cutscene");
            return;
        }
        FindTheBugInator.Mark("endlab-advance");
        Multiplayer.DoMultiplayerStatusMessageLog("ConcurrentEndings: advancing Crimson Ascent → End Lab");
        PlayerCheckpoint.WriteLayerTransition();
        world.StartCoroutine(world.RegenerateWorld());
    }

    /// <summary>上报结局阶段标记：recv=已进入结局演出，done=已点完结算回执，escape=无芯片成功逃出。</summary>
    public static void ReportEnding(string which) {
        if (!Net.IsRunning || ScavPlayer.LocalPlayer == null) {
            return;
        }
        if (Net.IsServer) {
            NoteEnding(ScavPlayer.LocalPlayer, which);
            return;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.EndingFlag + " " + which);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    private static void NoteEnding(ScavPlayer plr, string which) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth < 10) {
            return;
        }
        FindTheBugInator.Mark("flag-" + which, "player=" + (plr.playerName ?? "?"));
        if (which == "recv") {
            _endingReceived.Add(plr.playerId);
        } else if (which == "done" || which == "escape") {
            _endingReceived.Add(plr.playerId);
            _cutsceneFinished.Add(plr.playerId);
            if (which == "escape") {
                _unchippedEscapeFinished = true;
            }
        }
        TryReturnLobbyIfReady();
    }

    private static bool MustFinishEnding(ScavPlayer plr) {
        if (plr == null) {
            return false;
        }
        if (_endingReceived.Contains(plr.playerId) || _cutsceneFinished.Contains(plr.playerId)) {
            return true;
        }
        return plr.body != null && plr.body.alive;
    }

    public static bool AllCutscenesFinished() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (!MustFinishEnding(plr)) {
                continue;
            }
            any = true;
            if (!_cutsceneFinished.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    private static bool AllEndingsReady() {
        bool any = false;
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (!MustFinishEnding(plr)) {
                continue;
            }
            any = true;
            if (!_endingReceived.Contains(plr.playerId) || !_cutsceneFinished.Contains(plr.playerId)) {
                return false;
            }
        }
        return any;
    }

    public static bool BlockHostReturn() {
        if (!Net.IsRunning || !Net.IsServer) {
            return false;
        }
        if (!ScientistDead && !EndingCardVisible && _endingReceived.Count == 0) {
            return false;
        }
        return !AllCutscenesFinished();
    }

    public static void TryReturnLobbyIfReady() {
        if (!Net.IsServer || _lobbyReturnStarted || !Net.IsRunning) {
            return;
        }
        if (!ScientistDead && !_unchippedEscapeFinished) {
            return;
        }
        if (!AllEndingsReady()) {
            return;
        }
        _lobbyReturnStarted = true;
        FindTheBugInator.Mark("lobby-return");
        if (PlayerCamera.main != null) {
            PlayerCamera.main.ToMainMenu();
        }
    }

    [CTPostInit]
    static void Init() {
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.AscentGo] = (cmd, reader) => {
            BeginAscentScroller();
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.EndLabReady] = (plr, cmd, args) => {
            if (plr != null) {
                MarkAscentDone(plr.playerId);
            }
            Server_TryAdvanceToEndLab();
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.CargoOff] = (plr, cmd, args) => {
            MacroFleshRebuilderMp.HostAcceptHeartRemoved(plr);
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.BlueHeartRemoved] = (plr, cmd, args) => {
            MacroFleshRebuilderMp.HostAcceptHeartRemoved(plr);
        };
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.BlueHeartRemoved] = (cmd, reader) => {
            MacroFleshRebuilderMp.GrantLocalEscape();
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.EndingFlag] = (plr, cmd, args) => {
            string which = (args != null && args.Length > 1) ? args[1] : "done";
            NoteEnding(plr, which);
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.SurgeryTp] = (plr, cmd, args) => {
            if (plr == null || plr.body == null || !plr.body.alive) {
                return;
            }
            if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11 || !ScientistDead) {
                return;
            }
            HostTeleportAllIntoSurgery();
        };
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.SurgeryBags] = (plr, cmd, args) => {
            if (plr == null || plr.body == null || !plr.body.alive || !plr.IsUnchipped()) {
                return;
            }
            if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 11) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, SurgeryRoom) > 12f
                && Vector2.Distance(plr.body.transform.position, SurgeryEntrance) > 16f) {
                return;
            }
            EndLabScript lab = EndLabScript.instance;
            if (lab == null) {
                return;
            }
            HostSpawnExtraAnestheticBags(lab);
        };
        WorldMP.OnWorldgenFinish += () => {
            ResetLayerLocalState();
            TryGrantEndLabWoncharEarly();
        };
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.MercEnding))]
static class WorldGeneration_MercEnding_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ConcurrentEndings.MpMercEnding(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.SurfaceBreachCutscene))]
static class WorldGeneration_SurfaceBreachCutscene_MultiplayerPatch {
    static bool Prefix(WorldGeneration __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = ConcurrentEndings.MpSurfaceBreachThenRequestLab(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(EndLabScript), nameof(EndLabScript.BeginEscapeSequence))]
static class EndLabScript_BeginEscapeSequence_MultiplayerPatch {
    static bool UnchippedGunman() {
        NetBody shooter = TurretScript_Shoot_MultiplayerPatch.LastActiveShooter;
        if (shooter == null) {
            return false;
        }
        if (shooter.plr != null) {
            return shooter.plr.IsUnchipped();
        }
        return shooter.unchipped;
    }

    static bool Prefix(EndLabScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (ConcurrentEndings.ApplyingScientistShot) {
            return false;
        }
        if (__instance.doingEscape || ConcurrentEndings.ScientistDead) {
            return false;
        }
        // 自动炮塔或携带芯片玩家的子弹不得开启逃生序列或引爆队友项圈
        if (!UnchippedGunman()) {
            return false;
        }

        if (Net.IsServer) {
            BroadcastScientistShot();
            ConcurrentEndings.ApplyingScientistShot = true;
            try {
                ConcurrentEndings.OnScientistShotLocal();
            } finally {
                ConcurrentEndings.ApplyingScientistShot = false;
            }
        } else {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ScientistShot);
            writer.Put(true);
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }
        return false;
    }

    static void BroadcastScientistShot() {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ScientistShot);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ScientistShot)]
    static void Server_ScientistShot(knetid clientId, NetDataReader reader) {
        if (ConcurrentEndings.ScientistDead) {
            return;
        }
        // 严格校验开枪者存活、无芯片且服务端当前处于第 11 层终局实验室，杜绝跨层伪造包引爆全队项圈
        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AliveInWorld, LogDomain.Story, (byte)NetmsgId.SERVER_ScientistShot, out VerifiedCaller caller)) {
            return;
        }
        EndLabScript lab = EndLabScript.instance;
        if (!caller.Player.IsUnchipped()
            || WorldGeneration.world == null
            || WorldGeneration.world.biomeDepth != 11
            || lab == null
            || lab.doingEscape) {
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, (ushort)clientId, detailCode: (int)NetmsgId.SERVER_ScientistShot);
            return;
        }
        BroadcastScientistShot();
        ConcurrentEndings.ApplyingScientistShot = true;
        try {
            ConcurrentEndings.OnScientistShotLocal();
        } finally {
            ConcurrentEndings.ApplyingScientistShot = false;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ScientistShot)]
    static void Client_ScientistShot(knetid _, NetDataReader reader) {
        ConcurrentEndings.ApplyingScientistShot = true;
        try {
            ConcurrentEndings.OnScientistShotLocal();
        } finally {
            ConcurrentEndings.ApplyingScientistShot = false;
        }
    }
}

[HarmonyPatch(typeof(FullgameMultiplayerPatcher), "Update")]
static class FullgameMultiplayerPatcher_Cutscene_SkipFinaleBroadcast_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning || WorldGeneration.world == null) {
            return true;
        }
        if (WorldGeneration.world.biomeDepth >= 10) {
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(WorldGeneration), "SetGameWon")]
static class WorldGeneration_SetGameWon_MultiplayerPatch {
    static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        ConcurrentEndings.AnnounceEndingCredit();
        ConcurrentEndings.ReportEnding("recv");
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.EndSequence))]
static class PlayerCamera_EndSequence_MultiplayerPatch {
    static void Postfix(int type, ref IEnumerator __result) {
        if (!Net.IsRunning || __result == null) {
            return;
        }
        if (type == 0) {
            if (ConcurrentEndings.SuppressDeathScreen) {
                __result = ConcurrentEndings.EmptyFinale();
            }
            return;
        }
        bool chippedReceipt = type == 1 && ConcurrentEndings.AllowChippedReceipt;
        if (chippedReceipt) {
            ConcurrentEndings.AllowChippedReceipt = false;
        }
        if (ConcurrentEndings.FinaleLatched && !ConcurrentEndings.IsLocalUnchipped() && !chippedReceipt) {
            __result = ConcurrentEndings.EmptyFinale();
            return;
        }
        IEnumerator inner = __result;
        __result = AfterEndingReceipt(inner, type);
    }

    private static IEnumerator AfterEndingReceipt(IEnumerator inner, int type) {
        ConcurrentEndings.EndingCardVisible = true;
        while (inner.MoveNext()) {
            yield return inner.Current;
        }
        Body body = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (body != null) {
            body.forceNoMovement = false;
            body.forceWalk = false;
        }
        SharedMain.IsInCutscene = false;
        bool chipDeath = type == 1 && ConcurrentEndings.ScientistDead && !ConcurrentEndings.IsLocalUnchipped();
        if (chipDeath && body != null && body.alive) {
            ConcurrentEndings.LatchDeadFinale();
            if (PlayerCamera.main != null) {
                PlayerCamera.main.StartSelfDestruct();
            }
            yield return new WaitForSecondsRealtime(5f);
            ConcurrentEndings.EnterFinaleSpectator();
        }
        if (type == 2) {
            ConcurrentEndings.ReportEnding("escape");
        } else {
            ConcurrentEndings.ReportEnding("done");
        }
        ConcurrentEndings.TryReturnLobbyIfReady();
    }
}

static class ChippedBiopodEnding_Once_MultiplayerPatch {
    public static bool Replace(ref IEnumerator __result, string loreKey) {
        if (!Net.IsRunning) {
            return true;
        }
        if (ConcurrentEndings.IsLocalUnchipped() && !ConcurrentEndings.FinaleLatched) {
            return true;
        }
        if (ConcurrentEndings.FinaleLatched) {
            __result = ConcurrentEndings.EmptyFinale();
            return false;
        }
        if (loreKey != "biopodnote" && !ConcurrentEndings.ScientistDead) {
            __result = ConcurrentEndings.IgnoreEarlyBrainBiopod();
            return false;
        }
        __result = ConcurrentEndings.ChippedBiopodDialogue(loreKey);
        return false;
    }
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEnding))]
static class WorldGeneration_BiopodEnding_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnote");
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEndingAlt))]
static class WorldGeneration_BiopodEndingAlt_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnotealt");
}

[HarmonyPatch(typeof(WorldGeneration), nameof(WorldGeneration.BiopodEndingAlt2))]
static class WorldGeneration_BiopodEndingAlt2_Once_MultiplayerPatch {
    static bool Prefix(ref IEnumerator __result) => ChippedBiopodEnding_Once_MultiplayerPatch.Replace(ref __result, "biopodnotealt2");
}

[HarmonyPatch(typeof(PlayerCamera), "HandleDeathScreen")]
static class PlayerCamera_HandleDeathScreen_SkipFinale_MultiplayerPatch {
    static bool Prefix() {
        return !Net.IsRunning || !ConcurrentEndings.SuppressDeathScreen;
    }
}

[HarmonyPatch(typeof(UIInGame), nameof(UIInGame.StopSpectatorMode))]
static class UIInGame_StopSpectatorMode_KeepFinale_MultiplayerPatch {
    static void Postfix() {
        if (!ConcurrentEndings.KeepSpectating || !Net.IsRunning) {
            return;
        }
        if (!Util.IsInWorld()) {
            ConcurrentEndings.KeepSpectating = false;
            return;
        }
        if (!UIInGame.SpectatorModeActive) {
            UIInGame.StartSpectatorMode();
        }
    }
}

static class EndingCredit_MultiplayerPatch {
    [ServerReceiver((byte)NetmsgId.SERVER_EndingCredit)]
    static void Server_EndingCredit(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer sender) || sender == null) {
            return;
        }
        ushort senderId = clientId;
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth < 10) {
            MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, senderId, detailCode: (int)NetmsgId.SERVER_EndingCredit);
            return;
        }
        ConcurrentEndings.GrantWoncharForLocalCharacter();
        List<knetid> others = new List<knetid>();
        foreach (knetid id in ServerMain.AllClientIdsExceptHost) {
            if ((ushort)id != senderId) {
                others.Add(id);
            }
        }
        ConcurrentEndings.SendEndingCredit(others);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_EndingCredit)]
    static void Client_EndingCredit(knetid _, NetDataReader reader) {
        ConcurrentEndings.GrantWoncharForLocalCharacter();
    }
}

[HarmonyPatch(typeof(MusicManager), "PlaySong")]
static class MusicManager_PlaySong_EndingAudible {
    static void Postfix() {
        if (MusicManager.main == null || WorldGeneration.world == null) {
            return;
        }
        if (WorldGeneration.world.biomeDepth >= 10) {
            MusicManager.main.cutsceneVolMult = 1f;
        }
    }
}
#endif
