using HarmonyLib;
using KrokV5Optimization;
using System;
using System.Collections.Generic;
using System.IO;
using System.IO.Compression;
using System.Linq;
using System.Reflection;
using System.Runtime.CompilerServices;
using Together;
using Together.MpConstants;
using UnityEngine;

namespace CasualtiesTogetherUtils;

public static partial class Util {

    #region WORLD_STUFF

    public static bool IsInWorldBoundaries(in Vector2 pos) {
        return Math.Abs(pos.x) < (float)world.width * 0.5f && Math.Abs(pos.y) < (float)world.height * 0.5f;
    }

    public static float TilesToMeters(float tiles_distance) {
        //OG: WorldGeneration.PlayerLayerDepthMeters V5t4
        return tiles_distance * 0.3f;
    }

    public static float MetersToTiles(float meters_distance) {
        //OG: WorldGeneration.PlayerLayerDepthMeters V5t4
        return meters_distance / 0.3f;
    }


    public static void BlockFill(int x, int y, int endx, int endy, ushort block_id) {
        if (world != null) {
            if (x > endx) {
                var g = x;
                x = endx;
                endx = g;
            }
            if (y > endy) {
                var g = y;
                y = endy;
                endy = g;
            }

            x = Math.Max(x, 0);
            y = Math.Max(y, 0);

            BlockFillUnsafe(
                x,
                y,
                Math.Min(endx, (int)WorldGeneration.world.width),
                Math.Min(endy, (int)WorldGeneration.world.height),
                block_id
            );
        }

    }


    public static void BlockFillUnsafe(int startX, int startY, int endX, int endY, ushort block_id) {

        var tiles = worldBlocks;

        for (int x = startX; x <= endX; x++) {
            for (int y = startY; y <= endY; y++) {
                tiles[x, y] = block_id;
            }
        }
        if (!world.generatingWorld) {

            for (int x = startX / WorldGeneration.CHUNKSIZE; x <= endX / WorldGeneration.CHUNKSIZE; x++) {
                for (int y = startY / WorldGeneration.CHUNKSIZE; y <= endY / WorldGeneration.CHUNKSIZE; y++) {
                    WorldGeneration.world.UpdateChunk(new Vector2Int(x, y));
                }
            }

        }
    }


    public static void RegisterLayerModifier<T>() where T : LayerModifier {
        var inst = Activator.CreateInstance(typeof(T), LayerModifier.availableModifiers.Length) as LayerModifier;

        LayerModifier.availableModifiers = LayerModifier.availableModifiers.AddToArray(inst);
    }

    // heatedrock=36, mantle=39, magma liquid=6 - layer 9 top-row landings were cooking players mid-load.
    public static bool PlaceBody_SpawnSurfaceIsUnsafe(Vector2 groundPos) {
        if (WorldGeneration.world == null) {
            return false;
        }
        Vector2Int blockPos = WorldGeneration.world.WorldToBlockPos(groundPos);
        ushort block = WorldGeneration.world.GetBlock(blockPos);
        if (block == 36 || block == 39) {
            return true;
        }
        if (FluidManager.main != null && FluidManager.main.GetLiquid(groundPos) == 6) {
            return true;
        }
        // also reject standing in magma a step above the ground tile
        if (FluidManager.main != null && FluidManager.main.GetLiquid(groundPos + Vector2.up) == 6) {
            return true;
        }
        return false;
    }

