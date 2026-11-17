using System;
using System.Collections.Generic;
using System.Reflection;
using System.Reflection.Emit;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// 钟乳石落石（<see cref="StalactiteDropper"/>）客户端触发上报、伤害木箱（<see cref="DamagingCrate"/>）IL 注入补丁与 <see cref="ScavTraps"/> 接收器。
/// </summary>
[HarmonyPatch(typeof(StalactiteDropper), nameof(StalactiteDropper.Drop))]
public static class StalactiteDropper_Drop_MultiplayerPatch {
    static void Prefix(StalactiteDropper __instance) {
        if (Multiplayer.IsNetworkActiveAndIsClient() && NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var si)) {
            si.SetIgnoreTimeForRoundTrip(1.0f + Vector2.Distance(__instance.transform.position, PlayerCamera.main.transform.position) * 0.3f);
        }
    }
}

[HarmonyPatch(typeof(StalactiteDropper), nameof(StalactiteDropper.OnWillRenderObject))]
public static class StalactiteDropper_OnWillRenderObject_MultiplayerPatch {
    static void Prefix(StalactiteDropper __instance) {
        if (!Multiplayer.IsNetworkActiveAndIsClient()
            || __instance.GetComponent<Rigidbody2D>().bodyType == RigidbodyType2D.Dynamic) {
            return;
        }

        RaycastHit2D hit = Physics2D.Raycast(__instance.transform.position, Vector2.down, 40f, LayerMask.GetMask("Body", "Limb"));
        if (!hit || !NetObjectRegistry.TryGetSyncInfoOrRegister(__instance, out var si)) {
            return;
        }

        bool hitLocal = (hit.collider.TryGetComponent<Limb>(out var limb) && Util.IsBodyLocal(limb.body))
            || (hit.collider.TryGetComponent<Body>(out var body) && Util.IsBodyLocal(body));
        if (!hitLocal) {
            return;
        }

        var cd = __instance.GetOrAddComponent<ScavTraps.DamagingCrateCooldownThing>();
        if (!cd.stalagtite_dropped) {
            cd.stalagtite_dropped = true;
            Multiplayer.Client_SendSimpleMessageToServer((byte)NetmsgId.SERVER_ITriggeredStalactiteDropperDropCounter, si.syncId, false);
        }
    }
}

internal static class StalactiteDropperSync {
    [ServerReceiver((byte)NetmsgId.SERVER_ITriggeredStalactiteDropperDropCounter)]
    private static void ServerReceiver_ITriggeredStalactiteDropperDropCounter(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid stalactiteSyncId);

        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var pb) || pb == null || !pb.alive) {
            return;
        }
        if (!NetObjectRegistry.TryGetSyncInfo(stalactiteSyncId, out var si) || si.go == null || !si.go.TryGetComponent<StalactiteDropper>(out var stlgt)) {
            return;
        }

        if (pb.transform.position.y < stlgt.transform.position.y
            && pb.transform.position.y + 50f > stlgt.transform.position.y
            && Mathf.Abs(pb.transform.position.x - stlgt.transform.position.x) < 5f) {
            stlgt.counting = true;
        }
    }
}

