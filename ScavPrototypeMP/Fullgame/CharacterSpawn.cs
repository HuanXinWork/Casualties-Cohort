#if FULLGAME
using CasualtiesTogetherUtils;
using HarmonyLib;
using System;
using TMPro;
using Together.Api;
using Together.Diagnostics;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版三物种（Experiment / Milky / Dune）预制体缓存、同 <c>netId</c> 物种切换网格重建与笔记字体加载器。
/// </summary>
public static class CharacterSpawn {
    static readonly string[] PrefabPaths = {
        "Special/Characters/Experiment",
        "Special/Characters/Milky",
        "Special/Characters/Dune",
    };

    static readonly GameObject[] PrefabCache = new GameObject[3];
    static TMP_FontAsset[] _noteFonts;

    public static int ClampChar(int chr) {
        if (chr < 0) return 0;
        if (chr > 2) return 2;
        return chr;
    }

    public static int ResolveCharForPlayer(ScavPlayer plr, SyncBody sb) {
        if (plr != null && sb != null) {
            int fromPlr = ClampChar(plr.selectedCharacter);
            int fromSb = ClampChar(sb.characterSelected.Value);
            if (fromPlr != fromSb) {
                if (Net.IsClient && !Net.IsServer) {
                    plr.selectedCharacter = (byte)fromSb;
                    return fromSb;
                }
                return fromPlr;
            }
            return fromPlr;
        }
        if (plr != null) {
            return ClampChar(plr.selectedCharacter);
        }
        if (sb != null) {
            return ClampChar(sb.characterSelected.Value);
        }
        return ClampChar(PreRunScript.characterSelected);
    }

    public static GameObject InstantiateCharacter(int chr, string name) {
        chr = ClampChar(chr);
        GameObject prefab = GetOrCreatePrefab(chr);
        GameObject chara = UnityEngine.Object.Instantiate(prefab);
        chara.name = "Character_" + name;
        chara.SetActive(true);
        return chara;
    }

    public static GameObject GetOrCreatePrefab(int chr) {
        chr = ClampChar(chr);
        if (PrefabCache[chr] != null) {
            Body cachedBody = PrefabCache[chr].GetComponentInChildren<Body>(true);
            if (cachedBody != null && ClampChar(cachedBody.charType) == chr) {
                return PrefabCache[chr];
            }
            PrefabCache[chr] = null;
        }

        if (chr == 0 && ServerMain.CHARACTER_PREFAB != null) {
            Body stash = ServerMain.CHARACTER_PREFAB.GetComponentInChildren<Body>(true);
            if (stash != null && ClampChar(stash.charType) == 0) {
                PrefabCache[0] = ServerMain.CHARACTER_PREFAB;
                return PrefabCache[0];
            }
        }

        GameObject live = Utils.Create(PrefabPaths[chr], Vector2.zero, 0f);
        CaptureHingeState(live);
        GameObject cached = UnityEngine.Object.Instantiate(live);
        cached.SetActive(false);
        PrefabCache[chr] = cached;
        if (chr == 0) {
            ServerMain.CHARACTER_PREFAB = cached;
        }

        if (PlayerCamera.main == null || PlayerCamera.main.body == null ||
            (PlayerCamera.main.body.transform.parent != null
                ? PlayerCamera.main.body.transform.parent.gameObject
                : PlayerCamera.main.body.gameObject) != live) {
            UnityEngine.Object.Destroy(live);
        }
        return PrefabCache[chr];
    }

    static void CaptureHingeState(GameObject root) {
        HingeJoint2D[] hinges = root.GetComponentsInChildren<HingeJoint2D>();
        for (int i = 0; i < hinges.Length; i++) {
            HingeJoint2D hinge = hinges[i];
            if (hinge.GetComponent<ClientMain.SavedHingeJointState>() != null) {
                continue;
            }
            ClientMain.SavedHingeJointState state = hinge.gameObject.AddComponent<ClientMain.SavedHingeJointState>();
            state.a = hinge.transform.localPosition;
            state.b = hinge.transform.localRotation;
            state.aa = hinge.connectedBody.transform.localPosition;
            state.bb = hinge.connectedBody.transform.localRotation;
            state.useLimits = hinge.useLimits;
            state.limitsmax = hinge.limits.max;
            state.limitsmin = hinge.limits.min;
            state.referenceAngle = hinge.referenceAngle;
            state.hinge = hinge;
            state.connectedBody = hinge.connectedBody;
        }
    }

