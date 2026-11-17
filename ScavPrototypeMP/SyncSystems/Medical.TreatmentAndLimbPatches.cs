using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 拦截肢体特殊医疗动作（摘除止血带/夹板、启动取弹片/正骨小游戏）并向服务端同步。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.WoundSpecialAction))]
public static class PlayerCamera_WoundSpecialAction_MultiplayerPatch {
    public static void OG_TakeOffReimplemented(Body healer, TourniquetScript tourniquet) {
        Limb limb = tourniquet.GetComponent<Limb>();
        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            GameObject gameObject = Utils.Create("tourniquet", tourniquet.transform.position, 0);
            gameObject.GetComponent<Item>().condition = tourniquet.condition;
            healer.AutoPickUpItem(gameObject.GetComponent<Item>());
        } else if (healer.IsBodyLocal() && limb != null && limb.TryGetNetBody(out NetBody patient)) {
            patient.SetNetHealthSyncIgnoreTime(0.05f);
        }
        tourniquet.affectedLimbs.ForEach(x => {
            if (x != null) {
                x.blockedBleeding = false;
            }
        });
        Object.Destroy(tourniquet);
    }

    public static void OG_TakeOffReimplemented(Body healer, SplintLimb splint) {
        Limb limb = splint.GetComponent<Limb>();
        if (limb != null) {
            limb.splinted = false;
        }
        if (!Multiplayer.IsNetworkActiveAndIsClient()) {
            GameObject gameObject = Utils.Create(splint.item, splint.transform.position, 0);
            gameObject.GetComponent<Item>().condition = splint.condition;
            healer.AutoPickUpItem(gameObject.GetComponent<Item>());
        } else if (healer.IsBodyLocal() && limb != null && limb.TryGetNetBody(out NetBody patient)) {
            patient.SetNetHealthSyncIgnoreTime(0.05f);
        }
        Object.Destroy(splint);
    }

    public static void OG_WoundSpecialAction(Body healer, Limb targetLimb) {
        if (healer == null || targetLimb == null) {
            return;
        }
        bool healerIsLocal = Util.IsBodyLocal(healer);

        if (healer.conscious) {
            if (targetLimb.TryGetComponent(out TourniquetScript tourniquet)) {
                OG_TakeOffReimplemented(healer, tourniquet);
            } else if (targetLimb.hasShrapnel && healerIsLocal) {
                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;
                MinigameBase.main.StartMinigame(new ShrapnelMinigame(targetLimb), null);
            }
            if (targetLimb.TryGetComponent(out SplintLimb splint)) {
                OG_TakeOffReimplemented(healer, splint);
            } else if (targetLimb.dislocated && healerIsLocal) {
                MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;
                MinigameBase.main.StartMinigame(new DislocationMinigame(targetLimb), null);
            }
        }

        if (targetLimb.TryGetNetBody(out NetBody nbPatient) && healerIsLocal) {
            nbPatient.SetNetHealthSyncIgnoreTime(0.01f);
        }
    }

    public static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return true;
        }

        if (!__instance.body.allowUseItem) {
            PlayerCamera.main.UseFailUnhappiness();
            return false;
        }
        if (!__instance.body.conscious || __instance.selectedLimb == null) {
            return false;
        }

        if (!Util.DoFullInteractionCheck(__instance.body, __instance.selectedLimb.body, true)) {
            PlayerCamera.main.DoAlert(Lang.Get("plr_unreachable"));
            PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            return false;
        }

        if (__instance.selectedLimb.body.TryGetNetBody(out NetBody pb)) {
            MpCommsTrace.ClientSend(LogDomain.Medical, (byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction, detail: "WoundSpecialAction");
            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_WoundSpecialAction);
            writer.Put(new LimbNetId(__instance.selectedLimb));
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);

            if (Net.IsClient) {
                pb.SetNetHealthSyncIgnoreTime(0.05f);
            }
        }

        OG_WoundSpecialAction(__instance.body, __instance.selectedLimb);
        return false;
    }
}

/// <summary>
/// 拦截对肢体使用医疗/义肢/工具物品，校验抑郁拒诊与义肢可用性，并在协同治疗时借用施救者更高智力属性。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ApplyWoundItem))]
public static class PlayerCamera_ApplyWoundItem_MultiplayerPatch {
    public static Body last_wounditem_user;
    public static Limb applying_on_limb = null;
    public static Item applying_item = null;

