using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using static GunScript;

namespace Together;

/// <summary>
/// 枪械持有者追踪组件、开火弹道采样上报与服务端/客机枪击同步。
/// </summary>
public class TrackerGunScript : BaseComponentTracker<GunScript> {
    public int shotsPerFire { get; private set; }
    public SyncInfo si => tracker.syncInfo;
    public BodyGetterOverrider bgo => this.GetOrAddComponent<BodyGetterOverrider>();
    public Item item => GetComponent<Item>();

    public NetBody pbody {
        get {
            var b = bgo.body;
            return b != null ? b.GetNetBody() : null;
        }
        set => bgo.body = value != null ? value.body : null;
    }

    public Body body {
        get => bgo.GetBody();
        set => bgo.body = value;
    }

    public bool isEquipped {
        get {
            var b = body;
            return b != null && b.GetItem(b.handSlot)?.transform == transform;
        }
    }

    public bool override_shoot_trajectory = false;
    public Vector2 override_shoot_trajectory_origin = Vector2.left;
    public Vector2 override_shoot_trajectory_direction = Vector2.left;
    public bool send_racked_state = false;

    private bool _emulatedFiringPinStruck = false;

    public Body GetBody() => body;
    public bool IsShotgun() => shotsPerFire > 2;

    protected override void TrackerAwake() {
        shotsPerFire = og.shotsPerFire;
        if (Net.IsRunning) {
            og.shotsPerFire = 0;
        }
        InvokeRepeating(nameof(SlowUpdate), 1.0f, 0.1f);
    }

    public void ScanForBody() {
        if (bgo.TryGetBodyFromParent()) {
            return;
        }
        if (ScavPlayer.GetDistanceToNearestLivingPlayer(transform.position).Item1 is var plr && plr != null) {
            pbody = plr.netBody;
            return;
        }
        body = PlayerCamera.main.body;
    }

    void SlowUpdate() {
        if (!gameObject.TryGetTracker(out var trk) || !trk.isNearby || !isEquipped || !Multiplayer.rules.PVP) {
            return;
        }

        float rightOrLeft = body.isRight ? 1f : -1f;
        Vector2 gunLook = og.transform.right * rightOrLeft;
        var rayHits = Physics2D.RaycastAll(og.barrel.position, gunLook, 4f);

        foreach (var rayHit in rayHits) {
            Limb limb = null;
            if (!(rayHit.collider.TryGetComponent(out limb) && limb.isVital)) {
                limb = null;
            }
            if (limb == null) {
                if (rayHit.collider.TryGetComponent<NetBody>(out var nb)) {
                    Vector2 realLook = KM.NormalTo(og.barrel.position, nb.GetHeadPos());
                    if (Vector2.Dot(gunLook, realLook) > 0.9f) {
                        limb = nb.head;
                    }
                }
                if (limb == null) {
                    continue;
                }
            }
            if (limb.body != body && limb.body.happiness > -40f) {
                limb.body.eyeScareTime = Math.Max(limb.body.eyeScareTime, 0.3f);
            }
        }
    }

    void FixedUpdate() {
        if (pbody == null || !isEquipped || pbody.piggybacking_on == null || body.standing) {
            return;
        }

        float flipped = body.isRight ? 1f : -1f;
        var hand = body.slots[body.handSlot].limb;
        Vector2 direction = (Vector2)body.targetLookPos - hand.rb.position;

        if (direction.sqrMagnitude > 2f && hand.rb.simulated) {
            float targetAngle = Mathf.Atan2(direction.x, -direction.y) * Mathf.Rad2Deg;
            float angleDiff = targetAngle - hand.rb.rotation;
            float delta = 400f * Time.fixedDeltaTime * hand.totalForce * Mathf.Clamp01(1f - og.gasTime * 4f);
            float angleStepped = Mathf.MoveTowardsAngle(hand.rb.rotation, targetAngle, delta * (30f + Mathf.Abs(angleDiff)));

            var wrist = hand.connectedLimbs[0];
            var arm = wrist.connectedLimbs[0];
            if (wrist.rb.simulated) {
                wrist.rb.MoveRotation(Mathf.MoveTowardsAngle(wrist.rb.rotation, targetAngle + 20f * flipped, delta * 8f));
            }
            if (arm.rb.simulated) {
                arm.rb.MoveRotation(Mathf.MoveTowardsAngle(arm.rb.rotation, targetAngle - 20f * flipped, delta * 12f));
            }
            hand.rb.MoveRotation(angleStepped);
        }
    }

