using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokV5Optimization;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.Tilemaps;
using static GunScript;

namespace Together {
    /// <summary>
    /// SyncInfo 的客户端预制体生成、网格重绑、平滑姿态插值与物品/建筑状态应用分部类。
    /// 注意：严禁在本分部文件中声明任何 SyncVar / SyncString / SyncList，以保护 SyncInfo.cs 中的反射字段顺序。
    /// </summary>
    public partial class SyncInfo {
        public bool CreateObject() {
            if (go == null) {
                RebindMesh();
            }

            if (tracker != null && go == null && tracker.gameObject != null && ReferenceEquals(tracker.syncInfo, this)) {
                go = tracker.gameObject;
            }

            if (tracker != null && go != null) {
                tracker.syncId = Id;
                objectWasSpawned = true;
                if (!NetObjectRegistry.SyncRegistry.TryGetValue(go, out SyncInfo listed) || !ReferenceEquals(listed, this)) {
                    NetObjectRegistry.SyncRegistry[go] = this;
                }
                return true;
            }
            if (tracker != null && tracker.gameObject != null) {
                return false;
            }

            if (go == null) {
                if (!Net.IsServer && TrapClaim.GlassBlocked(Id)) {
                    return false;
                }
                if (!Util.IsWorldInstantiated() || string.IsNullOrEmpty(resourceId.Value)) {
                    return false;
                }
                if (objectWasSpawned && Util.IsGeneratingWorld()) {
                    return false;
                }

                go = InstantiateResource(resourceId.Value, SpawnNearOwner());
            }
            if (go != null) {
                objectWasSpawned = true;
                if (tracker == null) {
                    tracker = go.GetOrAddComponent<SyncInfoGameObjectTracker>();
                    tracker.syncInfo = this;
                    tracker.syncId = Id;
                    NetObjectRegistry.SyncRegistry[go] = this;
                }
                if (!Net.IsServer) {
                    ApplyPacketToObject();
                }
                return true;
            }
            return false;
        }

        private Vector2 SpawnNearOwner() {
            Vector2 pos = _position.Value;
            if (Net.IsServer || container_data1.Value == ContainerEncoding.WorldByte) {
                return pos;
            }

            try {
                byte data = container_data1.Value;
                if (data >= ContainerEncoding.WearableByte && ContainerLink.TryBody(this, out NetBody nb) && nb != null) {
                    return nb.transform.position;
                }
                if (data == ContainerEncoding.ContainerByte && ContainerLink.TryBag(this, out SyncInfo bag) && bag.container != null) {
                    return bag.go.transform.position;
                }
            } catch (Exception) {
                // 容器宿主实体尚未就绪或销毁瞬间访问 Transform 异常时安全回退至同步坐标
            }
            return pos;
        }

        private static readonly HashSet<string> UnfoundResourceIds = new HashSet<string>();

        private GameObject InstantiateResource(string resourceId, in Vector2 pos) {
            bool isBackground = false;
            GameObject resource = Resources.Load<GameObject>(resourceId);

            if (resource == null) {
                if (condition.Value <= 0f) {
                    return null;
                }
                if (resourceId.StartsWith(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix)) {
                    if (ScavMPBuildingSynchronizer.known_entities_with_nonunique_id.TryGetValue(resourceId, out GameObject bgstuff)) {
                        resource = bgstuff;
                        if (resource.TryGetComponent(out Tilemap _)) {
                            isBackground = true;
                        }
                    } else {
                        if (Util.IsGeneratingWorld()) {
#if TESTING
                            log.warn($"Special resource was tried to get created too early! {resourceId}");
#endif
                            return null;
                        }
                        resource = Resources.Load<GameObject>(resourceId.Substring(BuildingEntity_Start_MultiplayerPatch.identified_bg_prefix.Length));
                        if (resource) {
#if TESTING
                            log.warn($"Special resource wasn't known. But Resources.Load found something! {resourceId}");
#endif
                        } else {
                            if (UnfoundResourceIds.Add(resourceId)) {
                                ModLog.Error(LogDomain.ObjectSync, $"Special resource wasn't identified! {resourceId}");
                            }
                            return resource;
                        }
                    }
                } else {
                    if (UnfoundResourceIds.Add(resourceId)) {
                        ModLog.Error(LogDomain.ObjectSync, $"Resource does not exist: {resourceId}");
                    }
                    return resource;
                }
            }
            if (resource == null) {
                return null;
            }

            GameObject instantiatedObject = UnityEngine.Object.Instantiate(resource, (Vector3)pos, Quaternion.identity);
            if (isBackground) {
                instantiatedObject.transform.SetParent(WorldGeneration.world.worldGrid.transform);
            }
            if (!Net.IsServer) {
                FixContainerBroke.Strip(instantiatedObject, true);
            }
            return instantiatedObject;
        }

