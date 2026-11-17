using System.Collections.Generic;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public enum MoveKind : byte {
    World = 0,
    Bag = 1,
    Slot = 2,
    Worn = 3,
}

public enum MoveReason : byte {
    Ok = 0,
    Missing = 1,
    Unreachable = 2,
    Taken = 3,
    Unconscious = 4,
    BadSlot = 5,
    BrokenBag = 6,
    StillSyncing = 7,
}

/// <summary>
/// 物品与容器原子移动提交器（客户端手势收敛、超时回收、重发调度与回执锁存）。
/// 单次手势（拖拽、换手、热键槽位互换）合并为一个原子事务提交服务端裁决，并依据回执落位消除双发回声。
/// </summary>
public static partial class MoveCommit {
    public struct Move {
        public ushort item;
        public MoveKind kind;
        public ushort destNet;
        public byte destSlot;
    }

    struct Row {
        public ushort item;
        public byte data;
        public ushort net;
    }

    sealed class Attempt {
        public uint session;
        public uint id;
        public List<Move> moves;
        public float bornAt;
        public float sentAt;
    }

    sealed class Receipt {
        public uint session;
        public uint attempt;
        public bool accepted;
        public MoveReason reason;
        public List<Row> rows;
    }

    sealed class Book {
        public uint session;
        public readonly Dictionary<uint, Receipt> receipts = new Dictionary<uint, Receipt>();
        public readonly Queue<uint> order = new Queue<uint>();
    }

    struct Latch {
        public byte data;
        public ushort net;
        public float until;
    }

    const int Remember = 64;

    static uint _session;
    static uint _next = 1;
    static bool _running;
    static int _depth;
    static int _throwing;
    static int _commit;
    static bool _applying;
    static int _fromNet;
    static int _guardFrame = -1;

    static readonly Dictionary<uint, Attempt> Attempts = new Dictionary<uint, Attempt>();
    static readonly HashSet<ushort> Exclusive = new HashSet<ushort>();
    static readonly HashSet<ushort> Guard = new HashSet<ushort>();
    static readonly Dictionary<ushort, uint> Newest = new Dictionary<ushort, uint>();
    static readonly Dictionary<ushort, Latch> Latches = new Dictionary<ushort, Latch>();
    static readonly List<Item> Gesture = new List<Item>();
    static readonly Dictionary<knetid, Book> Books = new Dictionary<knetid, Book>();
    static readonly List<uint> DropIds = new List<uint>();
    static readonly List<Attempt> ExpiredAttempts = new List<Attempt>();

    internal static bool Held(ushort id) => Exclusive.Contains(id);

    internal static bool Held(Item item) {
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return false;
        ushort id = si.syncId;
        return id != 0 && Exclusive.Contains(id);
    }

    internal static bool Busy(Item item) {
        if (Held(item))
            return true;
        if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return false;
        ushort id = si.syncId;
        return id != 0 && Latches.ContainsKey(id);
    }

    internal static void Tick() {
        if (!Net.IsRunning) {
            if (_running)
                Clear();
            _running = false;
            return;
        }
        _running = true;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        float now = Time.realtimeSinceStartup;
        // 先用临时列表收集超过最大存活期的在途事务再统一释放，防止目标持续 StillSyncing 导致整局死锁
        if (Attempts.Count > 0) {
            ExpiredAttempts.Clear();
            foreach (var pair in Attempts) {
                Attempt attempt = pair.Value;
                if (now - attempt.bornAt >= NetTimingConstants.CommitMaxLifetimeSeconds)
                    ExpiredAttempts.Add(attempt);
            }
            for (int i = 0; i < ExpiredAttempts.Count; i++) {
                Attempt expired = ExpiredAttempts[i];
                uint firstSyncId = expired.moves != null && expired.moves.Count > 0 ? expired.moves[0].item : 0u;
                MpFlightRecorder.Record(LogDomain.Container, FlightEventKind.CommitTimeout, 0, firstSyncId, expired.id, expired.moves?.Count ?? 0);
                ModLog.Warning($"MoveCommit attempt {expired.id} timed out after {NetTimingConstants.CommitMaxLifetimeSeconds:0.#}s (firstItem={firstSyncId})", LogDomain.Container);
                Release(expired);
            }
            ExpiredAttempts.Clear();
        }

        if (Net.IsServer)
            return;

        foreach (var pair in Attempts) {
            Attempt attempt = pair.Value;
            if (now - attempt.sentAt >= NetTimingConstants.CommitResendIntervalSeconds)
                Send(attempt);
        }
    }

