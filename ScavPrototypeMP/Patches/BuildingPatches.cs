using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using System.Collections;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Text;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.UI;
using Random = UnityEngine.Random;

namespace Together;




[HarmonyPatch(typeof(OilPipeScript), nameof(OilPipeScript.Start))]
static class OilPipeScript_Start_MultiplayerPatch {
    static void Postfix(OilPipeScript __instance) {
        if (!Net.IsRunning) {
            return;
        }
        __instance.lastOilTime = Time.time + UnityEngine.Random.Range(1f, 30f);
        __instance.coolTime *= (Mathf.Max(1, ScavPlayer.ClientIdToPlayerDict.Count) + 0.5f);
    }
}

[HarmonyPatch(typeof(OilPipeScript), nameof(OilPipeScript.Update))]
static class OilPipeScript_Update_MultiplayerPatch {
    static bool Prefix(OilPipeScript __instance) {
        // stock indexes fluid[] with no check. during a tilemap rebuild that array is null
        // or the cell is off the grid, and the pump throws instead of waiting.
        if (!PumpReady(__instance))
            return false;
        if (!Net.IsRunning)
            return true;
        // the lifepod pump is not a synced object. each machine runs its own.
        // if the host skips it, the next fluid chunk is empty and the client's pool vanishes.
        if (Net.IsServer && __instance.TryGetComponent(out BuildingEntity building) && building.id == "lifepodpump")
            return true;
        return SharedMain.PositionIsInSimulatedChunk(__instance.transform.position);
    }

    static bool PumpReady(OilPipeScript pipe) {
        if (pipe == null)
            return false;
        WorldGeneration world = WorldGeneration.world;
        if (world == null || !world.worldExists || world.generatingWorld)
            return false;
        FluidManager fluids = FluidManager.main;
        if (fluids == null || fluids.fluid == null)
            return false;
        Vector2Int cell = world.WorldToBlockPos(pipe.transform.position);
        int w = fluids.fluid.GetLength(0);
        int h = fluids.fluid.GetLength(1);
        return cell.x >= 0 && cell.y >= 0 && cell.x < w && cell.y < h;
    }
}

// stock only rumbles if the host camera is close. use the sim chunks instead, same as oil pipes.
[HarmonyPatch(typeof(GeyserScript), nameof(GeyserScript.OnTriggerEnter2D))]
static class GeyserScript_OnTriggerEnter2D_MultiplayerPatch {
    static bool Prefix(GeyserScript __instance, Collider2D collision) {
        if (!Net.IsRunning)
            return true;
        if (!collision.attachedRigidbody || collision.attachedRigidbody.bodyType != RigidbodyType2D.Dynamic)
            return false;
        if (!SharedMain.PositionIsInSimulatedChunk(__instance.transform.position))
            return false;
        __instance.TryRumble();
        return false;
    }
}

// shake, hp rumble, and the fluid spit. runs if somebodys in those chunks, so a far client still gets it.
[HarmonyPatch(typeof(GeyserScript), nameof(GeyserScript.Update))]
static class GeyserScript_Update_MultiplayerPatch {
    static bool Prefix(GeyserScript __instance) {
        if (!Net.IsRunning)
            return true;
        return SharedMain.PositionIsInSimulatedChunk(__instance.transform.position);
    }
}





[HarmonyPatch(typeof(ElderThornbackBehaviour), nameof(ElderThornbackBehaviour.Update))]
static class ElderThornbackBehaviour_Update_MultiplayerPatch {
    static ElderThornbackBehaviour_Update_MultiplayerPatch() {
        // stays awake outside the 3x3, so the opt out can be checked in game
        ChunkSleep.Ignore(typeof(ElderThornbackBehaviour));
    }

    static bool Prefix(ElderThornbackBehaviour __instance) {
        if (!Net.IsRunning) return true;

        //OG: ElderThornbackBehaviour.Update V7.0.1

        // far
        foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance * 2.25f)) {
            if (nb.IsBodyLocal()) { continue; }

            var body = nb.body;

            body.adrenaline = 100f;

            if (body.horrifiedLevel < 50f) {
                body.horrifiedLevel = 50f;
            }
            if (body.sleeping) {
                body.energy = Mathf.Max(body.energy, 10f);
            }
        }

        // close
        foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance)) {
            if (nb.IsBodyLocal()) { continue; }
            var body = nb.body;

            if (__instance.stage != 0) {
                body.focusedLevel = 100f;
            }

            body.stamina += 1f;
            body.energy = Mathf.Max(body.energy, 15f);

            if (body.sleeping) {
                body.sleeping = false;
            }

            body.adrenaline = 50f;
            body.horrifiedLevel = 100f;
        }


        return true;
    }
}


