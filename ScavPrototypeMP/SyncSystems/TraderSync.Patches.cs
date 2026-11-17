using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using static TraderScript;

namespace Together;

/// <summary>
/// 商人挥砍、交涉、买卖、初次会面及小憩等交互方法的 Harmony 联机补丁集。
/// </summary>
[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Swing))]
static class TraderScript_Swing_MultiplayerPatch {
    private static bool Prefix(TraderScript __instance, out bool __state) {
        __state = false;
        if (!Net.IsRunning) {
            __state = true;
            return true;
        }
        if (Net.IsClient) {
            return false;
        }
        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        tt.bgo.SetNearestBody();
        __state = true;
        return true;
    }

    static void Postfix(TraderScript __instance, bool __state) {
        if (!Net.IsRunning || !Net.IsServer || !__state)
            return;

        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        Body target = tt.focused_body;
        if (target != null && target.TryGetNetBody(out NetBody body)) {
            body.OnDamageEvent(new BodyDamageEvent(__instance.build, body, 10));
            MedicalSync.ForceHealth(body);
        }
        Vector2 aim = target != null ? (Vector2)target.transform.position : (Vector2)__instance.transform.position;
        TraderCommit.RelaySwing(__instance, aim);
        TraderCommit.Publish(__instance);
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.AskToMove))]
public static class TraderScript_AskToMove_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Move);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderAskToMove)]
    static void ReceiveTraderAskToMove(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid targetTrader);
        if (TraderSync.Server_TraderInteractionCheck(clientId, targetTrader, "AskToMove", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Move, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targetTrader, out si) && si.trader != null) {
            TraderCommit.PublishTo(si.trader, clientId);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.Threaten))]
public static class TraderScript_Threaten_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Threaten);
    }

    public static void Postfix(TraderScript __instance) {
        if (Net.IsRunning && !Net.IsClient && !TraderCommit.Running) {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();
            tt.Server_AnnounceTraderReputationState();
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderThreaten)]
    static void ReceiveTraderThreaten(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid targetTrader);
        if (TraderSync.Server_TraderInteractionCheck(clientId, targetTrader, "Threaten", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Threaten, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targetTrader, out si) && si.trader != null) {
            TraderCommit.PublishTo(si.trader, clientId);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryHug))]
public static class TraderScript_TryHug_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance, ref float __state) {
        __state = __instance.reputation;
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Hug);
    }

    public static void Postfix(TraderScript __instance, ref float __state) {
        if (!Net.IsRunning || Net.IsClient)
            return;

        var tt = __instance.GetOrAddComponent<TrackerTrader>();
        if (__instance.hostility != 100f && __state != __instance.reputation && __instance.reputation < __state) {
            if (!Util.IsBodyLocal(tt.focused_body) && tt.focused_body.TryGetNetPlayer(out var plr)) {
                plr.Server_RemindPlayersCurrentState(true);
            }
        }

        if (!TraderCommit.Running)
            tt.Server_AnnounceTraderReputationState();
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderTryHug)]
    static void ReceiveTraderTryHug(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid targetTrader);
        if (TraderSync.Server_TraderInteractionCheck(clientId, targetTrader, "Hug", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Hug, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targetTrader, out si) && si.trader != null) {
            TraderCommit.PublishTo(si.trader, clientId);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.PromptResponse))]
static class TraderScript_PromptResponse_MultiplayerPatch {
    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(Talker), nameof(Talker.PromptTraderResponse))]
static class Talker_PromptTraderResponse_MultiplayerPatch {
    static bool Prefix(Talker __instance, string textType) {
        if (!Net.IsRunning)
            return true;

        if (__instance.body != null && __instance.body.alive) {
            Collider2D[] physInRadius = Physics2D.OverlapCircleAll(
                __instance.body.transform.position,
                GameplayRanges.ServerExchangeToleranceDistance);
            foreach (Collider2D col in physInRadius) {
                if (!col.TryGetComponent<TraderScript>(out _))
                    continue;
                var track = col.gameObject.GetOrAddComponent<TrackerTrader>();
                track.focused_body = __instance.body;
                track.og.PromptResponse(textType);
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryHaggle))]
public static class TraderScript_TryHaggle_MultiplayerPatch {
    static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Haggle);
    }

    static void Postfix(TraderScript __instance) {
        if (Net.IsRunning && !Net.IsClient && !TraderCommit.Running) {
            var tt = __instance.GetOrAddComponent<TrackerTrader>();
            tt.Server_AnnounceTraderReputationState();
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderTryHaggle)]
    static void ReceiveTraderTryHaggle(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid targetTrader);
        if (TraderSync.Server_TraderInteractionCheck(clientId, targetTrader, "TryHaggle", out ScavPlayer plr, out SyncInfo si, out _)) {
            TraderCommit.Submit(TraderCommit.Op.Haggle, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targetTrader, out si) && si.trader != null) {
            TraderCommit.PublishTo(si.trader, clientId);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.GiveItem))]
