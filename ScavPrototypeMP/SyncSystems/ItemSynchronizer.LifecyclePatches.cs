using System;
using System.Collections.Generic;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Rendering.Universal;

namespace Together;

/// <summary>
/// 无人拾取掉落物定时自动清理组件。
/// </summary>
public class ItemDespawnerIfUntouched : MonoBehaviour {
    public float despawntime = 60f;
    float timer = 0f;

    void Update() {
        if (Multiplayer.IsNetworkActiveAndIsClient() || transform.parent != null) {
            timer = 0f;
            return;
        }

        timer += Time.deltaTime;
        if (timer > despawntime) {
            timer = -1f;
            NetObjectRegistry.SafeDestroyObject(gameObject);
        }
    }
}

/// <summary>
/// 带电重力袋零耐久爆炸控制（防重入单次引爆，先泼洒内部物品再销毁袋体）。
/// </summary>
static class GravBagBoom {
    sealed class GravBagDetonated : MonoBehaviour { }

    internal static bool Armed(Item item, float condition) {
        return item != null && item.id == "gravbag" && condition <= 0.005f
            && item.battery != null && item.battery.hasBattery;
    }

    internal static void Play(Item item, float condition) {
        if (!Armed(item, condition) || item.GetComponent<GravBagDetonated>() != null)
            return;
        item.gameObject.AddComponent<GravBagDetonated>();
        WorldGeneration.CreateExplosion(new ExplosionParams {
            position = item.transform.position,
            disfigureChance = 0.15f,
        });
    }
}

/// <summary>
/// 特殊物品行为（喷气背包、合成临时瓶、重力袋、手电筒/测距仪/变焦护目镜）联机更新补丁。
/// </summary>
[HarmonyPatch(typeof(CustomItemBehaviour), nameof(CustomItemBehaviour.Update))]
public static class CustomItemBehaviour_Update_MultiplayerPatch {
    static float last_zoomtime = 0f;

    public static bool Prefix(CustomItemBehaviour __instance) {
        if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated())
            return true;

        Item item = __instance.GetComponent<Item>();
        last_zoomtime = PlayerCamera.main.zoomTime;

        if (item.id == "jetpack") {
            var pb = item.GetComponentInParent<NetBody>();
            if (pb) {
                if (pb.IsLocal)
                    return true;
                if (item.condition > 0f && !item.isWet && (bool)item.transform.parent
                    && item.transform.parent.TryGetComponent<Limb>(out var limb)
                    && limb.body.conscious && !limb.body.currentClimbable && limb.body.moveDir.y > 0.1f) {
                    item.condition -= Time.deltaTime * 0.0135f;
                }
            }
            return false;
        }

        if (item.id == "lightbulb") {
            var pb = item.GetComponentInParent<NetBody>();
            if (pb != null && pb.IsLocal && Net.IsClient) {
                __instance.state = 0;
            }
        } else if (item.id == "craftingbottle"
                   && NetObjectRegistry.IsRegistered(item.gameObject)
                   && item.TryGetComponent(out WaterContainerItem bottle)
                   && bottle.CurrentTotal <= 0f) {
            if (Net.IsServer && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo bottleSi) && bottleSi is not null && !bottleSi.IsDestroyed) {
                bottleSi.Destroy();
            }
            return false;
        } else if (GravBagBoom.Armed(item, item.condition)) {
            if (item.TryGetComponent(out Container box)) {
                BagSleep.WakeContents(box);
                box.ContainerBroke();
                if (Net.IsServer && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out var bagSi) && bagSi != null && !bagSi.IsDestroyed)
                    ItemSync.ReleaseNamedContents(bagSi);
            }
            GravBagBoom.Play(item, item.condition);
            if (Net.IsServer) {
                if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out var si) && si != null && !si.IsDestroyed)
                    si.Destroy();
                else
                    UnityEngine.Object.Destroy(item.gameObject);
            }
            return false;
        }

        return true;
    }

    public static void Postfix(CustomItemBehaviour __instance) {
        if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated())
            return;

        Item item = __instance.GetComponent<Item>();
        switch (item.id) {
            case "autozoomgoggles": {
                if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<Limb>(out var l) && item.battery.hasCharge) {
                    PlayerCamera.main.zoomTime = Util.IsBodyLocal(l.body) ? 0.5f : last_zoomtime;
                }
                break;
            }
            case "rangefinder": {
                if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<InventorySlot>(out var slot)
                    && (slot == slot.body.slots[0] || slot == slot.body.slots[1])) {
                    float z = Vector2.SignedAngle(Vector2.right, (slot.body.targetLookPos - __instance.transform.position).normalized);
                    item.transform.eulerAngles = new Vector3(0f, 0f, z);
                }
                break;
            }
            case "emergencylight":
            case "flashlight": {
                if ((bool)item.transform.parent && item.transform.parent.TryGetComponent<InventorySlot>(out var slot)
                    && (slot == slot.body.slots[0] || slot == slot.body.slots[1])) {
                    float angle = Vector2.SignedAngle(Vector2.right, (slot.body.targetLookPos - item.transform.position).normalized);
                    item.transform.eulerAngles = new Vector3(0f, 0f, angle - 90f);
                }
                if (ItemSync.TryGetSyncInfo(item, out SyncInfo lightSi) && Body_UseItemInHand_MultiplayerPatch.HeldState(lightSi.syncId, out byte held)) {
                    __instance.state = held;
                    if (__instance.transform.childCount > 0) {
                        Light2D beam = __instance.transform.GetChild(0).GetComponent<Light2D>();
                        if (beam is not null) {
                            beam.intensity = held switch {
                                1 => 1f,
                                2 => 0.5f,
                                3 => ((int)(Time.time * 18f) % 2 == 0) ? 1f : 0f,
                                _ => 0f,
                            };
                        }
                    }
                }
                break;
            }
        }
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.Start))]
static class Item_Start_MultiplayerPatch {
    static void Postfix(Item __instance) {
        if (!Net.IsRunning)
            return;

        if (__instance.id == "campfire") {
            __instance.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }

        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated()) {
            var np = ScavPlayer.GetDistanceToNearestLivingPlayer(__instance.transform.position);
            if (np.Item2 < GameplayRanges.SimulationKeepRadiusSqr) {
                NetObjectRegistry.NewGO(__instance.gameObject);
            }
        }
        if (__instance != null)
            CraftCommit.NoteCarriedSync(__instance);
    }
}

