#if FULLGAME
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 水晶词条联机门控辅助器与燃烧、EMP、电击、水晶怪（<see cref="CrystalEnemy"/>）战斗同步补丁。
/// </summary>
static class CrystalMpGates {
    internal static bool HostSimOnly(CrystalEffect effect) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer || effect == null || effect.crystal == null) {
            return false;
        }
        return SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(effect.crystal.transform.position);
    }

    internal static bool IsActivated(CrystalEffect effect) {
        if (effect == null) {
            return false;
        }
        var activated = Traverse.Create(effect).Field("activated");
        return activated.FieldExists() && activated.GetValue<bool>();
    }

    internal static void MarkActivated(CrystalEffect effect) {
        if (effect == null) {
            return;
        }
        var activated = Traverse.Create(effect).Field("activated");
        if (activated.FieldExists()) {
            activated.SetValue(true);
        }
    }

    internal static bool SkipOneShotFx(CrystalEffect effect) {
        return IsActivated(effect);
    }
}

internal static class CrystalDelayedFx {
    static readonly HashSet<int> _armBroadcast = new HashSet<int>();

    internal static void OnUnstableArmed(CrystalUnstable unstable, bool playTick) {
        if (unstable == null || unstable.crystal == null) {
            return;
        }
        Traverse t = Traverse.Create(unstable);
        var origPos = t.Field("origPos");
        if (origPos.FieldExists()) {
            Vector2 value = origPos.GetValue<Vector2>();
            if (value.sqrMagnitude < 0.0001f) {
                origPos.SetValue((Vector2)unstable.crystal.transform.position);
            }
        }
        var timerStarted = t.Field("timerStarted");
        if (timerStarted.FieldExists()) {
            timerStarted.SetValue(true);
        }
        if (playTick) {
            Sound.Play("crystaltick", unstable.crystal.transform.position, twoDimensional: true, pitchShift: false, unstable.crystal.transform, 1f, 1f, false, false);
        }
    }

    internal static void ClearArmBroadcasts() {
        _armBroadcast.Clear();
    }

    internal static void HostBroadcastUnstableArm(CrystalUnstable unstable) {
        if (!Net.IsServer || unstable == null || unstable.crystal == null) {
            return;
        }
        var timerStarted = Traverse.Create(unstable).Field("timerStarted");
        if (timerStarted.FieldExists() && timerStarted.GetValue<bool>() && _armBroadcast.Add(unstable.crystal.GetInstanceID())) {
            CrystalAuthority.HostCaptureAndBroadcast(unstable.crystal.gameObject);
        }
    }
}

[HarmonyPatch(typeof(CrystalBurning), nameof(CrystalBurning.Burn))]
internal static class CrystalBurning_Burn_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return TranspilerHelpers.PatchShakers(instructions, generator, __originalMethod);
    }

    static bool Prefix(CrystalBurning __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) {
            return true;
        }
        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CrystalEMP), nameof(CrystalEMP.TryEMP))]
internal static class CrystalEMP_TryEMP_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
        return TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
    }

    static bool Prefix(CrystalEMP __instance) {
        if (!Net.IsRunning || __instance.crystal == null) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance)) {
            return false;
        }
        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        Body trigger = CrystalLocalFx.TriggerBody ?? bgo.body;
        if (trigger == null) {
            return false;
        }
        bgo.body = trigger;
        return true;
    }

    static void Postfix(CrystalEMP __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.crystal == null) {
            return;
        }
        CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
    }
}

[HarmonyPatch(typeof(CrystalElectric), nameof(CrystalElectric.Shock))]
internal static class CrystalElectric_Shock_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
        return TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
    }

    static bool Prefix(CrystalElectric __instance, ref Body body) {
        if (!Net.IsRunning || __instance.crystal == null) {
            return true;
        }
        var bgo = __instance.crystal.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body != null) {
            body = bgo.body;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.body), MethodType.Getter)]
static class CrystalEnemy_get_body_MultiplayerPatch {
    static void Postfix(CrystalEnemy __instance, ref Body __result) {
        if (!Net.IsRunning) {
            return;
        }
        var bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        if (bgo.body == null) {
            bgo.SetNearestBody();
            if (bgo.body == null && PlayerCamera.main != null) {
                bgo.body = PlayerCamera.main.body;
            }
        }
        __result = bgo.body;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Update))]
