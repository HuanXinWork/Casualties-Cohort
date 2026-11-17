using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Text;
using Together.Api;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 记录角色受到的玩家攻击、建筑陷阱伤害、环境致死或复活事件，用于服务端生成死亡播报与击杀归因。
/// </summary>
public class BodyDamageEvent {
    public enum DamageEventType {
        CustomDeathMessage,
        PlayerAttack,
        BuildingAttack,
        Revive,
        Environment,
    }

    public DamageEventType type;
    public DateTime timestamp;
    public DateTime expirationTime;
    public Dictionary<string, object> data;

    public NetBody attackerBody;
    public BuildingEntity attackerBuilding;
    public NetBody victim;
    public Vector2 attackerPos;
    public Vector2 victimPos;

    public Item weapon;
    public string weaponItemId;
    public string attackerBuildingId;
    public float damage;
    public string deathMessage;

    public BodyDamageEvent() {
    }

    public BodyDamageEvent(string deathText, NetBody target, double expiration = 30) {
        type = DamageEventType.CustomDeathMessage;
        victim = target;
        if (target != null) {
            victimPos = target.position;
        }
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(expiration);
        deathMessage = deathText;
    }

    public BodyDamageEvent(DamageEventType type, NetBody target) {
        this.type = type;
        victim = target;
        if (target != null) {
            victimPos = target.position;
        }
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(30);
    }

    public BodyDamageEvent(NetBody evil, NetBody target, Item weapon, float dmg) {
        type = DamageEventType.PlayerAttack;
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(30);

        attackerPos = evil.position;
        victimPos = target.position;
        victim = target;
        attackerBody = evil;
        this.weapon = weapon;
        if (weapon != null) {
            weaponItemId = weapon.id;
        }
        damage = dmg;
        attackerBuildingId = evil.ToString();
    }

    public BodyDamageEvent(BuildingEntity evil, NetBody target, float dmg) {
        type = DamageEventType.BuildingAttack;
        timestamp = DateTime.UtcNow;
        expirationTime = timestamp.AddSeconds(20);

        attackerPos = evil.transform.position;
        victimPos = target.position;
        victim = target;
        attackerBuilding = evil;
        damage = dmg;
        attackerBuildingId = evil.id;
    }

    public override string ToString() {
        if (this == null) return "";
        return $"BodyDamageEvent({type}, {attackerBody.ToSafeString()} {attackerBuilding.ToSafeString()} {victim.ToSafeString()})";
    }

    public bool IsPlayerAttack() {
        return type == DamageEventType.PlayerAttack && attackerBody != null;
    }

    public bool IsBuildingAttack(out SyncInfoGameObjectTracker tracker) {
        tracker = null;
        return (type == DamageEventType.PlayerAttack || type == DamageEventType.BuildingAttack)
            && attackerBuilding != null
            && attackerBuilding.TryGetTracker(out tracker);
    }

    public bool IsSuicide() {
        return type == DamageEventType.PlayerAttack && attackerBody == victim;
    }
}

/// <summary>
/// 联机角色躯体核心网络绑定组件（维护 <see cref="Body"/>、<see cref="SyncBody"/> 与 <see cref="ScavPlayer"/> 之间的映射、状态同步与交互会话）。
/// </summary>
public partial class NetBody : MonoBehaviour {
    public static implicit operator Body(NetBody nb) => nb.body;

    public static readonly List<NetBody> all_instances = new();
    public static IReadOnlyDictionary<knetid, NetBody> NetIdToNetBody => _PRIV_NetIdToNetBody;
    private static readonly Dictionary<knetid, NetBody> _PRIV_NetIdToNetBody = new();

    public static event Action<ScavPlayer> OnPlayerSleep;
    public static event Action<ScavPlayer> OnPlayerDeath;

    public double _lasttime_nobraindamage_from_circulation = 100000000;
    public SyncBody syncBody { get; internal set; }

    public knetid netId {
        get {
            if (Net.IsRunning) {
                if (syncBody == null) {
                    throw new Exception("NetBody has no SyncBody!");
                }
            } else if (syncBody == null) {
                return 0;
            }
            return syncBody.Id;
        }
    }

