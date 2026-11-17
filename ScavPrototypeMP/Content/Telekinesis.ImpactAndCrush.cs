using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

/// <summary>
/// 隔空取物赋予场景物体的临时刚体、抛射砸击状态与静止回收管理器。
/// </summary>
static class Loose {
    const int FallingCrateType = 1;

    class Loan {
        public GameObject go;
        public Rigidbody2D rb;
        public int layer;
        public bool added;
        public BuildingEntity building;
        public bool optimize;
        public bool missile;
        public float rest;
        public bool crateOwned;
    }

    static readonly List<Loan> loans = new List<Loan>();

    internal static void Lend(GameObject go, Rigidbody2D rb, int layer, BuildingEntity building, bool optimize) {
        Loan loan = Find(go);
        if (loan == null) {
            loan = new Loan();
            loans.Add(loan);
        }
        loan.go = go;
        loan.rb = rb;
        loan.layer = layer;
        loan.added = true;
        loan.building = building;
        loan.optimize = optimize;
        loan.missile = true;
        loan.rest = 0f;
        EnsureFallingCrate(loan);
    }

    internal static void Arm(GameObject go, Rigidbody2D rb) {
        Loan loan = Find(go);
        if (loan == null) {
            loan = new Loan { layer = go.layer };
            loans.Add(loan);
        }
        loan.go = go;
        loan.rb = rb;
        loan.missile = true;
        loan.rest = 0f;
        EnsureFallingCrate(loan);
        Listen(go);
    }

    static void EnsureFallingCrate(Loan loan) {
        if (loan == null || loan.go == null) return;
        DamagingCrate crate = loan.go.GetComponent<DamagingCrate>();
        if (crate == null) {
            crate = loan.go.AddComponent<DamagingCrate>();
            crate.type = FallingCrateType;
            loan.crateOwned = true;
        } else if (loan.crateOwned) {
            crate.type = FallingCrateType;
        }
    }

    internal static void Listen(GameObject go) {
        if (go == null || go.GetComponent<MissileTouch>() != null) return;
        go.AddComponent<MissileTouch>();
    }

    internal static bool IsMissile(GameObject go) {
        if (go == null) return false;
        if (go.GetComponentInParent<SpiderHandler>() != null) return false;
        if (Impact.Live() && Impact.IsHeld(go)) return true;
        for (int i = 0; i < loans.Count; i++) {
            Loan loan = loans[i];
            if (!loan.missile || loan.go == null) continue;
            Transform a = go.transform;
            Transform b = loan.go.transform;
            if (a == b || a.IsChildOf(b) || b.IsChildOf(a)) return true;
        }
        return false;
    }

    internal static void Tick() {
        float dt = Time.fixedDeltaTime > 0.0001f ? Time.fixedDeltaTime : 0.02f;
        for (int i = loans.Count - 1; i >= 0; i--) {
            Loan loan = loans[i];
            if (loan.go == null) {
                loans.RemoveAt(i);
                continue;
            }
            if (Impact.IsHeld(loan.go)) {
                loan.rest = 0f;
                EnsureFallingCrate(loan);
                continue;
            }
            if (loan.missile) {
                EnsureFallingCrate(loan);
            }
            Rigidbody2D rb = loan.rb;
            bool asleep = rb == null || rb.IsSleeping();
            bool slow = rb != null && rb.velocity.magnitude < 0.45f && Mathf.Abs(rb.angularVelocity) < 15f;
            loan.rest = (asleep || slow) ? (loan.rest + dt) : 0f;
            if (loan.rest < 0.2f) continue;

            if (loan.crateOwned) {
                DamagingCrate crate = loan.go.GetComponent<DamagingCrate>();
                if (crate != null) Object.Destroy(crate);
                loan.crateOwned = false;
            }
            if (loan.added) {
                loan.go.layer = loan.layer;
                if (loan.building != null) loan.building.ignoreBodyOptimize = loan.optimize;
                if (rb != null) Object.Destroy(rb);
            }
            loans.RemoveAt(i);
        }
    }

    static Loan Find(GameObject go) {
        for (int i = 0; i < loans.Count; i++) {
            if (loans[i].go == go) return loans[i];
        }
        return null;
    }
}

static class CreatureHold {
    const float TossSpeed = 10f;
    const float FullStunSpeed = 30f;

