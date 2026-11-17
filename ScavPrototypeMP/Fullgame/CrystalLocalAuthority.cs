#if FULLGAME
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

/// <summary>标记已由 Together 水晶本地属性包或 Custom Structures 接管权威属性的水晶对象。</summary>
public class TogetherCrystalLocalMarker : MonoBehaviour { }

/// <summary>一次性注入客户端水晶的完整外观、光照、地热与特效快照。</summary>
public class TogetherCrystalProps : MonoBehaviour {
    public string EffectsCsv;
    public byte[] EffectIds;
    public float[] EffectForceMults;
    public string ColorR;
    public string ColorG;
    public string ColorB;
    public Color32 SpriteColor = new Color32(255, 255, 255, 255);
    public Color32 LightColor = new Color32(255, 255, 255, 255);
    public float Intensity = 1f;
    public float Volume;
    public float Falloff;
    public float HeaterTemp;
    public float HeaterLerp;
    public float CrystalSize = 1f;
    public float OuterRadius;
    public byte Flags;
    public bool HasAuthoredColor;
    public bool Initialized;
}

/// <summary>
/// 正式版水晶单包权威同步控制器（缓存、Bag 捕获分发与网络包接收分部）：
/// 房主在生成完毕或突变时捕获完整属性包（Bag），客机收到后通过 <c>SetUpEffects</c> 前缀一次性还原，并与 Custom Structures 无依赖共存。
/// </summary>
public static partial class CrystalLocalAuthority {
    public const byte FlagFlip = 2;
    public const byte FlagHasHeater = 4;
    public const byte FlagVolumeLight = 8;
    public const byte FlagSpecial = 16;

    const float MatchRadiusSq = 1f;
    const string CsLocalMarkerName = "MoreStructuresLocalObjectMarker";
    const string CsPropsMarkerName = "CrystalPropertiesMarker";

    static readonly Dictionary<knetid, byte[]> _bagBySync = new();
    static readonly HashSet<knetid> _bagApplied = new();
    static readonly Dictionary<knetid, float> _nextBagRequestAt = new();

    public static void ClearLayerCaches() {
        _bagBySync.Clear();
        _bagApplied.Clear();
        _nextBagRequestAt.Clear();
    }

    public static bool IsLocalAuth(GameObject go) {
        if (go == null) {
            return false;
        }
        if (go.GetComponent<TogetherCrystalLocalMarker>() != null) {
            return true;
        }
        TogetherCrystalProps props = go.GetComponent<TogetherCrystalProps>();
        if (props != null && props.Initialized) {
            return true;
        }
        return HasCustomStructuresCrystalAuth(go);
    }

    public static bool HasCustomStructuresCrystalAuth(GameObject go) {
        return FindComponentByTypeName(go, CsLocalMarkerName) != null
            || FindComponentByTypeName(go, CsPropsMarkerName) != null;
    }

    public static void MarkLocal(GameObject go) {
        if (go == null) {
            return;
        }
        if (go.GetComponent<TogetherCrystalLocalMarker>() == null) {
            go.AddComponent<TogetherCrystalLocalMarker>();
        }
    }

    public static void MarkAuthoritySatisfied(knetid syncId) {
        if (syncId.id == 0) {
            return;
        }
        _bagApplied.Add(syncId);
        CrystalAuthority.MarkLocalAuthSatisfied(syncId);
    }

    public static bool BagApplied(knetid syncId) {
        return syncId.id != 0 && _bagApplied.Contains(syncId);
    }

    public static Component FindComponentByTypeName(GameObject go, string typeName) {
        if (go == null || string.IsNullOrEmpty(typeName)) {
            return null;
        }
        Component[] comps = go.GetComponents<Component>();
        for (int i = 0; i < comps.Length; i++) {
            Component c = comps[i];
            if (c != null && c.GetType().Name == typeName) {
                return c;
            }
        }
        return null;
    }

    public static TogetherCrystalProps EnsureProps(GameObject go) {
        if (go == null) {
            return null;
        }
        TogetherCrystalProps props = go.GetComponent<TogetherCrystalProps>();
        if (props == null) {
            props = go.AddComponent<TogetherCrystalProps>();
        }
        return props;
    }