[HarmonyPatch(typeof(DamagingCrate), nameof(DamagingCrate.OnCollisionEnter2D))]
public static class DamagingCrate_OnCollisionEnter2D_MultiplayerPatch {
    static IEnumerable<CodeInstruction> Transpiler(IEnumerable<CodeInstruction> instructions, ILGenerator il) {
        var codes = new List<CodeInstruction>(instructions);
        int i = 0;
        int limbLocalIndex = 1;

        try {
            int limbStore = -1;
            for (int c = 0; c < codes.Count; c++) {
                if (codes[c].ToString().Contains("LimbFromObject")
                    && c + 1 < codes.Count
                    && (codes[c + 1].opcode == OpCodes.Stloc_0
                        || codes[c + 1].opcode == OpCodes.Stloc_1
                        || codes[c + 1].opcode == OpCodes.Stloc_2
                        || codes[c + 1].opcode == OpCodes.Stloc_3
                        || codes[c + 1].opcode == OpCodes.Stloc
                        || codes[c + 1].opcode == OpCodes.Stloc_S)) {
                    limbStore = c + 1;
                    if (codes[limbStore].opcode == OpCodes.Stloc_0) {
                        limbLocalIndex = 0;
                    } else if (codes[limbStore].opcode == OpCodes.Stloc_1) {
                        limbLocalIndex = 1;
                    } else if (codes[limbStore].opcode == OpCodes.Stloc_2) {
                        limbLocalIndex = 2;
                    } else if (codes[limbStore].opcode == OpCodes.Stloc_3) {
                        limbLocalIndex = 3;
                    } else if (codes[limbStore].operand is LocalBuilder lb) {
                        limbLocalIndex = lb.LocalIndex;
                    } else if (codes[limbStore].operand is int idx) {
                        limbLocalIndex = idx;
                    } else if (codes[limbStore].operand is byte b) {
                        limbLocalIndex = b;
                    }
                    break;
                }
            }
            if (limbStore < 0) {
                throw new InvalidOperationException("LimbFromObject store not found");
            }

            i = -1;
            for (int c = limbStore + 1; c + 2 < codes.Count; c++) {
                if (codes[c].opcode != OpCodes.Ldarg_0) {
                    continue;
                }
                string fld = codes[c + 1].ToString();
                if (!(fld.Contains("DamagingCrate") && fld.Contains("type"))) {
                    continue;
                }
                if (codes[c + 2].opcode != OpCodes.Ldc_I4_1) {
                    continue;
                }
                i = c - 1;
                break;
            }
            if (i < 0) {
                throw new InvalidOperationException("type==1 damage branch not found after LimbFromObject");
            }

            FieldInfo prevFrameSpeedField = null;
            for (int c = 0; c < codes.Count; c++) {
                if (codes[c].opcode == OpCodes.Ldfld || codes[c].opcode == OpCodes.Ldflda) {
                    if (codes[c].ToString().Contains("prevFrameSpeed") && codes[c].operand is FieldInfo fi) {
                        prevFrameSpeedField = fi;
                        break;
                    }
                }
            }
            prevFrameSpeedField ??= AccessTools.Field(typeof(DamagingCrate), "prevFrameSpeed");
            if (prevFrameSpeedField == null) {
                throw new InvalidOperationException("prevFrameSpeed field missing");
            }

            var newInstructions = new List<CodeInstruction> {
                new CodeInstruction(OpCodes.Ldarg_0),
                limbLocalIndex <= 3
                    ? new CodeInstruction(limbLocalIndex == 0 ? OpCodes.Ldloc_0
                        : limbLocalIndex == 1 ? OpCodes.Ldloc_1
                        : limbLocalIndex == 2 ? OpCodes.Ldloc_2
                        : OpCodes.Ldloc_3)
                    : new CodeInstruction(OpCodes.Ldloc_S, limbLocalIndex),
                new CodeInstruction(OpCodes.Ldarg_0),
                new CodeInstruction(OpCodes.Ldfld, prevFrameSpeedField),
                new CodeInstruction(
                    OpCodes.Call,
                    AccessTools.Method(typeof(ScavTraps), nameof(ScavTraps.DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck))),
            };

            int insertAt = i + 1;
            var endOfPatchLabel = il.DefineLabel();
            codes[insertAt].labels.Add(endOfPatchLabel);
            newInstructions.Add(new CodeInstruction(OpCodes.Brfalse_S, endOfPatchLabel));
            newInstructions.Add(new CodeInstruction(OpCodes.Ret));
            codes.InsertRange(insertAt, newInstructions);

            ModLog.Debug($"DamagingCrate OnCollisionEnter2D transpiler applied (limbLocal={limbLocalIndex}, insertAt={insertAt})", LogDomain.Combat);
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Combat, $"DamagingCrateTranspiler(idx={i}/{codes.Count})", ex);
        }
        return codes;
    }
}

public class ScavTraps : CasualtiesTogetherSingleton {
    public class DamagingCrateCooldownThing : MonoBehaviour {
        public bool stalagtite_dropped = false;
        public double last_hit = -10000000;
    }

    static readonly Dictionary<int, double> hostCrateHit = new();

    public static bool HostCrateReady(knetid crate, knetid player) {
        int key = (crate.id << 16) ^ player.id;
        double now = Time.realtimeSinceStartupAsDouble;
        if (hostCrateHit.TryGetValue(key, out var last) && now - last < 1.0) {
            return false;
        }
        hostCrateHit[key] = now;
        if (hostCrateHit.Count > 400) {
            hostCrateHit.Clear();
        }
        return true;
    }

    public static bool DamagingCrate_OnCollisionEnter2D_StolenWithFirstCheck(object rawCrate, Limb limb, Vector2 prevFrameSpeed) {
        var dmgcrate = rawCrate as DamagingCrate;
        if (dmgcrate == null || limb == null) {
            return true;
        }

        bool islocal = Util.IsBodyLocal(limb.body);
        if (!islocal) {
            limb.body.Ragdoll();
            return true;
        }

        if (Net.IsRunning && !NetObjectRegistry.TryGetSyncInfoOrRegister(dmgcrate, out _) && Net.IsClient) {
            return true;
        }

        var cd = dmgcrate.GetOrAddComponent<DamagingCrateCooldownThing>();
        double timePassed = Time.realtimeSinceStartupAsDouble - cd.last_hit;
        cd.last_hit = Time.realtimeSinceStartupAsDouble;
        if (timePassed < 1.0) {
            return true;
        }

        DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(dmgcrate, limb, prevFrameSpeed.magnitude);
        return true;
    }