/// <summary>
/// 拦截 <see cref="Item.Update"/>：统一由服务端裁决零耐久销毁、同步背包休眠与区块模拟区，并处理视野外火把浸水熄灭。
/// </summary>
[HarmonyPatch(typeof(Item), nameof(Item.Update))]
public static class Item_Update_MultiplayerPatch {
    static bool Prefix(Item __instance) {
        if (Net.IsRunning && __instance != null && __instance.affect == null) {
            ModLog.Flow(LogDomain.Item, "Item.Update.NullAffect", __instance.ToString());
        }

        ItemSync.SpentDestroy(__instance);
        if (Net.IsClient && __instance != null && NetObjectRegistry.TryGetSyncInfo(__instance, out SyncInfo deadSi) && deadSi.IsDestroyed) {
            UnityEngine.Object.Destroy(__instance.gameObject);
            return false;
        }
        if (Net.IsClient && ItemSync.DyingContainer(__instance)) {
            ItemSync.ParentTestChest("chest spill client", __instance, __instance.gameObject);
            if (__instance.TryGetComponent(out Container box))
                box.ContainerBroke();
            return false;
        }
        if (Net.IsClient && __instance.Stats is not null && __instance.Stats.destroyAtZeroCondition && __instance.condition <= 0f && NetObjectRegistry.IsRegistered(__instance.gameObject)) {
            return false;
        }
        return true;
    }

    static void Postfix(Item __instance) {
        if (__instance != null) {
            ChunkSleep.Watch(__instance);
            BagSleep.Note(__instance);
        }
        if (__instance != null && __instance.id == "dynamite" && __instance.TryGetComponent(out CustomItemBehaviour fuse)) {
            object[] data = fuse.data;
            if (data != null && data.Length > 0 && data[0] is bool lit && lit)
                SimKeep.ArmIfIdle(__instance.gameObject);
        }
        if (__instance != null && __instance.condition < 0f)
            __instance.condition = 0f;

        if (Net.IsRunning && __instance != null && __instance.TryGetComponent<SyncInfoGameObjectTracker>(out _)) {
            if (TorchWet.InWetLiquid(__instance) && __instance.canGetWet) {
                __instance.wetTime = Time.time;
                if (__instance.id == "torch") {
                    if (Net.IsServer)
                        TorchWet.Note(__instance);
                    else
                        TorchWet.Report(__instance);
                }
            }
        }
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions) {
        var codes = new List<CodeInstruction>(instructions);
        int i = 0;
        while (!(codes[i].ToString().Contains("get_enabled") && codes[i].opcode == OpCodes.Callvirt && codes[i].ToString().Contains("Renderer"))) {
            i++;
        }
        i++;
        codes.RemoveRange(0, i);

        var newInstructions = new List<CodeInstruction> {
            new CodeInstruction(OpCodes.Ldarg_0),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Component), "get_transform")),
            new CodeInstruction(OpCodes.Callvirt, AccessTools.Method(typeof(Transform), "get_position")),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(Vector2), "op_Implicit", new Type[] { typeof(Vector3) })),
            new CodeInstruction(OpCodes.Call, AccessTools.Method(typeof(SharedMain), nameof(SharedMain.WorldInSimRange))),
        };
        codes.InsertRange(0, newInstructions);
        return codes;
    }
}

