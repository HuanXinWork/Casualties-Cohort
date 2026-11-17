using CasualtiesTogetherUtils;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Reflection;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

public partial class ClientMain {

    internal static void _RegisterClientReceivers() {
#if DEBUG
        Plugin.log.LogInfo("REGISTERING CLIENT Receivers");
#endif
        foreach (var item in Multiplayer.all_network_receivers_from_attributes) {
            if (item.Key.GetReceiverType() == NetworkMessageReceiverAttribute.ReceiverType.Client) {
                var attr = (ClientReceiverAttribute)item.Key;

                Net.RegisterClientReceiver(
                    attr.GetMessageId(),
                    (knetid clientId, LiteNetLib.Utils.NetDataReader reader) => {
                        if (Net.IsHost && attr.ignore_host) {
                            if (log.verbose)
                                Multiplayer.DoMultiplayerStatusMessageError($"DEV: I AM HOST, I CANT DO THIS SHIT: {attr.GetMessageName()}\n{new StackTrace()}");
                            else
                                log.error($"DEV: I AM HOST, I CANT DO THIS SHIT: {attr.GetMessageName()}");
                            return;
                        }

                        try {
                            item.Value.Invoke(clientId, reader);
                        } catch (Exception e) {
                            ModLog.Exception(LogDomain.Transport, $"ClientReceiver:{attr.GetMessageName()}", e);
                        }
                    }
                );
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.PlayerDamageBlockRelay)]
    static void ClientReceiver__PlayerDamageBlockRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out Vector2Int pos);
        reader.Get(out float dmg);

        if (NetBody.TryGetNetBodyFromId(who, out var nb)) {
            if (!nb.IsBodyLocal()) {
                BlockDamageAuthority.ApplyingRelayedDamage = true;
                try {
                    var blockDamage = WorldGeneration.world.GetBlockDamage(pos);

                    if (blockDamage == null) {
                        WorldGeneration.world.DamageBlock(pos, dmg, true, false);
                    } else {
                        var diff = dmg - blockDamage.damage;

                        WorldGeneration.world.DamageBlock(pos, diff, !Mathf.Approximately(blockDamage.damage, dmg), false);
                    }
                } finally {
                    BlockDamageAuthority.ApplyingRelayedDamage = false;
                }
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CurrentPingOfAllPlayers)]
    static void ClientReceiver__CurrentPingOfAllPlayers(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        for (; ; ) {
            reader.Get(out bool next);
            if (!next) {
                break;
            }

            reader.Get(out knetid plrid);
            reader.Get(out float ping);

            if (ScavPlayer.TryGetPlayerFromClientId(plrid, out var plr)) {
                plr.ping = ping;
                if (plr.IsLocal) {
                    ClientMain.LOCAL_PING = (int)(ping * 1000f);
                }
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_AnnounceDoAlert, false)]
    static void ClientReceiver__AnnounceDoAlert(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out string alert);

        Lang.MsgTryParse(ref alert);

        reader.Get(out bool important);

        if (PlayerCamera.main) {
            log.l($"Received DoAlert: {alert} ");
            PlayerCamera.main.DoAlert(alert, important);
        }
    }

    [ClientReceiver((byte)NetmsgId.CustomLogMessage, false)]
    static void ClientReceiver__CustomLogMessage(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out string msg);
        reader.Get(out bool is_error);

        if (is_error) {
            Multiplayer.DoMultiplayerStatusMessageError(msg);
        } else {
            Multiplayer.DoMultiplayerStatusMessageLog(msg);
        }
    }

    static HashSet<Vector2Int> SetBlockChunks = new();
    [ClientReceiver((byte)NetmsgId.SetBlock)]
    static void ClientReceiver__SetBlock(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!SharedMain.LocalWorldIsGenerated) return;

        while (!reader.EndOfData) {
            reader.Get(out WorldGeneration_SetBlock_MultiplayerPatch.ChangedBlockInfo block);
            var pos = (Vector2Int)block.pos;

#if DEBUG
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                var realPos = WorldGeneration.world.BlockToWorldPos(pos);
                log.devevent($"C: SetBlock {block.block} - {block.GetBlockInfo()?.name}  {block.pos} -> {realPos}", realPos);
            }
