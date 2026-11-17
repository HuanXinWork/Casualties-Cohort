using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 联机协同小游戏会话与远端手部同步管理器。
/// </summary>
public partial class MinigameMPManager : CasualtiesTogetherSingleton {
    private static readonly Dictionary<Type, (FieldInfo limbField, FieldInfo toDestroyField)> TargetFieldCache =
        new Dictionary<Type, (FieldInfo, FieldInfo)>();

    public static readonly List<MinigameSession> active_sessions = new List<MinigameSession>();
    private static ushort _idCounter;

    private static readonly Dictionary<string, float> NoteUntil = new Dictionary<string, float>();

    public static double client_dislocation_mg_ignoretime = 0;
    public static bool client_i_know_my_minigame_session = false;

    private static byte _lastSentHandImage = 2;
    private static double _lastSentHandPos = 0;
    private static TwoWayDictionary<Type, byte> _minigameIds;

    private void OnGUI() {
        if (!log.verbose) {
            return;
        }

        Minigame curMg = MinigameBase.main?.currentMinigame;
        if (curMg is ShrapnelMinigame sm && sm.objects != null) {
            for (int i = 0; i < 5 && i < sm.objects.Count; i++) {
                RectTransform shrap = sm.objects[i];
                if (shrap == null) {
                    continue;
                }
                Vector3 pos = shrap.position;
                ScavPlayer owner = ShrapnelMinigameSession.client_shrapnel_owners[i];
                GUI.Label(new Rect(pos.x, Screen.height - pos.y, 200f, 20f), owner.ToSafeString());
            }
        }
    }

    internal static void Note(string why) {
        ModLog.Debug("[mg] " + (Net.IsServer ? "host " : "client ") + why, LogDomain.Medical);
    }

    internal static void NoteSlow(string why) {
        float now = Time.unscaledTime;
        int cut = why.LastIndexOf(" by ", StringComparison.Ordinal);
        string key = cut > 0 ? why.Substring(0, cut) : why;
        if (NoteUntil.TryGetValue(key, out float until) && now < until) {
            return;
        }

        NoteUntil[key] = now + 1f;
        Note(why);
    }

    public static MinigameSession Server_GetSessionForObject<T>(GameObject obj, byte minigameType, ScavPlayer requestingPlayer)
        where T : MinigameSession, new() {
        MinigameSession foundSess = active_sessions.FirstOrDefault(s => s.obj == obj && s.minigame_type == minigameType);
        if (foundSess == null) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS && obj != null) {
                DebugHelp.OnNetEvent(obj.transform.position, "S: GetSessionForObject, CREATING NEW SESSION");
            }

