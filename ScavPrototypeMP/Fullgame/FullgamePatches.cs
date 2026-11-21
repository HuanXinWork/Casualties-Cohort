using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 兼顾 Demo 与 Fullgame 双版本的反射兼容扩展方法集。
/// </summary>
public static class FullgameCompatExtensions {
    private static readonly MethodInfo _propCanRead = ReflectionHelp.PropertyGetter(typeof(Body), "canRead");
    private static readonly MethodInfo _propFinishSpot = ReflectionHelp.PropertyGetter(typeof(DislocationMinigame), "FinishSpot");
    private static readonly FieldInfo _fieldDismemberChance = ReflectionHelp.Field(typeof(ExplosionParams), "dismemberChance");
    private static readonly MethodInfo _methodHasHearingProtection = ReflectionHelp.Method(typeof(Body), "HasHearingProtection");

    public static bool CanRead(this Body b) {
        if (_propCanRead == null) {
            return !b.IsMindwiped();
        }
        return (bool)_propCanRead.Invoke(b, System.Array.Empty<object>());
    }

    public static float GetDismemberChance(this ExplosionParams ep) {
        if (_fieldDismemberChance == null) {
            return 0f;
        }
        return (float)_fieldDismemberChance.GetValue(ep);
    }

    public static void SetDismemberChance(this ExplosionParams ep, float value) {
        if (_fieldDismemberChance == null || ep == null) {
            return;
        }
        _fieldDismemberChance.SetValue(ep, value);
    }

    public static bool MPHasHearingProtection(this Body b) {
        if (_methodHasHearingProtection == null) {
            return b.HasWearable("earmuffs");
        }
        return (bool)_methodHasHearingProtection.Invoke(b, System.Array.Empty<object>());
    }

    public static Vector2 GetFinishSpot(this DislocationMinigame dm) {
        if (_propFinishSpot == null || dm == null) {
            return new Vector2(375f, 54.6f);
        }
        return (Vector2)_propFinishSpot.Invoke(dm, System.Array.Empty<object>());
    }
}

/// <summary>
/// 正式版过场状态同步与默认地雷参数修补单例。
/// </summary>
public class FullgameMultiplayerPatcher : CasualtiesTogetherSingleton {
#if FULLGAME
    bool _lastCutsceneActive;
    bool _cutsceneArmed;
#endif
    static float _vanillaMineDismemberChance = -1f;
    bool _mpMineChanceApplied;

    void Start() {
#if RELEASE
        if (Util.GameIsDemo()) {
            return;
        }
#endif
        if (_vanillaMineDismemberChance < 0f && Constants.DefaultMineExplosionParams != null) {
            _vanillaMineDismemberChance = Traverse.Create(Constants.DefaultMineExplosionParams).Field("dismemberChance").GetValue<float>();
        }
        SyncMineDismemberChance();
    }

    void SyncMineDismemberChance() {
#if RELEASE
        if (Util.GameIsDemo()) {
            return;
        }
#endif
        if (Constants.DefaultMineExplosionParams == null || _vanillaMineDismemberChance < 0f) {
            return;
        }
        bool wantMp = Net.IsRunning;
        if (wantMp == _mpMineChanceApplied) {
            return;
        }
        _mpMineChanceApplied = wantMp;
        Traverse.Create(Constants.DefaultMineExplosionParams).Field("dismemberChance")
            .SetValue(wantMp ? 0.15f : _vanillaMineDismemberChance);
    }

    void Update() {
        SyncMineDismemberChance();
#if FULLGAME
        if (!Net.IsRunning || !Util.IsInWorld() || WorldGeneration.world == null) {
            _cutsceneArmed = false;
            return;
        }
        bool active = WorldGeneration.world.cutsceneActive;
        if (!_cutsceneArmed) {
            _lastCutsceneActive = active;
            _cutsceneArmed = true;
            return;
        }
        if (active == _lastCutsceneActive) {
            return;
        }
        _lastCutsceneActive = active;
        if (!Net.IsServer) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_CutsceneActive);
        writer.Put(active);
        Net.Server_SendToClients(DeliveryMethod.ReliableOrdered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
#endif
    }

#if FULLGAME

    [ClientReceiver((byte)NetmsgId.CLIENT_CutsceneActive)]
    static void Client_CutsceneActive(knetid _, NetDataReader reader) {
        reader.Get(out bool active);
        if (!Util.IsInWorld() || WorldGeneration.world == null) {
            return;
        }
        WorldGeneration.world.cutsceneActive = active;
        SharedMain.IsInCutscene = active;
    }
#endif
}

