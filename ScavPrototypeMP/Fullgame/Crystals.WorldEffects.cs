#if FULLGAME
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
/// 治愈、动能、变质、拟态、羞怯、易碎、败血、传送、不稳定、滴液与温控水晶词条联机同步补丁。
/// </summary>
[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Touched))]
internal static class CrystalHealing_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);
    }

    static bool Prefix(CrystalHealing __instance, GameObject touched) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance) || CrystalLooksSpent(__instance)) {
            return false;
        }
        if (__instance.crystal == null || !Utils.GetBody(touched, out Body body) || body == null || !body.alive) {
            return false;
        }
        CrystalMpGates.MarkActivated(__instance);
        bool mine = PlayerCamera.main != null && body == PlayerCamera.main.body;
        if (mine) {
            PlayerCamera.main.StartCoroutine("FlashBrief");
            Sound.Play("crystalheal", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            if (!Net.IsServer && NetObjectRegistry.TryGetSyncInfo(__instance.crystal.gameObject, out SyncInfo si) && si != null) {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CustomWireCommands.HealingTouch + " " + si.syncId.id.ToString());
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }
        if (Net.IsServer) {
            ApplyHeal(body);
            __instance.crystal.SetColor(Color.black);
            if (body.TryGetNetBody(out NetBody nb) && nb != null) {
                MedicalSync.Server_QueueSendCharacterHealth(nb, true);
            }
            CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
        }
        return false;
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.HealingTouch] = (plr, cmd, args) => {
            if (!Net.IsServer || plr == null || plr.body == null || args == null || args.Length < 2) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour crystal) || crystal.effects == null) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, crystal.transform.position) > GameplayRanges.TraderMenuKeepOpenDistance) {
                return;
            }
            for (int i = 0; i < crystal.effects.Count; i++) {
                if (crystal.effects[i] is not CrystalHealing healing) {
                    continue;
                }
                if (CrystalMpGates.SkipOneShotFx(healing) || CrystalLooksSpent(healing) || !plr.body.alive) {
                    return;
                }
                ApplyHeal(plr.body);
                if (healing.crystal != null) {
                    healing.crystal.SetColor(Color.black);
                }
                CrystalMpGates.MarkActivated(healing);
                if (plr.body.TryGetNetBody(out NetBody nb) && nb != null) {
                    MedicalSync.Server_QueueSendCharacterHealth(nb, true);
                }
                CrystalAuthority.HostCaptureAndBroadcast(crystal.gameObject);
                return;
            }
        };
    }

    static bool CrystalLooksSpent(CrystalHealing healing) {
        if (healing == null || healing.crystal == null || healing.crystal.sprite == null) {
            return false;
        }
        Color c = healing.crystal.sprite.color;
        return c.r < 0.08f && c.g < 0.08f && c.b < 0.08f;
    }

    static void ApplyHeal(Body body) {
        if (body.talker != null) {
            body.talker.Talk(Locale.GetCharacter("magicheal"));
        }
        body.brainHealth = Mathf.Lerp(body.brainHealth, 100f, 0.1f);
        if (body.limbs != null) {
            for (int i = 0; i < body.limbs.Length; i++) {
                Limb limb = body.limbs[i];
                if (limb == null) {
                    continue;
                }
                limb.bleedAmount -= 10f;
                limb.muscleHealth += 25f;
                limb.skinHealth += 25f;
                limb.infectionAmount -= 40f;
                limb.pain = 0f;
            }
        }
        body.happiness += 2f;
        body.internalBleeding *= 0.5f;
        body.septicShock *= 0.5f;
        body.sicknessAmount *= 0.66f;
        body.strokeAmount *= 0.1f;
        body.temperature = 37f;
        body.thirst = Mathf.Lerp(body.thirst, 125f, 0.25f);
        body.hunger = Mathf.Lerp(body.hunger, 125f, 0.25f);
        body.radiationSickness *= 0.75f;
        body.fibrillationProgress = 0f;
        body.bloodVolume = Mathf.Lerp(body.bloodVolume, 100f, 0.1f);
    }
}

