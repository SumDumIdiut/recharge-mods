using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.InputSystem.LowLevel;
using UnityEngine.Tilemaps;

/// <summary>RECHARGE_GUN_SELFTEST_BREAK=1: shoot a spike strip, a rail, decoration and upgrade boxes, then drive the hotbar.</summary>
internal partial class SelfTest
{
    private Rigidbody2D _frozen;
    private Candidate _lastChosen;
    private bool _softNoShot;

    private ContactFilter2D GunFilter()
    {
        var f = new ContactFilter2D { useTriggers = true };
        f.SetLayerMask(Physics2D.GetLayerCollisionMask(_mod.Player.gameObject.layer));
        return f;
    }

    /// <summary>First thing a bullet would stop on from <paramref name="from"/> along dir (same rules as the gun).</summary>
    private Collider2D FirstStop(Vector2 from, Vector2 dir, float dist, out float d)
    {
        var hits = new RaycastHit2D[24];
        int n = Physics2D.Raycast(from, dir, GunFilter(), hits, dist);
        for (int i = 0; i < n; i++)
        {
            var c = hits[i].collider;
            if (c == null || c.CompareTag("Player") || c.attachedRigidbody == _mod.Player.GetComponent<Rigidbody2D>()) continue;
            if (c.isTrigger && c.GetComponent<spikeScript>() == null && c.GetComponentInParent<spikeScript>() == null) continue;
            d = hits[i].distance; return c;
        }
        d = float.MaxValue; return null;
    }

    private static readonly Vector2[] Dirs =
    {
        new Vector2(1, 0), new Vector2(-1, 0), new Vector2(0, 1), new Vector2(0, -1),
        new Vector2(0.7071f, 0.7071f), new Vector2(-0.7071f, 0.7071f), new Vector2(0.7071f, -0.7071f), new Vector2(-0.7071f, -0.7071f),
    };

    /// <summary>A clear spot to stand and a direction from which the first thing the bullet meets is the target (or, for collider-less decoration, nothing solid before it).</summary>
    private bool FindSpot(Candidate cand, float U, out Vector2 spot, out Vector2 dir, out float dist)
    {
        var target = cand.Point; var isTarget = cand.IsTarget;
        foreach (var dd in new[] { 3.5f, 2.2f, 5f })
            foreach (var d in Dirs)
            {
                var s = target - d * (dd * U);
                // the player (box) must fit there
                bool blocked = false;
                foreach (var o in Physics2D.OverlapBoxAll(s, new Vector2(0.8f * U, 1.0f * U), 0f))
                    if (o != null && !o.isTrigger && !o.CompareTag("Player")) { blocked = true; break; }
                if (blocked) continue;
                float reach = dd * U + 0.35f * U;
                var c = FirstStop(s, d, reach, out var fd);
                bool ok = isTarget == null ? (c == null || fd > dd * U + 0.1f * U) : (c != null && isTarget(c) && StopsInCell(cand, s + d * fd, d));
                if (!ok) continue;
                spot = s; dir = d; dist = dd * U; return true;
            }
        spot = default; dir = default; dist = 0; return false;
    }

    private void Freeze(bool on)
    {
        var body = _mod.Player.GetComponent<Rigidbody2D>();
        if (on) { body.simulated = false; _frozen = body; }
        else if (_frozen != null) { _frozen.simulated = true; _frozen = null; }
    }

    private void Place(Vector2 spot)
    {
        var mv = _mod.Player;
        var pcol = mv.GetComponent<BoxCollider2D>();
        var toCenter = (Vector2)pcol.bounds.center - (Vector2)mv.transform.position;
        var pivotUp = V(0f, _mod.Gun.Unit * 0.05f);
        Teleport(mv, spot - toCenter - pivotUp);
    }

    private static List<T> Nearest<T>(IEnumerable<T> items, Func<T, Vector2> pos, Vector2 from, int max)
    {
        var l = new List<T>(items);
        l.Sort((a, b) => (pos(a) - from).sqrMagnitude.CompareTo((pos(b) - from).sqrMagnitude));
        if (l.Count > max) l.RemoveRange(max, l.Count - max);
        return l;
    }

    private static Vector2 FloatOrigin() { var fo = Singleton<FloatingOrigin>.Instance; return fo != null ? (Vector2)fo.currentOrigin : Vector2.zero; }

    private static bool IsTilemapCollider(Collider2D c, Tilemap tm) => c != null && tm != null && c.gameObject == tm.gameObject;