#if FULLGAME
[HarmonyPatch(typeof(PreRunScript), nameof(PreRunScript.SelectCharacter))]
static class PreRunScript_SelectCharacter_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        if (CharacterSelectWait.SpeciesLocked) {
            CharacterSelectWait.EnforceLockedSpecies();
            return false;
        }
        return true;
    }

    static void Postfix() {
        if (!Net.IsRunning) {
            return;
        }
        CharacterSelectWait.OnLocalCharacterBrowsed(PreRunScript.characterSelected);
    }
}

[HarmonyPatch(typeof(ConsoleScript), nameof(ConsoleScript.CheckForCheats))]
static class ConsoleScript_CheckForCheats_MultiplayerPatch {
    static void Prefix(ref bool __state) {
        __state = SteamManager.disableAchievements;
        if (!Net.IsRunning) {
            return;
        }
        Con.ConFailIfCheatsDisabled();
    }

    static void Postfix(bool __state) {
        if (!Net.IsRunning || !Net.IsServer) {
            return;
        }
        if (__state || !SteamManager.disableAchievements) {
            return;
        }
        AchievementBreakSync.Server_Broadcast(showAlert: true);
    }
}

[HarmonyPatch(typeof(CustomItemBehaviour), nameof(CustomItemBehaviour.CryoActivate))]
public static class CustomItemBehaviour_CryoActivate_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }

    static void Prefix(CustomItemBehaviour __instance) {
        if (!Net.IsRunning) {
            return;
        }
        BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody();
    }
}

[HarmonyPatch]
static class GuardSpawner_MultiplayerPatch {
    class TrackerGuardSpawner : BaseComponentTracker<GuardSpawner> {
        protected override void TrackerAwake() {
        }

        void Update() {
            if (Net.IsClient) {
                if ((bool)EndLabScript.instance && EndLabScript.instance.doingEscape) {
                    og.openProgress = 0f;
                    Collider2D[] cols = og.GetComponentsInChildren<Collider2D>(true);
                    for (int i = 0; i < cols.Length; i++) {
                        if (cols[i] != null) {
                            cols[i].enabled = false;
                        }
                    }
                }
                return;
            }

            if ((bool)EndLabScript.instance && EndLabScript.instance.doingEscape) {
                og.openProgress += Time.deltaTime;
            }

            (_, float sqrDist) = NetBody.GetNearestBody(og.transform.position, true);
            float dist = Mathf.Sqrt(sqrDist);
            if (!(dist < og.minDistanceToSpawn) && !(dist > og.maxDistanceToSpawn) && og.guardsToSpawn > 0 && !(og.openProgress < og.minTimeBetweenSpawn)) {
                og.CreateGuard();
            }
        }
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Start))]
    static bool StartPrefix(GuardSpawner __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        __instance.GetOrAddComponent<TrackerGuardSpawner>();
        return true;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(GuardSpawner), nameof(GuardSpawner.Update))]
    static bool UpdatePrefix() {
        return !Net.IsRunning;
    }
}

[HarmonyPatch(typeof(GuardScript), "body", MethodType.Getter)]
static class GuardScript_Body_Nearest_MultiplayerPatch {
    static bool Prefix(GuardScript __instance, ref Body __result) {
        if (!Net.IsRunning || !Net.IsServer) {
            return true;
        }
        __result = GuardHost.Nearest(__instance);
        return __result == null;
    }
}

[HarmonyPatch(typeof(GuardScript), "Update")]
static class GuardScript_Update_Host_MultiplayerPatch {
    static bool Prefix(GuardScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            GuardHost.Hold(__instance);
            return false;
        }
        return true;
    }

    static void Postfix(GuardScript __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        GuardHost.AfterSim(__instance);
    }
}

sealed class GuardPose : MonoBehaviour {
    public bool Has;
    public Vector2 Pos;
    public float Scale;
    public float Arm;
    public float Head;
    public float Laser;
    public byte Walk;
    public byte Flags;
    public ushort Target;
    public bool Died;
    public float SwingUntil;
    public float ShotUntil;
}

/// <summary>
/// 终局实验室守卫房主权威 AI 与客户端姿态镜像同步器。
/// </summary>
static class GuardHost {
    const byte Fired = 1;
    const byte Melee = 2;
    const byte Dead = 4;
    const byte Stunned = 8;