[HarmonyPatch(typeof(CrystalHealing), nameof(CrystalHealing.Update))]
internal static class CrystalHealing_Update_DeadLastStand_MultiplayerPatch {
    const float ReviveRange = GameplayRanges.CraftGroundScanRadius;

    static void Postfix(CrystalHealing __instance) {
        if (!Net.IsRunning || !Net.IsServer || __instance == null || __instance.crystal == null) {
            return;
        }
        if (Traverse.Create(__instance).Field("activated").GetValue<bool>() || !Multiplayer.rules.LastStandAllowed) {
            return;
        }

        Vector2 crystalPos = __instance.crystal.transform.position;
        Body revive = null;
        float best = ReviveRange;
        foreach (var plr in ScavPlayer.AllDeadPlayers) {
            if (plr == null || plr.body == null) {
                continue;
            }
            Body body = plr.body;
            if (body.alive || body.succesfullyRolledLastStand || body.triedRollingLastStand) {
                continue;
            }
            float dist = Vector2.Distance(body.transform.position, crystalPos);
            if (dist <= best) {
                best = dist;
                revive = body;
            }
        }
        if (revive == null) {
            return;
        }

        Traverse.Create(__instance).Field("activated").SetValue(true);
        __instance.crystal.SetColor(Color.black);
        Sound.Play("crystalheal", __instance.crystal.transform.position, twoDimensional: false, pitchShift: false);

        CrystalLocalFx.Begin(revive, __instance.crystal);
        try {
            revive.ForceLastStand();
        } finally {
            CrystalLocalFx.End();
        }

        if (__instance.crystal.build != null) {
            __instance.crystal.build.SetHealth(0f);
        }
        CrystalAuthority.HostCaptureAndBroadcast(__instance.crystal.gameObject);
    }
}

[HarmonyPatch(typeof(CrystalKinetic), nameof(CrystalKinetic.Touched))]
internal static class CrystalKinetic_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
        return TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
    }

    static bool Prefix(CrystalKinetic __instance, GameObject touched) {
        if (!Net.IsRunning || __instance.crystal == null || Net.IsServer) {
            return true;
        }
        return Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body);
    }
}

[HarmonyPatch(typeof(CrystalMetamorphic), nameof(CrystalMetamorphic.Touched))]
internal static class CrystalMetamorphic_Touched_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        return TranspilerHelpers.PatchCoroutines(instructions, __originalMethod, null);
    }

    static bool Prefix(CrystalMetamorphic __instance, GameObject touched) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.HostSimOnly(__instance)) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance)) {
            return false;
        }
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            PlayerCamera.main.StartCoroutine("FlashBrief");
            Sound.Play("crystalenemylaugh", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalMimic), nameof(CrystalMimic.Touched))]
internal static class CrystalMimic_Touched_MultiplayerPatch {
    static bool Prefix(CrystalMimic __instance, GameObject touched) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.HostSimOnly(__instance)) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance)) {
            return false;
        }
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body)) {
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalMimic), nameof(CrystalMimic.Hit))]
internal static class CrystalMimic_Hit_MultiplayerPatch {
    static bool Prefix(CrystalMimic __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.HostSimOnly(__instance)) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance)) {
            return false;
        }
        Body attacker = CrystalLocalFx.TriggerBody ?? Body_Attack_MultiplayerPatch.LastAttacker;
        if (attacker != null && Util.IsBodyLocal(attacker)) {
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalShy), nameof(CrystalShy.Touched))]
internal static class CrystalShy_Touched_MultiplayerPatch {
    static bool Prefix(CrystalShy __instance, GameObject touched) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.HostSimOnly(__instance)) {
            return true;
        }
        if (CrystalMpGates.SkipOneShotFx(__instance)) {
            return false;
        }
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            Sound.Play("observerlaugh", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false);
            CrystalMpGates.MarkActivated(__instance);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalFragile), nameof(CrystalFragile.Touched))]
internal static class CrystalFragile_Touched_MultiplayerPatch {
    static bool Prefix(CrystalFragile __instance) {
        return CrystalMpGates.HostSimOnly(__instance);
    }
}