    static SpiderHandler held;
    static SpiderHandler flung;
    static float flungUntil;

    internal static SpiderHandler Held => held;

    internal static void Sync(SpiderHandler spider) {
        if (held == spider) return;
        Release();
        held = spider;
    }

    internal static void Release() {
        if (held == null) return;
        Rigidbody2D body = held.GetComponent<Rigidbody2D>();
        float speed = body != null ? body.velocity.magnitude : 0f;
        if (speed >= TossSpeed) {
            float t = Mathf.Clamp01((speed - TossSpeed) / (FullStunSpeed - TossSpeed));
            float stun = Mathf.Lerp(0.35f, 1f, t);
            if (held.stunTime < stun) held.stunTime = stun;
            flung = held;
            flungUntil = Time.time + stun;
        }
        held = null;
    }

    internal static bool IsFlung(GameObject go) {
        if (flung == null || go == null || Time.time > flungUntil) return false;
        Transform a = go.transform;
        Transform b = flung.transform;
        return a == b || a.IsChildOf(b) || b.IsChildOf(a);
    }
}

static class Impact {
    const float Slow = 6f;
    const float Fast = 26f;
    static int lastId;
    static float nextTime;

    internal static bool Live() {
        return Con._DEV_TELEKINESIS && Multiplayer.IsNetworkActiveAndIsServer() && Con._DEV_TELEKINESIS_OBJECT != null;
    }

    internal static bool IsHeld(GameObject go) {
        GameObject held = Con._DEV_TELEKINESIS_OBJECT;
        if (held == null || go == null) return false;
        Transform a = go.transform;
        Transform b = held.transform;
        return a == b || a.IsChildOf(b) || b.IsChildOf(a);
    }

    internal static void Hurt(GameObject go, float speed, float scale) {
        if (go == null || speed < Slow) return;
        if (!Live() && !CreatureHold.IsFlung(go) && scale <= 1f) return;

        float damage = Amount(speed) * scale;
        BuildingEntity build = go.GetComponent<BuildingEntity>() ?? go.GetComponentInParent<BuildingEntity>();
        if (build != null) {
            Spend(build, () => { build.health -= damage; });
            return;
        }
        Limb limb = go.GetComponent<Limb>() ?? go.GetComponentInParent<Limb>();
        if (limb == null) return;
        Object key = limb.body != null ? limb.body : (Object)limb;
        Spend(key, () => {
            float left = limb.muscleHealth - damage;
            limb.muscleHealth = left > 0f ? left : 0f;
        });
    }

    internal static void Hurt(GameObject go, float speed) => Hurt(go, speed, 1f);

    static float Amount(float speed) => Mathf.Lerp(10f, 100f, Mathf.InverseLerp(Slow, Fast, speed));

    static void Spend(Object key, System.Action apply) {
        int id = key.GetInstanceID();
        if (id == lastId && Time.time < nextTime) return;
        lastId = id;
        nextTime = Time.time + 0.25f;
        apply();
    }
}

[HarmonyPatch(typeof(BuildingEntity), "OnCollisionEnter2D")]
static class BuildingImpactPatch {
    static void Postfix(BuildingEntity __instance, Collision2D collision) {
        if (__instance == null) return;
        GameObject other = collision.collider != null ? collision.collider.gameObject : null;
        float speed = collision.relativeVelocity.magnitude;
        if (other != null && (Loose.IsMissile(__instance.gameObject) || (Impact.Live() && Impact.IsHeld(__instance.gameObject)))) {
            PlayerCrush.NoteContact(collision, __instance.gameObject);
            SpiderHandler foe = other.GetComponent<SpiderHandler>() ?? other.GetComponentInParent<SpiderHandler>();
            if (foe != null) Impact.Hurt(foe.gameObject, speed, 2f);
        }
        if (CreatureHold.IsFlung(__instance.gameObject) || (Impact.Live() && Impact.IsHeld(__instance.gameObject))) {
            Impact.Hurt(__instance.gameObject, speed);
        }
    }
}

