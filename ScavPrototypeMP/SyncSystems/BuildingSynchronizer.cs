using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 多人加热/制冷源重写组件：仅由服务端统一计算半径内所有玩家与 NPC 躯体的温度插值，再经医疗同步下发，避免客户端本地预测与房主快照拉扯。
/// </summary>
public class HeaterMultiplayerRewrite : MonoBehaviour {
    private const float FallbackRadius = 10f;
    private const float TickInterval = 0.5f;

    static readonly List<NetBody> _near = new List<NetBody>();
    static Heater[] _cached = Array.Empty<Heater>();
    static float _nextScan;
    static float _nextApply;

    Heater heater;

    void Awake() {
        heater = GetComponent<Heater>();
    }

    void Update() {
        if (!Net.IsRunning || !Net.IsServer)
            return;

        if (heater == null) {
            heater = GetComponent<Heater>();
        }
        Apply(heater);
    }

    // 结构空调常挂在子物体上，房主不在视野内时 OnWillRenderObject 永不触发，且区块休眠会关闭整个结构；
    // 由此全局轮询兜底结算所有非激活/未挂载重写组件的温控源并同步给范围内玩家。
    public static void Tick() {
        if (!Net.IsRunning || !Net.IsServer || !SharedMain.LocalWorldIsGenerated)
            return;

        float now = Time.unscaledTime;
        if (now < _nextApply)
            return;

        _nextApply = now + TickInterval;
        if (now >= _nextScan) {
            _cached = UnityEngine.Object.FindObjectsByType<Heater>(FindObjectsInactive.Include, FindObjectsSortMode.None);
            _nextScan = now + 2f;
        }
        for (int i = 0; i < _cached.Length; i++) {
            Heater heater = _cached[i];
            if (heater == null)
                continue;
            if (heater.gameObject.activeInHierarchy && heater.GetComponent<HeaterMultiplayerRewrite>() != null)
                continue;
            Apply(heater);
        }
    }

    // 单机模式（!Net.IsRunning）下原版 Heater.OnWillRenderObject 未被拦截，此处必须提前返回，避免无 NetBody 时空刷 heater.lastTime 吞掉原版温控冷却
    public static void Apply(Heater heater) {
        if (!Net.IsRunning || !Net.IsServer || heater == null || !heater.enabled)
            return;

        float radius = heater.maxDistance;
        if (radius <= 0.01f)
            radius = FallbackRadius;

        if (Time.time - heater.lastTime <= TickInterval)
            return;

        heater.lastTime = Time.time;
        _near.Clear();
        NetBody.GetBodiesInRadius(heater.transform.position, radius, _near);
        for (int i = 0; i < _near.Count; i++) {
            NetBody nb = _near[i];
            if (nb == null)
                continue;
            Body body = nb.body;
            if (body == null)
                continue;

            bool towards = (heater.isHeater && body.temperature < heater.desiredTemp)
                || (!heater.isHeater && body.temperature > heater.desiredTemp);
            if (!towards)
                continue;

            body.temperature = Mathf.Lerp(body.temperature, heater.desiredTemp, heater.tempLerpStrength);
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
    }
}

/// <summary>
/// 多人尸体目击与破坏情绪惩罚重写组件（同时兼容场景遗骸与阵亡玩家躯体）。
/// </summary>
public class CorpseScriptMultiplayerRewrite : MonoBehaviour {
    public bool animalCorpse = true;
    public byte corpseSpriteIndex = 0;

    public HashSet<NetBody> people_that_seen_it = new();

    public const float SEE_RADIUS = 7f;
    private const float BreakWitnessRadius = 28f;
    private const float AttackerNearSqr = 10f * 10f;

    void Awake() {
        if (TryGetComponent<CorpseScript>(out var corpse)) {
            animalCorpse = corpse.animalCorpse;
            corpseSpriteIndex = (byte)Array.IndexOf(corpse.startSprites, corpse.GetComponent<SpriteRenderer>().sprite);
        } else if (TryGetComponent<Body>(out _)) {
            animalCorpse = false;
        }
    }

