using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 8 位紧凑布尔掩码结构体，实现 <see cref="INetSerializable"/> 单字节网络传输。
/// </summary>
public struct Bitset8 : INetSerializable {
    public byte bayt;

    public void Deserialize(NetDataReader reader) {
        reader.Get(out bayt);
    }

    public void Serialize(NetDataWriter writer) {
        writer.Put(bayt);
    }

    public Bitset8(byte bayt) {
        this.bayt = bayt;
    }

    public Bitset8(params bool[] bits) {
        bayt = 0;
        for (int i = 0; i < bits.Length; ++i) {
            this[i] = bits[i];
        }
    }

    public bool this[int position] {
        get => Test(position);
        set => Set(position, value);
    }

    public static bool operator ==(Bitset8 a, Bitset8 b) => a.bayt == b.bayt;
    public static bool operator !=(Bitset8 a, Bitset8 b) => !(a == b);
    public static Bitset8 operator &(Bitset8 a, Bitset8 b) => new Bitset8((byte)(a.bayt & b.bayt));
    public static Bitset8 operator |(Bitset8 a, Bitset8 b) => new Bitset8((byte)(a.bayt | b.bayt));
    public static Bitset8 operator ^(Bitset8 a, Bitset8 b) => new Bitset8((byte)(a.bayt ^ b.bayt));
    public static Bitset8 operator ~(Bitset8 a) => new Bitset8((byte)(~a.bayt));

    public bool Test(int position) {
        byte mask = (byte)(1 << position);
        return (bayt & mask) == mask;
    }

    public void Set(int position) {
        bayt |= (byte)(1 << position);
    }

    public void Set(int position, bool value) {
        if (value)
            Set(position);
        else
            Reset(position);
    }

    public void Reset(int position) {
        bayt &= (byte)(~(1 << position));
    }

    public void Flip(int position) {
        bayt ^= (byte)(1 << position);
    }

    public bool Equals(Bitset8 s) => this == s;
    public override bool Equals(object obj) => obj is Bitset8 other && other == this;
    public override int GetHashCode() => bayt;
    public override string ToString() => Convert.ToString(bayt, 2).PadLeft(8, '0');
}

/// <summary>
/// 标记需要排除在网络实体注册之外的本地附着子物体（如飞船起始门）。
/// </summary>
public class KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE : MonoBehaviour {
    void Start() {
        if (TryGetComponent<SyncInfoGameObjectTracker>(out var t)) {
            if (t.syncInfo != null) {
                t.syncInfo.go = null;
                t.syncInfo.Destroy();
            }
            Destroy(t);
        }
    }
}

/// <summary>
/// 挂载在同步物品与建筑上的本地追踪器：负责按根宿主坐标计算视距/模拟区分级，并在物理步驱动 <see cref="SyncInfo"/> 读写。
/// </summary>
public class SyncInfoGameObjectTracker : MonoBehaviour {
    public float playerDistanceSqr = 64f;
    public bool isNearby = false;
    public bool isVisible = true;
    public bool isInSimulationRange = true;
    public bool isFar => !isInSimulationRange;

    public knetid syncId;
    public SyncInfo syncInfo;

    static int VisualUpdateSlowerWhenNotVisible = 15;
    int visualUpdateTimer = 0;
    Transform _heldParent;
    bool _held;
    bool _wasLoose;
    int _crystal = -1;
    float distanceCheckTime = 0f;

    internal bool IsCrystal() {
        if (_crystal < 0) {
            _crystal = GetComponent<CrystalEnemy>() != null ? 1 : 0;
        }
        return _crystal == 1;
    }

    public override string ToString() {
        if (this == null)
            return "SyncInfo(NULL)";
        // 当 syncInfo 尚未绑定时统一返回未注册安全描述，防止解引用 null syncInfo 抛 NRE
        if (syncInfo == null) {
            string kind = IsBuilding() ? "building" : (IsItem() ? "item" : "UNKNOWN_TYPE");
            return $"UnregisteredTracker({kind}, id: {syncId}, res: {SyncInfo.GetItemID(gameObject) ?? gameObject.ToString()})";
        }
        return syncInfo.ToString();
    }

    public bool IsItem() => GetComponent<Item>() != null;
    public bool IsContainer() => GetComponent<Container>() != null;
    public bool IsBuilding() => GetComponent<BuildingEntity>() != null;

