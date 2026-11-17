using System.Collections.Generic;
using HarmonyLib;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 缓存 <see cref="PlayerCamera.ItemHoverDescription"/> 悬停描述文本，仅当耐久、液体、电量、标签等可视属性指纹变更时重新拼接字符串。
/// </summary>
[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.ItemHoverDescription))]
internal static class FixItemTooltip {
    internal static long Hits;

    sealed class Entry {
        public Item Item;
        public int Stamp;
        public string Name;
        public string Desc;
    }

    static readonly Dictionary<int, Entry> Cache = new Dictionary<int, Entry>();

    internal static string Evidence() => "tipCache=" + Hits;

    static bool Prefix(Item item, ref (string, string) __result, ref int __state) {
        if (Together.log.verbose)
            return true;
        __state = 0;
        if (item == null)
            return true;
        int stamp = Stamp(item);
        __state = stamp;
        int id = item.GetInstanceID();
        if (Cache.TryGetValue(id, out Entry entry) && entry.Item == item && entry.Stamp == stamp) {
            __result = (entry.Name, entry.Desc);
            Hits++;
            return false;
        }
        return true;
    }

    // ValueTuple __result 使用 ref 传递，消除 BepInEx.Analyzers Harmony003 告警
    static void Postfix(Item item, ref (string, string) __result, int __state) {
        if (Together.log.verbose || item == null)
            return;
        int id = item.GetInstanceID();
        if (!Cache.TryGetValue(id, out Entry entry)) {
            entry = new Entry();
            Cache[id] = entry;
        }
        entry.Item = item;
        entry.Stamp = __state;
        entry.Name = __result.Item1;
        entry.Desc = __result.Item2;
        if (Cache.Count <= 2000)
            return;
        var drop = new List<int>();
        foreach (var pair in Cache) {
            if (pair.Value.Item == null)
                drop.Add(pair.Key);
        }
        for (int i = 0; i < drop.Count; i++)
            Cache.Remove(drop[i]);
        if (Cache.Count > 2000)
            Cache.Clear();
    }

    static int Stamp(Item item) {
        unchecked {
            int hash = 17;
            hash = hash * 31 + (item.favourited ? 1 : 0);
            hash = hash * 31 + (item.isWet ? 1 : 0);
            hash = hash * 31 + Mathf.FloorToInt(item.condition * 100f);
            hash = hash * 31 + Mathf.RoundToInt(item.totalWeight * 100f);
            if (item.Stats != null && item.Stats.fullName != null)
                hash = hash * 31 + item.Stats.fullName.GetHashCode();
            hash = hash * 31 + (item.label == null ? 0 : item.label.GetHashCode());
            bool expand = PlayerCamera.alwaysExpandDescriptions || Input.GetKey(KeyBinds.GetBind("expanddesc"));
            hash = hash * 31 + (expand ? 1 : 0);
            if (item.Stats != null && item.Stats.rec.recognizable == false)
                hash = hash * 31 + 2;
            if (item.battery != null) {
                hash = hash * 31 + (item.battery.hasBattery ? 1 : 0);
                hash = hash * 31 + (int)item.battery.preset;
                hash = hash * 31 + (item.battery.batteryType == null ? 0 : item.battery.batteryType.GetHashCode());
            }
            if (item.TryGetComponent(out WaterContainerItem water)) {
                hash = hash * 31 + Mathf.RoundToInt(water.CurrentTotal);
                hash = hash * 31 + Mathf.RoundToInt(water.Capacity);
                var stack = water.stack;
                for (int i = 0; i < stack.Count; i++) {
                    hash = hash * 31 + Mathf.RoundToInt(stack[i].amount);
                    hash = hash * 31 + (stack[i].liquidId == null ? 0 : stack[i].liquidId.GetHashCode());
                }
            }
            if (item.TryGetComponent(out Container box)) {
                hash = hash * 31 + Mathf.RoundToInt(box.GetHoldingWeight() * 100f);
                hash = hash * 31 + Mathf.RoundToInt(box.maxWeight * 100f);
            }
            if (item.TryGetComponent(out AmmoScript ammo))
                hash = hash * 31 + ammo.rounds;
            if (item.TryGetComponent(out BoughtItem bought))
                hash = hash * 31 + Mathf.RoundToInt(bought.time);
            if (expand && item.Stats != null && item.Stats.rotSpeed > 0f)
                hash = hash * 31 + (int)item.TimeUntilDecayed().TotalSeconds;
            if (item.Stats != null)
                hash = hash * 31 + item.Stats.GetValue(item);
            return hash;
        }
    }
}
