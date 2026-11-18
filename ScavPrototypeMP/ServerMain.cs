using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections;
using System.Collections.Generic;
using UnityEngine;

namespace Together;

public partial class ServerMain : MonoBehaviour {

    public const float SleepEnergySpeedMult = 25f;

    public static Dictionary<string, PlayerSavedState> Server_LastPlayerStates = new();

    public static float AVERAGE_PING = 0;

    public static int CURRENT_TPS = 0;
    public static int CURRENT_FPS = 0;

    public static byte AVG_CONNECTION_QUALITY = 0;

    public struct StartGameAnnouncementPacket : INetSerializeByMemcpy {
        public WorldMP.RunPrefs prefs;
        public UnityEngine.Random.State randomstate;
        public MultiplayerGameRules rules;
    }

    // MUST BE CALLED MANUALLY OR THE CLIENT WOULDNT KNOW WHATS HAPPENING
    internal static StartGameAnnouncementPacket LastStartGameAnnouncementPacket;
    public static void Server_Announce_GAME_START() {
        var runpref = (new WorldMP.RunPrefs()).ReadPrefs();

        var writer = Net.CreateWriter(NetmsgId.StartGameAnnouncement);

        LastStartGameAnnouncementPacket = new StartGameAnnouncementPacket {
            prefs = runpref,
            randomstate = UnityEngine.Random.state,
            rules = Multiplayer.rules,
        };
        writer.Put(LastStartGameAnnouncementPacket);
        writer.Put(WorldMP.CompileRunSettings(), true);
#if FULLGAME
        AchievementBreakSync.WriteAnnouncementFlag(writer);
#endif

        if (runpref.tutorial) {
            Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:host_started_tutorial>"));
        } else {
            Chat.Server_ChatAnnouncement(Chat.AddColorTags(Chat.DefaultChatColors.Server, "<key:host_started>"));
        }

        Multiplayer.DoMultiplayerStatusMessageLog($"### STARTING GAME ###");

        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void Server_SendMPLogMessage(in string msg, in bool is_error = false, IReadOnlyList<knetid> targets = null) {
        var writer = Net.CreateWriter(NetmsgId.CustomLogMessage);
        writer.Put(msg);
        writer.Put(is_error);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: targets ?? ServerMain.AllClientIds);
    }

    public static void _Server_OnServerNameChange(IReadOnlyList<knetid> targets = null) {
        var writer = Net.CreateWriter(NetmsgId.ServerInfoUpdate);
        writer.Put(Net.MyServerInfo);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: targets ?? ServerMain.AllClientIdsExceptHost);

