#if FULLGAME
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

internal static class CeilingTurretHostSim {
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
        Publish(ct, gunAngle, fired, seeing);
    }

    struct SentAim {
        public float Angle;
        public float Next;
    }

    static readonly Dictionary<int, SentAim> Sent = new Dictionary<int, SentAim>();

    static void Publish(CeilingTurret ct, float angle, bool fired, bool seeing) {
        if (ct == null || ServerMain.AllClientIdsExceptHost == null || ServerMain.AllClientIdsExceptHost.Count == 0) {
            return;
        }
        int id = ct.GetInstanceID();
        bool known = Sent.TryGetValue(id, out SentAim prev);
        bool aimDue = fired || (seeing && (!known || Mathf.Abs(Mathf.DeltaAngle(prev.Angle, angle)) > 3f || Time.time >= prev.Next));
        if (!aimDue) {
            return;
        }
        Sent[id] = new SentAim { Angle = angle, Next = Time.time + (fired ? 0.05f : 0.2f) };
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.CeilingGun);
        writer.Put((Vector2)ct.transform.position);
        writer.Put(angle);
        writer.Put(fired);
        Net.Server_SendToClients(fired ? DeliveryMethod.ReliableUnordered : DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void ApplyRemote(Vector2 pos, float angle, bool fired) {
        CeilingTurret ct = FindNear(pos);
        if (ct == null) {
            return;
        }
        Pose(ct, angle);
        if (!fired || ct.lowerGun == null) {
            return;
        }
        Traverse.Create(ct).Field("timeSinceShot").SetValue(0f);
        if (ct.barrelAnimCurve != null) {
            ct.lowerGun.localPosition = new Vector2(0f, ct.barrelAnimCurve.Evaluate(0f));
        }
        Sound.Play("rifleshot", ct.lowerGun.position, true, false, null, 1f, 1f, false, false);
        if (WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 11 && PlayerCamera.main != null) {
            PlayerCamera.main.threatMusicTime = 2f;
        }
        TurretScript_Shoot_MultiplayerPatch.SuppressBodyDamage = true;
        try {
            TurretScript.Shoot(new FireInfo {
                pos = ct.lowerGun.position,
                dir = -ct.lowerGun.up,
                ignoreTrans = ct.transform,
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
        if (ct == null || ct.lowerGun == null || ct.barrelAnimCurve == null) {
            return;
        }
        Traverse tr = Traverse.Create(ct);
        float t = tr.Field("timeSinceShot").GetValue<float>() + Time.deltaTime;
        tr.Field("timeSinceShot").SetValue(t);
        ct.lowerGun.localPosition = new Vector2(0f, ct.barrelAnimCurve.Evaluate(t));
    }

    static CeilingTurret[] _cached;
    static int _cachedFrame = -1;

    static CeilingTurret FindNear(Vector2 pos) {
        if (_cached == null || Time.frameCount - _cachedFrame > 45) {
            _cached = UnityEngine.Object.FindObjectsByType<CeilingTurret>(FindObjectsSortMode.None);
            _cachedFrame = Time.frameCount;
        }
        CeilingTurret best = null;
        float bestSqr = 4f;
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

    [CTPostInit]
    static void InitGuns() {
        ClientMain.ClientCustomCommandsDict[CustomWireCommands.CeilingGun] = (cmd, reader) => {
            reader.Get(out Vector2 pos);
            reader.Get(out float angle);
            reader.Get(out bool fired);
            ApplyRemote(pos, angle, fired);
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
            if (body.transform.position.y >= ct.transform.position.y) {
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
/// 单人跑酷教程控制器在联机下应予屏蔽；新版原版已将 <c>MovementCourse.Start</c> 迁移为 <c>TutorialCourse.Sequence</c>，
/// 此处先通过 <see cref="AccessTools.Method(System.Type,string,System.Type[],System.Type[])"/> 判空，若目标方法不存在则安全跳过挂载，消除启动报错。
/// </summary>
[HarmonyPatch]
static class MovementCourse_Start_MultiplayerPatch {
    static MethodBase TargetMethod() {
        return AccessTools.Method(typeof(MovementCourse), "Start");
    }

    static bool Prepare() {
        bool exists = TargetMethod() != null;
        if (!exists) {
            ModLog.Debug(LogDomain.World, "MovementCourse.Start not found in current game build; skipping legacy tutorial patch.");
        }
        return exists;
    }

    static bool Prefix() => !Net.IsRunning;
}
#endif
