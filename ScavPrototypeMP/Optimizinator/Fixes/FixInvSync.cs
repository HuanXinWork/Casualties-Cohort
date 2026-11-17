using System;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using Together;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 物品与父容器三元组 <c>(container_data1, container_netId, container_ver)</c> 权威同步修复器（第 1 部分：服务端重发布、容器世代监测与全局就绪判定）。
/// </summary>
internal static partial class FixInvSync {
    private struct Fingerprint {
        internal byte Data;
        internal ushort Net;
        internal byte Ver;

        internal bool Matches(byte data, ushort net, byte ver) =>
            Data == data && Net == net && Ver == ver;
    }

    private struct Wait {
        internal SyncInfo Info;
        internal Fingerprint Fp;
        internal float Until;
    }

    internal static long Hits;
    internal static long Retries;
    internal static long GiveUps;
    internal static long Republish;
    static int Warns;

    const float WaitWhenPresent = 3f;
    const float WaitWhenMissing = 10f;
    const float WaitRttCap = 2f;
    const float VersionWatchEvery = 0.5f;

    private static readonly List<SyncInfo> Batch = new List<SyncInfo>(256);
    private static readonly HashSet<SyncInfo> Seen = new HashSet<SyncInfo>();
    private static readonly Dictionary<int, Wait> Pending = new Dictionary<int, Wait>();
    private static readonly Dictionary<int, Fingerprint> Exhausted = new Dictionary<int, Fingerprint>();
    private static readonly Dictionary<ushort, byte> SeenContainerVer = new Dictionary<ushort, byte>();
    private static readonly List<int> Done = new List<int>();
    private static readonly List<ushort> VersionChanged = new List<ushort>(32);
    private static float _nextSweep;
    private static float _nextVersionWatch;
    private static bool _wasInGame;
    private static bool _sweepQuiet;

    private static Action<SyncInfo> _apply;
    private static bool _ready;
    private static bool _dead;
    private static bool _ticking;

    internal static string Evidence() =>
        "invSync=" + Hits + " retry=" + Retries + " giveUp=" + GiveUps + " republish=" + Republish;

    internal static string ApplyMissReason(SyncInfo info) {
        if (info == null)
            return "unknown";
        if (StaleVersion(info))
            return "stale";
        if (TargetMissing(info))
            return "missing";
        if (info.item != null) {
            int id = info.item.GetInstanceID();
            if (Exhausted.TryGetValue(id, out Fingerprint fp)
                && fp.Matches(info.container_data1.Value, info.container_netId.Value, info.container_ver.Value))
                return "exhausted";
        }
        return "seat-fail";
    }

    internal static void Arm(Harmony harmony) {
        // 用一次性构建的强类型开实例委托绑定私有 SyncInfo.UpdateItemContainer，消除每帧 MethodInfo.Invoke 反射装箱
        MethodInfo applyMethod = AccessTools.Method(typeof(SyncInfo), "UpdateItemContainer");
        if (applyMethod == null)
            throw new MissingMethodException("inv sync apply");
        _apply = (Action<SyncInfo>)Delegate.CreateDelegate(typeof(Action<SyncInfo>), applyMethod);
        OptimizerMain.Log.LogDebug("[KrokV5Opt] inv sync is in UpdateItemContainer");
    }

    // 供 BagPlace.AfterApply 查询是否已由 FixInvSync 队列托管重试，避免双队列重复轮询同一装包物品
    internal static bool Manages(SyncInfo info) {
        if (info == null || info.item == null)
            return false;
        int id = info.item.GetInstanceID();
        return Pending.ContainsKey(id) || Exhausted.ContainsKey(id);
    }

    internal static void ClearManaged(SyncInfo info) {
        if (info == null || info.item == null)
            return;
        int id = info.item.GetInstanceID();
        Pending.Remove(id);
        Exhausted.Remove(id);
    }