    void Start() {
        if (syncInfo != null) {
            gameObject.name = syncInfo.ToString();
        }
    }

    public void UpdateDistanceChecks() {
        distanceCheckTime = 0f;

        // 沿父链取根宿主（Container / InventorySlot / Limb / Body）坐标计算视距，防止闭合背包内物品停留在旧拾取点被误判远距
        Vector2 pos = EffectiveWorldPosition();
        isInSimulationRange = Net.IsServer
            ? NetObjectRegistry.ShouldKeepRegistered(gameObject)
            : SharedMain.WorldInSimRange(pos);

        if (!isInSimulationRange) {
            isNearby = false;
            isVisible = false;
            return;
        }

        if (Net.IsServer) {
            var cdist = ScavPlayer.GetDistanceToNearestLivingPlayer(pos);
            playerDistanceSqr = cdist.Item2;
        } else if (Camera.main != null) {
            playerDistanceSqr = KM.DistanceSqr(Camera.main.transform.position, pos);
        }
        isNearby = playerDistanceSqr < GameplayRanges.ObjectNearbyRadiusSqr;
        isVisible = playerDistanceSqr < ItemSync.max_plr_view_distance_sqr;
    }

    Vector2 EffectiveWorldPosition() {
        Transform cursor = transform;
        Transform root = cursor;
        while (cursor.parent != null) {
            Transform p = cursor.parent;
            if (p.GetComponent<Container>() != null
                || p.GetComponent<InventorySlot>() != null
                || p.GetComponent<Limb>() != null
                || p.GetComponent<Body>() != null) {
                root = p;
            }
            cursor = p;
        }
        return root.position;
    }

    bool HeldByBody() {
        Transform parent = transform.parent;
        if (parent == _heldParent)
            return _held;
        _heldParent = parent;
        _held = parent != null && parent.GetComponentInParent<Body>() != null;
        return _held;
    }

    internal void NoteCarry() {
        HeldByBody();
        _wasLoose = transform.parent == null;
    }

    bool LooseAwake() {
        if (transform.parent != null)
            return false;
        Rigidbody2D rb;
        bool has = syncInfo != null && syncInfo.go == gameObject
            ? syncInfo.TryCachedRigidbody(out rb)
            : TryGetComponent(out rb);
        if (!has || rb == null || !rb.simulated || rb.IsSleeping())
            return false;
        return rb.bodyType == RigidbodyType2D.Dynamic;
    }

    void FixedUpdate() {
        TickSync();
    }

    internal void TickSync() {
        if (syncInfo == null)
            return;

        if (!Net.IsServer) {
            // 水晶怪（CrystalEnemy）高速突进时必须每物理步更新姿态，跳过视野外 15 步节流防止突刺位置滞后一个区块
            if (IsCrystal()) {
                visualUpdateTimer = 0;
                syncInfo.ClientFixedUpdate();
                return;
            }
            visualUpdateTimer++;
            if (!isVisible && visualUpdateTimer < VisualUpdateSlowerWhenNotVisible)
                return;
            visualUpdateTimer = 0;
            syncInfo.ClientFixedUpdate();
            return;
        }

        // 服务端每物理步写入水晶怪姿态，供实体网络 Tick 即时下发而非等待慢速区块扫描
        if (IsCrystal()) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }

        // 穿戴与手持物品仅在字段实际发生变更时才执行完整打包，避免 50Hz 空跑反射与液体哈希
        if (HeldByBody()) {
            _wasLoose = false;
            visualUpdateTimer = 0;
            if (!syncInfo.InactiveChanged())
                return;
            syncInfo.WriteObjectIntoPacket();
            return;
        }