[HarmonyPatch(typeof(Limb), "OnCollisionEnter2D")]
static class LimbImpactPatch {
    static void Postfix(Limb __instance, Collision2D col) {
        if (__instance == null) return;
        PlayerCrush.NoteLimb(__instance, col);
        PlayerCrush.NoteSprite(__instance, col);
        if (!Impact.Live()) return;
        float speed = col.relativeVelocity.magnitude;
        GameObject other = col.collider != null ? col.collider.gameObject : null;
        if (other != null && Impact.IsHeld(other)) {
            Impact.Hurt(other, speed);
        }
        if (__instance.body != null && Impact.IsHeld(__instance.body.gameObject)) {
            Impact.Hurt(__instance.gameObject, speed);
        }
    }
}

[HarmonyPatch(typeof(SpiderHandler), "OnCollisionEnter2D")]
static class SpiderImpactPatch {
    static void Postfix(SpiderHandler __instance, Collision2D collision) {
        if (__instance == null) return;
        GameObject go = __instance.gameObject;
        float speed = collision.relativeVelocity.magnitude;
        GameObject other = collision.collider != null ? collision.collider.gameObject : null;
        if (other != null && Loose.IsMissile(other)) {
            Impact.Hurt(go, speed, 2f);
            return;
        }
        if (CreatureHold.IsFlung(go) || (Impact.Live() && Impact.IsHeld(go))) {
            Impact.Hurt(go, speed);
        }
    }
}

[HarmonyPatch(typeof(Body), "OnCollisionEnter2D")]
static class BodyCrushPatch {
    static void Postfix(Body __instance, Collision2D collision) {
        PlayerCrush.NoteBody(__instance, collision);
        PlayerCrush.NoteSpriteBody(__instance, collision);
    }
}

static class PlayerCrush {
    const float HitSpeed = 6f;
    internal const float PropHitSpeed = 2.5f;
    const float RestSpeed = 4f;
    const float FlightTime = 2.5f;

    static Body live;
    static float until;
    static int lastId;
    static float nextTime;

    internal static void Sync(Body victim) {
        if (victim != null) {
            if (live != null && live != victim) Collide(live, true);
            live = victim;
            until = Time.time + FlightTime;
            Collide(live, false);
            return;
        }
        if (live == null) return;
        if (Time.time > until || Rested(live)) {
            Collide(live, true);
            live = null;
            return;
        }
        Collide(live, false);
    }

    internal static void NoteLimb(Limb limb, Collision2D col) {
        if (limb != null && limb.body != null) Note(limb.body, col, limb);
    }

    internal static void NoteBody(Body body, Collision2D col) => Note(body, col, null);

    internal static void NoteSprite(Limb limb, Collision2D col) {
        if (limb != null && limb.body != null) NoteSprite(limb.body, col, limb);
    }

    internal static void NoteSpriteBody(Body body, Collision2D col) => NoteSprite(body, col, null);

    static void NoteSprite(Body owner, Collision2D col, Limb ownerLimb) {
        if (owner == null || col == null) return;
        GameObject missile = MissileOf(col, owner.transform);
        if (missile != null) NoteContact(col, missile, owner, ownerLimb);
    }

    internal static void NoteContact(Collision2D col, GameObject missile) {
        if (col == null || missile == null) return;
        Body victim = ForeignBody(col, missile.transform);
        Limb limb = victim != null ? LimbOf(null, victim, col) : null;
        NoteContact(col, missile, victim, limb);
    }

    internal static void NoteTrigger(GameObject missile, Collider2D other) {
        Rigidbody2D sprite = RigidOf(missile);
        NoteTrigger(missile, other, sprite != null ? sprite.velocity.magnitude : 0f);
    }

    internal static void NoteTrigger(GameObject missile, Collider2D other, float speed) {
        if (missile == null || other == null || !Loose.IsMissile(missile)) return;
        Body victim = BodyOf(other);
        if (!Foreign(victim, missile.transform) || speed < PropHitSpeed) return;
        Limb limb = LimbOf(other, victim, null);
        if (Crush(victim, limb, speed)) {
            Shove(victim, RigidOf(missile));
        }
    }

    static void NoteContact(Collision2D col, GameObject missile, Body victim, Limb limb) {
        if (col == null || missile == null || !Loose.IsMissile(missile) || !Foreign(victim, missile.transform)) return;
        float speed = col.relativeVelocity.magnitude;
        Rigidbody2D sprite = RigidOf(missile);
        if (sprite != null && sprite.velocity.magnitude > speed) speed = sprite.velocity.magnitude;
        if (speed < PropHitSpeed) return;
        limb ??= LimbOf(null, victim, col);
        if (Crush(victim, limb, speed)) {
            Shove(victim, sprite);
        }
    }

