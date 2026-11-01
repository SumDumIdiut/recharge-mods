using System;
using System.Collections.Generic;
using System.Reflection;
using TMPro;
using UnityEngine;

/// <summary>
/// The free "Unlock gun" box: a clone of one of the scene's own upgrade boxes, set to a purchase that
/// changes nothing in the game. Touching it runs the game's normal buy path (sound, particles, button
/// animation, cost/cap handling); the mod only watches TimesUsed to unlock the gun.
///
/// No-op purchase: upgrade = GLOBAL and globalUpgrade = treeGrowth. upgradeBox.DoBuyUpgrade's GLOBAL default
/// branch only does globalUpgradeDict[key] += 1, and treeGrowth is read by no other game code (checked in
/// the decompiled assembly); the mod writes the value straight back anyway. GLOBAL boxes are also skipped by
/// upgradeBox.onPrestige, so prestige / atom resets never un-buy it. Cost 0 so it is always affordable.
/// </summary>
internal class GunBox
{
    private const BindingFlags Any = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
    private const string Title = "Unlock gun";
    private const globalStats.globalUpgradeSet DummyKey = globalStats.globalUpgradeSet.treeGrowth;

    // Object references on the copied box that point into the original's course / tree / gate; cut them all.
    private static readonly string[] Detached = { "course", "clones", "upgradeTree", "gateAnim", "tripBreakerScript", "atomColliderScript", "statuePrestigeText", "ObjectReference", "doorToOpen", "courseToExemptReference", "localUpgradeScript" };
    private static readonly string[] DetachedArrays = { "oldCourses", "boxesToActivate", "boxesToDeactivate", "coursesToReset", "SegmentsToTrigger" };

    private readonly GunMod _mod;
    private GameObject _go;
    private upgradeBox _box;
    private TMP_Text _name, _cost;
    private double _dictBaseline;
    private bool _hasBaseline;
    private float _nextTry, _builtSeenAt = -1f;
    private float _playerAge;
    private bool _sawPurchase;

    internal GameObject Object => _go;
    internal upgradeBox Box => _box;
    /// <summary>How the box was placed (for the log and the self-test).</summary>
    internal bool InRow; internal int RowCount; internal float RowSpacing, RowBottomDelta, RowBottom, RowSide;
    internal string RowInfo = "not placed";
    internal Vector2 Center => _go != null ? (Vector2)_go.transform.position : Vector2.zero;

    internal GunBox(GunMod mod) { _mod = mod; }

    internal void Dispose()
    {
        if (_go != null) UnityEngine.Object.Destroy(_go);
        _go = null; _box = null;
    }

    internal void OnPlayerFound(Movement mv)
    {
        // The previous scene's box died with the scene; the new scene gets its own, placed once.
        _go = null; _box = null; _name = null; _cost = null; _hasBaseline = false;
        _nextTry = 0f; _builtSeenAt = -1f;
    }

    internal void Tick(Movement mv, float age)
    {
        _playerAge = age;
        if (mv == null || UnityEngine.SceneManagement.SceneManager.GetActiveScene().name == "MainMenu") return;
        // Placed once; the box never follows the respawn point (checkpoints move it all the time).
        if (_go != null || age <= 1.0f || Time.unscaledTime < _nextTry) return;
        _nextTry = Time.unscaledTime + 0.5f;
        if (!SpawnForPlacement(mv, age, out var spawn)) return;
        TryPlace(mv, spawn);
    }

    /// <summary>
    /// Where the box goes, once it may go there. Base game: the save's spawn (the respawn point the game
    /// loaded). Navigator map: the map's own spawn, only after Navigator has finished building the map.
    /// </summary>
    private bool SpawnForPlacement(Movement mv, float age, out Vector2 spawn)
    {
        spawn = BaseSpawn(mv);
        var nav = _mod.NavigatorState();
        if (!nav.Active) return age > 1.5f;
        if (nav.Built)
        {
            if (_builtSeenAt < 0f) { _builtSeenAt = Time.unscaledTime; _mod.Host.Log("Navigator map '" + nav.MapId + "' built; placing the box shortly"); }
            // Navigator drops the player on the ground a couple of physics steps after the build.
            if (Time.unscaledTime - _builtSeenAt < 2f) return false;
            if (nav.HasSpawn) spawn = nav.Spawn;
            return true;
        }
        if (age > 60f) { _mod.Host.LogWarning("Navigator map never finished building; placing the box at the respawn point"); return true; }
        return false;
    }

