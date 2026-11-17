using System;
using System.Linq;
using System.Runtime.CompilerServices;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 场地环境危害、接触陷阱、自动炮塔与声波炮同步逻辑。
/// 钟乳石落石与坠落木箱的 IL 补丁与冷却同步见 <c>Traps.Crates.cs</c>。
/// </summary>
[HarmonyPatch(typeof(RadioactiveObject), nameof(RadioactiveObject.Update))]
static class RadioactiveObject_Update_MultiplayerPatch {
    static bool Prefix(RadioactiveObject __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        if (Net.IsServer) {
            foreach (var bodynpc in NetBody.all_instances) {
                var body = bodynpc?.body;
                if (body == null) {
                    continue;
                }

                float num = Vector2.Distance(__instance.transform.position, body.transform.position) * 0.3f;
                float num2 = __instance.radAtZero * (1f / Mathf.Max(num * num, 1f));
                if (num2 > 0.1f) {
                    body.radiationSickness += num2 * Time.deltaTime;
                    PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(bodynpc, num2 * 0.5f);
                }
            }
            return false;
        }

        if (ScavPlayer.TryGetLocalNetBody(out var localNb) && localNb.body != null) {
            float num = Vector2.Distance(__instance.transform.position, localNb.body.transform.position) * 0.3f;
            float num2 = __instance.radAtZero * (1f / Mathf.Max(num * num, 1f));
            if (num2 > 0.1f) {
                PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(localNb, num2 * 0.5f);
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(XalorisScript), nameof(XalorisScript.OnWillRenderObject))]
public static class XalorisScript_OnWillRenderObject_MultiplayerPatch {
    public class Krokosha_XalorisScript_OverrideComponent : MonoBehaviour {
        float lastTime = 0f;

        void Update() {
            if (Time.time - lastTime > 0.5f) {
                lastTime = Time.time;
                foreach (var obj in ScavPlayer.GetPlayerBodiesInRadius(transform.position, 5.5f)) {
                    if (obj?.body != null) {
                        obj.body.septicShock += 0.074f;
                    }
                }
            }
        }
    }

    static bool Prefix(XalorisScript __instance) {
        if (Net.IsRunning) {
            __instance.GetOrAddComponent<Krokosha_XalorisScript_OverrideComponent>();
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.Stab))]
static class SpikeStabberScript_Stab_MultiplayerPatch {
    static void Postfix(SpikeStabberScript __instance) {
        __instance.GetComponent<Animator>().speed = 1f;
        if (Net.IsRunning && NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var si) && Net.IsClient) {
            si.SetIgnoreTimeForRoundTrip();
        }
    }
}

[HarmonyPatch(typeof(JumpPadScript), nameof(JumpPadScript.OnCollisionEnter2D))]
internal static class JumpPadScript_OnCollisionEnter2D_MultiplayerPatch {
    internal class ShakerState {
        internal Vector2 pos;
        internal Vector2 vel;

        internal ShakerState(Shaker s) {
            if (s != null) {
                pos = s.pos;
                vel = s.velocity;
            }
        }

        internal void Apply(Shaker s) {
            if (s != null) {
                s.pos = pos;
                s.velocity = vel;
            }
        }
    }

    static float cooldownBefore;

    static bool Prefix(JumpPadScript __instance, Collision2D collision, ref ShakerState __state) {
        cooldownBefore = __instance.cooldown;
        __state = new ShakerState(PlayerCamera.main?.shaker);

        try {
            if (!__instance.disabled && __instance.cooldown <= 0f && collision.rigidbody && !collision.rigidbody.isKinematic) {
                if (Util.TryGetBodyFromObject(collision.gameObject, out var bodyObj) && bodyObj.TryGetNetBody(out var nb)) {
                    nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(JumpPadScript_OnCollisionEnter2D_MultiplayerPatch), ex);
        }

        return !TrapClaim.SkipRemote(collision.collider);
    }

    static void Postfix(JumpPadScript __instance, Collision2D collision, ref ShakerState __state) {
        TrapClaim.JumpFired(__instance, cooldownBefore);
        if (collision.gameObject.TryGetComponent<Limb>(out var limb) && !limb.IsBodyLocal()) {
            __state?.Apply(PlayerCamera.main?.shaker);
        }
    }
}

[HarmonyPatch(typeof(CoilScript), nameof(CoilScript.Shock))]
internal static class CoilScript_Shock_MultiplayerPatch {
    static bool Prefix(CoilScript __instance, Limb limb, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main?.shaker);