    internal static void Push() => _depth++;

    internal static void Pop() {
        if (_depth > 0)
            _depth--;
    }

    internal static void BeginThrow() => _throwing++;

    internal static void EndThrow() {
        if (_throwing > 0)
            _throwing--;
    }

    internal static void EndHands(Body body) {
        Pop();
        if (body != null) {
            Keep(body.GetItem(0));
            Keep(body.GetItem(1));
        }
        FinishGesture();
    }

    internal static void EndSwap(Body body, int slot1, int slot2) {
        Pop();
        if (body != null && body.slots != null) {
            if (slot1 >= 0 && slot1 < body.slots.Length)
                Keep(body.GetItem(slot1));
            if (slot2 >= 0 && slot2 < body.slots.Length)
                Keep(body.GetItem(slot2));
        }
        FinishGesture();
    }

    internal static void EndDrag(Item item) {
        Pop();
        Keep(item);
        FinishGesture();
    }

    internal static bool Applying => _applying || _fromNet > 0;

    internal static void PushApply() => _fromNet++;

    internal static void PopApply() {
        if (_fromNet > 0)
            _fromNet--;
    }

    internal static void SubmitLive(Item item) {
        if (item == null || Busy() || _fromNet > 0)
            return;
        if (_depth > 0) {
            Keep(item);
            return;
        }
        if (!TryDescribe(item, out Move move))
            return;
        if (Guarded(move.item))
            return;
        if (Latched(move))
            return;
        // 若已有目标落点完全一致的在途 Attempt，跳过下一物理帧 ItemContainerChanged 触发的重复提交
        if (SameInFlight(move))
            return;

        var moves = new List<Move>(1) { move };
        Submit(moves);
    }

    static bool SameInFlight(Move move) {
        if (!Newest.TryGetValue(move.item, out uint curId))
            return false;
        if (!Attempts.TryGetValue(curId, out Attempt cur) || cur == null || cur.moves == null)
            return false;
        for (int i = 0; i < cur.moves.Count; i++) {
            Move m = cur.moves[i];
            if (m.item != move.item)
                continue;
            return m.kind == move.kind
                && m.destNet == move.destNet
                && (move.kind != MoveKind.Slot || m.destSlot == move.destSlot);
        }
        return false;
    }

    static void Keep(Item item) {
        if (item == null)
            return;
        for (int i = 0; i < Gesture.Count; i++) {
            if (Gesture[i] == item)
                return;
        }
        Gesture.Add(item);
    }

    static void FinishGesture() {
        if (_depth > 0)
            return;
        if (Busy()) {
            Gesture.Clear();
            return;
        }
        var moves = new List<Move>(Gesture.Count);
        for (int i = 0; i < Gesture.Count; i++)
            TryAdd(Gesture[i], moves);
        Gesture.Clear();
        Submit(moves);
    }

    static bool Busy() => _throwing > 0 || _applying || _commit > 0;

    static void TryAdd(Item item, List<Move> moves) {
        if (!TryDescribe(item, out Move move))
            return;
        for (int i = 0; i < moves.Count; i++) {
            if (moves[i].item == move.item)
                return;
        }
        moves.Add(move);
    }

