using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Together;

[HarmonyPatch(typeof(Body), nameof(Body.DropWearable))]
public static class Body_DropWearable_MultiplayerPatch {
    static bool forceDrop = false;

    public static void Force(Body __instance, Item item) {
        forceDrop = true;
        if (__instance != null)
            __instance.DropWearable(item);
        forceDrop = false;
        HeldSeat.Detach(item);
    }

    public static void Postfix(Body __instance, Item item) {
        HeldSeat.Release(item);
        if (!Net.IsRunning)
            return;
        if (!forceDrop && Util.IsWorldGenerated()) {
            if (Util.IsBodyLocal(__instance) && !ItemSync.ItemCanBeIgnoredForNetwork(item)) {
                MoveCommit.SubmitLive(item);
            }
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.WearWearable))]
public static class Body_WearWearable_MultiplayerPatch {
    static void Postfix(Body __instance, Item item) {
        if (!Net.IsRunning)
            return;
        if (item != null && item.TryGetComponent(out BodyGetterOverrider bgo)) {
            bgo.SetBody(__instance);
        }
        if (Util.IsWorldGenerated() && Util.IsBodyLocal(__instance) && !ItemSync.ItemCanBeIgnoredForNetwork(item) && __instance.GetWearable(item.id) == item) {
            MoveCommit.SubmitLive(item);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.SwapSlots))]
static class Body_SwapSlots_MultiplayerPatch {
    static void Prefix() {
        if (!Net.IsRunning)
            return;
        MoveCommit.Push();
    }

    static void Postfix(Body __instance, int slot1, int slot2) {
        if (!Net.IsRunning)
            return;
        if (!__instance.IsBodyLocal()) {
            MoveCommit.Pop();
            return;
        }
        ItemSync.RecordAndApplyInvState(Util.GetLocalBody());
        MoveCommit.EndSwap(__instance, slot1, slot2);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.UnloadItem))]
static class Container_UnloadItem_MultiplayerPatch {
    static void Prefix(Item item) {
        if (!Net.IsRunning)
            return;
        if (ItemSync.TryGetSyncInfo(item, out var si) && PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_TryPerformInventoryAction) {
            ItemSync.ItemContainerChanged.Add(si);
            si.SetIgnoreTimeForRoundTrip(1f);
        }
    }

    static void Postfix(Item item) {
        if (!Net.IsRunning)
            return;
        HeldSeat.Release(item);
    }
}

[HarmonyPatch(typeof(Container), nameof(Container.LoadItem))]
static class Container_LoadItem_MultiplayerPatch {
    static void Prefix(Item item) {
        if (Net.IsRunning)
            ItemSync.SnapOntoContainer(item);
    }

    static void Postfix(Container __instance, Item item) {
        if (!Net.IsRunning || item == null || __instance == null)
            return;
        if (item.transform.parent != __instance.transform)
            return;
        if (!PlayerCamera_TryPerformInventoryAction_MultiplayerPatch.is_inside_TryPerformInventoryAction)
            return;
        if (!ItemSync.TryGetSyncInfo(item, out var si))
            return;
        ItemSync.ItemContainerChanged.Add(si);
        ModLog.Flow(LogDomain.Container, "Container.LoadItem", si.ToString());
        si.SetIgnoreTimeForRoundTrip(1f);
    }
}

/// <summary>
/// 拦截 <see cref="PlayerCamera.TryPerformInventoryAction"/>：同步电池拆装，并使向容器拖拽采用“先校验入包、拒绝则保留原位”的无损语义。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformInventoryAction))]
public static class PlayerCamera_TryPerformInventoryAction_MultiplayerPatch {
    public static bool is_inside_TryPerformInventoryAction = false;