    void Start() {
        InvokeRepeating(nameof(SlowUpdate), UnityEngine.Random.value * 2f, 0.1f);
    }

    void SlowUpdate() {
        if (animalCorpse)
            return;

        if (TryGetComponent<NetBody>(out var npc)) {
            if (npc.body.alive) {
                Destroy(this);
                return;
            }
            if (npc.timeHasBeenDead < 2)
                return;
        }

        foreach (NetBody plr in ScavPlayer.GetPlayerBodiesInRadius(transform.position, SEE_RADIUS)) {
            if (!people_that_seen_it.Add(plr))
                continue;

            if (!plr.IsLocal || npc != null) {
                plr.body.happiness -= 3.5f * plr.body.desensitizedMult;
                plr.body.sicknessAmount += 6f * plr.body.desensitizedMult;
                plr.body.desensitizedMult *= 0.9f;
                plr.body.corpsesSeen++;
                if (plr.body.totalHappiness < -55f) {
                    plr.body.talker.Talk(Locale.GetCharacter("seecorpsesuicidal"), null, force: true);
                } else if (plr.body.corpsesSeen < 9) {
                    plr.body.talker.Talk(Locale.GetCharacter("seecorpse"));
                    plr.body.eyeScareTime = 4f;
                } else {
                    plr.body.talker.Talk(Locale.GetCharacter("seecorpsedesensitized"));
                }
            }
        }
    }

    private void OnWillRenderObject() {
        foreach (NetBody plr in ScavPlayer.GetPlayerBodiesInRadius(transform.position, SEE_RADIUS)) {
            plr.body.overrideLookTime = 0.5f;
            plr.body.overrideLookPos = transform.position;
            if (plr.body.corpsesSeen < 9) {
                plr.body.eyeScareTime = 0.5f;
            }
        }
    }

    void OnDestroy() {
        if (animalCorpse)
            return;
        if (!gameObject || !gameObject.scene.isLoaded || !Util.IsWorldGenerated())
            return;
        if (TryGetComponent(out BuildingEntity b) && b.GetHealth() > 0f)
            return;

        var nearest = NetBody.GetNearestBody(transform.position, true, true);
        if (nearest.Item1 == null || nearest.Item2 > AttackerNearSqr)
            return;

        foreach (NetBody witness in ScavPlayer.GetPlayerBodiesInRadius(transform.position, BreakWitnessRadius)) {
            if (Net.IsServer && witness.body.IsBodyLocal())
                continue;
            if (!witness.body.conscious)
                continue;

            bool isAttacker = witness.body.attackCooldown > 0f;
            float totalDamage = 5f;
            if (!Util.AccurateRaycastInteractionCheckObstruction(nearest.Item1.body, witness.body, false)) {
                totalDamage *= 0.5f;
            }

            witness.body.happiness -= totalDamage * witness.body.desensitizedMult;
            witness.body.eyeScareTime = 5f;
            if (isAttacker) {
                witness.body.talker.Talk(Locale.GetCharacter("breakcorpse"));
            }
        }
    }
}

/// <summary>
/// 当实体处于房主镜头视野外、但位于远端玩家可视半径内时，代为在服务端驱动原版依赖 <c>OnWillRenderObject</c> 的 AI 与机关更新。
/// </summary>
public class OnWillRenderObject_ForceForMPComponent : MonoBehaviour {
    public SyncInfoGameObjectTracker tracker => GetComponent<SyncInfoGameObjectTracker>();
    public Renderer renderer => GetComponent<Renderer>();
    public MonoBehaviour target;

    Renderer[] _childRenderers;
    MethodInfo _willRender;
    bool _wantSim;

    void OnEnable() {
        Camera.onPostRender += AfterCam;
    }

    void OnDisable() {
        Camera.onPostRender -= AfterCam;
    }