    private static Skills _pendingPatientSkills;
    private static int _pendingSavedInt = -1;

    public static bool DoApplyWoundItemChecks(Body healer, Limb limb, Item item) {
        if (healer == null || limb == null || item == null) {
            return false;
        }

        bool healerIsMe = Util.IsBodyLocal(healer);
        Body otherBody = limb.body;

        if (limb.dismembered || !healer.conscious || !item.Stats.ActuallyUsableOnLimb(item)) {
            return false;
        }

        // 普通药物在义肢上为空操作但会白耗耐久；仅放行截肢工具、义肢安装件、修复耗材与扳手
        if (limb.HasProsthetic()
            && item.Stats.usableOnLimb
            && !Item_DoAmputate_MultiplayerPatch.IsAmputateTool(item)
            && !IsAllowedOnProsthetic(item.id)) {
            return false;
        }

        if (otherBody != healer) {
            PlayerCamera pc = PlayerCamera.main;
            if (!otherBody.alive) {
                return true;
            }
            if (healer.aboveMedicalCutoff) {
                if (MedicalSync.IsRefusingHelp(otherBody) && !item.Stats.ignoreDepression) {
                    if (healerIsMe && pc != null) {
                        pc.DoAlert(Lang.Get("deny_refuse"));
                        pc.PlayUISound(PlayerCamera.UISoundType.Deny);
                    }
                    return false;
                }
                return true;
            }
            if (healerIsMe && pc != null) {
                pc.UseFailUnhappiness();
            }
            return false;
        }

        if (!healer.aboveMedicalCutoff && !item.Stats.ignoreDepression) {
            if (healerIsMe && PlayerCamera.main != null) {
                PlayerCamera.main.DoAlert(Locale.GetOther("alerttoounhappy"));
            }
            return false;
        }
        return true;
    }

    private static bool IsAllowedOnProsthetic(string itemId) {
        return itemId is "peglimb" or "steelprosthesis" or "peo"
            or "scrapmetal" or "scrapcube" or "scrappanel" or "scraptube"
            or "woodscraps" or "woodcube" or "woodpanel" or "stick"
            or "titaniumslab" or "titaniumsheet" or "titaniumrod"
            or "wrench" or "makeshiftwrench";
    }

    public static void ForceApplyWoundItem(Body healer, Limb limb, Item item) {
        last_wounditem_user = healer;

        limb.GetCoUtilsInstance().SelectForMPOverride();
        Limb previousLimb = applying_on_limb;
        Item previousItem = applying_item;
        bool healerIsHere = healer != null && Util.IsBodyLocal(healer);
        if (healerIsHere) {
            applying_on_limb = limb;
            applying_item = item;
        }

        // 原版义肢安装读取患者自身 INT；为队友安装时临时取施救者与患者的较高 INT
        int savedInt = -1;
        Skills patientSkills = null;
        if (healer != null && limb != null && limb.body != null && healer != limb.body
            && healer.skills != null && limb.body.skills != null) {
            patientSkills = limb.body.skills;
            savedInt = patientSkills.INT;
            if (healer.skills.INT > patientSkills.INT) {
                patientSkills.INT = healer.skills.INT;
            }
        }

        try {
            if (item.Stats.usableOnLimb) {
                item.Stats.useLimbAction(limb, item);
            } else if (item.TryGetComponent(out WaterContainerItem waterContainer)) {
                waterContainer.ApplyToLimb(limb);
            }
        } finally {
            if (healerIsHere) {
                applying_on_limb = previousLimb;
                applying_item = previousItem;
            }
            if (patientSkills != null && savedInt >= 0) {
                patientSkills.INT = savedInt;
            }
        }
    }