    private static Vector2 BaseSpawn(Movement mv)
    {
        var origin = Singleton<FloatingOrigin>.Instance != null ? (Vector2)Singleton<FloatingOrigin>.Instance.currentOrigin : Vector2.zero;
        // Movement.respawn drops the player 12 below the respawn point.
        var spawn = (Vector2)mv.respawnPoint + origin + new Vector2(0f, -12f);
        if (spawn.sqrMagnitude < 0.01f) spawn = mv.transform.position;
        return spawn;
    }

    // ---------- placing ----------

    private void TryPlace(Movement mv, Vector2 spawn)
    {
        var source = PickSource();
        if (source == null) { _mod.Host.LogWarning("no scene upgrade box to copy yet; will retry"); return; }

        var scale = source.transform.lossyScale;
        _go = UnityEngine.Object.Instantiate(source.gameObject);
        _go.name = "RechargeGunBox";
        _go.transform.SetParent(null, true);
        _go.transform.localScale = scale;
        _go.SetActive(true);
        _box = _go.GetComponent<upgradeBox>();
        // the original's on-screen key prompt (e.g. "LMB") is a child object; it makes no sense here
        foreach (var prompt in _go.GetComponentsInChildren<KeypromptSetter>(true))
            if (prompt != null && prompt.gameObject != _go) UnityEngine.Object.Destroy(prompt.gameObject);
        Configure(_box);
        Reposition(mv, spawn);
        _mod.Host.Log("upgrade box placed (copied from '" + source.name + "'), unlocked=" + _mod.IsUnlocked() + ", scale=" + scale + ", navigatorMap=" + _mod.NavigatorMapActive);
    }

    private static upgradeBox PickSource()
    {
        upgradeBox best = null; int bestScore = -1;
        foreach (var b in UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (b == null || b.gameObject.name == "RechargeGunBox") continue;
            if (b.GetComponent<SpriteRenderer>() == null) continue;
            int score = 0;
            if (b.gameObject.activeInHierarchy) score += 4;
            if (b.upgrade == localUpgrades.localUpgradeSet.Movement && b.movementUpgrade == upgradeBox.movementUpgrades.dash) score += 2;
            if (Field(b, "upgradeTree") == null) score += 1;
            if (score > bestScore) { best = b; bestScore = score; }
        }
        return best;
    }

    private static object Field(upgradeBox b, string name) => typeof(upgradeBox).GetField(name, Any)?.GetValue(b);
    private static void Set(upgradeBox b, string name, object v) => typeof(upgradeBox).GetField(name, Any)?.SetValue(b, v);

    private void Configure(upgradeBox box)
    {
        foreach (var n in Detached) Set(box, n, null);
        foreach (var n in DetachedArrays)
        {
            var f = typeof(upgradeBox).GetField(n, Any);
            if (f != null && f.FieldType.IsArray) f.SetValue(box, Array.CreateInstance(f.FieldType.GetElementType(), 0));
        }
        Set(box, "triggerAchievement", false);
        Set(box, "isSecret", false);
        Set(box, "isDemoBuild", false);
        Set(box, "achievement", "");
        Set(box, "upgradeCurrency", globalStats.Currencies.Cash);
        Set(box, "effectMult", 1.0);
        Set(box, "TierMod", 0.0);
        Set(box, "boxCooldown", 0.2f);

        box.upgrade = localUpgrades.localUpgradeSet.GLOBAL;
        box.globalUpgrade = DummyKey;
        box.movementUpgrade = upgradeBox.movementUpgrades.dash;
        box.baseUpgradeCost = 0.0; box.upgradeCost = 0.0;
        box.upgradeAddFactor = 0.0; box.upgradePowerScaleFactor = 1.0; box.upgradeScaleFactor = 1.0;
        box.Cap = 1; box.baseCap = 1; box.baseCapMult = 1.0;
        box.buyMax = false; box.neverBuyMax = true;
        box.ExemptFromPrestige = true;
        box.visible = true; box.init = true; box.isActive = true;
        box.TimesUsed = _mod.IsUnlocked() ? 1 : 0;

        _name = Field(box, "upgradeNameDisplay") as TMP_Text;
        _cost = Field(box, "upgradeCostDisplay") as TMP_Text;
        _hasBaseline = globalStats.globalUpgradeDict != null && globalStats.globalUpgradeDict.TryGetValue(DummyKey, out _dictBaseline);
        _sawPurchase = false;

        // reactivateAndReset() zeroes TimesUsed, so restore bought-ness through the game's own deactivate().
        if (box.TimesUsed > 0) Call(box, "deactivate");
        ApplyText();
    }

