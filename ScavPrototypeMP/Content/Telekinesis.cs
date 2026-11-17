using CasualtiesTogetherUtils;
using HarmonyLib;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

/// <summary>
/// 房主开发者隔空取物（Telekinesis）抓取目标解析与刚体质量估算器。
/// </summary>
static class Grab {
    internal const float MedcrateMass = 70f;
    const float ElderMass = 100f;

    internal static bool TryRemote(GameObject go, out NetBody nb) {
        nb = null;
        if (go == null) return false;
        Body body = go.GetComponent<Body>();
        if (body == null) {
            Limb limb = go.GetComponent<Limb>();
            if (limb != null) body = limb.body;
        }
        if (body == null) body = go.GetComponentInParent<Body>();
        return body != null && body.TryGetNetBody(out nb) && nb != null && !nb.IsLocal;
    }

    internal static Limb HeldLimb(GameObject go, Body body) {
        Limb held = go.GetComponent<Limb>() ?? go.GetComponentInParent<Limb>();
        return held != null ? held : body.GetUpperTorso();
    }

    internal static Body Host() {
        PlayerCamera cam = PlayerCamera.main;
        return cam != null ? cam.body : null;
    }

    internal static bool TryAnchor(GameObject go, out Transform held) {
        held = null;
        BuildingEntity building = go.GetComponent<BuildingEntity>() ?? go.GetComponentInParent<BuildingEntity>();
        if (building != null && IsFixture(building.id)) {
            held = building.transform;
            return true;
        }
        return false;
    }

    internal static SpiderHandler Creature(Rigidbody2D rb) {
        if (rb == null || rb.mass >= ElderMass) return null;
        return rb.GetComponent<SpiderHandler>() ?? rb.GetComponentInParent<SpiderHandler>();
    }

    static bool IsFixture(string id) {
        return id == "geyser"
            || id == "dropcapsule"
            || id == "drillpod"
            || id == "drillpodbroken"
            || id == "reinforceddoor"
            || id == "shuttledoor";
    }

    internal static bool TryPull(GameObject go, out Rigidbody2D rb) {
        BuildingEntity building = go.GetComponent<BuildingEntity>() ?? go.GetComponentInParent<BuildingEntity>();
        if (building != null) {
            go = building.gameObject;
        }

        rb = go.GetComponent<Rigidbody2D>() ?? go.GetComponentInParent<Rigidbody2D>();
        bool added = false;
        int keptLayer = go.layer;
        bool keptOptimize = building != null && building.ignoreBodyOptimize;
        if (rb == null) {
            rb = go.AddComponent<Rigidbody2D>();
            rb.gravityScale = 1f;
            rb.mass = 8f;
            rb.collisionDetectionMode = CollisionDetectionMode2D.Continuous;
            added = true;
        }
        if (building != null) {
            WeighBuilding(go, rb);
        }
        int ground = LayerMask.NameToLayer("Ground");
        if (ground >= 0 && Physics2D.GetIgnoreLayerCollision(go.layer, ground)) {
            go.layer = 0;
        }
        if (building != null) {
            building.ignoreBodyOptimize = true;
        }
        if (added) {
            Loose.Lend(go, rb, keptLayer, building, keptOptimize);
        }
        Loose.Listen(go);
        return rb != null;
    }

    static void WeighBuilding(GameObject go, Rigidbody2D rb) {
        Vector3 scale = go.transform.lossyScale;
        float sx = Mathf.Abs(scale.x);
        float sy = Mathf.Abs(scale.y);
        float area = 0f;
        Collider2D[] cols = go.GetComponents<Collider2D>();
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            if (col == null || col.isTrigger) continue;
            float piece;
            if (col is BoxCollider2D box) {
                piece = Mathf.Abs(box.size.x * sx * box.size.y * sy);
            } else if (col is CircleCollider2D circle) {
                piece = circle.radius * circle.radius * Mathf.PI * sx * sy;
            } else if (col is CapsuleCollider2D capsule) {
                piece = Mathf.Abs(capsule.size.x * sx * capsule.size.y * sy);
            } else {
                Vector3 bounds = col.bounds.size;
                piece = Mathf.Abs(bounds.x * bounds.y);
            }
            if (piece > area) area = piece;
        }

