using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.MpConstants;
using UnityEngine;
using Random = UnityEngine.Random;

namespace Together;

/// <summary>
/// <see cref="TrapClaim"/> 的冰冻无人机射线、碎玻璃踩踏认领、以及 <c>SERVER_TrapClaim</c> / <c>CLIENT_TrapState</c> 收包路由与服务端核验。
/// </summary>
internal static partial class TrapClaim {
#if FULLGAME
    static readonly Dictionary<int, float> cryoClaimed = new();

    internal static void ClientCryoFollowHost(LaserDroneScript drone) {
        if (drone == null || !NetObjectRegistry.TryGetSyncInfo(drone, out var si)) {
            return;
        }
        Vector2 hostPos = si._position.InterpolatedValue;
        drone.transform.position = hostPos;
        if (drone.TryGetComponent(out Rigidbody2D rb) && rb != null) {
            rb.simulated = false;
            rb.position = hostPos;
            rb.velocity = Vector2.zero;
        }
        bool hostOpen = si.specialdata1.Value != 0;
        if (drone.open != hostOpen) {
            ApplyCryoOpenVisual(drone, hostOpen);
        }
        drone.openAmount = Mathf.MoveTowards(drone.openAmount, hostOpen ? 1f : 0f, Time.deltaTime * 2f);
    }

    static void ApplyCryoOpenVisual(LaserDroneScript drone, bool open) {
        if (drone == null) {
            return;
        }
        if (open && drone.hidden) {
            drone.hidden = false;
            if (drone.render != null && drone.openSprite != null) {
                drone.render.sprite = drone.openSprite;
            }
            BuildingEntity bld = drone.GetComponent<BuildingEntity>();
            if (bld != null) {
                bld.ignoreBodyOptimize = true;
                bld.fullName = Locale.GetBuilding("laserdrone2");
                bld.description = Locale.GetBuilding("laserdronedsc2");
            }
            LookTarget look = drone.GetComponent<LookTarget>();
            if (look != null) {
                look.enabled = true;
            }
        }
        drone.open = open;
    }

