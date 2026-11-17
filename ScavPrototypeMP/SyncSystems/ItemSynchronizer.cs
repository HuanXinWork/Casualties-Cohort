using System;
using System.Collections.Generic;
using System.Diagnostics;
using CasualtiesTogetherUtils;
using HarmonyLib;
using KrokV5Optimization;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 物品与容器权威同步核心（第 1 部分：父容器拓扑解析、穿戴与装包执行、面对面赠送拾取及零耐久销毁）。
/// </summary>
internal partial class ItemSync : CasualtiesTogetherSingleton {
    public static bool verbose => Multiplayer.verbose;

    public static bool CustomItemBehaviourStateCanBeJustByte(Item item) {
        return item.id == "flashlight" || item.id == "emergencylight";
    }

    public static Dictionary<GameObject, SyncInfo> SyncRegistry => NetObjectRegistry.SyncRegistry;
    public static Dictionary<knetid, SyncInfo> NetIdToSyncInfoDict => NetObjectRegistry.NetIdToSyncInfoDict;

    public static HashSet<SyncInfo> ItemContainerChanged = new();

    internal static int SuppressContainerWrite;
    internal static bool ParentTestSwap;

    public const float max_items_distance_to_consider = 240f;
    public const float max_plr_view_distance = GameplayRanges.SimulationKeepRadius;
    public const float max_items_distance_to_consider_sqr = max_items_distance_to_consider * max_items_distance_to_consider;
    public const float max_plr_view_distance_sqr = GameplayRanges.SimulationKeepRadiusSqr;

    internal const int max_items_to_check_per_iteration = 30;
    internal static int cur_items_check_index = 0;
    internal static List<Item> cur_items_check = new List<Item>();

    public class ItemsContainerInfo {
        public bool has_container;
        public bool is_netbody;
        public bool is_in_locplr_surface_inv;
        public bool is_in_surface_inv;
        public knetid knetid;
        public byte containerVer;

        public Item item;

        public bool is_wearing;
        public Limb limb;

        public knetid body_netid;
        public InventorySlot inv_slot;
        public byte inv_slot_number;
        public ScavPlayer plr;

        public Container container;
        public SyncInfo container_si;

        public NetBody bodynpc;

        public byte containerExtraData1;

        public bool JustContainerChanged(ItemsContainerInfo otherici) {
            return otherici == null || container != otherici.container;
        }

        public bool Equals(ItemsContainerInfo otherici) {
            if (otherici == null)
                return false;
            return has_container == otherici.has_container &&
                is_netbody == otherici.is_netbody &&
                item == otherici.item &&
                is_wearing == otherici.is_wearing &&
                limb == otherici.limb &&
                body_netid == otherici.body_netid &&
                inv_slot_number == otherici.inv_slot_number &&
                container == otherici.container;
        }

        public override string ToString() {
            string str = "ICI-";
            if (!has_container) {
                return str + "NONE";
            }
            if (is_wearing) {
                str += $"WEAR-({bodynpc})";
            } else if (container_si != null) {
                str += $"CONT-{container_si}";
                if (bodynpc != null)
                    str += $"-({bodynpc})";
            } else if (inv_slot != null) {
                str += $"INV-{inv_slot_number}-({bodynpc})";
            }
            return str;
        }
    }

