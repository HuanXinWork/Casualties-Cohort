using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {
    public partial class ScavPlayer {
        static TMP_FontAsset s_retroGamingFontAsset = null;

        public void ApplyNameAndColor(string name, Color24 color) {
            // 兼容 KSteam.OnLobbyCreated 传入 SteamId.ToString() 的历史调用：本地房主恢复为真实 Unicode 期望昵称，避免房主名退化为 17 位数字 ID
            if (SteamId != 0 && string.Equals(name, SteamId.ToString(), StringComparison.Ordinal)) {
                if (IsLocal && Net.IsServer) {
                    string localDesired = Multiplayer.SanitizeUnicodeTextInput(Multiplayer.PLAYER_USERNAME);
                    if (!string.IsNullOrEmpty(localDesired)) {
                        name = localDesired;
                    }
                } else if (!nameIsCustom && KSteam.Loaded) {
                    string persona = Multiplayer.SanitizeUnicodeTextInput(KSteam.GetSteamUsername(SteamId));
                    if (!string.IsNullOrEmpty(persona)) {
                        name = persona;
                    }
                }
            }

            // 先为 Retro GamingPix 挂载系统动态 Unicode 回退字体表，使后续 ReplaceMissingCharacters(HasCharacter(..., true)) 原生识别中文/俄文/日韩文而不替换为 '?'
            {
                if (s_retroGamingFontAsset == null) {
                    IEnumerable<TMP_FontAsset> ggggfd = GameObject.FindObjectsOfType<TextMeshProUGUI>(true).Select(x => x.font);
                    s_retroGamingFontAsset = ggggfd.FirstOrDefault(x => x != null && x.name == "Retro GamingPix");
                    if (s_retroGamingFontAsset == null) {
                        s_retroGamingFontAsset = Resources.FindObjectsOfTypeAll<TMP_FontAsset>()
                            .FirstOrDefault(x => x != null && x.name == "Retro GamingPix");
                    }
                }
                if (s_retroGamingFontAsset != null) {
                    UnicodeFontFallback.EnsureInstalled(s_retroGamingFontAsset);
                    name = FontUtils.ReplaceMissingCharacters(s_retroGamingFontAsset, name);
                }
            }

            if (BasePlayer != null) {
                if (Net.IsServer) {
                    BasePlayer.playerName.Value = name;
                    BasePlayer.playerColor.Value = color;
                }
            }

            // the synced name is the one that player connected with. do not replace it with this machine's friend cache.
            nameIsCustom = true;

            playerName = name;
            playerColor = color;
            gameObject.name = ScavPlayer.OBJNAMEPREFIX_PLR + name;

            Color color_float = (Color)color;
            Color transparent = new Color(color_float.r, color_float.g, color_float.b, 0f);

            if (body != null) {
                netBody.ApplyNameAndColor(name, color);
            }

            if (locationPingCircle) {
                locationPingCircle.name = ("fingerpointercircle_" + playerName);
                SpriteRenderer spr = locationPingCircle.GetOrAddComponent<SpriteRenderer>();
                spr.color = transparent;
            }
            if (locationPingArrow) {
                locationPingArrow.name = ("fingerpointerarrow_" + playerName);
                SpriteRenderer spr = locationPingArrow.GetOrAddComponent<SpriteRenderer>();
                spr.color = transparent;
            }
        }

        public bool IsInSameTeamAs(ScavPlayer plr) {
            return this.playerColor == plr.playerColor;
        }

        public List<ScavPlayer> GetTeamPlayers() {
            List<ScavPlayer> plrs = new List<ScavPlayer>(ScavPlayer.AllPlayers);
            plrs.RemoveAll(x => !this.IsInSameTeamAs(x));
            return plrs;
        }

        #region COLOR_BULLSHIT

        public Color24 playerColor = Color.black;

        public static Color[] DEFAULT_TEAMS = new Color[] {
            Color.red,
            Color.blue,
            Color.green,
            Color.yellow,
        };

        public static Color[] NAMETAG_DEFAULT_COLORS = new Color[] {
            Color.red,
            Color.green,
            Color.blue,
            Color.cyan,
            Color.yellow,
            Color.magenta,
            Color.white
        };
        public static Color[] NAMETAG_DEFAULT_COLORS_RANDOMIZED = new Color[] {
            Color.red,
            Color.green,
            Color.blue,
            Color.cyan,
            Color.yellow,
            Color.magenta,
            Color.white
        };
        private static byte SERVER_NAMETAG_COLOR_PICKER_COUNTER = 0;

        public static bool CheckIfPlrColorIsValid(Color24 c) {
            if (c.Sum() < 153) {
                return false;
            }
            return true;
        }

        static Color24 PickNewPlayerColor() {
            for (int i = 0; i < NAMETAG_DEFAULT_COLORS.Length; i++) {
                if (SERVER_NAMETAG_COLOR_PICKER_COUNTER >= NAMETAG_DEFAULT_COLORS.Length) {
                    SERVER_NAMETAG_COLOR_PICKER_COUNTER = 0;
                }
                Color24 result = (Color24)NAMETAG_DEFAULT_COLORS[SERVER_NAMETAG_COLOR_PICKER_COUNTER];

                SERVER_NAMETAG_COLOR_PICKER_COUNTER += 1;

                if (ClientIdToPlayerDict.Values.Any(x => x.playerColor == result)) {
                    continue;
                }
                return result;
            }

            for (int i = 0; i < 200; i++) {
                Color24 result = new Color24((byte)UnityEngine.Random.Range(0, 255), (byte)UnityEngine.Random.Range(0, 255), (byte)UnityEngine.Random.Range(0, 255));

                //dont allow it to be too dark
                if (!CheckIfPlrColorIsValid(result)) {
                    result[UnityEngine.Random.Range(0, 3)] = (byte)UnityEngine.Random.Range(200, 255);
                }

                if (ClientIdToPlayerDict.Values.Any(x => x.playerColor == result)) {
                    continue;
                }
                return result;
            }

            log.error($"COULDN'T PICK A NEW COLOR FOR A PLAYER AAAAAAAAA WTFFFFF!!?!?!?!?");
            return NAMETAG_DEFAULT_COLORS.PickRandom();
        }

        #endregion

        // is called also from LES on sync
        public void SetUnchipped(bool unchipped) {
            if (WorldMP.RunUnchippedLock.BlocksLocalChange(unchipped)) {
                return;
            }

            if (body != null && netBody.unchipped != unchipped) {
                netBody.SetUnchipped(unchipped);
            }

            if (this.unchipped != unchipped) {
                this.unchipped = unchipped;
            }

            if (IsLocal) {
                Util.SetUnchipped(unchipped);
            }
        }

        public bool HasGunEquipped(out GunScript gun) {
            if (body != null && body.conscious &&
                //OG: PlayerCamera.GunToggleSafety and others where they check for a gun
                body.HoldingItem(body.handSlot) && body.GetItem(body.handSlot).TryGetComponent(out GunScript component)
            ) {
                gun = component;
                return true;
            }
            gun = null;
            return false;
        }

        public bool CanCommunicateWith_TextChat(ScavPlayer target) {
            if (target == this) {
                return true;
            }
            if (target != null) {
                if (Util.IsTutorialWorld()) {
                    return true;
                }
                bool targetisalive = target.body == null || target.body.alive;
                bool iamalive = body == null || body.alive;
                if (!iamalive && targetisalive && !(Multiplayer.rules.DeadTextchat)) {
                    return false;
                }
                return true;
            }
            return false;
        }

        public void ResetEntropy() {
            if (body != null) {
                body.ResetEntropy();
                netBody.SetNetHealthSyncIgnoreTime(0.1f);
            }
        }

        public void CreateCharacter() {
            if (Util.IsInMainMenu()) {
                return;
            }
#if FULLGAME
            // species pick gate - no default Experiment body before Join/Ready.
            if (Net.IsServer && CharacterSelectWait.IsPendingLateJoin(playerId)) {
                return;
            }
            // lobby gate only. a late joiner who already picked must still get a body
            // even if Waiting was left set, or the host never sends them a spawn point.
            if (Net.IsServer && CharacterSelectWait.Waiting && !LateJoin) {
                return;
            }
#endif
            if (Net.IsServer) {
                NetBody.CreateNewPlayerCharacter(this);
            }
        }

        public bool Server_HasForcedServerOwnershipOfBody() {
            if (body != null) {
                if (!body.alive) {
                    return true;
                }
            }
            return false;
        }

        public void Server_DoAlertSingle(in string msg, bool reliable = true) => Server_DoAlertSingle(msg, false, reliable);
        public void Server_DoAlertSingle(in string msg, bool important, bool reliable) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceDoAlert);
            writer.Put(msg);
            writer.Put(important);
            Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: playerId);
        }

        public void Server_Kick(string message = "Kicked.") {
            if (Net.TryGetSteamTransport(out TransportSteamworks _) && KnownPersons.IsSteamUserPrivileged(SteamId)) {
                // kick protection
                return;
            }

            try {
                if (body != null) {
                    body.Body_DropAllItems();
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Lobby, "ScavPlayer.Server_Kick.DropItems", e);
            }

            string msg = $"Kicked {playerName}";
            Chat.Server_ChatAnnouncement(msg);
            try {
                ServerMain.Server_SendMPLogMessage(msg + ": " + message, false, new knetid[] { playerId });
            } catch (Exception e) {
                log.error(e);
            }

            Net.Server_Kick(playerId, message);
        }

        public void Server_ReviveCharacter(bool remind_health_now = true, bool unmindwipe = false) {
            if (Util.IsInWorld()) {
                CreateCharacter();

                if (!body.alive) {
                    if (!Multiplayer.rules.RespawnKeepSkills) {
                        body.ResetMind();
                    }
                }

                body.ResetHealth(unmindwipe);

                if (remind_health_now) {
                    MedicalSync.Server_SendCharacterHealth(netBody);
                }
            }
        }

        // used for teleporting
        public void Server_RemindPlayersCurrentState(bool keep_velocity = false, bool reliable = true) {
            if (TryGetNetBody(out NetBody nb)) {
                if (!keep_velocity) {
                    body.SetVelocity(Vector2.zero);
                }
                nb.Server_RemindPlayersCurrentState(reliable);
            } else {
                if (log.verbose) {
                    log.warn($"Attempted to remind body state of {this} but theres no body.");
                }
            }
        }

        public void Server_TeleportCharacter(Vector2 pos) {
            if (Util.IsInWorld()) {
                CreateCharacter();

                if (IsLocal) {
                    UIInGame._DEV_FREECAM_CURPOS = pos;
                }

                netBody.SetBodyPosition(pos);

                if (!this.IsLocal) {
                    WorldChunkSync.Server_CheckPlrPosChunk(this);
                }

                Server_RemindPlayersCurrentState();
            }
        }

        public void Server_RespawnCharacter(Vector2 pos, bool level_transition = false) {
            if (Util.IsInWorld()) {
                CreateCharacter();

                if (level_transition && !Multiplayer.rules.RespawnKeepInventory) {
                    Server_DropAllInventory();
                }

                if (!Multiplayer.rules.RespawnKeepSkills) {
                    body.ResetMind();
                }

                bool should_unmindwipe = !Multiplayer.rules.RespawnKeepSkills;

                netBody.StopPiggyback();

                Server_ReviveCharacter(true, should_unmindwipe);
                body.ForceStand();

                Server_TeleportCharacter(pos);
            }
        }

        // can be called from client btw
        public void Server_DropAllInventory() {
            body?.Body_DropAllItems();
        }

        public void Server_HealCharacter() {
            Server_ReviveCharacter(false);
        }

        public bool IsAtTheEndOfLayer() {
            if (body == null) { return false; }
            return ServerMain.DidHeFinishTheLayer(body);
        }
    }
}
