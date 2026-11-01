using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// What a bullet is allowed to remove. Everything is a whitelist:
///  - tiles on tilemaps that carry no scripts and sit under no progression object (and are not the
///    invisible world boundary), plus the same world cell on every other such tilemap (the art that
///    draws the block);
///  - spikes (spikeScript objects are switched off);
///  - plain solid sprites: a non-trigger collider whose object has a SpriteRenderer and no scripts at all.
/// Upgrade boxes, checkpoints, courses and clones, doors and gates, teleporters, platforms, triggers
/// and the player are never touched. Removals are runtime-only: nothing is saved, a scene reload or a
/// Navigator rebuild brings everything back.
/// </summary>
internal static class TileBreaker
{
    private static readonly Dictionary<Tilemap, bool> Allowed = new Dictionary<Tilemap, bool>();
    private static Tilemap[] _maps = new Tilemap[0];
    private static float _mapsAt = -100f;
    private static readonly StringBuilder Sb = new StringBuilder();

    /// <summary>Self-test aid (RECHARGE_GUN_LEGACY=1): the behaviour before the break-everything change, to show why things survived.</summary>
    internal static readonly bool Legacy = Environment.GetEnvironmentVariable("RECHARGE_GUN_LEGACY") == "1";

    internal static string LastInfo = "";
    internal static int LastCells;
    internal static readonly List<string> LastCleared = new List<string>();
    internal static Vector3Int LastCell;
    internal static Vector2 LastCellCenter;

    private static readonly string[] BlockedNameParts = { "invisible", "boundary", "killzone", "deathzone", "border" };

    private static bool IsProgression(MonoBehaviour mb)
    {
        return mb is PlatformMover || mb is courseScript || mb is localUpgrades || mb is startGate || mb is endGate
            || mb is checkpointScript || mb is TeleporterScript || mb is upgradeBox || mb is zoneChanger
            || mb is Movement || mb is SpringScript || mb is JiggleDropScript;
    }

    private static readonly string[] ProtectedTypeParts = { "clone", "player", "gate", "checkpoint", "door", "teleport", "spring", "platform" };

    /// <summary>
    /// Scripts whose object (and its art) a decoration must not sit under: things the player interacts with or that
    /// carry saving / movement. Containers such as a course or zone folder do not count: their decoration is art.
    /// </summary>
    internal static bool IsProtectedScript(MonoBehaviour mb)
    {
        if (mb is PlatformMover || mb is startGate || mb is endGate || mb is checkpointScript || mb is TeleporterScript
            || mb is upgradeBox || mb is Movement || mb is SpringScript || mb is JiggleDropScript || mb is spikeScript) return true;
        var n = mb.GetType().Name.ToLowerInvariant();
        foreach (var part in ProtectedTypeParts) if (n.Contains(part)) return true;
        return false;
    }

    internal static bool TilemapOk(Tilemap tm) => TilemapAllowed(tm);

    private static readonly Dictionary<Tilemap, string> Why = new Dictionary<Tilemap, string>();

    private static bool AncestorsOk(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
            foreach (var mb in p.GetComponents<MonoBehaviour>())
                if (mb != null && !(mb is spikeScript) && (Legacy ? IsProgression(mb) : IsProtectedScript(mb))) { WhyNot = "script " + mb.GetType().Name + " on '" + p.name + "'"; return false; }
        return true;
    }

    private static string WhyNot = "";

    private static bool TilemapAllowed(Tilemap tm)
    {
        if (tm == null) return false;
        if (Allowed.TryGetValue(tm, out var ok)) return ok;
        ok = false; WhyNot = "";
        try
        {
            ok = tm.gameObject.scene.IsValid();
            if (!ok) WhyNot = "not in a scene";
            var name = tm.name.ToLowerInvariant();
            foreach (var part in BlockedNameParts) if (name.Contains(part)) { ok = false; WhyNot = "name has '" + part + "'"; }
            // spike tilemaps are trigger-only by design and are exactly what may go; any other tilemap whose
            // effective collider is a trigger (a composite decides when there is one) is a zone, not a block
            bool isSpike = tm.GetComponent<spikeScript>() != null;
            if (Legacy) { var lc = tm.GetComponent<Collider2D>(); if (lc != null && lc.isTrigger) ok = false; }
            else if (!isSpike && EffectiveTrigger(tm)) { ok = false; WhyNot = "trigger zone"; }
            if (ok)
                foreach (var mb in tm.GetComponents<MonoBehaviour>())
                    if (mb != null && !(mb is spikeScript)) { ok = false; WhyNot = "own script " + mb.GetType().Name; break; }
            if (ok) ok = AncestorsOk(tm.transform);
        }
        catch (Exception e) { ok = false; WhyNot = "error " + e.Message; }
        Allowed[tm] = ok;
        if (!ok) Why[tm] = WhyNot;
        return ok;
    }