    // RaycastResult 为非 readonly struct，必须使用 ref 传递以消除 BepInEx.Analyzers Harmony003 告警
    static bool Prefix(PlayerCamera __instance, ref bool __result, ref RaycastResult hit, List<RaycastResult> uiCasts) {
        if (!Net.IsRunning)
            return true;

        if (Util.IsWorldGenerated()) {
            if (!hit.gameObject.TryGetComponent(out InvButton component) || !component.Overlaps(uiCasts)) {
                is_inside_TryPerformInventoryAction = false;
                return false;
            }
            if (component.GetItem() == __instance.dragItem) {
                __result = true;
                is_inside_TryPerformInventoryAction = false;
                return false;
            }
            Item item = component.GetItem();
            if ((bool)item && (bool)item.battery && ItemSync.TryGetSyncInfo(item, out var si1) && ItemSync.TryGetSyncInfo(__instance.dragItem, out var si2)) {
                if (__instance.dragItem.Stats.HasTag("tool") && item.battery.hasBattery) {
                    NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemBatteryManipulation);
                    writer.Put(false);
                    writer.Put(si1.syncId);
                    writer.Put(si2.syncId);
                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                    item.battery.UnloadBattery();
                    __result = true;
                    return false;
                }
                if (__instance.dragItem.Stats.HasTag("battery") && !item.battery.hasBattery && __instance.dragItem.Stats.GetType() == typeof(BatteryInfo)) {
                    NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemBatteryManipulation);
                    writer.Put(true);
                    writer.Put(si1.syncId);
                    writer.Put(si2.syncId);
                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);

                    item.battery.LoadBattery(__instance.dragItem);
                    __result = true;
                    return false;
                }
            }

            if ((bool)item && item.TryGetComponent(out Container dest) && (bool)__instance.dragItem) {
                is_inside_TryPerformInventoryAction = true;
                bool expand = Input.GetKey(KeyBinds.GetBind("expanddesc")) && item != __instance.dragItem;
                if (!expand) {
                    hit.gameObject.transform.localScale = Vector3.one * (dest.CanHoldItem(__instance.dragItem) ? 1.5f : 0.8f);
                    if (MovedInto(dest, __instance.dragItem))
                        __instance.PlayBackpackSound();
                    __result = true;
                    return false;
                }
                if (Poured(dest, __instance.dragItem)) {
                    __instance.PlayBackpackSound();
                    __result = true;
                    return false;
                }
                __result = FinishSlotOrCombine(__instance, component, item);
                return false;
            }

            is_inside_TryPerformInventoryAction = true;
        }
        return true;
    }

    static void Postfix() {
        is_inside_TryPerformInventoryAction = false;
    }

    static bool MovedInto(Container dest, Item item) {
        if (dest == null || item == null)
            return false;
        Transform before = item.transform.parent;
        dest.LoadItem(item);
        return item.transform.parent == dest.transform && before != dest.transform;
    }

    static bool Poured(Container dest, Item bag) {
        var kids = new List<Item>();
        foreach (Transform child in bag.transform) {
            if (child.TryGetComponent(out Item kid))
                kids.Add(kid);
        }
        bool any = false;
        foreach (Item kid in kids) {
            if (MovedInto(dest, kid))
                any = true;
        }
        return any;
    }

    static bool FinishSlotOrCombine(PlayerCamera cam, InvButton button, Item under) {
        Item drag = cam.dragItem;
        if ((bool)under && cam.body.CanCombine(under, drag)) {
            cam.body.CombineItems(under, drag);
            return true;
        }
        if (!button.isBody)
            return false;
        if (drag.Stats.wearable && !drag.Stats.wearableCanBeHeld) {
            cam.DoAlert(Locale.GetOther("alertpickupwearable"));
            return true;
        }
        if (cam.body.HoldingItem(button.slot) && cam.body.HoldingItem(drag)) {
            cam.body.SwapSlots(button.slot, cam.body.SlotOf(drag));
            button.transform.localScale = Vector3.one * 1.5f;
        } else {
            if (cam.body.HoldingItem(drag))
                cam.body.DropItem(drag);
            if (cam.body.HoldingItem(button.slot))
                cam.body.DropItem(button.slot);
            cam.body.PickUpItem(drag, button.slot);
            button.transform.localScale = Vector3.one * 1.5f;
        }
        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemBatteryManipulation)]
    static void ReceiveBatteryManipulation(knetid clientId, NetDataReader reader) {
        reader.Get(out bool load);
        reader.Get(out knetid targetSyncId);
        reader.Get(out knetid toolOrBatterySyncId);

        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb) &&
            ItemSync.TryGetItemSyncInfo(targetSyncId, out var itemSi) &&
            ItemSync.CheckIfBodyReachThisItem(itemSi, pb.body, GameplayRanges.ServerExchangeToleranceDistance, false) &&
            ItemSync.TryGetItemSyncInfo(toolOrBatterySyncId, out var toolOrBatterySi) &&
            ItemSync.CheckIfBodyReachThisItem(toolOrBatterySi, pb.body, GameplayRanges.ServerExchangeToleranceDistance, false)) {
            ModLog.Flow(LogDomain.Item, "BatteryManipulation", $"{plr} load={load} target={itemSi} second={toolOrBatterySi}");

            Item tool = toolOrBatterySi.item;
            Item item = itemSi.item;
            if (item.battery) {
                if (!load && tool.Stats.HasTag("tool")) {
                    Sound.Play("batteryinsert", item.transform.position);
                    Item spawnedBattery = (UnityEngine.Object.Instantiate(Resources.Load(item.battery.batteryType), item.transform.position, Quaternion.identity) as GameObject)
                        .GetComponent<Item>();
                    spawnedBattery.condition = item.condition;
                    spawnedBattery.favourited = item.battery.batteryWasFavourited;
                    pb.body.AutoPickUpItem(spawnedBattery);
                    item.battery.UnloadBattery(true);
                } else if (load && tool.Stats.HasTag("battery")) {
                    item.battery.LoadBattery(tool);
                }
            }
            return;
        }

        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, (ushort)clientId, targetSyncId.id, toolOrBatterySyncId.id);
        log.serverdeny($"ItemBatteryManipulation denied for: {DebugHelp.GetPlayerFullDebugString(clientId)} item1:{targetSyncId} item2:{toolOrBatterySyncId}");
    }
}