[HarmonyPatch(typeof(ElderThornbackBehaviour), nameof(ElderThornbackBehaviour.OnDestroy))]
static class ElderThornbackBehaviour_OnDestroy_MultiplayerPatch {
    static bool Prefix(ElderThornbackBehaviour __instance) {
        if (__instance.build == null) {
            return false;
        }
        if (!Net.IsRunning) return true;
        if (__instance.build.GetHealth() <= 0) {
            foreach (var nb in NetBody.GetBodiesInRadius(__instance.transform.position, ElderThornbackBehaviour.maxDistance)) {
                if (nb.IsBodyLocal()) { continue; }

                var body = nb.body;
                body.horrifiedLevel = 0f;
                body.happiness += 40f;
                body.caffeinated += 600f;
            }
        }
        return true;
    }
}













// rewritten
[HarmonyPatch(typeof(BatteryRecharger), nameof(BatteryRecharger.OnUse))]
public static class BatteryRecharger_OnUse_MultiplayerPatch {



    public static bool Copypasted_OnUse(BatteryRecharger charger, Item item, Body body = null) {
        //OG: BatteryRecharger.OnUse V600 steam playtest 1

        var isFull = charger.items[0] != null && charger.items[1] != null;

        if ((bool)item && item.Stats is BatteryInfo && !isFull && !charger.items.Contains(item)) {

            ItemSync.SafeUnloadItem(ItemSync.ItemGetContainerInfo(item));

            int firstAvailable = !charger.items[0] ? 0 : 1;


            item.transform.rotation = Quaternion.identity;
            item.transform.position = charger.targets[firstAvailable].position;
            item.rb.rotation = 0f;
            item.rb.position = item.transform.position;

            item.GetComponent<SpriteRenderer>().sortingOrder = charger.GetComponent<SpriteRenderer>().sortingOrder + 1;

            charger.joints[firstAvailable] = item.gameObject.AddComponent<FixedJoint2D>();
            charger.joints[firstAvailable].frequency = 20f;
            charger.joints[firstAvailable].autoConfigureConnectedAnchor = true;
            charger.joints[firstAvailable].connectedAnchor = charger.targets[firstAvailable].position;
            charger.items[firstAvailable] = item;
            Util.PlayWorldSoundOnScreenIfInRange("batteryinsert", charger.transform.position, 1, 1);
            if (charger.firstTime) {
                charger.GetComponent<Talker>().Talk(Locale.GetOther("rechargingstationcheer"));
                if (Net.IsServer) {
                    Item component = Utils.Create("mp3player", charger.transform.position, 0f).GetComponent<Item>();
                    body?.AutoPickUpItem(component);
                    component.battery.UnloadBattery(skipSpawn: true);
                }
                charger.firstTime = (false);
            }


            if (Net.IsRunning) {
                if (NetObjectRegistry.TryGetSyncInfo(item, out var battery_si)) {

                    if (Net.IsClient) {

                        battery_si.SetIgnoreTimeForRoundTrip(0.5f);

                    } else {
                        if (NetObjectRegistry.TryGetSyncInfo(charger, out var charger_si)) {

                            var writer = Net.CreateWriter(NetmsgId.CLIENT_BatteryRechargerOnUseRelay);
                            writer.Put(battery_si.syncId);
                            writer.Put(charger_si.syncId);
                            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                            // WRITER END;
                        }
                    }
                }

            }
            return true;
        }

        return false;
    }



    static bool Prefix(BatteryRecharger __instance) {
        if (!Net.IsRunning) return true;

        //OG: BatteryRecharger.OnUse V5t5
        Item item = PlayerCamera.main.body.GetItem(PlayerCamera.main.body.handSlot);

        if (Copypasted_OnUse(__instance, item, PlayerCamera.main.body)) {
            if (Net.IsClient) {
                if (NetObjectRegistry.TryGetSyncInfoOrRegister(item, out var si) && NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var chasi)) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_BatteryRechargerOnUse, si.syncId, chasi.syncId, true);
                } else {
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                }
            }
        }

        return false;
    }
}

