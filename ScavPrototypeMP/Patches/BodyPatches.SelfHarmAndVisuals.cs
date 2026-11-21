using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 角色自残/自尽联机同步核心（<see cref="SelfHarmMultiplayerSync"/>）、面部死亡定格与多人睡眠速率动态平衡补丁。
/// </summary>
public static class SelfHarmMultiplayerSync {
    internal static bool force_run = false;

    /// <summary>记录当前开启自残小游戏的目标躯体，防止残留小游戏结算误伤本地玩家。</summary>
    public static Body selfHarmMinigameBody;

    public static void DoMoodPenaltyInRadius(Body depressedGuy, float emotionalDamage, float maxDist) {
        if (depressedGuy == null) {
            return;
        }
        foreach (NetBody witness in ScavPlayer.GetPlayerBodiesInRadius(depressedGuy.transform.position, maxDist)) {
            if (witness?.body == null || witness.body == depressedGuy || !witness.body.alive) {
                continue;
            }

            float totalDamage = emotionalDamage;
            if (witness.body.conscious && Util.AccurateRaycastInteractionCheckObstruction(depressedGuy, witness.body, false)) {
                totalDamage += emotionalDamage * 0.5f;
            }

            witness.body.happiness -= totalDamage * witness.body.desensitizedMult;
            ModLog.Debug($"DoMoodPenaltyInRadius witness={witness.playerName} curHap={witness.body.happiness}", LogDomain.Medical);
        }
    }

    public static void SelfHarmClawAnim(Body body) {
        SelfHarmer harmer = body.GetComponent<SelfHarmer>();
        MethodInfo method = Util.GetMethod<SelfHarmer>("ClawAnim");
        method?.Invoke(harmer, null);
    }

    public static void SelfHarm_Copypasted(byte type, Body body, ref Limb limb) {
        bool isEye = type == 1;
        bool isSelfHarm = type == 2;

        if (isSelfHarm) {
            limb ??= body.limbs[Random.Range(1, body.limbs.Length)];
            limb.muscleHealth -= 13f;
            limb.skinHealth -= 35f;
            limb.bleedAmount += Random.Range(6f, 10f);
            limb.pain += Random.Range(20f, 30f);
            WoundView.view?.AddImageToLimb(limb, Resources.Load<Sprite>("Special/selfHarmWound"), flip: false, l => l.skinHealth >= 95f);
            body.rb.AddForce(Random.insideUnitCircle * 500f, ForceMode2D.Impulse);
            body.talker.Talk(Locale.GetCharacter("selfharm"), null, force: true);
            body.happiness += 4f;
            Sound.Play("selfharm", body.transform.position);

            if (Util.IsBodyLocal(body)) {
                Sound.Play("harmSting", Vector2.zero, twoDimensional: true, pitchShift: false, null, 0.7f);
            }

            SelfHarmClawAnim(body);
            DoMoodPenaltyInRadius(body, Multiplayer.rules.SelfharmWitnessMoodDebuff * 0.25f, GameplayRanges.CraftGroundScanRadius);
        } else {
            limb ??= isEye
                ? body.limbs[(int)LimbIndex.Head]
                : (Random.Range(0f, 1f) < 0.5f
                    ? body.limbs[(int)LimbIndex.PrimaryForearm]
                    : body.limbs[(int)LimbIndex.SecondaryForearm]);
            limb.muscleHealth -= 30f;
            limb.skinHealth -= 70f;
            limb.bleedAmount += Random.Range(45f, 60f);
            limb.pain += Random.Range(25f, 35f);
            body.adrenaline += 50f;
            body.rb.AddForce(Random.insideUnitCircle * 700f, ForceMode2D.Impulse);
            if (!isEye) {
                body.talker.Talk(Locale.GetCharacter(VanillaAssetKeys.TalkSuicide), null, force: true);
            }
            body.happiness += isEye ? 16f : 12.5f;
            Sound.Play(VanillaAssetKeys.TalkSuicide, body.transform.position);

            if (Util.IsBodyLocal(body)) {
                MusicManager.main.PlaySong(Resources.Load<AudioClip>("Sounds/music/whathaveidone"));
                Sound.Play("harmSting", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 0.7f);
            }

            WoundView.view?.AddImageToLimb(limb, Resources.Load<Sprite>("Special/suicideWound"), flip: false, l => l.skinHealth >= 95f);
            if (isEye) {
                body.RemoveEye();
            }
            SelfHarmClawAnim(body);
            DoMoodPenaltyInRadius(body, Multiplayer.rules.SelfharmWitnessMoodDebuff, 25f);
        }
    }