    private static bool EffectiveTrigger(Tilemap tm)
    {
        var comp = tm.GetComponent<CompositeCollider2D>();
        if (comp != null) return comp.isTrigger;
        var tc = tm.GetComponent<Collider2D>();
        return tc != null && tc.isTrigger;
    }

    private static void RefreshMaps()
    {
        if (Time.unscaledTime - _mapsAt < 3f) return;
        _mapsAt = Time.unscaledTime;
        _maps = UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None);
        // forget verdicts for destroyed tilemaps
        List<Tilemap> dead = null;
        foreach (var kv in Allowed) if (kv.Key == null) (dead ?? (dead = new List<Tilemap>())).Add(kv.Key);
        if (dead != null) foreach (var d in dead) Allowed.Remove(d);
    }

    internal static void Invalidate() { _mapsAt = -100f; Allowed.Clear(); }

    /// <summary>A bullet hit <paramref name="col"/> at <paramref name="point"/>. Returns true if something was removed.</summary>
    internal static bool Hit(Collider2D col, Vector2 point, Vector2 normal, GunConfig cfg, int extraRadius, GunFx fx, Action<string> log, Vector2 dir = default)
    {
        LastInfo = ""; LastCells = 0; LastCleared.Clear();
        if (!cfg.BreakTiles || col == null) return false;
        try
        {
            var spike = col.GetComponent<spikeScript>();
            if (spike == null && col.isTrigger) spike = col.GetComponentInParent<spikeScript>();
            if (spike != null && spike.GetComponent<Tilemap>() != null)
            {
                // spikes are painted as tiles (a tilemap carries the spikeScript): remove that cell and its art
                var stm = spike.GetComponent<Tilemap>();
                ClearedHere.Clear();
                bool shape = !Legacy && DisableShapes(col, stm, point);
                bool cells = BreakTiles(stm, point, normal, cfg, extraRadius, fx, log, dir, true);
                // the per-spike polygons of the cleared cells go with them, whichever collider the bullet struck
                if (cells && !Legacy && DisableShapesAt(stm)) shape = true;
                if (shape && !cells)
                {
                    fx.Burst(point, 8, new Color(1f, 0.4f, 0.3f), 400f, 0.4f, normal);
                    LastInfo = "spike shape on '" + stm.name + "' removed";
                    log?.Invoke(LastInfo);
                }
                return shape || cells;
            }
            if (spike != null)
            {
                var go = spike.gameObject;
                go.SetActive(false);
                fx.Burst(go.transform.position, 8, new Color(1f, 0.4f, 0.3f), 400f, 0.4f, Vector2.zero);
                LastInfo = "spike '" + go.name + "' removed";
                log?.Invoke(LastInfo);
                return true;
            }
            if (col.isTrigger) return false;

            var tm = col.GetComponent<Tilemap>();
            if (tm != null) return BreakTiles(tm, point, normal, cfg, extraRadius, fx, log);

            if (Legacy) return false;
            // anything else with something visible on it: the whole object it belongs to goes (doors, springs, movers, boxes, plain solids ...)
            var start = Groups.VisibleStart(col);
            if (start == null) { LastInfo = "'" + col.name + "' has nothing visible: left alone"; return false; }
            var root = Groups.Resolve(start);
            if (root == null) { LastInfo = "'" + col.name + "' is protected (" + Groups.LastWhy + ")"; return false; }
            Deco.BreakRoot(root, GunMod.Instance != null && GunMod.Instance.Gun != null ? GunMod.Instance.Gun.Unit : 60f, fx);
            LastInfo = "object '" + Survey.Path(root) + "' removed";
            log?.Invoke(LastInfo);
            return true;
        }
        catch (Exception e) { log?.Invoke("break failed: " + e.Message); return false; }
    }

    /// <summary>
    /// Some spike tilemaps (the map editor's) carry one PolygonCollider2D per spike next to the tile collider;
    /// those do not follow SetTile, so the ones under the hit are switched off by hand.
    /// </summary>
    private static bool DisableShapes(Collider2D hit, Tilemap tm, Vector2 point)
    {
        bool any = false;
        if (hit is TilemapCollider2D || hit is CompositeCollider2D) return false;
        foreach (var c in ShapesOf(tm))
        {
            if (c == null || !c.enabled) continue;
            if (c == hit || c.bounds.Contains(new Vector3(point.x, point.y, c.bounds.center.z)))
            {
                c.enabled = false; any = true;
            }
        }
        return any;
    }

    // Vines (BigThornLoop, smallTangle ...) are one tile whose picture spans up to 10x10 cells (320px), anchored at one cell:
    // a hit on the far end of a vine is nowhere near its anchor. So the cells searched reach that far.
    private const int ShapeSearch = 6;

    /// <summary>Of the cells around the hit (far enough to reach a vine's anchor), the tile whose (transformed) physics shape lies nearest to the point.</summary>
    private static bool NearestShapeCell(Tilemap tm, Vector2 point, float cell, out Vector3Int best, out float bestDist)
    {
        best = default; bestDist = float.MaxValue;
        float bestCentre = float.MaxValue;
        try
        {
            var c0 = tm.WorldToCell(point);
            var pts = new List<Vector2>();
            var tr = tm.transform;
            var pl = (Vector2)tr.InverseTransformPoint(point);
            float scale = Mathf.Max(0.0001f, Mathf.Abs(tr.lossyScale.x));
            for (int dx = -ShapeSearch; dx <= ShapeSearch; dx++)
                for (int dy = -ShapeSearch; dy <= ShapeSearch; dy++)
                {
                    var c = new Vector3Int(c0.x + dx, c0.y + dy, c0.z);
                    if (!tm.HasTile(c)) continue;
                    var sp = tm.GetSprite(c);
                    float d;
                    if (sp == null || sp.GetPhysicsShapeCount() == 0)
                    {
                        // no shape to test: only a tile right by the hit can be the one struck
                        if (Mathf.Abs(dx) > 1 || Mathf.Abs(dy) > 1) continue;
                        d = ((Vector2)tm.GetCellCenterWorld(c) - point).magnitude;
                    }
                    else
                    {
                        var m = tm.GetTransformMatrix(c);
                        var centre = (Vector2)tm.GetCellCenterLocal(c);
                        d = float.MaxValue;
                        for (int i = 0; i < sp.GetPhysicsShapeCount(); i++)
                        {
                            pts.Clear(); sp.GetPhysicsShape(i, pts);
                            for (int k = 0; k < pts.Count; k++)
                                pts[k] = centre + (Vector2)m.MultiplyPoint3x4(pts[k]);
                            d = Mathf.Min(d, PolyDist(pts, pl));
                        }
                        d *= scale;
                    }
                    // The shape nearest the point wins (a shape the point is inside is at 0, so a vine anchored far away still wins
                    // over a spike merely nearby); between near-equal shapes, the cell whose centre is nearest.
                    float centreDist = ((Vector2)tm.GetCellCenterWorld(c) - point).magnitude;
                    if (d < bestDist - 0.5f || (Mathf.Abs(d - bestDist) <= 0.5f && centreDist < bestCentre))
                    { bestDist = d; bestCentre = centreDist; best = c; }
                }
        }
        catch { return false; }
        return bestDist < cell * 0.5f;
    }

    private static float PolyDist(List<Vector2> poly, Vector2 q)
    {
        bool inside = false; float min = float.MaxValue;
        for (int i = 0, j = poly.Count - 1; i < poly.Count; j = i++)
        {
            var a = poly[i]; var b = poly[j];
            if ((a.y > q.y) != (b.y > q.y) && q.x < (b.x - a.x) * (q.y - a.y) / (b.y - a.y) + a.x) inside = !inside;
            var ab = b - a; float t = ab.sqrMagnitude < 1e-9f ? 0f : Mathf.Clamp01(Vector2.Dot(q - a, ab) / ab.sqrMagnitude);
            min = Mathf.Min(min, (q - (a + ab * t)).magnitude);
        }
        return inside ? 0f : min;
    }

    private static readonly List<Vector3> ClearedHere = new List<Vector3>();

    // Spike tilemaps hand their tile shapes to one CompositeCollider2D. Every SetTile then rebuilds that whole composite from
    // every spike's many-pointed physics shape (6000+ tiles in the base level): a big hitch per broken spike. So the first time
    // a spike tilemap is broken, its tiles collide on their own instead (one rebuild), and later removals update just that tile.
    // Spikes still kill the same way: spikeScript reacts to any trigger on the object, and the colour swapper toggles the
    // TilemapCollider2D itself, which keeps working without the composite.
    private static readonly HashSet<int> PerTile = new HashSet<int>();
    private static void UsePerTileCollision(Tilemap tm)
    {
        if (tm == null || !PerTile.Add(tm.GetInstanceID())) return;
        var tc = tm.GetComponent<TilemapCollider2D>();
        if (tc == null || tc.compositeOperation == Collider2D.CompositeOperation.None) return;
        var comp = tm.GetComponent<CompositeCollider2D>();
        tc.isTrigger = comp != null ? comp.isTrigger : tc.isTrigger;
        tc.compositeOperation = Collider2D.CompositeOperation.None;
    }

    // The extra per-spike colliders of a tilemap, read once instead of on every hit (GetComponents allocates and some maps carry dozens).
    private static readonly Dictionary<int, Collider2D[]> ShapeColliders = new Dictionary<int, Collider2D[]>();
    private static Collider2D[] ShapesOf(Tilemap tm)
    {
        if (ShapeColliders.TryGetValue(tm.GetInstanceID(), out var arr)) return arr;
        var list = new List<Collider2D>();
        foreach (var c in tm.GetComponents<Collider2D>())
            if (c != null && !(c is TilemapCollider2D) && !(c is CompositeCollider2D)) list.Add(c);
        arr = list.ToArray();
        ShapeColliders[tm.GetInstanceID()] = arr;
        return arr;
    }

    /// <summary>Switches off the extra per-spike colliders that sit in a cell just cleared on <paramref name="tm"/>.</summary>
    private static bool DisableShapesAt(Tilemap tm)
    {
        bool any = false;
        var grid = tm.layoutGrid;
        var cs = grid != null ? Vector3.Scale(grid.cellSize, grid.transform.lossyScale) : Vector3.one * 16f;
        float half = Mathf.Max(Mathf.Abs(cs.x), Mathf.Abs(cs.y)) * 0.6f;
        foreach (var c in ShapesOf(tm))
        {
            if (c == null || !c.enabled) continue;
            var b = c.bounds.center;
            foreach (var cc in ClearedHere)
                if (Mathf.Abs(b.x - cc.x) <= half && Mathf.Abs(b.y - cc.y) <= half) { c.enabled = false; any = true; break; }
        }
        return any;
    }

    private static readonly float[] Depths = { 0.25f, 0.6f, 1.0f };

    private static bool BreakTiles(Tilemap hitMap, Vector2 point, Vector2 normal, GunConfig cfg, int extraRadius, GunFx fx, Action<string> log, Vector2 dir = default, bool bySpikeShape = false)
    {
        if (!TilemapAllowed(hitMap)) { LastInfo = "tilemap '" + hitMap.name + "' is protected (" + (Why.TryGetValue(hitMap, out var w) ? w : "?") + ")"; return false; }
        RefreshMaps();

        var grid = hitMap.layoutGrid;
        var cs = grid != null ? Vector3.Scale(grid.cellSize, grid.transform.lossyScale) : Vector3.one * 16f;
        float cell = Mathf.Max(1f, Mathf.Min(Mathf.Abs(cs.x), Mathf.Abs(cs.y)));

        // nudge inside the surface until the struck tilemap really has a tile there
        Vector2 p = point;
        bool found = false;
        if (bySpikeShape && NearestShapeCell(hitMap, point, cell, out var shapeCell, out var shapeDist))
        {
            // a spike's collider is its sprite's physics shape (rotated / flipped by the tile): pick the cell whose shape
            // the bullet really struck, not the neighbour the surface normal happens to point into
            p = hitMap.GetCellCenterWorld(shapeCell);
            found = true;
        }
        if (!found)
        foreach (var depth in Depths)
        {
            p = point - normal * (cell * depth);
            if (hitMap.HasTile(hitMap.WorldToCell(p))) { found = true; break; }
        }
        if (!found && dir.sqrMagnitude > 0.01f)
            foreach (var depth in Depths)
            {
                p = point + dir.normalized * (cell * depth * 0.5f);
                if (hitMap.HasTile(hitMap.WorldToCell(p))) { found = true; break; }
            }
        if (!found) { LastInfo = "no tile found under hit on '" + hitMap.name + "'"; return false; }

        int radius = Mathf.Clamp(cfg.BreakRadius + extraRadius, 0, 3);
        int count = 0;
        Sb.Length = 0;
        Vector3 center = p;
        foreach (var m in _maps)
        {
            if (m == null || !m.gameObject.activeInHierarchy || !TilemapAllowed(m)) continue;
            var c0 = m.WorldToCell(p);
            for (int dx = -radius; dx <= radius; dx++)
                for (int dy = -radius; dy <= radius; dy++)
                {
                    var c = new Vector3Int(c0.x + dx, c0.y + dy, c0.z);
                    if (!m.HasTile(c)) continue;
                    if (m.GetComponent<spikeScript>() != null) UsePerTileCollision(m);
                    m.SetTile(c, null);
                    count++;
                    if (m == hitMap) ClearedHere.Add(m.GetCellCenterWorld(c));
                    if (m == hitMap && dx == 0 && dy == 0) { LastCell = c; center = m.GetCellCenterWorld(c); }
                    if (LastCleared.Count < 16) LastCleared.Add(m.name + "@" + c.x + "," + c.y);
                }
        }
        if (count == 0) return false;
        LastCells = count; LastCellCenter = center;
        fx.Burst(center, 10, new Color(0.75f, 0.7f, 0.6f), cell * 14f, 0.45f, normal);
        fx.PlayHit();
        LastInfo = "cleared " + count + " tile(s) at world " + (Vector2)center + " on [" + string.Join(", ", LastCleared) + "] (struck '" + hitMap.name + "')";
        log?.Invoke(LastInfo);
        return true;
    }
}
