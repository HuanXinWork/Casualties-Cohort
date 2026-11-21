using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using CasualtiesTogetherUtils;
using KrokV5Optimization;
using Together.Diagnostics;
using Together.MpConstants;
using Unity.VisualScripting;
using UnityEngine;

namespace Together;

/// <summary>
/// 全局网络实体注册表（第 2 部分：视距内近场注册、远距自动回收、索引查询与调试叠加层）。
/// </summary>
public partial class NetObjectRegistry {
    const float ImmediateRegistrationSearchRadius = 80f;
    static int MAX_REGISTER_BATCH = 16;
    static int server_slowmode_type_counter = 0;

    static float timer_fastsync = 0f;
    static float timer_objsync = 0f;
    static float timer_raresync = 0f;

    public static bool _DEV_ENABLE_SYNCINFO_SNITCHING = false;

    internal static HashSet<string> lowpriority_objects_resourceids = new HashSet<string> {
        VanillaAssetKeys.PrefabBulletCasing,
        VanillaAssetKeys.PrefabDroppings,
    };

    static readonly HashSet<string> networkignored_buildings_maingame = new HashSet<string> {
        "KSMUTLTI_IGN",
        "background",
        "lifepodbuttonheat",
        "lifepodbuttonshower",
        "lifepodheater",
        "lifepodshower",
        "lifepodpump",
        "sandvine",
        "veintop",
        "veinbottom",
        "marbleBackground",
        "mushroomdropperpayload",
        "macrofleshrebuilder",
    };

    public static void NewGO(GameObject obj) {
        if (Net.IsServer) {
            Server_EnsureItemIsNetworkRegistered(obj);
        } else if (!SyncRegistry.TryGetValue(obj, out _)) {
            SafeDestroyObject(obj);
        }
    }

    public static void SafeNewGO(GameObject obj) {
        if (!ObjectShouldNotBeSynced(obj)) {
            NewGO(obj);
        }
    }

    public static HashSet<GameObject> GetIdentifiedBackgroundsInRadius(Vector2 pos, float radius) {
        var result = new HashSet<GameObject>();
        float radiusSqr = radius * radius;
        foreach (var item in TrackerBuildingEntityBackgroundified.all_instances) {
            if (item.is_a_background_tilemap && KM.DistanceCheck_PreSqr(item.transform.position, pos, radiusSqr)) {
                result.Add(item.gameObject);
            }
        }
        return result;
    }

    public static HashSet<GameObject> GatherClosestObjectsToSync(Vector2 pos, Body body, float radius) {
        HashSet<GameObject> objects = GetIdentifiedBackgroundsInRadius(pos, radius);
        Collider2D[] physInRadius = Physics2D.OverlapCircleAll(pos, radius);
        var itemsAndBuildings = physInRadius
            .Where(x => x.GetComponent<Item>() != null || x.GetComponent<BuildingEntity>() != null)
            .Select(x => x.gameObject);

        foreach (GameObject item in itemsAndBuildings) {
            if (item.TryGetComponent<Container>(out var ci)) {
                objects.AddRange(ci.GetAllItems().Select(x => x.gameObject));
            }
        }

        objects.AddRange(itemsAndBuildings);

        if (body != null) {
            objects.AddRange(body.GetAllItemsThorough().Select(x => x.gameObject));
        }

        var nearbyBodies = NetBody.GetBodiesInRadius(pos, radius);
        if (body != null && body.TryGetNetBody(out var locNb)) {
            nearbyBodies.Remove(locNb);
        }
        foreach (NetBody nb in nearbyBodies) {
            foreach (Item carried in nb.body.GetAllItemsThorough()) {
                objects.Add(carried.gameObject);
            }
        }

        objects.RemoveWhere(x => x == null || ObjectShouldNotBeSynced(x));
        return objects;
    }