    internal static void HostCryoFire(LaserDroneScript drone) {
        if (drone == null || drone.barrel == null) {
            return;
        }
        Traverse tr = Traverse.Create(drone);
        if (tr.Field("overheat").GetValue<bool>()) {
            return;
        }
        drone.AddHeat(30f);
        Vector2 origin = drone.barrel.position;
        Vector2 dir = drone.barrel.up;
        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, dir, CryoReach, LayerMask.GetMask("Ground", "Body", "Limb"));
        Vector2 end = origin + dir * CryoReach;
        float roll = Random.Range(0.85f, 1.15f);
        float buildDmg = roll * 50f;
        float limbDmg = roll * 15f;
        Body localVictim = null;
        for (int i = 0; i < hits.Length; i++) {
            RaycastHit2D hit = hits[i];
            if (hit.transform == drone.transform) {
                continue;
            }
            if (hit.collider.TryGetComponent(out BuildingEntity build) && !build.cantHit) {
                build.health -= buildDmg;
                if (build.animal) {
                    build.gameObject.SendMessage("AnimalHit", buildDmg);
                    Sound.Play(VanillaAssetKeys.SoundGore + Random.Range(1, 6), hit.point);
                }
                WorldGeneration.CreateDamageNumber(hit.point, (int)buildDmg);
            } else if (hit.collider.gameObject.layer == 6) {
                WorldGeneration.world.DamageBlock(hit.point + dir * 0.5f, buildDmg);
                WorldGeneration.CreateDamageNumber(hit.point, (int)buildDmg);
                end = hit.point;
                break;
            }
            if (hit.rigidbody) {
                hit.rigidbody.AddForceAtPosition(dir * buildDmg, hit.point, ForceMode2D.Impulse);
            }
            Body hitBody = null;
            if (hit.collider.TryGetComponent(out Limb limbHit)) {
                hitBody = limbHit.body;
            }
            if (hitBody == null) {
                hit.collider.TryGetComponent(out hitBody);
            }
            if (hitBody != null) {
                end = hit.point;
                if (Util.IsBodyLocal(hitBody)) {
                    ApplyCryoLimbHit(hitBody, hit.point, limbDmg);
                    localVictim = hitBody;
                }
                break;
            }
        }
        SpawnCryoLine(origin, end);
        if (NetObjectRegistry.TryGetSyncInfo(drone, out var si)) {
            OpenWindow(KindCryo, si.syncId, false, CryoWindow);
            Relay(KindCryo, si.syncId, 0f);
            if (localVictim != null && localVictim.TryGetNetBody(out var nb)) {
                Publish(nb);
            }
        }
    }

    internal static void ApplyCryoHurtLocal(LaserDroneScript drone, Body body, Vector2 hitPoint, float limbDmg) {
        if (drone == null || body == null || !Util.IsBodyLocal(body) || !NetObjectRegistry.TryGetSyncInfo(drone, out var si)) {
            return;
        }
        int key = si.syncId.id ^ body.GetInstanceID();
        float now = Time.unscaledTime;
        if (cryoClaimed.TryGetValue(key, out float until) && now < until) {
            return;
        }
        cryoClaimed[key] = now + CryoWindow;
        if (cryoClaimed.Count > 400) {
            cryoClaimed.Clear();
        }
        ApplyCryoLimbHit(body, hitPoint, limbDmg);
        Ignore(body);
        if (Net.IsClient) {
            Send(KindCryo, si.syncId, 0);
        } else if (body.TryGetNetBody(out var nb)) {
            Publish(nb);
        }
    }

    static void ApplyCryoLimbHit(Body body, Vector2 hitPoint, float limbDmg) {
        if (body == null) {
            return;
        }
        body.DoGoreSound();
        Limb closest = body.GetClosestLimb(hitPoint);
        if (closest == null) {
            return;
        }
        float armor = closest.GetArmorReduction();
        closest.pain += limbDmg / armor;
        body.adrenaline += 25f;
        closest.skinHealth -= limbDmg / armor;
        closest.muscleHealth -= limbDmg / armor * 0.25f;
        closest.DamageWearables(0.015f);
        body.stamina += 35f;
        body.temperature -= 0.5f;
        body.CreateCloudSmall(hitPoint, Vector2.zero);
    }

    static void SpawnCryoLine(Vector2 start, Vector2 end) {
        GameObject obj = (GameObject)Object.Instantiate(Resources.Load("Special/DroneCryoLine"), Vector2.zero, Quaternion.identity);
        if (obj == null) {
            return;
        }
        LineRenderer line = obj.GetComponent<LineRenderer>();
        if (line != null) {
            line.SetPosition(0, start);
            line.SetPosition(1, end);
        }
        Sound.Play("droneFire1", end, twoDimensional: false, pitchShift: false);
        if (obj.GetComponent<LaserDroneCryoLine>() == null) {
            obj.AddComponent<LaserDroneCryoLine>();
        }
    }

    static void ClientCryoShotCue(LaserDroneScript drone) {
        if (drone == null || drone.barrel == null) {
            return;
        }
        Vector2 origin = drone.barrel.position;
        Vector2 dir = drone.barrel.up;
        RaycastHit2D[] hits = Physics2D.RaycastAll(origin, dir, CryoReach, LayerMask.GetMask("Ground", "Body", "Limb"));
        Vector2 end = origin + dir * CryoReach;
        float limbDmg = Random.Range(0.85f, 1.15f) * 15f;
        for (int i = 0; i < hits.Length; i++) {
            RaycastHit2D hit = hits[i];
            if (hit.transform == drone.transform) {
                continue;
            }
            if (hit.collider.gameObject.layer == 6) {
                end = hit.point;
                break;
            }
            Body hitBody = null;
            if (hit.collider.TryGetComponent(out Limb limbHit)) {
                hitBody = limbHit.body;
            }
            if (hitBody == null) {
                hit.collider.TryGetComponent(out hitBody);
            }
            if (hitBody != null) {
                end = hit.point;
                if (Util.IsBodyLocal(hitBody)) {
                    ApplyCryoHurtLocal(drone, hitBody, hit.point, limbDmg);
                }
                break;
            }
        }
        SpawnCryoLine(origin, end);
    }

    static bool AcceptCryo(knetid id, NetBody nb) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent(out LaserDroneScript drone)) {
            return false;
        }
        if (nb.body == null || !KM.DistanceCheck(drone.transform.position, nb.body.transform.position, CryoReach)) {
            return false;
        }
        if (!TakeWindow(KindCryo, id, nb.netId, false, CryoWindow)) {
            return false;
        }
        Publish(nb);
        return true;
    }