    public static ItemsContainerInfo ItemGetContainerInfo(Item item) {
        var ici = new ItemsContainerInfo { item = item };
        if (item == null)
            return ici;

        try {
            Transform p = item.transform.parent;
            if ((bool)p) {
                if (item.Stats.wearable && p.TryGetComponent(out Limb limbSlot)) {
                    ici.has_container = true;
                    ici.is_netbody = true;
                    ici.is_wearing = true;
                    ici.is_in_surface_inv = true;
                    ici.limb = limbSlot;

                    if (limbSlot.body.TryGetNetBody(out ici.bodynpc)) {
                        ici.plr = ici.bodynpc.plr;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.knetid = ici.body_netid;
                        ici.containerVer = ContainerLink.VersionOf(ici.bodynpc);
                    } else {
                        ModLog.Error($"Item is worn without NetBody: {item.name}\n{new StackTrace()}", LogDomain.Container);
                    }
                } else if (p.TryGetComponent(out InventorySlot invSlot)) {
                    ici.has_container = true;
                    ici.is_netbody = true;
                    ici.inv_slot = invSlot;
                    ici.inv_slot_number = (byte)invSlot.slot;
                    ici.is_in_surface_inv = true;
                    if (invSlot.body.IsBodyLocal()) {
                        ici.is_in_locplr_surface_inv = true;
                    }

                    if (invSlot.body.TryGetNetBody(out ici.bodynpc)) {
                        if (ici.bodynpc.syncBody == null)
                            return new ItemsContainerInfo();
                        ici.plr = ici.bodynpc.plr;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.knetid = ici.body_netid;
                        ici.containerVer = ContainerLink.VersionOf(ici.bodynpc);
                    } else {
                        ModLog.Error($"Item is in InventorySlot without NetBody: {item.name}\n{new StackTrace()}", LogDomain.Container);
                    }
                } else if (p.TryGetComponent<Container>(out var cont)) {
                    ici.has_container = true;
                    ici.bodynpc = ParentBody(item.transform);
                    if (ici.bodynpc == null) {
                        ici.is_netbody = false;
                    } else {
                        if (ici.bodynpc.syncBody == null)
                            return new ItemsContainerInfo();
                        ici.is_netbody = true;
                        ici.body_netid = ici.bodynpc.netId;
                        ici.plr = ici.bodynpc.plr;
                    }

                    ici.container = cont;
                    if (NetObjectRegistry.TryGetSyncInfoOrRegister(p, out var isi)) {
                        ici.container_si = isi;
                        ici.knetid = isi.syncId;
                        ici.containerVer = isi.Version;
                    }
                }
            }

            if (ici.is_wearing) {
                ici.containerExtraData1 = ContainerEncoding.WearableByte;
            } else if (ici.is_in_surface_inv) {
                ici.containerExtraData1 = ContainerEncoding.EncodeBodySlot(ici.inv_slot_number);
            } else if (ici.has_container) {
                ici.containerExtraData1 = ContainerEncoding.ContainerByte;
            } else {
                ici.containerExtraData1 = ContainerEncoding.WorldByte;
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Container, $"ItemGetContainerInfo({ici})", ex);
        }

        return ici;
    }

    public static ItemsContainerInfo ItemGetContainerInfo(GameObject go) {
        return go != null ? ItemGetContainerInfo(go.GetComponent<Item>()) : new ItemsContainerInfo();
    }

    public static void ApplyWearableSpriteSwap(Item item, Body body) {
        if (item == null || body == null)
            return;
        if (!item.TryGetComponent<WearableSpriteSwap>(out var swap) || swap == null)
            return;
#if FULLGAME
        int chr = CharacterSpawn.ClampChar(body.charType);
#else
        int chr = Mathf.Max(0, body.charType);
#endif
        SpriteRenderer sr = item.GetComponent<SpriteRenderer>();
        if (sr != null && swap.sprites != null && swap.sprites.Count > chr && swap.sprites[chr] != null) {
            sr.sprite = swap.sprites[chr];
        }
        float scale = 1f;
        if (swap.customScale != null && swap.customScale.Count > chr && swap.customScale[chr] != 0f) {
            scale = swap.customScale[chr];
        }
        item.transform.localScale = Vector3.one * scale;
    }

    public static void RefreshWearableSpeciesSprites(Body body) {
        if (body == null)
            return;
        foreach (Item item in body.GetAllWearables()) {
            if (item == null)
                continue;
            ApplyWearableSpriteSwap(item, body);
            if (item.TryGetComponent<Wearable>(out var wearable)) {
                item.TryGetComponent(out WearableSpriteSwap swap);
                wearable.CreateSprites(body, swap);
            }
        }
    }

