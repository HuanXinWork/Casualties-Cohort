using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using System.Data;
using System.Diagnostics;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

public partial class Multiplayer {

    internal static NetDataWriter GetClientConnectIntroductionPacket(NetDataWriter writer = null) {
        var credentials_buffer = writer ?? new NetDataWriter();

        credentials_buffer.Put(GAME_FILES_HASH);
        credentials_buffer.Put(FULL_VERSION_TAG, true);
        // 握手包 userName 字段始终写入兼容原版 v5.5.3.0 房主 ASCII 校验的确定性安全名；
        // 若本地真实昵称含中文/西里尔等字符，入服后由 UnicodeFontFallback 通过 CustomCommand "unicode_name" 无缝升级。
        string handshakeSafeName = BuildAsciiHandshakeSafeName(PLAYER_USERNAME);
        credentials_buffer.Put(handshakeSafeName, true);

        var l = credentials_buffer.Length;

        credentials_buffer.Put(INPUT_PASSWORD, true);
        credentials_buffer.Put(UIMainMenu.LAST_VALID_INPUT_COLOR);

        credentials_buffer.Put(CLIENT_JOIN_SECRET, true);
        credentials_buffer.PutArray(GetModGUIDs());

        credentials_buffer.CompressWriter(l);

        return credentials_buffer;
    }

    public struct IntroductionPlayerInfo {
        public ulong fileHash;
        public string version;
        public string userName;
        public string password;
        public string secret;
        public Color24 color;
        public string[] modlist;
        public HashSet<string> modlist_hashset;
    }

    public static bool ReadClientConnectIntroductionPacket(NetDataReader credentialsBuffer, ref IntroductionPlayerInfo userInfo) {
        userInfo.fileHash = 0;
        userInfo.version = "";
        userInfo.userName = "NONAME";
        userInfo.secret = "0";
        userInfo.modlist = null;
        bool aaaaaaaaaaa = true;
        try {
            credentialsBuffer.Get(out userInfo.fileHash);
            credentialsBuffer.Get(out userInfo.version, true);
            credentialsBuffer.Get(out userInfo.userName, true);
            credentialsBuffer = credentialsBuffer.DecompressReader();
            credentialsBuffer.Get(out userInfo.password, true);
            credentialsBuffer.Get(out userInfo.color);
            if (!credentialsBuffer.EndOfData) { // 4.0.0
                credentialsBuffer.Get(out userInfo.secret, true);
                credentialsBuffer.TryGetStringArray(out userInfo.modlist);
            }
        } catch (Exception ex) {
            ModLog.Debug($"ReadClientConnectIntroductionPacket failed: {ex.Message}", LogDomain.Security);
            aaaaaaaaaaa = false;
        }
        return aaaaaaaaaaa;
    }