        if (TrapClaim.SkipRemote(limb)) {
            return false;
        }

        try {
            if (__instance.cooldown <= 0f && limb.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(CoilScript_Shock_MultiplayerPatch), ex);
        }

        if (__instance.cooldown <= 0f && limb.IsBodyLocal()) {
            TrapClaim.ArmCoil(__instance);
        }
        return true;
    }

    static void Postfix(CoilScript __instance, Limb limb, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        TrapClaim.FinishCoil(__instance, limb);
        if (!limb.IsBodyLocal()) {
            __state?.Apply(PlayerCamera.main?.shaker);
        }
    }
}

[HarmonyPatch(typeof(BearTrap), nameof(BearTrap.OnTriggerEnter2D))]
internal static class BearTrap_OnCollisionEnter2D_MultiplayerPatch {
    static int sprangId;

    static bool Prefix(BearTrap __instance, Collider2D other, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        sprangId = 0;
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main?.shaker);

        if (TrapClaim.SkipRemote(other)) {
            return false;
        }
        if (!__instance.activated) {
            sprangId = __instance.GetInstanceID();
        }

        try {
            if (!__instance.activated && other.TryGetComponent(out Limb limb) && limb.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), nb, 1));
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(BearTrap_OnCollisionEnter2D_MultiplayerPatch), ex);
        }

        return true;
    }

    static void Postfix(BearTrap __instance, Collider2D other, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        if (other != null && other.TryGetComponent<Limb>(out var limb)) {
            if (sprangId == __instance.GetInstanceID() && limb.IsBodyLocal() && __instance.activated) {
                TrapClaim.AfterLocalBear(__instance, limb);
            }
            if (!limb.IsBodyLocal()) {
                __state?.Apply(PlayerCamera.main?.shaker);
            }
        }
    }
}

[HarmonyPatch(typeof(SpikeStabberScript), nameof(SpikeStabberScript.CheckStab))]
internal static class SpikeStabberScript_CheckStab_MultiplayerPatch {
    static bool Prefix(SpikeStabberScript __instance, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        __state = new JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState(PlayerCamera.main?.shaker);

        if (Net.IsRunning) {
            try {
                TrapClaim.SpikeFromAnim(__instance);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Combat, nameof(SpikeStabberScript_CheckStab_MultiplayerPatch), ex);
            }
            return false;
        }

        try {
            var targets = Enumerable.ToHashSet(
                Physics2D.RaycastAll(__instance.transform.position, __instance.transform.up, 6f)
                    .Select(x => x.transform.GetComponent<Limb>())
                    .Where(x => x != null)
                    .Select(x => x.GetNetBody())
                    .Where(x => x != null));

            foreach (var item in targets) {
                item.OnDamageEvent(new BodyDamageEvent(__instance.GetComponent<BuildingEntity>(), item, 1));
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(SpikeStabberScript_CheckStab_MultiplayerPatch), ex);
        }
        return true;
    }

    static void Postfix(SpikeStabberScript __instance, ref JumpPadScript_OnCollisionEnter2D_MultiplayerPatch.ShakerState __state) {
        if (Camera.main != null && !KM.DistanceCheck(__instance.transform.position, Camera.main.transform.position, 10f)) {
            __state?.Apply(PlayerCamera.main?.shaker);
        }
    }
}

[HarmonyPatch(typeof(MineScript), nameof(MineScript.OnCollisionEnter2D))]
static class MineScript_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(MineScript __instance, Collision2D collision) {
        if (__instance.pressed) {
            return false;
        }

        if (Net.IsRunning) {
            if (Net.IsClient) {
                TrapClaim.ArmMine(__instance, collision.collider);
            } else {
                if ((bool)collision.collider.attachedRigidbody
                    && !collision.collider.attachedRigidbody.isKinematic
                    && NetBody.GetNearestBody(__instance.transform.position, true, false).Item2 < GameplayRanges.SimulationKeepRadiusSqr) {
                    __instance.pressed = true;
                    Sound.Play("mine", __instance.transform.position);
                    __instance.GetComponent<SpriteRenderer>().sprite = __instance.pressedSprite;

                    var writer = Net.CreateWriter(NetmsgId.CLIENT_MineStepped);
                    writer.Put((Vector2)__instance.transform.position);
                    Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                }
                return false;
            }
        }
        return true;
    }
}

