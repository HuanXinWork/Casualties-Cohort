using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 商人组件网络追踪器：负责交易窗口安全钉住（距离/敌对/存活保护）、焦点躯体切换与库存快照同步请求。
/// </summary>
public class TrackerTrader : BaseComponentTracker<TraderScript> {
    public static TraderScript currentTrader;

    public struct TraderStatePacket : INetSerializeByMemcpy {
        public float reputation;
        public float hostility;
        public bool freeDressing;
        public bool didHug;
        public byte freeAmount;
        public float haggleAmount;
        public ushort valueGiven;
        public ushort totalValueGiven;

        public TraderStatePacket(TrackerTrader tracker) {
            TraderScript trader = tracker.og;
            reputation = trader.reputation;
            hostility = trader.hostility;
            freeDressing = trader.freeDressing;
            didHug = trader.didHug;
            freeAmount = (byte)trader.freeAmount;
            haggleAmount = trader.haggleAmount;
            valueGiven = (ushort)trader.valueGiven;
            totalValueGiven = (ushort)trader.totalValueGiven;
        }

        public void Apply(TrackerTrader tracker) {
            TraderScript trader = tracker.og;
            trader.reputation = reputation;
            TraderWindup.Note(trader, hostility);
            trader.freeDressing = freeDressing;
            trader.didHug = didHug;
            trader.freeAmount = freeAmount;
            trader.haggleAmount = haggleAmount;
            trader.valueGiven = valueGiven;
            trader.totalValueGiven = totalValueGiven;
        }
    }

    public BodyGetterOverrider bgo => this.GetOrAddComponent<BodyGetterOverrider>();

    public Body focused_body {
        get => bgo.body;
        set => bgo.body = value;
    }

    public bool CanBeRecruited_ForRespawn() {
        return build.GetHealth() > 200f && og.reputation > og.minHugReputation;
    }

    public bool is_registered => tracker != null;
    public SyncInfo si => tracker?.syncInfo;