    public static bool ValidateClientConnectIntroductionPacket(NetDataReader reader, ref IntroductionPlayerInfo userInfo, out string deny_reason, bool check_name = true, bool userHasMasterKey = false) {
        userInfo.userName = "EMPTY_STRING";
        userInfo.color = Color24.black;
        deny_reason = $"Server broke or some shi ig.";

        try {
            if (!Multiplayer.ReadClientConnectIntroductionPacket(reader, ref userInfo)) {
                deny_reason = ($"Corrupt payload.");
                return false;
            }

            if (!userHasMasterKey) {
                if (userInfo.version != (Multiplayer.FULL_VERSION_TAG) || userInfo.modlist == null) {
#if !RELEASE
                    if (userInfo.modlist == null)
                        deny_reason = ($"modlist == null");
                    else
                        deny_reason = ($"Version mismatch.");
#else
                    deny_reason = ($"Version mismatch.");
#endif
                    return false;
                }
                if (Multiplayer.SERVER_TOGGLE_CHECK_GAME_HASH) {
                    if (Multiplayer.GAME_FILES_HASH != userInfo.fileHash) {
                        deny_reason = ($"Game file Hashes mismatch.");
                        return false;
                    }
                }
            }
            if (check_name) {
                // 支持 Unicode 昵称校验：含非 ASCII 字符允许 Length >= 1，纯 ASCII 仍要求 Length >= 3
                string sanitizedUnicode = Multiplayer.SanitizeUnicodeTextInput(userInfo.userName);
                if (userInfo.userName != sanitizedUnicode) {
                    deny_reason = ($"Corrupt payload.");
                    return false;
                }
                int minLength = Multiplayer.ContainsNonAscii(userInfo.userName) ? 1 : 3;
                if (userInfo.userName.Length < minLength) {
                    deny_reason = ($"Name too short.");
                    return false;
                }
            }

            if (!userHasMasterKey) {
                if (Multiplayer.SERVER_TOGGLE_ENFORCE_MODLIST) {
                    if (!(new HashSet<string>(GetModGUIDs()).SetEquals(userInfo.modlist))) {
                        deny_reason = ($"This server requires different set of mods.");
                        return false;
                    }
                }
#if TESTING
                log.l($"plr {userInfo.userName}  DISCORD SECRET TEST:{userInfo.secret} ");
#endif
                if (Multiplayer.SERVER_JOIN_SECRETS.Contains(userInfo.secret)) {
                    // move it to the front to get removed faster
                    Multiplayer.SERVER_JOIN_SECRETS.Remove(userInfo.secret);
                    Multiplayer.SERVER_JOIN_SECRETS.Insert(0, userInfo.secret);
                } else {
                    if (!String.IsNullOrEmpty(Multiplayer.INPUT_PASSWORD) && userInfo.password != Multiplayer.INPUT_PASSWORD) {
                        deny_reason = ($"Wrong password.");
                        return false;
                    }
                }

                if (!ScavPlayer.CheckIfPlrColorIsValid(userInfo.color)) {
                    deny_reason = ($"Invalid color.");
                    return false;
                }
            }

            deny_reason = "Success";
            userInfo.password = null;
            return true;
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Security, nameof(ValidateClientConnectIntroductionPacket), ex);
            deny_reason = $"Corrupt payload.";
            return false;
        }
    }

    #region OLD_SENDERS

    public static void Client_SendSimpleMessageToServer(in byte name, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(true);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, bool data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, byte data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, byte data1, Vector2 data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data1);
        writer.Put(data);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, Vector2 data, Vector2 data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, ushort data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, byte data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, ushort data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, float data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, float data2, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Client_SendSimpleMessageToServer(in byte name, ushort data, byte data2, byte data3, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        writer.Put(data2);
        writer.Put(data3);
        Net.Client_Send(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer);
    }

    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, ushort data, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, Vector2 data, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_SendRelayMessageToClients(in byte name, ushort any_id, Vector2 data, Vector2 data2, bool reliable = true, bool includehost = false) {
        var writer = Net.CreateWriter(name);
        writer.Put(any_id);
        writer.Put(data);
        writer.Put(data2);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: includehost ? ServerMain.AllClientIds : ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_SendSimpleMessageToOneClient(in NetmsgId name, ushort clientId, bool reliable = true) => Server_SendSimpleMessageToOneClient((byte)name, clientId, reliable);

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(true);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
    }

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, ushort data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
    }

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, float data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
    }

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, Vector2 data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
    }

    public static void Server_SendSimpleMessageToOneClient(in byte name, ushort clientId, string data, bool reliable = true) {
        var writer = Net.CreateWriter(name);
        writer.Put(data, true);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: clientId);
    }

    #endregion

    public static NetDataWriter CreateNamedWriter(string name) {
        return CreateNamedWriter((uint)name.StableHash());
    }
    public static NetDataWriter CreateNamedWriter(uint nameHash) {
        var ndw = new NetDataWriter();
        ndw.Put((byte)NetmsgId.CustomMessage);
        ndw.Put(nameHash);
        return ndw;
    }

    static TwoWayDictionary<uint, string> _ReceiversOriginalNames = new();
    static Dictionary<uint, Action<ScavPlayer, NetDataReader>> _ServerReceivers = new();
    static Dictionary<uint, Action<NetDataReader>> _ClientReceivers = new();

    public static uint RegisterCustomServerReceiver(string name, Action<ScavPlayer, NetDataReader> receiver) {
        var hash = (uint)name.StableHash();
        if (_ServerReceivers.ContainsKey(hash)) {
            throw new DuplicateNameException($"Hash of \"{name}\" collided! ({hash.ToString()}) Change the receiver name!");
        }
        InternalAddCustomServerReceiver(hash, receiver);
        _ReceiversOriginalNames[hash] = name;
        return hash;
    }

    public static uint RegisterCustomClientReceiver(string name, Action<NetDataReader> receiver) {
        var hash = (uint)name.StableHash();
        if (_ClientReceivers.ContainsKey(hash)) {
            throw new DuplicateNameException($"Hash of \"{name}\" collided! ({hash.ToString()}) Change the receiver name!");
        }
        InternalAddCustomClientReceiver(hash, receiver);
        _ReceiversOriginalNames[hash] = name;
        return hash;
    }

    public static void InternalAddCustomServerReceiver(uint id, Action<ScavPlayer, NetDataReader> receiver) {
        if (_ServerReceivers.ContainsKey(id)) {
            throw new DuplicateNameException($"Receiver id collided! ({id.ToString()}) Change the id!");
        }
        _ServerReceivers[id] = receiver;
    }

    public static void InternalAddCustomClientReceiver(uint id, Action<NetDataReader> receiver) {
        if (_ClientReceivers.ContainsKey(id)) {
            throw new DuplicateNameException($"Receiver id collided! ({id.ToString()}) Change the id!");
        }
        _ClientReceivers[id] = receiver;
    }

    [ClientReceiver((byte)NetmsgId.CustomMessage, false)]
    static void ClientReceiver__CustomMessage(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint hash);
        if (_ClientReceivers.TryGetValue(hash, out var func)) {
            try {
                func(reader);
            } catch (Exception e) {
                var messageStr = _ReceiversOriginalNames.GetValueSafe(hash) ?? hash.ToString();
                ModLog.Exception(LogDomain.Transport, $"ClientCustomReceiver:\"{messageStr}\"", e);
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.CustomMessage)]
    static void ServerReceiver__CustomMessage(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint hash);
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && _ServerReceivers.TryGetValue(hash, out var func)) {
            try {
                func(plr, reader);
            } catch (Exception e) {
                var messageStr = _ReceiversOriginalNames.GetValueSafe(hash) ?? hash.ToString();
                ModLog.Exception(LogDomain.Transport, $"ServerCustomReceiver:\"{messageStr}\"", e);
            }
        }
    }

    public static void InternalAddServerReceiver(byte name, Multiplayer.HandleMessageDelegate receiver) {
        Multiplayer.all_network_receivers_from_attributes.Add(new ServerReceiverAttribute(name), receiver);
    }
    public static void InternalAddClientReceiver(byte id, Multiplayer.HandleMessageDelegate receiver, bool ignoreHost = false) {
        Multiplayer.all_network_receivers_from_attributes.Add(new ClientReceiverAttribute(id, ignoreHost), receiver);
    }
}