    static bool TryDescribe(Item item, out Move move) {
        move = default;
        if (item == null || !Net.IsRunning)
            return false;
        SyncInfo si;
        bool found = Net.IsServer
            ? NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out si)
            : ItemSync.TryGetSyncInfo(item, out si);
        if (!found || si == null || si.syncId.id == 0)
            return false;

        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        move.item = si.syncId;
        if (ici.is_wearing) {
            move.kind = MoveKind.Worn;
            move.destNet = ici.body_netid;
        } else if (ici.is_in_surface_inv) {
            move.kind = MoveKind.Slot;
            move.destNet = ici.body_netid;
            move.destSlot = ici.inv_slot_number;
        } else if (ici.has_container && ici.container_si != null) {
            move.kind = MoveKind.Bag;
            move.destNet = ici.container_si.syncId;
        } else {
            move.kind = MoveKind.World;
        }
        return true;
    }

    static void Submit(List<Move> moves) {
        if (moves == null || moves.Count == 0 || !Net.IsRunning)
            return;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;

        float now = Time.realtimeSinceStartup;
        var attempt = new Attempt {
            session = _session,
            id = id,
            moves = moves,
            bornAt = now,
            sentAt = now,
        };
        Hold(attempt);
        Attempts[attempt.id] = attempt;
        DropOlder(attempt);

        MpFlightRecorder.Record(LogDomain.Container, FlightEventKind.CommitSubmit, 0, moves[0].item, id, moves.Count);
        ModLog.Flow(LogDomain.Container, "MoveCommit.Submit", $"attempt={id} count={moves.Count} first={moves[0].item} kind={moves[0].kind}");

        if (Net.IsServer)
            FinishLocal(attempt);
        else
            Send(attempt);
    }

    static void FinishLocal(Attempt attempt) {
        Body body = Util.GetLocalBody();
        if (body == null || !body.TryGetNetBody(out NetBody nb) || nb.plr == null) {
            Release(attempt);
            return;
        }
        Receipt receipt = Commit(body, attempt.session, attempt.id, attempt.moves, 0, out bool terminal);
        if (terminal)
            Release(attempt);
        else
            attempt.sentAt = Time.realtimeSinceStartup;
        if (receipt != null && terminal)
            Apply(receipt);
    }

    static void Hold(Attempt attempt) {
        int frame = Time.frameCount;
        if (frame != _guardFrame) {
            _guardFrame = frame;
            Guard.Clear();
        }
        for (int i = 0; i < attempt.moves.Count; i++) {
            ushort id = attempt.moves[i].item;
            Exclusive.Add(id);
            Guard.Add(id);
            Newest[id] = attempt.id;
            Latches.Remove(id);
        }
    }

    static void DropOlder(Attempt newer) {
        DropIds.Clear();
        foreach (var pair in Attempts) {
            if (pair.Key == newer.id)
                continue;
            Attempt old = pair.Value;
            if (old.moves == null)
                continue;
            for (int i = old.moves.Count - 1; i >= 0; i--) {
                ushort id = old.moves[i].item;
                if (Newest.TryGetValue(id, out uint cur) && cur != old.id)
                    old.moves.RemoveAt(i);
            }
            if (old.moves.Count == 0)
                DropIds.Add(pair.Key);
        }
        for (int i = 0; i < DropIds.Count; i++)
            Attempts.Remove(DropIds[i]);
        DropIds.Clear();
    }

    static void Release(Attempt attempt) {
        Attempts.Remove(attempt.id);
        if (attempt.moves == null)
            return;
        for (int i = 0; i < attempt.moves.Count; i++) {
            ushort id = attempt.moves[i].item;
            if (Newest.TryGetValue(id, out uint cur) && cur != attempt.id)
                continue;
            Exclusive.Remove(id);
        }
    }

    static bool Fresh(ushort item, uint attempt) {
        if (!Newest.TryGetValue(item, out uint cur))
            return true;
        return cur == attempt;
    }

    static bool Guarded(ushort id) => _guardFrame == Time.frameCount && Guard.Contains(id);

    internal static bool StaleContainer(SyncInfo si) {
        if (si == null)
            return false;
        ushort id = si.syncId;
        if (id == 0 || !Latches.TryGetValue(id, out Latch latch))
            return false;
        if (si.container_data1.Value == latch.data && si.container_netId.Value.id == latch.net) {
            Latches.Remove(id);
            return false;
        }
        if (Time.realtimeSinceStartup >= latch.until) {
            Latches.Remove(id);
            return false;
        }
        return true;
    }

    static bool Latched(Move move) {
        if (!Latches.TryGetValue(move.item, out Latch latch))
            return false;
        if (Time.realtimeSinceStartup >= latch.until)
            return false;
        return SamePlace(move, latch.data, latch.net);
    }

    static bool SamePlace(Move move, byte data, ushort net) {
        if (move.kind == MoveKind.World)
            return ContainerEncoding.IsLooseInWorld(data);
        if (move.kind == MoveKind.Bag)
            return ContainerEncoding.IsInsideContainer(data) && net == move.destNet;
        if (move.kind == MoveKind.Worn)
            return ContainerEncoding.IsWornOnBody(data) && net == move.destNet;
        return data == ContainerEncoding.EncodeBodySlot(move.destSlot) && net == move.destNet;
    }

    static void Clear() {
        Attempts.Clear();
        Exclusive.Clear();
        Guard.Clear();
        Newest.Clear();
        Latches.Clear();
        Gesture.Clear();
        Books.Clear();
        DropIds.Clear();
        ExpiredAttempts.Clear();
        _fromNet = 0;
        _depth = 0;
        _throwing = 0;
        _commit = 0;
        _applying = false;
        _session = 0;
    }
}