    public static Vector2 PlaceBody_FindSpawnLocation(float x, Body b) {
        WorldGeneration world = WorldGeneration.world;
        Vector2 p = Vector2.zero;
        try {
            if (world == null) {
                return p;
            }
            if (world.biomeOverride == WorldGeneration.OverrideSceneType.Tutorial) {
                return GameObject.Find("TUTORIALSPAWN").transform.position;
            }
            if (world.biomeDepth == 11) {
                return GameObject.Find("ENDLABSPAWN").transform.position;
            }
            // ascent: the drill pod is built on this spot, then the body is lifted.
            // one point for the whole group, read from blocks so a missing collider cannot drop us into the floor.
            if (world.biomeDepth == 10) {
                return AscentSpawn(world);
            }
            // every other layer stays in the top chunk row. a physics walk kept going
            // through unbuilt chunks and stopped inside the mantle on where we must reach.
            p = TopRowSpawn(world, x);
        } catch (Exception e) {
            log.error(e);
        }
        return p;
    }

    static Vector2 TopRowSpawn(WorldGeneration world, float x) {
        int size = ChunkSize();
        int height = (int)world.height;
        int width = (int)world.width;
        int top = height - 1;
        int row = top - (top % size);
        int bx = BlockX(world, x, width);
        bool air = false;
        for (int y = top; y >= row; y--) {
            ushort block = world.GetBlock(new Vector2Int(bx, y));
            if (block == 0) {
                air = true;
                continue;
            }
            if (!air) {
                continue;
            }
            PaintSpawnChunk(world, bx, y + 1, size);
            return world.BlockToWorldPos(new Vector2Int(bx, y + 1));
        }
        int hang = top - 2;
        if (hang < row) {
            hang = row;
        }
        PaintSpawnChunk(world, bx, hang, size);
        return world.BlockToWorldPos(new Vector2Int(bx, hang));
    }

    static Vector2 AscentSpawn(WorldGeneration world) {
        int size = ChunkSize();
        int height = (int)world.height;
        int width = (int)world.width;
        int bx = BlockX(world, 0f, width);
        for (int y = 0; y < height; y++) {
            if (world.GetBlock(new Vector2Int(bx, y)) != 0) {
                continue;
            }
            PaintSpawnChunk(world, bx, y, size);
            return world.BlockToWorldPos(new Vector2Int(bx, y));
        }
        PaintSpawnChunk(world, bx, 1, size);
        return world.BlockToWorldPos(new Vector2Int(bx, 1));
    }

    static int BlockX(WorldGeneration world, float x, int width) {
        int bx = world.WorldToBlockPos(new Vector2(x, 0f)).x;
        if (bx < 0) {
            return 0;
        }
        if (bx >= width) {
            return width - 1;
        }
        return bx;
    }

    static int ChunkSize() {
        int size = WorldGeneration.CHUNKSIZE;
        if (size < 1) {
            return 64;
        }
        return size;
    }

    static void PaintSpawnChunk(WorldGeneration world, int bx, int by, int size) {
        int cx = bx / size;
        int cy = by / size;
        if (cx < 0 || cy < 0) {
            return;
        }
        world.UpdateChunk(new Vector2Int(cx, cy));
    }

    #endregion

    public static int CalculateMaxRepeats<T>(List<T> list) {
        var occurences = list.GroupBy(x => x).Select(g => new { Value = g.Key, Count = g.Count() });
        return occurences.Max(g => g.Count);
    }
    public static List<T> LimitMaxRepeats<T>(List<T> list, int maxrepeats) {
        var occurences = list.GroupBy(x => x).SelectMany(g => g.Take(maxrepeats)).ToList();
        return occurences;
    }


    public static bool WorldPositionIsOnScreen(Vector2 pos) => WorldPositionIsOnScreen(pos, Vector2.zero);
    public static bool WorldPositionIsOnScreen(Vector2 pos, Vector2 bounds) {
        var cam = Camera.main;

        float hh = cam.orthographicSize;
        float hw = hh * cam.aspect;

        var campos = (Vector2)cam.transform.position;

        float minX = campos.x - hw + bounds.x;
        float maxX = campos.x + hw - bounds.x;
        float minY = campos.y - hh + bounds.y;
        float maxY = campos.y + hh - bounds.y;

        return pos.x.IsInRange(minX, maxX) && pos.y.IsInRange(minY, maxY);
    }
    public static Vector2 ClampWorldPositionToCameraView(Vector2 pos) {
        return ClampWorldPositionToCameraView(pos, Vector2.zero);
    }