    public void ApplyRecoil() {
        if (pbody?.piggybacking_on != null) {
            float flipped = body.isRight ? 1f : -1f;
            var hand = body.slots[body.handSlot].limb;
            hand.rb.velocity += (Vector2)hand.transform.right * (flipped * og.knockBack * 8f);
            hand.rb.rotation += flipped * 0.1f;
        }
    }

    void Update() {
        if (!pbody || !pbody.IsLocal) {
            return;
        }
        if (send_racked_state && og.racked == og.lastRacked) {
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunRack, og.racked, true);
            send_racked_state = false;
        }
        if (og.firingPinStruck && !_emulatedFiringPinStruck) {
            _emulatedFiringPinStruck = true;
            if (og.gasTime <= 0f) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(si.transformPosition, "C sending: GunEmptyClick");
                }
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_GunEmptyClick, tracker.syncId, false);
            }
        } else {
            _emulatedFiringPinStruck = og.firingPinStruck;
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_GunEmptyClick)]
    static void Server_GunEmptyClick(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid gunSyncId);
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var shooter)
            && shooter.body.conscious
            && ItemSync.TryGetItemSyncInfo(gunSyncId, out var gunSi)
            && gunSi.IsGun()
            && ItemSync.CheckIfBodyReachThisItem(gunSi, shooter.body)) {
            var gst = gunSi.go.GetOrAddComponent<TrackerGunScript>();
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(gunSi.transformPosition, $"S: GunEmptyClick {DebugHelp.GetPlayerFullDebugString(clientId)}");
            }
            ServerMain.Server_AnnounceSound(gst.transform.position, "guntrigger", ServerMain.GetListOfClientIdsExceptThis(clientId));
        }
    }

#if !RELEASE
    void OnGUI() {
        if (!log.verbose || !isEquipped || Camera.main == null) {
            return;
        }
        int ogFont = GUI.skin.label.fontSize;
        Vector2 pos = Camera.main.WorldToScreenPoint(transform.position);
        pos = new Vector2(pos.x, Screen.height - pos.y);

        if (pos.x > 0 && pos.y > 0 && pos.x < Screen.width && pos.y < Screen.width) {
            const float bsize = 17f;
            const float elsize = 13f;
            float y = pos.y;
            GUI.skin.label.normal.textColor = Color.white;
            GUI.skin.label.fontSize = 10;
            GUI.Label(new Rect(pos.x, y, 400, bsize), "chamber: " + og.roundInChamber);
            y += elsize;
            GUI.Label(new Rect(pos.x, y, 400, bsize), "ammo: " + og.roundsInMag);
            y += elsize;
            GUI.Label(new Rect(pos.x, y, 100, bsize), "racked: " + og.racked);
            y += elsize;
            if (pbody != null && pbody.syncBody?.drng_attackCounter != null) {
                GUI.Label(new Rect(pos.x, y, 100, bsize), "rroll: " + pbody.syncBody.drng_attackCounter.Value);
            }
        }
        GUI.skin.label.normal.textColor = Color.white;
        GUI.skin.label.fontSize = ogFont;
    }
#endif
}

/// <summary>
/// 枪械开火结算、命中列表上报与广播回放业务类（独立于 <see cref="HarmonyPatch"/> 类以避免 <c>Harmony003</c>）。
/// </summary>
internal static class GunCombatSync {
    /// <summary>
    /// 供 <see cref="GunScript.Fire"/> 与 <see cref="GunScript.UnloadMag"/> 的 Transpiler 调用：
    /// 单机模式下直接返回原版 <c>gun.body</c> 以避免挂载 <see cref="TrackerGunScript"/> 清零 <c>shotsPerFire</c>；联机模式下走追踪器覆写躯体。
    /// </summary>
    internal static Body ResolveGunBody(GunScript gun) {
        if (!Net.IsRunning) {
            return gun.body;
        }
        return gun.GetOrAddComponent<TrackerGunScript>().GetBody();
    }

    public static Vector2 RollVerticalSpread(GunScript gun) {
        return gun.transform.up * (UnityEngine.Random.Range(-1f, 1f) * gun.verticalSpread);
    }

