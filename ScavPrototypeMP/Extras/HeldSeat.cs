using CasualtiesTogetherUtils;
using HarmonyLib;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 随身槽位与穿戴物品的碰撞体静默及强制入槽辅助器：
/// 清醒远端玩家身上的物品禁用碰撞体以免被本机世界交互误拾取，丢弃时当场恢复碰撞体与刚体模拟。
/// </summary>
[HarmonyPatch]
internal static class HeldSeat {
    internal static void Quiet(Item item) {
        if (item == null)
            return;
        bool block = Blocked(item);
        HeldQuiet quiet = item.GetComponent<HeldQuiet>();
        if (block) {
            if (quiet == null || quiet.Released) {
                if (quiet != null)
                    Object.Destroy(quiet);
                item.gameObject.AddComponent<HeldQuiet>();
            }
            if (item.rb != null)
                item.rb.simulated = false;
            return;
        }
        if (quiet != null)
            Object.Destroy(quiet);
    }

    internal static void Detach(Item item) {
        if (item == null || !Net.IsRunning)
            return;
        if (item.transform.parent != null) {
            item.transform.SetParent(null);
            item.transform.localScale = Vector3.one;
        }
        if (item.TryGetComponent(out Wearable wear))
            wear.ClearSprites();
        Release(item);
    }

    internal static void Release(Item item) {
        if (item == null || !Net.IsRunning || item.transform.parent != null)
            return;
        HeldQuiet quiet = item.GetComponent<HeldQuiet>();
        if (quiet != null) {
            quiet.Release();
            Object.Destroy(quiet);
        }
        if (item.TryGetComponent(out Wearable worn))
            worn.ClearSprites();
        Wake(item);
        if (item.rb != null) {
            item.rb.bodyType = RigidbodyType2D.Dynamic;
            item.rb.simulated = true;
        }
    }

    internal static bool Blocked(Item item) {
        if (item == null || !Net.IsRunning || item.transform.parent == null)
            return false;
        Body body = BodyOf(item);
        if (body != null)
            return !body.IsBodyLocal() && body.conscious;
        if (!item.TryGetComponent(out SyncInfoGameObjectTracker tracker) || tracker.syncInfo == null)
            return false;
        byte data = tracker.syncInfo.container_data1.Value;
        if (!ContainerEncoding.IsAttachedToBody(data))
            return false;
        if (!ContainerLink.TryBody(tracker.syncInfo, out NetBody owner) || owner.body == null)
            return true;
        return !owner.body.IsBodyLocal() && owner.body.conscious;
    }

    internal static void KeepSlot(SyncInfo info) {
        if (info == null || info.item == null)
            return;
        Transform parent = info.item.transform.parent;
        if (parent == null)
            return;
        InventorySlot slot = parent.GetComponent<InventorySlot>();
        if (slot == null || slot.body == null)
            return;
        if (!slot.body.TryGetNetBody(out NetBody owner) || owner.syncBody == null)
            return;
        byte data = ContainerEncoding.EncodeBodySlot(slot.slot);
        knetid id = owner.netId;
        byte ver = ContainerLink.VersionOf(owner);
        if (info.container_data1.Value == data && info.container_netId.Value == id && info.container_ver.Value == ver)
            return;
        info.container_data1.Value = data;
        info.container_netId.Value = id;
        info.container_ver.Value = ver;
    }

    internal static void Seat(Body body, Item item, int slot) {
        if (body == null || item == null || body.slots == null || slot < 0 || slot >= body.slots.Length)
            return;
        InventorySlot hold = body.slots[slot];
        if (hold == null)
            return;
        if (item.rb != null)
            item.rb.simulated = false;
        SpriteRenderer sprite = item.GetComponent<SpriteRenderer>();
        if (sprite != null)
            sprite.sortingOrder = hold.spriteSortOrder;
        item.transform.SetParent(hold.transform);
        item.transform.SetAsFirstSibling();
        Show(item);
        item.transform.localPosition = Vector3.zero;
        float rotation = item.Stats != null ? item.Stats.slotRotation : 0f;
        item.transform.localEulerAngles = new Vector3(0f, 0f, rotation);
        float scaleX = 1f;
        if (hold.limb != null && Mathf.Abs(hold.limb.transform.localScale.x) > 0.0001f)
            scaleX = 1f / hold.limb.transform.localScale.x;
        item.transform.localScale = new Vector3(scaleX, 1f, 1f);
        if (item.Stats != null && item.Stats.HasTag("backflip") && slot > 2)
            item.transform.localEulerAngles += Vector3.back * 90f;
        Quiet(item);
    }

    internal static void SeatBehind(Body body, Item item, int slot) {
        if (body == null || item == null || body.slots == null || slot < 0 || slot >= body.slots.Length)
            return;
        InventorySlot hold = body.slots[slot];
        if (hold == null)
            return;
        if (item.rb != null)
            item.rb.simulated = false;
        item.transform.SetParent(hold.transform);
        item.transform.SetAsLastSibling();
        item.transform.localPosition = Vector3.zero;
        item.transform.localScale = Vector3.one;
        SpriteRenderer sprite = item.GetComponent<SpriteRenderer>();
        if (sprite != null)
            sprite.enabled = false;
        Quiet(item);
    }

    internal static void Show(Item item) {
        if (item == null)
            return;
        SpriteRenderer sprite = item.GetComponent<SpriteRenderer>();
        if (sprite != null)
            sprite.enabled = true;
    }

    [HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.TryPickupFromWorld))]
    [HarmonyPostfix]
    static void AfterWorldPickup(PlayerCamera __instance) {
        if (Blocked(__instance.dragItem))
            __instance.dragItem = null;
    }

    static void Wake(Item item) {
        Collider2D[] cols = item.GetComponentsInChildren<Collider2D>(true);
        for (int i = 0; i < cols.Length; i++) {
            Collider2D col = cols[i];
            if (col == null)
                continue;
            Item owner = col.GetComponentInParent<Item>();
            if (owner != null && owner != item)
                continue;
            col.enabled = true;
        }
    }

    static Body BodyOf(Item item) {
        Transform parent = item.transform.parent;
        if (parent == null)
            return null;
        InventorySlot slot = parent.GetComponent<InventorySlot>();
        if (slot != null)
            return slot.body;
        Limb limb = parent.GetComponent<Limb>();
        if (limb != null)
            return limb.body;
        return parent.GetComponentInParent<Body>();
    }
}

sealed class HeldQuiet : MonoBehaviour {
    Collider2D[] _cols;
    bool[] _on;

    internal bool Released { get; private set; }

    void Awake() {
        _cols = GetComponentsInChildren<Collider2D>(true);
        _on = new bool[_cols.Length];
        for (int i = 0; i < _cols.Length; i++) {
            if (_cols[i] == null)
                continue;
            _on[i] = _cols[i].enabled;
            _cols[i].enabled = false;
        }
    }

    internal void Release() {
        Restore();
        Released = true;
    }

    void OnDestroy() {
        Restore();
    }

    void Restore() {
        if (_cols == null)
            return;
        for (int i = 0; i < _cols.Length; i++) {
            if (_cols[i] != null)
                _cols[i].enabled = _on[i];
        }
        _cols = null;
    }
}