    public static void ApplySelectedToSyncBody(SyncBody sb, ScavPlayer plr) {
        if (sb == null) {
            return;
        }
        byte chr = (byte)ResolveCharForPlayer(plr, sb);
        if (plr != null) {
            plr.selectedCharacter = chr;
        }
        sb.characterSelected.Value = chr;
    }

    public static string SaveSpeciesName(int chr) {
        return ClampChar(chr) switch {
            1 => "Milky",
            2 => "Dune",
            _ => "Experiment",
        };
    }

    public static void ServerForceSwapSpecies(ScavPlayer plr, int chr, bool wipeXp) {
        if (!Net.IsServer || plr == null) {
            return;
        }
        chr = ClampChar(chr);
        int was = plr.body != null ? ClampChar(plr.body.charType) : ClampChar(plr.selectedCharacter);

        int keepSTR = 0, keepRES = 0, keepINT = 0;
        float keepExpSTR = 0f, keepExpRES = 0f, keepExpINT = 0f;
        bool keepSkills = !wipeXp && plr.body != null && plr.body.skills != null;
        if (keepSkills) {
            Skills sk = plr.body.skills;
            keepSTR = sk.STR;
            keepRES = sk.RES;
            keepINT = sk.INT;
            keepExpSTR = sk.expSTR;
            keepExpRES = sk.expRES;
            keepExpINT = sk.expINT;
        }

        plr.selectedCharacter = (byte)chr;
        if (plr.IsLocal) {
            PreRunScript.characterSelected = chr;
        }

        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        if (sb != null) {
            sb.characterSelected.Value = (byte)chr;
        }

        if (plr.body != null && plr.body.charType != chr) {
            RebuildBodyMesh(plr, chr, dropInventory: true);
        } else if (plr.body != null) {
            plr.body.charType = chr;
            ItemSync.RefreshWearableSpeciesSprites(plr.body);
            if (plr.body.talker != null) {
                TalkerSpeciesFont.Apply(plr.body.talker);
            }
            TogetherPanelApi.NotifyBodyReadyOrRebuilt(plr.netBody, chr, isRebuild: true);
        } else {
            NetBody.CreateNewPlayerCharacter(plr, sb);
            if (plr.netBody != null) {
                ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            }
        }

        if (plr.body != null && plr.body.skills != null) {
            if (wipeXp) {
                plr.body.skills.Setup(chr);
            } else if (keepSkills) {
                Skills sk = plr.body.skills;
                sk.STR = keepSTR;
                sk.RES = keepRES;
                sk.INT = keepINT;
                sk.expSTR = keepExpSTR;
                sk.expRES = keepExpRES;
                sk.expINT = keepExpINT;
                sk.UpdateExpBoundaries();
                float[] mult = Skills.BaseMultipliers(chr);
                sk.STRGainMult = mult[0];
                sk.RESGainMult = mult[1];
                sk.INTGainMult = mult[2];
            } else {
                plr.body.skills.Setup(chr);
            }
        }

        RebindLocalCamera(plr);

        if (plr.netBody != null) {
            MedicalSync.ForceHealth(plr.netBody);
        }

        PlayerCheckpoint.RememberSpecies(plr.GetPersistentId(), SaveSpeciesName(chr), 0);
        PlayerCheckpoint.WriteOne(plr, false);

        log.warn($"[Species] force-swap \"{plr.playerName}\" {was}->{chr} wipeXp={wipeXp}");
        plr.Server_DoAlertSingle($"<key:species_forced> {SaveSpeciesName(chr)}");
    }

    public static void ClientRebuildLocalBodyIfNeeded(ScavPlayer plr, SyncBody sb, int want) {
        if (Net.IsServer || plr == null || !plr.IsLocal) {
            return;
        }
        want = ClampChar(want);
        plr.selectedCharacter = (byte)want;
        PreRunScript.characterSelected = want;
        if (plr.body != null && plr.body.charType == want) {
            ItemSync.RefreshWearableSpeciesSprites(plr.body);
            if (plr.body.talker != null) {
                TalkerSpeciesFont.Apply(plr.body.talker);
            }
            return;
        }
        RebuildBodyMesh(plr, want, dropInventory: true);
        RebindLocalCamera(plr);
    }

