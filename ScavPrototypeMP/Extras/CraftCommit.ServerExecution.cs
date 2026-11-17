using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public static partial class CraftCommit {
    private const string TitaniumSlabItemId = "titaniumslab";
    private const string HeavyMetalAchievementId = "HEAVYMETAL";

    struct Held {
        public Item item;
        public InventorySlot slot;
        public Container container;
        public bool wearing;
        public Body body;
    }

    internal static void HandleServer(knetid clientId, uint session, uint attempt, ushort recipeIndex, ushort[] ids) {
        if (ids == null)
            ids = Array.Empty<ushort>();
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb) || plr == null || pb == null)
            return;

        PlayerCrafts crafts = CraftsFor(clientId, session);
        if (crafts.receipts.TryGetValue(attempt, out Receipt cached)) {
            SendReceipt(plr.playerId, cached);
            return;
        }

        if (plr.body == null || !plr.body.conscious) {
            DenyAndSend(plr, pb, crafts, session, attempt, null, CraftReason.Unconscious, ids, clientId.id);
            return;
        }
        if (recipeIndex >= Recipes.recipes.Count) {
            DenyAndSend(plr, pb, crafts, session, attempt, null, CraftReason.BadRecipe, ids, clientId.id);
            return;
        }

        Recipe recipe = Recipes.recipes[recipeIndex];
        Receipt receipt = Commit(pb, session, attempt, recipe, ids, clientId.id);
        SendReceipt(plr.playerId, receipt);
    }

    static Receipt Commit(NetBody pb, uint session, uint attempt, Recipe recipe, ushort[] ids, ushort clientId) {
        knetid playerId = pb.plr != null ? pb.plr.playerId : default;
        PlayerCrafts crafts = CraftsFor(playerId, session);
        if (crafts.receipts.TryGetValue(attempt, out Receipt cached))
            return cached;

        var resolved = new List<Item>();
        for (int i = 0; i < ids.Length; i++) {
            knetid syncId = ids[i];
            if (!ItemSync.TryGetItemSyncInfo(syncId, out SyncInfo si) || si == null || !si.IsItem() || si.item == null)
                return Store(crafts, Deny(session, attempt, CraftReason.Taken, ids, pb, recipe, clientId));
            if (!ItemSync.CheckIfBodyReachThisItem(si, pb.body))
                return Store(crafts, Deny(session, attempt, CraftReason.Unreachable, ids, pb, recipe, clientId));
            resolved.Add(si.item);
        }

        var favourites = new List<Item>();
        var favouriteBits = new List<bool>();
        List<Item> matched = null;
        try {
            for (int i = 0; i < resolved.Count; i++) {
                favourites.Add(resolved[i]);
                favouriteBits.Add(resolved[i].favourited);
                resolved[i].favourited = false;
            }
            matched = PlayerCamera_TryCraft_MultiplayerPatch.CheckIfTheseItemsAreEnoughForTheRecipe(recipe, resolved);
        } finally {
            for (int i = 0; i < favourites.Count; i++) {
                if (favourites[i] != null)
                    favourites[i].favourited = favouriteBits[i];
            }
        }

        if (matched == null)
            return Store(crafts, Deny(session, attempt, CraftReason.Missing, ids, pb, recipe, clientId));

        List<Held> lifted = LiftIngredients(matched, recipe);
        CraftSpawn spawn;
        try {
            spawn = PlayerCamera_TryCraft_MultiplayerPatch.RecipeResult_SpawnResult(recipe.INT, recipe.result, pb.body);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Craft, "CraftCommit.SpawnResult", ex);
            spawn = CraftSpawn.Fail(null);
        }
        if (spawn.failed) {
            spawn.Discard();
            PutBack(lifted);
            return Store(crafts, Deny(session, attempt, CraftReason.SpawnFailed, ids, pb, recipe, clientId));
        }

        ushort resultId = 0;
        if (spawn.item != null && ItemSync.TryGetSyncInfo(spawn.item, out SyncInfo spawned) && spawned != null)
            resultId = (ushort)spawned.syncId;

        ushort[] spendIds = new ushort[matched.Count];
        byte[] spendVers = new byte[matched.Count];
        for (int n = 0; n < matched.Count; n++) {
            if (matched[n] != null && ItemSync.TryGetSyncInfo(matched[n], out SyncInfo ingredient) && ingredient != null) {
                spendIds[n] = (ushort)ingredient.syncId;
                spendVers[n] = ingredient.Version;
            }
        }

        var snaps = new List<Snap>();
        var exclusive = new List<int>();
        try {
            for (int i = 0; i < matched.Count; i++) {
                RecipeItem ingredient = recipe.items[i];
                if (ingredient.destroyItem && !ingredient.isLiquid) {
                    exclusive.Add(i);
                    continue;
                }
                snaps.Add(TakeSnap(matched[i]));
                ingredient.UseItem(matched[i]);
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Craft, "CraftCommit.SpendPartial", ex);
            RestoreSnaps(snaps);
            spawn.Discard();
            PutBack(lifted);
            return Store(crafts, Deny(session, attempt, CraftReason.SpawnFailed, ids, pb, recipe, clientId));
        }

        for (int i = 0; i < exclusive.Count; i++) {
            int index = exclusive[i];
            Item item = matched[index];
            if (item == null)
                continue;
            int inst = item.GetInstanceID();
            ushort sid = 0;
            bool had = ItemSync.TryGetSyncInfo(item, out SyncInfo spent) && spent != null;
            if (had)
                sid = (ushort)spent.syncId;
            try {
                recipe.items[index].UseItem(item);
            } catch (Exception ex) {
                ModLog.Exception(LogDomain.Craft, "CraftCommit.SpendDestroy", ex);
            }
            YieldedFrom.Remove(inst);
            if (had)
                Silent.Remove(sid);
        }

        if (spawn.item != null && pb.body != null && !Carried(pb.body, spawn.item))
            pb.body.AutoPickUpItem(spawn.item);

        AcceptRewards(pb, recipe);
        var receipt = new Receipt {
            session = session,
            attempt = attempt,
            accepted = true,
            reason = CraftReason.Ok,
            tick = Net.ServerTick,
            resultId = resultId,
            spends = Describe(matched, recipe, spendIds, spendVers),
        };
        FixCraftOrder.LogResult(pb, recipe, true, CraftReason.Ok.ToString());
        receipt = Store(crafts, receipt);
        SendSpend(receipt, playerId);
        return receipt;
    }

    static List<Held> LiftIngredients(List<Item> matched, Recipe recipe) {
        var lifted = new List<Held>();
        for (int i = 0; i < matched.Count; i++) {
            RecipeItem ingredient = recipe.items[i];
            if (!ingredient.destroyItem || ingredient.isLiquid)
                continue;
            Item item = matched[i];
            if (item == null)
                continue;
            ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
            if (!ici.has_container)
                continue;
            var held = new Held {
                item = item,
                slot = ici.inv_slot,
                container = ici.container,
                wearing = ici.is_wearing,
                body = ici.inv_slot != null ? ici.inv_slot.body : (ici.bodynpc != null ? ici.bodynpc.body : null),
            };
            try {
                StepAside(item);
            } catch (Exception ex) {
                ModLog.Warning($"CraftCommit.LiftIngredient: {ex.Message}", LogDomain.Craft);
                continue;
            }
            lifted.Add(held);
        }
        return lifted;
    }

    static void PutBack(List<Held> lifted) {
        for (int i = 0; i < lifted.Count; i++) {
            Held held = lifted[i];
            if (held.item == null)
                continue;
            try {
                FinishAside(held.item);
                if (held.slot != null && held.body != null)
                    held.body.PickUpItem(held.item, held.slot.slot, true);
                else if (held.container != null)
                    held.container.LoadItem(held.item);
                else if (held.wearing && held.body != null)
                    held.body.WearWearable(held.item);
            } catch (Exception ex) {
                ModLog.Warning($"CraftCommit.PutBack: {ex.Message}", LogDomain.Craft);
            }
        }
    }

    static Snap TakeSnap(Item item) {
        var snap = new Snap { item = item, condition = item != null ? item.condition : 0f };
        if (item != null && item.TryGetComponent(out WaterContainerItem wci)) {
            snap.liquid = true;
            snap.ids = new List<string>();
            snap.amounts = new List<float>();
            for (int i = 0; i < wci.stack.Count; i++) {
                snap.ids.Add(wci.stack[i].liquidId);
                snap.amounts.Add(wci.stack[i].amount);
            }
        }
        return snap;
    }

    static void RestoreSnaps(List<Snap> snaps) {
        for (int i = 0; i < snaps.Count; i++) {
            Snap snap = snaps[i];
            if (snap.item == null)
                continue;
            snap.item.condition = snap.condition;
            if (!snap.liquid || !snap.item.TryGetComponent(out WaterContainerItem wci))
                continue;
            wci.stack.Clear();
            for (int j = 0; j < snap.ids.Count; j++)
                wci.stack.Add(new LiquidStack(snap.ids[j], snap.amounts[j]));
        }
    }

    static List<Spend> Describe(List<Item> matched, Recipe recipe, ushort[] spendIds, byte[] versions) {
        var spends = new List<Spend>();
        for (int i = 0; i < matched.Count; i++) {
            Item item = matched[i];
            bool gone = item == null;
            spends.Add(new Spend {
                syncId = spendIds[i],
                version = versions[i],
                destroyed = gone || (recipe.items[i].destroyItem && !recipe.items[i].isLiquid),
                condition = gone ? 0f : item.condition,
                liquids = ReadLiquids(item),
            });
        }
        return spends;
    }

    static List<Liq> ReadLiquids(Item item) {
        if (item == null || !item.TryGetComponent(out WaterContainerItem wci) || Item_SetupItems_Listener.LiquidIdRegistry == null)
            return null;
        var list = new List<Liq>();
        for (int i = 0; i < wci.stack.Count; i++) {
            if (!Item_SetupItems_Listener.LiquidIdRegistry.TryGetValue(wci.stack[i].liquidId, out byte netId))
                continue;
            list.Add(new Liq { id = netId, ml = LiquidStackPart.Pack(wci.stack[i].amount) });
        }
        return list.Count == 0 ? null : list;
    }

    static void AcceptRewards(NetBody pb, Recipe recipe) {
        if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
            DebugHelp.OnNetEvent(pb.body.transform.position, "S: TryCraft id:" + recipe.index + " - " + recipe.fullName);
        ServerMain.Server_AnnounceSound(pb.pos, VanillaAssetKeys.SoundCombine, ServerMain.GetListOfClientIdsExceptThis(pb));
        if (pb.IsLocal && pb.body != null)
            Sound.Play(VanillaAssetKeys.SoundCombine, pb.body.transform.position);
        if (pb.body != null && pb.body.TryGetNetBody(out NetBody known)) {
            if (!known.RecipesHasCrafterBefore.Contains(recipe.index)) {
                known.RecipesHasCrafterBefore.Add(recipe.index);
                known.body.happiness += 1f;
                known.body.skills.AddExp(2, 10f);
            }
            PlayerCheckpoint.SendCrafted(known);
        }
    }

    static Receipt Deny(uint session, uint attempt, CraftReason reason, ushort[] ids, NetBody pb, Recipe recipe, ushort clientId) {
        var spends = new List<Spend>();
        if (ids != null) {
            for (int i = 0; i < ids.Length; i++)
                spends.Add(new Spend { syncId = ids[i] });
        }
        uint recipeIdx = recipe != null ? (uint)recipe.index : 0u;
        MpFlightRecorder.Record(LogDomain.Craft, FlightEventKind.PacketReject, clientId, recipeIdx, attempt, (int)reason);
        ModLog.Flow(LogDomain.Craft, "CraftCommit.Deny", $"client={clientId} attempt={attempt} recipe={recipeIdx} reason={reason}");
        FixCraftOrder.LogResult(pb, recipe, false, reason.ToString());
        return new Receipt {
            session = session,
            attempt = attempt,
            accepted = false,
            reason = reason,
            tick = Net.ServerTick,
            spends = spends,
        };
    }

    static void DenyAndSend(ScavPlayer plr, NetBody pb, PlayerCrafts crafts, uint session, uint attempt, Recipe recipe, CraftReason reason, ushort[] ids, ushort clientId) {
        Receipt receipt = Store(crafts, Deny(session, attempt, reason, ids, pb, recipe, clientId));
        SendReceipt(plr.playerId, receipt);
    }

    static PlayerCrafts CraftsFor(knetid playerId, uint session) {
        if (!ServerCrafts.TryGetValue(playerId, out PlayerCrafts crafts)) {
            crafts = new PlayerCrafts { session = session };
            ServerCrafts[playerId] = crafts;
            return crafts;
        }
        if (crafts.session != session) {
            crafts.session = session;
            crafts.receipts.Clear();
            crafts.order.Clear();
        }
        return crafts;
    }

    static Receipt Store(PlayerCrafts crafts, Receipt receipt) {
        if (crafts.receipts.ContainsKey(receipt.attempt))
            return crafts.receipts[receipt.attempt];
        crafts.receipts[receipt.attempt] = receipt;
        crafts.order.Enqueue(receipt.attempt);
        while (crafts.order.Count > Remember)
            crafts.receipts.Remove(crafts.order.Dequeue());
        return receipt;
    }

    static void SendAttempt(Attempt attempt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_PlayerCamera_TryCraft);
        writer.Put(attempt.session);
        writer.Put(attempt.id);
        writer.Put(attempt.recipe);
        writer.PutArray(attempt.ids);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        attempt.sentAt = Time.realtimeSinceStartup;
    }

    static void SendReceipt(knetid playerId, Receipt receipt) {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CraftReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, playerId);
    }

    static void SendSpend(Receipt receipt, knetid crafterPlayerId) {
        if (receipt == null || !receipt.accepted)
            return;
        // 排除房主与合成发起者本人（发起者已由 HandleServer 的 SendReceipt 单独接收），避免给发起者双发两份相同的可靠有序回执
        List<knetid> ids = ServerMain.GetListOfClientIdsExceptThisAndHost(crafterPlayerId);
        if (ids == null || ids.Count == 0)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CraftReceipt);
        WriteReceipt(writer, receipt);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ids);
    }

    static void WriteReceipt(NetDataWriter writer, Receipt receipt) {
        writer.Put(receipt.session);
        writer.Put(receipt.attempt);
        writer.Put(receipt.accepted ? (byte)1 : (byte)0);
        writer.Put((byte)receipt.reason);
        writer.Put(receipt.tick);
        writer.Put(receipt.resultId);
        int count = receipt.spends != null ? receipt.spends.Count : 0;
        if (count > 255)
            count = 255;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            Spend spend = receipt.spends[i];
            writer.Put(spend.syncId);
            writer.Put(spend.version);
            byte flags = 0;
            if (spend.destroyed)
                flags |= 1;
            bool liquids = spend.liquids != null && spend.liquids.Count > 0;
            if (liquids)
                flags |= 2;
            writer.Put(flags);
            writer.Put(spend.condition);
            if (!liquids)
                continue;
            int n = spend.liquids.Count;
            if (n > 255)
                n = 255;
            writer.Put((byte)n);
            for (int j = 0; j < n; j++) {
                writer.Put(spend.liquids[j].id);
                writer.Put(spend.liquids[j].ml);
            }
        }
    }

    static Receipt ReadReceipt(NetDataReader reader) {
        var receipt = new Receipt {
            session = reader.GetUInt(),
            attempt = reader.GetUInt(),
            accepted = reader.GetByte() != 0,
            reason = (CraftReason)reader.GetByte(),
            tick = reader.GetUShort(),
            resultId = reader.GetUShort(),
        };
        int count = reader.GetByte();
        receipt.spends = new List<Spend>(count);
        for (int i = 0; i < count; i++) {
            var spend = new Spend {
                syncId = reader.GetUShort(),
                version = reader.GetByte(),
            };
            byte flags = reader.GetByte();
            spend.destroyed = (flags & 1) != 0;
            spend.condition = reader.GetFloat();
            if ((flags & 2) != 0) {
                int n = reader.GetByte();
                spend.liquids = new List<Liq>(n);
                for (int j = 0; j < n; j++)
                    spend.liquids.Add(new Liq { id = reader.GetByte(), ml = reader.GetUShort() });
            }
            receipt.spends.Add(spend);
        }
        return receipt;
    }

    static void ApplySpend(Receipt receipt) {
        if (receipt.spends == null)
            return;
        for (int i = 0; i < receipt.spends.Count; i++) {
            Spend spend = receipt.spends[i];
            Gates[spend.syncId] = new Gate {
                tick = receipt.tick,
                version = spend.version,
                destroyed = spend.destroyed,
                holdCondition = !spend.destroyed,
                condition = spend.condition,
                holdLiquid = spend.liquids != null && spend.liquids.Count > 0,
                liquids = spend.liquids,
            };
            knetid key = spend.syncId;
            if (!ItemSync.TryGetItemSyncInfo(key, out SyncInfo si) || si == null || si.item == null)
                continue;
            if (si.Version != spend.version)
                continue;
            if (spend.destroyed) {
                Consume(si.item);
                continue;
            }
            si.item.condition = spend.condition;
            ApplyLiquids(si.item, spend.liquids);
        }
    }

    static void ApplyLiquids(Item item, List<Liq> liquids) {
        if (liquids == null || !item.TryGetComponent(out WaterContainerItem wci))
            return;
        string[] names = Item_SetupItems_Listener.LiquidNetIdToId;
        if (names == null)
            return;
        wci.stack.Clear();
        for (int i = 0; i < liquids.Count; i++) {
            if (liquids[i].id >= names.Length)
                continue;
            string liquidId = names[liquids[i].id];
            if (string.IsNullOrEmpty(liquidId))
                continue;
            wci.stack.Add(new LiquidStack(liquidId, liquids[i].ml));
        }
    }

    static void NoteMade(Receipt receipt) {
        Recipe recipe = null;
        if (Attempts.TryGetValue(receipt.attempt, out Attempt attempt)) {
            if (attempt.recipe < Recipes.recipes.Count)
                recipe = Recipes.recipes[attempt.recipe];
        }
        if (recipe != null) {
            recipe.hasMadeBefore = true;
            // 客户端本机合成钛板获得服务端确认回执时，在本地解锁 HEAVYMETAL 成就（对齐原版 RecipeResult.SpawnResult）
            if (recipe.result != null && recipe.result.id == TitaniumSlabItemId) {
                SteamManager.TryUnlockAchievement(HeavyMetalAchievementId);
            }
        }
        Body body = Util.GetLocalBody();
        if (body != null)
            Sound.Play(VanillaAssetKeys.SoundCombine, body.transform.position);
    }
}

struct CraftSpawn {
    public bool failed;
    public bool noItem;
    public Item item;
    public List<GameObject> made;

    public static CraftSpawn Made(Item item) => new CraftSpawn { item = item };

    public static CraftSpawn None() => new CraftSpawn { noItem = true };

    public static CraftSpawn Fail(List<GameObject> made) {
        var spawn = new CraftSpawn { failed = true, made = made };
        spawn.Discard();
        return spawn;
    }

    public void Discard() {
        if (made == null)
            return;
        for (int i = 0; i < made.Count; i++) {
            if (made[i] != null)
                UnityEngine.Object.Destroy(made[i]);
        }
        made = null;
        item = null;
    }
}
