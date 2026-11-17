using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

public partial class Chat {
    enum MessageType : byte {
        PlayerMessage,
        ServerAnnouncement,
        ServerAnnouncementCustomName,
    }

    public static void OnLogMessage() {
        focustime = Math.Max(focustime, Time.realtimeSinceStartupAsDouble - 2.1f);
        CHATScrollPosition = 0f;
    }

    public static void LogMessage(string plrname, string msg, in bool richtext = false) {
        var msgContainer = new ChatMsgContainer(plrname, null, msg, richtext);
        CHAT_LOG.Enqueue(msgContainer);
        OnLogMessage();
    }

    private static bool CheckForCommandInMessageInput() {
        var splited = new List<string>(CHAT_current_input.Trim().TrimStart('/').Split(' '));
        if (splited[0] == "?" || splited[0] == "help") {
            LogMessage(
                NAMELABEL_LOCALSYSTEM,
                "Main commands:"
                + "\n/[console command] // run a console command"
                + "\n/clear // clear chat history"
                + "\n/team [message] // team message (If teams are enabled)"
                + "\n/whisper [plr] [message] // whisper"
                + $"\n// Use console for console commands. ({KeyBinds.GetBindName("console")})");
            return true;
        }

        string cmdname = splited[0];
        if (cmdname == "clear") {
            CHAT_LOG.Clear();
        } else if (cmdname == "team" || cmdname == "t") {
            string msg = string.Join(" ", splited.Skip(1));
            InputVerifyChatMessage(msg);

            var teammates = ScavPlayer.LocalPlayer != null ? ScavPlayer.LocalPlayer.GetTeamPlayers() : new List<ScavPlayer>();
            if (ScavPlayer.LocalPlayer == null || !Multiplayer.rules.Teams || teammates.Count <= 1) {
                LogMessage(NAMELABEL_LOCALSYSTEM, "You don't have a team.");
            } else {
                Client_SendChatMessage(msg, true);
            }
        } else if (cmdname == "whisper" || cmdname == "w") {
            string msg = string.Join(" ", splited.Skip(2));
            InputVerifyChatMessage(msg);

            if (splited.Count > 1) {
                List<ScavPlayer> targetplr = Con.GetPlayersForCommand(splited[1], ScavPlayer.LocalPlayer, true, true);
                if (targetplr != null && targetplr.Count > 0) {
                    if (ScavPlayer.LocalPlayer != null) {
                        Client_SendChatMessage(msg, false, true, targetplr[0].playerId);
                    } else {
                        Server_ChatAnnouncement("SERVER", Lang.MarkMsgAsLocaleKey("plr_chattag_whisper"), msg, new List<knetid> { targetplr[0].playerId });
                    }
                }
            }
        } else if (cmdname == "me") {
            return false;
        } else {
            LogMessage(NAMELABEL_LOCALSYSTEM, $"Unknown command: {cmdname}");
        }

        return true;
    }

    public static void OnEnteredUserMessage(string text) {
        CHAT_current_input = text;
        OnEnteredUserMessage();
    }

    static void OnEnteredUserMessage() {
        bool ran_a_command = CHAT_current_input.StartsWith("/") && CheckForCommandInMessageInput();
        if (!ran_a_command) {
            SendChatMessage(CHAT_current_input);
        }

        _chatinput_changed = true;
        if (!MyMessageLog.Contains(CHAT_current_input)) {
            MyMessageLog.Enqueue(CHAT_current_input);
        }
        MyMessageLog_Index = MyMessageLog.Count;
        CHAT_current_input = "";
    }

    public static bool ValidateChatMessage(in string msg) {
        return !msg.Contains('\r') && msg.Length <= MAX_MESSAGE_LENGTH;
    }

    public static bool CharCanBeReplacedWithBlank(in char c) {
        return !(char.IsWhiteSpace(c) || c == '?' || c == '!' || c == '.' || c == '-');
    }

    public static string TagName(string name, string tag, bool rich = false) {
        if (!string.IsNullOrWhiteSpace(tag)) {
            Lang.MsgTryParse(ref tag);
            if (!rich) {
                tag = Multiplayer.SanitizeRichText(tag);
            }
            name = $"*{tag}* {name}";
        }
        return name;
    }

