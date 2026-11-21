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

namespace Together;

/// <summary>
/// 近战攻击判定、PVP 肢体伤害、建筑/方块破坏权威结算与击退网络同步。
/// 业务逻辑与接收器独立于 <see cref="HarmonyPatch"/> 类，彻底消除 <c>Harmony003</c> 非引用值类型参数警告。
/// </summary>
internal static class MeleeCombatSync {
    private const float AttackGapLimit = 0.08f;
    private const int CorpseHitCharType = 2;

    private static readonly Dictionary<knetid, double> LastAttackAt = new();

    public static void Forget(knetid client) {
        LastAttackAt.Remove(client);
    }

    internal static Body LastAttacker;
    internal static AttackInfo LastAttackInfo = new();
    internal static Limb LastHitLimb;
    internal static BuildingEntity LastHitBuilding;
    internal static Vector2 LastHitPos;
    internal static bool LastHitWasBlock;
    internal static bool LastAttackResult;
    internal static Body HostRerunAttacker;

    public static void DoToolSpecificPVPDamage(Item usedTool, Limb target, float ruleMult, float strengthVsArmor) {
        if (usedTool == null || target == null) {
            return;
        }
        if (usedTool.id == "sledgehammer") {
            if (UnityEngine.Random.value < 0.5f * ruleMult) {
                if (UnityEngine.Random.value < 0.3f * ruleMult) {
                    target.BreakBone();
                } else {
                    target.Dislocate();
                }
            }

            if (target.isHead) {
                target.body.brainHealth -= 10f * strengthVsArmor;
                target.body.consciousness -= 50f * strengthVsArmor;
            }
            if (UnityEngine.Random.value < 0.7f * ruleMult && target == target.body.GetUpperTorso()) {
                target.body.respiratoryRate = Mathf.Max(0f, target.body.respiratoryRate - 50f);
            }

            target.body.eyePanicTime += 0.5f;
            target.body.eyeScareTime += 1.0f;
        }
    }