    public static bool TryCaptureBag(GameObject go, knetid syncId, out byte[] bag) {
        bag = null;
        if (go == null) {
            return false;
        }
        go.TryGetComponent(out CrystalBehaviour crystal);
        if (crystal == null && !CrystalAuthority.IsGenerationDone(go)) {
            return false;
        }

        List<CrystalEffect> effects = crystal != null ? CrystalAuthority.GetEffects(crystal) : null;
        effects ??= new List<CrystalEffect>();
        if (crystal != null && effects.Count == 0 && !CrystalAuthority.IsGenerationDone(go)) {
            return false;
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
        if (effects.Count > 0 || CrystalAuthority.IsSpecial(go)) {
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

        byte count = (byte)Mathf.Clamp(effects.Count, 0, 16);
        writer.Put(count);
        for (int i = 0; i < count; i++) {
            int id = CrystalAuthority.EffectIdOf(effects[i]);
            writer.Put((byte)Mathf.Clamp(id, 0, 255));
        }
        for (int i = 0; i < count; i++) {
            float mult = 0f;
            if (effects[i] is CrystalGravity) {
                float raw = Traverse.Create(effects[i]).Field("forceMult").GetValue<float>();
                mult = raw >= 0f ? 1f : -1f;
            }
            writer.Put(mult);
        }

        bag = writer.CopyData();
        return true;
    }

    public static void HostCaptureAndBroadcastBag(GameObject go, SyncInfo si = null) {
        if (!Net.IsServer || go == null) {
            return;
        }
        if (HasCustomStructuresCrystalAuth(go)) {
            return;
        }
        if (si == null) {
            NetObjectRegistry.TryGetSyncInfo(go, out si);
        }
        knetid syncId = si != null ? si.syncId : default;
        if (!TryCaptureBag(go, syncId, out byte[] bag) || bag == null) {
            return;
        }
        if (syncId.id != 0 && _bagBySync.TryGetValue(syncId, out var prevBag) && CrystalAuthority.SameBytes(prevBag, bag)) {
            return;
        }
        if (syncId.id != 0) {
            _bagBySync[syncId] = bag;
        }
        SendBag(bag, ServerMain.AllClientIdsExceptHost);
    }

    public static void HostPushRegisteredBags() {
        if (!Net.IsServer) {
            return;
        }
        var who = ServerMain.AllClientIdsExceptHost;
        if (who == null || who.Count == 0) {
            return;
        }
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si == null || si.go == null || !CrystalAuthority.LooksLikeCrystal(si.go)) {
                continue;
            }
            if (HasCustomStructuresCrystalAuth(si.go)) {
                continue;
            }
            if (!TryCaptureBag(si.go, si.syncId, out byte[] bag) || bag == null) {
                continue;
            }
            _bagBySync[si.syncId] = bag;
            SendBag(bag, who);
        }
    }