    private static bool Prefix(PlayerCamera __instance, Item item) {
        applying_on_limb = __instance.selectedLimb;
        applying_item = item;

        if (Net.IsRunning) {
            if (!ItemSync.CheckIfBodyReachThisItem(item, __instance.body, GameplayRanges.ServerExchangeToleranceDistance, true)) {
                PlayerCamera.main.DoAlert(Lang.Get("item_unreachable"));
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
                return false;
            }

            if (DoApplyWoundItemChecks(__instance.body, __instance.selectedLimb, item)) {
                if (ItemSync.TryGetItemSyncInfo(item, out SyncInfo isi)) {
                    MpCommsTrace.ClientSend(
                        LogDomain.Medical,
                        (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem,
                        isi.syncId.id,
                        detail: $"limb={Util.GetLimbIndex(__instance.selectedLimb)}");

                    if (Net.IsClient) {
                        isi.SetIgnoreTimeForRoundTrip();
                    }

                    NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_ApplyWoundItem);
                    writer.Put(new LimbNetId(__instance.selectedLimb));
                    writer.Put(isi.syncId);
                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                    if (Net.IsServer) {
                        return false;
                    }
                }
            } else {
                return false;
            }
        }

        last_wounditem_user = Util.GetLocalBody();
        if (__instance.selectedLimb != null && __instance.body != null && __instance.selectedLimb.body != __instance.body) {
            Body healer = __instance.body;
            Body patient = __instance.selectedLimb.body;
            if (healer.skills != null && patient.skills != null && healer.skills.INT > patient.skills.INT) {
                _pendingPatientSkills = patient.skills;
                _pendingSavedInt = patient.skills.INT;
                patient.skills.INT = healer.skills.INT;
            }
        }
        return true;
    }

    private static void Postfix() {
        applying_on_limb = null;
        applying_item = null;
        if (_pendingPatientSkills != null && _pendingSavedInt >= 0) {
            _pendingPatientSkills.INT = _pendingSavedInt;
            _pendingPatientSkills = null;
            _pendingSavedInt = -1;
        }
    }
}

/// <summary>
/// 血液流干或止血后强制归零出血粒子发射率，消除远端残留喷血粒子。
/// </summary>
[HarmonyPatch(typeof(Limb), nameof(Limb.FurBloodUpdate))]
public static class Limb_FurBloodUpdate_MultiplayerPatch {
    private static void Postfix(Limb __instance) {
        if (__instance.body.bloodVolume <= 0f || __instance.body.totalBleedSpeed <= 0f) {
            foreach (ParticleSystem ps in __instance.GetComponentsInChildren<ParticleSystem>()) {
                if (ps.name.Contains("BleedParticle")) {
                    var emission = ps.emission;
                    emission.rateOverTime = 0f;
                }
            }
        }
    }
}

[HarmonyPatch(typeof(Limb), nameof(Limb.Dislocate))]
public static class Limb_Dislocate_MultiplayerPatch {
    public static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase __originalMethod) {
        return TranspilerHelpers.PatchShakers(instructions, generator, __originalMethod);
    }
}

/// <summary>
/// 防止已断离的头部在 <see cref="Limb.BreakBone"/> 随机触发毁容时产生异常，并隔离远端屏幕震动。
/// </summary>
[HarmonyPatch(typeof(Limb), nameof(Limb.BreakBone))]
public static class Limb_BreakBone_MultiplayerPatch {
    private static bool Prefix(Limb __instance) {
        return !__instance.dismembered;
    }

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase __originalMethod) {
        return TranspilerHelpers.PatchShakers(instructions, generator, __originalMethod);
    }
}

/// <summary>
/// 屏蔽蜘蛛虫群刚体碰撞骨折误判，并记录当前碰撞来源供冲击伤害上报服务端。
/// </summary>
[HarmonyPatch(typeof(Limb), nameof(Limb.OnCollisionEnter2D))]
public static class Limb_OnCollisionEnter2D_MultiplayerPatch {
    public static Collision2D CurrentCollision = null;

    private static bool Prefix(Limb __instance, Collision2D col) {
        if (!Net.IsRunning) {
            return true;
        }
        if (col.rigidbody != null && col.rigidbody.TryGetComponent(out SpiderHandler _)) {
            return false;
        }
        CurrentCollision = col;

        if (__instance.IsBodyLocal()) {
            GameObject go = col.gameObject;
            if (go != null && go.TryGetTracker(out SyncInfoGameObjectTracker tracker) && tracker.syncInfo.IsContactDangerous()) {
                tracker.syncInfo.relaxed_combat_sync = true;
            }
        }
        return true;
    }