    internal static void CheckForHitsAndTellServer(GunScript gun, bool suicide) {
        var gst = gun.GetOrAddComponent<TrackerGunScript>();
        int shotsPerFire = gun.shotsPerFire;
        var body = PlayerCamera.main.body;
        if (gst != null) {
            gst.ScanForBody();
            body = gst.body;
            shotsPerFire = gst.shotsPerFire;
        }
        var npc = body.GetNetBody();
        bool isLocal = Util.IsBodyLocal(body);

        List<SyncInfo> hitBuildings = new();
        List<Limb> hitLimbs = new();

        if (!isLocal && !suicide) {
            TurretScript_Shoot_MultiplayerPatch.HitOnlyStatic = true;
        }

        float rightOrLeft = body.isRight ? 1f : -1f;
        for (int i = 0; i < shotsPerFire; i++) {
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooterIsSuicide = suicide;
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooter = npc;
            TurretScript_Shoot_MultiplayerPatch.LastActiveShooterGun = gst;

            if (gst.override_shoot_trajectory) {
                TurretScript.Shoot(new FireInfo {
                    pos = gst.override_shoot_trajectory_origin,
                    dir = gst.override_shoot_trajectory_direction + RollVerticalSpread(gun) * rightOrLeft,
                    doTinnitus = false,
                    ignoreBody = false,
                    structureDamage = gun.structureDamage,
                    animalDamage = gun.animalDamage,
                    forceHead = false
                });
            } else {
                TurretScript.Shoot(new FireInfo {
                    pos = gun.barrel.position,
                    dir = suicide
                        ? KM.NormalTo(gun.barrel.position, body.limbs[(int)LimbIndex.Head].rb.position)
                        : (((Vector2)gun.transform.right + RollVerticalSpread(gun)) * rightOrLeft),
                    doTinnitus = false,
                    ignoreBody = false,
                    structureDamage = gun.structureDamage,
                    animalDamage = gun.animalDamage,
                    forceHead = false
                });
            }

            hitBuildings.AddRange(TurretScript_Shoot_MultiplayerPatch.JustHitBuildings);
            hitLimbs.AddRange(TurretScript_Shoot_MultiplayerPatch.JustHitLimbs);
        }

        hitLimbs = Util.LimitMaxRepeats(hitLimbs, Math.Min(gst.shotsPerFire, GunScript_Fire_MultiplayerPatch.MAX_HITS_PER_LIMB_AT_ONCE));
        hitLimbs = hitLimbs.Where(l => l.body.TryGetNetBody(out _)).ToList();

        gst.override_shoot_trajectory = false;
        gst.send_racked_state = true;

        if (Multiplayer.IsNetworkActiveAndIsClient()) {
            TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, hitLimbs, hitBuildings);
        }

        if (Net.IsRunning) {
            if (isLocal) {
                gst.si.SetIgnoreTimeForRoundTrip(0.4f);
                if (!suicide) {
                    var writer = Net.CreateWriter(NetmsgId.SERVER_PlayerBodyShoot);
                    writer.Put(Net.ServerTick);
                    writer.Put(gst.tracker.syncId);
                    writer.Put((Vector2)gun.barrel.position);
                    writer.Put((Vector2)(gun.transform.right * rightOrLeft));
                    writer.Put(gun.racked);

                    byte hitLimbsAmount = (byte)Math.Min(hitLimbs.Count, 40);
                    writer.Put(hitLimbsAmount);
                    for (byte i = 0; i < hitLimbsAmount; i++) {
                        writer.Put(new LimbNetId(hitLimbs[i]));
                    }

                    byte hitBuildingsAmount = (byte)Math.Min(hitBuildings.Count, 40);
                    writer.Put(hitBuildingsAmount);
                    for (byte i = 0; i < hitBuildingsAmount; i++) {
                        writer.Put(hitBuildings[i].syncId);
                    }

                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                }
            }

            if (suicide && Net.IsServer && npc != null && !hitLimbs.Contains(npc.head)) {
                TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(npc.head, gst, false);
            }
        }

