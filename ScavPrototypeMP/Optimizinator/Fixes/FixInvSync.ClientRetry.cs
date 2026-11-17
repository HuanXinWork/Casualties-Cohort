using System;
using System.Collections.Generic;
using Together;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 物品与父容器三元组权威同步修复器（第 2 部分：客户端指纹重试队列、挂载前置拦截与容器实体检索）。
/// </summary>
internal static partial class FixInvSync {
    static void TickClientRetry() {
        if (_apply == null)
            return;
        if (!InGame()) {
            if (_wasInGame) {
                _wasInGame = false;
                _sweepQuiet = false;
                Pending.Clear();
                Exhausted.Clear();
            }
            return;
        }

        MedicalSync.FlushHeldHealth();
        if (!_wasInGame) {
            _wasInGame = true;
            _sweepQuiet = false;
            _nextSweep = 0f;
            Exhausted.Clear();
            OptimizerMain.Log?.LogInfo("[KrokV5Opt] inv apply now");
        }

        Sweep();
        if (Pending.Count == 0)
            return;

        Done.Clear();
        float now = Time.realtimeSinceStartup;
        _ticking = true;
        try {
            foreach (KeyValuePair<int, Wait> pair in Pending) {
                int key = pair.Key;
                Wait wait = pair.Value;
                SyncInfo info = wait.Info;
                if (info == null) {
                    Done.Add(key);
                    continue;
                }
                if (info.go == null)
                    info.CreateObject();
                if (info.go == null)
                    continue;
                if (info.item == null) {
                    Done.Add(key);
                    Exhausted.Remove(key);
                    continue;
                }

                byte data = info.container_data1.Value;
                ushort net = info.container_netId.Value;
                byte ver = info.container_ver.Value;
                if (ContainerEncoding.IsLooseInWorld(data) || Placed(info)) {
                    Done.Add(key);
                    Exhausted.Remove(key);
                    continue;
                }
                if (!wait.Fp.Matches(data, net, ver)) {
                    wait = NewWait(info, data, net, ver);
                    Pending[key] = wait;
                }

                bool missing = TargetMissing(info);
                bool stale = StaleVersion(info);
                if (now > wait.Until) {
                    Exhausted[key] = new Fingerprint { Data = data, Net = net, Ver = ver };
                    GiveUps++;
                    Done.Add(key);
                    continue;
                }
                if (missing || stale)
                    continue;

                try {
                    if (!Parent(info))
                        continue;
                    Retries++;
                    Done.Add(key);
                    Exhausted.Remove(key);
                } catch (Exception ex) {
                    if (Warns < 3) {
                        Warns++;
                        ModLog.Warning("[KrokV5Opt] inv retry failed: " + ex.Message, LogDomain.Container);
                    }
                }
            }
        } finally {
            _ticking = false;
        }

        for (int i = 0; i < Done.Count; i++)
            Pending.Remove(Done[i]);
    }

    static void Sweep() {
        if (_sweepQuiet)
            return;
        float now = Time.realtimeSinceStartup;
        if (now < _nextSweep || NetObjectRegistry.SyncRegistry == null)
            return;
        _nextSweep = now + 0.5f;

        Batch.Clear();
        foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
            if (info != null)
                Batch.Add(info);
        }

