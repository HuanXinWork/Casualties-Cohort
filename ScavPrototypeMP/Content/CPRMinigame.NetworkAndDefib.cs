using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using System.Collections.Generic;
using Together.Core;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;

namespace Together;

public partial class CPRHandler {
    static float CHANCE_TO_BREAK_RIBS = 0.008f;
    static int REQUIRED_PRESS_COUNT_FOR_HEART_KICKSTART = 30;

    [ServerReceiver((byte)NetmsgId.SERVER_CPR_PRESSED)]
    static void ServerReceiver_CPR_PRESSED(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid pacient_netid);
        reader.Get(out float holdtime);
        reader.Get(out float lastclicktime);
        reader.Get(out float averaged_rate);

        if (!NetGuard.TryVerifyCaller(clientId, CallerPolicy.AwakeInWorld, LogDomain.Medical, (byte)NetmsgId.SERVER_CPR_PRESSED, out VerifiedCaller caller)) {
            return;
        }
        if (!NetBody.TryGetNetBodyFromId(pacient_netid, out NetBody pacient_nb) || pacient_nb == null || pacient_nb.body == null) {
            return;
        }
        if (!(holdtime.IsFinite() && lastclicktime.IsFinite() && holdtime > 0f && lastclicktime > 0f)) {
            return;
        }
        if (!NetGuard.IsWithinDistance(in caller, pacient_nb.body.transform.position, GameplayRanges.ServerExchangeToleranceDistance)) {
            MpFlightRecorder.Record(LogDomain.Medical, FlightEventKind.GuardDenied, clientId.id, detailCode: (int)NetmsgId.SERVER_CPR_PRESSED);
            return;
        }

        Body healer = caller.Body;
        Body pacient = pacient_nb.body;

        if (!healer.IsBodyLocal()) {
            CPR_UseStaminaOnPress(healer);
        }
        if (!CheckIfStrongEnoughToPerformCPR(healer)) {
            return;
        }

        float time_passed_since_press = (float)(Time.realtimeSinceStartupAsDouble - pacient_nb.lastTimeCprPress);
        pacient_nb.lastTimeCprPress = Time.realtimeSinceStartupAsDouble;
        if (time_passed_since_press <= 0f) {
            return;
        }
        if (time_passed_since_press > 2f) {
            pacient_nb.cpr_cur_perfect_press_count = 0;
        }

        float hold_diff = Mathf.Abs(holdtime - lastclicktime);
        float fullholdtime = holdtime + lastclicktime;
        if (fullholdtime <= 0f) {
            return;
        }

        float real_rate = 60f / fullholdtime;
        averaged_rate = real_rate;
        if (!AcceptableRate.IsInRange(real_rate)) {
            pacient_nb.cpr_cur_perfect_press_count = 0;
            return;
        }

        if (time_passed_since_press < 2f) {
            pacient_nb.cpr_cur_perfect_press_count++;
        }

        if (fullholdtime * 0.9f > time_passed_since_press) {
            float diff = time_passed_since_press / fullholdtime;
            averaged_rate = 60f / time_passed_since_press;
            holdtime *= diff;
        }

        float effectivness = Mathf.Clamp01(1f - hold_diff);
        float rate_center = AcceptableRate.Center();
        float pumpamount = 0f;

