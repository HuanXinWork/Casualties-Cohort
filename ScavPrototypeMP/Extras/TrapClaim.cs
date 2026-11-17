using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using Together.MpConstants;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 受击方权威（Victim-Authoritative）陷阱伤害认领与冷却状态广播核心：
/// 本地受害者计算命中肢体掩码并上报主机，主机在时间窗与距离内校验后写入权威生命值并广播已触发状态。
/// <para>
/// - 爆炸、地雷、冷喷口、活塞与水晶幻影逻辑见 <c>TrapClaim.Explosions.cs</c>。<br/>
/// - 冰冻无人机、碎玻璃与网络收包分发器见 <c>TrapClaim.ProjectilesAndDispatch.cs</c>。
/// </para>
/// </summary>
internal static partial class TrapClaim {
    const byte KindBear = 1;
    const byte KindSpike = 2;
    const byte KindCoil = 3;
    const byte KindJump = 4;
    const byte KindTurret = 5;
    const byte KindBoom = 6;
    const byte KindGun = 7;
    const byte KindManifest = 8;
    const byte KindCryo = 9;
    const byte KindGlass = 10;

    const float ContactReach = 32f;
    const float SpikeWindow = 2f;
    const float BearWindow = 0.75f;
    const float BoomWindow = 2f;
    const float ManifestWindow = 1.25f;
    const float CryoWindow = 0.4f;
    const float CryoReach = 64f;
    const float GlassWindow = 0.75f;

    class Window {
        public float until;
        public HashSet<ushort> who;
    }

    class Boom {
        public knetid id;
        public float until;
        public ExplosionParams param;
        public HashSet<ushort> who;
    }

    static readonly Dictionary<int, Window> windows = new();
    static readonly Dictionary<int, float> turretNext = new();
    static readonly Dictionary<int, float> stabbed = new();
    static readonly List<Boom> booms = new();
    static readonly List<Collider2D> hidden = new();
    static readonly List<bool> hiddenWas = new();

    internal static knetid pendingBoom;
    static CoilScript armedCoil;
    static ushort turretMask;
    static knetid turretId;
    static bool turretClaim;
    static bool gunShot;
    static knetid gunId;
    static NetBody turretLocal;
    static Vector2 lastLocalBoom;
    static float lastLocalBoomTime;
    static int hideDepth;

    internal static bool SkipRemote(Component hit) {
        if (!Net.IsRunning || hit == null) {
            return false;
        }
        if (!Util.TryGetBodyFromObject(hit, out var body) || body == null) {
            return false;
        }
        return !Util.IsBodyLocal(body);
    }

    internal static bool IsLocal(Component hit, out Body body) {
        body = null;
        if (hit == null || !Util.TryGetBodyFromObject(hit, out body) || body == null) {
            return false;
        }
        return Util.IsBodyLocal(body);
    }