    public static void Force(SelfHarmer instance) {
        if (instance == null) {
            return;
        }
        force_run = true;
        try {
            instance.AttemptHarm();
        } finally {
            force_run = false;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_force_talker_say, true)]
    static void Client_ForceTalkerSay(knetid senderId, NetDataReader reader) {
        reader.Get(out knetid objSyncId);
        reader.Get(out byte type);
        reader.Get(out string message);

        object targetOwner = null;
        GameObject go = null;
        if (type == 0) {
            if (NetObjectRegistry.TryGetSyncInfoLive(objSyncId, out SyncInfo si)) {
                go = si.go;
                targetOwner = si;
            }
        } else if (type == 1) {
            if (ScavPlayer.TryGetPlayerAndNetBodyFromClientId(objSyncId, out _, out NetBody pb)) {
                go = pb.gameObject;
                targetOwner = pb;
            }
        }
        if (go != null && Util.TryGetTalkerOnObject(go, out Talker foundTalker, out _)) {
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(go.transform.position, $"C: {targetOwner} ForceTalkerSay: \"{message}\"");
            }
            Talker_Talk_MultiplayerPatch.ForceNoSpeechImpairment(foundTalker, message, true);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_SelfHarmRelay)]
    static void Client_SelfHarmMinigameMinigameEnd(knetid senderId, NetDataReader reader) {
        reader.Get(out byte type);
        reader.Get(out LimbNetId limbId);

        if (!limbId.TryGetNetBodyAndLimb(out NetBody pb, out Limb limb)) {
            return;
        }

        Body body = pb.body;
        if (type == 20) {
            if (body.FindByIdThorough("plushie", out _)) {
                Force(body.GetComponent<SelfHarmer>());
            }
        } else if (type == 10) {
            // 持枪自尽由当事方本地执行，远端忽略旧代号 10
        } else if (!Util.IsBodyLocal(body)) {
            SelfHarm_Copypasted(type, body, ref limb);
        }
    }
}

/// <summary>
/// 跨模块兼容转发层（供同程序集其它子系统平滑过渡至 <see cref="SelfHarmMultiplayerSync"/>）。
/// </summary>
public static class YOU_SHOULD_KILL_YOURSELF_NOOOW {
    public static Body selfHarmMinigameBody {
        get => SelfHarmMultiplayerSync.selfHarmMinigameBody;
        set => SelfHarmMultiplayerSync.selfHarmMinigameBody = value;
    }

    public static void DoMoodPenaltyInRadius(Body depressedguy, float emotional_damage, float maxdist)
        => SelfHarmMultiplayerSync.DoMoodPenaltyInRadius(depressedguy, emotional_damage, maxdist);

    public static void SelfHarmClawAnim(Body body)
        => SelfHarmMultiplayerSync.SelfHarmClawAnim(body);

    public static void SelfHarm_Copypasted(byte type, Body body, ref Limb limb)
        => SelfHarmMultiplayerSync.SelfHarm_Copypasted(type, body, ref limb);

    public static void Force(SelfHarmer instance)
        => SelfHarmMultiplayerSync.Force(instance);
}

[HarmonyPatch(typeof(SelfHarmer), nameof(SelfHarmer.Update))]
public static class SelfHarmer_Update_MultiplayerPatch {
    static void Postfix(SelfHarmer __instance) {
        if (!Net.IsRunning) {
            return;
        }
        Body body = __instance.GetComponent<Body>();
        if (!Util.IsBodyLocal(body) && __instance.scarySource.isPlaying) {
            __instance.scarySource.Stop();
            __instance.scarySource.volume = 0f;
            __instance.scarySource.loop = false;
        }
    }
}

