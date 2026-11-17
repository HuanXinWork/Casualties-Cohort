using CasualtiesTogetherUtils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 角色全身生理指标与 15 肢体完整健康快照同步包（按内存拷贝序列化，严禁修改任何字段顺序或条件编译分支）。
/// </summary>
public struct CharacterHealthStateSyncPacket : INetSerializeByMemcpy {
    ushort Tick;

    Bitset8 pb1;
    Bitset8 pb2;

    public bool bothEyesGone {
        get => pb1[0];
        set => pb1[0] = value;
    }
    public bool disfigured {
        get => pb1[1];
        set => pb1[1] = value;
    }
    public bool eyeGone {
        get => pb1[2];
        set => pb1[2] = value;
    }
    public bool triedRollingLastStand {
        get => pb1[3];
        set => pb1[3] = value;
    }
    public bool succesfullyRolledLastStand {
        get => pb1[4];
        set => pb1[4] = value;
    }
    public bool mindwiped {
        get => pb1[5];
        set => pb1[5] = value;
    }
    public bool painkilled {
        get => pb1[6];
        set => pb1[6] = value;
    }
    public bool sleeping {
        get => pb1[7];
        set => pb1[7] = value;
    }

    CharacterSkillsStateSyncPacket skills;

    private byte compressedclawHealth;
    public float clawHealth {
        get => compressedclawHealth / 2.55f;
        set => compressedclawHealth = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    private byte compressednum3;
    public float brainHealth {
        get => compressednum3 / 2.55f;
        set => compressednum3 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    public bool usedNeuralBooster;
    public float maxSpeed;
    public float moveForce;
    public float jumpSpeed;
    public float caffeinated;

    public float bloodVolume;
    public float bloodOxygen;
    public float bloodPressure;
    public float respiratoryRate;

    public float heartRate;
    public float bloodVesselSize;
    public float bloodViscosity;
    public float strokeAmount;
    public bool hasPulmonaryEmbolism;
    public float fibrillationProgress;

    public float venomCurrent;
    public float venomTotal;

    public float hunger;
    public float thirst;
    public float septicShock;
    public float temperature;
    public float sicknessAmount;
    public float consciousness;
    public float stamina;

    private byte compressednum4;
    public float energy {
        get => compressednum4 / 2.55f;
        set => compressednum4 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    public float happiness;
    public float weightOffset;
    public float radiationSickness;
    public float internalBleeding;
    public float hemothorax;
    public float traumaAmount;

    private byte compressednum2;
    public float dirtyness {
        get => compressednum2 / 2.55f;
        set => compressednum2 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    private byte compressednum1;
    public float wetness {
        get => compressednum1 / 2.55f;
        set => compressednum1 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    public float badSleepAmount;
    public float hearingLoss;
    public float antidepressantHappiness;
    public float opiateHappiness;
    public float antibioticImmunityTime;
    public float brainGrowSickness;
    public float lastStandTime;
    public float adrenaline;

    private byte compressednumb;
    public float burpTimer {
        get => (compressednumb / 2.55f / 0.9f) - 100f;
        set => compressednumb = (byte)(Mathf.Clamp((value + 100f) * 0.9f, 0f, 100f) * 2.55f);
    }

    public float stimulantMultiplier;

#if FULLGAME
    public bool hasCargo {
        get => pb2[0];
        set => pb2[0] = value;
    }
    public float venomFake;
    public float barotrauma;
#endif

    CharacterLimbHealthState limb0;
    CharacterLimbHealthState limb1;
    CharacterLimbHealthState limb2;
    CharacterLimbHealthState limb3;
    CharacterLimbHealthState limb4;
    CharacterLimbHealthState limb5;
    CharacterLimbHealthState limb6;
    CharacterLimbHealthState limb7;
    CharacterLimbHealthState limb8;
    CharacterLimbHealthState limb9;
    CharacterLimbHealthState limb10;
    CharacterLimbHealthState limb11;
    CharacterLimbHealthState limb12;
    CharacterLimbHealthState limb13;
    CharacterLimbHealthState limb14;

    public CharacterHealthStateSyncPacket() { }

    public void Write(Body body) {
        skills = new CharacterSkillsStateSyncPacket(body);

        burpTimer = body.burpTimer;
        bothEyesGone = body.bothEyesGone;
        disfigured = body.disfigured;
        eyeGone = body.eyeGone;
        triedRollingLastStand = body.triedRollingLastStand;
        succesfullyRolledLastStand = body.succesfullyRolledLastStand;
        clawHealth = body.clawHealth;
        brainHealth = body.brainHealth;

        usedNeuralBooster = body.usedNeuralBooster;
        maxSpeed = body.maxSpeed;
        moveForce = body.moveForce;
        jumpSpeed = body.jumpSpeed;
        caffeinated = body.caffeinated;

        bloodVolume = body.bloodVolume;
        bloodOxygen = body.bloodOxygen;
        bloodPressure = body.bloodPressure;
        respiratoryRate = body.respiratoryRate;
        heartRate = body.heartRate;
        bloodVesselSize = body.bloodVesselSize;
        bloodViscosity = body.bloodViscosity;
        strokeAmount = body.strokeAmount;
        hasPulmonaryEmbolism = body.hasPulmonaryEmbolism;
        fibrillationProgress = body.fibrillationProgress;

        venomCurrent = body.venomCurrent;
        venomTotal = body.venomTotal;

        hunger = body.hunger;
        thirst = body.thirst;
        septicShock = body.septicShock;
        temperature = body.temperature;
        sicknessAmount = body.sicknessAmount;
        consciousness = body.consciousness;
        stamina = body.stamina;
        energy = body.energy;
        happiness = body.happiness;
        weightOffset = body.weightOffset;
        radiationSickness = body.radiationSickness;
        internalBleeding = body.internalBleeding;
        hemothorax = body.hemothorax;
        traumaAmount = body.traumaAmount;
        dirtyness = body.dirtyness;
        wetness = body.wetness;
        badSleepAmount = body.badSleepAmount;
        hearingLoss = body.hearingLoss;
        antidepressantHappiness = body.antidepressantHappiness;
        opiateHappiness = body.opiateHappiness;
        antibioticImmunityTime = body.antibioticImmunityTime;
        brainGrowSickness = body.brainGrowSickness;
        lastStandTime = body.lastStandTime;
        adrenaline = body.adrenaline;

        sleeping = body.sleeping;
        mindwiped = body.IsMindwiped();
        stimulantMultiplier = body.stimulantMultiplier;

#if FULLGAME
        hasCargo = body.hasCargo;
        venomFake = body.venomFake;
        barotrauma = body.barotrauma;
#endif

        painkilled = body.TryGetComponent(out Painkillers _);

        limb0 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.Head]);
        limb1 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.UpperTorso]);
        limb2 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.LowerTorso]);
        limb3 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.PrimaryUpperArm]);
        limb4 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.PrimaryForearm]);
        limb5 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.PrimaryHand]);
        limb6 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.SecondaryUpperArm]);
        limb7 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.SecondaryForearm]);
        limb8 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.SecondaryHand]);
        limb9 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.FrontThigh]);
        limb10 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.FrontCalf]);
        limb11 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.FrontFoot]);
        limb12 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.BackThigh]);
        limb13 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.BackCalf]);
        limb14 = new CharacterLimbHealthState(body.limbs[(int)LimbIndex.BackFoot]);

        Tick = Net.ServerTick;
    }

    public void Apply(NetBody npc, bool force = false) {
        if (npc == null || npc.body == null) {
            return;
        }
        int seqDiff = LiteEntitySystem.Utils.SequenceDiff(Tick, npc._lastHealthPacketReceiveTick);
        if (!force && seqDiff <= 0) {
            if (seqDiff < 0) {
                ModLog.Debug($"丢弃乱序旧健康快照: diff={seqDiff}, target={npc}", LogDomain.Medical);
            }
            return;
        }
        npc._lastHealthPacketReceiveTick = Tick;
        Apply(npc.body);
    }

    public void Apply(Body body) {
        if (body == null) {
            return;
        }
        bool isLocal = Util.IsBodyLocal(body);

        if (body.IsAnyLimbMissing() && HasLimbStructureMismatch(body)) {
            Util.RegrowAllLimbs(body);
        }

        limb0.Apply(body.limbs[(int)LimbIndex.Head]);
        limb1.Apply(body.limbs[(int)LimbIndex.UpperTorso]);
        limb2.Apply(body.limbs[(int)LimbIndex.LowerTorso]);
        limb3.Apply(body.limbs[(int)LimbIndex.PrimaryUpperArm]);
        limb4.Apply(body.limbs[(int)LimbIndex.PrimaryForearm]);
        limb5.Apply(body.limbs[(int)LimbIndex.PrimaryHand]);
        limb6.Apply(body.limbs[(int)LimbIndex.SecondaryUpperArm]);
        limb7.Apply(body.limbs[(int)LimbIndex.SecondaryForearm]);
        limb8.Apply(body.limbs[(int)LimbIndex.SecondaryHand]);
        limb9.Apply(body.limbs[(int)LimbIndex.FrontThigh]);
        limb10.Apply(body.limbs[(int)LimbIndex.FrontCalf]);
        limb11.Apply(body.limbs[(int)LimbIndex.FrontFoot]);
        limb12.Apply(body.limbs[(int)LimbIndex.BackThigh]);
        limb13.Apply(body.limbs[(int)LimbIndex.BackCalf]);
        limb14.Apply(body.limbs[(int)LimbIndex.BackFoot]);

        body.clawHealth = clawHealth;
        body.bothEyesGone = bothEyesGone;
        body.disfigured = disfigured;
        body.eyeGone = eyeGone;
        body.brainHealth = brainHealth;

        body.usedNeuralBooster = usedNeuralBooster;
        body.maxSpeed = maxSpeed;
        body.moveForce = moveForce;
        body.jumpSpeed = jumpSpeed;
        body.caffeinated = caffeinated;

        body.bloodVolume = bloodVolume;
        body.bloodOxygen = bloodOxygen;
        body.bloodPressure = bloodPressure;
        body.respiratoryRate = respiratoryRate;
        body.heartRate = heartRate;
        body.bloodVesselSize = bloodVesselSize;
        body.bloodViscosity = bloodViscosity;
        body.strokeAmount = strokeAmount;
        body.hasPulmonaryEmbolism = hasPulmonaryEmbolism;
        body.fibrillationProgress = fibrillationProgress;

        body.venomCurrent = venomCurrent;
        body.venomTotal = venomTotal;

        body.hunger = hunger;
        body.thirst = thirst;
        body.septicShock = septicShock;
        body.temperature = temperature;
        body.sicknessAmount = sicknessAmount;
        body.consciousness = consciousness;
        body.stamina = stamina;
        body.energy = energy;
        body.happiness = happiness;

        body.weightOffset = weightOffset;
        body.radiationSickness = radiationSickness;
        body.internalBleeding = internalBleeding;
        body.hemothorax = hemothorax;
        body.traumaAmount = traumaAmount;
        body.dirtyness = dirtyness;
        body.wetness = wetness;
        body.badSleepAmount = badSleepAmount;
        body.hearingLoss = hearingLoss;
        body.antidepressantHappiness = antidepressantHappiness;
        body.opiateHappiness = opiateHappiness;
        body.antibioticImmunityTime = antibioticImmunityTime;
        body.brainGrowSickness = brainGrowSickness;
        body.lastStandTime = lastStandTime;
        body.adrenaline = adrenaline;
        body.sleeping = sleeping;

        if (isLocal && succesfullyRolledLastStand && !body.succesfullyRolledLastStand && PlayerCamera.main != null) {
            ModLog.Info($"{UIMainMenu.USERINPUT_NAME} 成功触发绝地反击 (Last Stand)", LogDomain.Medical);
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.LastStandSequence());
        }
        body.triedRollingLastStand = triedRollingLastStand;
        body.succesfullyRolledLastStand = succesfullyRolledLastStand;

        if (mindwiped && body.mindWipe == null) {
            body.mindWipe = body.gameObject.GetOrAddComponent<MindwipeScript>();
            body.mindWipe.active = true;
        } else if (!mindwiped && body.mindWipe != null) {
            Object.Destroy(body.mindWipe);
        }

        if (!painkilled && body.TryGetComponent<Painkillers>(out Painkillers component)) {
            component.opiateAmount = 0f;
            component.opiateTolerance = 0f;
        }

        body.stimulantMultiplier = stimulantMultiplier;