    internal static bool ShouldKeepRegistered(GameObject obj) {
        if (obj == null)
            return false;
        // 挂载 KeepForJoiners 的关键层建筑（如货运心脏）常驻注册表，保证中途入房客户端随基线直接接收
        if (obj.GetComponent<KeepForJoiners>() != null)
            return true;
        Transform simulationTransform = obj.transform;
        while (simulationTransform.parent != null && simulationTransform.parent.TryGetComponent<Container>(out _)) {
            simulationTransform = simulationTransform.parent;
        }
        return SharedMain.WorldInSimRange(simulationTransform.position);
    }

    internal static void _ImmidiatelyRegisterCloseObjects(Vector2 pos, Body body) {
        HashSet<GameObject> touching = GatherClosestObjectsToSync(pos, body, ImmediateRegistrationSearchRadius);
        touching.RemoveWhere(x => IsRegistered(x) || (Net.IsServer && !ShouldKeepRegistered(x)));

        int counter = 0;
        int crystalCounter = 0;
        foreach (GameObject thing in touching) {
#if FULLGAME
            bool crystal = CrystalAuthority.LooksLikeCrystal(thing);
            if (crystal && crystalCounter >= CrystalAuthority.SyncBudget.HostRegisterPerStamp) {
                continue;
            }
#endif
            NewGO(thing);
#if FULLGAME
            if (crystal) {
                crystalCounter++;
                counter++;
            } else
#endif
            if (!KM.DistanceCheck(thing.transform.position, pos, GameplayRanges.CraftGroundScanRadius)) {
                counter++;
            }
            if (counter >= MAX_REGISTER_BATCH) {
                break;
            }
        }
    }

    public static bool IsObjectDynamic(GameObject go) {
        if (go.TryGetComponent<Item>(out _))
            return go.transform.parent == null;
        if (go.TryGetComponent<GrabberPlant>(out _))
            return true;
        if (!go.TryGetComponent<Rigidbody2D>(out var rb))
            return false;
        return rb.bodyType == RigidbodyType2D.Dynamic;
    }

    public static bool IsObjectPhysicsActive(GameObject go) {
        if (go.TryGetComponent<Item>(out _) && go.transform.parent != null)
            return false;
        if (!go.TryGetComponent<Rigidbody2D>(out var rb) || rb.IsSleeping())
            return false;
        return true;
    }

    public static void AlertObjectNotRegistered(bool popup = false) {
        if (popup) {
            PlayerCamera.main.DoAlert(Lang.Get("obj_not_net_registered"));
        }
        string msg = "Attempted to interact with unregistered object";
        if (log.verbose) {
            msg += ":\n" + new StackTrace();
        }
        ModLog.Warning(msg, LogDomain.Item);
        PlayerCamera.main.PlayUISound(PlayerCamera.UISoundType.Deny);
    }

    internal static void _ObjectSyncUpdateLoopUniversal(bool do_slow_mode) {
        if (Net.IsClient) {
            ScavPlayer locPlr = ScavPlayer.LocalPlayer;
            if (locPlr.body != null) {
                if (!locPlr.body.alive == do_slow_mode) {
                    _ImmidiatelyRegisterCloseObjects(PlayerCamera.main.transform.position, locPlr.body);
                }
                if (locPlr.body.alive && do_slow_mode) {
                    _ImmidiatelyRegisterCloseObjects((Vector2)PlayerCamera.main.transform.position - new Vector2(0f, 60f), null);
                }
            }
            return;
        }

        if (!do_slow_mode)
            return;

#if FULLGAME
        LayerBuildingStream.HostRegister();
#endif

        int autoUnregisterCount = 0;
        List<SyncInfo> dropWhole = null;
        foreach (var item in SyncRegistry) {
            if (IsSaveStateItem(item.Key))
                continue;
            item.Value.tracker.UpdateDistanceChecks();
            if (!item.Value.tracker.isFar)
                continue;
            if (InPlayerInventory(item.Key))
                continue;
            if (item.Key.TryGetComponent(out TraderScript _))
                continue;

            if (item.Value != null && !item.Value.IsDestroyed) {
                if (item.Value.go == null)
                    item.Value.go = item.Key;
                dropWhole ??= new List<SyncInfo>();
                dropWhole.Add(item.Value);
                autoUnregisterCount++;
                if (autoUnregisterCount > MAX_REGISTER_BATCH * 3)
                    break;
            }
        }

        if (dropWhole != null) {
            for (int i = 0; i < dropWhole.Count; i++) {
                SyncInfo si = dropWhole[i];
                if (si != null && !si.IsDestroyed && !KeepFarMesh(si, si.go))
                    ReleaseFarSync(si);
            }
        }

        if (server_slowmode_type_counter == 0) {
            foreach (ScavPlayer plr in ScavPlayer.AllDeadPlayers) {
                SlowStamp.Enqueue(plr.pos, plr.body);
            }
            server_slowmode_type_counter++;
        } else if (server_slowmode_type_counter == 1) {
            foreach (ScavPlayer plr in ScavPlayer.AllLivingPlayers) {
                SlowStamp.Enqueue(plr.pos - new Vector2(0f, 80f), plr.body);
            }
            server_slowmode_type_counter++;
        } else {
            server_slowmode_type_counter = 0;
        }
    }