/// <summary>
/// 客户端视野内但处于服务端休眠区块的火把浸水上报与服务端补扣耐久协调器。
/// </summary>
static class TorchWet {
    static readonly Dictionary<int, Item> Noted = new Dictionary<int, Item>();
    static readonly Dictionary<int, float> NextReport = new Dictionary<int, float>();
    static readonly List<int> Drop = new List<int>();

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.TorchWet] = (plr, cmd, args) => {
            if (args.Length < 2 || !ushort.TryParse(args[1], out ushort raw))
                return;
            if (!NetObjectRegistry.TryGetSyncInfo((knetid)raw, out SyncInfo si) || si == null || si.IsDestroyed || si.item == null)
                return;
            Item item = si.item;
            if (item.id != "torch")
                return;
            bool near = plr != null && plr.body != null
                && Vector2.Distance(plr.body.transform.position, item.transform.position) <= GameplayRanges.CraftGroundScanRadius;
            if (!near && !InWetLiquid(item))
                return;
            item.wetTime = Time.time;
            Note(item);
            if (!item.gameObject.activeInHierarchy)
                si.WriteObjectIntoPacket();
        };
    }

    public static bool InWetLiquid(Item item) {
        if (item == null || WorldGeneration.world == null || FluidManager.main == null)
            return false;
        Vector2 p = item.transform.position;
        if (Mathf.Abs(p.x) >= WorldGeneration.world.halfWidth || Mathf.Abs(p.y) >= WorldGeneration.world.halfHeight)
            return false;
        return FluidManager.main.HasWetLiquid(WorldGeneration.world.WorldToBlockPos(p));
    }

    public static void Note(Item item) {
        if (item != null)
            Noted[item.GetInstanceID()] = item;
    }

    public static void Report(Item item) {
        if (!Net.IsClient || Net.IsServer || item == null || item.id != "torch")
            return;
        if (!NetObjectRegistry.TryGetSyncInfo(item, out SyncInfo si) || si == null || si.IsDestroyed)
            return;
        int id = item.GetInstanceID();
        float now = Time.unscaledTime;
        if (NextReport.TryGetValue(id, out float at) && now < at)
            return;
        NextReport[id] = now + 0.5f;
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.TorchWet + " " + si.syncId.id);
        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
    }

    public static void Tick() {
        if (!Net.IsServer || Noted.Count == 0)
            return;
        float now = Time.time;
        float dt = Time.deltaTime;
        Drop.Clear();
        foreach (var pair in Noted) {
            Item item = pair.Value;
            if (item == null || now - item.wetTime >= 120f) {
                Drop.Add(pair.Key);
                continue;
            }
            if (item.gameObject.activeInHierarchy || item.condition <= 0f)
                continue;
            item.condition = Mathf.Max(0f, item.condition - dt);
            if (NetObjectRegistry.TryGetSyncInfo(item, out SyncInfo si) && si != null && !si.IsDestroyed)
                si.WriteObjectIntoPacket();
        }
        for (int i = 0; i < Drop.Count; i++) {
            Noted.Remove(Drop[i]);
            NextReport.Remove(Drop[i]);
        }
    }
}

[HarmonyPatch(typeof(WaterContainerItem), nameof(WaterContainerItem.UpdateCondition))]
static class WaterContainerItem_UpdateCondition_Clamp {
    static void Postfix(WaterContainerItem __instance) {
        if (__instance == null)
            return;
        Item item = __instance.GetComponent<Item>();
        if (item != null && item.condition < 0f)
            item.condition = 0f;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.HandleDecay))]
static class Item_HandleDecay_MultiplayerPatch {
    static bool Prefix(Item __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient && __instance is not null && NetObjectRegistry.IsRegistered(__instance.gameObject)) {
            return false;
        }

        float decMultiplier = __instance.decayMultiplier * (__instance.isWet ? 6f : 1f) * WorldGeneration.globalDecayRate;
        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWithoutContainerItem) != 0 && __instance.cont.itemCount == 0) {
            decMultiplier *= 0f;
        }
        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWhenNotWorn) != 0 && (!__instance.transform.parent || (bool)__instance.ParentContainer())) {
            decMultiplier *= 0f;
        }
        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.NoDecayWhenStill) != 0 &&
            (!__instance.transform.parent || __instance.GetOrAddComponent<BodyGetterOverrider>().GetBody().rb.velocity.magnitude < 0.5f)) {
            decMultiplier *= 0f;
        }

        if ((__instance.Stats.decayInfo & (byte)ItemInfo.DecayType.BatteryDecay) != 0) {
            float mult = __instance.battery.preset == BatteryItem.BatteryPreset.Large
                ? 3f
                : (__instance.battery.preset == BatteryItem.BatteryPreset.Medium ? 1f : 0.5f);
            __instance.battery.DrainCharge(__instance.Stats.rotSpeed * 0.01f * mult * decMultiplier * Time.deltaTime);
        } else {
            __instance.condition -= __instance.Stats.rotSpeed * decMultiplier * Time.deltaTime * 0.01f;
        }

        __instance.condition = Mathf.Clamp01(__instance.condition);
        return false;
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.OnWillRenderObject))]
static class Item_OnWillRenderObject_MultiplayerPatch {
    static bool Prefix(Item __instance) {
        if (!Net.IsRunning)
            return true;
        if (__instance.TryGetComponent<SyncInfoGameObjectTracker>(out var tracker)) {
            tracker.isInSimulationRange = true;
            tracker.isVisible = true;
            return false;
        }
        return true;
    }
}
