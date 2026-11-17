using System.Reflection;
using System.Runtime.CompilerServices;
using System.Runtime.InteropServices;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 单个肢体健康状态二进制快照（按内存布局直接拷贝序列化，严禁改动字段顺序或类型以保持 v5.5.x 线协议 100% 兼容）。
/// </summary>
public struct CharacterLimbHealthState : INetSerializeByMemcpy {
    private static readonly FieldInfo ProsthesisRepairLimbField = AccessTools.Field(typeof(ProsthesisRepairMinigame), "limb");
    private static readonly FieldInfo ProsthesisRepairStartedBrokenField = AccessTools.Field(typeof(ProsthesisRepairMinigame), "startedBroken");

    Bitset8 pb1;

    public bool dismembered {
        get => pb1[0];
        set => pb1[0] = value;
    }
#if FULLGAME
    public bool has_prosthetic {
        get => pb1[1];
        set => pb1[1] = value;
    }
    public byte prostheticKind;
#endif
    public bool infected {
        get => pb1[2];
        set => pb1[2] = value;
    }

    public bool has_splint {
        get => pb1[3];
        set => pb1[3] = value;
    }

    public bool splint_type {
        get => pb1[4];
        set => pb1[4] = value;
    }

    public bool has_tourniquet {
        get => pb1[5];
        set => pb1[5] = value;
    }

    private byte shrapnel;

    private byte compressednumfurBloodAmount;
    public float furBloodAmount {
        get => compressednumfurBloodAmount / 25.5f;
        set => compressednumfurBloodAmount = (byte)(Mathf.Clamp(value, 0f, 10f) * 25.5f);
    }