    static void RebuildBodyMesh(ScavPlayer plr, int want, bool dropInventory) {
        if (plr == null || plr.body == null) {
            return;
        }
        want = ClampChar(want);
        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        Vector2 pos = plr.body.transform.position;
        NetBody old = plr.netBody;
        log.warn($"[Species] rebuild \"{plr.playerName}\" {plr.body.charType}->{want}");
        if (dropInventory) {
            try {
                plr.body.Body_DropAllItems();
            } catch (Exception e) {
                ModLog.Exception(LogDomain.Item, "CharacterSpawn.RebuildBodyMesh DropAllItems", e);
            }
        }
        plr.body = null;
        if (old != null) {
            NetBody.DetachForMeshRebuild(old);
            ForceDestroyNetBodyMesh(old);
        }
        NetBody.CreateNewPlayerCharacter(plr, sb);
        if (plr.netBody != null) {
            plr.netBody.SetBodyPosition(pos);
            ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            if (plr.body != null) {
                plr.body.charType = want;
                ItemSync.RefreshWearableSpeciesSprites(plr.body);
                if (plr.body.talker != null) {
                    TalkerSpeciesFont.Apply(plr.body.talker);
                }
            }
            // 同 netId 物种网格重建完毕：通知外部模组（如 SkinSync）立即重涂新物种躯体贴图
            TogetherPanelApi.NotifyBodyReadyOrRebuilt(plr.netBody, want, isRebuild: true);
        }
    }

    static void RebindLocalCamera(ScavPlayer plr) {
        if (plr == null || !plr.IsLocal || plr.body == null || PlayerCamera.main == null) {
            return;
        }
        PlayerCamera.main.body = plr.body;
        if (MoodleManager.main != null) {
            MoodleManager.main.body = plr.body;
        }
    }

    public static void ServerRebuildBodyIfSpeciesMismatch(ScavPlayer plr) {
        if (!Net.IsServer || plr == null || plr.body == null) {
            return;
        }
        int want = ClampChar(plr.selectedCharacter);
        if (plr.body.charType == want) {
            return;
        }
        SyncBody sb = plr.netBody != null ? plr.netBody.syncBody : null;
        Vector2 pos = plr.body.transform.position;
        NetBody old = plr.netBody;
        log.warn($"[Species] rebuild \"{plr.playerName}\" {plr.body.charType}->{want}");
        plr.body = null;
        if (old != null) {
            NetBody.DetachForMeshRebuild(old);
            ForceDestroyNetBodyMesh(old);
        }
        NetBody.CreateNewPlayerCharacter(plr, sb);
        if (plr.netBody != null) {
            plr.netBody.SetBodyPosition(pos);
            ApplySelectedToSyncBody(plr.netBody.syncBody ?? sb, plr);
            EnsureBodySpecies(plr.body);
            TogetherPanelApi.NotifyBodyReadyOrRebuilt(plr.netBody, want, isRebuild: true);
        }
    }

    static void ForceDestroyNetBodyMesh(NetBody npc) {
        if (npc == null) {
            return;
        }
        if (npc.IsPlayer && npc.plr != null) {
            ScavPlayer.AllLivingPlayers.Remove(npc.plr);
            ScavPlayer.AllDeadPlayers.Remove(npc.plr);
        }
        GameObject root = npc.transform.parent != null
            ? npc.transform.parent.gameObject
            : npc.gameObject;
        UnityEngine.Object.Destroy(root);
    }

    public static void EnsureBodySpecies(Body body) {
        if (body == null || !Net.IsRunning) {
            return;
        }
        if (!body.TryGetNetBody(out NetBody nb) || nb == null) {
            return;
        }
        if (nb.plr == null && nb.syncBody == null) {
            return;
        }
        int want = ResolveCharForPlayer(nb.plr, nb.syncBody);
        if (body.charType == want) {
            return;
        }
        int was = body.charType;
        log.warn($"[Species] body.charType {was}->{want} on \"{body.name}\"");
        if (Net.IsServer && nb.plr != null && nb.plr.body == body) {
            ServerRebuildBodyIfSpeciesMismatch(nb.plr);
            return;
        }
        body.charType = want;
        ItemSync.RefreshWearableSpeciesSprites(body);
        if (body.skills != null) {
            int[] oldBase = Skills.BaseSkills(ClampChar(was));
            Skills sk = body.skills;
            if (sk.STR == oldBase[0] && sk.RES == oldBase[1] && sk.INT == oldBase[2]) {
                sk.Setup(want);
            }
        }
        if (body.talker != null) {
            TalkerSpeciesFont.Apply(body.talker);
        }
        TogetherPanelApi.NotifyBodyReadyOrRebuilt(nb, want, isRebuild: true);
    }