#endif

            SetBlockChunks.Add(pos / WorldGeneration.CHUNKSIZE);

            if (block.block == 0) {
                var bdamage = WorldGeneration.world.GetBlockDamage(pos);
                if (bdamage != null) {
                    bdamage.DestroySprite();
                    WorldGeneration.world.blockDamages.Remove(bdamage);
                }
            }
            WorldGeneration.world.SetBlockNoUpdate(pos, block.block);
        }

        foreach (var v in SetBlockChunks) {
            WorldGeneration.world.UpdateChunk(v);
        }

        SetBlockChunks.Clear();
    }

    [ClientReceiver((byte)NetmsgId.CreateExplosionEffect)]
    static void ClientReceiver__CreateExplosionEffect(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!TrapClaim.ReadBoom(reader, out var boomId, out var boom))
            return;

        WorldGeneration_CreateExplosion_MultiplayerPatch.ForceCreateExplosionEffect(boom);
        TrapClaim.ClientSawBoom(boomId, boom);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerJumpRelay)]
    static void ClientReceiver__PlayerJumpRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid who);

        if (NetBody.TryGetNetBodyFromId(who, out var nb) && Util.IsWorldGenerated()) {
            nb.body.Jump();
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_forcespectator)]
    static void ClientReceiver__forcespectator(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        if (!UIInGame.SpectatorModeActive) {
            log.l("CLIENT: received command from server to enable spectator mode");
            UIInGame.StartSpectatorMode();
        }
    }

    internal static Dictionary<string, Action<string, LiteNetLib.Utils.NetDataReader>> ClientCustomCommandsDict = new();

    internal static void RunCustomCommand(LiteNetLib.Utils.NetDataReader reader) {
        if (Net.IsServer) {
            return;
        }

        var com = reader.GetString();

        if (ClientCustomCommandsDict.TryGetValue(com, out var action)) {
            try {
                action.Invoke(com, reader);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Transport, $"ClientCustomCommand:{com}", ex);
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_GoBackToMainMenu)]
    static void ClientReceiver__GoBackToMainMenu(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        Multiplayer.DoMultiplayerStatusMessageLog("Received announcement to go to main menu.");

        Multiplayer.showMultiplayerMenu = true;
        PlayerCamera.main.ToMainMenu();
    }

    [ClientReceiver((byte)NetmsgId.PlayerPointFingerAtRelay)]
    static void ClientReceiver__PlayerPointFingerAtRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out Vector2 pos);
        reader.Get(out byte t);

        if (ScavPlayer.TryGetPlayerFromClientId(who, out var plr)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, $"C: PlayerPointFingerAtRelay {plr}");
            plr.PointFingerAt(pos, t);
        }
    }

    [ClientReceiver((byte)NetmsgId.CurrentWorldStateSync)]
    static void ClientReceiver__CurrentWorldStateSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out SerializableRandomState randomstate);
        reader.Get(out bool worldgenerated);

        ServerIsGeneratingWorld = !worldgenerated;

        UnityEngine.Random.state = randomstate.State;
#if FULLGAME
        FullgameWorldCompat.ReadWorldExtras(reader);
#endif
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Announce_LayerModifier)]
    static void ClientReceiver__Announce_LayerModifier(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort modifierindex);
#if FULLGAME
        float gravityY = Physics2D.gravity.y;
        if (!reader.EndOfData) {
            reader.Get(out gravityY);
        }
