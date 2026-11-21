using System;
using LiteEntitySystem;
using LiteEntitySystem.Extensions;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together {
    /// <summary>
    /// 水壶/注射器等液体容器同步列表元素。
    /// </summary>
    public struct LiquidStackPart : IEquatable<LiquidStackPart> {
        public byte LiquidId;
        public float LiquidMl;

        public static ushort Pack(float amount) {
            if (amount <= 0f) {
                return 0;
            }
            if (amount < 1f) {
                return 1;
            }
            int ml = Mathf.RoundToInt(amount);
            if (ml < 1) {
                ml = 1;
            }
            if (ml > ushort.MaxValue) {
                ml = ushort.MaxValue;
            }
            return (ushort)ml;
        }

        public bool Equals(LiquidStackPart other) => other.LiquidId == LiquidId && other.LiquidMl == LiquidMl;

        public override bool Equals(object obj) => obj is LiquidStackPart other && Equals(other);

        public override int GetHashCode() => HashCode.Combine(LiquidId, LiquidMl);
    }

    /// <summary>
    /// 物品、容器、建筑与世界交互实体同步核心（SyncVar 声明、生命周期与状态查询部分）。
    /// 注意：LiteEntitySystem 按反射元数据声明顺序建立 SyncVar 索引，本类所有 SyncVar / SyncString / SyncList 必须且只能集中声明在本文件中，严禁调整顺序或在分部文件中增删同步字段。
    /// </summary>
    [EntityFlags(EntityFlags.UpdateOnClient)]
    public partial class SyncInfo : PredictableEntityLogic {
        public SyncInfo(EntityParams entityParams) : base(entityParams) {
            _scale.Value = Vector2.zero;
            condition.Value = 1;
        }

        public double lastPositionUpdateTime = 0;
        private bool poseFresh;
        private bool poseHold;
        private float poseHoldUntil;
        private bool poseSeen;
        private byte posePrevData;

        private void NotePositionSync() {
            lastPositionUpdateTime = Time.realtimeSinceStartupAsDouble;
            poseFresh = true;
            poseHold = false;
            poseHoldUntil = 0f;
        }

        private bool _poseGlide;
        private ushort _poseTick;
        private Vector2 _poseFrom;
        private Vector2 _poseTo;
        private float _poseFromAng;
        private float _poseToAng;
        private float _poseStart;
        private float _poseDur;
        private float _poseSample;

        public readonly SyncString resourceId = new();

        [SyncVarFlags(SyncFlags.Interpolated | SyncFlags.AlwaysRollback)]
        public SyncVar<Vector2_4byte_512> _position;
        [SyncVarFlags(SyncFlags.Interpolated)]
        public SyncVar<FloatAngle> _rotation;

        public SyncVar<Vector2_4byte_200> _velocity;

        private readonly SyncList<LiquidStackPart> _liquidStack = new();

        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<byte> container_data1; // is 0 when no container
        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<knetid> container_netId;
        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<byte> container_ver;
        public SyncVar<bool> rb_dynamic;
        public SyncVar<bool> itemLock;

        public SyncVar<Vector2> _scale; // default is 1,1

        private const float angvel_to_short = 1f / 200f * (float)short.MaxValue;
        public SyncVar<short> angvel_quantized; // make it max 200

        public float angvel {
            get => (float)angvel_quantized / angvel_to_short;
            set => angvel_quantized.Value = (short)Mathf.Clamp(value * angvel_to_short, short.MinValue, short.MaxValue);
        }

        [SyncVarFlags(SyncFlags.AlwaysRollback)]
        public SyncVar<float> condition; // aka health for buildings

        public SyncVar<byte> compressedboughtitemtime;
        public float boughtitemtime {
            get => compressedboughtitemtime.Value / 0.85f;
            set => compressedboughtitemtime.Value = (byte)(Mathf.Clamp(value, 0, 300f) * 0.85f);
        }

        public SyncVar<byte> compressedwetTime;
        public float wetTime {
            get => compressedwetTime.Value / 255f * 125f;
            set => compressedwetTime.Value = (byte)(Mathf.Clamp(value, 0, 125f) / 125f * 255f);
        }

        public SyncVar<bool> freshitemdrop;
        public SyncVar<bool> favourited;
#if FULLGAME
        public readonly SyncString itemLabel = new();
        public SyncVar<byte> colorSwatch;
#endif

        public enum CurrentBattery : byte {
            None,
            smallbattery,
            mediumbattery,
            largebattery
        }

        public SyncVar<byte> current_battery;

        public SyncVar<byte> specialdata1;
        public SyncVar<float> specialdata2; // data0 for customitembehavior thing
        public SyncVar<byte> rounds;

        public SyncVar<Vector2_4byte_512> spider_target;
        public SyncVar<float> spider_biteCooldown;
        public SyncVar<float> spider_stunTime;

        public SyncVar<Vector2_4byte_512> trader_desiredpos;

        public SyncVar<bool> gun_racked;
        public SyncVar<bool> gun_safe;
        public SyncVar<bool> gun_hasMag;
        public SyncVar<bool> gun_roundInChamber;
        public SyncVar<bool> gun_roundInChamber_is_casing;

        public SyncVar<bool> building_backgroundified;

        [SyncVarFlags(SyncFlags.Interpolated)]
        public SyncVar<Vector2_4byte_512> grabberplant_tipPos;

        protected override void OnConstructed() {
            CreateObject();
#if !RELEASE
            log.l($"CREATED SyncInfo: resourceId: {resourceId.Value} \tId:{Id} Owner:{OwnerId}");
#endif
            NetObjectRegistry.NetIdToSyncInfoDict[Id] = this;

            if (go == null) {
                if (string.IsNullOrEmpty(resourceId.Value) && Net.IsServer) {
                    Destroy();
                }
                if (Net.IsClient) {
                    nullGoRetryAt = Time.unscaledTime + NullGoRetryDelay;
                }
            } else if (Net.IsServer) {
                WriteObjectIntoPacket();
            }

            lastUpdateTick = (ushort)(Net.ServerTick - 1);
            useIgnoreArmed = false;
            pendingAcks = 0;
            pendingAckDeadline = 0f;
            if (Net.IsServer && go != null && container != null) {
                KrokV5Optimization.FixInvSync.NoteContainerVersion(Id, Version);
            }
        }

        private bool CheckIgnore() {
            prev_was_ignored = last_was_ignored;
            last_was_ignored = IsIgnored();
            if (last_was_ignored) {
                return false;
            }
            useIgnoreArmed = false;
            lastUpdateTick = (ushort)(Net.ServerTick - 1);
            return true;
        }

        protected override void RegisterRPC(ref RPCRegistrator r) {
            base.RegisterRPC(ref r);

            r.BindOnChange(this, ref _position, _ => NotePositionSync(), BindOnChangeFlags.ExecuteOnSync);
            r.BindOnChange(this, ref _rotation, _ => OnTransformUpdate(), BindOnChangeFlags.ExecuteOnSync);
            r.BindOnChange(this, ref _scale, _ => OnTransformUpdate(), BindOnChangeFlags.ExecuteOnSync);
        }

        private void OnTransformUpdate() {
        }

        /// <summary>
        /// 当 <see cref="SyncInfo"/> 实体被销毁时触发（携带 <c>syncId.id</c>），供义肢修补缓存或预测状态表安全回收条目。
        /// </summary>
        internal static event Action<ushort> OnSyncInfoDestroyed;

        protected override void OnDestroy() {
            if (NetObjectRegistry.NetIdToSyncInfoDict.TryGetValue(Id, out SyncInfo listed) && ReferenceEquals(listed, this)) {
                NetObjectRegistry.NetIdToSyncInfoDict.Remove(Id);
            }
            CombatItemUseSync.ClearHeldState(syncId.id);
            try {
                OnSyncInfoDestroyed?.Invoke(syncId.id);
            } catch (Exception e) {
                ModLog.Exception(LogDomain.ObjectSync, $"SyncInfo.OnDestroy event {this}", e);
            }
            DestroyTrackedGameObject();
        }

        private const float NullGoRetryDelay = 0.4f;
        private float nullGoRetryAt;

        private void RetryNullObject() {
            float now = Time.unscaledTime;
            if (now < nullGoRetryAt) {
                return;
            }

            nullGoRetryAt = now + NullGoRetryDelay;
            CreateObject();
        }

        protected override void Update() {
            base.Update();

            try {
                if (IsClient) {
                    if (go == null && tracker != null) {
                        RebindMesh();
                    }
                    if (go == null) {
                        RetryNullObject();
                    }
                    if (go != null && CheckIgnore()) {
                        bool bound = tracker != null && ReferenceEquals(tracker.syncInfo, this);
                        if (!bound) {
                            if (!CreateObject()) {
                                return;
                            }
                        } else if (BuildingPacketUnchanged()) {
                            return;
                        }
                        // 仅对地面散落物（container_data1 == WorldByte）启用视距外 10-Tick 降频，确保装包或随身携带物品当帧零延迟应用状态变更
                        if (container_data1.Value == ContainerEncoding.WorldByte && tracker != null && !tracker.isVisible) {
                            updateTimer++;
                            if (updateTimer < NetTimingConstants.OffscreenLooseItemUpdateStrideTicks) {
                                return;
                            }
                        }
                        ApplyPacketToObject();
                    }
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.ObjectSync, $"SyncInfo.Update {this}", e);
            }
        }

        private int updateTimer;

        protected override void VisualUpdate() {
        }

        internal void ClientFixedUpdate() {
            UpdateTransform();
        }

        #region KROK_OLD_SYNCINFO

        public string GetItemID() {
            if (go != null) {
                string itemId = GetItemID(go);
                if (!string.IsNullOrEmpty(itemId)) {
                    resourceId.Value = itemId;
                }
            }
            return resourceId.Value;
        }

        public static string GetItemID(GameObject go) {
            if (go.TryGetComponent(out Item item)) {
                return item.id;
            }
            if (go.TryGetComponent(out BuildingEntity b)) {
                return b.id;
            }
            return null;
        }

        public bool objectWasSpawned;
        public bool prev_was_ignored;
        public bool last_was_ignored;
        public ushort lastUpdateTick;
        private bool useIgnoreArmed;
        public knetid syncId => Id;
        public GameObject go;
        public SyncInfoGameObjectTracker tracker;

        public bool relaxed_combat_sync;

        public Vector2 transformPosition => go.transform.position;

        public Item item {
            get {
                if (go == null) {
                    return null;
                }
                EnsureParts();
                return _itemLive && _item != null ? _item : null;
            }
        }

        // 客户端入图等待 CreateObject / NullGoRetryDelay 期间 go 可能暂为 null（Issue #50）：所有组件查询必须前置判空，杜绝异步中继包触发 NRE
        public Container container => go != null ? go.GetComponent<Container>() : null;
        public BuildingEntity building => go != null ? go.GetComponent<BuildingEntity>() : null;
        public TraderScript trader => go != null ? go.GetComponent<TraderScript>() : null;
        public SpiderHandler spider => go != null ? go.GetComponent<SpiderHandler>() : null;
        public GunScript gun => go != null ? go.GetComponent<GunScript>() : null;
        public AmmoScript ammo => go != null ? go.GetComponent<AmmoScript>() : null;
        public WaterContainerItem liquidcontainer => go != null ? go.GetComponent<WaterContainerItem>() : null;

#if DEBUG
        public double deleted_time;
        public bool is_deleted;
#endif

        public bool IsItem() => go != null && go.GetComponent<Item>() != null;
        public bool CanBeUsedToAmputate() => IsItem() && item != null && Item_DoAmputate_MultiplayerPatch.ItemsThatCanAmputate.Contains(item.id);
        public bool IsContainer() => container != null;
        public bool IsBuilding() => go != null && go.GetComponent<BuildingEntity>() != null;
        public bool IsTrader() => IsBuilding() && trader != null;
        public bool IsGun() => gun != null;
        public bool IsAmmo() => ammo != null;
        public bool IsLiquidContainer() => liquidcontainer != null;
        public bool IsSpider() => spider != null;
        public bool IsAED() => IsItem() && item != null && item.IsAED();
        public bool IsManualDefibrillator() => IsItem() && item != null && item.IsManualDefibrillator();

        private const int MaxIgnoreLead = 16384;
        private int pendingAcks;
        private float pendingAckDeadline;

        public void ArmPredictedUse() {
            // 每次实际发出的使用请求都必须独立计入待确认深度；但旧版窗口下维持 useIgnoreArmed 防空体力连点无限顺延 tick 截止期
            AwaitServerAck();
            if (useIgnoreArmed) {
                return;
            }
            useIgnoreArmed = true;
            SetIgnoreTimeForRoundTrip();
        }

        /// <summary>
        /// 客户端等待服务端 <c>use_ack</c> 确认（PR #61）：仅在 <see cref="UseAck.ServerSupportsUseAck"/> 协商通过时启用自适应锁，
        /// 连入未发探针的旧版 <c>v5.5.3.0</c> 房主时自动回退至 <see cref="SetIgnoreTimeForRoundTrip"/> 短窗口，避免卡死 7.5 秒。
        /// </summary>
        public void AwaitServerAck() {
            if (!Net.IsRunning || !Net.IsClient) {
                return;
            }
            if (!UseAck.ServerSupportsUseAck) {
                if (!useIgnoreArmed) {
                    SetIgnoreTimeForRoundTrip();
                }
                return;
            }
            float rtt = Mathf.Max(0f, ClientMain.RTT_IN_SECONDS_with_margin);
            float wait = 1.5f + Mathf.Min(rtt * 2f, 6f);
            pendingAcks++;
            pendingAckDeadline = Mathf.Max(pendingAckDeadline, Time.realtimeSinceStartup + wait);
        }

        internal void OnServerAck() {
            if (pendingAcks <= 0) {
                return;
            }
            pendingAcks--;
            if (pendingAcks > 0) {
                return;
            }
            useIgnoreArmed = false;
            // 服务端裁决已落定，但携带最新权威状态的 LES 快照可能比可靠通道的 use_ack 晚到 1~2 个发送周期；保留 0.25s（至少 2 ticks）短尾缓冲并同步收敛手电预测锁
            int span = Mathf.Clamp((int)(0.25f * Net.ServerEntitySendRate), 2, MaxIgnoreLead);
            ushort newTick = (ushort)(Net.ServerTick + span);
            if (LiteEntitySystem.Utils.SequenceDiff(newTick, lastUpdateTick) > 0) {
                lastUpdateTick = newTick;
            }
            CombatItemUseSync.OnServerAck(syncId.id);
        }

        public void SetIgnoreTimeForRoundTrip(float additional_time_seconds = 0.3f) {
            float extra = Mathf.Clamp(ClientMain.RTT_IN_SECONDS_with_margin, 0f, 2f);
            additional_time_seconds = Mathf.Max(0f, additional_time_seconds + extra);
            int span = Mathf.Clamp((int)(additional_time_seconds * Net.ServerEntitySendRate), 1, MaxIgnoreLead);
            ushort newTick = (ushort)(Net.ServerTick + span);
            if (LiteEntitySystem.Utils.SequenceDiff(newTick, lastUpdateTick) > 0) {
                lastUpdateTick = newTick;
            }
        }

        public bool IsIgnored() {
            if (pendingAcks > 0) {
                if (UseAck.ServerSupportsUseAck && Time.realtimeSinceStartup < pendingAckDeadline) {
                    return true;
                }
                pendingAcks = 0;
            }
            int lead = LiteEntitySystem.Utils.SequenceDiff(lastUpdateTick, Net.ServerTick);
            return lead >= 0 && lead <= MaxIgnoreLead;
        }

        public override string ToString() {
            if (go == null) {
                string res = GetItemID();
                return string.IsNullOrEmpty(res)
                    ? $"SyncInfo(NULL_OBJ, id: {syncId})"
                    : $"SyncInfo(NULL_OBJ, id: {syncId}, res: {res})";
            }
            return $"SyncInfo({(IsBuilding() ? "building" : (IsItem() ? "item" : "UNKNOWN_TYPE"))}, id: {syncId}, res: {GetItemID() ?? go.ToString()})";
        }

        #endregion
    }
}