    public static void Body_ForceWearWearable(Body body, Item item) {
        Limb limb = body.LimbByName(item.Stats.desiredWearLimb);
        if (limb == null || limb.dismembered)
            return;

        Item wearableBySlotId = body.GetWearableBySlotID(item.Stats.wearSlotId);
        if (wearableBySlotId != null) {
            if (wearableBySlotId == item)
                return;
            body.DropWearable(wearableBySlotId);
        }
        if (item.transform.parent != null) {
            SafeUnloadItem(item);
        }

        if (item.TryGetParentContainer(out var cont)) {
            cont.UnloadItem(item);
            if (body.IsBodyLocal())
                PlayerCamera.main.PlayBackpackSound();
        }
        item.rb.simulated = false;
        item.GetComponent<SpriteRenderer>().sortingOrder = limb.GetComponent<SpriteRenderer>().sortingOrder + item.Stats.wearableVisualOffset;
        item.transform.SetParent(limb.transform);
        item.transform.localRotation = Quaternion.identity;
        item.transform.localPosition = Vector3.zero;
#if FULLGAME
        ApplyWearableSpriteSwap(item, body);
#endif
        if (!item.TryGetComponent<WearableSpriteSwap>(out _)) {
            item.transform.localScale = Vector3.one;
        }
        if (item.TryGetComponent<Wearable>(out var wearable)) {
#if FULLGAME
            item.TryGetComponent(out WearableSpriteSwap swap);
            wearable.CreateSprites(body, swap);
#else
            wearable.CreateSprites(body);
#endif
        }
    }

    public static void Container_ForceLoadItem(Item item, Container container) {
        if (BrokenContainer(container))
            ParentTestChest("chest FAIL load", item, container != null ? container.gameObject : null);
        Container left = null;
        if (item != null && item.TryGetParentContainer(out Container was))
            left = was;
        BagSleep.Wake(item);
        item.transform.SetParent(container.transform);
        item.rb.simulated = false;
        item.transform.position = container.GetComponent<Collider2D>().bounds.center;
        item.GetComponent<SpriteRenderer>().enabled = container.itemsVisible;
        if (container.itemsVisible) {
            item.GetComponent<SpriteRenderer>().sortingOrder = container.GetComponent<SpriteRenderer>().sortingOrder;
            item.transform.localEulerAngles = new Vector3(0f, 0f, 180f);
        }
        if ((bool)item.GetComponent<Wearable>()) {
            item.GetComponent<Wearable>().ClearSprites();
        }
        Container.UpdateItemLight(item.gameObject, !container.itemsVisible);

        BagSleep.Refresh(container);
        if (left != container)
            BagSleep.Refresh(left);
    }

    public static bool ConfirmItemOffer(NetBody giver, NetBody taker, Item item) {
        if (item == null || giver == null || taker == null)
            return false;
        // 异步弹窗确认期间状态可能已变更，必须复核双方存活、赠送者清醒、交互容差距离与物品可达性
        if (giver.body == null || taker.body == null || !giver.body.alive || !taker.body.alive || !giver.body.conscious)
            return false;
        if (!KM.DistanceCheck(giver, taker, GameplayRanges.ServerExchangeToleranceDistance))
            return false;
        if (!CheckIfBodyReachThisItem(item, giver.body))
            return false;

        ItemsContainerInfo ici = ItemGetContainerInfo(item);
        return !ici.has_container || ici.bodynpc == giver;
    }