    private static void Call(upgradeBox b, string method) => typeof(upgradeBox).GetMethod(method, Any, null, Type.EmptyTypes, null)?.Invoke(b, null);

    private void Reposition(Movement mv, Vector2 spawn)
    {
        if (_go == null) return;
        var sr = _go.GetComponent<SpriteRenderer>();
        var size = sr != null ? (Vector2)sr.bounds.size : new Vector2(60f, 60f);
        float pw = 20f;
        var col = mv.GetComponent<BoxCollider2D>();
        if (col != null) pw = col.bounds.extents.x;

        InRow = false; RowInfo = "no row of upgrade boxes near the spawn; default spot beside the spawn";
        if (TryPlaceInRow(spawn)) return;

        float gap = pw + size.x * 0.5f + Mathf.Max(8f, size.x * 0.25f);
        float side = 1f;
        var hit = Probe(spawn + new Vector2(0f, size.y * 0.5f), Vector2.right, gap + size.x * 0.5f);
        if (hit.collider != null) side = -1f;
        float x = spawn.x + side * gap;

        float groundY = spawn.y - 20f;
        var down = Probe(new Vector2(x, spawn.y + size.y), Vector2.down, size.y * 4f + 200f);
        if (down.collider != null) groundY = down.point.y;

        // The game's boxes are bumped from below: the buy trigger is a thin strip at the underside of the solid
        // body (that body reaches a little below the sprite). So the box floats with the underside a jump
        // above the ground: walk under it and jump into it. Bounds come from the colliders, not the sprite.
        Collider2D solid = null;
        foreach (var c in _go.GetComponents<Collider2D>()) if (!c.isTrigger) { solid = c; break; }
        var bounds = solid != null ? solid.bounds : (sr != null ? sr.bounds : new Bounds(_go.transform.position, Vector3.one));
        float clearance = Mathf.Max(pw * 2f * 2.6f, 110f);   // ~2.6 player heights (a jump reaches ~3.3)
        var ceiling = Probe(new Vector2(x, groundY + 10f), Vector2.up, clearance + bounds.size.y + 20f);
        if (ceiling.collider != null && ceiling.point.y - groundY < clearance + bounds.size.y + 10f)
            clearance = Mathf.Max(pw * 2f * 1.4f, ceiling.point.y - groundY - bounds.size.y - 10f);
        var delta = new Vector3(x - bounds.center.x, groundY + clearance - bounds.min.y, 0f);
        _go.transform.position += delta;
        _mod.Host.Log("upgrade box at " + (Vector2)_go.transform.position + " (clearance " + clearance.ToString("0") + ", spawn " + spawn + ", player half-width " + pw.ToString("0.#") + ", box size " + size + ", ground " + (down.collider != null ? down.collider.name : "none") + ", side " + side + ")");
    }

    // ---------- the row of upgrade boxes ----------

    private static Bounds BoxBounds(GameObject go)
    {
        Collider2D solid = null;
        foreach (var c in go.GetComponents<Collider2D>()) if (c != null && !c.isTrigger) { solid = c; break; }
        if (solid != null) return solid.bounds;
        var sr = go.GetComponent<SpriteRenderer>();
        return sr != null ? sr.bounds : new Bounds(go.transform.position, Vector3.one * 50f);
    }

    private class Rowed { public upgradeBox B; public Bounds Bd; }

