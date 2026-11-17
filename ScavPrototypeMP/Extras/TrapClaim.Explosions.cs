using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// <see cref="TrapClaim"/> 的地雷引信、爆炸伤害认领/广播、以及冷喷口、活塞与水晶幻影突袭逻辑。
/// </summary>
internal static partial class TrapClaim {
    struct ArmedMine {
        internal Body body;
        internal float until;
    }

    static readonly Dictionary<int, ArmedMine> armedMines = new();

    internal static void ArmMine(MineScript mine, Component hit) {
        if (mine == null || mine.exploded || !IsLocal(hit, out var body)) {
            return;
        }
        int id = mine.GetInstanceID();
        if (armedMines.ContainsKey(id)) {
            return;
        }
        if (armedMines.Count > 64) {
            armedMines.Clear();
        }
        armedMines[id] = new ArmedMine { body = body, until = Time.time + 0.8f };
    }

    internal static void TickMine(MineScript mine) {
        if (mine == null) {
            return;
        }
        int id = mine.GetInstanceID();
        if (!armedMines.TryGetValue(id, out var arm) || Time.time < arm.until) {
            return;
        }
        armedMines.Remove(id);
        if (arm.body != null) {
            ClientMine(mine, arm.body);
        }
    }

    internal static void ClientMine(MineScript mine, Component hit) {
        if (mine == null || !IsLocal(hit, out var body) || !NetObjectRegistry.TryGetSyncInfo(mine, out var si)) {
            return;
        }
        ExplosionParams param = new ExplosionParams { position = mine.transform.position + Vector3.up };
        if (!FreshBoom(param.position)) {
            return;
        }
        ushort mask = ApplyBoomLocal(body, param);
        if (mask == 0) {
            return;
        }
        Ignore(body);
        Send(KindBoom, si.syncId, mask);
    }

    internal static void ClientSawBoom(knetid id, ExplosionParams param) {
        if (param == null || !FreshBoom(param.position)) {
            return;
        }
        var cam = PlayerCamera.main;
        if (cam == null || cam.body == null || !Util.IsBodyLocal(cam.body)) {
            return;
        }
        ushort mask = ApplyBoomLocal(cam.body, param);
        bool standingOnIt = KM.DistanceCheck(param.position, cam.body.transform.position, Mathf.Min(4f, param.range > 0f ? param.range : 4f));
        if ((mask == 0 && !standingOnIt) || id.id == 0) {
            return;
        }
        Ignore(cam.body);
        Send(KindBoom, id, mask);
    }

    static bool FreshBoom(Vector2 pos) {
        float now = Time.unscaledTime;
        if (now - lastLocalBoomTime < 1f && KM.DistanceCheck(pos, lastLocalBoom, 2f)) {
            return false;
        }
        lastLocalBoom = pos;
        lastLocalBoomTime = now;
        return true;
    }

    static ushort ApplyBoomLocal(Body body, ExplosionParams param) {
        ushort mask = 0;
        if (body == null || body.limbs == null) {
            return 0;
        }
        int i = 0;
        foreach (var limb in body.limbs) {
            if (limb == null) {
                i++;
                continue;
            }
            if (i > 15) {
                break;
            }
            Vector2 origin = param.position;
            Vector2 toLimb = (Vector2)limb.transform.position - origin;
            float span = toLimb.magnitude;
            bool standingOnIt = span <= 4f;
            if (!standingOnIt && span > 0.01f) {
                origin += toLimb / span * 1.5f;
            }
            if ((!standingOnIt && Physics2D.Linecast(origin, limb.transform.position, PhysicsLayers.GroundMask))
                || !KM.DistanceCheck(param.position, limb.transform.position, param.range)) {
                i++;
                continue;
            }
            mask |= (ushort)(1 << i);
            ApplyBoomLimb(limb, param, true);
            i++;
        }
        return mask;
    }

