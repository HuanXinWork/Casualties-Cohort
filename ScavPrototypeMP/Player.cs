using CasualtiesTogetherUtils;
using LiteEntitySystem;
using LiteNetLib.Utils;
using System;
using System.Collections.Generic;
using Together.Api;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Events;

namespace Together {

    public class Server_PlayerState {

        public int didmutewarn;

        public int fps = 60;
        public int tps = 60;

        public bool admin_privilege = false;

        public float layer_transition_x_offset;

        public double last_cursormove = 0;

        public bool loaded_save = false;
        public bool did_give_spawn_location = false;
        public bool did_give_spawn_location_from_a_save = false;
        public bool finished_worldgen = false;
        public bool is_loaded_in = false; // this is determined by the type of packets that are sent by the client, that are guranteed to be sent only after world generated
        public bool[,] known_chunks;

        Dictionary<string, double> cooldowns = new();

        public struct LocSnapshot {
            public bool alive;
            public Vector2_4byte_512 pos;
        }

        public List<LocSnapshot> cur_location_history;
        public List<List<LocSnapshot>> location_history = new();

        public void SaveLocationSnapshot() {
            if (this.cur_location_history == null) {
                this.cur_location_history = new();
                this.location_history.Add(this.cur_location_history);
            }

            if (this.cur_location_history.Count > 400) {
                this.cur_location_history.RemoveAt(UnityEngine.Random.Range(1, 399));
                this.cur_location_history.RemoveAt(UnityEngine.Random.Range(1, 398));
            }

            this.cur_location_history.Add(new Server_PlayerState.LocSnapshot {
                alive = plr.IsAlive(),
                pos = plr.pos
            });
        }

        public ScavPlayer plr;

        public Server_PlayerState(ScavPlayer p) {
            plr = p;
            OnWorldGenerate();
            OnSceneChangeOrWorldStartGenerate();
        }
        public void ResetKnownChunks() {
            known_chunks = new bool[WorldChunkSync.SYNCCHUNK_TILESIZE, WorldChunkSync.SYNCCHUNK_TILESIZE];

            // host knows about everything
            if (plr.IsLocal) {
                for (int y = 0; y < WorldChunkSync.SYNCCHUNK_TILESIZE; y++) {
                    for (int x = 0; x < WorldChunkSync.SYNCCHUNK_TILESIZE; x++) {
                        known_chunks[x, y] = true;
                    }
                }
            }
        }

        // only called right before WorldGeneration.FinishWorldGeneration
        public void OnWorldGenerate() {
            ResetKnownChunks();
        }

        public void OnWorldgenStart() {
            ResetKnownChunks();
            did_give_spawn_location = false;
            finished_worldgen = false;
        }
        // only called in OnSceneLoaded and WorldGeneration.GenerateWorld
        public void OnSceneChangeOrWorldStartGenerate() {
            did_give_spawn_location = false;
            is_loaded_in = false;
            loaded_save = false;
        }

        public bool Cooldown(in string id, float cooldown) {
            if (cooldowns.TryGetValue(id, out double cd)) {
                double diff = Time.realtimeSinceStartupAsDouble - cd;
                if (diff < cooldown) {
                    if (log.verbose) {
                        log.serverdeny($"COOLDOWN, TOO FAST, {plr}, req: {cooldown} cur: {diff} ");
                    }

                    return false;
                }
            }
            cooldowns[id] = Time.realtimeSinceStartupAsDouble;
            return true;
        }
    }


    public partial class ScavPlayer : MonoBehaviour {

        public readonly Dictionary<string, object> CUSTOM_LOCAL_DATA = new();
        public BasePlayer BasePlayer { get; internal set; }

        public Server_PlayerState ServerPlayerState = null;

        public ulong SteamId;
        public Texture2D IconLarge;
        public Texture2D IconMedium;
        public Texture2D IconSmall;
        public Texture2D IconAnyLargeToSmall => IconLarge ?? IconMedium ?? IconSmall;
        public Texture2D IconAnySmallToLarge => IconSmall ?? IconMedium ?? IconLarge;

        public Sprite IconLargeSprite;
        public Sprite IconMediumSprite;
        public Sprite IconSmallSprite;

