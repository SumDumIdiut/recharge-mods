using System;
using System.Collections.Generic;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Things a bullet's raycast never sees (no solid collider: decorations, trigger-only objects such as checkpoints,
/// teleporters and gates, animated parts) are found by sweeping each bullet step against two things that are
/// collected once per level (scene or Navigator map change), never per frame:
///  - tilemaps without a collider (decoration art): the cells along the segment are cleared;
///  - every visible renderer (sprites, meshes, particle systems, lines): kept in a spatial grid of their bounds (in
///    level space, so the game's floating-origin shifts do not stale it). A renderer the segment crosses is resolved
///    to the object it belongs to (<see cref="Groups"/>) and that whole object is switched off.
/// Renderers that move on their own (movers, zip movers) are re-measured live on each sweep.
/// Everything is runtime-only: a reload brings it all back.
/// </summary>
internal static class Deco
{
    private const float GridCell = 400f;
    private static readonly Dictionary<long, List<Entry>> Grid = new Dictionary<long, List<Entry>>();
    private static readonly List<Tilemap> Maps = new List<Tilemap>();
    private static readonly List<Entry> Hits = new List<Entry>();
    private static readonly List<Entry> Dyn = new List<Entry>();
    private static readonly List<Renderer> Pending = new List<Renderer>();     // sprite renderers whose sprite is not assigned yet (an animator sets it later)
    private static float _pendingAt;
    private static bool _built;
    private static string _key = "";
    private static float _keyAt;

    internal static int SpriteCount, MapCount, CellCount, DynCount;
    internal static Action<string> Log;
    internal static int Builds;
    internal static double LastBuildMs;
    internal static string LastInfo = "";
    internal static readonly List<string> LastRemoved = new List<string>();
    internal static readonly List<string> LastGroups = new List<string>();
    internal static int GroupsBroken;
    /// <summary>Called with the root that was just switched off (self-test and error watching).</summary>
    internal static Action<Transform> OnBroke;

    private class Entry { public Renderer R; public Bounds B; public bool Gone, Prot, Moving; }

    internal static void Invalidate() { _built = false; Grid.Clear(); Dyn.Clear(); Maps.Clear(); Pending.Clear(); Groups.Reset(); }

    private static Vector2 Origin()
    {
        try { var fo = Singleton<FloatingOrigin>.Instance; return fo != null ? (Vector2)fo.currentOrigin : Vector2.zero; } catch { return Vector2.zero; }
    }

    /// <summary>Could this renderer ever show something? (build time: sprite present, enabled or not)</summary>
    private static bool Candidate(Renderer r)
    {
        if (r == null || r is TilemapRenderer || r is TrailRenderer) return false;
        return r is SpriteRenderer || r is MeshRenderer || r is ParticleSystemRenderer || r is LineRenderer;
    }

    /// <summary>Is it showing something right now? (sweep time)</summary>
    internal static bool Showing(Renderer r)
    {
        if (r == null || !r.enabled || !r.gameObject.activeInHierarchy) return false;
        var sr = r as SpriteRenderer;
        if (sr != null) return sr.sprite != null && sr.color.a >= 0.05f;
        return true;
    }

    private static bool Moves(Transform t)
    {
        for (var p = t; p != null; p = p.parent)
        {
            if (p.GetComponent<PlatformMover>() != null) return true;
            var rb = p.GetComponent<Rigidbody2D>();
            if (rb != null && rb.bodyType != RigidbodyType2D.Static && p.GetComponent<Tilemap>() == null) return true;
        }
        return false;
    }

    /// <summary>HUD / menu graphics (under a screen-space canvas) are never decoration; sprites on a world-space canvas (upgrade box art) are.</summary>
    private static bool IsScreenUi(GameObject go)
    {
        if (go.GetComponent<RectTransform>() == null) return false;
        var cv = go.GetComponentInParent<Canvas>(true);
        return cv != null && cv.rootCanvas.renderMode != RenderMode.WorldSpace;
    }