    public static Vector2 ClampWorldPositionToCameraView(Vector2 pos, Vector2 bounds) {

        var cam = Camera.main;

        float hh = cam.orthographicSize;
        float hw = hh * cam.aspect;

        var campos = (Vector2)cam.transform.position;

        float minX = campos.x - hw + bounds.x;
        float maxX = campos.x + hw - bounds.x;
        float minY = campos.y - hh + bounds.y;
        float maxY = campos.y + hh - bounds.y;

        return new Vector2(
                Mathf.Clamp(pos.x, minX, maxX),
                Mathf.Clamp(pos.y, minY, maxY)
        );
    }


    public static void SwitchDirLookAt(this Body body, Vector2 targetLookAt) {
        if ((body.isRight && body.targetLookPos.x < body.transform.position.x) || (!body.isRight && body.targetLookPos.x > body.transform.position.x)) {
            body.SwitchDir();
        }
    }


    public static void ForceSwitchDir(this Body body, bool isRight) {
        if (isRight != body.isRight) {
            ForceSwitchDir(body);
        }
    }
    public static void ForceSwitchDir(this Body body) {
        var wasstanding = body.standing;
        body.standing = true;
        body.SwitchDir(); // it works only when body.standing is true,  and it doesnt seem to break anything anyway
        body.standing = wasstanding;
    }

    public static void ResetEntropy(this Body body) {
        body.moveDir = Vector2.zero;
        body.SetVelocity(Vector2.zero);
        body.ForceStand();
    }

    public static void ForceStand(this Body body) {
        if (!body.standing) {
            body.shock = 0f;
            body.Stand(true);
        }
    }


    public static bool IsDeadOrCriticallyDying(this Body body) {
        return !body.alive || body.isCriticallyDying;
    }


    public static bool IsAnyLimbMissing(this Body body) {
        return body.limbs.Any(x => x.dismembered);
    }


    public static void RegrowAllLimbs(this Body body) {
        if (body.IsAnyLimbMissing()) {

            var ogbodypos = body.transform.position;

            if (!body.isRight) {
                body.ForceSwitchDir();
            }
            body.Ragdoll();


            var a = body.GetComponentsInChildren<ClientMain.SavedHingeJointState>(true);

            foreach (var item in a) {

                item.transform.localPosition = item.a;
                item.transform.localRotation = item.b;

                item.connectedBody.transform.localPosition = item.aa;

                var euler_with_reference = item.bb.eulerAngles;
                euler_with_reference.z = item.transform.localRotation.eulerAngles.z - item.referenceAngle;

                item.connectedBody.transform.localRotation = Quaternion.Euler(euler_with_reference);

            }
            for (int i = 0; i < body.limbs.Length; i++) {
                var limb = body.limbs[i];
                limb.gameObject.SetActive(false);
                limb.dismembered = false;
            }


            foreach (Limb limb in body.limbs) {
                limb.gameObject.SetActive(true);
                limb.dismembered = false;
            }

            foreach (var item in a) {
                item.hinge.connectedBody = item.connectedBody;
            }


            foreach (Limb limb in body.limbs) {

                // because this shit gets reset for some reason
                var ca = limb.GetComponent<Collider2D>();
                foreach (var other_npc in NetBody.all_instances) {
                    var other_chara = other_npc.transform.parent;
                    var b = other_chara.GetComponentsInChildren<Collider2D>(true);
                    foreach (var cb in b) {
                        Physics2D.IgnoreCollision(ca, cb, true);
                    }
                }
            }

            body.shock = 0;
            body.transform.position = ogbodypos;
        }
    }