#if FULLGAME
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.CloseItemCustomizer))]
static class PlayerCamera_CloseItemCustomizer_MultiplayerPatch {
    static Item watched;

    static void Prefix(PlayerCamera __instance) {
        watched = Net.IsRunning ? __instance.customizedItem : null;
    }

    static void Postfix() {
        if (!Net.IsRunning || watched == null)
            return;
        ItemSync.PushCosmetics(watched);
        ItemSync.PushFavourite(watched);
    }
}

static class ItemCosmetics_NetReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_ItemCosmetics)]
    static void Server_ItemCosmetics(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out string label);
        reader.Get(out byte swatch);
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body))
            return;
        if (!ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) || si == null || si.item == null)
            return;

        if (body != null && ItemSync.CheckIfBodyReachThisItem(si, body)) {
            si.item.label = label ?? "";
            si.item.colorSwatch = swatch;
            si.itemLabel.Value = si.item.label;
            si.colorSwatch.Value = (byte)Mathf.Clamp(si.item.colorSwatch, 0, 255);
        } else if (plr != null && !plr.IsLocal) {
            ItemSync.EchoCosmetics(plr, si);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemCosmetics, false)]
    static void Client_ItemCosmetics(knetid _, NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out string label);
        reader.Get(out byte swatch);
        ItemSync.ClearCosmeticsPending(id);
        if (ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) && si != null && si.item != null) {
            si.item.label = label ?? "";
            si.item.colorSwatch = swatch;
        }
    }
}
#endif

static class ItemFavourite_NetReceivers {
    [ServerReceiver((byte)NetmsgId.SERVER_ItemFavourited)]
    static void Server_ItemFavourited(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out bool fav);
        if (!ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out ScavPlayer plr, out Body body))
            return;
        if (!ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) || si == null || si.item == null)
            return;

        if (body != null && ItemSync.CheckIfBodyReachThisItem(si, body)) {
            si.item.favourited = fav;
            si.favourited.Value = fav;
        } else if (plr != null && !plr.IsLocal) {
            ItemSync.EchoFavourite(plr, si);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemFavourited, false)]
    static void Client_ItemFavourited(knetid _, NetDataReader reader) {
        reader.Get(out knetid id);
        reader.Get(out bool fav);
        ItemSync.ClearFavouritePending(id);
        if (ItemSync.TryGetItemSyncInfo(id, out SyncInfo si) && si != null && si.item != null) {
            si.item.favourited = fav;
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleWhileDragging))]
static class PlayerCamera_Favourite_MultiplayerPatch {
    static Item favWatch;
    static bool favBefore;
#if FULLGAME
    static byte swatchBefore;
#endif