    internal static void Refresh() {
        if (!Net.IsServer || !Ready() || NetObjectRegistry.SyncRegistry == null)
            return;

        int wrote = 0;
        int skipped = 0;
        Seen.Clear();
        for (int pass = 0; pass < 4; pass++) {
            Batch.Clear();
            foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
                if (info != null)
                    Batch.Add(info);
            }
            int fresh = 0;
            for (int i = 0; i < Batch.Count; i++) {
                SyncInfo info = Batch[i];
                if (!Seen.Add(info))
                    continue;
                fresh++;
                int result = Publish(info);
                if (result > 0)
                    wrote++;
                else if (result < 0)
                    skipped++;
            }
            if (fresh == 0)
                break;
        }
        Seen.Clear();
        Batch.Clear();
        Hits += wrote;
        OptimizerMain.Log?.LogInfo("[KrokV5Opt] inv sync items=" + wrote + " skip=" + skipped);
    }

    internal static void NoteContainerVersion(ushort netId, byte ver) {
        if (!Net.IsServer || netId == 0)
            return;
        if (SeenContainerVer.TryGetValue(netId, out byte prev) && prev == ver)
            return;
        bool had = SeenContainerVer.ContainsKey(netId);
        SeenContainerVer[netId] = ver;
        if (had)
            RepublishForNet(netId);
    }

    internal static void RepublishForNet(ushort netId) {
        if (!Net.IsServer || netId == 0 || !Ready() || NetObjectRegistry.SyncRegistry == null)
            return;

        int wrote = 0;
        Batch.Clear();
        foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
            if (info != null)
                Batch.Add(info);
        }
        for (int i = 0; i < Batch.Count; i++) {
            SyncInfo info = Batch[i];
            Item item;
            try {
                item = info.item;
            } catch (Exception) {
                continue;
            }
            if (item == null)
                continue;

            bool claims = info.container_netId.Value.id == netId;
            bool liveOk = ReadLive(item, out byte data, out ushort net, out byte ver) && net == netId;
            if (!claims && !liveOk)
                continue;
            if (FixContainerInfo.ParentLinkMissing(item))
                continue;
            if (MoveCommit.Held(info.syncId) || ItemSync.SuppressContainerWrite > 0)
                continue;

            if (!liveOk) {
                Touch(info, "container_data1");
                Touch(info, "container_netId");
                Touch(info, "container_ver");
                wrote++;
                continue;
            }
            info.container_data1.Value = data;
            info.container_netId.Value = net;
            info.container_ver.Value = ver;
            FixContainerInfo.Remember(item, net, data, ver);
            Touch(info, "container_data1");
            Touch(info, "container_netId");
            Touch(info, "container_ver");
            wrote++;
        }
        Batch.Clear();
        if (wrote > 0) {
            Republish += wrote;
            Hits += wrote;
            OptimizerMain.Log?.LogInfo("[KrokV5Opt] inv republish net=" + netId + " items=" + wrote);
        }
    }

    internal static bool GearSettled() {
        if (!Net.IsServer || !Ready() || NetObjectRegistry.SyncRegistry == null)
            return true;
        foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
            if (info == null || info.go == null)
                continue;
            Item item;
            try {
                item = info.item;
            } catch (Exception) {
                return false;
            }
            if (item == null || item.transform == null || item.transform.parent == null)
                continue;
            if (!OnCharacter(item.transform.parent))
                continue;
            if (FixContainerInfo.ParentLinkMissing(item))
                return false;
            if (!ReadLive(item, out byte data, out ushort net, out byte ver))
                return false;
            if (ContainerEncoding.IsLooseInWorld(data) || net == 0)
                return false;
            if (info.container_data1.Value != data || info.container_netId.Value != net || info.container_ver.Value != ver)
                return false;
        }
        return true;
    }

    static bool OnCharacter(Transform parent) {
        if (parent.GetComponent<InventorySlot>() != null)
            return true;
        if (parent.GetComponent<Limb>() != null)
            return true;
        return parent.GetComponent<Container>() != null;
    }

    internal static bool InGame() => Util.IsWorldGenerated();

    internal static void Tick() {
        if (Net.IsServer) {
            WatchVersions();
            return;
        }
        TickClientRetry();
    }

    static void WatchVersions() {
        float now = Time.realtimeSinceStartup;
        if (now < _nextVersionWatch)
            return;
        _nextVersionWatch = now + VersionWatchEvery;
        VersionChanged.Clear();

        if (NetBody.all_instances != null) {
            foreach (NetBody nb in NetBody.all_instances) {
                if (nb == null || nb.syncBody == null)
                    continue;
                ushort id;
                byte ver;
                try {
                    id = nb.syncBody.Id;
                    ver = nb.syncBody.Version;
                } catch (Exception) {
                    continue;
                }
                if (id == 0)
                    continue;
                if (SeenContainerVer.TryGetValue(id, out byte prev) && prev == ver)
                    continue;
                bool had = SeenContainerVer.ContainsKey(id);
                SeenContainerVer[id] = ver;
                if (had)
                    VersionChanged.Add(id);
            }
        }

        if (NetObjectRegistry.SyncRegistry != null) {
            foreach (SyncInfo info in NetObjectRegistry.SyncRegistry.Values) {
                if (info == null || info.go == null)
                    continue;
                Container box = info.container;
                if (box == null)
                    box = info.go.GetComponent<Container>();
                if (box == null)
                    continue;
                ushort id;
                byte ver;
                try {
                    id = info.syncId;
                    ver = info.Version;
                } catch (Exception) {
                    continue;
                }
                if (id == 0)
                    continue;
                if (SeenContainerVer.TryGetValue(id, out byte prev) && prev == ver)
                    continue;
                bool had = SeenContainerVer.ContainsKey(id);
                SeenContainerVer[id] = ver;
                if (had)
                    VersionChanged.Add(id);
            }
        }

        for (int i = 0; i < VersionChanged.Count; i++)
            RepublishForNet(VersionChanged[i]);
        VersionChanged.Clear();
    }

    static int Publish(SyncInfo info) {
        Item item = info.item;
        if (item == null || MoveCommit.Held(info.syncId))
            return 0;
        if (ItemSync.SuppressContainerWrite > 0) {
            if (ItemSync.ParentTestSwap)
                ItemSync.ParentTest("swap block publish " + ItemSync.ParentTestItem(item));
            return 0;
        }

        byte storedData = info.container_data1.Value;
        ushort storedNet = info.container_netId.Value;
        byte storedVer = info.container_ver.Value;
        if (FixContainerInfo.ParentLinkMissing(item))
            return -1;
        if (!ReadLive(item, out byte data, out ushort net, out byte ver))
            return -1;
        if (net == 0 && item.transform.parent != null)
            return -1;
        if (net == 0)
            ver = 0;

        bool changed = storedData != data || storedNet != net || storedVer != ver;
        info.container_data1.Value = data;
        info.container_netId.Value = net;
        info.container_ver.Value = ver;
        FixContainerInfo.Remember(item, net, data, ver);
        if (ContainerEncoding.IsLooseInWorld(data) && !changed)
            return 0;

        Touch(info, "container_data1");
        Touch(info, "container_netId");
        Touch(info, "container_ver");
        return 1;
    }

    static void Touch(SyncInfo info, string fieldName) {
        if (info == null)
            return;
        if (fieldName == "container_data1")
            info.container_data1.MarkDirty();
        else if (fieldName == "container_netId")
            info.container_netId.MarkDirty();
        else if (fieldName == "container_ver")
            info.container_ver.MarkDirty();
    }

    static bool ReadLive(Item item, out byte data, out ushort net, out byte ver) {
        data = ContainerEncoding.WorldByte;
        net = 0;
        ver = 0;
        if (item == null)
            return false;
        ItemSync.ItemsContainerInfo info = ItemSync.ItemGetContainerInfo(item);
        if (info == null)
            return false;
        data = info.containerExtraData1;
        net = info.knetid.id;
        ver = info.containerVer;
        return true;
    }

    static bool Ready() {
        if (_ready)
            return true;
        if (_dead)
            return false;
        try {
            if (_apply == null) {
                MethodInfo applyMethod = AccessTools.Method(typeof(SyncInfo), "UpdateItemContainer");
                if (applyMethod == null)
                    throw new MissingMethodException("inv sync read");
                _apply = (Action<SyncInfo>)Delegate.CreateDelegate(typeof(Action<SyncInfo>), applyMethod);
            }
            _ready = true;
            return true;
        } catch (Exception ex) {
            _dead = true;
            ModLog.Warning("[KrokV5Opt] inv sync off: " + ex.Message, LogDomain.Container);
            return false;
        }
    }
}