#if FULLGAME
        if (MacroFleshRebuilderMp.BlockCargoReembed(body) && hasCargo) {
            hasCargo = false;
        }
        if (hasCargo && !body.hasCargo) {
            body.hasCargo = true;
            if (body.cargo != null) {
                body.cargo.SetActive(true);
            }
            body.Ragdoll();
        } else if (!hasCargo && body.hasCargo) {
            body.hasCargo = false;
            if (body.cargo != null) {
                body.cargo.SetActive(false);
            }
        } else {
            body.hasCargo = hasCargo;
        }

        body.venomFake = venomFake;
        body.barotrauma = barotrauma;
#endif

        body.burpTimer = burpTimer;
        skills.Apply(body);

        if (body.TryGetNetBody(out NetBody npc)) {
            npc.last_mood = happiness;
        }
    }

    private bool HasLimbStructureMismatch(Body body) {
        return
            body.limbs[(int)LimbIndex.Head].dismembered != limb0.dismembered ||
            body.limbs[(int)LimbIndex.UpperTorso].dismembered != limb1.dismembered ||
            body.limbs[(int)LimbIndex.LowerTorso].dismembered != limb2.dismembered ||
            body.limbs[(int)LimbIndex.PrimaryUpperArm].dismembered != limb3.dismembered ||
            body.limbs[(int)LimbIndex.PrimaryForearm].dismembered != limb4.dismembered ||
            body.limbs[(int)LimbIndex.PrimaryHand].dismembered != limb5.dismembered ||
            body.limbs[(int)LimbIndex.SecondaryUpperArm].dismembered != limb6.dismembered ||
            body.limbs[(int)LimbIndex.SecondaryForearm].dismembered != limb7.dismembered ||
            body.limbs[(int)LimbIndex.SecondaryHand].dismembered != limb8.dismembered ||
            body.limbs[(int)LimbIndex.FrontThigh].dismembered != limb9.dismembered ||
            body.limbs[(int)LimbIndex.FrontCalf].dismembered != limb10.dismembered ||
            body.limbs[(int)LimbIndex.FrontFoot].dismembered != limb11.dismembered ||
            body.limbs[(int)LimbIndex.BackThigh].dismembered != limb12.dismembered ||
            body.limbs[(int)LimbIndex.BackCalf].dismembered != limb13.dismembered ||
            body.limbs[(int)LimbIndex.BackFoot].dismembered != limb14.dismembered;
    }

    public override string ToString() {
        return $"HealthPacket(Tick: {Tick})";
    }
}