#endif

        var lm = LayerModifier.availableModifiers[modifierindex];

        Plugin.log.LogInfo($"CLIENT: Received layer modifier: {nameof(lm)}  index: {lm.modifierIndex}");

        Util.CallLambdaWhenWorldGenerates(() => {
            lm.Initialize(WorldGeneration.world);
            lm.active = true;
#if FULLGAME
            // turbulent rolled gravity on the host - stamp the shared value after Initialize
            FullgameWorldCompat.ApplyModifierGravity(gravityY);
#endif

            if (log.verbose) log.l($"Applied layer modifier {lm.GetType()}");

            var pf = Locale.GetOther("layermodifier" + lm.modifierIndex);
            var dsc = Locale.GetOther("layermodifier" + lm.modifierIndex + "dsc");

            WorldGeneration.world.layerPrefix = (pf);
            WorldGeneration.world.layerDescription = (dsc);

            Do_The_Modifier_Announcement(pf, dsc);
        });
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Announce_RegenerateWorld)]
    static void ClientReceiver__Announce_RegenerateWorld(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort doPod);

        var wginst = WorldGeneration.world;
        if ((Net.IsClient) && wginst) {
            if (wginst.generatingWorld) {
                wginst.StopCoroutine("GenerateWorld");
                wginst.generatingWorld = false;
                Multiplayer.DoMultiplayerStatusMessageLog("Received RegenerateWorld announcement. (CANCELLING GenerateWorld !!!)");
            } else {
                Multiplayer.DoMultiplayerStatusMessageLog("Received RegenerateWorld announcement.");
            }

            PlayerCamera.main.body.forceWalk = false;
            wginst.savePanel.SetActive(false);

            bool dopod = (doPod == 1);

            wginst.ResetLayerModifiers();

            wginst.doPod = dopod;
            wginst.StartCoroutine("RegenerateWorld", dopod);

            if (dopod) {
                Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CreateInstance, false)]
    static void ClientReceiver__CreateInstance(uint _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out Vector2 where);
        reader.Get(out float rot);
        reader.Get(out string soundname, true);

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(where, $"C: CreateInstance {soundname}");

        Utils.Create(soundname, where, rot);
    }

    [ClientReceiver((byte)NetmsgId.PlayThisSoundHere, false)]
    static void ClientReceiver__PlayThisSoundHere(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out Vector2 where);
        reader.Get(out string soundname, true);

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(where, $"C: PlayThisSoundHere {soundname}");

        Sound.Play(soundname, where);
    }

    [ClientReceiver((byte)NetmsgId.ServerInfoUpdate, false)]
    static void ClientReceiver__ServerInfoUpdate(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        Net.CurrentServerInfo.Deserialize(reader);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBarkRelay)]
    static void ClientReceiver__PlayerBarkRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid who);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(who, out var plr, out var body) && !plr.IsLocal) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"C: Bark");
            body.eatTime = 0;
            body.GetComponent<PantSound>().Bark();
            if (body.eatTime == 0.5f) {// to account for jitter
                body.eatTime = 0.45f;
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BodyTeleportPacket)]
    static void ClientReceiver__SingleCharacterPositionsSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid plrId);
        reader.Get(out knetid body_netId);
        reader.Get(out BodyTeleportPacket pack);

        if (NetBody.TryGetNetBodyFromId(body_netId, out var nb)) {
            if ((nb.IsPlayer && nb.IsLocal)) {
                if (Util.IsGeneratingWorld() || !Util.IsInWorld()) {
                    _last_reminderpack_while_generating_received = true;
                }
                _last_reminderpack_while_generating = pack;
                if (nb != null) {
                    nb.OnReceiveSyncPacket(pack, true);
                }
            } else {
                nb.OnReceiveSyncPacket(pack);
            }
        } else if (ScavPlayer.LocalPlayer != null && plrId == ScavPlayer.LocalPlayer.playerId && (Util.IsGeneratingWorld() || !Util.IsInWorld())) {
            // the spawn packet beat the body. keep it so the wait can finish, apply once the body exists.
            _last_reminderpack_while_generating = pack;
            _last_reminderpack_while_generating_received = true;
            log.l("CLIENT: spawn location arrived before the body. holding it.");
        }
    }

#if RELEASE
    internal static bool _last_reminderpack_while_generating_received = false;
#else
    internal static bool _last_reminderpack_while_generating_received__ = false;
    internal static bool _last_reminderpack_while_generating_received {
        get {
            return _last_reminderpack_while_generating_received__;
        }
        set {
            if (value) {
                log.l($"last_reminderpack_while_generating_received set to true from: {new StackTrace()}");
            }
            _last_reminderpack_while_generating_received__ = value;
        }
    }
#endif
    internal static BodyTeleportPacket _last_reminderpack_while_generating;

    [ClientReceiver((byte)NetmsgId.CLIENT_ragdollpack)]
    static void ClientReceiver__ragdollpack(knetid senderId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid netbodyid);
        if (NetBody.TryGetNetBodyFromId(netbodyid, out var _)) {
            _ = reader.DecompressReader();
        }
    }

    [ClientReceiver((byte)NetmsgId.GenerateWorld_SeedAnnounce)]
    static void ClientReceiver__GenerateWorld_SeedAnnounce(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out LastBeforeGenerationState rand);

        Multiplayer.DoMultiplayerStatusMessageLog("GenerateWorld_SeedAnnounce, received worldgen seed.");

        WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = false;
        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams = rand;
    }

    [ClientReceiver((byte)NetmsgId.StartGameAnnouncement)]
    static void ClientReceiver_StartGameAnnouncement(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ServerMain.StartGameAnnouncementPacket sgap);
        reader.Get(out string runsettings, true);