/// <summary>
/// 电池充电座网络接收器（独立于 <c>[HarmonyPatch]</c> 类以规避值类型参数 <c>Harmony003</c> 告警）。
/// </summary>
internal static class BatteryRechargerSync {
    [ServerReceiver((byte)NetmsgId.SERVER_BatteryRechargerOnUse)]
    private static void ServerReceiver_BatteryRechargerOnUse(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid batteryId);
        reader.Get(out knetid chargerId);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body)
            && body.conscious
            && NetObjectRegistry.TryGetSyncInfo(batteryId, out var batterySi)
            && NetObjectRegistry.TryGetSyncInfo(chargerId, out var chargerSi)
            && chargerSi.go != null
            && KM.DistanceCheck(body, chargerSi.go.transform, Together.MpConstants.GameplayRanges.ServerExchangeToleranceDistance)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(chargerSi.transformPosition, $"BatteryRecharger.OnUse {plr} {batterySi} {chargerSi}");
            }
            BatteryRecharger_OnUse_MultiplayerPatch.Copypasted_OnUse(chargerSi.go.GetComponent<BatteryRecharger>(), batterySi.item);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BatteryRechargerOnUseRelay)]
    private static void ClientReceiver_BatteryRechargerOnUseRelay(knetid _, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid batteryId);
        reader.Get(out knetid chargerId);

        if (NetObjectRegistry.TryGetSyncInfo(batteryId, out var batterySi)
            && NetObjectRegistry.TryGetSyncInfo(chargerId, out var chargerSi)
            && chargerSi.go != null) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(chargerSi.transformPosition, $"BatteryRechargerOnUseRelay {batterySi} {chargerSi}");
            }
            BatteryRecharger_OnUse_MultiplayerPatch.Copypasted_OnUse(chargerSi.go.GetComponent<BatteryRecharger>(), batterySi.item);
        }
    }
}






/// <summary>
/// 洞穴蜱虫巢（<see cref="CaveTickSpawner"/>）联机权威触发与静态巢穴销毁同步补丁（对应 Issue #6）：
/// <para>
/// 1. 客户端拦截本地 <see cref="CaveTickSpawner.OnTriggerEnter2D"/>，杜绝客机本地生成 16 只未注册假蜱虫或单方面销毁巢穴。
/// 2. 服务端使用 <see cref="ConditionalWeakTable{TKey,TValue}"/> 按实例独立节流（0.25s，避免多巢穴共享全局计时器互相饿死），
///    在模拟区块内主动扫描存活玩家/角色距离并权威触发刷怪，同时通过既有 <see cref="NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny"/>
///    移除全客机静态巢穴并广播 3D 音效（零新增包 ID，100% 兼容原版 v5.5.3.0 线协议）。
/// 3. 若房主自身物理碰撞触发原版 <c>OnTriggerEnter2D</c>，同样在 Postfix 统一广播 3D 音效与 <c>deny</c> 消息，并即时移除服务端
///    <see cref="TrackerStaticCaveTicks"/> 以防 10s 延迟销毁窗口内响应客机查询而误重发巢穴。
/// </para>
/// </summary>
[HarmonyPatch(typeof(CaveTickSpawner))]
static class CaveTickSpawner_MultiplayerPatch {
    private const float ServerScanIntervalSeconds = 0.25f;
    private const float DefaultTriggerRadius = 2.5f;

    private sealed class SpawnerRuntimeState {
        public float NextScanTime;
        public float CachedRadius = -1f;
        public Vector2 CachedOffset;
        public bool DenyBroadcasted;
    }

    private static readonly ConditionalWeakTable<CaveTickSpawner, SpawnerRuntimeState> States = new ConditionalWeakTable<CaveTickSpawner, SpawnerRuntimeState>();

    private static SpawnerRuntimeState GetState(CaveTickSpawner spawner) {
        return States.GetOrCreateValue(spawner);
    }