    public static float DoPVPMeleeDamage(Body attacker, Limb target, AttackInfo atk, float armstrength, Item usedTool, out Vector2 knockback) {
        Vector2 attackDir = KM.NormalTo(attacker.limbs[(int)LimbIndex.UpperTorso].transform.position, attacker.targetLookPos);
        knockback = new Vector2(attackDir.x, 0.4f) * (atk.knockBack * 0.04f);

        attacker.attackCooldown *= 3.5f * atk.attackCooldownMult;
        if (Util.IsBodyLocal(attacker)) {
            PlayerCamera.main.lastAttackCool = attacker.attackCooldown;
        }

        float ogHp = target.skinHealth + target.muscleHealth;
        float ruleMult = Multiplayer.rules.PVPDamageMultiplier;
        float strengthVsArmor = (armstrength / target.GetArmorReduction()) * ruleMult;

        if (!target.body.conscious) {
            strengthVsArmor *= 2f;
            knockback *= 0.8f;
            if (!target.body.alive) {
                knockback *= 0.8f;
                strengthVsArmor = armstrength * 2.0f;
                if (atk.unarmed) {
                    knockback *= 0.1f;
                }
            }
        }

        target.body.traumaAmount += 0.2f * strengthVsArmor;
        attacker.traumaAmount += Multiplayer.rules.PVPMoodDebuff;
        if (attacker.happiness > -5f) {
            attacker.happiness -= Multiplayer.rules.PVPMoodDebuff;
        }

        target.skinHealth -= atk.damage * 0.3f * strengthVsArmor;
        target.muscleHealth -= atk.damage * 0.17f * strengthVsArmor;
        target.bleedAmount += atk.damage * 0.02f * strengthVsArmor;
        target.body.adrenaline += 35f;
        target.pain = Math.Max(target.pain + 5f * strengthVsArmor, atk.damage * strengthVsArmor);
        target.DamageWearables(atk.damage * 0.009f * strengthVsArmor);
        target.body.DoGoreSound();
        target.body.eyeScareTime += 0.5f;

        if (usedTool == null) {
            if (attacker.clawHealth > 18f) {
                target.skinHealth -= atk.damage * 0.17f * strengthVsArmor;
                target.bleedAmount += atk.damage * 0.07f * strengthVsArmor;
                target.DamageWearables(atk.damage * 0.005f * strengthVsArmor);
                target.body.eyeScareTime += 1.0f;
            }
        } else {
            if (armstrength > 0.5f) {
                DoToolSpecificPVPDamage(usedTool, target, ruleMult, strengthVsArmor);
            }

            var cuttingQuality = usedTool.Stats.qualities?.FirstOrDefault(q => q.id == "cutting");
            if (cuttingQuality != null) {
                float cutMult = Math.Max(20f, cuttingQuality.amount) / 100f;
                target.skinHealth -= atk.damage * 0.6f * strengthVsArmor * cutMult;
                target.muscleHealth -= atk.damage * 0.02f * strengthVsArmor * cutMult;
                target.bleedAmount += atk.damage * 0.5f * strengthVsArmor * cutMult;
                target.body.adrenaline += 10f;
                target.pain += 10f * ruleMult;
                target.DamageWearables(atk.damage * 0.03f * strengthVsArmor * cutMult);
                target.body.eyePanicTime += 2.0f;
                target.body.eyeScareTime += 6.0f;
            }
        }

        if (armstrength > 0.5f && Multiplayer.rules.PVPCombatDismember && target.isHead && target.muscleHealth < 30f && target.skinHealth < 40f) {
            if (UnityEngine.Random.value * (target.muscleHealth + target.skinHealth) / 100f < 0.1f) {
                if (UnityEngine.Random.value < 0.7f) {
                    target.body.RemoveEye();
                } else {
                    target.body.Disfigure();
                }
            }
        }

        if (target.muscleHealth <= 5f && target.skinHealth <= 5f && Net.IsServer) {
            if ((Multiplayer.rules.PVPCombatDismember || !target.body.alive)
                && (target.body.IsDeadOrCriticallyDying() || UnityEngine.Random.value < 0.6f)) {
                CombatStuff.BetterDismember(target);
            }
        }

        if (!target.body.conscious && target.body.isDying) {
            if (target.body.alive) {
                attacker.happiness -= Multiplayer.rules.PVPMoodDebuff;
            }
            if (!attacker.specialCrying && Multiplayer.rules.PVPMoodDebuff > 0) {
                attacker.StartCoroutine("Cry");
            }
        }

        float totalDamage = ogHp - (target.skinHealth + target.muscleHealth);

        if (Net.IsServer) {
            if (target.isVital && target.muscleHealth < 40f && target.skinHealth < 30f) {
                float hpScale = Mathf.Clamp01(1f - (target.muscleHealth + target.skinHealth) / 70f) * 2.0f + 1.0f;
                target.body.internalBleeding += totalDamage * 0.03f * armstrength * hpScale;
            }
            if (target.isHead && target.muscleHealth < 20f && target.skinHealth < 20f) {
                float hpScale = Mathf.Clamp01(1f - (target.muscleHealth + target.skinHealth) / 40f) * 2.0f + 1.0f;
                target.body.brainHealth -= totalDamage * 0.04f * armstrength * hpScale;
            }

            float resilienceExp = Mathf.Abs(totalDamage / 50f);
            target.body.skills.AddExp(1, resilienceExp);
        }

        if (target.TryGetNetBody(out var nb) && attacker.TryGetNetBody(out var attackerNb)) {
            nb.OnDamageEvent(new BodyDamageEvent(attackerNb, nb, usedTool, totalDamage));
        }

        return totalDamage;
    }

