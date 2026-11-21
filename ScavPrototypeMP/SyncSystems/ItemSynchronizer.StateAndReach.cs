using System;
using System.Collections.Generic;
using System.Linq;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 物品与容器权威同步核心（第 2 部分：客户端槽位状态轮询、待定父节点锁存、可达性与防盗窃判定、涂装/收藏预测及诊断日志）。
/// </summary>
internal partial class ItemSync {
    internal static SyncInfo[] Client_last_inventory_state = new SyncInfo[8];

    static readonly Dictionary<knetid, Container> PickupBag = new();
    static readonly HashSet<int> ParentTestChestSeen = new();
    static readonly Dictionary<int, int> ParentTestHandStamp = new();

    struct PendingParent {
        internal byte Data;
        internal knetid Net;
        internal float Until;
    }

    static readonly Dictionary<knetid, PendingParent> PendingParentAnnounce = new();
    static readonly List<knetid> PendingParentDead = new();

    struct FavPending {
        internal bool want;
        internal double until;
    }

    static readonly Dictionary<knetid, FavPending> FavPendingMap = new Dictionary<knetid, FavPending>();

    internal static void ParentTest(string line) {
        if (!log.verbose)
            return;
        try {
            string side = Net.IsServer ? "host" : "client";
            Plugin.log.LogInfo("[parentTest] " + side + " " + line);
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Container, "ParentTest", ex.Message);
        }
    }

    internal static string ParentTestItem(Item item) {
        try {
            if (item == null)
                return "null";
            string parent = item.transform != null && item.transform.parent != null ? item.transform.parent.name : "none";
            bool sim = item.rb != null && item.rb.simulated;
            ushort id = 0;
            byte data = 255;
            ushort cnet = 0;
            if (item.TryGetComponent(out SyncInfoGameObjectTracker tracker) && tracker.syncInfo != null) {
                id = tracker.syncInfo.syncId.id;
                data = tracker.syncInfo.container_data1.Value;
                cnet = tracker.syncInfo.container_netId.Value.id;
            }
            return item.id + "#" + id + " byte=" + data + " cnet=" + cnet + " parent=" + parent + " sim=" + sim;
        } catch (Exception) {
            return item != null ? item.name : "null";
        }
    }

    static int ParentTestKey(int a, int b, int c, int d) {
        unchecked {
            int h = a;
            h = (h * 397) ^ b;
            h = (h * 397) ^ c;
            h = (h * 397) ^ d;
            return h;
        }
    }

    internal static void ParentTestChest(string what, Item item, UnityEngine.Object bag) {
        try {
            int itemId = item != null ? item.GetInstanceID() : 0;
            int parentId = item != null && item.transform.parent != null ? item.transform.parent.GetInstanceID() : 0;
            int bagId = bag != null ? bag.GetInstanceID() : 0;
            int sim = item != null && item.rb != null && item.rb.simulated ? 1 : 0;
            int key = ParentTestKey(what.GetHashCode(), itemId, parentId ^ bagId, sim);
            if (ParentTestChestSeen.Count > 4096)
                ParentTestChestSeen.Clear();
            if (!ParentTestChestSeen.Add(key))
                return;
            string bagName = bag != null ? bag.name : "none";
            ParentTest(what + " " + ParentTestItem(item) + " bag=" + bagName);
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Container, "ParentTestChest", ex.Message);
        }
    }

    internal static void ParentTestHand(Item item) {
        try {
            if (item == null || item.transform == null)
                return;
            if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
                return;
            Transform parent = item.transform.parent;
            bool inSlot = parent != null && parent.TryGetComponent(out InventorySlot _);
            byte data = tracker.syncInfo.container_data1.Value;
            if (!inSlot && !ContainerEncoding.IsHeldInBodySlot(data))
                return;
            ushort cnet = tracker.syncInfo.container_netId.Value.id;
            int parentId = parent != null ? parent.GetInstanceID() : 0;
            int sim = item.rb != null && item.rb.simulated ? 1 : 0;
            int stamp = ParentTestKey(data, cnet, parentId, sim);
            int key = item.GetInstanceID();
            if (ParentTestHandStamp.TryGetValue(key, out int prev) && prev == stamp)
                return;
            if (ParentTestHandStamp.Count > 4096)
                ParentTestHandStamp.Clear();
            ParentTestHandStamp[key] = stamp;
            string tag = inSlot && ContainerEncoding.IsLooseInWorld(data) ? "swap apply loose" : "swap seen";
            ParentTest(tag + " " + ParentTestItem(item));
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Container, "ParentTestHand", ex.Message);
        }
    }

    static bool ParentTestWatch(Item item, byte slot, Body body) {
        if (item == null || body == null)
            return false;
        if (item.transform.parent != null)
            return true;
        Item occ = body.GetItem(slot);
        return occ != null && occ != item;
    }

    public static bool ItemIsInContainer(Item item) => item.transform.parent != null;
    public static bool ItemIsInContainer(GameObject o) => o.transform.parent != null;
    public static bool ItemIsInContainer(SyncInfo si) => si.go.transform.parent != null;
    public static bool ItemIsRegistered(Item item) => NetObjectRegistry.IsRegistered(item.gameObject);

    public static void RecordAndApplyInvState(Body body) {
        for (int i = 0; i < body.slots.Length; i++) {
            Item item = body.GetItem(i);
            Client_last_inventory_state[i] = NetObjectRegistry.GetSyncInfo(item);
        }
    }

    public static SyncInfo[] RecordInvState(Body body) {
        var snapshot = new SyncInfo[8];
        for (int i = 0; i < body.slots.Length; i++) {
            Item item = body.GetItem(i);
            snapshot[i] = NetObjectRegistry.GetSyncInfo(item);
        }
        return snapshot;
    }

    internal static void RememberPickupBag(knetid id, Container bag) {
        if (bag == null)
            PickupBag.Remove(id);
        else
            PickupBag[id] = bag;
    }

    internal static bool TakePickupBag(knetid id, out Container bag) {
        if (!PickupBag.TryGetValue(id, out bag))
            return false;
        PickupBag.Remove(id);
        return bag != null;
    }

    static Container BagFromByte(SyncInfo isi) {
        if (isi == null || !ContainerEncoding.IsInsideContainer(isi.container_data1.Value))
            return null;
        if (!ContainerLink.TryBag(isi, out SyncInfo bagSi) || bagSi == null || !bagSi.IsContainer())
            return null;
        return bagSi.container;
    }

    internal static void ClearPendingParent(knetid id) {
        PendingParentAnnounce.Remove(id);
    }

    static void ExpirePendingParents() {
        if (PendingParentAnnounce.Count == 0)
            return;
        float now = Time.realtimeSinceStartup;
        PendingParentDead.Clear();
        foreach (var pair in PendingParentAnnounce) {
            if (pair.Value.Until < now)
                PendingParentDead.Add(pair.Key);
        }
        for (int i = 0; i < PendingParentDead.Count; i++)
            PendingParentAnnounce.Remove(PendingParentDead[i]);
    }

    internal static bool HoldContainerReseat(SyncInfo si) {
        if (si == null || !Net.IsClient)
            return false;
        if (!PendingParentAnnounce.TryGetValue(si.syncId, out var row))
            return false;
        if (row.Until < Time.realtimeSinceStartup
            || (si.container_data1.Value == row.Data && si.container_netId.Value.id == row.Net.id)) {
            PendingParentAnnounce.Remove(si.syncId);
            return false;
        }
        return true;
    }

    static bool SlotEmptyBecauseBagged(SyncInfo si) {
        if (si == null)
            return false;
        if (ContainerEncoding.IsInsideContainer(si.container_data1.Value))
            return true;
        if (si.go == null)
            return false;
        Transform parent = si.go.transform.parent;
        return parent != null && parent.GetComponent<Container>() != null;
    }

    static bool ItemDropHitsBag(SyncInfo isi, Item item) {
        if (item == null)
            return false;
        Transform parent = item.transform.parent;
        if (parent != null) {
            if (parent.GetComponent<InventorySlot>() != null || parent.GetComponent<Limb>() != null)
                return false;
            if (parent.GetComponent<Container>() != null)
                return true;
        }
        return isi != null && ContainerEncoding.IsInsideContainer(isi.container_data1.Value);
    }

    public static void Client_AnnounceContainerChange(SyncInfo si) {
        if (!BagPlace.BeforeAnnounce(si))
            return;
        if (si == null || si.item == null)
            return;
        MoveCommit.SubmitLive(si.item);
    }

    public static void Server_AnnounceCombineSound(SyncInfo si1, SyncInfo si2, knetid combinerClientId) {
        Item it1 = si1.item;
        Item it2 = si2.item;
        string soundName = null;

        var gun = it1.GetComponent<GunScript>();
        var ammo1 = it1.GetComponent<AmmoScript>();
        var ammo2 = it2.GetComponent<AmmoScript>();

        if ((bool)gun && (bool)ammo2) {
            if (ammo2.ammoType == gun.ammoType) {
                if (gun.racked && gun.roundInChamber == GunScript.RoundInChamber.None && ammo2.itemType == AmmoScript.AmmoItemType.Round) {
                    soundName = "gunloadshell";
                } else if (!gun.hasMag && gun.feedType == GunScript.FeedType.Mag && ammo2.itemType == AmmoScript.AmmoItemType.Magazine) {
                    soundName = "gunloadmag";
                } else if (gun.feedType == GunScript.FeedType.Direct && ammo2.itemType == AmmoScript.AmmoItemType.Round && gun.roundsInMag < gun.magCapacity) {
                    soundName = "gunloadshell";
                } else if (gun.feedType == GunScript.FeedType.Direct && ammo2.itemType == AmmoScript.AmmoItemType.Magazine && ammo2.rounds > 0) {
                    bool room = gun.roundsInMag < gun.magCapacity || (gun.racked && gun.roundInChamber == GunScript.RoundInChamber.None);
                    if (room && ScavPlayer.TryGetPlayerAndBodyFromClientId(combinerClientId, out _, out Body combiner) && combiner != null && combiner.charType == 1) {
                        soundName = "gunloadshell";
                    }
                }
            }
        } else if ((bool)ammo1 && (bool)ammo2) {
            if (ammo2.itemType == AmmoScript.AmmoItemType.Round && ammo1.itemType == AmmoScript.AmmoItemType.Magazine && ammo2.ammoType == ammo1.ammoType && ammo1.rounds < ammo1.maxRounds) {
                soundName = "gunloadshell";
            }
        } else {
            soundName = VanillaAssetKeys.SoundCombine;
        }

        if (soundName != null) {
            ServerMain.Server_AnnounceSound(it1.transform.position, soundName, ServerMain.GetListOfClientIdsExceptThisAndHost(combinerClientId));
        }
    }

    internal static void PredictFavourite(knetid id, bool want) {
        FavPendingMap[id] = new FavPending {
            want = want,
            until = Time.realtimeSinceStartupAsDouble + ClientMain.RTT_IN_SECONDS_with_margin + 1.0,
        };
    }

    internal static void ClearFavouritePending(knetid id) {
        FavPendingMap.Remove(id);
    }

    internal static bool LocalCustomizing(Item item) {
#if FULLGAME
        if (item == null || !Net.IsClient)
            return false;
        PlayerCamera cam = PlayerCamera.main;
        return cam != null && cam.customizedItem == item;
#else
        return false;
#endif
    }

    internal static void ApplyFavourite(Item item, knetid id, bool value) {
        if (item == null || LocalCustomizing(item))
            return;
        if (FavPendingMap.TryGetValue(id, out FavPending pending)) {
            if (pending.want == value || Time.realtimeSinceStartupAsDouble >= pending.until)
                FavPendingMap.Remove(id);
            else
                return;
        }
        item.favourited = value;
    }

    internal static void EchoFavourite(ScavPlayer plr, SyncInfo si) {
        if (plr == null || si == null || si.item == null)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ItemFavourited);
        writer.Put(si.syncId);
        writer.Put(si.item.favourited);
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    internal static void PushFavourite(Item item) {
        if (!Net.IsRunning || item == null)
            return;
        if (!TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        if (Net.IsServer) {
            si.favourited.Value = item.favourited;
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemFavourited);
        writer.Put(si.syncId);
        writer.Put(item.favourited);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        PredictFavourite(si.syncId, item.favourited);
    }

#if FULLGAME
    struct CosmeticsPending {
        internal string label;
        internal byte swatch;
        internal double until;
    }

    static readonly Dictionary<knetid, CosmeticsPending> CosmeticsPendingMap = new Dictionary<knetid, CosmeticsPending>();

    internal static void PredictCosmetics(knetid id, string label, byte swatch) {
        CosmeticsPendingMap[id] = new CosmeticsPending {
            label = label ?? "",
            swatch = swatch,
            until = Time.realtimeSinceStartupAsDouble + ClientMain.RTT_IN_SECONDS_with_margin + 1.0,
        };
    }

    internal static void ClearCosmeticsPending(knetid id) {
        CosmeticsPendingMap.Remove(id);
    }

    internal static void ApplyCosmetics(Item item, knetid id, string label, byte swatch) {
        if (item == null || LocalCustomizing(item))
            return;
        if (CosmeticsPendingMap.TryGetValue(id, out CosmeticsPending pending)) {
            bool match = pending.swatch == swatch && (pending.label ?? "") == (label ?? "");
            if (match || Time.realtimeSinceStartupAsDouble >= pending.until) {
                CosmeticsPendingMap.Remove(id);
            } else {
                return;
            }
        }
        item.label = label ?? "";
        item.colorSwatch = swatch;
    }

    internal static void EchoCosmetics(ScavPlayer plr, SyncInfo si) {
        if (plr == null || si == null || si.item == null)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_ItemCosmetics);
        writer.Put(si.syncId);
        writer.Put(si.item.label ?? "");
        writer.Put((byte)Mathf.Clamp(si.item.colorSwatch, 0, 255));
        Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
    }

    internal static void PushCosmetics(Item item) {
        if (!Net.IsRunning || item == null)
            return;
        if (!TryGetSyncInfo(item, out SyncInfo si) || si == null)
            return;
        string label = item.label ?? "";
        byte swatch = (byte)Mathf.Clamp(item.colorSwatch, 0, 255);
        if (Net.IsServer) {
            si.itemLabel.Value = label;
            si.colorSwatch.Value = swatch;
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemCosmetics);
        writer.Put(si.syncId);
        writer.Put(label);
        writer.Put(swatch);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        PredictCosmetics(si.syncId, label, swatch);
    }