public class TrackerTurret : BaseComponentTracker<TurretScript> {
    bool last_didBeep = false;
    bool last_didShoot = false;

    public float timeSinceFired {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => og.timeSinceFired;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => og.timeSinceFired = value;
    }

    public float beepTime {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => og.beepTime;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => og.beepTime = value;
    }

    public bool didShoot {
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        get => og.didShoot;
        [MethodImpl(MethodImplOptions.AggressiveInlining)]
        set => og.didShoot = value;
    }

    protected override void TrackerAwake() { }

    public void NudgeShoot() {
        if (didShoot) {
            return;
        }
        og.didBeep = true;
        beepTime = 0.5f;
        didShoot = false;
    }

    public void NudgeBeep(bool isVisible) {
        if (!og.didBeep || (beepTime >= 0.5f && timeSinceFired > 14f)) {
            last_didBeep = false;
            didShoot = false;
            beepTime = isVisible ? 0f : -0.5f;
            Sound.Play("turretsee", base.transform.position, twoDimensional: true, pitchShift: false);
            og.didBeep = true;
        }
    }

    void Update() {
        if (!Net.IsRunning) {
            return;
        }

        SyncInfo si = null;
        if (Net.IsClient) {
            NetObjectRegistry.SyncRegistry.TryGetValue(gameObject, out si);
        } else {
            float dist = NetBody.GetNearestBody(transform.position, true, false).Item2;
            if (dist < 10000f) {
                NetObjectRegistry.TryGetSyncInfoOrRegister(gameObject, out si);
            }
        }

        if (!last_didBeep && og.didBeep) {
            if (Net.IsClient) {
                var writer = Net.CreateWriter(NetmsgId.SERVER_TurretDoBeep);
                writer.Put((Vector2)transform.position);
                writer.Put(og.spr.isVisible);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            } else {
                NetObjectRegistry.SafeNewGO(gameObject);
                if (si != null || NetObjectRegistry.TryGetSyncInfo(gameObject, out si)) {
                    var writer = Net.CreateWriter(NetmsgId.CLIENT_TurretDoBeep_Relay);
                    writer.Put(si.syncId);
                    writer.Put(beepTime >= 0f);
                    Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, ServerMain.AllClientIdsExceptHost);
                }
            }
        }

        if (!last_didShoot && didShoot) {
            if (Net.IsServer) {
                if (si != null || NetObjectRegistry.TryGetSyncInfo(gameObject, out si)) {
                    Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_TurretDoShoot_Relay, si.syncId, true, false);
                }
            } else if (ScavPlayer.LocalPlayer.TryGetNetBody(out var nb)) {
                nb.SetNetHealthSyncIgnoreTime();
            }
        }

        last_didBeep = og.didBeep;
        last_didShoot = didShoot;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TurretDoBeep)]
    static void Server_TurretDoBeep(knetid clientId, NetDataReader reader) {
        reader.Get(out Vector2 turretpos);
        reader.Get(out bool isVisible);

        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) || body == null || !body.alive) {
            return;
        }

        if (!KM.DistanceCheck_PreSqr(turretpos, (Vector2)body.transform.position, 10000f)) {
            return;
        }

        if (Math.Abs(turretpos.y - body.transform.position.y) > 8f) {
            ModLog.Debug($"TurretDoBeep rejected: vertical delta > 8 for {plr}", LogDomain.Combat);
            return;
        }

        var touching = Physics2D.OverlapCircleAll(turretpos, 1f);
        foreach (var col in touching) {
            if (col.TryGetComponent<TurretScript>(out var turret) && (Vector2)turret.transform.position == turretpos) {
                var tst = turret.GetOrAddComponent<TrackerTurret>();
                tst.NudgeBeep(isVisible);
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(turretpos, $"S: TurretDoBeep {plr}");
                }
                return;
            }
        }
        ModLog.Debug("TurretDoBeep: target turret not found at reported position", LogDomain.Combat);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TurretDoBeep_Relay)]
    static void Client_TurretDoBeep_Relay(knetid _, NetDataReader reader) {
        reader.Get(out knetid turretid);
        reader.Get(out bool isVisible);

        if (NetObjectRegistry.TryGetSyncInfo(turretid, out var si) && si.go != null) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(si.go.transform.position, "C: TurretDoBeep_Relay");
            }
            var tst = si.go.GetOrAddComponent<TrackerTurret>();
            tst.NudgeBeep(isVisible);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TurretDoShoot_Relay)]
    static void Client_TurretDoShoot_Relay(knetid _, NetDataReader reader) {
        reader.Get(out knetid turretid);

        if (NetObjectRegistry.TryGetSyncInfo(turretid, out var si) && si.go != null) {
            var tst = si.go.GetOrAddComponent<TrackerTurret>();
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(si.go.transform.position, $"C: TurretDoShoot_Relay cur_shoot:{tst.didShoot}");
            }
            tst.NudgeShoot();
        }
    }
}

