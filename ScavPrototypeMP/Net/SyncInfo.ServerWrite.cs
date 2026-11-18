using System;
using System.Collections.Generic;
using HarmonyLib;
using KrokV5Optimization;
using LiteEntitySystem.Extensions;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using static GunScript;

namespace Together {
    /// <summary>
    /// SyncInfo 的服务端组件缓存、脏位检测与快照写入分部类。
    /// 注意：严禁在本分部文件中声明任何 SyncVar / SyncString / SyncList，以保护 SyncInfo.cs 中的反射字段顺序。
    /// </summary>
    public partial class SyncInfo {
        private int _partGo;
        private int _partCount = -1;
        private bool _itemLive;
        private bool _rbLive;
        private bool _buildLive;
        private bool _gunLive;
        private bool _ammoLive;
        private bool _waterLive;
        private bool _customLive;
        private bool _plushLive;
        private bool _freshLive;
        private bool _boughtLive;
        private bool _lockLive;
        private Item _item;
        private Rigidbody2D _rb;
        private BuildingEntity _build;
        private GunScript _gun;
        private AmmoScript _ammo;
        private WaterContainerItem _water;
        private CustomItemBehaviour _custom;
        private PlushScript _plush;
        private FreshItemDrop _fresh;
        private BoughtItem _bought;
        private ItemLock _lock;
        private Transform _linkParent;
        private int _linkCount = -2;
        private Container _linkBox;
        private InventorySlot _linkSlot;
        private Limb _linkLimb;

        private void ClearParts() {
            _partGo = 0;
            _partCount = -1;
            _itemLive = false;
            _rbLive = false;
            _buildLive = false;
            _gunLive = false;
            _ammoLive = false;
            _waterLive = false;
            _customLive = false;
            _plushLive = false;
            _freshLive = false;
            _boughtLive = false;
            _lockLive = false;
            _item = null;
            _rb = null;
            _build = null;
            _gun = null;
            _ammo = null;
            _water = null;
            _custom = null;
            _plush = null;
            _fresh = null;
            _bought = null;
            _lock = null;
        }

        private void EnsureParts() {
            if (go == null) {
                ClearParts();
                return;
            }
            int id = go.GetInstanceID();
            int count = go.GetComponentCount();
            bool destroyed = (_itemLive && _item == null)
                || (_rbLive && _rb == null)
                || (_buildLive && _build == null)
                || (_gunLive && _gun == null)
                || (_ammoLive && _ammo == null)
                || (_waterLive && _water == null)
                || (_customLive && _custom == null)
                || (_plushLive && _plush == null)
                || (_freshLive && _fresh == null)
                || (_boughtLive && _bought == null)
                || (_lockLive && _lock == null);
            if (id == _partGo && count == _partCount && !destroyed) {
                return;
            }
            _partGo = id;
            _partCount = count;
            _itemLive = go.TryGetComponent(out _item);
            _rbLive = go.TryGetComponent(out _rb);
            _buildLive = go.TryGetComponent(out _build);
            _gunLive = go.TryGetComponent(out _gun);
            _ammoLive = go.TryGetComponent(out _ammo);
            _waterLive = go.TryGetComponent(out _water);
            _customLive = go.TryGetComponent(out _custom);
            _plushLive = go.TryGetComponent(out _plush);
            _freshLive = go.TryGetComponent(out _fresh);
            _boughtLive = go.TryGetComponent(out _bought);
            if (_rb != null) {
                _lockLive = _rb.TryGetComponent(out _lock);
            } else {
                _lockLive = false;
                _lock = null;
            }
        }

        internal bool TryCachedRigidbody(out Rigidbody2D rb) {
            if (go == null) {
                rb = null;
                return false;
            }
            EnsureParts();
            rb = _rb;
            return _rbLive && rb != null;
        }

        private void LinkParent(Transform parent) {
            int count = parent == null ? -1 : parent.gameObject.GetComponentCount();
            if (parent == _linkParent && count == _linkCount) {
                return;
            }
            _linkParent = parent;
            _linkCount = count;
            _linkBox = null;
            _linkSlot = null;
            _linkLimb = null;
            if (parent == null) {
                return;
            }
            parent.TryGetComponent(out _linkBox);
            parent.TryGetComponent(out _linkSlot);
            parent.TryGetComponent(out _linkLimb);
        }

