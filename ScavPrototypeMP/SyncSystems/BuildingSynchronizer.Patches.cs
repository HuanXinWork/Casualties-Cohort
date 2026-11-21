using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;

namespace Together;

[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Backgroundify))]
public static class BuildingEntity_Backgroundify_MultiplayerPatch {
    static void Prefix(BuildingEntity __instance) {
        if (!Net.IsRunning)
            return;
        var tr = __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
        tr.is_backgroundified = true;
    }
}

[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.CheckSeating))]
internal static class BuildingEntity_CheckSeating_MultiplayerPatch {
    static bool Prefix() {
        return !(Net.IsRunning && Net.IsClient);
    }
}

/// <summary>
/// 拦截 <see cref="BuildingEntity.Update"/>：客户端禁止自行结算掉落物，仅播放单次破坏粒子与音效；物理休眠范围对齐联机模拟区。
/// </summary>
[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Update))]
public static class BuildingEntity_Update_MultiplayerPatch {
    static bool Prefix(BuildingEntity __instance) {
        if (Net.IsRunning && Net.IsClient && __instance.GetHealth() < 0.5f) {
            PlayDeathOnce(__instance);
            return false;
        }
        // 联机模式下在 Prefix 直接接管刚体休眠状态：若落入原版 Update，房主视野外区块渲染关闭会先置 Static 清零下落速度，导致远端掉落物卡在空中
        if (Net.IsRunning && __instance != null && __instance.TryGetComponent(out Rigidbody2D rb) && !__instance.ignoreBodyOptimize) {
            BuildingOptimization(rb);
            return false;
        }
        return true;
    }

    internal static void PlayDeathOnce(BuildingEntity be) {
        if (be == null || be.GetComponent<ClientDeathFxPlayed>() != null)
            return;
        be.gameObject.AddComponent<ClientDeathFxPlayed>();

        if (be.TryGetComponent(out SpriteRenderer sprite) && sprite.sprite != null) {
            var broken = Object.Instantiate(Resources.Load("BuildingBreakParticle"), be.transform.position, be.transform.rotation) as GameObject;
            if (broken != null && broken.TryGetComponent(out ParticleSystem ps)) {
                var shape = ps.shape;
                shape.texture = sprite.sprite.texture;
                shape.sprite = sprite.sprite;
                ps.Play();
            }
        }
        Object.Instantiate(Resources.Load<GameObject>("DustBig"), be.transform.position, Quaternion.identity);
        PlayAnimalDeathFx(be);
        Sound.Play("footstep/Rock/11", be.transform.position);
    }

    static void PlayAnimalDeathFx(BuildingEntity be) {
        if (be.TryGetComponent(out CrystalEnemy _)) {
            Sound.Play("crystalenemydeath", be.transform.position, false, false);
            GameObject distort = Utils.Create("Special/CrystalDistort", be.transform.position, 0f);
            if (distort != null)
                distort.AddComponent<CrystalDeathAnimation>();
            return;
        }
        if (be.TryGetComponent(out SpiderHandler spider)) {
            Sound.Play(VanillaAssetKeys.SoundGore, be.transform.position);
            if (spider.doDeathExplode)
                Object.Instantiate(Resources.Load("BloodExplosion"), be.transform.position, Quaternion.identity);
            return;
        }
        if (be.TryGetComponent(out TraderScript trader)) {
            Vector3 pos = trader.torso != null ? trader.torso.position : be.transform.position;
            Sound.Play(VanillaAssetKeys.SoundGore, pos);
            Object.Instantiate(Resources.Load("BloodExplosion"), pos, Quaternion.identity);
        }
    }

    static void Postfix(BuildingEntity __instance) {
        if (!Net.IsRunning)
            return;
        if (__instance != null)
            ChunkSleep.Watch(__instance);
    }

    static void BuildingOptimization(Rigidbody2D rb) {
        if (!WorldGeneration.world.worldExists || !(Time.timeScale <= 5f)) {
            if (rb.bodyType != RigidbodyType2D.Static)
                rb.bodyType = RigidbodyType2D.Static;
            return;
        }
        bool inSimRange = SharedMain.WorldInSimRange(rb.position);
        RigidbodyType2D want = inSimRange ? RigidbodyType2D.Dynamic : RigidbodyType2D.Static;
        if (rb.bodyType != want)
            rb.bodyType = want;
        // 曾进入 Static 帧的刚体可能残留 simulated=false，恢复 Dynamic 时需同步唤醒物理模拟以保证正常自由落体
        if (inSimRange && !rb.simulated)
            rb.simulated = true;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return FullgameBodyOptimizeSkipPatch.Rewrite(instructions);
    }
}

