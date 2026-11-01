using System;
using System.Collections;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>RECHARGE_GUN_SELFTEST_SPIKES=1 (with _BREAK=1): survey every spike (rotated tiles, objects, sprites) and shoot one of each orientation.</summary>
internal partial class SelfTest
{
    private static bool IsSpikeName(string n)
    {
        if (string.IsNullOrEmpty(n)) return false;
        n = n.ToLowerInvariant();
        return n.Contains("spike") || n.Contains("thorn") || n.Contains("saw");
    }

    /// <summary>Rotation of a tile's transform in degrees (0, 90, 180, 270) plus a flip tag.</summary>
    private static string Orient(Matrix4x4 m, out int angle)
    {
        var up = m.MultiplyVector(Vector3.up);
        var right = m.MultiplyVector(Vector3.right);
        float a = Mathf.Atan2(-right.y, right.x) * Mathf.Rad2Deg;      // angle of the x axis
        angle = ((Mathf.RoundToInt(Mathf.Atan2(right.y, right.x) * Mathf.Rad2Deg / 90f) * 90) % 360 + 360) % 360;
        // handedness: right x up
        float det = right.x * up.y - right.y * up.x;
        return "rot" + angle + (det < 0 ? "F" : "");
    }

    private static string PhysTip(Tilemap tm, Vector3Int c)
    {
        try
        {
            var sp = tm.GetSprite(c);
            if (sp == null || sp.GetPhysicsShapeCount() == 0) return "noshape";
            var pts = new List<Vector2>();
            sp.GetPhysicsShape(0, pts);
            var m = tm.GetTransformMatrix(c);
            Vector2 sum = Vector2.zero; foreach (var p in pts) sum += (Vector2)m.MultiplyPoint3x4(p);
            sum /= pts.Count;
            return "shapeCentroid(" + sum.x.ToString("0.00") + "," + sum.y.ToString("0.00") + ") n" + pts.Count;
        }
        catch { return "?"; }
    }

    /// <summary>Which way a spike tile's collider sits in its cell (the side its mass is on): L, R, U, D or C.</summary>
    private static string ShapeDir(Tilemap tm, Vector3Int c)
    {
        try
        {
            var sp = tm.GetSprite(c);
            if (sp == null || sp.GetPhysicsShapeCount() == 0) return "C";
            var pts = new List<Vector2>(); sp.GetPhysicsShape(0, pts);
            var m = tm.GetTransformMatrix(c); Vector2 sum = Vector2.zero;
            foreach (var p in pts) sum += (Vector2)m.MultiplyPoint3x4(p);
            sum /= pts.Count;
            if (sum.magnitude < 1f) return "C";
            return Mathf.Abs(sum.x) > Mathf.Abs(sum.y) ? (sum.x > 0 ? "R" : "L") : (sum.y > 0 ? "U" : "D");
        }
        catch { return "C"; }
    }