    private static bool AddRenderer(Renderer r, float unit, float cap, Vector2 org)
    {
        var go = r.gameObject;
        var sr = r as SpriteRenderer;
        if (sr != null && sr.sprite == null) return false;
        Bounds b;
        if (go.activeInHierarchy && r.enabled) b = r.bounds; else if (sr != null) b = SpriteBounds(sr); else return false;
        if (!(r is SpriteRenderer) && (b.size.x < 1f || b.size.y < 1f)) b.Expand(new Vector3(Mathf.Max(0f, unit * 0.8f - b.size.x), Mathf.Max(0f, unit * 0.8f - b.size.y), 0f));
        if (b.size.x > cap || b.size.y > cap || b.size.x < 1f && b.size.y < 1f) return true;     // huge backdrops / specks are not "things"
        bool moving = r is SpriteRenderer && Moves(go.transform);
        var e = new Entry { R = r, Moving = moving };
        if (moving) { Dyn.Add(e); return true; }
        b.center = new Vector3(b.center.x - org.x, b.center.y - org.y, 0f); b.size = new Vector3(b.size.x, b.size.y, 1f);
        e.B = b;
        int x0 = Mathf.FloorToInt(b.min.x / GridCell), x1 = Mathf.FloorToInt(b.max.x / GridCell);
        int y0 = Mathf.FloorToInt(b.min.y / GridCell), y1 = Mathf.FloorToInt(b.max.y / GridCell);
        for (int x = x0; x <= x1; x++)
            for (int y = y0; y <= y1; y++)
            {
                var k = ((long)x << 32) ^ (uint)y;
                if (!Grid.TryGetValue(k, out var list)) Grid[k] = list = new List<Entry>();
                list.Add(e);
            }
        return true;
    }

    /// <summary>Twice a second: renderers that had no sprite when the grid was built and have one now join the grid.</summary>
    private static void ServicePending(float unit)
    {
        if (Pending.Count == 0 || Time.unscaledTime - _pendingAt < 0.5f) return;
        _pendingAt = Time.unscaledTime;
        var org = Origin();
        float cap = Mathf.Max(unit * 25f, 1500f);
        for (int i = Pending.Count - 1; i >= 0; i--)
        {
            var r = Pending[i];
            if (r == null) { Pending.RemoveAt(i); continue; }
            var sr = r as SpriteRenderer;
            if (sr == null || sr.sprite == null) continue;
            Pending.RemoveAt(i);
            AddRenderer(r, unit, cap, org);
        }
    }