    public static void ForcePickupItem(NetBody body, Item item) {
        if (body == null || body.body == null || item == null || item.Stats == null)
            return;

        Body targetBody = body.body;
        ItemsContainerInfo prevIci = ItemGetContainerInfo(item);

        // 先预检接收者是否有合法穿戴位、空物品槽或可容纳随身容器；满格时绝不卸下赠送者手中的物品，防止客户端手槽撕裂
        bool canWear = false;
        int targetSlot = -1;
        Container targetContainer = null;

        if (item.Stats.wearable) {
            Limb wearLimb = targetBody.LimbByName(item.Stats.desiredWearLimb);
            if (wearLimb != null && !wearLimb.dismembered && targetBody.GetWearableBySlotID(item.Stats.wearSlotId) == null)
                canWear = true;
        }

        if (!canWear && (!item.Stats.wearable || item.Stats.wearableCanBeHeld) && targetBody.slots != null) {
            for (int i = 0; i < targetBody.slots.Length; i++) {
                InventorySlot hold = targetBody.slots[i];
                if (hold == null || targetBody.HoldingItem(i) || !hold.canPickUp)
                    continue;
                if (!HostCanSeat(targetBody, item, i, out _) || SlotHold.Other(targetBody, i, item))
                    continue;
                targetSlot = i;
                break;
            }
        }

        if (!canWear && targetSlot < 0) {
            bool nestedNonEmpty = item.TryGetComponent(out Container ownBox) && ownBox.GetHoldingWeight() > 0f;
            if (!nestedNonEmpty) {
                targetContainer = FindAcceptingContainer(targetBody.GetAllWearables(), item)
                    ?? FindAcceptingContainer(targetBody.GetSurfaceInventoryItems(), item);
            }
        }

        if (!canWear && targetSlot < 0 && targetContainer == null) {
            body.plr?.Server_DoAlertSingle("Inventory full.");
            if (prevIci.plr != null && prevIci.plr != body.plr)
                prevIci.plr.Server_DoAlertSingle("Target inventory is full.");
            ModLog.Flow(LogDomain.Item, "ForcePickupItem.Full", $"target={body} item={item.id}");
            return;
        }

        SuppressContainerWrite++;
        try {
            SafeUnloadItem(prevIci);
            item.transform.position = body.GetPosition();
            if (canWear) {
                Body_ForceWearWearable(targetBody, item);
                if (targetBody.IsBodyLocal() && PlayerCamera.main != null)
                    PlayerCamera.main.UpdateWearables();
            } else if (targetSlot >= 0) {
                targetBody.PickUpItem(item, targetSlot, true);
                if (!targetBody.HoldingItem(item))
                    HeldSeat.Seat(targetBody, item, targetSlot);
            } else if (targetContainer != null) {
                Container_ForceLoadItem(item, targetContainer);
            }
        } finally {
            SuppressContainerWrite--;
        }

        if (prevIci.bodynpc != null && prevIci.bodynpc.body != null && prevIci.bodynpc.body.IsBodyLocal())
            RecordAndApplyInvState(prevIci.bodynpc.body);
        if (targetBody.IsBodyLocal())
            RecordAndApplyInvState(targetBody);

        HeldSeat.Quiet(item);
        if (Net.IsServer && TryGetSyncInfo(item, out SyncInfo si) && si != null && !si.IsDestroyed)
            si.WriteObjectIntoPacket();
    }

    static Container FindAcceptingContainer(IEnumerable<Item> carriers, Item item) {
        if (carriers == null || item == null)
            return null;
        foreach (Item carrier in carriers) {
            if (carrier == null || carrier == item || !carrier.TryGetComponent(out Container box) || box == null)
                continue;
            if (BrokenContainer(box) || !box.CanHoldItem(item))
                continue;
            return box;
        }
        return null;
    }

    public static void SafeUnloadItem(Item item) {
        SafeUnloadItem(ItemGetContainerInfo(item));
    }

    public static void SafeUnloadItem(ItemsContainerInfo ici) {
        if (!FixInvSync.SkipLoose(ici))
            return;
        if (!ici.has_container)
            return;

        if (ici.item.TryGetSyncInfo(out SyncInfo si)) {
            si._position.Value = ici.item.transform.position;
        }
        if (ici.is_wearing) {
            if (NetBody.TryGetNetBodyFromId(ici.body_netid, out var nb)) {
                Body_DropWearable_MultiplayerPatch.Force(nb.body, ici.item);
            }
        } else if (ici.inv_slot != null) {
            ici.inv_slot.body.DropItem(ici.item);
            if (si != null) {
                int idx = Array.IndexOf(Client_last_inventory_state, si);
                if (idx != -1) {
                    Client_last_inventory_state[idx] = null;
                }
            }
        } else {
            ici.container.UnloadItem(ici.item);
            if (PlayerCamera.main.currentContainer == ici.container) {
                PlayerCamera.main.RepopulateContainer();
            }
        }
    }

    internal static bool TooSpentToUse(Item item) {
        if (item == null)
            return true;
        if (Net.IsRunning && TryGetSyncInfo(item, out SyncInfo si) && si != null && si.IsDestroyed)
            return true;
        if (item.Stats != null && item.Stats.wearable && !item.Stats.usable)
            return false;
        if (item.id == "bloodbag" || item.id == "bloodbaghuman")
            return false;
        return item.condition <= 0.0001f;
    }

