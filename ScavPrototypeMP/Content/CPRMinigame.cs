using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Linq;
using TMPro;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using UnityEngine.EventSystems;
using UnityEngine.UI;

namespace Together;

/// <summary>
/// 联机心肺复苏（CPR）小游戏与双掌按压节奏交互前端。
/// </summary>
public class CPRMinigame : Minigame {
    public CPRHandler handler = game.GetOrAddComponent<CPRHandler>();

    public Body LOCAL_BODY => Util.GetLocalBody();
    public Body healer => LOCAL_BODY;

    public Body pacient;
    // 保留此字段供 MinigameMPManager 同步反射读取
    public Limb limb;

    public bool stupid => healer.skills.INT < CPRHandler.SMART_INT_REQUIREMENT;

    public CPRMinigame(Body body) {
        pacient = body;
        limb = body.GetUpperTorso();
    }

    public override HandSpriteType HandType() => HandSpriteType.Grasp;

    public string GetGuideText() => stupid ? Lang.Get("cpr_minigame_guide_stupid") : Lang.Get("cpr_minigame_guide");

    public override string GuideLocaleString() => GetGuideText();

    public override bool NeedsItem() => false;

    public override void Start() {
        handler.pacient = pacient;

        game.CreateScreen("Special/SelfHarmMinigame");
        foreach (Image item in game.spawnedMiniGame.GetComponentsInChildren<Image>()) {
            Color c = item.color;
            c.a = 0f;
            item.color = c;
        }

        handler.MG_CreateSecondHand();
    }

    public override void Update(List<RaycastResult> uiCasts) {
        if (pacient == null) {
            MinigameMPManager.Note("cpr end, patient gone");
            handler.MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
            return;
        }
        if (!KM.DistanceCheck(pacient, LOCAL_BODY, GameplayRanges.PlayerInteractDistance * 2f)) {
            MinigameMPManager.Note("cpr end, too far");
            handler.MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
            return;
        }

        game.guideText.text = GuideLocaleString();
        handler.MG_Update(uiCasts);
    }

    public override void PhysicsUpdate(float deltaTime) {
        handler.MG_PhysicsUpdate(deltaTime);
    }

    public override float HandRotOffset() => handler.cpr_rot;
}

public partial class CPRHandler : MonoBehaviour {
    public double last_click_time = 0.0;
    public double pressed_time = 0.0;

    public float last_full_cycle_time = 0f;
    public float last_hold_time = 0f;

    public float last_nohold_time_permin = 0f;
    public float last_hold_time_permin = 0f;

    public float cur_cpr_rate = 0f;
    public static float CPR_RATE_SMOOTHING = 0.3f;
    public bool is_minigame = true;

    public static RangeF AcceptableRate = new RangeF(65f, 220f);

    public Body healer;
    public Body pacient;

    public Limb mainhand => healer.GetPrimaryHand();
    public Limb secondhand => healer.GetSecondaryHand();

    public RectTransform MG_SecondHandTransform;
    public Image MG_SecondHandSprite;

    public static int SMART_INT_REQUIREMENT = 12;
    public static int MINIMUM_STR_REQUIREMENT = 3;

    public bool stupid => healer.skills.INT < SMART_INT_REQUIREMENT;
    public bool is_in_cpr_zone = false;
    public bool is_ui_overlapping = false;
    public bool ready_for_cpr = false;
    public bool clicking_into_cpr = false;
    public bool clicking_not_into_cpr = false;

    public string cpr_status = "";
    public double cpr_status_last_change = 0.0;

    public float cpr_rot = 0f;
    float smooth_cpr_handscale = 1f;
    float cpr_press_scalar => KM.RemapClamped(smooth_cpr_handscale, 1f, CPR_SCALE_PRESS_TARGET, 0f, 1f);

    static float CPR_SCALE_PRESS_TARGET = 0.55f;
    public static float CPR_STAMINA_USE_MULTIPLIER = 1f;