internal static class CrystalEnemy_Update_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
        return TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
    }

    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            if (PlayerCamera.main != null
                && Util.TryGetLocalBody(out Body local)
                && Vector2.Distance(__instance.transform.position, local.transform.position) < GameplayRanges.SimulationKeepRadius) {
                PlayerCamera.main.currentThreatTheme = 18;
                if (WorldGeneration.world != null
                    && WorldGeneration.world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial) {
                    PlayerCamera.main.currentThreatTheme = 17;
                }
                PlayerCamera.main.threatMusicTime = 2f;
            }
            ClientHum(__instance);
            ClientStreak(__instance);
            return false;
        }

        var bgu = __instance.GetComponent<BodyGetterUpdater>();
        if (bgu == null) {
            bgu = __instance.AddComponent<BodyGetterUpdater>();
            bgu.cooldown = 5f;
            bgu.condition = g => {
                if (g.TryGetComponent(out CrystalEnemy ce) && ce.close && ce.body != null) {
                    List<RaycastHit2D> rh = new();
                    int count = Physics2D.Linecast(
                        ce.body.GetLowerTorso().transform.position,
                        ce.transform.position,
                        new ContactFilter2D { layerMask = PhysicsLayers.GroundMask },
                        rh);
                    if (count > 0) {
                        rh.RemoveAll(x => x.transform == null || Util.TryGetBodyFromObject(x.transform, out _) || ce.transform == x.transform);
                        count = rh.Count;
                    }
                    return count > 0;
                }
                return true;
            };
        }
        return true;
    }

    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning || !Net.IsServer || PlayerCamera.main == null) {
            return;
        }
        if (!Util.TryGetLocalBody(out Body local)) {
            return;
        }
        if (Vector2.Distance(__instance.transform.position, local.transform.position) < GameplayRanges.SimulationKeepRadius) {
            return;
        }
        var bgo = __instance.GetComponent<BodyGetterOverrider>();
        Body target = bgo != null ? bgo.body : null;
        if (target == null || Util.IsBodyLocal(target)) {
            return;
        }
        if (Vector2.Distance(__instance.transform.position, target.transform.position) >= GameplayRanges.SimulationKeepRadius) {
            return;
        }
        if (PlayerCamera.main.currentThreatTheme == 17 || PlayerCamera.main.currentThreatTheme == 18) {
            PlayerCamera.main.currentThreatTheme = 0;
            PlayerCamera.main.threatMusicTime = 0f;
        }
    }

    // 循环音源仅在原版 Update 内调整音高与音量，客户端跳过了 AI Update，在此独立驱动嗡鸣反馈
    static void ClientHum(CrystalEnemy enemy) {
        Traverse fields = Traverse.Create(enemy);
        AudioSource source = fields.Field("source").GetValue<AudioSource>();
        Rigidbody2D rb = fields.Field("rb").GetValue<Rigidbody2D>();
        BuildingEntity build = fields.Field("build").GetValue<BuildingEntity>();
        if (source == null || rb == null) {
            return;
        }
        bool stuck = fields.Field("stuck").GetValue<bool>();
        float timeBeforeAttack = fields.Field("timeBeforeAttack").GetValue<float>();
        float hurt = build != null ? 1f - build.GetHealth() / 450f : 0f;
        float wind = Mathf.Clamp01(-timeBeforeAttack) * (stuck ? 0f : 1f);
        float speed = rb.velocity.magnitude * 0.005f;
        float pitch = 1f + speed + wind + hurt;
        float volume = Mathf.Clamp01(0.3f + speed) + wind;
        source.pitch = Mathf.Lerp(source.pitch, pitch, Time.deltaTime * 8f);
        source.volume = Mathf.Lerp(source.volume, volume, Time.deltaTime * 8f);
        if (!source.isPlaying) {
            source.loop = true;
            source.Play();
        }
    }

    struct Streak {
        public bool stuck;
        public bool aimed;
        public Vector3 from;
        public Vector3 to;
    }

    static readonly Dictionary<int, Streak> streaks = new Dictionary<int, Streak>();

    // 宿主在 Update 中绘制蓄力瞄准线与突进残影，客户端跳过 AI 后在此还原线条轨迹
    static void ClientStreak(CrystalEnemy enemy) {
        Traverse fields = Traverse.Create(enemy);
        LineRenderer rend = fields.Field("rend").GetValue<LineRenderer>();
        SpriteRenderer sprite = fields.Field("sprite").GetValue<SpriteRenderer>();
        if (rend == null || sprite == null) {
            return;
        }
        bool stuck = fields.Field("stuck").GetValue<bool>();
        float timeBeforeAttack = fields.Field("timeBeforeAttack").GetValue<float>();
        int id = enemy.GetInstanceID();
        if (!streaks.TryGetValue(id, out Streak aim)) {
            aim = new Streak();
        }
        if (!stuck) {
            aim.from = enemy.transform.position;
            if (timeBeforeAttack < 0f) {
                Vector3 from = enemy.transform.position;
                RaycastHit2D hit = Physics2D.Raycast(from, enemy.transform.up, 999f, PhysicsLayers.GroundMask);
                Vector3 to = hit.collider != null ? (Vector3)hit.point : from + enemy.transform.up * 999f;
                rend.SetPosition(0, from);
                rend.SetPosition(1, to);
                rend.startColor = Color.Lerp(Color.clear, sprite.color, -timeBeforeAttack * 2f);
                rend.widthMultiplier = timeBeforeAttack * 2f;
                aim.aimed = true;
                aim.to = to;
            } else {
                rend.endColor = Color.clear;
                rend.startColor = Color.clear;
                aim.aimed = false;
            }
        } else {
            if (!aim.stuck) {
                if (!aim.aimed) {
                    Vector3 from = aim.from != Vector3.zero ? aim.from : enemy.transform.position;
                    RaycastHit2D hit = Physics2D.Raycast(from, enemy.transform.up, 999f, PhysicsLayers.GroundMask);
                    aim.from = from;
                    aim.to = hit.collider != null ? (Vector3)hit.point : enemy.transform.position;
                }
                rend.SetPosition(0, aim.from);
                rend.SetPosition(1, aim.to);
                rend.endColor = sprite.color;
                rend.startColor = Color.clear;
            }
            rend.endColor = Color.Lerp(sprite.color, Color.clear, -timeBeforeAttack);
        }
        aim.stuck = stuck;
        streaks[id] = aim;
        if (streaks.Count > 64) {
            streaks.Clear();
            streaks[id] = aim;
        }
    }
}

