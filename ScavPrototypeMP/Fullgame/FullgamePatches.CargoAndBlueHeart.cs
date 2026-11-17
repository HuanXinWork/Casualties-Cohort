#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using UnityEngine;

namespace Together;

/// <summary>
/// 高功率钻井舱（<see cref="CargoDrillPod"/>）与第 10 层蓝色心脏（<see cref="BlueHeart"/>）联机同步与演出控制器。
/// </summary>
[HarmonyPatch]
static class CargoDrillPod_MultiplayerPatch {
    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.OnUse))]
    static bool StartPrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        NetDataWriter writer = Net.CreateWriter(NetmsgId.SERVER_CargoDrillPodOnUse);
        writer.Put(AnyObjectNetId.GetNetIdFromGameObject(__instance.gameObject));
        Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(typeof(CargoDrillPod), nameof(CargoDrillPod.Update))]
    static bool UpdatePrefix(CargoDrillPod __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        try {
            TryMarkReady(__instance);
        } catch (Exception ex) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] Update ready check failed: " + ex.Message);
            if (__instance != null && !__instance.didReady && IsCargoUnlocked()) {
                __instance.didReady = true;
            }
        }
        return false;
    }

    static bool IsCargoUnlocked() {
        if (BlueHeartMp.CargoUnlocked) {
            return true;
        }
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr != null && plr.body != null && plr.body.hasCargo) {
                return true;
            }
        }
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null && kv.Key.hasCargo) {
                return true;
            }
        }
        return false;
    }

    static void TryMarkReady(CargoDrillPod drill) {
        if (drill == null || drill.didReady || !IsCargoUnlocked()) {
            return;
        }
        drill.didReady = true;
        BlueHeartMp.CargoUnlocked = true;
        if (drill.bld == null) {
            drill.bld = drill.GetComponent<BuildingEntity>();
        }
        try {
            UsableObject usable = drill.GetComponent<UsableObject>();
            if (usable != null) {
                usable.toggleString = Locale.GetOther("finaltravel");
            }
        } catch (Exception ex) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] toggleString failed: " + ex.Message);
        }
        try {
            if (drill.bld != null) {
                drill.bld.description = Locale.GetBuilding("highpowereddrillpoddsc2");
            }
        } catch (Exception ex) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] description failed: " + ex.Message);
        }
        try {
            GameObject tesla = Utils.Create("Special/Tesla", drill.transform);
            if (tesla != null) {
                tesla.transform.localScale = Vector3.one * 2f;
            }
        } catch (Exception ex) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] Tesla create failed: " + ex.Message);
        }
    }

    static void RealOnUse(Body b, CargoDrillPod drill) {
        if (drill == null) {
            return;
        }
        try {
            TryMarkReady(drill);
        } catch (Exception ex) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] RealOnUse ready failed: " + ex.Message);
            if (!drill.didReady && IsCargoUnlocked()) {
                drill.didReady = true;
            }
        }
        if (!drill.didReady) {
            if (b != null && b.talker != null) {
                b.talker.Talk(Locale.GetCharacter("endpodexamine"));
            }
            return;
        }
        if (drill.started) {
            return;
        }
        if (WorldGeneration.world == null) {
            ModLog.Warn(LogDomain.Story, "[CargoDrill] ready but WorldGeneration.world is null");
            return;
        }
        drill.started = true;
        try {
            WorldGeneration.world.StartCoroutine("RegenerateWorld", false);
            Sound.Play("drillpoduse", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true);
        } catch (Exception ex) {
            drill.started = false;
            ModLog.Exception(LogDomain.Story, "[CargoDrill] start regenerate failed", ex);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_CargoDrillPodOnUse)]
    static void Server_CargoDrillPodOnUse(knetid clientId, NetDataReader reader) {
        reader.Get(out AnyObjectNetId aoni);
        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AliveInWorld, LogDomain.Story, (byte)NetmsgId.SERVER_CargoDrillPodOnUse, out VerifiedCaller caller)) {
            return;
        }
        CargoDrillPod drill = null;
        if (aoni.TryGetGameObject(out GameObject go) && go != null) {
            go.TryGetComponent(out drill);
        }
        if (drill == null) {
            drill = UnityEngine.Object.FindObjectOfType<CargoDrillPod>();
        }
        if (drill != null) {
            if (!NetGuard.IsWithinDistance(in caller, drill.transform.position, GameplayRanges.ServerExchangeToleranceDistance)) {
                MpFlightRecorder.Record(LogDomain.Security, FlightEventKind.GuardDenied, (ushort)clientId, detailCode: (int)NetmsgId.SERVER_CargoDrillPodOnUse);
                return;
            }
            RealOnUse(caller.Body, drill);
        } else {
            ModLog.Warn(LogDomain.Story, $"[CargoDrill] use from {clientId} but no highpowereddrillpod");
        }
    }
}

