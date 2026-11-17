using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

public partial class Multiplayer {

    public static bool DEV_VERBOSE_SHOW_CURSOR_INFO = true;

    static void _GUI_RenderMainGUI() {
#if !RELEASE
        {
            var t = "";
            if (Net.ClientEntityManager != null) {
                var _entityManager = Net.ClientEntityManager;
                t = $@"
C_ServerTick: {_entityManager.ServerTick}
C_Tick: {_entityManager.Tick}
C_LPRCS: {_entityManager.LastProcessedTick}
C_StoredCommands: {_entityManager.StoredCommands}
C_Entities: {_entityManager.EntitiesCount}
C_ServerInputBuffer: {_entityManager.ServerInputBuffer}
C_LerpBufferCount: {_entityManager.LerpBufferCount}
C_LerpBufferTime: {_entityManager.LerpBufferTimeLength}
Jitter: {_entityManager.NetworkJitter}
PendingRemove: {_entityManager.PendingToRemoveEntites}";
            } else if (Net.ServerEntityManager != null) {
                var _entityManager = Net.ServerEntityManager;
                t = $@"
S_Tick: {_entityManager.Tick}
S_Entities: {_entityManager.EntitiesCount}
S_LerpFactor: {_entityManager.LerpFactor}";
            }

            var content = KrokV5Optimization.FixGuiContent.Content(t, 35);
            var size = GUI.skin.label.CalcSize(content);

            GUI.Label(new Rect(Screen.width - 400, 20, size.x, size.y), t);
        }
#endif

        if (Con._DEV_HIDEHUD) {
            return;
        }

        float y = 20;
        const float elsize = 20f;

#if TESTING
        GUI.Label(new Rect(20, y, 1100, elsize), $"MULTIPLAYER CO-OP MOD v{Plugin.FULL_MOD_VERSION} FOR  CASUALTIES: UNKNOWN {Application.version} APPID: {KSteam.STEAM_APPID} \"TESTING BUILD\" \"TESTING BUILD\" 2");
        y += elsize;
#elif !RELEASE
        GUI.Label(new Rect(20, y, 1100, elsize), $"MULTIPLAYER CO-OP MOD v{Plugin.FULL_MOD_VERSION} FOR  CASUALTIES: UNKNOWN {Application.version} APPID: {KSteam.STEAM_APPID} \"TESTING BUILD\" \"TESTING BUILD\" \"TESTING BUILD\" v");
        y += elsize;
#endif

        if (Net.IsDedicatedServer) {
            GUI.Label(new Rect(20, y, 500, elsize), "DEDICATED SERVER: SWITCH COUNTER: " + Mathf.Round(ServerMain._ded_server_switch_counter).ToString());
            y += elsize;
        }

        if (verbose) {
            GUI.Label(new Rect(20, y, 500, elsize), "FPS: " + (ServerMain.CURRENT_FPS).ToString() + "   TPS: " + (ServerMain.CURRENT_TPS).ToString());
            y += elsize;

            GUI.Label(new Rect(20, y, 500, elsize), "UTC: " + DateTime.UtcNow.ToString("HH:mm:ss.fff"));
            y += elsize;
        }

        if (verbose && DEV_VERBOSE_SHOW_CURSOR_INFO) {
            var yy = Screen.height - UnityEngine.Input.mousePosition.y + elsize;
            if (Util.TryGetLocalBody(out var _)) {
                if (MinigameBase.main?.currentMinigame) {
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg handpos: " + MinigameBase.main.handPos.ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg sprite : " + MinigameMPManager.GetLocalHandSpriteIndex().ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "mg lmb : " + MinigameBase.main.handClicking.ToString());
                    yy += elsize;
                } else {
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "dist: " + Vector2.Distance(PlayerCamera.main.body.transform.position, (Vector2)Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition)));
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "pos: " + ((Vector2)Camera.main.ScreenToWorldPoint(UnityEngine.Input.mousePosition)).ToString());
                    yy += elsize;
                    GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "vel: " + (PlayerCamera.main.body.rb.velocity).ToString());
                    yy += elsize;
                }
            }
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "time: " + Math.Round(Time.realtimeSinceStartupAsDouble));
            yy += elsize;