    struct Sent {
        public int Ammo;
        public bool Melee;
        public bool Dead;
        public float Next;
    }

    static readonly Dictionary<int, Sent> Last = new Dictionary<int, Sent>();
    static int _lastPruneAt = StaleIds.FirstThreshold;

    [CTPostInit]
    static void Init() {
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.GuardPose] = (cmd, reader) => {
            reader.Get(out ushort id);
            reader.Get(out Vector2 pos);
            reader.Get(out float scale);
            reader.Get(out float arm);
            reader.Get(out float head);
            reader.Get(out float laser);
            reader.Get(out byte walk);
            reader.Get(out byte flags);
            reader.Get(out ushort target);
            Apply(id, pos, scale, arm, head, laser, walk, flags, target);
        };
    }

    public static Body Nearest(GuardScript guard) {
        if (guard == null) {
            return null;
        }
        Body best = null;
        float bestSqr = float.MaxValue;
        Vector2 origin = guard.transform.position;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.alive) {
                continue;
            }
            float sqr = ((Vector2)plr.body.transform.position - origin).sqrMagnitude;
            if (sqr < bestSqr) {
                bestSqr = sqr;
                best = plr.body;
            }
        }
        return best;
    }

    public static void AfterSim(GuardScript guard) {
        if (guard.doingMelee) {
            Body hit = Nearest(guard);
            if (hit != null && hit.TryGetNetBody(out NetBody hurt)) {
                MedicalSync.Server_QueueSendCharacterHealth(hurt, true);
            }
        }
        Publish(guard);
    }

    public static void Hold(GuardScript guard) {
        if (guard == null || !guard.TryGetComponent(out GuardPose pose) || !pose.Has) {
            return;
        }
        ApplyPose(guard, pose);
    }

    static void Publish(GuardScript guard) {
        if (ServerMain.AllClientIdsExceptHost == null || ServerMain.AllClientIdsExceptHost.Count == 0) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(guard.gameObject, out SyncInfo si) && !NetObjectRegistry.TryGetSyncInfoOrRegister(guard.gameObject, out si)) {
            return;
        }
        if (si == null) {
            return;
        }
        int key = guard.GetInstanceID();
        StaleIds.Prune<GuardScript, Sent>(Last, ref _lastPruneAt);
        int ammo = Traverse.Create(guard).Field("ammo").GetValue<int>();
        bool melee = guard.doingMelee;
        bool dead = false;
        if (guard.TryGetComponent(out BuildingEntity build)) {
            dead = build.GetHealth() <= 0f;
        }
        bool known = Last.TryGetValue(key, out Sent prev);
        bool fired = known && ammo < prev.Ammo;
        bool meleeEdge = melee && (!known || !prev.Melee);
        bool deadEdge = dead && (!known || !prev.Dead);
        bool due = fired || meleeEdge || deadEdge || !known || Time.time >= prev.Next;
        Last[key] = new Sent { Ammo = ammo, Melee = melee, Dead = dead, Next = due ? Time.time + 0.05f : prev.Next };
        if (!due) {
            return;
        }
        byte flags = 0;
        if (fired) {
            flags |= Fired;
        }
        if (meleeEdge) {
            flags |= Melee;
        }
        if (dead) {
            flags |= Dead;
        }
        if (guard.armSprite != null && guard.armSprite.sprite == guard.armsStun) {
            flags |= Stunned;
        }
        ushort target = 0;
        Body who = Nearest(guard);
        if (who != null && who.TryGetNetBody(out NetBody nb) && nb.plr != null) {
            target = nb.plr.playerId.id;
        }
        float laser = 0f;
        if (guard.laserLine != null) {
            laser = guard.laserLine.transform.localScale.y;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.GuardPose);
        writer.Put((ushort)si.syncId);
        writer.Put((Vector2)guard.transform.position);
        writer.Put(guard.transform.localScale.x);
        writer.Put(guard.arms != null ? guard.arms.eulerAngles.z : 0f);
        writer.Put(guard.head != null ? guard.head.eulerAngles.z : 0f);
        writer.Put(laser);
        writer.Put(WalkFrame(guard));
        writer.Put(flags);
        writer.Put(target);
        Net.Server_SendToClients((fired || meleeEdge || deadEdge) ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    static byte WalkFrame(GuardScript guard) {
        if (guard.myBody == null || guard.bodyMoveSprites == null) {
            return 255;
        }
        for (int i = 0; i < guard.bodyMoveSprites.Length; i++) {
            if (guard.myBody.sprite == guard.bodyMoveSprites[i]) {
                return (byte)i;
            }
        }
        return 255;
    }

    static void Apply(ushort id, Vector2 pos, float scale, float arm, float head, float laser, byte walk, byte flags, ushort target) {
        if (!NetObjectRegistry.TryGetSyncInfo((knetid)id, out SyncInfo si) || si.go == null) {
            return;
        }
        if (!si.go.TryGetComponent(out GuardScript guard)) {
            return;
        }
        GuardPose pose = guard.GetOrAddComponent<GuardPose>();
        pose.Has = true;
        pose.Pos = pos;
        pose.Scale = scale;
        pose.Arm = arm;
        pose.Head = head;
        pose.Laser = laser;
        pose.Walk = walk;
        pose.Flags = flags;
        pose.Target = target;
        ApplyPose(guard, pose);
    }

    static void ApplyPose(GuardScript guard, GuardPose pose) {
        guard.transform.position = pose.Pos;
        guard.transform.localScale = new Vector3(Mathf.Abs(pose.Scale) < 0.01f ? 1f : pose.Scale, 1f, 1f);
        Animator anim = null;
        if (guard.transform.childCount > 0) {
            anim = guard.transform.GetChild(0).GetComponent<Animator>();
        }
        if ((pose.Flags & Dead) != 0) {
            if (!pose.Died) {
                pose.Died = true;
                if (anim != null) {
                    anim.enabled = true;
                    anim.Play("GuardFall");
                }
                Collider2D col = guard.GetComponent<Collider2D>();
                if (col != null) {
                    col.enabled = false;
                }
                if (guard.laserLine != null) {
                    guard.laserLine.enabled = false;
                }
                if (guard.laserDot != null) {
                    guard.laserDot.enabled = false;
                }
            }
            return;
        }
        if ((pose.Flags & Fired) != 0 && Time.time >= pose.ShotUntil && guard.barrel != null) {
            pose.ShotUntil = Time.time + 0.12f;
            Sound.Play("pistolshot", guard.transform.position);
            TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = true;
            try {
                TurretScript.Shoot(new FireInfo {
                    pos = guard.barrel.position,
                    dir = guard.barrel.right * Mathf.Sign(pose.Scale == 0f ? 1f : pose.Scale),
                    doTinnitus = false,
                    ignoreBody = false,
                    structureDamage = 90f,
                    animalDamage = 90f,
                    playerDamageMultiplier = 0.35f,
                    ignoreTrans = guard.transform,
                });
            } finally {
                TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = false;
            }
        }
        if ((pose.Flags & Melee) != 0 && Time.time >= pose.SwingUntil) {
            pose.SwingUntil = Time.time + 0.95f;
            if (anim != null) {
                anim.enabled = true;
                anim.Play("GuardSwing", -1, 0f);
            }
            if (NetBody.TryGetLocalNetBody(out NetBody me) && me.plr != null && me.body != null && me.plr.playerId.id == pose.Target) {
                me.body.Ragdoll();
            }
        }
        if (Time.time < pose.SwingUntil) {
            return;
        }
        if (guard.arms != null) {
            guard.arms.rotation = Quaternion.Euler(0f, 0f, pose.Arm);
        }
        if (guard.head != null) {
            guard.head.rotation = Quaternion.Euler(0f, 0f, pose.Head);
        }
        if (guard.laserLine != null) {
            guard.laserLine.transform.localScale = new Vector3(1f, pose.Laser, 1f);
            guard.laserLine.enabled = true;
        }
        if (guard.laserDot != null && guard.laserLine != null) {
            guard.laserDot.transform.localPosition = (Vector2)guard.laserLine.transform.localPosition + Vector2.right * pose.Laser;
            guard.laserDot.enabled = true;
        }
        if (guard.myBody != null) {
            if (pose.Walk == 255) {
                if (guard.bodyStaySprite != null) {
                    guard.myBody.sprite = guard.bodyStaySprite;
                }
            } else if (guard.bodyMoveSprites != null && pose.Walk < guard.bodyMoveSprites.Length) {
                guard.myBody.sprite = guard.bodyMoveSprites[pose.Walk];
            }
        }
        if (guard.armSprite != null) {
            guard.armSprite.sprite = (pose.Flags & Stunned) != 0 ? guard.armsStun : guard.armsNormal;
        }
        if (anim != null) {
            anim.enabled = false;
        }
    }
}
#endif