    // 商人精灵位于子节点上，主摄像机不会回调根节点的 OnWillRenderObject；服务端在有存活玩家靠近或敌对时保持唤醒并推进仇恨与挥击
    void TickTrader(TraderScript trader) {
        if (trader == null || !Net.IsRunning) {
            _wantSim = false;
            return;
        }
        if (Net.IsClient) {
            Body local = Util.GetLocalBody();
            bool close = local != null && Vector2.Distance(trader.transform.position, local.transform.position) < 12f;
            _wantSim = trader.hostile || close;
            return;
        }
        Body near = TrackerTrader.ClosestLiving(trader.transform.position);
        bool hostClose = near != null && Vector2.Distance(trader.transform.position, near.transform.position) < 12f;
        if (trader.hostile || hostClose) {
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            ChunkSleep.Ignore(gameObject);
        }
        _wantSim = trader.hostile || hostClose || (tracker != null && tracker.isVisible);
        TraderScript_OnWillRender_PinLog.EnsureClimb(trader);
    }

    void AfterCam(Camera cam) {
        if (cam != Camera.main || !_wantSim || !(target is TraderScript trader))
            return;
        if (TraderScript_OnWillRender_PinLog.RanThisFrame(trader))
            return;
        InvokeWillRender();
    }

    void InvokeWillRender() {
        if (_willRender == null) {
            _willRender = target.GetType().GetMethod(
                "OnWillRenderObject",
                BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
        }
        if (_willRender == null)
            return;
        try {
            _willRender.Invoke(target, null);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, $"ForceOnWillRenderObject({target?.name})", ex);
        }
    }

    void CacheRenderers() {
        if (target == null || (_childRenderers != null && _childRenderers.Length > 0))
            return;
        _childRenderers = target.GetComponentsInChildren<Renderer>(true);
    }

    bool AnyRendererVisible() {
        CacheRenderers();
        if (_childRenderers == null || _childRenderers.Length == 0)
            return false;
        for (int i = 0; i < _childRenderers.Length; i++) {
            Renderer r = _childRenderers[i];
            if (r != null && r.isVisible)
                return true;
        }
        return false;
    }

    void Update() {
        if (target is TraderScript trader) {
            TickTrader(trader);
            return;
        }
        if (Net.IsClient) {
            if (target is GunmineScript gun) {
                TrapClaim.TickClientGun(gun);
            }
#if FULLGAME
            if (target is BloodSuckerScript bloodSucker) {
                BloodSuckerClientVisual.Tick(bloodSucker);
            }
#endif
            return;
        }

        try {
            if (tracker == null || target == null || !tracker.isVisible)
                return;
            // 若房主镜头本身可见则原版会自动调用 OnWillRenderObject，此处跳过以防双倍步进
            if (AnyRendererVisible())
                return;
            if (_willRender == null) {
                _willRender = target.GetType().GetMethod(
                    "OnWillRenderObject",
                    BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            }
            if (_willRender == null) {
                ModLog.Error($"OnWillRenderObject method not found on {target.GetType()}", LogDomain.World);
                enabled = false;
                return;
            }
            _willRender.Invoke(target, null);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.World, $"ForceOnWillRenderObject({target?.name})", ex);
        }
    }
}

public class TrackerBuildingEntityBackgroundified : MonoBehaviour {
    public static readonly List<TrackerBuildingEntityBackgroundified> all_instances = new();

    void OnEnable() => all_instances.Add(this);
    void OnDisable() => all_instances.Remove(this);

    public bool is_backgroundified = false;
    public bool is_a_background_tilemap = false;
}

/// <summary>
/// 静态场景特殊实体（藤蔓绳索、洞穴蜱虫等）按视距懒同步基类。
/// </summary>
public class BaseSpecialStaticEntityTracker : MonoBehaviour {
    protected byte REQ = (byte)NetmsgId.IsThereASandvine__;

    public bool was_synced = false;
    float checktimer = 1f;

    void Start() {
        checktimer = UnityEngine.Random.value;
    }