    // 商人 12 格怒气递增判定需遍历全部存活玩家；旧版 GetNearestBody 会漏掉远处尚未绑定/激活 Tracker 的玩家实体
    internal static Body ClosestLiving(Vector2 pos) {
        Body best = null;
        float bestSqr = float.PositiveInfinity;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive)
                continue;
            float d = ((Vector2)plr.body.transform.position - pos).sqrMagnitude;
            if (d < bestSqr) {
                bestSqr = d;
                best = plr.body;
            }
        }
        return best;
    }

    public bool asked_for_inv { get; private set; }
    public bool received_their_inv;

    public BuildingEntity build => GetComponent<BuildingEntity>();
    readonly HashSet<Body> griefNear = new HashSet<Body>();

    float doublesendRepState = 0f;

    public void Server_AnnounceTraderReputationState(in IReadOnlyList<knetid> to_who = null) {
        if (Net.IsClient || og == null)
            return;
        if (!is_registered) {
            NetObjectRegistry.NewGO(gameObject);
            if (!is_registered) {
                ModLog.Error($"SERVER: trader snapshot skipped, {build?.id} is not registered", LogDomain.Trader);
                return;
            }
        }
        if (to_who != null && to_who.Count == 1) {
            TraderCommit.PublishTo(og, to_who[0]);
            return;
        }
        TraderCommit.Publish(og);
    }

    public void Server_SendTraderInventory(knetid clientId) {
        if (og == null)
            return;
        TraderCommit.PublishTo(og, clientId);
    }

    public void Server_SendTraderInventory(List<knetid> clients_to_enlighten) {
        if (og == null || clients_to_enlighten == null || clients_to_enlighten.Count == 0)
            return;
        if (clients_to_enlighten.Count == 1) {
            TraderCommit.PublishTo(og, clients_to_enlighten[0]);
            return;
        }
        TraderCommit.Publish(og);
    }

    protected override void TrackerAwake() {
        var owro = this.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
        owro.target = og;
        // 摊位数量极少，豁免 ChunkSleep 防止房主镜头离开后商人在客户端面前停止挥砍或敌意递增
        ChunkSleep.Ignore(gameObject);
        HookTraderPin();
    }

    void OnEnable() {
        if (Net.IsClient) {
            TraderCommit.Ask(this);
        }
    }

    private static bool pinHooked;

    private static void HookTraderPin() {
        if (pinHooked)
            return;
        pinHooked = true;
        Camera.onPostRender += PinOpenTrader;
    }

    /// <summary>
    /// 校验当前钉住的商人仍处于合法可交易状态（非敌对、血量大于 200，且本机角色存活清醒并位于保持打开距离内）。
    /// </summary>
    internal static bool CanKeepTradeMenuOpen(TraderScript trader) {
        if (trader == null || trader.hostile)
            return false;
        if (!trader.TryGetComponent(out BuildingEntity b) || b == null || b.GetHealth() <= 200f)
            return false;
        if (!Util.TryGetLocalBody(out Body local) || local == null || !local.alive || !local.conscious)
            return false;
        return Vector2.Distance(trader.transform.position, local.transform.position) <= GameplayRanges.TraderMenuKeepOpenDistance;
    }

    internal static void ReleasePinnedTradeMenu(PlayerCamera cam) {
        currentTrader = null;
        if (cam == null)
            return;
        cam.currentTrader = null;
        if (cam.tradeMenu != null && cam.tradeMenu.activeSelf)
            cam.ToggleTradeMenu();
    }

    private static void PinOpenTrader(Camera _) {
        PlayerCamera cam = PlayerCamera.main;
        if (!Util.IsInTradingMenu() || cam is null)
            return;
        if (!CanKeepTradeMenuOpen(currentTrader)) {
            ReleasePinnedTradeMenu(cam);
            return;
        }
        cam.currentTrader = currentTrader;
        TraderPinLog.Sample("pin");
        TrackerTrader tt = currentTrader.GetOrAddComponent<TrackerTrader>();
        tt.focused_body = Util.GetLocalBody();
    }

    void Start() {
        if (Net.IsRunning && Net.IsClient) {
            StartCoroutine(AskWhenReady());
        }
        if (Net.IsRunning && Net.IsServer) {
            StartCoroutine(HostRefresh());
        }
        if (og.MoveRange.min == 0f && og.MoveRange.max == 0f) {
            og.MoveRange = new RangeF(og.transform.position.x - 5f, og.transform.position.x + 5f);
        }
        StartCoroutine(ScanForNearestBody());
    }

    void LateUpdate() {
        if (!Net.IsRunning)
            return;

        if (og.hostile) {
            build.cantHit = false;
            if (!gameObject.activeSelf)
                gameObject.SetActive(true);
            ChunkSleep.Ignore(gameObject);
        }

        // 原版 OnWillRenderObject 无距离限制直接惩罚 PlayerCamera.main.body；联机提前在此接管并按摊位 16 格范围结算目击者与凶手罪责
        ClaimDeathGuilt();

        if (Net.IsServer && is_registered) {
            if (doublesendRepState > 0f) {
                doublesendRepState -= Time.deltaTime;
                if (doublesendRepState < 0f) {
                    Server_AnnounceTraderReputationState();
                    doublesendRepState = -900f;
                }
            }
        }
    }

    const float TraderDeathGuiltRange = 16f;

    // 替代旧版 DoMoodPenaltyInRadius(focused_body, ...)：既防止远程爆炸致死时 focused_body == null 抛 NRE，又确保真正在摊位行凶的玩家不漏扣创伤/心情
    internal void ClaimDeathGuilt() {
        if (!Net.IsRunning || og == null || build == null || build.GetHealth() >= 200f)
            return;
        Traverse flag = Traverse.Create(og).Field("didDeathMoodDebuff");
        if ((bool)flag.GetValue())
            return;

        flag.SetValue(true);
        build.description = Locale.GetBuilding("traderdscdead");
        build.fullName = Locale.GetBuilding("corpseseen");
        UsableObject use = GetComponent<UsableObject>();
        if (use != null)
            Destroy(use);
        if (!Net.IsServer)
            return;

        og.DropInventory();
        StampGrief();
        Vector2 pos = transform.position;
        NetBody nearest = null;
        float nearestSqr = float.PositiveInfinity;
        foreach (Body b in griefNear) {
            if (b == null || !b.alive || !b.TryGetNetBody(out NetBody witness) || !witness.IsPlayer)
                continue;
            float sqr = ((Vector2)b.transform.position - pos).sqrMagnitude;
            if (sqr < nearestSqr) {
                nearestSqr = sqr;
                nearest = witness;
            }
            if (b.charType == 2)
                continue;
            b.happiness -= 10f;
            b.traumaAmount += 30f;
            float sick = 30f;
            if (b.mindWipe != null)
                sick *= 0.5f;
            b.sicknessAmount += sick;
            witness.last_mood = b.happiness;
            b.StartCoroutine(b.Cry());
            if (b.talker != null)
                b.talker.Talk(Locale.GetCharacter("murderregret"));
            MedicalSync.Server_QueueSendCharacterHealth(witness);
        }
        if (nearest != null && nearest.body != null) {
            nearest.body.skills.AddExp(1, 60f);
        }
        Body local = Util.GetLocalBody();
        if (local != null && griefNear.Contains(local)) {
            SteamManager.TryUnlockAchievement("MYSURVIVALOVERYOURS");
        }
    }

    void StampGrief() {
        griefNear.Clear();
        Collider2D[] hits = Physics2D.OverlapCircleAll(transform.position, TraderDeathGuiltRange, LayerMask.GetMask("Body", "Limb"));
        for (int i = 0; i < hits.Length; i++) {
            Collider2D hit = hits[i];
            if (hit == null)
                continue;
            Body body = hit.GetComponent<Body>();
            if (body == null) {
                Limb limb = hit.GetComponent<Limb>();
                if (limb != null)
                    body = limb.body;
            }
            if (body == null || !body.alive)
                continue;
            griefNear.Add(body);
        }
    }

    private IEnumerator ScanForNearestBody() {
        while (gameObject && og) {
            if (Net.IsRunning) {
                bgo.SetNearestBody();
                if (tracker && !tracker.isNearby) {
                    yield return new WaitForSecondsRealtime(2.1021504f);
                }
            }
            yield return new WaitForSecondsRealtime(0.1021504f);
        }
    }

    private IEnumerator AskWhenReady() {
        while (Net.IsClient && Net.IsRunning && !received_their_inv) {
            SyncInfo sync = si;
            if (sync != null && sync.syncId != 0) {
                TraderCommit.Ask(this);
            }
            yield return new WaitForSecondsRealtime(0.5f);
        }
    }

    // 服务端每秒向范围内客户端同步一次摊位状态，消除声望、库存与敌意进度长期漂移
    private IEnumerator HostRefresh() {
        yield return new WaitForSecondsRealtime(Random.Range(0.05f, 1f));
        while (this && og) {
            if (Net.IsServer && Net.IsRunning && is_registered) {
                TraderCommit.Publish(og);
            }
            yield return new WaitForSecondsRealtime(1f);
        }
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Start))]
static class TraderScript_Start_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        if (!Net.IsRunning || !Net.IsClient)
            return true;
        __instance.standAmount = 0f;
        __instance.items ??= new List<TraderItem>();
        return false;
    }

    static void Postfix(TraderScript __instance) {
        __instance.GetOrAddComponent<TrackerTrader>();
        if (Net.IsRunning) {
            __instance.farEnoughToMove = true;
            if (!Net.IsClient) {
                __instance.didMove = false;
                TraderCommit.Publish(__instance);
            }
        }
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.DropInventory))]
static class TraderScript_DropInventory_MultiplayerPatch {
    static bool Prefix() {
        return !(Net.IsRunning && Net.IsClient);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.OnUse))]
