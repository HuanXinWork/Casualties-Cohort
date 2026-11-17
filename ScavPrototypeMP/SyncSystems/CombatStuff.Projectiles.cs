using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 炮塔与枪械共享弹道射线命中过滤、散弹断肢及弹道伤害结算业务类（独立于 <see cref="HarmonyPatch"/> 类以根除 <c>Harmony003</c>）。
/// </summary>
internal static class ProjectileDamageSync {
    internal static bool HitOnlyStatic;
    internal static bool SuppressBodyDamage;
    internal static bool LastActiveShooterIsSuicide;
    internal static NetBody LastActiveShooter;
    internal static TrackerGunScript LastActiveShooterGun;
    internal static FireInfo LastFireInfo;

    internal static readonly List<SyncInfo> JustHitBuildings = new();
    internal static readonly List<Limb> JustHitLimbs = new();

    public static bool Shoot_DidHitABuilding(BuildingEntity building) {
        if (!Net.IsRunning) {
            return false;
        }
        if (SuppressBodyDamage) {
            return true;
        }

        if (ScavMPBuildingSynchronizer.BuildingHasActivePhysics(building)) {
            if (NetObjectRegistry.TryGetSyncInfo(building.gameObject, out var si)) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(building.transform.position, "Shoot: Noted hit building", Color.green);
                }
                JustHitBuildings.Add(si);
            }