    private byte compressednum6;
    public float muscleHealth {
        get => compressednum6 / 2.55f;
        set => compressednum6 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    private byte compressednum7;
    public float skinHealth {
        get => compressednum7 / 2.55f;
        set => compressednum7 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    public float boneHealTimer;
    public float dislocationTimer;

    private byte compressednum9;
    public float infectionAmount {
        get => compressednum9 / 2.55f;
        set => compressednum9 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    private byte compressednum4;
    public float bleedAmount {
        get => compressednum4 / 2.55f;
        set => compressednum4 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    private byte compressednum5;
    public float pain {
        get => compressednum5 / 2.55f;
        set => compressednum5 = (byte)(Mathf.Clamp(value, 0f, 100f) * 2.55f);
    }

    public ushort disinfectionTime;
    private byte bandageSlowAmount;

    public CharacterLimbHealthState(Limb limb) {
        dismembered = limb.dismembered;
#if FULLGAME
        has_prosthetic = limb.HasProsthetic();
        prostheticKind = FullgameWorldCompat.EncodeProsthetic(limb);
#endif
        shrapnel = (byte)limb.shrapnel;
        infected = limb.infected;
        muscleHealth = limb.muscleHealth;
        skinHealth = limb.skinHealth;
        furBloodAmount = limb.furBloodAmount;
        boneHealTimer = limb.broken ? limb.boneHealTimer : 0f;
        dislocationTimer = limb.dislocated ? limb.dislocationTimer : 0f;
        infectionAmount = limb.infectionAmount;
        bleedAmount = limb.bleedAmount;
        pain = limb.pain;
        disinfectionTime = (ushort)Mathf.Clamp(limb.disinfectionTime, 0, ushort.MaxValue);
        bandageSlowAmount = limb.bandageSlowAmount < 0.5f
            ? (byte)0
            : (byte)Mathf.Clamp(Mathf.Ceil(limb.bandageSlowAmount), 0, byte.MaxValue);

        has_splint = limb.splinted;
        if (limb.TryGetComponent<SplintLimb>(out SplintLimb splint)) {
            splint_type = splint.item == "carcasssplint";
        }
        has_tourniquet = limb.TryGetComponent(out TourniquetScript _);
    }

    private static void DropHeld(Limb limb) {
        if (limb == null || limb.body == null) {
            return;
        }
        InventorySlot slot = limb.GetComponentInChildren<InventorySlot>(true);
        if (slot == null || slot.body == null) {
            return;
        }
        MoveCommit.PushApply();
        try {
            slot.body.DropItem(slot.slot);
        } finally {
            MoveCommit.PopApply();
        }
    }

    private static void DropWorn(Limb limb) {
        if (limb == null || limb.body == null) {
            return;
        }
        var parts = TourniquetScript.GetLowerLimbs(limb);
        MoveCommit.PushApply();
        try {
            for (int i = 0; i < parts.Count; i++) {
                Limb part = parts[i];
                if (part == null) {
                    continue;
                }
                var worn = part.GetLimbWearables();
                for (int w = 0; w < worn.Count; w++) {
                    Item item = worn[w];
                    if (item == null) {
                        continue;
                    }
                    limb.body.DropWearable(item);
                    HeldSeat.Detach(item);
                }
            }
        } finally {
            MoveCommit.PopApply();
        }
    }

    public void Apply(Limb limb) {
        if (dismembered && !limb.dismembered && SharedMain.LocalWorldIsGenerated) {
            limb.body.DoGoreSound();
            if (WoundView.view != null && WoundView.view.body == limb.body) {
                WoundView.view.limbLookingAt = (int)LimbIndex.UpperTorso;
            }
            DropHeld(limb);
            DropWorn(limb);
        }

        limb.dismembered = dismembered;
        limb.gameObject.SetActive(!dismembered);

        if (has_splint) {
            SplintLimb splint = limb.GetComponent<SplintLimb>();
            if (splint == null) {
                splint = limb.gameObject.AddComponent<SplintLimb>();
                if (splint_type) {
                    splint.conditionLossMinute = 0.036f;
                    splint.item = "carcasssplint";
                } else {
                    splint.conditionLossMinute = 0.015f;
                    splint.item = "splint";
                }
            }
            splint.condition = 1.0f;
        } else if (limb.TryGetComponent<SplintLimb>(out SplintLimb splint)) {
            Object.Destroy(splint);
        }
        limb.splinted = has_splint;

        if (has_tourniquet) {
            limb.gameObject.GetOrAddComponent<TourniquetScript>();
        } else if (limb.TryGetComponent<TourniquetScript>(out TourniquetScript tourniquet)) {
            tourniquet.affectedLimbs.ForEach(x => {
                if (x != null) {
                    x.blockedBleeding = false;
                }
            });
            Object.Destroy(tourniquet);
        }

#if FULLGAME
        FullgameWorldCompat.ApplyProstheticKind(limb, has_prosthetic ? prostheticKind : (byte)0);
#endif

        if (dislocationTimer > 0f) {
            if (!limb.dislocated) {
                limb.Dislocate();
            }
        } else {
            limb.dislocated = false;
        }

        bool bendInProgress = ProsthesisBendInProgress(limb);
        if (boneHealTimer > 0f) {
            if (!limb.broken) {
                if (ProstheticBreakShouldStayQuiet(limb)) {
                    limb.broken = true;
                } else {
                    limb.BreakBone();
                }
            }
        } else if (!bendInProgress) {
            limb.MendBone();
        }

        if (Net.IsClient && MinigameBase.main != null && MinigameBase.main.currentMinigame != null) {
            Minigame mg = MinigameBase.main.currentMinigame;
            if (mg is ShrapnelMinigame && limb.shrapnel != shrapnel) {
                MinigameMPManager.NoteSlow("health shrapnel by " + limb.shrapnel + " -> " + shrapnel);
            }
            if (mg is DislocationMinigame && Mathf.Abs(limb.dislocationTimer - dislocationTimer) > 8f) {
                MinigameMPManager.NoteSlow("health dislocation by " + Mathf.RoundToInt(limb.dislocationTimer) + " -> " + Mathf.RoundToInt(dislocationTimer));
            }
            if (mg is BandageMinigame && Mathf.Abs(limb.bandageSlowAmount - bandageSlowAmount) > 0.5f) {
                MinigameMPManager.NoteSlow("health bandage by " + limb.bandageSlowAmount.ToString("0.0") + " -> " + bandageSlowAmount);
            }
        }

        limb.furBloodAmount = furBloodAmount;
        limb.shrapnel = shrapnel;
        limb.infected = infected;
        limb.muscleHealth = muscleHealth;
        limb.skinHealth = skinHealth;
        if (!bendInProgress) {
            limb.boneHealTimer = boneHealTimer;
        }
        limb.dislocationTimer = dislocationTimer;
        limb.infectionAmount = infectionAmount;
        limb.bleedAmount = bleedAmount;
        limb.pain = pain;
        limb.disinfectionTime = disinfectionTime;
        limb.bandageSlowAmount = bandageSlowAmount;
    }

    private static bool ProsthesisBendInProgress(Limb limb) {
        if (limb == null || Minigame.game == null || Minigame.game.currentMinigame is not ProsthesisRepairMinigame repair) {
            return false;
        }
        if (ProsthesisRepairLimbField == null || ProsthesisRepairStartedBrokenField == null) {
            return false;
        }
        Limb active = ProsthesisRepairLimbField.GetValue(repair) as Limb;
        if (!ReferenceEquals(active, limb)) {
            return false;
        }
        return ProsthesisRepairStartedBrokenField.GetValue(repair) is bool startedBroken && startedBroken;
    }

    private static bool ProstheticBreakShouldStayQuiet(Limb limb) {
        if (limb == null || !limb.HasProsthetic() || !Net.IsRunning) {
            return false;
        }
        if (limb.IsBodyLocal()) {
            return false;
        }
        if (PlayerCamera.main == null || PlayerCamera.main.body == null) {
            return true;
        }
        return Vector2.Distance(PlayerCamera.main.body.transform.position, limb.transform.position) > 18f;
    }

    public static int GetByteSize() {
        return Marshal.SizeOf(typeof(CharacterLimbHealthState));
    }
}

/// <summary>
/// 角色阿片类止痛药与拮抗剂状态同步包（按内存拷贝序列化，严禁改动字段顺序）。
/// </summary>
public struct CharacterHealthPainkillerStateSyncPacket : INetSerializeByMemcpy {
    public float opiateAmount;
    public float opiateTolerance;
    public float opiateReception;
    public float antagonistAmount;
    public float actualOpiateReception;

    public CharacterHealthPainkillerStateSyncPacket(Body body) {
        Painkillers painkiller = body != null ? body.GetComponent<Painkillers>() : null;
        if (painkiller != null) {
            opiateAmount = painkiller.opiateAmount;
            opiateTolerance = painkiller.opiateTolerance;
            opiateReception = painkiller.opiateReception;
            antagonistAmount = painkiller.antagonistAmount;
            actualOpiateReception = painkiller.actualOpiateReception;
        } else {
            opiateAmount = 0f;
            opiateTolerance = 0f;
            opiateReception = 0f;
            antagonistAmount = 0f;
            actualOpiateReception = 0f;
        }
    }

    public void Apply(Body body) {
        if (body == null) {
            return;
        }
        Painkillers painkiller = body.gameObject.GetOrAddComponent<Painkillers>();
        painkiller.opiateAmount = opiateAmount;
        painkiller.opiateTolerance = opiateTolerance;
        painkiller.opiateReception = opiateReception;
        painkiller.antagonistAmount = antagonistAmount;
        painkiller.actualOpiateReception = actualOpiateReception;
    }
}

/// <summary>
/// 角色三维属性与经验同步包（按内存拷贝序列化，严禁改动字段顺序）。
/// </summary>
public struct CharacterSkillsStateSyncPacket : INetSerializeByMemcpy {
    public ushort skill_exp_STR;
    public ushort skill_exp_RES;
    public ushort skill_exp_INT;
    public ushort skill_STR;
    public ushort skill_RES;
    public ushort skill_INT;

    private static readonly ConditionalWeakTable<Skills, Skills> AppliedSkills = new ConditionalWeakTable<Skills, Skills>();

    public CharacterSkillsStateSyncPacket(Body body) {
        Skills s = body.skills;
        skill_exp_STR = (ushort)s.expSTR;
        skill_exp_RES = (ushort)s.expRES;
        skill_exp_INT = (ushort)s.expINT;
        skill_STR = (ushort)s.STR;
        skill_RES = (ushort)s.RES;
        skill_INT = (ushort)s.INT;
    }

    public void Apply(Body body) {
        Skills s = body != null ? body.skills : null;
        if (s == null) {
            return;
        }
        int wasStr = s.STR;
        int wasRes = s.RES;
        int wasInt = s.INT;
        bool first = FirstApply(s);

        s.STR = skill_STR;
        s.RES = skill_RES;
        s.INT = skill_INT;
        s.expSTR = skill_exp_STR;
        s.expRES = skill_exp_RES;
        s.expINT = skill_exp_INT;
        s.UpdateExpBoundaries();

        if (first || !Util.IsBodyLocal(body)) {
            return;
        }
        Notice(wasStr, s.STR, "strength");
        Notice(wasRes, s.RES, "resilience");
        Notice(wasInt, s.INT, "intelligence");
    }

    private static bool FirstApply(Skills skills) {
        if (skills == null || AppliedSkills.TryGetValue(skills, out _)) {
            return false;
        }
        AppliedSkills.Add(skills, skills);
        return true;
    }

    private static void Notice(int from, int to, string stat) {
        if (to <= from || PlayerCamera.main == null) {
            return;
        }
        PlayerCamera.main.DoAlert(
            Locale.GetOther("skillup")
                .Replace("<>", Locale.GetOther(stat).ToLower())
                .Replace("<1>", from.ToString())
                .Replace("<2>", to.ToString()));
        Sound.Play("music/levelup", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
    }
}
