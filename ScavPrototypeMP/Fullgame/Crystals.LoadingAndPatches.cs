#if FULLGAME
using System;
using System.Collections;
using System.Collections.Generic;
using CasualtiesTogetherUtils;
using HarmonyLib;
using LiteNetLib.Utils;
using Together.Diagnostics;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 正式版水晶权威同步管理器（全图推送与网络接收器、客机加载分帧生成节流器，以及 <see cref="CrystalBehaviour"/> / <see cref="SyncInfo"/> 生命周期补丁分部）。
/// </summary>
public static partial class CrystalAuthority {
    public static void HostPushRegisteredCrystalStates() {
        if (!Net.IsServer) {
            return;
        }
        var who = ServerMain.AllClientIdsExceptHost;
        if (who == null || who.Count == 0) {
            return;
        }
        int n = 0;
        foreach (var kv in NetObjectRegistry.SyncRegistry) {
            SyncInfo si = kv.Value;
            if (si == null || si.go == null || !LooksLikeCrystal(si.go)) {
                continue;
            }
            HostCapture(si.go);
            ServerSendTo(si.syncId, who);
            n++;
        }
        if (n > 0) {
            Bug($"push registered sent={n}");
        }
    }

    [CTPostInit]
    static void Init() {
        WorldMP.OnWorldgenFinish += () => {
            ClearLayerCaches();
            if (Net.IsServer) {
                foreach (var be in UnityEngine.Object.FindObjectsOfType<BuildingEntity>()) {
                    if (be == null || !LooksLikeCrystal(be.gameObject)) {
                        continue;
                    }
                    MarkGenerationDone(be.gameObject);
                    HostCapture(be.gameObject);
                }
                HostFlushPendingCaptures();
                HostPushRegisteredCrystalStates();
                CrystalLocalAuthority.HostPushRegisteredBags();
                Bug($"worldgen capture cells={_byPos.Count} entries={PosEntryCount()} pending={_pendingCapture.Count}");
            }
            if (Net.IsClient) {
                ClientResyncAllCrystals();
            }
        };
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalState)]
    static void Server_RequestFill(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        Bug($"host recv req FILL sync={syncId} from={clientId}");
        if (NetObjectRegistry.TryGetSyncInfo(syncId, out var si) && si.go != null
            && !si.go.TryGetComponent(out CrystalBehaviour _)
            && !LooksLikeCrystal(si.go)) {
            return;
        }
        ServerSendTo(syncId, new[] { clientId });
    }

    [ServerReceiver((byte)NetmsgId.SERVER_RequestCrystalSpecials)]
    static void Server_RequestSpecials(knetid clientId, NetDataReader reader) {
        reader.Get(out knetid syncId);
        Bug($"host recv req SPECIALS sync={syncId} from={clientId}");
        if (!NetObjectRegistry.TryGetSyncInfo(syncId, out var si) || si.go == null) {
            if (_specialsBySync.TryGetValue(syncId, out var cached) && cached != null) {
                SendSpecials(cached, new[] { clientId });
            } else {
                Bug($"host SPECIALS miss sync={syncId}");
            }
            return;
        }
        if (!IsSpecial(si.go) && !(si.go.TryGetComponent(out CrystalBehaviour cb) && GetEffects(cb) != null && GetEffects(cb).Count > 0)) {
            Bug($"host SPECIALS refuse sync={syncId} not special");
            return;
        }
        if (CapturePair(si.go, syncId, out _, out var specials) && specials != null) {
            Store(si.go, syncId, null, specials);
            SendSpecials(specials, new[] { clientId });
        } else if (TrySpecials(syncId, PosKeyOf(si.go), out var cached)) {
            SendSpecials(cached, new[] { clientId });
        } else {
            Bug($"host SPECIALS empty sync={syncId}");
        }
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalState)]
    static void Client_ReceiveFill(knetid _, NetDataReader reader) {
        ReceiveState(reader);
    }

    [ClientReceiver((byte)NetmsgId.CLIENT_CrystalSpecials)]
    static void Client_ReceiveSpecials(knetid _, NetDataReader reader) {
        ReceiveSpecials(reader);
    }

    static void CollectPendingSyncInfos(List<SyncInfo> needCreate, List<SyncInfo> needCrystalApply) {
        needCreate.Clear();
        needCrystalApply.Clear();
        foreach (var kv in NetObjectRegistry.NetIdToSyncInfoDict) {
            SyncInfo si = kv.Value;
            if (si == null) {
                continue;
            }
            string res;
            try {
                res = si.resourceId.Value;
            } catch (Exception ex) {
                ModLog.Debug(LogDomain.World, $"CrystalAuthority.CollectPendingSyncInfos skipped uninitialized resourceId: {ex.Message}");
                continue;
            }
            if (string.IsNullOrEmpty(res)) {
                continue;
            }
            if (si.go == null) {
                needCreate.Add(si);
            } else if (LooksLikeCrystalId(res)) {
                needCrystalApply.Add(si);
            }
        }
    }

    static string FormatSyncLoadText(string label, int index, int total) {
        float t = Mathf.Clamp01((float)index / Math.Max(1, total));
        int percent = Mathf.RoundToInt(t * 100f);
        const int width = 22;
        int filled = Mathf.Clamp(Mathf.RoundToInt(t * width), 0, width);
        string bar = "[" + new string('#', filled) + new string('-', width - filled) + "]";
        return $"Syncing {label}..\n ({index}/{total})\n{bar} {percent}%\n\n \n ";
    }

    public static IEnumerator ClientLoadingScreenStep(WorldGeneration world) {
        if (!Net.IsClient || world == null) {
            yield break;
        }
        List<SyncInfo> needCreate = new List<SyncInfo>(512);
        List<SyncInfo> needCrystalApply = new List<SyncInfo>(256);
        CollectPendingSyncInfos(needCreate, needCrystalApply);
        int total = needCreate.Count + needCrystalApply.Count;
        if (total == 0 || world.loadingObject == null || !world.loadingObject.activeSelf) {
            yield break;
        }
        _fillApplied.Clear();
        _specialsApplied.Clear();
        _nextFillAt.Clear();
        _nextSpecialAt.Clear();
        LoadStep.Begin();
        try {
            int done = 0;
            for (int i = 0; i < needCreate.Count; i++) {
                SyncInfo si = needCreate[i];
                if (si == null || si.go != null) {
                    done++;
                    continue;
                }
                world.SetLoadingTextNoLocale(FormatSyncLoadText("world objects", done + 1, total));
                LoadStep.GrantOne();
                try {
                    si.CreateObject();
                } catch (Exception ex) {
                    ModLog.Debug(LogDomain.World, $"CrystalAuthority.ClientLoadingScreenStep CreateObject failed: {ex.Message}");
                }
                if (si.go != null && LooksLikeCrystal(si.go)) {
                    ClientTryApplyOrRequest(si);
                }
                done++;
                yield return null;
            }
            for (int i = 0; i < needCrystalApply.Count; i++) {
                SyncInfo si = needCrystalApply[i];
                if (si == null || si.go == null) {
                    done++;
                    continue;
                }
                world.SetLoadingTextNoLocale(FormatSyncLoadText("crystals", done + 1, total));
                ClientTryApplyOrRequest(si);
                done++;
                yield return null;
            }
        } finally {
            LoadStep.End();
        }
    }

    public static class LoadStep {
        static bool _active;
        static int _grantFrame = -1;

        public static bool Active => _active;

        public static void Begin() {
            _active = true;
            _grantFrame = -1;
        }

        public static void End() {
            _active = false;
            _grantFrame = -1;
        }

        public static void GrantOne() {
            _grantFrame = Time.frameCount;
        }

        public static bool TryAllowCreate() {
            if (!_active || _grantFrame != Time.frameCount) {
                return false;
            }
            _grantFrame = -1;
            return true;
        }
    }

    public static class SyncBudget {
        public const int HostRegisterPerStamp = 8;
        public const int ClientCreatePerFrame = 4;
        public const int HostPushPerFrame = 8;
        static int _createFrame = -1;
        static int _createsThisFrame;

        public static bool TryConsumeClientCreate() {
            int frame = Time.frameCount;
            if (frame != _createFrame) {
                _createFrame = frame;
                _createsThisFrame = 0;
            }
            if (_createsThisFrame >= ClientCreatePerFrame) {
                return false;
            }
            _createsThisFrame++;
            return true;
        }
    }

    public static class ClientGenThrottle {
        public const string CrystalStubName = "~mp-crystal-skip";
        public static bool TerrainGenActive;

        static GameObject _crystalStub;
        static int _crystalSkips;

        public static bool IsCrystalStub(CrystalBehaviour c) {
            return c != null && c.gameObject != null && c.gameObject.name == CrystalStubName;
        }

        public static bool TrySkipCrystalCreate(string id, ref GameObject result) {
            if (!Net.IsRunning || !Net.IsClient) {
                return false;
            }
            if (!SharedMain.LocalWorldIsGenerating && !Util.IsGeneratingWorld()) {
                return false;
            }
            if (!LooksLikeCrystalId(id) || string.Equals(id, "Special/CrystalDistort", StringComparison.OrdinalIgnoreCase)) {
                return false;
            }
            if (_crystalStub == null) {
                _crystalStub = new GameObject(CrystalStubName);
                _crystalStub.hideFlags = HideFlags.HideAndDontSave;
                _crystalStub.SetActive(false);
                _crystalStub.AddComponent<CrystalBehaviour>();
            }
            result = _crystalStub;
            _crystalSkips++;
            return true;
        }

        public static void NoteTerrainBegin(int depth) {
            TerrainGenActive = true;
            _crystalSkips = 0;
        }

        public static void NoteTerrainEnd(int depth) {
            TerrainGenActive = false;
            _crystalSkips = 0;
        }
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.SetColor))]
static class CrystalBehaviour_SetColor_Exact_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Color clr) {
        if (!Net.IsRunning) {
            return true;
        }
        if (Net.IsClient || CrystalAuthority.ApplyingAuthority) {
            if (__instance.sprite != null) {
                __instance.sprite.color = clr;
            }
            if (__instance.light != null) {
                __instance.light.color = clr;
            }
            return false;
        }
        return true;
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.GenerationFinished))]
static class CrystalBehaviour_GenerationFinished_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, ref GameObject __state) {
        __state = __instance != null ? __instance.gameObject : null;
        if (!Net.IsRunning || !Net.IsClient) {
            return true;
        }
        if (CrystalAuthority.ClientGenThrottle.IsCrystalStub(__instance)) {
            return false;
        }
        if (WorldGeneration.world != null
            && WorldGeneration.world.GetBlock(__instance.transform.position - __instance.transform.up * 2f) == 0) {
            UnityEngine.Object.Destroy(__instance.gameObject);
            Collider2D col = __instance.GetComponent<Collider2D>();
            if (col != null) {
                col.enabled = false;
            }
        }
        return false;
    }

    static void Postfix(CrystalBehaviour __instance, GameObject __state) {
        if (Net.IsRunning && Net.IsClient
            && __instance != null
            && !CrystalAuthority.ClientGenThrottle.IsCrystalStub(__instance)) {
            CrystalAuthority.EnsureClientEffectsList(__instance);
        }
        if (!Net.IsRunning || !Net.IsServer || __state == null) {
            return;
        }
        CrystalAuthority.MarkGenerationDone(__state);
        CrystalAuthority.HostCapture(__state);
        CrystalLocalAuthority.HostCaptureAndBroadcastBag(__state);
    }
}