#if FULLGAME
[HarmonyPatch(typeof(BuildingEntity), "CheckDamage")]
public static class BuildingEntity_CheckDamage_MultiplayerPatch {
    static bool Prefix(BuildingEntity __instance) {
        if (Net.IsRunning && Net.IsClient) {
            if (__instance.GetHealth() < 0.5f) {
                BuildingEntity_Update_MultiplayerPatch.PlayDeathOnce(__instance);
            }
            return false;
        }
        return true;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        return FullgameBodyOptimizeSkipPatch.Rewrite(instructions);
    }
}
#endif

internal static class FullgameBodyOptimizeSkipPatch {
    internal static bool ShouldMarkFreshDrop(BuildingEntity self) {
        if (Net.IsRunning) {
            return true;
        }
        return self != null && PlayerCamera.main != null && PlayerCamera.main.body != null
            && Vector2.Distance(self.transform.position, PlayerCamera.main.body.transform.position) < 8f;
    }

    internal static IEnumerable<CodeInstruction> Rewrite(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);

        int ldstr = -1;
        for (int k = 0; k < codes.Count; k++) {
            if (codes[k].opcode == OpCodes.Ldstr && codes[k].operand as string == "footstep/Rock/11") {
                ldstr = k;
                break;
            }
        }

        if (ldstr >= 0) {
            int stloc = -1;
            for (int k = ldstr; k < codes.Count; k++) {
                if (codes[k].opcode == OpCodes.Stloc_1) {
                    stloc = k;
                    break;
                }
            }
            if (stloc >= 0) {
                int beginning = stloc;
                while (beginning >= 0 && codes[beginning].opcode != OpCodes.Ldarg_0) {
                    beginning--;
                }
                if (beginning >= 0) {
                    codes.RemoveRange(beginning + 1, stloc - 1 - beginning);
                    codes.Insert(beginning + 1, new CodeInstruction(
                        OpCodes.Call,
                        AccessTools.Method(typeof(FullgameBodyOptimizeSkipPatch), nameof(ShouldMarkFreshDrop))));
                }
            }
        }

        int bodyCheck = -1;
        for (int k = 0; k < codes.Count; k++) {
            if (codes[k].opcode == OpCodes.Ldfld && codes[k].operand != null && codes[k].operand.ToString().Contains("ignoreBodyOptimize")) {
                bodyCheck = k;
                break;
            }
        }

        if (bodyCheck >= 0 && bodyCheck + 1 < codes.Count &&
            (codes[bodyCheck + 1].opcode == OpCodes.Brtrue || codes[bodyCheck + 1].opcode == OpCodes.Brtrue_S)) {
            int brtrue = bodyCheck + 1;
            Label endLabel = (Label)codes[brtrue].operand;
            // 仅在联机运行时跳过原版单人刚体休眠判定（交由 Postfix 的 ChunkSleep 与 WorldInSimRange 接管），单机保留原版行为
            codes.InsertRange(brtrue + 1, new[] {
                new CodeInstruction(OpCodes.Call, AccessTools.PropertyGetter(typeof(Net), nameof(Net.IsRunning))),
                new CodeInstruction(OpCodes.Brtrue, endLabel),
            });
        }

        return codes;
    }
}

sealed class ClientDeathFxPlayed : MonoBehaviour {
}

/// <summary>
/// 拦截 <see cref="BuildingEntity.Start"/>，按实体组件自动挂载对应的联机同步追踪器并纳入 <see cref="NetObjectRegistry"/>。
/// </summary>
[HarmonyPatch(typeof(BuildingEntity), nameof(BuildingEntity.Start))]
public static class BuildingEntity_Start_MultiplayerPatch {
    public const string identified_bg_prefix = "KMPSR_";

    static void Prefix(BuildingEntity __instance) {
        if (!Net.IsRunning)
            return;
        __instance.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
    }

