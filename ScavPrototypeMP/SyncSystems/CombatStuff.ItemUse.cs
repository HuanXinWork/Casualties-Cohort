using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 手持消耗品与手电筒开关的客户端预测锁及服务端使用校验同步（独立于 <see cref="HarmonyPatch"/> 类以避免分析器误判）。
/// </summary>
internal static class CombatItemUseSync {
    private struct StateHold {
        public byte state;
        public float until;
    }

    private static readonly Dictionary<ushort, StateHold> StateHolds = new();

    internal static bool StaleState(ushort id, byte incoming) {
        if (!HeldState(id, out byte held)) {
            return false;
        }
        return incoming != held;
    }

    internal static void HoldState(ushort id, byte state) {
        if (id == 0) {
            return;
        }
        StateHolds[id] = new StateHold { state = state, until = Time.realtimeSinceStartup + 2f };
    }

    internal static bool HeldState(ushort id, out byte state) {
        state = 0;
        if (!StateHolds.TryGetValue(id, out StateHold hold)) {
            return false;
        }
        if (Time.realtimeSinceStartup >= hold.until) {
            StateHolds.Remove(id);
            return false;
        }
        state = hold.state;
        return true;
    }

    internal static void ClearHeldState(ushort id) {
        if (id != 0) {
            StateHolds.Remove(id);
        }
    }

    internal static void AnnouncePredictedUse(ushort itemId, knetid bodyNet) {
        if (Net.IsServer || itemId == 0) {
            return;
        }
        if (ItemSync.TryGetItemSyncInfo((knetid)itemId, out SyncInfo si) && si is not null) {
            si.ArmPredictedUse();
            if (si.item is not null && si.item.TryGetComponent(out CustomItemBehaviour cib) && ItemSync.CustomItemBehaviourStateCanBeJustByte(si.item)) {
                HoldState(itemId, (byte)cib.state);
            }
        }
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestUseItem, (knetid)itemId, bodyNet, true);
    }

    internal static void SendLightMode(SyncInfo si, NetBody nb) {
        if (si is null || si.item is null || nb == null || !si.item.TryGetComponent(out CustomItemBehaviour light)) {
            return;
        }
        knetid user = nb.plr != null ? nb.plr.playerId : default;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_RequestUseItemRelay);
        writer.Put(user);
        writer.Put(si.syncId);
        writer.Put(nb.netId);
        writer.Put((byte)light.state);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(user));
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestUseItem)]
    static void Server_RequestUseItem(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid item);
        reader.Get(out knetid targetPerson);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var giverPlr, out var giver)
            || giver?.body == null
            || !giver.body.conscious
            || !NetBody.TryGetNetBodyFromId(targetPerson, out var targetNb)
            || !NetObjectRegistry.TryGetSyncInfo(item, out var si)
            || si.go == null
            || !si.IsItem()) {
            return;
        }

        bool allowed = (giver == targetNb || !MedicalSync.IsRefusingHelp(targetNb.body))
            && KM.DistanceCheck(giver, targetNb.body, SharedMain.max_player_interaction_distance * 1.6f)
            && (si.item.Stats.usable || si.item.Stats.wearable)
            && ItemSync.CheckIfBodyReachThisItem(si, giver.body)
            && !CombatStuff.ItemIsUsedForAttacking(si.item)
            && (si.item.Stats.wearable || !ItemSync.TooSpentToUse(si.item));

        if (!allowed) {
            log.serverdeny($"RequestUseItem {giverPlr} -> {targetNb} - {si}", Vector2.Lerp(giverPlr.body.transform.position, targetNb.position, 0.5f));
            return;
        }

        void ExecuteUseItem() {
            if (!(giver == targetNb && giver.IsLocal)) {
                ItemSync.BetterUseItem(targetNb.body, si.item);
            }

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(Vector2.Lerp(giver.position, targetNb.position, 0.5f), $"S: RequestUseItem {si}");
            }

            byte lightState = 255;
            if (si.go is not null && si.item is not null && si.item.TryGetComponent(out CustomItemBehaviour light) && ItemSync.CustomItemBehaviourStateCanBeJustByte(si.item)) {
                lightState = (byte)light.state;
            }

            var writer = Net.CreateWriter(NetmsgId.CLIENT_RequestUseItemRelay);
            writer.Put(clientId);
            writer.Put(item);
            writer.Put(targetPerson);
            writer.Put(lightState);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
        }

        if (Multiplayer.rules.ConsentForFeeding && targetNb != giver && targetNb.body.conscious && targetNb.IsPlayer) {
            string actionKey = si.item.Stats.wearable
                ? "plrint_feed_receive_wearable"
                : (si.item.IsEdible()
                    ? "plrint_feed_receive_food"
                    : (si.item.Stats.category == "water" ? "plrint_feed_receive_water" : "plrint_useitem"));

            ServerMain.PromptPlayer(
                targetNb.plr,
                "FEED_OFFER",
                $"<body:{giver.netId}> <key:plrint_feed_prompt>",
                $"<key:{actionKey}> \"<item:{si.syncId}>\"",
                answer => {
                    if (answer == 0) {
                        if (si.go == null) {
                            giverPlr.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:giveitem_noitem>.");
                            return;
                        }
                        if (ItemSync.ConfirmItemOffer(giver, targetNb, si.item)) {
                            ExecuteUseItem();
                            giverPlr.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:accepted>.");
                        }
                    } else {
                        giverPlr.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:declined>.");
                    }
                });
        } else {
            ExecuteUseItem();
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_RequestUseItemRelay)]
    static void Client_RequestUseItemRelay(knetid _, NetDataReader reader) {
        reader.Get(out knetid _);
        reader.Get(out knetid item);
        reader.Get(out knetid target);

        byte lightState = 255;
        if (!reader.EndOfData) {
            reader.Get(out lightState);
        }

        if (!NetBody.TryGetNetBodyFromId(target, out var targetNb) || !NetObjectRegistry.TryGetSyncInfo(item, out var si) || si.go == null) {
            return;
        }

        if (DebugHelp._DEV_VISUALISE_NET_EVENTS && ScavPlayer.LocalPlayer?.body != null) {
            DebugHelp.OnNetEvent(Vector2.Lerp(ScavPlayer.LocalPlayer.body.transform.position, targetNb.position, 0.8f), $"C: RequestUseItemRelay {si}");
        }

        if (lightState != 255 && si.item is not null && si.item.TryGetComponent(out CustomItemBehaviour light)) {
            light.state = lightState;
            HoldState(si.syncId, lightState);
            Sound.Play("flashlighttoggle", si.item.transform.position);
        } else {
            ItemSync.BetterUseItem(targetNb.body, si.item);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.UseItemInHand))]