        SpriteRenderer sprite = go.GetComponent<SpriteRenderer>();
        if (sprite != null && sprite.sprite != null) {
            Vector3 size = Vector3.Scale(sprite.sprite.bounds.size, scale);
            float spriteArea = Mathf.Abs(size.x * size.y);
            if (spriteArea > area) area = spriteArea;
        }
        float sized = area * (70f / 16f);
        if (sized > rb.mass) rb.mass = sized;
    }
}

[HarmonyPatch(typeof(Con), "FixedUpdate")]
static class DragPatch {
    const float AccelPerMeter = 48f;
    const float MaxLead = 15f;
    const float SettleAccel = 8f;
    const float SwingShare = 0.45f;

    internal static Body HeldVictim;

    static bool Prefix() {
        Loose.Tick();
        if (!Con._DEV_TELEKINESIS || !Multiplayer.IsNetworkActiveAndIsServer()) {
            CreatureHold.Release();
            SetHeld(null);
            return true;
        }
        GameObject go = Con._DEV_TELEKINESIS_OBJECT;
        if (go == null || !Con.CanCheat()) {
            CreatureHold.Release();
            SetHeld(null);
            return true;
        }

        Vector2 cursor = Util.GetCursorWorldPos();
        Con.lastworldcursorpos = cursor;

        Body victim = null;
        Limb heldLimb = null;
        Transform heldTransform;
        Rigidbody2D rb = null;
        bool brace = false;

        if (Grab.TryRemote(go, out NetBody nb) && nb.body != null) {
            victim = nb.body;
            if (nb.IsPiggybacking()) nb.StopPiggyback();
            if (victim.currentClimbable != null) victim.StopClimbing();
            if (victim.standing) victim.Ragdoll();
            victim.shock = 100f;
            victim.moveDir = Vector2.zero;
            heldLimb = Grab.HeldLimb(go, victim);
            if (heldLimb == null || heldLimb.rb == null) {
                SetHeld(null);
                return false;
            }
            heldTransform = heldLimb.transform;
            rb = heldLimb.rb;
        } else if (Grab.TryAnchor(go, out heldTransform)) {
            brace = true;
        } else if (!Grab.TryPull(go, out rb)) {
            SetHeld(null);
            return false;
        } else {
            heldTransform = rb.transform;
        }

        if (!brace) {
            rb.simulated = true;
            if (rb.bodyType != RigidbodyType2D.Dynamic) {
                rb.bodyType = RigidbodyType2D.Dynamic;
            }
            rb.WakeUp();
        }
        CreatureHold.Sync(brace ? null : Grab.Creature(rb));
        if (!brace && rb != null && CreatureHold.Held == null) {
            Loose.Arm(rb.gameObject, rb);
        }
        SetHeld(victim);

        Link.Bind(heldTransform, cursor);
        Vector2 anchor = Link.Anchor(heldTransform);

        Vector2 toCursor = cursor - (brace ? anchor : rb.position);
        float dist = toCursor.magnitude;
        Vector2 dir = dist > 0.05f ? toCursor / dist : Vector2.zero;
        float weight = DragMath.BodyMass(Grab.Host());

        SpiderHandler spider = CreatureHold.Held;
        bool coasting = rb != null && rb.velocity.sqrMagnitude > 0.04f;
        if (!brace && (dir.sqrMagnitude > 0f || coasting) && spider != null) {
            Vector2 useAccel = DragMath.PullAccel(dir, dist, rb.mass, weight, rb.velocity, AccelPerMeter, MaxLead, SettleAccel);
            rb.AddForce(useAccel * rb.mass);
            rb.angularVelocity *= 0.96f;
            if (spider.stunTime < 0.35f) spider.stunTime = 0.35f;
        } else if (!brace && (dir.sqrMagnitude > 0f || coasting)) {
            float targetMass = Mathf.Clamp(victim != null ? DragMath.BodyMass(victim) : rb.mass, 0.2f, Grab.MedcrateMass);
            Vector2 useAccel = DragMath.PullAccel(dir, dist, targetMass, weight, rb.velocity, AccelPerMeter, MaxLead, SettleAccel);
            if (victim != null) {
                DragMath.PushBody(victim, useAccel, SwingShare, heldLimb);
            } else {
                rb.AddForceAtPosition(useAccel * rb.mass, anchor);
                rb.angularVelocity *= 0.96f;
            }
        }
        return false;
    }