        TurretScript_Shoot_MultiplayerPatch.HitOnlyStatic = false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBodyShoot)]
    static void ServerReceiver__PlayerBodyShoot(knetid clientId, NetDataReader reader) {
        reader.Get(out ushort _);
        reader.Get(out knetid gunSyncId);

        if (!Util.IsWorldGenerated()
            || !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var shooter)
            || !shooter.body.conscious
            || !ItemSync.TryGetItemSyncInfo(gunSyncId, out var gunSi)
            || !gunSi.IsGun()
            || !(gunSi.gun.roundsInMag > 0 || gunSi.gun.roundInChamber == RoundInChamber.Round || shooter.IsLocal)
            || !ItemSync.CheckIfBodyReachThisItem(gunSi, shooter.body)) {
            return;
        }

        var gst = gunSi.go.GetOrAddComponent<TrackerGunScript>();
        gst.body = shooter.body;
        gst.pbody = shooter;

        reader.Get(out Vector2 gunBarrelPos);
        reader.Get(out Vector2 gunDirection);
        reader.Get(out bool newRacked);
        gunDirection.Normalize();

        if (gunDirection == Vector2.zero || !gunDirection.IsFinite()) {
            ModLog.Warning($"PlayerBodyShoot: {plr} invalid shoot direction.", LogDomain.Combat);
            return;
        }

        if (!gunBarrelPos.IsFinite()
            || (!KM.DistanceCheck(gunBarrelPos, gunSi.gun.barrel.position, 10f)
                && !KM.DistanceCheck(gunBarrelPos, shooter.body.GetClosestLimb(gunBarrelPos).transform.position, 2f))) {
            ModLog.Warning($"PlayerBodyShoot: {plr} shoot origin too far from barrel.", LogDomain.Combat);
            return;
        }

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(gunSi.transformPosition, $"S: PlayerBodyShoot {DebugHelp.GetPlayerFullDebugString(clientId)}", Color.magenta);
        }

        List<Limb> limbsHit = new();
        reader.Get(out byte hitLimbsAmount);
        for (byte i = 0; i < hitLimbsAmount; i++) {
            reader.Get(out LimbNetId limbNetId);
            if (limbNetId.TryGetNetBodyAndLimbSafe(out var nb, out var lim)) {
                if (shooter.CanAttackThisGuy(nb.body)) {
                    limbsHit.Add(lim);
                } else {
                    ModLog.Warning($"PlayerBodyShoot: {plr} hit non-attackable target {nb}.", LogDomain.Combat);
                }
            }
        }
        limbsHit = Util.LimitMaxRepeats(limbsHit, Math.Min(gst.shotsPerFire, GunScript_Fire_MultiplayerPatch.MAX_HITS_PER_LIMB_AT_ONCE));

        List<SyncInfo> buildingsHit = new();
        reader.Get(out byte hitBuildingsAmount);
        for (byte i = 0; i < hitBuildingsAmount; i++) {
            reader.Get(out knetid buildingSyncId);
            if (NetObjectRegistry.TryGetSyncInfo(buildingSyncId, out var bsi) && bsi.IsBuilding() && NetObjectRegistry.IsObjectDynamic(bsi.go)) {
                if (!bsi.building.cantHit) {
                    buildingsHit.Add(bsi);
                }
            }
        }
        buildingsHit = Util.LimitMaxRepeats(buildingsHit, gst.shotsPerFire);

        var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerBodyShoot_Relay);
        writer.Put(clientId);
        writer.Put(gst.tracker.syncId);
        writer.Put(gunBarrelPos);
        writer.Put(gunDirection);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));

        GunScript_JamChance_MultiplayerPatch.ForceJam = !gunSi.gun.racked;
        if (!Util.IsBodyLocal(shooter.body)) {
            ReplayRemoteGunshotVisuals(gst.si, gunBarrelPos, gunDirection);
        }
        GunScript_JamChance_MultiplayerPatch.ForceJam = false;

        gunSi.gun.racked = newRacked;
        gunSi.gun.lastRacked = false;
        if (gunSi.gun.firingMode != FiringMode.Pump && newRacked) {
            gst.og.gasTime = gunSi.gun.desiredGasTime;
        }

        TurretScript_Shoot_MultiplayerPatch.ApplyShootDamages(gst, limbsHit, buildingsHit);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBodyShoot_Relay, false)]
    static void Client_PlayerBodyShoot_Relay(knetid _, NetDataReader reader) {
        reader.Get(out knetid shooterClientId);
        reader.Get(out knetid gunSyncId);
        reader.Get(out Vector2 gunBarrelOrigin);
        reader.Get(out Vector2 gunBarrelDirection);

        if (ItemSync.TryGetItemSyncInfo(gunSyncId, out var gunSi) && gunSi.IsGun()) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(gunSi.transformPosition, $"C: PlayerBodyShoot_Relay {DebugHelp.GetPlayerFullDebugString(shooterClientId)}");
            }
            ReplayRemoteGunshotVisuals(gunSi, gunBarrelOrigin, gunBarrelDirection);
        }
    }

    static void ReplayRemoteGunshotVisuals(SyncInfo gunSi, Vector2 gunBarrelOrigin, Vector2 gunBarrelDirection) {
        var gun = gunSi.gun;
        var gst = gunSi.go.GetOrAddComponent<TrackerGunScript>();

        gst.override_shoot_trajectory = true;
        gst.override_shoot_trajectory_origin = gunBarrelOrigin;
        gst.override_shoot_trajectory_direction = gunBarrelDirection;

        gun.triggerPressed = true;
        gun.firingPinStruck = false;

        if (gun.roundInChamber != RoundInChamber.Round) {
            if (gun.roundInChamber == RoundInChamber.Casing) {
                var casing = UnityEngine.Object.Instantiate(Resources.Load("casing"), gun.transform.position, gun.transform.rotation) as GameObject;
                if (casing != null && casing.TryGetComponent<Rigidbody2D>(out var casingRb)) {
                    casingRb.velocity = gun.transform.up * 12f;
                }
            }
            gun.roundInChamber = RoundInChamber.Round;
            gun.roundsInMag--;
        }

        gun.lastRacked = false;
        gun.racked = false;
        gun.safe = false;

        gst.ApplyRecoil();
        gunSi.gun.Update();
    }
}

