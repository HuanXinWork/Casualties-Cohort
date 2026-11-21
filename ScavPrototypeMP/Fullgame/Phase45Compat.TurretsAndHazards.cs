#if FULLGAME
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版第 4/5 阶段机械炮塔、激光无人机、活塞、科技球、义肢/货物健康同步与单人教程屏蔽补丁。
/// </summary>
[HarmonyPatch(typeof(LaserDroneScript), "Update")]
static class LaserDroneScript_Update_HostAuth_MultiplayerPatch {
    static bool Prefix(LaserDroneScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            TrapClaim.ClientCryoFollowHost(__instance);
            return false;
        }
        BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody();
        if (bgo.body == null && PlayerCamera.main != null) {
            bgo.body = PlayerCamera.main.body;
        }
        if (__instance.hidden && bgo.body != null
            && Vector2.Distance(__instance.transform.position, bgo.body.transform.position) < 10f) {
            __instance.Open();
        }
        return true;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(LaserDroneScript), "FixedUpdate")]
static class LaserDroneScript_FixedUpdate_HostSim_MultiplayerPatch {
    static bool Prefix(LaserDroneScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            return false;
        }
        BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
        bgo.SetNearestBody();
        if (bgo.body == null && PlayerCamera.main != null) {
            bgo.body = PlayerCamera.main.body;
        }
        return true;
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(LaserDroneScript), nameof(LaserDroneScript.FireShot))]
static class LaserDroneScript_FireShot_TrapClaim_MultiplayerPatch {
    static bool Prefix(LaserDroneScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            return false;
        }
        TrapClaim.HostCryoFire(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(LaserDroneScript), "OnWillRenderObject")]
static class LaserDroneScript_OnWillRenderObject_Bgo_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(LaserDroneScript), "OnCollisionEnter2D")]
static class LaserDroneScript_OnCollisionEnter2D_LocalOnly_MultiplayerPatch {
    static bool Prefix(Collision2D collision) {
        return !TrapClaim.SkipRemote(collision?.collider);
    }

    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator generator, MethodBase __originalMethod) {
        return BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
    }
}

[HarmonyPatch(typeof(PistonScript), "Update")]
static class PistonScript_Update_HostAuth_MultiplayerPatch {
    static bool Prefix(PistonScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient) {
            TrapClaim.ClientPistonFollowHost(__instance);
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CeilingTurret), "OnWillRenderObject")]
static class CeilingTurret_OnWillRenderObject_HostSim_MultiplayerPatch {
    static bool Prefix() => !Net.IsRunning;
}

internal sealed class CeilingTurretClient : MonoBehaviour {
    public bool Seeing;
    public float TimeSaw;
}

internal static class CeilingTurretHostSim {
    private struct SentAim {
        public float Angle;
        public float Next;
        public bool Seeing;
    }

    private struct MoveSample {
        public Vector2 Pos;
        public float Time;
        public bool Fast;
    }

    static readonly Dictionary<int, SentAim> Sent = new Dictionary<int, SentAim>();
    static int _sentPruneAt = StaleIds.FirstThreshold;
    static CeilingTurret[] _cached;
    static int _cachedFrame = -1;
    static readonly Dictionary<int, MoveSample> _moveSample = new Dictionary<int, MoveSample>();
    static int _movePruneAt = StaleIds.FirstThreshold;

