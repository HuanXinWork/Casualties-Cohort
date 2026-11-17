#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版第 4/5 阶段世界状态与生态灾害联机桥接：
/// 负责第 10 层出口判定、世界修正器重力/地震/季节权威同步、义肢种类编码，以及风扇/吸血触手/地热口/冷气团/脓包同步。
/// </summary>
public static class FullgameWorldCompat {
    public static float HostGravityY = -55f;
    public static byte HostSeason;
    public static bool HostIsMercRun;
    public static bool HostCanStartMercRun;
    public static int HostEndlessLoops;

    public static bool IsAscentLayer() {
        return WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 10;
    }

    public static bool IsWwmrLayer() {
        return WorldGeneration.world != null && WorldGeneration.world.biomeDepth == 9;
    }

    public static bool IsAtLayerExit(Body body) {
        if (body == null || WorldGeneration.world == null) {
            return false;
        }
        if (IsWwmrLayer()) {
            return false;
        }
        float y = body.transform.position.y;
        uint half = WorldGeneration.world.halfHeight;
        if (IsAscentLayer()) {
            return y > (float)half - 5f;
        }
        return y < (float)(0L - (long)half) + 3.1f;
    }

    public static void WriteWorldExtras(NetDataWriter writer) {
        writer.Put(Physics2D.gravity.y);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.isMercRun);
        writer.Put(WorldGeneration.world != null && WorldGeneration.world.canStartMercRun);
        writer.Put(WorldGeneration.world != null ? WorldGeneration.world.endlessLoops : 0);
        writer.Put((byte)SeasonalManager.GetSeasonalEvent());
        writer.Put(WorldGeneration.world == null || WorldGeneration.world.allowEarthquakes);
    }

    public static void ReadWorldExtras(NetDataReader reader) {
        if (reader.EndOfData) {
            return;
        }
        reader.Get(out float gravityY);
        HostGravityY = gravityY;
        Physics2D.gravity = new Vector2(0f, gravityY);

        if (reader.EndOfData) {
            return;
        }
        reader.Get(out bool merc);
        reader.Get(out bool canMerc);
        reader.Get(out int endless);
        reader.Get(out byte season);
        bool allowQuakes = true;
        if (!reader.EndOfData) {
            reader.Get(out allowQuakes);
        }

        HostIsMercRun = merc;
        HostCanStartMercRun = canMerc;
        HostEndlessLoops = endless;
        HostSeason = season;

        if (WorldGeneration.world != null) {
            WorldGeneration.world.isMercRun = merc;
            WorldGeneration.world.canStartMercRun = canMerc;
            WorldGeneration.world.endlessLoops = endless;
            WorldGeneration.world.allowEarthquakes = allowQuakes;
        }
        WorldMP.earthquake_enabled = allowQuakes;
    }

    public static void ApplyModifierGravity(float gravityY) {
        HostGravityY = gravityY;
        Physics2D.gravity = new Vector2(0f, gravityY);
    }

    public static byte EncodeProsthetic(Limb limb) {
        if (limb == null || !limb.HasProsthetic()) {
            return 0;
        }
        string id = limb.prostheticInfo.itemId;
        if (id == "peglimb") {
            return 1;
        }
        if (id == "steelprosthesis") {
            return 2;
        }
        if (id == "peo") {
            return 3;
        }
        return 1;
    }

    public static void ApplyProstheticKind(Limb limb, byte kind) {
        if (limb == null) {
            return;
        }
        bool want = kind != 0;
        bool has = limb.HasProsthetic();
        if (!want) {
            if (has) {
                limb.prostheticInfo = default;
                if (limb.mat != null) {
                    limb.mat.SetTexture("_OverlayTex", null);
                }
            }
            return;
        }
        if (has && EncodeProsthetic(limb) == kind) {
            return;
        }
        Limb.ProstheticInfo info = kind switch {
            2 => SteelInfo(),
            3 => PeoInfo(),
            _ => Item.PegLimbInfo(),
        };
        limb.AttachProsthetic(info, isLoad: true);
    }

    static Limb.ProstheticInfo SteelInfo() {
        return new Limb.ProstheticInfo {
            hasProsthetic = true,
            itemId = "steelprosthesis",
            forceChange = 0f,
            armorChange = 0.2f,
            destroyItem = "scrapmetal",
            appearancePath = "Sprites/steelprosthesisoverlay",
            maxHoles = 15,
            r = 0.5f,
            g = 0.5f,
            b = 0.5f,
        };
    }

    static Limb.ProstheticInfo PeoInfo() {
        return new Limb.ProstheticInfo {
            hasProsthetic = true,
            itemId = "peo",
            forceChange = 0.25f,
            armorChange = 0.6f,
            destroyItem = "titaniumsheet",
            appearancePath = "Sprites/peooverlay",
            maxHoles = 20,
            r = 0.361f,
            g = 0.729f,
            b = 0.729f,
        };
    }
}

[HarmonyPatch(typeof(RichModifier), nameof(RichModifier.Initialize))]
static class RichModifier_Initialize_ClientSkip_MultiplayerPatch {
    static bool Prefix() {
        return !Net.IsRunning || Net.IsServer;
    }
}