    public static bool IsRegistered(Component obj) => SyncRegistry.ContainsKey(obj.gameObject);
    public static bool IsRegistered(GameObject obj) => SyncRegistry.ContainsKey(obj);

    public static SyncInfo GetSyncInfo(Component obj) {
        if (obj != null && SyncRegistry.TryGetValue(obj.gameObject, out var si))
            return si;
        return null;
    }

    public static bool TryGetSyncInfo(Component obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj.gameObject, out si))
            return true;
        si = null;
        return false;
    }

    public static bool TryGetSyncInfo(GameObject obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj, out si))
            return true;
        si = null;
        return false;
    }

    public static bool TryGetSyncInfo(knetid syncid, out SyncInfo si) {
        if (NetIdToSyncInfoDict.TryGetValue(syncid, out si))
            return true;
        si = null;
        return false;
    }

    public static bool TryGetSyncInfoLive(knetid syncid, out SyncInfo si) {
        if (TryGetSyncInfo(syncid, out si) && si is not null && !si.IsDestroyed)
            return true;
        si = null;
        if (SyncRegistry == null)
            return false;
        foreach (SyncInfo cur in SyncRegistry.Values) {
            if (cur is null || cur.IsDestroyed)
                continue;
            try {
                if (cur.syncId != syncid)
                    continue;
            } catch (Exception) {
                continue;
            }
            NetIdToSyncInfoDict[syncid] = cur;
            si = cur;
            return true;
        }
        return false;
    }

    public static bool TryGetSyncInfoOrRegister(Component obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj.gameObject, out si))
            return true;
        NewGO(obj.gameObject);
        si = null;
        bool created = false;
        FixSyncRegister.Recover(obj.gameObject, ref si, ref created);
        return created;
    }

    public static bool TryGetSyncInfoOrRegister(GameObject obj, out SyncInfo si) {
        if (SyncRegistry.TryGetValue(obj, out si))
            return true;
        NewGO(obj);
        si = null;
        bool created = false;
        FixSyncRegister.Recover(obj, ref si, ref created);
        return created;
    }

    public static bool ObjectShouldBeSynced(GameObject obj) => !ObjectShouldNotBeSynced(obj);

    public static bool ObjectShouldNotBeSynced(GameObject obj) {
        if (obj.TryGetComponent(out BuildingEntity b)) {
            if (networkignored_buildings_maingame.Contains(b.id))
                return true;
            if (obj.TryGetComponent<Climbable>(out var c)) {
                // spicevine / spicevineceiling 既是可攀爬植物又可被击碎，必须作为普通同步建筑处理否则破坏状态无法跨端同步
                bool spice = b.id == TrackerClimbableEntity.spicevine_id
                    || b.id == TrackerClimbableEntity.spicevineceiling_id
                    || (c.name != null && c.name.StartsWith("spicevineceiling"));
                if (spice) {
                    if (c.name != null && c.name.StartsWith("spicevineceiling") && b.id != TrackerClimbableEntity.spicevineceiling_id)
                        b.id = TrackerClimbableEntity.spicevineceiling_id;
                    return false;
                }
                if (!obj.TryGetComponent<TrackerClimbableEntity>(out _)) {
                    var r = obj.AddComponent<TrackerClimbableEntity>();
                    if (b.id == "veinloop" || b.id == TrackerClimbableEntity.veinloop_new_id) {
                        if (string.IsNullOrEmpty(r.og_id))
                            r.og_id = "veinloop";
                        b.id = TrackerClimbableEntity.veinloop_new_id;
                    }
                    if (!r.is_known_climbable) {
                        ModLog.Error($"Unknown Climbable: {c.name} -> id: {b.id}", LogDomain.World);
                    }
                }
                return true;
            }
        }

        if (obj.TryGetComponent(out KrokoshaScavMultiGameObjectNetworkTracker_FORCEIGNORE _))
            return true;

#if FULLGAME
        if (obj.TryGetComponent(out TogetherCrystalLocalMarker _)
            && !obj.TryGetComponent(out SyncInfoGameObjectTracker _)) {
            return true;
        }
#endif
        return false;
    }

    public static string DebugStringGameObject(GameObject go) {
        string kind = go.TryGetComponent<BuildingEntity>(out _) ? "BUILDING" : (go.TryGetComponent<Item>(out _) ? "ITEM" : "UNKNOWN_OBJECT");
        string s = $"{kind}(res: {SyncInfo.GetItemID(go)}, name: {go.name}";
        if (go.TryGetTracker(out var tracker)) {
            s += $"{tracker}";
        }
        return s + ")";
    }

    public static SyncInfo Server_EnsureItemIsNetworkRegistered(GameObject go) {
        try {
            // 退房或场景卸载期间 Net.ShutdownReset 已清空 ServerEntityManager 但 Type 复位为 Host，须先校验会话存活与实体管理器非空
            if (go == null || !Net.IsRunning || !Net.IsServer || Net.ServerEntityManager == null)
                return null;

            if (SyncRegistry.TryGetValue(go, out var v) && v != null && !v.IsDestroyed)
                return v;

            if (go.TryGetComponent(out SyncInfoGameObjectTracker existing) && existing.syncInfo != null && !existing.syncInfo.IsDestroyed) {
                SyncRegistry[go] = existing.syncInfo;
                if (existing.syncInfo.go == null)
                    existing.syncInfo.go = go;
#if FULLGAME
                if (go.TryGetComponent(out CrystalBehaviour _) || CrystalAuthority.LooksLikeCrystal(go)) {
                    CrystalAuthority.HostOnCrystalRegistered(go, existing.syncInfo);
                }
#endif
                return existing.syncInfo;
            }

            var si = Net.ServerEntityManager.AddEntity<SyncInfo>(e => {
                e.go = go;
                e.GetItemID();
            });

#if FULLGAME
            if (go != null && si != null && (go.TryGetComponent(out CrystalBehaviour _) || CrystalAuthority.LooksLikeCrystal(go))) {
                SyncRegistry[go] = si;
                if (si.go == null)
                    si.go = go;
                CrystalAuthority.HostOnCrystalRegistered(go, si);
            }
#endif
            return si;
        } catch (Exception ex) {
            ModLog.Exception(LogDomain.Item, $"Server_EnsureItemIsNetworkRegistered({go})", ex);
            return null;
        }
    }

    public static bool Client_DeleteUnregisteredSyncableObject(GameObject obj) {
        if (!ObjectShouldNotBeSynced(obj)) {
            SafeDestroyObject(obj);
            return true;
        }
        return false;
    }

    public static void SafeDestroyObject(GameObject go) {
        if (go.TryGetComponent(out Container container)) {
            container.UnloadAllItems();
        }
        if (SyncRegistry.TryGetValue(go, out var si)) {
            SyncRegistry.Remove(si.go);
            si.go = null;
            si.Destroy();
        }
        Destroy(go.gameObject);
    }

    void Update() {
        if (!Net.IsRunning || !Util.IsWorldGenerated())
            return;

        timer_fastsync += ClientMain.AdaptiveSyncTimerDelta;
        timer_objsync += ClientMain.AdaptiveSyncTimerDelta;
        timer_raresync += ClientMain.AdaptiveSyncTimerDelta;
        if (timer_fastsync > _FastSyncFrequency) {
            timer_fastsync = 0f;
        }
        if (timer_objsync > _DefaultSyncFrequency) {
            _ObjectSyncUpdateLoopUniversal(false);
            timer_objsync = 0f;
        }
        if (timer_raresync > _RareSyncFrequency) {
            _ObjectSyncUpdateLoopUniversal(true);
            timer_raresync = 0f;
        }
    }

    void OnGUI() {
        if (!Net.IsRunning || !_DEV_ENABLE_SYNCINFO_SNITCHING || Camera.main == null)
            return;

        int ogFont = GUI.skin.label.fontSize;
        foreach (var item in SyncRegistry) {
            GameObject go = item.Key;
            SyncInfo isi = item.Value;
            if (go == null || isi == null)
                continue;
            try {
                Vector2 pos = Camera.main.WorldToScreenPoint(go.transform.position);
                pos = new Vector2(pos.x, Screen.height - pos.y);
                if (pos.x <= 0f || pos.y <= 0f || pos.x >= Screen.width || pos.y >= Screen.width)
                    continue;

                const float bsize = 17f;
                const float elsize = 13f;
                float y = 0f;
                GUI.skin.label.normal.textColor = Color.white;
                GUI.skin.label.fontSize = 10;
                GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "syncid: " + isi.syncId);
                y += elsize;
                GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "combat_relax: " + isi.relaxed_combat_sync);
                y += elsize;
                GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "lastUpdateTick: " + isi.lastUpdateTick);
                y += elsize;
                GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "pos: " + (Vector2)go.transform.position);
                y += elsize;

                int o = 0;
                bool activeNow = false;
                if (isi.IsItem()) {
                    o = ItemSync.cur_items_check.IndexOf(isi.item);
                    activeNow = o >= ItemSync.cur_items_check_index || o < ItemSync.cur_items_check_index + ItemSync.max_items_to_check_per_iteration;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "is contained: " + ItemSync.ItemIsInContainer(isi.item));
                    y += elsize;
                } else if (isi.IsBuilding()) {
                    o = ScavMPBuildingSynchronizer.cur_buildings_check.IndexOf(isi.building);
                    activeNow = o >= ScavMPBuildingSynchronizer.cur_buildings_check_index || o < ItemSync.cur_items_check_index + ScavMPBuildingSynchronizer.cur_buildings_check_index;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "physics: " + ScavMPBuildingSynchronizer.BuildingHasActivePhysics(isi.building));
                    y += elsize;
                    if (isi.IsTrader()) {
                        var ttracker = go.GetComponent<TrackerTrader>();
                        if (ttracker != null && ttracker.focused_body != null) {
                            GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "trader_body: " + ttracker.focused_body.GetNetBody().plr);
                            y += elsize;
                        }
                    }
                }
                GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "system index: " + o + (activeNow ? " NOW" : ""));
                y += elsize;
                SyncInfoGameObjectTracker t = isi.tracker;
                if (t != null) {
                    GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "see12: " + t.isNearby);
                    y += elsize;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "see64: " + t.isVisible);
                    y += elsize;
                    GUI.Label(new Rect(pos.x, pos.y + y, 400f, bsize), "seeSim: " + t.isInSimulationRange);
                }
            } catch (Exception ex) {
                ModLog.Flow(LogDomain.UI, "NetObjectRegistry.OnGUI", ex.Message);
            }
        }

        GUI.skin.label.normal.textColor = Color.white;
        GUI.skin.label.fontSize = ogFont;
    }
}