static class TraderScript_OnUse_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        PlayerCamera.main.currentTrader = __instance;
        TrackerTrader.currentTrader = __instance;
        TraderPinLog.Sample("use");
        if (Net.IsRunning && Net.IsClient && !Util.IsInTradingMenu()) {
            TraderCommit.Ask(__instance.GetOrAddComponent<TrackerTrader>());
        }
        return true;
    }
}

/// <summary>
/// 拦截 <see cref="TraderScript"/> 渲染周期：客户端回退本地敌意自增以服从服务端快照，服务端监测敌意阈值阶段跃迁并及时推送。
/// </summary>
[HarmonyPatch(typeof(TraderScript), "OnWillRenderObject")]
static class TraderScript_OnWillRender_PinLog {
    static readonly Dictionary<int, int> HostilityStage = new();
    static readonly Dictionary<int, int> RanFrame = new();
    static readonly Dictionary<int, int> ClimbedFrame = new();
    static readonly Dictionary<int, float> ClimbedValue = new();
    static int _stagePruneAt = StaleIds.FirstThreshold;
    static int _framePruneAt = StaleIds.FirstThreshold;

    // 相机渲染阶段已执行过 OnWillRenderObject 时记录当前帧号，供屏幕外模拟 Tick 判重跳过
    internal static bool RanThisFrame(TraderScript trader) {
        if (trader == null)
            return false;
        return RanFrame.TryGetValue(trader.GetInstanceID(), out int frame) && frame == Time.frameCount;
    }

