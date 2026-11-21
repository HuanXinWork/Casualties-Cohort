using System;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using static TraderScript;

namespace Together;

/// <summary>
/// 商人交易与交涉串行权威提交器：按商人实体独立排队执行买卖/砍价/拥抱/招募等操作，统一发布权威快照。
/// </summary>
internal static class TraderCommit {
    internal enum Op : byte {
        Buy = 1,
        Sell = 2,
        Haggle = 3,
        Threaten = 4,
        Hug = 5,
        Move = 6,
        Rest = 7,
        Recruit = 8,
    }

    internal static bool Running { get; private set; }
    internal static Body Actor { get; private set; }

    private sealed class Job {
        public Op op;
        public Body actor;
        public SyncInfo trader;
        public byte index;
        public string itemId;
        public ushort value;
        public knetid sold;
    }

    private static readonly Dictionary<knetid, Queue<Job>> Queues = new();
    private static readonly HashSet<knetid> Pumping = new();
    private static readonly Dictionary<knetid, HashSet<byte>> ReservedOffer = new();
    private static readonly HashSet<knetid> ReservedSell = new();
    private static int sellFrame = -1;

    internal static bool LetStock(TraderScript trader, Op op) {
        if (!Net.IsRunning || Running)
            return true;

        PlayerCamera cam = PlayerCamera.main;
        if (cam == null || cam.currentTrader != trader)
            return true;

        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        if (tt.si == null || tt.si.syncId == 0)
            return false;

        if (op == Op.Hug && trader.didHug) {
            cam.PlayUISound(PlayerCamera.UISoundType.Deny);
            return false;
        }

        MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.CommitSubmit, 0, tt.si.syncId, 0, (int)op);
        ModLog.Flow(LogDomain.Trader, "TraderCommit.LetStock", $"op={op} trader={tt.si.syncId}");

        if (Net.IsServer) {
            Submit(op, cam.body, tt.si);
            return false;
        }