    public bool aed_minigame_overlapping_with_pads = false;
    public Vector2 last_minigame_mouse_localpos = Vector2.zero;

    public static MinigameBase game => MinigameBase.main;

    public void Awake() {
        healer = Util.GetLocalBody();
    }

    public void LateUpdate() {
        if (is_minigame && game.currentMinigame == null) {
            Destroy(this);
        }
    }

    public void MG_End() {
        game.EndMinigame();
        Destroy(this);
    }

    public void MG_CreateSecondHand() {
        GameObject ni = Instantiate(MinigameBase.main.guideText.gameObject, game.spawnedMiniGame, false);
        RectTransform guideRect = ni.GetComponent<RectTransform>();
        TextMeshProUGUI ttx = ni.GetComponent<TextMeshProUGUI>();
        ttx.text = Lang.Get("click_to_cpr");
        guideRect.offsetMin = Vector2.zero;
        guideRect.offsetMax = Vector2.zero;
        guideRect.anchorMin = Vector2.one * 0.25f;
        guideRect.anchorMax = Vector2.one * 0.75f;
        ttx.color *= 0.5f;
        ttx.fontSize *= 1.3f;

        GameObject n = Instantiate(MinigameBase.main.handTransform.gameObject, game.spawnedMiniGame, false);
        MG_SecondHandTransform = n.GetComponent<RectTransform>();
        MG_SecondHandSprite = n.GetComponent<Image>();

        Vector3 sc = MinigameBase.main.handTransform.localScale;
        sc.x *= -1f;
        MG_SecondHandTransform.localScale = sc;
        MG_SecondHandSprite.color = new Color(1f, 1f, 1f, 0f);
    }

    public static float GetHandMaxTotalForce(Body body) {
        Limb hand1 = body.limbs[(int)LimbIndex.PrimaryHand];
        Limb hand2 = body.limbs[(int)LimbIndex.SecondaryHand];
        return Mathf.Max(hand1.totalForce, hand2.totalForce);
    }

    public static bool CheckIfStrongEnoughToPerformCPR(Body body) {
        return body.skills.STR * GetHandMaxTotalForce(body) > MINIMUM_STR_REQUIREMENT;
    }

    public static bool CheckIfBodyIsNotOkAndNeedCPR(Body body) {
        return (body.heartRate < 50f && body.bloodOxygen < 100f && body.bloodPressure < 30f)
            || body.heartRate < 5f
            || body.isDying
            || body.isCriticallyDying;
    }

    public static bool PlayerIsPerformingCPR(ScavPlayer plr, out NetBody targetNb) {
        targetNb = null;
        if (!Net.IsRunning || plr == null || plr.body == null || !plr.IsInMinigame || plr.minigame_targetobject == null) {
            return false;
        }
        Type t = plr.MinigameType;
        bool hasTarget = plr.minigame_targetobject.TryGetNetBody(out targetNb);
        return hasTarget && targetNb != null && (t == typeof(CPRMinigame) || (t == typeof(AEDMinigame) && targetNb.body != plr.body));
    }

    public static bool PlayerIsPerformingCPR(ScavPlayer plr) {
        if (!Net.IsRunning || plr == null || plr.body == null || !plr.IsInMinigame || plr.minigame_targetobject == null) return false;
        Type t = plr.MinigameType;
        return t == typeof(CPRMinigame) || (t == typeof(AEDMinigame) && (!plr.minigame_targetobject.TryGetPlayer(out ScavPlayer p) || p != plr));
    }