    /// <summary>
    /// Looks for a row of upgrade boxes near the spawn (boxes whose bottoms are at the same height) and puts
    /// this box at the end of the nearest row: same bottom, same centre-to-centre spacing as the row's last
    /// two boxes, on the side whose spot is free (OverlapBox against colliders and other boxes). Returns
    /// false (nothing moved) when there is no row or no free end.
    /// </summary>
    private bool TryPlaceInRow(Vector2 spawn)
    {
        var cam = Camera.main;
        float halfH = cam != null && cam.orthographic ? cam.orthographicSize : 270f;
        float halfW = cam != null && cam.orthographic ? halfH * cam.aspect : halfH * 1.78f;
        float reachX = halfW * 2f * 1.5f, reachY = halfH * 2f * 1.5f;

        var near = new List<Rowed>();
        foreach (var b in UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsInactive.Exclude, FindObjectsSortMode.None))
        {
            if (b == null || b.gameObject == _go || !b.gameObject.activeInHierarchy) continue;
            var sr = b.GetComponent<SpriteRenderer>();
            if (sr == null || !sr.enabled) continue;
            var bd = BoxBounds(b.gameObject);
            if (Mathf.Abs(bd.center.x - spawn.x) > reachX || Mathf.Abs(bd.center.y - spawn.y) > reachY) continue;
            near.Add(new Rowed { B = b, Bd = bd });
        }
        if (near.Count < 2) return false;

        // group by bottom height, then split each group into runs of neighbours (a gap much wider than the
        // row's usual spacing starts a new run, so a far-away box at the same height is not part of the row)
        var mine = BoxBounds(_go);
        float tol = Mathf.Max(3f, mine.size.y * 0.08f);
        near.Sort((a, c) => a.Bd.min.y.CompareTo(c.Bd.min.y));
        List<Rowed> best = null; float bestDist = float.MaxValue;
        int i = 0;
        while (i < near.Count)
        {
            var group = new List<Rowed> { near[i] };
            int j = i + 1;
            while (j < near.Count && near[j].Bd.min.y - group[0].Bd.min.y <= tol) { group.Add(near[j]); j++; }
            i = j;
            if (group.Count < 2) continue;
            group.Sort((a, c) => a.Bd.center.x.CompareTo(c.Bd.center.x));
            var gaps = new List<float>();
            for (int k = 1; k < group.Count; k++) gaps.Add(group[k].Bd.center.x - group[k - 1].Bd.center.x);
            var sorted = new List<float>(gaps); sorted.Sort();
            float usual = sorted[sorted.Count / 2];
            var run = new List<Rowed> { group[0] };
            for (int k = 1; k <= group.Count; k++)
            {
                if (k < group.Count && gaps[k - 1] <= usual * 1.5f + 1f) { run.Add(group[k]); continue; }
                if (run.Count >= 2)
                {
                    float d = float.MaxValue;
                    foreach (var r in run) d = Mathf.Min(d, Vector2.Distance(r.Bd.center, spawn));
                    if (d < bestDist) { bestDist = d; best = new List<Rowed>(run); }
                }
                run.Clear();
                if (k < group.Count) run.Add(group[k]);
            }
        }
        if (best == null) return false;

        best.Sort((a, c) => a.Bd.center.x.CompareTo(c.Bd.center.x));
        { var sb = new System.Text.StringBuilder("row candidates:"); foreach (var r in best) sb.Append(" '").Append(r.B.name).Append("'@").Append(r.Bd.center.x.ToString("0")).Append(",").Append(r.Bd.min.y.ToString("0")).Append(r.B.visible ? "v" : "h").Append(r.B.isActive ? "a" : "i"); _candidates = sb.ToString(); _mod.Host.Log(_candidates); }
        int n = best.Count;
        float bottom = 0f; foreach (var r in best) bottom += r.Bd.min.y; bottom /= n;
        float spR = best[n - 1].Bd.center.x - best[n - 2].Bd.center.x;     // spacing at each end of the row
        float spL = best[1].Bd.center.x - best[0].Bd.center.x;
        float xR = best[n - 1].Bd.center.x + spR, xL = best[0].Bd.center.x - spL;
        var cands = new List<KeyValuePair<float, float>>();                  // x, side
        if (spR > 1f) cands.Add(new KeyValuePair<float, float>(xR, 1f));
        if (spL > 1f) cands.Add(new KeyValuePair<float, float>(xL, -1f));
        cands.Sort((a, c) => Mathf.Abs(a.Key - spawn.x).CompareTo(Mathf.Abs(c.Key - spawn.x)));

