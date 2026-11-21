using System.Linq;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 战斗系统通用工具、肢体断肢安全判定、尸块掉落控制与枪械模式/弹匣/保险丝服务端接收器。
/// <para>
/// - 手持非武器物品预测与确认见 <c>CombatStuff.ItemUse.cs</c>。<br/>
/// - 近战攻击判定、击退与方块/建筑伤害见 <c>CombatStuff.Melee.cs</c>。<br/>
/// - 枪械追踪器与开火同步见 <c>CombatStuff.Guns.cs</c>。<br/>
/// - 弹道命中、炮塔射击与散弹致残结算见 <c>CombatStuff.Projectiles.cs</c>。
/// </para>
/// </summary>
internal class CombatStuff : CasualtiesTogetherSingleton {
    public static AttackInfo default_unarmed_attackinfo => new AttackInfo {
        damage = 30f,
        structuralDamage = 20f,
        distance = 4.5f,
        knockBack = 200f,
        cooldown = 0.2f,
        attackAnim = Resources.Load<GameObject>("ClawAnim"),
        staminaUse = 1f,
        piercing = false,
        swingSounds = new string[4] { "BSSwing1", "BSSwing2", "BSSwing3", "BSSwing4" },
        volume = 0.3f,
        rotateAmount = 11.5f,
        unarmed = true
    };

    /// <summary>
    /// 校验目标肢体是否允许被断肢：
    /// 存活角色禁止截断躯干/头部，且手臂与腿部各自最多允许失去一侧以保持基本可玩性；
    /// 死亡角色须先卸除四肢与头部后才允许分解躯干，防止物理关节链断裂异常。
    /// </summary>
    public static bool CheckIfItsOkeyToDismemberThatLimb(Limb limb) {
        if (limb == null || limb.body == null) {
            return false;
        }
        int limbIndex = Util.GetLimbIndex(limb);
        if (limb.isHead && limb.body.IsDeadOrCriticallyDying()) {
            return true;
        }
        if (limbIndex <= (int)LimbIndex.LowerTorso) {
            if (limb.body.alive) {
                return false;
            }
            if (!limb.isHead) {
                var limbs = limb.body.limbs;
                if (!limbs[(int)LimbIndex.PrimaryForearm].dismembered
                    || !limbs[(int)LimbIndex.SecondaryForearm].dismembered
                    || !limbs[(int)LimbIndex.FrontCalf].dismembered
                    || !limbs[(int)LimbIndex.BackCalf].dismembered) {
                    return false;
                }

                if (limb.isVital) {
                    if (!limbs[(int)LimbIndex.Head].dismembered) {
                        return false;
                    }
                } else if (!limbs[(int)LimbIndex.UpperTorso].dismembered) {
                    return false;
                }
            }
        }
        return true;
    }

    /// <summary>
    /// 拆解尸体时优先重定向到尚未断离的远端四肢或头部，避免直接打碎躯干导致肢体产出丢失。
    /// </summary>
    public static Limb CannibalismGetFarthestLimbToBeEfficient(Limb limb) {
        if (limb == null || limb.body == null) {
            return limb;
        }
        int limbIndex = Util.GetLimbIndex(limb);
        if (limb.isHead) {
            return limb;
        }
        if (limbIndex <= (int)LimbIndex.LowerTorso) {
            bool foundLimb = false;
            int total = limb.body.limbs.Length;
            for (int i = (int)LimbIndex.PrimaryUpperArm; i < total; i++) {
                Limb candidate = limb.body.limbs[i];
                bool isRootLimb = i == (int)LimbIndex.PrimaryUpperArm
                    || i == (int)LimbIndex.SecondaryUpperArm
                    || i == (int)LimbIndex.FrontThigh
                    || i == (int)LimbIndex.BackThigh;
                if (isRootLimb && !candidate.dismembered) {
                    limb = candidate;
                    foundLimb = true;
                    break;
                }
            }

            if (!foundLimb) {
                if (limb.isVital) {
                    if (!limb.body.limbs[(int)LimbIndex.Head].dismembered) {
                        return limb.body.limbs[(int)LimbIndex.Head];
                    }
                } else if (!limb.body.limbs[(int)LimbIndex.UpperTorso].dismembered) {
                    return limb.body.limbs[(int)LimbIndex.UpperTorso];
                }
            }
        } else {
            float lastHeartDist = limb.distanceToHeart;
            foreach (Limb candidate in limb.body.limbs) {
                if (candidate.distanceToHeart > lastHeartDist && !candidate.dismembered) {
                    limb = candidate;
                    lastHeartDist = candidate.distanceToHeart;
                }
            }
        }
        return limb;
    }