public class TrackerSoundCannon : BaseComponentTracker<SoundCannon> {
    bool announced_charge = false;

    public bool charging = false;
    public bool spent = false;
    public float endAngle;
    bool hostLocked;
    public float checkTime = 1f;
    public float maxDistance = 50f;
    public float chargeTime = 0f;

    public void soundcannon_spent(float angle) {
        endAngle = angle;
        hostLocked = true;
        og.spent = true;
        PointBarrel(angle);
        if (charging && chargeTime > 1f && !spent) {
            return;
        }
        spent = true;
        charging = false;
    }

    void PointBarrel(float angle) {
        if (transform.childCount == 0) {
            return;
        }
        transform.GetChild(0).eulerAngles = new Vector3(0f, 0f, angle);
    }

    void PullHostSpent() {
        if (Net.IsServer || hostLocked) {
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(gameObject, out var si) && si.specialdata1.Value != 0) {
            soundcannon_spent(si.specialdata2.Value);
        }
    }

    void PublishSpent() {
        if (!Net.IsServer || !NetObjectRegistry.TryGetSyncInfo(gameObject, out var si)) {
            return;
        }
        si.specialdata1.Value = 1;
        si.specialdata2.Value = endAngle;
    }

    protected override void TrackerAwake() { }

    public static bool CanSeePos(Transform me, Vector2 to) {
        RaycastHit2D[] array = Physics2D.LinecastAll(me.transform.position, to, PhysicsLayers.GroundMask);
        foreach (RaycastHit2D hit in array) {
            if (hit.transform != me.transform) {
                return false;
            }
        }
        return true;
    }