    static void SetHeld(Body victim) {
        PlayerCrush.Sync(victim);
        if (HeldVictim == victim) {
            if (victim != null) JointMotors.Relax(victim);
            return;
        }
        JointMotors.Restore();
        HeldVictim = victim;
        if (victim != null) JointMotors.Relax(victim);
    }
}

static class DragMath {
    internal static Vector2 PullAccel(Vector2 dir, float dist, float mass, float weight, Vector2 velocity, float accelPerMeter, float maxLead, float settleAccel) {
        float useMass = Mathf.Clamp(mass, 0.2f, Grab.MedcrateMass);
        float useDist = dist > maxLead ? maxLead : dist;
        float scale = weight / useMass;
        Vector2 pull = dir * (useDist * accelPerMeter * scale);
        float speed = velocity.magnitude;
        if (speed < 0.05f) return pull;
        return pull - velocity * (settleAccel * scale / speed);
    }

    internal static float BodyMass(Body body) {
        if (body == null || body.limbs == null) return 1f;
        float sum = 0f;
        Limb[] limbs = body.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb != null && limb.rb != null) sum += limb.rb.mass;
        }
        return sum > 0.2f ? sum : 1f;
    }

    internal static void PushBody(Body body, Vector2 accel, float swingShare, Limb lead = null) {
        Limb[] limbs = body.limbs;
        float leadMass = 0f;
        float otherMass = 0f;
        bool swing = lead != null && lead.rb != null && lead.rb.simulated && accel.sqrMagnitude > 1f;
        if (swing) {
            leadMass = lead.rb.mass;
            for (int i = 0; i < limbs.Length; i++) {
                Limb limb = limbs[i];
                if (limb == null || limb == lead || limb.rb == null || !limb.rb.simulated) continue;
                otherMass += limb.rb.mass;
            }
            if (leadMass < 0.01f || otherMass < 0.01f) swing = false;
        }
        Vector2 extra = swing ? accel * swingShare : Vector2.zero;
        float pay = swing ? leadMass / otherMass : 0f;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null || !limb.rb.simulated) continue;
            Vector2 a = accel;
            if (swing) a += limb == lead ? extra : -extra * pay;
            limb.rb.AddForce(a * limb.rb.mass, ForceMode2D.Force);
        }
    }
}

[HarmonyPatch(typeof(Body), "FixedUpdate")]
static class HeldRagdollPatch {
    static bool Prefix(Body __instance) => DragPatch.HeldVictim != __instance;
}

static class JointMotors {
    static readonly Dictionary<HingeJoint2D, bool> motors = new Dictionary<HingeJoint2D, bool>();

    internal static void Relax(Body body) {
        Limb[] limbs = body.limbs;
        if (limbs == null) return;
        for (int i = 0; i < limbs.Length; i++) {
            Limb limb = limbs[i];
            if (limb == null || limb.rb == null) continue;
            if (limb.rb.gravityScale < 1f) limb.rb.gravityScale = 1f;
            HingeJoint2D joint = limb.joint;
            if (joint == null) continue;
            if (!motors.ContainsKey(joint)) motors.Add(joint, joint.useMotor);
            joint.useMotor = false;
        }
    }

    internal static void Restore() {
        foreach (KeyValuePair<HingeJoint2D, bool> pair in motors) {
            if (pair.Key != null) pair.Key.useMotor = pair.Value;
        }
        motors.Clear();
    }
}

static class Link {
    static GameObject held;
    static Vector2 localPoint;

    internal static void Bind(Transform limb, Vector2 cursor) {
        if (held != limb.gameObject) {
            held = limb.gameObject;
            localPoint = limb.InverseTransformPoint(cursor);
        }
    }

    internal static Vector2 Anchor(Transform limb) => limb.TransformPoint(localPoint);

    internal static void Clear() {
        held = null;
        Beam.Hide();
    }
}

static class Beam {
    const int Points = 7;
    static LineRenderer core;
    static LineRenderer haze;

    internal static void Show(Vector2 from, Vector2 to) {
        Ensure();
        core.enabled = true;
        haze.enabled = true;
        Write(core, from, to);
        Write(haze, from, to);
    }

    internal static void Hide() {
        if (core != null) core.enabled = false;
        if (haze != null) haze.enabled = false;
    }