            if (LastActiveShooter != null || HitOnlyStatic) {
                return true;
            }
        }
        return false;
    }

    /// <summary>由 <see cref="TurretScript_Shoot_MultiplayerPatch"/> Transpiler 在弹道循环内调用。</summary>
    public static bool Shoot_DidHitABody(RaycastHit2D hit2D) {
        if (!Net.IsRunning) {
            return false;
        }
        if (SuppressBodyDamage) {
            return true;
        }

        var collider = hit2D.collider;
        if (collider == null) {
            return true;
        }
        var limb = collider.GetComponent<Limb>();
        var body = collider.GetComponent<Body>();
        if (limb == null) {
            if (body == null) {
                return true;
            }
        } else {
            body = limb.body;
        }
        limb = body.GetClosestLimb(hit2D.point);

        if (LastActiveShooter == null) {
            if (LastFireInfo.ignoreTrans != null && LastFireInfo.ignoreTrans.TryGetComponent(out TurretScript turret) && body.TryGetNetBody(out var nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(turret.build, nb, 10));
            }

#if FULLGAME
            if (LastFireInfo.ignoreTrans != null && limb != null
                && (LastFireInfo.ignoreTrans.TryGetComponent<CeilingTurret>(out _)
                    || LastFireInfo.ignoreTrans.TryGetComponent<GuardScript>(out _))) {
                JustHitLimbs.Add(limb);
                return false;
            }
#endif

            if (Net.IsRunning && body != null && !body.IsBodyLocal()) {
                return true;
            }
            if (Net.IsRunning) {
                TrapClaim.NoteTurret(body, limb);
            }
            return false;
        }

        bool actuallyCommittingSuicide = false;
        bool doDamage = false;

        if (limb.TryGetNetBody(out var bodyn)) {
            actuallyCommittingSuicide = LastActiveShooterIsSuicide && bodyn == LastActiveShooter;
            if (actuallyCommittingSuicide) {
                doDamage = true;
            } else if (LastActiveShooter.CanAttackThisGuy(limb.body)) {
                doDamage = true;
            }
            if (HitOnlyStatic) {
                doDamage = false;
            }

            if (doDamage) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                    DebugHelp.OnNetEvent(bodyn.GetHeadPos(), "Shoot: Noted hit body", Color.green);
                }
                JustHitLimbs.Add(limb);
            }
        }

        if (actuallyCommittingSuicide) {
            return false;
        }
        if ((bodyn == null || !bodyn.IsPlayer) && Multiplayer.rules.PVP) {
            return false;
        }
        return true;
    }

    public static void ApplyShootDamages(TrackerGunScript gst, IEnumerable<Limb> hitLimbs, IEnumerable<SyncInfo> hitBuildings) {
        bool attackerIsLocal = Util.IsBodyLocal(gst.body);
        var hitLimbsDuplicateCount = hitLimbs
            .GroupBy(x => x, ReferenceEqualityComparer.Instance)
            .ToDictionary(a => (Limb)a.Key, b => b.Count());

        Dictionary<NetBody, float> plrDamages = new();
        foreach (var limb in hitLimbs) {
            float dmg = Stolen_ShootApplyDamageLimb(limb, gst, true);
            if (limb.TryGetNetBody(out var nb)) {
                plrDamages[nb] = plrDamages.GetValueOrDefault(nb, 0f) + dmg;
            }
        }

        List<Body> amputatedOnPlrs = new();
        foreach (var limbCount in hitLimbsDuplicateCount) {
            var limb = limbCount.Key;
            if (amputatedOnPlrs.Contains(limb.body) || (!Multiplayer.rules.PVPCombatDismember && limb.body.alive)) {
                continue;
            }
            if (limbCount.Value >= 3 && KM.DistanceCheck(gst.transform.position, limb.transform.position, 16f)) {
                CombatStuff.BetterDismember(limb, !limb.isHead);
                if (limb.dismembered) {
                    amputatedOnPlrs.Add(limb.body);
                }
            }
        }

        foreach (var dmg in plrDamages) {
            Vector2 p = dmg.Key.GetHeadPos() + Vector2.up;
            WorldGeneration.CreateDamageNumber(p, (int)dmg.Value);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(p, $"Shoot: Damaged body {dmg.Key}", Color.red);
            }
            dmg.Key.body.DoGoreSound();

            if (!dmg.Key.body.conscious && dmg.Key.body.isDying) {
                gst.body.happiness -= Multiplayer.rules.PVPMoodDebuff * 2f;
                if (!gst.body.specialCrying && Multiplayer.rules.PVPMoodDebuff > 0) {
                    gst.body.StartCoroutine("Cry");
                }
            }

            dmg.Key.OnDamageEvent(new BodyDamageEvent(gst.pbody, dmg.Key, gst.item, dmg.Value));

            if (Net.IsServer) {
                MedicalSync.Server_QueueSendCharacterHealth(dmg.Key, true);
            } else if (attackerIsLocal) {
                dmg.Key.SetNetHealthSyncIgnoreTime();
            }
        }

        foreach (var building in hitBuildings) {
            float dmg = Stolen_ShootApplyDamageBuilding(building.building, gst);
            if (!Net.IsServer && attackerIsLocal) {
                building.SetIgnoreTimeForRoundTrip();
            }

            if (building.building.animal) {
                Sound.Play(VanillaAssetKeys.SoundGore + Random.Range(1, 6), building.transformPosition);
            }
            Vector2 p = building.transformPosition + Vector2.up * 2f;
            WorldGeneration.CreateDamageNumber(p, (int)dmg);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(p, "Shoot: Damaged building", Color.red);
            }
        }
    }

    public static float Stolen_ShootApplyDamageBuilding(BuildingEntity b, TrackerGunScript gst) {
        float num = Random.Range(0.85f, 1.15f);
        float damage = b.animal ? (gst.og.animalDamage * num) : (gst.og.structureDamage * num);
        b.SetHealth(b.GetHealth() - damage);
        if (b.animal) {
            b.gameObject.SendMessage("AnimalHit", damage);
        }
        return damage;
    }

    public static float Stolen_ShootApplyDamageLimb(Limb limb, TrackerGunScript gst, bool usePvpDamageModel) {
        limb.body.adrenaline = 100f;
        float armor = limb.GetArmorReduction();
        float ruleMult = Multiplayer.rules.PVPDamageMultiplier;
        float damage = 0f;

        if (usePvpDamageModel) {
            armor /= (ruleMult == 0f ? 0.00001f : ruleMult);
            if (ruleMult != 0f) {
                limb.pain += Random.Range(50f, 70f) / armor;
                damage = gst.og.animalDamage / armor * 0.5f;
                limb.skinHealth -= damage;
                limb.muscleHealth -= damage;
                limb.bleedAmount += Random.Range(30f, 40f) / armor;
                limb.shrapnel = Math.Max(limb.shrapnel, Random.Range(0, (int)Mathf.Clamp(5f * ruleMult, 1f, 5f)));
                limb.DamageWearables(0.8f * ruleMult);
            }
        } else {
            ruleMult = 1f;
            limb.pain += Random.Range(80f, 100f) / armor;
            damage = Random.Range(70f, 100f) / armor;
            limb.skinHealth -= damage;
            limb.muscleHealth -= Random.Range(70f, 100f) / armor;
            limb.bleedAmount += Random.Range(30f, 40f) / armor;
            limb.shrapnel = 5;
            limb.DamageWearables(1f);
        }

        if (limb.isVital || limb.isHead) {
            limb.body.internalBleeding += Random.Range(18f, 22f) / armor;
        }
        if (Random.Range(0f, 1f) < 0.3f * ruleMult) {
            limb.BreakBone();
        }
        if (limb.isHead) {
            if (usePvpDamageModel) {
                limb.pain += Random.Range(10f, 50f) / armor;
                limb.body.consciousness -= 35f / armor;
                if (limb.body.brainHealth > 20f) {
                    limb.body.brainHealth = Math.Max(4f, limb.body.brainHealth - Random.Range(10f, 70f) / armor);
                } else {
                    limb.body.brainHealth -= Random.Range(5f, Math.Max(limb.body.brainHealth - 1f, 9f)) / armor;
                }

                if (Multiplayer.rules.PVPCombatDismember && Random.value * ruleMult > 0.2f && !limb.body.GetHead().dismembered) {
                    if (Random.value > 0.5f) {
                        limb.body.Disfigure();
                    } else {
                        limb.body.RemoveEye();
                    }
                }
            } else {
                limb.body.consciousness = 0f;
                limb.body.brainHealth -= Random.Range(10f, 85f) / armor;
                if (Random.value > 0.5f) {
                    limb.body.Disfigure();
                } else {
                    limb.body.RemoveEye();
                }
            }
        }

        if ((Multiplayer.rules.PVPCombatDismember || !limb.body.alive) && usePvpDamageModel) {
            float muscleThreshold = 1f;
            float skinThreshold = 3f;
            if (limb.body.IsDeadOrCriticallyDying()) {
                muscleThreshold = 2f;
                skinThreshold = gst.IsShotgun() ? 52f : 5f;
            }
            if (limb.muscleHealth <= muscleThreshold && limb.skinHealth <= skinThreshold && Net.IsServer) {
                if (Random.value < 0.2f * muscleThreshold * ruleMult) {
                    var head = limb.body.limbs[(int)LimbIndex.Head];
                    if (Util.GetLimbIndex(limb) <= (int)LimbIndex.LowerTorso && !head.dismembered) {
                        CombatStuff.BetterDismember(head, true);
                    } else {
                        CombatStuff.BetterDismember(limb, true);
                    }
                }
            }
        }

        return damage;
    }
}