        internal void WriteContainerInfo() {
            if (CraftCommit.Silenced(this)) {
                return;
            }
            if (ItemSync.SuppressContainerWrite > 0) {
                if (ItemSync.ParentTestSwap) {
                    ItemSync.ParentTest("swap block write " + ItemSync.ParentTestItem(item));
                }
                return;
            }

            bool write = FixContainerInfo.Begin(this, out FixContainerInfo.Shot shot);
            if (write) {
                ItemSync.ItemsContainerInfo ici = ItemSync.ItemGetContainerInfo(item);
                container_netId.Value = ici.knetid;
                container_ver.Value = ici.knetid.id == 0 ? (byte)0 : ici.containerVer;
                container_data1.Value = ici.containerExtraData1;
            }
            if (Net.IsServer) {
                HeldSeat.KeepSlot(this);
            }
            FixContainerInfo.End(this, shot);
        }

        internal bool InactiveChanged() {
            if (go == null) {
                return false;
            }
            EnsureParts();
            if (!_itemLive || _item == null) {
                return true;
            }
            Item item = _item;
            if (item.condition < 0f) {
                item.condition = 0f;
            }
            if (condition.Value != item.condition) {
                return true;
            }
            if (favourited.Value != item.favourited) {
                return true;
            }
#if FULLGAME
            string label = item.label ?? "";
            if (itemLabel.Value != label) {
                return true;
            }
            byte swatch = (byte)Mathf.Clamp(item.colorSwatch, 0, 255);
            if (colorSwatch.Value != swatch) {
                return true;
            }
#endif
            byte wet = (byte)(Mathf.Clamp(Time.time - item.wetTime, 0f, 125f) / 125f * 255f);
            if (compressedwetTime.Value != wet) {
                return true;
            }
            if (ContainerDirty(item)) {
                return true;
            }
            bool fresh = _freshLive && _fresh != null && _fresh.timeLeft > 5f;
            if (freshitemdrop.Value != fresh) {
                return true;
            }
            if (_boughtLive && _bought != null) {
                byte bought = (byte)(Mathf.Clamp(_bought.time, 0f, 300f) * 0.85f);
                if (compressedboughtitemtime.Value != bought) {
                    return true;
                }
            } else if (compressedboughtitemtime.Value != 0) {
                return true;
            }
            if (_customLive && _custom != null && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
                if (specialdata1.Value != (byte)_custom.state) {
                    return true;
                }
                if (_custom.data != null && _custom.data.Length > 0 && _custom.data[0] is float data0 && specialdata2.Value != data0) {
                    return true;
                }
            } else if (_plushLive && _plush != null && _plush.index >= 0) {
                if (specialdata1.Value != (byte)_plush.index) {
                    return true;
                }
            }
            if (item.battery != null && item.battery.hasBattery) {
                if (!Enum.TryParse(item.battery.batteryType, out CurrentBattery parsed)) {
                    return true;
                }
                if (current_battery.Value != (byte)parsed) {
                    return true;
                }
            } else if (current_battery.Value != 0) {
                return true;
            }
            if (_gunLive && _gun != null) {
                if (gun_racked.Value != _gun.racked || gun_safe.Value != _gun.safe || gun_hasMag.Value != _gun.hasMag) {
                    return true;
                }
                bool chamber = _gun.roundInChamber != RoundInChamber.None;
                bool casing = _gun.roundInChamber == RoundInChamber.Casing;
                if (gun_roundInChamber.Value != chamber || gun_roundInChamber_is_casing.Value != casing) {
                    return true;
                }
                if (rounds.Value != (byte)_gun.roundsInMag) {
                    return true;
                }
            } else if (_ammoLive && _ammo != null) {
                if (rounds.Value != (byte)_ammo.rounds) {
                    return true;
                }
            }
            if (LiquidDirty()) {
                return true;
            }
            bool hasRb = _rbLive && _rb != null;
            bool onBody = go.transform.parent != null && go.transform.parent.GetComponentInParent<Body>() != null;
            return PoseDirty(onBody, hasRb, _rb);
        }