    static void ApplyBoomLimb(Limb limb, ExplosionParams param, bool local) {
        float armor = limb.GetArmorReduction();
        if (Random.Range(0f, 1f) < param.skinDamageChance) {
            limb.skinHealth -= param.skinDamage.RandomFromRange() / armor;
        }
        limb.muscleHealth -= param.muscleDamage.RandomFromRange() / armor;
        limb.body.shock = 100f;
        if (local) {
            limb.body.lastTimeStepVelocity = ((Vector2)limb.body.transform.position - param.position).normalized * param.velocity;
            limb.body.Ragdoll();
        }
        if (!limb.hasShrapnel) {
            limb.shrapnel = Random.value < param.shrapnelChance ? 5 : 0;
        }
        limb.DamageWearables(param.shrapnelChance);
        if (limb.isVital && Random.value < 0.5f) {
            limb.body.internalBleeding += param.muscleDamage.RandomFromRange() * 0.4f / armor;
        }
        if (Random.Range(0f, 1f) < param.bleedChance) {
            limb.bleedAmount += param.bleedAmount.RandomFromRange() / armor;
        }
        if (Random.Range(0f, 1f) < param.boneBreakChance / armor) {
            limb.BreakBone();
        }
        if (Random.Range(0f, 1f) < param.dislocationChance / armor) {
            limb.Dislocate();
        }
        if (!limb.isHead) {
            return;
        }
        limb.body.consciousness = 0f;
        if (Random.Range(0f, 1f) < 0.7f / armor) {
            limb.body.brainHealth -= param.muscleDamage.RandomFromRange() / armor * 0.5f;
        }
        if (Random.Range(0f, 1f) < param.disfigureChance / armor) {
            limb.body.Disfigure();
        }
        if (Random.Range(0f, 1f) < param.disfigureChance / armor) {
            limb.body.RemoveEye();
        }
    }

    internal static void HostBoom(ExplosionParams param) {
        if (param == null) {
            return;
        }
        var id = pendingBoom;
        pendingBoom = default;
        if (id.id == 0) {
            id = NextBoomId();
        }
        float now = Time.unscaledTime;
        booms.Add(new Boom {
            id = id,
            until = now + BoomWindow,
            param = param,
            who = new HashSet<ushort>(),
        });
        if (booms.Count > 32) {
            booms.RemoveRange(0, booms.Count - 16);
        }

        var writer = Net.CreateWriter(NetmsgId.CreateExplosionEffect);
        WriteBoom(writer, id, param);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);