    public static void DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(DamagingCrate dmgcrate, Limb limb, float prevspeed) {
        bool islocal = Util.IsBodyLocal(limb.body);

        if (dmgcrate.type == 1) {
            if (limb.isHead) {
                limb.body.consciousness = 20f / limb.GetArmorReduction();
            }
            limb.muscleHealth -= prevspeed * 0.75f / limb.GetArmorReduction();
            limb.skinHealth -= prevspeed / limb.GetArmorReduction();
            limb.bleedAmount += prevspeed * 0.1f / limb.GetArmorReduction();
            limb.DamageWearables(0.1f);
        } else {
            if (limb.isHead) {
                limb.body.consciousness = 0f / limb.GetArmorReduction();
            }
            limb.muscleHealth -= prevspeed * 1.25f / limb.GetArmorReduction();
            limb.skinHealth -= prevspeed * 0.6f / limb.GetArmorReduction();
            limb.bleedAmount += prevspeed * 0.05f / limb.GetArmorReduction();
            if (Random.value < 0.5f && prevspeed > 25f) {
                limb.Dislocate();
            }
            if (Random.value < 0.5f && prevspeed > 45f) {
                limb.BreakBone();
            }
            limb.DamageWearables(0.25f);
        }

        Sound.Play(dmgcrate.stalactiteHitSound, dmgcrate.transform.position, twoDimensional: islocal);
        if (Net.IsRunning) {
            if (limb.TryGetNetBody(out var nb) && dmgcrate.TryGetComponent(out BuildingEntity build)) {
                nb.OnDamageEvent(new BodyDamageEvent(build, nb, prevspeed));
            }

            if (Net.IsClient) {
                if (islocal && NetObjectRegistry.TryGetSyncInfoOrRegister(dmgcrate, out var si)) {
                    if (limb.TryGetNetBody(out var locnb)) {
                        locnb.SetNetHealthSyncIgnoreTime(0.05f);
                    }

                    var writer = Net.CreateWriter(NetmsgId.SERVER_DamagingCrate_OnCollisionEnter_D_hit);
                    writer.Put(si.syncId);
                    writer.Put(prevspeed);
                    writer.Put(Util.GetLimbIndex(limb));
                    Net.Client_Send(DeliveryMethod.ReliableUnordered, writer);
                }
            } else if (limb.TryGetNetBody(out var pb)) {
                ServerMain.Server_AnnounceSound(dmgcrate.transform.position, dmgcrate.stalactiteHitSound, ServerMain.GetListOfClientIdsExceptThisAndHost(pb.netId));
                if (dmgcrate.TryGetComponent<StalactiteDropper>(out var stalactite)
                    && dmgcrate.GetComponent<Rigidbody2D>().bodyType != RigidbodyType2D.Dynamic) {
                    stalactite.Drop();
                }
            }
        }

        limb.body.Ragdoll();
    }

    [ServerReceiver((byte)NetmsgId.SERVER_DamagingCrate_OnCollisionEnter_D_hit)]
    static void ServerReceiver_ClientGotHitBy_DamagingCrate_OnCollisionEnter2D(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid crateSyncId);
        reader.Get(out float prevspeed);
        reader.Get(out byte limbIndex);

        if (!MedicalSync.TryGetPlayerAndLimb(clientId, limbIndex, out var pb, out var limb)
            || !pb.alive
            || !Util.IsFinite(prevspeed)
            || prevspeed <= 4f
            || !NetObjectRegistry.TryGetSyncInfo(crateSyncId, out var si)
            || si.go == null
            || !si.go.TryGetComponent(out DamagingCrate dmgcrate)) {
            return;
        }

        if (!KM.DistanceCheck(dmgcrate.transform.position, limb.GetPosition(), 32f)) {
            MpCommsTrace.HostRejected(LogDomain.Combat, clientId.id, (byte)NetmsgId.SERVER_DamagingCrate_OnCollisionEnter_D_hit, crateSyncId.id, "too far");
            return;
        }
        if (!HostCrateReady(crateSyncId, clientId)) {
            return;
        }

        DamagingCrate_OnCollisionEnter2D_TheDamagingPartStolen(dmgcrate, limb, Mathf.Abs(prevspeed));
        if (limb.TryGetNetBody(out var hitNb)) {
            TrapClaim.Publish(hitNb);
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_SoundCannonISCHARGING____)]
    static void ClientReceiver__SoundCannonISCHARGING(knetid _, NetDataReader reader) {
        reader.Get(out knetid syncid);
        if (NetObjectRegistry.TryGetSyncInfo(syncid, out var si) && si.go != null && si.go.TryGetComponent(out TrackerSoundCannon tr) && !tr.spent) {
            tr.charging = true;
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_MineStepped)]
    static void ClientReceiver__MineStepped(knetid _, NetDataReader reader) {
        reader.Get(out Vector2 minepos);

        Collider2D[] array = Physics2D.OverlapCircleAll(minepos, 2.0f);
        foreach (Collider2D obj in array) {
            if (obj.TryGetComponent(out MineScript component)) {
                if (!component.pressed) {
                    Sound.Play("mine", minepos);
                    component.GetComponent<SpriteRenderer>().sprite = component.pressedSprite;
                    component.exploded = true;
                }
                break;
            }
        }
    }
}