        private bool ContainerDirty(Item item) {
            if (CraftCommit.Silenced(this) || ItemSync.SuppressContainerWrite > 0) {
                return false;
            }
            if (FixContainerInfo.ParentLinkMissing(item)) {
                return false;
            }
            Transform parent = item.transform.parent;
            LinkParent(parent);
            if (parent == null) {
                return container_data1.Value != ContainerEncoding.WorldByte || container_netId.Value.id != 0 || container_ver.Value != 0;
            }
            if (item.Stats != null && item.Stats.wearable && _linkLimb != null && _linkLimb.body != null && _linkLimb.body.TryGetNetBody(out NetBody worn) && worn.syncBody != null) {
                byte ver = ContainerLink.VersionOf(worn);
                return container_data1.Value != ContainerEncoding.WearableByte || container_netId.Value.id != worn.netId.id || container_ver.Value != ver;
            }
            if (_linkSlot != null && _linkSlot.body != null && _linkSlot.body.TryGetNetBody(out NetBody owner) && owner.syncBody != null) {
                byte data = ContainerEncoding.EncodeBodySlot(_linkSlot.slot);
                byte ver = ContainerLink.VersionOf(owner);
                return container_data1.Value != data || container_netId.Value.id != owner.netId.id || container_ver.Value != ver;
            }
            if (_linkBox != null) {
                if (!NetObjectRegistry.TryGetSyncInfo(parent.gameObject, out SyncInfo bag) || bag is null) {
                    return true;
                }
                ushort id = bag.syncId.id;
                byte ver = id == 0 ? (byte)0 : bag.Version;
                return container_data1.Value != ContainerEncoding.ContainerByte || container_netId.Value.id != id || container_ver.Value != ver;
            }
            return container_data1.Value != ContainerEncoding.WorldByte || container_netId.Value.id != 0 || container_ver.Value != 0;
        }

        private bool LiquidDirty() {
            if (!_waterLive || _water == null) {
                return false;
            }
            if (_water.stack == null) {
                return true;
            }
            IList<LiquidStack> stack = _water.stack;
            Dictionary<string, byte> reg = Item_SetupItems_Listener.LiquidIdRegistry;
            if (reg == null) {
                return true;
            }
            for (int i = 0; i < _liquidStack.Count; i++) {
                byte id = _liquidStack[i].LiquidId;
                if (!LastMl(stack, reg, id, out float ml) || ml != _liquidStack[i].LiquidMl) {
                    return true;
                }
            }
            for (int i = 0; i < stack.Count; i++) {
                string key = stack[i].liquidId;
                if (key == null || !reg.TryGetValue(key, out byte id) || !LiquidListed(id)) {
                    return true;
                }
            }
            return false;
        }

        private static bool LastMl(IList<LiquidStack> stack, Dictionary<string, byte> reg, byte id, out float ml) {
            ml = 0f;
            bool found = false;
            for (int i = 0; i < stack.Count; i++) {
                string key = stack[i].liquidId;
                if (key == null || !reg.TryGetValue(key, out byte cur) || cur != id) {
                    continue;
                }
                ml = Mathf.Max(0f, stack[i].amount);
                found = true;
            }
            return found;
        }

        private bool PoseDirty(bool onBody, bool hasRb, Rigidbody2D rb) {
            if (!onBody && (!hasRb || rb == null || rb.transform.parent == null || rb.simulated)) {
                Vector2_4byte_512 pos = go.transform.position;
                if (pos != _position.Value) {
                    return true;
                }
                FloatAngle rot = go.transform.rotation.eulerAngles.z;
                rot.Normalize();
                if (!rot.Equals(_rotation.Value)) {
                    return true;
                }
                Vector2 scale = go.transform.localScale;
                Vector2 sent = _scale.Value;
                if (scale.x != sent.x || scale.y != sent.y) {
                    return true;
                }
            }
            if (hasRb && rb != null && !onBody) {
                Vector2_4byte_200 vel = rb.velocity;
                if (vel != _velocity.Value) {
                    return true;
                }
                short ang = (short)Mathf.Clamp(rb.angularVelocity * angvel_to_short, short.MinValue, short.MaxValue);
                if (angvel_quantized.Value != ang) {
                    return true;
                }
                bool dyn = rb.bodyType == RigidbodyType2D.Dynamic;
                if (rb_dynamic.Value != dyn) {
                    return true;
                }
                bool locked = _lockLive && _lock != null;
                if (itemLock.Value != locked) {
                    return true;
                }
            }
            return false;
        }