    static void Send(byte kind, knetid id, ushort mask) {
        if (!Net.IsClient || (id.id == 0 && kind != KindGlass)) {
            return;
        }
        var writer = Net.CreateWriter(NetmsgId.SERVER_TrapClaim);
        writer.Put(kind);
        writer.Put(id);
        writer.Put(mask);
        writer.Put(0f);
        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    static void Relay(byte kind, knetid id, float cooldown) {
        if (!Net.IsServer || id.id == 0) {
            return;
        }
        var writer = Net.CreateWriter(NetmsgId.CLIENT_TrapState);
        writer.Put(kind);
        writer.Put(id);
        writer.Put(cooldown);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    static void RelayOf(byte kind, Component trap, float cooldown) {
        if (trap != null && NetObjectRegistry.TryGetSyncInfo(trap, out var si)) {
            Relay(kind, si.syncId, cooldown);
        }
    }

    static void Ignore(Body body) {
        if (body != null && body.TryGetNetBody(out var nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }

    internal static void Publish(NetBody nb) {
        if (nb == null || !Net.IsServer) {
            return;
        }
        MedicalSync.Server_QueueSendCharacterHealth(nb, true);
    }

    static bool Near(Component trap, Body body) {
        if (trap == null || body == null) {
            return false;
        }
        return KM.DistanceCheck(trap.transform.position, body.transform.position, ContactReach);
    }

    static bool TakeWindow(byte kind, knetid source, knetid player, bool spent, float window) {
        int key = (kind << 16) ^ source.id;
        float now = Time.unscaledTime;
        if (!windows.TryGetValue(key, out var w) || now > w.until) {
            if (spent) {
                return false;
            }
            w = new Window { until = now + window, who = new HashSet<ushort>() };
            windows[key] = w;
        }
        if (windows.Count > 400) {
            windows.Clear();
        }
        return w.who.Add(player.id);
    }

    static void OpenWindow(byte kind, knetid source, bool spent, float window) {
        int key = (kind << 16) ^ source.id;
        float now = Time.unscaledTime;
        if (!windows.TryGetValue(key, out var w) || now > w.until) {
            if (spent) {
                return;
            }
            windows[key] = new Window { until = now + window, who = new HashSet<ushort>() };
        }
    }

    static void Hurt(Component src, Body body) {
        if (src == null || body == null || !body.TryGetNetBody(out var nb)) {
            return;
        }
        var building = src.GetComponent<BuildingEntity>() ?? src.GetComponentInParent<BuildingEntity>();
        if (building != null) {
            nb.OnDamageEvent(new BodyDamageEvent(building, nb, 1));
        }
    }

    static ushort Bit(Limb limb) {
        int index = Util.GetLimbIndex(limb);
        if (index < 0 || index > 15) {
            return 0;
        }
        return (ushort)(1 << index);
    }

    static void Each(Body body, ushort mask, System.Action<Limb> fn) {
        if (body == null || body.limbs == null) {
            return;
        }
        int i = 0;
        foreach (var limb in body.limbs) {
            if (i > 15) {
                break;
            }
            if (limb != null && (mask & (1 << i)) != 0) {
                fn(limb);
            }
            i++;
        }
    }

    internal static void AfterLocalBear(BearTrap trap, Limb limb) {
        if (trap == null || limb == null || !limb.IsBodyLocal() || !NetObjectRegistry.TryGetSyncInfo(trap, out var si)) {
            return;
        }
        Ignore(limb.body);
        if (Net.IsClient) {
            Send(KindBear, si.syncId, Bit(limb));
        } else if (limb.TryGetNetBody(out var nb)) {
            Publish(nb);
            Relay(KindBear, si.syncId, 0f);
        }
    }

    static void ApplyBear(BearTrap trap, Limb limb) {
        if (trap == null || limb == null) {
            return;
        }
        Hurt(trap, limb.body);
        limb.body.shock = 100f;
        limb.pain += 100f;
        limb.body.adrenaline += 80f;
        if (Random.Range(0f, 1f) < 0.5f) {
            limb.BreakBone();
        }
        float armor = limb.GetArmorReduction();
        limb.skinHealth -= Random.Range(70f, 100f) / armor;
        limb.muscleHealth -= Random.Range(50f, 100f) / armor;
        limb.bleedAmount += Random.Range(14f, 18f) / armor;
        limb.DamageWearables(0.9f);
        if (limb.isHead) {
            limb.body.consciousness = 0f;
            limb.body.brainHealth -= Random.Range(0f, 40f);
            if (Random.value > 0.6f) {
                limb.body.RemoveEye();
            }
        }
        if (Util.IsBodyLocal(limb.body)) {
            limb.body.Ragdoll();
            if (limb.rb != null) {
                limb.rb.constraints = RigidbodyConstraints2D.FreezeAll;
            }
            PlayerCamera.main?.shaker?.Shake(50f);
        }
        SpendBear(trap);
        if (Net.IsServer) {
            RelayOf(KindBear, trap, 0f);
        }
        Sound.Play("beartrap", trap.transform.position);
        Sound.Play(VanillaAssetKeys.SoundGore, limb.transform.position);
    }

    internal static void SpendBear(BearTrap trap) {
        if (trap == null || trap.activated) {
            return;
        }
        trap.activated = true;
        var sprite = trap.GetComponent<SpriteRenderer>();
        if (sprite != null && trap.closeSprite != null) {
            sprite.sprite = trap.closeSprite;
        }
        if (trap.transform.childCount > 0) {
            Object.Destroy(trap.transform.GetChild(0).gameObject);
        }
    }

    internal static void SpikeNow(SpikeStabberScript spike, Component hit, Body body) {
        if (spike == null || body == null || !Util.IsBodyLocal(body)) {
            return;
        }
        RollSpike(spike, body, hit);
    }

    internal static void SpikeFromAnim(SpikeStabberScript spike) {
        if (!Net.IsRunning || spike == null) {
            return;
        }
        var cam = PlayerCamera.main;
        if (cam != null && cam.body != null && Util.IsBodyLocal(cam.body)) {
            RollSpike(spike, cam.body, null);
        }
    }

    static void RollSpike(SpikeStabberScript spike, Body body, Component touch) {
        ushort mask = 0;
        var hits = Physics2D.RaycastAll(spike.transform.position, spike.transform.up, 6f);
        for (int i = 0; i < hits.Length; i++) {
            var t = hits[i].transform;
            if (t == spike.transform || !t.TryGetComponent<Limb>(out var limb)) {
                continue;
            }
            if (limb.body != body || Random.Range(0f, 1f) >= 0.5f) {
                continue;
            }
            ushort bit = Bit(limb);
            if (bit == 0 || (mask & bit) != 0) {
                continue;
            }
            mask |= bit;
        }
        if (mask == 0 && touch != null && touch.TryGetComponent<Limb>(out var touched) && touched.body == body) {
            mask = Bit(touched);
        }
        if (mask == 0) {
            TellSpike(spike, body, 0);
            return;
        }

        int key = spike.GetInstanceID() ^ body.GetInstanceID();
        float now = Time.unscaledTime;
        if (stabbed.TryGetValue(key, out var until) && now < until) {
            return;
        }
        stabbed[key] = now + 1.2f;
        if (stabbed.Count > 400) {
            stabbed.Clear();
        }

        Each(body, mask, limb => ApplySpikeLimb(spike, limb));
        Sound.Play("loudStab", spike.transform.position);
        if (spike.spikeRenderer != null) {
            spike.spikeRenderer.sprite = spike.hitSpikeSprite;
        }
        Scare(spike.transform.position);
        Ignore(body);
        TellSpike(spike, body, mask);
    }

    static void TellSpike(SpikeStabberScript spike, Body body, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(spike, out var si)) {
            return;
        }
        if (Net.IsClient) {
            Send(KindSpike, si.syncId, mask);
            return;
        }
        if (mask != 0 && body != null && body.TryGetNetBody(out var nb)) {
            Publish(nb);
        }
        Relay(KindSpike, si.syncId, 0f);
    }

    static void ApplySpikeLimb(SpikeStabberScript spike, Limb limb) {
        Hurt(spike, limb.body);
        float armor = limb.GetArmorReduction();
        float mult = spike.damageMult;
        if (Util.IsBodyLocal(limb.body) && limb.rb != null) {
            limb.rb.AddForce(spike.transform.up * 300f * mult, ForceMode2D.Impulse);
        }
        limb.skinHealth -= Random.Range(50f, 80f) / armor * mult;
        limb.muscleHealth -= Random.Range(30f, 50f) / armor * mult;
        limb.bleedAmount += Random.Range(0f, 40f) / armor * mult;
        limb.pain += Random.Range(70f, 100f) / armor * mult;
        limb.body.shock = 70f * mult;
        limb.body.adrenaline = 100f;
        limb.DamageWearables(1f * mult);
        if (Random.Range(0f, 1f) < 0.3f / armor * mult) {
            limb.BreakBone();
        }
        if (limb.isHead) {
            limb.body.consciousness = 0f;
            if (Random.Range(0f, 1f) < 0.5f * mult) {
                limb.body.brainHealth -= Random.Range(5f, 70f) * mult / armor;
                limb.body.Disfigure();
            } else {
                limb.body.RemoveEye();
            }
        }
        if (limb.isHead || limb.isVital) {
            limb.body.internalBleeding += Random.Range(16f, 30f) * mult / armor;
        }
        limb.body.Scream();
        if (Util.IsBodyLocal(limb.body)) {
            PlayerCamera.main?.shaker?.Shake(50f);
        }
    }

    static void Scare(Vector2 pos) {
        var hits = Physics2D.OverlapCircleAll(pos, 15f, LayerMask.GetMask("Body", "Limb"));
        for (int i = 0; i < hits.Length; i++) {
            if (hits[i].TryGetComponent<Body>(out var body)) {
                body.eyeScareTime = 4f;
                body.eyePanicTime = 0.5f;
            }
            if (hits[i].TryGetComponent<Limb>(out var limb)) {
                limb.body.eyeScareTime = 4f;
                limb.body.eyePanicTime = 0.5f;
            }
        }
    }

    internal static void ArmCoil(CoilScript coil) {
        armedCoil = coil;
    }

    internal static void FinishCoil(CoilScript coil, Limb limb) {
        if (armedCoil != coil || coil == null || limb == null) {
            return;
        }
        armedCoil = null;
        if (!limb.IsBodyLocal() || !NetObjectRegistry.TryGetSyncInfo(coil, out var si)) {
            return;
        }
        Ignore(limb.body);
        if (Net.IsClient) {
            Send(KindCoil, si.syncId, Bit(limb));
        } else if (limb.TryGetNetBody(out var nb)) {
            Publish(nb);
            Relay(KindCoil, si.syncId, 30f);
        }
    }

    static void ApplyCoil(CoilScript coil, Limb limb) {
        if (coil == null || limb == null || limb.body == null) {
            return;
        }
        Hurt(coil, limb.body);
        bool local = Util.IsBodyLocal(limb.body);
        if (local) {
            limb.body.Ragdoll();
        }
        foreach (var limb2 in limb.body.limbs) {
            if (limb2 == null) {
                continue;
            }
            limb2.muscleHealth -= Random.Range(0f, 40f);
            limb2.skinHealth -= Random.Range(0f, 25f);
            limb2.pain = 100f;
            if (local && limb2.rb != null) {
                limb2.rb.velocity = ((Vector2)limb2.transform.position - (Vector2)coil.transform.position).normalized * 30f;
            }
        }
        limb.body.temperature += 5f;
        limb.body.consciousness = 0f;
        limb.body.shock = 100f;
        limb.body.respiratoryRate = 0f;
        limb.body.adrenaline = 50f;
        limb.body.fibrillationProgress = 0f;
        coil.cooldown = 30f;
        if (coil.light != null) {
            coil.light.intensity = 1f;
        }
        if (Net.IsServer) {
            RelayOf(KindCoil, coil, 30f);
        }
        Sound.Play("zap", coil.transform.position);
        if (local) {
            PlayerCamera.main?.shaker?.Shake();
        }
    }

    internal static void JumpFired(JumpPadScript pad, float cooldownBefore) {
        if (!Net.IsRunning || pad == null || cooldownBefore > 0f || pad.cooldown <= 0f) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(pad, out var si)) {
            return;
        }
        if (Net.IsClient) {
            Send(KindJump, si.syncId, 0);
        } else {
            Relay(KindJump, si.syncId, 15f);
        }
    }

    internal static void PlayJump(JumpPadScript pad) {
        if (pad == null) {
            return;
        }
        if (pad.light != null) {
            pad.light.intensity = 1f;
        }
        Sound.Play("jumppad", pad.transform.position);
    }

    internal static void RaiseIfIdle(JumpPadScript pad, byte seconds) {
        if (pad != null && seconds > 0 && pad.cooldown <= 0f) {
            pad.cooldown = seconds;
        }
    }

    internal static void RaiseIfIdle(CoilScript coil, byte seconds) {
        if (coil != null && seconds > 0 && coil.cooldown <= 0f) {
            coil.cooldown = seconds;
        }
    }

    internal static void RaiseGun(GunmineScript gun, byte seconds) {
        if (gun == null || seconds == 0 || gun.cooldown > 0f) {
            return;
        }
        gun.cooldown = seconds;
        gun.didReload = false;
    }

    internal static void PlayGun(GunmineScript gun) {
        if (gun == null) {
            return;
        }
        Sound.Play("rifleshot", gun.transform.position);
        var puff = gun.GetComponentInChildren<ParticleSystem>();
        if (puff != null) {
            puff.Play();
        }
    }

    internal static void TickClientGun(GunmineScript gun) {
        if (!Net.IsClient || gun == null) {
            return;
        }
        var hit = Physics2D.Raycast(gun.transform.position, gun.transform.up, 1f, LayerMask.GetMask("Body", "Limb"));
        bool local = hit && Util.TryGetBodyFromObject(hit.collider, out var body) && body != null && Util.IsBodyLocal(body);
        if (!local) {
            gun.cooldown -= Time.deltaTime;
        }
        if (gun.cooldown < 1f && !gun.didReload) {
            gun.didReload = true;
            Sound.Play("reload", gun.transform.position);
        }
        if (gun.cooldown > 0f || !local) {
            return;
        }
        PlayGun(gun);
        TurretScript.Shoot(new FireInfo {
            pos = gun.transform.position + gun.transform.up * 0.25f,
            dir = gun.transform.up,
            ignoreTrans = gun.transform
        });
        gun.didReload = false;
        gun.cooldown = 6f;
    }

    internal static void BeginShot() {
        turretMask = 0;
        turretId = default;
        turretClaim = false;
        gunShot = false;
        gunId = default;
        turretLocal = null;
    }

    internal static void NoteTurret(Body body, Limb limb) {
        if (!Net.IsRunning || body == null || !Util.IsBodyLocal(body) || !body.TryGetNetBody(out var nb)) {
            return;
        }
        var info = TurretScript_Shoot_MultiplayerPatch.last_fire_info;
        GunmineScript gun = null;
        bool isGun = info.ignoreTrans != null && info.ignoreTrans.TryGetComponent(out gun);
        if (Net.IsServer) {
            turretLocal = nb;
            if (isGun && NetObjectRegistry.TryGetSyncInfo(gun, out var gsi)) {
                gunShot = true;
                gunId = gsi.syncId;
            }
            return;
        }
        if (isGun) {
            if (!NetObjectRegistry.TryGetSyncInfo(gun, out var gsi)) {
                return;
            }
            gunShot = true;
            turretId = gsi.syncId;
            turretMask |= Bit(limb);
            Ignore(body);
            return;
        }
        if (info.ignoreTrans == null || !info.ignoreTrans.TryGetComponent<TurretScript>(out var turret) || !NetObjectRegistry.TryGetSyncInfo(turret, out var si)) {
            return;
        }
        turretClaim = true;
        turretId = si.syncId;
        turretMask |= Bit(limb);
        Ignore(body);
    }

    internal static void EndShot() {
        if (turretLocal != null) {
            Publish(turretLocal);
        }
        if (gunShot && Net.IsServer && gunId.id != 0) {
            Relay(KindGun, gunId, 6f);
        } else if (gunShot && Net.IsClient) {
            Send(KindGun, turretId, turretMask);
        } else if (turretClaim) {
            Send(KindTurret, turretId, turretMask);
        }
        BeginShot();
    }
}

[HarmonyPatch(typeof(GroundGlass), "OnCollisionEnter2D")]
static class GroundGlass_OnCollision_TrapClaim {
    static bool Prefix(GroundGlass __instance, Collision2D collision) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!TrapClaim.IsLocal(collision.collider, out Body body)) {
            return false;
        }
        TrapClaim.GlassHitLocal(__instance, body);
        return false;
    }
}