[HarmonyPatch(typeof(CrystalSeptic), nameof(CrystalSeptic.Update))]
internal static class CrystalSeptic_Update_MultiplayerPatch {
    static readonly Dictionary<ushort, float> nextHealthSend = new Dictionary<ushort, float>();

    static bool Prefix(CrystalSeptic __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (__instance.crystal == null || !Net.IsServer) {
            return false;
        }

        float size = __instance.crystal.crystalSize;
        if (size < 0.05f) {
            size = 1f;
        }
        float range = 12f * size;
        float add = Time.deltaTime * 0.5f;
        float now = Time.unscaledTime;
        Vector2 pos = __instance.crystal.transform.position;
        foreach (NetBody nb in NetBody.GetBodiesInRadius(pos, range)) {
            Body body = nb != null ? nb.body : null;
            if (body == null || !body.alive) {
                continue;
            }
            body.septicShock += add;
            ushort id = nb.netId.id;
            if (nextHealthSend.TryGetValue(id, out float at) && now < at) {
                continue;
            }
            nextHealthSend[id] = now + 0.5f;
            MedicalSync.Server_QueueSendCharacterHealth(nb, true);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalTeleport), nameof(CrystalTeleport.Touched))]
internal static class CrystalTeleport_Touched_MultiplayerPatch {
    static bool Prefix(CrystalTeleport __instance, GameObject touched) {
        if (!Net.IsRunning || __instance.crystal == null) {
            return true;
        }
        if (!Utils.GetBody(touched, out Body body) || body == null) {
            return false;
        }
        if (Net.IsServer) {
            HostTeleportBody(__instance.crystal, body);
        }
        if (Util.IsBodyLocal(body)) {
            if (PlayerCamera.main != null) {
                PlayerCamera.main.StartCoroutine("FlashBrief");
            }
            Sound.Play("observerlaugh", Vector2.zero, twoDimensional: true, pitchShift: false, null, 1f, 1f, noReverb: true, ignoreMixer: true);
        }
        return false;
    }

    static void HostTeleportBody(CrystalBehaviour crystal, Body body) {
        if (crystal == null || body == null || WorldGeneration.world == null) {
            return;
        }
        int halfWidth = (int)WorldGeneration.world.halfWidth;
        int mask = PhysicsLayers.GroundMask;
        for (int i = 0; i < 1000; i++) {
            Vector2 pos = (Vector2)crystal.transform.position + UnityEngine.Random.insideUnitCircle * 64f;
            if (pos.x < -halfWidth + 10f) {
                pos.x = -halfWidth + 10f;
            }
            if (pos.x > halfWidth - 10f) {
                pos.x = halfWidth - 10f;
            }
            if (Physics2D.OverlapPoint(pos, mask)) {
                continue;
            }
            RaycastHit2D hit = Physics2D.Raycast(pos, Vector2.down, 999f, mask);
            if (!hit) {
                continue;
            }
            Vector2 dest = hit.point + Vector2.up * 3f;
            body.consciousness = 100f;
            body.shock = 0f;
            if (body.rb != null) {
                body.rb.velocity = Vector2.zero;
            }
            body.Stand(false);
            if (body.TryGetNetBody(out var nb) && nb != null) {
                nb.SetBodyPosition(dest);
                if (body.rb != null) {
                    body.rb.velocity = Vector2.zero;
                }
                if (!nb.IsBodyLocal()) {
                    nb.Server_RemindPlayersCurrentState();
                }
            } else {
                body.transform.position = dest;
            }
            break;
        }
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Touched))]
internal static class CrystalUnstable_Touched_HostSim_MultiplayerPatch {
    static bool Prefix(CrystalUnstable __instance, GameObject touched) {
        if (!Net.IsRunning || Net.IsServer) {
            return true;
        }
        if (Utils.GetBody(touched, out Body body) && Util.IsBodyLocal(body) && __instance.crystal != null) {
            body.eyeScareTime = 3f;
            if (body.talker != null) {
                body.talker.Talk("..!");
            }
            CrystalDelayedFx.OnUnstableArmed(__instance, playTick: true);
            if (NetObjectRegistry.TryGetSyncInfo(__instance.crystal.gameObject, out SyncInfo si) && si != null) {
                NetDataWriter writer = Multiplayer.CreateNamedWriter(CustomWireCommands.ChannelCustomCommand);
                writer.Put(CustomWireCommands.UnstableArm + " " + si.syncId.id.ToString());
                Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
            }
        }
        return false;
    }