    void FixedUpdate() {
        if (!Net.IsRunning) {
            Destroy(this);
            return;
        }
        if (was_synced && !WantsAnotherPass())
            return;

        checktimer -= Time.fixedDeltaTime;
        if (checktimer >= 0f)
            return;

        checktimer = UnityEngine.Random.value * 0.4f;
        if (!SharedMain.LocalWorldIsGenerated || SharedMain.LastWorldgenFinishTime < 5)
            return;

        if (Net.IsClient) {
            if (ClientMain.ServerIsGeneratingWorld)
                return;
            if (!CheckDist(PlayerCamera.main.transform.position))
                return;
        } else {
            ScavPlayer closestPlr = null;
            foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
                if (plr.ServerPlayerState.is_loaded_in && CheckDist(plr.pos)) {
                    closestPlr = plr;
                    break;
                }
            }
            if (closestPlr == null)
                return;
        }

        if (Net.IsClient) {
            NetDataWriter writer = Net.CreateWriter(REQ);
            writer.Put((Vector2)transform.position);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        } else {
            Server_Announce();
        }
        was_synced = !WantsAnotherPass();
    }

    protected virtual bool WantsAnotherPass() => false;

    public virtual bool CheckDist(Vector2 pos) {
        return KM.DistanceCheck_PreSqr(pos, transform.position, GameplayRanges.SimulationKeepRadiusSqr);
    }

    public virtual void Server_Announce() {
        if (!was_synced) {
            was_synced = true;
        }
    }

    public static T GetAtPos<T>(Vector2 pos) where T : BaseSpecialStaticEntityTracker {
        foreach (T item in UnityEngine.Object.FindObjectsOfType<T>()) {
            if ((Vector2)item.transform.position == pos) {
                return item;
            }
        }
        return null;
    }
}

/// <summary>
/// 可攀爬绳索/藤蔓/梯子几何节点与销毁同步追踪器。
/// </summary>
public class TrackerClimbableEntity : BaseSpecialStaticEntityTracker {
    public const string sandvine_new_id = "Special/sandvinerope";
    public const string climbingropeextended_id = "climbingropeextended";
    public const string mushroomrope_new_id = "Special/mushroomrope";
    public const string veinloop_new_id = "Special/veinloop";
    public const string spicevine_id = "spicevine";
    public const string spicevineceiling_id = "spicevineceiling";

    public string og_id = null;

    void Awake() {
        REQ = (byte)NetmsgId.SERVER_IsThereARope__;
    }

    public bool is_ladder => GetComponent<BuildingEntity>().id == "ladder";
    public bool is_sandvine => GetComponent<BuildingEntity>().id == sandvine_new_id;
    public bool is_mushroomrope => GetComponent<BuildingEntity>().id == mushroomrope_new_id;
    public bool is_veinloop => GetComponent<BuildingEntity>().id == veinloop_new_id;

    public bool is_spicevine {
        get {
            string id = GetComponent<BuildingEntity>().id;
            return id == spicevine_id || id == spicevineceiling_id;
        }
    }

    public bool is_mushroomrope_or_sandvine => is_sandvine || is_mushroomrope || is_veinloop || is_ladder;
    public bool is_climbingropeextended => GetComponent<BuildingEntity>().id == climbingropeextended_id;
    public bool is_known_climbable => is_ladder || is_sandvine || is_mushroomrope || is_veinloop || is_climbingropeextended || is_spicevine;

    Vector2 spritescale {
        get {
            if (is_mushroomrope_or_sandvine || is_spicevine)
                return transform.localScale;
            if (is_climbingropeextended)
                return transform.GetChild(0).localScale;
            return Vector2.one;
        }
        set {
            if (is_mushroomrope_or_sandvine || is_spicevine)
                transform.localScale = value;
            if (is_climbingropeextended)
                transform.GetChild(0).localScale = value;
        }
    }