#if !RELEASE
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI scaled: " + (Vector2Int16)(UIBullshit.GetCursorPosInGUI()));
            yy += elsize;
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI unscaled: " + (Vector2Int16)(UIBullshit.GetCursorPosInGUI() / UIBullshit.uiScale));
            yy += elsize;
            GUI.Label(new Rect(UnityEngine.Input.mousePosition.x, yy, 400, elsize), "UI scalar: " + (Util.GetCursorPos() / Util.GetResolution()));
            yy += elsize;
#endif
        }

        if (Util.TryGetLocalBody(out var _)) {
            if (verbose) {
                var pos = Util.GetLocalBodyPosition();
                if (UIInGame.SpectatorModeActive) {
                    pos = PlayerCamera.main.transform.position;
                }

                GUI.Label(new Rect(20, y, 500, elsize), $"Position: x:{(int)pos.x} y:{(int)pos.y}");
                y += elsize;

                GUI.Label(new Rect(20, y, 500, elsize), "REGISTERED OBJECTS COUNT: " + NetObjectRegistry.SyncRegistry.Count.ToString());
                y += elsize;
            }

#if !RELEASE
            if (UnityEngine.Input.GetKey(KeyCode.PageUp) || UnityEngine.Input.GetKey(KeyCode.PageDown)) {
                GUI.Label(new Rect(20, y, 500, elsize), "ORTHO SIZE: " + Camera.main.orthographicSize.ToString());
                y += elsize;
            }
#endif
        }

        if (ConsoleScript.instance.noClip) {
            GUI.Label(new Rect(20, y, 200, elsize), "NOCLIP ACTIVE");
            y += elsize;
        }

        if (Con._DEV_GODMODE) {
            GUI.Label(new Rect(20, y, 200, elsize), "GODMODE ACTIVE");
            y += elsize;
        }

        {
            if (UIInGame.SpectatorModeActive) {
                GUI.Label(new Rect(20, y, 400, elsize), "Spectator mode active ");
                y += elsize;
            }
            if (UIInGame.interaction_menu_target_body != null) {
                GUI.Label(new Rect(20, y, 400, elsize), "Player interaction menu active " + UIInGame.interaction_menu_target_body.ToString());
                y += elsize;
            }

#if !RELEASE
            if (WorldGeneration.world) {
                var locplrpos = (PlayerCamera.main.body.alive ? PlayerCamera.main.body.transform.position : Camera.main.transform.position);
                var locplr_syncchunkpos = WorldChunkSync.WorldPosToSyncchunkCoordinateSafe(locplrpos);
                GUI.Label(new Rect(20, y, 700, elsize), $"SYNCCHUNK: {locplr_syncchunkpos.x} {locplr_syncchunkpos.y}");
                y += elsize;
            }
            if (Util.IsInWoundView()) {
                if (Time.realtimeSinceStartup % 1f > 0.6f) {
                    if (Net.IsClient && Net.IsRunning) {
                        GUI.Label(new Rect(20, y, 400, elsize), "CLIENT CANT DO DEV LIMB HEALTH MANIPULATION");
                    } else {
                        GUI.Label(new Rect(20, y, 900, elsize), "(dev limb health binds:   Keypad0 bleeding,  Keypad1 shrapnel,  Keypad2 break bone,  Keypad3 dislocate,  Keypad4 amputate,  Keypad5 infect): ");
                    }
                }
                y += elsize;
            }
#endif

            if (verbose) {
                if (Util.IsInWoundView() && !Util.IsBodyLocal(WoundView.view.body)) {
                    GUI.Label(new Rect(20, y, 400, elsize), "Viewing health panel of player: " + WoundView.view.body.GetComponent<NetBody>().bodyName);
                    y += elsize;
                }

                if (MinigameMPManager.active_sessions.Count > 0) {
                    GUI.Label(new Rect(20, y, 400, elsize), "MINIGAME SESSIONS: ");
                    y += elsize;
                    foreach (var item in MinigameMPManager.active_sessions) {
                        var str = "";
                        foreach (var plr in item.involved_players) {
                            str += plr.playerName + " " + plr.IsInMinigame.ToString() + " " + item.involved_players.Contains(plr).ToString() + " " + plr.minigame_session + " " + plr.minigame_current_type + " | ";
                        }

#if !RELEASE
                        GUI.Label(new Rect(20, y, 1000, elsize), $"type:{item.minigame_type} id:{item.session_id} -- {str}");
                        y += elsize;
                        GUI.Label(new Rect(20, y, 1000, elsize), $"     SCI IDS: {String.Join(" ", item.InvolvedPlayersClientIds())}");
                        y += elsize;
                        if (item is MinigameMPManager.KeypadMinigameSession kpses) {
                            GUI.Label(new Rect(20, y, 1000, elsize), $"     input: {kpses.current_input}");
                            y += elsize;
                        }
#endif
                    }
                }

                if (Minigame.game?.currentMinigame != null) {
                    if (Minigame.game.currentMinigame is DislocationMinigame) {
                        GUI.Label(new Rect(20, y, 1000, elsize), $"DISLOCATION MINIGAME");
                        y += elsize;
                        GUI.Label(new Rect(20, y, 1000, elsize), $"BONE POS: {DislocationMinigame_CheckForHit_MultiplayerPatch.real_bonepos}");
                        y += elsize;
                        GUI.Label(new Rect(20, y, 1000, elsize), $"BONE VEL: {DislocationMinigame_CheckForHit_MultiplayerPatch.boneVelocity}");
                        y += elsize;
                    } else if (Minigame.game.currentMinigame is LockpingMinigame lm) {
                        GUI.Label(new Rect(20, y, 500, elsize), $"LOCKPICKING MINIGAME");
                        y += elsize;
#if !RELEASE
                        GUI.Label(new Rect(20, y, 500, elsize), $"SWEETSPOT: {lm.correctAngle}");
                        y += elsize;
                        GUI.Label(new Rect(20, y, 500, elsize), $"MaxTurnProgress: {lm.MaxTurnProgress()}");
                        y += elsize;
#endif
                        GUI.Label(new Rect(20, y, 500, elsize), $"HAND ANGLE: {lm.HandAngle}");
                        y += elsize;
                    }

                    GUI.Label(new Rect(20, y, 400, elsize), "HAND VEL: " + MinigameBase.main.handVelocity.ToString());
                    y += elsize;
                    GUI.Label(new Rect(20, y, 400, elsize), "HAND VEL MAGN: " + ((int)MinigameBase.main.handVelocity.magnitude).ToString());
                    y += elsize;
                }
            }
        }

        bool did_show_status_message = false;

        if (Util.IsInPauseMenuOrMainMenu() || Net.IsDedicatedServer) {
            y += 10;
        } else {
            if (!Net.IsRunning) {
                GUI.Label(new Rect(20, y, 1280, elsize * 2), Lang.Get("singleplayer_warn"));
                y += elsize * 2;
            } else {
                if (log.verbose) {
                    var mode = Net.IsHost ? "Host" : Net.IsDedicatedServer ? "Server" : "Client";
                    GUI.Label(new Rect(20, y, 200, elsize), "Mode: " + mode);
                    y += elsize;

                    if (!Net.IsConnecting && ScavPlayer.LocalPlayer != null && Net.IsClient) {
                        GUI.Label(new Rect(20, y, 200, elsize), "Ping: " + (ClientMain.LOCAL_PING));
                        y += elsize;
                    }
                }
            }
        }
        if (!did_show_status_message && (Time.realtimeSinceStartupAsDouble - lastMultiplayerStatusMessageChangeTime < 10.0)) {
            var ogwordwrap = GUI.skin.label.wordWrap;
            GUI.skin.label.wordWrap = true;

            var txt = "Last Status Message:\n" + multiplayerStatusMessage;
            var texthiehgt = GUI.skin.label.CalcHeight(KrokV5Optimization.FixGuiContent.Content(txt, 36), 400);

            GUI.Label(new Rect(20, y, 400, texthiehgt), txt);
            y += texthiehgt;

            GUI.skin.label.wordWrap = ogwordwrap;
        }

        if (Voicechat.my_output_volume > 0) {
            if (UIBullshit.IsAnyMenuOpen()) {
                GUI.Label(new Rect(20, y, 300, elsize), "Voicechat current volume level: " + Voicechat.my_output_volume.ToString("F2"));
                y += elsize;
            } else {
                GUI.Label(new Rect(20, y, 200, elsize), "Voicechat active.");
                y += elsize;
            }
        }