[HarmonyPatch(typeof(CrystalEnemy), "FixedUpdate")]
static class CrystalEnemy_FixedUpdate_HostSim_MultiplayerPatch {
    static bool Prefix() => !Net.IsRunning || Net.IsServer;
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Lunge))]
static class CrystalEnemy_Lunge_VictimAuth_MultiplayerPatch {
    static bool Prefix(CrystalEnemy __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            return false;
        }

        Traverse tr = Traverse.Create(__instance);
        tr.Field("stuck").SetValue(true);
        tr.Field("didAttackWarning").SetValue(false);
        LineRenderer rend = tr.Field("rend").GetValue<LineRenderer>();
        Rigidbody2D rb = tr.Field("rb").GetValue<Rigidbody2D>();

        if (rend != null) {
            rend.endColor = rend.startColor;
            rend.startColor = Color.clear;
        }

        bool hitSomeone = false;
        knetid victim = default;
        RaycastHit2D[] hits = Physics2D.RaycastAll(__instance.transform.position, __instance.transform.up);
        for (int i = 0; i < hits.Length; i++) {
            RaycastHit2D hit = hits[i];
            if (hit.transform == null) {
                continue;
            }
            if (Utils.GetBody(hit.transform.gameObject, out Body body) && !hitSomeone) {
                hitSomeone = true;
                if (body.TryGetNetBody(out NetBody nb)) {
                    victim = nb.netId;
                }
                if (TrapClaim.ManifestMayHurt(__instance, body)) {
                    TrapClaim.ManifestHurtLocal(__instance, body);
                }
            }
            if (hit.transform.gameObject.layer == PhysicsLayers.GroundLayerIndex) {
                __instance.transform.position = hit.point;
                if (rb != null) {
                    rb.MovePosition(hit.point);
                    rb.velocity = Vector2.zero;
                    rb.constraints = RigidbodyConstraints2D.FreezeAll;
                }
                Sound.Play("crystalenemylodge", __instance.transform.position, twoDimensional: false, pitchShift: false);
                break;
            }
        }

        TrapClaim.HostManifestLunged(__instance, victim);
        return false;
    }
}

[HarmonyPatch(typeof(CrystalEnemy), nameof(CrystalEnemy.Awake))]
static class CrystalEnemy_Awake_MultiplayerPatch {
    static void Postfix(CrystalEnemy __instance) {
        if (!Net.IsRunning) {
            return;
        }
        __instance.GetOrAddComponent<BodyGetterOverrider>().SetNearestBody();
    }
}
#endif