#endif

    [ServerReceiver((byte)NetmsgId.SERVER_TrapClaim)]
    static void Recv(knetid clientId, NetDataReader reader) {
        reader.Get(out byte kind);
        reader.Get(out knetid id);
        reader.Get(out ushort mask);
        reader.Get(out float _);
        if (!ScavPlayer.TryGetPlayerAndNetBodyFromClientId(clientId, out _, out var nb) || nb == null || nb.body == null) {
            return;
        }
        if (kind == KindJump) {
            AcceptJump(id, nb);
            return;
        }

        bool ok = false;
        if (kind == KindBear) {
            ok = AcceptBear(id, nb, mask);
        } else if (kind == KindSpike) {
            ok = AcceptSpike(id, nb, mask);
        } else if (kind == KindCoil) {
            ok = AcceptCoil(id, nb, mask);
        } else if (kind == KindTurret) {
            ok = AcceptTurret(id, nb, mask);
        } else if (kind == KindGun) {
            ok = AcceptGun(id, nb, mask);
        } else if (kind == KindBoom) {
            ok = AcceptBoom(id, nb, mask);
        } else if (kind == KindManifest) {
            ok = AcceptManifest(id, nb);
        }
#if FULLGAME
        else if (kind == KindCryo) {
            ok = AcceptCryo(id, nb);
        }
#endif
        else if (kind == KindGlass) {
            ok = AcceptGlass(nb, mask);
        }

        if (!ok) {
            Publish(nb);
        }
    }

    internal static void GlassHitLocal(GroundGlass shard, Body body) {
        if (shard == null || body == null || !Util.IsBodyLocal(body) || shard.transform.position.y >= body.transform.position.y) {
            return;
        }
        Item boots = body.GetWearableBySlotID("feet");
        if (boots) {
            boots.SetCondition(boots.condition - 0.1f * boots.Stats.wearableHitDurabilityLossMultiplier);
            Sound.Play("glassshard", shard.transform.position);
            Sound.Play("glass", shard.transform.position);
            Object.Destroy(shard.gameObject);
            return;
        }
        int footIdx = Random.value > 0.5f ? (int)LimbIndex.FrontFoot : (int)LimbIndex.BackFoot;
        Limb limb = AnatomyConstants.IsValidLimbIndex(body, footIdx) ? body.limbs[footIdx] : null;
        while (limb != null && limb.dismembered && limb.connectedLimbs != null && limb.connectedLimbs.Length > 0) {
            limb = limb.connectedLimbs[0];
        }
        if (limb == null) {
            Object.Destroy(shard.gameObject);
            return;
        }
        ApplyGlassLimb(limb);
        ushort mask = Bit(limb);
        Ignore(body);
        if (Net.IsClient) {
            Send(KindGlass, default, mask);
        } else if (body.TryGetNetBody(out var nb)) {
            Publish(nb);
        }
        Object.Destroy(shard.gameObject);
    }

    static void ApplyGlassLimb(Limb limb) {
        if (limb == null) {
            return;
        }
        limb.skinHealth -= Random.Range(25f, 40f);
        limb.muscleHealth -= 20f;
        limb.bleedAmount += Random.Range(5f, 20f);
        limb.pain += 80f;
        limb.shrapnel = 5;
        limb.body.adrenaline = 70f;
        limb.CreateTemporarySprite(Resources.Load<Sprite>("Special/footglass"), 0f, null, scaleLimb: false, 60f, (Limb x) => !x.hasShrapnel);
        if (WoundView.view != null) {
            WoundView.view.AddImageToLimb(limb, Resources.Load<Sprite>("Special/footglasshealthpanel"), flip: false, (Limb x) => !x.hasShrapnel || x.dismembered);
        }
        Sound.Play("glassshard", limb.transform.position);
        limb.body.DoGoreSound();
        limb.body.talker?.Invoke("TalkGlass", 30f);
    }

    static bool AcceptGlass(NetBody nb, ushort mask) {
        if (nb == null || nb.body == null || !TakeWindow(KindGlass, nb.netId, nb.netId, false, GlassWindow)) {
            return false;
        }
        Each(nb.body, mask, limb => {
            if (limb != null && !limb.hasShrapnel) {
                ApplyGlassLimb(limb);
            }
        });
        Publish(nb);
        return true;
    }

    static void AcceptJump(knetid id, NetBody nb) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<JumpPadScript>(out var pad)) {
            return;
        }
        if (pad.cooldown > 0f || !KM.DistanceCheck(pad.transform.position, nb.body.transform.position, GameplayRanges.SimulationKeepRadius)) {
            return;
        }
        pad.cooldown = 15f;
        PlayJump(pad);
        Relay(KindJump, id, 15f);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_TrapState)]
    static void RecvState(knetid _, NetDataReader reader) {
        reader.Get(out byte kind);
        reader.Get(out knetid id);
        reader.Get(out float cooldown);
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null) {
            return;
        }
        if (kind == KindJump && si.go.TryGetComponent<JumpPadScript>(out var pad)) {
            bool idle = pad.cooldown <= 0f;
            RaiseIfIdle(pad, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
            if (idle) {
                PlayJump(pad);
            }
        } else if (kind == KindCoil && si.go.TryGetComponent<CoilScript>(out var coil)) {
            RaiseIfIdle(coil, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
        } else if (kind == KindBear && si.go.TryGetComponent<BearTrap>(out var bear)) {
            SpendBear(bear);
        } else if (kind == KindSpike && si.go.TryGetComponent<SpikeStabberScript>(out var spike) && !spike.activated) {
            spike.Stab();
        } else if (kind == KindGun && si.go.TryGetComponent<GunmineScript>(out var gun)) {
            bool idle = gun.cooldown <= 0f;
            RaiseGun(gun, (byte)Mathf.Clamp(Mathf.CeilToInt(cooldown), 0, 255));
            if (idle) {
                PlayGun(gun);
            }
        } else if (kind == KindManifest && si.go.TryGetComponent(out CrystalEnemy manifest)) {
            if (Util.TryGetLocalBody(out Body local) && Near(manifest, local)) {
                RaycastHit2D[] hits = Physics2D.RaycastAll(manifest.transform.position, manifest.transform.up);
                for (int i = 0; i < hits.Length; i++) {
                    if (hits[i].transform != null
                        && Util.TryGetBodyFromObject(hits[i].transform.gameObject, out Body hitBody)
                        && hitBody == local) {
                        ManifestHurtLocal(manifest, local);
                        break;
                    }
                }
            }
        }
#if FULLGAME
        else if (kind == KindCryo && si.go.TryGetComponent(out LaserDroneScript cryo)) {
            ClientCryoShotCue(cryo);
        }
#endif
    }

    static bool AcceptBear(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<BearTrap>(out var trap)) {
            return false;
        }
        if (!Near(trap, nb.body) || !TakeWindow(KindBear, id, nb.netId, trap.activated, BearWindow)) {
            return false;
        }
        Limb limb = null;
        Each(nb.body, mask, l => limb = l);
        if (limb == null) {
            return false;
        }
        ApplyBear(trap, limb);
        Publish(nb);
        return true;
    }

    static bool AcceptSpike(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<SpikeStabberScript>(out var spike)) {
            return false;
        }
        if (!Near(spike, nb.body)) {
            return false;
        }
        if (mask == 0) {
            OpenWindow(KindSpike, id, spike.activated, SpikeWindow);
            if (!spike.activated) {
                spike.Stab();
            }
            RelayOf(KindSpike, spike, 0f);
            return true;
        }
        if (!TakeWindow(KindSpike, id, nb.netId, spike.activated, SpikeWindow)) {
            return false;
        }
        if (!spike.activated) {
            spike.Stab();
        }
        RelayOf(KindSpike, spike, 0f);
        Each(nb.body, mask, limb => ApplySpikeLimb(spike, limb));
        if (spike.spikeRenderer != null) {
            spike.spikeRenderer.sprite = spike.hitSpikeSprite;
        }
        Publish(nb);
        return true;
    }

    static bool AcceptCoil(knetid id, NetBody nb, ushort mask) {
        if (!NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<CoilScript>(out var coil)) {
            return false;
        }
        if (coil.cooldown > 0f || !Near(coil, nb.body)) {
            return false;
        }
        Limb limb = null;
        Each(nb.body, mask, l => limb = l);
        if (limb == null && nb.body.limbs != null) {
            foreach (var l in nb.body.limbs) {
                if (l != null) {
                    limb = l;
                    break;
                }
            }
        }
        if (limb == null) {
            return false;
        }
        ApplyCoil(coil, limb);
        Publish(nb);
        return true;
    }

    static bool AcceptTurret(knetid id, NetBody nb, ushort mask) {
        if (mask == 0 || !NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<TurretScript>(out _)) {
            return false;
        }
        int key = (id.id << 16) ^ nb.netId.id;
        float now = Time.unscaledTime;
        if (turretNext.TryGetValue(key, out var next) && now < next) {
            return false;
        }
        turretNext[key] = now + 0.15f;
        if (turretNext.Count > 400) {
            turretNext.Clear();
        }
        Each(nb.body, mask, limb => TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(limb, null, false));
        Publish(nb);
        return true;
    }

    static bool AcceptGun(knetid id, NetBody nb, ushort mask) {
        if (mask == 0 || !NetObjectRegistry.TryGetSyncInfo(id, out var si) || si.go == null || !si.go.TryGetComponent<GunmineScript>(out var gun)) {
            return false;
        }
        if (gun.cooldown > 0f) {
            return false;
        }
        gun.cooldown = 6f;
        gun.didReload = false;
        PlayGun(gun);
        Relay(KindGun, id, 6f);
        Each(nb.body, mask, limb => TurretScript_Shoot_MultiplayerPatch.Stolen_ShootApplyDamageLimb(limb, null, false));
        Publish(nb);
        return true;
    }

    static bool AcceptBoom(knetid id, NetBody nb, ushort mask) {
        var boom = FindBoom(id);
        if (boom == null && id.id != 0 && NetObjectRegistry.TryGetSyncInfo(id, out var si) && si.go != null && si.go.TryGetComponent<MineScript>(out var mine) && !mine.exploded) {
            ConsumeMine(mine, id);
            boom = FindBoom(id);
        }
        boom ??= LatestBoom();
        if (boom == null || !boom.who.Add(nb.netId.id)) {
            return false;
        }
        if (mask == 0) {
            int i = 0;
            foreach (var limb in nb.body.limbs) {
                if (i > 15) {
                    break;
                }
                if (limb != null) {
                    mask |= (ushort)(1 << i);
                }
                i++;
            }
        }
        Each(nb.body, mask, limb => ApplyBoomLimb(limb, boom.param, false));
        Publish(nb);
        return true;
    }
}