    float spritey {
        get {
            if (is_mushroomrope_or_sandvine)
                return GetComponent<SpriteRenderer>().size.y;
            return 100f;
        }
        set {
            if (is_mushroomrope_or_sandvine) {
                GetComponent<SpriteRenderer>().size = new Vector2(2.5f, value);
            }
        }
    }

    bool isPointless = false;
    public bool announce_reliable = true;
    readonly HashSet<ScavPlayer> _told = new();
    SpriteRenderer[] _sprites;

    protected override bool WantsAnotherPass() {
        if (!Net.IsServer)
            return false;
        Climbable climbable = GetComponent<Climbable>();
        if (climbable == null || climbable.points == null || climbable.points.Count < 2)
            return true;
        return HasUntoldClient();
    }

    bool HasUntoldClient() {
        var clients = ServerMain.AllClientIdsExceptHost;
        for (int i = 0; i < clients.Count; i++) {
            if (!ScavPlayer.TryGetPlayerFromClientId(clients[i], out var plr) || plr == null)
                continue;
            if (plr.ServerPlayerState == null || !plr.ServerPlayerState.is_loaded_in)
                continue;
            if (!_told.Contains(plr))
                return true;
        }
        return false;
    }

    public override void Server_Announce() {
        Climbable climbable = GetComponent<Climbable>();
        if (climbable == null || climbable.points == null || climbable.points.Count < 2) {
            if (climbable != null && !isPointless && climbable.points != null && climbable.points.Count == 0) {
                ModLog.Error($"Rope has 0 points: {NetObjectRegistry.DebugStringGameObject(gameObject)} at {(Vector2)climbable.transform.position}", LogDomain.World);
                isPointless = true;
            }
            was_synced = false;
            return;
        }

        bool refresh = !announce_reliable;
        announce_reliable = true;

        var clients = ServerMain.AllClientIdsExceptHost;
        var targets = new List<knetid>(clients.Count);
        for (int i = 0; i < clients.Count; i++) {
            if (!ScavPlayer.TryGetPlayerFromClientId(clients[i], out var plr) || plr == null)
                continue;
            if (plr.ServerPlayerState == null || !plr.ServerPlayerState.is_loaded_in)
                continue;
            if (!refresh && _told.Contains(plr))
                continue;
            targets.Add(clients[i]);
            _told.Add(plr);
        }
        if (targets.Count == 0) {
            was_synced = !HasUntoldClient();
            return;
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ThereIsARope____);
        writer.Put((Vector2)climbable.transform.position);
        writer.Put(climbable.GetComponent<BuildingEntity>().id, true);
        writer.Put(climbable.points.ToArray());
        writer.Put(climbable.downwardsVelocity);
        writer.Put(spritescale);
        writer.Put(spritey);
        Net.Server_SendToClients(refresh ? DeliveryMethod.Unreliable : DeliveryMethod.ReliableUnordered, writer, clientIds: targets);

        was_synced = !HasUntoldClient();
    }