    public static void Body_DropAllItems(this Body body) {
        for (int i = 0; i < body.slots.Length; i++)
            if ((bool)body.GetItem(i))
                body.DropItem(i);
        foreach (var item in body.GetAllWearables())
            body.DropWearable(item);
    }
    public static void Body_ResetSkills(this Body body) {
        body.skills.Setup(body.charType);
    }


    public static void SetSetting(string name, object value) {
        var setting = Settings.settings.Find((Setting s) => s.name == name);
        setting.SetValue(value);
    }


    public static byte GetLimbIndex(Limb limb) => limb.GetIndex();


    public static Limb GetClosestLimb(this Body body, Vector2 pos, out float dist_sqr) {
        dist_sqr = 999999f;
        Limb result = body.limbs[(int)LimbIndex.Head];
        foreach (Limb limb in body.limbs) {
            if (!limb.dismembered && KM.DistanceCheck_PreSqr(pos, limb.transform.position, dist_sqr, out var newdist)) {
                result = limb;
                dist_sqr = newdist;
            }
        }
        return result;
    }

    public static bool TryGetLocalDiscordUser(out DiscordRPC.User user) {
        if (RPCManager.instance != null && RPCManager.instance.client != null && RPCManager.instance.client.CurrentUser != null) {
            user = RPCManager.instance.client.CurrentUser;
            return true;
        }
        user = null;
        return false;
    }


    public static bool CanTalk(this Body talker) {
        //OG: Talker.Talk V7.0.1
        return talker != null && (talker.conscious && talker.brainHealth > 50f && !talker.IsMindwiped());
    }


    public static bool TryGetTalkerOnObject(SyncInfo si, out Talker talker, out string name) {
        if (si != null) {
            return TryGetTalkerOnObject(si.go, out talker, out name);
        }
        talker = null;
        name = "";
        return false;
    }
    public static bool TryGetTalkerOnObject(GameObject go, out Talker talker) => TryGetTalkerOnObject(go, out talker, out var _);
    public static bool TryGetTalkerOnObject(GameObject go, out Talker talker, out string name) {

        name = "";
        talker = null;

        if (go != null) {
            if (go.TryGetComponent(out Body body)) {
                name = body.name;
                if (body.TryGetNetBody(out var npc)) { // get player username
                    name = npc.bodyName;
                }
                talker = body.talker;
            } else if (go.TryGetComponent(out talker)) {
                name = talker.name;
            } else if (go.TryGetComponent(out Limb limb)) {
                body = limb.body;
                name = body.name;
                if (body.TryGetNetBody(out var npc)) {// get player username
                    name = npc.bodyName;
                }
                talker = body.talker;
            } else if (go.TryGetComponent(out TraderScript trader)) {
                name = trader.name;
                talker = trader.talker;
            }
        }

        return talker != null;
    }


    public static (T, float) GetNearestComponent<T>(in List<T> all_instances, Vector2 pos) where T : MonoBehaviour {
        float dist = float.PositiveInfinity;
        T nearest = null;
        foreach (var c in all_instances) {
            var cdist = KM.DistanceSqr(c.transform.position, pos);
            if (cdist < dist) {
                dist = cdist;
                nearest = c;
            }
        }
        return (nearest, (dist));
    }


    public static Sprite CreateSprite(Texture2D texture, float pixelsPerUnit = 100f) {
        return Sprite.Create(texture, new Rect(0, 0, texture.width, texture.height), new Vector2(0.5f, 0.5f), pixelsPerUnit);
    }


    public static bool IsFinite(this float value) {
        return !float.IsNaN(value) && !float.IsInfinity(value);
    }
    public static bool IsFinite(this Vector2 value) {
        return IsFinite(value.x) && IsFinite(value.y);
    }


    public static object InvokeStatic(this MethodInfo method, params object[] parameters) {
        return method.Invoke(null, parameters);
    }