        foreach (var cand in cands)
        {
            var center = new Vector2(cand.Key, bottom + mine.size.y * 0.5f);
            if (!SpotFree(center, mine.size)) continue;
            var delta = new Vector3(cand.Key - mine.center.x, bottom - mine.min.y, 0f);
            _go.transform.position += delta;
            Physics2D.SyncTransforms();
            var after = BoxBounds(_go);
            InRow = true; RowCount = n; RowSide = cand.Value; RowSpacing = cand.Value > 0 ? spR : spL; RowBottom = bottom; RowBottomDelta = after.min.y - bottom;
            RowInfo = _candidates + " => end of a row of " + n + " upgrade boxes (bottom y " + bottom.ToString("0.#") + ", spacing " + RowSpacing.ToString("0.#") + ", side " + (cand.Value > 0 ? "right" : "left") + ")";
            _mod.Host.Log("upgrade box lined up: " + RowInfo + "; box bottom " + after.min.y.ToString("0.#") + ", centre x " + after.center.x.ToString("0.#") + ", spawn " + spawn);
            return true;
        }
        _mod.Host.Log("row of " + n + " upgrade boxes found but both ends are blocked; using the default spot");
        return false;
    }

    private string _candidates = "";
    private readonly Collider2D[] _overlap = new Collider2D[24];

    /// <summary>Nothing solid, and no other upgrade box, inside this box-sized area.</summary>
    private bool SpotFree(Vector2 center, Vector2 size)
    {
        var filter = new ContactFilter2D { useTriggers = true };
        filter.SetLayerMask(Physics2D.AllLayers);
        int n = Physics2D.OverlapBox(center, size * 0.9f, 0f, filter, _overlap);
        for (int k = 0; k < n; k++)
        {
            var c = _overlap[k];
            if (c == null || c.CompareTag("Player")) continue;
            if (_go != null && c.transform.IsChildOf(_go.transform)) continue;
            if (c.GetComponentInParent<upgradeBox>() != null) return false;   // another box, solid or trigger
            if (c.isTrigger) continue;
            return false;
        }
        return true;
    }

    private static RaycastHit2D Probe(Vector2 from, Vector2 dir, float dist)
    {
        var results = new RaycastHit2D[8];
        var filter = new ContactFilter2D { useTriggers = false };
        filter.SetLayerMask(Physics2D.AllLayers);
        int n = Physics2D.Raycast(from, dir, filter, results, dist);
        for (int i = 0; i < n; i++)
        {
            var c = results[i].collider;
            if (c == null || c.CompareTag("Player") || c.GetComponentInParent<upgradeBox>() != null) continue;
            return results[i];
        }
        return default;
    }

    // ---------- per-frame watch ----------

    internal void LateTick()
    {
        if (_box == null) { _go = _go != null ? _go : null; return; }

        // Un-do the dummy key's increment so the game state is exactly as before.
        if (_hasBaseline && globalStats.globalUpgradeDict != null && globalStats.globalUpgradeDict.TryGetValue(DummyKey, out var v) && v != _dictBaseline)
            globalStats.globalUpgradeDict[DummyKey] = _dictBaseline;

        bool unlocked = _mod.IsUnlocked();
        if (_box.TimesUsed > 0 && !unlocked && !_sawPurchase)
        {
            _sawPurchase = true;
            _mod.Host.Log("box bought (TimesUsed=" + _box.TimesUsed + ") at " + Center);
            _mod.SetUnlocked(true);
            _mod.Gun?.OnUnlocked();
            unlocked = true;
        }
        else if (unlocked && _box.TimesUsed == 0)
        {
            // something reset the box (prestige-style reset, scene logic): the mod is the source of truth
            _box.TimesUsed = 1;
            Call(_box, "deactivate");
        }
        else if (!unlocked && _box.TimesUsed == 0 && !_box.isActive)
        {
            _box.reactivateAndReset();
            _sawPurchase = false;
        }
        else if (!unlocked) _sawPurchase = false;
        ApplyText();
    }

    private void ApplyText()
    {
        if (_name != null && _name.text != Title) _name.text = Title;
        if (_box != null && _box.isActive && _cost != null && _cost.text != "FREE")
        {
            _cost.text = "FREE";
            _cost.color = new Color(0.55f, 1f, 0.6f);
        }
    }

    /// <summary>Pause-menu "Re-lock gun": the box is buyable again.</summary>
    internal void Relock()
    {
        _mod.SetUnlocked(false);
        _sawPurchase = false;
        if (_box != null)
        {
            _box.reactivateAndReset();
            _box.TimesUsed = 0;
            ApplyText();
        }
    }
}
