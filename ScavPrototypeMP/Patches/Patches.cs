using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 辐射线推进、呕吐广播、进食/饮水/攀爬/负重与角色帧更新生理同步补丁。
/// 钻探舱、躯体初始化、飞船舱门与教程覆盖补丁见 <c>Patches.WorldAndTutorial.cs</c>。
/// </summary>
[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Activate))]
static class RadiationLine_Activate_MultiplayerPatch {
    static bool Prefix(RadiationLine __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        return __instance.timeGone != -10f;
    }
}

[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Deactivate))]
static class RadiationLine_Deactivate_MultiplayerPatch {
    public static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        RadiationLine_Update_MultiplayerPatch.timeGone = 0f;
    }
}

[HarmonyPatch(typeof(RadiationLine), nameof(RadiationLine.Update))]
public static class RadiationLine_Update_MultiplayerPatch {
    public static float timeGone = 0f;
    public static float timeGone_limit => WorldGeneration.world.height - 8;
    public static bool someone_is_getting_fried = false;

    public static void Postfix(RadiationLine __instance) {
        bool realFrying = false;
        if (Net.IsRunning) {
            if (!__instance.active) {
                return;
            }

            var line = RadiationLine.line;
            if (line.timeGone == 0f) {
                timeGone = 0f;
            } else {
                float lineFullSpeed = 1.5f;
                const float lineSlowSpeed = 0.2f;

                if (ServerMain.CheckIfShouldStartRadlineForStragglers(2) && !someone_is_getting_fried) {
                    lineFullSpeed *= 1.7f;
                }

                if (Multiplayer.rules.DisableSleep) {
                    timeGone += Time.deltaTime * lineFullSpeed;
                } else {
                    timeGone += Time.deltaTime * (ServerMain.CheckIfEveryoneAliveIsActuallyConscious() ? 1f : lineSlowSpeed) * lineFullSpeed;
                }

                if (timeGone > timeGone_limit) {
                    timeGone = timeGone_limit;
                }
                line.timeGone = timeGone;

                foreach (var bodynpc in NetBody.all_instances) {
                    var body = bodynpc?.body;
                    if (body == null || !body.alive || body.transform.position.y <= line.transform.position.y) {
                        continue;
                    }

                    realFrying = true;
                    float distAbove = body.transform.position.y - line.transform.position.y;

                    if (Multiplayer.rules.RadlineExtraDamage) {
                        if (distAbove > 40f || (!body.conscious && !body.sleeping)) {
                            body.brainHealth -= Time.deltaTime * Mathf.Clamp((100f - body.brainHealth) * 0.3f, 1f, 30f);
                            body.GetHead().pain += Time.deltaTime;
                        }
                        if (distAbove > 20f) {
                            body.GetHead().pain += Time.deltaTime;
                        }
                    }

                    PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(bodynpc, distAbove * 0.05f);

                    // 原版 RadiationLine.Update 已对本地相机躯体累加过一次基础辐射病与惊吓时间，此处必须跳过本地躯体以消除双倍辐射伤害
                    if (!body.IsBodyLocal()) {
                        float clamped = Mathf.Min(distAbove, 10f);
                        body.radiationSickness += clamped * Time.deltaTime * 0.03f;
                        body.eyeScareTime = 1f;
                    }
                }
            }
        }
        someone_is_getting_fried = realFrying;
    }
}

[HarmonyPatch(typeof(OnLimbWound), nameof(OnLimbWound.Update))]
static class OnLimbWound_Update_MultiplayerPatch {
    static bool Prefix(OnLimbWound __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (__instance.limb == null || __instance.cond(__instance.limb)) {
            UnityEngine.Object.Destroy(__instance.gameObject);
        }
        return false;
    }
}

internal static class VomiterSyncHelper {
    internal static void ForceNoWarning(Vomiter instance, bool isBlood) {
        if (!isBlood) {
            instance.vomiting = true;
        }
        var body = instance.GetComponent<Body>();
        if (body != null && body.IsBodyLocal() && body.TryGetNetBody(out NetBody nb)) {
            nb.SetNetHealthSyncIgnoreTime(0.1f);
        }
        instance.StartCoroutine(isBlood ? "DoBloodVomit" : "DoVomit");
    }

    internal static void AnnounceVomit(knetid plr, bool isBlood) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_VomiterVomit);
        writer.Put(plr);
        writer.Put(isBlood);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    internal static bool VerifyAndAnnounceVomit(Vomiter instance, bool isBlood) {
        if (!Net.IsRunning) {
            return true;
        }

        var body = instance.GetComponent<Body>();
        if (body == null || !body.alive) {
            return false;
        }

        if (Net.IsServer && body.TryGetNetBody(out var pb)) {
            AnnounceVomit(pb.netId, isBlood);
        }

        if (body.IsBodyLocal()) {
            return true;
        }
        ForceNoWarning(instance, isBlood);
        return false;
    }
}