        // 三个尺寸可能指向同一个 Sprite，且底层 Texture2D 属于共享头像缓存，仅安全销毁去重后的 Sprite 实例
        void DestroyIconSprites() {
            HashSet<Sprite> sprites = new HashSet<Sprite>();
            sprites.Add(IconLargeSprite);
            sprites.Add(IconMediumSprite);
            sprites.Add(IconSmallSprite);
            sprites.Remove(null);
            foreach (Sprite sprite in sprites) {
                UnityEngine.Object.Destroy(sprite);
            }

            IconLargeSprite = null;
            IconMediumSprite = null;
            IconSmallSprite = null;
        }

        public List<Sprite> KnownUserTagIcons = new();

        public knetid playerId { get; internal set; }
        public bool IsLocal { get; internal set; }
        public bool IsHost { get; internal set; }

        // fullgame prerun pick. stamped during CharacterSelectWait, consumed on spawn (phase 3).
        public byte selectedCharacter;

        public Vector2 CursorWorldPos { get { return BasePlayer.LookPos.Value; } set { BasePlayer.LookPos.Value = value; } }
        public Vector2 CursorScreenPos => BasePlayer.CursorPos.InterpolatedValue;
        public Vector2 CameraViewPos => BasePlayer.CameraPos.Value;

        internal VoicechatOutput vc_output { get; private set; }

        #region STATIC_SHIT

        public static event Action<ScavPlayer> OnPlayerJoined;
        public static event Action<ScavPlayer> OnPlayerLeft;
        public static UnityEvent<ScavPlayer> OnPlayerJoinedUE = new();
        public static UnityEvent<ScavPlayer> OnPlayerLeftUE = new();

        public static Dictionary<Body, ScavPlayer> BodyToPlayerDict = new Dictionary<Body, ScavPlayer>();
        public static Dictionary<knetid, ScavPlayer> ClientIdToPlayerDict = new Dictionary<knetid, ScavPlayer>();
        public static IEnumerable<ScavPlayer> AllPlayers => ClientIdToPlayerDict.Values;

        public static List<ScavPlayer> AllLivingPlayers = new();
        public static List<ScavPlayer> AllDeadPlayers = new();

        public static ScavPlayer LocalPlayer;

        #endregion

        #region NetPlayer_SafeBodyGetters

        public bool IsAlive() {
            if (body == null) {
                return false;
            }
            return body.alive;
        }
        public bool IsUnchipped() {
            if (body == null) {
                return unchipped;
            }
            if (netBody == null) {
                return unchipped;
            }
            return netBody.unchipped;
        }
        public bool ImpairedSpeech() {
            if (body == null) {
                return false;
            }
            return body.alive && body.talker.impairedSpeech;
        }
        public bool IsAliveAndNotCriticallyDying() {
            if (body == null) {
                return false;
            }
            return (body.alive && !body.isCriticallyDying);
        }
        public bool IsDeadOrCriticallyDying() {
            if (body == null) {
                return false;
            }
            return (body.IsDeadOrCriticallyDying());
        }
        public bool IsConscious() {
            if (body == null) {
                return false;
            }
            return body.conscious;
        }
        public bool IsDead() {
            if (body != null) {
                return !body.alive;
            }
            return false;
        }
        public bool IsAliveAndMindwiped() {
            if (IsAlive()) {
                return body.IsMindwiped();
            }
            return false;
        }
        public bool IsAliveAndNotConscious() {
            if (body != null && body.alive) {
                return !body.conscious;
            }
            return false;
        }
        public bool IsAliveAndUnconsciousAndNotSleeping() {
            if (body != null && body.alive) {
                return !body.conscious && !body.sleeping;
            }
            return false;
        }

        #endregion

        public bool LateJoin = false;
        public bool unchipped;
        public double levelPlayTime = 0;
        public CoUtils CoUtilsInstance;
        public UnityEvent OnNameChange = new();

        public bool IsInMinigame => minigame_current_type != 255;
        public SyncInfo minigame_currentItem_syncinfo {
            get {
                if (ItemSync.TryGetItem(BasePlayer.MinigameCurrentItemId.Value, out SyncInfo si, out Item _)) {
                    return si;
                }
                return null;
            }
            set {
                BasePlayer.MinigameCurrentItemId.Value = value.syncId;
            }
        }
        public Item minigame_currentItem {
            get {
                if (ItemSync.TryGetItem(BasePlayer.MinigameCurrentItemId.Value, out SyncInfo _, out Item item)) {
                    return item;
                }
                return null;
            }
            set {
                if (value == null || NetObjectRegistry.ObjectShouldNotBeSynced(value.gameObject)) {
                    BasePlayer.MinigameCurrentItemId.Value = 0;
                    return;
                }
                if (Net.IsServer) {
                    SyncInfo si = NetObjectRegistry.Server_EnsureItemIsNetworkRegistered(value.gameObject);
                    if (si != null) {
                        BasePlayer.MinigameCurrentItemId.Value = si.syncId;
                    }
                }
            }
        }