    public static NetBody GetClosestVisibleBody(Transform me) {
        NetBody closestBody = null;
        float closestDist = 99999999999f;
        NetBody closestVisibleBody = null;
        float closestVisibleDist = 9999999999f;

        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if (item.Value?.netBody == null) {
                continue;
            }
            Vector2 headpos = item.Value.netBody.GetHeadPos();
            float dist = KM.DistanceSqr(me.position, headpos);

            if (dist < closestDist) {
                closestDist = dist;
                closestBody = item.Value.netBody;
            }
            if (dist < closestVisibleDist && CanSeePos(me, headpos)) {
                closestVisibleDist = dist;
                closestVisibleBody = item.Value.netBody;
            }
        }
        return closestVisibleBody ?? closestBody;
    }

    void Update() {
        PullHostSpent();

        if (spent) {
            if (hostLocked) {
                PointBarrel(endAngle);
            }
            return;
        }

        if (!WorldGeneration.world || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (!Net.IsRunning) {
            Destroy(this);
            return;
        }

        checkTime -= Time.deltaTime;
        if (checkTime < 0f) {
            checkTime = Net.IsClient ? 0.4f : 0.1f;

            if (!NetObjectRegistry.TryGetSyncInfo(gameObject, out _) && ScavPlayer.GetDistanceToNearestLivingPlayer(transform.position).Item2 < 128f) {
                NetObjectRegistry.NewGO(gameObject);
                return;
            }

            if (!charging && !spent) {
                foreach (var item in ScavPlayer.BodyToPlayerDict) {
                    if (item.Key?.limbs == null || !AnatomyConstants.IsValidLimbIndex(item.Key, (int)LimbIndex.Head)) {
                        continue;
                    }
                    Vector2 headpos = item.Key.limbs[(int)LimbIndex.Head].transform.position;
                    if (KM.DistanceCheck(transform.position, headpos, maxDistance) && CanSeePos(transform, headpos)) {
                        charging = true;
                        break;
                    }
                }
            }
        }

        if (!NetObjectRegistry.TryGetSyncInfo(gameObject, out var si) || !charging) {
            return;
        }

        if (!announced_charge) {
            Sound.Play("sonarcharge", base.transform.position, twoDimensional: true, pitchShift: false);
            if (Net.IsRunning && Net.IsServer) {
                Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_SoundCannonISCHARGING____, si.syncId, true, false);
            }
            announced_charge = true;
        }
        chargeTime += Time.deltaTime;

        var closestPlayerBody = GetClosestVisibleBody(transform);
        if (closestPlayerBody == null || closestPlayerBody.body == null) {
            return;
        }

        Vector2 nearestHeadPos = closestPlayerBody.GetHeadPos();
        Vector2 goalLookDir = -KM.NormalTo(nearestHeadPos, base.transform.position, out _);

        if (hostLocked) {
            PointBarrel(endAngle);
        } else if (base.transform.childCount > 0) {
            base.transform.GetChild(0).eulerAngles = new Vector3(
                0f,
                0f,
                Mathf.LerpAngle(base.transform.GetChild(0).eulerAngles.z, Vector2.SignedAngle(Vector2.up, goalLookDir), Time.deltaTime * 4f));
        }

        Vector2 currentLookDir = base.transform.childCount > 0 ? (Vector2)base.transform.GetChild(0).up : Vector2.up;
        if (chargeTime < 5f) {
            return;
        }

        spent = true;
        charging = false;
        og.spent = true;
        if (hostLocked) {
            PointBarrel(endAngle);
        } else if (Net.IsServer) {
            if (transform.childCount > 0) {
                endAngle = transform.GetChild(0).eulerAngles.z;
            }
            PublishSpent();
        }

        foreach (var plr in ScavPlayer.AllLivingPlayers) {
            var body = plr.body;
            if (body == null || plr.netBody == null) {
                continue;
            }
            Vector2 headpos = plr.netBody.GetHeadPos();
            Vector2 goalDirForPlr = -KM.NormalTo(headpos, base.transform.position, out _);
            float dot = Vector2.Dot(currentLookDir, goalDirForPlr);
            bool inSameEnvironment = FluidManager.main.HasLiquid(WorldGeneration.world.WorldToBlockPos(base.transform.position)) == body.inWater;

            if (CanSeePos(transform, headpos) && dot > 0.55f && inSameEnvironment) {
                if (body.bodyAffect.wasWater) {
                    if (plr.IsLocal) {
                        Sound.Play("sonarmegaouchfixed", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    }
                    body.hearingLoss = 100f;
                    body.internalBleeding += 100f;
                    body.happiness -= 10f;
                    body.shock = 100f;
                    body.limbs[(int)LimbIndex.Head].pain = 100f;
                    body.brainHealth -= 20f;
                    if (plr.IsLocal) {
                        PlayerCamera.main.shaker.Shake(3000f);
                        PlayerCamera.main.bonusAbber = -70f;
                        PlayerCamera.main.globalMuteTime += 120f;
                    }
                } else {
                    if (plr.IsLocal) {
                        Sound.Play("sonarouch", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    }
                    body.hearingLoss += 60f;
                    body.internalBleeding += Random.Range(20f, 30f);
                    body.happiness -= 2f;
                    body.shock = 65f;
                    body.limbs[(int)LimbIndex.Head].pain = 80f;
                    body.brainHealth -= 5f;
                    if (plr.IsLocal) {
                        PlayerCamera.main.shaker.Shake(1000f);
                        PlayerCamera.main.bonusAbber = -30f;
                        PlayerCamera.main.globalMuteTime += 30f;
                    }
                }
                if (plr.IsLocal) {
                    Sound.Play("tinnitus", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                }
                body.consciousness -= 69f;
                body.eyeCloseTime = 10f;
                body.eyeScareTime = 25f;
                body.adrenaline += 100f;
                body.respiratoryRate = 0f;
                foreach (Limb obj in body.limbs) {
                    obj.muscleHealth -= Random.Range(20f, 30f);
                    obj.pain += 40f;
                }
            } else {
                body.consciousness -= 30f;
                body.eyeScareTime = 4f;
                if (plr.IsLocal) {
                    Sound.Play("sonarouchblocked", base.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: false, ignoreMixer: true);
                    PlayerCamera.main.shaker.Shake();
                    PlayerCamera.main.globalMuteTime += 3f;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(SoundCannon), "Update")]
public static class SoundCannon_Update_MultiplayerPatch {
    public static bool Prefix(SoundCannon __instance) {
        if (Net.IsRunning) {
            __instance.GetOrAddComponent<TrackerSoundCannon>();
            return false;
        }
        return true;
    }
}
