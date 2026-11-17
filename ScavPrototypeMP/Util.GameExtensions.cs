using HarmonyLib;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Text;
using Together;
using Together.MpConstants;
using UnityEngine;

namespace CasualtiesTogetherUtils;


public static class Util_ItemExtensions {

    public static bool IsEdible(this Item item) {
        return item.Stats.category == "food" || (item.id == "foliage" || item.id == "dryfoliage");
    }

    public static bool IsHandcrank(this Item item) {
        return item.id == "handcrank";
    }

    public static bool IsAED(this Item item) {
        return item.id == "aed";
    }
    public static bool IsManualDefibrillator(this Item item) {
        return item.id == "manualdefibrillator";
    }


    public static Sprite GetSprite(this Item item) {
        return item.GetComponent<SpriteRenderer>().sprite;
    }


    public static bool IsItemOrBuilding(this GameObject go) {
        return go.GetComponent<Item>() != null || go.GetComponent<BuildingEntity>() != null;
    }

    //affect.wasWater
    public static bool IsInWater(this Item item) {
        return item.affect.wasWater;
    }
}

public static class Util_ContainerExtensions {

    public static List<Item> GetAllItems(this Container container) {
        var list = new List<Item>(container.transform.childCount);
        foreach (Transform item in container.transform)
            if (item.TryGetComponent<Item>(out var component))
                list.Add(component);
        return list;
    }

}


public static class Util_LimbExtensions {
    public static byte GetIndex(this Limb limb) {
        // this is actually Array.IndexOf but less safe but faster
        for (byte i = 0; i < (byte)limb.body.limbs.Length; i++) {
            if (limb == limb.body.limbs[i]) {
                return i;
            }
        }
        return 0;
    }

    public static Vector2 GetPosition(this Limb limb) {
        return limb.transform.position;
    }
    public static bool IsBodyLocal(this Limb limb) {
        return limb.body.IsBodyLocal();
    }

    public static bool HasProsthetic(this Limb limb) {
        if (limb == null) {
            return false;
        }
        try {
            Traverse info = Traverse.Create(limb).Field("prostheticInfo");
            if (!info.FieldExists()) {
                return false;
            }
            object boxed = info.GetValue();
            if (boxed == null) {
                return false;
            }
            return Traverse.Create(boxed).Field("hasProsthetic").GetValue<bool>();
        } catch (Exception) {
            // Demo 版本或不含义肢字段的实体在反射读取时安全降级为无义肢
            return false;
        }
    }

}


public static class Util_BodyExtensions {

    public static bool IsBodyLocal(this Vomiter vomiter) {
        return PlayerCamera.main.body == vomiter.body;
    }
    public static bool IsBodyLocal(this Body body) {
        return PlayerCamera.main.body == body;
    }
    public static Vector2 GetPosition(this Body body) {
        return body.transform.position;
    }
    public static Vector2 GetVelocity(this Body body) {
        if (body.standing) {
            return body.rb.velocity;
        } else {
            return body.GetUpperTorso().rb.velocity;
        }
    }

    public static bool IsAirBreathable(this Body body) {
        return !body.inWater || body.hasScubaGear;
    }

    public static bool IsMindwiped(this Body body) {
        return body.mindWipe != null && body.mindWipe.active;
    }

    public static bool IsRibsBroken(this Body body) {
        //OG: MoodleManager  V510
        return body.limbs[(int)LimbIndex.UpperTorso].broken;
    }
    public static bool IsRagdolling(this Body body) => !body.standing;


    public static bool IsAliveAndUnconsciousAndNotSleeping(this Body body) {
        if (body.alive) {
            return !body.conscious && !body.sleeping;
        }
        return false;
    }


    public static Limb GetPrimaryHand(this Body body) => body.limbs[(body.handSlot == AnatomyConstants.PrimaryHandSlot) ? (int)LimbIndex.PrimaryHand : (int)LimbIndex.SecondaryHand];
    public static Limb GetSecondaryHand(this Body body) => body.limbs[(body.handSlot == AnatomyConstants.PrimaryHandSlot) ? (int)LimbIndex.SecondaryHand : (int)LimbIndex.PrimaryHand];