    // get field, with backup names
    public static FieldInfo GetField(Type type, params string[] fieldNames) {
        foreach (var f in fieldNames) {
            var field = AccessTools.Field(type, f);
            if (field != null)
                return field;
        }
        return null;
    }
    public static FieldInfo GetField<T>(params string[] fieldNames) {
        return GetField(typeof(T), fieldNames);
    }


    public static MethodInfo GetMethod(Type typ, params string[] methodNames) {
        foreach (var m in methodNames) {
            var method = typ.GetMethod(m, BindingFlags.NonPublic | BindingFlags.Public | BindingFlags.Static | BindingFlags.Instance | BindingFlags.FlattenHierarchy);
            if (method != null)
                return method;
        }
        return null;
    }

    public static MethodInfo GetMethod<T>(params string[] methodNames) {
        return GetMethod(typeof(T), methodNames);
    }


    private static bool? _checkedIfIsDemo = null;

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GameIsDemo() { // Stolen from CUCoreLib
        if (_checkedIfIsDemo.HasValue) return _checkedIfIsDemo.Value;

        string[] FullGameOnlyTypeNames = {
            "GuardScript",
            "GuardSpawner",
            "EndLabScript",
        };

        var matched = new HashSet<string>(StringComparer.Ordinal);
        var assemblies = AppDomain.CurrentDomain.GetAssemblies();
        foreach (var typeName in FullGameOnlyTypeNames) {
            foreach (var assembly in assemblies) {
                Type resolved;
                try {
                    resolved = assembly.GetType(typeName, false);
                } catch (Exception) {
                    // 动态生成或第三方异常程序集在类型探测时可能抛 TypeLoadException，忽略继续探测
                    resolved = null;
                }

                if (resolved == null) continue;

                matched.Add(typeName);
                break;
            }

            if (matched.Count >= 2) break;
        }

        _checkedIfIsDemo = matched.Count < 2;
        return _checkedIfIsDemo.Value;
    }

    [MethodImpl(MethodImplOptions.AggressiveInlining)]
    public static bool GameIsFull() => !GameIsDemo();

    #region COMPRESSION

    // 直接内联 CompressPool 池化 SharpZipLib 快路径（零非托管句柄泄漏、无寄生 Harmony Prefix）；仅在锁竞争或异常载荷时回退标准流
    public static byte[] Compress(byte[] data) {
        if (CompressPool.TryCompressGzip(data, out byte[] pooled)) {
            return pooled;
        }
        using var outputstream = new MemoryStream();
        using (var gzipstream = new GZipStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            gzipstream.Write(data, 0, data.Length);
        }
        return outputstream.ToArray();
    }

    public static byte[] Decompress(byte[] compressed_data) {
        if (CompressPool.TryDecompressGzip(compressed_data, out byte[] pooled)) {
            return pooled;
        }
        using var inputstream = new MemoryStream(compressed_data);
        using var gzipstream = new GZipStream(inputstream, CompressionMode.Decompress);
        using var outputstream = new MemoryStream();
        gzipstream.CopyTo(outputstream);
        return outputstream.ToArray();
    }


    public static byte[] CompressDeflate(byte[] data) {
        if (CompressPool.TryCompressDeflate(data, out byte[] pooled)) {
            return pooled;
        }
        using var outputstream = new MemoryStream();
        using (var gzipstream = new DeflateStream(outputstream, System.IO.Compression.CompressionLevel.Optimal)) {
            gzipstream.Write(data, 0, data.Length);
        }
        return outputstream.ToArray();
    }

    public static byte[] DecompressDeflate(byte[] compressed_data) {
        if (CompressPool.TryDecompressDeflate(compressed_data, out byte[] pooled)) {
            return pooled;
        }
        using var inputstream = new MemoryStream(compressed_data);
        using var gzipstream = new DeflateStream(inputstream, CompressionMode.Decompress);
        using var outputstream = new MemoryStream();
        gzipstream.CopyTo(outputstream);
        return outputstream.ToArray();
    }

    #endregion

}