        int added = 0;
        for (int i = 0; i < Batch.Count; i++) {
            SyncInfo info = Batch[i];
            if (ContainerEncoding.IsLooseInWorld(info.container_data1.Value))
                continue;
            if (info.go == null)
                info.CreateObject();
            if (info.go == null || info.item == null || Placed(info))
                continue;
            if (Enqueue(info))
                added++;
        }
        Batch.Clear();
        if (added == 0 && Pending.Count == 0)
            _sweepQuiet = true;
    }

    internal static bool KeepHeld(SyncInfo __instance) {
        if (_ticking || Net.IsServer || __instance == null || __instance.go == null)
            return true;
        if (!InGame())
            return false;

        Item item;
        try {
            item = __instance.item;
        } catch (Exception) {
            return true;
        }
        if (item == null)
            return true;

        byte data = __instance.container_data1.Value;
        if (ContainerEncoding.IsLooseInWorld(data))
            return true;
        if (ContainerEncoding.IsInsideContainer(data))
            return false;
        if (StaleVersion(__instance) || TargetMissing(__instance))
            return false;
        if (Parked(__instance, item))
            return false;
        return true;
    }

    internal static void AfterApply(SyncInfo __instance) {
        if (_ticking || Net.IsServer || __instance == null || __instance.go == null || __instance.item == null || _apply == null)
            return;
        int id = __instance.item.GetInstanceID();
        if (InGame() && (ContainerEncoding.IsLooseInWorld(__instance.container_data1.Value) || Placed(__instance))) {
            Pending.Remove(id);
            Exhausted.Remove(id);
            return;
        }
        if (ContainerEncoding.IsLooseInWorld(__instance.container_data1.Value))
            return;
        _sweepQuiet = false;
        Enqueue(__instance);
    }

    static bool Parked(SyncInfo info, Item item) {
        int id = item.GetInstanceID();
        if (!Exhausted.TryGetValue(id, out Fingerprint fp))
            return false;
        byte data = info.container_data1.Value;
        ushort net = info.container_netId.Value;
        byte ver = info.container_ver.Value;
        if (!fp.Matches(data, net, ver)) {
            Exhausted.Remove(id);
            return false;
        }
        if (!TargetMissing(info) && !StaleVersion(info)) {
            Exhausted.Remove(id);
            return false;
        }
        return true;
    }

    static bool Enqueue(SyncInfo info) {
        if (info == null || info.item == null)
            return false;
        int id = info.item.GetInstanceID();
        byte data = info.container_data1.Value;
        ushort net = info.container_netId.Value;
        byte ver = info.container_ver.Value;
        if (ContainerEncoding.IsLooseInWorld(data))
            return false;

        if (Exhausted.TryGetValue(id, out Fingerprint fp) && fp.Matches(data, net, ver)) {
            if (TargetMissing(info) || StaleVersion(info))
                return false;
            Exhausted.Remove(id);
        }
        if (Pending.TryGetValue(id, out Wait wait)) {
            if (!wait.Fp.Matches(data, net, ver)) {
                Pending[id] = NewWait(info, data, net, ver);
            } else {
                wait.Info = info;
                Pending[id] = wait;
            }
            return false;
        }
        if (Pending.Count > 4000)
            return false;
        Pending[id] = NewWait(info, data, net, ver);
        return true;
    }

    static Wait NewWait(SyncInfo info, byte data, ushort net, byte ver) {
        float grace = TargetMissing(info) || StaleVersion(info) ? WaitWhenMissing : WaitWhenPresent;
        if (ClientMain.RTT_IN_SECONDS_with_margin > 0f)
            grace += Mathf.Min(ClientMain.RTT_IN_SECONDS_with_margin, WaitRttCap);
        return new Wait {
            Info = info,
            Fp = new Fingerprint { Data = data, Net = net, Ver = ver },
            Until = Time.realtimeSinceStartup + grace,
        };
    }

    internal static bool SkipLoose(ItemSync.ItemsContainerInfo ici) {
        return ici == null || ici.has_container;
    }

    static bool Parent(SyncInfo info) {
        byte data = info.container_data1.Value;
        ushort net = info.container_netId.Value;
        byte ver = info.container_ver.Value;
        if (ContainerEncoding.IsLooseInWorld(data) || net == 0 || info.go == null || info.item == null)
            return false;
        if (StaleVersion(info) || TargetMissing(info))
            return false;

        if (ContainerEncoding.IsAttachedToBody(data)) {
            if (!ContainerLink.TryBody(info, out _))
                return false;
            _apply(info);
            return Placed(info);
        }

        SyncInfo bag = FindBag(net, ver, out Container box);
        if (bag == null || bag.go == null || box == null)
            return false;
        if (ItemSync.BrokenBag(bag, box)) {
            ItemSync.ParentTestChest("chest skip retry", info.item, bag.go);
            return false;
        }

        ItemSync.Container_ForceLoadItem(info.item, box);
        return Placed(info);
    }

    static bool Placed(SyncInfo info) {
        byte data = info.container_data1.Value;
        if (ContainerEncoding.IsLooseInWorld(data) || info.go == null || info.item == null)
            return ContainerEncoding.IsLooseInWorld(data);

        Transform parent = info.item.transform.parent;
        if (parent == null)
            return false;
        if (ContainerEncoding.IsAttachedToBody(data))
            return parent.GetComponent<InventorySlot>() != null || parent.GetComponent<Limb>() != null;

        ushort net = info.container_netId.Value;
        byte ver = info.container_ver.Value;
        if (net == 0)
            return false;
        if (ParentBagId(info.item) == net
            && parent.gameObject != null
            && NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo liveBag)
            && liveBag != null
            && liveBag.Version == ver) {
            return true;
        }
        SyncInfo bag = FindBag(net, ver, out _);
        if (bag == null || bag.go == null)
            return false;
        return parent == bag.go.transform || parent.IsChildOf(bag.go.transform);
    }

    static bool TargetMissing(SyncInfo info) {
        byte data = info.container_data1.Value;
        if (ContainerEncoding.IsLooseInWorld(data))
            return false;
        ushort net = info.container_netId.Value;
        if (net == 0)
            return true;
        return ContainerEncoding.IsAttachedToBody(data) ? !BodyExists(net) : !BagExists(net);
    }

    static bool StaleVersion(SyncInfo info) {
        byte data = info.container_data1.Value;
        if (ContainerEncoding.IsLooseInWorld(data))
            return false;
        ushort net = info.container_netId.Value;
        byte ver = info.container_ver.Value;
        if (net == 0)
            return false;

        if (ContainerEncoding.IsAttachedToBody(data)) {
            if (!NetBody.TryGetNetBodyFromId(new knetid(net), out NetBody body) || body == null || body.syncBody == null)
                return false;
            return body.syncBody.Version != ver;
        }

        if (!NetObjectRegistry.TryGetSyncInfo(new knetid(net), out SyncInfo bag) || bag == null) {
            bag = ScanBag(net);
            if (bag == null)
                return false;
        }
        try {
            return bag.Version != ver;
        } catch (Exception) {
            return false;
        }
    }

    static bool BodyExists(ushort net) {
        return NetBody.TryGetNetBodyFromId(new knetid(net), out NetBody body) && body != null && body.syncBody != null;
    }

    static bool BagExists(ushort net) {
        if (net != 0 && NetObjectRegistry.TryGetSyncInfo(new knetid(net), out SyncInfo direct) && direct != null && direct.go != null)
            return true;
        SyncInfo scanned = ScanBag(net);
        return scanned != null && scanned.go != null;
    }

    static SyncInfo FindBag(ushort net, byte ver, out Container box) {
        box = null;
        SyncInfo found = null;
        if (net != 0 && NetObjectRegistry.TryGetSyncInfo(new knetid(net), out SyncInfo direct) && direct != null)
            found = direct;
        if (found == null)
            found = ScanBag(net);
        if (found != null && found.Version != ver)
            found = null;
        if (found == null)
            return null;
        box = Box(found);
        return found;
    }

    static SyncInfo ScanBag(ushort net) {
        if (NetObjectRegistry.NetIdToSyncInfoDict != null) {
            foreach (SyncInfo info in NetObjectRegistry.NetIdToSyncInfoDict.Values) {
                if (SameBag(info, net))
                    return info;
            }
        }
        if (NetObjectRegistry.SyncRegistry == null)
            return null;
        foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
            if (SameBag(info, net))
                return info;
        }
        return null;
    }

    static bool SameBag(SyncInfo info, ushort net) {
        if (info == null)
            return false;
        try {
            return (ushort)info.syncId == net;
        } catch (Exception) {
            return false;
        }
    }

    static Container Box(SyncInfo bag) {
        if (bag == null || bag.go == null)
            return null;
        Container own = bag.container;
        if (own != null)
            return own;
        Transform root = bag.go.transform;
        for (int i = 0; i < root.childCount; i++) {
            Container child = root.GetChild(i).GetComponent<Container>();
            if (child != null)
                return child;
        }
        return null;
    }

    static ushort ParentBagId(Item item) {
        Transform parent = item.transform.parent;
        if (parent == null || parent.GetComponent<Container>() == null)
            return 0;
        if (!NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag == null)
            return 0;
        try {
            return bag.syncId;
        } catch (Exception) {
            return 0;
        }
    }
}
