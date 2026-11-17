#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版水晶单包权威同步控制器（重复网络壳体去重重绑、特效列表工厂与 <see cref="CrystalBehaviour.SetUpEffects"/> 补丁分部）。
/// </summary>
public static partial class CrystalLocalAuthority {
    static FieldInfo _crystalEffectsField;

    public static string CrystalResourceKey(GameObject go) {
        if (go == null) {
            return null;
        }
        if (go.TryGetComponent(out BuildingEntity build) && !string.IsNullOrEmpty(build.id)) {
            return build.id;
        }
        string name = go.name;
        if (string.IsNullOrEmpty(name)) {
            return null;
        }
        if (name.EndsWith("(Clone)", StringComparison.Ordinal)) {
            name = name.Substring(0, name.Length - "(Clone)".Length);
        }
        int mark = name.IndexOf("res: ", StringComparison.Ordinal);
        if (mark >= 0) {
            int start = mark + 5;
            int end = name.IndexOf(')', start);
            if (end > start) {
                return name.Substring(start, end - start).Trim();
            }
        }
        return name;
    }

    public static bool ResourceIdsMatch(string wantedRes, GameObject go) {
        if (string.IsNullOrEmpty(wantedRes) || go == null) {
            return false;
        }
        string key = CrystalResourceKey(go);
        if (!string.IsNullOrEmpty(key)) {
            return string.Equals(key, wantedRes, StringComparison.OrdinalIgnoreCase);
        }
        return false;
    }

    public static bool HasLocalMarkedCrystalAt(Vector2 position, GameObject except = null, string requiredRes = null) {
        CrystalBehaviour[] crystals = UnityEngine.Object.FindObjectsOfType<CrystalBehaviour>();
        for (int i = 0; i < crystals.Length; i++) {
            CrystalBehaviour c = crystals[i];
            if (c == null) {
                continue;
            }
            GameObject go = c.gameObject;
            if (except != null && go == except) {
                continue;
            }
            if (!IsLocalAuth(go) || (requiredRes != null && !ResourceIdsMatch(requiredRes, go))) {
                continue;
            }
            Vector2 delta = (Vector2)go.transform.position - position;
            if (delta.sqrMagnitude <= MatchRadiusSq) {
                return true;
            }
        }
        return false;
    }

    public static GameObject FindLocalMarkedCrystalAt(Vector2 position, GameObject except = null, string requiredRes = null) {
        CrystalBehaviour[] crystals = UnityEngine.Object.FindObjectsOfType<CrystalBehaviour>();
        GameObject best = null;
        float bestSq = MatchRadiusSq;
        for (int i = 0; i < crystals.Length; i++) {
            CrystalBehaviour c = crystals[i];
            if (c == null) {
                continue;
            }
            GameObject go = c.gameObject;
            if (except != null && go == except) {
                continue;
            }
            if (!IsLocalAuth(go) || (requiredRes != null && !ResourceIdsMatch(requiredRes, go))) {
                continue;
            }
            float sq = ((Vector2)go.transform.position - position).sqrMagnitude;
            if (sq <= bestSq) {
                bestSq = sq;
                best = go;
            }
        }
        return best;
    }

