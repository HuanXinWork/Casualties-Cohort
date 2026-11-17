using CasualtiesTogetherUtils;
using LiteNetLib;
using Newtonsoft.Json;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

public partial class ServerMain {

    double tpscalc_lasttime = 0;
    int tpscalc_framecounter = 0;

    internal static void _UpdateSpecialPlayerLists() {
        _real_AllClientIdsExceptHost.Clear();
        _real_AllPlayersExceptHost.Clear();
        _real_AllClientIds.Clear();
        if (!Net.IsRunning) return;

        foreach (var plr in ScavPlayer.AllPlayers) {
            _real_AllClientIds.Add(plr.playerId);
            if (plr != null && !plr.IsHost) {
                _real_AllClientIdsExceptHost.Add(plr.playerId);
                _real_AllPlayersExceptHost.Add(plr);
            }
        }
    }

    private void FixedUpdate() {
        {
            tpscalc_framecounter += 1;

            var time = Time.realtimeSinceStartupAsDouble;
            if (time - tpscalc_lasttime >= 1) {
                tpscalc_lasttime = time;
                CURRENT_TPS = tpscalc_framecounter;
                tpscalc_framecounter = 0;
                if (Net.IsServer) {
                    ClientMain.SERVER_TPS = CURRENT_TPS;
                    if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.ServerPlayerState != null) {
                        ScavPlayer.LocalPlayer.ServerPlayerState.tps = CURRENT_TPS;
                    }
                }
            }
        }

        _UpdateSpecialPlayerLists();
        if (!Net.IsRunning) return;

        // for server, move the local body somewhere far offscreen so that it doesnt cause anything bad
        if (Net.IsDedicatedServer && Util.TryGetLocalBody(out var locbody)) {
            (locbody).ResetHealth();
            Util.Body_DropAllItems(locbody);

            locbody.ForceStand();
            if (Util.IsWorldGenerated()) {
                locbody.transform.position = new Vector2(999999, 999999);
            }
            locbody.rb.gravityScale = 0;
            locbody.rb.velocity = Vector2.zero;
            locbody.rb.angularVelocity = 0;
        }

