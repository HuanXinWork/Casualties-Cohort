using System;
using System.Collections;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using CasualtiesTogetherUtils;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 服务端权威健康状态广播、客户端健康快照缓冲以及医疗物资/特殊伤口操作网络校验总管。
/// </summary>
internal class MedicalSync : CasualtiesTogetherSingleton {
    private static bool _lastHeartBeating = false;
    private static readonly Dictionary<knetid, CharacterHealthStateSyncPacket> HeldHealth = new Dictionary<knetid, CharacterHealthStateSyncPacket>();
    private static readonly List<knetid> HeldHealthDone = new List<knetid>();
    private static readonly HashSet<knetid> ForceHeld = new HashSet<knetid>();
    private static readonly Queue<NetBody> ServerToSendQueue = new Queue<NetBody>();

    private static int _miscHealthSyncCurBody = 0;
    private static int _healthSyncCurBody = 0;

    internal static ECGVisualizer woundview_ecg = null;
    public const float SyncFrequencyHealth = 0.9124532513f;
    public float timer_HealthUpdateSyncClients = 0f;

    internal static void HoldHealth(knetid id, CharacterHealthStateSyncPacket health, bool force = false) {
        if (force) {
            ForceHeld.Add(id);
        }
        if (KrokV5Optimization.FixInvSync.InGame() && NetBody.TryGetNetBodyFromId(id, out NetBody nb) && nb.body != null) {
            bool confirm = force || ForceHeld.Remove(id);
            health.Apply(nb, confirm);
            return;
        }
        HeldHealth[id] = health;
    }

    internal static void FlushHeldHealth() {
        if (!Util.IsInWorld()) {
            HeldHealth.Clear();
            ForceHeld.Clear();
            return;
        }
        if (!KrokV5Optimization.FixInvSync.InGame() || HeldHealth.Count == 0) {
            return;
        }
        HeldHealthDone.Clear();
        foreach (KeyValuePair<knetid, CharacterHealthStateSyncPacket> pair in HeldHealth) {
            if (!NetBody.TryGetNetBodyFromId(pair.Key, out NetBody nb) || nb.body == null) {
                continue;
            }
            pair.Value.Apply(nb, ForceHeld.Remove(pair.Key));
            HeldHealthDone.Add(pair.Key);
        }
        for (int i = 0; i < HeldHealthDone.Count; i++) {
            HeldHealth.Remove(HeldHealthDone[i]);
        }
    }

    public static bool IsRefusingHelp(Body b) {
        return b != null && !b.aboveMedicalCutoff && b.conscious;
    }

    private void UpdateCPREffects() {
        if (PlayerCamera.main == null || !NetBody.TryGetLocalNetBody(out NetBody locNb) || locNb.body == null) {
            return;
        }

        bool isCprPatient = CPRHandler.IsCPRBeingPerformedOnThisBody(locNb, out ScavPlayer rescuer);
        if (isCprPatient && locNb.body.IsRagdolling() && locNb.body.moveDir == Vector2.zero && !PlayerCamera.main.wantsJumpInput) {
            locNb.body.Ragdoll();
        }

        ECGVisualizer consciousnessEcg = PlayerCamera.main.consciousnessECG;
        if (consciousnessEcg != null && consciousnessEcg.active) {
            consciousnessEcg.followBody = !isCprPatient;
            if (rescuer != null && rescuer.netBody != null) {
                consciousnessEcg.writeHeight = KM.Clamp11(
                    locNb.body.GetECGHeight(Mathf.Min(0.1f, Time.unscaledDeltaTime) - 0.028f) - rescuer.netBody.cprPress);
            }
        }
    }

    private void LateUpdate() {
        UpdateCPREffects();

        if (!Util.IsInWoundView()) {
            _lastHeartBeating = false;
            return;
        }

        Body body = WoundView.view != null ? WoundView.view.body : null;
        if (body == null) {
            return;
        }

        AudioSource alertAndBreathAudio = WoundView.view.GetComponent<AudioSource>();
        bool antiDispersion = false;

        try {
            if (woundview_ecg == null) {
                woundview_ecg = WoundView.view.gameObject.GetComponentInChildren<ECGVisualizer>(true);
            }
            if (woundview_ecg != null) {
                bool oldFollowBody = woundview_ecg.followBody;
                if (body.IsBodyLocal()) {
                    woundview_ecg.followBody = true;
                } else {
                    woundview_ecg.followBody = false;
                    woundview_ecg.writeHeight = body.GetECGHeight(woundview_ecg.timeToUpdate - 0.028f);
                }
                if (woundview_ecg.followBody != oldFollowBody && woundview_ecg.pixelGrid != null) {
                    for (int j = 0; j < woundview_ecg.width; j++) {
                        for (int k = 0; k < woundview_ecg.height; k++) {
                            Color32 color = woundview_ecg.pixelGrid[j, k];
                            color.a = 0;
                            woundview_ecg.pixelGrid[j, k] = color;
                        }
                    }
                }
            }
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Medical, "WoundViewEcgOverride", ex);
        }