    public static TMP_FontAsset GetNoteFont(int chr) {
        EnsureNoteFonts();
        chr = ClampChar(chr);
        if (_noteFonts != null && chr < _noteFonts.Length) {
            return _noteFonts[chr];
        }
        return null;
    }

    public static TMP_FontAsset GetLocalNoteFont() {
        int chr = PreRunScript.characterSelected;
        if (Util.TryGetLocalBody(out Body body)) {
            chr = body.charType;
        } else if (ScavPlayer.LocalPlayer != null) {
            chr = ScavPlayer.LocalPlayer.selectedCharacter;
        }
        return GetNoteFont(chr);
    }

    public static void CacheNoteFonts(TMP_FontAsset[] fonts) {
        if (fonts == null || fonts.Length == 0 || _noteFonts != null) {
            return;
        }
        _noteFonts = fonts;
        string[] names = new string[fonts.Length];
        for (int i = 0; i < fonts.Length; i++) {
            names[i] = fonts[i] != null ? fonts[i].name : "null";
        }
        log.warn($"[TalkerFont] cached note fonts [{string.Join(", ", names)}]");
    }

    static void EnsureNoteFonts() {
        if (_noteFonts != null) {
            return;
        }
        SurvivorNote note = UnityEngine.Object.FindObjectOfType<SurvivorNote>(true);
        if (note == null || note.fonts == null || note.fonts.Length == 0) {
            SurvivorNote[] loaded = Resources.FindObjectsOfTypeAll<SurvivorNote>();
            for (int i = 0; i < loaded.Length; i++) {
                if (loaded[i] != null && loaded[i].fonts != null && loaded[i].fonts.Length > 0) {
                    note = loaded[i];
                    break;
                }
            }
        }
        if (note == null) {
            string[] paths = { "LoreNote", "Special/LoreNote", "Special/survivornote", "survivornote" };
            for (int i = 0; i < paths.Length; i++) {
                GameObject probe = Resources.Load<GameObject>(paths[i]);
                if (probe == null) {
                    continue;
                }
                note = probe.GetComponent<SurvivorNote>() ?? probe.GetComponentInChildren<SurvivorNote>(true);
                if (note != null && note.fonts != null && note.fonts.Length > 0) {
                    break;
                }
                note = null;
            }
        }
        if (note != null && note.fonts != null && note.fonts.Length > 0) {
            CacheNoteFonts(note.fonts);
        }
    }
}

[HarmonyPatch(typeof(SurvivorNote), "Start")]
static class SurvivorNote_Start_CacheFonts_MultiplayerPatch {
    static void Postfix(SurvivorNote __instance) {
        if (!Net.IsRunning || __instance == null) {
            return;
        }
        CharacterSpawn.CacheNoteFonts(__instance.fonts);
    }
}

[HarmonyPatch(typeof(PlayerCamera), "Awake")]
static class PlayerCamera_Awake_CharacterSelect_MultiplayerPatch {
    static void Prefix(PlayerCamera __instance) {
        if (!Net.IsRunning) {
            return;
        }
        int chr = PreRunScript.characterSelected;
        if (ScavPlayer.LocalPlayer != null) {
            chr = CharacterSpawn.ClampChar(ScavPlayer.LocalPlayer.selectedCharacter);
        }
        PreRunScript.characterSelected = chr;
        PlayerPrefs.SetInt("lastchar", chr);
    }
}

[HarmonyPatch(typeof(ScrollableText), nameof(ScrollableText.CreateText))]
static class ScrollableText_CreateText_CharFont_MultiplayerPatch {
    static void Prefix(bool cover, ref TMP_FontAsset font) {
        if (!Net.IsRunning || font != null || cover || SharedMain.IsInCutscene) {
            return;
        }
        TMP_FontAsset localFont = CharacterSpawn.GetLocalNoteFont();
        if (localFont != null) {
            font = localFont;
        }
    }
}
#endif