        if (effectivness > 0.2f) {
            bool isribsbroken = pacient.IsRibsBroken();
            if (isribsbroken) {
                effectivness = Mathf.Lerp(effectivness, 1f, 0.5f);
            }

            if (effectivness > 0.5f) {
                if (pacient.bloodVolume > 0f) {
                    pumpamount = Mathf.Clamp(pacient.bloodVolume, 30f, 110f) * Mathf.Clamp(holdtime * 1.13f, 0.06f, 0.3f) * effectivness;
                }
                if (pacient.bloodOxygen < 50f && (pacient.IsAirBreathable() || healer.IsAirBreathable())) {
                    pacient.bloodOxygen += pumpamount * 0.7f;
                }
                if (pacient.bloodPressure < 100f) {
                    pacient.bloodPressure += pumpamount;
                }

                if (averaged_rate > rate_center && effectivness < 0.9f && !isribsbroken && healer.skills.STR > 8) {
                    float chance_to_break_ribs = (healer.skills.STR - healer.skills.INT * 0.7f) * CHANCE_TO_BREAK_RIBS;
                    if (chance_to_break_ribs > 0f && Random.value < chance_to_break_ribs) {
                        Limb l = pacient.GetUpperTorso();
                        DebugHelp.OnNetEvent(l.GetPosition(), $"BROKE RIBS chance:{chance_to_break_ribs}");

                        if (!l.broken) {
                            l.broken = true;
                            l.skinHealth -= 10f;
                            l.pain += Random.Range(20f, 50f);
                            l.body.adrenaline += 30f;
                            l.MakeBoneSprite();
                            Sound.Play("BoneBreak" + Random.Range(1, 4), l.transform.position);
                            l.boneHealTimer = 100f;
                            l.body.PromptTalk();
                            l.body.DoGoreSound();
                            l.body.internalBleeding += 5f + chance_to_break_ribs;
                        } else {
                            l.boneHealTimer = 100f;
                        }
                    }
                }

                if (pacient.bloodPressure > 80f
                    && pacient_nb.cpr_cur_perfect_press_count > REQUIRED_PRESS_COUNT_FOR_HEART_KICKSTART
                    && pacient.alive
                    && pacient.inCardiacArrest
                    && pacient.bloodVolume > 10f) {
                    float roll = Random.value * effectivness;
                    if (roll > 0.8f) {
                        DebugHelp.OnNetEvent(pacient.GetPosition(), $"KICKSTARTED THE HEART cur roll:{roll} cur press count:{pacient_nb.cpr_cur_perfect_press_count}");
                        pacient.heartRate = Mathf.Max(pacient.heartRate, Mathf.LerpUnclamped(pacient.heartRate, averaged_rate * 0.6f, effectivness));
                    }
                }

                MedicalSync.Server_QueueSendCharacterHealth(pacient_nb);
            }
        }

        if (log.verbose) {
            string summary = $"SERVER: :: CPR PRESSED :: EFFECT:{effectivness}\tCENTER:{rate_center}\tpumpamount:{pumpamount}\tpress count:{pacient_nb.cpr_cur_perfect_press_count}";
#if !RELEASE
            log.l(summary);
#endif
            if (DebugHelp._DEV_VISUALISE_NET_EVENTS) {
                DebugHelp.OnNetEvent(pacient_nb.position, summary);
            }
        }
    }
}

[HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Start))]
public static class AEDMinigame_Start_MultiplayerPatch {
    public static void Postfix(AEDMinigame __instance) {
        if (!Net.IsRunning) return;
        if (__instance != null && !__instance.limb.body.IsBodyLocal()) {
            CPRHandler cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
            cpr.pacient = __instance.limb.body;
            cpr.MG_CreateSecondHand();
        }
    }
}

[HarmonyPatch(typeof(AEDMinigame), nameof(AEDMinigame.Update))]
public static class AEDMinigame_Update_MultiplayerPatch {
    public static int last_state = 0;
    public static bool did_send_charge = false;

    public static void Postfix(AEDMinigame __instance, ref List<RaycastResult> uiCasts) {
        if (!Net.IsRunning || __instance == null) return;

        if (__instance.state == 3 && last_state != __instance.state) {
            if (MinigameBase.main.currentItem != null && NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out SyncInfo curitemsi)) {
                var writer = Net.CreateWriter(NetmsgId.SERVER_AED_CHAAAARGEE);
                writer.Put(curitemsi.syncId);
                writer.Put(new LimbNetId(__instance.limb));
                Net.Client_Send(DeliveryMethod.ReliableOrdered, writer);
            } else {
                NetObjectRegistry.AlertObjectNotRegistered(true);
            }
        }
        last_state = __instance.state;

