using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

/// <summary>
/// 水晶本地第一人称反馈门控（闪屏、音效、震动仅对实际触碰/受击的本地角色生效，兼容 Demo 与 Fullgame 调用）。
/// </summary>
public static class CrystalLocalFx {
    public static Body TriggerBody;
    public static CrystalBehaviour Crystal;

    static readonly HashSet<string> TriggerFeedbackClips = new HashSet<string> {
        "observerlaugh",
        "crystalheal",
        "crystalemp",
        "crystalenemylaugh",
        "crystaltick",
        "Scald",
        "zap",
    };

    public static bool IsTriggerFeedbackClip(string clip) {
        return clip != null && TriggerFeedbackClips.Contains(clip);
    }

    internal static int AllowFlashFrame = -1;
    internal static int BlockFlashFrame = -1;

    public static void Begin(Body body, CrystalBehaviour crystal) {
        TriggerBody = body;
        Crystal = crystal;
        if (crystal != null && body != null) {
            crystal.GetOrAddComponent<BodyGetterOverrider>().body = body;
        }
        if (Net.IsRunning && body != null) {
            int until = Time.frameCount + 2;
            if (PlayerCamera.main != null && body == PlayerCamera.main.body) {
                AllowFlashFrame = until;
            } else {
                BlockFlashFrame = until;
            }
        }
    }

    public static void End() {
        TriggerBody = null;
        Crystal = null;
    }

    public static bool IsLocalTrigger() {
        return TriggerBody != null && Util.IsBodyLocal(TriggerBody);
    }

    public static bool SuppressNonLocalFeedback() {
        if (!Net.IsRunning) {
            return false;
        }
        if (TriggerBody != null) {
            return !Util.IsBodyLocal(TriggerBody);
        }
        if (Crystal != null && Crystal.TryGetComponent(out BodyGetterOverrider bgo) && bgo.body != null) {
            return !Util.IsBodyLocal(bgo.body);
        }
        return false;
    }
}

#if FULLGAME
/// <summary>
/// 正式版水晶权威同步管理器（状态缓存、特效工厂映射与房主序列化捕获分部）：
/// 房主生成水晶后捕获基础外观（Fill）与特殊词条（Specials），在单包权威（<see cref="CrystalLocalAuthority"/>）之外兜底运行时突变与补发。
/// </summary>
public static partial class CrystalAuthority {
    const byte FlagInert = 1;
    const byte FlagFlip = 2;
    const byte FlagHasHeater = 4;
    const byte FlagVolumeLight = 8;
    const byte FlagSpecial = 16;
    const byte FlagGravityForce = 4;

    const string SpecialKey = "CrystalSpecial";
    const string GenDoneKey = "CrystalGenDone";

    struct PosEntry {
        public knetid syncId;
        public byte[] fill;
        public byte[] specials;
    }

    static readonly Dictionary<long, List<PosEntry>> _byPos = new();
    static readonly Dictionary<knetid, byte[]> _fillBySync = new();
    static readonly Dictionary<knetid, byte[]> _specialsBySync = new();
    static readonly HashSet<knetid> _fillApplied = new();
    static readonly HashSet<knetid> _specialsApplied = new();
    static readonly Dictionary<knetid, float> _nextFillAt = new();
    static readonly Dictionary<knetid, float> _nextSpecialAt = new();
    static readonly HashSet<GameObject> _pendingCapture = new();

    public static bool ApplyingAuthority;

    static float _nextBlankScanAt;
    const float BlankScanInterval = 2.5f;
    const int BlankScanBudget = 12;

    static Func<CrystalEffect>[] _factories;
    static Type[] _effectTypes;

    static void Bug(string msg) {
        ModLog.Debug(LogDomain.World, "[CrystalAuthority] " + msg);
    }

    public static long PosKey(Vector2 p) {
        int qx = Mathf.RoundToInt(p.x * 10f);
        int qy = Mathf.RoundToInt(p.y * 10f);
        return ((long)qx << 32) ^ (uint)qy;
    }