    private static void Postfix() {
        CurrentCollision = null;
    }
}

/// <summary>
/// 同步本地玩家物理跌落/撞击伤害到服务端，并在远端角色身上仅保留跌落音效与毛发飞溅表现。
/// </summary>
[HarmonyPatch(typeof(Limb), nameof(Limb.ImpactDamage))]
public static class Limb_ImpactDamage_MultiplayerPatch {
    private static bool _forceRun = false;
    public static bool last_impact_is_local_body = false;

    private static IEnumerable<CodeInstruction> Transpiler(
        IEnumerable<CodeInstruction> instructions,
        ILGenerator generator,
        MethodBase __originalMethod) {
        return TranspilerHelpers.PatchShakers(instructions, generator, __originalMethod);
    }

    public static void Force(Limb limb, float force, Vector2 dir) {
        _forceRun = true;
        try {
            limb.ImpactDamage(force, dir);
        } finally {
            _forceRun = false;
        }
    }

    private static bool Prefix(Limb __instance, ref float force, ref Vector2 dir) {
        last_impact_is_local_body = __instance.IsBodyLocal();
        Body body = __instance.body;

        if (!last_impact_is_local_body) {
            body.fallShakeCooldown = 1f;
        }
        if (SharedMain.LocalWorldIsGenerating) {
            return false;
        }
        if (!Net.IsRunning) {
            return true;
        }

        if (!_forceRun) {
            if (__instance.TryGetNetBody(out NetBody netBody)) {
                if (netBody.piggybacking_on != null) {
                    return false;
                }
                if (!netBody.IsPlayer) {
                    netBody.RollDamageRandom();
                    return true;
                }
            }

            if (!last_impact_is_local_body) {
                if (Net.IsServer) {
                    return false;
                }
                if (!(force > body.jumpSpeed)) {
                    return false;
                }

                if (force > body.jumpSpeed * 1.66f) {
                    body.DoFurTuft();
                }
                if (force > Random.Range(body.jumpSpeed * 1.55f, body.jumpSpeed * 4.4f) * (__instance.muscleHealth < 50f ? 0.632f : 1f)) {
                    if (force > body.jumpSpeed * 2.6f) {
                        body.DoGoreSound();
                    }
                }
                Vector2 pos = (Vector2)__instance.transform.position + dir;
                ushort block = WorldGeneration.world.GetBlock(pos);
                if (block > 0) {
                    Sound.Play(WorldGeneration.world.RandomStepSound(WorldGeneration.world.GetBlockInfo(block).stepsound), pos);
                }
                if (force > 3f && body.soundCooldown <= 0f) {
                    body.soundCooldown = 0.25f;
                    Sound.Play($"bodyFall{Random.Range(1, 6)}", __instance.transform.position);
                }
                return false;
            }
        }

        if (__instance.TryGetNetBody(out NetBody nb)) {
            nb.RollDamageRandom();
        }
        return true;
    }

    private static void Postfix(Limb __instance, ref float force, ref Vector2 dir) {
        if (!Net.IsRunning) {
            return;
        }

        if (!_forceRun && __instance.IsBodyLocal() && Net.IsClient && Util.IsWorldGenerated()) {
            Body body = __instance.body;
            if (!(force > body.jumpSpeed)) {
                return;
            }

            if (body.TryGetNetBody(out NetBody nb)) {
                nb.SetNetHealthSyncIgnoreTime();
                if (nb.piggybacking_on != null) {
                    return;
                }
            }

            NetDataWriter writer = Net.CreateWriter(NetmsgId.PlayerImpactDamage);
            writer.Put((byte)Util.GetLimbIndex(__instance));
            writer.Put(force);
            writer.Put(dir);
            GameObject hitFrom = Limb_OnCollisionEnter2D_MultiplayerPatch.CurrentCollision != null
                ? Limb_OnCollisionEnter2D_MultiplayerPatch.CurrentCollision.gameObject
                : null;
            writer.Put(AnyObjectNetId.GetNetIdFromGameObject(hitFrom));
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            MpCommsTrace.ClientSend(LogDomain.Medical, (byte)NetmsgId.PlayerImpactDamage, detail: $"force={force:F1}");
        }
        _forceRun = false;
    }
}