    static void Prefix(List<RaycastResult> uiCasts) {
        favWatch = null;
        if (!Net.IsRunning || uiCasts == null || (ConsoleScript.instance != null && ConsoleScript.instance.active))
            return;
#if FULLGAME
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null && cam.itemCustomizer != null && cam.itemCustomizer.activeSelf)
            return;
#endif
        if (!Input.GetKeyDown(KeyBinds.GetBind("favourite")))
            return;

        for (int i = 0; i < uiCasts.Count; i++) {
            if (uiCasts[i].gameObject.TryGetComponent(out InvButton btn) && btn.Overlaps(uiCasts)) {
                Item item = btn.GetItem();
                if (item != null) {
                    favWatch = item;
                    favBefore = item.favourited;
#if FULLGAME
                    swatchBefore = (byte)Mathf.Clamp(item.colorSwatch, 0, 255);
#endif
                }
                break;
            }
        }
    }

    static void Postfix() {
        Item item = favWatch;
        favWatch = null;
        if (!Net.IsRunning || item == null)
            return;
        if (item.favourited != favBefore) {
            ItemSync.PushFavourite(item);
        }
#if FULLGAME
        if ((byte)Mathf.Clamp(item.colorSwatch, 0, 255) != swatchBefore) {
            ItemSync.PushCosmetics(item);
        }
#endif
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.CombineItems))]
public static class Body_CombineItems_MultiplayerPatch {
    public static bool Body_CombineItems(Body combinerBody, Item it1, Item it2) {
        if (!combinerBody.CanCombine(it1, it2))
            return true;
        if ((bool)it1.GetComponent<GunScript>() && (bool)it2.GetComponent<AmmoScript>()) {
            it1.GetComponent<GunScript>().LoadMag(it2.GetComponent<AmmoScript>());
            return true;
        }
        if ((bool)it1.GetComponent<AmmoScript>() && (bool)it2.GetComponent<AmmoScript>()) {
            it1.GetComponent<AmmoScript>().LoadRound(it2.GetComponent<AmmoScript>());
            return true;
        }
        if (it1.TryGetComponent<WaterContainerItem>(out var toCont) && it2.TryGetComponent<WaterContainerItem>(out var fromCont)) {
            if (toCont.SpaceLeft > 0f && it1.id != "craftingbottle" && Util.IsBodyLocal(combinerBody)) {
                PlayerCamera.main.StartLiquidTransfer(toCont, fromCont);
            }
            return false;
        }
        float needed = 1f - it1.condition;
        it1.condition += Mathf.Min(needed, it2.condition);
        it2.condition -= Mathf.Min(needed, it2.condition);
        combinerBody.CreateCloudMini(it1.transform.position);
        Util.PlayWorldSoundOnScreenIfInRange(VanillaAssetKeys.SoundCombine, combinerBody.transform.position);
        if (it1.TryGetParentContainer(out var cont) && (cont.AboveHoldingWeight() || it1.totalWeight > cont.maxWeightPerItem)) {
            cont.UnloadItem(it1);
        }
        return true;
    }

    public static bool DoTheCombineAnnouncement(bool isFluid, float ml, Body body, Item it1, Item it2) {
        if (ItemSync.ItemCanBeIgnoredForNetwork(it1) || ItemSync.ItemCanBeIgnoredForNetwork(it2))
            return true;

        if (ItemSync.SyncRegistry.TryGetValue(it1.gameObject, out var isi1) && ItemSync.SyncRegistry.TryGetValue(it2.gameObject, out var isi2)) {
            isi1.SetIgnoreTimeForRoundTrip();
            isi2.SetIgnoreTimeForRoundTrip();

            bool didCombine = isFluid || Body_CombineItems(body, it1, it2);
            if (didCombine) {
                NetmsgId msgId = isFluid ? NetmsgId.SERVER_CombineLiquidItems : NetmsgId.SERVER_BodyCombineItems;
                NetDataWriter writer = Net.CreateWriter(msgId);
                writer.Put(isi1.syncId);
                writer.Put(isi2.syncId);
                if (isFluid)
                    writer.Put(ml);
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                ModLog.Flow(LogDomain.Item, "CombineItems.Send", $"{msgId} {isi1} x {isi2}");
            }
            return false;
        }

        ModLog.Warning($"Attempted to combine unregistered items {it1.id} and {it2.id}, aborting.", LogDomain.Item);
        NetObjectRegistry.AlertObjectNotRegistered();
        NetObjectRegistry.NewGO(it1.gameObject);
        NetObjectRegistry.NewGO(it2.gameObject);
        return false;
    }