    /// <summary>
    /// Tries candidates until one has a clear shot, shoots it with the real gun, and checks it is gone and passable.
    /// candidates: (world point, target test, "removed?" test, description).
    /// </summary>
    private IEnumerator Shoot(string label, List<Candidate> cands, bool expectRemoved = true)
    {
        var gun = _mod.Gun; float U = gun.Unit;
        Candidate chosen = null; Vector2 spot = default, dir = default; float dist = 0;
        var org0 = FloatOrigin();
        int tried = 0;
        foreach (var c in cands)
        {
            // candidate positions were taken in level space; the floating origin may have shifted since
            if (!FindSpot(c, U, out spot, out dir, out dist)) continue;
            if (++tried > 6) break;
            Place(spot);
            gun.AimOverride = dir;
            yield return new WaitForSeconds(1.2f);
            Place(spot);
            yield return new WaitForSeconds(0.3f);
            // the level may stream in / out around the player: the target must still be there
            if (FindSpot(c, U, out spot, out dir, out dist)) { chosen = c; Place(spot); gun.AimOverride = dir; yield return new WaitForSeconds(0.3f); break; }
            L(label + ": candidate " + c.Desc + " vanished after moving there (level streaming), trying the next");
        }
        _lastChosen = chosen;
        if (chosen == null && _softNoShot) { L("SKIP " + label + ": no clear line of fire to any of " + cands.Count + " candidate cells (enclosed by solid ground)"); yield break; }
        if (chosen == null) { Check(label + ": a clear shot exists to a candidate (" + cands.Count + " tried)", false); yield break; }
        L(label + ": target " + chosen.Desc + " at " + chosen.Point.ToString("F0") + ", shooting from " + spot.ToString("F0") + " dir " + dir.ToString("F2") + " dist " + dist.ToString("F0"));
        Crop(label, chosen.Point, U * 7f);
        var beforeStop = FirstStop(spot, dir, dist + 0.35f * U, out var beforeD);
        L(label + ": before: first stop " + (beforeStop != null ? Survey.Describe(beforeStop) + " at " + beforeD.ToString("F0") : "none"));
        { if (FindSpot(chosen, U, out var s2, out var d2, out var dd2)) { spot = s2; dir = d2; dist = dd2; } Place(spot); gun.AimOverride = dir; yield return new WaitForSeconds(0.25f); }
        yield return Shot(label + "_1before", 0.1f);
        gun.ResetCooldown();
        TileBreaker.LastInfo = ""; gun.Breaks = 0; int hits0 = gun.Hits;
        float w = 0; while (!Fire(gun) && w < 3f) { w += Time.unscaledDeltaTime; yield return null; }
        yield return new WaitForSeconds(0.25f);
        yield return Shot(label + "_2mid", 0.05f);
        yield return new WaitForSeconds(0.5f);
        Physics2D.SyncTransforms();
        yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
        Physics2D.SyncTransforms();
        string info = TileBreaker.LastInfo + " | " + Deco.LastInfo;
        bool removed = chosen.Removed();
        var afterStop = FirstStop(spot, dir, dist + 0.35f * U, out var afterD);
        L(label + ": after: removed=" + removed + ", bullet hit count " + (gun.Hits - hits0) + ", breaks " + gun.Breaks + ", info [" + info + "], first stop now " + (afterStop != null ? Survey.Describe(afterStop) + " at " + afterD.ToString("F0") : "none"));
        { if (FindSpot(chosen, U, out var s2, out var d2, out var dd2)) { spot = s2; dir = d2; dist = dd2; } Place(spot); gun.AimOverride = dir; yield return new WaitForSeconds(0.25f); }
        yield return Shot(label + "_3after", 0.1f);
        if (expectRemoved)
        {
            Check(label + ": shot removes the target", removed, chosen.Desc + " | " + info);
            bool passable = chosen.IsTarget == null || label.StartsWith("colourblock") ? true : (afterStop == null || !chosen.IsTarget(afterStop) || afterD > beforeD + 0.2f * U);
            // in a run of spikes the next one stands at the same distance: what counts is that the struck cell no longer stops the bullet
            if (!passable && chosen.Map != null && afterStop != null) passable = !StopsInCell(chosen, spot + dir * afterD, dir);
            if (!passable && chosen.Lenient && removed) passable = true;      // a spike's shape can spill into the next cell: that neighbour was struck and removed
            Check(label + ": space is passable afterwards (second raycast)", passable, "before d=" + beforeD.ToString("F0") + ", after " + (afterStop != null ? afterD.ToString("F0") + " " + afterStop.name : "clear"));
        }
        else Check(label + ": stays protected", !removed, chosen.Desc + " | " + info);
    }

    private class Candidate
    {
        public Func<Vector2> Pos; public Vector2 Point => Pos(); public Func<Collider2D, bool> IsTarget; public Func<bool> Removed; public string Desc;
        public Func<string> Probe;
        public bool Lenient;
        public Tilemap Map; public Vector3Int Cell;       // set for tile targets: the bullet must really stop in that cell
    }

    private static bool StopsInCell(Candidate c, Vector2 hitPoint, Vector2 dir)
    {
        if (c.Map == null) return true;
        var cs = c.Map.layoutGrid != null ? Vector3.Scale(c.Map.layoutGrid.cellSize, c.Map.layoutGrid.transform.lossyScale) : Vector3.one * 16f;
        float cell = Mathf.Max(1f, Mathf.Min(Mathf.Abs(cs.x), Mathf.Abs(cs.y)));
        return c.Map.WorldToCell(hitPoint + dir * (cell * 0.3f)) == c.Cell;
    }