[HarmonyPatch(typeof(Utils), nameof(Utils.Create), new Type[] { typeof(string), typeof(Vector2), typeof(float) })]
static class Utils_Create_SkipClientCrystalDuringGen_MultiplayerPatch {
    static bool Prefix(string id, ref GameObject __result) {
        return !CrystalAuthority.ClientGenThrottle.TrySkipCrystalCreate(id, ref __result);
    }
}

[HarmonyPatch(typeof(PlayerCamera), nameof(PlayerCamera.HandleBuildingHover))]
static class Crystal_HandleBuildingHover_ExamineGate_MultiplayerPatch {
    static void Postfix(BuildingEntity build, ref UsableObject usableObject) {
        if (!Net.IsRunning || !build.TryGetComponent(out CrystalBehaviour crystal)) {
            return;
        }
        var effects = CrystalAuthority.GetEffects(crystal);
        if (effects == null || effects.Count == 0) {
            if (!CrystalLocalAuthority.IsLocalAuth(build.gameObject)) {
                usableObject = null;
                if (PlayerCamera.main != null) {
                    var cursorField = Traverse.Create(PlayerCamera.main).Field("cursor");
                    if (cursorField.GetValue<int>() == 4) {
                        cursorField.SetValue(2);
                    }
                }
            }
            return;
        }
        if (build.TryGetComponent(out UsableObject uo) && uo != null) {
            uo.enabled = true;
            usableObject = uo;
        }
    }
}