[HarmonyPatch(typeof(Vomiter), nameof(Vomiter.VomitBlood))]
public static class Vomiter_VomitBlood_MultiplayerPatch {
    public static bool Prefix(Vomiter __instance) {
        return VomiterSyncHelper.VerifyAndAnnounceVomit(__instance, true);
    }
}

[HarmonyPatch(typeof(Vomiter), nameof(Vomiter.Vomit))]
public static class Vomiter_Vomit_MultiplayerPatch {
    public static void ForceNoWarning(Vomiter __instance, bool isblood) => VomiterSyncHelper.ForceNoWarning(__instance, isblood);
    public static void AnnounceVomit(knetid plr, bool isblood) => VomiterSyncHelper.AnnounceVomit(plr, isblood);
    internal static bool VerifyAndAnnounceVomit(Vomiter __instance, bool isblood) => VomiterSyncHelper.VerifyAndAnnounceVomit(__instance, isblood);

    public static bool Prefix(Vomiter __instance) {
        if (__instance.vomiting) {
            return true;
        }
        return VomiterSyncHelper.VerifyAndAnnounceVomit(__instance, false);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Eat), new[] { typeof(float), typeof(float) })]
static class Body_Eat_MultiplayerPatch {
    static void Prefix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }
        if (__instance.IsBodyLocal() && __instance.TryGetNetBody(out var nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }
}

#if FULLGAME
[HarmonyPatch(typeof(Body), nameof(Body.Eat), new[] { typeof(float), typeof(float), typeof(int) })]
static class Body_Eat_FoodType_MultiplayerPatch {
    static void Prefix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }
        CharacterSpawn.EnsureBodySpecies(__instance);
        if (__instance.IsBodyLocal() && __instance.TryGetNetBody(out NetBody nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }
}
#endif

[HarmonyPatch(typeof(Body), nameof(Body.Drink))]
static class Body_Drink_MultiplayerPatch {
    static void Prefix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }
        if (__instance.IsBodyLocal() && __instance.TryGetNetBody(out var nb)) {
            nb.SetNetHealthSyncIgnoreTime();
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.StartClimbing))]
public static class Body_StartClimbing_MultiplayerPatch {
    public static Climbable last_interacted_climbable = null;
    public static Body last_interacted_body = null;

    static void Prefix(Body __instance, Climbable climbable) {
        if (!Net.IsRunning) {
            return;
        }
        last_interacted_climbable = climbable;
        last_interacted_body = __instance;
    }

    static void Postfix(Body __instance) {
        last_interacted_climbable = null;
        if (Multiplayer.IsNetworkActiveAndIsServer()
            && Util.IsWorldGenerated()
            && __instance.currentClimbable != null
            && __instance.currentClimbable.TryGetComponent<TrackerClimbableEntity>(out var tracker)) {
            tracker.announce_reliable = false;
            tracker.Server_Announce();
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.GetTotalEncumberance))]
static class Body_GetTotalEncumberance_MultiplayerPatch {
    public static void Postfix(Body __instance, ref float __result) {
        if (Net.IsRunning && __instance.TryGetComponent<NetBody>(out var npc) && npc.carrying_person != null) {
            float realWeight = npc.carrying_person.GetRealFullEncumberanceOfBody();
            __result += realWeight * Multiplayer.rules.PiggybackWeightMultiplier;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DoPickupCheck))]
static class Body_DoPickupCheck_MultiplayerPatch {
    static bool Prefix(Body __instance, Item item, ref bool noAlerts, ref bool __result) {
        if (!__instance.conscious || Util.IsGeneratingWorld()) {
            noAlerts = true;
        }
        if (Net.IsRunning) {
            if (!__instance.IsBodyLocal() || !__instance.conscious) {
                noAlerts = true;
                __result = true;
                return false;
            }
            ItemSync.SnapOntoContainer(item);
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HasWearable), typeof(Item))]
static class Body_HasWearable_Item_MultiplayerPatch {
    static bool Prefix(Body __instance, Item item, ref bool __result) {
        if (item == null || item.Stats == null || __instance.LimbByName(item.Stats.desiredWearLimb) == null) {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HasWearable), typeof(string))]