        if (!body.alive) {
            _lastHeartBeating = false;
            Util.WoundViewShowAlertText(Lang.Get("wv_deceased"));
        } else {
            if (_lastHeartBeating && body.inCardiacArrest) {
                _lastHeartBeating = false;
                if (!Util.IsUnchipped() && !body.IsBodyLocal()) {
                    Sound.Play("flatline", Vector2.zero, twoDimensional: true, pitchShift: false, null, 0.8f, 1f, noReverb: true, ignoreMixer: true);
                }
            }
            _lastHeartBeating = !body.inCardiacArrest;

            UpdateDispersionAlert(body, ref antiDispersion);
        }

        if (alertAndBreathAudio != null) {
            alertAndBreathAudio.volume = (body.alive || antiDispersion) ? 1f : 0f;
        }
    }

    private static void UpdateDispersionAlert(Body body, ref bool antiDispersion) {
        if (!Net.IsRunning) {
            return;
        }
        try {
            if (!SharedMain.CheckIfDispersionPunishmentProtocolRuleIsActive()) {
                return;
            }
            ScavPlayer targetPlr = ScavPlayer.LocalPlayer;
            if (UIInGame.SpectatorModeActive && body.TryGetNetBody(out NetBody pb)) {
                targetPlr = pb.plr;
            }
            if (!Util.IsBodyLocal(body) || targetPlr == null || targetPlr.body == null) {
                return;
            }

            if (SharedMain.CheckIfShouldActivateDispersionPunishmentProtocol(targetPlr, out float maxDist, out _, out _)) {
                antiDispersion = true;
                string alertText = ((int)Time.unscaledTime % 3) switch {
                    0 => Lang.Get("healthchipalert_antidispersion1"),
                    2 => Lang.Get("healthchipalert_antidispersion3"),
                    _ => Locale.GetOther("healthchipradalert2"),
                };
                Util.WoundViewShowAlertText(alertText);
            } else if (!Util.WoundViewAlertIsShown()
                       && maxDist > Util.MetersToTiles(Multiplayer.rules.ScatterPunishDistance) * 0.8f) {
                string warnText = ((int)Time.unscaledTime % 2 == 0)
                    ? Lang.Get("healthchipalert_antidispersion_warn")
                    : Lang.Get("healthchipalert_antidispersion3");
                Util.WoundViewShowAlertText(warnText);
            }
        } catch (Exception ex) {
            ModLog.Debug($"UpdateDispersionAlert 已忽略跨帧空引用: {ex.Message}", LogDomain.Medical);
        }
    }

    public static bool TryGetPlayerAndLimb(knetid playerId, byte limbId, out NetBody nb, out Limb limb) {
        if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(playerId, out _, out nb)
            && nb.body != null && nb.body.limbs != null && limbId < nb.body.limbs.Length) {
            limb = nb.body.limbs[limbId];
            return limb != null;
        }
        nb = null;
        limb = null;
        return false;
    }

    public static bool TryGetNetBodyAndLimb(knetid netBodyId, byte limbId, out NetBody nb, out Limb limb) {
        if (NetBody.TryGetNetBodyFromId(netBodyId, out nb)
            && nb.body != null && nb.body.limbs != null && limbId < nb.body.limbs.Length) {
            limb = nb.body.limbs[limbId];
            return limb != null;
        }
        nb = null;
        limb = null;
        return false;
    }

    public static bool Server_CheckCanHealerUseItemForLimb(
        knetid healerClientId,
        knetid targetBodyNetId,
        byte limbId,
        knetid itemSyncId,
        out ScavPlayer healerPlr,
        out NetBody targetNb,
        out SyncInfo itemSi) {
        healerPlr = null;
        targetNb = null;
        itemSi = null;

        float maxInteractDist = SharedMain.max_player_interaction_distance * 2.5f;
        return ScavPlayer.TryGetPlayerAndBodyFromClientId(healerClientId, out healerPlr, out Body healerBody)
            && healerBody.conscious
            && TryGetNetBodyAndLimb(targetBodyNetId, limbId, out targetNb, out _)
            && NetObjectRegistry.TryGetSyncInfo(itemSyncId, out itemSi)
            && itemSi.IsItem()
            && itemSi.item.Stats.usableOnLimb
            && ItemSync.CheckIfBodyReachThisItem(itemSi.item, healerBody, maxInteractDist, false)
            && Util.DoFullInteractionCheck(healerBody, targetNb.body, false, maxInteractDist, false);
    }

    public static void Server_SendCharacterHealth(NetBody nb, bool force = false) {
        if (nb == null || nb.body == null || nb.syncBody == null || !(force || CanSyncHealth())) {
            return;
        }

        var health = new CharacterHealthStateSyncPacket();
        health.Write(nb.body);

        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_HealthSync);
        writer.Put(nb.netId);
        writer.Put(health);
        writer.Put(nb.healthConfirm);
        nb.healthConfirm = false;

        writer.CompressWriter();
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_QueueSendCharacterHealth(NetBody nb, bool force = false) {
        Con.ConFailIfNetworkIsRunningAndIsClient();
        if (nb == null) {
            return;
        }

        if (force || (CanSyncHealth() && !nb._SERVER_healthsync_skip_frame)) {
            if (force) {
                nb.healthConfirm = true;
            }
            nb._SERVER_healthsync_skip_frame = true;
            ServerToSendQueue.Enqueue(nb);
        }
    }

    public static void ForceHealth(NetBody nb) {
        if (nb == null || nb.body == null || !Net.IsServer) {
            return;
        }
        nb.healthConfirm = true;
        Server_SendCharacterHealth(nb, true);
    }

    public static bool CanSyncHealth() {
        return ServerMain._DEV_ENABLE_HP_SYNC;
    }

    private IEnumerator HealthUpdateSyncClients() {
        // try/finally 确保跨帧实体销毁或异常不会让周期健康同步计时器永久冻死在负值
        try {
            if (Util.IsWorldGenerated() && Net.IsRunning && CanSyncHealth()) {
                var toSync = new List<NetBody>();
                int byteCounter = 0;
                int fullPackSize = Marshal.SizeOf(typeof(CharacterHealthPainkillerStateSyncPacket)) + 8 + 2;
                NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_HealthPainkillerSync);

                while (true) {
                    bool doBreak = false;
                    if (_miscHealthSyncCurBody >= NetBody.all_instances.Count) {
                        _miscHealthSyncCurBody = 0;
                        doBreak = true;
                    }
                    if (byteCounter + fullPackSize >= NetTimingConstants.UnreliableHealthBatchMaxBytes) {
                        doBreak = true;
                    }

                    if (doBreak) {
                        timer_HealthUpdateSyncClients = -5f;
                        yield return null;
                        toSync.RemoveAll(x => x == null || x.body == null || x.syncBody == null);

                        if (toSync.Count > 0) {
                            writer.SetPosition(sizeof(NetmsgId));
                            writer.Put(toSync[0].netId);
                            writer.Put(new CharacterHealthPainkillerStateSyncPacket(toSync[0].body));
                            for (int i = 1; i < toSync.Count; i++) {
                                writer.Put(true);
                                writer.Put(toSync[i].netId);
                                writer.Put(new CharacterHealthPainkillerStateSyncPacket(toSync[i].body));
                            }
                            writer.Put(false);
                            Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                            byteCounter = 0;
                            toSync.Clear();
                        }
                        break;
                    }

                    NetBody nb = NetBody.all_instances[_miscHealthSyncCurBody];
                    _miscHealthSyncCurBody++;

                    if (nb == null || nb._SERVER_healthsync_skip_frame || nb.body == null || nb.syncBody == null
                        || !nb.body.TryGetComponent<Painkillers>(out _)) {
                        continue;
                    }

                    toSync.Add(nb);
                    byteCounter += fullPackSize;
                    yield return null;
                }

                while (true) {
                    if (_healthSyncCurBody >= NetBody.all_instances.Count) {
                        _healthSyncCurBody = 0;
                        break;
                    }

                    NetBody npc = NetBody.all_instances[_healthSyncCurBody];
                    _healthSyncCurBody++;

                    if (npc == null || npc.body == null || npc.syncBody == null) {
                        continue;
                    }
                    if (npc._SERVER_healthsync_skip_frame) {
                        npc._SERVER_healthsync_skip_frame = false;
                        continue;
                    }
                    if (!npc.body.alive) {
                        npc._SERVER_healthsync_skip_frame = !npc._SERVER_healthsync_skip_frame;
                    }

                    Server_SendCharacterHealth(npc);
                    yield return null;
                }
            }
            yield return null;
        } finally {
            timer_HealthUpdateSyncClients = 0f;
        }
    }

    protected void Update() {
        if (Net.IsClient) {
            FlushHeldHealth();
            return;
        }

        if (ServerToSendQueue.Count > 0) {
            NetBody nb = ServerToSendQueue.Dequeue();
            if (nb != null && nb.body != null && nb.syncBody != null) {
                nb._SERVER_healthsync_skip_frame = true;
                Server_SendCharacterHealth(nb, true);
            }
        }

        timer_HealthUpdateSyncClients += Time.unscaledDeltaTime;
        if (timer_HealthUpdateSyncClients > SyncFrequencyHealth) {
            timer_HealthUpdateSyncClients = -2f * NetBody.all_instances.Count;
            StartCoroutine(HealthUpdateSyncClients());
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_HealthPainkillerSync)]
    private static void ClientReceiver__HealthPainkillerSync(knetid _, NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out CharacterHealthPainkillerStateSyncPacket health);
        if (NetBody.TryGetNetBodyFromId(who, out NetBody nb) && nb.body != null) {
            health.Apply(nb.body);
        }

        while (true) {
            reader.Get(out bool next);
            if (!next) {
                break;
            }
            reader.Get(out knetid nextWho);
            reader.Get(out CharacterHealthPainkillerStateSyncPacket nextHealth);
            if (NetBody.TryGetNetBodyFromId(nextWho, out NetBody nextNb) && nextNb.body != null) {
                nextHealth.Apply(nextNb.body);
            }
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_HealthSync)]
    private static void ClientReceiver__HealthSync(knetid _, NetDataReader reader) {
        reader = reader.DecompressReader();
        reader.Get(out knetid npcId);
        reader.Get(out CharacterHealthStateSyncPacket health);
        bool confirm = false;
        if (reader.AvailableBytes >= 1) {
            reader.Get(out confirm);
        }
        HoldHealth(npcId, health, confirm);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_VomiterVomit)]
    private static void ClientReceiver__VomiterVomit(knetid _, NetDataReader reader) {
        reader.Get(out knetid who);
        reader.Get(out bool isBlood);

        Body body = ScavPlayer.GetBodyFromClientId(who);
        if (body != null && body.vomiter != null) {
            Vomiter_Vomit_MultiplayerPatch.ForceNoWarning(body.vomiter, isBlood);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction)]
    private static void ServerReceiver_WoundSpecialAction(knetid clientId, NetDataReader reader) {
        if (!CanSyncHealth()) {
            return;
        }

        reader.Get(out LimbNetId targetLimb);
        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AwakeInWorld, LogDomain.Medical, (byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction, out VerifiedCaller caller)) {
            return;
        }

        if (!targetLimb.TryGetNetBodyAndLimbSafe(out NetBody targetPb, out Limb limb) || targetPb.body == null) {
            MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction, 0, "InvalidLimb");
            return;
        }

        if (caller.Body != targetPb.body) {
            if (!KM.DistanceCheck(caller.Body, targetPb.body, SharedMain.max_player_interaction_distance * 1.5f)
                || !Util.AccurateRaycastInteractionCheckObstruction(caller.Body, targetPb.body, false)) {
                MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_WoundSpecialAction, targetPb.syncBody != null ? targetPb.netId.id : 0u, "OutOfRangeOrObstructed");
                return;
            }
        }

        if (!caller.Player.IsLocal) {
            PlayerCamera_WoundSpecialAction_MultiplayerPatch.OG_WoundSpecialAction(caller.Body, limb);
        }
        Server_QueueSendCharacterHealth(targetPb);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem)]
    private static void ServerReceiver_ApplyWoundItem(knetid clientId, NetDataReader reader) {
        if (!CanSyncHealth()) {
            return;
        }

        reader.Get(out LimbNetId targetLimb);
        reader.Get(out knetid itemSyncId);

        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AwakeInWorld, LogDomain.Medical, (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem, out VerifiedCaller caller)) {
            return;
        }

        if (!targetLimb.TryGetNetBodyAndLimbSafe(out NetBody targetNb, out Limb limb)
            || targetNb.body == null
            || !ItemSync.TryGetItemSyncInfo(itemSyncId, out SyncInfo isi)
            || isi.item == null) {
            MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem, itemSyncId.id, "TargetOrItemMissing");
            return;
        }

        ScavPlayer healer = caller.Player;
        NetBody healerNb = caller.NetBody;

        // 对齐 Server_CheckCanHealerUseItemForLimb 的交互容差半径（9f * 2.5f = 22.5f，对应 PR #60/#58）：
        // 此前此处使用 ServerExchangeToleranceDistance（20f），导致 20~22.5 距离区间内客机本地可正常打开医疗/义肢修补小游戏，
        // 但服务端拒收初始 ApplyWoundItem 包从而漏记状态并引发修补回滚；Release 与 Debug 双构建统一强制校验。
        float maxInteractDist = SharedMain.max_player_interaction_distance * 2.5f;
        if (healerNb != targetNb
            && !KM.DistanceCheck(caller.Body, targetNb.body, maxInteractDist)) {
            MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem, itemSyncId.id, "TargetTooFar");
            ModLog.Warn($"拒绝远程医疗请求（超出交互距离）: {healer} -> {targetLimb}, item={isi}", LogDomain.Medical);
            return;
        }

        if (!ItemSync.CheckIfBodyReachThisItem(isi, caller.Body, maxInteractDist, true)) {
            MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem, itemSyncId.id, "ItemUnreachable");
            ModLog.Warn($"拒绝远程医疗请求（物品不可达）: {healer} -> {targetLimb}, item={isi}", LogDomain.Medical);
            return;
        }

        if (!PlayerCamera_ApplyWoundItem_MultiplayerPatch.DoApplyWoundItemChecks(caller.Body, limb, isi.item)) {
            MpCommsTrace.HostRejected(LogDomain.Medical, clientId.id, (byte)NetmsgId.SERVER_PlayerCamera_ApplyWoundItem, itemSyncId.id, "WoundItemChecksDenied");
            return;
        }

        bool removingProsthetic = limb.HasProsthetic() && Item_DoAmputate_MultiplayerPatch.IsAmputateTool(isi.item);

        void FinishApply() {
            var recipients = ServerMain.GetListOfClientIdsExceptThisAndHost(clientId);
            NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay);
            writer.Put(clientId);
            writer.Put(targetLimb);
            writer.Put(itemSyncId);
            Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: recipients);
            MpCommsTrace.HostBroadcast(LogDomain.Medical, (byte)NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay, recipients.Count, clientId.id);

            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = !Util.IsBodyLocal(caller.Body);
            PlayerCamera_ApplyWoundItem_MultiplayerPatch.ForceApplyWoundItem(caller.Body, limb, isi.item);
            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;

            if (removingProsthetic) {
                ForceHealth(targetNb);
            } else {
                Server_QueueSendCharacterHealth(targetNb);
            }
        }

        bool askConsent = removingProsthetic
            && healerNb != targetNb
            && targetNb.IsPlayer
            && targetNb.plr != null
            && targetNb.body != null
            && targetNb.body.conscious;
        if (askConsent && healerNb.syncBody != null && targetNb.syncBody != null) {
            ServerMain.PromptPlayer(
                targetNb.plr,
                "PROSTHETIC_REMOVE",
                $"<body:{healerNb.netId}> wants to remove your prosthetic.",
                "Allow this?",
                answer => {
                    if (targetNb.syncBody == null) {
                        return;
                    }
                    if (answer == 0) {
                        if (isi.go == null || limb == null || !limb.HasProsthetic()) {
                            healer.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:giveitem_noitem>.");
                            return;
                        }
                        FinishApply();
                        healer.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:accepted>.");
                    } else {
                        healer.Server_DoAlertSingle($"<bodyc:{targetNb.netId}> <key:declined>.");
                    }
                });
        } else {
            FinishApply();
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay)]
    private static void ClientReceiver__ApplyWoundItem_Relay(knetid senderId, NetDataReader reader) {
        reader.Get(out knetid whoClientId);
        reader.Get(out LimbNetId targetLimb);
        reader.Get(out knetid itemSyncId);

        if (targetLimb.TryGetNetBodyAndLimb(out _, out Limb limb)
            && ScavPlayer.TryGetPlayerAndBodyFromClientId(whoClientId, out ScavPlayer healer, out Body healerBody)
            && !healer.IsLocal
            && NetObjectRegistry.TryGetSyncInfo(itemSyncId, out SyncInfo si)
            && si.item != null) {
            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = true;
            PlayerCamera_ApplyWoundItem_MultiplayerPatch.ForceApplyWoundItem(healerBody, limb, si.item);
            MinigameBase_StartMinigame_MultiplayerPatch.ignore_next_mgstart_cuz_its_not_me = false;
            MpCommsTrace.ClientReceive(LogDomain.Medical, (byte)NetmsgId.CLIENT_PlayerCamera_ApplyWoundItem_Relay, itemSyncId.id);
        }
    }
}