[HarmonyPatch(typeof(TurbulentModifier), nameof(TurbulentModifier.Initialize))]
static class TurbulentModifier_Initialize_CaptureGravity_MultiplayerPatch {
    static void Postfix() {
        if (Net.IsRunning && Net.IsServer) {
            FullgameWorldCompat.HostGravityY = Physics2D.gravity.y;
        }
    }
}

[HarmonyPatch(typeof(TurbulentModifier), nameof(TurbulentModifier.Disable))]
static class TurbulentModifier_Disable_CaptureGravity_MultiplayerPatch {
    static void Postfix() {
        if (Net.IsRunning && Net.IsServer) {
            FullgameWorldCompat.HostGravityY = Physics2D.gravity.y;
        }
    }
}

[HarmonyPatch(typeof(StableModifier), nameof(StableModifier.Initialize))]
static class StableModifier_Initialize_EarthquakeFlag_MultiplayerPatch {
    static void Postfix() {
        if (Net.IsRunning) {
            WorldMP.earthquake_enabled = false;
        }
    }
}

[HarmonyPatch(typeof(StableModifier), nameof(StableModifier.Disable))]
static class StableModifier_Disable_EarthquakeFlag_MultiplayerPatch {
    static void Postfix() {
        if (Net.IsRunning) {
            WorldMP.earthquake_enabled = true;
        }
    }
}

[HarmonyPatch(typeof(SeasonalManager), nameof(SeasonalManager.GetSeasonalEvent))]
static class SeasonalManager_GetSeasonalEvent_HostAuthority_MultiplayerPatch {
    static bool Prefix(ref SeasonalManager.SeasonalEventType __result) {
        if (!Net.IsRunning || Net.IsServer) {
            return true;
        }
        __result = (SeasonalManager.SeasonalEventType)FullgameWorldCompat.HostSeason;
        return false;
    }
}

[HarmonyPatch(typeof(FanTrap), "OnWillRenderObject")]
static class FanTrap_OnWillRenderObject_HostSim_MultiplayerPatch {
    static bool Prefix() => !Net.IsRunning || Net.IsServer;
}

internal static class BloodSuckerClientVisual {
    internal static void Tick(BloodSuckerScript sucker) {
        if (sucker == null) {
            return;
        }
        Traverse tr = Traverse.Create(sucker);
        Limb limb = tr.Field("limb").GetValue<Limb>();
        if (limb == null) {
            return;
        }
        float suckTime = tr.Field("suckTime").GetValue<float>() - Time.deltaTime;
        tr.Field("suckTime").SetValue(suckTime);
        if (suckTime < 0f || limb.body == null
            || Vector2.Distance(limb.body.transform.position, sucker.transform.position) > 12f) {
            tr.Field("limb").SetValue(null);
        }
    }

    internal static void DrawTentacle(BloodSuckerScript sucker) {
        Traverse tr = Traverse.Create(sucker);
        LineRenderer line = tr.Field("line").GetValue<LineRenderer>();
        if (line == null) {
            return;
        }
        Limb limb = tr.Field("limb").GetValue<Limb>();
        Vector2 linePos = tr.Field("linePos").GetValue<Vector2>();
        int segments = sucker.segments;
        Vector2 target = limb != null
            ? (Vector2)sucker.transform.InverseTransformPoint(limb.transform.position)
            : Vector2.zero;
        linePos = Vector2.Lerp(linePos, target, Time.deltaTime * 5f * (limb != null ? 3.5f : 1f));
        tr.Field("linePos").SetValue(linePos);
        for (int i = 0; i < segments; i++) {
            float t01 = (float)i / (float)(segments - 1);
            line.SetPosition(i, Vector2.Lerp(Vector2.zero, linePos, t01));
        }
        if (limb != null) {
            AnimationCurve curve = new AnimationCurve();
            float wave = Time.time * (-sucker.waveSpeed);
            for (int k = 0; k < segments; k++) {
                float t01 = (float)k / (float)(segments - 1);
                float pulse = Mathf.Sin(t01 * Mathf.PI * 2f * sucker.waveFrequency - wave);
                float width = 1.6f + pulse * sucker.pulseStrength;
                curve.AddKey(t01, 0.6f * width);
            }
            line.widthCurve = curve;
        } else {
            line.widthCurve = new AnimationCurve(
                new Keyframe(0f, 0.6f),
                new Keyframe(0f, 0.6f));
        }
    }
}