        try {
            var hits = Physics2D.OverlapCircleAll(param.position, param.range);
            var seen = new HashSet<NetBody>();
            for (int i = 0; i < hits.Length; i++) {
                if (Util.TryGetBodyFromObject(hits[i], out var body) && body.TryGetNetBody(out var nb)) {
                    seen.Add(nb);
                }
            }
            foreach (var nb in seen) {
                nb.OnDamageEvent(new BodyDamageEvent(ServerMain.PickRandomDeathMessageTag("plrdeath_exploded"), nb));
            }
        } catch (System.Exception ex) {
            ModLog.Exception(LogDomain.Combat, nameof(HostBoom), ex);
        }
    }

    internal static void ClearPending() {
        pendingBoom = default;
    }

    static knetid NextBoomId() {
        ushort serial = 50000;
        for (int i = 0; i < 64; i++) {
            ushort id = (ushort)(serial + (Time.frameCount + i) % 15000);
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out _)) {
                return id;
            }
        }
        return (ushort)(50000 + (Time.frameCount % 15000));
    }

    internal static void WriteBoom(NetDataWriter writer, knetid id, ExplosionParams param) {
        writer.Put(param.position);
        writer.Put(param.velocity);
        writer.Put(param.range);
        writer.Put(id);
        writer.Put(param.muscleDamage.min);
        writer.Put(param.muscleDamage.max);
        writer.Put(param.skinDamage.min);
        writer.Put(param.skinDamage.max);
        writer.Put(param.skinDamageChance);
        writer.Put(param.boneBreakChance);
        writer.Put(param.dislocationChance);
        writer.Put(param.disfigureChance);
        writer.Put(param.bleedChance);
        writer.Put(param.bleedAmount.min);
        writer.Put(param.bleedAmount.max);
        writer.Put(param.shrapnelChance);
        writer.Put(param.structuralDamage);
        writer.Put(param.sound ?? "explosion");
    }

    internal static bool ReadBoom(NetDataReader reader, out knetid id, out ExplosionParams param) {
        id = default;
        param = null;
        if (reader.AvailableBytes < 12) {
            return false;
        }
        reader.Get(out Vector2 pos);
        reader.Get(out float vel);
        reader.Get(out float range);
        param = new ExplosionParams { position = pos, velocity = vel, range = range };
        if (reader.AvailableBytes < 2) {
            return true;
        }
        reader.Get(out id);
        if (reader.AvailableBytes < 4) {
            return true;
        }
        param.muscleDamage = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.skinDamage = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.skinDamageChance = reader.GetFloat();
        param.boneBreakChance = reader.GetFloat();
        param.dislocationChance = reader.GetFloat();
        param.disfigureChance = reader.GetFloat();
        param.bleedChance = reader.GetFloat();
        param.bleedAmount = new RangeF(reader.GetFloat(), reader.GetFloat());
        param.shrapnelChance = reader.GetFloat();
        param.structuralDamage = reader.GetFloat();
        param.sound = reader.GetString();
        return true;
    }

    internal static void HideRemoteBodies() {
        if (hideDepth++ > 0) {
            return;
        }
        foreach (var nb in NetBody.all_instances) {
            if (nb == null || nb.body == null || nb.IsBodyLocal()) {
                continue;
            }
            var cols = nb.body.GetComponentsInChildren<Collider2D>(true);
            for (int i = 0; i < cols.Length; i++) {
                var col = cols[i];
                if (col == null || col.GetComponentInParent<Item>() != null) {
                    continue;
                }
                hidden.Add(col);
                hiddenWas.Add(col.enabled);
                col.enabled = false;
            }
        }
    }

    internal static void ShowRemoteBodies() {
        if (hideDepth <= 0 || --hideDepth > 0) {
            return;
        }
        for (int i = 0; i < hidden.Count; i++) {
            if (hidden[i] != null) {
                hidden[i].enabled = hiddenWas[i];
            }
        }
        hidden.Clear();
        hiddenWas.Clear();
    }

    internal static void PublishLocals(ExplosionParams param) {
        if (param == null || !Net.IsServer) {
            return;
        }
        foreach (var nb in NetBody.all_instances) {
            if (nb == null || nb.body == null || !nb.IsBodyLocal()) {
                continue;
            }
            if (KM.DistanceCheck(param.position, nb.body.transform.position, param.range)) {
                Publish(nb);
            }
        }
    }

    static Boom FindBoom(knetid id) {
        float now = Time.unscaledTime;
        for (int i = booms.Count - 1; i >= 0; i--) {
            if (booms[i].until < now) {
                continue;
            }
            if (id.id != 0 && booms[i].id.id == id.id) {
                return booms[i];
            }
        }
        return null;
    }

    static Boom LatestBoom() {
        float now = Time.unscaledTime;
        for (int i = booms.Count - 1; i >= 0; i--) {
            if (booms[i].until >= now) {
                return booms[i];
            }
        }
        return null;
    }

    static void ConsumeMine(MineScript mine, knetid id) {
        mine.pressed = true;
        mine.exploded = true;
        if (mine.build != null) {
            mine.build.SetHealth(0f);
        }
        var sprite = mine.GetComponent<SpriteRenderer>();
        if (sprite != null && mine.pressedSprite != null) {
            sprite.sprite = mine.pressedSprite;
        }
        var writer = Net.CreateWriter(NetmsgId.CLIENT_MineStepped);
        writer.Put((Vector2)mine.transform.position);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        pendingBoom = id;
        ExplosionParams explosion = new ExplosionParams { position = mine.transform.position + Vector3.up };
        WorldGeneration.CreateExplosion(explosion);
        pendingBoom = default;
    }