    public static FacialExpression GetFace(this Body body) => body.limbs[(int)Body.LimbNum.Head].GetComponent<FacialExpression>();
    public static PantSound GetPantSound(this Body body) => body.GetComponent<PantSound>();
    public static Limb GetHead(this Body body) => body.limbs[(int)Body.LimbNum.Head];
    public static Limb GetUpperTorso(this Body body) => body.limbs[(int)Body.LimbNum.UpTorso];
    public static Limb GetLowerTorso(this Body body) => body.limbs[(int)Body.LimbNum.DownTorso];
    public static Limb GetLimb(this Body body, Body.LimbNum limbNum) => body.limbs[(int)limbNum];
    public static Limb GetLimb(this Body body, int limbNum) => body.limbs[limbNum];
    public static Limb GetLimb(this Body body, byte limbNum) => body.limbs[limbNum];



    public static Limb GetClosestLimb(this Body body, Vector2 pos, out float dist_sqr) {
        dist_sqr = 999999f;
        Limb result = body.limbs[(int)LimbIndex.Head];
        foreach (Limb limb in body.limbs) {
            if (!limb.dismembered && KM.DistanceCheck_PreSqr(pos, limb.transform.position, dist_sqr, out var newdist)) {
                result = limb;
                dist_sqr = newdist;
            }
        }
        return result;
    }


    public static void ResetMind(this Body body) {
        body.Body_ResetSkills();
        if (body.TryGetNetBody(out var nb) && nb.IsPlayer) {
            nb.ResetMind();
        }
        body.corpsesSeen = 0;
        body.desensitizedMult = 1;
    }

    public static void ResetLimbHealth(this Limb limb) {
        if (limb.TryGetComponent(out SplintLimb splint)) {
            splint.condition = 0;
            splint.TakeOff();
        }
        if (limb.TryGetComponent(out TourniquetScript tour)) {
            tour.condition = 0;
            tour.TakeOff();
        }
        limb.muscleHealth = 100f;
        limb.skinHealth = 100f;
        limb.boneHealTimer = 0f;
        limb.dislocationTimer = 0f;
        limb.infectionAmount = 0f;
        limb.bleedAmount = 0f;
        limb.pain = 0f;
        limb.shrapnel = 0;
        limb.furBloodAmount = 0f;
        limb.infected = false;
    }

    public static void ResetHealth(this Body body, bool unmindwipe = true) {
        try {
            // regrow missing limbs (ts kinda broken still)
            body.RegrowAllLimbs();

            foreach (var limb in body.limbs) {
                ResetLimbHealth(limb);
            }

            body.brainHealth = 100f;

            body.bloodVolume = 100f;
            body.bloodOxygen = 100f;
            body.bloodPressure = 120f;
            body.heartRate = 70f;
            body.bloodVesselSize = 1f;
            body.bloodViscosity = 0f;
            body.respiratoryRate = 100f;
            body.strokeAmount = 0f;
            body.hasPulmonaryEmbolism = false;
            body.fibrillationProgress = 0f;

            body.hunger = 100f;
            body.thirst = 100f;
            body.septicShock = 0f;
            body.temperature = 37f;
            body.sicknessAmount = 0f;
            body.consciousness = 100f;
            body.stamina = 100f;
            body.energy = 100f;
            body.happiness = 0f;
            body.weightOffset = 0f;
            body.radiationSickness = 0f;
            body.disfigured = false;
            body.eyeGone = false;
            body.bothEyesGone = false;
            body.internalBleeding = 0f;
            body.hemothorax = 0f;
            body.traumaAmount = 0f;
            body.dirtyness = 0f;
            body.wetness = 0f;
            body.badSleepAmount = 0f;
            body.hearingLoss = 0f;
            body.antidepressantHappiness = 0f;
            body.antibioticImmunityTime = 0f;
            body.brainGrowSickness = 0f;
            body.triedRollingLastStand = false;
            body.succesfullyRolledLastStand = false;
            body.reversedControls = false;
            body.lastStandTime = -1000f;
            body.adrenaline = 0f;
            body.curAdrenaline = 0f;
            body.opiateHappiness = 0f;
            body.venomCurrent = 0f;
            body.venomTotal = 0f;
            body.clawHealth = 100f;
            body.bloodPressureChangeFromMedicine = 0f;
            body.caffeinated = 0f;
            body.clawRegrowTime = 0f;
            body.painShock = 0f;

            Traverse.Create(body).Field("venomFake").SetValue(0f);
            Traverse.Create(body).Field("barotrauma").SetValue(0f);

            if (body.TryGetComponent(out Painkillers component))
                UnityEngine.Object.Destroy(component);
            if (body.TryGetComponent(out SleepingPills component2))
                UnityEngine.Object.Destroy(component2);
            if (body.TryGetComponent(out Antidepressants component3))
                UnityEngine.Object.Destroy(component3);

            if (body.TryGetNetBody(out var nb)) {
                // to ensure attempt to get the real coutils instance
                if (nb.IsPlayer && nb.plr.CoUtilsInstance != null) {
                    var coutil = nb.plr.CoUtilsInstance;
                    coutil.CancelAll();
                }
                nb.OnDamageEvent(new BodyDamageEvent(BodyDamageEvent.DamageEventType.Revive, nb));
            }

            body.GetCoUtilsInstance().CancelAll();

            if (unmindwipe) {
                if (body.TryGetComponent(out MindwipeScript component222222))
                    UnityEngine.Object.Destroy(component222222); // the vignette is removed by other patch
            }

        } catch (Exception ex) {
            log.error(ex);
            throw;
        }
    }