static class Body_HasWearable_Id_MultiplayerPatch {
    static bool Prefix(Body __instance, string itemid, ref bool __result) {
        if (itemid == null || Item.GlobalItems == null || !Item.GlobalItems.TryGetValue(itemid, out var info) || info == null || __instance.LimbByName(info.desiredWearLimb) == null) {
            __result = false;
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.OnCollisionEnter2D))]
public static class Body_OnCollisionEnter2D_MultiplayerPatch {
    public static bool Prefix(Body __instance, Collision2D collision) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Util.IsBodyLocal(__instance)) {
            __instance.standLerpTime = 0.58f;
            __instance.crouchAmount -= __instance.lastTimeStepVelocity.y * 0.03f;
            if (__instance.lastTimeStepVelocity.y < 0f - __instance.jumpSpeed - 1f) {
                __instance.visualBodyOffset += new Vector2(0f, __instance.lastTimeStepVelocity.y * 0.035f);
                if (__instance.lastTimeStepVelocity.y < 0f - __instance.jumpSpeed - 5f) {
                    __instance.skills.AddExp(1, 0.5f);
                }
            }
            return false;
        }

        var go = collision.gameObject;
        if (go != null && go.TryGetTracker(out var tracker) && tracker.syncInfo.IsContactDangerous()) {
            tracker.syncInfo.relaxed_combat_sync = true;
        }
        return true;
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.HandleVisuals))]
public static class Body_HandleVisuals_MultiplayerPatch {
    static void Prefix(Body __instance, ref bool __state) {
        __state = __instance.inWater;
        if (!Net.IsRunning) {
            return;
        }
        if (!Util.IsBodyLocal(__instance)) {
            __instance.inWater = false;
            __instance.lastLiquidColor = Color.clear;
        }
    }

    static void Postfix(Body __instance, ref bool __state) {
        if (!Net.IsRunning) {
            return;
        }
        __instance.inWater = __state;
        NetBody.ApplyPiggybackCarryPose(__instance);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.Update))]
public static class Body_Update_MultiplayerPatch {
    static float lastgunangle = 0.0f;