    static int StageOf(float hostility, float reputation) {
        if (hostility >= 100f)
            return 3;
        if (hostility > 55f)
            return 2;
        if (hostility > 30f)
            return 1;
        if (reputation < 30f)
            return 0;
        return -1;
    }

    // 声望低于 30 且 12 格内有存活玩家时每秒递增 5 点敌意；房主镜头不在现场时由此推进怒气与警告台词
    internal static void EnsureClimb(TraderScript trader) {
        if (!Net.IsRunning || !Net.IsServer || trader == null)
            return;
        BuildingEntity build = trader.GetComponent<BuildingEntity>();
        if (build == null || build.GetHealth() <= 200f)
            return;
        if (!trader.hostile && trader.reputation >= 30f)
            return;
        Body near = TrackerTrader.ClosestLiving(trader.transform.position);
        if (near == null || Vector2.Distance(trader.transform.position, near.transform.position) >= 12f)
            return;

        int id = trader.GetInstanceID();
        float stepped = Mathf.MoveTowards(trader.hostility, 100f, Time.deltaTime * 5f);
        trader.hostility = stepped;
        if (stepped > 30f && trader.hostileLineTime < 1) {
            trader.hostileLineTime = 1;
            trader.sitTime = Time.time;
            trader.standTarget = 1f;
            if (trader.talker != null)
                trader.talker.Talk(Locale.GetCharacter("traderwarning", trader.character));
        }
        if (stepped > 55f && trader.hostileLineTime < 2) {
            trader.hostileLineTime = 2;
            if (trader.talker != null)
                trader.talker.Talk(Locale.GetCharacter("traderwarning", trader.character));
        }
        if (stepped >= 100f && trader.hostileLineTime < 3) {
            trader.hostileLineTime = 3;
            if (trader.talker != null)
                trader.talker.Talk(Locale.GetCharacter("tradercombat", trader.character));
        }
        ClimbedFrame[id] = Time.frameCount;
        ClimbedValue[id] = trader.hostility;
        PublishStage(trader);
    }

    internal static void HoldClimb(TraderScript trader) {
        if (trader == null || !ClimbedFrame.TryGetValue(trader.GetInstanceID(), out int frame) || frame != Time.frameCount)
            return;
        if (ClimbedValue.TryGetValue(trader.GetInstanceID(), out float value))
            trader.hostility = value;
    }

    internal static void PublishStage(TraderScript trader) {
        if (!Net.IsRunning || !Net.IsServer || trader == null)
            return;
        int key = trader.GetInstanceID();
        StaleIds.Prune<TraderScript, int>(HostilityStage, ref _stagePruneAt);
        int stage = StageOf(trader.hostility, trader.reputation);
        bool had = HostilityStage.TryGetValue(key, out int prev);
        if (!had || prev != stage) {
            HostilityStage[key] = stage;
            if (stage >= 0 && (!had || stage > prev)) {
                TraderCommit.Publish(trader);
            }
        }
    }

