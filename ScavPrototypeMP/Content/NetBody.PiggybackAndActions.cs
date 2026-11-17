using CasualtiesTogetherUtils;
using LiteNetLib;
using System;
using System.Collections.Generic;
using System.Text;
using Together.MpConstants;
using UnityEngine;

namespace Together;

public partial class NetBody {
    public const float KG_to_encumburenceUnit = 0.17f;
    public const float PUSH_COOLDOWN = 1f;
    public const float PUSH_VELOCITY = 15f;
    static float PUSH_STAMINA_USE_MULTIPLIER = 1f;

    public NetBody piggybacking_on = null;
    public NetBody carrying_person = null;
    public float piggybackStartedUnscaled;
    bool carryPoseAligned;
    double lastPushTime;

    public bool CanAttackThisGuy(Body target) {
        NetBody targetnpc = target.GetComponent<NetBody>();
        if (targetnpc != null) {
            if (carrying_person == targetnpc || piggybacking_on == targetnpc) {
                return false;
            }
        }
        if (!target.alive) {
            return true;
        }
        if (!Multiplayer.rules.PVP || target == body) {
            return false;
        }
        if (targetnpc != null && targetnpc.IsPlayer) {
            if (targetnpc.plr.levelPlayTime < 1f) {
                return false;
            }
            if (IsPlayer && Multiplayer.rules.Teams && targetnpc.plr.playerColor == plr.playerColor) {
                return false;
            }
        }
        return true;
    }