    [HarmonyPatch(typeof(Item), nameof(Item.DrawBlood))]
    static class Item_DrawBlood_MultiplayerPatch {
        static void Postfix(WaterContainerItem wat, Limb limb) {
            if (!Net.IsServer || wat == null)
                return;
            Item item = wat.GetComponent<Item>();
            if (item != null && NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo si) && si != null && !si.IsDestroyed) {
                si.WriteObjectIntoPacket();
            }
            if (limb != null && limb.body != null && limb.body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
    }

    public static void BetterUseItem(Body body, Item item) {
        SetBodyOverride(item, body);
#if FULLGAME
        CharacterSpawn.EnsureBodySpecies(body);
#endif
        if (item.Stats.wearable) {
            Body_ForceWearWearable(body, item);
            if (item.transform.parent != null) {
                if (body.IsBodyLocal()) {
                    PlayerCamera.main.UpdateWearables();
                }
                return;
            }
            item.transform.position = body.transform.position;
        }
        if (item.Stats.usable) {
            if (TooSpentToUse(item))
                return;
            item.Stats.useAction(body, item);
            if (item != null && item.condition < 0f) {
                item.condition = 0f;
            }
            SpentDestroy(item);
            if (Net.IsServer && item != null && TryGetSyncInfo(item, out SyncInfo spent) && spent != null && !spent.IsDestroyed) {
                spent.WriteObjectIntoPacket();
            }
        }
    }

    public static void SetBodyOverride(Item item, Body body) {
        body.GetCoUtilsInstance().SelectForMPOverride();
        item.GetOrAddComponent<BodyGetterOverrider>().SetBody(body);
    }

    internal static bool DyingContainer(Item item) {
        if (item == null || item.condition > 0f || item.Stats == null || !item.Stats.destroyAtZeroCondition)
            return false;
        if (GravBagBoom.Armed(item, item.condition))
            return false;
        if (item.GetComponent<Container>() == null)
            return false;
        return NetObjectRegistry.IsRegistered(item.gameObject);
    }

    internal static bool BrokenContainer(Container box) {
        if (box == null || !box.TryGetComponent(out Item bag))
            return false;
        return DyingContainer(bag);
    }

    internal static bool BrokenBag(SyncInfo bag, Container box) {
        if (BrokenContainer(box))
            return true;
        if (bag == null || bag.go == null || !bag.go.TryGetComponent(out Item holder))
            return false;
        return DyingContainer(holder);
    }

    internal static void SpentDestroy(Item item) {
        if (!Net.IsServer || item == null || item.condition > 0f)
            return;
        if (item.Stats == null || !item.Stats.destroyAtZeroCondition)
            return;
        if (GravBagBoom.Armed(item, item.condition))
            return;

        if (item.TryGetComponent(out Container box)) {
            ParentTestChest("chest spill host", item, item.gameObject);
            box.ContainerBroke();
            if (NetObjectRegistry.TryGetSyncInfo(item.gameObject, out SyncInfo bag) && bag != null && !bag.IsDestroyed) {
                ReleaseNamedContents(bag);
                bag.Destroy();
            } else {
                UnityEngine.Object.Destroy(item.gameObject, 0.05f);
            }
            return;
        }
        UnityEngine.Object.Destroy(item.gameObject);
    }

    internal static void ReleaseNamedContents(SyncInfo bag) {
        if (bag == null || bag.IsDestroyed)
            return;
        ushort id;
        try {
            id = bag.syncId;
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Container, "ReleaseNamedContents.SyncId", ex.Message);
            return;
        }
        if (id == 0)
            return;

        List<SyncInfo> named = null;
        foreach (SyncInfo other in NetObjectRegistry.SyncRegistry.Values) {
            if (other == null || other == bag)
                continue;
            try {
                if ((ushort)other.container_netId.Value != id)
                    continue;
            } catch (Exception ex) {
                ModLog.Flow(LogDomain.Container, "ReleaseNamedContents.Check", ex.Message);
                continue;
            }
            named ??= new List<SyncInfo>();
            named.Add(other);
        }
        if (named == null)
            return;

        for (int i = 0; i < named.Count; i++) {
            named[i].WriteContainerInfo();
            ParentTestChest("chest host clear", named[i].item, bag.go);
        }
    }
}