    [CTPostInit]
    static void Init() {
        ServerMain.ServerCustomCommandsDict[CustomWireCommands.UnstableArm] = (plr, cmd, args) => {
            if (!Net.IsServer || args == null || args.Length < 2 || plr == null || plr.body == null || !plr.body.alive) {
                return;
            }
            if (!ushort.TryParse(args[1], out ushort syncId)) {
                return;
            }
            if (!NetObjectRegistry.TryGetSyncInfo(syncId, out SyncInfo si) || si == null || si.go == null) {
                return;
            }
            if (!si.go.TryGetComponent(out CrystalBehaviour crystal) || crystal.effects == null) {
                return;
            }
            if (Vector2.Distance(plr.body.transform.position, crystal.transform.position) > GameplayRanges.ObjectNearbyRadius) {
                return;
            }
            for (int i = 0; i < crystal.effects.Count; i++) {
                if (crystal.effects[i] is not CrystalUnstable unstable) {
                    continue;
                }
                if (Traverse.Create(unstable).Field("timerStarted").GetValue<bool>()) {
                    return;
                }
                CrystalDelayedFx.OnUnstableArmed(unstable, playTick: false);
                CrystalDelayedFx.HostBroadcastUnstableArm(unstable);
                return;
            }
        };
    }

    static void Postfix(CrystalUnstable __instance) {
        if (Net.IsRunning && Net.IsServer) {
            CrystalDelayedFx.HostBroadcastUnstableArm(__instance);
        }
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Hit))]
internal static class CrystalUnstable_Hit_HostSim_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(
            IEnumerable<CodeInstruction> instructions,
            ILGenerator generator,
            MethodBase __originalMethod) {
        var codes = BodyGetterOverrider.TranspileAll(instructions, generator, __originalMethod.DeclaringType);
        codes = TranspilerHelpers.PatchShakers(codes, generator, __originalMethod);
        return TranspilerHelpers.PatchCoroutines(codes, __originalMethod, null);
    }

    static bool Prefix(CrystalUnstable __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        if (CrystalMpGates.HostSimOnly(__instance)) {
            return true;
        }
        Body attacker = CrystalLocalFx.TriggerBody ?? Body_Attack_MultiplayerPatch.LastAttacker;
        if (attacker != null && Util.IsBodyLocal(attacker) && __instance.crystal != null) {
            Sound.Play("crystaltick", __instance.crystal.transform.position, twoDimensional: true, pitchShift: false, __instance.crystal.transform);
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalUnstable), nameof(CrystalUnstable.Update))]
internal static class CrystalUnstable_Update_HostSim_MultiplayerPatch {
    static bool Prefix(CrystalUnstable __instance) {
        if (Net.IsRunning && Net.IsServer && __instance != null && __instance.crystal != null && __instance.crystal.crystalSize < 0.2f) {
            __instance.crystal.crystalSize = 1f;
        }
        return !Net.IsRunning || Net.IsServer;
    }
}

[HarmonyPatch(typeof(CrystalDripping), nameof(CrystalDripping.Update))]
internal static class CrystalDripping_Update_HostSim_MultiplayerPatch {
    static bool Prefix() {
        return !Net.IsRunning || Net.IsServer;
    }
}

[HarmonyPatch(typeof(CrystalTemperature), "Start")]
public static class CrystalTemperature_Start_MultiplayerPatch {
    static void Postfix(CrystalTemperature __instance) {
        if (Net.IsRunning && __instance != null && __instance.crystal != null) {
            __instance.crystal.gameObject.GetOrAddComponent<HeaterMultiplayerRewrite>();
        }
    }
}
#endif