        private void RebindMesh() {
            if (go != null) {
                return;
            }

            if (tracker != null && tracker.gameObject != null && ReferenceEquals(tracker.syncInfo, this)) {
                go = tracker.gameObject;
                NetObjectRegistry.SyncRegistry[go] = this;
                return;
            }
            foreach (KeyValuePair<GameObject, SyncInfo> pair in NetObjectRegistry.SyncRegistry) {
                if (!ReferenceEquals(pair.Value, this) || pair.Key == null) {
                    continue;
                }

                go = pair.Key;
                if (tracker == null && go.TryGetComponent(out SyncInfoGameObjectTracker found)) {
                    tracker = found;
                }
                return;
            }
        }

        private void DestroyTrackedGameObject() {
            if (go == null) {
                RebindMesh();
            }
            if (go == null) {
                return;
            }

            GameObject mesh = go;
            if (tracker != null) {
                tracker.syncInfo = null;
            } else if (mesh.TryGetComponent(out SyncInfoGameObjectTracker onGo)) {
                onGo.syncInfo = null;
            }

            NetObjectRegistry.SyncRegistry.Remove(mesh);
            try {
                if (!Net.IsServer) {
                    ApplyPacketToObject();
                }
                if (mesh.TryGetComponent(out Item boom)) {
                    GravBagBoom.Play(boom, Mathf.Min(boom.condition, condition.Value));
                }
            } catch (Exception e) {
                ModLog.Exception(LogDomain.ObjectSync, $"SyncInfo destroy mesh {this}", e);
            }
            if (mesh != null) {
                UnityEngine.Object.Destroy(mesh);
            }
        }

        // 0: 未知, 1: 物品, 2: 静态状态建筑, 3: 含插值末端的动态建筑（如抓人花）
        private byte _applyKind;
        private float _appliedCondition;
        private byte _appliedS1;
        private float _appliedS2;
        private bool _appliedBg;
        private float _appliedTraderX;
        private float _appliedTraderY;

        private bool BuildingPacketUnchanged() {
            if (_applyKind != 2) {
                return false;
            }

            Vector2_4byte_512 trader = trader_desiredpos.Value;
            return condition.Value == _appliedCondition
                && specialdata1.Value == _appliedS1
                && specialdata2.Value == _appliedS2
                && building_backgroundified.Value == _appliedBg
                && trader.x == _appliedTraderX
                && trader.y == _appliedTraderY;
        }

        private void RememberBuildingPacket() {
            _appliedCondition = condition.Value;
            _appliedS1 = specialdata1.Value;
            _appliedS2 = specialdata2.Value;
            _appliedBg = building_backgroundified.Value;
            Vector2_4byte_512 trader = trader_desiredpos.Value;
            _appliedTraderX = trader.x;
            _appliedTraderY = trader.y;
            _applyKind = _applyKind == 3 ? (byte)3 : (byte)2;
        }

        private void ApplyPacketToObject() {
            updateTimer = 0;

            if (go.TryGetComponent(out Item _)) {
                _applyKind = 1;
                UpdateItem();
                UpdateLiquids();
            } else if (go.TryGetComponent(out BuildingEntity _)) {
                UpdateBuilding();
                RememberBuildingPacket();
            }
        }

        private void UpdateLiquids() {
            if (!go.TryGetComponent(out WaterContainerItem wci)) {
                return;
            }

            CraftCommit.ClearSeenLiquids();
            for (int n = 0; n < _liquidStack.Count; n++) {
                CraftCommit.SeeLiquid(_liquidStack[n].LiquidId, LiquidStackPart.Pack(_liquidStack[n].LiquidMl));
            }
            if (CraftCommit.SkipSeenLiquids(syncId)) {
                return;
            }

            wci.stack.Clear();
            for (int i = 0; i < _liquidStack.Count; i++) {
                LiquidStackPart liquid = _liquidStack[i];
                string liquidid = Item_SetupItems_Listener.LiquidNetIdToId[liquid.LiquidId];
                wci.stack.Add(new LiquidStack(liquidid, liquid.LiquidMl));
            }
        }