        if (!Util.IsInWorld() && Multiplayer.rules.AutoContinue) { // is in main menu and has the autocontinue rule
            if (Net.IsServer) {
                if (ScavPlayer.ClientIdToPlayerDict.Count >= (Multiplayer.rules.AutoMinPlrsToStart)) {
                    ServerMain._ded_server_switch_counter += Time.deltaTime;
                    if (ServerMain._ded_server_switch_counter > 20f) { // open ready gate - do NOT dump WaitLoad as default Exp
                        ServerMain._ded_server_switch_counter = -100;

                        var prs = PreRunScript.instance;
                        if (prs) {
#if FULLGAME
                            // autostart only proceeds once everyone is Ready (CharacterSelectWait).
                            if (!CharacterSelectWait.Waiting) {
                                Multiplayer.DoMultiplayerStatusMessageLog(Lang.GetEN("autostarting_run"));
                                Chat.Server_ChatAnnouncement("<key:autostarting_run>");
                                Chat.Server_ChatAnnouncement("Ready up to start...");
                                CharacterSelectWait.HostBeginNewRun(false);
                            }
#else
                            var msg = "autostarting_run";
                            Multiplayer.DoMultiplayerStatusMessageLog(Lang.GetEN(msg));
                            Chat.Server_ChatAnnouncement("<key:autostarting_run>");
                            prs.StartCoroutine(prs.WaitLoad());
#endif
                        } else {
                            Multiplayer.DoMultiplayerStatusMessageError($"Failed to autostart the run. Theres no PreRunScript???? what???");
                            ServerMain._ded_server_switch_counter = 10;
                        }
                    }
                } else {
                    ServerMain._ded_server_switch_counter = 0;
                }
            }
        }
    }

    double fpscalc_lasttime = 0;
    int fpscalc_framecounter = 0;

    public const float SyncFrequency = 0.09f;
    public const float SyncFrequencyRare = 5.0f;
    public const float SyncFrequencySecond = 1.0f;

    internal static float timer_UpdateSyncClientsToClients = 0;
    internal static float timer_RareUpdateSyncClients = 0;
    internal static float timer_SecondUpdateSyncClients = 1;

    void Update() {
        {
            fpscalc_framecounter += 1;

            var time = Time.realtimeSinceStartupAsDouble;
            if (time - fpscalc_lasttime >= 1) {
                fpscalc_lasttime = time;
                CURRENT_FPS = fpscalc_framecounter;
                fpscalc_framecounter = 0;
                if (Net.IsServer) {
                    ClientMain.SERVER_FPS = CURRENT_FPS;
                    if (ScavPlayer.LocalPlayer != null && ScavPlayer.LocalPlayer.ServerPlayerState != null) {
                        ScavPlayer.LocalPlayer.ServerPlayerState.fps = CURRENT_FPS;
                    }
                }
            }
        }

        if (!Multiplayer.IsNetworkActiveAndIsServer()) return;

        timer_UpdateSyncClientsToClients += Time.unscaledDeltaTime;
        timer_RareUpdateSyncClients += Time.unscaledDeltaTime;
        timer_SecondUpdateSyncClients += Time.unscaledDeltaTime;

        if (timer_RareUpdateSyncClients > SyncFrequencyRare) {
            timer_RareUpdateSyncClients = 0;
            RareUpdateSyncClients();
        } else if (timer_SecondUpdateSyncClients > SyncFrequencySecond) {
            timer_SecondUpdateSyncClients = 0;
            SecondUpdateSyncClients();
        } else if (timer_UpdateSyncClientsToClients > SyncFrequency) {
            timer_UpdateSyncClientsToClients = 0;
            UpdateSyncClientsToClients();
        }
    }

    void LateUpdate() {
        if (!Net.IsRunning) return;

        HandleSleepUpdate();
        HandleDedicatedServerUpdate();
    }

    public static float _ded_server_switch_counter = 0;
    static void HandleDedicatedServerUpdate() {
        if (!Util.IsGeneratingWorld()) {
            ServerMain._ded_server_switch_counter += Time.unscaledDeltaTime;
        }

        if (Util.IsWorldGenerated()) {
            if (Net.IsDedicatedServer) {
                if (_ded_server_switch_counter > 10f) {
                    if (Multiplayer.rules.AutoContinue) {
                        if (Multiplayer.rules.AutoExitWhenAllLeft && ScavPlayer.ClientIdToPlayerDict.Count == 0) {
                            _ded_server_switch_counter = -30;

                            var msg = $"Auto-exiting to main menu because everyone left the server.";
                            Multiplayer.DoMultiplayerStatusMessageLog(msg);
                            Chat.Server_ChatAnnouncement(msg);

                            PlayerCamera.main.ToMainMenu();
                            return;
                        } else if (Multiplayer.rules.AutoExitWhenAllDied && ScavPlayer.AllLivingPlayers.Count == 0 && !ModHooks.QueryBlockAutoExitOnAllDead()) {
                            _ded_server_switch_counter = -5;

                            var msg = $"Auto-exiting to main menu because everyone died.";
                            Multiplayer.DoMultiplayerStatusMessageLog(msg);
                            Chat.Server_ChatAnnouncement(msg);

                            PlayerCamera.main.ToMainMenu();
                            return;
                        }
                    }
                }
            }
        }
    }

    public const float SoloSleepBonusRampSeconds = 10f;

    static bool _togetherSleepTimeScale;
    static int _energyMedianFrame = -1;
    static float _energyMedian;
    static bool _energyMedianValid;

    // living players' energy median for this frame (host and clients, from local body views)
    public static bool TryGetLivingEnergyMedian(out float median) {
        if (_energyMedianFrame == Time.frameCount) {
            median = _energyMedian;
            return _energyMedianValid;
        }
        _energyMedianFrame = Time.frameCount;
        _energyMedianValid = false;
        median = 0f;

        List<float> energies = null;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null) {
                continue;
            }
            if (energies == null) {
                energies = new List<float>(ScavPlayer.AllLivingPlayers.Count);
            }
            energies.Add(plr.body.energy);
        }
        if (energies == null || energies.Count == 0) {
            return false;
        }
        energies.Sort();
        int mid = energies.Count / 2;
        if ((energies.Count & 1) == 1) {
            _energyMedian = energies[mid];
        } else {
            _energyMedian = (energies[mid - 1] + energies[mid]) * 0.5f;
        }
        _energyMedianValid = true;
        median = _energyMedian;
        return true;
    }

    static void HandleSleepUpdate() {
        if (Multiplayer.rules.DisableSleep) {
            if (Util.IsInWoundView()) {
                WoundView.view.sleepTip.tipName = Lang.Get("sleep_is_disabled");
                WoundView.view.sleepTip.tipDesc = Lang.Get("sleep_disabled_desc");
            }
            ClearTogetherSleepTimeScale();
            return;
        }

        // everyone asleep: vanilla together-sleep timescale (ignores DisableTimeManipulation)
        if (CheckIfEveryoneIsSleeping()) {
            _togetherSleepTimeScale = true;
            Time.timeScale = 25f;
            if (PlayerCamera.main != null) {
                PlayerCamera.main.curTimeScale = PlayerCamera.SpeedType.UnconsciousFast;
            }
            return;
        }

        ClearTogetherSleepTimeScale();
    }

    static void ClearTogetherSleepTimeScale() {
        if (!_togetherSleepTimeScale) {
            return;
        }
        _togetherSleepTimeScale = false;
        Time.timeScale = 1f;
        if (PlayerCamera.main != null && PlayerCamera.main.curTimeScale == PlayerCamera.SpeedType.UnconsciousFast) {
            PlayerCamera.main.curTimeScale = PlayerCamera.SpeedType.Normal;
        }
    }

    private void Start() {
        {
            LastStartGameAnnouncementPacket = new StartGameAnnouncementPacket {
                prefs = (new WorldMP.RunPrefs()).ReadPrefs(),
                randomstate = UnityEngine.Random.state,
                rules = Multiplayer.rules,
            };
        }

        InvokeRepeating(nameof(update10s), 10, 10);

        ScavPlayer.OnPlayerJoined += (ScavPlayer plr) => {
            _lastSentRunSettings = "";
        };

        ScavPlayer.OnPlayerLeft += (ScavPlayer plr) => {
            _real_AllClientIds.Remove(plr.playerId);
            _real_AllClientIdsExceptHost.Remove(plr.playerId);
            _real_AllPlayersExceptHost.Remove(plr);
        };

        Multiplayer.RegisterCustomServerReceiver(CustomWireCommands.ChannelCustomCommand, RunClientCustomCommand);

        {
            var t = "plrdeath";
            AllDeathMessageKeys.AddRange(Lang.EN.Keys.Where(x => x == t || (x.TryTrimStart(t, out var _))));
        }
    }
    static readonly List<string> AllDeathMessageKeys = new List<string>();

    void update10s() {
        if (Net.IsServer && Net.IsRunning) {
            _Server_OnServerNameChange();
            Net.MyServerInfo.SetValues();
            Net.CurrentServerInfo.SetValues();
        }
    }

    public static void OnPlayerSleep(ScavPlayer plr) {
        PlayerCamera.main.DoAlert(plr.playerName + Lang.Get("on_plr_sleep"));
    }

    public static string PickRandomDeathMessageLangKey(string t) {
        return AllDeathMessageKeys.Where(x => x == t || (x.TryTrimStart(t, out var trimmed) && byte.TryParse(trimmed, out var _))).PickRandom();
    }

    public static string PickRandomDeathMessageTag(string t) {
        return $"<key:{PickRandomDeathMessageLangKey(t)}>";
    }

    static string GenerateDeathMessage(ScavPlayer plr) {
        var nb = plr.netBody;
        var body = nb.body;

        StringBuilder deathMessage = new StringBuilder($"<plrc:{plr.playerId}> <color={Chat.DefaultChatColors.Kill.ToHex()}>");

        int brokenBoneCount = 0;
        int dislocationCount = 0;
        foreach (var l in body.limbs) {
            if (l.broken) {
                brokenBoneCount++;
            }
            if (l.dislocated) {
                dislocationCount++;
            }
        }

        var lastAttack = plr.netBody.GetLastDamageEvent();

#if !RELEASE
        log.l($"Player died. Last damage event: {lastAttack}");
#endif

        if (lastAttack != null) {
            if (!String.IsNullOrEmpty(lastAttack.deathMessage)) {
                deathMessage.Append(lastAttack.deathMessage);
            } else if (lastAttack.IsPlayerAttack()) {
                if (lastAttack.victim == lastAttack.attackerBody) {
                    if (lastAttack.damage == PlayerCamera_StartSelfDestruct_MultiplayerPatch.SelfDestructIdentifier) {
                        deathMessage.Append($"<key:plrdeath_suicide0>");
                    } else {
                        deathMessage.Append($"{PickRandomDeathMessageTag("plrdeath_suicide")}");
                    }
                } else {
                    deathMessage.Append($"<key:plrdeath_plrkill> <bodyc:{lastAttack.attackerBody.netId}>");
                    if (lastAttack.weapon != null && lastAttack.weapon.TryGetTracker(out var tracker)) {
                        deathMessage.Append($" <key:plrdeath_plrkill_using> <obj:{tracker.syncId}>");
                    }
                }

                deathMessage.Append(".");
            } else if (lastAttack.IsBuildingAttack(out var tracker)) {
                if (lastAttack.attackerBuilding.TryGetComponent(out SpiderHandler _)) {
                    deathMessage.Append($"<key:plrdeath_creaturekill>");
                } else {
                    deathMessage.Append($"<key:plrdeath_objkill>");
                }
                deathMessage.Append($" <obj:{tracker.syncId}>");
            } else {
                lastAttack = null;
            }
        }

        if (lastAttack == null) {
            if (!body.IsAirBreathable() && body.bloodOxygen <= 1 && body.inWater) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_drown"));
            } else if (body.bloodVolume <= 1 && body.totalBleedSpeed > 0.02f) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_bleed"));
            } else if (body.TryGetComponent(out Painkillers pain) && pain.opiateReception > 90) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_overdose"));
            } else if (brokenBoneCount > 13 || dislocationCount > 13) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_brokenbones"));
            } else if (body.hunger <= 1) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_starve"));
            } else if (body.temperature < 27) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_freeze"));
            } else if (body.transform.position.y > RadiationLine.line.transform.position.y) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_radline"));
            } else if (body.radiationSickness > 95) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_radiation"));
            } else if (body.septicShock > 81) {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath_sepsis"));
            } else {
                deathMessage.Append(PickRandomDeathMessageTag("plrdeath"));
            }
        }

        deathMessage.Append(".</color>");

        return deathMessage.ToString();
    }

    public static void OnPlayerDeath(ScavPlayer plr) {
        if (Net.IsServer) {
            if (Multiplayer.rules.TrackPlayerDeaths) {
                var deathMessage = GenerateDeathMessage(plr);
                Chat.Server_ChatAnnouncement(deathMessage);
            }

            if (Util.IsTutorialWorld()) {
                plr.OnFinishTutorial(false); // finished cuz died and cant move
            }

            if (ScavPlayer.AllLivingPlayers.Count == 0) {
                var fgdg = ("<key:everyone_is_dead>");
                Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, fgdg));
                Server_AnnounceAlert(fgdg, true, true, null);

                PlayerCamera.main.DoAlert(Lang.Get("everyone_is_dead_host"));
            }
        }

        // this will make witnesses stare at the dead body
        var corpse = plr.GetOrAddComponent<CorpseScriptMultiplayerRewrite>();
        corpse.animalCorpse = false;
    }

    private void SecondUpdateSyncClients() {
        {
            {
                if (ScavPlayer.LocalPlayer != null) {
                    ScavPlayer.LocalPlayer.ping = 0;
                }
                ClientMain.LOCAL_PING = 0;
            }

            var writer = Net.CreateWriter(NetmsgId.CurrentPingOfAllPlayers);

            foreach (var w in ScavPlayer.AllPlayers) {
                writer.Put(true);

                writer.Put((knetid)w.playerId);
                writer.Put((float)w.ping);

                if (writer.Length > NetTimingConstants.UnreliableHealthBatchMaxBytes) {
                    break;
                }
            }
            writer.Put(false);

            if (Net.TryGetSteamTransport(out var tsteam)) {
                tsteam.Server_SendToClients(Steamworks.Constants.k_nSteamNetworkingSend_Unreliable, writer, ServerMain.AllClientIdsExceptHost);
            } else {
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
            }
        }

        {
            var curRunSettings = WorldMP.CompileRunSettings();
            var curRules = JsonConvert.SerializeObject(Multiplayer.rules, Formatting.None);

            if (_lastSentRunSettings != curRunSettings || _lastSentRules != curRules) {
                _lastSentRunSettings = curRunSettings;
                _lastSentRules = curRules;

                var writer = Net.CreateWriter(NetmsgId.CLIENT_RunSettings);
                writer.Put(curRunSettings, true);
                writer.Put(Multiplayer.rules);
                writer.CompressWriter();
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIdsExceptHost);
            }
        }
    }
    internal static string _lastSentRunSettings = "";
    internal static string _lastSentRules = "";

    private void RareUpdateSyncClients() {
        if (!Util.IsWorldGenerated()) return;
        Server_SendWorldState();
    }

    private void UpdateSyncClientsToClients() {
        if (!Util.IsInWorld()) return;

        { // allow skipping layer, when whoever first reaches the end
            var world = WorldGeneration.world;
#if FULLGAME
            // one player at the top starts the scroller. autocontinue must not skip it.
            if (FullgameWorldCompat.IsAscentLayer()) {
                ConcurrentEndings.Server_TryStartAscentCutscene();
                return;
            }
#endif
            if (!WorldGeneration.world.doingRegen && !WorldGeneration.world.generatingWorld && WorldGeneration.world.worldExists) {
                ScavPlayer new_finisher = null;

                foreach (var item in ScavPlayer.BodyToPlayerDict) {
                    if (item.Key.alive && item.Key.conscious) {
                        if ((item.Value.IsAtTheEndOfLayer())) {
                            if (!item.Value.finishedLayer) {
                                new_finisher = item.Value;
                                item.Value.finishedLayer = true;
                                break;
                            }
                        }
                    }
                }

                bool can_autocontinue = false;

                if (new_finisher != null) {
                    var msg = String.Format(Lang.Get("plr_finished_layer"), new_finisher.playerName);
                    if (!new_finisher.IsLocal) {
                        PlayerCamera.main.DoAlert(msg);
                    }
                    Chat.Server_ChatAnnouncement(msg);
                }

                if (ServerMain.CheckIfEnoughPeopleAreAtLayerFinish(out int _, out int _)) {
                    world.savePanel.SetActive(value: true);
                    can_autocontinue = true;
                } else if (CheckIfShouldStartRadlineForStragglers()) {
                    if (Util.IsWorldGenerated() && !RadiationLine.line.active) {
                        RadiationLine.line.Activate();

                        ScavPlayer highest_plr = ScavPlayer.AllLivingPlayers.First();
                        foreach (var item in ScavPlayer.AllLivingPlayers) {
                            if (item.netBody.GetHeadPos().y > highest_plr.netBody.GetHeadPos().y) {
                                highest_plr = item;
                            }
                            if (!item.finishedLayer) {
                                item.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("straggler_radline"));
                            }
                        }

                        RadiationLine_Update_MultiplayerPatch.timeGone = world.halfHeight - highest_plr.netBody.GetHeadPos().y - 64;
                    }
                }

                if (can_autocontinue && Multiplayer.rules.AutoContinue) {
                    if (!WorldGeneration.world.doingRegen && !world.generatingWorld && world.worldExists) {
                        world.generatingWorld = true;

                        Plugin.log.LogInfo($"SERVER: Autocontinuing the run! ");
                        Chat.Server_ChatAnnouncement($"<key:autocontinue_run>");

                        world.savePanel.SetActive(value: false);
                        world.StartCoroutine(world.RegenerateWorld());
                    }
                }
            }
        }
    }
}