public static class TraderScript_GiveItem_MultiplayerPatch {
    public static bool Prefix(TraderScript __instance, Item item) {
        if (!Net.IsRunning || TraderCommit.Running)
            return true;

        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        if (tt.si is null || tt.si.syncId == 0 || item == null || !ItemSync.TryGetSyncInfo(item, out SyncInfo itemsi))
            return false;

        if (Net.IsServer) {
            if (PlayerCamera.main == null || PlayerCamera.main.body == null)
                return false;
            TraderCommit.SubmitSell(PlayerCamera.main.body, tt.si, itemsi.syncId);
            return false;
        }

        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_TraderSellItem, tt.si.syncId, itemsi.syncId, true);
        return false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderSellItem)]
    private static void ReceiveTraderSell(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid targetTrader);
        reader.Get(out knetid soldItem);

        if (TraderSync.Server_TraderInteractionCheck(clientId, targetTrader, "SellItem", out ScavPlayer plr, out SyncInfo traderSi, out _)) {
            TraderCommit.SubmitSell(plr.body, traderSi, soldItem);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(targetTrader, out traderSi) && traderSi.trader != null) {
            TraderCommit.PublishTo(traderSi.trader, clientId);
        }
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.body), MethodType.Getter)]
static class TraderScript_get_body_MultiplayerPatch {
    static void Postfix(TraderScript __instance, ref Body __result) {
        if (!Net.IsRunning)
            return;

        Body actor = TraderCommit.Actor;
        if (actor != null) {
            __result = actor;
            return;
        }

        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        BodyGetterOverrider bgo = tt.bgo;
        if (Net.IsClient) {
            Body local = Util.GetLocalBody();
            if (local != null) {
                bgo.body = local;
                __result = local;
                return;
            }
        }
        bgo.SetNearestBody();
        if (bgo.body == null && PlayerCamera.main != null) {
            bgo.body = PlayerCamera.main.body;
        }
        __result = bgo.body;
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.TryPurchase))]
internal static class TraderScript_TryPurchase_MultiplayerPatch {
    public static Item TryPurchase_rewritten(TraderScript trader, TraderItem titem, Body buyer) {
        bool isLocal = Util.IsBodyLocal(buyer);
        Item didBuy = null;
        var tracker = trader.GetComponent<TrackerTrader>();
        var build = trader.GetComponent<BuildingEntity>();
        if (build.GetHealth() < 200f)
            return null;

        if (trader.valueGiven >= trader.ItemPrice(titem)) {
            trader.valueGiven -= trader.ItemPrice(titem);
            if (trader.ItemPrice(titem) > 0) {
                if (titem.preference == TraderItemPreference.WantsTrade) {
                    trader.reputation += 7f;
                } else if (titem.preference == TraderItemPreference.Indifferent) {
                    trader.reputation += 4f;
                }
            }
            if (tracker.og.freeAmount > 0) {
                tracker.og.freeAmount--;
            } else {
                trader.talker.Talk(Locale.GetCharacter("traderbuy", trader.character));
                buyer.happiness += 0.75f;
                buyer.skills.AddExp(2, 1f);
            }
            if (titem.info.HasTag("dressing")) {
                tracker.og.freeDressing = false;
            }
            trader.items.Remove(titem);
            GameObject gameObject = Utils.Create(titem.id, trader.transform.position, 0f);
            gameObject.AddComponent<BoughtItem>();
            if (gameObject.TryGetComponent<AmmoScript>(out var ammo) && ammo.itemType == AmmoScript.AmmoItemType.Magazine) {
                ammo.rounds = Random.Range((int)(ammo.maxRounds * 0.5f), ammo.maxRounds);
            }
            buyer.AutoPickUpItem(gameObject.GetComponent<Item>());
            if (isLocal) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Click);
            } else {
                TraderCommit.RelaySound(trader, buyer, "click", trader.transform.position);
            }
            didBuy = gameObject.GetComponent<Item>();
        } else {
            if (trader.totalValueGiven != MAX_VALUE_GIVEN) {
                trader.talker.Talk(Locale.GetCharacter("traderbuyfail", trader.character));
            } else {
                trader.talker.Talk(Locale.GetCharacter("traderbuyfailmaxvalue", trader.character));
            }
            if (isLocal) {
                PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
            } else {
                TraderCommit.RelaySound(trader, buyer, "warning", trader.transform.position);
            }
            trader.reputation -= 2f;
        }

        if (PlayerCamera.main != null && PlayerCamera.main.currentTrader == trader && Util.IsInTradingMenu()) {
            PlayerCamera.main.RefreshTraderInventories();
            PlayerCamera.main.UpdateTradeTexts();
        }
        return didBuy;
    }

    static bool Prefix(TraderScript __instance, TraderItem item) {
        if (!Net.IsRunning || TraderCommit.Running)
            return true;
        if (PlayerCamera.main == null || PlayerCamera.main.currentTrader != __instance || item == null)
            return true;

        TrackerTrader tt = __instance.GetOrAddComponent<TrackerTrader>();
        if (tt.si == null || tt.si.syncId == 0)
            return false;

        byte index = (byte)__instance.items.IndexOf(item);
        if (Net.IsServer) {
            TraderCommit.SubmitBuy(PlayerCamera.main.body, tt.si, index, item.id, (ushort)item.value);
            return false;
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_Trader_TryPurchase);
        writer.Put(tt.si.syncId);
        writer.Put((ushort)item.value);
        writer.Put(index);
        writer.Put(item.id, true);
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        return false;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Trader_TryPurchase)]
    static void ReceiveTraderTryPurchase(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderSyncId);
        reader.Get(out ushort allegedValue);
        reader.Get(out byte allegedIndex);
        reader.Get(out string itemId, true);

        if (ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out Body buyer) &&
            buyer != null &&
            buyer.conscious &&
            NetObjectRegistry.TryGetSyncInfoLive(traderSyncId, out SyncInfo isi) &&
            isi.IsTrader() &&
            KM.DistanceCheck(buyer, isi, GameplayRanges.ServerExchangeToleranceDistance)) {
            TraderCommit.SubmitBuy(buyer, isi, allegedIndex, itemId, allegedValue);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(traderSyncId, out isi) && isi.trader != null) {
            TraderCommit.PublishTo(isi.trader, clientId);
        }
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.MeetPlayer))]
public static class TraderScript_MeetPlayer_MultiplayerPatch {
    private const float FirstMeetScanRadius = 16f;