    static void Postfix(BuildingEntity __instance) {
        if (!Net.IsRunning)
            return;

        // 结构空调位于建筑子层级而非根节点，递归遍历所有子节点挂载 HeaterMultiplayerRewrite
        if (!AttachHeaterRewrites(__instance)) {
            __instance.StartCoroutine(AttachHeaterRewriteNextFrame(__instance));
        }

        if (__instance.TryGetComponent(out TrackerClimbableEntity ropeTracker) && !string.IsNullOrEmpty(ropeTracker.og_id)) {
            __instance.fullName = Locale.GetBuilding(ropeTracker.og_id);
            if (!__instance.skipDescriptionSet) {
                __instance.description = Locale.GetBuilding(ropeTracker.og_id + "dsc");
            }
        } else if (__instance.TryGetComponent(out TraderScript trader)) {
            __instance.id = "trader" + (trader.character + 1);
        } else if (__instance.TryGetComponent(out SoundCannon _)) {
            __instance.GetOrAddComponent<TrackerSoundCannon>();
        } else if (__instance.TryGetComponent(out TurretScript _)) {
            __instance.GetOrAddComponent<TrackerTurret>();
        } else if (__instance.TryGetComponent(out CorpseScript _)) {
            __instance.GetOrAddComponent<CorpseScriptMultiplayerRewrite>();
        } else if (__instance.TryGetComponent(out GunmineScript gunmine)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = gunmine;
        } else if (__instance.TryGetComponent(out StalactiteDropper dropper)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = dropper;
#if FULLGAME
        } else if (__instance.TryGetComponent(out LaserDroneScript cryoDrone)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = cryoDrone;
        } else if (__instance.TryGetComponent(out BloodSuckerScript bloodSucker)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = bloodSucker;
        } else if (__instance.TryGetComponent(out FanTrap fanTrap)) {
            var owro = __instance.GetOrAddComponent<OnWillRenderObject_ForceForMPComponent>();
            owro.target = fanTrap;
        } else if (__instance.id == "macrofleshrebuilder" && __instance.GetComponent<SurgicalDeviceBehavior>() == null) {
            __instance.gameObject.AddComponent<SurgicalDeviceBehavior>();
#endif
        } else if (__instance.TryGetComponent(out SpiderHandler spider)) {
            spider.GetOrAddComponent<TrackerSpiderHandler>();
        } else if (__instance.id == "background") {
            ModLog.Error($"Unknown background entity: {__instance} id=({__instance.id}) at {(Vector2)__instance.transform.position}", LogDomain.World);
        } else if (__instance.id == "shuttledoor" && __instance.transform.parent?.TryGetComponent(out ShuttleStartOpen _) == true) {
            __instance.GetOrAddComponent<KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE>();
            return;
        }

        if (WorldGeneration.world && !WorldGeneration.world.generatingWorld && __instance.gameObject.activeInHierarchy && NetObjectRegistry.ObjectShouldBeSynced(__instance.gameObject)) {
            if (SharedMain.WorldInSimRange(__instance.transform.position))
                NetObjectRegistry.NewGO(__instance.gameObject);
        }
    }

    static bool AttachHeaterRewrites(BuildingEntity building) {
        if (building == null)
            return false;
        Heater[] heaters = building.GetComponentsInChildren<Heater>(true);
        bool any = false;
        for (int i = 0; i < heaters.Length; i++) {
            Heater heater = heaters[i];
            if (heater == null)
                continue;
            any = true;
            heater.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }
        return any;
    }

    static IEnumerator AttachHeaterRewriteNextFrame(BuildingEntity building) {
        yield return null;
        AttachHeaterRewrites(building);
    }
}

[HarmonyPatch(typeof(SpiderHandler), nameof(SpiderHandler.PlayThreatMusic))]
static class SpiderHandler_PlayThreatMusic_MultiplayerPatch {
    static bool Prefix(SpiderHandler __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        return Camera.main != null && KM.DistanceCheck(Camera.main.transform.position, __instance.transform.position, __instance.seeDistance);
    }
}