        if (Net.IsRunningSteam) {
            KSteam.Server_UpdateLobbyData();
        }
    }

    public static void Server_InstantiateInstance(Vector2 pos, float rot, string name, IReadOnlyList<knetid> to_who) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_CreateInstance);

        writer.Put(pos);
        writer.Put(rot);
        writer.Put(name, true);

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: to_who);
    }

    public static void Server_AnnounceAlert(in string msg, bool important, bool reliable = true, IReadOnlyList<knetid> targets = null) {
        if (Net.IsDedicatedServer) {
            Util.DoAlert(msg, important);
        }

        var writer = Net.CreateWriter(NetmsgId.CLIENT_AnnounceDoAlert);
        writer.Put(msg);
        writer.Put(important);
        Net.Server_SendToClients(reliable ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, clientIds: targets ?? ServerMain.AllClientIds);
    }

    public static bool ForceTalkerSayAndAnnounce(in SyncInfo si, in string msg, IReadOnlyList<knetid> targets = null) {
        if (Util.TryGetTalkerOnObject(si.go, out var foundtalker, out var _)) {
            Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);

            if (targets == null) targets = ServerMain.AllClientIdsExceptHost;

            return TalkerSayAnnounce(si.syncId, 0, msg, targets);
        }

        return false;
    }
    public static bool ForceTalkerSayAndAnnounce(in NetBody pb, in string msg, IReadOnlyList<knetid> targets = null) {
        if (Util.TryGetTalkerOnObject(pb.gameObject, out var foundtalker, out var _)) {
            Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundtalker, msg, true);

            if (targets == null) targets = ServerMain.AllClientIdsExceptHost;

            return TalkerSayAnnounce(pb.netId, 1, msg, targets);
        }

        return false;
    }
    public static bool TalkerSayAnnounce(in knetid id, in byte type, in string msg, IReadOnlyList<knetid> targets = null) {
        var writer = Net.CreateWriter(NetmsgId.CLIENT_force_talker_say);
        writer.Put(id);
        writer.Put(type);
        writer.Put(msg);
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: targets ?? ServerMain.AllClientIds);

        return true;
    }

    public static void Server_AnnounceSeed(IReadOnlyList<knetid> to_who) {
        var writer = Net.CreateWriter(NetmsgId.GenerateWorld_SeedAnnounce);
        writer.Put(WorldGeneration_GenerateWorld_MultiplayerPatch.firstworldgenparams);
        // 仅在启用外部自定义种子时追加 4 字节尾载荷：原版 v5.5.x 客户端忽略剩余字节，新版客户端据此同步激活固定种子
        if (ModHooks.LastAppliedCustomWorldSeed != 0) {
            writer.Put(ModHooks.LastAppliedCustomWorldSeed);
        }
        Net.Server_SendToClientsVeryReliable(writer, to_who);
    }

    public static void Server_AnnounceSound(Vector2 pos, string name, IReadOnlyList<knetid> to_who) {
        var writer = Net.CreateWriter(NetmsgId.PlayThisSoundHere);

        writer.Put(pos);
        writer.Put(name, true);

        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: to_who);
    }

    public static void Server_SendWorldState() {
        var writer = Net.CreateWriter(NetmsgId.CurrentWorldStateSync);

        writer.Put(new SerializableRandomState { State = UnityEngine.Random.state });
        writer.Put(Util.IsWorldGenerated());
#if FULLGAME
        FullgameWorldCompat.WriteWorldExtras(writer);
#endif

        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void PromptPlayer(ScavPlayer plr, string promptId, string title, string text, Action<byte> action) {
        var key = (uint)((new System.Random()).Next());
        var realKey = $"PROMPT_{promptId}_{key}";

        plr.CUSTOM_LOCAL_DATA[realKey] = action;

        try {
            var writer = Net.CreateWriter(NetmsgId.CLIENT_CustomPrompt);
            writer.Put(key);
            writer.Put(promptId);
            writer.Put(title);
            writer.Put(text);
            Net.Server_SendTo(DeliveryMethod.ReliableOrdered, writer, plr.playerId);
        } catch (Exception e) {
            log.error(e);
        }

        Util.DelayCallLambda(30, () => {
            if (plr != null) {
                plr.CUSTOM_LOCAL_DATA.Remove(realKey);
            }
        });
    }

    public static bool _DEV_ENABLE_HP_SYNC = true;

    public static GameObject CHARACTER_PREFAB = null;
    public static Body CHARACTER_PREFAB_BODY => CHARACTER_PREFAB.transform.GetChild(0).GetComponent<Body>();

    public static IReadOnlyList<knetid> AllClientIds => _real_AllClientIds;
    public static IReadOnlyList<knetid> AllClientIdsExceptHost => _real_AllClientIdsExceptHost;
    public static IReadOnlyList<ScavPlayer> AllPlayersExceptHost => _real_AllPlayersExceptHost;

    private static List<knetid> _real_AllClientIds = new List<knetid>();
    private static List<knetid> _real_AllClientIdsExceptHost = new List<knetid>();
    private static List<ScavPlayer> _real_AllPlayersExceptHost = new List<ScavPlayer>();

    static readonly HashSet<knetid> _spawnBusy = new HashSet<knetid>();

    internal static bool SpawnPlacementBusy(knetid clientId) => _spawnBusy.Contains(clientId);

    public static IEnumerator GiveSpawnLocationToPlayer(knetid clientId) {
        if (!_spawnBusy.Add(clientId)) {
            yield break;
        }
#if TESTING
        log.l($"GiveSpawnLocationToPlayer called  clientId:{clientId}");
#endif

        ScavPlayer plr;
        NetBody pb;

        while (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out plr, out pb) || !Util.IsWorldGenerated() || !Body_PlaceBody_MultiplayerPatch.has_spawn_location) {
            if (ScavPlayer.TryGetPlayerFromClientId(clientId, out plr) && plr != null) {
                plr.ResetEntropy();
                // regen and a late join can reach this with no character. the client
                // waits on the spawn packet, and this wait used to wait on the body.
                if (plr.body == null && Util.IsWorldGenerated() && Body_PlaceBody_MultiplayerPatch.has_spawn_location) {
                    plr.CreateCharacter();
                }
            }

            yield return null;
        }

        if (plr != null) {
            plr.ResetEntropy();
#if TESTING
            log.l($"GiveSpawnLocationToPlayer finished waiting for {plr}");
#endif

            if (!pb.body.alive && Multiplayer.rules.LateJoinSpectate) {
                // dont give them any spawn location, cuz they were moved to outside of the map
            } else {
                // 优先检查外部存档管理模组（如 CuSaveManager）的回档精确坐标覆写，防止被 did_give_spawn_location_from_a_save 短路跳过
                if (ModHooks.TryResolveSpawnOverride(plr, pb.body, out Vector2 overridePos)) {
                    plr.ServerPlayerState.did_give_spawn_location_from_a_save = false;
                    pb.SetBodyPosition(overridePos);
                    PlayerCheckpoint.ReclaimGear(plr.playerId);
                } else if (plr.ServerPlayerState.did_give_spawn_location_from_a_save) {
                    plr.ServerPlayerState.did_give_spawn_location_from_a_save = false;
                } else {
                    if (
                        !Body_PlaceBody_MultiplayerPatch.ShouldKeepLayerXOffset() // off, or doPod (everyone in the pod)
                        || WorldGeneration.world.totalTraveled <= 0 // if its the first layer all spawn in the pod
                        || plr.LateJoin // if late join it must pick random player to spawn on
                    ) {
                        LateSpawnLocation(pb);
                    } else {
                        pb.SetBodyPosition(Util.PlaceBody_FindSpawnLocation(plr.ServerPlayerState.layer_transition_x_offset, plr.body));
                    }
                }
            }

            if (!pb.IsBodyLocal()) {
                log.l($"SERVER: Sending a spawn location for {pb} to {pb.transform.position} ");

                pb.body.SetVelocity(Vector2.zero);
                pb.Server_RemindPlayersCurrentState();
            } else {
#if !RELEASE
                log.warn("SERVER: LateSpawnLocation actually gets called for local body lol");
#endif
            }

            plr.ServerPlayerState.SaveLocationSnapshot();

            WorldChunkSync.singleton.timer_TilemapSync = 10;
        }
        _spawnBusy.Remove(clientId);
    }

    public static int GetAlivePlayerCountPercent(float scale) {
        var LivingPlayerCount = ScavPlayer.AllLivingPlayers.Count;
        if (LivingPlayerCount == 0)
            return 1;

        int result = Mathf.CeilToInt(((float)LivingPlayerCount) * scale);
        return Mathf.Clamp(result, 1, LivingPlayerCount);
    }

    public static int GetNumberOfPlayersRequiredToFinishLayer() {
        return GetAlivePlayerCountPercent(((float)Multiplayer.rules.LayerFinishPlrPercent) * 0.01f);
    }

    public static bool CheckIfEnoughPeopleAreAtLayerFinish() => CheckIfEnoughPeopleAreAtLayerFinish(out int _, out int _);
    public static bool CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required) {
        if (Multiplayer.rules.CheckIfLayerContinueIsDisabled()) {
            current = 0;
            required = 1000;
            return false;
        }
#if FULLGAME
        return CheckIfCanFinishLayerCustomCondition((ScavPlayer plr) => FullgameWorldCompat.IsAtLayerExit(plr.body), out current, out required);
#else
        return CheckIfCanFinishLayerCustomCondition((ScavPlayer plr) => { return (plr.pos.y < (float)(0L - (long)WorldGeneration.world.halfHeight) + 3.1f); }, out current, out required);
#endif
    }

    public static bool CheckIfShouldStartRadlineForStragglers(byte minimum = 1) {
        if (Multiplayer.rules.StragglerRadlinePercent < 100) {
            ServerMain.CheckIfEnoughPeopleAreAtLayerFinish(out int current, out int required);
            if (ScavPlayer.AllLivingPlayers.Count > 1 &&
                required > 0 &&
                current != ScavPlayer.AllLivingPlayers.Count &&
                current >= minimum
            ) {
                int required_for_radline = GetAlivePlayerCountPercent(Multiplayer.rules.StragglerRadlinePercent * 0.01f);
                if (required_for_radline > 0 && current >= required_for_radline) {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool CheckIfCanFinishLayerCustomCondition(Func<ScavPlayer, bool> func, out int current, out int required) {
        required = GetNumberOfPlayersRequiredToFinishLayer();
        current = 0;

        foreach (var item in ScavPlayer.AllLivingPlayers) {
            // OG: WorldGeneration.ContinueRun V5t4
            if ((!Net.IsServer || item.ServerPlayerState.is_loaded_in) && func(item)) {
                current++;

                if (current >= required) {
                    return true;
                }
            }
        }

        return false;
    }

    public static void LateSpawnLocation(NetBody b) {
        var pb = GetPlayerBodyHostOrAnyoneAlive(b.plr, b);
        var shouldSpawnOnPlayer = Multiplayer.rules.SpawnOnPlayer && pb != null && pb.body.alive;

        if (shouldSpawnOnPlayer && IsInWorld(pb.pos)) {
#if TESTING
            log.warn($"LateSpawnLocation: {b}.SetBodyPosition({pb.pos});");
#endif
            b.SetBodyPosition(pb.pos);
        } else {
#if TESTING
            log.warn($"LateSpawnLocation: {b}.body.PlaceBody();");
#endif
            if (RadiationLine.line != null) {
                Body_PlaceBody_MultiplayerPatch.SpawnLocation.y = Mathf.Min(Body_PlaceBody_MultiplayerPatch.SpawnLocation.y, RadiationLine.line.transform.position.y);
            }

            b.transform.position = Body_PlaceBody_MultiplayerPatch.SpawnLocation;
        }
        // the load sat extras at the old spot. now that he has moved, put them back on him.
        if (b != null && b.plr != null)
            PlayerCheckpoint.ReclaimGear(b.plr.playerId);
    }

    public static bool IsInWorld(in Vector2 pos) {
        return Mathf.Abs(pos.x) < WorldChunkSync.WORLDSIZE * 0.5f && Mathf.Abs(pos.y) < WorldChunkSync.WORLDSIZE * 0.5f;
    }

    public static NetBody GetPlayerBodyHostOrAnyoneAlive(ScavPlayer for_who = null, NetBody except = null) {
        NetBody pb = null;
        NetBody pb_team = null;
        if (!Net.IsDedicatedServer) {
            pb = ScavPlayer.GetLocalNetBodyNullable();
        }
        if (pb == null || !pb.body.conscious) {
            foreach (var item in ScavPlayer.BodyToPlayerDict) {
                if (item.Key.conscious && except != item.Value.netBody && for_who != item.Value && IsInWorld(item.Key.transform.position) && item.Value.ServerPlayerState.did_give_spawn_location && item.Value.ServerPlayerState.is_loaded_in) {
                    if (item.Key == Util.GetLocalBody()) { // cuz its a default fallback
                        continue;
                    }
                    if (Multiplayer.rules.Teams && for_who != null) {
                        if (item.Value.IsInSameTeamAs(for_who)) {
                            pb_team = item.Value.netBody;
                        }
                    }
                    return item.Value.netBody;
                }
            }
        }
        if (pb_team != null) {
            return pb_team;
        }
        return pb;
    }

    public static bool CheckIfEveryoneAliveIsActuallyConscious() {
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if (item.Key.alive) {
                if (!item.Key.conscious) {
                    return false;
                }
            }
        }
        return true;
    }

    public static List<knetid> GetListOfClientIdsExceptThis(NetBody exclude) {
        if (exclude.IsPlayer) {
            return GetListOfClientIdsExceptThis(exclude.plr.playerId);
        } else {
            return new List<knetid>(AllClientIds);
        }
    }

    public static List<knetid> GetListOfClientIdsExceptThis(knetid exclude) {
        var l = new List<knetid>();
        foreach (var item in ScavPlayer.AllPlayers) {
            var plr = item.GetComponent<ScavPlayer>();
            if (plr != null && (exclude != plr.playerId)) {
                l.Add(plr.playerId);
            }
        }
        return l;
    }

    public static List<knetid> GetListOfClientIdsExceptThisAndHost(knetid exclude) {
        var l = new List<knetid>(_real_AllClientIdsExceptHost);
        l.Remove(exclude);
        return l;
    }

    public static List<knetid> GetListOfClientIdsExceptThisAndHost(NetBody nb) {
        var l = new List<knetid>(_real_AllClientIdsExceptHost);
        if (nb.IsPlayer) {
            l.Remove(nb.plr.playerId);
        }
        return l;
    }

    public static bool CheckIfAnyoneIsMoving() {
        foreach (var plr in ScavPlayer.AllLivingPlayers) {
            if (plr.body.conscious) {
                if ((plr.body.moveDir != Vector2.zero)) {
                    return true;
                }
            }
        }
        return false;
    }

    public static bool CheckIfAnyoneFinishedLayer(out ScavPlayer who) {
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if (item.Key.alive && item.Key.conscious) {
                if (DidHeFinishTheLayer(item.Key)) {
                    who = item.Value;
                    return true;
                }
            }
        }
        who = null;
        return false;
    }

    public static bool DidHeFinishTheLayer(Body body) {
#if FULLGAME
        return FullgameWorldCompat.IsAtLayerExit(body);
#else
        if (body == null || WorldGeneration.world == null) {
            return false;
        }
        if ((body.transform.position.y < (float)(0L - (long)WorldGeneration.world.halfHeight) + 3.1f)) {
            return true;
        }
        return false;
#endif
    }

    public static bool HostBodyIsDead() {
        if (!Net.IsRunning || Net.IsDedicatedServer) {
            return false;
        }
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr != null && plr.IsHost) {
                return !plr.IsAlive();
            }
        }
        return false;
    }

    public static bool LocalPlayerAtLayerExit() {
        PlayerCamera cam = PlayerCamera.main;
        if (cam == null || cam.body == null || !cam.body.alive) {
            return false;
        }
        return DidHeFinishTheLayer(cam.body);
    }

    public static bool CheckIfEveryoneIsSleeping() {
        bool any = false;
        foreach (var item in ScavPlayer.BodyToPlayerDict) {
            if (!item.Key.alive) {
                continue;
            }
            any = true;
            // KO drops consciousness without setting sleeping. only a real sleep counts.
            if (!item.Key.sleeping) {
                return false;
            }
        }
        return any;
    }

    public static void ResetPlayersFinishedStatus() {
        foreach (var item in ScavPlayer.AllPlayers) {
            item._tutorial_finished = false;
            item.finishedLayer = false;
        }
    }
}