    public static void BetterDismember(Limb limb, bool do_checks_to_not_break_the_game = true) {
        if (limb == null || limb.dismembered) {
            return;
        }
        if (do_checks_to_not_break_the_game && !CheckIfItsOkeyToDismemberThatLimb(limb)) {
            return;
        }

        if (limb.body.TryGetComponent(out NetBody npc)) {
            bool notAccidental = limb.body.IsDeadOrCriticallyDying();

            foreach (Limb lowerLimb in TourniquetScript.GetLowerLimbs(limb)) {
                lowerLimb.shrapnel = 0;
                if (lowerLimb.dismembered) {
                    continue;
                }

                if (npc.butchering_dropmeat_skip_counter > 0) {
                    npc.butchering_dropmeat_skip_counter = 0;
                    continue;
                }

                if (Util.GetLimbIndex(lowerLimb) <= (int)LimbIndex.LowerTorso) {
                    var org = Utils.Create("internalorgans", (Vector2)lowerLimb.transform.position + Vector2.up, 0f);
                    if (notAccidental) {
                        org.AddComponent<FreshItemDrop>();
                    }
                    org.GetComponent<Item>().rb.velocity = lowerLimb.rb.velocity * 0.5f + Random.insideUnitCircle * 7f;
                }
                var flesh = Utils.Create("experimentflesh", (Vector2)lowerLimb.transform.position + Vector2.up, 0f);
                if (notAccidental) {
                    flesh.AddComponent<FreshItemDrop>();
                }
                flesh.GetComponent<Item>().rb.velocity = lowerLimb.rb.velocity * 0.5f + Random.insideUnitCircle * 7f;

                if (!limb.body.alive) {
                    npc.butchering_dropmeat_skip_counter++;
                }
            }
        }

        if (limb.isHead) {
            limb.body.brainHealth = 0f;
            limb.body.heartRate = 0f;
            limb.body.bothEyesGone = false;
            limb.body.eyeGone = false;
            limb.body.disfigured = false;
        }

        limb.Dismember();
    }

    public static bool ItemIsUsedForAttacking(Item item) {
        return item != null && item.Stats != null && (item.Stats.HasTag("tool") || item.Stats.HasTag("gun"));
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunRack)]
    static void ServerReceiver__GunRack(knetid clientId, NetDataReader reader) {
        reader.Get(out bool racked);
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)) {
            gun.racked = racked;
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunEjectMag)]
    static void ServerReceiver__GunEjectMag(knetid clientId, NetDataReader reader) {
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)) {
            gun.UnloadMag();
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }
    }

    [ServerReceiver((byte)NetmsgId.SERVER_PlayerCamera_GunToggleSafety)]
    static void ServerReceiver__GunToggleSafety(knetid clientId, NetDataReader reader) {
        reader.Get(out bool safe);
        if (ScavPlayer.TryGetPlayerFromClientId(clientId, out var gunner) && gunner.HasGunEquipped(out var gun)) {
            gun.safe = safe;
            NetObjectRegistry.SafeNewGO(gun.gameObject);
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunRack))]
public static class PlayerCamera_GunRack_MultiplayerPatch {
    static void Postfix() {
        if (Multiplayer.IsNetworkActiveAndIsClient() && ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
            var tracker = gun.GetComponent<SyncInfoGameObjectTracker>();
            if (tracker != null) {
                var gst = gun.GetOrAddComponent<TrackerGunScript>();
                ModLog.Debug($"Local GunRack {tracker.syncInfo}", LogDomain.Combat);
                gst.send_racked_state = true;
                tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.5f);
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunEjectMag))]
public static class PlayerCamera_GunEjectMag_MultiplayerPatch {
    static void Postfix() {
        if (Multiplayer.IsNetworkActiveAndIsClient() && ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
            var tracker = gun.GetComponent<SyncInfoGameObjectTracker>();
            if (tracker != null) {
                ModLog.Debug($"Local GunEjectMag {tracker.syncInfo}", LogDomain.Combat);
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunEjectMag, true);
                tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.4f);
            }
        }
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.GunToggleSafety))]
public static class PlayerCamera_GunToggleSafety_MultiplayerPatch {
    static void Postfix() {
        if (Multiplayer.IsNetworkActiveAndIsClient() && ScavPlayer.LocalPlayer.HasGunEquipped(out var gun)) {
            var tracker = gun.GetOrAddComponent<SyncInfoGameObjectTracker>();
            if (tracker != null) {
                ModLog.Debug("Local GunToggleSafety", LogDomain.Combat);
                Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_PlayerCamera_GunToggleSafety, gun.safe, true);
                tracker.syncInfo.SetIgnoreTimeForRoundTrip(0.4f);
            }
        }
    }
}