    public static string TagNameMask(string name, string tag, bool showName = false) {
        if (!string.IsNullOrWhiteSpace(tag)) {
            Lang.MsgTryParse(ref tag);
            if (showName) {
                tag = Multiplayer.SanitizeRichText(tag);
                name = $"<color=#00000000>*{tag}* </color>{name}";
            } else {
                name = $"*{tag}* <color=#00000000>{name}</color>";
            }
        }
        return name;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ChatMessage, false)]
    static void Client_ChatMessageReceive(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out byte rawType);
        var msgType = (MessageType)rawType;

        switch (msgType) {
            case MessageType.PlayerMessage: {
                reader.Get(out knetid speakerId);
                reader.Get(out string tag);
                reader.Get(out string msg);

                string plrname = speakerId.ToString();
                if (ScavPlayer.TryGetPlayerFromClientId(speakerId, out ScavPlayer plr)) {
                    plrname = plr.playerName;
                    if (plr.IsAlive()) {
                        Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(plr.body.talker, msg, true);
                    }
                }

                if (Net.IsClient) {
                    try {
                        OnPlayerChatMessage?.Invoke(plr, msg);
                    } catch (Exception e) {
                        ModLog.Exception(LogDomain.UI, "CLIENT: OnPlayerChatMessage", e);
                    }
                }

                if (Net.IsClient && SHOULD_LOG_CHAT) {
                    Plugin.log.LogInfo(log.do_timestamp($"CLIENT: CHAT PLAYER MESSAGE: {plrname}: {msg}"));
                }

                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.95f, 0.3f);
                CHAT_LOG.Enqueue(plr != null
                    ? new ChatMsgContainer(plr, tag, msg, false)
                    : new ChatMsgContainer(plrname, tag, msg, false));
                OnLogMessage();
                break;
            }

            case MessageType.ServerAnnouncement: {
                reader.Get(out string msg);
                Lang.MsgTryParse(ref msg);
                if (Net.IsClient && SHOULD_LOG_CHAT) {
                    Plugin.log.LogInfo($"CLIENT: SERVER ANNOUNCEMENT MESSAGE: {msg}");
                }
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.86f, 0.9f);
                LogMessage(NAMELABEL_SERVER, msg, true);
                break;
            }

            case MessageType.ServerAnnouncementCustomName: {
                reader.Get(out string name);
                reader.Get(out string tag);
                reader.Get(out string msg);
                if (Net.IsClient && SHOULD_LOG_CHAT) {
                    Plugin.log.LogInfo($"CLIENT: SERVER NAMED MESSAGE: {name}: {msg}");
                }
                Lang.MsgTryParse(ref msg);
                Util.PlayUISound(PlayerCamera.UISoundType.MiniClick, 0.95f, 0.7f);
                LogMessage(TagName(name, tag), msg, true);
                break;
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerChatMessageSend)]
    static void Server_PlayerChatMessageSend(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out bool only_for_team);
        reader.Get(out bool whisper);
        reader.Get(out knetid whisperTargetPlrId);
        reader.Get(out string message);

        if (string.IsNullOrWhiteSpace(message)) return;

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer yapper)) {
            if (yapper.server_mute_tc) {
                var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
                writer.Put((byte)MessageType.ServerAnnouncement);
                writer.Put("You're muted by the server!");
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, yapper.playerId);
                return;
            }

            if (!ValidateChatMessage(message)) {
                yapper.Server_DoAlertSingle("SERVER: Invalid chat message.", false);
                return;
            }
            if (SHOULD_LOG_CHAT) {
                Plugin.log.LogInfo($"SERVER: \"{yapper}\" CHAT MESSAGE: {message}");
            }

            try {
                OnPlayerChatMessage?.Invoke(yapper, message);
            } catch (Exception e) {
                ModLog.Exception(LogDomain.UI, "SERVER: OnPlayerChatMessage", e);
            }

            bool chatspy = Con._DEV_CHATSPY;
            bool forceseelocally = chatspy || Net.IsDedicatedServer;
            string yappertag = yapper.IsDead() ? Lang.MarkMsgAsLocaleKey("plr_chattag_dead") : "";

            if (forceseelocally) {
                LogMessage(TagName(yapper.playerName, yappertag), message);
            }

            if (yapper.IsAlive() && Multiplayer.rules.SpeechImpairedChat) {
                message = yapper.body.talker.DistortString(message);
            }

            if (forceseelocally) {
                yapper.body?.talker?.ForceNoSpeechImpairment(message, true);
            }

            List<ScavPlayer> targetlisteners = ScavPlayer.AllPlayers.ToList();
            if (only_for_team) {
                targetlisteners = yapper.GetTeamPlayers();
                yappertag = $"<color={yapper.playerColor.ToHex()}><key:plr_chattag_team></color>";
            }

            ScavPlayer whisperTargetPlr = null;
            if (whisper) {
                if (!ScavPlayer.TryGetPlayerFromClientId(whisperTargetPlrId, out whisperTargetPlr)) {
                    yapper.Server_DoAlertSingle("SERVER: Invalid chat message. (Whisper player not found?)", false);
                    return;
                }
                targetlisteners = new List<ScavPlayer> { whisperTargetPlr, yapper };
                yappertag = "<key:plr_chattag_whisper>";
            }

            foreach (ScavPlayer plr in targetlisteners) {
                if (chatspy && plr.IsLocal) continue;

                string individualmessage = message;
                string individualtag = yappertag;

                if (plr == yapper) {
                    if (whisper) {
                        individualtag = $"{individualtag} <key:plr_chat_to> <plr:{whisperTargetPlr.playerId}>";
                    }
                } else if (Util.IsWorldGenerated()) {
                    if (!yapper.CanCommunicateWith_TextChat(plr)) continue;
                    if (plr.IsAlive()) {
                        plr.netBody.HearinglossDistortMessage(yapper.netBody, ref individualmessage, ref individualtag);
                    }
                    if (string.IsNullOrWhiteSpace(individualmessage)) continue;
                }

                var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
                writer.Put((byte)MessageType.PlayerMessage);
                writer.Put(clientId);
                writer.Put(individualtag);
                writer.Put(individualmessage);
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
            }
        }
    }

    public static bool InputVerifyChatMessage(in string message) {
        if (string.IsNullOrEmpty(message)) return false;
        if (!ValidateChatMessage(message)) {
            LogMessage(NAMELABEL_LOCALSYSTEM, "Message is invalid.");
            return false;
        }
        if (!Net.IsRunning) {
            LogMessage("*OFFLINE*", message);
            return false;
        }
        return true;
    }

    public static void SendChatMessage(in string message, bool force_server_if_server = false) {
        InputVerifyChatMessage(message);
        if (Net.IsDedicatedServer || (force_server_if_server && Net.IsServer)) {
            Server_ChatAnnouncement(message);
        } else {
            Client_SendChatMessage(message);
        }
    }

    public static void Client_SendChatMessage(in string message, bool team = false, bool whisper = false, ushort whisperTargetPlrId = 0) {
        var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerChatMessageSend);
        writer.Put(team);
        writer.Put(whisper);
        writer.Put(whisperTargetPlrId);
        writer.Put(message);
        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    public static void Server_ChatAnnouncement(in string message) {
        if (Net.IsDedicatedServer) {
            LogMessage(NAMELABEL_SERVER, message);
        }
        if (SHOULD_LOG_CHAT) {
            log.l($"SERVER: SENDING CHAT ANNOUNCEMENT: {message.Replace("\r", "")}");
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
        writer.Put((byte)MessageType.ServerAnnouncement);
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.AllClientIds);
    }

    public static void Server_ChatAnnouncement(string name, string tag, string message, IReadOnlyList<knetid> targets = null) {
        targets ??= ServerMain.AllClientIds;
        if (Net.IsDedicatedServer) {
            LogMessage(TagName(name, tag), message);
        }
        if (SHOULD_LOG_CHAT) {
            log.l($"SERVER: SENDING NAMED CHAT ANNOUNCEMENT: NAME:\"{name}\" TAG:\"{tag}\": {message}");
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_ChatMessage);
        writer.Put((byte)MessageType.ServerAnnouncementCustomName);
        writer.Put(name);
        writer.Put(tag);
        writer.Put(message);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, targets);
    }
}