    /// <summary>Collect this level's decoration tilemaps and sprites. Called lazily and after every scene / map change.</summary>
    internal static void Build(float unit)
    {
        var sw = System.Diagnostics.Stopwatch.StartNew();
        Invalidate();
        _built = true;
        _key = Key();
        var org = Origin();
        float cap = Mathf.Max(unit * 25f, 1500f);
        int total = 0;
        foreach (var tm in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (tm == null || tm.GetComponent<Collider2D>() != null) continue;
            var ln = tm.name.ToLowerInvariant();
            if (ln.Contains("oob") || ln.Contains("light")) continue;      // out-of-bounds shading and light blockers are not decoration
            if (!TileBreaker.TilemapOk(tm)) continue;
            Maps.Add(tm);
        }
        foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (!Candidate(r)) continue;
            var go = r.gameObject;
            if (!go.scene.IsValid() || IsScreenUi(go)) continue;
            if (!AddRenderer(r, unit, cap, org)) { var sp = r as SpriteRenderer; if (sp != null && sp.sprite == null) Pending.Add(r); }
            else total++;
        }
        DynCount = Dyn.Count;
        SpriteCount = total; MapCount = Maps.Count;
        Builds++; LastBuildMs = sw.Elapsed.TotalMilliseconds;
        Log?.Invoke("deco grid built (#" + Builds + "): " + total + " renderers (" + Dyn.Count + " moving) in " + Grid.Count + " grid cells, " + Maps.Count + " decoration tilemaps, " + LastBuildMs.ToString("0.0") + " ms, key " + _key);
    }

    /// <summary>World-space box of the sprite, computed from the transform so it is right even while the object is inactive.</summary>
    private static Bounds SpriteBounds(SpriteRenderer r)
    {
        var lb = r.sprite.bounds;
        var m = r.transform.localToWorldMatrix;
        var min = new Vector2(float.MaxValue, float.MaxValue); var max = new Vector2(float.MinValue, float.MinValue);
        for (int i = 0; i < 4; i++)
        {
            var c = (Vector2)m.MultiplyPoint3x4(new Vector3((i & 1) == 0 ? lb.min.x : lb.max.x, (i & 2) == 0 ? lb.min.y : lb.max.y, 0f));
            min = Vector2.Min(min, c); max = Vector2.Max(max, c);
        }
        var b = new Bounds(); b.SetMinMax(new Vector3(min.x, min.y, 0f), new Vector3(max.x, max.y, 0f));
        return b;
    }

    internal static string Key()
    {
        string map = "";
        try { map = GunMod.Instance != null && GunMod.Instance.NavigatorMapActive ? GunMod.Instance.SlotKey() : ""; } catch { }
        bool built = false;
        try { built = GunMod.Instance != null && GunMod.Instance.NavigatorState().Built; } catch { }
        return UnityEngine.SceneManagement.SceneManager.GetActiveScene().handle + "|" + map + "|" + built;
    }

    /// <summary>Cheap check (twice a second): did the scene or Navigator map change since the grid was built?</summary>
    internal static void Watch(float unit)
    {
        if (Time.unscaledTime - _keyAt < 0.5f) return;
        _keyAt = Time.unscaledTime;
        if (_built && Key() != _key) _built = false;
    }

    internal static void Ensure(float unit) { if (!_built) Build(unit); }

    /// <summary>Remove decoration along the segment a -> b (world space). Returns how many things were removed.</summary>
    internal static int Sweep(Vector2 a, Vector2 b, float unit, GunFx fx)
    {
        if (TileBreaker.Legacy) return 0;
        Ensure(unit);
        ServicePending(unit);
        LastInfo = ""; LastRemoved.Clear();
        int removed = 0;
        Vector2 firstAt = default; bool any = false;

        // tilemap cells
        foreach (var tm in Maps)
        {
            if (tm == null || !tm.gameObject.activeInHierarchy) continue;
            var cs = tm.layoutGrid != null ? Vector3.Scale(tm.layoutGrid.cellSize, tm.layoutGrid.transform.lossyScale) : Vector3.one * 16f;
            float cell = Mathf.Max(1f, Mathf.Min(Mathf.Abs(cs.x), Mathf.Abs(cs.y)));
            float len = (b - a).magnitude;
            int steps = Mathf.Max(1, Mathf.CeilToInt(len / (cell * 0.5f)));
            Vector3Int last = new Vector3Int(int.MinValue, 0, 0);
            for (int i = 0; i <= steps; i++)
            {
                var p = Vector2.Lerp(a, b, (float)i / steps);
                var c = tm.WorldToCell(p);
                if (c == last) continue;
                last = c;
                if (!tm.HasTile(c)) continue;
                tm.SetTile(c, null);
                removed++; CellCount++;
                if (LastRemoved.Count < 8) LastRemoved.Add(tm.name + "@" + c.x + "," + c.y);
                if (!any) { any = true; firstAt = tm.GetCellCenterWorld(c); }
            }
        }

        // renderers in the cells the segment crosses
        var org = Origin();
        var la = a - org; var lb = b - org;
        float slen = (lb - la).magnitude;
        int ss = Mathf.Max(1, Mathf.CeilToInt(slen / (GridCell * 0.5f)));
        long lastKey = long.MinValue;
        Hits.Clear();
        for (int i = 0; i <= ss; i++)
        {
            var p = Vector2.Lerp(la, lb, (float)i / ss);
            int gx = Mathf.FloorToInt(p.x / GridCell), gy = Mathf.FloorToInt(p.y / GridCell);
            var k = ((long)gx << 32) ^ (uint)gy;
            if (k == lastKey) continue;
            lastKey = k;
            if (!Grid.TryGetValue(k, out var list)) continue;
            foreach (var e in list) if (!e.Gone && !e.Prot && !Hits.Contains(e) && Crosses(e.B, la, lb)) Hits.Add(e);
        }
        foreach (var e in Dyn)
        {
            if (e.Gone || e.Prot || !Showing(e.R)) continue;
            var wb = e.R.bounds;
            var bb = new Bounds(new Vector3(wb.center.x, wb.center.y, 0f), new Vector3(wb.size.x, wb.size.y, 1f));
            if (Crosses(bb, a, b)) Hits.Add(e);
        }
        LastGroups.Clear();
        foreach (var e in Hits)
        {
            if (!Showing(e.R)) continue;   // not showing right now: nothing to remove yet
            var root = Groups.Resolve(e.R.transform);
            if (root == null) { e.Prot = true; continue; }
            if (!root.gameObject.activeSelf) continue;
            var at = BreakRoot(root, unit, fx);
            removed++;
            if (LastRemoved.Count < 8) LastRemoved.Add("object:" + Survey.Path(root));
            if (!any) { any = true; firstAt = at; }
        }
        if (removed > 0)
        {
            if (!(Hits.Count > 0)) fx.Burst(firstAt, 4, new Color(0.7f, 0.8f, 0.65f), unit * 4f, 0.35f, Vector2.zero, 1f);
            LastInfo = "deco removed " + removed + " (" + string.Join(", ", LastRemoved) + ")";
        }
        return removed;
    }

    /// <summary>Switch off a whole object with a debris burst sized to it. Returns its centre.</summary>
    internal static Vector2 BreakRoot(Transform root, float unit, GunFx fx)
    {
        Bounds all = default; bool have = false; Color tint = new Color(0.7f, 0.8f, 0.65f);
        foreach (var r in root.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || r is TilemapRenderer) continue;
            var wb = r.bounds;
            if (!have) { all = wb; have = true; var sr = r as SpriteRenderer; if (sr != null && sr.color.r + sr.color.g + sr.color.b < 2.9f) tint = Color.Lerp(sr.color, new Color(0.8f, 0.8f, 0.85f), 0.4f); }
            else all.Encapsulate(wb);
        }
        var at = have ? (Vector2)all.center : (Vector2)root.position;
        float size = have ? Mathf.Max(all.size.x, all.size.y) / Mathf.Max(1f, unit) : 1f;
        root.gameObject.SetActive(false);
        GroupsBroken++;
        LastGroups.Add(Survey.Path(root));
        int n = Mathf.Clamp(Mathf.RoundToInt(6 + size * 5f), 6, 30);
        fx.Burst(at, n, tint, unit * Mathf.Clamp(size * 3f + 3f, 4f, 14f), 0.5f, Vector2.zero, 1.3f);
        fx.Burst(at, Mathf.Max(3, n / 3), new Color(1f, 0.6f, 0.2f), unit * 5f, 0.35f, Vector2.zero, 1.1f);
        fx.PlayHit();
        try { OnBroke?.Invoke(root); } catch { }
        Log?.Invoke("broke object '" + Survey.Path(root) + "' (" + root.childCount + " children)");
        return at;
    }

    private static bool Crosses(Bounds bd, Vector2 a, Vector2 b)
    {
        if (bd.Contains(new Vector3(a.x, a.y, 0f)) || bd.Contains(new Vector3(b.x, b.y, 0f))) return true;
        var d = b - a; float len = d.magnitude;
        if (len < 0.0001f) return false;
        return bd.IntersectRay(new Ray(new Vector3(a.x, a.y, 0f), new Vector3(d.x / len, d.y / len, 0f)), out var dist) && dist <= len;
    }

    // ---- test helpers ----

    /// <summary>Why a renderer is (not) breakable: for the self-test log.</summary>
    internal static string Probe(Transform obj)
    {
        var sb = new System.Text.StringBuilder();
        Ensure(60f);
        var org = Origin();
        foreach (var r in obj.GetComponentsInChildren<Renderer>(true))
        {
            Entry found = null;
            foreach (var kv in Grid) { foreach (var e in kv.Value) if (e.R == r) { found = e; break; } if (found != null) break; }
            if (found == null) foreach (var e in Dyn) if (e.R == r) { found = e; break; }
            var sr = r as SpriteRenderer;
            sb.Append("[").Append(r.GetType().Name).Append(' ').Append(r.gameObject.name).Append(" enabled=").Append(r.enabled).Append(" active=").Append(r.gameObject.activeInHierarchy)
              .Append(sr != null ? " alpha=" + sr.color.a.ToString("0.##") + " sprite=" + (sr.sprite != null ? sr.sprite.name : "none") : "")
              .Append(" worldBounds=").Append(r.bounds.center.ToString("F0")).Append("/").Append(r.bounds.size.ToString("F0"))
              .Append(found != null ? " inGrid levelBounds=" + found.B.center.ToString("F0") + "/" + found.B.size.ToString("F0") + (found.Prot ? " PROT" : "") + (found.Moving ? " moving" : "") : " NOT IN GRID")
              .Append("] ");
        }
        var root = Groups.Resolve(obj);
        sb.Append(" -> group ").Append(root != null ? Survey.Path(root) : "none (" + Groups.LastWhy + ")");
        return sb.ToString();
    }


    /// <summary>Renderers whose level-space bounds are near the point (self-test target picking).</summary>
    internal static List<Renderer> SpritesNear(Vector2 world, float radius)
    {
        Ensure(60f);
        var res = new List<Renderer>();
        var l = world - Origin();
        int gx = Mathf.FloorToInt(l.x / GridCell), gy = Mathf.FloorToInt(l.y / GridCell);
        for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
            if (Grid.TryGetValue(((long)(gx + dx) << 32) ^ (uint)(gy + dy), out var list))
                foreach (var e in list) if (!e.Gone && e.R != null && e.R.gameObject.activeInHierarchy && e.B.SqrDistance(new Vector3(l.x, l.y, 0f)) < radius * radius && !res.Contains(e.R)) res.Add(e.R);
        return res;
    }

    internal static List<Tilemap> DecoMaps { get { Ensure(60f); return Maps; } }
    internal static IEnumerable<Renderer> AllSprites()
    {
        Ensure(60f);
        var seen = new HashSet<Entry>();
        foreach (var kv in Grid) foreach (var e in kv.Value) if (seen.Add(e) && !e.Gone && Showing(e.R)) yield return e.R;
        foreach (var e in Dyn) if (Showing(e.R)) yield return e.R;
    }
}