[HarmonyPatch(typeof(GunScript), nameof(GunScript.Fire))]
public static class GunScript_Fire_MultiplayerPatch {
    public static int MAX_HITS_PER_LIMB_AT_ONCE = 4;

    internal static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var instructionList = instructions.ToList();
        int i = 0;
        bool patched = false;

        var resolveBodyMethod = AccessTools.Method(typeof(GunCombatSync), nameof(GunCombatSync.ResolveGunBody));

        while (i < instructionList.Count) {
            var code = instructionList[i];
            if (code.opcode == OpCodes.Call
                && code.operand is MethodInfo method
                && method.Name == "get_body"
                && method.DeclaringType == typeof(GunScript)) {
                var callInst = new CodeInstruction(OpCodes.Call, resolveBodyMethod);
                callInst.MoveLabelsFrom(code);
                yield return callInst;
                i++;
                patched = true;
            } else {
                yield return instructionList[i];
                i++;
            }
        }
        if (!patched) {
            ModLog.Error("GunScript_Fire_MultiplayerPatch: failed to patch body getters", LogDomain.Combat);
        }
    }

    public static Vector2 RollVerticalSpread(GunScript gun) => GunCombatSync.RollVerticalSpread(gun);

    public static void Prefix(GunScript __instance, ref GunScript __state) {
        __state = GunScript_Update_MultiplayerPatch.current_executing_gun;
        if (!Net.IsRunning) {
            return;
        }
        if (__state == null) {
            GunScript_Update_MultiplayerPatch.current_executing_gun = __instance;
        }
        var gst = __instance.GetOrAddComponent<TrackerGunScript>();
        if (gst != null) {
            gst.ScanForBody();
            gst.ApplyRecoil();
        }
    }

    static void Postfix(GunScript __instance, bool suicide, ref GunScript __state) {
        if (!Net.IsRunning) {
            return;
        }
        try {
            GunCombatSync.CheckForHitsAndTellServer(__instance, suicide);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(GunScript_Fire_MultiplayerPatch), ex);
        }
        GunScript_Update_MultiplayerPatch.current_executing_gun = __state;
    }
}

[HarmonyPatch(typeof(GunScript), nameof(GunScript.JamChance))]
public static class GunScript_JamChance_MultiplayerPatch {
    public static bool ForceJam = false;

    static void Postfix(GunScript __instance, ref float __result) {
        if (!Net.IsRunning) {
            return;
        }
        var gst = __instance.GetOrAddComponent<TrackerGunScript>();
        if (gst.body != null && gst.body.TryGetNetBody(out var nb) && !nb.IsLocal) {
            if (ForceJam) {
                ForceJam = false;
                __result = 3f;
            } else {
                __result = -1f;
            }
        }
    }
}

[HarmonyPatch(typeof(GunScript), nameof(GunScript.UnloadMag))]
public static class GunScript_UnloadMag_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return GunScript_Fire_MultiplayerPatch.Transpiler(instructions);
    }

    public static void Prefix(GunScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        __instance.GetOrAddComponent<TrackerGunScript>();
    }
}

[HarmonyPatch(typeof(GunScript), nameof(GunScript.Update))]
public static class GunScript_Update_MultiplayerPatch {
    public static GunScript current_executing_gun = null;

    static void Prefix(GunScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        current_executing_gun = __instance;
    }

    static void Postfix() {
        current_executing_gun = null;
    }
}

[HarmonyPatch(typeof(GunScript), nameof(GunScript.Start))]
public static class GunScript_Start_MultiplayerPatch {
    static void Postfix(GunScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        __instance.GetOrAddComponent<TrackerGunScript>();
        __instance.lastRacked = __instance.racked;
    }
}