    static GameObject MissileOf(Collision2D col, Transform victim) {
        if (IsMissileSide(col.collider, victim)) return col.collider.gameObject;
        if (IsMissileSide(col.otherCollider, victim)) return col.otherCollider.gameObject;
        if (col.gameObject != null && Loose.IsMissile(col.gameObject) && !col.gameObject.transform.IsChildOf(victim)) {
            return col.gameObject;
        }
        return null;
    }

    static bool IsMissileSide(Collider2D col, Transform victim) {
        return col != null && Loose.IsMissile(col.gameObject) && !col.transform.IsChildOf(victim);
    }

    static Body ForeignBody(Collision2D col, Transform missile) {
        Body a = BodyOf(col.collider);
        if (Foreign(a, missile)) return a;
        Body b = BodyOf(col.otherCollider);
        if (Foreign(b, missile)) return b;
        if (col.gameObject != null) {
            Body g = BodyOf(col.gameObject.GetComponent<Collider2D>());
            if (g == null) {
                Limb limb = col.gameObject.GetComponent<Limb>() ?? col.gameObject.GetComponentInParent<Limb>();
                g = limb != null ? limb.body : col.gameObject.GetComponentInParent<Body>();
            }
            if (Foreign(g, missile)) return g;
        }
        int count = col.contactCount;
        for (int i = 0; i < count; i++) {
            ContactPoint2D point = col.GetContact(i);
            Body hit = BodyOf(point.collider);
            if (Foreign(hit, missile)) return hit;
            hit = BodyOf(point.otherCollider);
            if (Foreign(hit, missile)) return hit;
        }
        return null;
    }

    static bool Foreign(Body body, Transform missile) {
        return body != null && missile != null && body != Grab.Host() && !missile.IsChildOf(body.transform);
    }

    static Rigidbody2D RigidOf(GameObject go) {
        return go == null ? null : (go.GetComponent<Rigidbody2D>() ?? go.GetComponentInParent<Rigidbody2D>());
    }

