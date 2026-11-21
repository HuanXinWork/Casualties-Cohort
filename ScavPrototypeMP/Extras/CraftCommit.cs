using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public enum CraftReason : byte {
    Ok = 0,
    Missing = 1,
    Unreachable = 2,
    Taken = 3,
    Unconscious = 4,
    BadRecipe = 5,
    SpawnFailed = 6,
    StillSyncing = 7,
}

/// <summary>
/// 联机权威合成事务协调器（核心状态机、客户端发起入口、材料静默让位与网络条件/液体防回滚门控）。
/// 客户端在点击合成时预锁定候选材料，服务端原子扣除材料并生成产物，通过 <c>CLIENT_CraftReceipt</c> 同步回执。
/// </summary>
public static partial class CraftCommit {
    public struct CraftStep {
        public bool Pending;
        public bool Accepted;
        public CraftReason Reason;
    }

    static bool _stepOpen;
    static bool _stepDone;
    static uint _stepAttempt;
    static CraftStep _stepResult;

    const float TombstoneSeconds = 2f;
    const int Remember = 64;
    const int GateTicks = 240;
    const int LiquidTrustTicks = 45;

    enum PoolMode { SyncedReachable, AnyReachable, Wide }

    sealed class Claim {
        public ushort syncId;
        public bool exclusive;
        public float condition;
        public string liquidId;
        public float liquid;
    }

    sealed class Attempt {
        public uint session;
        public uint id;
        public ushort recipe;
        public ushort[] ids;
        public List<Claim> claims;
        public float bornAt;
        public float sentAt;
    }

    struct Saved {
        public Item item;
        public float condition;
        public bool liquid;
        public List<string> ids;
        public List<float> amounts;
    }

    struct Snap {
        public Item item;
        public float condition;
        public bool liquid;
        public List<string> ids;
        public List<float> amounts;
    }

    sealed class Liq {
        public byte id;
        public ushort ml;
    }

    sealed class Spend {
        public ushort syncId;
        public byte version;
        public bool destroyed;
        public float condition;
        public List<Liq> liquids;
    }

    sealed class Receipt {
        public uint session;
        public uint attempt;
        public bool accepted;
        public CraftReason reason;
        public ushort tick;
        public ushort resultId;
        public List<Spend> spends;
    }

    sealed class PlayerCrafts {
        public uint session;
        public readonly Dictionary<uint, Receipt> receipts = new Dictionary<uint, Receipt>();
        public readonly Queue<uint> order = new Queue<uint>();
    }

    sealed class Gate {
        public ushort tick;
        public byte version;
        public bool destroyed;
        public bool holdCondition;
        public bool conditionOpen;
        public float condition;
        public bool holdLiquid;
        public bool liquidOpen;
        public List<Liq> liquids;
    }

    static uint _session;
    static uint _next = 1;
    static bool _running;
    static int _depth;
    static bool _collecting;
    static int _poolFrame = -1;
    static List<Item> _pool;

    static readonly Dictionary<uint, Attempt> Attempts = new Dictionary<uint, Attempt>();
    static readonly HashSet<ushort> Exclusive = new HashSet<ushort>();
    static readonly Dictionary<ushort, float> ConditionHold = new Dictionary<ushort, float>();
    static readonly Dictionary<ushort, Dictionary<string, float>> LiquidHold = new Dictionary<ushort, Dictionary<string, float>>();
    static readonly Dictionary<ushort, float> Tombstones = new Dictionary<ushort, float>();
    static readonly List<Saved> SavedItems = new List<Saved>();
    static readonly Dictionary<knetid, PlayerCrafts> ServerCrafts = new Dictionary<knetid, PlayerCrafts>();
    static readonly Dictionary<ushort, Gate> Gates = new Dictionary<ushort, Gate>();
    static readonly Dictionary<ushort, byte> Silent = new Dictionary<ushort, byte>();
    static readonly Dictionary<int, int> YieldedFrom = new Dictionary<int, int>();
    static readonly HashSet<uint> Seen = new HashSet<uint>();
    static readonly Queue<uint> SeenOrder = new Queue<uint>();
    static readonly List<byte> SeenLiquidId = new List<byte>();
    static readonly List<ushort> SeenLiquidMl = new List<ushort>();
    static readonly List<uint> _expiredAttempts = new List<uint>();
    static readonly List<ushort> _drop = new List<ushort>();