    internal static void Tick(CeilingTurret ct) {
        if (ct == null) {
            return;
        }
        Traverse tr = Traverse.Create(ct);
        float timeSinceShot = tr.Field("timeSinceShot").GetValue<float>() + Time.deltaTime;
        tr.Field("timeSinceShot").SetValue(timeSinceShot);
        Transform lowerGun = ct.lowerGun;
        if (lowerGun != null && ct.barrelAnimCurve != null) {
            lowerGun.localPosition = new Vector2(0f, ct.barrelAnimCurve.Evaluate(timeSinceShot));
        }
        Body target = PickTarget(ct, out bool seeing);
        float timeSaw = tr.Field("timeSawMovement").GetValue<float>();
        float desired = tr.Field("desiredAngle").GetValue<float>();
        float gunVelocity = tr.Field("gunVelocity").GetValue<float>();
        float gunAngle = tr.Field("gunAngle").GetValue<float>();
        AudioSource source = tr.Field("source").GetValue<AudioSource>();
        if (ct.alwaysSee && ct.openLight != null) {
            ct.openLight.intensity = Mathf.PingPong(Time.time * 10f, 1f) * timeSaw * 5f;
        }
        if (seeing && target != null) {
            Vector2 toBody = target.transform.position - ct.transform.position;
            desired = Vector2.SignedAngle(Vector2.down, toBody.normalized);
            if (ct.lights != null) {
                ct.lights.enabled = true;
            }
            timeSaw += Time.deltaTime * (ct.alwaysSee ? 0.3f : 1f);
            if (WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11
                && PlayerCamera.main != null) {
                PlayerCamera.main.threatMusicTime = 2f;
            }
            if (source != null) {
                source.volume = Mathf.MoveTowards(source.volume, 1f, Time.deltaTime * 3f);
            }
        } else {
            if (!ct.alwaysSee) {
                timeSaw = 0f;
            }
            if (ct.lights != null) {
                ct.lights.enabled = Mathf.PingPong(Time.time, 0.2f) > 0.1f;
            }
            if (source != null) {
                source.volume = Mathf.MoveTowards(source.volume, 0f, Time.deltaTime * 3f);
            }
        }
        if (source != null) {
            if (source.volume > 0f && !source.isPlaying) {
                source.Play();
            }
            if (source.volume <= 0f && source.isPlaying) {
                source.Stop();
            }
            source.pitch = 0.5f + timeSaw * 1.5f;
        }
        float delta = Mathf.Clamp(desired - gunAngle, -30f, 30f);
        gunVelocity += delta * Time.deltaTime * 15f;
        gunVelocity *= 1f / (1f + Time.deltaTime);
        gunAngle += gunVelocity * Time.deltaTime;
        gunAngle = Mathf.Clamp(gunAngle, -80f, 80f);
        if (ct.upperGun != null) {
            ct.upperGun.eulerAngles = new Vector3(0f, 0f, gunAngle);
            ct.upperGun.localPosition = ct.upperGun.up * -0.75f;
        }
        tr.Field("desiredAngle").SetValue(desired);
        tr.Field("gunVelocity").SetValue(gunVelocity);
        tr.Field("gunAngle").SetValue(gunAngle);
        bool fired = false;
        if (seeing && target != null && timeSaw > 0.7f && target.conscious) {
            timeSaw = 0f;
            tr.Field("timeSinceShot").SetValue(0f);
            fired = true;
            if (lowerGun != null) {
                Sound.Play("rifleshot", lowerGun.position, true, false, null, 1f, 1f, false, false);
                TurretScript.Shoot(new FireInfo {
                    pos = lowerGun.position,
                    dir = -lowerGun.up,
                    ignoreTrans = ct.transform,
                    playerDamageMultiplier = 1f,
                });
            }
        }
        tr.Field("timeSawMovement").SetValue(timeSaw);
        Vector2 shotPos = lowerGun != null ? (Vector2)lowerGun.position : (Vector2)ct.transform.position;
        Vector2 shotDir = lowerGun != null ? (Vector2)(-lowerGun.up) : Vector2.down;
        Publish(ct, gunAngle, fired, seeing, timeSaw, shotPos, shotDir);
    }