        internal void WriteObjectIntoPacket() {
            EnsureParts();
            FixSyringeHold.Begin(this, out FixSyringeHold.Snap syringe);

            bool has_rb = _rbLive && _rb != null;
            Rigidbody2D rb = _rb;
            bool onBody = go.transform.parent != null && go.transform.parent.GetComponentInParent<Body>() != null;

            if (!onBody && (!has_rb || rb.transform.parent == null || rb.simulated)) {
                _position.Value = go.transform.position;
                FloatAngle newRot = go.transform.rotation.eulerAngles.z;
                newRot.Normalize();
                _rotation.Value = newRot;
                _scale.Value = go.transform.localScale;
            }

            if (has_rb && !onBody) {
                _velocity.Value = rb.velocity;
                angvel = rb.angularVelocity;
                rb_dynamic.Value = rb.bodyType == RigidbodyType2D.Dynamic;
                itemLock.Value = _lockLive && _lock != null;
            }

            if (_itemLive && _item != null) {
                Item item = _item;
                if (item.condition < 0f) {
                    item.condition = 0f;
                }

                condition.Value = item.condition;
                favourited.Value = item.favourited;
#if FULLGAME
                itemLabel.Value = item.label ?? "";
                colorSwatch.Value = (byte)Mathf.Clamp(item.colorSwatch, 0, 255);
#endif
                wetTime = Time.time - item.wetTime;

                WriteContainerInfo();

                if (_freshLive && _fresh != null) {
                    if (_fresh.timeLeft > 5.0f) {
                        freshitemdrop.Value = true;
                    }
                } else {
                    freshitemdrop.Value = false;
                }

                if (_boughtLive && _bought != null) {
                    boughtitemtime = _bought.time;
                } else {
                    compressedboughtitemtime.Value = 0;
                }

                if (_customLive && _custom != null && ItemSync.CustomItemBehaviourStateCanBeJustByte(item)) {
                    specialdata1.Value = (byte)_custom.state;
                    if (_custom.data != null && _custom.data.Length > 0 && _custom.data[0] is float f) {
                        specialdata2.Value = f;
                    }
                } else if (_plushLive && _plush != null && _plush.index >= 0) {
                    specialdata1.Value = (byte)_plush.index;
                }

                if (item.battery != null && item.battery.hasBattery) {
                    if (!Enum.TryParse(item.battery.batteryType, out CurrentBattery parsedBattery)) {
                        ModLog.Error(LogDomain.Item, $"Unknown battery type: {item.battery.batteryType}");
                    }
                    current_battery.Value = (byte)parsedBattery;
                } else {
                    current_battery.Value = 0;
                }

                if (_gunLive && _gun != null) {
                    gun_racked.Value = _gun.racked;
                    gun_safe.Value = _gun.safe;
                    gun_hasMag.Value = _gun.hasMag;
                    gun_roundInChamber.Value = _gun.roundInChamber != RoundInChamber.None;
                    gun_roundInChamber_is_casing.Value = _gun.roundInChamber == RoundInChamber.Casing;
                    rounds.Value = (byte)_gun.roundsInMag;
                } else if (_ammoLive && _ammo != null) {
                    rounds.Value = (byte)_ammo.rounds;
                }

                if (_waterLive && _water != null) {
                    SyncLiquidStack(_water);
                }
            } else if (_buildLive && _build != null) {
                BuildingEntity b = _build;
                condition.Value = b.GetHealth();

                if (b.TryGetComponent(out TrackerBuildingEntityBackgroundified tr)) {
                    building_backgroundified.Value = tr.is_backgroundified;
                }

                if (b.TryGetComponent(out CorpseScript corpse)) {
                    specialdata1.Value = corpse.GetOrAddComponent<CorpseScriptMultiplayerRewrite>().corpseSpriteIndex;
                } else if (b.TryGetComponent(out SpikeStabberScript spike)) {
                    specialdata1.Value = spike.activated.ToByte();
                } else if (b.TryGetComponent(out BearTrap bear)) {
                    specialdata1.Value = bear.activated.ToByte();
                } else if (b.TryGetComponent(out JumpPadScript pad)) {
                    specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(pad.cooldown), 0, 255);
                } else if (b.TryGetComponent(out CoilScript coil)) {
                    specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(coil.cooldown), 0, 255);
                } else if (b.TryGetComponent(out GunmineScript gun)) {
                    specialdata1.Value = (byte)Mathf.Clamp(Mathf.CeilToInt(gun.cooldown), 0, 255);
                } else if (b.TryGetComponent(out DrillPod_Update_MultiplayerPatch.DrillPodMultiplayerRewrite drel)) {
                    specialdata1.Value = drel.working.ToByte();
                } else if (go.TryGetComponent(out SpiderHandler spider)) {
                    spider_target.Value = spider.target;
                    spider_biteCooldown.Value = spider.biteCooldown;
                    spider_stunTime.Value = spider.stunTime;
                } else if (go.TryGetComponent(out GrabberPlant grabber)) {
                    if (GrabberPlant_Update_MultiplayerPatch.Live(go.transform.position)) {
                        grabberplant_tipPos.Value = grabber.tipPos;
                        GrabberPlant_Update_MultiplayerPatch.WriteScale(grabber, this);
                    }
                } else if (go.TryGetComponent(out TraderScript trader)) {
                    trader_desiredpos.Value = trader.desiredPos;
                } else if (b.TryGetComponent(out TrackerSoundCannon cannon)) {
                    specialdata1.Value = cannon.spent.ToByte();
                    if (cannon.spent) {
                        specialdata2.Value = cannon.endAngle;
                    }
                } else if (b.TryGetComponent(out ColdPuffScript coldPuff)) {
                    specialdata1.Value = Traverse.Create(coldPuff).Field("blown").GetValue<bool>().ToByte();
                } else if (b.TryGetComponent(out PistonScript piston)) {
                    specialdata1.Value = Traverse.Create(piston).Field("up").GetValue<bool>().ToByte();
                } else if (b.TryGetComponent(out CrystalEnemy manifestation)) {
                    specialdata1.Value = Traverse.Create(manifestation).Field("stuck").GetValue<bool>().ToByte();
#if FULLGAME
                } else if (b.TryGetComponent(out LaserDroneScript cryoDrone)) {
                    specialdata1.Value = cryoDrone.open.ToByte();
                } else if (b.TryGetComponent(out FanTrap fanTrap)) {
                    FanTrapSync.Write(fanTrap, this);
#endif
                } else if (go.transform.childCount == 1 && go.transform.GetChild(0) is Transform child && child != null && child.TryGetComponent(out GeyserScript geyser)) {
                    specialdata1.Value = geyser.liquidType;
                }
            }