[HarmonyPatch(typeof(SyncInfo), nameof(SyncInfo.CreateObject))]
static class SyncInfo_CreateObject_CrystalAuthority_MultiplayerPatch {
    static bool _meshAlreadyThere;

    static bool Prefix(SyncInfo __instance, ref bool __result) {
        _meshAlreadyThere = __instance != null && __instance.go != null;
        if (!Net.IsRunning || !Net.IsClient || __instance == null || _meshAlreadyThere) {
            return true;
        }
        if (SharedMain.LocalWorldIsGenerating || Util.IsGeneratingWorld()) {
            if (!CrystalAuthority.LoadStep.TryAllowCreate()) {
                __result = false;
                return false;
            }
            return true;
        }
        string res;
        try {
            res = __instance.resourceId.Value;
        } catch (Exception ex) {
            ModLog.Debug(LogDomain.World, $"SyncInfo.CreateObject resourceId read deferred: {ex.Message}");
            return true;
        }
        if (CrystalAuthority.LooksLikeCrystalId(res) && !CrystalAuthority.SyncBudget.TryConsumeClientCreate()) {
            __result = false;
            return false;
        }
        return true;
    }

    static void Postfix(SyncInfo __instance, bool __result) {
        if (_meshAlreadyThere || !__result || !Net.IsRunning || !Net.IsClient || __instance == null || __instance.go == null) {
            return;
        }
        if (__instance.go.TryGetComponent(out CrystalBehaviour _) || CrystalAuthority.LooksLikeCrystal(__instance.go)) {
            if (CrystalLocalAuthority.TryDiscardReplica(__instance)) {
                return;
            }
            if (CrystalLocalAuthority.TryApplyCachedBag(__instance)) {
                return;
            }
            CrystalAuthority.ClientTryApplyOrRequest(__instance);
        }
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnCollisionEnter2D))]
static class CrystalBehaviour_OnCollisionEnter2D_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, Collision2D collision) {
        if (!Net.IsRunning) {
            return true;
        }
        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            bool isBodyHit = Util.TryGetBodyFromObject(collision.gameObject, out Body body);
            if (isBodyHit) {
                bgo.body = body;
            } else {
                body = null;
                bgo.body = null;
            }
            data["CrystalCollision"] = collision;
            data["CrystalCollisionIsBody"] = isBodyHit;
            CrystalLocalFx.Begin(body, __instance);
            try {
                foreach (CrystalEffect effect in __instance.effects) {
                    effect.Touched(collision.gameObject);
                }
            } finally {
                CrystalLocalFx.End();
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.BuildingHit))]
static class CrystalBehaviour_BuildingHit_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance, AttackInfo atk) {
        if (!Net.IsRunning) {
            return true;
        }
        if (SharedMain.LocalWorldIsGenerated && SharedMain.WorldInSimRange(__instance.transform.position)) {
            BodyGetterOverrider bgo = __instance.GetOrAddComponent<BodyGetterOverrider>();
            AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
            Body attacker = Body_Attack_MultiplayerPatch.LastAttacker;
            if (attacker != null) {
                bgo.body = attacker;
                data["CrystalBuildingHitAttacker"] = attacker;
            } else {
                bgo.body = null;
            }
            data["CrystalBuildingHitAtk"] = atk;
            CrystalLocalFx.Begin(attacker, __instance);
            try {
                foreach (CrystalEffect effect in __instance.effects) {
                    effect.Hit(atk);
                }
            } finally {
                CrystalLocalFx.End();
            }
        }
        return false;
    }
}

