using System.Collections.Generic;
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
/// 近战挥砍主循环 IL 注入与主客机伤害/方块破坏上报补丁。
/// </summary>
[HarmonyPatch(typeof(Body), nameof(Body.Attack))]
public static class Body_Attack_MultiplayerPatch {
    public const float BlockHitReachReference = 5.2f;
    public const float BlockHitMaxReach = BlockHitReachReference * 2f;

    public static bool breakTheAttackLoop = false;
    public static bool StaticHitValidation = false;
    public static bool ForceOnlyStaminaUse = false;
    public static bool ForceOnlyRetrieveAttackInfo = false;

    public static Body LastAttacker {
        get => MeleeCombatSync.LastAttacker;
        set => MeleeCombatSync.LastAttacker = value;
    }

    public static bool CantHitForSwing(BuildingEntity building) => MeleeCombatSync.CantHitForSwing(building);

    private sealed class AttackSwingState {
        public float lastAttackCool;
        public bool canDoAttack;
        public Vector2 targetLookPos;
        public Vector2 limb1Pos;
        public AttackInfo atk;
        public float atkPower;
    }

    private static int LabelTargetIndex(List<CodeInstruction> codes, Label label) {
        for (int k = 0; k < codes.Count; k++) {
            if (codes[k].labels.Contains(label)) {
                return k;
            }
        }
        return -1;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        List<CodeInstruction> codes = new List<CodeInstruction>(instructions);

        int loopEntry = -1;
        int loopHead = -1;
        int loopBackedge = -1;
        int foundLoops = 0;
        bool loopHasForwardShape = false;

        for (int k = 2; k < codes.Count; k++) {
            if (codes[k].opcode == OpCodes.Br && codes[k].operand is Label && codes[k - 1].opcode == OpCodes.Stloc_S &&
                (codes[k - 2].opcode == OpCodes.Ldc_I4_0 || codes[k - 2].opcode == OpCodes.Ldc_I4_S || codes[k - 2].opcode == OpCodes.Ldc_I4)) {
                int head = LabelTargetIndex(codes, (Label)codes[k].operand);
                if (head < 0) {
                    continue;
                }
                bool forwardShape = head > k;
                if (forwardShape) {
                    head = k + 1;
                }

                bool raycastLoop = false;
                int backedge = -1;
                for (int b = head; b < codes.Count; b++) {
                    if (codes[b].opcode == OpCodes.Stloc_S && codes[b].operand is LocalBuilder hl && hl.LocalType == typeof(RaycastHit2D)) {
                        raycastLoop = true;
                    }
                    if ((codes[b].opcode == OpCodes.Blt || codes[b].opcode == OpCodes.Blt_S || codes[b].opcode == OpCodes.Blt_Un || codes[b].opcode == OpCodes.Blt_Un_S) &&
                        codes[b].operand is Label back && LabelTargetIndex(codes, back) == head) {
                        backedge = b;
                        break;
                    }
                }
                if (!raycastLoop || backedge < 0) {
                    continue;
                }

                foundLoops++;
                loopEntry = k;
                loopHead = head;
                loopBackedge = backedge;
                loopHasForwardShape = forwardShape;
                if (foundLoops >= 2) {
                    break;
                }
            }
        }

        int realHitCheckPos = -1;
        for (int k = loopHead; k > 0 && loopHead >= 0 && k < loopBackedge && realHitCheckPos < 0; k++) {
            if (codes[k].opcode == OpCodes.Stloc_S && codes[k].operand is LocalBuilder hl && hl.LocalType == typeof(RaycastHit2D)) {
                realHitCheckPos = k;
            }
        }
        int rayHitResultPos = -1;
        for (int k = realHitCheckPos + 1; k < loopBackedge && realHitCheckPos >= 0 && rayHitResultPos < 0; k++) {
            if (codes[k].opcode == OpCodes.Ldloca_S && Equals(codes[k].operand, codes[realHitCheckPos].operand)) {
                rayHitResultPos = k;
            }
        }

        int currentHitSkipTarget = -1;
        for (int m = realHitCheckPos + 1; m < loopBackedge && currentHitSkipTarget < 0; m++) {
            if (codes[m].operand is Label l2 && LabelTargetIndex(codes, l2) > m && LabelTargetIndex(codes, l2) <= loopBackedge &&
                (codes[m].opcode == OpCodes.Brfalse || codes[m].opcode == OpCodes.Brfalse_S || codes[m].opcode == OpCodes.Brtrue ||
                 codes[m].opcode == OpCodes.Brtrue_S || codes[m].opcode == OpCodes.Br || codes[m].opcode == OpCodes.Br_S)) {
                currentHitSkipTarget = m;
            }
        }

        int continueJumpPos = -1;
        if (loopHasForwardShape && loopBackedge >= 0 && loopBackedge + 1 < codes.Count) {
            continueJumpPos = loopBackedge + 1;
        }
        for (int k = loopBackedge + 1; k < codes.Count && continueJumpPos < 0; k++) {
            if (codes[k].opcode == OpCodes.Brfalse || codes[k].opcode == OpCodes.Brfalse_S ||
                codes[k].opcode == OpCodes.Brtrue || codes[k].opcode == OpCodes.Brtrue_S ||
                codes[k].opcode == OpCodes.Br || codes[k].opcode == OpCodes.Br_S) {
                continueJumpPos = k;
            }
        }

        if (realHitCheckPos < 0 || rayHitResultPos < 0 || loopHead < 0 || currentHitSkipTarget < 0 || continueJumpPos < 0) {
            ModLog.Warning("Body.Attack transpiler: hit-loop markers not found, leaving stock IL", LogDomain.Combat);
            return codes;
        }

        Label skipHitLabel = (Label)codes[currentHitSkipTarget].operand;
        Label stopLoopLabel;
        if (loopHasForwardShape) {
            stopLoopLabel = il.DefineLabel();
            codes[continueJumpPos].labels.Add(stopLoopLabel);
        } else {
            stopLoopLabel = (Label)codes[continueJumpPos].operand;
        }

        var injected = new List<CodeInstruction>();
        CodeInstruction loadHit = new CodeInstruction(OpCodes.Ldloc_S, codes[realHitCheckPos].operand);
        loadHit.MoveLabelsFrom(codes[rayHitResultPos]);
        injected.Add(loadHit);
        injected.Add(new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(MeleeCombatSync), nameof(MeleeCombatSync.BodyAttack_DidHitSomething))));

        Label endOfPatchLabel = il.DefineLabel();
        injected.Add(new CodeInstruction(OpCodes.Brfalse_S, endOfPatchLabel));
        injected.Add(new CodeInstruction(OpCodes.Ldsfld, AccessTools.Field(typeof(Body_Attack_MultiplayerPatch), nameof(breakTheAttackLoop))));
        injected.Add(new CodeInstruction(OpCodes.Brfalse, skipHitLabel));
        injected.Add(new CodeInstruction(OpCodes.Br, stopLoopLabel));

        codes.InsertRange(rayHitResultPos, injected);
        codes[rayHitResultPos + injected.Count].labels.Add(endOfPatchLabel);

        ModLog.Debug($"Body.Attack transpiler patched at {rayHitResultPos} (loops={foundLoops}, entry={loopEntry})", LogDomain.Combat);
        return codes;
    }

    private static bool Prefix(Body __instance, ref AttackInfo atk, ref AttackSwingState __state) {
        MeleeCombatSync.LastAttackInfo = atk;
        MeleeCombatSync.LastAttacker = __instance;
        if (Util.IsBodyLocal(__instance)) {
            BlockDamageAuthority.BeginLocalSwing();
        }

        __state = new AttackSwingState {
            lastAttackCool = PlayerCamera.main.lastAttackCool,
            targetLookPos = __instance.targetLookPos,
            limb1Pos = __instance.limbs[(int)LimbIndex.UpperTorso].transform.position,
            atk = atk,
            atkPower = MeleeCombatSync.CalculateAttackPower(__instance, MeleeCombatSync.LastAttackInfo),
        };

        if (ForceOnlyRetrieveAttackInfo) {
            ForceOnlyRetrieveAttackInfo = false;
            return false;
        }

        breakTheAttackLoop = false;

        if (ForceOnlyStaminaUse) {
            atk = atk.CloneViaSerialization();
            atk.distance = 0f;
            ForceOnlyStaminaUse = false;
        }

        if (__instance.conscious && __instance.attackCooldown <= 0f) {
            __state.canDoAttack = true;
        }
        if (__state.canDoAttack && __instance.TryGetNetBody(out NetBody nb)) {
            nb.RollAttackRandom();
        }

        return true;
    }

    private static void Postfix(Body __instance, ref bool __result, ref AttackSwingState __state) {
        MeleeCombatSync.LastAttackResult = __result;
        StaticHitValidation = false;
        breakTheAttackLoop = false;

        bool attackerIsLocal = Util.IsBodyLocal(__instance);
        if (!attackerIsLocal) {
            PlayerCamera.main.lastAttackCool = __state.lastAttackCool;
            if (__state.canDoAttack) {
                __instance.attackCooldown *= 0.9f;
                __instance.attackCooldown -= 0.05f;
            }
        }

        if (__state.canDoAttack && Net.IsRunning && Util.IsWorldGenerated()) {
            if (MeleeCombatSync.LastHitLimb != null && !MeleeCombatSync.LastHitLimb.body.alive) {
                MeleeCombatSync.LastHitLimb = CombatStuff.CannibalismGetFarthestLimbToBeEfficient(MeleeCombatSync.LastHitLimb);
            }

            Item holdItem = __instance.GetItem(__instance.handSlot);

            if (Net.IsClient) {
                if (MeleeCombatSync.LastHitLimb != null) {
                    float totalDmg = MeleeCombatSync.DoPVPMeleeDamage(
                        __instance,
                        MeleeCombatSync.LastHitLimb,
                        __state.atk,
                        __state.atkPower,
                        Body_UseItem_MultiplayerPatch.current_used_item ?? holdItem,
                        out Vector2 knock);
                    WorldGeneration.CreateDamageNumber(
                        (Vector2)MeleeCombatSync.LastHitLimb.body.limbs[(int)LimbIndex.Head].transform.position + Vector2.up,
                        (int)totalDmg);

                    var kb = new BodyKnockbackPacket { Velocity = knock };
                    MeleeCombatSync.DoKnockback(MeleeCombatSync.LastHitLimb.body, in kb);

                    if (attackerIsLocal && MeleeCombatSync.LastHitLimb.TryGetNetBody(out NetBody pb)) {
                        pb.SetNetHealthSyncIgnoreTime();
                    }
                } else if (MeleeCombatSync.LastHitBuilding != null) {
                    float totalDmg = MeleeCombatSync.DamageBuilding(
                        __instance,
                        __state.atk,
                        MeleeCombatSync.LastHitBuilding,
                        __state.atkPower,
                        true,
                        MeleeCombatSync.LastHitPos);
                    WorldGeneration.CreateDamageNumber((Vector2)MeleeCombatSync.LastHitBuilding.transform.position + Vector2.up * 2f, (int)totalDmg);

                    if (NetObjectRegistry.TryGetSyncInfo(MeleeCombatSync.LastHitBuilding, out SyncInfo bSi)) {
                        bSi.SetIgnoreTimeForRoundTrip();
                    }
                }
            }

            if (attackerIsLocal) {
                AnyObjectNetId victimNetId = new AnyObjectNetId();
                if (MeleeCombatSync.LastHitLimb != null) {
                    if (MeleeCombatSync.LastHitLimb.TryGetNetBody(out _)) {
                        victimNetId = new AnyObjectNetId(MeleeCombatSync.LastHitLimb);
                    }
                } else if (MeleeCombatSync.LastHitBuilding != null) {
                    if (NetObjectRegistry.TryGetSyncInfoOrRegister(MeleeCombatSync.LastHitBuilding, out SyncInfo victimSi)) {
                        victimNetId = new AnyObjectNetId(victimSi);
                        victimSi.relaxed_combat_sync = true;
                        victimSi.SetIgnoreTimeForRoundTrip();
                    }
                } else if (MeleeCombatSync.LastHitWasBlock) {
                    victimNetId = AnyObjectNetId.ForBlockHit();
                }

                NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerBodyAttack);
                writer.Put(__state.targetLookPos);
                writer.Put(__state.limb1Pos);
                writer.Put(__state.atk.distance);
                writer.Put(__state.atk.unarmed);

                if (Body_UseItem_MultiplayerPatch.current_used_item != null
                    && NetObjectRegistry.TryGetSyncInfo(Body_UseItem_MultiplayerPatch.current_used_item.gameObject, out SyncInfo si1)) {
                    writer.Put(si1.syncId);
                    si1.SetIgnoreTimeForRoundTrip();
                } else if (holdItem != null
                    && holdItem.Stats.usable
                    && CombatStuff.ItemIsUsedForAttacking(holdItem)
                    && NetObjectRegistry.TryGetSyncInfo(holdItem.gameObject, out SyncInfo si)) {
                    writer.Put(si.syncId);
                    si.SetIgnoreTimeForRoundTrip();
                } else {
                    writer.Put((knetid)0);
                }

                writer.Put(__result);
                writer.Put(victimNetId);
                writer.Put(MeleeCombatSync.LastHitPos);
                writer.Put(BlockDamageAuthority.TakeLocalBlockDamage());

                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }

        MeleeCombatSync.LastHitBuilding = null;
        MeleeCombatSync.LastHitLimb = null;
        MeleeCombatSync.LastHitWasBlock = false;
    }
}