    void OnDestroy() {
        if (!Net.IsServer || !Util.IsWorldGenerated())
            return;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || world.generatingWorld)
            return;
        BuildingEntity build = GetComponent<BuildingEntity>();
        if (build != null && build.GetHealth() > 0.5f)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_NoTheresNoRopeFuckOff);
        writer.Put((Vector2)transform.position);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    public override bool CheckDist(Vector2 pos) {
        Climbable climbable = GetComponent<Climbable>();
        if (climbable == null) {
            Destroy(this);
            return false;
        }
        if (NearSprite(pos))
            return true;
        if (climbable.points == null || climbable.points.Count == 0)
            return Net.IsClient;

        int last = climbable.points.Count - 1;
        for (int i = 0; i < last; i++) {
            Vector2 closest = KM.v2getClosestPointOnLine(climbable.points[i], climbable.points[i + 1], pos);
            if ((closest - pos).sqrMagnitude < GameplayRanges.SimulationKeepRadiusSqr)
                return true;
        }
        return false;
    }

    bool NearSprite(Vector2 pos) {
        if (_sprites == null)
            _sprites = GetComponentsInChildren<SpriteRenderer>(true);
        for (int i = 0; i < _sprites.Length; i++) {
            SpriteRenderer sprite = _sprites[i];
            if (sprite != null && sprite.bounds.SqrDistance(pos) <= GameplayRanges.SimulationKeepRadiusSqr)
                return true;
        }
        return ((Vector2)transform.position - pos).sqrMagnitude < GameplayRanges.SimulationKeepRadiusSqr;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_NoTheresNoRopeFuckOff)]
    static void ReceiveRopeDestroyed(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        var sv = GetAtPos<TrackerClimbableEntity>(pos);
        if (sv != null) {
            Destroy(sv.gameObject);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ThereIsARope____)]
    static void ReceiveRopeAnnounce(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 ropePos);
        reader.Get(out string ropeResourceId, true);

        var rope = GetAtPos<TrackerClimbableEntity>(ropePos);
        if (rope == null) {
            rope = UnityEngine.Object.Instantiate(Resources.Load(ropeResourceId), ropePos, Quaternion.identity).GetOrAddComponent<TrackerClimbableEntity>();
        }
        rope.was_synced = true;

        Climbable climbable = rope.GetComponent<Climbable>();
        reader.Get(out Vector2[] newPoints);
        reader.Get(out float downwardsVelocity);
        reader.Get(out Vector2 scale);
        reader.Get(out float sizeY);

        climbable.points = new List<Vector2>(newPoints);
        climbable.downwardsVelocity = downwardsVelocity;
        rope.spritescale = scale;
        rope.spritey = sizeY;

        SpriteRenderer spr = rope.GetComponent<SpriteRenderer>();
        if (rope.is_sandvine || rope.is_veinloop) {
            spr.color = Color.Lerp(Color.gray, Color.white, UnityEngine.Random.value);
            if (rope.is_sandvine) {
                spr.flipX = UnityEngine.Random.value > 0.5f;
            }
        } else if (rope.is_ladder) {
            spr.size = new Vector2(1.5f, spr.size.y);
        }

        climbable.CalculateTotalLength();
    }

    [ServerReceiver((byte)NetmsgId.SERVER_IsThereARope__)]
    static void ReceiveRopeQuery(knetid clientId, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        var thing = GetAtPos<TrackerClimbableEntity>(pos);
        if (thing == null) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_NoTheresNoRopeFuckOff);
            writer.Put(pos);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: clientId);
        } else {
            thing.Server_Announce();
        }
    }
}

/// <summary>
/// 静态洞穴蜱虫群落位置存在性同步追踪器。
/// </summary>
public class TrackerStaticCaveTicks : BaseSpecialStaticEntityTracker {
    void Awake() {
        REQ = (byte)NetmsgId.SERVER_Krokosha_CaveTicks_Tracker_request;
    }

    public override void Server_Announce() {
        was_synced = true;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_announce);
        writer.Put((Vector2)transform.position);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny)]
    static void ReceiveCaveTicksDeny(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        var ticks = GetAtPos<TrackerStaticCaveTicks>(pos);
        if (ticks != null) {
            Destroy(ticks.gameObject);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_announce)]
    static void ReceiveCaveTicksAnnounce(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        if (GetAtPos<TrackerStaticCaveTicks>(pos) == null) {
            GameObject spawned = (GameObject)UnityEngine.Object.Instantiate(Resources.Load("CaveTicks"), pos, Quaternion.identity);
            var tracker = spawned.GetOrAddComponent<TrackerStaticCaveTicks>();
            tracker.was_synced = true;
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Krokosha_CaveTicks_Tracker_request)]
    static void ReceiveCaveTicksRequest(knetid clientId, NetDataReader reader) {
        reader.Get(out Vector2 pos);
        var thing = GetAtPos<TrackerStaticCaveTicks>(pos);
        if (thing == null) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny);
            writer.Put(pos);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: clientId);
        } else {
            thing.Server_Announce();
        }
    }
}
