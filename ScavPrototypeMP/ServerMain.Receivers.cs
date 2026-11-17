using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class ServerMain {

    public static void _RegisterServerReceivers() {
#if DEBUG
        Plugin.log.LogInfo("REGISTERING SERVER Receivers ");
#endif
        foreach (var item in Multiplayer.all_network_receivers_from_attributes) {
            if (item.Key.GetReceiverType() == NetworkMessageReceiverAttribute.ReceiverType.Server) {
                Net.RegisterServerReceiver(
                    item.Key.GetMessageId(),
                    (knetid clientId, LiteNetLib.Utils.NetDataReader reader) => {
                        try {
                            item.Value.Invoke(clientId, reader);
                        } catch (Exception e) {
                            ModLog.Exception(LogDomain.Transport, $"ServerReceiver:{item.Key.GetMessageName()} ({DebugHelp.GetPlayerFullDebugString(clientId)})", e);
                        }
                    }
                );
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestPiggyback)]
    static void ServerReceiver__RequestPiggyback(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid this_guy);

        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out NetBody plrbody) && NetBody.TryGetNetBodyFromId(this_guy, out NetBody guybody) && plrbody != guybody) {
            if (
                plr.IsLocal ||
                (plr.body.conscious && guybody.IsPiggybackable() &&
                plrbody.StartPiggyback(guybody, true, true))
            ) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestPiggyback {clientId} to {this_guy}"
                    );

                if (log.verbose)
                    log.l($"RequestPiggyback success: {plr} -> {guybody}");
                return;
            }
            plr.Server_DoAlertSingle("SERVER: Denied Piggyback.");

            log.serverdeny($"RequestPiggyback {plr} -> {guybody}");

            plr.Server_RemindPlayersCurrentState();
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestStopCarryPiggyback)]
    static void ServerReceiver__RequestStopCarryPiggyback(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var _)) {
            if (
                plr.IsConscious() &&
                plr.netBody.piggybacking_on != null
            ) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestStopCarryPiggyback {plr} "
                    );

                plr.netBody.StopPiggyback();

                if (log.verbose)
                    log.l($"RequestStopCarryPiggyback success: {plr} ");
                return;
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_GiveItem)]
    static void ServerReceiver__GiveItem(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid itemId);
        reader.Get(out knetid this_guy);

        // 校验赠送者清醒状态与物品可触及性，并统一引用 GameplayRanges.ServerExchangeToleranceDistance 防隔空瞬移
        if (Util.IsWorldGenerated()
            && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var giverPlr, out NetBody giverNetBody)
            && giverNetBody.body != null && giverNetBody.body.conscious
            && NetBody.TryGetNetBodyFromId(this_guy, out NetBody receiverNetBody) && giverNetBody != receiverNetBody
            && receiverNetBody.body != null
            && ItemSync.TryGetItemSyncInfo(itemId, out var si) && si.item != null
            && ItemSync.CheckIfBodyReachThisItem(si, giverNetBody.body)
            && KM.DistanceCheck(giverNetBody, receiverNetBody, GameplayRanges.ServerExchangeToleranceDistance)
        ) {
            var ici = ItemSync.ItemGetContainerInfo(si.item);
            if (ici.bodynpc != receiverNetBody) {
                if (receiverNetBody.body.conscious && receiverNetBody.IsPlayer) {
                    PromptPlayer(receiverNetBody.plr, "ITEM_OFFER", $"<body:{giverNetBody.netId}> <key:plrint_give_prompt>", $"<key:plrint_take_prompt> \"<item:{si.syncId}>\"?", (answer) => {
                        if (answer == 0) {
                            if (ItemSync.ConfirmItemOffer(giverNetBody, receiverNetBody, si.item)) {
                                ItemSync.ForcePickupItem(receiverNetBody, si.item);

                                giverPlr.Server_DoAlertSingle($"<bodyc:{receiverNetBody.netId}> <key:accepted>.");
                            }
                        } else {
                            giverPlr.Server_DoAlertSingle($"<bodyc:{receiverNetBody.netId}> <key:declined>.");
                        }
                    });
                } else {
                    ItemSync.ForcePickupItem(receiverNetBody, si.item);
                }
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_CustomPromptAnswer)]
    static void ServerReceiver__CustomPromptAnswer(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out uint key);
        reader.Get(out string promptId);
        reader.Get(out byte answer);

        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            var realKey = $"PROMPT_{promptId}_{key}";
            try {
                if (
                    plr.CUSTOM_LOCAL_DATA.TryGetValue(realKey, out var offerAction) && offerAction is Action<byte> act
                ) {
                    act(answer);
                }
            } catch (Exception e) {
                log.error(e);
            }
            plr.CUSTOM_LOCAL_DATA.Remove(realKey);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCarryPerson)]
    static void ServerReceiver__RequestCarryPerson(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid this_guy);

        if (Util.IsWorldGenerated() && ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out NetBody plrbody) && NetBody.TryGetNetBodyFromId(this_guy, out NetBody nb2) && plrbody != nb2) {
            if (
                plrbody.carrying_person == null &&
                plr.body.conscious == true
            ) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(
                        plr.body.transform.position,
                        $"S: RequestCarryPerson {clientId} to {this_guy}"
                    );

                if (
                    nb2.CanBeCarriedBySomeone() &&
                    nb2.StartPiggyback(plr.netBody, true, true)
                ) {
                    if (log.verbose) log.l($"RequestCarryPerson success: {plr} -> {nb2}");
                    return;
                }
            }
            plr.Server_DoAlertSingle("SERVER: Denied Carry.");

            log.serverdeny($"RequestCarryPerson {plr} -> {nb2}");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestStopCarryPerson)]
    static void ServerReceiver__RequestStopCarryPerson(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            Util.IsWorldGenerated() &&
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var plr, out var _) &&
            plr.IsConscious() &&
            plr.netBody.carrying_person != null
        ) {
            var sci2 = plr.netBody.carrying_person;

            sci2.StopPiggyback(); // it sends the reminder

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(plr.body.transform.position, $"S: StopCarryPerson {plr} to {sci2}");
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Tutorial_Finished)]
    static void ServerReceiver__Tutorial_Finished(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.GetPlayerFromClientId(clientId) is var plr && plr != null) {
            if (!Util.IsTutorialWorld()) {
                log.serverdeny($"SUS: {plr} sent Tutorial_Finished when we are not even in a tutorial.");
                return;
            }

            if (!plr._tutorial_finished) {
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(plr.body.transform.position, $"S: Tutorial_Finished {clientId}");

                plr.OnFinishTutorial();
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.PlayerPointFingerAt)]
    static void ServerReceiver__PlayerPointFingerAt(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr) && Util.IsInWorld()) {
            reader.Get(out Vector2 pos);
            reader.Get(out byte t);

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(pos, $"S: PlayerPointFingerAt {DebugHelp.GetPlayerFullDebugString(clientId)}");

            plr.PointFingerAt(pos, t);

            var writer = Net.CreateWriter(NetmsgId.PlayerPointFingerAtRelay);
            writer.Put(clientId);
            writer.Put(pos);
            writer.Put(t);
            Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, ServerMain.GetListOfClientIdsExceptThisAndHost(clientId));
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerJump)]
    static void ServerReceiver__PlayerJump(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out var _, out var body)
            && body.conscious && body.standing && Util.IsWorldGenerated()
        ) {
            body.Jump();
        }
    }

    [ServerReceiver((byte)NetmsgId.PlayerImpactDamage)]
    static void ServerReceiver__PlayerImpactDamage(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (!Util.IsWorldGenerated()) {
            return;
        }

        reader.Get(out byte i);
        reader.Get(out float f);
        reader.Get(out Vector2 dir);
        reader.Get(out AnyObjectNetId obj);
        var force = Mathf.Abs(f);

        // 统一使用 AnatomyConstants.IsValidLimbIndex 校验肢体下标范围，杜绝越界异常
        if (
            ScavPlayer.GetBodyFromClientId(clientId) is var body && body != null &&
            body.alive && AnatomyConstants.IsValidLimbIndex(body, i) &&
            Util.IsFinite(f) && dir.IsFinite()
        ) {
#if !RELEASE
            Plugin.log.LogWarning($"Server recieved  PlayerImpactDamage {i} {f} {dir}  for plr {clientId}");
#endif

            var limb = body.limbs[i];

            if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                DebugHelp.OnNetEvent(limb.transform.position, $"S: Impact {limb.name} force:{force} dir:{dir}");

            Limb_ImpactDamage_MultiplayerPatch.Force(limb, force, dir);

            if (limb.TryGetNetBody(out var nb) && obj.TryGetBuilding(out var b, out var _) && b.TryGetComponent(out DamagingCrate _)) {
                nb.OnDamageEvent(new BodyDamageEvent(b, nb, force));
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerTalkerSay)]
    static void ServerReceiver__PlayerTalkerSay(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid speakerId);
        reader.Get(out string msg);
        if (String.IsNullOrEmpty(msg)) {
            return;
        }
        if (!ScavPlayer.TryGetPlayerFromClientId(speakerId, out var plr) || plr == null || plr.netBody == null) {
            if (!ScavPlayer.TryGetPlayerFromClientId(clientId, out plr) || plr == null || plr.netBody == null) {
                return;
            }
        }
        List<knetid> others = new List<knetid>();
        foreach (knetid id in AllClientIdsExceptHost) {
            if (id != plr.playerId) {
                others.Add(id);
            }
        }
        ForceTalkerSayAndAnnounce(plr.netBody, msg, others);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerBark)]
    static void ServerReceiver__PlayerBark(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(
                clientId,
                out var plr,
                out var body
            ) &&
            body.conscious
        ) {
            if (!body.IsBodyLocal()) {
                body.eatTime = 0;
                body.GetComponent<PantSound>().Bark();
            }
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) DebugHelp.OnNetEvent(plr.pos, $"S: Bark");
            Multiplayer.Server_SendRelayMessageToClients((byte)NetmsgId.CLIENT_PlayerBarkRelay, clientId, false, false);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_Push_Ego_Naxuy)]
    static void ServerReceiver__Push_Ego_Naxuy(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid target);

        if (
            ScavPlayer.TryGetPlayerAndBodyFromClientId(
                clientId, out var pusher_plr, out var pusher) &&
            pusher.conscious && pusher.standing &&
            pusher_plr.ServerPlayerState.Cooldown("push", NetBody.PUSH_COOLDOWN) &&
            NetBody.TryGetNetBodyFromId(target, out var target_nb) &&
            KM.DistanceCheck(pusher, target_nb.body, SharedMain.max_player_interaction_distance * 1.2f)
        ) {
            pusher_plr.netBody.Push(target_nb);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_SetTimeScale)]
    static void ServerReceiver__SetTimeScale(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out ushort sc);

        // 严格执行房间时间倍率规则校验，阻止在未开启 EnableTimeManipulation 时篡改全局流速
        if (!Multiplayer.rules.EnableTimeManipulation)
            return;

        if ((PlayerCamera.SpeedType)sc == PlayerCamera.SpeedType.Paused)
            return;

        if (Util.IsInWorld() && ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            Plugin.log.LogInfo($"SetTimeScale: Received SetTimeScale from a client {clientId} {plr.ToString()}  ");

            PlayerCamera.main.SetTimeScale((PlayerCamera.SpeedType)sc, true);
            PlayerCamera.main.DoAlert(String.Format(Lang.Get("plr_set_timescale"), plr.playerName, $"{sc}"));
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ClientCharacterSyncUpdate)]
    static void ServerReceiver__ClientCharacterSyncUpdate(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader = reader.DecompressReader();
        reader.Get(out ClientToServer_NetBodySyncPacket pack);

        if (Util.IsWorldGenerated()) {
            if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out var plr, out var pb)) {
                if (!plr.IsLocal) {
                    plr.ServerPlayerState.is_loaded_in = true;
                    if (pb.alive && !plr.Server_HasForcedServerOwnershipOfBody()) {
                        {
                            // 拦截乱序旧位置包并维持 SetNetIgnoreTime 传送/击退防回弹窗口
                            var seqDiff = LiteEntitySystem.Utils.SequenceDiff(pack.Tick, pb._lastSyncPacketReceiveTick);
                            if (seqDiff <= 0) {
#if !RELEASE
                                if (log.verbose) log.l($"OnReceiveSyncPacket {pb}: received packet that is too old, ignoring it.");
#endif
                                return;
                            }
                            pb._lastSyncPacketReceiveTick = pack.Tick;

                            if (pb.body != null) {
                                pack.Apply(pb);
                            }
                        }

                        if (pack.IncludeRagData() && !reader.EndOfData && pb.piggybacking_on == null) {
                            ClientMain.ReadRagdollPacket(ref reader, pb.body);
                        }
                    }
                }
            } else {
                log.error($"player {clientId} doesnt have a playerprefab object ?????  ClientCharacterSyncUpdate");
            }
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerDrinkLiquid)]
    static void ServerReceiver__PlayerDrinkLiquid(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var plr)) {
            if (Util.IsWorldGenerated() && plr.IsAlive()) {
                if (log.verbose)
                    log.l($"Server approves drink for: {plr} ");
                if (DebugHelp._DEV_VISUALISE_NET_EVENTS)
                    DebugHelp.OnNetEvent(plr.body.transform.position, $"S: PlayerDrinkLiquid {plr}");

                var drinkblockpos = WorldGeneration.world.WorldToBlockPos(
                    plr.body.transform.position - Vector3.up * 2.5f);

                var there_is_a_fluid =
                    FluidManager.main.GetLiquid(drinkblockpos.x, drinkblockpos.y);
                if (!plr.IsLocal) {
                    FluidManager.main.DrinkLiquid(drinkblockpos, plr.body);
                }
                if (there_is_a_fluid != 0 || plr.IsLocal) {
                    ServerMain.Server_AnnounceSound(plr.body.transform.position, "drink", ServerMain.AllClientIdsExceptHost);
                }

                MedicalSync.Server_QueueSendCharacterHealth(plr.netBody);
                return;
            }
            log.sus($"PlayerDrinkLiquid {plr} is ded or sum  ");
        }
    }

    internal static Dictionary<string, Action<ScavPlayer, string, string[]>> ServerCustomCommandsDict = new();
    internal static void RunClientCustomCommand(ScavPlayer plr, LiteNetLib.Utils.NetDataReader reader) {
        var com = reader.GetString();
        var args = com.Trim().Split(' ');

        if (ServerCustomCommandsDict.TryGetValue(args[0], out var action)) {
            try {
                action.Invoke(plr, com, args);
            } catch (Exception ex) {
                Con.Server_SendConsoleLog(ex.Message, plr);
            }
        }
    }
}