/// <summary>
/// 蜘蛛撕咬判定与伤害同步补丁：清醒状态下由受击客户端本机判定碰撞后上报服务端，昏迷躯体由服务端直接结算；长老蜘蛛随机断肢仅由服务端权威执行。
/// </summary>
[HarmonyPatch(typeof(SpiderHandler), nameof(SpiderHandler.CheckForLimbDamage))]
public static class SpiderHandler_CheckForLimbDamage_MultiplayerPatch {
    static bool Prefix(SpiderHandler __instance, Collision2D collision) {
        if (!Net.IsRunning) {
            return true;
        }
        if (NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out var si)) {
            Limb limb = Body.LimbFromObject(collision.gameObject, __instance.transform.position);
            if (limb != null && limb.TryGetNetBody(out var pb)) {
                if (!pb.IsBodyLocal()) {
                    if (pb.body.conscious) {
                        return false;
                    }
                } else if (Net.IsClient && pb.body.conscious && __instance.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) {
                    if (Vector2.Dot(((Vector2)collision.transform.position - collision.GetContact(0).point).normalized, __instance.transform.up) > __instance.minVectorDotToBite) {
                        ModLog.Flow(LogDomain.Combat, "SpiderBite.ClientReport", $"si={si} limb={limb.name}");
                        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_SpiderJustBitMeFuck, tracker.syncId, Util.GetLimbIndex(limb), false);
                        pb.SetNetHealthSyncIgnoreTime();
                    }
                }

                pb.RollDamageRandom();
                pb.OnDamageEvent(new BodyDamageEvent(__instance.bld, pb, __instance.biteMuscleDamage));
            }
        }
        return true;
    }

    public static void Spider_BiteDamage(SpiderHandler spider, Limb limb) {
        var spiderTracker = spider.GetOrAddComponent<TrackerSpiderHandler>();

        if (limb.TryGetNetBody(out var nb)) {
            nb.RollDamageRandom();
            nb.OnDamageEvent(new BodyDamageEvent(spider.bld, nb, spider.biteMuscleDamage));
        }

        spider.biteCooldown = spider is SpiderHandlerTBE ? 5f : spider.biteCoolToSet;
        limb.body.happiness -= spider.happinessLoss;
        spider.target = (spider.transform.position - limb.transform.position).normalized * 15f + spider.transform.position;
        spider.moveTime = spider.retreatMoveTime;
        spider.DamageLimb(limb);
        if (spider.hitConnected) {
            foreach (Limb connected in limb.connectedLimbs) {
                spider.DamageLimb(connected);
            }
        }

        if (Net.IsServer && nb != null && spiderTracker.tracker) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_SpiderAttackEffect);
            writer.Put(nb.netId);
            writer.Put(spiderTracker.tracker.syncId);
            writer.Put(spider.transform.position);
            writer.Put(spider.transform.eulerAngles.z);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThis(nb.netId));
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SpiderJustBitMeFuck)]
    static void ReceiveSpiderBiteReport(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid spiderSyncId);
        reader.Get(out byte limbId);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) &&
            body.conscious &&
            limbId < body.limbs.Count() &&
            NetObjectRegistry.TryGetSyncInfo(spiderSyncId, out var si) &&
            si.IsSpider()) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(plr.body.transform.position, "S: SpiderBiteReport");
            Spider_BiteDamage(si.spider, body.limbs[limbId]);
        }
    }

    [HarmonyPatch(typeof(SpiderHandlerTBE), nameof(SpiderHandlerTBE.DamageLimb))]
    static class SpiderHandlerTBE_DamageLimb_MultiplayerPatch {
        static bool Prefix() {
            return !Net.IsRunning || Net.IsServer;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_SpiderAttackEffect, false)]
    static void ReceiveSpiderAttackEffect(knetid _, NetDataReader reader) {
        reader.Get(out knetid targetClientId);
        reader.Get(out knetid spiderSyncId);
        reader.Get(out Vector2 _);
        reader.Get(out float angle);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(targetClientId, out var plr, out var body) &&
            NetObjectRegistry.TryGetSyncInfo(spiderSyncId, out var si) &&
            si.IsSpider()) {
            SpiderHandler spider = si.spider;
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(plr.body.transform.position, "C: SpiderAttackEffect");

            Sound.Play(spider.biteSound, spider.transform.position);
            body.eyeScareTime = 5f;
            spider.PlayThreatMusic();
            if (spider.clawAnim) {
                GameObject obj = Object.Instantiate(Resources.Load<GameObject>("ClawAnim"));
                obj.transform.eulerAngles = new Vector3(0f, 0f, angle);
                obj.transform.position = spider.transform.position;
                obj.transform.SetParent(spider.transform);
                Object.Destroy(obj, 5f);
            }
        }
    }
}

public class TrackerSpiderHandler : BaseComponentTracker<SpiderHandler> {
    private const float ExpShareRadius = 32f;

    protected override void TrackerAwake() {
    }

    void LateUpdate() {
        if (SharedMain.WorldInSimRange(transform.position)) {
            if (!TryGetComponent(out SyncInfoGameObjectTracker _) && gameObject != null) {
                NetObjectRegistry.NewGO(gameObject);
            }
        }
    }

    public void AnimalDeath() {
        foreach (NetBody item in NetBody.GetBodiesInRadius(og.transform.position, ExpShareRadius)) {
            if (!Util.IsBodyLocal(item.body)) {
                item.body.skills.AddExp(1, og.maxHealth / 100f);
            }
        }
    }
}