    internal static void Tick() {
        if (!Net.IsRunning) {
            if (_running)
                ClearAll();
            _running = false;
            return;
        }
        _running = true;
        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);

        if (!Net.IsServer) {
            float now = Time.realtimeSinceStartup;
            if (Attempts.Count > 0) {
                // 先用临时列表收集超时在途合成事务再统一释放，避免遍历修改字典或永久锁死 _stepOpen
                _expiredAttempts.Clear();
                foreach (var pair in Attempts) {
                    Attempt attempt = pair.Value;
                    if (now - attempt.bornAt >= NetTimingConstants.CommitMaxLifetimeSeconds)
                        _expiredAttempts.Add(pair.Key);
                }
                if (_expiredAttempts.Count > 0) {
                    for (int i = 0; i < _expiredAttempts.Count; i++) {
                        uint deadId = _expiredAttempts[i];
                        uint recipeIdx = Attempts.TryGetValue(deadId, out Attempt deadAttempt) ? deadAttempt.recipe : 0u;
                        MpFlightRecorder.Record(LogDomain.Craft, FlightEventKind.CommitTimeout, 0, recipeIdx, deadId);
                        ModLog.Warning($"CraftCommit attempt {deadId} timed out after {NetTimingConstants.CommitMaxLifetimeSeconds:0.#}s (recipe={recipeIdx})", LogDomain.Craft);
                        Release(deadId);
                        if (_stepOpen && deadId == _stepAttempt) {
                            _stepOpen = false;
                            _stepDone = false;
                            _stepAttempt = 0;
                        }
                    }
                    _expiredAttempts.Clear();
                    RefreshIfOpen();
                }
                foreach (var pair in Attempts) {
                    Attempt attempt = pair.Value;
                    if (now - attempt.sentAt >= NetTimingConstants.CommitResendIntervalSeconds)
                        SendAttempt(attempt);
                }
            } else if (_stepOpen && !_stepDone) {
                _stepOpen = false;
                _stepAttempt = 0;
            }
        }
        ExpireTombstones();
        ExpireGates();
    }

    internal static void TryClick(PlayerCamera cam) {
        if (cam == null)
            return;
        Body body = Util.GetLocalBody();
        if (body == null || cam.selectedRecipe < 0 || cam.selectedRecipe >= Recipes.recipes.Count) {
            FailLocal(cam, CraftReason.BadRecipe);
            return;
        }
        if (!body.conscious) {
            FailLocal(cam, CraftReason.Unconscious);
            return;
        }

        Recipe recipe = Recipes.recipes[cam.selectedRecipe];
        if (!TryClaim(body, recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason)) {
            FailLocal(cam, reason);
            return;
        }

        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;

        float now = Time.realtimeSinceStartup;
        var attempt = new Attempt {
            session = _session,
            id = id,
            recipe = (ushort)recipe.index,
            ids = ids,
            claims = claims,
            bornAt = now,
            sentAt = now,
        };
        Hold(attempt);

        MpFlightRecorder.Record(LogDomain.Craft, FlightEventKind.CommitSubmit, 0, (uint)recipe.index, id, ids.Length);
        ModLog.Flow(LogDomain.Craft, "CraftCommit.Submit", $"attempt={id} recipe={recipe.index}({recipe.fullName}) items={ids.Length}");

        if (Net.IsServer) {
            if (!body.TryGetNetBody(out NetBody nb)) {
                Release(id);
                FailLocal(cam, CraftReason.BadRecipe);
                return;
            }
            Receipt receipt = Commit(nb, _session, id, recipe, ids, 0);
            FinishLocal(cam, recipe, receipt);
            return;
        }

        SendAttempt(attempt);
        cam.RefreshRecipeList();
    }

    public static CraftStep Request(PlayerCamera cam, Recipe recipe) {
        if (_stepOpen) {
            if (!_stepDone)
                return new CraftStep { Pending = true, Accepted = false, Reason = CraftReason.Ok };
            CraftStep done = _stepResult;
            _stepOpen = false;
            _stepDone = false;
            return done;
        }

        if (cam == null || recipe == null || !Net.IsRunning)
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };

        int index = recipe.index;
        if (Recipes.recipes == null || index < 0 || index >= Recipes.recipes.Count) {
            FailLocal(cam, CraftReason.BadRecipe);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
        }

        recipe = Recipes.recipes[index];
        cam.selectedRecipe = index;
        Body body = Util.GetLocalBody();
        if (body == null) {
            FailLocal(cam, CraftReason.BadRecipe);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
        }
        if (!body.conscious) {
            FailLocal(cam, CraftReason.Unconscious);
            return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.Unconscious };
        }
        if (!TryClaim(body, recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason)) {
            FailLocal(cam, reason);
            return new CraftStep { Pending = false, Accepted = false, Reason = reason };
        }

        if (_session == 0)
            _session = (uint)UnityEngine.Random.Range(1, int.MaxValue);
        uint id = _next++;
        if (_next == 0)
            _next = 1;

        float now = Time.realtimeSinceStartup;
        var attempt = new Attempt {
            session = _session,
            id = id,
            recipe = (ushort)recipe.index,
            ids = ids,
            claims = claims,
            bornAt = now,
            sentAt = now,
        };
        Hold(attempt);

        MpFlightRecorder.Record(LogDomain.Craft, FlightEventKind.CommitSubmit, 0, (uint)recipe.index, id, ids.Length);
        ModLog.Flow(LogDomain.Craft, "CraftCommit.Request", $"attempt={id} recipe={recipe.index}({recipe.fullName}) items={ids.Length}");

        if (Net.IsServer) {
            if (!body.TryGetNetBody(out NetBody nb)) {
                Release(id);
                FailLocal(cam, CraftReason.BadRecipe);
                return new CraftStep { Pending = false, Accepted = false, Reason = CraftReason.BadRecipe };
            }
            Receipt receipt = Commit(nb, _session, id, recipe, ids, 0);
            FinishLocal(cam, recipe, receipt);
            return new CraftStep { Pending = false, Accepted = receipt.accepted, Reason = receipt.reason };
        }

        SendAttempt(attempt);
        cam.RefreshRecipeList();
        _stepOpen = true;
        _stepDone = false;
        _stepAttempt = id;
        return new CraftStep { Pending = true, Accepted = false, Reason = CraftReason.Ok };
    }

    internal static void OnReceipt(NetDataReader reader) {
        Receipt receipt = ReadReceipt(reader);
        bool mine = receipt.session == _session;
        if (mine && !Seen.Add(receipt.attempt))
            return;
        if (mine) {
            SeenOrder.Enqueue(receipt.attempt);
            while (SeenOrder.Count > Remember)
                Seen.Remove(SeenOrder.Dequeue());
            MpFlightRecorder.Record(LogDomain.Craft, FlightEventKind.CommitReceipt, 0, receipt.resultId, receipt.attempt, (int)receipt.reason);
            ModLog.Flow(LogDomain.Craft, "CraftCommit.Receipt", $"attempt={receipt.attempt} accepted={receipt.accepted} reason={receipt.reason} result={receipt.resultId}");
        }

        if (receipt.accepted)
            ApplySpend(receipt);
        if (!mine)
            return;

        if (receipt.accepted)
            NoteMade(receipt);
        else if (receipt.reason == CraftReason.Taken)
            Tombstone(receipt);

        Release(receipt.attempt);
        if (_stepOpen && receipt.attempt == _stepAttempt) {
            _stepDone = true;
            _stepResult = new CraftStep { Pending = false, Accepted = receipt.accepted, Reason = receipt.reason };
        }
        // 成功分支已在 Release 前执行 NoteMade（读取未释放的配方记录并播放音效与解锁成就），此处仅在失败时弹提示，避免重复叠播音效
        if (!receipt.accepted)
            Util.DoAlert(ReasonText(receipt.reason));
        RefreshIfOpen();
    }

    internal static bool EatDestroyed(SyncInfo si) {
        if (si == null || si.item == null)
            return false;
        ushort id = (ushort)si.syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || !gate.destroyed)
            return false;
        if (si.Version != gate.version)
            return false;
        Consume(si.item);
        return true;
    }

    internal static void StepAside(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        int from = -1;
        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
        if (ici.inv_slot != null)
            from = ici.inv_slot.slot;
        if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
            Silent[(ushort)si.syncId] = si.Version;
        MoveCommit.PushApply();
        ItemSync.SuppressContainerWrite++;
        try {
            ItemSync.SafeUnloadItem(item);
            Freeze(item);
        } finally {
            ItemSync.SuppressContainerWrite--;
            MoveCommit.PopApply();
        }
        YieldedFrom[inst] = from;
    }

    internal static void FinishAside(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        bool was = YieldedFrom.ContainsKey(inst);
        ushort sid = 0;
        bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (had)
            sid = (ushort)si.syncId;
        YieldedFrom.Remove(inst);
        if (had)
            Silent.Remove(sid);
        if (was && item.rb != null && item.transform.parent == null)
            item.rb.simulated = true;
    }

    internal static void ReleaseIfLeaving(Item item, int slot) {
        if (item == null || !YieldedFrom.TryGetValue(item.GetInstanceID(), out int from))
            return;
        if (from >= 0 && from != slot)
            FinishAside(item);
    }

    internal static void Consume(Item item) {
        if (item == null)
            return;
        int inst = item.GetInstanceID();
        ushort sid = 0;
        bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (had)
            sid = (ushort)si.syncId;
        StepAside(item);
        if (item != null)
            UnityEngine.Object.Destroy(item.gameObject);
        YieldedFrom.Remove(inst);
        if (had)
            Silent.Remove(sid);
    }

    internal static bool Yielded(Item item) => item != null && YieldedFrom.ContainsKey(item.GetInstanceID());

    internal static bool Silenced(SyncInfo si) {
        if (si == null)
            return false;
        return Silent.TryGetValue((ushort)si.syncId, out byte ver) && ver == si.Version;
    }

    static void Freeze(Item item) {
        if (item == null || item.rb == null)
            return;
        item.rb.velocity = Vector2.zero;
        item.rb.angularVelocity = 0f;
        item.rb.simulated = false;
    }

    internal static bool BlockCondition(knetid syncId, float incoming) {
        ushort id = (ushort)syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || gate.destroyed || !gate.holdCondition || gate.conditionOpen)
            return false;
        if (OtherGen(id, gate))
            return false;
        if (incoming <= gate.condition + 0.0001f) {
            gate.conditionOpen = true;
            Relax(id, gate);
            return false;
        }
        return true;
    }

    internal static void ClearSeenLiquids() {
        SeenLiquidId.Clear();
        SeenLiquidMl.Clear();
    }

    internal static void SeeLiquid(byte id, ushort ml) {
        SeenLiquidId.Add(id);
        SeenLiquidMl.Add(ml);
    }

    internal static bool SkipSeenLiquids(knetid syncId) {
        ushort id = (ushort)syncId;
        if (!Gates.TryGetValue(id, out Gate gate) || gate.destroyed || !gate.holdLiquid || gate.liquidOpen)
            return false;
        if (OtherGen(id, gate))
            return false;
        if (LiquidsLookStale(gate))
            return true;
        if (!LiquidsCaughtUp(gate) && LiteEntitySystem.Utils.SequenceDiff(Net.ServerTick, gate.tick) < LiquidTrustTicks)
            return true;
        gate.liquidOpen = true;
        Relax(id, gate);
        return false;
    }

    static bool LiquidsLookStale(Gate gate) {
        if (gate.liquids == null)
            return false;
        for (int i = 0; i < SeenLiquidId.Count; i++) {
            ushort allowed = 0;
            for (int j = 0; j < gate.liquids.Count; j++) {
                if (gate.liquids[j].id == SeenLiquidId[i])
                    allowed = gate.liquids[j].ml;
            }
            if (SeenLiquidMl[i] > allowed)
                return true;
        }
        return false;
    }

    static bool LiquidsCaughtUp(Gate gate) {
        if (gate.liquids == null)
            return true;
        for (int i = 0; i < gate.liquids.Count; i++) {
            bool found = false;
            for (int j = 0; j < SeenLiquidId.Count; j++) {
                if (SeenLiquidId[j] != gate.liquids[i].id)
                    continue;
                found = true;
                if (SeenLiquidMl[j] > gate.liquids[i].ml)
                    return false;
            }
            if (!found && gate.liquids[i].ml > 0)
                return false;
        }
        return true;
    }

    static bool OtherGen(ushort id, Gate gate) {
        knetid key = id;
        if (!ItemSync.TryGetItemSyncInfo(key, out SyncInfo si) || si == null)
            return false;
        return si.Version != gate.version;
    }

    static void Relax(ushort id, Gate gate) {
        if (gate.destroyed)
            return;
        if ((!gate.holdCondition || gate.conditionOpen) && (!gate.holdLiquid || gate.liquidOpen))
            Gates.Remove(id);
    }

    static void ExpireGates() {
        if (Gates.Count == 0)
            return;
        _drop.Clear();
        foreach (var pair in Gates) {
            if (LiteEntitySystem.Utils.SequenceDiff(Net.ServerTick, pair.Value.tick) > GateTicks)
                _drop.Add(pair.Key);
        }
        for (int i = 0; i < _drop.Count; i++)
            Gates.Remove(_drop[i]);
    }

    public static void Forget(knetid player) {
        ServerCrafts.Remove(player);
    }

    static void ClearAll() {
        _session = 0;
        _next = 1;
        _stepOpen = false;
        _stepDone = false;
        _stepAttempt = 0;
        _pool = null;
        _poolFrame = -1;
        Attempts.Clear();
        Exclusive.Clear();
        ConditionHold.Clear();
        LiquidHold.Clear();
        Tombstones.Clear();
        SavedItems.Clear();
        ServerCrafts.Clear();
        Gates.Clear();
        Silent.Clear();
        YieldedFrom.Clear();
        Seen.Clear();
        SeenOrder.Clear();
        _expiredAttempts.Clear();
    }

    static string ReasonText(CraftReason reason) {
        switch (reason) {
            case CraftReason.Missing: return Lang.Get("craft_deny_missing");
            case CraftReason.Unreachable: return Lang.Get("craft_deny_unreachable");
            case CraftReason.Taken: return Lang.Get("craft_deny_taken");
            case CraftReason.Unconscious: return Lang.Get("craft_deny_unconscious");
            case CraftReason.BadRecipe: return Lang.Get("craft_deny_badrecipe");
            case CraftReason.SpawnFailed: return Lang.Get("craft_deny_spawnfailed");
            case CraftReason.StillSyncing: return Lang.Get("craft_deny_stillsyncing");
            default: return Lang.Get("craft_deny_default");
        }
    }
}

[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetItemsForRecipe))]
[HarmonyPriority(Priority.First)]
static class CraftCommit_GetItems {
    static bool Prefix(Recipe __instance, ref List<Item> __result) {
        if (!Net.IsRunning)
            return true;
        __result = CraftCommit.MatchList(__instance, thorough: false);
        return false;
    }
}

[HarmonyPatch(typeof(Recipe), nameof(Recipe.GetItemsForRecipeThorough))]
[HarmonyPriority(Priority.First)]
static class CraftCommit_GetItemsThorough {
    static bool Prefix(Recipe __instance, ref List<Item> __result) {
        if (!Net.IsRunning)
            return true;
        __result = CraftCommit.MatchList(__instance, thorough: true);
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.RefreshRecipeList))]
static class CraftCommit_Refresh {
    static void Prefix() {
        if (Net.IsRunning)
            CraftCommit.OpenPool();
    }

    static void Postfix() {
        CraftCommit.ClosePool();
    }
}