    /// <summary>当网络壳体落在已有本地权威水晶重合位置时，将 <see cref="SyncInfo"/> 重绑到本地水晶并延迟销毁冗余壳体。</summary>
    public static bool TryDiscardReplica(SyncInfo si) {
        if (!Net.IsClient || si == null || si.go == null) {
            return false;
        }
        GameObject replica = si.go;
        if (IsLocalAuth(replica) && !HasCustomStructuresCrystalAuth(replica)) {
            return false;
        }
        string requiredRes = null;
        try {
            requiredRes = si.resourceId.Value;
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.World, $"CrystalLocalAuthority: failed to inspect SyncInfo.resourceId: {ex.Message}");
            requiredRes = null;
        }
        GameObject local = FindLocalMarkedCrystalAt(replica.transform.position, replica, requiredRes);
        if (local == null) {
            return false;
        }
        Plugin.StartCoroutine(DiscardReplicaNextFrame(si, replica, local));
        return true;
    }

    static IEnumerator DiscardReplicaNextFrame(SyncInfo si, GameObject replica, GameObject local) {
        yield return null;
        if (si == null || local == null) {
            yield break;
        }
        if (replica != null) {
            try {
                if (NetObjectRegistry.SyncRegistry.TryGetValue(replica, out var mapped) && mapped == si) {
                    NetObjectRegistry.SyncRegistry.Remove(replica);
                }
                if (replica.TryGetComponent(out SyncInfoGameObjectTracker repTracker)) {
                    repTracker.syncInfo = null;
                    UnityEngine.Object.Destroy(repTracker);
                }
                UnityEngine.Object.Destroy(replica);
            } catch (Exception ex) {
                ModLog.Warn(LogDomain.World, $"[CrystalLocalAuth] discard replica failed: {ex.Message}");
            }
        }

        si.go = local;
        NetObjectRegistry.SyncRegistry[local] = si;
        SyncInfoGameObjectTracker tracker = local.GetComponent<SyncInfoGameObjectTracker>()
            ?? local.AddComponent<SyncInfoGameObjectTracker>();
        tracker.syncInfo = si;
        MarkLocal(local);
        MarkAuthoritySatisfied(si.syncId);
    }

    public static List<CrystalEffect> CreateEffectsFromIds(byte[] effectIds) {
        List<CrystalEffect> list = new List<CrystalEffect>();
        if (effectIds == null || effectIds.Length == 0) {
            return list;
        }
        CrystalAuthority.EnsureEffectMapsPublic();
        for (int i = 0; i < effectIds.Length; i++) {
            CrystalEffect effect = CrystalAuthority.CreateEffectById(effectIds[i]);
            if (effect != null) {
                list.Add(effect);
            }
        }
        return list;
    }

    public static List<CrystalEffect> CreateEffectsFromCsv(string raw) {
        List<CrystalEffect> list = new List<CrystalEffect>();
        HashSet<string> seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        string[] parts = (raw ?? string.Empty).Split(',');
        for (int i = 0; i < parts.Length; i++) {
            string token = parts[i].Trim();
            if (token.Length == 0 || !seen.Add(token)) {
                continue;
            }
            bool matched = false;
            foreach (var possible in CrystalBehaviour.possibleEffects) {
                CrystalEffect effect = possible.factory();
                if (string.Equals(effect.GetType().Name, "Crystal" + token, StringComparison.OrdinalIgnoreCase)) {
                    list.Add(effect);
                    matched = true;
                    break;
                }
            }
            if (!matched) {
                ModLog.Warn(LogDomain.World, $"[CrystalLocalAuth] Unknown crystal property '{token}'.");
            }
        }
        return list;
    }

    static FieldInfo CrystalEffectsField {
        get {
            _crystalEffectsField ??= AccessTools.Field(typeof(CrystalBehaviour), "effects");
            return _crystalEffectsField;
        }
    }

    public static void AssignEffects(CrystalBehaviour crystal, List<CrystalEffect> effects) {
        if (crystal == null) {
            return;
        }
        crystal.effects = effects ?? new List<CrystalEffect>();
        CrystalEffectsField?.SetValue(crystal, crystal.effects);
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.SetUpEffects))]
static class CrystalBehaviour_SetUpEffects_LocalAuth_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance) {
        if (!Net.IsRunning || __instance == null) {
            return true;
        }

        if (CrystalLocalAuthority.FindComponentByTypeName(__instance.gameObject, "CrystalPropertiesMarker") != null) {
            return true;
        }

        TogetherCrystalProps props = __instance.GetComponent<TogetherCrystalProps>();
        if (props == null) {
            return true;
        }
        if (props.Initialized) {
            return false;
        }
        props.Initialized = true;