    public static float DamageBuilding(Body attacker, AttackInfo atk, BuildingEntity be, float armstrength, bool doSound, Vector2 hitPos) {
        if (be.TryGetComponent(out Rigidbody2D rb2d) && !attacker.IsBodyLocal()) {
            Vector2 swingDirection = (attacker.targetLookPos - attacker.limbs[(int)LimbIndex.UpperTorso].transform.position).normalized;
            rb2d.AddForceAtPosition(swingDirection * armstrength * atk.knockBack, hitPos, ForceMode2D.Impulse);
        }

        float buildingDmg = (be.animal ? atk.damage : atk.structuralDamage) * armstrength * ((atk.metalMoreDamage && be.metallic) ? 10f : 1f);
        be.SetHealth(be.GetHealth() - buildingDmg);

        if (doSound) {
            Sound.Play(be.hitSound, be.transform.position);
        }

        if (be.animal) {
            be.gameObject.SendMessage("AnimalHit", atk.damage * armstrength);
            attacker.attackCooldown *= 3.5f * atk.attackCooldownMult;
            if (Util.IsBodyLocal(attacker)) {
                PlayerCamera.main.lastAttackCool = attacker.attackCooldown;
            }
        }

        return buildingDmg;
    }

    public static float CalculateAttackPower(in Body attacker, in AttackInfo atk) {
        float totalArmPower = 1f;
        int slot = attacker.handSlot;
        if (atk.physicalSwing) {
            float bonusStrength = Traverse.Create(attacker).Field("bonusStrength").GetValue<float>();
            totalArmPower = attacker.slots[slot].armPowerMult * (1f + bonusStrength * 0.166f);
            if (slot == AnatomyConstants.SecondaryHandSlot) {
                totalArmPower *= 0.75f;
            }
            totalArmPower *= 1f + attacker.skills.STRFrom10 * 0.0334f;
        }
        if (atk.unarmed) {
            totalArmPower *= attacker.clawDamageCurve.Evaluate(attacker.clawHealth);
        }
        return totalArmPower;
    }

    public static void ApplyHitStaminaAndClawEffects(Body attacker, AttackInfo atk, float attackPowerScale, Vector2 atkdir) {
        int slot = attacker.handSlot;
        if (attacker.standing) {
            attacker.rb.AddForce(-atkdir * atk.knockBack * attackPowerScale, ForceMode2D.Impulse);
        } else {
            attacker.limbs[(int)LimbIndex.UpperTorso].rb.AddForce(-atkdir * atk.knockBack * attackPowerScale, ForceMode2D.Impulse);
        }

        if (atk.unarmed) {
            attacker.clawHealth -= 0.3f;
            if (attacker.clawHealth < 20f && UnityEngine.Random.value < 0.1f) {
                var handLimb = attacker.slots[slot].limb;
                handLimb.skinHealth -= 3f;
                handLimb.muscleHealth -= 2f;
                handLimb.pain += 12f;
                handLimb.bleedAmount += UnityEngine.Random.Range(0.35f, 0.85f);
            }
        }
        if (atk.physicalSwing) {
            attacker.dirtyness += atk.cooldown;
        }
    }

    public static bool CantHitForSwing(BuildingEntity building) {
        if (HostRerunAttacker != null && building.TryGetComponent(out CorpseScript corpse) && !corpse.animalCorpse) {
            return HostRerunAttacker.charType != CorpseHitCharType;
        }
        return building.cantHit;
    }

    static bool CheckIfHitBody(in RaycastHit2D hit) {
        var collider = hit.collider;
        if (collider == null) {
            return false;
        }
        var limb = collider.GetComponent<Limb>();
        var body = collider.GetComponent<Body>();
        if (limb == null) {
            if (body == null) {
                return false;
            }
            limb = body.GetClosestLimb(hit.point);
        } else {
            body = limb.body;
        }

        if (Util.IsBodyLocal(body) || LastAttacker == null) {
            return false;
        }

        var me = LastAttacker.GetNetBody();
        if (me != null && me.CanAttackThisGuy(body)) {
            LastHitLimb = limb;
            LastHitPos = hit.point;
            Body_Attack_MultiplayerPatch.breakTheAttackLoop = true;
            return true;
        }
        return false;
    }