    public static long PosKeyOf(GameObject go) {
        if (go == null) {
            return 0;
        }
        return PosKey(go.transform.position);
    }

    static int PosEntryCount() {
        int n = 0;
        foreach (var kv in _byPos) {
            if (kv.Value != null) {
                n += kv.Value.Count;
            }
        }
        return n;
    }

    static void ClearLayerCaches() {
        _byPos.Clear();
        _fillBySync.Clear();
        _specialsBySync.Clear();
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        _pendingCapture.Clear();
        _nextBlankScanAt = 0f;
        CrystalDelayedFx.ClearArmBroadcasts();
        CrystalLocalAuthority.ClearLayerCaches();
    }

    public static void MarkLocalAuthSatisfied(knetid syncId) {
        if (syncId.id == 0) {
            return;
        }
        _fillApplied.Add(syncId);
        _specialsApplied.Add(syncId);
        _nextFillAt.Remove(syncId);
        _nextSpecialAt.Remove(syncId);
    }

    public static void EnsureEffectMapsPublic() {
        EnsureEffectMaps();
    }

    public static CrystalEffect CreateEffectById(int id) {
        EnsureEffectMaps();
        if (id < 0 || _factories == null || id >= _factories.Length || _factories[id] == null) {
            return null;
        }
        return _factories[id]();
    }

    static void EnsureEffectMaps() {
        if (_factories != null) {
            return;
        }
        var list = CrystalBehaviour.possibleEffects;
        _factories = new Func<CrystalEffect>[list.Count];
        _effectTypes = new Type[list.Count];
        for (int i = 0; i < list.Count; i++) {
            _factories[i] = list[i].factory;
            _effectTypes[i] = list[i].factory().GetType();
        }
    }

    public static int EffectIdOf(CrystalEffect effect) {
        if (effect == null) {
            return -1;
        }
        EnsureEffectMaps();
        Type t = effect.GetType();
        for (int i = 0; i < _effectTypes.Length; i++) {
            if (_effectTypes[i] == t) {
                return i;
            }
        }
        return -1;
    }

    public static List<CrystalEffect> GetEffects(CrystalBehaviour crystal) {
        return crystal != null ? crystal.effects : null;
    }

    public static void SetSpecial(GameObject go, bool special) {
        if (go == null) {
            return;
        }
        go.GetOrAddComponent<AnyDataContainerComponent>().Set(SpecialKey, special);
    }