    static void Prefix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }
        SpeechCharContext.scope++;
        __instance.GetCoUtilsInstance().SelectForMPOverride();
    }

    static void Postfix(Body __instance) {
        if (Net.IsRunning && SpeechCharContext.scope > 0) {
            SpeechCharContext.scope--;
        }
        CoUtilsMultiplayer.DeselectMPOverride();

        var body = __instance;
        if (!Util.IsBodyLocal(body)) {
            body.liquidDrinkTime = -0.5f;
            if (Util.world != null && Util.world.biomeDepth == 5 && body.temperature < 32.5f) {
                body.snowAmount = Mathf.MoveTowards(body.snowAmount, 1f, Time.deltaTime * 0.0125f);
            } else {
                body.snowAmount = Mathf.MoveTowards(body.snowAmount, 0f, Time.deltaTime * 0.02f);
            }
        }

        if (!Net.IsRunning || WorldGeneration.world == null || WorldGeneration.world.generatingWorld) {
            return;
        }

        var npc = body.GetComponent<NetBody>();
        if (npc == null) {
            return;
        }

        if (body.TryGetNetPlayer(out var plr)) {
            if (plr.is_pointingfingerat && !body.armsAnimator.GetBool("gun")) {
                float torsoZ = body.limbs[(int)LimbIndex.UpperTorso].transform.eulerAngles.z;
                if (torsoZ > 180f) {
                    torsoZ -= 360f;
                }
                float facing = body.isRight ? 1f : -1f;
                body.armsAnimator.SetBool("gun", true);

                Vector2 aimDir = (plr.is_pointingfingeratTARGET - (Vector2)body.limbs[(int)LimbIndex.UpperTorso].transform.position).normalized;
                float value = Mathf.Lerp(
                    lastgunangle,
                    Vector2.SignedAngle(body.transform.right * facing, aimDir) * facing - torsoZ * facing,
                    Time.deltaTime * 8f * body.slots[body.handSlot].armPowerMult);
                body.armsAnimator.SetFloat("gunangle", value);
                lastgunangle = value;
            } else {
                lastgunangle = body.armsAnimator.GetFloat("gunangle");
            }
        }

        if (body.alive) {
            if (!Util.IsBodyLocal(body)) {
                body.attackCooldown -= Time.deltaTime * 0.05f;
            }

            // 原版 Body.Update 已累加 standingOn.toxicity 的辐射病，此处仅同步被联机拦截的镜头辐射渐晕强度
            var standingOn = body.standingOn;
            if (body.grounded && standingOn != null && standingOn.toxicity > 0f) {
                PlayerCamera_SetIrradiateIntensity_MultiplayerPatch.RealSetIrradiateIntensity(npc, standingOn.toxicity * 0.25f);
            }

            var nearestMedic = ScavPlayer.GetNearestConsciousPlayerToThisBody(npc);
            bool lastStanding = nearestMedic.Item1 == null;
            bool isDyingAlone = lastStanding || nearestMedic.Item2 > GameplayRanges.RagdollSyncGroupRadiusSqr;
            bool isLastAlive = ScavPlayer.AllLivingPlayers.Count == 1 && body.alive;

            if (ScavPlayer.ClientIdToPlayerDict.Count == 1) {
                lastStanding = false;
            }

            float totalBrainHealthLoss = 0f;
            if (body.brainDying) {
                totalBrainHealthLoss += 1.5f;
            } else if (body.bloodOxygen < 80f) {
                totalBrainHealthLoss += ((80f - body.bloodOxygen) / 600f) * 0.4f;
            }
            if (body.temperature > 42f) {
                totalBrainHealthLoss += 0.5f;
            }
            if (body.radiationSickness > 0f) {
                totalBrainHealthLoss += body.radiationSickness * 0.0005f;
            }
            if (body.TryGetComponent<Painkillers>(out var painkill)
                && !body.conscious
                && painkill.actualOpiateReception > 50f
                && body.energy < 40f
                && body.bloodOxygen < 30f) {
                totalBrainHealthLoss += (painkill.actualOpiateReception - 40f) * 0.015f;
            }
            if (!body.conscious && body.radiationSickness > 90f) {
                totalBrainHealthLoss += 0.3f;
            }

            if (!lastStanding && !body.brainDying && body.brainHealth < 100f && body.IsAirBreathable()) {
                float realRegenMult = Multiplayer.rules.AdditionalBrainRegen;
                if (realRegenMult > 0f) {
                    float hungerBrainMult = Mathf.Clamp(body.hunger / 100f, 0f, 1.3f)
                        * Mathf.Clamp(body.thirst / 100f, 0f, 1.3f)
                        * KM.RemapClamped(body.brainHealth, 100f, 70f, 0f, 1f);
                    float finalRegen = realRegenMult * hungerBrainMult - totalBrainHealthLoss * 0.7f;
                    if (finalRegen > 0f) {
                        body.brainHealth += 0.16f * finalRegen * Time.deltaTime;
                    }
                }
            }

            if (body.consciousness < 19f) {
                if (body.brainDying && body.lastTimeStepVelocity.magnitude < 7f && !body.sleeping && isDyingAlone) {
                    float timeSpeedup = 1.0f;
                    if (lastStanding) {
                        timeSpeedup = 3.5f;
                    }
                    if (isLastAlive) {
                        timeSpeedup = 10f;
                    }
                    if (npc.piggybacking_on != null || (body.succesfullyRolledLastStand && body.lastStandTime > 280f)) {
                        timeSpeedup = 0f;
                    }

                    float realDecayMult = timeSpeedup * Math.Max(Multiplayer.rules.AdditionalHealthDecay, 0f);
                    if (realDecayMult > 0f) {
                        float deltaMultiplied = Time.deltaTime * realDecayMult;
                        if (body.totalBleedSpeed > 0f) {
                            body.bloodVolume -= body.totalBleedSpeed * deltaMultiplied;
                        }
                        if (body.TryGetComponent<Painkillers>(out var pain) && pain.actualOpiateReception > 30f) {
                            body.energy -= pain.actualOpiateReception * 0.0025f * deltaMultiplied;
                        }
                        body.brainHealth -= totalBrainHealthLoss * deltaMultiplied;
                    }
                }
            } else {
                float realRegenMult = Math.Max(Multiplayer.rules.AdditionalHealthRegen, 0f);
                if (realRegenMult > 0f) {
                    float regenDelta = Time.deltaTime * realRegenMult;
                    if (!body.brainDying && body.IsAirBreathable()) {
                        body.brainHealth += 0.003f * regenDelta;
                    }
                    body.hearingLoss = Mathf.Clamp(body.hearingLoss - 0.05f * regenDelta, 0f, 100f);
                    body.clawHealth = Mathf.Clamp(body.clawHealth + regenDelta * body.clawGrowthRate * 0.5f, 0f, 100f);
                    body.hungerLimbHealCurrent *= realRegenMult;
                    if (body.totalBleedSpeed <= 0f) {
                        body.bloodVolume = Mathf.MoveTowards(body.bloodVolume, 100f, regenDelta * body.bloodRegenSpeed);
                    }
                    if (body.stamina > 60f && ((body.standing && Mathf.Abs(body.moveDir.x) < 0.1f && !npc.exercising) || !body.standing)) {
                        body.stamina += regenDelta * 0.5f;
                    }
                }
            }
        } else if (body.sleeping) {
            body.WakeUp();
        }

        if (Multiplayer.rules.DisableSleep) {
            if (!body.isCriticallyDying) {
                body.energy = Mathf.MoveTowards(body.energy, 100f, body.conscious ? Time.deltaTime : Time.deltaTime * 0.25f);
            }
            if (body.sleeping) {
                body.WakeUp();
            }
        }
    }
}