    //OG: Body.ForceAutoPickUpItem V7.0.1
    public static void ForceAutoPickUpItem(this Body body, Item item) {
        //only removed the noautopickup check

        if (!item.Stats.wearable) {
            int? num = body.FirstEmptySlot();
            if (!num.HasValue) {
                foreach (Item surfaceInventoryItem in body.GetSurfaceInventoryItems()) {
                    if ((bool)surfaceInventoryItem.container && surfaceInventoryItem.container.CanHoldItem(item)) {
                        surfaceInventoryItem.container.LoadItem(item);
                        break;
                    }
                }

                return;
            }

            body.PickUpItem(item, num.Value, force: true);
        } else {
            Item wearableBySlotID = body.GetWearableBySlotID(item.Stats.wearSlotId);
            if ((bool)wearableBySlotID) {
                body.DropItem(wearableBySlotID);
            }

            body.WearWearable(item);
            PlayerCamera.main.UpdateWearables();
        }
    }


    public static string DumpBodyVars(this Body body) {
        var sb = new StringBuilder();
        sb.AppendLine("BODY VARS DUMP:");

        try {
            {
                var bodyfields = typeof(Body).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();
                foreach (var field in bodyfields) {
                    sb.AppendLine($"\t{field.Name}: {field.GetValue(body)}");
                }
            }
            {
                if (body.TryGetComponent(out Painkillers painkill)) {
                    var painkillfields = typeof(Painkillers).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();

                    sb.AppendLine("Painkillers:");
                    foreach (var field in painkillfields) {
                        sb.AppendLine($"\t{field.Name}: {field.GetValue(painkill)}");
                    }
                }
            }
            {
                sb.AppendLine("Limbs:");
                var limbfields = typeof(Limb).GetFields(BindingFlags.Instance | BindingFlags.Public).Where((FieldInfo f) => f.FieldType.IsPrimitive).ToArray();
                foreach (var limb in body.limbs) {
                    sb.AppendLine($"\t{limb.name} - {limb.fullName} - {limb.shortName}:");
                    if (limb.dismembered) {
                        sb.AppendLine($"\t\tAMPUTATED");
                    } else {
                        foreach (var field in limbfields) {
                            sb.AppendLine($"\t\t{field.Name}: {field.GetValue(limb)}");
                        }
                    }
                }
            }
            {
                sb.AppendLine("CoUtils:");
                var inst = body.GetNetBody()?.plr?.CoUtilsInstance;
                if (inst == null) {
                    sb.AppendLine("NULL");
                } else {
                    if (inst.activeOps != null) {
                        if (inst.activeOps.Count == 0) {
                            sb.AppendLine("ZERO ACTIVE");
                        } else {
                            foreach (var item in inst.activeOps) {
                                sb.AppendLine(item.ToString());
                            }
                        }
                    } else {
                        sb.AppendLine("ERROR");
                    }
                }
            }
        } catch (Exception ex) {
            sb.AppendLine(ex.ToString());
        }

        return sb.ToString();
    }

}