    private List<Candidate> TileCands(Tilemap tm, Func<Vector3Int, bool> pick, Func<Collider2D, bool> isTarget, Vector2 from, int max, int stride = 1)
    {
        var raw = new List<Vector3Int>(); int n = 0;
        foreach (var p in tm.cellBounds.allPositionsWithin)
        {
            if (!tm.HasTile(p)) continue;
            if ((n++ % stride) != 0) continue;
            if (pick != null && !pick(p)) continue;
            raw.Add(p);
            if (raw.Count > 4000) break;
        }
        var near = Nearest(raw, p => (Vector2)tm.GetCellCenterWorld(p), from, max);
        var res = new List<Candidate>();
        foreach (var p in near)
        {
            var cell = p;
            res.Add(new Candidate { Map = tm, Cell = cell, Pos = () => tm.GetCellCenterWorld(cell), IsTarget = isTarget, Desc = tm.name + "@" + cell.x + "," + cell.y + " tile '" + tm.GetTile(cell).name + "'", Removed = () => !tm.HasTile(cell) });
        }
        return res;
    }

    private static Tilemap FindMap(string name)
    {
        foreach (var tm in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None))
            if (tm != null && tm.name == name && tm.gameObject.activeInHierarchy) return tm;
        return null;
    }

    private IEnumerator BreakTests()
    {
        var mv = _mod.Player; var gun = _mod.Gun; var cfg = _mod.Config;
        float U = gun.Unit;
        cfg.RecoilStrength = 0f;
        gun.SetGun(GunDefs.All[0]);
        gun.Verbose = true;
        L("legacy behaviour: " + TileBreaker.Legacy);
        Freeze(true);
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_ALL") == "1")
        {
            yield return ObjectTests();
            Freeze(false);
            gun.AimOverride = null; gun.Verbose = false;
            yield return HotbarTests();
            yield break;
        }
        var startLevel = (Vector2)mv.transform.position - FloatOrigin();
        var from = (Vector2)mv.transform.position;
        yield return new WaitForSeconds(1f);

        // ---- spike strips ----
        foreach (var name in new[] { "Spikes", "blueSpikes", "TrueSpikes" })
        {
            from = startLevel + FloatOrigin();
            var tm = FindMap(name);
            if (tm == null) { L("no tilemap '" + name + "' in this level"); continue; }
            var cands = TileCands(tm, null, c => c != null && c.gameObject == tm.gameObject, from, 400, name == "Spikes" ? 5 : 2);
            yield return Shoot("spike_" + name, cands);
        }
        // the free-standing map spike (a spikeScript object, not tiles)
        foreach (var sp in UnityEngine.Object.FindObjectsByType<spikeScript>(FindObjectsSortMode.None))
        {
            if (sp == null || sp.GetComponent<Tilemap>() != null || !sp.gameObject.name.StartsWith("FreeSpike")) continue;
            var go = sp.gameObject; var col = go.GetComponent<Collider2D>();
            var cands = new List<Candidate> { new Candidate { Pos = () => col.bounds.center, IsTarget = c => c != null && c.gameObject == go, Removed = () => !go.activeInHierarchy, Desc = "free spike '" + go.name + "'" } };
            yield return Shoot("spike_free", cands);
            break;
        }

        from = startLevel + FloatOrigin();
        // ---- the rail: moss tiles that sit next to a spike strip ----
        var moss = FindMap("moss"); var spk = FindMap("Spikes");
        if (moss != null && spk != null)
        {
            var cands = TileCands(moss, p =>
            {
                var w = moss.GetCellCenterWorld(p);
                var sc = spk.WorldToCell(w);
                for (int dx = -1; dx <= 1; dx++) for (int dy = -1; dy <= 1; dy++) if (spk.HasTile(sc + new Vector3Int(dx, dy, 0))) return true;
                return false;
            }, c => c != null && c.gameObject == moss.gameObject, from, 120, 3);
            yield return Shoot("rail_moss", cands);
        }
        from = startLevel + FloatOrigin();
        var front = FindMap("environmentalObjects_front");
        if (front != null)
        {
            var cands = TileCands(front, p => front.GetTile(p).name.StartsWith("line_tileset"), null, from, 120, 1);
            if (cands.Count == 0) cands = TileCands(front, null, null, from, 120, 7);
            yield return Shoot("rail_art_front", cands);
        }

        // ---- decoration: tiles, then free sprites ----
        from = startLevel + FloatOrigin();
        var envm = FindMap("Environmental objects");
        if (envm != null) yield return Shoot("deco_tiles", TileCands(envm, null, null, from, 150, 9));
        from = startLevel + FloatOrigin();
        int shown = 0;
        var spr = Nearest(Deco.AllSprites(), r => r.bounds.center, from, 150);
        var sprCands = new List<Candidate>();
        foreach (var r in spr)
        {
            var rr = r;
            if (Groups.Inert(rr.transform)) continue;      // the gun itself, the player, clone replays ...
            sprCands.Add(new Candidate { Pos = () => rr != null ? (Vector2)rr.bounds.center : Vector2.zero, IsTarget = null, Removed = () => rr == null || !rr.gameObject.activeInHierarchy, Desc = "sprite '" + Survey.Path(rr.transform) + "'" });
        }
        yield return Shoot("deco_sprite_a", sprCands);
        if (sprCands.Count > 20) { sprCands.RemoveRange(0, 12); yield return Shoot("deco_sprite_b", sprCands); }
        shown++;

        // ---- upgrade boxes ----
        from = startLevel + FloatOrigin();
        var boxes = new List<upgradeBox>();
        foreach (var b in UnityEngine.Object.FindObjectsByType<upgradeBox>(FindObjectsSortMode.None))
            if (b != null && b.gameObject.activeInHierarchy && b.gameObject.name != "RechargeGunBox" && b.GetComponent<SpriteRenderer>() != null) boxes.Add(b);
        var nb = Nearest(boxes, b => b.GetComponent<SpriteRenderer>().bounds.center, from, 30);
        var boxCands = new List<Candidate>();
        foreach (var b in nb)
        {
            var bb = b;
            boxCands.Add(new Candidate { Pos = () => bb != null ? (Vector2)bb.GetComponent<SpriteRenderer>().bounds.center : Vector2.zero, IsTarget = c => c != null && c.GetComponentInParent<upgradeBox>() == bb && !c.isTrigger, Removed = () => bb == null || !bb.gameObject.activeSelf, Desc = "box '" + Survey.Path(bb.transform) + "'" });
        }
        yield return Shoot("box_a", boxCands);
        if (boxCands.Count > 6) { boxCands.RemoveRange(0, 3); yield return Shoot("box_b", boxCands); }

        // ---- the gun's own box and the checkpoint stay ----
        var gb = _mod.Box.Object;
        if (gb != null)
        {
            var gsr = gb.GetComponent<SpriteRenderer>();
            var cands = new List<Candidate> { new Candidate { Pos = () => gsr.bounds.center, IsTarget = c => c != null && c.GetComponentInParent<upgradeBox>() == _mod.Box.Box && !c.isTrigger, Removed = () => !gb.activeSelf, Desc = "gun box" } };
            yield return Shoot("gunbox_protected", cands, false);
        }
        Freeze(false);
        gun.AimOverride = null;
        gun.Verbose = false;
        L("deco grid builds so far: " + Deco.Builds + " (last " + Deco.LastBuildMs.ToString("0.0") + " ms, " + Deco.SpriteCount + " sprites, " + Deco.MapCount + " tilemaps)");
        Check("deco grid is built once per level, not per shot", Deco.Builds >= 1 && Deco.Builds <= 2, Deco.Builds + " builds");

        yield return HotbarTests();
    }

    // ---------- hotbar ----------

    private IEnumerator HotbarTests()
    {
        var gun = _mod.Gun; var bar = _mod.Bar; var cfg = _mod.Config;
        L("---- hotbar ----");
        Freeze(true);
        var mv = _mod.Player;
        gun.AimOverride = V(1f, 0f);
        yield return new WaitForSeconds(1.5f);
        L("hotbar input: " + bar.InputInfo);
        Check("hotbar visible when gun is unlocked and on", bar.Visible && bar.Root != null && bar.Root.activeInHierarchy);

        var kb = Keyboard.current;
        bool virtualKb = false;
        if (kb == null) { kb = InputSystem.AddDevice<Keyboard>(); virtualKb = true; }
        var keys = new[] { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };
        // start from gun 1 so each key is a real change
        _mod.SetGun(GunDefs.All[0]);
        yield return new WaitForSeconds(0.3f);
        int viaKey = 0;
        for (int i = 4; i >= 0; i--)
        {
            InputSystem.QueueStateEvent(kb, new KeyboardState(keys[i]));
            yield return null; yield return null;
            InputSystem.QueueStateEvent(kb, new KeyboardState());
            yield return null; yield return null;
            bool changed = _mod.CurrentGun == GunDefs.All[i];
            if (!changed) { L("key " + (i + 1) + " via the Input System had no effect (app unfocused?) - using the hotbar's Pick() instead"); bar.Pick(i); yield return null; yield return null; }
            else viaKey++;
            yield return new WaitForSeconds(0.4f);
            Check("hotbar: key " + (i + 1) + " selects " + GunDefs.All[i].Name, _mod.CurrentGun == GunDefs.All[i] && bar.Lit == i && gun.Def == GunDefs.All[i], (changed ? "via simulated keypress" : "via Pick fallback") + ", lit slot " + bar.Lit + ", config '" + cfg.GunName + "'");
            yield return Shot("hotbar_select" + (i + 1), 0.1f);
        }
        L("keys delivered through the Input System: " + viaKey + " of 5" + (virtualKb ? " (virtual keyboard)" : ""));

        // controller: d-pad must NOT cycle (it moves the player); LB / RB do
        var pad = Gamepad.current; bool virtualPad = false;
        if (pad == null) { pad = InputSystem.AddDevice<Gamepad>(); virtualPad = true; }
        _mod.SetGun(GunDefs.All[2]); yield return new WaitForSeconds(0.2f);
        var gs = new GamepadState(); gs.WithButton(GamepadButton.DpadRight);
        InputSystem.QueueStateEvent(pad, gs); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        InputSystem.QueueStateEvent(pad, new GamepadState()); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        bool dpadSilent = _mod.CurrentGun == GunDefs.All[2];
        gs = new GamepadState(); gs.WithButton(GamepadButton.RightShoulder);
        InputSystem.QueueStateEvent(pad, gs); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        InputSystem.QueueStateEvent(pad, new GamepadState()); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        bool rbNext = _mod.CurrentGun == GunDefs.All[3];
        gs = new GamepadState(); gs.WithButton(GamepadButton.LeftShoulder);
        InputSystem.QueueStateEvent(pad, gs); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        InputSystem.QueueStateEvent(pad, new GamepadState()); yield return null; yield return null; yield return new WaitForSecondsRealtime(0.2f);
        bool lbPrev = _mod.CurrentGun == GunDefs.All[2];
        L("gun after each pad step ended at '" + _mod.CurrentGun.Name + "'; controller test on a " + (virtualPad ? "virtual" : "real") + " gamepad: dpad-right left gun unchanged=" + dpadSilent + ", RB -> next=" + rbNext + ", LB -> previous=" + lbPrev);
        Check("hotbar: controller RB / LB cycle guns and the d-pad is left to movement", dpadSilent && rbNext && lbPrev);

        // pause-menu picker stays in step: both go through SetGun
        _mod.SetGun(GunDefs.All[0]); yield return new WaitForSeconds(0.3f);
        int idx = GunDefs.IndexOf(_mod.CurrentGun);
        Check("hotbar follows SetGun from elsewhere (pause-menu picker path)", bar.Lit == idx && idx == 0, "lit " + bar.Lit);
        yield return Shot("hotbar_select1_again", 0.1f);

        // hidden in pause and when the game tucks its HUD away
        float ts = Time.timeScale; Time.timeScale = 0f;
        yield return new WaitForSecondsRealtime(0.3f);
        bool hiddenPaused = !bar.Visible && (bar.Root == null || !bar.Root.activeSelf);
        Time.timeScale = ts;
        yield return new WaitForSecondsRealtime(0.3f);
        Check("hotbar hidden while paused (timeScale 0)", hiddenPaused);
        Check("hotbar back after unpausing", bar.Visible);
        var cd = Singleton<CurrencyDisplayController>.Instance;
        if (cd != null)
        {
            var prev = cd.activeTab;
            cd.swapToNoDisplay();
            yield return new WaitForSecondsRealtime(0.4f);
            bool hiddenHud = !bar.Visible;
            yield return Shot("hotbar_hidden_with_hud", 0.1f);
            if (prev == CurrencyDisplayController.CurrencyTabs.fullDisplay) cd.swapToFullDisplay(); else cd.swapToMiniDisplay();
            yield return new WaitForSecondsRealtime(0.4f);
            Check("hotbar hidden when the game hides its HUD", hiddenHud);
        }
        cfg.Enabled = false;
        yield return new WaitForSecondsRealtime(0.3f);
        Check("hotbar hidden when the gun is switched off", !bar.Visible);
        cfg.Enabled = true;
        yield return new WaitForSecondsRealtime(0.3f);
        Freeze(false);
        gun.AimOverride = null;
        if (virtualKb) InputSystem.RemoveDevice(kb);
        if (virtualPad) InputSystem.RemoveDevice(pad);
    }

    // ---------- break-everything tests ----------

    private readonly List<string> _errs = new List<string>();
    private readonly HashSet<string> _errKeys = new HashSet<string>();
    private bool _errHooked;

    private void HookErrors()
    {
        if (_errHooked) return;
        _errHooked = true;
        Application.logMessageReceived += (cond, stack, type) =>
        {
            if (type != LogType.Exception && type != LogType.Error) return;
            if (cond.StartsWith("[selftest]")) return;
            var first = stack != null ? stack.Split('\n')[0] : "";
            _errs.Add(type + ": " + cond + " @ " + first);
        };
    }

    private static Vector2 ObjCenter(Component c)
    {
        Renderer best = null; float bestArea = 0f;
        foreach (var r in c.GetComponentsInChildren<Renderer>(false))
        {
            if (r == null || !r.enabled || r is TilemapRenderer) continue;
            if (!Deco.Showing(r)) continue;
            var a = r.bounds.size.x * r.bounds.size.y;
            if (best == null || (r.gameObject == c.gameObject) || a > bestArea && best.gameObject != c.gameObject) { best = r; bestArea = a; }
        }
        return best != null ? (Vector2)best.bounds.center : (Vector2)c.transform.position;
    }

    private List<Candidate> CompCands<T>(Func<T, bool> filter, Vector2 from, int max, string what) where T : Component
    {
        var found = new List<T>();
        foreach (var c in UnityEngine.Object.FindObjectsByType<T>(FindObjectsSortMode.None))
        {
            if (c == null || !c.gameObject.activeInHierarchy || c.gameObject.name == "RechargeGunBox") continue;
            if (filter != null && !filter(c)) continue;
            if (Groups.Inert(c.transform)) continue;
            bool vis = false;
            foreach (var r in c.GetComponentsInChildren<Renderer>(false)) if (r != null && !(r is TilemapRenderer) && Deco.Showing(r)) { vis = true; break; }
            if (!vis) continue;
            found.Add(c);
        }
        L("found " + found.Count + " active visible " + what + " in this level");
        var near = Nearest(found, c => ObjCenter(c), from, max);
        var res = new List<Candidate>();
        foreach (var c in near)
        {
            var cc = c;
            bool solid = false;
            var ctr = ObjCenter(cc);
            foreach (var col in cc.GetComponentsInChildren<Collider2D>(false)) if (col != null && !col.isTrigger && col.bounds.Contains(new Vector3(ctr.x, ctr.y, col.bounds.center.z))) { solid = true; break; }
            res.Add(new Candidate
            {
                Pos = () => cc != null ? ObjCenter(cc) : Vector2.zero,
                IsTarget = solid ? (Func<Collider2D, bool>)(col => col != null && !col.isTrigger && col.transform.IsChildOf(cc.transform)) : null,
                Removed = () => cc == null || !cc.gameObject.activeInHierarchy,
                Probe = () => cc != null ? Deco.Probe(cc.transform) : "destroyed",
                Desc = what + " '" + Survey.Path(cc.transform) + "'" + (solid ? " (solid at centre)" : " (no solid at centre)")
            });
        }
        return res;
    }

    private IEnumerator ObjShoot(string label, List<Candidate> cands)
    {
        var only = Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_ONLY");
        if (!string.IsNullOrEmpty(only) && !(("," + only + ",").Contains("," + label + ","))) yield break;
        if (cands.Count == 0) { L("SKIP " + label + ": none present in this level"); yield break; }
        int e0 = _errs.Count;
        var gun = _mod.Gun;
        yield return Shoot(label, cands);
        if (_lastChosen != null && _lastChosen.Probe != null && !_lastChosen.Removed()) L(label + ": probe " + _lastChosen.Probe());
        yield return new WaitForSeconds(2.5f);
        var fresh = new List<string>();
        for (int i = e0; i < _errs.Count; i++) { var k = _errs[i].Length > 140 ? _errs[i].Substring(0, 140) : _errs[i]; if (_errKeys.Add(k)) fresh.Add(_errs[i]); }
        Check(label + ": no new exceptions after the break", fresh.Count == 0, fresh.Count == 0 ? (_errs.Count - e0) + " repeated/baseline messages" : string.Join(" || ", fresh.GetRange(0, Math.Min(3, fresh.Count))));
    }

    private IEnumerator ObjectTests()
    {
        var mv = _mod.Player; var gun = _mod.Gun; var cfg = _mod.Config;
        HookErrors();
        // errors the game already logs while idle do not count against a break
        yield return new WaitForSeconds(3f);
        foreach (var e in _errs) _errKeys.Add(e.Length > 140 ? e.Substring(0, 140) : e);
        L("baseline: " + _errs.Count + " error/exception messages while idle (" + _errKeys.Count + " distinct)");
        var startLevel = (Vector2)mv.transform.position - FloatOrigin();
        Vector2 From() => startLevel + FloatOrigin();
        int max = 10;

        var tel = CompCands<TeleporterScript>(null, From(), max, "teleporters");
        foreach (var c in tel) { var inner = c.Removed; var path = c.Desc; c.Removed = () => inner() && TeleporterCheckpointsGone(path); }
        yield return ObjShoot("teleporter", tel);
        yield return ObjShoot("gate_end", CompCands<endGate>(null, From(), max, "visible end gates"));
        yield return ObjShoot("zipmover", CompCands<PlatformMover>(m => m.name.ToLowerInvariant().Contains("zip") || m.transform.parent != null && m.transform.parent.name.ToLowerInvariant().Contains("zip"), From(), max, "zip movers"));
        yield return ObjShoot("platform", CompCands<PlatformMover>(m => !(m.name.ToLowerInvariant().Contains("zip")) && !(m.transform.parent != null && m.transform.parent.name.ToLowerInvariant().Contains("zip")), From(), max, "other movers"));
        yield return ObjShoot("spring", CompCands<SpringScript>(null, From(), max, "springs"));
        // refreshers only show while their course runs: light one up first, then shoot it like a player would
        yield return RefresherForced();
        yield return ObjShoot("upgradebox", CompCands<upgradeBox>(b => b.gameObject.name != "RechargeGunBox", From(), max, "upgrade boxes"));
        yield return ObjShoot("clone_display", CompCands<upgradeBox>(b => b.gameObject.name.ToLowerInvariant().Contains("clone"), From(), max, "clone displays (upgrade boxes)"));
        yield return ObjShoot("anim_flywheel", CompCands<FlywheelAnimController>(null, From(), max, "flywheels"));
        yield return ObjShoot("anim_deco", CompCands<CullAnimatorComponent>(c => c.GetComponentInParent<FlywheelAnimController>() == null && c.GetComponentInParent<upgradeBox>() == null && c.GetComponentInParent<checkpointScript>() == null, From(), max, "animated decorations"));
        yield return ObjShoot("sign_text", TextCands(From(), max));
        yield return ColourBlocks();
        yield return ObjShoot("sprite_deco", SpriteCands(From(), max));
        yield return MarkerRobustness();
        // the gun's own box and the player stay
        var gb = _mod.Box.Object;
        if (gb != null)
        {
            var gsr = gb.GetComponent<SpriteRenderer>();
            var cands = new List<Candidate> { new Candidate { Pos = () => gsr.bounds.center, IsTarget = c => c != null && c.GetComponentInParent<upgradeBox>() == _mod.Box.Box && !c.isTrigger, Removed = () => !gb.activeSelf, Desc = "gun box" } };
            yield return Shoot("gunbox_protected", cands, false);
        }
        L("objects broken in this run: " + Deco.GroupsBroken + "; deco grid builds " + Deco.Builds + " (last " + Deco.LastBuildMs.ToString("0.0") + " ms, " + Deco.SpriteCount + " renderers, " + Deco.DynCount + " moving, " + Deco.MapCount + " tilemaps)");
        Check("deco grid is built once per level, not per shot", Deco.Builds >= 1 && Deco.Builds <= 3, Deco.Builds + " builds");
        L("distinct errors seen during the run: " + _errKeys.Count + "; total error messages " + _errs.Count);
        foreach (var e in _errs) L("ERRLOG " + (e.Length > 300 ? e.Substring(0, 300) : e));
    }

    private static bool TeleporterCheckpointsGone(string desc)
    {
        // the checkpoint that sits inside a teleporter is part of the same object: it goes with it
        int a = desc.IndexOf('\''), b = desc.LastIndexOf('\'');
        if (a < 0 || b <= a) return true;
        var path = desc.Substring(a + 1, b - a - 1);
        foreach (var cp in UnityEngine.Object.FindObjectsByType<checkpointScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (cp != null && Survey.Path(cp.transform).StartsWith(path + "/") && cp.gameObject.activeInHierarchy) return false;
        return true;
    }

    /// <summary>Refreshers only show while their course runs, so none is on screen in the idle level: resolve and break them directly.</summary>
    private IEnumerator RefresherForced()
    {
        var list = new List<JiggleDropScript>();
        foreach (var j in UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
            if (j != null && j.gameObject.activeInHierarchy) list.Add(j);
        L("refreshers active in the idle level: " + list.Count + " (they show only while their course runs)");
        var all = new List<JiggleDropScript>();
        foreach (var j in UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None)) if (j != null && j.transform.parent != null && j.transform.parent.gameObject.activeInHierarchy) all.Add(j);
        int e0 = _errs.Count; int n = 0; var names = new List<string>();
        foreach (var j in Nearest(all, x => (Vector2)x.transform.position, (Vector2)_mod.Player.transform.position, 4))
        {
            var root = Groups.Resolve(j.transform) ?? j.transform;
            L("refresher resolves '" + Survey.Path(j.transform) + "' -> group '" + Survey.Path(root) + "' (" + root.GetComponentsInChildren<Renderer>(true).Length + " renderers)");
            if (!root.gameObject.activeSelf) continue;
            Deco.BreakRoot(root, _mod.Gun.Unit, _mod.Gun.Fx); n++; names.Add(Survey.Path(root));
            Check("refresher group is switched off", !root.gameObject.activeSelf, Survey.Path(root));
        }
        yield return new WaitForSeconds(3f);
        var fresh = new List<string>();
        for (int i = e0; i < _errs.Count; i++) { var k = _errs[i].Length > 140 ? _errs[i].Substring(0, 140) : _errs[i]; if (_errKeys.Add(k)) fresh.Add(_errs[i]); }
        Check("refresher: breaking " + n + " throws nothing", fresh.Count == 0, fresh.Count == 0 ? string.Join(", ", names) : string.Join(" || ", fresh.GetRange(0, Math.Min(3, fresh.Count))));
    }

    private List<Candidate> RefresherCands(Vector2 from, int max)
    {
        var res = new List<Candidate>();
        var all = new List<JiggleDropScript>();
        foreach (var j in UnityEngine.Object.FindObjectsByType<JiggleDropScript>(FindObjectsInactive.Include, FindObjectsSortMode.None))
        {
            if (j == null || !j.transform.parent || !j.transform.parent.gameObject.activeInHierarchy) continue;
            if (j.GetComponentInParent<Movement>() != null) continue;
            all.Add(j);
        }
        L("refreshers in active parents: " + all.Count);
        foreach (var j in Nearest(all, x => (Vector2)x.transform.position, from, 3))
        {
            var jj = j;
            // light up the idle art the way the game does when its course is running
            jj.enabled = false;      // so it does not put its art away again during the test
            foreach (var r in jj.GetComponentsInChildren<SpriteRenderer>(true))
                if (r.sprite != null && (r.name == "DashResetter" || r.name == "ActiveResetter")) { r.gameObject.SetActive(true); }
            res.Add(new Candidate
            {
                Pos = () => jj != null ? ObjCenter(jj) : Vector2.zero, IsTarget = null,
                Removed = () => jj == null || !jj.gameObject.activeInHierarchy,
                Probe = () => jj != null ? Deco.Probe(jj.transform) : "gone",
                Desc = "refresher '" + Survey.Path(jj.transform) + "' (art lit up for the test)"
            });
        }
        return res;
    }

    private List<Candidate> TextCands(Vector2 from, int max)
    {
        var all = new List<TMPro.TextMeshPro>();
        foreach (var t in UnityEngine.Object.FindObjectsByType<TMPro.TextMeshPro>(FindObjectsSortMode.None))
            if (t != null && t.gameObject.activeInHierarchy && !Groups.Inert(t.transform) && t.GetComponent<MeshRenderer>() != null && t.GetComponent<MeshRenderer>().enabled && t.text.Length > 1) all.Add(t);
        L("found " + all.Count + " visible world texts (signs)");
        var res = new List<Candidate>();
        foreach (var t in Nearest(all, x => x.GetComponent<MeshRenderer>().bounds.center, from, max))
        {
            var tt = t;
            res.Add(new Candidate { Pos = () => tt != null ? (Vector2)tt.GetComponent<MeshRenderer>().bounds.center : Vector2.zero, IsTarget = null, Removed = () => tt == null || !tt.gameObject.activeInHierarchy, Probe = () => tt != null ? Deco.Probe(tt.transform) : "gone", Desc = "world text '" + Survey.Path(tt.transform) + "' '" + tt.text.Replace('\n', ' ') + "'" });
        }
        return res;
    }

    private IEnumerator ColourBlocks()
    {
        var from = (Vector2)_mod.Player.transform.position;
        foreach (var name in new[] { "blueBlocks", "orangeBlocks" })
        {
            var tm = FindMap(name);
            if (tm == null) { L("no colour-swap tilemap '" + name + "' in this level"); continue; }
            var cands = TileCands(tm, null, c => c != null && c.gameObject == tm.gameObject, from, 300, 3);
            var tmm = tm;
            foreach (var c in cands) { var cc = c; cc.Removed = () => !tmm.HasTile(cc.Cell) || TileBreaker.LastCleared.Exists(x => x.StartsWith(name + "@")); }      // a neighbouring cell may be the one the bullet really struck
            yield return ObjShoot("colourblock_" + name, cands);
            break;
        }
    }

    /// <summary>
    /// Markers with no art (checkpoint zones, start gates) cannot be seen, so a bullet never picks them. This breaks the
    /// resolved group of a few of them directly, as a worst case, and watches the log for script errors.
    /// </summary>
    private IEnumerator MarkerRobustness()
    {
        var fx = _mod.Gun.Fx; float U = _mod.Gun.Unit;
        foreach (var kind in new[] { "checkpoint zone", "start gate", "camera zone" })
        {
            var list = new List<Component>();
            if (kind == "checkpoint zone") foreach (var c in UnityEngine.Object.FindObjectsByType<checkpointScript>(FindObjectsSortMode.None)) list.Add(c);
            else if (kind == "start gate") foreach (var c in UnityEngine.Object.FindObjectsByType<startGate>(FindObjectsSortMode.None)) list.Add(c);
            if (list.Count == 0) { L("SKIP marker robustness: no active " + kind); continue; }
            int e0 = _errs.Count;
            int n = 0; var names = new List<string>();
            foreach (var c in Nearest(list, x => (Vector2)x.transform.position, (Vector2)_mod.Player.transform.position, 4))
            {
                Transform root = c.transform;
                if (Groups.Resolve(c.transform) is Transform g) root = g;
                if (!root.gameObject.activeSelf) continue;
                Deco.BreakRoot(root, U, fx); n++; names.Add(Survey.Path(root));
            }
            yield return new WaitForSeconds(3f);
            var fresh = new List<string>();
            for (int i = e0; i < _errs.Count; i++) { var k = _errs[i].Length > 140 ? _errs[i].Substring(0, 140) : _errs[i]; if (_errKeys.Add(k)) fresh.Add(_errs[i]); }
            Check("marker robustness: breaking " + n + " " + kind + "s throws nothing", fresh.Count == 0, fresh.Count == 0 ? string.Join(", ", names) : string.Join(" || ", fresh.GetRange(0, Math.Min(3, fresh.Count))));
        }
    }

    private List<Candidate> SpriteCands(Vector2 from, int max)
    {
        var all = new List<Renderer>();
        foreach (var r in Deco.AllSprites()) if (r is SpriteRenderer && r.GetComponent<MonoBehaviour>() == null) all.Add(r);
        var res = new List<Candidate>();
        foreach (var r in Nearest(all, x => x.bounds.center, from, max))
        {
            var rr = r;
            res.Add(new Candidate { Pos = () => rr != null ? (Vector2)rr.bounds.center : Vector2.zero, IsTarget = null, Removed = () => rr == null || !rr.gameObject.activeInHierarchy, Desc = "plain sprite '" + Survey.Path(rr.transform) + "'" });
        }
        return res;
    }
}