    static void Publish(CeilingTurret ct, float angle, bool fired, bool seeing, float timeSaw, Vector2 shotPos, Vector2 shotDir) {
        if (ct == null || ServerMain.AllClientIdsExceptHost == null || ServerMain.AllClientIdsExceptHost.Count == 0) {
            return;
        }
        int id = ct.GetInstanceID();
        bool known = Sent.TryGetValue(id, out SentAim prev);
        bool lostSight = known && prev.Seeing && !seeing;
        bool aimDue = fired || lostSight || (seeing && (!known || Mathf.Abs(Mathf.DeltaAngle(prev.Angle, angle)) > 3f || Time.time >= prev.Next));
        if (!aimDue) {
            return;
        }
        StaleIds.Prune<CeilingTurret, SentAim>(Sent, ref _sentPruneAt);
        Sent[id] = new SentAim {
            Angle = angle,
            Next = Time.time + (fired ? 0.05f : 0.2f),
            Seeing = seeing,
        };
        ushort syncId = 0;
        if (NetObjectRegistry.TryGetSyncInfo(ct.gameObject, out SyncInfo si) && si != null) {
            syncId = si.syncId;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.CeilingGun);
        writer.Put(syncId);
        writer.Put((Vector2)ct.transform.position);
        writer.Put(angle);
        writer.Put(seeing);
        writer.Put(timeSaw);
        writer.Put(fired);
        if (fired) {
            writer.Put(shotPos);
            writer.Put(shotDir);
        }
        Net.Server_SendToClients(fired ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void ApplyRemote(ushort syncId, Vector2 pos, float angle, bool seeing, float timeSaw, bool fired, Vector2 shotPos, Vector2 shotDir) {
        CeilingTurret ct = FindById(syncId) ?? FindNear(pos);
        if (ct != null) {
            Pose(ct, angle);
            CeilingTurretClient clientState = ct.GetComponent<CeilingTurretClient>();
            if (clientState == null) {
                clientState = ct.gameObject.AddComponent<CeilingTurretClient>();
            }
            clientState.Seeing = seeing;
            clientState.TimeSaw = timeSaw;
            if (fired) {
                Traverse.Create(ct).Field("timeSinceShot").SetValue(0f);
                if (ct.lowerGun != null && ct.barrelAnimCurve != null) {
                    ct.lowerGun.localPosition = new Vector2(0f, ct.barrelAnimCurve.Evaluate(0f));
                }
            }
        }
        if (!fired) {
            return;
        }
        if (shotDir.sqrMagnitude < 0.01f) {
            shotDir = Vector2.down;
        }
        Sound.Play("rifleshot", shotPos, true, false);
        if (WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11 && PlayerCamera.main != null) {
            PlayerCamera.main.threatMusicTime = 2f;
        }
        TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = true;
        try {
            TurretScript.Shoot(new FireInfo {
                pos = shotPos,
                dir = shotDir,
                ignoreTrans = ct != null ? ct.transform : null,
                playerDamageMultiplier = 1f,
            });
        } finally {
            TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = false;
        }
    }

    static void Pose(CeilingTurret ct, float angle) {
        Traverse.Create(ct).Field("gunAngle").SetValue(angle);
        if (ct.upperGun == null) {
            return;
        }
        ct.upperGun.eulerAngles = new Vector3(0f, 0f, angle);
        ct.upperGun.localPosition = ct.upperGun.up * -0.75f;
    }

    internal static void ClientBarrel(CeilingTurret ct) {
        if (ct == null) {
            return;
        }
        CeilingTurretClient clientState = ct.GetComponent<CeilingTurretClient>();
        float timeSaw = clientState != null ? clientState.TimeSaw : 0f;
        bool seeing = clientState != null && clientState.Seeing;
        if (ct.upperGun != null) {
            float angle = Traverse.Create(ct).Field("gunAngle").GetValue<float>();
            ct.upperGun.eulerAngles = new Vector3(0f, 0f, angle);
            ct.upperGun.localPosition = ct.upperGun.up * -0.75f;
        }
        if (ct.lowerGun != null && ct.barrelAnimCurve != null) {
            Traverse tr = Traverse.Create(ct);
            float t = tr.Field("timeSinceShot").GetValue<float>() + Time.deltaTime;
            tr.Field("timeSinceShot").SetValue(t);
            ct.lowerGun.localPosition = new Vector2(0f, ct.barrelAnimCurve.Evaluate(t));
        }
        if (ct.alwaysSee && ct.openLight != null) {
            ct.openLight.intensity = Mathf.PingPong(Time.time * 10f, 1f) * timeSaw * 5f;
        }
        if (ct.lights != null) {
            ct.lights.enabled = seeing || Mathf.PingPong(Time.time, 0.2f) > 0.1f;
        }
        AudioSource source = Traverse.Create(ct).Field("source").GetValue<AudioSource>();
        if (source != null) {
            source.volume = Mathf.MoveTowards(source.volume, seeing ? 1f : 0f, Time.deltaTime * 3f);
            if (source.volume > 0f && !source.isPlaying) {
                source.Play();
            }
            if (source.volume <= 0f && source.isPlaying) {
                source.Stop();
            }
            source.pitch = 0.5f + timeSaw * 1.5f;
        }
    }

    static CeilingTurret FindNear(Vector2 pos) {
        if (_cached == null || Time.frameCount - _cachedFrame > 45) {
            _cached = UnityEngine.Object.FindObjectsByType<CeilingTurret>(FindObjectsSortMode.None);
            _cachedFrame = Time.frameCount;
        }
        CeilingTurret best = null;
        float bestSqr = 64f;
        for (int i = 0; i < _cached.Length; i++) {
            CeilingTurret ct = _cached[i];
            if (ct == null) {
                continue;
            }
            float sqr = ((Vector2)ct.transform.position - pos).sqrMagnitude;
            if (sqr < bestSqr) {
                bestSqr = sqr;
                best = ct;
            }
        }
        return best;
    }

    static CeilingTurret FindById(ushort syncId) {
        if (syncId == 0 || !NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
            return null;
        }
        CeilingTurret ct = si.go.GetComponent<CeilingTurret>();
        if (ct == null) {
            ct = si.go.GetComponentInChildren<CeilingTurret>(includeInactive: true);
        }
        return ct;
    }

    [CTPostInit]
    static void InitGuns() {
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.CeilingGun] = (cmd, reader) => {
            reader.Get(out ushort syncId);
            reader.Get(out Vector2 pos);
            reader.Get(out float angle);
            reader.Get(out bool seeing);
            reader.Get(out float timeSaw);
            reader.Get(out bool fired);
            Vector2 shotPos = pos;
            Vector2 shotDir = Vector2.down;
            if (fired) {
                reader.Get(out shotPos);
                reader.Get(out shotDir);
            }
            ApplyRemote(syncId, pos, angle, seeing, timeSaw, fired, shotPos, shotDir);
        };
    }

    static Body PickTarget(CeilingTurret ct, out bool seeing) {
        seeing = false;
        Body best = null;
        float bestSqr = float.MaxValue;
        int mask = PhysicsLayers.GroundMask;
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null) {
                continue;
            }
            Body body = plr.body;
            if (body.transform.position.y >= ct.transform.position.y || (!ct.alwaysSee && !MovingFast(body))) {
                continue;
            }
            bool clear = true;
            RaycastHit2D[] hits = Physics2D.LinecastAll(ct.transform.position, body.transform.position, mask);
            for (int i = 0; i < hits.Length; i++) {
                if (hits[i].transform != ct.transform) {
                    clear = false;
                    break;
                }
            }
            if (!clear) {
                continue;
            }
            Vector2 delta = (Vector2)body.transform.position - (Vector2)ct.transform.position;
            float sqr = delta.sqrMagnitude;
            if (sqr < bestSqr) {
                bestSqr = sqr;
                best = body;
            }
        }
        seeing = best != null;
        return best;
    }

    static bool MovingFast(Body body) {
        if (body == null) {
            return false;
        }
        int id = body.GetInstanceID();
        StaleIds.Prune<Body, MoveSample>(_moveSample, ref _movePruneAt);
        Vector2 pos = body.transform.position;
        float now = Time.time;
        if (body.grounded && body.moveDir.sqrMagnitude < 0.04f) {
            _moveSample[id] = new MoveSample { Pos = pos, Time = now, Fast = false };
            return false;
        }
        if (!_moveSample.TryGetValue(id, out MoveSample prev)) {
            _moveSample[id] = new MoveSample { Pos = pos, Time = now, Fast = false };
            return false;
        }
        float dt = now - prev.Time;
        if (dt < 0.25f) {
            return prev.Fast;
        }
        bool fast = (pos - prev.Pos).magnitude > 15f * dt;
        _moveSample[id] = new MoveSample { Pos = pos, Time = now, Fast = fast };
        return fast;
    }
}