    public static bool IsCPRBeingPerformedOnThisBody(Body b, out ScavPlayer who) {
        if (!Net.IsRunning || b == null || ScavPlayer.AllLivingPlayers == null) {
            who = null;
            return false;
        }
        foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
            if (plr == null || plr.body == null || !plr.body.conscious) {
                continue;
            }
            if (PlayerIsPerformingCPR(plr) && plr.minigame_targetobject.TryGetNetBody(out NetBody nb) && nb != null && nb.body == b) {
                who = plr;
                return true;
            }
        }
        who = null;
        return false;
    }

    public static bool IsCPRBeingPerformedOnThisBody_IsMeOrNobody(Body b) {
        return !IsCPRBeingPerformedOnThisBody(b, out ScavPlayer who) || who.IsLocal;
    }

    public static bool IsCPRBeingPerformedOnThisBody_IsThisGuyOrNobody(Body b, ScavPlayer plr) {
        return !IsCPRBeingPerformedOnThisBody(b, out ScavPlayer who) || who == plr;
    }

#if TESTING
    void OnGUI() {
        if (is_minigame) {
            Vector2 scaled = UIBullshit.ScaleSize(500.91f, 611.41f);
            var mainpanel = new Rect(0f, 0f, scaled.x, scaled.y) {
                center = new Vector2(Screen.width - scaled.x * 0.5f, UIBullshit.GetHalfScreenSize().y),
            };

            GUI.Box(mainpanel, "");
            GUILayout.BeginArea(mainpanel);
            try {
                GUILayout.Label("CPR METRICS");
                GUILayout.Label("cpr_press_scale: " + Math.Round(cpr_press_scalar, 2));
                GUILayout.Label($"last_hold_time_permin: {Math.Round(last_hold_time_permin)}");
                GUILayout.Label($"last_nohold_time_permin: {Math.Round(last_nohold_time_permin)}");
                GUILayout.Label($"cur_cpr_rate: {Math.Round(cur_cpr_rate)}");
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Minigame, "CPR ONGUI", e);
            }
            GUILayout.EndArea();
        }
    }
