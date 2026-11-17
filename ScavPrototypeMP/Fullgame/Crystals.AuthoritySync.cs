#if FULLGAME
using System;
using System.Collections.Generic;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

/// <summary>
/// 正式版水晶权威同步管理器（网络分发、客户端状态还原、特效重建与数据包接收器分部）。
/// </summary>
public static partial class CrystalAuthority {
    static void SendFill(byte[] fill, IReadOnlyList<knetid> who) {
        if (fill == null || fill.Length < 3 || who == null || who.Count == 0) {
            return;
        }
        NetDataReader probe = new NetDataReader(fill);
        probe.Get(out knetid syncId);
        probe.Get(out byte flags);
        Bug($"send FILL sync={syncId} special={(flags & FlagSpecial) != 0} len={fill.Length} to={who.Count}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalState);
        writer.PutBytesWithLength(fill);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    static void SendSpecials(byte[] specials, IReadOnlyList<knetid> who) {
        if (specials == null || specials.Length < 3 || who == null || who.Count == 0) {
            return;
        }
        NetDataReader probe = new NetDataReader(specials);
        probe.Get(out knetid syncId);
        probe.Get(out byte count);
        Bug($"send SPECIALS sync={syncId} effects={count} len={specials.Length} to={who.Count}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CrystalSpecials);
        writer.PutBytesWithLength(specials);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, who);
    }

    public static void ServerSendTo(knetid syncId, IReadOnlyList<knetid> who) {
        byte[] fill = null;
        byte[] specials = null;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null) {
            bool hasCrystal = si.go.TryGetComponent(out CrystalBehaviour _);
            if (!hasCrystal && !LooksLikeCrystal(si.go)) {
                Bug($"send skip sync={syncId} not a crystal");
                return;
            }
            if (CapturePair(si.go, syncId, out var freshFill, out var freshSpec)) {
                Store(si.go, syncId, freshFill, freshSpec);
                fill = freshFill;
                specials = freshSpec;
            } else {
                TryFill(syncId, PosKeyOf(si.go), out fill);
                TrySpecials(syncId, PosKeyOf(si.go), out specials);
                if (hasCrystal && fill == null) {
                    _pendingCapture.Add(si.go);
                }
            }
        } else {
            _fillBySync.TryGetValue(syncId, out fill);
            _specialsBySync.TryGetValue(syncId, out specials);
        }
        CrystalLocalAuthority.ServerSendBagTo(syncId, who);
        if (fill == null) {
            Bug($"send miss sync={syncId} no fill");
            return;
        }
        SendFill(fill, who);
        if (PayloadIsSpecial(fill) && specials != null) {
            SendSpecials(specials, who);
        }
    }

    public static void ClientForceReapply(knetid syncId) {
        _fillApplied.Remove(syncId);
        _specialsApplied.Remove(syncId);
        _nextFillAt.Remove(syncId);
        _nextSpecialAt.Remove(syncId);
    }

    public static void ClientResyncAllCrystals() {
        if (!Net.IsClient) {
            return;
        }
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si != null) {
                ClientTryApplyOrRequest(si);
            }
        }
    }

    public static void ClientTryApplyOrRequest(SyncInfo si) {
        if (!Net.IsClient || si == null || si.go == null) {
            return;
        }
        if (CrystalLocalAuthority.IsLocalAuth(si.go) || CrystalLocalAuthority.BagApplied(si.syncId)) {
            MarkLocalAuthSatisfied(si.syncId);
            return;
        }
        if (CrystalLocalAuthority.TryApplyCachedBag(si)) {
            return;
        }
        if (_fillApplied.Contains(si.syncId) && (!NeedsSpecials(si) || _specialsApplied.Contains(si.syncId))) {
            return;
        }
        if (!si.go.TryGetComponent(out CrystalBehaviour _)
            && !_fillBySync.ContainsKey(si.syncId)
            && !LooksLikeCrystal(si.go)) {
            return;
        }
        if (!SharedMain.LocalWorldIsGenerated) {
            return;
        }
        if (!CrystalLocalAuthority.TryGetBag(si.syncId, out _)) {
            CrystalLocalAuthority.MaybeRequestBag(si.syncId);
        }
        if (Net.IsServer) {
            if (si.go.TryGetComponent(out CrystalBehaviour hostCrystal)) {
                var hostEffects = GetEffects(hostCrystal);
                if (hostEffects != null && hostEffects.Count > 0) {
                    if (si.go.TryGetComponent(out UsableObject hostUsable) && hostUsable != null) {
                        hostUsable.enabled = true;
                    }
                    _fillApplied.Add(si.syncId);
                    _specialsApplied.Add(si.syncId);
                    return;
                }
            } else if (IsGenerationDone(si.go)) {
                _fillApplied.Add(si.syncId);
                return;
            }
        }

        if (si.go.TryGetComponent(out CrystalBehaviour crystal)) {
            EnsureClientEffectsList(crystal);
            if (!IsSpecial(si.go) && si.go.TryGetComponent(out UsableObject usable)) {
                usable.enabled = false;
            }
        }

        long posKey = PosKeyOf(si.go);
        if (!_fillApplied.Contains(si.syncId)) {
            if (TryFill(si.syncId, posKey, out var fill)) {
                ApplyFill(fill);
            } else {
                MaybeRequestFill(si.syncId);
                return;
            }
        }
        if (NeedsSpecials(si) && !_specialsApplied.Contains(si.syncId)) {
            if (TrySpecials(si.syncId, posKey, out var specials) && SpecialsHaveEffects(specials)) {
                ApplySpecials(specials);
            }
            if (!_specialsApplied.Contains(si.syncId)) {
                MaybeRequestSpecials(si.syncId);
            }
        }
    }

    static bool SpecialsHaveEffects(byte[] specials) {
        if (specials == null || specials.Length < 4) {
            return false;
        }
        try {
            NetDataReader reader = new NetDataReader(specials);
            reader.Get(out knetid _);
            reader.Get(out byte count);
            return count > 0;
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.World, $"CrystalAuthority.SpecialsHaveEffects parse failed: {ex.Message}");
            return false;
        }
    }

    static bool NeedsSpecials(SyncInfo si) {
        if (si == null || si.go == null) {
            return false;
        }
        if (IsSpecial(si.go)) {
            if (si.go.TryGetComponent(out CrystalBehaviour crystal)) {
                var fx = GetEffects(crystal);
                return fx == null || fx.Count == 0;
            }
            return true;
        }
        return _fillBySync.TryGetValue(si.syncId, out var fill) && PayloadIsSpecial(fill);
    }

    static void MaybeRequestFill(knetid syncId) {
        float now = Time.unscaledTime;
        if (_nextFillAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextFillAt[syncId] = now + 2.5f;
        Bug($"req FILL sync={syncId}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalState);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static void MaybeRequestSpecials(knetid syncId) {
        float now = Time.unscaledTime;
        if (_nextSpecialAt.TryGetValue(syncId, out float next) && now < next) {
            return;
        }
        _nextSpecialAt[syncId] = now + 1f;
        Bug($"req SPECIALS sync={syncId}");
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_RequestCrystalSpecials);
        writer.Put(syncId);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static bool IsNearWhite(Color32 c) {
        return c.r >= 245 && c.g >= 245 && c.b >= 245;
    }

    static void TickSpecialRetries() {
        if (Net.IsServer) {
            return;
        }
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si == null || si.go == null || _specialsApplied.Contains(si.syncId)) {
                continue;
            }
            if (!NeedsSpecials(si)) {
                continue;
            }
            ClientTryApplyOrRequest(si);
        }
    }

    public static void ClientTickBlankResync() {
        if (!Net.IsClient || !Net.IsRunning || !SharedMain.LocalWorldIsGenerated) {
            return;
        }
        TickSpecialRetries();
        float now = Time.unscaledTime;
        if (now < _nextBlankScanAt) {
            return;
        }
        _nextBlankScanAt = now + BlankScanInterval;

        int sent = 0;
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            if (sent >= BlankScanBudget) {
                break;
            }
            SyncInfo si = kv.Value;
            if (si == null || si.go == null) {
                continue;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour _) && !LooksLikeCrystal(si.go)) {
                continue;
            }
            bool needFill = !_fillApplied.Contains(si.syncId);
            if (!needFill && si.go.TryGetComponent(out CrystalBehaviour blank)) {
                var sr = blank.sprite != null ? blank.sprite : si.go.GetComponent<SpriteRenderer>();
                if (sr != null && IsNearWhite((Color32)sr.color)) {
                    needFill = true;
                }
            }
            bool needSpec = NeedsSpecials(si) && !_specialsApplied.Contains(si.syncId);
            if (!needFill && !needSpec) {
                continue;
            }
            if (needFill) {
                _fillApplied.Remove(si.syncId);
                _nextFillAt.Remove(si.syncId);
            }
            if (needSpec) {
                _nextSpecialAt.Remove(si.syncId);
            }
            ClientTryApplyOrRequest(si);
            sent++;
        }
        if (sent > 0) {
            Bug($"blank-resync touched={sent}");
        }
    }

    public static void EnsureClientEffectsList(CrystalBehaviour crystal) {
        if (crystal != null && crystal.effects == null) {
            crystal.effects = new List<CrystalEffect>();
        }
    }

    public static void ReceiveState(NetDataReader reader) {
        byte[] payload = reader.GetBytesWithLength();
        if (payload == null || payload.Length < 3) {
            Bug($"recv FILL bad len={(payload == null ? -1 : payload.Length)}");
            return;
        }
        NetDataReader probe = new NetDataReader(payload);
        probe.Get(out knetid syncId);
        probe.Get(out byte flags);
        Bug($"recv FILL sync={syncId} special={(flags & FlagSpecial) != 0} len={payload.Length}");
        _fillBySync[syncId] = payload;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            Store(si.go, syncId, payload, null);
        }
        _nextFillAt.Remove(syncId);
        _fillApplied.Remove(syncId);
        ApplyFill(payload);
    }

    public static void ReceiveSpecials(NetDataReader reader) {
        byte[] payload = reader.GetBytesWithLength();
        if (payload == null || payload.Length < 3) {
            Bug($"recv SPECIALS bad len={(payload == null ? -1 : payload.Length)}");
            return;
        }
        NetDataReader probe = new NetDataReader(payload);
        probe.Get(out knetid syncId);
        probe.Get(out byte count);
        Bug($"recv SPECIALS sync={syncId} effects={count} len={payload.Length}");
        _specialsBySync[syncId] = payload;
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si != null && si.go != null) {
            SetSpecial(si.go, true);
            Store(si.go, syncId, null, payload);
        }
        _nextSpecialAt.Remove(syncId);
        _specialsApplied.Remove(syncId);
        ApplySpecials(payload);
    }

    static void ApplyFill(byte[] payload) {
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid syncId);
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            _fillApplied.Remove(syncId);
            Bug($"apply FILL deferred sync={syncId} (no SyncInfo yet)");
            return;
        }
        reader.Get(out byte flags);
        GameObject go = si.go;

        if ((flags & FlagInert) != 0 && reader.AvailableBytes < 8) {
            Bug($"apply FILL inert-stub ignored sync={syncId}");
            return;
        }

        bool special = (flags & FlagSpecial) != 0;
        if (special) {
            SetSpecial(go, true);
        }

        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            if (!special) {
                reader.Get(out Color24 sprite24b);
                reader.Get(out byte spriteAb);
                reader.Get(out Color24 light24b);
                reader.Get(out byte lightAb);
                reader.Get(out float intensityB);
                var sr = go.GetComponent<SpriteRenderer>();
                if (sr != null) {
                    Color32 c = sprite24b;
                    c.a = spriteAb;
                    sr.color = c;
                }
                var lightB = go.GetComponent<Light2D>();
                if (lightB != null) {
                    Color32 lc = light24b;
                    lc.a = lightAb;
                    lightB.color = lc;
                    lightB.intensity = intensityB;
                }
                Vector3 scaleB = go.transform.localScale;
                float magB = Mathf.Abs(scaleB.x) < 0.001f ? 1f : Mathf.Abs(scaleB.x);
                scaleB.x = ((flags & FlagFlip) != 0) ? -magB : magB;
                go.transform.localScale = scaleB;
                _fillApplied.Add(syncId);
                Bug($"apply FILL decorative sync={syncId} no CB");
                return;
            }
            crystal = go.AddComponent<CrystalBehaviour>();
            if (crystal == null) {
                return;
            }
            crystal.sprite ??= go.GetComponent<SpriteRenderer>();
            crystal.light ??= go.GetComponent<Light2D>();
            crystal.build ??= go.GetComponent<BuildingEntity>();
            EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject _)) {
                go.AddComponent<UsableObject>();
            }
        }

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

        crystal.crystalSize = crystalSize;
        Vector3 scale = go.transform.localScale;
        float magX = Mathf.Abs(scale.x) < 0.001f ? 1f : Mathf.Abs(scale.x);
        scale.x = ((flags & FlagFlip) != 0) ? -magX : magX;
        go.transform.localScale = scale;

        ApplyingAuthority = true;
        try {
            Color32 sprite32 = sprite24;
            sprite32.a = spriteA;
            Color32 light32 = light24;
            light32.a = lightA;
            ApplyExactColor(crystal, sprite32, light32, intensity, (flags & FlagVolumeLight) != 0, volume, falloff, outerRadius);
            if ((flags & FlagHasHeater) != 0) {
                Heater heater = go.GetComponent<Heater>() ?? go.AddComponent<Heater>();
                heater.desiredTemp = heaterTemp;
                heater.tempLerpStrength = heaterLerp;
                go.GetOrAddComponent<HeaterMultiplayerRewrite>();
            }
        } finally {
            ApplyingAuthority = false;
        }

        if (!special) {
            if (go.TryGetComponent(out UsableObject bareUsable)) {
                UnityEngine.Object.Destroy(bareUsable);
            }
            UnityEngine.Object.Destroy(crystal);
        } else if (go.TryGetComponent(out UsableObject usable) && usable != null) {
            usable.enabled = true;
        }

        Store(go, syncId, payload, null);
        _fillApplied.Add(syncId);
        Bug($"apply FILL ok sync={syncId} special={special} size={crystalSize}");

        if (!special) {
            CrystalLocalAuthority.MarkLocal(go);
            CrystalLocalAuthority.MarkAuthoritySatisfied(syncId);
        }

        if (special && !_specialsApplied.Contains(syncId)) {
            if (TrySpecials(syncId, PosKeyOf(go), out var spec)) {
                ApplySpecials(spec);
            } else if (!Net.IsServer) {
                MaybeRequestSpecials(syncId);
            }
        }
    }

    static void ApplySpecials(byte[] payload) {
        NetDataReader reader = new NetDataReader(payload);
        reader.Get(out knetid syncId);
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            _specialsApplied.Remove(syncId);
            Bug($"apply SPECIALS deferred sync={syncId}");
            return;
        }
        reader.Get(out byte effectCount);
        byte[] effectIds = new byte[effectCount];
        (float cooldown, byte flags)[] effectStates = new (float cooldown, byte flags)[effectCount];
        for (int i = 0; i < effectCount; i++) {
            reader.Get(out effectIds[i]);
            reader.Get(out effectStates[i].cooldown);
            reader.Get(out effectStates[i].flags);
        }

        GameObject go = si.go;
        SetSpecial(go, true);
        if (!go.TryGetComponent(out CrystalBehaviour crystal)) {
            crystal = go.AddComponent<CrystalBehaviour>();
            crystal.sprite ??= go.GetComponent<SpriteRenderer>();
            crystal.light ??= go.GetComponent<Light2D>();
            crystal.build ??= go.GetComponent<BuildingEntity>();
            EnsureClientEffectsList(crystal);
            if (!go.TryGetComponent(out UsableObject _)) {
                go.AddComponent<UsableObject>();
            }
        }

        ApplyingAuthority = true;
        try {
            RebuildEffects(crystal, effectIds, effectStates);
        } finally {
            ApplyingAuthority = false;
        }

        var live = GetEffects(crystal);
        if (effectCount <= 0 || live == null || live.Count == 0) {
            _specialsApplied.Remove(syncId);
            Bug($"apply SPECIALS empty sync={syncId} wanted={effectCount} — retry in 1s");
            return;
        }
        if (go.TryGetComponent(out UsableObject usable) && usable != null) {
            usable.enabled = true;
        }
        TryRegisterChunk(crystal);
        Store(go, syncId, null, payload);
        _specialsApplied.Add(syncId);
        CrystalLocalAuthority.MarkLocal(go);
        CrystalLocalAuthority.MarkAuthoritySatisfied(syncId);
        Bug($"apply SPECIALS ok sync={syncId} effects={live.Count}");
    }

    static void TryRegisterChunk(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        var effects = GetEffects(crystal);
        if (effects == null || effects.Count == 0 || WorldGeneration.world == null || !WorldGeneration.world.worldExists) {
            return;
        }
        crystal.CheckCurrentChunk(WorldGeneration.world.GetClosestChunkScript(crystal.transform.position));
    }

    static void RebuildEffects(CrystalBehaviour crystal, byte[] effectIds, (float cooldown, byte flags)[] effectStates = null) {
        var existing = GetEffects(crystal);
        EnsureEffectMaps();
        if (!NeedsEffectRebuild(existing, effectIds)) {
            IgnoreEffectChildren(crystal);
            ApplyEffectStates(existing, effectStates);
            return;
        }

        ClearEffectChildren(crystal);

        List<CrystalEffect> effects = new List<CrystalEffect>();
        crystal.effects = effects;

        for (int i = 0; i < effectIds.Length; i++) {
            int id = effectIds[i];
            if (id < 0 || id >= _factories.Length) {
                continue;
            }
            CrystalEffect effect = _factories[id]();
            effect.crystal = crystal;
            effects.Add(effect);
            effect.Start();
            if (effectStates != null && i < effectStates.Length) {
                ApplyOneEffectState(effect, effectStates[i].cooldown, effectStates[i].flags);
            }
        }

        if (effects.Count > 1) {
            bool hasDistort = false;
            for (int c = 0; c < crystal.transform.childCount; c++) {
                if (crystal.transform.GetChild(c).name.Contains("CrystalDistort")) {
                    hasDistort = true;
                    break;
                }
            }
            if (!hasDistort) {
                GameObject obj = Utils.Create("Special/CrystalDistort", crystal.transform);
                obj.transform.localPosition = Vector3.zero;
                obj.transform.localScale = Vector3.one;
                obj.transform.rotation = Quaternion.identity;
                var sr = obj.GetComponent<SpriteRenderer>();
                if (sr != null && sr.material != null) {
                    sr.material.SetFloat("_DistortStrength", effects.Count * 0.015f);
                }
            }
        }

        IgnoreEffectChildren(crystal);

        if (crystal.build != null && effects.Count > 0) {
            string suffix = effects.Count switch {
                1 => " " + Locale.GetOther("crystaleffect1"),
                2 => " " + Locale.GetOther("crystaleffect2"),
                3 => " " + Locale.GetOther("crystaleffect3"),
                4 => " " + Locale.GetOther("crystaleffect4"),
                _ => " " + Locale.GetOther("crystaleffectmany"),
            };
            if (crystal.build.description == null || !crystal.build.description.Contains(suffix.Trim())) {
                crystal.build.description += suffix;
            }
        }
    }

    static void IgnoreEffectChildren(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        for (int i = 0; i < crystal.transform.childCount; i++) {
            Transform child = crystal.transform.GetChild(i);
            if (child != null) {
                child.gameObject.GetOrAddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
            }
        }
    }

    static void ClearEffectChildren(CrystalBehaviour crystal) {
        if (crystal == null) {
            return;
        }
        for (int i = crystal.transform.childCount - 1; i >= 0; i--) {
            Transform child = crystal.transform.GetChild(i);
            if (child == null) {
                continue;
            }
            string n = child.name;
            if (n.Contains("CrystalFire") || n.Contains("CrystalDistort") || n.Contains("heatwave")) {
                UnityEngine.Object.Destroy(child.gameObject);
            }
        }
        AudioSource[] audios = crystal.GetComponents<AudioSource>();
        for (int i = 0; i < audios.Length; i++) {
            if (audios[i] != null && audios[i].loop) {
                UnityEngine.Object.Destroy(audios[i]);
            }
        }
        Heater oldHeater = crystal.GetComponent<Heater>();
        if (oldHeater != null) {
            UnityEngine.Object.Destroy(oldHeater);
        }
    }

    static bool NeedsEffectRebuild(List<CrystalEffect> existing, byte[] effectIds) {
        if (effectIds == null || effectIds.Length == 0) {
            return false;
        }
        if (existing == null || existing.Count == 0 || existing.Count != effectIds.Length) {
            return true;
        }
        for (int i = 0; i < effectIds.Length; i++) {
            int id = effectIds[i];
            if (id < 0 || id >= _effectTypes.Length || existing[i] == null || existing[i].GetType() != _effectTypes[id]) {
                return true;
            }
        }
        return false;
    }

    static void ApplyEffectStates(List<CrystalEffect> effects, (float cooldown, byte flags)[] effectStates) {
        if (effects == null || effectStates == null) {
            return;
        }
        int n = Mathf.Min(effects.Count, effectStates.Length);
        for (int i = 0; i < n; i++) {
            ApplyOneEffectState(effects[i], effectStates[i].cooldown, effectStates[i].flags);
        }
    }

    static void ApplyOneEffectState(CrystalEffect effect, float cooldownOrTimer, byte flags) {
        if (effect == null) {
            return;
        }
        var t = Traverse.Create(effect);
        if (effect is CrystalGravity || (flags & FlagGravityForce) != 0) {
            var force = t.Field("forceMult");
            if (force.FieldExists()) {
                force.SetValue(cooldownOrTimer >= 0f ? 1f : -1f);
            }
        } else {
            var cooldown = t.Field("cooldown");
            if (cooldown.FieldExists()) {
                cooldown.SetValue(cooldownOrTimer);
            } else {
                var timer = t.Field("timer");
                if (timer.FieldExists()) {
                    timer.SetValue(cooldownOrTimer);
                }
            }
        }
        var activated = t.Field("activated");
        if (activated.FieldExists()) {
            activated.SetValue((flags & 1) != 0);
        }
        var timerStarted = t.Field("timerStarted");
        bool wasStarted = timerStarted.FieldExists() && timerStarted.GetValue<bool>();
        if (timerStarted.FieldExists()) {
            bool started = (flags & 2) != 0;
            timerStarted.SetValue(started);
            if (started && !wasStarted && effect is CrystalUnstable unstable) {
                CrystalDelayedFx.OnUnstableArmed(unstable, playTick: true);
            }
        }
        if ((flags & 1) != 0 && effect is CrystalEMP) {
            var part = t.Field("part");
            if (part.FieldExists()) {
                var go = part.GetValue<GameObject>();
                if (go != null) {
                    go.SetActive(false);
                }
            }
        }
    }

    static void ApplyExactColor(CrystalBehaviour crystal, Color32 spriteCol, Color32 lightCol, float intensity, bool volumeEnabled, float volume, float falloff, float outerRadius = -1f) {
        if (crystal.sprite != null) {
            crystal.sprite.color = spriteCol;
        }
        if (crystal.light != null) {
            crystal.light.color = lightCol;
            crystal.light.intensity = intensity;
            crystal.light.volumeIntensityEnabled = volumeEnabled;
            if (volumeEnabled) {
                crystal.light.volumeIntensity = volume;
            }
            crystal.light.falloffIntensity = falloff;
            if (outerRadius > 0f) {
                crystal.light.pointLightOuterRadius = outerRadius;
            }
        }
    }
}
#endif