            foundSess = new T {
                obj = obj,
                session_id = _idCounter++,
                minigame_type = minigameType,
            };
            active_sessions.Add(foundSess);
            foundSess.Init();
        } else if (DebugHelp._DEV_VISUALISE_NET_EVENTS && obj != null) {
            DebugHelp.OnNetEvent(obj.transform.position, "S: GetSessionForObject, SESSION ALREADY EXISTS");
        }

        if (requestingPlayer != null) {
            if (foundSess.involved_players.Count != 0 && foundSess.involved_players.Count >= Multiplayer.rules.MinigameMaxPlayers) {
                Server_ForceEndMinigameForPlayer(requestingPlayer);
                requestingPlayer.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("minigame_deny_full"));
                return null;
            }
            foundSess.involved_players.Add(requestingPlayer);
            foundSess.all_players_that_ever_were_involved.Add(requestingPlayer);
            foundSess.OnPlayerJoin(requestingPlayer);
        }

        return foundSess;
    }

    public static MinigameSession GetSessionThisPlayerIsInvolvedIn(ScavPlayer plr) {
        if (plr == null) {
            return null;
        }
        return active_sessions.FirstOrDefault(
            session => session.involved_players.Contains(plr)
                && plr.IsInMinigame
                && plr.minigame_session == session.session_id
                && plr.minigame_current_type == session.minigame_type);
    }

    public static bool TryGetSessionThisPlayerIsInvolvedIn(ScavPlayer plr, out MinigameSession session) {
        session = GetSessionThisPlayerIsInvolvedIn(plr);
        return session != null;
    }

    public static bool TryGetSessionThisPlayerIsInvolvedIn<T>(ScavPlayer plr, out T session) where T : MinigameSession {
        if (GetSessionThisPlayerIsInvolvedIn(plr) is T typed) {
            session = typed;
            return true;
        }
        session = null;
        return false;
    }

    public static void Client_SendHandPos() {
        byte handSpriteIndex = GetLocalHandSpriteIndex();
        if (_lastSentHandImage != handSpriteIndex) {
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_MinigameHandSprite, handSpriteIndex, true);
            _lastSentHandImage = handSpriteIndex;
        }

        double now = Time.realtimeSinceStartupAsDouble;
        if (now - _lastSentHandPos > 0.06) {
            _lastSentHandPos = now;
            Vector2 mousePos = GetLocalMousePosMinigameSpace();
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_MinigameHandpos, Minigame.game.handPos, mousePos, false);
        }
    }

    public static Vector2 GetLocalMousePosMinigameSpace() {
        Vector2 vector = Input.mousePosition;
        vector -= (Vector2)MinigameBase.main.minigameScreen.transform.position;
        vector /= PlayerCamera.uiScale;
        KM.v2clampSquare(ref vector, 524f);
        return vector;
    }

    public static byte GetLocalHandSpriteIndex() {
        if (MinigameBase.main?.currentMinigame != null && MinigameBase.main.handTransform != null) {
            Image image = MinigameBase.main.handTransform.GetComponent<Image>();
            int idx = image != null ? Array.IndexOf(MinigameBase.main.handSprites, image.sprite) : -1;
            if (idx >= 0) {
                return (byte)idx;
            }
        }
        return 2;
    }

    public static bool Server_ValidateHandSprite(byte index, NetBody npc) {
        if (MinigameBase.main == null || npc == null || npc.body == null) {
            return false;
        }
        if (index >= MinigameBase.main.handSprites.Length) {
            return false;
        }

        // 断手固定对应贴图下标 10，非断手禁止使用断手贴图
        bool handMissing = npc.body.limbs[(int)LimbIndex.PrimaryHand].dismembered;
        return handMissing ? index == 10 : index != 10;
    }

    public static AnyObjectNetId GetMinigameTargetObjectNetId(Minigame mg) {
        if (mg == null) {
            return new AnyObjectNetId();
        }
        Type mgType = mg.GetType();
        if (!TargetFieldCache.TryGetValue(mgType, out var fields)) {
            fields = (AccessTools.Field(mgType, "limb"), AccessTools.Field(mgType, "toDestroy"));
            TargetFieldCache[mgType] = fields;
        }

        Limb limb = fields.limbField?.GetValue(mg) as Limb;
        BuildingEntity building = fields.toDestroyField?.GetValue(mg) as BuildingEntity;

        if (limb != null && limb.TryGetNetBody(out _)) {
            return new AnyObjectNetId(limb);
        }
        if (building != null && building.TryGetTracker(out SyncInfoGameObjectTracker tracker)) {
            return new AnyObjectNetId(tracker);
        }
        return new AnyObjectNetId();
    }

    public static Type GetMinigameTypeFromIdNullable(byte mg) {
        if (_minigameIds != null && _minigameIds.TryGet(mg, out Type minigame)) {
            return minigame;
        }
        return null;
    }

    public static bool TryGetMinigameTypeFromId(byte mg, out Type minigame) {
        if (_minigameIds == null) {
            minigame = null;
            return false;
        }
        return _minigameIds.TryGet(mg, out minigame);
    }

    public static byte GetMinigameTypeId(Type mg) {
        return _minigameIds[mg];
    }

    private void Awake() {
        _minigameIds = GenerateInheritedClassesIdDict(typeof(Minigame));
        client_dislocation_mg_ignoretime = Time.realtimeSinceStartupAsDouble;
    }

    private void CleanUnusedSessions() {
        active_sessions.RemoveAll(sess => {
            if (sess.object_required && sess.obj == null) {
                Server_EndMinigame(sess, false);
                Note("session " + sess.session_id + " dropped, target gone");
                return true;
            }
            if (sess.involved_players.Count > 0) {
                return false;
            }
            Note("session " + sess.session_id + " dropped, nobody left");
            return true;
        });
    }

    private void Update() {
        if (Util.IsInWorld() && MinigameBase.main?.currentMinigame && client_i_know_my_minigame_session) {
            Client_SendHandPos();
        }

        if (Net.IsServer) {
            CleanUnusedSessions();
            for (int i = 0; i < active_sessions.Count; i++) {
                active_sessions[i].Update();
            }
        }
    }

    private void FixedUpdate() {
        if (!Net.IsServer) {
            return;
        }

        for (int i = 0; i < active_sessions.Count; i++) {
            MinigameSession sess = active_sessions[i];
            if (sess.obj != null) {
                foreach (ScavPlayer plr in sess.involved_players) {
                    if (!KM.DistanceCheck(plr.pos, sess.obj.transform.position, sess.maxdist) && plr.IsInMinigame) {
                        Server_ForceEndMinigameForPlayer(plr);
                        break;
                    }
                }
            } else if (sess.object_required) {
                Server_EndMinigame(sess, false);
                continue;
            }

            sess.FixedUpdate();
        }
    }

    public static TwoWayDictionary<Type, byte> GenerateInheritedClassesIdDict(Type baseType) {
        List<Type> inheritedClasses = AppDomain.CurrentDomain.GetAssemblies()
            .SelectMany(a => {
                try {
                    return a.GetTypes();
                } catch (ReflectionTypeLoadException ex) {
                    return ex.Types.Where(t => t != null);
                }
            })
            .Where(t => {
                try {
                    return t.IsClass && !t.IsAbstract && baseType.IsAssignableFrom(t);
                } catch (Exception ex) {
                    ModLog.Debug($"跳过无法解析基类的小游戏类型: {ex.Message}", LogDomain.Medical);
                    return false;
                }
            })
            .ToList();

        inheritedClasses.Sort((a, b) => string.Compare(a.Name, b.Name, StringComparison.Ordinal));

        var dict = new TwoWayDictionary<Type, byte>();
        byte id = 0;
        for (int i = 0; i < inheritedClasses.Count; i++) {
            dict.Add(inheritedClasses[i], id++);
        }
        return dict;
    }

    public static void Server_EndMinigame(MinigameSession sess, bool actuallyDeleteSession) {
        if (sess == null || sess.minigame_ended) {
            return;
        }
        sess.minigame_ended = true;
        Note("end session " + sess.session_id + " type " + sess.minigame_type + (actuallyDeleteSession ? " delete" : " keep"));

        var list = new List<knetid>(sess.involved_players.Count);
        foreach (ScavPlayer plr in sess.involved_players) {
            list.Add(plr.playerId);
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameForceEnd);
        writer.Put(sess.session_id);
        writer.Put(sess.minigame_type);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: list);

        if (actuallyDeleteSession) {
            active_sessions.Remove(sess);
        }
    }

    public static void Server_PlayerExitedMinigame(ScavPlayer plr) {
        if (plr == null) {
            return;
        }
        plr.minigame_current_type = 255;
        plr.minigame_currentItem = null;
        plr.minigame_targetobject = new AnyObjectNetId();

        for (int i = 0; i < active_sessions.Count; i++) {
            active_sessions[i].involved_players.Remove(plr);
        }
    }

    public static void Server_ForceEndMinigameForPlayer(ScavPlayer plr) {
        if (plr == null) {
            return;
        }
        Note("force end " + plr.MinigameType + " session " + plr.minigame_session);
        Server_PlayerExitedMinigame(plr);
        Multiplayer.Server_SendSimpleMessageToOneClient((byte)NetmsgId.CLIENT_MinigameForceEnd, plr.playerId, (knetid)0, true);
    }

    public static void Server_SendCurrentInvolved(ScavPlayer plr, MinigameSession session) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameInvolvedPlayers);
        var plrIds = new HashSet<knetid> { plr.playerId };

        if (session == null) {
            writer.Put(_idCounter++);
            writer.Put((byte)0);
            writer.Put((ushort)1);
            writer.Put(plr.playerId);
            writer.Put((byte)2);
        } else {
            writer.Put(session.session_id);
            writer.Put(session.minigame_type);

            var allPlrs = new List<ScavPlayer>(session.involved_players.Count);
            foreach (ScavPlayer iplr in session.involved_players) {
                allPlrs.Add(iplr);
                plrIds.Add(iplr.playerId);
            }

            // 预先按单包上限裁剪写入条目数，确保计数头与实际写入玩家条目 100% 对称不越界
            int maxEntries = (NetTimingConstants.UnreliableHealthBatchMaxBytes - 16) / 3;
            int writeCount = Mathf.Min(allPlrs.Count, maxEntries);
            writer.Put((ushort)writeCount);
            for (int i = 0; i < writeCount; i++) {
                ScavPlayer item = allPlrs[i];
                writer.Put(item.playerId);
                writer.Put(item.minigame_handsprite);
            }
        }

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: plrIds.ToList());
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameInvolvedPlayers, false)]
    private static void ClientReceiver_MinigameInvolvedPlayers(knetid _, NetDataReader reader) {
        if (Minigame.game == null || Minigame.game.currentMinigame == null) {
            return;
        }

        client_i_know_my_minigame_session = true;
        reader.Get(out ushort mySessionId);
        reader.Get(out byte minigameType);
        reader.Get(out ushort otherPlrsCount);

        ScavPlayer localPlr = ScavPlayer.LocalPlayer;
        if (localPlr == null) {
            return;
        }
        localPlr.minigame_session = mySessionId;

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(localPlr.pos, $"C: involved plrs count: {otherPlrsCount}");
        }

        Client_DeleteAllRemoteHands();

        for (int i = 0; i < otherPlrsCount; i++) {
            reader.Get(out knetid involvedPlr);
            reader.Get(out byte theirHandSprite);

            if (ScavPlayer.TryGetPlayerFromClientId(involvedPlr, out ScavPlayer plr) && plr != localPlr) {
                plr.minigame_session = mySessionId;
                plr.minigame_current_type = minigameType;
                plr.minigame_handsprite = theirHandSprite;
                Client_CreateARemoteHand(plr);
            }
        }
    }

    private sealed class MinigameRemotePeerHand : MonoBehaviour {
        public static readonly List<MinigameRemotePeerHand> all_instances = new List<MinigameRemotePeerHand>();
        private void OnDestroy() => all_instances.Remove(this);

        public Vector2 handVelocity = Vector2.zero;
        public ScavPlayer plr;
        public RectTransform nametagTransform;
        public RectTransform handTransform;
        public Image handSprite;

        private void Awake() {
            handTransform = GetComponent<RectTransform>();
            handSprite = GetComponent<Image>();
            all_instances.Add(this);
        }

        private void Update() {
            if (plr == null || MinigameBase.main == null) {
                Destroy(gameObject);
                return;
            }
            float delta = Time.deltaTime;
            Vector2 targetMousePos = plr.minigame_mousepos;

            float maxLength = 85f;
            float responsiveness = 0.25f;
            float accel = 5f;
            if (plr.body != null) {
                accel += plr.body.skills.STRFrom10 * 0.3f;
                accel *= plr.body.consciousness * 0.01f;
                responsiveness *= plr.body.consciousness * 0.01f;
            }

            Vector2 desiredVel = Vector2.ClampMagnitude((targetMousePos - handTransform.anchoredPosition) * responsiveness, maxLength);
            handVelocity = Vector2.Lerp(handVelocity, desiredVel, accel * delta);
            handVelocity = Vector2.Lerp(handVelocity, Vector2.zero, delta * 1.5f);

            handTransform.anchoredPosition += handVelocity * delta * 120f;
            handTransform.anchoredPosition = Vector2.Lerp(handTransform.anchoredPosition, plr.minigame_handpos, delta);
            handTransform.eulerAngles = new Vector3(0f, 0f, handVelocity.x * 0.5f);
            handTransform.localScale = new Vector3(
                plr.body?.handSlot == AnatomyConstants.PrimaryHandSlot ? 1f : -1f,
                1f,
                1f);

            if (plr.minigame_handsprite < MinigameBase.main.handSprites.Length) {
                handSprite.sprite = MinigameBase.main.handSprites[plr.minigame_handsprite];
            }
            if (nametagTransform != null) {
                nametagTransform.rotation = Quaternion.identity;
            }
        }

        private void Start() {
            if (plr != null && plr.body != null) {
                UIManager.CreateLabel(handTransform, "RemoteHandPlayerName", null, null, out _, out TextMeshProUGUI text, out RectTransform rt);
                rt.sizeDelta *= 4f;
                text.text = plr.playerName;
                text.color = plr.playerColor;
                rt.pivot = Vector2.zero;
                rt.sizeDelta = new Vector2(text.preferredWidth, text.preferredHeight);
                rt.anchorMin = new Vector2(0f, 0.74f);
                rt.anchorMax = new Vector2(0.38f, 1.0f);
                text.richText = false;

                CharStatusVisuals.TryMakeNametagFancy(text, plr);
                CharStatusVisuals.FancyNametagUpdateColor(text, plr.playerColor);
                nametagTransform = rt;
            }
        }
    }

    public static void Client_DeleteAllRemoteHands() {
        for (int i = MinigameRemotePeerHand.all_instances.Count - 1; i >= 0; i--) {
            MinigameRemotePeerHand hand = MinigameRemotePeerHand.all_instances[i];
            if (hand != null && hand.gameObject != null) {
                Destroy(hand.gameObject);
            }
        }
    }

    private static void Client_CreateARemoteHand(ScavPlayer ownerPlayer) {
        if (MinigameBase.main == null || MinigameBase.main.handTransform == null) {
            return;
        }
        GameObject go = Instantiate(MinigameBase.main.handTransform.gameObject);
        MinigameRemotePeerHand remoteHand = go.GetOrAddComponent<MinigameRemotePeerHand>();
        remoteHand.plr = ownerPlayer;
        remoteHand.handTransform.SetParent(MinigameBase.main.handTransform.parent);
        remoteHand.handTransform.localScale = MinigameBase.main.handTransform.localScale;
        remoteHand.handSprite.color = new Color(1f, 1f, 1f, 0.3f);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_MinigameHandSprite)]
    private static void ServerReceiver_MinigameHandSprite(knetid clientId, NetDataReader reader) {
        reader.Get(out byte sprIndex);
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)
            || pb.body == null || !pb.body.alive) {
            return;
        }

        if (!Server_ValidateHandSprite(sprIndex, pb)) {
            ModLog.Warn($"小游戏手部贴图下标越界或与断肢状态不符，已回退默认值: client={clientId.id}, index={sprIndex}", LogDomain.Medical);
            sprIndex = 2;
        }

        MinigameSession sess = active_sessions.FirstOrDefault(s => s.involved_players.Contains(plr) && plr.IsInMinigame);
        if (sess != null) {
            plr.minigame_handsprite = sprIndex;
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameHandSprite_Relay);
            writer.Put(clientId);
            writer.Put(sprIndex);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: sess.InvolvedPlayersClientIds());
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_MinigameHandpos)]
    private static void ServerReceiver_MinigameHandpos(knetid clientId, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        reader.Get(out Vector2 mousePos);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)
            || pb.body == null || !pb.body.alive || !Util.IsFinite(pos) || !Util.IsFinite(mousePos)) {
            return;
        }

        KM.v2clampSquare(ref mousePos, 524f);
        plr.minigame_handpos = pos;
        plr.minigame_mousepos = mousePos;

        MinigameSession sess = active_sessions.FirstOrDefault(s => s.involved_players.Contains(plr) && plr.IsInMinigame);
        if (sess != null) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_MinigameHandpos_Relay);
            writer.Put(clientId);
            writer.Put(pos);
            writer.Put(mousePos);
            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: sess.InvolvedPlayersClientIds());
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameHandpos_Relay, false)]
    private static void ClientReceiver_MinigameHandposRelay(knetid _, NetDataReader reader) {
        reader.Get(out knetid clientId);
        reader.Get(out Vector2 pos);
        reader.Get(out Vector2 mousePos);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb) && !pb.IsLocal) {
            plr.minigame_handpos = pos;
            plr.minigame_mousepos = mousePos;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameHandSprite_Relay, false)]
    private static void ClientReceiver_MinigameHandSpriteRelay(knetid _, NetDataReader reader) {
        reader.Get(out knetid clientId);
        reader.Get(out byte spr);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb) && !pb.IsLocal) {
            plr.minigame_handsprite = spr;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MinigameForceEnd, false)]
    private static void ClientReceiver_MinigameForceEnd(knetid _, NetDataReader reader) {
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS && ScavPlayer.LocalPlayer != null) {
            DebugHelp.OnNetEvent(ScavPlayer.LocalPlayer.pos, "C: Server told me to stop the minigame.");
        }
        MinigameBase.main?.EndMinigame();
        Note("server closed the minigame");
    }

    [ServerReceiver((byte)NetmsgId.SERVER_MinigameStart)]
    private static void ServerReceiver_MinigameStart(knetid clientId, NetDataReader reader) {
        reader.Get(out byte mType);
        reader.Get(out knetid currentItemSyncId);
        reader.Get(out AnyObjectNetId objNet);

        if (_minigameIds == null
            || !_minigameIds.TryGet(mType, out Type realType)
            || !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)
            || pb.body == null) {
            return;
        }

        Server_PlayerExitedMinigame(plr);
        plr.minigame_current_type = mType;

        if (ItemSync.TryGetItemSyncInfo(currentItemSyncId, out SyncInfo curItemSi)) {
            if (!ItemSync.CheckIfBodyReachThisItem(curItemSi, pb.body)) {
                ModLog.Warn($"{plr} 尝试在不可达距离使用小游戏物品 {curItemSi}", LogDomain.Medical);
            }
            plr.minigame_currentItem = curItemSi.item;
            if (curItemSi.IsAED() && curItemSi.item.battery != null) {
                curItemSi.item.battery.DrainCharge(0.01f);
                ServerMain.Server_AnnounceSound(curItemSi.transformPosition, "aedstart", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));
            }
        } else {
            plr.minigame_currentItem = null;
        }

        MinigameSession mgSess = Server_CreateMinigameSession(plr, realType, mType, objNet);
        if (mgSess != null) {
            plr.minigame_session = mgSess.session_id;
            Server_SendCurrentInvolved(plr, mgSess);
        }
    }

    private static void Server_DenyMinigame(ScavPlayer plr) {
        Note("deny " + plr.MinigameType);
        Server_ForceEndMinigameForPlayer(plr);
        plr.Server_DoAlertSingle(Lang.MarkMsgAsLocaleKey("minigame_deny_server"));
    }

    private static MinigameSession Server_CreateMinigameSession(ScavPlayer plr, Type realType, byte realTypeId, AnyObjectNetId objNet) {
        MinigameSession mgSess = null;
        NetBody pb = plr.netBody;

        bool isJustCpr = realType == typeof(CPRMinigame);
        if (realType == typeof(AEDMinigame) || isJustCpr) {
            if (!objNet.TryGetNetBody(out NetBody targetPb)
                || !KM.DistanceCheck(pb.body, targetPb.body, GameplayRanges.ServerExchangeToleranceDistance)
                || (isJustCpr && !CPRHandler.IsCPRBeingPerformedOnThisBody_IsThisGuyOrNobody(targetPb.body, plr))) {
                Server_DenyMinigame(plr);
                return null;
            }
        } else if (realType == typeof(ShrapnelMinigame) || realType == typeof(DislocationMinigame)) {
            if (objNet.TryGetNetBodyAndLimb(out NetBody targetPb, out Limb targetLimb)
                && KM.DistanceCheck(pb.body, targetPb.body, GameplayRanges.ServerExchangeToleranceDistance)) {
                if (realType == typeof(ShrapnelMinigame) && targetLimb.shrapnel > 0) {
                    mgSess = Server_GetSessionForObject<ShrapnelMinigameSession>(targetLimb.gameObject, realTypeId, plr);
                } else if (realType == typeof(DislocationMinigame) && targetLimb.dislocated) {
                    mgSess = Server_GetSessionForObject<DislocationMinigameSession>(targetLimb.gameObject, realTypeId, plr);
                } else {
                    Server_DenyMinigame(plr);
                    return null;
                }
            } else {
                Server_DenyMinigame(plr);
                return null;
            }
        } else if (realType == typeof(LockpingMinigame) || realType == typeof(KeypadMinigame)) {
            if (objNet.TryGetSyncInfo(out SyncInfo si)
                && si.go != null
                && si.go.TryGetComponent(out Openable openable)
                && KM.DistanceCheck(pb.body, si.go.transform, GameplayRanges.ServerExchangeToleranceDistance)) {
                if (realType == typeof(LockpingMinigame) && !openable.isKeypad) {
                    // 撬锁不建立共享会话
                } else if (realType == typeof(KeypadMinigame) && openable.isKeypad) {
                    mgSess = Server_GetSessionForObject<KeypadMinigameSession>(si.go, realTypeId, plr);
                } else {
                    Server_DenyMinigame(plr);
                    return null;
                }
            } else {
                Server_DenyMinigame(plr);
                return null;
            }
        }

        plr.minigame_targetobject = objNet;
        return mgSess;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_MinigameEnd)]
    private static void ServerReceiver_MinigameEnd(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)) {
            return;
        }
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(plr.pos, $"S: MinigameEnd {pb}");
        }

        MinigameSession sess = GetSessionThisPlayerIsInvolvedIn(plr);
        Server_PlayerExitedMinigame(plr);
        if (sess != null) {
            Server_SendCurrentInvolved(plr, sess);
        }
    }
}