    public Dictionary<string, string> MindMemory = new();
    public List<int> RecipesHasCrafterBefore = new();
    readonly MaxCapacityQueue<BodyDamageEvent> DamageHistory = new(100);

    public double lastTimeBark;
    public double lastTimeCprPress;
    public int cpr_cur_perfect_press_count;
    public bool chip_punishment_protocol = false;
    public float scatterMoodOffset = 0f;
    float scatterMoodTick;

    public double timeOfDeath { get; protected set; }
    public double timeHasBeenDead => Time.unscaledTimeAsDouble - timeOfDeath;

    public float irradiateIntensity = 0f;
    public float timeSinceRadUpdate = 0f;
    internal float cprPress = 0f;
    bool lastCpr = false;

    public byte butchering_dropmeat_skip_counter = 0;
    public bool _SERVER_healthsync_skip_frame = false;
    internal bool healthConfirm;
    public ushort _lastHealthPacketReceiveTick = 0;
    public ushort _lastSyncPacketReceiveTick = 0;

    public bool IsLocal => Util.IsBodyLocal(body);
    public bool IsPlayer => plr != null;
    public ScavPlayer plr { get; internal set; }
    public CharStatusVisuals visual { get; protected set; }
    public Rigidbody2D rb => body.rb;

    public bool alive => body.alive;
    public bool exercising => body.exercising;
    // 注意：外部模组（如 SkinSync）通过反射读取 Together.NetBody.body，签名严禁改动。
    public Body body { get; private set; }
    public GameObject chara => transform.parent.gameObject;
    public Limb head => body.limbs[(int)Body.LimbNum.Head];

    private string _cur_bodyname = null;
    protected Color24 _color;
    private bool _unchipped = false;

    public bool idle_anim_allowed = false;
    public bool useiteminhand;
    public bool[] localoverride_showInfection;
    private bool prev_useiteminhand = false;

    public SByteScalarVec2 moveDir;
    public float last_mood = 0f;

    public Vector2 position => transform.position;
    public Vector2 pos => body.standing
        ? (Vector2)body.transform.position
        : (Vector2)body.limbs[(int)Body.LimbNum.UpTorso].transform.position;

    public string playerName => IsPlayer ? plr.playerName : bodyName;

    public string bodyName {
        get => _cur_bodyname ?? syncBody?.Id.ToString() ?? "NO_NAME_BODY";
        set => ApplyNameAndColor(value, color);
    }

    public Color color {
        get => IsPlayer ? plr.playerColor : _color;
        set => _color = value;
    }

    public bool unchipped {
        get => syncBody == null ? _unchipped : syncBody.Unchipped.Value;
        private set {
            _unchipped = value;
            if (syncBody != null) {
                syncBody.Unchipped.Value = value;
            }
        }
    }