#if FULLGAME
    internal static bool ColdPuffBlowLocal(ColdPuffScript puff) {
        if (puff == null) {
            return true;
        }
        var blownField = Traverse.Create(puff).Field("blown");
        if (blownField.GetValue<bool>()) {
            return false;
        }
        blownField.SetValue(true);

        Vector2 puffPos = puff.transform.position;
        const float coolRadius = 8f;

        if (!Net.IsRunning || Net.IsServer) {
            ChillBodies(puff, puffPos, coolRadius);
        } else if (NetObjectRegistry.TryGetSyncInfo(puff.gameObject, out SyncInfo si) && si != null) {
            NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
            writer.Put(CustomWireCommands.ColdPuffBlow + " " + si.syncId.id.ToString());
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        if (Util.TryGetLocalBody(out Body local)
            && Vector2.Distance(local.transform.position, puffPos) < coolRadius) {
            if (Net.IsRunning && Net.IsClient) {
                local.snowAmount = 100f;
            }
            local.talker?.Talk("...");
            if (local.rb != null) {
                local.rb.velocity += Vector2.up * 12f;
            }
        }

        BuildingEntity build = puff.GetComponent<BuildingEntity>();
        if (build != null) {
            build.description = Locale.GetBuilding("coldpuffdsc2");
        }
        Object.Destroy(Utils.Create("Special/ColdPuffParticle", puff.transform.position, 0f), 10f);
        Sound.Play("puff", puff.transform.position);
        var sr = puff.GetComponent<SpriteRenderer>();
        if (sr != null && puff.openSprite != null) {
            sr.sprite = puff.openSprite;
        }
        return false;
    }

    static readonly HashSet<int> chilledPuffs = new();

    internal static void ChillBodies(ColdPuffScript puff, Vector2 puffPos, float coolRadius) {
        if (puff == null || !chilledPuffs.Add(puff.GetInstanceID())) {
            return;
        }
        bool any = false;
        foreach (var nb in NetBody.GetBodiesInRadius(puffPos, coolRadius)) {
            Body coolBody = nb != null ? nb.body : null;
            if (coolBody == null) {
                continue;
            }
            any = true;
            coolBody.temperature -= 8f;
            coolBody.snowAmount = 100f;
            if (Net.IsRunning && Net.IsServer) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
        if (!any) {
            chilledPuffs.Remove(puff.GetInstanceID());
        }
    }

    internal static void ApplyColdPuffBlown(ColdPuffScript puff) {
        if (puff == null) {
            return;
        }
        var blownField = Traverse.Create(puff).Field("blown");
        if (blownField.GetValue<bool>()) {
            return;
        }
        blownField.SetValue(true);
        BuildingEntity build = puff.GetComponent<BuildingEntity>();
        if (build != null) {
            build.description = Locale.GetBuilding("coldpuffdsc2");
        }
        var sr = puff.GetComponent<SpriteRenderer>();
        if (sr != null && puff.openSprite != null) {
            sr.sprite = puff.openSprite;
        }
    }

    internal static void ClientPistonFollowHost(PistonScript piston) {
        if (piston == null || !NetObjectRegistry.TryGetSyncInfo(piston, out var si) || si == null) {
            return;
        }
        bool hostUp = si.specialdata1.Value != 0;
        var upField = Traverse.Create(piston).Field("up");
        bool wasUp = upField.GetValue<bool>();
        upField.SetValue(hostUp);

        Rigidbody2D pistonRb = Traverse.Create(piston).Field("piston").GetValue<Rigidbody2D>();
        if (pistonRb == null && piston.transform.childCount > 0) {
            pistonRb = piston.transform.GetChild(0).GetComponent<Rigidbody2D>();
        }
        if (pistonRb == null) {
            return;
        }

        if (hostUp != wasUp) {
            var rend = piston.GetComponent<SpriteRenderer>();
            if (rend == null || rend.isVisible) {
                Sound.Play(hostUp ? "piston" : "unpiston", piston.transform.position, twoDimensional: false, pitchShift: false);
            }
        }

        pistonRb.collisionDetectionMode = (hostUp && Time.timeScale <= 5f)
            ? CollisionDetectionMode2D.Continuous
            : CollisionDetectionMode2D.Discrete;

        if (Time.deltaTime > 0.2f) {
            pistonRb.transform.localPosition = Vector2.up * 4f * (hostUp ? 1f : 0f);
            pistonRb.velocity = Vector2.zero;
        } else if (hostUp && pistonRb.transform.localPosition.y < 4f) {
            pistonRb.velocity = (Vector2)piston.transform.up * 60f;
        } else if (!hostUp && pistonRb.transform.localPosition.y > 0f) {
            pistonRb.velocity = (Vector2)piston.transform.up * -60f * 0.3f;
        } else {
            pistonRb.velocity = Vector2.zero;
            pistonRb.transform.localPosition = Vector2.up * 4f * (hostUp ? 1f : 0f);
        }

        if (hostUp && piston.transform.up.y < 0.9f && Util.TryGetLocalBody(out Body local)) {
            Collider2D overlap = Traverse.Create(piston).Field("overlap").GetValue<Collider2D>();
            if (overlap == null && piston.transform.childCount > 1) {
                overlap = piston.transform.GetChild(1).GetComponent<Collider2D>();
            }
            if (overlap == null) {
                return;
            }
            ContactFilter2D filter = default;
            filter.SetLayerMask(LayerMask.GetMask("Body"));
            Collider2D[] hits = new Collider2D[4];
            int n = overlap.OverlapCollider(filter, hits);
            for (int i = 0; i < n; i++) {
                if (hits[i] != null && hits[i].transform == local.transform) {
                    local.Ragdoll();
                    local.Scream();
                    break;
                }
            }
        }
    }
#endif

    static readonly Dictionary<int, float> manifestClaimed = new();

    internal static void HostManifestLunged(CrystalEnemy enemy) {
        if (Net.IsServer && enemy != null && NetObjectRegistry.TryGetSyncInfo(enemy, out var si)) {
            Relay(KindManifest, si.syncId, 0f);
        }
    }

    internal static bool ManifestMayHurt(CrystalEnemy enemy, Body body) {
        if (!Net.IsRunning || enemy == null || body == null) {
            return true;
        }
        return Util.IsBodyLocal(body);
    }

    internal static void ManifestHurtLocal(CrystalEnemy enemy, Body body) {
        if (enemy == null || body == null || !Util.IsBodyLocal(body) || !NetObjectRegistry.TryGetSyncInfo(enemy, out var si)) {
            return;
        }
        int key = si.syncId.id ^ body.GetInstanceID();
        float now = Time.unscaledTime;
        if (manifestClaimed.TryGetValue(key, out float until) && now < until) {
            return;
        }
        manifestClaimed[key] = now + ManifestWindow;
        if (manifestClaimed.Count > 400) {
            manifestClaimed.Clear();
        }

        ApplyManifestLunge(enemy, body);
        Ignore(body);
        if (Net.IsClient) {
            Send(KindManifest, si.syncId, 0);
        } else if (body.TryGetNetBody(out var nb)) {
            Publish(nb);
        }
    }

    static void ApplyManifestLunge(CrystalEnemy enemy, Body body) {
        if (enemy == null || body == null || body.limbs == null || body.limbs.Length == 0) {
            return;
        }
        List<Limb> live = new();
        for (int i = 0; i < body.limbs.Length; i++) {
            Limb limb = body.limbs[i];
            if (limb != null && !limb.dismembered) {
                live.Add(limb);
            }
        }
        if (live.Count == 0) {
            return;
        }
        Limb pick = live[Random.Range(0, live.Count)];
        float armor = pick.GetArmorReduction();
        pick.DamageWearables(0.4f);
        pick.muscleHealth -= 35f / armor;
        pick.skinHealth -= 50f / armor;
        pick.pain += 35f / armor;
        pick.bleedAmount += 15f / armor;
        body.adrenaline += 70f;
        body.stamina = 100f;
        body.eyePanicTime = 0.5f;
        body.Scream();
        body.Ragdoll();
        body.DoGoreSound();
        if (Net.IsServer) {
            BuildingEntity build = enemy.GetComponent<BuildingEntity>();
            if (build != null) {
                build.SetHealth(build.GetHealth() - 100f);
            }
        }
        Sound.Play("crystalenemylaugh", enemy.transform.position, twoDimensional: false, pitchShift: false);
    }

    static bool AcceptManifest(knetid id, NetBody nb) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent(out CrystalEnemy enemy)) {
            return false;
        }
        if (!Near(enemy, nb.body) || !TakeWindow(KindManifest, id, nb.netId, false, ManifestWindow)) {
            return false;
        }
        BuildingEntity build = enemy.GetComponent<BuildingEntity>();
        if (build != null && build.GetHealth() > 0.5f) {
            build.SetHealth(build.GetHealth() - 100f);
        }
        Publish(nb);
        return true;
    }
}