[HarmonyPatch(typeof(BloodSuckerScript), "OnTriggerEnter2D")]
static class BloodSuckerScript_OnTriggerEnter2D_HostSim_MultiplayerPatch {
    static bool Prefix(BloodSuckerScript __instance, Collider2D collision) {
        if (!Net.IsRunning || Net.IsServer) {
            return true;
        }
        if (__instance == null || !Utils.GetBody(collision.gameObject, out Body body) || !Util.IsBodyLocal(body)) {
            return false;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out SyncInfo si) || si == null) {
            return false;
        }
        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
        writer.Put(CustomWireCommands.BloodsuckerTouch + " " + si.syncId.id.ToString());
        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        return false;
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.BloodsuckerTouch] = (plr, cmd, args) => {
            if (!Net.IsServer || plr == null || plr.body == null || args == null || args.Length < 2) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out BloodSuckerScript sucker) || sucker == null) {
                return;
            }
            float suckTime = Traverse.Create(sucker).Field("suckTime").GetValue<float>();
            if (suckTime >= -10f) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, sucker.transform.position) > 6f) {
                return;
            }
            Limb limb = null;
            if (plr.body.limbs != null) {
                for (int i = 0; i < plr.body.limbs.Length; i++) {
                    Limb candidate = plr.body.limbs[i];
                    if (candidate != null && !candidate.dismembered) {
                        limb = candidate;
                        break;
                    }
                }
            }
            if (limb == null) {
                return;
            }
            sucker.BeginSucc(limb);
        };
    }
}

[HarmonyPatch(typeof(BloodSuckerScript), nameof(BloodSuckerScript.BeginSucc))]
static class BloodSuckerScript_BeginSucc_Relay_MultiplayerPatch {
    static void Postfix(BloodSuckerScript __instance, Limb lm) {
        if (!Net.IsRunning || !Net.IsServer || lm == null || lm.body == null) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(__instance.gameObject, out SyncInfo si)) {
            return;
        }
        if (!lm.body.TryGetNetBody(out NetBody nb)) {
            return;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_BloodSuckerSucc);
        writer.Put(si.syncId);
        writer.Put(nb.netId);
        writer.Put(Util.GetLimbIndex(lm));
        Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BloodSuckerSucc, false)]
    static void Client_BloodSuckerSucc(knetid _, NetDataReader reader) {
        reader.Get(out knetid denId);
        reader.Get(out knetid bodyNetId);
        reader.Get(out byte limbIndex);
        if (!NetObjectRegistry.TryGetSyncInfo(denId, out SyncInfo si) || si.go == null) {
            return;
        }
        if (!si.go.TryGetComponent(out BloodSuckerScript sucker)) {
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(bodyNetId, out NetBody nb) || nb.body == null) {
            return;
        }
        if (limbIndex >= nb.body.limbs.Length) {
            return;
        }
        Limb lm = nb.body.limbs[limbIndex];
        if (lm == null || lm.dismembered) {
            return;
        }
        Traverse tr = Traverse.Create(sucker);
        tr.Field("limb").SetValue(lm);
        tr.Field("suckTime").SetValue(1.25f);
        lm.body.eyeScareTime = 2.5f;
        lm.body.Scream();
        lm.body.DoGoreSound();
    }
}

[HarmonyPatch(typeof(BloodSuckerScript), "OnWillRenderObject")]
static class BloodSuckerScript_OnWillRenderObject_HostDrain_MultiplayerPatch {
    static bool Prefix(BloodSuckerScript __instance) {
        if (!Net.IsRunning || Net.IsServer) {
            return true;
        }
        BloodSuckerClientVisual.DrawTentacle(__instance);
        return false;
    }
}

[HarmonyPatch(typeof(Heater), "OnWillRenderObject")]
static class Heater_OnWillRenderObject_AllBodies_MultiplayerPatch {
    static bool Prefix(Heater __instance) {
        if (!Net.IsRunning || __instance == null) {
            return true;
        }
        if (!Net.IsServer) {
            return false;
        }
        Traverse tr = Traverse.Create(__instance);
        float last = tr.Field("lastTime").GetValue<float>();
        if (Time.time - last <= 0.5f) {
            return false;
        }
        tr.Field("lastTime").SetValue(Time.time);
        float desired = __instance.desiredTemp;
        bool heating = desired >= 37f;
        float reach = __instance.maxDistance;
        if (reach <= 0f) {
            return false;
        }
        Vector2 pos = __instance.transform.position;
        foreach (NetBody nb in NetBody.GetBodiesInRadius(pos, reach)) {
            Body body = nb != null ? nb.body : null;
            if (body == null) {
                continue;
            }
            bool towards = (heating && body.temperature < desired) || (!heating && body.temperature > desired);
            if (!towards) {
                continue;
            }
            body.temperature = Mathf.Lerp(body.temperature, desired, __instance.tempLerpStrength);
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
        return false;
    }
}

[HarmonyPatch(typeof(ColdPuffScript), nameof(ColdPuffScript.Blow))]
static class ColdPuffScript_Blow_LocalBody_MultiplayerPatch {
    static bool Prefix(ColdPuffScript __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        return TrapClaim.ColdPuffBlowLocal(__instance);
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.ColdPuffBlow] = (plr, cmd, args) => {
            if (!Net.IsServer || args == null || args.Length < 2) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out ColdPuffScript puff) || puff == null) {
                return;
            }
            TrapClaim.ChillBodies(puff, puff.transform.position, 8f);
        };
    }
}

[HarmonyPatch(typeof(PusSackScript), "OnWillRenderObject")]
static class PusSackScript_OnWillRenderObject_HostSim_MultiplayerPatch {
    static bool Prefix() => true;
}
#endif