        List<CrystalEffect> effects = (props.EffectIds != null && props.EffectIds.Length > 0)
            ? CrystalLocalAuthority.CreateEffectsFromIds(props.EffectIds)
            : CrystalLocalAuthority.CreateEffectsFromCsv(props.EffectsCsv);

        CrystalLocalAuthority.AssignEffects(__instance, effects);
        for (int i = 0; i < effects.Count; i++) {
            effects[i].crystal = __instance;
            effects[i].Start();
            if (props.EffectForceMults != null && i < props.EffectForceMults.Length
                && effects[i] is CrystalGravity
                && Mathf.Abs(props.EffectForceMults[i]) > 0.1f) {
                Traverse.Create(effects[i]).Field("forceMult").SetValue(props.EffectForceMults[i] >= 0f ? 1f : -1f);
            }
        }

        if (props.HasAuthoredColor) {
            CrystalAuthority.ApplyingAuthority = true;
            try {
                if (__instance.sprite != null) {
                    __instance.sprite.color = props.SpriteColor;
                }
                if (__instance.light != null) {
                    __instance.light.color = props.LightColor;
                    __instance.light.intensity = props.Intensity;
                    __instance.light.volumeIntensityEnabled = (props.Flags & CrystalLocalAuthority.FlagVolumeLight) != 0;
                    if (__instance.light.volumeIntensityEnabled) {
                        __instance.light.volumeIntensity = props.Volume;
                    }
                    __instance.light.falloffIntensity = props.Falloff;
                    if (props.OuterRadius > 0f) {
                        __instance.light.pointLightOuterRadius = props.OuterRadius;
                    }
                }
            } finally {
                CrystalAuthority.ApplyingAuthority = false;
            }
        } else if (!string.IsNullOrEmpty(props.ColorR) || !string.IsNullOrEmpty(props.ColorG) || !string.IsNullOrEmpty(props.ColorB)) {
            if (TryParseRgb(props, out Color color)) {
                if (__instance.sprite != null) {
                    __instance.sprite.color = color;
                }
                if (__instance.light != null) {
                    __instance.light.color = color;
                }
            }
        }

        if (effects.Count > 1) {
            GameObject distort = Utils.Create("Special/CrystalDistort", __instance.transform);
            if (distort != null) {
                distort.transform.localPosition = Vector3.zero;
                distort.transform.localScale = Vector3.one;
                distort.transform.rotation = Quaternion.identity;
                SpriteRenderer sr = distort.GetComponent<SpriteRenderer>();
                if (sr != null && sr.material != null) {
                    sr.material.SetFloat("_DistortStrength", effects.Count * 0.015f);
                }
            }
        }

        if (effects.Count > 0 && WorldGeneration.world != null) {
            __instance.CheckCurrentChunk(WorldGeneration.world.GetClosestChunkScript(__instance.transform.position));
        }

        if (__instance.build != null && effects.Count > 0) {
            __instance.build.description = Locale.GetBuilding(__instance.build.id + "dsc");
            string key = effects.Count switch {
                1 => "crystaleffect1",
                2 => "crystaleffect2",
                3 => "crystaleffect3",
                4 => "crystaleffect4",
                _ => "crystaleffectmany",
            };
            __instance.build.description += " " + Locale.GetOther(key);
            __instance.build.skipDescriptionSet = true;
        }

        if (effects.Count == 0) {
            UsableObject usable = __instance.GetComponent<UsableObject>();
            if (usable != null) {
                UnityEngine.Object.Destroy(usable);
            }
            UnityEngine.Object.Destroy(__instance);
        }

        CrystalLocalAuthority.MarkLocal(__instance != null ? __instance.gameObject : props.gameObject);
        return false;
    }

    static bool TryParseRgb(TogetherCrystalProps props, out Color color) {
        if (!float.TryParse(props.ColorR, out float r)) {
            r = 1f;
        }
        if (!float.TryParse(props.ColorG, out float g)) {
            g = 1f;
        }
        if (!float.TryParse(props.ColorB, out float b)) {
            b = 1f;
        }
        color = new Color(r, g, b, 1f);
        return true;
    }
}
#endif