#endif

    public static void CPR_UseStaminaOnPress(Body body) {
        body.stamina -= 0.9f * CPR_STAMINA_USE_MULTIPLIER;
        body.temperature += 0.02f * CPR_STAMINA_USE_MULTIPLIER;
    }

    void HandStoppedCPRPress() {
        float holdtime = Mathf.Max(0.001f, (float)(Time.unscaledTimeAsDouble - pressed_time));
        float lastcycletime = Mathf.Max(0.001f, (float)(Time.unscaledTimeAsDouble - last_click_time));
        float lastclicktime = Mathf.Max(0.001f, lastcycletime - holdtime);

        last_click_time = Time.unscaledTimeAsDouble;
        const float torate = 60f;

        float holdtime_permin = torate / (holdtime * 2f);
        float lastclicktime_permin = torate / (lastclicktime * 2f);
        float permin_middle = Mathf.LerpUnclamped(holdtime_permin, lastclicktime_permin, 0.5f);
        holdtime_permin = Mathf.LerpUnclamped(holdtime_permin, permin_middle, CPR_RATE_SMOOTHING);
        lastclicktime_permin = Mathf.LerpUnclamped(lastclicktime_permin, permin_middle, CPR_RATE_SMOOTHING);

        last_full_cycle_time = lastcycletime;
        last_hold_time = holdtime;
        last_hold_time_permin = holdtime_permin;
        last_nohold_time_permin = lastclicktime_permin;

        float averaged_rate = Mathf.LerpUnclamped(torate / lastcycletime, permin_middle, CPR_RATE_SMOOTHING);
        cur_cpr_rate = Mathf.Lerp(cur_cpr_rate, averaged_rate, 0.5f);

        if (pacient.TryGetComponent(out NetBody pacient_nb)) {
            var writer = Net.CreateWriter(NetmsgId.SERVER_CPR_PRESSED);
            writer.Put((knetid)pacient_nb.netId);
            writer.Put(holdtime);
            writer.Put(lastclicktime);
            writer.Put(averaged_rate);
            Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
        }

        if (!stupid) {
            if (AcceptableRate.IsInRange(holdtime_permin) && AcceptableRate.IsInRange(lastclicktime_permin)) {
                cpr_status = Lang.Get("cpr_minigame_good");
            } else if (AcceptableRate.IsInRange(averaged_rate)) {
                cpr_status = Lang.Get("cpr_minigame_bad_timing");
            } else if (permin_middle > AcceptableRate.Center()) {
                cpr_status = Lang.Get("cpr_minigame_fast");
            } else {
                cpr_status = Lang.Get("cpr_minigame_slow");
            }
            cpr_status_last_change = Time.realtimeSinceStartupAsDouble;
        }
    }

    public void MG_Update(List<RaycastResult> uiCasts) {
        if ((pacient.standing && pacient.moveDir != Vector2.zero) || !healer.standing) {
            MG_End();
            PlayerCamera.main.DoAlert(Lang.Get("plr_moving"));
            return;
        }

        bool is_aed_minigame = false;
        if (is_minigame && game.currentMinigame != null) {
            is_aed_minigame = game.currentMinigame is AEDMinigame;
            if (Time.realtimeSinceStartupAsDouble - cpr_status_last_change < 5.0 && !string.IsNullOrEmpty(cpr_status)) {
                game.guideText.text = game.currentMinigame.GuideLocaleString() + "\n" + cpr_status;
            }
        }

        Vector2 mouse_localpos = MinigameMPManager.GetLocalMousePosMinigameSpace();
        bool last_is_in_cpr_zone = is_in_cpr_zone;
        const float sq = 110f;
        is_in_cpr_zone = Mathf.Abs(game.handPos.x) < sq && Mathf.Abs(game.handPos.y) < sq
            && Mathf.Abs(mouse_localpos.x) < sq && Mathf.Abs(mouse_localpos.y) < sq;

        if (is_aed_minigame && is_in_cpr_zone) {
            var as_aed = (AEDMinigame)game.currentMinigame;
            if (last_minigame_mouse_localpos != mouse_localpos || last_is_in_cpr_zone != is_in_cpr_zone) {
                aed_minigame_overlapping_with_pads = uiCasts.Any(x => as_aed.pads.Contains(x.gameObject.GetComponent<RectTransform>()));
            }
            if (as_aed.currentlyHeld != null) {
                aed_minigame_overlapping_with_pads = true;
            }
        } else {
            aed_minigame_overlapping_with_pads = false;
        }

        is_ui_overlapping = aed_minigame_overlapping_with_pads
            || uiCasts.Any(x => x.gameObject.transform.IsChildOf(game.spawnedMiniGame) && x.gameObject.GetComponent<Button>() != null);

        float righthanded = Mathf.Sign(game.handTransform.localScale.x);
        float possible_cprot_target = (stupid ? 30f : 36f) * righthanded;

        Image og_handSprite = game.handTransform.GetComponent<Image>();
        game.handTransform.localScale *= smooth_cpr_handscale;

        Vector2 mirrored_handpos = game.handPos;
        mirrored_handpos.x *= -1f;
        MG_SecondHandTransform.anchoredPosition = mirrored_handpos;
        Vector3 sc = game.handTransform.localScale;
        sc.x *= -1f;
        MG_SecondHandTransform.localScale = sc;

        game.handTransform.eulerAngles = new Vector3(0f, 0f, game.handVelocity.x * 0.5f + cpr_rot);
        MG_SecondHandTransform.eulerAngles = new Vector3(0f, 0f, game.handVelocity.x * 0.5f - cpr_rot);

        Color c2 = MG_SecondHandSprite.color;
        float targetAlpha = 0f;
        if (!clicking_not_into_cpr && (ready_for_cpr || (!is_ui_overlapping && (game.handPos.x * righthanded > 1f || is_in_cpr_zone)))) {
            targetAlpha = Mathf.Min(
                KM.RemapClamped(Mathf.Abs(game.handPos.x), 120f, 90f, 0f, 0.995f),
                KM.RemapClamped(Mathf.Abs(game.handPos.y), 120f, 90f, 0f, 0.9951f));
            if (!ready_for_cpr) {
                targetAlpha *= targetAlpha * 0.5f;
            }
        }
        c2.a = Mathf.MoveTowards(c2.a, targetAlpha, Time.deltaTime * 10f);
        MG_SecondHandSprite.color = c2;

        Color c1 = og_handSprite.color;
        c1.a = KM.RemapClamped(Mathf.Abs(game.handPos.x), 220f, 130f, 0.8f, 0.99f);
        og_handSprite.color = c1;

        if (game.handStoppedClicking && clicking_into_cpr) {
            HandStoppedCPRPress();
        }
        if (game.handStartedClicking) {
            if (!KM.DistanceCheck(pacient, healer, GameplayRanges.PlayerInteractDistance * 2f)) {
                MG_End();
                PlayerCamera.main.DoAlert(Lang.Get("plr_too_far"));
                return;
            }

            clicking_into_cpr = ready_for_cpr;
            if (clicking_into_cpr) {
                pressed_time = Time.unscaledTimeAsDouble;
                CPR_UseStaminaOnPress(healer);

                if (!CheckIfStrongEnoughToPerformCPR(healer)) {
                    PlayerCamera.main.DoAlert(Lang.Get("ur_too_weak"));
                    if (!is_aed_minigame) {
                        MG_End();
                        return;
                    }
                } else if (smooth_cpr_handscale > 0.9f && !(pacient.IsRagdolling() || pacient.grounded)) {
                    if (healer.TryGetNetBody(out NetBody nb) && pacient.TryGetNetBody(out NetBody pacient_nb)) {
                        nb.Push(pacient_nb);
                        MG_End();
                        return;
                    }
                }
            }
        } else if (!game.handClicking) {
            clicking_into_cpr = false;
            ready_for_cpr = is_in_cpr_zone && !is_ui_overlapping;
        }
        clicking_not_into_cpr = game.handClicking && !clicking_into_cpr;

        float cpr_press_target = 1f;
        float cprrot_target = 0f;
        if (ready_for_cpr) {
            cprrot_target = possible_cprot_target;
            if (game.handClicking) {
                og_handSprite.sprite = game.handSprites[0];
                cpr_press_target = CPR_SCALE_PRESS_TARGET;
            } else {
                og_handSprite.sprite = game.handSprites[1];
            }
            MG_SecondHandSprite.sprite = secondhand.dismembered ? game.handSprites[10] : og_handSprite.sprite;
            if (mainhand.dismembered) {
                og_handSprite.sprite = game.handSprites[10];
            }
        }

        cpr_rot = Mathf.MoveTowards(cpr_rot, cprrot_target, Time.deltaTime * 360f);
        smooth_cpr_handscale = Mathf.Lerp(smooth_cpr_handscale, cpr_press_target, Time.deltaTime * GetPushSpeed(Util.GetLocalBody(), pacient, game.handClicking));
        last_minigame_mouse_localpos = mouse_localpos;
    }

    public void MG_PhysicsUpdate(float deltaTime) {
        if (ready_for_cpr) {
            Vector2 target = stupid ? new Vector2(40f, 0f) : new Vector2(-40f, 5f);
            game.handPos = Vector2.Lerp(game.handPos, target * smooth_cpr_handscale, deltaTime * 10f);
            game.handVelocity = Vector2.Lerp(game.handVelocity, Vector2.zero, deltaTime * 10f);
        }
    }

    public static float GetPushSpeed(Body healer, Body pacient, bool pressing) {
        float con = healer.consciousness * 0.01f * 7f;
        float n = 10f + con + healer.skills.STRFrom10 * 0.5f;

        if (pacient.IsRibsBroken()) {
            n += 5f;
            if (!pressing) n = Mathf.Max(18f, n);
        } else if (!pressing) {
            n = Mathf.Max(10f, n);
        }

        if (pressing) {
            n *= Mathf.Clamp(0.6f + GetHandMaxTotalForce(healer) * 0.5f, 0.6f, 1f);
        }
        return n;
    }
}