    private static void Prefix(TraderScript __instance, out (float rep, float host) __state) {
        if (!Net.IsRunning) {
            __state = default;
            return;
        }
        if (Net.IsClient) {
            TraderWindup.Before(__instance);
        }
        __state = __instance == null ? default : (__instance.reputation, __instance.hostility);
        if (__instance != null) {
            StaleIds.Prune<TraderScript, int>(RanFrame, ref _framePruneAt);
            RanFrame[__instance.GetInstanceID()] = Time.frameCount;
            TrackerTrader guilt = __instance.GetComponent<TrackerTrader>();
            if (guilt != null)
                guilt.ClaimDeathGuilt();
        }
        // 原版 body getter 固定返回相机角色；联机服务端改为定位摊位最近存活玩家，客户端绑定本地角色
        if (__instance != null && TraderCommit.Actor == null) {
            TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
            if (Net.IsServer) {
                Body near = TrackerTrader.ClosestLiving(__instance.transform.position);
                if (near != null)
                    tt.bgo.body = near;
            } else {
                Body local = Util.GetLocalBody();
                if (local != null)
                    tt.bgo.body = local;
            }
        }
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }

    // 使用 ref 接收 ValueTuple __state，消除 BepInEx.Analyzers Harmony003 非 ref 结构体访问告警
    private static void Postfix(TraderScript __instance, ref (float rep, float host) __state) {
        if (!Net.IsRunning)
            return;
        // 客户端本地敌意涨势上限受控于服务端快照，声望完全以服务端为准
        if (Net.IsClient && __instance != null) {
            __instance.reputation = __state.rep;
            TraderWindup.After(__instance);
        }
        if (Net.IsServer && __instance != null) {
            if (PlayerCamera.main != null && PlayerCamera.main.currentTrader == __instance && !Util.IsInTradingMenu()) {
                Body local = Util.GetLocalBody();
                if (local == null || Vector2.Distance(__instance.transform.position, local.transform.position) >= GameplayRanges.PlayerInteractDistance) {
                    PlayerCamera.main.currentTrader = null;
                }
            }
            // 同帧已由 EnsureClimb 推进过敌意时保持该值，避免相机渲染二次累加
            HoldClimb(__instance);
            PublishStage(__instance);
        }
        TraderPinLog.AfterWillRender(__instance);
        TraderPinLog.Hold(PlayerCamera.main);
    }
}

static class TraderPinLog {
    static string lastId = "";
    static bool lastMenu;
    static bool haveSample;
    static string lastPainted = "";
    static string lastOpened = "";
    static bool haveMismatch;
    static bool loggedMiss;