        private void UpdateItemContainer() {
            if (go == null || item == null) {
                return;
            }

            ItemSync.ParentTestHand(item);
            if (MoveCommit.Held(syncId) || MoveCommit.StaleContainer(this) || ItemSync.HoldContainerReseat(this)) {
                return;
            }

            Item parked = null;
            MoveCommit.PushApply();
            try {
                bool run = FixInvSync.KeepHeld(this) && BagPlace.BeforeApply(this);
                if (container_data1.Value < ContainerEncoding.BodySlotOffset) {
                    CraftCommit.FinishAside(item);
                }

                if (run && container_data1.Value == ContainerEncoding.WorldByte) {
                    bool deadHold = OnDismemberedLimb(go.transform.parent);
                    if (go.transform.parent != null && (!Ours(go.transform.parent) || deadHold)) {
                        ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
                        ItemSync.SafeUnloadItem(ici);
                    }
                    if (item != null && item.transform.parent != null && (!Ours(item.transform.parent) || OnDismemberedLimb(item.transform.parent))) {
                        HeldSeat.Detach(item);
                    } else if (item != null && item.transform.parent == null) {
                        HeldSeat.Release(item);
                    }
                } else if (run) {
                    if (!Net.IsServer && go.transform.parent == null) {
                        go.transform.position = SpawnNearOwner();
                    }

                    ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
                    if (CheckIfContainerIsWrong(ici)) {
                        if (container_data1.Value >= ContainerEncoding.WearableByte) {
                            if (ContainerLink.TryBody(this, out NetBody nb)) {
                                if (container_data1.Value == ContainerEncoding.WearableByte) {
                                    go.transform.position = nb.transform.position;
                                    if (!item.Stats.wearable) {
                                        nb.body.AutoPickUpItem(item);
                                    } else {
                                        ItemSync.Body_ForceWearWearable(nb.body, item);
                                    }
                                } else {
                                    int slot = ContainerEncoding.DecodeBodySlot(container_data1.Value);
                                    CraftCommit.ReleaseIfLeaving(item, slot);
                                    bool slotOk = nb.body.slots != null && slot >= 0 && slot < nb.body.slots.Length;
                                    bool stole = false;
                                    if (slotOk && nb.body.slots[slot].limb != null && nb.body.slots[slot].limb.dismembered) {
                                        HeldSeat.Detach(item);
                                        slotOk = false;
                                    }
                                    if (slotOk && nb.body.HoldingItem(slot)) {
                                        Item occupant = nb.body.slots[slot].transform.childCount > 0
                                            ? nb.body.slots[slot].transform.GetChild(0).GetComponent<Item>()
                                            : null;
                                        if (occupant == item) {
                                            HeldSeat.Show(item);
                                        } else if (occupant != null && occupant != item && CraftCommit.Yielded(item)) {
                                            stole = true;
                                        } else if (occupant != null && occupant != item && SlotHold.Other(nb.body, slot, item)) {
                                            int? open = SlotHold.NextOpen(nb.body);
                                            if (open.HasValue) {
                                                nb.body.PickUpItem(item, open.Value, true);
                                                if (!nb.body.HoldingItem(item)) {
                                                    HeldSeat.Seat(nb.body, item, open.Value);
                                                }
                                                if (nb.IsBodyLocal()) {
                                                    parked = item;
                                                }
                                            } else {
                                                HeldSeat.SeatBehind(nb.body, item, slot);
                                            }
                                            stole = true;
                                        } else if (occupant != null && occupant != item) {
                                            CraftCommit.StepAside(occupant);
                                        }
                                    }
                                    if (slotOk && !stole) {
                                        nb.body.PickUpItem(item, slot, true);
                                    }
                                    if (slotOk && !stole && !nb.body.HoldingItem(item)) {
                                        HeldSeat.Seat(nb.body, item, slot);
                                    }
                                }
                                if (nb.IsBodyLocal()) {
                                    ItemSync.RecordAndApplyInvState(nb.body);
                                }
                                if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                                    DebugHelp.OnNetEvent(nb.transform.position, $"OBJSYNC: Item pickup {this} -> {nb}  data1: {container_data1}");
                                }
                            }
                        } else if (ContainerLink.TryBag(this, out SyncInfo container_si) && container_si.container != null) {
                            if (ItemSync.BrokenBag(container_si, container_si.container)) {
                                ItemSync.ParentTestChest("chest skip apply", item, container_si.go);
                            } else {
                                ItemSync.Container_ForceLoadItem(item, container_si.container);
                            }
                        }
                    } else if (ici.has_container) {
                        _position.Value = go.transform.position;
                    }
                }
                FixInvSync.AfterApply(this);
                BagPlace.AfterApply(this);
                if (item != null && container_data1.Value >= ContainerEncoding.WearableByte) {
                    HeldSeat.Quiet(item);
                }
            } finally {
                MoveCommit.PopApply();
                if (parked != null) {
                    MoveCommit.SubmitLive(parked);
                }
            }
        }