static class BlueHeartMp {
    public const float TriggerRange = 10f;
    public const float NearbyRange = 48f;
    public static bool ClientCutsceneFromHost;
    public static bool CargoUnlocked;
    static readonly HashSet<Body> _affected = new HashSet<Body>();
    static readonly HashSet<int> _cargoEnsuredBodies = new HashSet<int>();
    static float _nextEnsureAll;

    public static List<Body> CollectNearby(Vector2 heartPos, float range) {
        List<Body> list = new List<Body>();
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            Body body = kv.Key;
            if (body == null || !body.alive) {
                continue;
            }
            if (Vector2.Distance(body.transform.position, heartPos) <= range) {
                list.Add(body);
            }
        }
        return list;
    }

    public static List<Body> CollectAllBodies() {
        List<Body> list = new List<Body>();
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            Body body = kv.Key;
            if (body != null && !list.Contains(body)) {
                list.Add(body);
            }
        }
        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            if (plr == null || plr.body == null || list.Contains(plr.body)) {
                continue;
            }
            list.Add(plr.body);
        }
        return list;
    }

    public static Body FindTriggerBody(Vector2 heartPos) {
        Body best = null;
        float bestDist = TriggerRange;
        foreach (Body body in CollectNearby(heartPos, TriggerRange)) {
            if (!body.standing) {
                continue;
            }
            float d = Vector2.Distance(body.transform.position, heartPos);
            if (d < bestDist) {
                bestDist = d;
                best = body;
            }
        }
        return best;
    }

    public static void HostTeleportNearby(BlueHeart heart, List<Body> nearby) {
        _affected.Clear();
        Vector2 heartPos = heart.transform.position;
        int i = 0;
        foreach (Body body in nearby) {
            if (body == null) {
                continue;
            }
            _affected.Add(body);
            float angle = (i / (float)Mathf.Max(1, nearby.Count)) * Mathf.PI * 2f;
            Vector2 dest = heartPos + new Vector2(Mathf.Cos(angle), Mathf.Sin(angle)) * 2.5f;
            body.forceNoMovement = true;
            if (body.TryGetNetBody(out NetBody nb)) {
                nb.SetBodyPosition(dest);
                body.SetVelocity(Vector2.zero);
                nb.Server_RemindPlayersCurrentState(true);
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            } else {
                body.transform.position = dest;
            }
            i++;
        }
    }

    public static void HostAnnounceCutscene() {
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_BlueHeartCutscene);
        writer.Put(true);
        Net.Server_SendToClientsVeryReliable(writer, ServerMain.AllClientIdsExceptHost);
    }

    public static void ApplyCargoEffectsNoSfx(Body body) {
        if (body == null || body.hasCargo) {
            return;
        }
        body.hasCargo = true;
        if (body.cargo != null) {
            body.cargo.SetActive(true);
        }
        Limb[] limbs = body.limbs;
        for (int i = 0; i < limbs.Length; i++) {
            limbs[i].bleedAmount *= 0.2f;
        }
        body.internalBleeding *= 0.2f;
        body.radiationSickness *= 0.25f;
        const int lowerTorsoIdx = (int)LimbIndex.LowerTorso;
        if (limbs.Length > lowerTorsoIdx && limbs[lowerTorsoIdx] != null) {
            limbs[lowerTorsoIdx].muscleHealth = 20f;
            limbs[lowerTorsoIdx].skinHealth = 20f;
            limbs[lowerTorsoIdx].pain += 80f;
            limbs[lowerTorsoIdx].bleedAmount += 15f;
        }
        body.Ragdoll();
        body.shock = 50f;
    }

    public static void HostEmbedAffected(Body speaker, Vector2 gorePos) {
        Sound.Play(VanillaAssetKeys.SoundGore, gorePos, twoDimensional: false, pitchShift: false);
        Body talkBody = (speaker != null && _affected.Contains(speaker) && speaker.alive) ? speaker : null;
        int embedded = 0;
        foreach (Body body in _affected) {
            if (body == null || body.hasCargo) {
                continue;
            }
            ApplyCargoEffectsNoSfx(body);
            embedded++;
            if (talkBody == null && body.alive) {
                talkBody = body;
            }
            if (body.TryGetNetBody(out NetBody nb)) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
        }
        if (embedded > 0 || AnyBodyHasCargo()) {
            CargoUnlocked = true;
        }
        if (talkBody != null && talkBody.talker != null) {
            talkBody.talker.TalkDelayed(3f, Locale.GetCharacter("cargoimpale"), null, force: false, resetTalkTimer: true, 9);
            talkBody.talker.TalkDelayed(11f, Locale.GetCharacter("cargoimpale2"), null, force: false, resetTalkTimer: true, 9);
            talkBody.talker.TalkDelayed(20f, Locale.GetCharacter("cargoimpale3"), null, force: false, resetTalkTimer: true, 9);
        }
        _affected.Clear();
    }

    static bool AnyBodyHasCargo() {
        foreach (Body body in BodyToPlayerSafe()) {
            if (body != null && body.hasCargo) {
                return true;
            }
        }
        return false;
    }

    static IEnumerable<Body> BodyToPlayerSafe() {
        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null) {
                yield return kv.Key;
            }
        }
    }

    public static bool IsProtectedCargoDrillBuilding(BuildingEntity be) {
        if (be == null) {
            return false;
        }
        if (be.GetComponent<CargoDrillPod>() != null) {
            return true;
        }
        string id = be.id;
        if (string.IsNullOrEmpty(id)) {
            return false;
        }
        if (id == "highpowereddrillpod") {
            return true;
        }
        return id.StartsWith("highpowereddrillpod", StringComparison.OrdinalIgnoreCase);
    }

    public static void Server_TryApplyCargoToLateJoiner(ScavPlayer plr) {
        Server_EnsureCargoOnAscentLoad(plr);
    }

    public static void Server_EnsureCargoOnAscentLoad(ScavPlayer plr) {
        if (!Net.IsServer || plr == null || plr.body == null) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10 || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (WorldGeneration.GetRunSettingBool("endless")) {
            return;
        }

        Body body = plr.body;
        int bodyId = body.GetInstanceID();
        if (MacroFleshRebuilderMp.HasRemovedHeart(plr.playerId)) {
            _cargoEnsuredBodies.Add(bodyId);
            return;
        }
        if (body.hasCargo) {
            _cargoEnsuredBodies.Add(bodyId);
            CargoUnlocked = true;
            return;
        }

        if (!_cargoEnsuredBodies.Add(bodyId)) {
            return;
        }
        const int lowerTorsoIdx = (int)LimbIndex.LowerTorso;
        if (body.cargo == null || body.limbs == null || body.limbs.Length <= lowerTorsoIdx) {
            _cargoEnsuredBodies.Remove(bodyId);
            return;
        }

        ApplyCargoEffectsNoSfx(body);
        CargoUnlocked = true;
        if (body.TryGetNetBody(out NetBody nb)) {
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
        ModLog.Info(LogDomain.Story, $"[BlueHeart] ascent load-in cargo applied to {plr}");
    }

    public static void Server_EnsureAllMissingCargoOnAscent() {
        if (!Net.IsServer) {
            return;
        }
        if (WorldGeneration.world == null || WorldGeneration.world.biomeDepth != 10
            || WorldGeneration.world.generatingWorld) {
            return;
        }
        if (Time.unscaledTime < _nextEnsureAll) {
            return;
        }
        _nextEnsureAll = Time.unscaledTime + 1f;

        foreach (ScavPlayer plr in ScavPlayer.AllPlayers) {
            Server_EnsureCargoOnAscentLoad(plr);
        }
    }

    public static void ResetCargoEnsureState() {
        _cargoEnsuredBodies.Clear();
        _nextEnsureAll = 0f;
    }
}