    private static float GetTriggerRadiusAndCenter(CaveTickSpawner spawner, SpawnerRuntimeState state, out Vector2 center) {
        Transform t = spawner.transform;
        Vector2 basePos = t.position;
        if (state.CachedRadius < 0f) {
            if (spawner.TryGetComponent(out CircleCollider2D circle) && circle.radius > 0.01f) {
                Vector3 lossy = t.lossyScale;
                float scale = Mathf.Max(Mathf.Abs(lossy.x), Mathf.Abs(lossy.y));
                state.CachedRadius = circle.radius * (scale > 0.0001f ? scale : 1f);
                state.CachedOffset = new Vector2(circle.offset.x * lossy.x, circle.offset.y * lossy.y);
            } else {
                state.CachedRadius = DefaultTriggerRadius;
                state.CachedOffset = Vector2.zero;
            }
        }
        center = basePos + state.CachedOffset;
        return state.CachedRadius;
    }

    private static void BroadcastDenyOnce(CaveTickSpawner spawner, SpawnerRuntimeState state) {
        if (state.DenyBroadcasted || !Net.IsRunning || !Net.IsServer || spawner == null) {
            return;
        }
        state.DenyBroadcasted = true;

        if (spawner.TryGetComponent(out TrackerStaticCaveTicks tracker) && tracker != null) {
            UnityEngine.Object.Destroy(tracker);
        }

        if (ServerMain.AllClientIdsExceptHost.Count > 0) {
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_Krokosha_CaveTicks_Tracker_deny);
            writer.Put((Vector2)spawner.transform.position);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        }
    }

    private static void NotifyServerTriggered(CaveTickSpawner spawner, SpawnerRuntimeState state) {
        Sound_Play_MultiplayerPatch.force = true;
        Sound.Play("caveticks", spawner.transform.position, twoDimensional: false, pitchShift: false, null, 0.7f);
        BroadcastDenyOnce(spawner, state);
    }

    [HarmonyPatch(nameof(CaveTickSpawner.OnTriggerEnter2D))]
    [HarmonyPrefix]
    static bool OnTriggerEnter2D_Prefix(CaveTickSpawner __instance, ref bool __state) {
        if (!Net.IsRunning) {
            __state = __instance != null && __instance.started;
            return true;
        }
        if (Net.IsClient) {
            __state = false;
            return false;
        }
        __state = __instance != null && __instance.started;
        return true;
    }

    [HarmonyPatch(nameof(CaveTickSpawner.OnTriggerEnter2D))]
    [HarmonyPostfix]
    static void OnTriggerEnter2D_Postfix(CaveTickSpawner __instance, ref bool __state) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        if (__instance.started && !__state) {
            NotifyServerTriggered(__instance, GetState(__instance));
        }
    }

    [HarmonyPatch(nameof(CaveTickSpawner.Update))]
    [HarmonyPostfix]
    static void Update_Postfix(CaveTickSpawner __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        if (WorldGeneration.world == null || !WorldGeneration.world.worldExists) {
            return;
        }

        SpawnerRuntimeState state = GetState(__instance);
        if (__instance.started) {
            if (!state.DenyBroadcasted) {
                BroadcastDenyOnce(__instance, state);
            }
            return;
        }

        float now = Time.unscaledTime;
        if (now < state.NextScanTime) {
            return;
        }
        state.NextScanTime = now + ServerScanIntervalSeconds;

        Vector2 spawnerPos = __instance.transform.position;
        if (!SharedMain.PositionIsInSimulatedChunk(spawnerPos)) {
            return;
        }

        float radius = GetTriggerRadiusAndCenter(__instance, state, out Vector2 center);
        float radiusSqr = radius * radius;

        bool triggered = false;
        for (int i = 0; i < NetBody.all_instances.Count; i++) {
            NetBody nb = NetBody.all_instances[i];
            if (nb == null || nb.body == null || !nb.body.alive) {
                continue;
            }
            if ((nb.pos - center).sqrMagnitude <= radiusSqr) {
                triggered = true;
                break;
            }
        }

        if (!triggered) {
            return;
        }

        __instance.started = true;
        __instance.StartCoroutine(__instance.SpawnSpiders());
        __instance.GetComponent<ParticleSystem>()?.Stop(false, ParticleSystemStopBehavior.StopEmitting);
        UnityEngine.Object.Destroy(__instance.gameObject, 10f);
        NotifyServerTriggered(__instance, state);
    }
}