    static void SendBag(byte[] bag, IReadOnlyList<knetid> who) {
        if (bag == null || bag.Length < 4 || who == null || who.Count == 0) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalBag);
        writer.PutBytesWithLength(bag);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    public static void ServerSendBagTo(knetid syncId, IReadOnlyList<knetid> who) {
        if (!Net.IsServer || who == null || who.Count == 0) {
            return;
        }
        byte[] bag = null;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null) {
            if (HasCustomStructuresCrystalAuth(si.go)) {
                return;
            }
            if (TryCaptureBag(si.go, syncId, out var fresh)) {
                bag = fresh;
                _bagBySync[syncId] = fresh;
            }
        }
        if (bag == null) {
            _bagBySync.TryGetValue(syncId, out bag);
        }
        if (bag != null) {
            SendBag(bag, who);
        }
    }

    public static void StoreBag(knetid syncId, byte[] bag) {
        if (syncId.id == 0 || bag == null) {
            return;
        }
        _bagBySync[syncId] = bag;
        _bagApplied.Remove(syncId);
    }

    public static bool TryGetBag(knetid syncId, out byte[] bag) {
        return _bagBySync.TryGetValue(syncId, out bag) && bag != null && bag.Length >= 4;
    }

    public static bool TryApplyBagToGo(GameObject go, knetid syncId, byte[] bag) {
        if (go == null || bag == null || bag.Length < 4) {
            return false;
        }
        if (HasCustomStructuresCrystalAuth(go)) {
            MarkAuthoritySatisfied(syncId);
            return true;
        }

        NetDataReader reader = new NetDataReader(bag);
        reader.Get(out knetid payloadSync);
        if (syncId.id == 0) {
            syncId = payloadSync;
        }
        reader.Get(out byte flags);
        reader.Get(out Color24 sprite24);
        reader.Get(out byte spriteA);
        reader.Get(out Color24 light24);
        reader.Get(out byte lightA);
        reader.Get(out float intensity);
        reader.Get(out float volume);
        reader.Get(out float falloff);
        reader.Get(out float heaterTemp);
        reader.Get(out float heaterLerp);
        reader.Get(out float crystalSize);
        reader.Get(out float outerRadius);
        reader.Get(out byte effectCount);
        byte[] effectIds = new byte[effectCount];
        for (int i = 0; i < effectCount; i++) {
            reader.Get(out effectIds[i]);
        }
        float[] forceMults = new float[effectCount];
        if (reader.AvailableBytes >= effectCount * 4) {
            for (int i = 0; i < effectCount; i++) {
                reader.Get(out forceMults[i]);
            }
        }

        TogetherCrystalProps props = EnsureProps(go);
        props.Flags = flags;
        props.SpriteColor = sprite24;
        props.SpriteColor = new Color32(props.SpriteColor.r, props.SpriteColor.g, props.SpriteColor.b, spriteA);
        Color32 lightCol = light24;
        lightCol.a = lightA;
        props.LightColor = lightCol;
        props.Intensity = intensity;
        props.Volume = volume;
        props.Falloff = falloff;
        props.HeaterTemp = heaterTemp;
        props.HeaterLerp = heaterLerp;
        props.CrystalSize = crystalSize;
        props.OuterRadius = outerRadius;
        props.EffectIds = effectIds;
        props.EffectForceMults = forceMults;
        props.HasAuthoredColor = true;
        props.Initialized = false;

        bool special = (flags & FlagSpecial) != 0 && effectCount > 0;
        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            if (!special) {
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) {
                    sr.color = props.SpriteColor;
                }
                var lightOnly = go.GetComponent<Light2D>();
                if (lightOnly != null) {
                    lightOnly.color = props.LightColor;
                    lightOnly.intensity = intensity;
                }
                ApplyFlip(go, flags);
                props.Initialized = true;
                MarkLocal(go);
                MarkAuthoritySatisfied(syncId);
                return true;
            }
            crystal = go.AddComponent<CrystalBehaviour>();
            crystal.sprite = go.GetComponent<SpriteRenderer>();
            crystal.light = go.GetComponent<Light2D>();
            crystal.build = go.GetComponent<BuildingEntity>();
            CrystalAuthority.EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject _)) {
                go.AddComponent<UsableObject>();
            }
        }

        crystal.crystalSize = crystalSize;
        ApplyFlip(go, flags);
        CrystalAuthority.SetSpecial(go, special);

        crystal.SetUpEffects();

        if ((flags & FlagHasHeater) != 0) {
            Heater heater = go.GetComponent<Heater>() ?? go.AddComponent<Heater>();
            heater.desiredTemp = heaterTemp;
            heater.tempLerpStrength = heaterLerp;
            go.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }

        if (special && go.TryGetComponent(out UsableObject usable) && usable != null) {
            usable.enabled = true;
        }

        MarkLocal(go);
        MarkAuthoritySatisfied(syncId);
        return props.Initialized || IsLocalAuth(go);
    }

    static void ApplyFlip(GameObject go, byte flags) {
        Vector3 scale = go.transform.localScale;
        float mag = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
        scale.x = ((flags & FlagFlip) != 0) ? -mag : mag;
        go.transform.localScale = scale;
    }

    public static bool TryApplyCachedBag(SyncInfo si) {
        if (si == null || si.go == null) {
            return false;
        }
        if (BagApplied(si.syncId) || IsLocalAuth(si.go)) {
            MarkAuthoritySatisfied(si.syncId);
            return true;
        }
        if (!TryGetBag(si.syncId, out byte[] bag)) {
            return false;
        }
        return TryApplyBagToGo(si.go, si.syncId, bag);
    }

    public static void MaybeRequestBag(knetid syncId) {
        if (!Net.IsClient || syncId.id == 0 || BagApplied(syncId)) {
            return;
        }
        float now = Time.unscaledTime;
        if (_nextBagRequestAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextBagRequestAt[syncId] = now + 2.5f;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalBag);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalBag)]
    static void Client_ReceiveBag(knetid _, NetDataReader reader) {
        byte[] bag = reader.GetBytesWithLength();
        if (bag == null || bag.Length < 4) {
            return;
        }
        NetDataReader probe = new NetDataReader(bag);
        probe.Get(out knetid syncId);
        StoreBag(syncId, bag);
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            TryApplyBagToGo(si.go, syncId, bag);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalBag)]
    static void Server_RequestBag(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        ServerSendBagTo(syncId, new[] { clientId });
    }
}
#endif