        if (!__instance.limb.body.IsBodyLocal()) {
            CPRHandler cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
            cpr.pacient = __instance.limb.body;
            cpr.MG_Update(uiCasts);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_AED_CHAAAARGEE)]
    static void ServerReceiver_AED_CHAAAARGEE(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid aed_syncid);
        reader.Get(out LimbNetId netlimb);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)) return;
        if (!netlimb.TryGetNetBodyAndLimbSafe(out _, out Limb target_limb) || !NetObjectRegistry.TryGetSyncInfo(aed_syncid, out SyncInfo aed_si)) {
            return;
        }

        if (aed_si.IsAED() && aed_si.item == plr.minigame_currentItem && ItemSync.CheckIfBodyReachThisItem(aed_si, pb.body)) {
            if (!aed_si.item.battery.hasCharge) {
                log.serverdeny($"AED CHARGE for {plr} -> {aed_si}: ran out of battery");
                return;
            }
            if (!plr.ServerPlayerState.Cooldown("AED", 3.5f)) {
                return;
            }

            ServerMain.Server_AnnounceSound(target_limb.GetPosition(), "aedcharge", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));

            if (!plr.IsLocal) {
                aed_si.item.battery.DrainCharge(0.02f);
                Util.DelayCallLambda(3.5f, () => {
                    if (aed_si.item == plr.minigame_currentItem) {
                        if (log.verbose) log.l($"{plr} fired an AED {aed_si}");
                        Item_Defibrillate_MultiplayerPatch.force = true;
                        aed_si.item.Defibrillate(new Item.DefibInfo {
                            chance = 1f,
                            limb = target_limb,
                        });
                        aed_si.item.battery.DrainCharge(0.14f);
                    } else if (log.verbose) {
                        log.warn($"{plr} CANCELLED AED ???");
                    }
                });
            }
            return;
        }

        log.serverdeny($"AED CHARGE for {plr} cuz its not aed or he doesnt even have an aed ");
        plr.Server_DoAlertSingle("AED Denied, idk why");
        MinigameMPManager.Server_ForceEndMinigameForPlayer(plr);
    }
}

[HarmonyPatch(typeof(ManualDefibMinigame), nameof(ManualDefibMinigame.Update))]
public static class ManualDefibMinigame_Update_MultiplayerPatch {
    public static void Postfix(ManualDefibMinigame __instance) {
        if (!Net.IsRunning) return;
        if (__instance.onTorso && __instance.limb != null && !__instance.limb.IsBodyLocal()) {
            __instance.ecg.followBody = false;
            __instance.ecg.writeHeight = __instance.limb.body.GetECGHeight(__instance.ecg.timeToUpdate - 0.028f);
        }
    }
}

[HarmonyPatch(typeof(Minigame), nameof(Minigame.PhysicsUpdate))]
public static class Minigame_PhysicsUpdate_MultiplayerPatch {
    public static void Postfix(Minigame __instance, ref float deltaTime) {
        if (!Net.IsRunning) return;
        if (__instance is AEDMinigame aedmg && !aedmg.limb.body.IsBodyLocal()) {
            CPRHandler cpr = MinigameBase.main.GetOrAddComponent<CPRHandler>();
            cpr.pacient = aedmg.limb.body;
            cpr.MG_PhysicsUpdate(deltaTime);
        }
    }
}

[HarmonyPatch(typeof(Item), nameof(Item.Defibrillate))]
public static class Item_Defibrillate_MultiplayerPatch {
    public static bool force = false;