[HarmonyPatch(typeof(SelfHarmer), nameof(SelfHarmer.AttemptHarm))]
public static class SelfHarmer_AttemptHarm_MultiplayerPatch {
    private static bool Prefix(SelfHarmer __instance) {
        if (!Net.IsRunning || SelfHarmMultiplayerSync.force_run) {
            return true;
        }

        Body body = __instance.GetComponent<Body>();
        if (body.FindByIdThorough("plushie", out _)) {
            if (Multiplayer.IsNetworkActiveAndIsServer()) {
                NetDataWriter writer = Net.CreateWriter(NetmsgId.CLIENT_SelfHarmRelay);
                writer.Put((byte)20);
                writer.Put(new LimbNetId(body.GetHead()));
                Net.Server_SendToClients(DeliveryMethod.ReliableUnordered, writer, clientIds: ServerMain.AllClientIdsExceptHost);
                return true;
            }
        } else if (!Util.IsBodyLocal(body)) {
            __instance.cooldownTime = 120f;
            return false;
        } else if (body.totalHappiness > -90f) {
            return true;
        } else if (body.FindByTagThorough("gun", out Item gun)) {
            if (Multiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetBody(out NetBody nb)) {
                nb.OnDamageEvent(new BodyDamageEvent(nb, nb, gun, 100));
            }
            return true;
        } else {
            return true;
        }

        __instance.cooldownTime = 120f;
        return false;
    }
}

[HarmonyPatch(typeof(SelfHarmer), "Suicide")]
public static class SelfHarmer_Suicide_MultiplayerPatch {
    private static bool Prefix(SelfHarmer __instance, ref IEnumerator __result) {
        if (!Net.IsRunning) {
            return true;
        }
        Body body = __instance.GetComponent<Body>();
        if (body != null && !Util.IsBodyLocal(body)) {
            __instance.harming = false;
            __instance.cooldownTime = 120f;
            __result = Skip();
            return false;
        }
        return true;
    }

    private static IEnumerator Skip() {
        yield break;
    }
}

[HarmonyPatch(typeof(SelfHarmer), "WaitForMinigame")]
public static class SelfHarmer_WaitForMinigame_MultiplayerPatch {
    private static void Prefix(SelfHarmer __instance) {
        SelfHarmMultiplayerSync.selfHarmMinigameBody = __instance.GetComponent<Body>();
    }
}

[HarmonyPatch(typeof(FacialExpression), nameof(FacialExpression.Update))]
static class FacialExpression_Update_MultiplayerPatch {
    class FacialExpressionPause : MonoBehaviour {
        public FacialExpression face => GetComponent<FacialExpression>();
        public bool lastAlive = true;
        public List<Eye> originalEyeList;
        public List<Eye> pausedEyeList;

        void Awake() {
            if (TryGetComponent(out FacialExpression f)) {
                originalEyeList = f.eyeList;
            }
        }

        public void SetPausedEye(Eye curEye) {
            pausedEyeList = new();
            for (int i = 0; i < originalEyeList.Count; i++) {
                pausedEyeList.Add(curEye);
            }
        }

        public (byte, bool) GetCurrentExpressionId() {
            Sprite curEyeSprite = face.eyes.sprite;
            byte index = 2;
            Eye curEye = originalEyeList.Find(x => x.front == curEyeSprite || x.back == curEyeSprite);
            if (curEye.front != null) {
                index = (byte)originalEyeList.IndexOf(curEye);
            }
            return (index, face.doBackEye);
        }

        public Sprite GetEyeSprite(byte? index, bool? doBackEye) {
            (byte i, bool b) = GetCurrentExpressionId();
            i = index.GetValueOrDefault(i);
            b = doBackEye.GetValueOrDefault(b);
            Eye curEye = originalEyeList[i];
            return b ? curEye.back : curEye.front;
        }

        public Eye GetFrozenEye(byte? index, bool? doBackEye) {
            Sprite curEyeSprite = GetEyeSprite(index, doBackEye);
            return new Eye { front = curEyeSprite, back = curEyeSprite, lightMult = 0f };
        }
    }