internal class ScavMPBuildingSynchronizer : CasualtiesTogetherSingleton {
    public static bool BuildingHasActivePhysics(BuildingEntity building) {
        if (building.TryGetComponent(out Rigidbody2D rb) && rb.bodyType == RigidbodyType2D.Dynamic)
            return true;
        if (building.TryGetComponent(out GrabberPlant _))
            return true;
        return false;
    }

    public static bool BuildingHasActivePhysics(SyncInfo si) {
        return BuildingHasActivePhysics(si.building);
    }

    public static Dictionary<string, GameObject> known_entities_with_nonunique_id = new();
    public static Dictionary<string, string> TEMP_known_entities_with_nonunique_id_to_og = new();

    internal static int cur_buildings_check_index = 0;
    internal static List<BuildingEntity> cur_buildings_check = new List<BuildingEntity>();

    public static void RegisterNonUniqueIdBg(Transform ch, bool report_fail) {
        if (ch.TryGetComponent(out BuildingEntity build) && ch.TryGetComponent(out Tilemap _)) {
            bool needsRename = string.IsNullOrEmpty(build.id) || build.id == "background";
            if (needsRename) {
                if (ch.parent == null) {
                    ModLog.Error($"RegisterNonUniqueIdBg: tilemap building has no parent (id='{build.id}' name={ch.name})", LogDomain.World);
                    return;
                }

                string ogResName = ch.parent.name;
                string newName = BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix + ogResName;
                ModLog.Flow(LogDomain.World, "RegisterNonUniqueIdBg", $"id={build.id} parent={ch.parent} renamed={newName}");

                build.id = newName;
                known_entities_with_nonunique_id[build.id] = build.gameObject;
                TEMP_known_entities_with_nonunique_id_to_og[build.id] = ogResName;

                var bgTracker = build.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
                bgTracker.is_a_background_tilemap = true;
                build.name = build.id;
                return;
            }
            if (!build.id.StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix)) {
                ModLog.Error($"Unexpected building tilemap id='{build.id}' name={ch.name} parent={ch.parent?.name}", LogDomain.World);
                return;
            }
        }
        if (report_fail) {
            ModLog.Warning($"RegisterNonUniqueIdBg: unrecognized target '{ch?.name}'", LogDomain.World);
        }
    }
}

// 标记关键建筑在全体玩家远离后仍保留于网络快照中，确保中途加入的客机也能收到
internal sealed class KeepForJoiners : MonoBehaviour {
}

#if FULLGAME
internal static class LayerBuildingStream {
    internal static void HostRegister() {
        if (!Net.IsRunning || !Net.IsServer)
            return;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists)
            return;

        CeilingTurret[] guns = Object.FindObjectsOfType<CeilingTurret>(true);
        for (int i = 0; i < guns.Length; i++) {
            if (guns[i] == null)
                continue;
            BuildingEntity gunBuild = guns[i].GetComponent<BuildingEntity>();
            if (gunBuild != null && string.IsNullOrEmpty(gunBuild.id))
                gunBuild.id = "ceilingturret";
            Try(gunBuild, true);
        }

        BloodSuckerScript[] dens = Object.FindObjectsOfType<BloodSuckerScript>(true);
        for (int i = 0; i < dens.Length; i++) {
            BuildingEntity den = BuildingOf(dens[i]);
            if (den != null && string.IsNullOrEmpty(den.id))
                den.id = "bloodsuckerden";
            Try(den, true);
        }

        BlueHeart[] hearts = Object.FindObjectsOfType<BlueHeart>(true);
        for (int i = 0; i < hearts.Length; i++)
            Try(BuildingOf(hearts[i]), false);
    }

    static BuildingEntity BuildingOf(Component part) {
        if (part == null)
            return null;
        BuildingEntity build = part.GetComponent<BuildingEntity>();
        if (build == null)
            build = part.GetComponentInParent<BuildingEntity>();
        return build;
    }

    static void Try(BuildingEntity build, bool nearOnly) {
        if (build == null || build.gameObject == null)
            return;
        if (string.IsNullOrEmpty(build.id))
            return;
        if (NetObjectRegistry.ObjectShouldNotBeSynced(build.gameObject))
            return;
        if (!nearOnly)
            build.gameObject.GetOrAddComponent<KeepForJoiners>();
        if (NetObjectRegistry.IsRegistered(build.gameObject))
            return;
        if (nearOnly && !SharedMain.WorldInSimRange(build.transform.position))
            return;
        NetObjectRegistry.NewGO(build.gameObject);
    }
}
#endif
