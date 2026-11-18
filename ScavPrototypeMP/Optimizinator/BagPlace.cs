using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together;
using Together.MpConstants;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 容器装包父节点实时发布与客户端延迟入包收敛器。
/// 当背包尚未获得网络 ID 时暂缓客户端容器通告以防误发卸下字节；并在与 <see cref="FixInvSync"/> 协同下避免双队列重复轮询。
/// </summary>
internal static class BagPlace {
    private const float WaitSeconds = 30f;
    private const float AnnounceSeconds = NetTimingConstants.CommitResendIntervalSeconds;
    private const float AnnounceGap = 0.15f;
    private const int SweepBudget = 32;

    private struct Pending {
        internal SyncInfo Info;
        internal int ItemId;
        internal float Until;
    }

    private struct Delay {
        internal SyncInfo Info;
        internal int ItemId;
        internal float Next;
        internal float Until;
    }

    private static readonly List<Pending> Waiting = new List<Pending>(16);
    private static readonly List<Delay> Announcing = new List<Delay>(8);
    private static readonly List<SyncInfo> SweepList = new List<SyncInfo>(256);
    private static int _sweepAt;
    private static bool _sweeping;
    private static float _sweepNext;
    private static bool _fromRetry;
    private static bool _blocked;
    private static int _warns;

    internal static void Install(Harmony harmony) {
        if (harmony == null)
            return;

        MethodInfo load = AccessTools.Method(typeof(Container), "LoadItem", new[] { typeof(Item) });
        if (load != null)
            harmony.Patch(load, postfix: new HarmonyMethod(typeof(BagPlace), nameof(AfterLoad)));
        else
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] bag place missed Container.LoadItem");

        HookPublish(harmony, AccessTools.Method(typeof(Body), "PickUpItem", new[] { typeof(Item), typeof(int), typeof(bool) }));
        HookPublish(harmony, AccessTools.Method(typeof(Body), "DropItem", new[] { typeof(Item) }));
        HookPublish(harmony, AccessTools.Method(typeof(Body), "WearWearable", new[] { typeof(Item) }));
        HookPublish(harmony, AccessTools.Method(typeof(Body), "DropWearable", new[] { typeof(Item) }));