    public static bool TryGetLocalNetBody(out NetBody nb) {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null && PlayerCamera.main.body.TryGetComponent(out nb)) {
            return true;
        }
        nb = null;
        return false;
    }

    public static NetBody GetLocalNetBodyNullable() {
        if (PlayerCamera.main != null && PlayerCamera.main.body != null) {
            return PlayerCamera.main.body.GetNetBody();
        }
        return null;
    }

    public static (NetBody, float) GetNearestBodyFallbackToDead(Vector2 pos) {
        NetBody nearestAliveCon = null;
        float nearestAliveConDist = float.PositiveInfinity;

        NetBody nearestAlive = null;
        float nearestAliveDist = float.PositiveInfinity;

        NetBody nearestAny = null;
        float nearestAnyDist = float.PositiveInfinity;

        foreach (NetBody npc in all_instances) {
            float dist = KM.DistanceSqr(npc.transform.position, pos);

            if (npc.body.alive && npc.body.conscious && dist < nearestAliveConDist) {
                nearestAliveConDist = dist;
                nearestAliveCon = npc;
            }
            if (npc.body.alive && dist < nearestAliveDist) {
                nearestAliveDist = dist;
                nearestAlive = npc;
            }
            if (dist < nearestAnyDist) {
                nearestAnyDist = dist;
                nearestAny = npc;
            }
        }

        if (nearestAliveCon != null) return (nearestAliveCon, nearestAliveConDist);
        if (nearestAlive != null) return (nearestAlive, nearestAliveDist);
        return (nearestAny, nearestAnyDist);
    }

    public static (NetBody, float) GetNearestBodyPrioritizeAlive(Vector2 pos) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (NetBody npc in all_instances) {
            float dist = KM.DistanceSqr(npc.transform.position, pos);
            float d2 = dist;
            if (!npc.body.alive) d2 += 200f;
            if (!npc.body.conscious) d2 += 200f;

            if (d2 < nearest) {
                nearest = dist;
                nearestnpc = npc;
            }
        }
        return (nearestnpc, nearest);
    }

    public static (NetBody, float) GetNearestBody(Vector2 pos, bool must_be_alive = true, bool must_be_conscious = false) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (NetBody npc in all_instances) {
            if (must_be_alive && !npc.body.alive) continue;
            if (must_be_conscious && !npc.body.conscious) continue;

            float dist = KM.DistanceSqr(npc.transform.position, pos);
            if (dist < nearest) {
                nearest = dist;
                nearestnpc = npc;
            }
        }
        return (nearestnpc, nearest);
    }

    public static (NetBody, float) GetNearestConsciousBodyToThisBody(NetBody thisone) {
        float nearest = float.PositiveInfinity;
        NetBody nearestnpc = null;
        foreach (NetBody nb in all_instances) {
            if ((nb.body.conscious || nb.body.sleeping) && thisone != nb) {
                float dist = KM.DistanceSqr(nb.transform.position, thisone.transform.position);
                if (dist < nearest) {
                    nearest = dist;
                    nearestnpc = nb;
                }
            }
        }
        return (nearestnpc, nearest);
    }

    public static void GetBodiesInRadius(Vector2 pos, float radius, List<NetBody> list) {
        radius *= radius;
        foreach (NetBody b in all_instances) {
            if (KM.DistanceCheck_PreSqr(b.transform.position, pos, radius)) {
                list.Add(b);
            }
        }
    }

    public static List<NetBody> GetBodiesInRadius(Vector2 pos, float radius) {
        radius *= radius;
        var b = new List<NetBody>();
        foreach (NetBody item in all_instances) {
            if (KM.DistanceCheck_PreSqr(item.transform.position, pos, radius)) {
                b.Add(item);
            }
        }
        return b;
    }

    public static NetBody GetNetBodyFromId(knetid netId) => _PRIV_NetIdToNetBody.GetValueSafe(netId);
    public static bool TryGetNetBodyFromId(knetid netId, out NetBody nb) => _PRIV_NetIdToNetBody.TryGetValue(netId, out nb);

    void OnEnable() => all_instances.Add(this);
    void OnDisable() => all_instances.Remove(this);

    public void RollAttackRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((syncBody.drng_attackCounter.Value++) * (netId + 1));
    }

    public void RollUseRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((syncBody.drng_useCounter.Value++) * (netId + 1));
    }

    public void RollDamageRandom() {
        if (syncBody == null) return;
        UnityEngine.Random.InitState((syncBody.drng_damageCounter.Value++) * (netId + 1));
    }

    public void ResetMind() {
        MindMemory.Clear();
        RecipesHasCrafterBefore.Clear();
    }

    public BodyDamageEvent GetLastDamageEvent() {
        List<BodyDamageEvent> events = plr.netBody.DamageHistory.JustGiveTheQueue().ToList();
        int foundRevive = events.FindIndex(x => x.type == BodyDamageEvent.DamageEventType.Revive);
        if (foundRevive != -1) {
            events = events.Skip(foundRevive).ToList();
        }

        List<BodyDamageEvent> unexpired = events.Where(x => x.expirationTime > DateTime.UtcNow).Reverse().ToList();
        return unexpired.Count > 0 ? unexpired[0] : null;
    }

    public void OnDamageEvent(BodyDamageEvent dmg) {
        DamageHistory.Enqueue(dmg);
#if !RELEASE
        log.l($"DAMAGE LOG: EVENT: {dmg}");
#endif
    }

    public override string ToString() {
        if (this == null) {
            return "NB(NULL)";
        }
        if (IsPlayer) {
            return $"NB(ID:{syncBody?.Id}, PLRID:{plr.playerId}, name:{playerName})";
        }
        return $"NB(ID:{syncBody?.Id}, name:{playerName})";
    }

    void Awake() {
        body = GetComponent<Body>();
        body.moveDir = Vector2.zero;
        body.crouching = false;
        localoverride_showInfection = new bool[body.limbs.Length];
        visual = this.GetOrAddComponent<CharStatusVisuals>();
        idle_anim_allowed = true;
    }

    internal void OnFoundNetPlayerInitFinish() {
        plr.body = body;
        ApplyNameAndColor(playerName, plr.playerColor);
        unchipped = plr.unchipped;
        ScavPlayer.BodyToPlayerDict[body] = plr;
    }

    public void BackupLocalPlayerInit() {
        if (plr == null && Net.IsRunning && ScavPlayer.LocalPlayer != null) {
            plr = ScavPlayer.LocalPlayer;
#if !RELEASE
            Plugin.log.LogWarning("BackupLocalSCIInit ACTUALLY WORKED YAY!!!!!!!");
#endif
            OnFoundNetPlayerInitFinish();
        }
    }

    protected void Start() {
        _lastHealthPacketReceiveTick = (ushort)(Net.ServerTick - 1);
        _lastSyncPacketReceiveTick = (ushort)(Net.ServerTick - 1);

        if (plr == null) {
            plr = ScavPlayer.GetPlayerFromBody(body);
            if (plr != null) {
                OnFoundNetPlayerInitFinish();
            }
            if (IsLocal) {
                BackupLocalPlayerInit();
            }
        }

        InvokeRepeating(nameof(SlowerUpdate), UnityEngine.Random.value * 2f, 0.1f);
        body.talker.text.richText = false;

#if !RELEASE
        if (!IsLocal && !IsPlayer) {
            body.targetLookPos = pos + Vector2.right * 100f * (UnityEngine.Random.value > 0.5f ? -1f : 1f) + Vector2.up * ((UnityEngine.Random.value - 0.5f) * 2f);
            if (UnityEngine.Random.value > 0.5f) {
                body.happiness = UnityEngine.Random.Range(-80, 200);
            }
            body.GetFace().eyeLight.enabled = false;
            if (UnityEngine.Random.value > 0.7f) {
                body.SwitchDir();
            }
        }
#endif

        // 躯体首次初始化就绪：通知外部模组（如 SkinSync）挂载贴图与渲染钩子
        if (body != null) {
            TogetherPanelApi.NotifyBodyReadyOrRebuilt(this, body.charType, isRebuild: false);
        }
    }

    protected void OnDeath() {
        ServerMain._ded_server_switch_counter = 0;
        try {
            if (plr != null) {
                ServerMain.OnPlayerDeath(plr);
                if (Net.IsServer) {
                    MedicalSync.Server_QueueSendCharacterHealth(this, true);
                }
                plr.minigame_current_type = 255;
                plr.minigame_currentItem = null;
                plr.locationPingArrow.SetActive(false);
                plr.locationPingCircle.SetActive(false);
                OnPlayerDeath?.Invoke(plr);
            }
        } catch (Exception e) {
            ModLog.Exception(LogDomain.Medical, "NetBody.OnDeath", e);
        }
        timeOfDeath = Time.unscaledTimeAsDouble;
    }

    protected void OnSleep() {
        if (Multiplayer.rules.DisableSleep) {
            return;
        }
        if (plr != null) {
            ServerMain.OnPlayerSleep(plr);
            OnPlayerSleep?.Invoke(plr);
        }
    }

    void OnDestroy() {
        all_instances.Remove(this);
        if (body != null) {
            ScavPlayer.BodyToPlayerDict.Remove(body);
        }

        if (MoodleManager.main != null && MoodleManager.main.body == body && Util.TryGetLocalBody(out Body locbody)) {
            MoodleManager.main.body = locbody;
        }

        SyncBody sb = syncBody;
        if (sb != null) {
            try {
                knetid id = sb.Id;
                if (_PRIV_NetIdToNetBody.TryGetValue(id, out NetBody mapped) && ReferenceEquals(mapped, this)) {
                    _PRIV_NetIdToNetBody.Remove(id);
                }
                if (Net.IsServer && (sb.nb == null || ReferenceEquals(sb.nb, this))) {
                    syncBody = null;
                    sb.Destroy();
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Core, "NetBody.OnDestroy SyncBody解绑异常已隔离", e);
            }
        }
    }

    public void SetNetIgnoreTime(float additionaltime = 0f) {
        if (Net.IsClient) {
            float t = ClientMain.RTT_IN_SECONDS_with_margin;
            if (IsPlayer && !plr.IsLocal) {
                t += (float)plr.rtt_in_seconds_with_margin;
            }
            additionaltime += t;
        } else if (IsPlayer) {
            additionaltime += (float)plr.rtt_in_seconds_with_margin;
        }

        ushort newTick = (ushort)(Net.ServerTick + (additionaltime * Net.ServerEntitySendRate));
        int seqDiff = LiteEntitySystem.Utils.SequenceDiff(newTick, _lastSyncPacketReceiveTick);
        if (seqDiff > 0) {
            _lastSyncPacketReceiveTick = newTick;
        }
    }

    public void SetNetHealthSyncIgnoreTime(float additionaltime = 0f) {
        // 保持兼容空方法签名：服务端权威伤势快照即时应用，避免超前推进 Tick 造成新鲜伤势包被误判为过期丢弃。
    }

    public void SetPlayer(ScavPlayer newPlayer) {
        if (body != null) {
            ScavPlayer.BodyToPlayerDict.Remove(body);
        }
        if (IsPlayer) {
            plr.body = null;
        }
        plr = newPlayer;
        if (plr != null) {
            plr.body = body;
            if (plr.body != null) {
                ScavPlayer.BodyToPlayerDict[plr.body] = plr;
            }
            ApplyNameAndColor(plr.playerName, plr.playerColor);
            SetUnchipped(plr.unchipped);
        }
    }

    public void ApplyNameAndColor(string name, Color color) {
        _cur_bodyname = name;
        this.color = color;
        body.name = "Body_" + bodyName;
        visual.ApplyNameAndColor();
    }

    public void SetUnchipped(bool unchippedVal) {
        _unchipped = unchippedVal;
        if (unchipped != unchippedVal) {
            unchipped = unchippedVal;
        }
        if (IsLocal) {
            Util.SetUnchipped(unchippedVal);
        }
        if (plr != null && plr.unchipped != unchippedVal) {
            plr.SetUnchipped(unchippedVal);
        }
    }

    public bool IsBodyLocal() => body.IsBodyLocal();

    public Vector2 GetPosition() {
        return !body.standing
            ? body.limbs[(int)Body.LimbNum.UpTorso].rb.position
            : (Vector2)transform.position;
    }

    public Vector2 GetHeadPos() {
        return body.limbs[(int)Body.LimbNum.Head].transform.position;
    }

    public void SetBodyPosition(Vector2 targetPos) {
        if (!body.standing) {
            Rigidbody2D torsoRb = body.limbs[(int)Body.LimbNum.UpTorso].rb;
            var snapshot = new List<(Vector2 pos, Rigidbody2D rb)>(body.limbs.Length);
            foreach (Limb limbe in body.limbs) {
                snapshot.Add((limbe.rb.position, limbe.rb));
            }
            Vector2 diff = targetPos - torsoRb.position;
            torsoRb.position = targetPos;
            foreach (var joint in snapshot) {
                joint.rb.position = joint.pos + diff;
            }
        } else {
            body.transform.position = targetPos;
        }
    }

    public void CameraShakeOverrideFunc(float num) {
        if (body.IsBodyLocal()) {
            PlayerCamera.main.shaker.Shake(num);
        }
    }
}