    static void Shove(Body victim, Rigidbody2D sprite) {
        if (victim == null || sprite == null || victim.limbs == null) return;
        float spriteMass = sprite.mass > 0.2f ? sprite.mass : 0.2f;
        float bodyMass = 0f;
        Limb[] limbs = victim.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null) bodyMass += limb.rb.mass;
        }
        if (bodyMass < 0.2f) bodyMass = 0.2f;
        float share = Mathf.Min(0.5f, spriteMass / (spriteMass + bodyMass));
        Vector2 kick = sprite.velocity * share;
        if (kick.sqrMagnitude < 0.01f) return;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null && limb.rb.simulated) {
                limb.rb.velocity += kick;
            }
        }
        sprite.velocity -= kick * (bodyMass / spriteMass);
    }

    static void Note(Body owner, Collision2D col, Limb ownerLimb) {
        if (live == null || owner == null || col == null) return;
        Body other = BodyOf(col.collider);
        if (other == null || other == owner) return;
        Body victim = owner == live ? other : (other == live ? owner : null);
        if (victim == null || col.relativeVelocity.magnitude <= HitSpeed) return;
        Limb limb = ownerLimb != null && ownerLimb.body == victim ? ownerLimb : LimbOf(col.collider, victim, col);
        Crush(victim, limb, col.relativeVelocity.magnitude);
    }

    static bool Crush(Body victim, Limb limb, float speed) {
        if (victim == null || limb == null) return false;
        int id = victim.GetInstanceID();
        if (id == lastId && Time.time < nextTime) return false;
        lastId = id;
        nextTime = Time.time + 0.25f;
        float armor = Mathf.Max(0.05f, limb.GetArmorReduction());
        if (limb.isHead) victim.consciousness = 0f;
        limb.muscleHealth -= speed * 1.25f / armor;
        limb.skinHealth -= speed * 0.6f / armor;
        limb.bleedAmount += speed * 0.05f / armor;
        if (Random.value < 0.5f && speed > 25f) limb.Dislocate();
        if (Random.value < 0.5f && speed > 45f) limb.BreakBone();
        limb.DamageWearables(0.25f);
        victim.Ragdoll();
        return true;
    }

    static Body BodyOf(Collider2D col) {
        if (col == null) return null;
        Limb limb = col.GetComponent<Limb>() ?? col.GetComponentInParent<Limb>();
        if (limb != null && limb.body != null) return limb.body;
        return col.GetComponent<Body>() ?? col.GetComponentInParent<Body>();
    }

    static Limb LimbOf(Collider2D col, Body victim, Collision2D hit) {
        if (col != null) {
            Limb limb = col.GetComponent<Limb>() ?? col.GetComponentInParent<Limb>();
            if (limb != null && limb.body == victim) return limb;
        }
        Vector2 pos = victim.transform.position;
        if (hit != null && hit.contactCount > 0) pos = hit.GetContact(0).point;
        else if (col != null) pos = col.bounds.center;
        return victim.GetClosestLimb(pos);
    }

    static bool Rested(Body body) {
        if (body == null) return true;
        float best = body.rb != null ? body.rb.velocity.sqrMagnitude : 0f;
        Limb[] limbs = body.limbs;
        if (limbs == null) return best < RestSpeed * RestSpeed;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null && limb.rb.velocity.sqrMagnitude > best) {
                best = limb.rb.velocity.sqrMagnitude;
            }
        }
        return best < RestSpeed * RestSpeed;
    }

    static void Collide(Body body, bool ignore) {
        if (body == null) return;
        if (!ignore) body.SetLimbCollisionType(CollisionDetectionMode2D.Continuous);
        Collider2D[] mine = body.GetComponentsInChildren<Collider2D>(true);
        Body[] bodies = Object.FindObjectsOfType<Body>();
        for (int i = 0; i < bodies.Length; i++) {
            Body other = bodies[i];
            if (other == null || other == body) continue;
            Collider2D[] theirs = other.GetComponentsInChildren<Collider2D>(true);
            for (int a = 0; a < mine.Length; a++) {
                Collider2D ca = mine[a];
                if (ca == null) continue;
                for (int b = 0; b < theirs.Length; b++) {
                    Collider2D cb = theirs[b];
                    if (cb != null && cb != ca) {
                        Physics2D.IgnoreCollision(ca, cb, ignore);
                    }
                }
            }
        }
    }
}

sealed class MissileTouch : MonoBehaviour {
    readonly Collider2D[] overlaps = new Collider2D[16];
    Collider2D[] cols;
    ContactFilter2D filter;
    bool filterReady;
    float lastSpeed;

    void OnCollisionEnter2D(Collision2D col) => PlayerCrush.NoteContact(col, gameObject);
    void OnTriggerEnter2D(Collider2D other) => PlayerCrush.NoteTrigger(gameObject, other);

    void FixedUpdate() {
        if (!Loose.IsMissile(gameObject)) {
            lastSpeed = 0f;
            return;
        }
        Rigidbody2D rb = GetComponent<Rigidbody2D>() ?? GetComponentInParent<Rigidbody2D>();
        float now = rb != null ? rb.velocity.magnitude : 0f;
        float speed = now > lastSpeed ? now : lastSpeed;
        lastSpeed = now;
        if (speed < PlayerCrush.PropHitSpeed) return;

        cols ??= GetComponentsInChildren<Collider2D>();
        Vector2 dir = now > 0.2f ? rb.velocity / now : Vector2.zero;
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            if (col == null || !col.enabled) continue;
            Vector2 size = col.bounds.size;
            if (size.x < 0.05f || size.y < 0.05f) continue;
            if (!filterReady) {
                filter = new ContactFilter2D();
                filter.NoFilter();
                filterReady = true;
            }
            Vector2 center = (Vector2)col.bounds.center + dir * 0.2f;
            int found = Physics2D.OverlapBox(center, size + new Vector2(0.25f, 0.25f), 0f, filter, overlaps);
            for (int h = 0; h < found; h++) {
                Collider2D other = overlaps[h];
                if (other == null || other.transform == transform || other.transform.IsChildOf(transform)) continue;
                PlayerCrush.NoteTrigger(gameObject, other, speed);
            }
        }
    }
}
