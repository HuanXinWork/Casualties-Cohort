using System;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 3 字节紧凑肢体网络标识（严禁修改字段顺序或新增实例字段，依赖 <see cref="INetSerializeByMemcpy"/> 原始内存拷贝）。
/// </summary>
public struct LimbNetId : INetSerializeByMemcpy {
    public knetid bodyNetId;
    public byte limbId;

    public LimbNetId() {
    }
    public LimbNetId(Limb limb) {
        bodyNetId = limb.body.GetComponent<NetBody>().netId;
        limbId = Util.GetLimbIndex(limb);
    }


    public Limb GetLimb() {
        return NetBody.GetNetBodyFromId(bodyNetId).body.limbs[limbId];
    }
    public bool TryGetNetBodyAndLimb(out NetBody nb, out Limb limb) {
        if (NetBody.TryGetNetBodyFromId(bodyNetId, out nb)) {
            limb = nb.body.limbs[limbId];
            return true;
        }
        nb = null;
        limb = null;
        return false;
    }
    public bool TryGetNetBodyAndLimbSafe(out NetBody nb, out Limb limb) {
        if (NetBody.TryGetNetBodyFromId(bodyNetId, out nb) && AnatomyConstants.IsValidLimbIndex(nb.body, limbId)) {
            limb = nb.body.limbs[limbId];
            return true;
        }
        nb = null;
        limb = null;
        return false;
    }

    public override string ToString() {
        if (TryGetNetBodyAndLimbSafe(out var nb, out var l)) {
            return $"NetLimb( limbId: {limbId}, name: {l.fullName}, {nb})";
        }
        if (Util.TryGetLocalBody(out var locbody) && AnatomyConstants.IsValidLimbIndex(locbody, limbId)) {
            return $"NetLimb( NOT FOUND: bodyNetId: {bodyNetId}, id: {limbId}, name: {locbody.limbs[limbId].fullName})";
        }
        return $"NetLimb( NOT FOUND: bodyNetId: {bodyNetId}, id: {limbId})";
    }
}

/// <summary>
/// 多态网络对象引用标识（严禁修改字段顺序或新增实例字段，依赖 <see cref="INetSerializeByMemcpy"/> 原始内存拷贝）。
/// </summary>
public struct AnyObjectNetId : INetSerializeByMemcpy, IEquatable<AnyObjectNetId> {
    public byte type; // 0 = unknown, 1 = netbody, 2 = plr, 3 = syncinfo, 4 = limb, 5 = block (hitPos carries world point)
    public knetid netId;
    public byte data1;

    public bool IsNothing() {
        return type == 0;
    }

    public bool IsBlockHit() {
        return type == 5;
    }

    public static AnyObjectNetId ForBlockHit() {
        return new AnyObjectNetId { type = 5 };
    }

    public override string ToString() {
        string typename = type switch {
            1 => "NetBody",
            2 => "Player",
            3 => "SyncInfo",
            4 => "Limb",
            5 => "Block",
            _ => "NONE",
        };
        string objinfo = $"type:{type}, netId:{netId}, data1:{data1}";

        if (TryGetLimb(out var foundlimb)) {
            objinfo = $"{foundlimb.shortName}, {foundlimb.GetNetBody()}";
        } else if (TryGetNetBody(out var nb)) {
            objinfo = nb.ToString();
        } else if (TryGetPlayer(out var foundplr)) {
            objinfo = foundplr.ToString();
        } else if (TryGetSyncInfo(out var si)) {
            objinfo = si.ToString();
        } else {
            objinfo = "NOT FOUND: " + objinfo;
        }

        if (type == 0 && netId == 0 && data1 == 0) {
            return "AnyObjectNetId( NULL )";
        }

        return $"AnyObjectNetId( {typename} / {objinfo} )";
    }


    public static bool TryGetNetIdFromGameObject(GameObject obj, out AnyObjectNetId ni) {
        ni = GetNetIdFromGameObject(obj);
        if (ni.type != 0) {
            return true;
        }
        return false;
    }

    public static AnyObjectNetId GetNetId(Item item) {
        if (item != null && item.TryGetTracker(out var tracker)) {
            return new AnyObjectNetId(tracker);
        }
        return new AnyObjectNetId();
    }
    public static AnyObjectNetId GetNetId(BuildingEntity building) {
        if (building != null && building.TryGetTracker(out var tracker)) {
            return new AnyObjectNetId(tracker);
        }
        return new AnyObjectNetId();
    }
    public static AnyObjectNetId GetNetIdFromGameObject(GameObject obj) {
        if (obj.TryGetComponent(out Limb li) && li.TryGetNetBody(out NetBody nb)) {
            return new(li);
        }
        if (obj.TryGetComponent(out nb)) {
            return new(nb);
        }
        if (obj.TryGetComponent(out SyncInfoGameObjectTracker tracker)) {
            return new(tracker);
        }
        if (obj.TryGetComponent(out ScavPlayer plr)) {
            return new(plr);
        }
        return new();
    }
    public AnyObjectNetId() {
        type = 0;
        netId = 0;
    }
    public AnyObjectNetId(NetBody nb) {
        type = 1;
        netId = nb.netId;
    }
    public AnyObjectNetId(ScavPlayer nb) {
        type = 2;
        netId = nb.playerId;
    }
    public AnyObjectNetId(SyncInfo si) {
        type = 3;
        netId = si.syncId;
    }
    public AnyObjectNetId(SyncInfoGameObjectTracker tracker) {
        type = 3;
        netId = tracker.syncId;
    }
    public AnyObjectNetId(Limb li) {
        type = 4;
        if (li.TryGetNetBody(out var nb)) {
            netId = nb.netId;
        }
        data1 = li.GetIndex();
    }