    public int CountPiggybackStackUp() {
        NetBody cur = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (cur.carrying_person != null) {
                cur = cur.carrying_person;
            } else {
                break;
            }
        }
        return i;
    }

    public int CountPiggybackStackDown() {
        NetBody cur = this;
        int i;
        for (i = 0; i < 1000; i++) {
            if (cur.piggybacking_on != null) {
                cur = cur.piggybacking_on;
            } else {
                break;
            }
        }
        return i;
    }

    public NetBody GetPiggybackStackTop() {
        NetBody cur = this;
        for (int i = 0; i < 1000; i++) {
            if (cur.carrying_person != null) {
                if (cur.carrying_person == this) {
                    log.error("PIGGYBACK GOT RECURSIVE !!!!!");
                    cur.StopPiggyback();
                    break;
                }
                cur = cur.carrying_person;
            } else {
                break;
            }
        }
        return cur;
    }

    public List<NetBody> GetPiggybackStackAll() {
        var result = new List<NetBody> { this };
        NetBody cur = this;
        for (int i = 0; i < 1000; i++) {
            if (cur.piggybacking_on != null) {
                if (cur.piggybacking_on == this) {
                    log.error($"PIGGYBACK GOT RECURSIVE !!!!! {string.Join(" ", result)} !!!");
                    cur.StopPiggyback();
                    break;
                }
                cur = cur.piggybacking_on;
                result.Add(cur);
            } else {
                break;
            }
        }
        cur = this;
        for (int i = 0; i < 1000; i++) {
            if (cur.carrying_person != null) {
                if (cur.carrying_person == this) {
                    log.error($"PIGGYBACK GOT RECURSIVE !!!!! {string.Join(" ", result)} !!!");
                    cur.StopPiggyback();
                    break;
                }
                cur = cur.carrying_person;
                result.Add(cur);
            } else {
                break;
            }
        }
        return result;
    }

    public bool IsPiggybackable() {
        if (piggybacking_on != null) {
            return CountPiggybackStackDown() < Multiplayer.rules.PiggybackMaxStack;
        }
        return body.standing;
    }

    public float GetRealFullEncumberanceOfBody() {
        float enc = body.GetTotalEncumberance();
        float bodyweight = body.weightOffset * 0.34f + 50f;
        return enc * 0.7f + (bodyweight * KG_to_encumburenceUnit);
    }

    public bool WantsMilkyCarrySit() {
        return body != null && body.charType == 1 && piggybacking_on != null;
    }

    public static void ClearSitAnims(Body b) {
        if (b == null) return;
        if (b.bodyAnimator != null) {
            AnimatorClipInfo[] bodyClips = b.bodyAnimator.GetCurrentAnimatorClipInfo(0);
            if (bodyClips.Length != 0 && bodyClips[0].clip != null && bodyClips[0].clip.name == "ExperimentSit") {
                b.bodyAnimator.Play("Grounded");
                b.standLerpTime = 0f;
            }
        }
        if (b.armsAnimator != null) {
            AnimatorClipInfo[] armClips = b.armsAnimator.GetCurrentAnimatorClipInfo(0);
            if (armClips.Length != 0 && armClips[0].clip != null && armClips[0].clip.name == "ArmsSit") {
                b.armsAnimator.Play("Grounded");
            }
        }
    }

    public static void ForceSitAnims(Body b) {
        if (b == null) return;
        if (b.bodyAnimator != null) {
            b.bodyAnimator.Play("ExperimentSit");
        }
        if (b.armsAnimator != null) {
            b.armsAnimator.Play("ArmsSit");
        }
    }

    public static void ForceMilkyCarrySitBodyAnim(Body b) {
        if (b == null) return;
        if (b.bodyAnimator != null) {
            b.bodyAnimator.Play("ExperimentSit");
        }
        if (b.armsAnimator != null) {
            AnimatorClipInfo[] armClips = b.armsAnimator.GetCurrentAnimatorClipInfo(0);
            if (armClips.Length != 0 && armClips[0].clip != null && armClips[0].clip.name == "ArmsSit") {
                b.armsAnimator.Play("Grounded");
            }
        }
    }

    static bool IsMilkyCarrySitLockedLimb(Body b, Limb limb) {
        if (b == null || limb == null) return false;
        if (limb.isLegLimb) return true;
        return ReferenceEquals(limb, b.GetLowerTorso());
    }

    public static void ApplyMilkyCarrySitLowerPose(Body b) {
        if (b == null || b.bodyAnimator == null || b.limbs == null) return;
        ForceMilkyCarrySitBodyAnim(b);
        float facing = b.isRight ? 1f : -1f;
        Limb down = b.GetLowerTorso();
        Limb legF = b.limbs.Length > (int)Body.LimbNum.LegF ? b.limbs[(int)Body.LimbNum.LegF] : null;
        Limb legB = b.limbs.Length > (int)Body.LimbNum.LegB ? b.limbs[(int)Body.LimbNum.LegB] : null;
        ApplyAnimPoseToLockedLimb(b, down, facing);
        ApplyAnimPoseToLockedLimb(b, legF, facing);
        ApplyAnimPoseToLockedLimb(b, legB, facing);
    }

    static void ApplyAnimPoseToLockedLimb(Body b, Limb limb, float facing) {
        if (limb == null || limb.animLimb == null || limb.rb == null) return;
        Vector2 local = b.bodyAnimator.transform.InverseTransformPoint(limb.animLimb.position);
        local *= b.heightMultiplier;
        limb.transform.localPosition = local;
        limb.transform.eulerAngles = new Vector3(0f, 0f, limb.animLimb.eulerAngles.z * facing);
        limb.rb.position = limb.transform.position;
        limb.rb.rotation = limb.transform.eulerAngles.z;
        limb.rb.velocity = Vector2.zero;
        limb.rb.angularVelocity = 0f;
    }

    public static void ApplyPiggybackCarryPose(Body b) {
        if (b == null || !b.TryGetComponent(out NetBody nb) || nb.piggybacking_on == null) return;
        ClearSitAnims(b);
    }

    void HandlePiggybackUpdate() {
        if (carrying_person != null) {
            body.idleTime = 0f;
            if (Net.IsServer && !IsPiggybackable()) {
                carrying_person.StopPiggyback();
            }
        }

        if (piggybacking_on != null) {
            body.idleTime = 0f;
            body.Ragdoll();

            Body targetbody = piggybacking_on.body;
            if (!piggybacking_on.IsPiggybackable()) {
                StopPiggyback();
                return;
            }

            body.ForceSwitchDir(targetbody.isRight);

            Rigidbody2D targettorsorb = targetbody.limbs[(int)Body.LimbNum.UpTorso].rb;
            Rigidbody2D torsorb = body.limbs[(int)Body.LimbNum.UpTorso].rb;

            float side = body.isRight ? 1f : -1f;
            Vector2 standupamount = Vector2.up * (1f - piggybacking_on.body.crouchAmount);
            Vector2 target_uppertorso_pos = targettorsorb.position + standupamount + (Vector2.left * side);
            if (Physics2D.OverlapCircle(target_uppertorso_pos, 1f, PhysicsLayers.GroundMask) != null) {
                target_uppertorso_pos = Vector2.LerpUnclamped(piggybacking_on.pos + standupamount, target_uppertorso_pos, 0.1f);
            }
            body.crouchAmount = piggybacking_on.body.crouchAmount;

            if (body.alive) {
                SetBodyPosition(target_uppertorso_pos);
                Vector2 carrierVel = targetbody.rb.velocity;
                foreach (Limb item in body.limbs) {
                    if (item == null || item.rb == null) continue;
                    item.rb.velocity = Vector2.Lerp(item.rb.velocity, carrierVel, 0.2f);
                }
                torsorb.velocity = carrierVel;
                Limb upperLimb = body.GetUpperTorso();
                Rigidbody2D upper = upperLimb != null && upperLimb.rb != null ? upperLimb.rb : torsorb;
                upper.velocity = carrierVel;
                Limb headLimb = body.GetHead();
                if (headLimb != null && headLimb.rb != null) {
                    headLimb.rb.velocity = carrierVel;
                }
                torsorb.rotation = targettorsorb.rotation;
                torsorb.angularVelocity = targettorsorb.angularVelocity;
                Limb carrierLower = targetbody.GetLowerTorso();
                if (carrierLower != null && carrierLower.rb != null && Mathf.DeltaAngle(carrierLower.rb.rotation, upper.rotation) > 10f) {
                    upper.rotation = Mathf.MoveTowardsAngle(upper.rotation, carrierLower.rb.rotation, Time.fixedDeltaTime * 100f);
                }
            } else {
                Vector2 chestDelta = target_uppertorso_pos - torsorb.position;
                float upright = Mathf.DeltaAngle(torsorb.rotation, targettorsorb.rotation);
                bool turnUpright = !carryPoseAligned && Mathf.Abs(upright) > 8f;
                Quaternion turn = Quaternion.Euler(0f, 0f, upright);
                Vector2 pivot = torsorb.position;
                foreach (Limb item in body.limbs) {
                    if (item == null || item.rb == null) continue;
                    Vector2 p = item.rb.position;
                    if (turnUpright) {
                        p = pivot + (Vector2)(turn * (p - pivot));
                        item.rb.MoveRotation(item.rb.rotation + upright);
                    }
                    item.rb.position = p + chestDelta;
                }
                if (turnUpright) {
                    carryPoseAligned = true;
                }
                torsorb.velocity = targetbody.rb.velocity;
                torsorb.angularVelocity = targettorsorb.angularVelocity;
                torsorb.MoveRotation(targettorsorb.rotation);
            }
        }
    }

    public bool CanBeCarriedBySomeone() {
#if FULLGAME
        if (ConcurrentEndings.ScientistDead && plr != null && !plr.IsUnchipped()) {
            return false;
        }
#endif
        if (Multiplayer.rules.AlwaysAllowCarry) {
            return true;
        }
        return !body.standing;
    }

    public bool IsPiggybacking() => piggybacking_on != null;
    public bool IsCarrying() => carrying_person != null;

    public bool CanPiggyback(NetBody target, bool check_distance = false, bool force = false) {
        if (this == target || target == null) {
            return false;
        }
#if FULLGAME
        if (EndingCarry.Blocked(this, target)) {
            return false;
        }
#endif
        if (piggybacking_on == target || body == null || target.body == null) {
            return false;
        }
        if (target.carrying_person != null) {
            return false;
        }
        if (!(force || target.IsPiggybackable())) {
            return false;
        }

        int up = CountPiggybackStackUp();
        int down = target.CountPiggybackStackDown();
        if (down + up + 1 > Multiplayer.rules.PiggybackMaxStack) {
#if !RELEASE
            log.warn("CanPiggyback: Piggyback stack max");
#endif
            return false;
        }

        if (check_distance) {
            if (IsLocal) {
                if (!KM.DistanceCheck(body, target.body, GameplayRanges.PlayerInteractDistance)) {
                    return false;
                }
            } else if (Net.IsServer) {
                if (!KM.DistanceCheck(body, target.body, GameplayRanges.PlayerInteractDistance * 1.75f)) {
                    return false;
                }
            }
        }

        if (IsLocal && !force) {
            if (!Util.QuickRaycastInteractionCheck(body.transform.position, target.body.transform.position, true)) {
                PlayerCamera.main.DoAlert(Lang.Get("interact_obstructed"));
                return false;
            }
        }

        return true;
    }

    public bool StartPiggyback(NetBody target, bool check_distance = false, bool force = false) {
        if (!CanPiggyback(target, check_distance, force)) {
            return false;
        }

        StopPiggyback(true);
        if (log.verbose) log.l($"{this} Started piggybacking {target}");

        piggybacking_on = target;
        target.carrying_person = this;
        piggybackStartedUnscaled = Time.unscaledTime;

        Sound.Play("backpack" + UnityEngine.Random.Range(1, 3), target.carrying_person.body.transform.position, twoDimensional: false, pitchShift: false, target.carrying_person.body.transform, 1f, UnityEngine.Random.Range(0.8f, 1.2f));

        target.body.rb.velocity = Vector2.Lerp(target.body.rb.velocity, body.rb.velocity, 0.5f);
        HandlePiggybackUpdate();

        if (IsPlayer) {
            if (IsBodyLocal() && !force && Net.IsClient) {
                ClientMain.Client_SendCharacterSyncPacket();
            }
            if (Net.IsServer) {
                if (!IsBodyLocal()) {
                    body.SetVelocity(Vector2.zero);
                    Server_RemindPlayersCurrentState();
                }
            } else {
                SetNetIgnoreTime();
            }
        }

        return true;
    }

    public bool StopPiggyback(bool isinternal = false) {
        if (body != null && piggybacking_on != null) {
            if (log.verbose) log.l($"{this} Stopped piggybacking {piggybacking_on}");

            NetBody carrier = piggybacking_on;
            carryPoseAligned = false;
            piggybacking_on.carrying_person = null;
            piggybacking_on = null;

            body.ForceStand();
            if (carrier) {
                SetBodyPosition(carrier.body.transform.position);
            }

            if (IsPlayer && !isinternal) {
                if (IsBodyLocal()) {
                    Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_RequestStopCarryPiggyback);
                }
                if (Net.IsServer && !IsBodyLocal()) {
                    Server_RemindPlayersCurrentState(true);
                }
                SetNetIgnoreTime(0.1f);
            }
            return true;
        }
        return false;
    }

    public bool CanPush(NetBody victim) {
        if (this == victim || victim == null) return false;
        if (victim.piggybacking_on == this || victim.carrying_person == this) return false;
        return body.conscious && body.standing;
    }

    public void Push(NetBody victim) {
        if (!CanPush(victim)) return;
        if (Time.realtimeSinceStartupAsDouble - lastPushTime < PUSH_COOLDOWN) return;
        lastPushTime = Time.realtimeSinceStartupAsDouble;

        float vel = PUSH_VELOCITY * Mathf.Clamp(1f + Mathf.Max(0f, body.skills.STRFrom10 - victim.body.skills.STRFrom10) * 0.1f, 0.2f, 3f);
        body.stamina -= 1f * PUSH_STAMINA_USE_MULTIPLIER;
        body.temperature += 0.03f * PUSH_STAMINA_USE_MULTIPLIER;

        if (Net.IsClient) {
            Body targetbody = victim.body;
            Util.PlayWorldSoundOnScreenIfInRange("landsmall1", victim.pos);
            targetbody.SetVelocity(targetbody.rb.velocity + KM.NormalTo(body.rb.position, targetbody.rb.position, out _) * vel);
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_Push_Ego_Naxuy, victim.netId, true);
        } else {
            victim.body.Ragdoll();
            victim.body.SetVelocity(victim.rb.velocity + KM.NormalTo(rb.position, victim.rb.position, out _) * vel);
            victim.Server_RemindPlayersCurrentState();
            ServerMain.Server_AnnounceSound(victim.body.rb.position, "landsmall1", IsPlayer ? ServerMain.GetListOfClientIdsExceptThis(plr.playerId) : ServerMain.AllClientIds);

            if (victim.IsPlayer) {
                var writer = Net.CreateWriter(NetmsgId.CLIENT_Knockback_thing);
                writer.Put(new BodyKnockbackPacket {
                    Velocity = Vector2.zero,
                    CloseUI = true,
                });
                Net.Server_SendToClients(DeliveryMethod.Unreliable, writer, victim.plr.playerId);
            }
        }
    }

    public void Hug(NetBody other) => HugSession.Request(this, other);
    public void Pat(NetBody other) => HeadpatSession.Request(this, other);
    public void Bite(NetBody other) => DuneBiteSession.Request(this, other);
    public static void TryDuneKeybindBite() => DuneBiteSession.TryKeybindBite();
    public static void DuneBiteSessionTick() => DuneBiteSession.ServerTickAuto();
    public static bool DuneBiteSessionCannotConsent(Body body) => DuneBiteSession.CannotConsent(body);

    public void HearinglossDistortMessage(NetBody yapper, ref string message, ref string chattag) {
        string distortionChars = GlobalDark.main.distortionChars;
        var stringBuilder = new StringBuilder(message);

        if (Multiplayer.rules.HearingLossChat) {
            float distortionAmount = (Voicechat.VCRULE_mindwiperule && body.IsMindwiped())
                ? 0.94f
                : Mathf.Max(0f, KM.RemapClamped(body.brainHealth, 85f, 0f, 0f, 0.8f));

            if (distortionAmount > 0f) {
                for (int i = 0; i < message.Length; i++) {
                    if (!char.IsWhiteSpace(stringBuilder[i]) && UnityEngine.Random.value < distortionAmount) {
                        stringBuilder[i] = distortionChars[UnityEngine.Random.Range(0, distortionChars.Length)];
                    }
                }
            }
        }

        bool doing_proximity = Multiplayer.rules.OnlyProximityChat || (Multiplayer.rules.UnchippedProximityChat && unchipped);
        float hearability = 1.1f;
        float distance_to_yapper = 10f;

        if (doing_proximity) {
            distance_to_yapper = Vector2.Distance(yapper.pos, pos);
            if (distance_to_yapper > Voicechat.MAX_HEAR_DISTANCE) {
                message = "";
                return;
            }
            hearability = Mathf.Clamp01(Body.Remap(distance_to_yapper, Voicechat.MAX_HEAR_DISTANCE * 0.4f, Voicechat.MAX_HEAR_DISTANCE, 1.01f, 0.001f));
        }

        if (Multiplayer.rules.HearingLossChat && body.hearingLoss > 30f) {
            hearability = Mathf.Min(hearability, Mathf.Clamp01(Body.Remap(body.hearingLoss, 30f, 100f, 1f, 0f)));
        }

        if (hearability < 1f) {
            for (int i = 0; i < message.Length; i++) {
                if (Chat.CharCanBeReplacedWithBlank(stringBuilder[i]) && UnityEngine.Random.value > hearability) {
                    stringBuilder[i] = '_';
                }
            }
        }

        message = stringBuilder.ToString();
        if (string.IsNullOrWhiteSpace(chattag) && doing_proximity && distance_to_yapper > 60f) {
            chattag = Lang.MarkMsgAsLocaleKey("plr_chattag_far");
        }
    }

    public void DoMouseHoverTooltip(ref string tooltipName_text, ref string tooltipDescription_text) {
        tooltipName_text = bodyName;
        if (!body.alive) {
            tooltipName_text += Lang.Get("plr_hover_dead");
        } else if (body.sleeping) {
            tooltipName_text += Lang.Get("plr_hover_sleeping");
        } else if (!body.conscious) {
            tooltipName_text += Lang.Get("plr_hover_unconscious");
        }

        if (log.verbose) {
            tooltipName_text += " (NPC ID: " + netId + " )";
            if (IsPlayer) {
                tooltipName_text += " (PLR ID: " + plr.playerId + " )";
            }

            tooltipDescription_text = IsPlayer
                ? ("\n!!! " + (Net.IsClient ? "CLIENT" : "SERVER") + " looking at " + (netId != 0 ? "CLIENT" : "SERVER") + "\n")
                : "";
            tooltipDescription_text += "body.alive: " + body.alive + "\n";
            tooltipDescription_text += "body.conscious: " + body.conscious + "\n";
            tooltipDescription_text += "body.standing: " + body.standing + "\n";
            tooltipDescription_text += "body.isCriticallyDying: " + body.isCriticallyDying + "\n";
            tooltipDescription_text += "body.isDying: " + body.isDying + "\n";

            if (IsPlayer) {
                if (plr.minigame_current_type == 255) {
                    tooltipDescription_text += "plr.minigame: None (255)\n";
                } else if (MinigameMPManager.TryGetMinigameTypeFromId(plr.minigame_current_type, out Type mgtype)) {
                    tooltipDescription_text += $"plr.minigame: {mgtype.Name}  ({plr.minigame_current_type})\n";
                } else {
                    tooltipDescription_text += $"plr.minigame: UNKNOWN  ({plr.minigame_current_type})\n";
                }
                tooltipDescription_text += "pointing: " + plr.is_pointingfingerat + "\n";
                tooltipDescription_text += "healing: " + plr.WoundviewTargetNetBodyId + "\n";
            }

            tooltipDescription_text += "carrying: " + carrying_person + "\n";
            tooltipDescription_text += "piggyback: " + piggybacking_on + "\n";
            tooltipDescription_text += "piggyback stack: " + CountPiggybackStackDown() + "\n";
            tooltipDescription_text += "temperature: " + Math.Round(body.temperature, 2) + "\n";

            if (IsPlayer) {
                if (Net.IsServer) {
                    tooltipDescription_text += "did_give_spawn_location: " + plr.ServerPlayerState.did_give_spawn_location + "\n";
                    tooltipDescription_text += "is_loaded_in: " + plr.ServerPlayerState.is_loaded_in + "\n";
                }
                tooltipDescription_text += "exist_time: " + (int)plr.levelPlayTime + "\n";
                if (plr.IsInMinigame) {
                    tooltipDescription_text += "mg_session: " + plr.minigame_session + "\n";
                    tooltipDescription_text += "mg_obj: " + plr.minigame_targetobject + "\n";
                    tooltipDescription_text += "mg_item: " + plr.minigame_currentItem + "\n";
                }
            }

            if (syncBody != null) {
                tooltipDescription_text += "ReadingRagdollData: " + syncBody.readingRagdollData + "\n";
                if (syncBody.TryGetOwnerTick(out ushort tick)) {
                    tooltipDescription_text += "SyncBody Tick: " + tick + "\n";
                }
            }

            CoUtils coUtils = body.GetCoUtilsInstance();
            if (coUtils?.activeOps != null && coUtils.activeOps.Count > 0) {
                tooltipDescription_text += "CoUtils:\n";
                foreach (var item in coUtils.activeOps) {
                    tooltipDescription_text += item + "\n";
                }
            }
        } else {
            tooltipDescription_text = "<sprite index=20>Interact.";
        }
    }

    public void OnReceiveSyncPacket(in BodyTeleportPacket pack, bool force = false) {
        if (body != null) {
            if (syncBody != null) {
                syncBody.ApplyBodyTeleportPacket(pack);
            } else {
                SyncBody.ApplyBodyTeleportPacket(this, pack);
            }
        }
    }

    public void Server_RemindPlayersCurrentState(bool reliable = true) {
        SetNetIgnoreTime();
        body.moveDir = Vector2.zero;
        syncBody.WriteBodyData();

        if (body.IsBodyLocal()) return;

        var writer = Net.CreateWriter(NetmsgId.CLIENT_BodyTeleportPacket);
        writer.Put(IsPlayer ? plr.playerId : ushort.MaxValue);
        writer.Put(netId);
        writer.Put(new BodyTeleportPacket(this));
        DeliveryMethod method = reliable ? DeliveryMethod.ReliableOrdered : DeliveryMethod.Unreliable;
        if (IsPlayer) {
            Net.Server_SendTo(method, writer, plr);
        } else {
            Net.Server_SendToClients(method, writer, clientIds: ServerMain.AllClientIdsExceptHost);
        }
    }
}