#if FULLGAME
        AchievementBreakSync.ReadAnnouncementFlag(reader);
#endif

        Multiplayer.rules = sgap.rules;
        Multiplayer.ApplyGameRules();

        WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams.randomstate = sgap.randomstate;
        WorldGeneration_GenerateWorld_MultiplayerPatch.client_firstworldgenparams_are_used = true;

        UnityEngine.Random.state = sgap.randomstate;

        try {
            WorldMP.ApplyRunSettings(runsettings);
            sgap.prefs.ApplyPrefs();
        } catch (Exception e) {
            log.error(e);
        }

        SaveSystem.loadedRun = false;
        if (PreRunScript.instance != null) {
#if FULLGAME
            // mid-run pick still open - don't WaitLoad until they Join
            if (CharacterSelectWait.ShouldBlockStartGameAnnouncement()) {
                log.warn("StartGameAnnouncement while late-join char select open; ignoring");
                return;
            }
            CharacterSelectWait.Reset();
#endif
            Multiplayer.DoMultiplayerStatusMessageLog($"### STARTING GAME (RECEIVED ANNOUNCEMENT FROM SERVER) ###");

            ScavGameStartPatch.starting_game = true;
            PreRunScript.instance.StartCoroutine(PreRunScript.instance.WaitLoad());
            Multiplayer.showMultiplayerMenu = false;
        } else {
            if (Util.IsInWorld()) {
                log.warn("Received StartGameAnnouncement but im in the world already... bruh");
            } else {
                log.warn("Received StartGameAnnouncement but theres no PreRunScript, what???");
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_RunSettings)]
    static void ClientReceiver__RunSettingsSync(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader = reader.DecompressReader();

        reader.Get(out string runsettings, true);
        reader.Get(out MultiplayerGameRules new_rules);

        if (runsettings != WorldMP.CompileRunSettings()) {
            if (log.verbose)
                Multiplayer.DoMultiplayerStatusMessageLog("Run settings changed.");

            WorldMP.ApplyRunSettings(runsettings);
            if (PreRunScript.instance != null) {
                PreRunScript.instance.UpdateAllSettingDisplays();
            }
        }

        {
            var count = 0;
            var msg = "";
            try {
                foreach (var fld in typeof(MultiplayerGameRules).GetFields(BindingFlags.Public | BindingFlags.Instance)) {
                    if (fld.GetValue(Multiplayer.rules).ToString() != fld.GetValue(new_rules).ToString()) {
                        msg += $"\n{fld.Name} = {fld.GetValue(new_rules)}";
                        count += 1;
                    }
                }
                msg = $"Server changed {count} game rules:{msg}";
            } catch (Exception e) {
                log.error(e);
            }

            if (count > 0) {
                Multiplayer.rules = new_rules;
                Multiplayer.ApplyGameRules();

                if (log.verbose && !String.IsNullOrWhiteSpace(msg) && count > 0)
                    Multiplayer.DoMultiplayerStatusMessageLog(msg);
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_SetTimeScaleRelay)]
    static void ClientReceiver__SetTimeScaleRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort sc);

        if ((PlayerCamera.SpeedType)sc == PlayerCamera.SpeedType.Paused)
            return;

        if (PlayerCamera.main != null && !Multiplayer.rules.DisableTimeManipulation) {
            if (log.verbose) log.l($"SetTimeScale: Received SetTimeScale from server: {(PlayerCamera.SpeedType)sc}");

            PlayerCamera_SetTimeScale_MultiplayerPatch.force = true;
            PlayerCamera.main.SetTimeScale((PlayerCamera.SpeedType)sc, true);

            ClientMain.ServerWorldState.Deserialize();
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CustomPrompt, false)]
    static void ClientReceiver__CustomPrompt(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint key);
        reader.Get(out string promptid);
        reader.Get(out string title);
        reader.Get(out string text);

        UIChoicePrompt.ClientDoNetPrompt(key, promptid, title, text);
    }
}