        public knetid DragItemId;
        public AnyObjectNetId minigame_targetobject { get { return BasePlayer.MinigameTargetObjectId.Value; } set { BasePlayer.MinigameTargetObjectId.Value = value; } }
        public ushort minigame_session { get { return BasePlayer.MinigameSessionId.Value; } set { BasePlayer.MinigameSessionId.Value = value; } }
        public Type MinigameType => MinigameMPManager.GetMinigameTypeFromIdNullable(minigame_current_type);
        public byte minigame_current_type { get { return BasePlayer.MinigameTypeId.Value; } set { BasePlayer.MinigameTypeId.Value = value; } } // 255 = none, other numbers are from the minigame_ids dict
        public Vector2 minigame_handpos = Vector2.zero;
        public Vector2 minigame_mousepos = Vector2.zero;
        public byte minigame_handsprite = 2;

        public bool IsInPauseMenu = false;
        public bool IsInConsole = false;
        public bool IsAltTab = false;
        public bool IsChatFocused = false;
        public bool IsInCraftingMenu = false;
        public bool IsInTradingMenu = false;
        public bool IsInInventory = false;
        public ushort WoundviewTargetNetBodyId = 0;

        public bool ButtonAttack;
        public bool ButtonIteminteract;

        public bool server_mute_vc { get { return BasePlayer.MuteVC.Value; } set { BasePlayer.MuteVC.Value = value; } }
        public bool server_mute_tc { get { return BasePlayer.MuteTC.Value; } set { BasePlayer.MuteTC.Value = value; } }

        public Vector2 pos {
            get {
                if (IsAlive()) {
                    return netBody.position;
                }
                return CameraViewPos;
            }
            set {
                if (body != null) {
                    netBody.SetBodyPosition(value);
                }
            }
        }

        public const string OBJNAMEPREFIX_PLR = "PlayerObject_";
        public const string OBJNAMEPREFIX_NAMETAG = "PlayerNameTag_";

        public bool nameIsCustom = false;
        public string playerName = "ERROR-NO-NAME";

        public bool _tutorial_finished = false;

        [Obsolete("probably?")]
        public void OnFinishTutorial(bool tp = true) {
            if (!_tutorial_finished) {
                _tutorial_finished = true;
                Chat.Server_ChatAnnouncement($"<plr:{playerId}> <key:plr_finished_tutorial>");
            }
        }

        public bool finishedLayer = false;
        // 抵达层尾出口线后保持闩锁防抖，直至玩家重新爬离出口阈值才复位
        public bool layerExitLatched = false;

        public double ping = -0.01;
        public double rtt_in_seconds_with_margin => ping * 4.1;
        public int ping_as_ms => (int)(ping * 1000);

        public GameObject locationPingCircle = null;
        public GameObject locationPingArrow = null;

        public GameObject chara => body.transform.parent.gameObject;
        public Body body;
        public NetBody netBody => body != null ? body.GetNetBody() : null;
        public bool TryGetNetBody(out NetBody pb) {
            if (this == null) {
                pb = null;
                return false;
            }
            if (body != null) {
                return body.TryGetNetBody(out pb);
            }
            pb = null;
            return false;
        }

        [Obsolete("Use NetBody.TryGetLocalNetBody")]
        public static bool TryGetLocalNetBody(out NetBody nb) {
            return NetBody.TryGetLocalNetBody(out nb);
        }
        [Obsolete("Use NetBody.GetLocalNetBodyNullable")]
        public static NetBody GetLocalNetBodyNullable() {
            return NetBody.GetLocalNetBodyNullable();
        }

        public override string ToString() {
            if (this == null) {
                return "PLR(NULL)";
            }
            if (SteamId != 0 && Net.IsRunningSteam) {
                return $"PLR(ID:{playerId}, name:{playerName}, SteamID: {SteamId})";
            }
            return $"PLR(ID:{playerId}, name:{playerName})";
        }

        private void FixedUpdate() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Players);