    static bool Prefix(FacialExpression __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        Body body = __instance.body;
        FacialExpressionPause data = __instance.GetOrAddComponent<FacialExpressionPause>();
        data.originalEyeList ??= __instance.eyeList;

        if (body.alive != data.lastAlive) {
            if (body.alive) {
                __instance.eyeList = data.originalEyeList;
                data.pausedEyeList = null;
            } else {
                if (data.pausedEyeList == null) {
                    (byte index, bool doBackEye) = data.GetCurrentExpressionId();
                    if (index == 3 || index == 5 || index == 7) {
                        index = 0;
                    }

                    Eye curEye = data.GetFrozenEye(index, doBackEye);
                    data.SetPausedEye(curEye);

                    if (Multiplayer.IsNetworkActiveAndIsServer() && body.TryGetNetBody(out NetBody nb)) {
                        NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelFacePause);
                        writer.Put(nb.netId);
                        writer.Put(index);
                        writer.Put(doBackEye);
                        Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, ServerMain.AllClientIdsExceptHost);
                    }
                }
                __instance.eyeList = data.pausedEyeList;
            }
        }
        data.lastAlive = body.alive;
        return true;
    }

    [CTPostInit]
    static void FacePause() {
        Multiplayer.RegisterCustomClientReceiver(CustomWireCommands.ChannelFacePause, r => {
            r.Get(out knetid nbId);
            r.Get(out byte index);
            r.Get(out bool doBackEye);
            if (NetBody.TryGetNetBodyFromId(nbId, out NetBody nb) && nb.body != null) {
                FacialExpression face = nb.body.GetFace();
                FacialExpressionPause data = face.GetOrAddComponent<FacialExpressionPause>();
                Eye curEye = data.GetFrozenEye(index, doBackEye);
                data.SetPausedEye(curEye);
                if (!nb.body.alive) {
                    face.eyeList = data.pausedEyeList;
                }
            }
        });
    }
}

[HarmonyPatch(typeof(Body), "HandleBody")]
static class Body_HandleBody_MpSleepSpeed {
    static void Prefix(Body __instance, out float __state) {
        __state = __instance.sleepCycleSpeed;
        if (!Net.IsRunning || __instance == null || !__instance.alive || !__instance.TryGetNetBody(out NetBody nb) || !nb.IsPlayer) {
            return;
        }

        float mult = 1f;
        if (ServerMain.TryGetLivingEnergyMedian(out float median)) {
            float offset = (__instance.energy - median) * 0.01f;
            if (!__instance.conscious) {
                offset = -offset;
            }
            mult *= Mathf.Clamp(1f + offset, 0.01f, 3f);
        }

        if (!Multiplayer.rules.DisableSleep && __instance.sleeping && !__instance.conscious && !ServerMain.CheckIfEveryoneIsSleeping()) {
            float ramp = 0f;
            if (nb.soloSleepBonusStart >= 0f) {
                ramp = Mathf.Clamp01((Time.realtimeSinceStartup - nb.soloSleepBonusStart) / ServerMain.SoloSleepBonusRampSeconds);
            }
            mult *= Mathf.Lerp(1f, 3f, ramp);
        }

        if (mult != 1f) {
            __instance.sleepCycleSpeed = __state * mult;
        }
    }

    static void Postfix(Body __instance, float __state) {
        if (__instance != null) {
            __instance.sleepCycleSpeed = __state;
        }
    }
}

// 原版倒地地面检测会按背负者脚下地板覆写 curSleep，需在 HandleGroundedState 之后将被背负状态重标为 Okay 睡眠质量。
[HarmonyPatch(typeof(Body), "HandleGroundedState")]
static class Body_HandleGroundedState_PiggySleep {
    static void Postfix(Body __instance) {
        if (!Net.IsRunning || __instance == null || !__instance.alive) {
            return;
        }
        if (__instance.TryGetNetBody(out NetBody nb) && nb.piggybacking_on != null) {
            __instance.curSleep = Body.SleepQuality.Okay;
        }
    }
}
