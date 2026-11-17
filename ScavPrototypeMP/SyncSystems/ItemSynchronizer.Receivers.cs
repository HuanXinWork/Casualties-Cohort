using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 物品与容器权威同步核心（第 3 部分：物品合并、液体倾倒、投掷、旧版拾取/穿戴/丢弃网络消息接收器及客户端回退处理）。
/// </summary>
internal partial class ItemSync {
    [ServerReceiver((byte)NetmsgId.SERVER_BodyCombineItems)]
    static void ServerReceiver__BodyCombineItems(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncid1);
        reader.Get(out knetid syncid2);

        if (!WorldGeneration.world || WorldGeneration.world.generatingWorld)
            return;
        Body body = ScavPlayer.GetBodyFromClientId(clientId);
        if (body == null || !body.conscious)
            return;

        if (!TryGetItem(syncid1, out var isi1, out _) || !TryGetItem(syncid2, out var isi2, out _))
            return;

        if (!CheckIfBodyReachThisItem(isi1, body) || !CheckIfBodyReachThisItem(isi2, body)) {
            MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, clientId.id, syncid1.id, syncid2.id);
            ModLog.Flow(LogDomain.Item, "BodyCombineItems.OutOfReach", $"client={clientId} {isi1} x {isi2}");
            return;
        }

        if (!Util.IsBodyLocal(body)) {
            bool canCombine = body.CanCombine(isi1.item, isi2.item);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(body.transform.position, $"S: BodyCombineItems {canCombine} {clientId} {isi1} x {isi2}");
            if (canCombine) {
                Server_AnnounceCombineSound(isi1, isi2, clientId);
                Body_CombineItems_MultiplayerPatch.Body_CombineItems(body, isi1.item, isi2.item);
            }
        }
        NetObjectRegistry.SafeNewGO(isi1.go);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_CombineLiquidItems)]
    static void ServerReceiver__CombineLiquidItems(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncid1);
        reader.Get(out knetid syncid2);
        reader.Get(out float amt);

        if (!Util.IsWorldGenerated() || !Util.IsFinite(amt))
            return;
        Body body = ScavPlayer.GetBodyFromClientId(clientId);
        if (body == null || !body.conscious)
            return;

        if (!TryGetItem(syncid1, out var isi1, out _) || !TryGetItem(syncid2, out var isi2, out _) || !isi1.IsLiquidContainer() || !isi2.IsLiquidContainer())
            return;

        if (!CheckIfBodyReachThisItem(isi1, body) || !CheckIfBodyReachThisItem(isi2, body)) {
            MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, clientId.id, syncid1.id, syncid2.id);
            ModLog.Flow(LogDomain.Item, "CombineLiquidItems.OutOfReach", $"client={clientId} {isi1} x {isi2}");
            return;
        }

        if (!Util.IsBodyLocal(body)) {
            bool canCombine = body.CanCombine(isi1.item, isi2.item);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(body.transform.position, $"S: CombineLiquidItems {canCombine} {clientId} {isi1} x {isi2}");
            if (canCombine) {
                ServerMain.Server_AnnounceSound(isi1.go.transform.position, "waterpour", ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
                body.CombineLiquids(isi1.liquidcontainer, isi2.liquidcontainer, amt);
            }
        }
        NetObjectRegistry.SafeNewGO(isi1.go);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemChangeContainer)]
    static void ServerReceiver__ItemChangeContainer(knetid clientId, NetDataReader reader) {
        reader.Get(out bool hasContainer);
        reader.Get(out knetid syncid);
        reader.Get(out knetid containerSyncid);

        if (!Util.IsWorldGenerated() || !ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var body) || !body.conscious)
            return;

        if (TryGetItem(syncid, out var si, out var item)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(body.transform.position, $"S: ItemChangeContainer {plr} load:{hasContainer} ids:{syncid}->{containerSyncid}");

            SnapOntoContainer(item);
            if (CheckIfBodyReachThisItem(si, body, GameplayRanges.ServerExchangeToleranceDistance, true)) {
                if (hasContainer) {
                    if (!TryGetItem(containerSyncid, out var contSi, out _) || !contSi.IsContainer() || contSi.container == null) {
                        ModLog.Flow(LogDomain.Container, "ItemChangeContainer.Fail", $"{plr} {si}->{containerSyncid} missing-bag");
                        return;
                    }
                    if (!CheckIfBodyReachThisItem(contSi, body, GameplayRanges.ServerExchangeToleranceDistance, true)) {
                        ModLog.Flow(LogDomain.Container, "ItemChangeContainer.Fail", $"{plr} {si}->{contSi} bag-out-of-reach");
                        return;
                    }
                    ItemsContainerInfo ici = ItemGetContainerInfo(item);
                    if (ici.container_si == contSi)
                        return;
                    if (!LoadWouldTake(contSi.container, item, out string why)) {
                        ModLog.Flow(LogDomain.Container, "ItemChangeContainer.Fail", $"{plr} {si}->{contSi} {why}");
                        return;
                    }
                    bool inBag = ici.container != null && ici.inv_slot == null && !ici.is_wearing;
                    if (!inBag)
                        SafeUnloadItem(ici);
                    contSi.container.LoadItem(item);
                    if (item.transform.parent != contSi.container.transform) {
                        ModLog.Flow(LogDomain.Container, "ItemChangeContainer.Fail", $"{plr} {si}->{contSi} load-refused");
                        return;
                    }
                    NetObjectRegistry.SafeNewGO(si.go);
                    return;
                }

                SafeUnloadItem(ItemGetContainerInfo(item));
                Limb limb = item.transform.parent != null ? item.transform.parent.GetComponent<Limb>() : null;
                if (limb != null && limb.body != null)
                    Body_DropWearable_MultiplayerPatch.Force(limb.body, item);

                NetObjectRegistry.SafeNewGO(si.go);
                return;
            }
        }

        MpFlightRecorder.Record(LogDomain.Container, FlightEventKind.PacketReject, clientId.id, syncid.id, containerSyncid.id);
        ModLog.Flow(LogDomain.Container, "ItemChangeContainer.Rejected", $"client={clientId} {syncid}->{containerSyncid}");
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ThrowItem)]
    static void ServerReceiver__ThrowItem(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncid);
        reader.Get(out float force);

        if (!Util.IsWorldGenerated() || !Util.IsFinite(force))
            return;

        if (NetIdToSyncInfoDict.TryGetValue(syncid, out var isi)
            && isi.IsItem()
            && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var pb)) {
            Body body = pb.body;
            bool isHost = Util.IsBodyLocal(body);
            if (isHost || body.GetItem(body.handSlot) == isi.item) {
                force = Mathf.Clamp01(force);
                if (!isHost)
                    body.ThrowItem(force);
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(body.transform.position, $"S: ThrowItem {clientId} {isi} {force}");
                NetObjectRegistry.SafeNewGO(isi.go);
                return;
            }
        }

        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, clientId.id, syncid.id);
        ModLog.Flow(LogDomain.Item, "ThrowItem.Declined", $"client={clientId} syncId={syncid} force={force}");
    }

    static (Item, byte, Container) _Server_ReadOneItem(ScavPlayer plr, ref NetDataReader reader) {
        reader.Get(out byte invSlot);
        reader.Get(out knetid itemSyncId);
        if (plr.body == null || plr.body.slots == null || invSlot >= plr.body.slots.Length) {
            DenyPickup(plr, itemSyncId, "bad slot " + invSlot);
            return (null, invSlot, null);
        }
        if (TryGetItem(itemSyncId, out var isi, out var item)) {
            if (plr.body.conscious && CheckIfBodyReachThisItem(item, plr.body)) {
                if (!HostCanSeat(plr.body, item, invSlot, out string why)) {
                    DenyPickup(plr, itemSyncId, why);
                    return (null, invSlot, null);
                }
                Container bag = null;
                ItemsContainerInfo ici = ItemGetContainerInfo(item);
                if (ici != null && ici.container != null)
                    bag = ici.container;
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(plr.pos, $"S: ItemPickup slot:{invSlot} {isi}");
                return (item, invSlot, bag);
            }
            DenyPickup(plr, itemSyncId, "out of reach");
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(plr.pos, $"S: DENIED ItemPickup {plr} {isi}", Color.red);
            return (null, invSlot, null);
        }
        DenyPickup(plr, itemSyncId, "missing item");
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
            DebugHelp.OnNetEvent(plr.pos, $"S: DENIED ItemPickup {plr} {itemSyncId}", Color.red);
        return (null, invSlot, null);
    }

    static void DenyPickup(ScavPlayer plr, knetid itemId, string why) {
        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, plr.playerId.id, itemId.id);
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ItemPickupDENY);
        writer.Put(itemId);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        log.serverdeny($"ItemPickup {plr} {itemId}: {why}");
    }

    internal static bool HostCanSeat(Body body, Item item, int slot, out string why) {
        why = "slot blocked";
        if (item == null || item.Stats == null)
            return false;
        InventorySlot hold = body.slots[slot];
        if (hold == null)
            return false;
        if (item.Stats.onlyHoldInHands && !hold.isHand) {
            why = "hands only";
            return false;
        }
        int kids = hold.transform.childCount;
        if (kids > 1)
            return false;
        if (kids == 1 && hold.transform.GetChild(0).GetComponent<Item>() == null)
            return false;
        why = null;
        return true;
    }

    static void SeatPickup(Body body, Item item, byte slot, Container bag) {
        body.PickUpItem(item, slot, true);
        if (item != null && body.HoldingItem(item))
            return;
        if (bag != null && item != null) {
            Container_ForceLoadItem(item, bag);
            if (TryGetItemSyncInfo(item, out var si))
                si.WriteContainerInfo();
            log.serverdeny($"ItemPickup seat failed slot {slot} {item}, back in bag");
            return;
        }
        log.serverdeny($"ItemPickup seat failed slot {slot} {item}");
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemPickup)]
    static void ServerReceiver__ItemPickup(knetid clientId, NetDataReader reader) {
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out _) || plr.body == null)
            return;
        var item1 = _Server_ReadOneItem(plr, ref reader);
        var item2 = default((Item, byte, Container));
        bool has2 = !reader.EndOfData;
        if (has2)
            item2 = _Server_ReadOneItem(plr, ref reader);
        if (item1.Item1 == null && (!has2 || item2.Item1 == null))
            return;

        bool watch = ParentTestWatch(item1.Item1, item1.Item2, plr.body)
            || (has2 && ParentTestWatch(item2.Item1, item2.Item2, plr.body));
        if (watch) {
            ParentTest("swap begin " + ParentTestItem(item1.Item1) + " slot " + item1.Item2
                + " | " + ParentTestItem(item2.Item1) + " slot " + (has2 ? item2.Item2.ToString() : "-"));
        }

        var touched = new List<Item>(4);
        ParentTestSwap = watch;
        SuppressContainerWrite++;
        try {
            if (has2 && item2.Item1 != null)
                MoveIntoSlot(plr.body, item2.Item1, item2.Item2, item2.Item3, touched);
            if (item1.Item1 != null)
                MoveIntoSlot(plr.body, item1.Item1, item1.Item2, item1.Item3, touched);
        } finally {
            SuppressContainerWrite--;
            ParentTestSwap = false;
        }

        for (int i = 0; i < touched.Count; i++) {
            if (touched[i] == null || !TryGetItemSyncInfo(touched[i], out var si) || si == null)
                continue;
            si.WriteContainerInfo();
            if (watch)
                ParentTest("swap end " + ParentTestItem(touched[i]));
        }
    }

    static void MoveIntoSlot(Body body, Item item, byte slot, Container bag, List<Item> touched) {
        Item occupant = body.GetItem(slot);
        if (occupant != null && occupant != item) {
            body.DropItem(slot);
            if (!touched.Contains(occupant))
                touched.Add(occupant);
        }
        SafeUnloadItem(item);
        if (!touched.Contains(item))
            touched.Add(item);
        SeatPickup(body, item, slot, bag);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemPickupDENY)]
    static void ClientReceiver_ItemPickupDENY(knetid _, NetDataReader reader) {
        reader.Get(out knetid itemSyncId);
        ClearPendingParent(itemSyncId);
        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.CommitReceipt, 0, itemSyncId.id, 0, -1);

        if (!TryGetItem(itemSyncId, out var isi, out var item))
            return;

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
            DebugHelp.OnNetEvent(isi.transformPosition, $"C: DENIED ItemPickup {isi}", Color.red);

        if (!TakePickupBag(itemSyncId, out Container bag))
            bag = BagFromByte(isi);

        // 客户端拆包回退期间加 MoveCommit 重入保护，防止 SafeUnloadItem -> DropItem 反向给服务端发 MoveKind.World 回声包
        MoveCommit.PushApply();
        try {
            ItemsContainerInfo ici = ItemGetContainerInfo(item);
            if (ici.is_in_surface_inv) {
                if (ici.bodynpc != null && ici.bodynpc.IsBodyLocal()) {
                    int idx = Array.IndexOf(Client_last_inventory_state, isi);
                    if (idx >= 0) {
                        Client_last_inventory_state[idx] = null;
                    }
                }
                SafeUnloadItem(ici);
                if (bag != null) {
                    Container_ForceLoadItem(item, bag);
                    log.devevent($"CLIENT: ItemPickup denied {isi}, back in bag", item.transform.position);
                }
                isi.lastUpdateTick = Net.ServerTick;
            } else if (bag != null && item.transform.parent == null) {
                Container_ForceLoadItem(item, bag);
                log.devevent($"CLIENT: ItemPickup denied {isi}, back in bag", item.transform.position);
            }
            if (Util.TryGetLocalBody(out Body lb))
                RecordAndApplyInvState(lb);
        } finally {
            MoveCommit.PopApply();
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemDrop)]
    static void ServerReceiver__ItemDropWearable(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncid);

        if (Util.IsWorldGenerated()
            && TryGetItem(syncid, out var isi, out var item)
            && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var pb)
            && pb.body.conscious
            && item.transform.parent != null
            && CheckIfBodyReachThisItem(item, pb.body)) {
            if (ItemDropHitsBag(isi, item)) {
                log.serverdeny($"ItemDrop {syncid} already in a bag");
                return;
            }

            SafeUnloadItem(item);
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(pb.position, $"S: ItemDrop {DebugHelp.GetPlayerFullDebugString(clientId)} {isi}");
            NetObjectRegistry.SafeNewGO(isi.go);
            return;
        }

        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, clientId.id, syncid.id);
        ModLog.Flow(LogDomain.Item, "ItemDrop.Denied", $"client={clientId} syncId={syncid}");
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemWearing)]
    static void ServerReceiver__ItemWearing(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncid);

        if (Util.IsWorldGenerated()
            && TryGetItem(syncid, out var isi, out var item)
            && item.Stats.wearable
            && ScavPlayer.GetBodyFromClientId(clientId) is var body
            && body != null
            && body.alive
            && KM.DistanceCheck(isi.go.transform.position, body.transform.position, 14f)) {
            if (CheckIfBodyCanStealThisItem(item, body) && !Util.IsBodyLocal(body)) {
                SafeUnloadItem(item);
                item.transform.position = body.transform.position;
                Body_ForceWearWearable(body, item);
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(body.transform.position, $"S: ItemWearing {DebugHelp.GetPlayerFullDebugString(clientId)} {isi}");
            NetObjectRegistry.SafeNewGO(isi.go);
            return;
        }

        MpFlightRecorder.Record(LogDomain.Item, FlightEventKind.PacketReject, clientId.id, syncid.id);
        ModLog.Flow(LogDomain.Item, "ItemWearing.Declined", $"client={clientId} syncId={syncid}");
    }
}