            if (Net.IsServer) {
                if (Util.IsInWorld()) {
                    if (Util.IsWorldGenerated()) {
                        if (body != null && !body.IsBodyLocal()) {
                            //OG: PlayerCamera.FixedUpdate V7.0.1
                            if (ItemSync.TryGetItem(DragItemId, out SyncInfo _, out Item dragItem) && dragItem.rb.simulated && Vector2.Distance(dragItem.transform.position, body.transform.position) < 10f) {
                                dragItem.rb.velocity *= 0.85f;
                                dragItem.rb.angularVelocity *= 0.85f;
                            }
                        }
                    } else {
                        ServerPlayerState.is_loaded_in = false;
                    }
                }
            }

            if (Util.IsWorldGenerated() && (IsLocal || Net.IsClient || ServerPlayerState.is_loaded_in)) {
                levelPlayTime += Time.fixedUnscaledDeltaTime;
            } else {
                levelPlayTime = 0;
            }
        }

        void Awake() {
            DontDestroyOnLoad(this.gameObject);

            if (Net.IsServer) {
                ServerPlayerState = new Server_PlayerState(this);
            }
        }

        void Start() {
            {
                {
                    locationPingCircle = new GameObject("fingerpointercircle");
                    SpriteRenderer spr = locationPingCircle.AddComponent<SpriteRenderer>();
                    spr.sprite = CoopModAssets.circlething;
                    spr.color = new Color(0, 0, 0, 0);
                    spr.sortingOrder = 6000;
                    DontDestroyOnLoad(locationPingCircle);
                    locationPingCircle.SetActive(false);
                }
                {
                    locationPingArrow = new GameObject("fingerpointerarrow");
                    SpriteRenderer spr = locationPingArrow.AddComponent<SpriteRenderer>();
                    spr.sprite = CoopModAssets.arrowicon;
                    spr.color = new Color(0, 0, 0, 0);
                    spr.sortingOrder = 6000;
                    locationPingArrow.transform.localScale = Vector3.one * 8f;
                    DontDestroyOnLoad(locationPingArrow);
                    locationPingArrow.SetActive(false);
                }
            }

            bool IS_STEAM = Net.Transport != null && Net.Transport is TransportSteamworks;

            if (IS_STEAM) {
                KSteam.LoadPlayerProfilePics(this);
            }
            KnownPersons.LoadTagsFor(this);

            {
                if (Net.IsServer) {
                    if (!IsLocal) {
                        try {
                            ServerMain._Server_OnServerNameChange(new List<knetid> { playerId });
                        } catch (Exception e) {
                            log.error(e.ToString());
                        }
                    }

                    if (!CheckIfPlrColorIsValid(playerColor)) {
                        playerColor = PickNewPlayerColor();
                    }

                    name = ScavPlayer.OBJNAMEPREFIX_PLR + playerName;

                    string msg = $"{Chat.AddColorTags(playerColor, playerName)}<parsebreak> {Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:plr_joined>")}";

                    int newcount = ScavPlayer.ClientIdToPlayerDict.Count + 1;
                    if (Multiplayer.rules.AutoContinue && !Util.IsInWorld() && newcount <= Multiplayer.rules.AutoMinPlrsToStart) {
                        msg += $" {newcount}/{Multiplayer.rules.AutoMinPlrsToStart} <key:auto_minimum_to_start>";
                    }

                    Chat.Server_ChatAnnouncement(msg);

                    if (Net.IsServer) { // try to find their old color
                        string pid = this.GetPersistentId();
                        if (ServerMain.Server_LastPlayerStates.TryGetValue(pid, out PlayerSavedState val)) {
                            playerColor = val.plrcolor;
                        }
                    }
                }

                ScavPlayer.ClientIdToPlayerDict[this.playerId] = this;
                vc_output = this.GetOrAddComponent<VoicechatOutput>();

                if (Util.IsInWorld()) {
                    LateJoin = true;
                }

                if (IsLocal) {
                    vc_output.enabled = false;
                    CoUtilsInstance = CoUtilsMultiplayer.GetLocalCoUtilsInstance();

                    if (!IS_STEAM) {
                        playerName = UIMainMenu.USERINPUT_NAME;
                    }

                    name = OBJNAMEPREFIX_PLR + playerName;

                    LocalPlayer = this;
                } else {
                    //OG: CoUtils.instance getter V5Demo
                    GameObject obj = new GameObject($"CoUtils_{playerId}_{playerName}");
                    CoUtilsInstance = obj.AddComponent<CoUtils>();
                    obj.transform.SetParent(this.transform);
                }

