using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public static partial class CraftCommit {
    internal static List<Item> MatchList(Recipe recipe, bool thorough) {
        if (_depth > 0)
            return thorough ? new List<Item>() : null;
        _depth++;
        try {
            List<Item> pool = Pool(PoolMode.SyncedReachable);
            return Evaluate(recipe, pool, reserve: true, thorough: thorough, capture: false, out _);
        } finally {
            _depth--;
        }
    }

    internal static void OpenPool() {
        _poolFrame = Time.frameCount;
        _pool = null;
    }

    internal static void ClosePool() {
        _poolFrame = -1;
        _pool = null;
    }

    static void FailLocal(PlayerCamera cam, CraftReason reason) {
        Util.DoAlert(ReasonText(reason));
        cam.RefreshRecipeList();
    }

    static void FinishLocal(PlayerCamera cam, Recipe recipe, Receipt receipt) {
        Release(receipt.attempt);
        if (!Seen.Add(receipt.attempt))
            return;
        SeenOrder.Enqueue(receipt.attempt);
        if (!receipt.accepted) {
            if (receipt.reason == CraftReason.Taken)
                Tombstone(receipt);
            Util.DoAlert(ReasonText(receipt.reason));
        } else if (recipe != null) {
            recipe.hasMadeBefore = true;
        }
        cam.RefreshRecipeList();
    }

    static bool TryClaim(Body body, Recipe recipe, out List<Claim> claims, out ushort[] ids, out CraftReason reason) {
        claims = null;
        ids = null;
        reason = CraftReason.Missing;
        // 单次扫描背包与地面半径内的全部候选物品并分桶，消除缺料点击时重复执行 3 次 OverlapCircleAll 与射线遮挡查询
        CollectAllPools(body, out List<Item> synced, out List<Item> anyReachable, out List<Item> wide);
        List<Item> matched = Evaluate(recipe, synced, reserve: true, thorough: false, capture: true, out claims);
        if (matched != null && claims != null) {
            ids = UniqueIds(claims);
            reason = CraftReason.Ok;
            return true;
        }
        if (Evaluate(recipe, synced, reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.Missing;
            return false;
        }
        if (Evaluate(recipe, anyReachable, reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.StillSyncing;
            return false;
        }
        if (Evaluate(recipe, wide, reserve: false, thorough: false, capture: false, out _) != null) {
            reason = CraftReason.Unreachable;
            return false;
        }
        reason = CraftReason.Missing;
        return false;
    }

    static void CollectAllPools(Body body, out List<Item> syncedReachable, out List<Item> anyReachable, out List<Item> wide) {
        syncedReachable = new List<Item>();
        anyReachable = new List<Item>();
        wide = new List<Item>();
        if (body == null || _collecting)
            return;
        _collecting = true;
        try {
            List<Item> inventory = body.GetAllItemsThorough();
            for (int i = 0; i < inventory.Count; i++) {
                Item invItem = inventory[i];
                if (invItem == null)
                    continue;
                wide.Add(invItem);
                anyReachable.Add(invItem);
                if (ItemSync.TryGetSyncInfo(invItem, out SyncInfo si) && si != null)
                    syncedReachable.Add(invItem);
            }
            Collider2D[] hits = Physics2D.OverlapCircleAll(
                body.transform.position,
                GameplayRanges.CraftGroundScanRadius,
                PhysicsLayers.ItemMask);
            for (int i = 0; i < hits.Length; i++) {
                if (!hits[i].TryGetComponent(out Item item) || item == null)
                    continue;
                if (!body.DoPickupCheck(item, true))
                    continue;
                wide.Add(item);
                if (!ItemSync.CheckIfBodyReachThisItem(item, body))
                    continue;
                anyReachable.Add(item);
                if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null)
                    syncedReachable.Add(item);
            }
        } finally {
            _collecting = false;
        }
    }

    static List<Item> Pool(PoolMode mode) {
        if (mode == PoolMode.SyncedReachable && _poolFrame == Time.frameCount && _pool != null)
            return _pool;
        Body body = Util.GetLocalBody();
        List<Item> list = Collect(body, mode);
        if (mode == PoolMode.SyncedReachable && _poolFrame == Time.frameCount)
            _pool = list;
        return list;
    }

    static List<Item> Collect(Body body, PoolMode mode) {
        var list = new List<Item>();
        if (body == null || _collecting)
            return list;
        _collecting = true;
        try {
            List<Item> inventory = body.GetAllItemsThorough();
            for (int i = 0; i < inventory.Count; i++)
                AddItem(list, inventory[i], mode, reachable: true);
            Collider2D[] hits = Physics2D.OverlapCircleAll(
                body.transform.position,
                GameplayRanges.CraftGroundScanRadius,
                PhysicsLayers.ItemMask);
            for (int i = 0; i < hits.Length; i++) {
                if (!hits[i].TryGetComponent(out Item item))
                    continue;
                if (!body.DoPickupCheck(item, true))
                    continue;
                bool reach = ItemSync.CheckIfBodyReachThisItem(item, body);
                if (mode != PoolMode.Wide && !reach)
                    continue;
                AddItem(list, item, mode, reach);
            }
            return list;
        } finally {
            _collecting = false;
        }
    }

    static void AddItem(List<Item> list, Item item, PoolMode mode, bool reachable) {
        if (item == null)
            return;
        if (mode == PoolMode.Wide && !reachable) {
            list.Add(item);
            return;
        }
        bool synced = ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null;
        if (mode == PoolMode.SyncedReachable && !synced)
            return;
        list.Add(item);
    }

    static List<Item> Evaluate(Recipe recipe, List<Item> pool, bool reserve, bool thorough, bool capture, out List<Claim> claims) {
        claims = null;
        if (recipe == null || recipe.items == null)
            return thorough ? new List<Item>() : null;
        List<Item> working = pool;
        bool restore = false;
        try {
            if (reserve || Tombstones.Count > 0) {
                working = Filter(pool, reserve);
                restore = true;
            }
            var result = new List<Item>();
            var used = new List<Item>();
            for (int i = 0; i < recipe.items.Count; i++) {
                RecipeItem ingredient = recipe.items[i];
                Item found = ingredient.GetMatchingItem(working, used);
                if (found != null) {
                    result.Add(found);
                    if (ingredient.destroyItem && !ingredient.isLiquid)
                        used.Add(found);
                } else if (thorough) {
                    result.Add(null);
                } else {
                    return null;
                }
            }
            if (capture)
                claims = BuildClaims(recipe, result);
            if (capture && claims == null)
                return null;
            return result;
        } finally {
            if (restore)
                Restore();
        }
    }

    static List<Item> Filter(List<Item> pool, bool reserve) {
        var working = new List<Item>();
        SavedItems.Clear();
        for (int i = 0; i < pool.Count; i++) {
            Item item = pool[i];
            if (item == null)
                continue;
            if (ItemSync.TryGetSyncInfo(item, out SyncInfo si) && si != null) {
                ushort id = (ushort)si.syncId;
                if (Tombstoned(id))
                    continue;
                if (reserve && Exclusive.Contains(id))
                    continue;
                if (reserve)
                    SaveAndReduce(item, id);
            }
            working.Add(item);
        }
        return working;
    }

    static void SaveAndReduce(Item item, ushort id) {
        for (int i = 0; i < SavedItems.Count; i++) {
            if (SavedItems[i].item == item)
                return;
        }
        float condition = item.condition;
        List<string> ids = null;
        List<float> amounts = null;
        bool touch = false;
        if (ConditionHold.TryGetValue(id, out float hold) && hold > 0.0001f) {
            item.condition = Mathf.Max(0f, item.condition - hold);
            touch = true;
        }
        if (LiquidHold.TryGetValue(id, out Dictionary<string, float> byLiquid) && item.TryGetComponent(out WaterContainerItem wci)) {
            ids = new List<string>();
            amounts = new List<float>();
            for (int i = 0; i < wci.stack.Count; i++) {
                ids.Add(wci.stack[i].liquidId);
                amounts.Add(wci.stack[i].amount);
                if (byLiquid.TryGetValue(wci.stack[i].liquidId, out float take) && take > 0f) {
                    LiquidStack entry = wci.stack[i];
                    entry.amount = Mathf.Max(0f, entry.amount - take);
                    wci.stack[i] = entry;
                }
            }
            touch = true;
        }
        if (!touch)
            return;
        SavedItems.Add(new Saved {
            item = item,
            condition = condition,
            liquid = ids != null,
            ids = ids,
            amounts = amounts,
        });
    }

    static void Restore() {
        for (int i = 0; i < SavedItems.Count; i++) {
            Saved saved = SavedItems[i];
            if (saved.item == null)
                continue;
            saved.item.condition = saved.condition;
            if (!saved.liquid || !saved.item.TryGetComponent(out WaterContainerItem wci))
                continue;
            wci.stack.Clear();
            for (int j = 0; j < saved.ids.Count; j++)
                wci.stack.Add(new LiquidStack(saved.ids[j], saved.amounts[j]));
        }
        SavedItems.Clear();
    }

    static List<Claim> BuildClaims(Recipe recipe, List<Item> matched) {
        var claims = new List<Claim>();
        for (int i = 0; i < matched.Count; i++) {
            Item item = matched[i];
            if (item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
                return null;
            var claim = new Claim { syncId = (ushort)si.syncId };
            RecipeItem ingredient = recipe.items[i];
            if (ingredient.destroyItem && !ingredient.isLiquid)
                claim.exclusive = true;
            else if (ingredient.isLiquid)
                FillLiquid(ingredient, item, claim);
            else
                FillCondition(ingredient, item, claim);
            Merge(claims, claim);
        }
        return claims;
    }

    static void Merge(List<Claim> claims, Claim claim) {
        for (int i = 0; i < claims.Count; i++) {
            Claim have = claims[i];
            if (have.syncId != claim.syncId)
                continue;
            if (claim.exclusive)
                have.exclusive = true;
            have.condition += claim.condition;
            if (claim.liquid > 0f && claim.liquidId != null) {
                if (have.liquidId == null || have.liquidId == claim.liquidId) {
                    have.liquidId = claim.liquidId;
                    have.liquid += claim.liquid;
                } else {
                    have.exclusive = true;
                }
            }
            return;
        }
        claims.Add(claim);
    }

    static void FillCondition(RecipeItem ingredient, Item item, Claim claim) {
        try {
            if (ingredient.specific) {
                claim.condition = ingredient.minimumCondition;
                return;
            }
            CraftingQuality met = Item.GetQualityThatMeetsCriteria(ingredient.quality, item.Stats.qualities);
            if (met == null || Mathf.Abs(met.amount) < 0.0001f) {
                claim.exclusive = true;
                return;
            }
            claim.condition = ingredient.quality.amount / met.amount;
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Craft, "FillCondition.Fallback", ex.Message);
            claim.exclusive = true;
        }
    }

    static void FillLiquid(RecipeItem ingredient, Item item, Claim claim) {
        try {
            if (!item.TryGetComponent(out WaterContainerItem wci)) {
                claim.exclusive = true;
                return;
            }
            if (ingredient.specific) {
                List<float> drains = wci.CalculateDrainSingleLiquid(ingredient.specificId, ingredient.minimumCondition);
                float take = 0f;
                for (int i = 0; i < drains.Count; i++)
                    take += drains[i];
                claim.liquidId = ingredient.specificId;
                claim.liquid = take;
                return;
            }
            for (int i = 0; i < wci.stack.Count; i++) {
                LiquidStack stack = wci.stack[i];
                if (!Liquids.Registry.TryGetValue(stack.liquidId, out var value) || value == null)
                    continue;
                CraftingQuality met = Item.GetQualityThatMeetsCriteria(ingredient.quality, value.GetScaledQualities(stack.amount));
                if (met == null || Mathf.Abs(met.amount) < 0.0001f)
                    continue;
                float max = Mathf.Lerp(0f, stack.amount, ingredient.quality.amount / met.amount);
                claim.liquidId = stack.liquidId;
                claim.liquid = Mathf.Min(stack.amount, max);
                return;
            }
            claim.exclusive = true;
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Craft, "FillLiquid.Fallback", ex.Message);
            claim.exclusive = true;
        }
    }

    static ushort[] UniqueIds(List<Claim> claims) {
        var ids = new List<ushort>();
        for (int i = 0; i < claims.Count; i++) {
            ushort id = claims[i].syncId;
            if (!ids.Contains(id))
                ids.Add(id);
        }
        return ids.ToArray();
    }

    static void Hold(Attempt attempt) {
        Attempts[attempt.id] = attempt;
        for (int i = 0; i < attempt.claims.Count; i++)
            Fold(attempt.claims[i], 1f);
    }

    static void Release(uint id) {
        if (!Attempts.TryGetValue(id, out Attempt attempt))
            return;
        for (int i = 0; i < attempt.claims.Count; i++)
            Fold(attempt.claims[i], -1f);
        Attempts.Remove(id);
    }

    static void Fold(Claim claim, float sign) {
        if (claim.exclusive) {
            if (sign > 0f)
                Exclusive.Add(claim.syncId);
            else
                Exclusive.Remove(claim.syncId);
            return;
        }
        if (claim.condition > 0f) {
            float next = (ConditionHold.TryGetValue(claim.syncId, out float have) ? have : 0f) + sign * claim.condition;
            if (next <= 0.0001f)
                ConditionHold.Remove(claim.syncId);
            else
                ConditionHold[claim.syncId] = next;
        }
        if (claim.liquid <= 0f || claim.liquidId == null)
            return;
        if (!LiquidHold.TryGetValue(claim.syncId, out Dictionary<string, float> byLiquid)) {
            if (sign < 0f)
                return;
            byLiquid = new Dictionary<string, float>();
            LiquidHold[claim.syncId] = byLiquid;
        }
        float left = (byLiquid.TryGetValue(claim.liquidId, out float held) ? held : 0f) + sign * claim.liquid;
        if (left <= 0.0001f) {
            byLiquid.Remove(claim.liquidId);
            if (byLiquid.Count == 0)
                LiquidHold.Remove(claim.syncId);
        } else {
            byLiquid[claim.liquidId] = left;
        }
    }

    static bool Tombstoned(ushort id) {
        if (!Tombstones.TryGetValue(id, out float until))
            return false;
        knetid key = id;
        if (Time.time >= until || !ItemSync.TryGetItemSyncInfo(key, out _)) {
            Tombstones.Remove(id);
            return false;
        }
        return true;
    }

    static void Tombstone(Receipt receipt) {
        float until = Time.time + TombstoneSeconds;
        if (receipt.spends == null)
            return;
        for (int i = 0; i < receipt.spends.Count; i++)
            Tombstones[receipt.spends[i].syncId] = until;
    }

    static void ExpireTombstones() {
        if (Tombstones.Count == 0)
            return;
        _drop.Clear();
        foreach (var pair in Tombstones) {
            knetid key = pair.Key;
            if (Time.time >= pair.Value || !ItemSync.TryGetItemSyncInfo(key, out _))
                _drop.Add(pair.Key);
        }
        for (int i = 0; i < _drop.Count; i++)
            Tombstones.Remove(_drop[i]);
    }

    static bool Carried(Body body, Item item) {
        Transform t = item.transform.parent;
        while (t != null) {
            if (t == body.transform)
                return true;
            if (t.TryGetComponent(out InventorySlot slot) && slot.body == body)
                return true;
            if (t.TryGetComponent(out Limb limb) && limb.body == body)
                return true;
            t = t.parent;
        }
        return false;
    }

    internal static void NoteCarriedSync(Item item) {
        if (item == null || !Net.IsRunning)
            return;
        PlayerCamera cam = PlayerCamera.main;
        if (cam == null || cam.craftingPanel == null || !cam.craftingPanel.activeSelf)
            return;
        Body body = Util.GetLocalBody();
        if (body == null || !Carried(body, item))
            return;
        if (!ItemSync.TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        cam.RefreshRecipeList();
    }

    static void RefreshIfOpen() {
        PlayerCamera cam = PlayerCamera.main;
        if (cam != null && cam.craftingPanel != null && cam.craftingPanel.activeSelf)
            cam.RefreshRecipeList();
    }
}