    static bool CheckIfHitBuilding(in RaycastHit2D hit) {
        var collider = hit.collider;
        if (collider == null) {
            return false;
        }
        var b = collider.GetComponent<BuildingEntity>();
        if (b == null || b.cantHit) {
            return false;
        }

        bool isLocal = Util.IsBodyLocal(LastAttacker);
        bool isPhysActive = ScavMPBuildingSynchronizer.BuildingHasActivePhysics(b);

        if (isLocal && b.TryGetSyncInfo(out var si)) {
            si.SetIgnoreTimeForRoundTrip();
        }
        if (Body_Attack_MultiplayerPatch.StaticHitValidation) {
            return false;
        }
        if (isPhysActive || isLocal) {
            LastHitBuilding = b;
            LastHitPos = hit.point;
            Body_Attack_MultiplayerPatch.breakTheAttackLoop = true;
            return true;
        }
        return false;
    }

    /// <summary>由 <see cref="Body_Attack_MultiplayerPatch.Transpiler"/> 在射线命中循环内直接调用（按值接收局部 <see cref="RaycastHit2D"/>）。</summary>
    public static bool BodyAttack_DidHitSomething(RaycastHit2D hit) {
        if (!Net.IsRunning) {
            return false;
        }
        if (!Body_Attack_MultiplayerPatch.StaticHitValidation && CheckIfHitBody(in hit)) {
            return true;
        }
        if (CheckIfHitBuilding(in hit)) {
            return true;
        }
        if (hit.collider != null && hit.collider.CompareTag("BlockGround")) {
            LastHitPos = hit.point;
            LastHitWasBlock = true;
            if (Net.IsServer && Util.IsBodyLocal(LastAttacker)) {
                Body_Attack_MultiplayerPatch.breakTheAttackLoop = true;
                return true;
            }
        }
        return false;
    }

    internal static void DoKnockback(Body body, in BodyKnockbackPacket knockback) {
        if (body == null) {
            return;
        }
        if (Util.IsBodyLocal(body)) {
            PlayerCamera.main?.shaker?.Shake(knockback.Velocity.magnitude);
            if (knockback.CloseUI) {
                if (MinigameBase.main != null) {
                    MinigameBase.main.EndMinigame();
                }
                Util.ToggleWoundView(false);
            }
        }

        if (body.standing) {
            body.rb.velocity += knockback.Velocity * 1.6f;
            body.lastTimeStepVelocity = body.rb.velocity;
            return;
        }

        Limb[] array = body.limbs;
        for (int i = 0; i < array.Length; i++) {
            if (array[i]?.rb != null) {
                array[i].rb.velocity += knockback.Velocity;
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBodyAttack)]
    static void ServerReceiver_PlayerBodyAttack(knetid clientId, NetDataReader reader) {
        if (!Util.IsWorldGenerated()
            || !ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)
            || plr?.body == null
            || !plr.body.conscious) {
            return;
        }

        if (!pb.IsLocal) {
            double now = Time.realtimeSinceStartupAsDouble;
            double gap = LastAttackAt.TryGetValue(clientId, out double prev) ? now - prev : AttackGapLimit;
            if (gap < AttackGapLimit) {
                MpCommsTrace.HostRejected(LogDomain.Combat, clientId.id, (byte)NetmsgId.SERVER_PlayerBodyAttack, 0, $"attacking too fast ({gap:0.###}s)");
                return;
            }
            LastAttackAt[clientId] = now;
        }

        reader.Get(out Vector2 targetlookpos);
        reader.Get(out Vector2 limb1pos);
        reader.Get(out float distUnused);
        reader.Get(out bool unarmed);
        reader.Get(out knetid attackitemSyncId);
        reader.Get(out bool didhit);
        reader.Get(out AnyObjectNetId hitobj);
        reader.Get(out Vector2 hitPos);
        reader.Get(out float clientReportedBlockDamage);

        if (!(targetlookpos.IsFinite() && limb1pos.IsFinite() && distUnused.IsFinite() && hitPos.IsFinite())) {
            limb1pos = KM.ClampDistance(limb1pos, pb.transform.position, 5f);
        }

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
            DebugHelp.OnNetEvent(plr.body.transform.position, $"S: PlayerBodyAttack {clientId} didhit:{didhit} unarmed:{unarmed} hitobj:{hitobj}");
        }