            FixSyringeHold.End(this, syringe);
        }

        private void SyncLiquidStack(WaterContainerItem wci) {
            List<LiquidStack> stack = wci.stack;
            Dictionary<string, byte> reg = Item_SetupItems_Listener.LiquidIdRegistry;
            for (int i = _liquidStack.Count - 1; i >= 0; i--) {
                byte id = _liquidStack[i].LiquidId;
                if (!LastLiquidMl(stack, reg, id, out float ml) || ml != _liquidStack[i].LiquidMl) {
                    _liquidStack.RemoveAt(i);
                }
            }
            for (int i = stack.Count - 1; i >= 0; i--) {
                byte id = reg[stack[i].liquidId];
                if (LiquidListed(id)) {
                    continue;
                }

                float amount = Mathf.Max(0f, stack[i].amount);
                _liquidStack.Add(new LiquidStackPart {
                    LiquidId = id,
                    LiquidMl = amount
                });
            }
        }

        private static bool LastLiquidMl(IList<LiquidStack> stack, Dictionary<string, byte> reg, byte id, out float ml) {
            ml = 0f;
            bool found = false;
            for (int i = 0; i < stack.Count; i++) {
                if (reg[stack[i].liquidId] != id) {
                    continue;
                }
                ml = Mathf.Max(0f, stack[i].amount);
                found = true;
            }
            return found;
        }

        private bool LiquidListed(byte id) {
            for (int i = 0; i < _liquidStack.Count; i++) {
                if (_liquidStack[i].LiquidId == id) {
                    return true;
                }
            }
            return false;
        }
    }
}