    public static (float, float, bool) MeetPlayer_CalculateFirstImpression(Body body) {
        float reputation = Random.Range(75f, 135f);
        float hostility = 0f;
        bool freeDressing = false;

        reputation += body.skills.INTFrom10 * 4f;
        if (WorldGeneration.world != null)
            reputation += WorldGeneration.GetRunSettingFloat("traderrepoffset");
        if (body.talker.impairedSpeech)
            reputation -= 20f;
        if (body.disfigured)
            reputation -= 5f;
        if (body.dirtyness > 50f)
            reputation -= body.dirtyness * 0.25f;
        if (body.HoldingItem(body.handSlot) && body.GetItem(body.handSlot).Stats.HasTag("gun")) {
            hostility += 50f;
            reputation -= 20f;
        }
        if ((bool)body.mindWipe)
            reputation *= 0.7f;
        reputation -= (100f - body.brainHealth) * 0.5f;
        reputation += body.happiness * 0.5f;
        if (body.hearingLoss > 50f)
            reputation -= 20f;
        if (body.totalBleedSpeed > 0.001f)
            freeDressing = true;

        return (reputation, hostility, freeDressing);
    }

    internal static bool EnsureFirstMeet(TraderScript trader, Body prefer = null) {
        if (trader == null || !Net.IsServer || trader.startedConvo)
            return false;

        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        float reputation = 0f;
        float hostility = 0f;
        bool freeDressing = false;
        bool any = false;
        var plrs = ScavPlayer.GetPlayerBodiesInRadius(trader.transform.position, FirstMeetScanRadius);
        foreach (NetBody pb in plrs) {
            if (pb?.body == null)
                continue;
            any = true;
            var res = MeetPlayer_CalculateFirstImpression(pb.body);
            reputation = Mathf.Max(res.Item1, reputation);
            hostility = Mathf.Max(res.Item2, hostility);
            if (res.Item3)
                freeDressing = true;
        }
        if (!any && prefer != null) {
            var res = MeetPlayer_CalculateFirstImpression(prefer);
            reputation = res.Item1;
            hostility = res.Item2;
            freeDressing = res.Item3;
            any = true;
        }
        if (!any)
            return false;

        trader.reputation = reputation;
        trader.hostility = hostility;
        tt.og.freeDressing = freeDressing;
        if (freeDressing && trader.items != null && !trader.items.Exists(x => x != null && x.id == "bandage")) {
            trader.items.Add(new TraderItem {
                id = "bandage",
                preference = TraderItemPreference.Indifferent,
                value = Item.GetItem("bandage").value,
            });
            trader.items = trader.items.OrderBy(x => (int)x.preference).ToList();
        }
        trader.eyes.sprite = trader.eyeSprites[1];
        Traverse.Create(trader).Field("sitTime").SetValue(Time.time);
        trader.startedConvo = true;
        trader.standTarget = Random.Range(0.9f, 1f);
        return true;
    }

    public static bool Prefix(TraderScript __instance) {
        if (!Net.IsRunning)
            return true;
        if (Net.IsClient) {
            __instance.startedConvo = true;
            __instance.eyes.sprite = __instance.eyeSprites[1];
            Traverse.Create(__instance).Field("sitTime").SetValue(Time.time);
            TraderCommit.Ask(__instance.GetOrAddComponent<TrackerTrader>());
            return false;
        }
        if (__instance.startedConvo)
            return false;
        EnsureFirstMeet(__instance, Util.GetLocalBodyNullable());
        if (__instance.startedConvo) {
            __instance.GetOrAddComponent<TrackerTrader>().Server_AnnounceTraderReputationState();
        }
        return false;
    }

    public static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(TraderScript), nameof(TraderScript.DoRest))]
internal static class TraderScript_DoRest_MultiplayerPatch {
    private static bool Prefix(TraderScript __instance) {
        return TraderCommit.LetStock(__instance, TraderCommit.Op.Rest);
    }
}