        SyncInfo toolSi = null;
        if (!unarmed
            && NetObjectRegistry.TryGetSyncInfo(attackitemSyncId, out var candidateTool)
            && candidateTool.IsItem()
            && CombatStuff.ItemIsUsedForAttacking(candidateTool.item)
            && ItemSync.CheckIfBodyReachThisItem(candidateTool, plr.body, GameplayRanges.ServerExchangeToleranceDistance, true)) {
            toolSi = candidateTool;
        }

        Body_Attack_MultiplayerPatch.ForceOnlyRetrieveAttackInfo = true;
        if (toolSi == null) {
            plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
        } else {
            ItemSync.BetterUseItem(pb.body, toolSi.item);
        }

        var atkinfo = LastAttackInfo.CloneViaSerialization();
        Vector2 atkdir = KM.NormalTo(limb1pos, targetlookpos);
        float attackpower = CalculateAttackPower(pb.body, atkinfo);

        bool didHitConfirmed = false;

        if (hitobj.IsBlockHit()) {
            Body_Attack_MultiplayerPatch.ForceOnlyStaminaUse = true;
            if (hitPos.IsFinite()
                && (KM.DistanceCheck(limb1pos, hitPos, Body_Attack_MultiplayerPatch.BlockHitMaxReach)
                    || KM.DistanceCheck((Vector2)plr.body.transform.position, hitPos, Body_Attack_MultiplayerPatch.BlockHitMaxReach))) {
                Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(hitPos + atkdir * 0.05f);
                if (WorldGeneration.world.GetBlock(blockPos) == 0) {
                    blockPos = WorldGeneration.world.WorldToBlockPos(hitPos);
                }
                if (WorldGeneration.world.GetBlock(blockPos) != 0) {
                    float blockDmg = BlockDamageAuthority.ChooseBlockDamage(atkinfo.structuralDamage * attackpower, clientReportedBlockDamage, clientId);
                    WorldGenerationDamageBlockAnnouncePatch.cur_damaging_netbody = pb;
                    try {
                        WorldGeneration.world.DamageBlock(blockPos, blockDmg, true, atkinfo.metalMoreDamage);
                    } finally {
                        WorldGenerationDamageBlockAnnouncePatch.cur_damaging_netbody = null;
                    }
                    WorldGeneration.CreateDamageNumber(
                        (Vector2)WorldGeneration.world.BlockToWorldPos(blockPos) + Vector2.up * 2f,
                        (int)blockDmg);
                    didHitConfirmed = true;
                }
            }
        } else if (hitobj.IsNothing()) {
            Body_Attack_MultiplayerPatch.StaticHitValidation = true;
        } else if (hitobj.TryGetLimb(out var victimLimb)) {
            Body_Attack_MultiplayerPatch.ForceOnlyStaminaUse = true;
            if (victimLimb.TryGetNetBody(out var victimPb)
                && KM.DistanceCheck(plr.body, victimPb.body, GameplayRanges.MeleeBuildingMaxServerDistance)
                && pb.CanAttackThisGuy(victimPb.body)) {
                float totalDmg = DoPVPMeleeDamage(plr.body, victimLimb, atkinfo, attackpower, toolSi?.item, out var knock);

                if (victimPb.IsPlayer) {
                    var wr = Net.CreateWriter(NetmsgId.CLIENT_Knockback_thing);
                    wr.Put(new BodyKnockbackPacket {
                        Velocity = knock,
                        CloseUI = true,
                    });
                    Net.Server_SendToClients(DeliveryMethod.Unreliable, wr, victimPb.plr.playerId);
                }

                if (!victimPb.IsLocal) {
                    var kb = new BodyKnockbackPacket { Velocity = knock };
                    DoKnockback(victimLimb.body, in kb);
                }

                WorldGeneration.CreateDamageNumber(victimPb.GetHeadPos() + Vector2.up, (int)totalDmg);
                MedicalSync.Server_QueueSendCharacterHealth(victimPb, true);
                didHitConfirmed = true;
            }
        } else if (hitobj.TryGetSyncInfo(out var buildingSi) && buildingSi.IsBuilding()) {
            Body_Attack_MultiplayerPatch.ForceOnlyStaminaUse = true;
            if (KM.DistanceCheck(plr.body, buildingSi, GameplayRanges.MeleeBuildingMaxServerDistance)) {
                if (buildingSi.go.TryGetComponent(out Rigidbody2D rb2d)) {
                    hitPos = rb2d.ClosestPoint(hitPos);
                }
                float totalDmg = DamageBuilding(plr.body, atkinfo, buildingSi.building, attackpower, true, hitPos);
                didHitConfirmed = true;
                WorldGeneration.CreateDamageNumber((Vector2)buildingSi.building.transform.position + Vector2.up * 2f, (int)totalDmg);
            }
        }