[HarmonyPatch(typeof(CrystalBehaviour), nameof(CrystalBehaviour.OnUse))]
static class CrystalBehaviour_OnUse_MultiplayerPatch {
    static bool Prefix(CrystalBehaviour __instance) {
        if (!Net.IsRunning) {
            return true;
        }
        List<CrystalEffect> effects = CrystalAuthority.GetEffects(__instance);
        if (effects == null || effects.Count == 0) {
            return false;
        }
        AnyDataContainerComponent data = __instance.GetOrAddComponent<AnyDataContainerComponent>();
        if (!data.WasntTouched("CrystalExaminedLocal")) {
            return false;
        }

        BuildingEntity buildingEntity = __instance.build;
        buildingEntity.description = buildingEntity.description + "\n\n" + ((effects.Count == 1) ? Locale.GetBuilding("crystalproperty") : Locale.GetBuilding("crystalproperties"));
        List<string> names = new List<string>();
        for (int i = 0; i < effects.Count; i++) {
            names.Add(Locale.GetBuilding(effects[i].GetType().Name.ToLower()));
        }
        __instance.build.description += string.Join(", ", names);
        if (__instance.TryGetComponent(out UsableObject uo)) {
            UnityEngine.Object.Destroy(uo);
        }
        return false;
    }
}

[HarmonyPatch(typeof(PlayerCamera), "FlashBrief")]
static class PlayerCamera_FlashBrief_CrystalLocal_MultiplayerPatch {
    static bool Prefix() {
        if (!Net.IsRunning) {
            return true;
        }
        if (Time.frameCount <= CrystalLocalFx.AllowFlashFrame) {
            return true;
        }
        if (Time.frameCount <= CrystalLocalFx.BlockFlashFrame) {
            return false;
        }
        return !CrystalLocalFx.SuppressNonLocalFeedback();
    }
}
#endif