        private static bool OnDismemberedLimb(Transform parent) {
            if (parent == null) {
                return false;
            }
            if (parent.TryGetComponent(out InventorySlot slot)) {
                return slot.limb != null && slot.limb.dismembered;
            }
            if (parent.TryGetComponent(out Limb limb)) {
                return limb.dismembered;
            }
            return false;
        }

        private static bool Ours(Transform parent) {
            if (parent.TryGetComponent(out InventorySlot slot)) {
                return slot.body != null && slot.body.IsBodyLocal();
            }
            if (parent.TryGetComponent(out Limb limb)) {
                return limb.body != null && limb.body.IsBodyLocal();
            }
            return false;
        }

        private void UpdateItem() {
            if (CraftCommit.EatDestroyed(this) || item == null) {
                return;
            }

            float cond = Mathf.Max(0f, condition.Value);
            bool predictedZero = item.Stats != null && item.Stats.destroyAtZeroCondition && item.condition <= 0f && cond > item.condition;
            bool keepZero = predictedZero && (Net.IsServer || IsIgnored());
            bool localWet = item.id == "torch" && Net.IsClient && TorchWet.InWetLiquid(item);
            bool blockRaise = localWet && cond > item.condition;
            if (!keepZero && !CraftCommit.BlockCondition(syncId, cond) && !blockRaise) {
                if (predictedZero && log.verbose) {
                    log.l($"[spent] sync={syncId} id={item.id} local={item.condition:0.###} host={cond:0.###}");
                }
                item.condition = cond;
            }
            ItemSync.ApplyFavourite(item, syncId, favourited.Value);
#if FULLGAME
            ItemSync.ApplyCosmetics(item, syncId, itemLabel.Value, colorSwatch.Value);
#endif
            item.wetTime = localWet ? Time.time : Time.time - wetTime;

            UpdateItemContainer();

            if (freshitemdrop) {
                item.GetOrAddComponent<FreshItemDrop>();
            }
            if (compressedboughtitemtime > 0) {
                BoughtItem bi = item.GetOrAddComponent<BoughtItem>();
                bi.time = boughtitemtime;
            }

            if (item.TryGetComponent(out CustomItemBehaviour cib) && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
                if (!Body_UseItemInHand_MultiplayerPatch.StaleState(syncId, specialdata1)) {
                    cib.state = specialdata1;
                }
                if (cib.data != null && cib.data.Length > 0 && cib.data[0] is float) {
                    cib.data[0] = specialdata2;
                }
            } else if (item.TryGetComponent(out PlushScript plush)) {
                if (plush.index != specialdata1) {
                    plush.index = specialdata1;
                    j.Prefix(plush);
                    plush.GetComponent<SpriteRenderer>().sprite = plush.possibleSprites[specialdata1];
                    plush.selectedSound = plush.possibleSounds[specialdata1];
                }
            }

            if (item.battery != null) {
                DoBattery(item.battery);
            }

            if (item.TryGetComponent(out GunScript gun)) {
                // Issue #50: 原版 GunScript.Update() 每帧对比 lastRacked 与 racked 判定拉栓/复位边沿；
                // 若仅更新 racked 而不对齐 lastRacked，客机将误触发拉栓抛壳/播音效或擅自推弹入膛扣减 roundsInMag。
                gun.lastRacked = gun_racked;
                gun.racked = gun_racked;
                gun.safe = gun_safe;
                gun.hasMag = gun_hasMag;
                gun.roundInChamber = gun_roundInChamber
                    ? (gun_roundInChamber_is_casing ? RoundInChamber.Casing : RoundInChamber.Round)
                    : RoundInChamber.None;
                gun.roundsInMag = rounds;
            } else if (item.TryGetComponent(out AmmoScript ammo)) {
                ammo.rounds = rounds;
            } else if (Net.IsClient && item.TryGetComponent(out NonDescriptCan nonDescCan)) {
                // PR #48: 罐头可能一经生成便装入休眠容器而尚未跑过 Start()，在首次绑定 SyncInfo 应用状态时即触发拉取权威数值。
                NonDescriptCanSync.EnsureClientSynced(nonDescCan, syncId);
            }
        }