    public static bool Prefix(Body __instance, Item it1, Item it2) {
        if (!Net.IsRunning || !Util.IsWorldGenerated())
            return true;

        if (!Util.IsBodyLocal(__instance)) {
            Body_CombineItems(__instance, it1, it2);
            return false;
        }
        if (__instance.CanCombine(it1, it2)) {
            return DoTheCombineAnnouncement(false, 0f, __instance, it1, it2);
        }
        return false;
    }
}

[HarmonyPatch(typeof(LiquidTransfer), nameof(LiquidTransfer.Finish))]
public static class LiquidTransfer_Finish_MultiplayerPatch {
    public static void Postfix(LiquidTransfer __instance) {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated() && Util.IsBodyLocal(PlayerCamera.main.body)) {
            Body_CombineItems_MultiplayerPatch.DoTheCombineAnnouncement(
                true,
                __instance.ml,
                PlayerCamera.main.body,
                __instance.transferTo.GetComponent<Item>(),
                __instance.transferFrom.GetComponent<Item>());
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.SwitchHands))]
static class Body_SwitchHands_MultiplayerPatch {
    static void Prefix() {
        if (!Net.IsRunning)
            return;
        MoveCommit.Push();
    }

    static void Postfix(Body __instance) {
        if (!Net.IsRunning)
            return;
        if (!__instance.IsBodyLocal()) {
            MoveCommit.Pop();
            return;
        }
        ItemSync.RecordAndApplyInvState(Util.GetLocalBody());
        MoveCommit.EndHands(__instance);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.PickUpItem))]
public static class Body_PickUpItem_MultiplayerPatch {
    public static void Postfix(Body __instance, Item item) {
        if (__instance.IsBodyLocal() && PlayerCamera.main.craftingPanel.activeSelf) {
            PlayerCamera.main.RefreshRecipeList();
        }
        if (!Net.IsRunning)
            return;
        if (SharedMain.LocalWorldIsGenerated && !Net.IsServer && __instance.IsBodyLocal()) {
            if (!ItemSync.ItemCanBeIgnoredForNetwork(item) && __instance.HoldingItem(item))
                MoveCommit.SubmitLive(item);
        }
        if (item != null && item.TryGetComponent(out BodyGetterOverrider bgo)) {
            bgo.SetBody(__instance);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.ThrowItem))]
static class Body_ThrowItem_MultiplayerPatch {
    static Item _thrown;

    static void Prefix(Body __instance, float force, ref SyncInfo __state) {
        __state = null;
        if (!Net.IsRunning)
            return;
        _thrown = __instance.GetItem(__instance.handSlot);
        MoveCommit.BeginThrow();
        if (!Multiplayer.IsNetworkActiveAndIsWorldGenerated())
            return;

        if (Util.IsBodyLocal(__instance) && _thrown != null && NetObjectRegistry.TryGetSyncInfo(_thrown, out __state)) {
            int idx = Array.IndexOf(ItemSync.Client_last_inventory_state, __state);
            if (idx != -1) {
                ItemSync.Client_last_inventory_state[idx] = null;
            }
            __state.SetIgnoreTimeForRoundTrip(1f);
            NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ThrowItem);
            writer.Put(__state.syncId);
            writer.Put(force);
            ModLog.Flow(LogDomain.Item, "ThrowItem.Send", $"{__state} force={force:0.##}");
            Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        }
    }

    static void Postfix() {
        if (!Net.IsRunning)
            return;
        MoveCommit.EndThrow();
        Item thrown = _thrown;
        _thrown = null;
        if (thrown != null && thrown.transform.parent == null)
            SimKeep.Arm(thrown.gameObject);
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.DropItem), new Type[] { typeof(Item) })]
static class Body_DropItem_MultiplayerPatch {
    static void Prefix(Body __instance, Item item, out bool __state) {
        __state = false;
        if (!Net.IsRunning)
            return;
        try {
            __state = __instance != null && item != null && __instance.HoldingItem(item);
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Container, "Body.DropItem.Prefix", ex.Message);
        }
    }

    static void Postfix(Body __instance, Item item, bool __state) {
        if (!Net.IsRunning)
            return;
        SlotHold.Release(__instance, item);
        HeldSeat.Release(item);
        if (__state && __instance != null && __instance.IsBodyLocal())
            MoveCommit.SubmitLive(item);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleReleaseDragging))]