        LastAttackResult = true;
        plr.body.limbs[(int)LimbIndex.UpperTorso].transform.position = limb1pos;
        plr.body.targetLookPos = targetlookpos;
        try {
            HostRerunAttacker = plr.body;
            Body_Attack_MultiplayerPatch.ForceHitResult = didHitConfirmed;
            if (toolSi == null) {
                plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
            } else {
                ItemSync.BetterUseItem(pb.body, toolSi.item);
            }
        } finally {
            HostRerunAttacker = null;
            Body_Attack_MultiplayerPatch.ForceHitResult = false;
        }

        if (!LastAttackResult && didHitConfirmed) {
            ApplyHitStaminaAndClawEffects(pb.body, atkinfo, attackpower, atkdir);
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerBodyAttack_Relay);
        writer.Put(clientId);
        writer.Put(toolSi != null);
        writer.Put((knetid)(toolSi == null ? 0 : toolSi.syncId));
        writer.Put(limb1pos);
        writer.Put(targetlookpos);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerBodyAttack_Relay)]
    static void Client_ReceiveAndVisualizePlayerBodyAttack(knetid _, NetDataReader reader) {
        reader.Get(out knetid attacker);
        reader.Get(out bool notUnarmed);
        reader.Get(out knetid toolSyncId);
        reader.Get(out Vector2 limb1pos);
        reader.Get(out Vector2 targetlookpos);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(attacker, out var plr, out var pb) || plr?.body == null) {
            return;
        }
        pb.body.attackCooldown = 0f;

        SyncInfo toolSi = null;
        if (notUnarmed
            && NetObjectRegistry.TryGetSyncInfo(toolSyncId, out var candidate)
            && candidate.go != null
            && candidate.IsItem()
            && CombatStuff.ItemIsUsedForAttacking(candidate.item)) {
            toolSi = candidate;
        }

        plr.body.limbs[(int)LimbIndex.UpperTorso].transform.position = limb1pos;
        plr.body.targetLookPos = targetlookpos;
        if (toolSi == null) {
            plr.body.Attack(CombatStuff.default_unarmed_attackinfo, plr.body.handSlot);
        } else {
            ItemSync.BetterUseItem(pb.body, toolSi.item);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_Knockback_thing, false)]
    static void Client_ReceiveKnockback(knetid _, NetDataReader reader) {
        reader.Get(out BodyKnockbackPacket knockback);
        var locPlr = ScavPlayer.LocalPlayer;
        if (locPlr != null && locPlr.body) {
            DoKnockback(locPlr.body, in knockback);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Attack))]
public static class Body_Attack_CantHitPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        FieldInfo cantHitField = AccessTools.Field(typeof(BuildingEntity), nameof(BuildingEntity.cantHit));
        MethodInfo cantHitForSwing = AccessTools.Method(typeof(MeleeCombatSync), nameof(MeleeCombatSync.CantHitForSwing));
        List<CodeInstruction> codes = new(instructions);
        int replaced = 0;
        foreach (CodeInstruction code in codes) {
            if (code.opcode == OpCodes.Ldfld && Equals(code.operand, cantHitField)) {
                code.opcode = OpCodes.Call;
                code.operand = cantHitForSwing;
                replaced++;
            }
        }
        if (replaced == 0) {
            ModLog.Warning("Body.Attack cantHit transpiler: no cantHit read found, leaving stock IL", LogDomain.Combat);
        } else {
            ModLog.Debug($"Body.Attack cantHit transpiler replaced {replaced} reads", LogDomain.Combat);
        }
        return codes;
    }
}