[HarmonyPatch(typeof(BlueHeart))]
static class BlueHeart_MultiplayerPatch {
    static float _nextPulseSend;
    static ushort _pulseSerial;
    static ushort _sendSeq;
    static float _clientTime;
    static float _clientRate = 1f;
    static float _clientRecvAt;
    static ushort _clientSerial;
    static ushort _clientSeq;
    static bool _clientHasPulse;
    static bool _clientHeartbeat;
    static Vector2 _clientHeartPos;
    static bool _clientHasHeartPos;

    static void HostSendPulse(float timeSincePulsed, float rate, bool pulsed, Vector2 heartPos) {
        if (!Net.IsServer) {
            return;
        }
        if (pulsed) {
            _pulseSerial++;
        } else if (Time.time < _nextPulseSend) {
            return;
        }
        _nextPulseSend = Time.time + 0.1f;
        NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_BlueHeartPulse);
        writer.Put(timeSincePulsed);
        writer.Put(rate);
        writer.Put(_pulseSerial);
        writer.Put(++_sendSeq);
        Vector2 posCopy = heartPos;
        writer.Put(posCopy.x);
        writer.Put(posCopy.y);
        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueHeartPulse)]
    static void Client_BlueHeartPulse(knetid _, NetDataReader reader) {
        reader.Get(out float timeSincePulsed);
        reader.Get(out float rate);
        reader.Get(out ushort serial);
        reader.Get(out ushort seq);
        reader.Get(out float heartX);
        reader.Get(out float heartY);
        if (_clientHasPulse) {
            int ahead = (short)(seq - _clientSeq);
            if (ahead <= 0) {
                return;
            }
            if (serial != _clientSerial) {
                _clientHeartbeat = true;
            }
        }
        _clientHasPulse = true;
        _clientTime = timeSincePulsed;
        _clientRate = rate;
        _clientRecvAt = Time.time;
        _clientSerial = serial;
        _clientSeq = seq;
        _clientHeartPos = new Vector2(heartX, heartY);
        _clientHasHeartPos = true;
    }

    [HarmonyPrefix]
    [HarmonyPatch("Update")]
    static bool UpdatePrefix(BlueHeart __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsServer) {
            BlueHeartMp.Server_EnsureAllMissingCargoOnAscent();
        }
        if (WorldGeneration.world != null && WorldGeneration.world.generatingWorld) {
            return false;
        }

        Traverse tr = Traverse.Create(__instance);
        if (!tr.Field<bool>("didTrapRemoval").Value) {
            tr.Field<bool>("didTrapRemoval").Value = true;
            Collider2D[] hits = Physics2D.OverlapAreaAll(
                __instance.transform.position + new Vector3(-64f, -64f),
                __instance.transform.position + new Vector3(32f, 32f),
                PhysicsLayers.GroundMask);
            for (int i = 0; i < hits.Length; i++) {
                if (hits[i].TryGetComponent(out BuildingEntity be) && !BlueHeartMp.IsProtectedCargoDrillBuilding(be)) {
                    UnityEngine.Object.Destroy(be.gameObject);
                }
            }
        }

        bool cutsceneStarted = tr.Field<bool>("cutsceneStarted").Value;
        if (!cutsceneStarted && Net.IsServer) {
            Body trigger = BlueHeartMp.FindTriggerBody(__instance.transform.position);
            if (trigger != null) {
                tr.Field<bool>("cutsceneStarted").Value = true;
                List<Body> nearby = BlueHeartMp.CollectAllBodies();
                if (nearby.Count == 0) {
                    nearby.Add(trigger);
                }
                BlueHeartMp.HostTeleportNearby(__instance, nearby);
                BlueHeartMp.HostAnnounceCutscene();
                PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
                __instance.StartCoroutine(__instance.Cutscene());
                if (!WorldGeneration.GetRunSettingBool("endless") && trigger.talker != null) {
                    trigger.talker.Talk(Locale.GetCharacter("cargo2"));
                }
                Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            }
        }

        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local == null) {
            return false;
        }
        Vector2 origPos = tr.Field<Vector2>("origPos").Value;
        float pulseSpeedAdd = tr.Field<float>("pulseSpeedAdd").Value;
        float timeSincePulsed = tr.Field<float>("timeSincePulsed").Value;
        float pulseTime = tr.Field<float>("pulseTime").Value;
        bool wasClose = tr.Field<bool>("wasClose").Value;
        Material mat = tr.Field<Material>("mat").Value;

        float num = Vector2.Distance(__instance.transform.position, local.transform.position) / 500f;
        float num2 = Mathf.Max(local.transform.position.y - __instance.transform.position.y, 0f) / 500f;
        if (num < 0.075f) {
            PlayerCamera.main.customLookPos = Vector2.Lerp(__instance.transform.position + Vector3.up * 3f, local.transform.position, num / 0.075f);
        }
        if (!wasClose && num < 0.06f) {
            tr.Field<bool>("wasClose").Value = true;
            local.happiness += 5f;
            if (!WorldGeneration.GetRunSettingBool("endless") && !cutsceneStarted && !BlueHeartMp.ClientCutsceneFromHost) {
                local.talker.Talk(Locale.GetCharacter("cargo1"));
            }
        }
        if (MusicManager.main != null) {
            MusicManager.main.cutsceneVolMult = Mathf.Clamp01((num - 0.1f) * 30f);
        }
        float num3 = 1f + pulseSpeedAdd;
        if (num < 0.5f) {
            num3 += (0.5f - num) * 6f;
        }
        float shownTime;
        if (Net.IsServer) {
            timeSincePulsed += Time.deltaTime * num3;
            pulseTime -= Time.deltaTime * (num3 * num3);
            tr.Field<float>("timeSincePulsed").Value = timeSincePulsed;
            tr.Field<float>("pulseTime").Value = pulseTime;
            bool pulsed = false;
            if (pulseTime < 0f) {
                __instance.Pulse();
                pulsed = true;
                timeSincePulsed = 0f;
                Sound.Play("blueheartbeat", __instance.transform.position, twoDimensional: true, pitchShift: false, null, 1f - num);
            }
            shownTime = timeSincePulsed;
            HostSendPulse(shownTime, num3, pulsed, origPos);
        } else if (_clientHasPulse) {
            if (_clientHasHeartPos && !cutsceneStarted) {
                origPos = _clientHeartPos;
                tr.Field<Vector2>("origPos").Value = origPos;
            }
            shownTime = _clientTime + (Time.time - _clientRecvAt) * _clientRate;
            if (_clientHeartbeat) {
                _clientHeartbeat = false;
                Sound.Play("blueheartbeat", __instance.transform.position, twoDimensional: true, pitchShift: false, null, 1f - num);
            }
        } else {
            shownTime = timeSincePulsed;
        }
        float value = shownTime * 50f;
        float value2 = 0.5f + num * 8f;
        float num4 = (1f - num2) * Mathf.Clamp01((10f - shownTime) * 0.1f);
        if (mat != null) {
            mat.SetFloat("_DesiredDistance", value);
            mat.SetFloat("_EdgeSize", value2);
            mat.SetColor("_Color", Color.cyan.WithAlpha(num4 * num4 * num4));
        }
        __instance.transform.position = origPos + UnityEngine.Random.insideUnitCircle * 0.1f;
        __instance.transform.localScale = new Vector2(1f + Mathf.Sin(Time.time * 3f) * 0.25f + UnityEngine.Random.value * pulseSpeedAdd, 1f);
        return false;
    }

    [HarmonyPrefix]
    [HarmonyPatch(nameof(BlueHeart.Cutscene))]
    static bool CutscenePrefix(BlueHeart __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        __result = MpCutscene(__instance);
        return false;
    }

    static IEnumerator MpCutscene(BlueHeart heart) {
        if (BlueHeartMp.ClientCutsceneFromHost) {
            Sound.Play("blueHeartObtain", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        }
        Sound.Play("blueheartsfx", heart.transform.position, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);

        Traverse tr = Traverse.Create(heart);
        Vector2 oldPos = tr.Field<Vector2>("origPos").Value;
        Vector2 newPos = oldPos + Vector2.up * 7f;
        var light = heart.GetComponent<UnityEngine.Rendering.Universal.Light2D>();
        float timer = 0f;
        while (timer < 5f) {
            timer += Time.deltaTime;
            tr.Field<float>("pulseSpeedAdd").Value = tr.Field<float>("pulseSpeedAdd").Value + Time.deltaTime * 1.5f;
            tr.Field<Vector2>("origPos").Value = Vector2.Lerp(oldPos, newPos, Utils.InQuad(timer * 0.2f));
            if (light != null) {
                light.intensity = 3f + timer * 30f * Utils.InQuint(timer * 0.2f);
            }
            if (Observer.main != null) {
                Observer.main.distance = 50f - timer * 9f;
            }
            yield return null;
        }

        Vector2 heartPos = heart.transform.position;
        if (PlayerCamera.main != null) {
            PlayerCamera.main.StartCoroutine(PlayerCamera.main.CommenceBlinding());
        }
        UnityEngine.Object.Destroy(heart.gameObject);

        if (!WorldGeneration.GetRunSettingBool("endless")) {
            if (Net.IsServer) {
                Body speaker = PlayerCamera.main != null ? PlayerCamera.main.body : null;
                BlueHeartMp.HostEmbedAffected(speaker, heartPos);
            }
            SteamManager.TryUnlockAchievement("MILLIONSTOONE");
            if (MusicManager.main != null) {
                MusicManager.main.cutsceneVolMult = 0f;
            }
        } else if (Net.IsServer) {
            Sound.Play("observerlaugh", heartPos, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            if (MusicManager.main != null) {
                MusicManager.main.cutsceneVolMult = 1f;
                MusicManager.main.StopSong();
            }
            WorldGeneration.world.biomeDepth = 0;
            WorldGeneration.world.StartCoroutine(WorldGeneration.world.RegenerateWorld());
            Body body = PlayerCamera.main.body;
            switch (WorldGeneration.world.endlessLoops) {
                case 0:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop0"), null, force: false, resetTalkTimer: true);
                    body.eyePanicTime = 11f;
                    body.eyeScareTime = 20f;
                    break;
                case 1:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop1"), null, force: false, resetTalkTimer: true);
                    body.eyeCloseTime = 15f;
                    body.eyeScareTime = 20f;
                    SteamManager.TryUnlockAchievement("ROUNDANDROUNDWEGO");
                    break;
                case 2:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop2"), null, force: false, resetTalkTimer: true);
                    body.StartCoroutine(body.Cry());
                    break;
                case 3:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloop3"), null, force: false, resetTalkTimer: true);
                    body.eyeScareTime = 12f;
                    break;
                default:
                    body.talker.TalkDelayed(12f, Locale.GetCharacter("endlessloopdefault"), null, force: false, resetTalkTimer: true);
                    break;
            }
            WorldGeneration.world.endlessLoops++;
        }

        foreach (var kv in ScavPlayer.BodyToPlayerDict) {
            if (kv.Key != null) {
                kv.Key.forceNoMovement = false;
            }
        }
        BlueHeartMp.ClientCutsceneFromHost = false;
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_BlueHeartCutscene)]
    static void Client_BlueHeartCutscene(knetid _, NetDataReader reader) {
        reader.Get(out bool start);
        if (!start || BlueHeart.main == null || Net.IsServer) {
            return;
        }
        BlueHeartMp.ClientCutsceneFromHost = true;
        Traverse tr = Traverse.Create(BlueHeart.main);
        if (tr.Field<bool>("cutsceneStarted").Value) {
            return;
        }
        tr.Field<bool>("cutsceneStarted").Value = true;
        Body local = PlayerCamera.main != null ? PlayerCamera.main.body : null;
        if (local != null) {
            local.forceNoMovement = true;
        }
        if (PlayerCamera.main != null) {
            PlayerCamera.main.SetTimeScale(PlayerCamera.SpeedType.Normal, switchSound: false, force: true);
        }
        BlueHeart.main.StartCoroutine(BlueHeart.main.Cutscene());
    }
}
#endif
