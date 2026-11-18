using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteEntitySystem;
using LiteNetLib;
using LiteNetLib.Utils;
using Steamworks;
using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using Together.Api;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together {

    public static partial class Net {
        public enum NetType {
            Client,
            Host,
            DedicatedServer
        }

        public static bool IsRunning { get; private set; }
        public static bool IsRunningSteam { get; private set; }
        public static bool IsClient => Type == NetType.Client;
        public static bool IsClientOrHost => !IsDedicatedServer;
        public static bool IsServer => !IsClient;
        public static bool IsHost => Type == NetType.Host;
        public static bool IsDedicatedServer => Type == NetType.DedicatedServer;
        public static bool IsConnecting => Transport != null && Transport.IsConnecting();
        public static bool IsConnected => IsRunning && !IsConnecting;
        public static NetType Type { get; internal set; }

        public static NetTransportBase Transport { get; internal set; }

        public static NetPublicServerInfo MyServerInfo = new NetPublicServerInfo();
        public static NetPublicServerInfo CurrentServerInfo = new NetPublicServerInfo();

        internal static ClientEntityManager ClientEntityManager { get; private set; }
        internal static ServerEntityManager ServerEntityManager { get; private set; }
        internal static EntityManager EntityManager => ((EntityManager)ClientEntityManager ?? (EntityManager)ServerEntityManager);

        private static readonly HashSet<NetPlayer> removingServerPlayers = new();

        // removePlayer destroys the entity before the id returns to the pool. a second call for the same player must not run.
        internal static void RemoveServerPlayer(NetPlayer player) {
            ServerEntityManager manager = ServerEntityManager;
            if (manager == null || player == null ||
                !ReferenceEquals(manager.GetPlayer(player.Id), player) ||
                !removingServerPlayers.Add(player)) {
                return;
            }

            try {
                manager.RemovePlayer(player);
            } finally {
                removingServerPlayers.Remove(player);
            }
        }

        public const ushort DEFAULT_PORT = 7790;
        public const string IP_LOCALHOST_IPv4 = "127.0.0.1";
        public const string IP_LOCALHOST_IPv6 = "::1";
        public const int SERVER_CLIENTID = 0;

        public static readonly int MAX_SERVER_NAME_LENGTH = 32;
        public static readonly int MAX_SERVER_DESC_LENGTH = 200;
        public static readonly int MAX_SERVER_DESC_NEWLINES = 3;

        public static bool IsMyServerMidjoinLocked() {
            return Util.IsInWorld() && !Multiplayer.rules.LateJoinAllowed;
        }

        public static bool TryGetSteamTransport(out TransportSteamworks tsteam) {
            if (Transport is TransportSteamworks tsteam2) {
                IsRunningSteam = true;
                tsteam = tsteam2;
                return true;
            }
            IsRunningSteam = false;
            tsteam = null;
            return false;
        }

        public static string ByteMsgIdToName(byte msgid) {
            string n = Enum.GetName(typeof(NetmsgId), msgid);
            return (n != null ? (n + $" ({msgid})") : msgid.ToString());
        }

        public static string GetDebugStatsString() {
            string infostr = "";
            if (Net.Transport == null) {
                return infostr;
            }

            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                if (Net.IsClient) {
                    var status = tsteam.client_status;
                    infostr =
                        $"State: {status.m_eState}" +
                        $"\nPing: {status.m_nPing}" +
                        $"\nConnectionQuality:  Local: {Mathf.RoundToInt(status.m_flConnectionQualityLocal * 100f)}%  Remote: {Mathf.RoundToInt(status.m_flConnectionQualityRemote * 100f)}%" +
                        $"\nPacketsPerSec:  Out: {status.m_flOutPacketsPerSec}  In: {status.m_flInPacketsPerSec}" +
                        $"\nBytesPerSec:  Out: {status.m_flOutBytesPerSec}  In: {status.m_flInBytesPerSec}" +
                        $"\nSendRateBytesPerSecond: {status.m_nSendRateBytesPerSecond}" +
                        $"\nPendingUnreliable: {status.m_cbPendingUnreliable}" +
                        $"\nPendingReliable: {status.m_cbPendingReliable}" +
                        $"\nSentUnackedReliable: {status.m_cbSentUnackedReliable}" +
                        $"\nusecQueueTime: {status.m_usecQueueTime}";
                } else {
                    SteamNetConnectionRealTimeStatus_t status = new();
                    var me = KSteam.GetLocalUserSteamID().m_SteamID;
                    int i = 0;
                    foreach (KeyValuePair<ulong, TransportSteamworks.SteamConnectionData> item in tsteam.connectionMapping) {
                        if (item.Key == me) { continue; }
                        i++;
                        status.m_flConnectionQualityLocal += item.Value.connectionStatus.m_flConnectionQualityLocal;
                        status.m_flOutPacketsPerSec += item.Value.connectionStatus.m_flOutPacketsPerSec;
                        status.m_flInPacketsPerSec += item.Value.connectionStatus.m_flInPacketsPerSec;
                        status.m_flOutBytesPerSec += item.Value.connectionStatus.m_flOutBytesPerSec;
                        status.m_flInBytesPerSec += item.Value.connectionStatus.m_flInBytesPerSec;
                        status.m_nSendRateBytesPerSecond += item.Value.connectionStatus.m_nSendRateBytesPerSecond;
                        status.m_cbPendingUnreliable += item.Value.connectionStatus.m_cbPendingUnreliable;
                        status.m_cbPendingReliable += item.Value.connectionStatus.m_cbPendingReliable;
                        status.m_cbSentUnackedReliable += item.Value.connectionStatus.m_cbSentUnackedReliable;
                        status.m_usecQueueTime.m_SteamNetworkingMicroseconds += item.Value.connectionStatus.m_usecQueueTime.m_SteamNetworkingMicroseconds;
                    }
                    if (i == 0) {
                        infostr = "NO PLAYERS TO MEASURE CONNECTION STATS";
                    } else {
                        status.m_flConnectionQualityLocal /= i;
                        status.m_usecQueueTime.m_SteamNetworkingMicroseconds /= i;

                        infostr =
                            $"ConnectionQuality:  {Mathf.RoundToInt(status.m_flConnectionQualityLocal * 100f)}%" +
                            $"\nPacketsPerSec:  Out: {status.m_flOutPacketsPerSec}  In: {status.m_flInPacketsPerSec}" +
                            $"\nBytesPerSec:  Out: {status.m_flOutBytesPerSec}  In: {status.m_flInBytesPerSec}" +
                            $"\nSendRateBytesPerSecond: {status.m_nSendRateBytesPerSecond}" +
                            $"\nPendingUnreliable: {status.m_cbPendingUnreliable}" +
                            $"\nPendingReliable: {status.m_cbPendingReliable}" +
                            $"\nSentUnackedReliable: {status.m_cbSentUnackedReliable}" +
                            $"\nusecQueueTime: {status.m_usecQueueTime}";
                    }
                }
            } else {
                if (Net.Transport is TransportLiteNetLib tlnl) {
                    NetStatistics stat = tlnl.NetManager.Statistics;
                    infostr =
                        string.Format(
                            "BytesReceived: {0}\nPacketsReceived: {1}\nBytesSent: {2}\nPacketsSent: {3}\nPacketLoss: {4}\nPacketLossPercent: {5}",
                            stat.BytesReceived,
                            stat.PacketsReceived,
                            stat.BytesSent,
                            stat.PacketsSent,
                            stat.PacketLoss,
                            stat.PacketLossPercent);
                }
            }
            return infostr;
        }

        static Dictionary<byte, Multiplayer.HandleMessageDelegate> SERVER_MESSAGE_HANDLERS = new();
        static Dictionary<byte, Multiplayer.HandleMessageDelegate> CLIENT_MESSAGE_HANDLERS = new();

        internal static void RegisterServerReceiver(byte msgid, Multiplayer.HandleMessageDelegate receiver) {
            SERVER_MESSAGE_HANDLERS.Add(msgid, receiver);
        }
        internal static void RegisterClientReceiver(byte msgid, Multiplayer.HandleMessageDelegate receiver) {
            CLIENT_MESSAGE_HANDLERS.Add(msgid, receiver);
        }

        internal static void InvokeServerMessage(knetid callerclientId, NetDataReader reader) {
            byte msgId = 0;
            try {
                reader.Get(out msgId);
                if (SERVER_MESSAGE_HANDLERS.TryGetValue(msgId, out Multiplayer.HandleMessageDelegate func)) {
                    func(callerclientId, reader);
                } else {
                    if (log.verbose) {
                        log.warn($"InvokeServerMessage: Unknown Message Id: {ByteMsgIdToName(msgId)}");
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Transport, $"InvokeServerMessage({DebugHelp.GetPlayerFullDebugString(callerclientId)}, {ByteMsgIdToName(msgId)})", e);
            }
        }

        internal static void InvokeClientMessage(knetid callerClientId, NetDataReader reader) {
            byte msgId = 0;
            try {
                reader.Get(out msgId);
                if (Net.IsDedicatedServer) {
                    log.error($"SERVER: Net.InvokeClientMessage: msgId:{msgId} : UNEXPECTED LOOPBACK ON DEDICATED SERVER !!!!");
                    return;
                }
                if (CLIENT_MESSAGE_HANDLERS.TryGetValue(msgId, out Multiplayer.HandleMessageDelegate func)) {
                    if (callerClientId != Net.SERVER_CLIENTID) {
                        return;
                    }
                    func(callerClientId, reader);
                } else {
                    if (log.verbose) {
                        log.warn($"InvokeClientMessage: Unknown Message Id: {ByteMsgIdToName(msgId)}");
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Transport, $"InvokeClientMessage({ByteMsgIdToName(msgId)})", e);
            }
        }

        // BAN LIST IS CHECKED BY THE TRANSPORT, NEAR THIS VALIDATION
        internal static bool ValidateNewClientHandshake(NetDataReader reader, bool should_validate_name, bool privileged_user, out Multiplayer.IntroductionPlayerInfo userInfo, out string reject_reason) {
            reject_reason = "The server doesn't like you ig.";
            userInfo = new();
            userInfo.userName = "?????????????????????";
            userInfo.color = Color24.black;

            try {
                try {
                    if (!Multiplayer.ValidateClientConnectIntroductionPacket(reader, ref userInfo, out string deny_reason, should_validate_name, privileged_user)) {
                        reject_reason = (deny_reason);
                        return false;
                    }
                } catch (Exception ex) {
                    ModLog.Debug($"ValidateNewClientHandshake inner packet error: {ex.Message}", LogDomain.Security);
                    reject_reason = ($"Corrupt payload.");
                    return false;
                }

                if (!privileged_user) {
                    if (ScavPlayer.ClientIdToPlayerDict.Count() + 1 > Multiplayer.PLAYER_COUNT_LIMIT) {
                        reject_reason = ($"Max players reached ({Multiplayer.PLAYER_COUNT_LIMIT}).");
                        return false;
                    }

                    if (should_validate_name) {
                        foreach (KeyValuePair<knetid, ScavPlayer> item in ScavPlayer.ClientIdToPlayerDict) {
                            if (item.Value.playerName == userInfo.userName) {
                                reject_reason = "Player with this name already exists.";
                                return false;
                            }
                        }
                    } else {
                        _ = Net.TryGetSteamTransport(out _);
                    }

                    string reject_reason2 = null;
                    if (!Multiplayer.AdditionalApprovalCheck(ref reader, userInfo.userName, ref reject_reason2)) {
                        reject_reason = (reject_reason2);
                        return false;
                    }
                }

                return true;
            } catch (Exception e) {
                ModLog.Debug(LogDomain.Security, "INetEventListener.OnConnectionRequest rejected malformed payload: " + e.Message);
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.PacketReject);
                reject_reason = ("You caused an error!! Fuck off!!!");
                return false;
            }
        }

        public static void ShutdownReset() {
            bool wasRunning = IsRunning;
            try {
                ClientEntityManager = null;
                ServerEntityManager = null;

                RPCManager_Initialize_MultiplayerPatch.QuickReset();

                if (IsClient) {
                    MP3Menu.dropdownList = null;
                    if (!String.IsNullOrEmpty(_clientRunSettingsBeforeConnect)) {
                        try {
                            WorldMP.ApplyRunSettings(_clientRunSettingsBeforeConnect);
                        } catch (Exception e) {
                            log.error(e);
                        }
                    }
                }
                _clientRunSettingsBeforeConnect = null;

                IsRunningSteam = false;
                Type = NetType.Host;
                ClientMain.LOCAL_PING = 0;
                ClientMain._last_sync_time = 0;

                ClientMain.SERVER_FPS = 60;
                ClientMain.SERVER_TPS = 60;
                ServerMain.CURRENT_TPS = 60;
                ServerMain.CURRENT_FPS = 60;

                ClientMain.MY_CONNECTION_QUALITY = 100;
                ServerMain.AVG_CONNECTION_QUALITY = 100;
                if (wasRunning) {
                    log.l("Network Shutdown Reset called.");

                    try {
                        Settings.ApplyAll();
                    } catch (Exception e) {
                        log.error(e);
                    }
                }

                IsRunning = false;
                UnicodeFontFallback.ResetClientSyncState();
                ModHooks.LastAppliedCustomWorldSeed = 0;
                ModHooks.PendingClientCustomWorldSeed = 0;
                SERVER_MESSAGE_HANDLERS.Clear();
                CLIENT_MESSAGE_HANDLERS.Clear();

                try {
                    HashSet<ScavPlayer> plrs = new HashSet<ScavPlayer>(GameObject.FindObjectsOfType<ScavPlayer>(true));
                    plrs.AddRange(ScavPlayer.AllPlayers);

                    foreach (ScavPlayer item in plrs) {
                        if (item == null) {
                            continue;
                        }

                        try {
                            GameObject.DestroyImmediate(item.gameObject);
                        } catch (Exception e) {
                            log.error($"Net.ShutdownReset: Player Deletion: {item}: " + e.ToString());
                        }
                    }
                } catch (Exception e) {
                    log.error("Net.ShutdownReset: Player Deletion: " + e.ToString());
                }

                if (Transport != null) {
                    try {
                        Transport.Shutdown();
                    } catch (Exception e) {
                        log.error($"Net.ShutdownReset: Transport Shutdown: {Transport.GetType()}: " + e.ToString());
                    }
                    Transport = null;
                }
            } catch (Exception e) {
                Multiplayer.DoMultiplayerStatusMessageError("Net.ShutdownReset: !!!! CRITICAL FAILURE !!!!: " + e.ToString());
            }

            if (wasRunning) {
                TogetherNetworkApi.NotifySessionEnded();
            }

            try {
                SavesystemPatch.UpdatePersistentDataPathCuzMultiplayer();
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Core, "Net.ShutdownReset.UpdatePersistentDataPath", ex);
            }
        }

        internal static void TransportCreated(NetType ntype, bool save_name = true) {
            Plugin.SkipMainMenuIntro();

            // 必须先写入 Net.Type，确保客户端连接前正确备份本地单人运行设置
            Net.Type = ntype;

            if (IsClient) {
                MP3Menu.dropdownList = null;
                _clientRunSettingsBeforeConnect = WorldMP.CompileRunSettings();
            }

            IsRunning = true;
            Application.runInBackground = true;

            if (save_name) {
#if RELEASE
                PlayerPrefs.SetString("KrokoshaMultiplayer_LastPlayerName", UIMainMenu.USERINPUT_NAME);
                PlayerPrefs.SetString("KrokoshaMultiplayer_LastPlayerColor", UIMainMenu.LAST_VALID_INPUT_COLOR.ToHex());
#endif
            }

            if (IsServer) {
                PlayerPrefs.SetString("KrokoshaMultiplayer_LastMyLobbyName", MyServerInfo.name);
                ServerMain._RegisterServerReceivers();
            }
            if (IsClientOrHost) {
                ClientMain._RegisterClientReceivers();
            }
            if (IsClient) {
                UIMainMenu._GUI____RenderRuleField_PresetChosen = 0;
                UIMainMenu._GUI____RenderRuleField_PresetName = "";
                Multiplayer.rules = new MultiplayerGameRules();
            }

            SavesystemPatch.UpdatePersistentDataPathCuzMultiplayer();
            TogetherNetworkApi.NotifySessionStarted();
        }
        static string _clientRunSettingsBeforeConnect = null;

        internal static void LiteEntitySystemRegisterFieldTypes() {
            EntityManager.RegisterFieldType<knetid>();
            EntityManager.RegisterFieldType<SByteScalarVec2>();
            EntityManager.RegisterFieldType<ShortScalarVec2>((a, b, t) => {
                return new ShortScalarVec2((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
            });
            EntityManager.RegisterFieldType<Vector2Int16>((a, b, t) => {
                return new Vector2Int16((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
            });
            EntityManager.RegisterFieldType<Vector2UInt8>();
            EntityManager.RegisterFieldType<AnyObjectNetId>();
            EntityManager.RegisterFieldType<LimbNetId>();
            EntityManager.RegisterFieldType<Color>();
            EntityManager.RegisterFieldType<Color24>();
            EntityManager.RegisterFieldType<Color32>();
            EntityManager.RegisterFieldType<LiquidStackPart>();
            EntityManager.RegisterFieldType<Vector2_4byte_512>((a, b, t) => {
                return new Vector2_4byte_512((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
            });
            EntityManager.RegisterFieldType<Vector2_4byte_200>((a, b, t) => {
                return new Vector2_4byte_200((short)Mathf.LerpUnclamped(a.x, b.x, t), (short)Mathf.LerpUnclamped(a.y, b.y, t));
            });
            EntityManager.RegisterFieldType<Vector2>(Vector2.LerpUnclamped);
            EntityManager.RegisterFieldType<Vector3>(Vector3.LerpUnclamped);
            EntityManager.RegisterFieldType<Quaternion>(Quaternion.LerpUnclamped);
        }

        internal static void LiteEntitySystemInit() {
            if (ClientEntityManager != null || ServerEntityManager != null) {
                throw new Exception("EntityManager already exists!");
            }

            EntityTypesMap typesMap = ScavEntityTypesMap.CreateTypesMap();
            LiteEntitySystemRegisterFieldTypes();

            LiteEntitySystem.Logger.LoggerImpl = new LiteEntitySystemLogger();

            if (IsClient) {
                ClientEntityManager = new ClientEntityManager(
                    typesMap,
                    new LESKrokNetClientPeer(),
                    (byte)NetmsgId.LiteEntitySystem
                );
            } else if (IsServer) {
                ServerEntityManager = new ServerEntityManager(
                    typesMap,
                    (byte)NetmsgId.LiteEntitySystem,
                    ServerEntitySendRate,
                    ServerSendRate.EqualToFPS
                );
                // distant objects skip three sends out of four. worldgen queues one more full baseline.
                ServerEntityManager.ShouldSendEntityDiff = SendBudget.Allow;
                ServerEntityManager.BeforeLogicTick = FixRejoinBaseline.BeforeTick;

                ServerEntityManager.AddSingleton<ServerWorldStateSingleton>();
            }
        }

        internal static void OnTransportConnected() {
            LiteEntitySystemInit();

            if (Net.Type == NetType.Host && ScavPlayer.LocalPlayer == null) {
                ulong steamId = 0;
                string aplayername = Multiplayer.PLAYER_USERNAME;
                if (Net.TryGetSteamTransport(out _)) {
                    steamId = KSteam.GetLocalUserSteamID().m_SteamID;
                }
                _ = BasePlayer.ServerCreatePlayer(aplayername, steamId, UIMainMenu.LAST_VALID_INPUT_COLOR, true);
            }
        }

        public static byte ServerEntitySendRate = 30;
        public static ushort ServerTick = 0;

        internal static void DoUpdate() {
            try {
                using (FindTheBugInator.Time(FindTheBugInator.Transport)) {
                    Transport?.Update();
                }
#if FULLGAME
                // during client WorldGenerateTerrain only: keep Steam alive but do not Deserialize
                // syncInfo Instantiates mid-hollow (WorldPlacePlayer / Finish wait still need LES).
                if (Net.IsClient && CrystalAuthority.ClientGenThrottle.TerrainGenActive) {
                    return;
                }
#endif
                using (FindTheBugInator.Time(FindTheBugInator.Les)) {
                    ClientEntityManager?.Update();
                    ServerEntityManager?.Update();
                }
            } catch (Exception e) {
                log.error("Net.DoUpdate: " + e.ToString());
            }

            if (Net.ServerEntityManager != null) {
                ServerTick = Net.ServerEntityManager.Tick;
            } else if (Net.ClientEntityManager != null) {
                ServerTick = Net.ClientEntityManager.ServerTick;
            }
        }

        public static void Server_Kick(knetid clientId, string message) {
            Con.ConFailIfNetworkIsRunningAndIsClient();
            Transport.Kick(clientId, message);
        }

        /// <summary>
        /// 解析直连输入的主机地址（IPv4 / IPv6 / 多级域名 / 单标签主机名如 <c>localhost</c>）与可选端口（修复上游 Issue #2）。
        /// <para>
        /// - 空串或全空白直接保持默认 <c>127.0.0.1:7790</c> 返回 <see langword="true"/>；<br/>
        /// - 优先识别 <c>[ipv6]:port</c> 与 <c>[ipv6]</c>；<br/>
        /// - 无方括号时按冒号个数消歧：<c>&gt;= 2</c> 视为无端口纯 IPv6，<c>== 1</c> 一律按 <c>host:port</c> 拆分（修复 <c>localhost:7791</c> 因无点号被误判为 IPv6 的缺陷）。
        /// </para>
        /// </summary>
        public static bool TryParseIPPORT(string IPPORT, out string ip, out ushort port) {
            ip = IP_LOCALHOST_IPv4;
            port = DEFAULT_PORT;

            if (string.IsNullOrWhiteSpace(IPPORT)) {
                return true;
            }

            string trimmed = IPPORT.Trim();
            try {
                if (trimmed.StartsWith("[", StringComparison.Ordinal)) {
                    int closeBracket = trimmed.IndexOf(']');
                    if (closeBracket <= 1) {
                        return false;
                    }

                    string ipv6Host = trimmed.Substring(1, closeBracket - 1).Trim();
                    if (string.IsNullOrEmpty(ipv6Host) || ContainsWhitespace(ipv6Host)) {
                        return false;
                    }

                    string remainder = trimmed.Substring(closeBracket + 1).Trim();
                    if (remainder.Length > 0) {
                        if (!remainder.StartsWith(":", StringComparison.Ordinal)) {
                            return false;
                        }
                        string portText = remainder.Substring(1).Trim();
                        if (string.IsNullOrEmpty(portText) || !ushort.TryParse(portText, out port)) {
                            return false;
                        }
                    }

                    ip = ipv6Host;
                    return true;
                }

                if (trimmed.IndexOf('[') >= 0 || trimmed.IndexOf(']') >= 0) {
                    return false;
                }

                int colonCount = 0;
                int singleColonIndex = -1;
                for (int i = 0; i < trimmed.Length; i++) {
                    if (trimmed[i] == ':') {
                        colonCount++;
                        singleColonIndex = i;
                    }
                }

                if (colonCount >= 2) {
                    // 无方括号且含 >= 2 个冒号：无端口纯 IPv6 字面量（如 ::1 或 fe80::1）
                    if (ContainsWhitespace(trimmed)) {
                        return false;
                    }
                    ip = trimmed;
                    return true;
                }

                if (colonCount == 1) {
                    string hostPart = trimmed.Substring(0, singleColonIndex).Trim();
                    string portPart = trimmed.Substring(singleColonIndex + 1).Trim();
                    if (string.IsNullOrEmpty(hostPart) || ContainsWhitespace(hostPart) || string.IsNullOrEmpty(portPart)) {
                        return false;
                    }
                    if (!ushort.TryParse(portPart, out port)) {
                        return false;
                    }
                    ip = hostPart;
                    return true;
                }

                if (ContainsWhitespace(trimmed)) {
                    return false;
                }
                ip = trimmed;
                return true;
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Transport, nameof(TryParseIPPORT), e);
                return false;
            }
        }

        private static bool ContainsWhitespace(string value) {
            for (int i = 0; i < value.Length; i++) {
                if (char.IsWhiteSpace(value[i])) {
                    return true;
                }
            }
            return false;
        }

        public static bool VerifyMyServerName() {
            if (NsfwContentFilter.RejectCreateIfBlocked()) {
                return false;
            }
            string n = MyServerInfo.name;
            if (string.IsNullOrEmpty(n)) {
                return false;
            }
            int minLen = Multiplayer.ContainsNonAscii(n) ? 1 : 3;
            if (n.Length < minLen) {
                return false;
            }
            if (n.Length > Net.MAX_SERVER_NAME_LENGTH || Multiplayer.SanitizeUnicodeTextInputAllowSpaces(n, Net.MAX_SERVER_NAME_LENGTH) != n) {
                return false;
            }

            return true;
        }

        public static NetDataWriter CreateWriter(in Enum msgid) {
            return CreateWriter((byte)(object)msgid);
        }
        public static NetDataWriter CreateWriter(in Enum msgid, int size, bool autoresize = false) {
            return CreateWriter((byte)(object)msgid, size, autoresize);
        }
        public static NetDataWriter CreateWriter(byte msgid) {
            NetDataWriter ndw = new NetDataWriter();
            ndw.Put(msgid);
            return ndw;
        }
        public static NetDataWriter CreateWriter(byte msgid, int size, bool autoresize = false) {
            NetDataWriter ndw = new NetDataWriter(autoresize, size);
            ndw.Put(msgid);
            return ndw;
        }

        public static void Server_SendTo(in DeliveryMethod delivery, in NetDataWriter writer, in ScavPlayer plr) => Server_SendTo(delivery, writer, plr.playerId);
        public static void Server_SendTo(in DeliveryMethod delivery, in ReadOnlySpan<byte> data, in knetid clientid) {
#if !RELEASE
            {
                // 直接读取首字节校验 NetmsgId，消除每包临时对象分配
                Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(data);
                if (Net.IsClient) {
                    throw new Exception("CLIENT CANT DO THAT!!!!");
                }
            }
#endif
            if (clientid == Net.SERVER_CLIENTID) { // loopback to myself (the server)
                Net.InvokeClientMessage(Net.SERVER_CLIENTID, new NetDataReader(data.ToArray()));
                return;
            }
            Transport.Server_SendTo(delivery, data, clientid);
        }
        public static void Server_SendTo(in DeliveryMethod delivery, in NetDataWriter writer, in knetid clientid) {
            Server_SendTo(delivery, writer.AsReadOnlySpan(), clientid);
        }

        public static void Server_SendToClientsVeryReliable(in NetDataWriter writer, in IEnumerable<knetid> clientIds) {
            if (Net.TryGetSteamTransport(out TransportSteamworks tsteam)) {
                List<knetid> targets = new List<knetid>(clientIds);
                if (targets.Count == 0) {
                    return;
                }
                // 补齐 Steam 极可靠发送路径对 SERVER_CLIENTID 的本地回环分发，与 Server_SendToClients 保持一致
                if (targets.Remove(SERVER_CLIENTID)) {
                    Net.InvokeClientMessage(Net.SERVER_CLIENTID, new NetDataReader(writer));
                    if (targets.Count == 0) {
                        return;
                    }
                }
                tsteam.Server_SendToClients(Steamworks.Constants.k_nSteamNetworkingSend_Reliable, writer, targets);
            } else {
                Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds);
            }
        }

        public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in knetid clientIds) => Server_SendTo(delivery, writer, clientIds);
        public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in IReadOnlyList<ScavPlayer> clients) => Server_SendToClients(delivery, writer, clients.Select(x => x.playerId).ToList());
        public static void Server_SendToClients(in DeliveryMethod delivery, in NetDataWriter writer, in IEnumerable<knetid> clientIds) {
#if !RELEASE
            Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(writer.AsReadOnlySpan());
            if (Net.IsClient) {
                throw new Exception("CLIENT CANT DO THAT!!!!");
            }
#endif
            if (clientIds.Count() == 0) {
                return;
            }

            List<knetid> aaaa = new List<knetid>(clientIds);
            if (aaaa.Remove(SERVER_CLIENTID)) { // loopback to myself (the server)
                Net.InvokeClientMessage(Net.SERVER_CLIENTID, new NetDataReader(writer));

                if (aaaa.Count == 0) {
                    return;
                }
            }

            Transport.Server_SendToClients(delivery, writer.AsReadOnlySpan(), aaaa);
        }

        public static void Client_Send(in DeliveryMethod delivery, in ReadOnlySpan<byte> data) {
#if !RELEASE
            Con.ConFailIfNotConnected();
            {
                Net.CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(data);
                if (Net.IsDedicatedServer) {
                    throw new Exception("DEDICATED SERVER CANT DO THAT!!!!");
                }
            }
#endif
            if (Net.IsServer) { // loopback to myself (the server)
                Net.InvokeServerMessage(Net.SERVER_CLIENTID, new NetDataReader(data.ToArray()));
                return;
            }
            Transport.Client_Send(delivery, data);
        }
        public static void Client_Send(in DeliveryMethod delivery, in NetDataWriter writer) {
            Client_Send(delivery, writer.AsReadOnlySpan());
        }

#if !RELEASE
        public static void CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(in ReadOnlySpan<byte> data) {
            if (data.IsEmpty || Enum.GetName(typeof(NetmsgId), data[0]) == null) {
                byte msgId = data.IsEmpty ? (byte)0 : data[0];
                log.warn($"MESSAGE ID BROKE?????: msgid:{msgId}  FROM\n{new StackTrace()}");
            }
        }
        public static void CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(in NetDataWriter writer) {
            CheckWhatsTheMessageIdToMakeSureItDidntGetMessedUp(writer.AsReadOnlySpan());
        }
#endif
    }
}