        Multiplayer.Client_SendSimpleMessageToServer(OpMessage(op), tt.si.syncId, true);
        if (op == Op.Rest) {
            cam.ToggleTradeMenu();
        } else if (op == Op.Hug || op == Op.Haggle) {
            cam.PlayUISound(PlayerCamera.UISoundType.MiniClick);
        }
        return false;
    }

    internal static void Submit(Op op, Body actor, SyncInfo trader) {
        if (actor == null || trader == null || trader.trader == null || trader.IsDestroyed)
            return;
        Enqueue(new Job {
            op = op,
            actor = actor,
            trader = trader,
        });
    }

    internal static void SubmitBuy(Body actor, SyncInfo trader, byte index, string itemId, ushort value) {
        if (actor == null || trader == null || trader.trader == null)
            return;

        TraderScript live = trader.trader;
        if (live.items == null || index >= live.items.Count) {
            MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.PacketReject, 0, trader.syncId, index, (int)Op.Buy);
            ModLog.Flow(LogDomain.Trader, "TraderCommit.BuyReject", $"trader={trader.syncId} index={index} out-of-range");
            Publish(live, actor);
            return;
        }

        TraderItem offer = live.items[index];
        if (offer is null || offer.id != itemId || (ushort)offer.value != value || Reserved(trader.syncId, index)) {
            MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.PacketReject, 0, trader.syncId, index, (int)Op.Buy);
            ModLog.Flow(LogDomain.Trader, "TraderCommit.BuyReject", $"trader={trader.syncId} index={index} item={itemId} mismatch/reserved");
            Publish(live, actor);
            return;
        }

        Reserve(trader.syncId, index);
        MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.CommitSubmit, 0, trader.syncId, index, (int)Op.Buy);
        Enqueue(new Job {
            op = Op.Buy,
            actor = actor,
            trader = trader,
            index = index,
            itemId = itemId,
            value = value,
        });
    }

    internal static void SubmitSell(Body actor, SyncInfo trader, knetid sold) {
        if (actor == null || trader is null || trader.trader == null || sold == 0)
            return;

        if (Time.frameCount != sellFrame) {
            ReservedSell.Clear();
            sellFrame = Time.frameCount;
        }
        if (ReservedSell.Contains(sold)) {
            MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.PacketReject, 0, trader.syncId, sold.id, (int)Op.Sell);
            Publish(trader.trader, actor);
            return;
        }

        ReservedSell.Add(sold);
        MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.CommitSubmit, 0, trader.syncId, sold.id, (int)Op.Sell);
        Enqueue(new Job {
            op = Op.Sell,
            actor = actor,
            trader = trader,
            sold = sold,
        });
    }

    internal static void Ask(TrackerTrader tt) {
        if (!Net.IsClient || tt == null)
            return;
        SyncInfo si = tt.si;
        if (si == null || si.syncId == 0)
            return;
        Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestTraderSnapshot, si.syncId, true);
    }

    internal static void Publish(TraderScript trader) {
        Publish(trader, null);
    }

    internal static void Publish(TraderScript trader, Body actor) {
        if (trader == null || !Net.IsServer)
            return;
        RefreshOpen(trader);
        List<knetid> ids = ClientsInRange(trader);
        if (actor != null && actor.TryGetNetPlayer(out ScavPlayer acting) && !acting.IsLocal && !ids.Contains(acting.playerId)) {
            ids.Add(acting.playerId);
        }
        if (ids.Count == 0)
            return;
        Send(trader, ids);
    }

    internal static void PublishTo(TraderScript trader, knetid clientId) {
        if (trader == null || !Net.IsServer || clientId == 0)
            return;
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out ScavPlayer plr) && plr.IsLocal) {
            RefreshOpen(trader);
            return;
        }
        Send(trader, new List<knetid> { clientId });
    }

    internal static bool InRange(Vector2 traderPos, Vector2 bodyPos) {
        WorldGeneration world = WorldGeneration.world;
        if (world == null)
            return false;
        Vector2Int traderChunk = world.BlockToChunkPos(world.WorldToBlockPos(traderPos));
        Vector2Int bodyChunk = world.BlockToChunkPos(world.WorldToBlockPos(bodyPos));
        int dx = Mathf.Abs(traderChunk.x - bodyChunk.x);
        int dy = Mathf.Abs(traderChunk.y - bodyChunk.y);
        return dx <= 1 && dy <= 1;
    }

    internal static void Apply(TraderScript trader, NetDataReader reader) {
        if (trader == null)
            return;

        int character = reader.GetInt();
        float desiredX = reader.GetFloat();
        float desiredY = reader.GetFloat();
        float moveMin = reader.GetFloat();
        float moveMax = reader.GetFloat();
        bool didMove = reader.GetBool();
        float reputation = reader.GetFloat();
        float hostility = reader.GetFloat();
        float haggleAmount = reader.GetFloat();
        bool freeDressing = reader.GetBool();
        bool didHug = reader.GetBool();
        byte freeAmount = reader.GetByte();
        ushort valueGiven = reader.GetUShort();
        ushort totalValueGiven = reader.GetUShort();
        byte count = reader.GetByte();

        var items = new List<TraderItem>(count);
        for (int i = 0; i < count; i++) {
            reader.Get(out string id, true);
            reader.Get(out bool bought);
            reader.Get(out ushort value);
            reader.Get(out byte preference);
            items.Add(new TraderItem {
                id = id,
                bought = bought,
                value = value,
                preference = (TraderItemPreference)preference,
            });
        }

        trader.character = character;
        trader.desiredPos = new Vector2(desiredX, desiredY);
        trader.MoveRange.min = moveMin;
        trader.MoveRange.max = moveMax;
        trader.didMove = didMove;
        trader.reputation = reputation;
        TraderWindup.Note(trader, hostility);
        trader.haggleAmount = haggleAmount;
        trader.freeDressing = freeDressing;
        trader.didHug = didHug;
        trader.freeAmount = freeAmount;
        trader.valueGiven = valueGiven;
        trader.totalValueGiven = totalValueGiven;

        if (trader.items == null) {
            trader.items = new List<TraderItem>();
        } else {
            trader.items.Clear();
        }
        trader.items.AddRange(items);
        trader.GetOrAddComponent<TrackerTrader>().received_their_inv = true;
        RefreshOpen(trader);
    }

    private static void RefreshOpen(TraderScript trader) {
        PlayerCamera cam = PlayerCamera.main;
        if (cam == null || cam.currentTrader != trader || !Util.IsInTradingMenu())
            return;
        cam.RefreshTraderInventories();
        cam.UpdateTradeTexts();
    }

    internal static void RelaySound(TraderScript trader, Body actor, string name, Vector2 pos) {
        if (!Net.IsServer || string.IsNullOrEmpty(name) || trader == null)
            return;
        List<knetid> ids = ClientsInRange(trader);
        if (actor != null && actor.TryGetNetPlayer(out ScavPlayer acting) && !acting.IsLocal && !ids.Contains(acting.playerId)) {
            ids.Add(acting.playerId);
        }
        if (ids.Count == 0)
            return;
        ServerMain.Server_AnnounceSound(pos, name, ids);
    }

    internal static void RelaySwing(TraderScript trader, Vector2 aim, knetid exceptClient = default) {
        if (!Net.IsServer || trader == null)
            return;
        knetid id = IdOf(trader);
        if (id == 0)
            return;
        List<knetid> ids = ClientsInRange(trader);
        if (exceptClient != 0) {
            ids.Remove(exceptClient);
        }
        if (ids.Count == 0)
            return;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_TraderSwing);
        writer.Put(id);
        writer.Put(aim.x);
        writer.Put(aim.y);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ids);
    }

    internal static void PlaySwingFx(TraderScript trader, Vector2 aim) {
        if (trader == null || trader.torso == null || trader.attackAnimation == null)
            return;
        Vector2 to = (aim - (Vector2)trader.torso.position).normalized;
        if (to.sqrMagnitude < 0.0001f) {
            to = Vector2.right;
        }
        GameObject slash = UnityEngine.Object.Instantiate(trader.attackAnimation);
        slash.transform.eulerAngles = new Vector3(0f, 0f, Vector2.SignedAngle(Vector3.right, to));
        slash.transform.localScale = new Vector3(1f, to.x > 0f ? 1f : -1f, 1f);
        slash.transform.position = trader.torso.position + trader.torso.up * 0.8f;
        UnityEngine.Object.Destroy(slash, 2f);
        Sound.Play("BSSwing1", trader.transform.position);

        Traverse arms = Traverse.Create(trader).Field("bonusArmRot");
        float[] bonusArmRot = arms.GetValue<float[]>();
        if (bonusArmRot != null && bonusArmRot.Length > 1) {
            bonusArmRot[to.x > 0f ? 1 : 0] = to.x > 0f ? 90f : -90f;
            arms.SetValue(bonusArmRot);
        }
        trader.bonusRot += to.x > 0f ? -10f : 10f;
        Traverse.Create(trader).Field("lastAttackTime").SetValue(Time.time + UnityEngine.Random.Range(-0.5f, 0.5f));
        if (PlayerCamera.main != null && Vector2.Distance(trader.transform.position, PlayerCamera.main.transform.position) < 24f) {
            PlayerCamera.main.threatMusicTime = 10f;
            PlayerCamera.main.currentThreatTheme = 12;
        }
    }

    internal static void ApplySwingLunge(TraderScript trader, Vector2 aim) {
        if (trader == null)
            return;
        Vector2 to = (aim - (Vector2)trader.transform.position).normalized;
        if (to.sqrMagnitude < 0.0001f) {
            to = Vector2.right;
        }
        Vector3 position = trader.transform.position;
        position.x += to.x * UnityEngine.Random.Range(0.125f, 0.5f)
            * (UnityEngine.Random.value < 0.25f ? -1.6f : 1f)
            * (UnityEngine.Random.value < 0.2f ? 5f : 1f);
        position.x = Mathf.Clamp(position.x, trader.MoveRange.min, trader.MoveRange.max);
        trader.desiredPos = position;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TraderSwing, false)]
    private static void Client_Swing(knetid _, NetDataReader reader) {
        reader.Get(out knetid traderId);
        float x = reader.GetFloat();
        float y = reader.GetFloat();
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || si is null || si.trader == null)
            return;
        PlaySwingFx(si.trader, new Vector2(x, y));
    }

    private static List<knetid> ClientsInRange(TraderScript trader) {
        var ids = new List<knetid>();
        Vector2 pos = trader.transform.position;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.IsLocal || plr.body == null)
                continue;
            if (InRange(pos, plr.body.transform.position)) {
                ids.Add(plr.playerId);
            }
        }
        return ids;
    }

    private static void Send(TraderScript trader, List<knetid> ids) {
        knetid id = IdOf(trader);
        if (id == 0) {
            NetObjectRegistry.NewGO(trader.gameObject);
            id = IdOf(trader);
            if (id == 0)
                return;
        }

        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_TraderSnapshot);
        writer.Put(id);
        writer.Put((int)trader.character);
        writer.Put(trader.desiredPos.x);
        writer.Put(trader.desiredPos.y);
        writer.Put((float)trader.MoveRange.min);
        writer.Put((float)trader.MoveRange.max);
        writer.Put(trader.didMove);
        writer.Put(trader.reputation);
        writer.Put(trader.hostility);
        writer.Put(trader.haggleAmount);
        writer.Put(trader.freeDressing);
        writer.Put(trader.didHug);
        writer.Put((byte)trader.freeAmount);
        writer.Put((ushort)trader.valueGiven);
        writer.Put((ushort)trader.totalValueGiven);

        int count = trader.items == null ? 0 : trader.items.Count;
        if (count > 255)
            count = 255;
        writer.Put((byte)count);
        for (int i = 0; i < count; i++) {
            TraderItem item = trader.items[i];
            string itemId = item is null ? "" : item.id;
            writer.Put(itemId, true);
            writer.Put(item is not null && item.bought);
            writer.Put((ushort)(item is null ? 0 : item.value));
            writer.Put((byte)(item is null ? 0 : (byte)item.preference));
        }
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ids);
    }

    private static knetid IdOf(TraderScript trader) {
        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        if (tt.si != null && tt.si.syncId != 0)
            return tt.si.syncId;
        if (NetObjectRegistry.TryGetSyncInfo(trader.gameObject, out SyncInfo si))
            return si.syncId;
        return 0;
    }

    private static byte OpMessage(Op op) {
        return op switch {
            Op.Haggle => (byte)NetmsgId.SERVER_TraderTryHaggle,
            Op.Threaten => (byte)NetmsgId.SERVER_TraderThreaten,
            Op.Hug => (byte)NetmsgId.SERVER_TraderTryHug,
            Op.Move => (byte)NetmsgId.SERVER_TraderAskToMove,
            Op.Rest => (byte)NetmsgId.SERVER_TraderDoRest,
            _ => 0,
        };
    }

    private static void Enqueue(Job job) {
        knetid id = job.trader.syncId;
        if (!Queues.TryGetValue(id, out Queue<Job> queue)) {
            queue = new Queue<Job>();
            Queues[id] = queue;
        }
        queue.Enqueue(job);
        Pump(id);
    }

    private static void Pump(knetid id) {
        if (!Pumping.Add(id))
            return;
        try {
            Queue<Job> queue = Queues[id];
            while (queue.Count > 0) {
                Job job = queue.Dequeue();
                try {
                    Run(job);
                } finally {
                    Release(job);
                }
                if (job.trader is not null && !job.trader.IsDestroyed && job.trader.trader != null) {
                    Publish(job.trader.trader, job.actor);
                }
            }
        } finally {
            Pumping.Remove(id);
            if (Queues.TryGetValue(id, out Queue<Job> drained) && drained.Count == 0) {
                Queues.Remove(id);
            }
        }
    }

    private static void Run(Job job) {
        TraderScript trader = job.trader.trader;
        Body actor = job.actor;
        if (trader == null || actor == null)
            return;

        TraderScript_MeetPlayer_MultiplayerPatch.EnsureFirstMeet(trader, actor);
        trader.startedConvo = true;
        TrackerTrader tt = trader.GetOrAddComponent<TrackerTrader>();
        Body previous = tt.focused_body;
        tt.focused_body = actor;
        Running = true;
        Actor = actor;
        try {
            switch (job.op) {
                case Op.Buy:
                    RunBuy(trader, actor, job);
                    break;
                case Op.Sell:
                    RunSell(trader, actor, job);
                    break;
                case Op.Haggle:
                    bool bite = trader.character == 2 && trader.totalValueGiven < MAX_VALUE_GIVEN;
                    Vector2 biteAt = actor != null ? (Vector2)actor.transform.position : (Vector2)trader.transform.position;
                    trader.TryHaggle();
                    if (bite) {
                        RelaySound(trader, actor, "eatFlesh", biteAt);
                        if (actor != null && actor.TryGetNetBody(out NetBody bitten)) {
                            bitten.OnDamageEvent(new BodyDamageEvent(trader.build, bitten, 30));
                            MedicalSync.ForceHealth(bitten);
                        }
                    }
                    break;
                case Op.Threaten:
                    trader.Threaten();
                    break;
                case Op.Hug:
                    if (!trader.didHug) {
                        float rep = trader.reputation;
                        trader.TryHug();
                        trader.didHug = true;
                        string hugSound = trader.reputation < rep ? "BSSwing1" : VanillaAssetKeys.SoundCombine;
                        RelaySound(trader, actor, hugSound, trader.transform.position);
                    }
                    break;
                case Op.Move:
                    trader.AskToMove();
                    trader.didMove = true;
                    break;
                case Op.Rest:
                    RunRest(trader, actor);
                    break;
                case Op.Recruit:
                    PlayerCamera_ToggleTradeMenu_MultiplayerPatch.PerformRecruit(job.trader);
                    break;
            }
        } finally {
            Actor = null;
            Running = false;
            tt.focused_body = previous;
        }
    }

    private static void RunBuy(TraderScript trader, Body actor, Job job) {
        if (trader.items == null || job.index >= trader.items.Count)
            return;
        TraderItem offer = trader.items[job.index];
        if (offer is null || offer.id != job.itemId || (ushort)offer.value != job.value)
            return;
        Item bought = TraderScript_TryPurchase_MultiplayerPatch.TryPurchase_rewritten(trader, offer, actor);
        if (bought != null) {
            NetObjectRegistry.TryGetSyncInfoOrRegister(bought, out _);
        }
    }

    private static void RunSell(TraderScript trader, Body actor, Job job) {
        if (!ItemSync.TryGetItemSyncInfo(job.sold, out SyncInfo itemsi) || itemsi.item == null)
            return;
        if (!ItemSync.CheckIfBodyReachThisItem(itemsi, actor) || trader.reputation < 30f)
            return;
#if FULLGAME
        if (itemsi.item.Stats != null && itemsi.item.Stats.untradeable)
            return;
#endif
        trader.GiveItem(itemsi.item);
    }

    private static void RunRest(TraderScript trader, Body actor) {
        if (Util.IsBodyLocal(actor)) {
            trader.DoRest();
            return;
        }
        if (!actor.canTakeNap)
            return;
        actor.sleeping = true;
        actor.consciousness = 30f;
        actor.forcedSleepQuality = Body.SleepQuality.Good;
    }

    private static void Release(Job job) {
        if (job.op == Op.Buy) {
            Unreserve(job.trader.syncId, job.index);
        }
    }

    private static bool Reserved(knetid trader, byte index) {
        return ReservedOffer.TryGetValue(trader, out HashSet<byte> held) && held.Contains(index);
    }

    private static void Reserve(knetid trader, byte index) {
        if (!ReservedOffer.TryGetValue(trader, out HashSet<byte> held)) {
            held = new HashSet<byte>();
            ReservedOffer[trader] = held;
        }
        held.Add(index);
    }

    private static void Unreserve(knetid trader, byte index) {
        if (!ReservedOffer.TryGetValue(trader, out HashSet<byte> held))
            return;
        held.Remove(index);
        if (held.Count == 0) {
            ReservedOffer.Remove(trader);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TraderSnapshot)]
    private static void Client_Snapshot(knetid _, NetDataReader reader) {
        reader.Get(out knetid traderId);
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || si is null) {
            ModLog.Flow(LogDomain.Trader, "TraderCommit.SnapshotMiss", $"trader={traderId} not-registered");
            return;
        }

        TraderScript trader;
        try {
            trader = si.trader;
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Trader, "TraderCommit.SnapshotComponentError", $"trader={traderId} err={ex.Message}");
            return;
        }

        if (trader == null) {
            ModLog.Flow(LogDomain.Trader, "TraderCommit.SnapshotNullTrader", $"trader={traderId} go={si.go?.name ?? "null"}");
            return;
        }

        try {
            Apply(trader, reader);
            MpFlightRecorder.Record(LogDomain.Trader, FlightEventKind.CommitReceipt, 0, traderId, 0, trader.items?.Count ?? 0);
            ModLog.Flow(LogDomain.Trader, "TraderCommit.SnapshotApplied", $"trader={traderId} items={trader.items?.Count ?? 0}");
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Trader, $"Client_Snapshot(trader={traderId})", ex);
            throw;
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestTraderSnapshot)]
    private static void Server_Request(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderId);
        if (!NetObjectRegistry.TryGetSyncInfoLive(traderId, out SyncInfo si) || si is null) {
            ModLog.Flow(LogDomain.Trader, "TraderCommit.RequestMiss", $"client={clientId} trader={traderId} no-sync");
            return;
        }

        TraderScript live;
        try {
            live = si.trader;
        } catch (Exception ex) {
            ModLog.Flow(LogDomain.Trader, "TraderCommit.RequestError", $"client={clientId} trader={traderId} err={ex.Message}");
            return;
        }

        if (live == null)
            return;
        // 服务端 TraderScript.Start 尚未初始化商品列表前不下发空快照，让客户端 AskWhenReady 继续轮询直到库存生成完毕
        if (live.items == null)
            return;

        ScavPlayer.TryGetPlayerAndBodyFromClientId(clientId, out _, out Body body);
        // 若该商人尚未完成房间首见判定则先计算初始好感；无论是否首见均必须下发快照，防止后到或重连客户端打开交易界面空白无货
        TraderScript_MeetPlayer_MultiplayerPatch.EnsureFirstMeet(live, body);
        PublishTo(live, clientId);
    }

    [ServerReceiver((byte)NetmsgId.SERVER_TraderDoRest)]
    private static void Server_Rest(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid traderId);
        if (TraderSync.Server_TraderInteractionCheck(clientId, traderId, "Rest", out ScavPlayer plr, out SyncInfo si, out _)) {
            Submit(Op.Rest, plr.body, si);
            return;
        }
        if (NetObjectRegistry.TryGetSyncInfo(traderId, out si) && si.trader != null) {
            PublishTo(si.trader, clientId);
        }
    }
}