        MethodInfo unload = AccessTools.Method(typeof(Container), "UnloadItem", new[] { typeof(Item), typeof(Body) });
        if (unload != null)
            harmony.Patch(unload, postfix: new HarmonyMethod(typeof(BagPlace), nameof(AfterUnload)));
        else
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] bag place missed Container.UnloadItem");

        OptimizerMain.Log.LogDebug("[KrokV5Optimization] bag place publishes the live parent");
    }

    internal static void Tick() {
        if (!Net.IsRunning)
            return;
        if (Net.IsServer) {
            Sweep();
            return;
        }

        RetryAnnounces();
        if (Waiting.Count == 0)
            return;

        float now = Time.realtimeSinceStartup;
        for (int i = Waiting.Count - 1; i >= 0; i--) {
            Pending row = Waiting[i];
            if (row.Info == null || row.Until < now || Settle(row.Info))
                Waiting.RemoveAt(i);
        }
    }

    private static void AfterLoad(Container __instance, Item item) {
        Publish(item, __instance != null ? __instance.gameObject : null);
    }

    private static void AfterSlot(Item item) {
        Publish(item, null);
    }

    private static void AfterUnload(Container __instance, Item item) {
        Publish(item, null);
    }

    internal static bool BeforeAnnounce(SyncInfo si) {
        _blocked = false;
        if (!Net.IsRunning || si == null || Net.IsServer)
            return true;
        Item item = ItemOf(si);
        Transform parent = item != null && item.transform != null ? item.transform.parent : null;
        if (parent == null || parent.GetComponent<Container>() == null)
            return true;
        if (!Nameless(item))
            return true;

        _blocked = true;
        if (!_fromRetry)
            HoldAnnounce(si);
        return false;
    }

    internal static bool BeforeApply(SyncInfo __instance) {
        if (__instance == null || Net.IsServer || !AnnounceHeld(__instance))
            return true;
        if (!ContainerEncoding.IsLooseInWorld(__instance.container_data1.Value))
            return true;
        Item item = ItemOf(__instance);
        Transform parent = item != null && item.transform != null ? item.transform.parent : null;
        return parent == null || parent.GetComponent<Container>() == null;
    }

    internal static void AfterApply(SyncInfo __instance) {
        if (__instance == null || Net.IsServer)
            return;
        if (Settle(__instance)) {
            FixInvSync.ClearManaged(__instance);
            return;
        }
        if (FixInvSync.Manages(__instance))
            return;
        Remember(__instance);
    }

    private static bool Settle(SyncInfo si) {
        Item item = ItemOf(si);
        if (item == null || !ContainerEncoding.IsInsideContainer(si.container_data1.Value))
            return true;

        ushort net = si.container_netId.Value;
        if (net == 0)
            return false;
        if (InThisBag(item, net))
            return true;
        if (!NetObjectRegistry.TryGetSyncInfo((knetid)net, out SyncInfo bag) || bag == null || bag.go == null)
            return false;

        Container box = bag.container;
        if (box == null)
            box = bag.go.GetComponent<Container>();
        if (box == null)
            box = bag.go.GetComponentInChildren<Container>(true);
        if (box == null)
            return false;

        if (ItemSync.BrokenBag(bag, box)) {
            ItemSync.ParentTestChest("chest skip settle", item, bag.go);
            return true;
        }

        try {
            ItemSync.Container_ForceLoadItem(item, box);
        } catch (Exception ex) {
            Warn("place", ex);
            return true;
        }
        return InThisBag(item, net);
    }

    private static bool InThisBag(Item item, ushort net) {
        Transform parent = item.transform != null ? item.transform.parent : null;
        if (parent == null || parent.GetComponent<Container>() == null)
            return false;
        if (!NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag == null)
            return true;
        try {
            return (ushort)bag.syncId == net;
        } catch (Exception) {
            return true;
        }
    }

    private static void Remember(SyncInfo si) {
        int id = ItemId(si);
        if (id == 0)
            return;
        for (int i = 0; i < Waiting.Count; i++) {
            if (Waiting[i].ItemId == id)
                return;
        }
        if (Waiting.Count > 256)
            Waiting.RemoveAt(0);
        Waiting.Add(new Pending {
            Info = si,
            ItemId = id,
            Until = Time.realtimeSinceStartup + WaitSeconds,
        });
    }

    private static void HoldAnnounce(SyncInfo si) {
        int id = ItemId(si);
        if (id == 0)
            return;
        float now = Time.realtimeSinceStartup;
        for (int i = 0; i < Announcing.Count; i++) {
            if (Announcing[i].ItemId != id)
                continue;
            Delay row = Announcing[i];
            row.Info = si;
            row.Next = now + AnnounceGap;
            row.Until = now + AnnounceSeconds;
            Announcing[i] = row;
            si.SetIgnoreTimeForRoundTrip(AnnounceSeconds);
            return;
        }
        if (Announcing.Count > 64)
            Announcing.RemoveAt(0);
        Announcing.Add(new Delay {
            Info = si,
            ItemId = id,
            Next = now + AnnounceGap,
            Until = now + AnnounceSeconds,
        });
        si.SetIgnoreTimeForRoundTrip(AnnounceSeconds);
    }

    private static bool AnnounceHeld(SyncInfo si) {
        int id = ItemId(si);
        if (id == 0)
            return false;
        for (int i = 0; i < Announcing.Count; i++) {
            if (Announcing[i].ItemId == id)
                return true;
        }
        return false;
    }

    private static void RetryAnnounces() {
        if (Announcing.Count == 0)
            return;
        float now = Time.realtimeSinceStartup;
        for (int i = Announcing.Count - 1; i >= 0; i--) {
            Delay row = Announcing[i];
            if (row.Info == null || row.Until < now) {
                Announcing.RemoveAt(i);
                continue;
            }
            if (now < row.Next)
                continue;
            row.Next = now + AnnounceGap;
            Announcing[i] = row;
            _fromRetry = true;
            try {
                _blocked = false;
                ItemSync.Client_AnnounceContainerChange(row.Info);
            } catch (Exception ex) {
                Warn("announce", ex);
                _blocked = false;
            } finally {
                _fromRetry = false;
            }
            if (!_blocked)
                Announcing.RemoveAt(i);
        }
    }

    private static void Sweep() {
        if (!_sweeping) {
            if (Time.realtimeSinceStartup < _sweepNext)
                return;
            SweepList.Clear();
            Dictionary<GameObject, SyncInfo> map = NetObjectRegistry.SyncRegistry;
            if (map != null) {
                foreach (SyncInfo si in map.Values) {
                    if (si != null)
                        SweepList.Add(si);
                }
            }
            _sweepAt = 0;
            _sweeping = SweepList.Count > 0;
            if (!_sweeping)
                _sweepNext = Time.realtimeSinceStartup + 1f;
            return;
        }

        int budget = SweepBudget;
        while (budget > 0 && _sweepAt < SweepList.Count) {
            PublishIfStale(SweepList[_sweepAt]);
            _sweepAt++;
            budget--;
        }
        if (_sweepAt >= SweepList.Count) {
            _sweeping = false;
            _sweepNext = Time.realtimeSinceStartup + 1f;
        }
    }

    private static void PublishIfStale(SyncInfo si) {
        Item item = ItemOf(si);
        if (item == null || LinkMissing(item) || !Stale(si, item))
            return;
        try {
            si.WriteContainerInfo();
        } catch (Exception ex) {
            Warn("sweep", ex);
        }
    }

    private static bool Stale(SyncInfo si, Item item) {
        byte stored = si.container_data1.Value;
        Transform parent = item.transform != null ? item.transform.parent : null;
        if (parent == null)
            return !ContainerEncoding.IsLooseInWorld(stored);
        InventorySlot slot = parent.GetComponent<InventorySlot>();
        if (slot != null)
            return stored != ContainerEncoding.EncodeBodySlot(slot.slot);
        if (parent.GetComponent<Container>() != null)
            return !ContainerEncoding.IsInsideContainer(stored);
        if (parent.GetComponent<Limb>() != null && item.Stats != null && item.Stats.wearable)
            return !ContainerEncoding.IsWornOnBody(stored);
        return false;
    }

    private static bool Nameless(Item item) {
        if (LinkMissing(item))
            return true;
        Transform parent = item.transform != null ? item.transform.parent : null;
        if (parent == null)
            return false;
        return !NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag == null;
    }

    private static bool LinkMissing(Item item) {
        Transform parent = item.transform != null ? item.transform.parent : null;
        if (parent == null)
            return false;
        InventorySlot slot = parent.GetComponent<InventorySlot>();
        if (slot != null)
            return slot.body != null && slot.body.TryGetNetBody(out NetBody body) && body.syncBody == null;
        if (parent.GetComponent<Container>() == null)
            return false;
        NetBody owner = item.GetComponentInParent<NetBody>();
        return owner != null && owner.syncBody == null;
    }

    private static void Publish(Item item, GameObject box) {
        if (!Net.IsRunning || !Net.IsServer || item == null)
            return;
        if (ItemSync.SuppressContainerWrite > 0) {
            if (ItemSync.ParentTestSwap)
                ItemSync.ParentTest("swap block bag " + ItemSync.ParentTestItem(item));
            return;
        }
        try {
            Ensure(box);
            Ensure(item.gameObject);
            if (!NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) || si == null)
                return;
            if (LinkMissing(item))
                return;
            si.WriteContainerInfo();
        } catch (Exception ex) {
            Warn("publish", ex);
        }
    }

    private static void HookPublish(Harmony harmony, MethodInfo method) {
        if (method == null) {
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] bag place missed a parent hook");
            return;
        }
        harmony.Patch(method, postfix: new HarmonyMethod(typeof(BagPlace), nameof(AfterSlot)));
    }

    private static void Ensure(GameObject go) {
        if (!Net.IsRunning || go == null || !Net.IsServer)
            return;
        if (NetObjectRegistry.IsRegistered(go) || !NetObjectRegistry.ObjectShouldBeSynced(go))
            return;
        NetObjectRegistry.NewGO(go);
    }

    private static Item ItemOf(SyncInfo si) {
        if (si == null)
            return null;
        try {
            return si.item;
        } catch (Exception) {
            return null;
        }
    }

    private static int ItemId(SyncInfo si) {
        Item item = ItemOf(si);
        if (item == null)
            return 0;
        try {
            return item.GetInstanceID();
        } catch (Exception) {
            return 0;
        }
    }

    private static void Warn(string where, Exception ex) {
        if (_warns >= 4)
            return;
        _warns++;
        string message = ex.InnerException != null ? ex.InnerException.Message : ex.Message;
        OptimizerMain.Log.LogWarning("[KrokV5Optimization] bag place " + where + ": " + message);
    }
}