[HarmonyPatch(typeof(CeilingTurret), "Update")]
static class CeilingTurret_Update_HostSim_MultiplayerPatch {
    static bool Prefix(CeilingTurret __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (!Net.IsServer) {
            CeilingTurretHostSim.ClientBarrel(__instance);
            return false;
        }
        CeilingTurretHostSim.Tick(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(TechballScript), "Update")]
static class TechballScript_Update_HostSim_MultiplayerPatch {
    static bool Prefix() => !Net.IsRunning || Net.IsServer;
}

[HarmonyPatch(typeof(Body), nameof(Body.EmbedCargo))]
static class Body_EmbedCargo_MultiplayerPatch {
    static void Postfix(Body __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null) {
            return;
        }
        if (__instance.TryGetNetBody(out NetBody nb)) {
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
    }
}

[HarmonyPatch(typeof(Body), nameof(Body.RemoveCargo))]
static class Body_RemoveCargo_MultiplayerPatch {
    static void Postfix(Body __instance) {
        if (!Net.IsRunning || __instance == null) {
            return;
        }
        MacroFleshRebuilderMp.NoteLocalCargoRemoved(__instance);
        if (!Net.IsServer) {
            return;
        }
        if (__instance.TryGetNetBody(out NetBody nb)) {
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
    }
}

[HarmonyPatch(typeof(Limb), nameof(Limb.AttachProsthetic))]
static class Limb_AttachProsthetic_HealthPush_MultiplayerPatch {
    static void Postfix(Limb __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.body == null) {
            return;
        }
        if (__instance.body.TryGetNetBody(out NetBody nb)) {
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
    }
}

/// <summary>
/// 联机大厅下屏蔽单人跑酷教程控制器协程。
/// Fullgame v1.0.2 已将入口从 <c>Start</c> 迁移为 <see cref="MovementCourse.Sequence"/>；
/// 拦截返回 <see cref="IEnumerator"/> 的协程方法时必须填充非空空枚举器，防止宿主 <c>StartCoroutine(null)</c> 抛出异常。
/// </summary>
[HarmonyPatch(typeof(MovementCourse), nameof(MovementCourse.Sequence))]
static class MovementCourse_Start_MultiplayerPatch {
    private static IEnumerator EmptyCoroutine() {
        yield break;
    }

    static bool Prefix(ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = EmptyCoroutine();
        return false;
    }
}
#endif