#if TESTING
        try {
            if (!UnityEngine.Input.GetKey(KeyCode.LeftShift)) {
                if (KSteam.Loaded) {
                    GUI.Label(new Rect(20, y, 2000, elsize), "Steam loaded.");
                    y += elsize;

                    if (KSteam.IS_IN_LOBBY) {
                        GUI.Label(new Rect(20, y, 2000, elsize), $"In Lobby: " + "SteamID: " + KSteam.CURRENT_LOBBY.lobby_steamID.ToString() + " - Slots: " + KSteam.CURRENT_LOBBY.members.Count + "/" + KSteam.CURRENT_LOBBY.memberlimit);
                        y += elsize;
                    }
                } else {
                    GUI.Label(new Rect(20, y, 2000, elsize), "STEAM DISABLED ");
                    y += elsize;
                }

                if (Net.Transport != null) {
                    if (Net.Transport is TransportLiteNetLib tlnl) {
                        GUI.Label(new Rect(20, y, 2000, elsize), "LNL ");
                        y += elsize;

                        if (tlnl.CLIENT_PEER != null) {
                            GUI.Label(new Rect(20, y, 2000, elsize), $"CLIENT_PEER STATE: {tlnl.CLIENT_PEER.ConnectionState} ");
                            y += elsize;
                        }

                        GUI.Label(new Rect(20, y, 2000, elsize), $"PEERS: {tlnl.NetManager.ConnectedPeersCount} ");
                        y += elsize;
                        var peers = new List<NetPeer>();
                        tlnl.NetManager.GetConnectedPeers(peers);
                        foreach (var item in peers) {
                            if (item.Tag != null && item.Tag is TagPlayerInfo kple) {
                                GUI.Label(new Rect(20, y, 2000, elsize), $"{item.Id}: {kple.intro.userName} - {item}  ping:{item.Ping} ");
                                y += elsize;
                            } else {
                                GUI.Label(new Rect(20, y, 2000, elsize), $"user {item.Id} - {item}  ping:{item.Ping} ");
                                y += elsize;
                            }
                        }
                    } else {
                        GUI.Label(new Rect(20, y, 2000, elsize), "Steam transport running. " + (KSteam.IS_IN_LOBBY ? $"Is in lobby: {KSteam.CURRENT_LOBBY.lobby_steamID}" : ""));
                        y += elsize;

#if !RELEASE
                        if (KSteam.IS_IN_LOBBY) {
                            GUI.Label(new Rect(20, y, 2000, elsize), "MEMBERS: ");
                            y += elsize;
                            foreach (var item in KSteam.CURRENT_LOBBY.members) {
                                GUI.Label(new Rect(20, y, 2000, elsize), item.Key.ToString());
                                y += elsize;
                                foreach (var item2 in item.Value.metadata) {
                                    GUI.Label(new Rect(20, y, 2000, elsize), " - " + item2.ToString());
                                    y += elsize;
                                }
                            }
                        }
#endif
                    }
                }
            }
        } catch (Exception e) {
            log.error("Main GUI Error: " + e.ToString());
        }
#endif
    }

    private void OnGUI() {
        if (Con.IsConsoleOpen()) {
            return;
        }

        try {
            _GUI_RenderMainGUI();
        } catch (Exception e) {
            log.error(e);
        }
    }
}