        bool loose = transform.parent == null;
        bool released = loose && !_wasLoose;
        _wasLoose = loose;
        if (released || LooseAwake()) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            if (released) {
                // 释放当帧显式标脏位置与旋转，防止原地垂直丢物时量化坐标未变导致客户端卡在 poseHold 悬空
                syncInfo._position.MarkDirty();
                syncInfo._rotation.MarkDirty();
            }
            return;
        }

        if (ChunkStamp.Due(transform.position, out bool inUnion)) {
            visualUpdateTimer = 0;
            syncInfo.WriteObjectIntoPacket();
            return;
        }
        if (inUnion)
            return;

        visualUpdateTimer++;
        if (visualUpdateTimer < VisualUpdateSlowerWhenNotVisible)
            return;
        visualUpdateTimer = 0;
        syncInfo.WriteObjectIntoPacket();
    }

    void OnDestroy() {
        NetObjectRegistry.SyncRegistry.Remove(gameObject);

        if (syncInfo != null && Net.IsServer) {
            if (NetObjectRegistry.KeepFarMesh(syncInfo, gameObject)) {
                syncInfo.tracker = null;
                syncInfo.go = null;
                syncInfo = null;
                return;
            }
            syncInfo.Destroy();
            syncInfo = null;
        }
    }

    void LateUpdate() {
        if (!Net.IsRunning || Net.IsServer || syncInfo == null || !IsCrystal())
            return;
        // 物理帧仅采样快照到达点，渲染帧间由 ClientCrystalPose 平滑插值水晶怪轨迹
        syncInfo.ClientCrystalPose();
    }

    void Update() {
        if (syncInfo == null)
            return;

        distanceCheckTime += Time.unscaledDeltaTime;
        if (distanceCheckTime > 0.1f) {
            UpdateDistanceChecks();
        }
    }

    public void AnimalHit() {
        ModLog.Flow(LogDomain.Combat, "Tracker.AnimalHit", name);
    }
}

/// <summary>
/// 全局网络实体注册表（第 1 部分：会话清理、跨层背包保活、远距释放与存档暂存保护）。
/// </summary>
public partial class NetObjectRegistry : CasualtiesTogetherSingleton {
    public static bool verbose => Multiplayer.verbose;

    const float _FastSyncFrequency = 0.043f;
    const float _DefaultSyncFrequency = 0.211f;
    const float _RareSyncFrequency = 1.894512f;

    public static Dictionary<GameObject, SyncInfo> SyncRegistry = new Dictionary<GameObject, SyncInfo>();
    public static Dictionary<knetid, SyncInfo> NetIdToSyncInfoDict = new Dictionary<knetid, SyncInfo>();

    public static void UnregisterAllObjects() {
        ModLog.Flow(LogDomain.Item, "NetObjectRegistry.UnregisterAllObjects", $"count={SyncRegistry.Count}");
        foreach (SyncInfo si in new List<SyncInfo>(SyncRegistry.Values)) {
            si.Destroy();
        }
        NetIdToSyncInfoDict.Clear();
        SyncRegistry.Clear();
        foreach (var item in FindObjectsOfType<SyncInfoGameObjectTracker>()) {
            item.syncInfo = null;
            Destroy(item);
        }
    }

    public static void WipeSessionEntities() {
        if (!Net.IsServer || Net.ServerEntityManager == null)
            return;

        int items = 0;
        var infos = new List<SyncInfo>(Net.ServerEntityManager.GetEntities<SyncInfo>());
        for (int i = 0; i < infos.Count; i++) {
            SyncInfo si = infos[i];
            if (si == null || si.IsDestroyed)
                continue;
            si.Destroy();
            items++;
        }

        int bodies = 0;
        var bodiesList = new List<SyncBody>(Net.ServerEntityManager.GetEntities<SyncBody>());
        for (int i = 0; i < bodiesList.Count; i++) {
            SyncBody sb = bodiesList[i];
            if (sb == null || sb.IsDestroyed)
                continue;
            sb.Destroy();
            bodies++;
        }

        SyncRegistry.Clear();
        NetIdToSyncInfoDict.Clear();
        foreach (var item in FindObjectsOfType<SyncInfoGameObjectTracker>()) {
            item.syncInfo = null;
            Destroy(item);
        }

        ModLog.Flow(LogDomain.Item, "NetObjectRegistry.WipeSessionEntities", $"items={items} bodies={bodies}");
    }