#endif

    public static bool TryGetItem(knetid syncid, out SyncInfo isi, out Item item) {
        if (NetIdToSyncInfoDict.TryGetValue(syncid, out var isii) && isii.go != null && isii.IsItem()) {
            isi = isii;
            item = isii.item;
            return true;
        }
        isi = null;
        item = null;
        return false;
    }

    public static bool TryGetItemSyncInfo(Item item, out SyncInfo isi) {
        if (item != null && SyncRegistry.TryGetValue(item.gameObject, out var isii)) {
            isi = isii;
            return true;
        }
        isi = null;
        return false;
    }

    public static bool TryGetSyncInfo(Item item, out SyncInfo si) {
        if (item != null && SyncRegistry.TryGetValue(item.gameObject, out var isi)) {
            si = isi;
            return true;
        }
        si = null;
        return false;
    }

    public static bool TryGetItemSyncInfo(knetid item_syncid, out SyncInfo si) {
        if (NetIdToSyncInfoDict.TryGetValue(item_syncid, out var isi) && isi.go != null && isi.IsItem()) {
            si = isi;
            return si != null;
        }
        si = null;
        return false;
    }

    public static bool ItemCanBeIgnoredForNetwork(Item item) {
        if (item == null)
            return true;
        return NetObjectRegistry.ObjectShouldNotBeSynced(item.gameObject);
    }

    static NetBody ParentBody(Transform start) {
        for (Transform t = start; t != null; t = t.parent) {
            if (t.TryGetComponent(out NetBody nb))
                return nb;
        }
        return null;
    }

    static bool OnBody(Transform start, Body body) {
        if (start == null || body == null)
            return false;
        for (Transform t = start; t != null; t = t.parent) {
            if (t.TryGetComponent(out InventorySlot slot) && slot.body == body)
                return true;
            if (t.TryGetComponent(out Limb limb) && limb.body == body)
                return true;
            if (t.TryGetComponent(out NetBody nb) && nb.body == body)
                return true;
        }
        return false;
    }

    static bool Carried(Item item, Body body) {
        if (item == null || body == null)
            return false;
        if (OnBody(item.transform, body))
            return true;
        if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
            return false;
        SyncInfo si = tracker.syncInfo;
        byte data = si.container_data1.Value;
        if (ContainerEncoding.IsLooseInWorld(data))
            return false;
        if (ContainerEncoding.IsAttachedToBody(data))
            return ContainerLink.TryBody(si, out NetBody nb) && nb.body == body;
        if (!ContainerLink.TryBag(si, out SyncInfo bag) || bag.go == null)
            return false;
        return OnBody(bag.go.transform, body);
    }

    internal static void SnapOntoContainer(Item item) {
        if (item == null)
            return;
        Transform parent = item.transform.parent;
        if (parent == null || !parent.TryGetComponent(out Container box))
            return;
        Vector3 at = box.transform.position;
        if ((item.transform.position - at).sqrMagnitude < 0.0001f)
            return;
        item.transform.position = at;
    }

    internal static bool LoadWouldTake(Container dest, Item item, out string why) {
        why = "missing";
        if (dest == null || item == null)
            return false;
        if (item.TryGetComponent(out Container nested) && nested.GetHoldingWeight() > 0f) {
            why = "stacking";
            return false;
        }
        Item bagItem = dest.GetComponent<Item>();
        if (bagItem != null && bagItem.TryGetParentContainer(out _)) {
            why = "bag in a bag";
            return false;
        }
        if (bagItem == item) {
            why = "itself";
            return false;
        }
        if (!dest.CanHoldItem(item)) {
            why = "weight";
            return false;
        }
        if (Vector2.Distance(item.transform.position, dest.transform.position) >= GameplayRanges.CraftGroundScanRadius) {
            why = "distance";
            return false;
        }
        why = null;
        return true;
    }

    public static bool CheckIfBodyReachThisItem(Item item, Body my_body, float maxdist = GameplayRanges.ServerExchangeToleranceDistance, bool check_obstruction = false) {
        return item != null && CheckIfBodyReachThisItem(item.gameObject, my_body, maxdist, check_obstruction);
    }

    public static bool CheckIfBodyReachThisItem(SyncInfo item, Body my_body, float maxdist = GameplayRanges.ServerExchangeToleranceDistance, bool check_obstruction = false) {
        return item != null && CheckIfBodyReachThisItem(item.go, my_body, maxdist, check_obstruction);
    }

    public static bool CheckIfBodyReachThisItem(GameObject item, Body my_body, float maxdist = GameplayRanges.ServerExchangeToleranceDistance, bool check_obstruction = false) {
        if (item == null || my_body == null)
            return false;

        if (item.TryGetComponent(out Item held)) {
            SnapOntoContainer(held);
            if (Carried(held, my_body))
                return true;
        }

        var pb = item.GetComponentInParent<NetBody>();
        if (pb != null && pb.body == my_body)
            return true;

        ItemsContainerInfo ici = ItemGetContainerInfo(item);
        if (!CheckIfBodyCanStealThisItem(item, my_body, ici))
            return false;
        if (!KM.DistanceCheck(item.transform.position, my_body.transform.position, maxdist))
            return false;

        if (check_obstruction) {
            if (ici.is_netbody) {
                if (!Util.AccurateRaycastInteractionCheckObstruction(my_body, ici.plr.body, false))
                    return false;
            } else if (!Util.AccurateRaycastInteractionCheck(my_body, item.transform.position, false)) {
                return false;
            }
        }
        return true;
    }

    public static bool CheckIfBodyCanStealThisItem(GameObject item, Body my_body, ItemsContainerInfo ici = null) {
        return item != null && CheckIfBodyCanStealThisItem(item.GetComponent<Item>(), my_body, ici);
    }

    public static bool CheckIfBodyCanStealThisItem(Item item, Body my_body, ItemsContainerInfo ici = null) {
        ici ??= ItemGetContainerInfo(item);
        if (ici.has_container && ici.bodynpc != null) {
            if (Multiplayer.rules.NoInventoryLock)
                return true;
            Body otherBody = ici.bodynpc.body;
            bool isOnConsciousOtherPlayer = otherBody != my_body && otherBody.conscious && (ici.is_wearing || ici.is_netbody);
            if (isOnConsciousOtherPlayer)
                return false;
        }
        return true;
    }

    internal static void Client_SendDoubleItemPickup(Item i1, Item i2) {
        SyncInfo si1 = NetObjectRegistry.GetSyncInfo(i1);
        SyncInfo si2 = NetObjectRegistry.GetSyncInfo(i2);
        bool any = false;
        if (si1 != null) {
            si1.SetIgnoreTimeForRoundTrip(1);
            any = true;
        }
        if (si2 != null) {
            si2.SetIgnoreTimeForRoundTrip(1);
            any = true;
        }
        if (!any)
            return;

        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_ItemPickup);
        if (si1 != null) {
            ItemsContainerInfo ici = ItemGetContainerInfo(si1.item);
            writer.Put(ici.inv_slot_number);
            writer.Put(si1.syncId);
        }
        if (si2 != null) {
            ItemsContainerInfo ici2 = ItemGetContainerInfo(si2.item);
            writer.Put(ici2.inv_slot_number);
            writer.Put(si2.syncId);
        }
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
    }

    static void DoClientInventoryUpdate() {
        SyncInfo[] newState = RecordInvState(Util.GetLocalBody());
        SyncInfo old = null;
        SyncInfo nn = null;
        try {
            for (int i = 0; i < newState.Length; i++) {
                old = Client_last_inventory_state[i];
                nn = newState[i];
                if (old != null && old.go == null)
                    old = null;

                if (old != nn) {
                    if (nn == null) {
                        if (!newState.Contains(old)) {
                            if (SlotEmptyBecauseBagged(old)) {
                                Client_last_inventory_state[i] = null;
                            } else if (old.item != null && !MoveCommit.Held(old.item) && !SlotHold.Owns(Util.GetLocalBody(), i, old.item) && !CraftCommit.Yielded(old.item)) {
                                MoveCommit.SubmitLive(old.item);
                            }
                        }
                    } else if (nn.item != null && !MoveCommit.Held(nn.item)) {
                        MoveCommit.SubmitLive(nn.item);
                    }
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Container, $"DoClientInventoryUpdate(old={old}, nn={nn})", ex);
        }

        for (int i = 0; i < newState.Length; i++) {
            Client_last_inventory_state[i] = newState[i];
        }
    }

    void FixedUpdate() {
        if (Multiplayer.IsNetworkActiveAndIsWorldGenerated() && Net.IsClient) {
            ExpirePendingParents();
            if (ItemContainerChanged.Count > 0) {
                foreach (SyncInfo item in ItemContainerChanged) {
                    Client_AnnounceContainerChange(item);
                }
                ItemContainerChanged.Clear();
            }
            DoClientInventoryUpdate();
        }
    }
}