#if FULLGAME
                // mid-run join: spawn ONLY after species known (save) or client Join-picks.
                if (Net.IsServer && LateJoin && !IsLocal) {
                    if (CharacterSelectWait.TryResolveSavedSpecies(this, out byte savedChr)) {
                        selectedCharacter = savedChr;
                        CreateCharacter();
                    } else {
                        CharacterSelectWait.Server_MarkPendingLateJoin(playerId);
                    }
                } else if (Net.IsServer && CharacterSelectWait.Waiting && !IsLocal) {
                    // new-run lobby gate still open - HostProceed stamps species then worldgen spawns
                } else {
                    CreateCharacter();
                }
#else
                CreateCharacter();
#endif

                ApplyNameAndColor(playerName, playerColor);

                OnPlayerJoined?.Invoke(this);
                OnPlayerJoinedUE?.Invoke(this);
                TogetherNetworkApi.NotifyPlayerJoined(this);

                InvokeRepeating(nameof(SlowUpdate), 1f, 1f);
                InvokeRepeating(nameof(SlowButFastUpdate), 1f, 0.1f);

                if (Net.IsServer) {
                    Multiplayer.ApplyGameRules();

                    Util.DelayCallLambda(0.1f, () => {
                        if (this != null && !IsLocal && Util.IsInWorld()) { // and tell him to generate the world NOW
#if FULLGAME
                            if (CharacterSelectWait.IsPendingLateJoin(playerId)) {
                                // char select first - StartGameAnnouncement after they pick
                                NetDataWriter pick = Net.CreateWriter(NetmsgId.CLIENT_LateJoinCharSelect);
                                Net.Server_SendToClientsVeryReliable(pick, new List<knetid> { playerId });
                                return;
                            }
#endif

                            NetDataWriter writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);
                            writer.Put(ServerMain.LastStartGameAnnouncementPacket);
                            writer.Put(WorldMP.CompileRunSettings(), true);
#if FULLGAME
                            AchievementBreakSync.WriteAnnouncementFlag(writer);
#endif
                            Net.Server_SendToClientsVeryReliable(writer, new List<knetid> { playerId });

                            ServerMain.Server_AnnounceSeed(new List<knetid> { playerId });
                        }
                    });
                }
            }
        }

        void SlowUpdate() {
            if (Net.IsServer) {
                if (body != null) {
                    if (Util.IsWorldGenerated()) {
                        if (IsLocal) {
                            ServerPlayerState.is_loaded_in = true;
                            ServerPlayerState.did_give_spawn_location = true;
                        }
                    }
                }
            }
        }

        public List<NetBody> visiblebodies = new();
        static NetDataWriter ragdollpacketwriter = Net.CreateWriter(NetmsgId.CLIENT_ragdollpack);
        void SlowButFastUpdate() {
            visiblebodies.Clear();
            if (!Net.IsServer || !Util.IsWorldGenerated()) {
                return;
            }
            NetBody.GetBodiesInRadius(pos, GameplayRanges.RagdollSyncGroupRadius, visiblebodies);
            if (ServerPlayerState != null && ServerPlayerState.is_loaded_in) {
                Server_SendRagdollPackets();
            }
            // 布娃娃姿态改由独立数据包直接投递，关闭 SyncGroup3 避免从原点插值造成的漂移与丢弃
            foreach (SyncBody otherBody in Net.ServerEntityManager.GetEntities<SyncBody>()) {
                if (otherBody == null) {
                    continue;
                }
                Net.ServerEntityManager.ToggleSyncGroup((byte)playerId, otherBody, SyncGroup.SyncGroup3, false);
            }
        }

        // 向视野内客机单独下发附近布娃娃肢体快照，房主无需发给自己
        void Server_SendRagdollPackets() {
            if (IsLocal) {
                return;
            }
            NetDataWriter writer = ragdollpacketwriter;
            for (int i = 0; i < visiblebodies.Count; i++) {
                NetBody visible = visiblebodies[i];
                if (visible == null || visible.body == null) {
                    continue;
                }
                if (visible.body.standing || visible.piggybacking_on != null) {
                    continue;
                }
                if (visible.plr == this && !Server_HasForcedServerOwnershipOfBody()) {
                    continue;
                }
                writer.ResetKeepMsgId();
                writer.Put((ushort)visible.netId);
                int length = writer.Length;
                ClientMain.WriteRagdollPacket(ref writer, visible.body);
                writer.CompressWriter(length);
                Net.Server_SendToClients(LiteNetLib.DeliveryMethod.Unreliable, writer, playerId);
            }
        }

        private void Update() {
            using FindTheBugInator.Scope _bug = FindTheBugInator.Time(FindTheBugInator.Players);

            // its not me and its alive
            if (IsAlive()) {
                if (Net.IsServer) { // server-only
                    if (!IsLocal) {
                        if (IsInMinigame) {
                            if (minigame_currentItem != null && ItemSync.CheckIfBodyReachThisItem(minigame_currentItem, body)) {
                                if (minigame_currentItem.IsManualDefibrillator() && minigame_currentItem.battery.hasCharge) {
                                    //OG: ManualDefibMinigame.Update V520dev1
                                    minigame_currentItem.battery.DrainCharge(Time.deltaTime / 800f);
                                }
                            }
                        }
                    }
                }
            }
        }

        void LateUpdate() {
            _UpdateFingerPointVisual();
        }

        public void DestroyCharacterIfNoLocal() {
            if (body != null) {
                if (body != PlayerCamera.main.body) {
                    if (body == InvButton_get_body_MultiplayerPatch.focused_body) {
                        InvButton_get_body_MultiplayerPatch.focused_body = null;
                    }
                    if (WoundView.view != null && WoundView.view.body == body) {
                        WoundView.view.body = PlayerCamera.main.body;
                    }

                    ScavPlayer.BodyToPlayerDict.Remove(body);

                    NetBody.DestroyNPC(body);
                    body = null;
                }
            }
        }

        public void OnDestroy() {
            try {
                // host only. clients that also built PlayerSavedState parked gear under a local
                // sAVESTATE_ without DetachSyncKeepMesh, then path5 fought a loose trashbag ghost.
                if (Net.IsServer && Util.IsInWorld() && body != null && Multiplayer.rules.DisconnectShouldSaveAnything) {
#if TESTING
                    log.l($"Saved player state of {this}");
#endif
                    PlayerCheckpoint.WriteOne(this, false);
                    if (ServerMain.Server_LastPlayerStates.TryGetValue(GetPersistentId(), out PlayerSavedState previousState)) {
                        previousState.Discard();
                    }
                    ServerMain.Server_LastPlayerStates[GetPersistentId()] = (new PlayerSavedState(body));
                }
            } catch (Exception e) {
                log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: saving plr state: " + e.ToString());
            }

            if (ScavPlayer.ClientIdToPlayerDict.TryGetValue(this.playerId, out ScavPlayer currentPlayer) &&
                ReferenceEquals(currentPlayer, this)) {
                ScavPlayer.ClientIdToPlayerDict.Remove(this.playerId);
            }
            ServerMain._UpdateSpecialPlayerLists();
            SharedMain.ForceUpdatePlayerLists();
            try {
                OnPlayerLeft?.Invoke(this);
            } catch (Exception e) {
                log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: OnPlayerLeft event: " + e.ToString());
            }

            OnPlayerLeftUE?.Invoke(this);
            DestroyIconSprites();
            TogetherNetworkApi.NotifyPlayerLeft(this);

            try {
                if (Net.IsServer && Net.IsRunning) {
                    string msg = $"{Chat.AddColorTags(playerColor, playerName)}<parsebreak> {Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:plr_left>")}";
                    Multiplayer.DoMultiplayerStatusMessageLog(Lang.ParseTaggedString(msg));
                    Chat.Server_ChatAnnouncement(msg);
                }
            } catch (Exception e) {
                log.error($"{nameof(ScavPlayer)}.{nameof(OnDestroy)}: disconnect handling: " + e.ToString());
            }

#if TESTING
            if (log.verbose) { log.l($"{this}: OnDestroy  {this.name}  islocal: {IsLocal}  hasbody: {body != null}"); }
#endif

            DestroyCharacterIfNoLocal();

            if (BasePlayer != null) {
                if (Net.ServerEntityManager != null) {
                    BasePlayer.Destroy();
                    BasePlayer = null;
                }
            }

            if (locationPingCircle) {
                Destroy(locationPingCircle);
            }
            if (locationPingArrow) {
                Destroy(locationPingArrow);
            }

#if DO_QUEUE_IDS
            if (clientId != 0 && !Net._QUEUED_PLAYER_IDS_TO_REUSE.Contains(clientId))
                Net._QUEUED_PLAYER_IDS_TO_REUSE.Enqueue(clientId);
#endif
        }
    }
}
