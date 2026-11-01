using System;
using System.Collections.Generic;
using System.Text;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>Diagnostics for the self-test: what a collider / object is made of.</summary>
internal static class Survey
{
    internal static string Path(Transform t)
    {
        var sb = new StringBuilder(t.name);
        for (var p = t.parent; p != null; p = p.parent) sb.Insert(0, p.name + "/");
        return sb.ToString();
    }

    internal static string Comps(GameObject go)
    {
        var sb = new StringBuilder();
        foreach (var c in go.GetComponents<Component>())
        {
            if (c == null) continue;
            if (sb.Length > 0) sb.Append(',');
            sb.Append(c.GetType().Name);
            var col = c as Collider2D; if (col != null && col.isTrigger) sb.Append("(trig)");
        }
        return sb.ToString();
    }

    internal static string Describe(Collider2D c)
    {
        if (c == null) return "null";
        var go = c.gameObject;
        string parents = "";
        for (var p = go.transform.parent; p != null; p = p.parent)
        {
            var scripts = new StringBuilder();
            foreach (var mb in p.GetComponents<MonoBehaviour>()) if (mb != null) scripts.Append(mb.GetType().Name).Append(' ');
            if (scripts.Length > 0) parents += " [" + p.name + ": " + scripts.ToString().Trim() + "]";
        }
        return "'" + Path(go.transform) + "' layer " + LayerMask.LayerToName(go.layer) + " tag " + go.tag + " collider " + c.GetType().Name + (c.isTrigger ? "(trigger)" : "") + " comps {" + Comps(go) + "}" + parents;
    }