    static string SyncText(TraderScript trader) {
        if (trader == null)
            return "0";
        if (!NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out SyncInfo si) || si == null) {
            TrackerTrader tt = trader.GetComponent<TrackerTrader>();
            if (tt != null && tt.si != null) {
                si = tt.si;
            }
        }
        return si == null ? "none" : si.syncId.ToString();
    }

    internal static void Hold(PlayerCamera cam) {
        if (!Net.IsRunning || cam == null || cam.tradeMenu == null || !cam.tradeMenu.activeSelf)
            return;

        TraderScript opened = TrackerTrader.currentTrader;
        if (opened == null) {
            if (!loggedMiss) {
                loggedMiss = true;
                ModLog.Error("[traderpin] hold missed, opened trader is null", LogDomain.Trader);
            }
            TrackerTrader.ReleasePinnedTradeMenu(cam);
            return;
        }
        if (!TrackerTrader.CanKeepTradeMenuOpen(opened)) {
            TrackerTrader.ReleasePinnedTradeMenu(cam);
            return;
        }
        cam.currentTrader = opened;
    }

    internal static void Sample(string site) {
        if (!Net.IsRunning || PlayerCamera.main == null)
            return;
        string id = SyncText(PlayerCamera.main.currentTrader);
        bool menu = Util.IsInTradingMenu();
        if (haveSample && id == lastId && menu == lastMenu)
            return;
        haveSample = true;
        lastId = id;
        lastMenu = menu;
        ModLog.Flow(LogDomain.Trader, "TraderPin.Sample", $"{site} sync={id} menu={(menu ? 1 : 0)}");
    }

    internal static void AfterWillRender(TraderScript writer) {
        if (!Net.IsRunning || PlayerCamera.main == null || PlayerCamera.main.currentTrader != writer || !Util.IsInTradingMenu())
            return;
        string painted = SyncText(writer);
        string opened = SyncText(TrackerTrader.currentTrader);
        if (painted == opened) {
            haveMismatch = false;
            return;
        }
        if (haveMismatch && painted == lastPainted && opened == lastOpened)
            return;
        haveMismatch = true;
        lastPainted = painted;
        lastOpened = opened;
        ModLog.Flow(LogDomain.Trader, "TraderPin.Mismatch", $"painted={painted} opened={opened}");
    }

    internal static void LogRefreshFault(PlayerCamera cam) {
        if (cam == null || cam.tradeMenu == null || !cam.tradeMenu.activeSelf)
            return;
        TraderScript trader = cam.currentTrader;
        if (trader == null) {
            ModLog.Error("[traderui] refresh currentTrader=null menu=open", LogDomain.Trader);
            return;
        }
        string id = SyncText(trader);
        if (trader.items == null) {
            ModLog.Error($"[traderui] refresh sync={id} items=null", LogDomain.Trader);
            return;
        }
        if (trader.collapsedCategories == null) {
            ModLog.Error($"[traderui] refresh sync={id} collapsedCategories=null items={trader.items.Count}", LogDomain.Trader);
            return;
        }
        if (cam.traderInventory == null) {
            ModLog.Error($"[traderui] refresh sync={id} traderInventory=null items={trader.items.Count}", LogDomain.Trader);
            return;
        }
        for (int i = 0; i < trader.items.Count; i++) {
            TraderItem item = trader.items[i];
            if (item == null) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}]=null", LogDomain.Trader);
                return;
            }
            if (string.IsNullOrEmpty(item.id)) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}] id empty", LogDomain.Trader);
                return;
            }
            GameObject prefab = Resources.Load(item.id) as GameObject;
            if (prefab == null) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}] id={item.id} prefab=null", LogDomain.Trader);
                return;
            }
            SpriteRenderer rend = prefab.GetComponent<SpriteRenderer>();
            if (rend == null || rend.sprite == null) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}] id={item.id} sprite=null", LogDomain.Trader);
                return;
            }
            if (rend.sprite.texture == null) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}] id={item.id} texture=null", LogDomain.Trader);
                return;
            }
            if (Item.GetItem(item.id) == null) {
                ModLog.Error($"[traderui] refresh sync={id} items[{i}] id={item.id} iteminfo=null", LogDomain.Trader);
                return;
            }
        }
    }
}

/// <summary>
/// 商人交互距离与状态校验单例。
/// </summary>
public class TraderSync : CasualtiesTogetherSingleton {
    public static bool Server_TraderInteractionCheck(
        knetid plrid,
        knetid traderid,
        in string interaction_name_for_log,
        out ScavPlayer plr,
        out SyncInfo trader_si,
        out TrackerTrader trader_tracker) {
        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(plrid, out plr, out Body actorBody) &&
            actorBody.conscious &&
            NetObjectRegistry.TryGetSyncInfoLive(traderid, out trader_si) &&
            trader_si.IsTrader() &&
            KM.DistanceCheck(actorBody, trader_si, GameplayRanges.ServerExchangeToleranceDistance)) {
            trader_tracker = trader_si.trader.GetOrAddComponent<TrackerTrader>();
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(trader_tracker.transform.position, $"S: trader {interaction_name_for_log} - {plr.playerName} -> {trader_si}");
            return true;
        }

        trader_si = null;
        trader_tracker = null;
        log.serverdeny($"trader {traderid} {interaction_name_for_log} for {plr}");
        return false;
    }

    internal static void SendTraderState(knetid clientId) {
        if (Net.IsClient || clientId == default)
            return;
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out Body body) || body == null)
            return;

        Vector2 pos = body.transform.position;
        int sent = 0;
        int skipped = 0;
        foreach (SyncInfo si in NetObjectRegistry.SyncRegistry.Values) {
            if (si == null || si.go == null || si.IsDestroyed || !si.IsTrader() || si.trader == null)
                continue;
            if (!TraderCommit.InRange(si.trader.transform.position, pos)) {
                skipped++;
                continue;
            }
            sent++;
            TraderCommit.PublishTo(si.trader, clientId);
        }
        ModLog.Flow(LogDomain.Trader, "TraderSync.SendTraderState", $"client={clientId} sent={sent} skipped={skipped}");
    }
}