public static class PlayerCamera_HandleReleaseDragging_MultiplayerPatch {
    static ItemSync.ItemsContainerInfo last_ici = null;

    public static void Prefix(PlayerCamera __instance, ref SyncInfo __state) {
        __state = null;
        if (!Net.IsRunning)
            return;

        MoveCommit.Push();
        if (__instance.dragItem) {
            if (ItemSync.TryGetSyncInfo(__instance.dragItem, out var si)) {
                last_ici = ItemSync.ItemGetContainerInfo(__instance.dragItem);
                __state = si;
            } else {
                NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(__instance.dragItem.gameObject);
            }
        }
    }

    public static void Postfix(ref SyncInfo __state) {
        if (__state != null) {
            ItemSync.ItemsContainerInfo newIci = ItemSync.ItemGetContainerInfo(__state.item);
            if (newIci.JustContainerChanged(last_ici) && newIci.inv_slot == null) {
                ItemSync.ItemContainerChanged.Add(__state);
            }
            if (newIci.is_in_locplr_surface_inv && last_ici != null && last_ici.container != null)
                ItemSync.RememberPickupBag(__state.syncId, last_ici.container);
            else
                ItemSync.RememberPickupBag(__state.syncId, null);
            MoveCommit.EndDrag(__state.item);
        } else if (Net.IsRunning) {
            MoveCommit.EndDrag(null);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPerformWorldActions))]
static class PlayerCamera_TryPerformWorldActions_MultiplayerPatch {
    static bool Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning)
            return true;
        Item drag = __instance.dragItem;
        if (drag == null)
            return false;

        Container source = null;
        bool inBag = drag.transform.parent != null && drag.transform.parent.TryGetComponent(out source);
        float dragged = Vector2.Distance(Input.mousePosition, __instance.clickPos);
        if (__instance.body.HoldingItem(drag) && !__instance.woundView.activeSelf && dragged > 10f)
            __instance.body.DropItem(drag);

        bool onLimb = drag.transform.parent != null && drag.transform.parent.GetComponent<Limb>() != null;
        if (drag.Stats != null && drag.Stats.wearable && dragged > 10f && (onLimb || (bool)__instance.body.GetWearable(drag.id))) {
            __instance.body.DropWearable(drag);
            if (drag.transform.parent != null && drag.transform.parent.GetComponent<Limb>() != null)
                HeldSeat.Detach(drag);
        }

        if (UIUtil.IsPointerOverUIElement())
            return false;

        if (inBag && drag.transform.parent != source.transform)
            inBag = false;

        Collider2D hit = Physics2D.OverlapPoint(Camera.main.ScreenToWorldPoint(Input.mousePosition), PhysicsLayers.ItemMask);
        if ((bool)hit && hit.TryGetComponent(out Container dest) && !drag.TryGetComponent<Container>(out _)) {
            Transform before = drag.transform.parent;
            dest.LoadItem(drag);
            if (drag.transform.parent == dest.transform && before != dest.transform) {
                __instance.PlayBackpackSound();
                return false;
            }
            if (inBag && drag.transform.parent == source.transform)
                return false;
        }

        if (inBag && source != null && drag.transform.parent == source.transform) {
            source.UnloadItem(drag);
            __instance.PlayBackpackSound();
        }
        return false;
    }
}