    internal static void Dump(Action<string> L)
    {
        L("---- survey: tilemaps ----");
        var maps = UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsInactive.Exclude, FindObjectsSortMode.None);
        foreach (var tm in maps)
        {
            int n = 0; string first = "";
            foreach (var p in tm.cellBounds.allPositionsWithin) if (tm.HasTile(p)) { n++; if (first == "") first = tm.GetTile(p).name; }
            var r = tm.GetComponent<TilemapRenderer>();
            L("tilemap '" + Path(tm.transform) + "' layer " + LayerMask.LayerToName(tm.gameObject.layer) + " tiles " + n + " first '" + first + "' sort '" + (r != null ? r.sortingLayerName + "/" + r.sortingOrder : "-") + "' comps {" + Comps(tm.gameObject) + "}");
        }
        L("---- survey: what sits next to spike cells ----");
        foreach (var tm in maps)
        {
            if (tm.GetComponent<spikeScript>() == null) continue;
            var agg = new Dictionary<string, int>(); int cells = 0; int step = 0;
            foreach (var p in tm.cellBounds.allPositionsWithin)
            {
                if (!tm.HasTile(p)) continue;
                if ((step++ % 7) != 0) continue;
                cells++;
                foreach (var o in maps)
                {
                    if (o == tm) continue;
                    for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++)
                    {
                        var q = o.WorldToCell(tm.GetCellCenterWorld(p)) + new Vector3Int(dx, dy, 0);
                        var t = o.GetTile(q);
                        if (t == null) continue;
                        var k = o.name + ":" + t.name + (dx == 0 && dy == 0 ? " (same cell)" : "");
                        agg[k] = agg.TryGetValue(k, out var v) ? v + 1 : 1;
                    }
                }
                if (cells >= 60) break;
            }
            var lines = new List<KeyValuePair<string, int>>(agg);
            lines.Sort((a, b) => b.Value.CompareTo(a.Value));
            var sb2 = new StringBuilder();
            for (int i = 0; i < lines.Count && i < 14; i++) sb2.Append(lines[i].Key).Append(" x").Append(lines[i].Value).Append("; ");
            L("spike tilemap '" + tm.name + "' (" + cells + " sampled cells) neighbours: " + sb2);
        }
        L("---- survey: spikes ----");
        foreach (var s in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            L("spike '" + Path(s.transform) + "' at " + (Vector2)s.transform.position + " comps {" + Comps(s.gameObject) + "}");
        L("---- survey: upgrade boxes ----");
        foreach (var s in UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
            L("box '" + Path(s.transform) + "' at " + (Vector2)s.transform.position + " comps {" + Comps(s.gameObject) + "}");
        L("---- survey: sprite renderers (grouped) ----");
        var groups = new Dictionary<string, int>(); var sample = new Dictionary<string, string>();
        foreach (var r in UnityEngine.Object.FindObjectsByType<SpriteRenderer>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            var go = r.gameObject;
            bool col = go.GetComponent<Collider2D>() != null;
            bool scr = false; foreach (var mb in go.GetComponents<MonoBehaviour>()) if (mb != null) scr = true;
            string key = r.sortingLayerName + " col=" + col + " scripts=" + scr + " parent='" + (go.transform.parent != null ? go.transform.parent.name : "-") + "'";
            groups[key] = groups.TryGetValue(key, out var v) ? v + 1 : 1;
            if (!sample.ContainsKey(key)) sample[key] = Path(go.transform) + " {" + Comps(go) + "}";
        }
        foreach (var kv in groups) L("sprites x" + kv.Value + " " + kv.Key + " e.g. " + sample[kv.Key]);
    }

    // ---- survey for the break-everything work ----

    internal static void DumpObjects(Action<string> L)
    {
        L("---- survey2: script types in the level ----");
        var counts = new Dictionary<string, int>(); var ex = new Dictionary<string, string>();
        foreach (var mb in UnityEngine.Object.FindObjectsByType<MonoBehaviour>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (mb == null) continue;
            var n = mb.GetType().Name;
            counts[n] = counts.TryGetValue(n, out var v) ? v + 1 : 1;
            if (!ex.ContainsKey(n)) ex[n] = Path(mb.transform);
        }
        var l = new List<KeyValuePair<string, int>>(counts);
        l.Sort((a, b) => b.Value.CompareTo(a.Value));
        foreach (var kv in l) L("script " + kv.Key + " x" + kv.Value + " e.g. " + ex[kv.Key]);
        L("---- survey2: renderer kinds ----");
        var rk = new Dictionary<string, int>();
        foreach (var r in UnityEngine.Object.FindObjectsByType<Renderer>(FindObjectsInactive.Include, FindObjectsSortMode.None)) { var n = r.GetType().Name + (r.enabled ? "" : "(off)"); rk[n] = rk.TryGetValue(n, out var v) ? v + 1 : 1; }
        foreach (var kv in rk) L("renderer " + kv.Key + " x" + kv.Value);
        try { var fo = Singleton<FloatingOrigin>.Instance; if (fo != null) L("FloatingOrigin on '" + Path(fo.transform) + "' children " + fo.transform.childCount + " comps {" + Comps(fo.gameObject) + "}"); } catch { }
        var cam = Camera.main; if (cam != null) L("main camera '" + Path(cam.transform) + "'");
        var mv = UnityEngine.Object.FindFirstObjectByType<Movement>(); if (mv != null) L("player '" + Path(mv.transform) + "' root children " + mv.transform.root.childCount);
        L("---- survey3: gameplay objects and their renderers ----");
        DescribeKind<checkpointScript>(L, 1); DescribeKind<JiggleDropScript>(L, 0); DescribeKind<startGate>(L, 1); DescribeKind<endGate>(L, 1); DescribeKind<SpringScript>(L, 1); DescribeKind<TeleporterScript>(L, 1); DescribeKind<PlatformMover>(L, 1); DescribeKind<upgradeBox>(L, 0);
        L("---- survey2: scene roots ----");
        foreach (var go in UnityEngine.SceneManagement.SceneManager.GetActiveScene().GetRootGameObjects())
            L("root '" + go.name + "' active=" + go.activeSelf + " children " + go.transform.childCount + " renderers " + go.GetComponentsInChildren<Renderer>(true).Length + " comps {" + Comps(go) + "}");
    }

    private static void DescribeKind<T>(Action<string> L, int n) where T : Component
    {
        int k = 0, total = 0, ownVis = 0, anyVis = 0;
        foreach (var c in UnityEngine.Object.FindObjectsByType<T>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (c == null) continue;
            total++;
            var own = c.GetComponent<SpriteRenderer>();
            if (own != null && own.sprite != null && own.color.a >= 0.05f) ownVis++;
            foreach (var r in c.GetComponentsInChildren<SpriteRenderer>(true)) if (r.sprite != null && r.color.a >= 0.05f) { anyVis++; break; }
            if (++k > n) continue;
            L(typeof(T).Name + " '" + Path(c.transform) + "' active=" + c.gameObject.activeInHierarchy + " parent children " + (c.transform.parent != null ? c.transform.parent.childCount : 0));
            foreach (var r in c.transform.parent != null && c.transform.parent.parent != null ? c.transform.parent.GetComponentsInChildren<Renderer>(true) : c.GetComponentsInChildren<Renderer>(true))
            {
                var sr = r as SpriteRenderer;
                L("    " + Path(r.transform) + " " + r.GetType().Name + " enabled=" + r.enabled + " active=" + r.gameObject.activeInHierarchy + (sr != null ? " sprite=" + (sr.sprite != null ? sr.sprite.name : "none") + " alpha=" + sr.color.a.ToString("0.##") : "") + " size=" + r.bounds.size.ToString("F0") + " comps {" + Comps(r.gameObject) + "}");
            }
        }
        L("SUMMARY " + typeof(T).Name + ": total " + total + ", own sprite visible (alpha>=0.05) " + ownVis + ", any sprite under it visible " + anyVis);
    }

    internal static void DumpUi(Action<string> L)
    {
        L("---- survey ui ----");
        var cd = Singleton<CurrencyDisplayController>.Instance;
        if (cd == null) { L("no CurrencyDisplayController"); return; }
        L("CurrencyDisplayController at '" + Path(cd.transform) + "' tab " + cd.activeTab);
        var cv = cd.GetComponentInParent<Canvas>();
        if (cv != null)
        {
            var root = cv.rootCanvas;
            var sc = root.GetComponent<UnityEngine.UI.CanvasScaler>();
            L("canvas '" + root.name + "' mode " + root.renderMode + " sort " + root.sortingOrder + " scaleFactor " + root.scaleFactor + (sc != null ? " scaler " + sc.uiScaleMode + " ref " + sc.referenceResolution + " match " + sc.matchWidthOrHeight : " no scaler"));
            Walk(root.transform, 0, 7, L);
        }
        L("---- survey ui: all sprites used by Images (distinct) ----");
        var seen = new HashSet<string>();
        foreach (var im in UnityEngine.Object.FindObjectsByType<UnityEngine.UI.Image>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (im.sprite == null) continue;
            var k = im.sprite.name + "|" + im.type;
            if (!seen.Add(k)) continue;
            L("image sprite '" + im.sprite.name + "' tex " + im.sprite.texture.width + "x" + im.sprite.texture.height + " rect " + im.sprite.rect + " border " + im.sprite.border + " type " + im.type + " color " + im.color + " e.g. " + Path(im.transform));
        }
        L("---- survey ui: fonts ----");
        var fs = new Dictionary<string, int>();
        foreach (var t in UnityEngine.Object.FindObjectsByType<TMPro.TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        { var k = (t.font != null ? t.font.name : "null") + " size " + t.fontSize + " color " + t.color + " style " + t.fontStyle + " e.g. " + Path(t.transform) + " '" + t.text + "'"; if (fs.Count < 40 && !fs.ContainsKey(k)) fs[k] = 1; }
        foreach (var kv in fs) L("text " + kv.Key);
    }

    private static void Walk(Transform t, int depth, int max, Action<string> L)
    {
        var sb = new StringBuilder(new string(' ', depth * 2));
        sb.Append(t.name).Append(" [").Append(t.gameObject.activeSelf ? "on" : "off").Append("]");
        var rt = t as RectTransform;
        if (rt != null) sb.Append(" anchors ").Append(rt.anchorMin).Append(rt.anchorMax).Append(" pos ").Append(rt.anchoredPosition).Append(" size ").Append(rt.sizeDelta).Append(" rect ").Append(rt.rect);
        var im = t.GetComponent<UnityEngine.UI.Image>();
        if (im != null) sb.Append(" IMG ").Append(im.sprite != null ? im.sprite.name : "-").Append(' ').Append(im.type).Append(' ').Append(im.color);
        var tx = t.GetComponent<TMPro.TMP_Text>();
        if (tx != null) sb.Append(" TMP '").Append(tx.text).Append("' ").Append(tx.font != null ? tx.font.name : "-").Append(' ').Append(tx.fontSize).Append(' ').Append(tx.color);
        L(sb.ToString());
        if (depth >= max) return;
        for (int i = 0; i < t.childCount && i < 25; i++) Walk(t.GetChild(i), depth + 1, max, L);
    }
}
