using System.Collections.Generic;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public static partial class MoveCommit {
    static void Send(Attempt attempt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemMove);
        writer.Put(attempt.session);
        writer.Put(attempt.id);
        int count = attempt.moves.Count;
        if (count > 16)
            count = 16;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Move move = attempt.moves[i];
            writer.Put(move.item);
            writer.Put((byte)move.kind);
            writer.Put(move.destNet);
            writer.Put(move.destSlot);
        }
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        attempt.sentAt = Time.realtimeSinceStartup;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ItemMove)]
    static void Server_ItemMove(knetid clientId, NetDataReader reader) {
        uint session = reader.GetUInt();
        uint attempt = reader.GetUInt();
        int count = reader.GetByte();
        var moves = new List<Move>(count);
        for (int i = 0; i < count && !reader.EndOfData; i++) {
            moves.Add(new Move {
                item = reader.GetUShort(),
                kind = (MoveKind)reader.GetByte(),
                destNet = reader.GetUShort(),
                destSlot = reader.GetByte(),
            });
        }
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody nb) || plr.body == null)
            return;

        Book book = BookFor(plr.playerId, session);
        if (book.receipts.TryGetValue(attempt, out Receipt prior)) {
            SendReceipt(plr.playerId, prior);
            return;
        }

        Receipt receipt = Commit(nb.body, session, attempt, moves, clientId.id, out bool terminal);
        if (receipt == null)
            return;
        if (terminal)
            Store(book, receipt);
        SendReceipt(plr.playerId, receipt);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_ItemMoveReceipt)]
    static void Client_ItemMoveReceipt(knetid _, NetDataReader reader) {
        Apply(ReadReceipt(reader));
    }

    static Receipt Commit(Body body, uint session, uint attempt, List<Move> moves, ushort clientId, out bool terminal) {
        terminal = true;
        if (moves == null || moves.Count == 0)
            return null;
        if (body == null || !body.conscious)
            return Refuse(session, attempt, MoveReason.Unconscious, moves, clientId);

        for (int i = 0; i < moves.Count; i++) {
            MoveReason reason = Check(body, moves[i]);
            if (reason == MoveReason.StillSyncing) {
                terminal = false;
                return new Receipt {
                    session = session,
                    attempt = attempt,
                    accepted = false,
                    reason = MoveReason.StillSyncing,
                    rows = new List<Row>(),
                };
            }
            if (reason != MoveReason.Ok)
                return Refuse(session, attempt, reason, moves, clientId);
        }

        var touched = new List<ushort>(moves.Count + 2);
        ItemSync.SuppressContainerWrite++;
        _commit++;
        _applying = true;
        try {
            for (int i = 0; i < moves.Count; i++)
                ApplyMove(body, moves[i], touched);
        } finally {
            _applying = false;
            _commit--;
            ItemSync.SuppressContainerWrite--;
        }

        for (int i = 0; i < touched.Count; i++) {
            if (!ItemSync.TryGetItemSyncInfo((knetid)touched[i], out SyncInfo si) || si == null)
                continue;
            si.WriteContainerInfo();
        }

        var rows = new List<Row>(touched.Count);
        for (int i = 0; i < touched.Count; i++)
            rows.Add(Current(touched[i]));
        for (int i = 0; i < moves.Count; i++) {
            bool listed = false;
            for (int j = 0; j < rows.Count; j++) {
                if (rows[j].item == moves[i].item)
                    listed = true;
            }
            if (!listed)
                rows.Add(Current(moves[i].item));
        }
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = Landed(moves, rows),
            reason = MoveReason.Ok,
            rows = rows,
        };
    }

    static MoveReason Check(Body body, Move move) {
        if (!ItemSync.NetIdToSyncInfoDict.TryGetValue(move.item, out SyncInfo si) || si == null)
            return MoveReason.StillSyncing;
        if (si.IsDestroyed || si.go == null)
            return MoveReason.Missing;
        Item item = si.item;
        if (item == null)
            return MoveReason.Missing;
        if (!ItemSync.CheckIfBodyCanStealThisItem(item, body))
            return MoveReason.Taken;
        if (!ItemSync.CheckIfBodyReachThisItem(item, body))
            return MoveReason.Unreachable;

        if (move.kind == MoveKind.World)
            return MoveReason.Ok;
        if (move.kind == MoveKind.Bag) {
            if (move.destNet == 0 || !NetObjectRegistry.TryGetSyncInfo(move.destNet, out SyncInfo box) || box == null || box.go == null)
                return MoveReason.StillSyncing;
            Container cont = box.container;
            if (cont == null && !box.go.TryGetComponent(out cont))
                return MoveReason.BrokenBag;
            if (ItemSync.BrokenBag(box, cont))
                return MoveReason.BrokenBag;
            return MoveReason.Ok;
        }
        if (!NetBody.TryGetNetBodyFromId(move.destNet, out NetBody owner) || owner.body == null)
            return MoveReason.Unconscious;
        if (move.kind == MoveKind.Worn) {
            if (item.Stats == null || !item.Stats.wearable)
                return MoveReason.BadSlot;
            Limb limb = owner.body.LimbByName(item.Stats.desiredWearLimb);
            if (limb == null || limb.dismembered)
                return MoveReason.BadSlot;
            return MoveReason.Ok;
        }
        if (owner.body.slots == null || move.destSlot >= owner.body.slots.Length)
            return MoveReason.BadSlot;
        if (!ItemSync.HostCanSeat(owner.body, item, move.destSlot, out _))
            return MoveReason.BadSlot;
        return MoveReason.Ok;
    }

    static void ApplyMove(Body asker, Move move, List<ushort> touched) {
        if (!ItemSync.TryGetItem(move.item, out _, out Item item) || item == null)
            return;
        BagSleep.Wake(item);
        if (move.kind == MoveKind.World) {
            ItemSync.SafeUnloadItem(item);
            HeldSeat.Release(item);
            Note(item, touched);
            return;
        }
        if (move.kind == MoveKind.Bag) {
            if (!NetObjectRegistry.TryGetSyncInfo(move.destNet, out SyncInfo box) || box == null || box.go == null)
                return;
            Container cont = box.container;
            if (cont == null)
                box.go.TryGetComponent(out cont);
            if (cont == null)
                return;
            ItemSync.SafeUnloadItem(item);
            ItemSync.Container_ForceLoadItem(item, cont);
            Note(item, touched);
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(move.destNet, out NetBody owner) || owner.body == null)
            return;
        if (move.kind == MoveKind.Worn) {
            ItemSync.SafeUnloadItem(item);
            ItemSync.Body_ForceWearWearable(owner.body, item);
            Note(item, touched);
            return;
        }
        Item occupant = owner.body.GetItem(move.destSlot);
        if (occupant != null && occupant != item) {
            owner.body.DropItem(move.destSlot);
            Note(occupant, touched);
        }
        ItemSync.SafeUnloadItem(item);
        owner.body.PickUpItem(item, move.destSlot, true);
        if (!owner.body.HoldingItem(item))
            HeldSeat.Seat(owner.body, item, move.destSlot);
        Note(item, touched);
    }

    static void Note(Item item, List<ushort> touched) {
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        ushort id = si.syncId;
        if (id == 0 || touched.Contains(id))
            return;
        touched.Add(id);
    }

    static bool Landed(List<Move> moves, List<Row> rows) {
        for (int i = 0; i < moves.Count; i++) {
            bool found = false;
            for (int j = 0; j < rows.Count; j++) {
                if (rows[j].item != moves[i].item)
                    continue;
                found = true;
                if (!Matches(moves[i], rows[j]))
                    return false;
            }
            if (!found)
                return false;
        }
        return true;
    }

    static bool Matches(Move move, Row row) {
        return SamePlace(move, row.data, row.net);
    }

    static Receipt Refuse(uint session, uint attempt, MoveReason reason, List<Move> moves, ushort clientId) {
        var rows = new List<Row>(moves.Count);
        for (int i = 0; i < moves.Count; i++)
            rows.Add(Current(moves[i].item));

        uint firstItem = moves.Count > 0 ? moves[0].item : 0u;
        MpFlightRecorder.Record(LogDomain.Container, FlightEventKind.PacketReject, clientId, firstItem, attempt, (int)reason);
        ModLog.Flow(LogDomain.Container, "MoveCommit.Refuse", $"client={clientId} attempt={attempt} reason={reason} first={firstItem}");
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = false,
            reason = reason,
            rows = rows,
        };
    }

    static Row Current(ushort id) {
        var row = new Row { item = id };
        if (!ItemSync.TryGetItemSyncInfo((knetid)id, out SyncInfo si) || si == null || si.item == null)
            return row;
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(si.item);
        row.data = ici.containerExtraData1;
        row.net = ici.knetid;
        return row;
    }

    static void Apply(Receipt receipt) {
        if (receipt == null || receipt.reason == MoveReason.StillSyncing)
            return;

        uint firstItem = receipt.rows != null && receipt.rows.Count > 0 ? receipt.rows[0].item : 0u;
        MpFlightRecorder.Record(LogDomain.Container, FlightEventKind.CommitReceipt, 0, firstItem, receipt.attempt, (int)receipt.reason);
        ModLog.Flow(LogDomain.Container, "MoveCommit.Receipt", $"attempt={receipt.attempt} accepted={receipt.accepted} reason={receipt.reason}");

        if (Attempts.TryGetValue(receipt.attempt, out Attempt attempt))
            Release(attempt);
        if (receipt.reason == MoveReason.Missing) {
            DestroyGone(receipt);
            return;
        }
        if (receipt.rows == null)
            return;

        _applying = true;
        try {
            for (int i = 0; i < receipt.rows.Count; i++) {
                if (!Fresh(receipt.rows[i].item, receipt.attempt))
                    continue;
                NoteLatch(receipt.rows[i]);
                ApplyRow(receipt.rows[i]);
            }
        } finally {
            // 退出拆包保护前统一对齐本地槽位快照，阻断下一物理帧把刚落位的结果当本地新变更反向发包
            if (Util.TryGetLocalBody(out Body lb))
                ItemSync.RecordAndApplyInvState(lb);
            _applying = false;
        }
    }

    static void NoteLatch(Row row) {
        Latches[row.item] = new Latch {
            data = row.data,
            net = row.net,
            until = Time.realtimeSinceStartup + NetTimingConstants.CommitResendIntervalSeconds,
        };
    }

    static void DestroyGone(Receipt receipt) {
        if (receipt.rows == null)
            return;
        _applying = true;
        try {
            for (int i = 0; i < receipt.rows.Count; i++) {
                if (!Fresh(receipt.rows[i].item, receipt.attempt))
                    continue;
                if (!ItemSync.TryGetItem(receipt.rows[i].item, out _, out Item item) || item == null)
                    continue;
                UnityEngine.Object.Destroy(item.gameObject);
            }
        } finally {
            if (Util.TryGetLocalBody(out Body lb))
                ItemSync.RecordAndApplyInvState(lb);
            _applying = false;
        }
    }

    static void ApplyRow(Row row) {
        if (!ItemSync.TryGetItem(row.item, out _, out Item item) || item == null)
            return;
        BagSleep.Wake(item);
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        if (ici.containerExtraData1 == row.data && ici.knetid.id == row.net) {
            if (ContainerEncoding.IsLooseInWorld(row.data))
                HeldSeat.Release(item);
            return;
        }
        if (ContainerEncoding.IsLooseInWorld(row.data)) {
            ItemSync.SafeUnloadItem(item);
            HeldSeat.Release(item);
            return;
        }
        if (ContainerEncoding.IsInsideContainer(row.data)) {
            if (!NetObjectRegistry.TryGetSyncInfo(row.net, out SyncInfo box) || box == null || box.go == null)
                return;
            Container cont = box.container;
            if (cont == null)
                box.go.TryGetComponent(out cont);
            if (cont == null || ItemSync.BrokenBag(box, cont))
                return;
            ItemSync.SafeUnloadItem(item);
            ItemSync.Container_ForceLoadItem(item, cont);
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(row.net, out NetBody owner) || owner.body == null)
            return;
        if (ContainerEncoding.IsWornOnBody(row.data)) {
            ItemSync.SafeUnloadItem(item);
            ItemSync.Body_ForceWearWearable(owner.body, item);
            return;
        }
        if (!ContainerEncoding.TryDecodeBodySlot(row.data, out int slot))
            return;
        if (owner.body.slots == null || slot < 0 || slot >= owner.body.slots.Length)
            return;
        Item occupant = owner.body.GetItem(slot);
        if (occupant != null && occupant != item)
            owner.body.DropItem(slot);
        ItemSync.SafeUnloadItem(item);
        owner.body.PickUpItem(item, slot, true);
        if (!owner.body.HoldingItem(item))
            HeldSeat.Seat(owner.body, item, slot);
        if (owner.IsBodyLocal())
            ItemSync.RecordAndApplyInvState(owner.body);
    }

    static Book BookFor(knetid playerId, uint session) {
        if (!Books.TryGetValue(playerId, out Book book)) {
            book = new Book { session = session };
            Books[playerId] = book;
            return book;
        }
        if (book.session != session) {
            book.session = session;
            book.receipts.Clear();
            book.order.Clear();
        }
        return book;
    }

    static void Store(Book book, Receipt receipt) {
        if (book.receipts.ContainsKey(receipt.attempt))
            return;
        book.receipts[receipt.attempt] = receipt;
        book.order.Enqueue(receipt.attempt);
        while (book.order.Count > Remember)
            book.receipts.Remove(book.order.Dequeue());
    }

    static void SendReceipt(knetid playerId, Receipt receipt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ItemMoveReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, playerId);
    }

    static void WriteReceipt(NetDataWriter writer, Receipt receipt) {
        writer.Put(receipt.session);
        writer.Put(receipt.attempt);
        writer.Put(receipt.accepted ? (byte)1 : (byte)0);
        writer.Put((byte)receipt.reason);
        int count = receipt.rows != null ? receipt.rows.Count : 0;
        if (count > 16)
            count = 16;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Row row = receipt.rows[i];
            writer.Put(row.item);
            writer.Put(row.data);
            writer.Put(row.net);
        }
    }

    static Receipt ReadReceipt(NetDataReader reader) {
        var receipt = new Receipt {
            session = reader.GetUInt(),
            attempt = reader.GetUInt(),
            accepted = reader.GetByte() != 0,
            reason = (MoveReason)reader.GetByte(),
        };
        int count = reader.GetByte();
        receipt.rows = new List<Row>(count);
        for (int i = 0; i < count && !reader.EndOfData; i++) {
            receipt.rows.Add(new Row {
                item = reader.GetUShort(),
                data = reader.GetByte(),
                net = reader.GetUShort(),
            });
        }
        return receipt;
    }

    public static void Forget(knetid player) {
        Books.Remove(player);
    }
}