public static class Body_UseItemInHand_MultiplayerPatch {
    public static bool IsUsingItemInHand = false;

    private static bool _hostLight;
    private static ushort _hostLightId;

    private struct PendingUse {
        public bool on;
        public ushort item;
        public knetid body;
    }

    private static PendingUse _pending;

    internal static bool StaleState(ushort id, byte incoming) => CombatItemUseSync.StaleState(id, incoming);
    internal static void HoldState(ushort id, byte state) => CombatItemUseSync.HoldState(id, state);
    internal static bool HeldState(ushort id, out byte state) => CombatItemUseSync.HeldState(id, out state);
    internal static void ClearHeldState(ushort id) => CombatItemUseSync.ClearHeldState(id);

    public static bool Prefix(Body __instance) {
        _hostLight = false;
        if (!Util.IsWorldGenerated()) {
            return false;
        }
        if (!Net.IsRunning) {
            return true;
        }

        _pending.on = false;
        if (Util.IsBodyLocal(__instance)) {
            if (__instance.conscious
                && __instance.GetItem(__instance.handSlot) is var item
                && item != null
                && item.Stats != null
                && item.Stats.usable
                && item.Stats.usableWithLMB) {
                if (ItemSync.TooSpentToUse(item)) {
                    return false;
                }
                if (CombatStuff.ItemIsUsedForAttacking(item) && NetObjectRegistry.TryGetSyncInfoOrRegister(item.gameObject, out var si)) {
                    si.SetIgnoreTimeForRoundTrip();
                } else if (!Net.IsServer
                    && !CombatStuff.ItemIsUsedForAttacking(item)
                    && __instance.TryGetNetBody(out var nb)
                    && nb.IsLocal
                    && ItemSync.TryGetSyncInfo(item, out var useSi)) {
                    _pending.on = true;
                    _pending.item = useSi.syncId;
                    _pending.body = nb.netId;
                } else if (Net.IsServer && ItemSync.CustomItemBehaviourStateCanBeJustByte(item) && ItemSync.TryGetSyncInfo(item, out SyncInfo lightSi)) {
                    _hostLight = true;
                    _hostLightId = lightSi.syncId;
                }
            }
            IsUsingItemInHand = true;
        }
        return true;
    }

    public static void Postfix(Body __instance) {
        if (!Net.IsRunning) {
            return;
        }

        if (_pending.on) {
            CombatItemUseSync.AnnouncePredictedUse(_pending.item, _pending.body);
            _pending.on = false;
        }
        if (_hostLight) {
            _hostLight = false;
            if (ItemSync.TryGetItemSyncInfo((knetid)_hostLightId, out SyncInfo lightSi) && __instance.TryGetNetBody(out NetBody nb)) {
                CombatItemUseSync.SendLightMode(lightSi, nb);
            }
        }

        if (!Net.IsServer && Util.GetLocalBody() == __instance) {
            IsUsingItemInHand = false;
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.UseItem))]
public static class Body_UseItem_MultiplayerPatch {
    public static Item current_used_item = null;

    internal static void SendLightMode(SyncInfo si, NetBody nb) => CombatItemUseSync.SendLightMode(si, nb);

    public static bool Prefix(Body __instance, ref SyncInfo __state, Item item) {
        current_used_item = item;
        __state = null;

        if (!Util.IsWorldGenerated()) {
            return false;
        }
        if (!Net.IsRunning) {
            return true;
        }
        if (item != null && item.Stats != null && item.Stats.usable && ItemSync.TooSpentToUse(item)) {
            return false;
        }

        if (item && __instance.TryGetNetBody(out var pb) && pb.IsLocal) {
            if (ItemSync.TryGetSyncInfo(item, out var si)) {
                if (item.Stats.usable) {
                    if (CombatStuff.ItemIsUsedForAttacking(item)) {
                        if (si.IsGun()) {
                            var gst = item.GetOrAddComponent<TrackerGunScript>();
                            gst.body = __instance;
                            gst.pbody = pb;
                        }
                    } else {
                        __state = si;
                        pb.RollUseRandom();
                    }
                }
            } else {
                NetObjectRegistry.Client_DeleteUnregisteredSyncableObject(item.gameObject);
            }
        }
        return true;
    }

    public static void Postfix(Body __instance, ref SyncInfo __state) {
        current_used_item = null;
        if (__state != null && __instance.TryGetNetBody(out var nb)) {
            CombatItemUseSync.AnnouncePredictedUse(__state.syncId, nb.netId);
        }
    }
}