    private void SpikeSurvey()
    {
        L("---- spike survey ----");
        foreach (var tm in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (tm == null || !tm.gameObject.scene.IsValid()) continue;
            bool sc = tm.GetComponent<spikeScript>() != null || LayerMask.LayerToName(tm.gameObject.layer) == "Spikes";
            var byOr = new Dictionary<string, int>(); var sample = new Dictionary<string, string>();
            int n = 0, nameHits = 0;
            foreach (var p in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(p)) continue;
                n++;
                var t = tm.GetTile(p);
                bool sn = t != null && IsSpikeName(t.name);
                if (!sc && !sn) continue;
                if (sn) nameHits++;
                var o = Orient(tm.GetTransformMatrix(p), out _);
                var key = o + (sn ? " spikeTile:" + t.name.Substring(0, Math.Min(18, t.name.Length)) : " tile:" + t.name);
                byOr.TryGetValue(key, out var k); byOr[key] = k + 1;
                if (!sample.ContainsKey(key)) sample[key] = p.x + "," + p.y + " " + PhysTip(tm, p);
            }
            if (!sc && nameHits == 0) continue;
            L("spike-ish tilemap '" + Survey.Path(tm.transform) + "' active=" + tm.gameObject.activeInHierarchy + " layer " + LayerMask.LayerToName(tm.gameObject.layer) + " tiles " + n + " spikeScript=" + (tm.GetComponent<spikeScript>() != null) + " comps {" + Survey.Comps(tm.gameObject).Substring(0, Math.Min(200, Survey.Comps(tm.gameObject).Length)) + "}");
            foreach (var kv in byOr) L("    " + kv.Key + " x" + kv.Value + " e.g. cell " + sample[kv.Key]);
        }
        foreach (var s in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (s == null || s.GetComponent<Tilemap>() != null) continue;
            var cols = s.GetComponentsInChildren<Collider2D>(true);
            L("spike OBJECT '" + Survey.Path(s.transform) + "' active=" + s.gameObject.activeInHierarchy + " layer " + LayerMask.LayerToName(s.gameObject.layer) + " pos " + (Vector2)s.transform.position + " rotZ " + s.transform.eulerAngles.z.ToString("0") + " colliders " + cols.Length + " comps {" + Survey.Comps(s.gameObject) + "} sprites in kids " + s.GetComponentsInChildren<SpriteRenderer>(true).Length);
        }
        int shown = 0;
        foreach (var sr in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (sr == null || sr.sprite == null) continue;
            if (!IsSpikeName(sr.sprite.name) && !IsSpikeName(sr.name)) continue;
            if (sr.GetComponentInParent<spikeScript>(true) != null) continue;
            if (++shown > 60) break;
            var col = sr.GetComponentInParent<Collider2D>(true);
            L("spike-looking SPRITE '" + Survey.Path(sr.transform) + "' sprite '" + sr.sprite.name + "' layer " + LayerMask.LayerToName(sr.gameObject.layer) + " pos " + (Vector2)sr.transform.position + " rotZ " + sr.transform.eulerAngles.z.ToString("0") + " collider " + (col != null ? col.GetType().Name + (col.isTrigger ? "(trig)" : "") + " on '" + col.name + "' layer " + LayerMask.LayerToName(col.gameObject.layer) : "none"));
        }
        // anything on the Spikes layer or with a killing script that is not a spikeScript
        foreach (var c in UnityEngine.Object.FindObjectsByType<Collider2D>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c == null) continue;
            if (LayerMask.LayerToName(c.gameObject.layer) != "Spikes") continue;
            if (c.GetComponentInParent<spikeScript>(true) != null) continue;
            L("Spikes-layer collider without spikeScript: " + Survey.Describe(c));
        }
        L("---- end spike survey ----");
    }

    /// <summary>A spike's shape can spill into the next cell: the bullet then strikes (and rightly removes) that neighbour.</summary>
    private static bool ClearedNear(Tilemap m, Vector3Int c)
    {
        foreach (var s in TileBreaker.LastCleared)
        {
            var at = s.IndexOf('@'); if (at < 0 || s.Substring(0, at) != m.name) continue;
            var xy = s.Substring(at + 1).Split(',');
            if (Math.Abs(int.Parse(xy[0]) - c.x) <= 1 && Math.Abs(int.Parse(xy[1]) - c.y) <= 1) return true;
        }
        return false;
    }

    /// <summary>Shoots one cell of each orientation of every spike tilemap, from each side the bullet could come.</summary>
    private IEnumerator SpikeShots()
    {
        var gun = _mod.Gun; var mv = _mod.Player;
        float U = gun.Unit;
        gun.Verbose = true; _softNoShot = true;
        _mod.Config.RecoilStrength = 0f;
        gun.SetGun(GunDefs.All[0]);
        Freeze(true);
        var startLevel = (Vector2)mv.transform.position - FloatOrigin();
        foreach (var tm in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (tm == null || tm.GetComponent<spikeScript>() == null) continue;
            var name = tm.name;
            var byOr = new Dictionary<string, List<Vector3Int>>();
            foreach (var p in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(p)) continue;
                Orient(tm.GetTransformMatrix(p), out var ang);
                var key = "side" + ShapeDir(tm, p) + "_rot" + ang;
                if (!byOr.TryGetValue(key, out var l)) byOr[key] = l = new List<Vector3Int>();
                l.Add(p);
            }
            foreach (var kv in byOr)
            {
                var cells = kv.Value; var m = tm;
                var from = startLevel + FloatOrigin();
                var near = Nearest(cells, c => (Vector2)m.GetCellCenterWorld(c), from, 40);
                var cands = new List<Candidate>();
                foreach (var cell in near)
                {
                    var cc = cell;
                    cands.Add(new Candidate { Map = m, Cell = cc, Pos = () => m.GetCellCenterWorld(cc), IsTarget = c => c != null && c.gameObject == m.gameObject, Lenient = true, Removed = () => !m.HasTile(cc) || ClearedNear(m, cc), Desc = m.name + "@" + cc.x + "," + cc.y + " " + kv.Key + " tile '" + m.GetTile(cc).name + "' " + PhysTip(m, cc) });
                }
                var only = Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_ONLY");
                yield return Shoot("spikeor_" + name + "_" + kv.Key, cands);
            }
        }
        // spike objects (not tiles): a few of each kind
        int objs = 0;
        foreach (var sp in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsSortMode.None))
        {
            if (sp == null || sp.GetComponent<Tilemap>() != null || !sp.gameObject.activeInHierarchy) continue;
            var go = sp.gameObject; var col = go.GetComponentInChildren<Collider2D>();
            if (col == null || ++objs > 6) continue;
            var srs = go.GetComponentsInChildren<SpriteRenderer>(true);
            var cands = new List<Candidate> { new Candidate { Pos = () => col.bounds.center, IsTarget = c => c != null && c.transform.IsChildOf(go.transform), Removed = () => !go.activeInHierarchy, Desc = "spike object '" + Survey.Path(go.transform) + "' rotZ " + go.transform.eulerAngles.z.ToString("0") + " art sprites " + srs.Length } };
            yield return Shoot("spikeobj" + objs, cands);
        }
        _softNoShot = false;
        Freeze(false);
        gun.AimOverride = null; gun.Verbose = false;
    }
}
