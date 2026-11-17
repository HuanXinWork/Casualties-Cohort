using System.Collections.Generic;
using Together;
using Together.MpConstants;
using UnityEngine;

namespace KrokV5Optimization;

/// <summary>
/// 服务端近场网络实体分帧注册安全网：每帧按存活玩家轮转扫描一个象限，近身范围内立即注册，远端共享固定配额。
/// </summary>
internal static class SafetyNet {
    private const float Radius = 80f;
    private const float Close = GameplayRanges.CraftGroundScanRadius;
    private const int FarBudget = 16;
    private const float WaitSeconds = 1.9f;

    private static readonly Vector2[] Quadrant = {
        new Vector2(1f, 1f),
        new Vector2(-1f, 1f),
        new Vector2(-1f, -1f),
        new Vector2(1f, -1f),
    };

    private static readonly Collider2D[] Hits = new Collider2D[256];
    private static readonly List<NetBody> Bodies = new List<NetBody>(16);

    private static bool _waiting;
    private static bool _logged;
    private static bool _bufferFullLogged;
    private static float _wait;
    private static int _player;
    private static int _quadrant;
    private static int _farLeft = FarBudget;

    internal static void Tick() {
        if (!Net.IsServer)
            return;

        if (_waiting) {
            _wait -= Time.deltaTime;
            if (_wait > 0f)
                return;
            _waiting = false;
            _player = 0;
            _quadrant = 0;
            _farLeft = FarBudget;
        }

        IList<ScavPlayer> living;
        try {
            living = ScavPlayer.AllLivingPlayers;
        } catch (System.Exception ex) {
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] living players: " + ex.Message);
            BeginWait();
            return;
        }

        if (living == null || living.Count == 0) {
            BeginWait();
            return;
        }

        if (_player >= living.Count)
            _player = 0;

        ScavPlayer plr = living[_player];
        if (plr != null)
            StampQuadrant(plr, _quadrant);

        if (!_logged) {
            _logged = true;
            OptimizerMain.Log.LogDebug("[KrokV5Optimization] safety net is one quadrant per frame, then wait " + WaitSeconds.ToString("0.0") + "s");
        }

        _quadrant++;
        if (_quadrant < Quadrant.Length)
            return;

        _quadrant = 0;
        _farLeft = FarBudget;
        _player++;
        if (_player >= living.Count)
            BeginWait();
    }

    private static void BeginWait() {
        _waiting = true;
        _wait = WaitSeconds;
        _player = 0;
        _quadrant = 0;
        _farLeft = FarBudget;
    }

    private static void StampQuadrant(ScavPlayer plr, int quadrant) {
        Vector2 pos = plr.pos;
        Vector2 center = pos + Quadrant[quadrant] * (Radius * 0.5f);
        int count;
        try {
            count = Physics2D.OverlapBoxNonAlloc(center, new Vector2(Radius, Radius), 0f, Hits);
        } catch (System.Exception ex) {
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant: " + ex.Message);
            return;
        }

        if (count >= Hits.Length && !_bufferFullLogged) {
            _bufferFullLogged = true;
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant hit buffer filled at " + Hits.Length);
        }

        float radiusSqr = Radius * Radius;
        for (int i = 0; i < count; i++) {
            Collider2D hit = Hits[i];
            Hits[i] = null;
            if (hit == null)
                continue;
            if (hit.GetComponent<Item>() == null && hit.GetComponent<BuildingEntity>() == null)
                continue;
            GameObject go = hit.gameObject;
            if (go == null)
                continue;
            if (((Vector2)go.transform.position - pos).sqrMagnitude > radiusSqr)
                continue;

            Note(go, pos);
            if (go.TryGetComponent<Container>(out Container container)) {
                foreach (Item item in container.GetAllItems()) {
                    if (item != null)
                        Note(item.gameObject, pos);
                }
            }
        }

        if (quadrant != 0)
            return;

        try {
            foreach (GameObject go in NetObjectRegistry.GetIdentifiedBackgroundsInRadius(pos, Radius))
                Note(go, pos);

            if (plr.body != null) {
                foreach (Item item in plr.body.GetAllItemsThorough()) {
                    if (item != null)
                        Note(item.gameObject, pos);
                }
            }

            Bodies.Clear();
            NetBody.GetBodiesInRadius(pos, Radius, Bodies);
            if (plr.body != null && plr.body.TryGetNetBody(out NetBody self))
                Bodies.Remove(self);
            for (int i = 0; i < Bodies.Count; i++) {
                NetBody other = Bodies[i];
                if (other == null || other.body == null)
                    continue;
                foreach (Item item in other.body.GetAllItemsThorough()) {
                    if (item != null)
                        Note(item.gameObject, pos);
                }
            }
        } catch (System.Exception ex) {
            OptimizerMain.Log.LogWarning("[KrokV5Optimization] quadrant extras: " + ex.Message);
        }
    }

    private static void Note(GameObject go, Vector2 pos) {
        if (go == null || NetObjectRegistry.IsRegistered(go) || !NetObjectRegistry.ObjectShouldBeSynced(go))
            return;
        if (!NetObjectRegistry.ShouldKeepRegistered(go))
            return;

#if FULLGAME
        bool crystal = CrystalAuthority.LooksLikeCrystal(go);
#else
        bool crystal = false;
#endif
        bool close = !crystal && ((Vector2)go.transform.position - pos).sqrMagnitude <= Close * Close;
        if (!close) {
            int cost = crystal ? 2 : 1;
            if (_farLeft < cost)
                return;
            _farLeft -= cost;
        }

        NetObjectRegistry.NewGO(go);
    }
}