    static void Write(LineRenderer line, Vector2 from, Vector2 to) {
        Vector2 dir = to - from;
        float len = dir.magnitude;
        Vector2 perp = len > 0.001f ? new Vector2(-dir.y, dir.x) / len : Vector2.right;
        for (int i = 0; i < Points; i++) {
            float t = i / (float)(Points - 1);
            float amp = (i != 0 && i != Points - 1) ? (Mathf.PerlinNoise(t * 4f, Time.time * 24f) - 0.5f) * 0.07f : 0f;
            line.SetPosition(i, (Vector3)(Vector2.Lerp(from, to, t) + perp * amp));
        }
    }

    static void Ensure() {
        if (core != null) return;
        var root = new GameObject("TelekinesisBeam");
        Object.DontDestroyOnLoad(root);
        core = Make(root, 0.027f, new Color(0.5425f, 0.8798f, 1f, 0.95f), 40);
        var soft = new GameObject("TelekinesisBeamHaze");
        soft.transform.SetParent(root.transform, false);
        haze = Make(soft, 0.09f, new Color(0.5425f, 0.8798f, 1f, 0.28f), 39);
    }

    static LineRenderer Make(GameObject go, float width, Color color, int order) {
        LineRenderer line = go.AddComponent<LineRenderer>();
        line.useWorldSpace = true;
        line.positionCount = Points;
        line.startWidth = width;
        line.endWidth = width;
        line.numCapVertices = 0;
        line.numCornerVertices = 0;
        line.sortingOrder = order;
        line.startColor = color;
        line.endColor = color;
        line.enabled = false;
        Shader shader = Shader.Find("Sprites/Default") ?? Shader.Find("Unlit/Color");
        if (shader != null) {
            line.material = new Material(shader) { color = Color.white };
        }
        return line;
    }
}

[HarmonyPatch(typeof(SyncBody), "ApplyDataToBody")]
static class KeepPhysicsPatch {
    static bool Prefix(SyncBody __instance) {
        if (!Multiplayer.IsNetworkActiveAndIsServer() || __instance == null || __instance.nb == null) return true;
        if (!Con._DEV_TELEKINESIS) return true;
        return !(Grab.TryRemote(Con._DEV_TELEKINESIS_OBJECT, out NetBody nb) && nb == __instance.nb);
    }
}

[HarmonyPatch(typeof(Con), "LateUpdate")]
static class RemindPatch {
    static void Postfix() {
        if (!Con._DEV_TELEKINESIS || !Multiplayer.IsNetworkActiveAndIsServer()) {
            Beam.Hide();
            return;
        }
        GameObject go = Con._DEV_TELEKINESIS_OBJECT;
        if (go == null) {
            Beam.Hide();
            return;
        }
        Transform heldTransform = null;
        if (Grab.TryRemote(go, out NetBody nb) && nb.body != null) {
            nb.Server_RemindPlayersCurrentState(false);
            Limb held = Grab.HeldLimb(go, nb.body);
            if (held != null) heldTransform = held.transform;
        } else if (Grab.TryAnchor(go, out heldTransform)) {
        } else if (Grab.TryPull(go, out Rigidbody2D rb)) {
            heldTransform = rb.transform;
        }

        Body host = Grab.Host();
        if (heldTransform == null || host == null) {
            Beam.Hide();
            return;
        }
        Link.Bind(heldTransform, Util.GetCursorWorldPos());
        Vector2 anchor = Link.Anchor(heldTransform);
        Vector2 from = host.rb != null && host.standing ? host.rb.position : (Vector2)host.transform.position;
        if (!host.standing) {
            Limb torso = host.GetUpperTorso();
            if (torso != null && torso.rb != null) from = torso.rb.position;
        }
        Beam.Show(from, anchor);
    }
}

[HarmonyPatch(typeof(ScavPlayer), nameof(ScavPlayer.Server_HasForcedServerOwnershipOfBody))]
static class HoldOwnershipPatch {
    static void Postfix(ScavPlayer __instance, ref bool __result) {
        if (__result || __instance == null || __instance.netBody == null) return;
        if (!Con._DEV_TELEKINESIS) return;
        if (Grab.TryRemote(Con._DEV_TELEKINESIS_OBJECT, out NetBody nb) && nb == __instance.netBody) {
            __result = true;
        }
    }
}