[HarmonyPatch(typeof(TurretScript), nameof(TurretScript.Shoot))]
public static class TurretScript_Shoot_MultiplayerPatch {
    public static bool HitOnlyStatic {
        get => ProjectileDamageSync.HitOnlyStatic;
        set => ProjectileDamageSync.HitOnlyStatic = value;
    }

    public static bool SuppressBodyDamage {
        get => ProjectileDamageSync.SuppressBodyDamage;
        set => ProjectileDamageSync.SuppressBodyDamage = value;
    }

    public static bool LastActiveShooterIsSuicide {
        get => ProjectileDamageSync.LastActiveShooterIsSuicide;
        set => ProjectileDamageSync.LastActiveShooterIsSuicide = value;
    }

    public static NetBody LastActiveShooter {
        get => ProjectileDamageSync.LastActiveShooter;
        set => ProjectileDamageSync.LastActiveShooter = value;
    }

    public static TrackerGunScript LastActiveShooterGun {
        get => ProjectileDamageSync.LastActiveShooterGun;
        set => ProjectileDamageSync.LastActiveShooterGun = value;
    }

    public static FireInfo last_fire_info {
        get => ProjectileDamageSync.LastFireInfo;
        set => ProjectileDamageSync.LastFireInfo = value;
    }

    public static List<SyncInfo> JustHitBuildings => ProjectileDamageSync.JustHitBuildings;
    public static List<Limb> JustHitLimbs => ProjectileDamageSync.JustHitLimbs;

    public static void ApplyShootDamages(TrackerGunScript gst, IEnumerable<Limb> hitLimbs, IEnumerable<SyncInfo> hitBuildings)
        => ProjectileDamageSync.ApplyShootDamages(gst, hitLimbs, hitBuildings);

    public static float Stolen_ShootApplyDamageBuilding(BuildingEntity b, TrackerGunScript gst)
        => ProjectileDamageSync.Stolen_ShootApplyDamageBuilding(b, gst);