    public bool TryGetLimb(out Limb limb) {
        if (type == 4) {
            if (NetBody.TryGetNetBodyFromId(netId, out var nb) && AnatomyConstants.IsValidLimbIndex(nb.body, data1)) {
                limb = nb.body.limbs[data1];
                return true;
            }
        }
        limb = null;
        return false;
    }

    public bool TryGetNetBodyAndLimb(out NetBody nb, out Limb limb) {
        if (type == 4) {
            if (NetBody.TryGetNetBodyFromId(netId, out nb) && AnatomyConstants.IsValidLimbIndex(nb.body, data1)) {
                limb = nb.body.limbs[data1];
                return true;
            }
        }
        nb = null;
        limb = null;
        return false;
    }

    public bool TryGetNetBody(out NetBody nb) {
        if (type == 1) {
            if (NetBody.TryGetNetBodyFromId(netId, out nb)) {
                return true;
            }
        }
        if (type == 2) {
            if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(netId, out var plr, out nb)) {
                return true;
            }
        }
        if (type == 4) {
            if (NetBody.TryGetNetBodyFromId(netId, out nb)) {
                return true;
            }
        }
        nb = null;
        return false;
    }
    public bool TryGetPlayer(out ScavPlayer plr) {
        if (type == 2) {
            if (ScavPlayer.TryGetPlayerFromClientId(netId, out plr)) {
                return true;
            }
        }
        if (type == 1) {
            if (NetBody.TryGetNetBodyFromId(netId, out var nb) && nb.IsPlayer) {
                plr = nb.plr;
                return true;
            }
        }
        if (type == 4) {
            if (NetBody.TryGetNetBodyFromId(netId, out var nb) && nb.IsPlayer) {
                plr = nb.plr;
                return true;
            }
        }
        plr = null;
        return false;
    }
    public bool TryGetSyncInfo(out SyncInfo si) {
        if (type == 3) {
            if (NetObjectRegistry.TryGetSyncInfo(netId, out si)) {
                return true;
            }
        }
        si = null;
        return false;
    }
    public bool TryGetItem(out Item item, out SyncInfo si) {
        if (type == 3) {
            if (NetObjectRegistry.TryGetSyncInfo(netId, out si) && si.go != null && si.go.TryGetComponent(out item)) {
                return true;
            }
        }
        item = null;
        si = null;
        return false;
    }
    public bool TryGetBuilding(out BuildingEntity building, out SyncInfo si) {
        if (type == 3) {
            if (NetObjectRegistry.TryGetSyncInfo(netId, out si) && si.go != null && si.go.TryGetComponent(out building)) {
                return true;
            }
        }
        building = null;
        si = null;
        return false;
    }
    // it will not get player instance btw!!
    public bool TryGetGameObject(out GameObject go) {
        if (type != 0) {
            if (TryGetLimb(out var li)) {
                go = li.gameObject;
                return true;
            }
            if (TryGetNetBody(out var nb)) {
                go = nb.gameObject;
                return true;
            }
            if (TryGetSyncInfo(out var si)) {
                go = si.go;
                return true;
            }
        }
        go = null;
        return false;
    }

    public bool TryGetTalker(out Talker talker) {
        if (TryGetGameObject(out var go)) {
            return Util.TryGetTalkerOnObject(go, out talker);
        }
        talker = null;
        return false;
    }


    public static bool operator ==(AnyObjectNetId lhs, AnyObjectNetId rhs) {
        return
            lhs.netId == rhs.netId &&
            lhs.type == rhs.type &&
            lhs.data1 == rhs.data1;
    }
    public static bool operator !=(AnyObjectNetId lhs, AnyObjectNetId rhs) {
        return !(lhs == rhs);
    }
    public bool Equals(AnyObjectNetId other) => netId == other.netId && type == other.type && data1 == other.data1;
    public override bool Equals(object other) {
        if (other is Component ccccc) {
            if (this.TryGetGameObject(out var go)) {
                return go == ccccc.gameObject;
            }
            return false;
        }
        if (!(other is AnyObjectNetId)) {
            return false;
        }
        return this == (AnyObjectNetId)other;
    }
    public override int GetHashCode() {
        return HashCode.Combine(type, netId.id, data1);
    }
}