    public static void UnregisterAllObjectsButKeepInventory() {
        var all = new List<SyncInfo>(SyncRegistry.Values);
        var stay = new HashSet<SyncInfo>();
        var named = new HashSet<int>();
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (!OnABody(si))
                continue;
            stay.Add(si);
            if (TryNet(si, out ushort net, out byte namedVer) && net != 0)
                named.Add(NameKey(net, namedVer));
        }
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (si == null || stay.Contains(si))
                continue;
            if (TryId(si, out ushort id) && named.Contains(NameKey(id, si.Version))) {
                stay.Add(si);
                continue;
            }
            if (si.go != null && IsSaveStateItem(si.go)) {
                ReleaseSaveStateItem(si);
                continue;
            }
            si.Destroy();
        }
        if (Net.IsServer)
            RetieAfterKeep();
    }

    internal static bool ContainerStillNamed(SyncInfo si) {
        if (!TryId(si, out ushort id) || id == 0 || SyncRegistry == null)
            return false;
        byte ver = si.Version;
        foreach (SyncInfo other in SyncRegistry.Values) {
            if (other == null || other == si)
                continue;
            if (TryNet(other, out ushort net, out byte namedVer) && net == id && namedVer == ver)
                return true;
        }
        return false;
    }

    internal static bool KeepFarMesh(SyncInfo si, GameObject go) {
        if (ContainerStillNamed(si))
            return true;
        return go != null && IsSaveStateItem(go);
    }

    static bool OnABody(SyncInfo si) => si != null && InPlayerInventory(si.go);

    static bool InPlayerInventory(GameObject go) {
        if (go == null)
            return false;
        try {
            if (go.GetComponent<Item>() == null || go.transform.parent == null)
                return false;
            return go.GetComponentInParent<Body>() != null;
        } catch (Exception) {
            return false;
        }
    }

    static void ReleaseFarSync(SyncInfo si) {
        if (si == null || si.IsDestroyed)
            return;
        GameObject go = si.go;
        if (go == null) {
            si.Destroy();
            return;
        }
        SyncInfoGameObjectTracker tracker = si.tracker;
        if (tracker == null)
            go.TryGetComponent(out tracker);
        if (tracker != null)
            tracker.syncInfo = null;
        SyncRegistry.Remove(go);
        si.go = null;
        si.Destroy();
    }

    static bool TryId(SyncInfo si, out ushort id) {
        id = 0;
        if (si == null)
            return false;
        try {
            id = si.syncId;
            return id != 0;
        } catch (Exception) {
            return false;
        }
    }

    static int NameKey(ushort id, byte ver) => id | (ver << 16);

    static bool TryNet(SyncInfo si, out ushort net, out byte ver) {
        net = 0;
        ver = 0;
        if (si == null)
            return false;
        try {
            net = si.container_netId.Value;
            ver = si.container_ver.Value;
            return true;
        } catch (Exception) {
            return false;
        }
    }

    static void RetieAfterKeep() {
        foreach (ScavPlayer plr in ScavPlayer.BodyToPlayerDict.Values) {
            if (plr != null && plr.body != null)
                PlayerCheckpoint.RegisterBody(plr.body);
        }
        FixInvSync.Refresh();
        var all = new List<SyncInfo>(SyncRegistry.Values);
        var named = new HashSet<int>();
        for (int i = 0; i < all.Count; i++) {
            if (!OnABody(all[i]))
                continue;
            if (TryNet(all[i], out ushort net, out byte namedVer) && net != 0)
                named.Add(NameKey(net, namedVer));
        }
        for (int i = 0; i < all.Count; i++) {
            SyncInfo si = all[i];
            if (si == null || OnABody(si))
                continue;
            if (si.go != null && IsSaveStateItem(si.go))
                continue;
            if (!TryId(si, out ushort id) || named.Contains(NameKey(id, si.Version)))
                continue;
            if (!si.IsDestroyed)
                si.Destroy();
        }
    }

    static bool IsSaveStateItem(GameObject go) {
        Transform walk = go.transform.parent;
        while (walk != null) {
            if (walk.name != null && walk.name.StartsWith("SAVESTATE_"))
                return true;
            walk = walk.parent;
        }
        return false;
    }

    static void ReleaseSaveStateItem(SyncInfo si) {
        if (si != null && si.go == null)
            si.Destroy();
    }

    public static void DetachSyncKeepMesh(GameObject go) {
        if (!Net.IsServer || go == null)
            return;
        SyncInfo si = null;
        if (SyncRegistry.TryGetValue(go, out SyncInfo listed) && listed != null && !listed.IsDestroyed) {
            si = listed;
        } else if (go.TryGetComponent(out SyncInfoGameObjectTracker existing) && existing.syncInfo != null && !existing.syncInfo.IsDestroyed) {
            si = existing.syncInfo;
        }
        if (si == null)
            return;

        if (go.TryGetComponent(out SyncInfoGameObjectTracker tracker)) {
            tracker.syncInfo = null;
        }
        SyncRegistry.Remove(go);
        si.tracker = null;
        si.go = null;
        if (!si.IsDestroyed) {
            si.Destroy();
        }
    }
}