    public static float Stolen_ShootApplyDamageLimb(Limb limb, TrackerGunScript gst, bool use_pvp_damage_model)
        => ProjectileDamageSync.Stolen_ShootApplyDamageLimb(limb, gst, use_pvp_damage_model);

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);
        int i = 100;

        while (!(
            codes[i - 3].opcode == OpCodes.Ldloc_S &&
            codes[i - 2].opcode == OpCodes.Ldfld &&
            codes[i - 1].opcode == OpCodes.Brtrue &&
            codes[i].opcode == OpCodes.Ldloc_S &&
            codes[i - 2].ToString().Contains("cantHit"))) {
            i++;
        }

        int buildingCheckLocation = i;
        i += 60;

        while (!(
            codes[i - 3].opcode == OpCodes.Ldloc_S &&
            codes[i - 2].opcode == OpCodes.Ldfld &&
            codes[i - 1].opcode == OpCodes.Stloc_S &&
            codes[i].opcode == OpCodes.Ldloc_S &&
            codes[i - 2].ToString().Contains("Limb") &&
            codes[i - 2].ToString().Contains("body"))) {
            i++;
        }

        int bodyCheckLocation = i;
        int continueJumpOperandIndex = bodyCheckLocation - 1;
        while (codes[continueJumpOperandIndex].opcode != OpCodes.Brtrue) {
            continueJumpOperandIndex--;
        }

        {
            int bodyOperandIndex = bodyCheckLocation - 1;
            while (!(codes[bodyOperandIndex].operand is LocalBuilder lb && lb.LocalType == typeof(Body))) {
                bodyOperandIndex--;
            }

            int rayHitOperandIndex = bodyCheckLocation - 1;
            while (!(codes[rayHitOperandIndex].operand is LocalBuilder lb && lb.LocalType == typeof(RaycastHit2D))) {
                rayHitOperandIndex--;
            }

            var newInstructions = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProjectileDamageSync), nameof(ProjectileDamageSync.Shoot_DidHitABody))),
                new CodeInstruction(OpCodes.Brtrue, codes[continueJumpOperandIndex].operand),
                new CodeInstruction(OpCodes.Ldloc_S, codes[bodyOperandIndex].operand),
            };

            codes[bodyCheckLocation].operand = codes[rayHitOperandIndex].operand;
            codes.InsertRange(bodyCheckLocation + 1, newInstructions);
        }

        {
            var newInstructions = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(ProjectileDamageSync), nameof(ProjectileDamageSync.Shoot_DidHitABuilding))),
                new CodeInstruction(OpCodes.Brtrue, codes[continueJumpOperandIndex].operand),
                new CodeInstruction(OpCodes.Ldloc_S, codes[buildingCheckLocation].operand),
            };
            codes.InsertRange(buildingCheckLocation + 1, newInstructions);
        }

        ModLog.Debug("TurretScript.Shoot transpiler applied successfully", LogDomain.Combat);
        return codes;
    }

    static void Prefix(ref FireInfo info) {
        TrapClaim.BeginShot();
        JustHitBuildings.Clear();
        JustHitLimbs.Clear();
        ProjectileDamageSync.LastFireInfo = info;

        if (info.doTinnitus) {
            foreach (var nb in NetBody.GetBodiesInRadius(info.pos, GameplayRanges.ServerExchangeToleranceDistance)) {
                if (nb.IsBodyLocal()) {
                    continue;
                }
                nb.body.hearingLoss += Random.Range(10f, 15f);
                nb.body.eyePanicTime = 0.6f;
                nb.body.eyeCloseTime = 4f;
                nb.body.eyeScareTime = 8f;
                nb.body.talker.Talk(Locale.GetCharacter(VanillaAssetKeys.TalkLoud));
            }
        }

        LastActiveShooter?.RollAttackRandom();
    }

    static void Postfix() {
#if FULLGAME
        if (Net.IsServer && ProjectileDamageSync.LastFireInfo.ignoreTrans != null
            && (ProjectileDamageSync.LastFireInfo.ignoreTrans.TryGetComponent<CeilingTurret>(out _)
                || ProjectileDamageSync.LastFireInfo.ignoreTrans.TryGetComponent<GuardScript>(out _))) {
            for (int i = 0; i < JustHitLimbs.Count; i++) {
                Limb limb = JustHitLimbs[i];
                if (limb != null && limb.body != null && limb.body.TryGetNetBody(out NetBody hurt)) {
                    MedicalSync.Server_QueueSendCharacterHealth(hurt, true);
                }
            }
        }
#endif
        TrapClaim.EndShot();
        LastActiveShooterIsSuicide = false;
        LastActiveShooter = null;
        LastActiveShooterGun = null;
    }
}