        private void UpdateBuilding() {
            BuildingEntity b = go.GetComponent<BuildingEntity>();
            b.SetHealth(condition, false);
            if (Net.IsClient && b.GetHealth() < 0.5f) {
                BuildingEntity_Update_MultiplayerPatch.PlayDeathOnce(b);
            }

            if (building_backgroundified.Value) {
                TrackerBuildingEntityBackgroundified tr = b.GetOrAddComponent<TrackerBuildingEntityBackgroundified>();
                if (!tr.is_backgroundified) {
                    b.Backgroundify();
                }
                tr.is_backgroundified = building_backgroundified.Value;
            }

            if (b.TryGetComponent(out CorpseScript corpse)) {
                corpse.GetComponent<SpriteRenderer>().sprite = corpse.startSprites[specialdata1];
            } else if (b.TryGetComponent(out SpikeStabberScript spike)) {
                bool isActivated = specialdata1.Value.ToBool();
                if (isActivated != spike.activated) {
                    if (isActivated) {
                        spike.Stab();
                    } else {
                        if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                            DebugHelp.OnNetEvent(spike.transform.position, "RESET SPIKE", Color.cyan);
                        }
                        Animator anim = spike.GetComponent<Animator>();
                        anim.Play("SpikeStab", -1, 0);
                        anim.speed = 0;
                        spike.activated = false;
                    }
                }
            } else if (b.TryGetComponent(out BearTrap bear)) {
                if (specialdata1.Value > 0) {
                    TrapClaim.SpendBear(bear);
                }
            } else if (b.TryGetComponent(out JumpPadScript pad)) {
                TrapClaim.RaiseIfIdle(pad, specialdata1.Value);
            } else if (b.TryGetComponent(out CoilScript coil)) {
                TrapClaim.RaiseIfIdle(coil, specialdata1.Value);
            } else if (b.TryGetComponent(out GunmineScript gun)) {
                TrapClaim.RaiseGun(gun, specialdata1.Value);
            } else if (b.TryGetComponent(out DrillPod_Update_MultiplayerPatch.DrillPodMultiplayerRewrite drel)) {
                if (specialdata1 == 1 && !drel.working) {
                    DrillPod_OnUse_MultiplayerPatch.ForceRepairDrill(drel.drill);
                }
            } else if (go.TryGetComponent(out GrabberPlant grabber)) {
                _applyKind = 3;
                if (GrabberPlant_Update_MultiplayerPatch.Live(go.transform.position)) {
                    GrabberPlant_Update_MultiplayerPatch.Follow(grabber, this);
                }
            } else if (go.TryGetComponent(out TraderScript trader)) {
                trader.desiredPos = trader_desiredpos.Value;
            } else if (b.TryGetComponent(out SoundCannon _)) {
                if (specialdata1.Value != 0) {
                    b.GetOrAddComponent<TrackerSoundCannon>().soundcannon_spent(specialdata2.Value);
                }
#if FULLGAME
            } else if (b.TryGetComponent(out ColdPuffScript coldPuff)) {
                if (specialdata1.Value != 0) {
                    TrapClaim.ApplyColdPuffBlown(coldPuff);
                }
#endif
            } else if (b.TryGetComponent(out PistonScript _)) {
                // 客户端活塞跟随由活塞自身的 Update 读取 specialdata1 处理
            } else if (b.TryGetComponent(out CrystalEnemy manifestation)) {
                Traverse manifest = Traverse.Create(manifestation);
                manifest.Field("stuck").SetValue(specialdata1.Value != 0);
                manifest.Field("timeBeforeAttack").SetValue(specialdata2.Value);
#if FULLGAME
            } else if (b.TryGetComponent(out LaserDroneScript cryoDrone)) {
                TrapClaim.ClientCryoFollowHost(cryoDrone);
            } else if (b.TryGetComponent(out FanTrap fanTrap)) {
                FanTrapSync.Apply(fanTrap, specialdata1.Value, specialdata2.Value);
#endif
            } else if (go.transform.childCount == 1 && go.transform.GetChild(0) is Transform child && child != null && child.TryGetComponent(out GeyserScript geyser)) {
                geyser.liquidType = specialdata1.Value;
            }
        }

        private void DoBattery(BatteryItem battery) {
            if (current_battery == 0) {
                battery.UnloadBattery(true);
            } else {
                string batid = ((CurrentBattery)current_battery.Value).ToString();
                BatteryInfo batteryInfo = Item.GlobalItems[batid] as BatteryInfo;
                battery.batteryType = batid;
                battery.maxCharge = batteryInfo.maxCharge;
            }
        }

        private bool CheckIfContainerIsWrong(ItemSync.ItemsContainerInfo ici) {
            if (ici.containerExtraData1 != container_data1.Value || ici.knetid != container_netId.Value) {
                ItemSync.SafeUnloadItem(ici);
                return true;
            }
            return false;
        }
    }
}