    public static bool IsSpecial(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.TryGetComponent(out AnyDataContainerComponent data) && data.dict.TryGetValue(SpecialKey, out object v) && v is bool b) {
            return b;
        }
        return false;
    }

    public static void MarkGenerationDone(GameObject go) {
        if (go == null) {
            return;
        }
        go.GetOrAddComponent<AnyDataContainerComponent>().Set(GenDoneKey, true);
    }

    public static bool IsGenerationDone(GameObject go) {
        if (go == null) {
            return SharedMain.LocalWorldIsGenerated;
        }
        if (go.TryGetComponent(out AnyDataContainerComponent data) && data.dict.ContainsKey(GenDoneKey)) {
            return true;
        }
        return SharedMain.LocalWorldIsGenerated && !go.TryGetComponent(out CrystalBehaviour _);
    }

    public static bool LooksLikeCrystal(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.TryGetComponent(out CrystalBehaviour _)) {
            return true;
        }
        if (go.TryGetComponent(out BuildingEntity be) && be != null && be.id != null) {
            return be.id.IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0;
        }
        return false;
    }

    public static bool LooksLikeCrystalId(string id) {
        return !string.IsNullOrEmpty(id) && id.IndexOf("crystal", StringComparison.OrdinalIgnoreCase) >= 0;
    }

    static byte[] RemapSyncId(byte[] payload, knetid syncId) {
        if (payload == null || payload.Length < 3) {
            return payload;
        }
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid _);
        int avail = reader.AvailableBytes;
        NetDataWriter headWriter = new NetDataWriter();
        headWriter.Put(syncId);
        byte[] head = headWriter.CopyData();
        if (avail <= 0) {
            return head;
        }
        byte[] result = new byte[head.Length + avail];
        Buffer.BlockCopy(head, 0, result, 0, head.Length);
        Buffer.BlockCopy(reader.RawData, reader.Position, result, head.Length, avail);
        return result;
    }

    static bool PayloadIsSpecial(byte[] fill) {
        if (fill == null || fill.Length < 4) {
            return false;
        }
        try {
            NetDataReader reader = new NetDataReader(fill);
            reader.Get(out knetid _);
            reader.Get(out byte flags);
            return (flags & FlagSpecial) != 0;
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.World, $"CrystalAuthority.PayloadIsSpecial parse failed: {ex.Message}");
            return false;
        }
    }

    static void Store(GameObject go, knetid syncId, byte[] fill, byte[] specials) {
        if (go == null) {
            return;
        }
        long key = PosKeyOf(go);
        if (syncId.id != 0) {
            if (fill != null) {
                fill = RemapSyncId(fill, syncId);
                _fillBySync[syncId] = fill;
            }
            if (specials != null) {
                specials = RemapSyncId(specials, syncId);
                _specialsBySync[syncId] = specials;
            }
        }
        if (!_byPos.TryGetValue(key, out var list) || list == null) {
            list = new List<PosEntry>(2);
            _byPos[key] = list;
        }
        int idx = -1;
        if (syncId.id != 0) {
            for (int i = 0; i < list.Count; i++) {
                if (list[i].syncId.id == syncId.id) {
                    idx = i;
                    break;
                }
            }
        }
        if (idx < 0 && list.Count == 1 && syncId.id != 0) {
            idx = 0;
        }
        if (idx >= 0) {
            var prev = list[idx];
            list[idx] = new PosEntry {
                syncId = syncId.id != 0 ? syncId : prev.syncId,
                fill = fill ?? prev.fill,
                specials = specials ?? prev.specials,
            };
            if (list[idx].fill != null && syncId.id != 0) {
                _fillBySync[syncId] = list[idx].fill;
            }
            if (list[idx].specials != null && syncId.id != 0) {
                _specialsBySync[syncId] = list[idx].specials;
            }
            return;
        }
        list.Add(new PosEntry { syncId = syncId, fill = fill, specials = specials });
    }

    static bool TryFill(knetid syncId, long posKey, out byte[] fill) {
        if (_fillBySync.TryGetValue(syncId, out fill) && fill != null && fill.Length >= 3) {
            return true;
        }
        fill = null;
        if (!_byPos.TryGetValue(posKey, out var list) || list == null) {
            return false;
        }
        for (int i = 0; i < list.Count; i++) {
            if (list[i].syncId.id == syncId.id && list[i].fill != null && list[i].fill.Length >= 3) {
                fill = RemapSyncId(list[i].fill, syncId);
                return true;
            }
        }
        if (list.Count == 1 && list[0].fill != null && list[0].fill.Length >= 3) {
            fill = RemapSyncId(list[0].fill, syncId);
            return true;
        }
        return false;
    }

    static bool TrySpecials(knetid syncId, long posKey, out byte[] specials) {
        if (_specialsBySync.TryGetValue(syncId, out specials) && specials != null && specials.Length >= 3) {
            return true;
        }
        specials = null;
        if (!_byPos.TryGetValue(posKey, out var list) || list == null) {
            return false;
        }
        for (int i = 0; i < list.Count; i++) {
            if (list[i].syncId.id == syncId.id && list[i].specials != null && list[i].specials.Length >= 3) {
                specials = RemapSyncId(list[i].specials, syncId);
                return true;
            }
        }
        if (list.Count == 1 && list[0].specials != null && list[0].specials.Length >= 3) {
            specials = RemapSyncId(list[0].specials, syncId);
            return true;
        }
        return false;
    }

    internal static bool SameBytes(byte[] a, byte[] b) {
        if (ReferenceEquals(a, b)) {
            return true;
        }
        if (a == null || b == null || a.Length != b.Length) {
            return false;
        }
        for (int i = 0; i < a.Length; i++) {
            if (a[i] != b[i]) {
                return false;
            }
        }
        return true;
    }

    public static void HostCapture(GameObject go) {
        if (!Net.IsServer || go == null) {
            return;
        }
        knetid syncId = default;
        if (NetObjectRegistry.TryGetSyncInfo(go, out var si) && si != null) {
            syncId = si.syncId;
        }
        if (!CapturePair(go, syncId, out var fill, out var specials)) {
            _pendingCapture.Add(go);
            return;
        }
        Store(go, syncId, fill, specials);
        _pendingCapture.Remove(go);
        Bug($"capture sync={syncId} special={PayloadIsSpecial(fill)} fillLen={(fill == null ? -1 : fill.Length)} specLen={(specials == null ? -1 : specials.Length)} pos={go.transform.position}");
    }

    public static void HostCaptureAndBroadcast(GameObject go) {
        if (!Net.IsServer || go == null) {
            return;
        }
        HostCapture(go);
        if (!NetObjectRegistry.TryGetSyncInfo(go, out var si) || si == null) {
            return;
        }
        HostCaptureAndBroadcast(go, si);
    }

    public static void HostCaptureAndBroadcast(GameObject go, SyncInfo si) {
        if (!Net.IsServer || go == null || si == null) {
            return;
        }
        if (CrystalLocalAuthority.HasCustomStructuresCrystalAuth(go)) {
            return;
        }
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(go, si);
        if (!CapturePair(go, si.syncId, out var fill, out var specials)) {
            if (TryFill(si.syncId, PosKeyOf(go), out var cached)) {
                fill = cached;
                TrySpecials(si.syncId, PosKeyOf(go), out specials);
            } else {
                _pendingCapture.Add(go);
                return;
            }
        }
        if (_fillBySync.TryGetValue(si.syncId, out var prevFill) && SameBytes(prevFill, fill)
            && (!PayloadIsSpecial(fill) || (_specialsBySync.TryGetValue(si.syncId, out var prevSpec) && SameBytes(prevSpec, specials)))) {
            _pendingCapture.Remove(go);
            return;
        }
        Store(go, si.syncId, fill, specials);
        SendFill(fill, ServerMain.AllClientIdsExceptHost);
        if (PayloadIsSpecial(fill) && specials != null) {
            SendSpecials(specials, ServerMain.AllClientIdsExceptHost);
        }
        _pendingCapture.Remove(go);
    }

    public static void HostOnCrystalRegistered(GameObject go, SyncInfo si = null) {
        if (!Net.IsServer || go == null) {
            return;
        }
        if (!go.TryGetComponent(out CrystalBehaviour _) && !LooksLikeCrystal(go)) {
            return;
        }
        HostCapture(go);
        if (si == null) {
            NetObjectRegistry.TryGetSyncInfo(go, out si);
        }
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(go, si);
    }

    public static void HostFlushPendingCaptures() {
        if (!Net.IsServer || _pendingCapture.Count == 0) {
            return;
        }
        List<GameObject> pending = new List<GameObject>(_pendingCapture);
        _pendingCapture.Clear();
        for (int i = 0; i < pending.Count; i++) {
            var go = pending[i];
            if (go != null) {
                HostCapture(go);
            }
        }
    }

    static bool CapturePair(GameObject go, knetid syncId, out byte[] fill, out byte[] specials) {
        fill = null;
        specials = null;
        if (go == null) {
            return false;
        }
        go.TryGetComponent(out CrystalBehaviour crystal);
        if (crystal == null && !IsGenerationDone(go)) {
            return false;
        }
        var effects = crystal != null ? GetEffects(crystal) : null;
        effects ??= new List<CrystalEffect>();
        bool genDone = IsGenerationDone(go);
        if (crystal != null && effects.Count == 0 && !genDone) {
            return false;
        }

        bool special = effects.Count > 0 || IsSpecial(go);
        if (special) {
            SetSpecial(go, true);
        }

        SpriteRenderer sprite = crystal != null && crystal.sprite != null
            ? crystal.sprite
            : go.GetComponent<SpriteRenderer>();
        Light2D light = crystal != null && crystal.light != null
            ? crystal.light
            : go.GetComponent<Light2D>();

        byte flags = 0;
        if (go.transform.localScale.x < 0f) {
            flags |= FlagFlip;
        }
        if (special) {
            flags |= FlagSpecial;
        }

        Color32 spriteCol = sprite != null ? (Color32)sprite.color : (Color32)Color.white;
        Color32 lightCol = light != null ? (Color32)light.color : spriteCol;
        float intensity = light != null ? light.intensity : 1f;
        float volume = 0f;
        float falloff = 0f;
        if (light != null) {
            if (light.volumeIntensityEnabled) {
                flags |= FlagVolumeLight;
                volume = light.volumeIntensity;
            }
            falloff = light.falloffIntensity;
        }

        float heaterTemp = 0f;
        float heaterLerp = 0f;
        if (go.TryGetComponent(out Heater heater)) {
            flags |= FlagHasHeater;
            heaterTemp = heater.desiredTemp;
            heaterLerp = heater.tempLerpStrength;
        }

        float crystalSize = crystal != null ? crystal.crystalSize : 1f;
        float outerRadius = light != null ? light.pointLightOuterRadius : 0f;

        NetDataWriter writer = new NetDataWriter();
        writer.Put(syncId);
        writer.Put(flags);
        writer.Put((Color24)spriteCol);
        writer.Put(spriteCol.a);
        writer.Put((Color24)lightCol);
        writer.Put(lightCol.a);
        writer.Put(intensity);
        writer.Put(volume);
        writer.Put(falloff);
        writer.Put(heaterTemp);
        writer.Put(heaterLerp);
        writer.Put(crystalSize);
        writer.Put(outerRadius);
        fill = writer.CopyData();

        if (special && effects.Count > 0) {
            NetDataWriter sw = new NetDataWriter();
            sw.Put(syncId);
            byte count = (byte)Mathf.Clamp(effects.Count, 0, 16);
            sw.Put(count);
            for (int i = 0; i < count; i++) {
                int id = EffectIdOf(effects[i]);
                sw.Put((byte)Mathf.Clamp(id, 0, 255));
                WriteEffectState(sw, effects[i]);
            }
            specials = sw.CopyData();
        }
        return true;
    }

    static void WriteEffectState(NetDataWriter writer, CrystalEffect effect) {
        if (effect == null) {
            writer.Put(0f);
            writer.Put((byte)0);
            return;
        }
        var t = Traverse.Create(effect);
        float cooldownOrTimer = 0f;
        byte flags = 0;
        if (effect is CrystalGravity) {
            var force = t.Field("forceMult");
            float mult = force.FieldExists() ? force.GetValue<float>() : 1f;
            cooldownOrTimer = mult >= 0f ? 1f : -1f;
            flags |= FlagGravityForce;
        } else {
            var cooldown = t.Field("cooldown");
            if (cooldown.FieldExists()) {
                cooldownOrTimer = cooldown.GetValue<float>();
            } else {
                var timer = t.Field("timer");
                if (timer.FieldExists()) {
                    cooldownOrTimer = timer.GetValue<float>();
                }
            }
        }
        var activated = t.Field("activated");
        if (activated.FieldExists() && activated.GetValue<bool>()) {
            flags |= 1;
        }
        var timerStarted = t.Field("timerStarted");
        if (timerStarted.FieldExists() && timerStarted.GetValue<bool>()) {
            flags |= 2;
        }
        writer.Put(cooldownOrTimer);
        writer.Put(flags);
    }
}
#endif