[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.OnTriggerEnter2D))]
static class SpikeStabberScript_OnTrigger_TrapClaim {
    static bool Prefix(SpikeStabberScript __instance, Collider2D collision, ref bool __state) {
        __state = __instance.activated;
        return !TrapClaim.SkipRemote(collision);
    }

    static void Postfix(SpikeStabberScript __instance, Collider2D collision, bool __state) {
        if (!Net.IsRunning || __state) {
            return;
        }
        if (!TrapClaim.IsLocal(collision, out var body)) {
            return;
        }
        TrapClaim.SpikeNow(__instance, collision, body);
    }
}

[HarmonyPatch(typeof(CoilScript), nameof(CoilScript.OnCollisionEnter2D))]
static class CoilScript_OnCollision_TrapClaim {
    static bool Prefix(CoilScript __instance, Collision2D collision) {
        if (__instance.cooldown > 0f) {
            return true;
        }
        return !TrapClaim.SkipRemote(collision.collider);
    }
}

[HarmonyPatch(typeof(MineScript), nameof(MineScript.Update))]
static class MineScript_Update_TrapClaim {
    static void Prefix(MineScript __instance) {
        if (Net.IsClient) {
            TrapClaim.TickMine(__instance);
        }
        if (!Net.IsServer || !__instance.pressed || __instance.exploded) {
            return;
        }
        if (__instance.timeSincePressed + Time.deltaTime <= 0.8f) {
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(__instance, out var si)) {
            TrapClaim.pendingBoom = si.syncId;
        }
    }

    static void Postfix() {
        TrapClaim.ClearPending();
    }
}

[HarmonyPatch(typeof(GunmineScript), nameof(GunmineScript.OnWillRenderObject))]
static class GunmineScript_OnWillRender_TrapClaim {
    static bool Prefix(GunmineScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            return false;
        }
        if (__instance.cooldown > 0f) {
            return true;
        }
        var hit = Physics2D.Raycast(__instance.transform.position, __instance.transform.up, 1f, LayerMask.GetMask("Body", "Limb"));
        if (!hit) {
            return true;
        }
        if (!Util.TryGetBodyFromObject(hit.collider, out var body) || body == null || Util.IsBodyLocal(body)) {
            return true;
        }
        __instance.cooldown -= Time.deltaTime;
        if (__instance.cooldown < 1f && !__instance.didReload) {
            __instance.didReload = true;
            Sound.Play("reload", __instance.transform.position);
        }
        return false;
    }
}