    static bool Prefix(Item __instance, Item.DefibInfo info) {
        if (!Net.IsRunning) return true;
        if (force) {
            force = false;
            return true;
        }

        if (MinigameBase.main?.currentItem == __instance && MinigameBase.main.currentMinigame != null) {
            if (MinigameBase.main.currentMinigame is ManualDefibMinigame md_mg) {
                if (NetObjectRegistry.TryGetSyncInfo(MinigameBase.main.currentItem, out SyncInfo curitemsi)) {
                    if (__instance.IsManualDefibrillator()) {
                        var writer = Net.CreateWriter(NetmsgId.SERVER_ManualDefibrillator_Defibrillate);
                        writer.Put(curitemsi.syncId);
                        writer.Put(new LimbNetId(info.limb));
                        writer.Put((float)info.chance);
                        writer.Put((float)md_mg.currentCharge);
                        Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                    }

                    if (Net.IsClient) {
                        if (MinigameBase.main.currentItem != null) {
                            info.limb.skinHealth -= 5f;
                            info.limb.pain += 20f;
                            info.limb.body.Ragdoll();
                            if (info.limb != info.limb.body.limbs[(int)LimbIndex.UpperTorso] || !info.limb.body.alive) {
                                return false;
                            }
                            info.limb.body.defibShockedFrames = 20;
                            info.limb.body.heartProg = -1f;
                            info.limb.body.bloodPressure = 40f;
                        } else {
                            NetObjectRegistry.AlertObjectNotRegistered(true);
                        }
                        return false;
                    }
                } else if (Net.IsClient) {
                    NetObjectRegistry.AlertObjectNotRegistered(true);
                    return false;
                }
            } else if (Net.IsClient && MinigameBase.main.currentMinigame is AEDMinigame) {
                // 客户端 AED 由服务端 ServerReceiver_AED_CHAAAARGEE 权威结算并同步电量，阻止客机本地重复除颤扣电
                info.limb.skinHealth -= 5f;
                info.limb.pain += 20f;
                info.limb.body.Ragdoll();
                if (info.limb == info.limb.body.limbs[(int)LimbIndex.UpperTorso] && info.limb.body.alive) {
                    info.limb.body.defibShockedFrames = 20;
                }
                return false;
            }
        }

        return true;
    }

    [ServerReceiver((byte)NetmsgId.SERVER_ManualDefibrillator_Defibrillate)]
    static void ServerReceiver_ManualDefibrillator_Defibrillate(knetid clientId, LiteNetLib.Utils.NetDataReader reader) {
        reader.Get(out knetid md_syncid);
        reader.Get(out LimbNetId netlimb);
        reader.Get(out float _);
        reader.Get(out float currentCharge);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out ScavPlayer plr, out NetBody pb)) return;

        if (!currentCharge.IsFinite() || currentCharge < 10f || currentCharge > 200f) {
            log.sus($"{plr} tried to defibrillate with invalid currentCharge: {currentCharge}");
            MpFlightRecorder.Record(LogDomain.Medical, FlightEventKind.GuardDenied, (ushort)clientId, detailCode: (int)NetmsgId.SERVER_ManualDefibrillator_Defibrillate);
            return;
        }

        if (netlimb.TryGetNetBodyAndLimbSafe(out _, out Limb target_limb) && NetObjectRegistry.TryGetSyncInfo(md_syncid, out SyncInfo manualdefib_si)) {
            if (manualdefib_si.IsManualDefibrillator() && manualdefib_si.item == plr.minigame_currentItem && ItemSync.CheckIfBodyReachThisItem(manualdefib_si, pb.body)) {
                if (!manualdefib_si.item.battery.hasCharge) {
                    log.serverdeny($"MANUALDEFIB CHARGE for {plr} -> {manualdefib_si}: ran out of battery");
                    return;
                }
                if (!plr.ServerPlayerState.Cooldown("MANUALDEFIB", 0.2f)) {
                    return;
                }

                ServerMain.Server_AnnounceSound(target_limb.GetPosition(), "manualdefib", ServerMain.GetListOfClientIdsExceptThis(plr.playerId));

                if (!plr.IsLocal) {
                    if (log.verbose) log.l($"{plr} fired a MANUALDEFIB {manualdefib_si}");
                    float real_chance = 1f - Mathf.Abs(target_limb.body.fibrillationProgress - currentCharge * 0.5f) / 40f;

                    force = true;
                    manualdefib_si.item.Defibrillate(new Item.DefibInfo {
                        chance = real_chance,
                        limb = target_limb,
                    });
                    manualdefib_si.item.battery.DrainCharge(currentCharge / 4000f);
                }
                return;
            }

            log.serverdeny($"MANUALDEFIB CHARGE for {plr} cuz its not manualdefib or he doesnt even have it");
            plr.Server_DoAlertSingle("MANUALDEFIB Denied, idk why");
            MinigameMPManager.Server_ForceEndMinigameForPlayer(plr);
        }
    }
}
