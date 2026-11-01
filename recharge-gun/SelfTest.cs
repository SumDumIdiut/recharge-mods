using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Text;
using UnityEngine;
using UnityEngine.SceneManagement;
using UnityEngine.Tilemaps;

/// <summary>
/// Scripted in-game proof, only when the environment variable RECHARGE_GUN_SELFTEST=1 is set (a no-op
/// otherwise): starts the game from the title screen, buys the box by touching it, fires a scripted
/// sequence (down in the air, sideways, up, then a wall and the floor to break tiles), logs everything
/// and writes screenshots plus a summary into the mod's data folder.
/// </summary>
internal partial class SelfTest
{
    private readonly GunMod _mod;
    private Runner _runner;
    private string _dir;
    private readonly List<string> _results = new List<string>();
    private readonly StringBuilder _report = new StringBuilder();

    private class Runner : MonoBehaviour { }

    internal SelfTest(GunMod mod) { _mod = mod; }

    internal void Dispose()
    {
        if (_runner != null) UnityEngine.Object.Destroy(_runner.gameObject);
    }

    internal void Begin()
    {
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST") != "1" && Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_MENU") != "1" && Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_BREAK") != "1") return;
        try { AudioListener.volume = 0f; } catch { }
        _dir = Path.Combine(_mod.Host.ModDataDir(_mod.Id), "selftest");
        Directory.CreateDirectory(_dir);
        var go = new GameObject("RechargeGunSelfTest");
        UnityEngine.Object.DontDestroyOnLoad(go);
        _runner = go.AddComponent<Runner>();
        _runner.StartCoroutine(Run());
        _mod.Host.Log("SELFTEST enabled, output in " + _dir);
    }

    private void L(string m)
    {
        _mod.Host.Log("[selftest] " + m);
        _report.AppendLine(m);
    }

    private void Check(string name, bool ok, string detail = "")
    {
        _results.Add((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " - " + detail : ""));
        L((ok ? "PASS " : "FAIL ") + name + (detail.Length > 0 ? " - " + detail : ""));
    }

    private IEnumerator Shot(string name, float after = 0.4f)
    {
        yield return new WaitForEndOfFrame();
        var path = Path.Combine(_dir, name + ".png");
        try { ScreenCapture.CaptureScreenshot(path); L("screenshot " + path); }
        catch (Exception e) { L("screenshot failed: " + e.Message); }
        if (after > 0f) yield return new WaitForSeconds(after);
    }

    private static Vector2 V(float x, float y) => new Vector2(x, y);

    /// <summary>Logs a crop window (screen pixels, y from the top) around a world point for the screenshot post-processing.</summary>
    private void Crop(string name, Vector2 world, float worldHalfWidth)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var a = (Vector2)cam.WorldToScreenPoint(world);
        var b = (Vector2)cam.WorldToScreenPoint(world + V(worldHalfWidth, 0f));
        L("CROP " + name + " " + a.x.ToString("0") + " " + (Screen.height - a.y).ToString("0") + " " + Mathf.Abs(b.x - a.x).ToString("0") + " screen " + Screen.width + "x" + Screen.height);
    }

    /// <summary>The gun box at the end of its row of upgrade boxes: stand under it and screenshot.</summary>
    private IEnumerator BoxRowShot(string label)
    {
        var mv = _mod.Player;
        var go = _mod.Box.Object;
        if (mv == null || go == null) yield break;
        var gb = _mod.Box;
        L(label + ": gun box placement: inRow=" + gb.InRow + ", " + gb.RowInfo + ", bottom delta " + gb.RowBottomDelta.ToString("0.###"));
        var sr = go.GetComponent<SpriteRenderer>();
        var bounds = sr != null ? sr.bounds : new Bounds(go.transform.position, Vector3.one * 50f);
        var pcol = mv.GetComponent<BoxCollider2D>();
        float half = pcol != null ? pcol.bounds.extents.y : 30f;
        // in the air just above the gun box (floors around the spawn can be spiked, and standing under the box would buy it)
        var spot = V(bounds.center.x, bounds.max.y + half + 30f);
        var keep = (Vector2)mv.transform.position;
        Teleport(mv, spot);
        yield return new WaitForSeconds(0.35f);
        Crop("box_row_" + label.Replace(' ', '_'), bounds.center, bounds.size.x * 4f);
        yield return Shot("box_row_" + label.Replace(' ', '_'));
        Teleport(mv, keep);
        yield return new WaitForSeconds(1f);
    }

    private void Teleport(Movement mv, Vector2 pos)
    {
        mv.transform.position = new Vector3(pos.x, pos.y, mv.transform.position.z);
        var body = mv.GetComponent<Rigidbody2D>();
        if (body != null) { body.position = pos; body.linearVelocity = Vector2.zero; }
        mv.Velocity = Vector2.zero; mv.momentum = Vector2.zero; mv.movingPlatformVelocity = Vector2.zero;
        try { if (mv.cam != null && Camera.main != null) { mv.cam.setup(mv.transform.position, Camera.main.orthographicSize); mv.cam.newTarget(mv.gameObject, mv.cam.defaultoffset, true, Vector2.zero); } } catch { }
        Physics2D.SyncTransforms();
    }

    private RaycastHit2D Ray(Vector2 from, Vector2 dir, float dist, bool skipBoxes = true)
    {
        var mv = _mod.Player;
        var filter = new ContactFilter2D { useTriggers = false };
        filter.SetLayerMask(Physics2D.GetLayerCollisionMask(mv.gameObject.layer));
        var res = new RaycastHit2D[8];
        int n = Physics2D.Raycast(from, dir, filter, res, dist);
        for (int i = 0; i < n; i++)
            if (res[i].collider != null && !res[i].collider.CompareTag("Player") && (!skipBoxes || res[i].collider.GetComponentInParent<upgradeBox>() == null)) return res[i];
        return default;
    }

    private void Dump(Movement mv)
    {
        L("---- scene dump ----");
        L("scene '" + SceneManager.GetActiveScene().name + "', player " + (Vector2)mv.transform.position + ", respawnPoint " + mv.respawnPoint + ", origin " + Singleton<FloatingOrigin>.Instance.currentOrigin);
        var col = mv.GetComponent<BoxCollider2D>();
        L("player collider size " + (col != null ? col.size.ToString() : "none") + " lossy " + mv.transform.lossyScale + ", runSpeed " + mv.runSpeed + ", jumpForce " + mv.jumpForce + ", gravity " + mv.gravity + ", airJumps " + mv.maxAirJumps + ", dashUnlocked " + mv.dashUnlocked);
        L("momentumDecay " + Recharge.ModApi.Reflect.TryGetField<float>(mv, "momentumDecay", -1f) + " flatMomentumDecay " + Recharge.ModApi.Reflect.TryGetField<float>(mv, "flatMomentumDecay", -1f) + " dashSpeed " + Recharge.ModApi.Reflect.TryGetField<float>(mv, "dashSpeed", -1f));
        L("camera ortho " + (Camera.main != null ? Camera.main.orthographicSize.ToString() : "none") + " pos " + (Camera.main != null ? Camera.main.transform.position.ToString() : ""));
        L("player layer " + LayerMask.LayerToName(mv.gameObject.layer) + " collides with mask 0x" + Physics2D.GetLayerCollisionMask(mv.gameObject.layer).ToString("X"));
        int tn = 0;
        foreach (var tm in UnityEngine.Object.FindObjectsByType<Tilemap>(FindObjectsSortMode.None))
        {
            if (tm == null) continue;
            tn++;
            var parent = tm.transform.parent != null ? tm.transform.parent.name : "-";
            var tr = tm.GetComponent<TilemapRenderer>();
            var cc = tm.GetComponent<Collider2D>();
            var mbs = new StringBuilder();
            foreach (var mb in tm.GetComponents<MonoBehaviour>()) if (mb != null) mbs.Append(mb.GetType().Name).Append(' ');
            L("tilemap '" + tm.name + "' parent '" + parent + "' active=" + tm.gameObject.activeInHierarchy + " layer=" + LayerMask.LayerToName(tm.gameObject.layer) + " pos=" + (Vector2)tm.transform.position + " cell=" + tm.cellSize + " bounds=" + tm.cellBounds.size +
              " collider=" + (cc != null ? cc.GetType().Name + (cc.isTrigger ? "(trigger)" : "") : "none") + " renderer=" + (tr != null) + " scripts=[" + mbs + "]");
        }
        L("tilemaps total " + tn);
        var mine = _mod.Box?.Object;
        if (mine != null)
            foreach (var c in mine.GetComponents<Collider2D>())
                L("gun box collider " + c.GetType().Name + " trigger=" + c.isTrigger + " bounds center " + (Vector2)c.bounds.center + " size " + (Vector2)c.bounds.size + " layer " + LayerMask.LayerToName(c.gameObject.layer));
        L("---- end dump ----");
    }

    private IEnumerator Run()
    {
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_BREAK") == "1")
        {
            yield return BreakRun();
            yield return Finish();
            yield break;
        }
        // Pause-menu layout mode: RECHARGE_GUN_SELFTEST_MENU=1 opens the Gun panel and screenshots it
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_MENU") == "1")
        {
            yield return MenuShots();
            yield return Finish();
            yield break;
        }
        // Navigator-map mode: start the map straight from the title screen (as a player does), skipping the base game
        var mapEnv = Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_MAP");
        if (!string.IsNullOrEmpty(mapEnv))
        {
            float tw = Time.unscaledTime;
            while (Time.unscaledTime - tw < 60f && !(SceneManager.GetActiveScene().name == "MainMenu" && Recharge.ModApi.PauseMenuHelper.FindMenu() != null)) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(5f);
            L("title screen ready after " + (Time.unscaledTime - tw).ToString("0.0") + " s, scene '" + SceneManager.GetActiveScene().name + "'");
            yield return MapStability(mapEnv);
            yield return Finish();
            yield break;
        }
        // ---- 1. title screen -> gameplay ----
        float t0 = Time.unscaledTime;
        float nextPress = 8f;
        while (_mod.Player == null || _mod.Box?.Object == null)
        {
            if (Time.unscaledTime - t0 > 150f) { Check("reached gameplay and placed box", false, "timed out in scene " + SceneManager.GetActiveScene().name); yield return Finish(); yield break; }
            if (SceneManager.GetActiveScene().name == "MainMenu" && Time.unscaledTime - t0 > nextPress)
            {
                nextPress = Time.unscaledTime - t0 + 20f;
                try { L("pressing Play (pauseMenuScript.changeScene) from the title screen"); _mod.Host.PauseMenu.changeScene(); }
                catch (Exception e) { L("changeScene failed: " + e.Message); }
            }
            yield return new WaitForSeconds(0.5f);
        }
        var mv = _mod.Player;
        L("in gameplay after " + (Time.unscaledTime - t0).ToString("0.0") + " s, scene '" + SceneManager.GetActiveScene().name + "'");
        yield return new WaitForSeconds(3f);
        Dump(mv);
        yield return Stability("base game");
        var mapId = Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_MAP");
        if (!string.IsNullOrEmpty(mapId))
        {
            yield return MapStability(mapId);
            yield return Finish();
            yield break;
        }
        var box = _mod.Box.Box;
        var boxGo = _mod.Box.Object;
        Check("box placed", box != null && boxGo != null, "at " + _mod.Box.Center);
        if (_mod.IsUnlocked())
        {
            // a previous run saved the unlock: this proves it comes back bought and greyed, then re-locks to test the purchase again
            Check("persistence: box comes back bought/greyed and gun is on after reload", box != null && !box.isActive && box.TimesUsed == 1 && _mod.Gun.Visible, "isActive=" + (box != null && box.isActive) + " used=" + (box != null ? box.TimesUsed : -1) + " gunVisible=" + _mod.Gun.Visible);
            yield return Shot("0_box_persisted_bought");
            _mod.Box.Relock();
            yield return new WaitForSeconds(1f);
        }
        Check("box not yet bought, gun hidden", !_mod.IsUnlocked() && !_mod.Gun.Visible, "unlocked=" + _mod.IsUnlocked());
        L("box title/cost before: " + (box != null ? box.upgradeCost.ToString() : "?") + " isActive=" + (box != null && box.isActive));
        double dictBefore = globalStats.globalUpgradeDict.TryGetValue(globalStats.globalUpgradeSet.treeGrowth, out var dv) ? dv : double.NaN;
        var cashBefore = globalStats.currencyLookup[globalStats.Currencies.Cash];
        var spawnPos = (Vector2)mv.transform.position;
        yield return Shot("1_box_before_buy");

        // ---- 2. buy it by touching ----
        var sr = boxGo.GetComponent<SpriteRenderer>();
        var boxCenter = sr != null ? (Vector2)sr.bounds.center : _mod.Box.Center;
        var pcol = mv.GetComponent<BoxCollider2D>();
        Collider2D trig = null, solid = null;
        foreach (var c in boxGo.GetComponents<Collider2D>()) { if (c.isTrigger) trig = trig ?? c; else solid = solid ?? c; }
        // stand beside the box so the player overlaps its trigger without being inside its solid body
        var ext = pcol.bounds.extents;
        var toCenter = (Vector2)pcol.bounds.center - (Vector2)mv.transform.position;
        var area = trig != null ? trig.bounds : (sr != null ? sr.bounds : new Bounds(boxCenter, Vector3.one * 50f));
        // player directly under the trigger strip, head poking into it (as after jumping into the box from below)
        Vector2 touchCenter = V(area.center.x, area.center.y - ext.y + 2f);
        Teleport(mv, touchCenter - toCenter);
        L("touch position: player center " + touchCenter + ", trigger bounds " + (trig != null ? trig.bounds.ToString() : "none") + ", solid bounds " + (solid != null ? solid.bounds.ToString() : "none"));
        float waited = 0f;
        while (box.TimesUsed == 0 && waited < 4f) { yield return new WaitForSeconds(0.2f); waited += 0.2f; }
        bool touched = box.TimesUsed > 0;
        if (!touched)
        {
            L("touching the box did not buy it within 4 s; calling the box's own DoBuyUpgrade like OnTriggerStay2D does");
            try { box.DoBuyUpgrade(mv.GetComponent<Collider2D>(), box.upgrade, box.movementUpgrade, box.globalUpgrade); } catch (Exception e) { L("DoBuyUpgrade threw " + e.Message); }
        }
        yield return new WaitForSeconds(1f);
        Check("box bought by touch", touched, touched ? "TimesUsed=" + box.TimesUsed : "needed DoBuyUpgrade fallback");
        Check("gun unlocked after purchase", _mod.IsUnlocked());
        Check("box shows bought (inactive)", !box.isActive && box.TimesUsed == 1, "isActive=" + box.isActive + " used=" + box.TimesUsed);
        double dictAfter = globalStats.globalUpgradeDict.TryGetValue(globalStats.globalUpgradeSet.treeGrowth, out var dv2) ? dv2 : double.NaN;
        Check("purchase changed nothing in the game", (double.IsNaN(dictBefore) && double.IsNaN(dictAfter) || dictBefore == dictAfter) && globalStats.currencyLookup[globalStats.Currencies.Cash] == cashBefore, "dummy key " + dictBefore + " -> " + dictAfter + ", cash unchanged=" + (globalStats.currencyLookup[globalStats.Currencies.Cash] == cashBefore));
        string unlockFile = Path.Combine(_mod.Host.ModDataDir(_mod.Id), "unlocks.json");
        Check("unlock saved per slot", File.Exists(unlockFile) && File.ReadAllText(unlockFile).Contains(_mod.SlotKey()), "slot " + _mod.SlotKey() + " in " + unlockFile);

        Teleport(mv, spawnPos);
        yield return new WaitForSeconds(1.2f);
        Check("gun visible on player", _mod.Gun.Visible && _mod.Gun.GunObject != null && _mod.Gun.GunObject.activeInHierarchy);
        _mod.Gun.AimOverride = V(1f, 0f);
        yield return Shot("2_box_after_buy");

        // ---- 3. shots ----
        var gun = _mod.Gun;
        gun.SetGun(GunDefs.All[0]);   // the box/recoil/tile checks run with the pistol
        gun.Verbose = true;
        _mod.Config.BreakTiles = false;   // aerial tests must not dig holes under the player
        float U = gun.Unit;
        var ground = Ray(mv.transform.position, Vector2.down, U * 30f);
        float groundY = ground.collider != null ? ground.point.y : mv.transform.position.y;
        L("ground below player: " + (ground.collider != null ? ground.collider.name + " at y=" + groundY.ToString("F0") : "none") + ", unit " + U.ToString("F0"));
        var shotLog = new List<string>();
        gun.ShotLog = m => shotLog.Add(m);
        var body = mv.GetComponent<Rigidbody2D>();
        float shotStart = Time.unscaledTime;
        var standPos = (Vector2)mv.transform.position;

        // on-ground shot down: no lift
        gun.AimOverride = V(0f, -1f);
        yield return new WaitForSeconds(0.4f);
        float vyG = mv.Velocity.y;
        Fire(gun);
        yield return new WaitForSeconds(0.3f);
        Check("shooting down on the ground gives no lift", mv.Velocity.y <= vyG + 1f, "Velocity.y " + vyG.ToString("F0") + " -> " + mv.Velocity.y.ToString("F0"));
        yield return new WaitForSeconds(0.4f);

        // airborne, down x4 quick (cooldown-limited): lifts then caps
        float cap = mv.jumpForce * _mod.Config.MaxRecoilSpeed;
        float maxVy = -1e9f;
        Teleport(mv, standPos + V(0f, U * 8f));
        yield return new WaitForSeconds(0.5f);
        L("airborne: onGround=" + mv.onGround + " body vel " + body.linearVelocity);
        int lifts = 0;
        for (int i = 0; i < 4; i++)
        {
            gun.AimOverride = V(0f, -1f);
            while (Time.time < 0 || !gun.CanFire) yield return null;
            float vy0 = mv.Velocity.y;
            while (!Fire(gun)) yield return null;
            if (mv.Velocity.y > vy0 + 1f) lifts++;
            float until = Time.time + 0.06f;
            while (Time.time < until) { maxVy = Mathf.Max(maxVy, body.linearVelocity.y); yield return null; }
            if (i == 0) yield return Shot("3_midshot_down");
            until = Time.time + 0.2f;
            while (Time.time < until) { maxVy = Mathf.Max(maxVy, body.linearVelocity.y); yield return null; }
        }
        Check("down shots in the air give upward boosts, every shot (no limit)", lifts >= 2, lifts + " boosting shots of 4");
        Check("upward speed is not capped", maxVy > cap, "max body vy " + maxVy.ToString("F0") + ", old cap " + cap.ToString("F0"));
        yield return new WaitForSeconds(0.5f);

        // ---- recoil at 2500%: one shot down in the air launches far up, and repeated shots keep lifting ----
        {
            float keepStrength = _mod.Config.RecoilStrength;
            _mod.Config.RecoilStrength = 25f;
            Teleport(mv, standPos + V(0f, U * 8f));
            yield return new WaitForSeconds(0.4f);
            float y0 = mv.transform.position.y, peak = y0, peak1 = y0, vy1 = 0f, vyMax = 0f; int up = 0;
            for (int i = 0; i < 6; i++)
            {
                gun.AimOverride = V(0f, -1f);
                while (!gun.CanFire) yield return null;
                float vyBefore = mv.Velocity.y;
                while (!Fire(gun)) yield return null;
                if (mv.Velocity.y > vyBefore + 1f) up++;
                if (i == 0) vy1 = mv.Velocity.y;
                vyMax = Mathf.Max(vyMax, mv.Velocity.y);
                float until = Time.time + 0.35f;
                while (Time.time < until) { peak = Mathf.Max(peak, mv.transform.position.y); if (i == 0) peak1 = peak; yield return null; }
                L("recoil2500 shot " + (i + 1) + ": Velocity.y " + vyBefore.ToString("F0") + " -> " + mv.Velocity.y.ToString("F0") + ", height gained so far " + ((peak - y0) / U).ToString("F1") + " units");
                if (i == 0) yield return Shot("recoil2500_1_after_first_shot", 0f);
            }
            yield return Shot("recoil2500_2_after_six_shots", 0f);
            Check("recoil 2500%: one shot down in the air launches far up", (peak1 - y0) > 15f * U && vy1 > mv.jumpForce * 3f, "first shot: vy " + vy1.ToString("F0") + " (jumpForce " + mv.jumpForce.ToString("F0") + "), rose " + ((peak1 - y0) / U).ToString("F1") + " units");
            Check("recoil 2500%: repeated shots keep lifting (no limit)", up >= 5 && (peak - y0) > (peak1 - y0) * 1.5f, up + " of 6 shots lifted, total rise " + ((peak - y0) / U).ToString("F1") + " units vs first shot " + ((peak1 - y0) / U).ToString("F1"));
            _mod.Config.RecoilStrength = keepStrength;
            yield return new WaitForSeconds(0.3f);
        }

        // sideways (aimed left, so the bullet is visible in flight), in the air
        Teleport(mv, standPos + V(0f, U * 5f));
        yield return new WaitForSeconds(0.5f);
        gun.AimOverride = V(-1f, 0f);
        while (!gun.CanFire) yield return null;
        var bodyBefore = body.linearVelocity;
        while (!Fire(gun)) yield return null;
        yield return new WaitForFixedUpdate(); yield return new WaitForFixedUpdate();
        var bodyAfter = body.linearVelocity;
        yield return new WaitForSeconds(0.02f);
        yield return Shot("4_midshot_sideways");
        Check("sideways shot (aim left) pushes the player right", bodyAfter.x > bodyBefore.x + 50f, "body vx " + bodyBefore.x.ToString("F0") + " -> " + bodyAfter.x.ToString("F0") + " two physics steps after the shot");

        // up
        Teleport(mv, standPos + V(0f, U * 5f));
        yield return new WaitForSeconds(0.5f);
        mv.Velocity = new Vector2(0f, -100f);   // a normal fall speed (it was at terminal velocity by now)
        gun.AimOverride = V(0f, 1f);
        float vyUp = mv.Velocity.y;
        while (!Fire(gun)) yield return null;
        Check("shooting up kicks the player down", mv.Velocity.y < vyUp - 1f, "Velocity.y " + vyUp.ToString("F0") + " -> " + mv.Velocity.y.ToString("F0"));
        yield return new WaitForSeconds(0.4f);
        Check("bullets were fired and travelled", gun.Shots >= 7, gun.Shots + " shots, " + gun.Hits + " hits");

        // ---- 4. tiles ----
        L("---- tile breaking ----");
        _mod.Config.BreakTiles = true;
        Teleport(mv, standPos);
        yield return new WaitForSeconds(0.8f);
        // floor
        var floorFrom = (Vector2)mv.transform.position;
        var floorBefore = Ray(floorFrom, Vector2.down, U * 30f);
        gun.AimOverride = V(0f, -1f);
        gun.Breaks = 0;
        while (!Fire(gun)) yield return null;
        yield return new WaitForSeconds(0.35f);
        bool floorBroke = TileBreaker.LastCells > 0;
        string floorInfo = TileBreaker.LastInfo;
        Physics2D.SyncTransforms();
        yield return new WaitForSeconds(0.2f);
        var floorAfter = Ray(floorFrom, Vector2.down, U * 30f);
        L("floor ray before: " + (floorBefore.collider != null ? floorBefore.collider.name + " at distance " + floorBefore.distance.ToString("F1") : "none") + "; after: " + (floorAfter.collider != null ? floorAfter.collider.name + " at distance " + floorAfter.distance.ToString("F1") : "none"));
        Check("floor shot removed tiles", floorBroke, floorInfo);
        bool floorPassable = floorBefore.collider != null && (floorAfter.collider == null || floorAfter.distance > floorBefore.distance + 1f);
        Check("second raycast: space is now passable (floor)", floorPassable);
        yield return Shot("5_floor_hole");

        // wall: nearest of left / right from a grounded spot
        // (not over the hole the floor shot just made: stand on solid ground a few units to the side)
        var wallStand = standPos + V(0f, 4f);
        if (FindGround(standPos + V(6f * U, 0f), U, out var wg)) wallStand = wg + V(0f, U * 0.5f);
        Teleport(mv, wallStand);
        yield return new WaitForSeconds(0.8f);
        var from = gun.PivotPoint;   // the bullet flies along the aim line from the pivot: probe the same row of tiles
        var r = Ray(from, Vector2.right, U * 150f, false); var l = Ray(from, Vector2.left, U * 150f, false);
        if (r.collider != null && r.collider.GetComponentInParent<upgradeBox>() != null) r = default;
        if (l.collider != null && l.collider.GetComponentInParent<upgradeBox>() != null) l = default;
        RaycastHit2D wall = default; float dirX = 0f;
        if (r.collider != null && (l.collider == null || r.distance <= l.distance)) { wall = r; dirX = 1f; }
        else if (l.collider != null) { wall = l; dirX = -1f; }
        if (wall.collider == null) L("SKIP wall: no wall within " + (U * 150f).ToString("F0") + " units either side");
        else
        {
            L("wall: '" + wall.collider.name + "' at distance " + wall.distance.ToString("F0") + " dir " + dirX);
            if (wall.distance > U * 6f)
            {
                var near = new Vector2(wall.point.x - dirX * U * 4f, wallStand.y + 4f);
                Teleport(mv, near);
                yield return new WaitForSeconds(0.8f);
                from = gun.PivotPoint;
            }
            var before = Ray(from, V(dirX, 0f), U * 150f, false);
            gun.AimOverride = V(dirX, 0f);
            TileBreaker.LastCells = 0;
            while (!Fire(gun)) yield return null;
            yield return new WaitForSeconds(0.4f);
            bool wallBroke = TileBreaker.LastCells > 0;
            string wallInfo = TileBreaker.LastInfo;
            Physics2D.SyncTransforms();
            yield return new WaitForSeconds(0.2f);
            var after = Ray(from, V(dirX, 0f), U * 150f, false);
            L("wall ray before: " + (before.collider != null ? before.collider.name + " d=" + before.distance.ToString("F1") : "none") + "; after: " + (after.collider != null ? after.collider.name + " d=" + after.distance.ToString("F1") : "none"));
            Check("wall shot removed tiles", wallBroke, wallInfo);
            Check("second raycast: space is now passable (wall)", before.collider != null && (after.collider == null || after.distance > before.distance + 1f));
            yield return Shot("6_wall_hole");
        }

        // ---- 5. the five guns ----
        gun.Verbose = false;
        gun.AimOverride = null;
        yield return GunsPhase(mv, standPos);
        gun.SetGun(_mod.CurrentGun);

        // protected things are untouched
        Check("upgrade box (gun box) still present", boxGo != null && boxGo.activeInHierarchy);

        // ---- 6. wait out the clock and finish ----
        while (Time.unscaledTime - shotStart < 20f) yield return new WaitForSeconds(0.5f);
        gun.AimOverride = null;
        gun.Verbose = false;
        yield return Finish();
    }

    private static Vector2 LevelPos(GameObject go)
    {
        var fo = Singleton<FloatingOrigin>.Instance;
        return (Vector2)go.transform.position - (fo != null ? (Vector2)fo.currentOrigin : Vector2.zero);
    }

    /// <summary>The box must not move when the respawn point changes (checkpoints) or the player goes far away.</summary>
    private IEnumerator Stability(string label)
    {
        var mv = _mod.Player;
        var go = _mod.Box.Object;
        if (mv == null || go == null) { Check(label + ": box exists for stability test", false); yield break; }
        yield return new WaitForSeconds(3f);
        var at0 = LevelPos(go);
        var player0 = (Vector2)mv.transform.position;
        L(label + ": box placed at level pos " + at0 + ", player " + player0 + ", respawnPoint " + mv.respawnPoint + ", player-to-box distance " + (at0 - player0).magnitude.ToString("0"));
        yield return Shot("s_" + label.Replace(' ', '_') + "_0_placed");
        yield return BoxRowShot(label);
        if (label == "navigator map") Check("map: gun box is lined up with the row of upgrade boxes (same bottom, row spacing)", _mod.Box.InRow && Mathf.Abs(_mod.Box.RowBottomDelta) < 0.5f, _mod.Box.RowInfo + ", bottom delta " + _mod.Box.RowBottomDelta.ToString("0.###"));
        else L(label + ": row placement is informational here: " + _mod.Box.RowInfo);
        // like a checkpoint: new respawn point far away, and the player walks (is moved) there
        var far = player0 + new Vector2(900f, 0f);
        mv.respawnPoint = far;
        Teleport(mv, far);
        L(label + ": respawnPoint set to " + far + " and player moved there; waiting 30 s");
        yield return new WaitForSeconds(15f);
        yield return Shot("s_" + label.Replace(' ', '_') + "_1_far_15s");
        yield return new WaitForSeconds(15f);
        var at1 = _mod.Box.Object != null ? LevelPos(_mod.Box.Object) : new Vector2(float.NaN, float.NaN);
        Check(label + ": box did not move after respawn point/player moved", _mod.Box.Object == go && (at1 - at0).sqrMagnitude < 0.01f, at0 + " -> " + at1);
        yield return Shot("s_" + label.Replace(' ', '_') + "_2_far_30s");
        Teleport(mv, player0);
        yield return new WaitForSeconds(2f);
        yield return Shot("s_" + label.Replace(' ', '_') + "_3_back_at_spawn");
    }

    // `a ?? b` does not see a destroyed Unity object as null, so test with == and fall back to the live menu
    private pauseMenuScript LiveMenu()
    {
        var m = Recharge.ModApi.PauseMenuHelper.FindMenu();
        if (m == null) m = _mod.Host.PauseMenu;
        if (m == null) m = UnityEngine.Object.FindFirstObjectByType<pauseMenuScript>(FindObjectsInactive.Include);
        L("pause menu for PlayMap: " + (m != null ? m.name : "NONE"));
        return m;
    }

    private IEnumerator MapStability(string mapId)
    {
        var mgrType = Type.GetType("MapManager");
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { if (mgrType != null) break; try { mgrType = asm.GetType("MapManager", false); } catch { } }
        if (mgrType == null) { Check("Navigator present", false); yield break; }
        const System.Reflection.BindingFlags sf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
        var inst = mgrType.GetProperty("Instance", sf)?.GetValue(null);
        var play = mgrType.GetMethod("PlayMap");
        L("loading Navigator map '" + mapId + "'");
        try { play.Invoke(inst, new object[] { mapId, LiveMenu(), true }); }
        catch (Exception e) { Check("PlayMap", false, e.Message); yield break; }
        float t0 = Time.unscaledTime;
        while (!(_mod.NavigatorState().Built && _mod.Box?.Object != null && _mod.Player != null))
        {
            if (Time.unscaledTime - t0 > 150f) { Check("map built and box placed", false, "timed out"); yield break; }
            yield return new WaitForSeconds(0.5f);
        }
        var st = _mod.NavigatorState();
        L("map '" + st.MapId + "' built; map spawn (live) " + st.Spawn + ", player " + (Vector2)_mod.Player.transform.position + ", box " + _mod.Box.Center);
        var box = _mod.Box.Object.GetComponent<SpriteRenderer>();
        bool overlapsSpawn = box != null && st.HasSpawn && box.bounds.Contains(new Vector3(st.Spawn.x, st.Spawn.y, box.bounds.center.z));
        Check("map: box does not cover the map spawn", !overlapsSpawn, "box bounds " + (box != null ? box.bounds.ToString() : "?"));
        yield return Stability("navigator map");
    }


    // ---------- the five guns ----------

    private static float AngDiff(float a, float b)
    {
        float d = (a - b) % 360f;
        if (d > 180f) d -= 360f; if (d < -180f) d += 360f;
        return Mathf.Abs(d);
    }

    private bool FindGround(Vector2 around, float U, out Vector2 point)
    {
        foreach (var k in new[] { 0f, 10f, -10f, 16f, -16f, 22f, -22f, 28f, -28f })
        {
            var from = new Vector2(around.x + k * U, around.y + U * 6f);
            var h = Ray(from, Vector2.down, U * 40f);
            if (h.collider != null && h.distance > U * 0.5f && Mathf.Abs(h.normal.y) > 0.9f) { point = h.point; return true; }
        }
        point = around; return false;
    }

    private IEnumerator GunsPhase(Movement mv, Vector2 standPos)
    {
        var gun = _mod.Gun;
        var cfg = _mod.Config;
        float U = gun.Unit;
        bool prevBreak = cfg.BreakTiles;
        cfg.BreakTiles = false;
        L("---- guns: aiming, firing, effects ----");
        if (!FindGround(standPos, U, out var ground)) { Check("guns: found solid ground for the gun tests", false); yield break; }
        var stand = ground + V(0f, U * 0.5f);
        var air = ground + V(0f, U * 8f);
        L("gun test ground at " + ground + ", standing spot " + stand + ", air spot " + air);
        var cam = Camera.main;
        int total = 0, bad = 0;

        for (int facing = 0; facing < 2; facing++)
        {
            bool faceLeft = facing == 1;
            // flip the player the way the game does: negative localScale.x
            mv.transform.localScale = new Vector3(faceLeft ? -1f : 1f, 1f, 1f);
            mv.facingRight = !faceLeft;
            foreach (var def in GunDefs.All)
            {
                gun.SetGun(def);
                int dirBad = 0;
                float worstGun = 0f, worstBullet = 0f, worstMuz = 0f;
                for (int i = 0; i < 8; i++)
                {
                    float deg = i * 45f;
                    var want = new Vector2(Mathf.Cos(deg * Mathf.Deg2Rad), Mathf.Sin(deg * Mathf.Deg2Rad));
                    Teleport(mv, air);
                    mv.transform.localScale = new Vector3(faceLeft ? -1f : 1f, 1f, 1f);
                    // the scripted "mouse": 300 units from the gun along the wanted direction, through the real screen-to-world maths
                    gun.ScreenAimFn = () => (Vector2)cam.WorldToScreenPoint(gun.PivotPoint + want * 300f);
                    gun.ResetCooldown();
                    yield return null;
                    yield return new WaitForEndOfFrame();
                    var gt = gun.GunObject.transform;
                    var sr = gun.GunObject.GetComponent<SpriteRenderer>();
                    float gunAng = Mathf.Atan2(gt.right.y, gt.right.x) * Mathf.Rad2Deg;
                    var tip = (Vector2)gt.TransformPoint(new Vector3(def.MuzzleX - GunDef.PivotX, 0f, 0f));
                    var muz = gun.MuzzleWorld(gun.Aim);
                    var vis = sr.flipY ? -gt.up : gt.up;
                    bool upright = vis.y > -0.01f;
                    float errGun = AngDiff(gunAng, deg);
                    float errMuz = (tip - muz).magnitude;
                    // the barrel tip must also lie on the line from the cursor-facing pivot: tip - pivot along 'want'
                    var tipDir = (tip - gun.PivotPoint).normalized;
                    float errTip = AngDiff(Mathf.Atan2(tipDir.y, tipDir.x) * Mathf.Rad2Deg, deg);
                    bool fired = gun.TryFire();
                    var b = gun.Fx.LastSpawned;
                    var p0 = b != null ? (Vector2)b.T.position : Vector2.zero;
                    float errSpawn = (p0 - muz).magnitude;
                    yield return null;
                    var p1 = b != null ? (Vector2)b.T.position : Vector2.zero;
                    var step = p1 - p0;
                    bool stepOk = b != null && b.Active && step.sqrMagnitude > 1e-4f;
                    float errBullet = stepOk ? AngDiff(Mathf.Atan2(step.y, step.x) * Mathf.Rad2Deg, deg) : 999f;
                    if (!stepOk && b != null && !b.Active) errBullet = 0f;   // hit something in the first step: direction recorded below
                    bool ok = fired && errGun < 3f && errTip < 3f && errBullet < 3f && errMuz < U * 0.05f && errSpawn < U * 0.05f && upright;
                    total++; if (!ok) { bad++; dirBad++; }
                    worstGun = Mathf.Max(worstGun, errGun); worstBullet = Mathf.Max(worstBullet, errBullet < 900f ? errBullet : 0f); worstMuz = Mathf.Max(worstMuz, errMuz);
                    L("aim " + (faceLeft ? "FACING LEFT " : "facing right") + " [" + def.Name + "] want " + deg.ToString("0") + " deg: sprite angle " + gunAng.ToString("0.0") + " (err " + errGun.ToString("0.0") + "), tip angle err " + errTip.ToString("0.0") +
                      ", muzzle " + muz.ToString("F0") + " vs sprite tip " + tip.ToString("F0") + " (off " + errMuz.ToString("0.0") + "), bullet spawn off " + errSpawn.ToString("0.0") + ", bullet first-step dir " + step.normalized.ToString("F2") + " vs " + want.ToString("F2") +
                      " (err " + (errBullet < 900f ? errBullet.ToString("0.0") : "n/a") + "), upright=" + upright + " flipY=" + sr.flipY + ", player lossyScale.x " + mv.transform.lossyScale.x.ToString("0") + (ok ? " OK" : " BAD"));
                    gun.ScreenAimFn = null;
                    yield return new WaitForSeconds(0.05f);
                }
                Check("aim: " + def.Name + ", 8 directions, player facing " + (faceLeft ? "left" : "right") + ": barrel, muzzle and bullet follow the cursor", dirBad == 0, "worst sprite err " + worstGun.ToString("0.0") + " deg, worst bullet err " + worstBullet.ToString("0.0") + " deg, worst muzzle off " + worstMuz.ToString("0.0"));
            }
        }
        mv.transform.localScale = new Vector3(1f, 1f, 1f); mv.facingRight = true;
        Check("aim: all " + total + " direction samples within 3 degrees, muzzle at the barrel tip, sprite upright", bad == 0, bad + " bad");

        // ---- per gun: fire in 4 directions, mid-shot and impact screenshots ----
        float slow = U * 5f;
        foreach (var def in GunDefs.All)
        {
            gun.SetGun(def);
            string tag = def.Name.Replace(' ', '_');
            gun.FlashBoost = 0.35f;
            var dirs = new[] { V(1f, 0f), V(-1f, 0f), V(0f, 1f), V(0f, -1f) };
            var names = new[] { "right", "left", "up", "down" };
            int fired4 = 0, spawned = 0;
            for (int i = 0; i < 4; i++)
            {
                Teleport(mv, stand);
                yield return new WaitForSeconds(0.5f);
                var want = dirs[i];
                gun.ScreenAimFn = () => (Vector2)cam.WorldToScreenPoint(gun.PivotPoint + want * 300f);
                gun.ResetCooldown();
                yield return null;
                int s0 = gun.Shots;
                bool slowShot = i == 0;
                if (slowShot) cfg.BulletSpeed = slow;
                if (slowShot) gun.FlashFrameOverride = 0;
                bool fired = gun.TryFire();
                int active = gun.Fx.ActiveBullets;
                if (fired && gun.Shots == s0 + 1) fired4++;
                if (active >= def.Pellets) spawned++;
                if (slowShot)
                {
                    yield return null;
                    Crop("gun_" + tag + "_midshot", gun.MuzzleWorld(gun.Aim) + gun.Aim * U * 0.5f, U * 2.4f);
                    yield return Shot("gun_" + tag + "_midshot");
                    gun.FlashFrameOverride = -1;
                    cfg.BulletSpeed = 0f;
                }
                else yield return new WaitForSeconds(0.25f);
                gun.ScreenAimFn = null;
                L("[" + def.Name + "] fired " + names[i] + ": fired=" + fired + ", bullets in flight " + active + " (expect " + def.Pellets + ")");
            }
            Check("fire: " + def.Name + " fires in 4 directions and spawns " + def.Pellets + " bullet(s) each", fired4 == 4 && spawned == 4, fired4 + "/4 fired, " + spawned + "/4 spawned the full set");

            // impact effect: shoot the ground ahead and a little down (first let the earlier bullets, and the rocket's smoke trail, finish)
            for (float w = 0f; gun.Fx.ActiveBullets > 0 && w < 6f; w += 0.1f) yield return new WaitForSeconds(0.1f);
            yield return new WaitForSeconds(0.6f);
            Teleport(mv, stand);
            yield return new WaitForSeconds(0.5f);
            var dn = V(1f, -0.3f).normalized;
            gun.ScreenAimFn = () => (Vector2)cam.WorldToScreenPoint(gun.PivotPoint + dn * 300f);
            gun.ResetCooldown();
            yield return null;
            int h0 = gun.Hits;
            gun.TryFire();
            float t = 0f;
            while (gun.Hits == h0 && t < 1.5f) { t += Time.unscaledDeltaTime; yield return null; }
            bool hit = gun.Hits > h0;
            yield return null;
            int live = gun.Fx.ActiveParticles;
            Crop("gun_" + tag + "_impact", gun.LastHitPoint + V(0f, U * 0.4f), U * 2.4f);
            yield return Shot("gun_" + tag + "_impact", 0f);
            yield return new WaitForSeconds(def.PopLife * 0.4f);
            Crop("gun_" + tag + "_impact2", gun.LastHitPoint + V(0f, U * 0.4f), U * 2.4f);
            yield return Shot("gun_" + tag + "_impact2", 0f);
            gun.ScreenAimFn = null;
            gun.FlashBoost = 0f;
            Check("impact: " + def.Name + " bullet hit the ground and its effect played", hit && live > 0, "hit=" + hit + ", live particles/pops " + live + ", at " + gun.LastHitPoint);
            yield return new WaitForSeconds(0.9f);
            Check("effects are short-lived: " + def.Name + " particles gone after 0.9 s", gun.Fx.ActiveParticles == 0, gun.Fx.ActiveParticles + " still alive");
        }

        // ---- rocket breaks more tiles than the pistol (Bullets break tiles on) ----
        cfg.BreakTiles = true;
        gun.SetGun(GunDefs.Find("Pistol"));
        int pistolCells = 0, rocketCells = 0;
        for (int k = 0; k < 2; k++)
        {
            var spot = ground + V((k == 0 ? 6f : -6f) * U, 0f);
            if (!FindGround(spot, U * 0.5f, out var g2) || Mathf.Abs(g2.x - ground.x) < U * 3f) { L("SKIP rocket-vs-pistol tile radius: no second ground spot"); break; }
            Teleport(mv, g2 + V(0f, U * 0.5f));
            yield return new WaitForSeconds(0.6f);
            gun.SetGun(GunDefs.All[k == 0 ? 0 : 4]);
            gun.ScreenAimFn = () => (Vector2)cam.WorldToScreenPoint(gun.PivotPoint + V(0f, -1f) * 300f);
            gun.ResetCooldown();
            TileBreaker.LastCells = 0;
            yield return null;
            int h0 = gun.Hits;
            gun.TryFire();
            float t = 0f;
            while (gun.Hits == h0 && t < 1.5f) { t += Time.unscaledDeltaTime; yield return null; }
            yield return new WaitForSeconds(0.2f);
            gun.ScreenAimFn = null;
            if (k == 0) pistolCells = TileBreaker.LastCells; else rocketCells = TileBreaker.LastCells;
        }
        if (pistolCells > 0) Check("rocket clears more tiles than the pistol", rocketCells > pistolCells, "pistol " + pistolCells + " cell(s), rocket " + rocketCells + " cell(s)");
        else L("SKIP rocket tile radius check: the pistol shot cleared no tiles here (" + TileBreaker.LastInfo + ")");
        cfg.BreakTiles = prevBreak;
        cfg.BulletSpeed = 0f;
        gun.FlashBoost = 0f;
        yield return new WaitForSeconds(0.5f);
    }

    private bool Fire(GunController gun) => gun.TryFire();

    // Opens the "Gun" mod panel the way its pause-menu row does (the row's onClick is panel.SetActive(true)),
    // then screenshots it: as opened, after Next, with the recoil slider at 300%.
    private IEnumerator MenuShots()
    {
        float tw = Time.unscaledTime;
        while (Time.unscaledTime - tw < 90f && !(SceneManager.GetActiveScene().name == "MainMenu" && Recharge.ModApi.PauseMenuHelper.FindMenu() != null)) yield return new WaitForSeconds(0.5f);
        yield return new WaitForSeconds(6f);
        var menu = Recharge.ModApi.PauseMenuHelper.FindMenu();
        GameObject panel = null;
        foreach (var t in Resources.FindObjectsOfTypeAll<Transform>())
            if (t != null && t.name == "GunPanelBit" && t.gameObject.scene.IsValid()) { panel = t.gameObject; break; }
        Check("Gun panel exists", panel != null);
        if (panel == null) yield break;
        var main = Recharge.ModApi.PauseMenuHelper.MainBit(menu);
        var sett = Recharge.ModApi.PauseMenuHelper.SettingsBit(menu);
        if (main != null) main.SetActive(false);
        if (sett != null) sett.SetActive(false);
        panel.SetActive(true);
        yield return null; yield return null; yield return new WaitForSeconds(1f);
        var in0 = panel.transform.Find("GunRoot/RecoilInput")?.GetComponent<TMPro.TMP_InputField>();
        if (in0 != null) { in0.text = "100"; in0.onEndEdit.Invoke("100"); yield return null; yield return new WaitForSeconds(0.3f); }
        yield return Shot("m0_panel_open");
        var next = panel.transform.Find("GunRoot/GunNext");
        Check("Next button found", next != null);
        if (next != null) next.GetComponent<UnityEngine.UI.Button>().onClick.Invoke();
        yield return null; yield return null; yield return new WaitForSeconds(0.5f);
        yield return Shot("m1_after_next");
        var inp = panel.transform.Find("GunRoot/RecoilInput")?.GetComponent<TMPro.TMP_InputField>();
        Check("recoil box found", inp != null);
        string[] typed = { "100", "2500", "10000", "99999", "abc", "-5", "" };
        float[] expectStrength = { 1f, 25f, 100f, 100f, 100f, 0f, 0f };
        string[] expectText = { "100", "2500", "10000", "10000", "10000", "0", "0" };
        for (int i = 0; i < typed.Length && inp != null; i++)
        {
            inp.text = typed[i]; inp.onEndEdit.Invoke(typed[i]);
            yield return null; yield return null; yield return new WaitForSeconds(0.3f);
            // "abc" / "" are invalid: the box snaps back to the saved value (what the previous entry left)
            string want = expectText[i]; float ws = expectStrength[i];
            if (typed[i] == "abc") { want = "10000"; ws = 100f; }
            if (typed[i] == "") { want = "0"; ws = 0f; }
            Check("number box: typing '" + typed[i] + "' -> '" + inp.text + "', strength " + _mod.Config.RecoilStrength, inp.text == want && Mathf.Approximately(_mod.Config.RecoilStrength, ws), "expected '" + want + "' / " + ws);
            if (typed[i] == "10000") yield return Shot("m2_recoil_10000");
        }
        if (inp != null) { inp.text = "10000"; inp.onEndEdit.Invoke("10000"); yield return null; yield return new WaitForSeconds(0.3f); yield return Shot("m3_recoil_10000_final"); }
        if (inp != null) { inp.text = "100"; inp.onEndEdit.Invoke("100"); }
    }

    private IEnumerator BreakRun()
    {
        var mapId = Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_MAP");
        float t0 = Time.unscaledTime;
        if (!string.IsNullOrEmpty(mapId))
        {
            while (Time.unscaledTime - t0 < 90f && !(SceneManager.GetActiveScene().name == "MainMenu" && Recharge.ModApi.PauseMenuHelper.FindMenu() != null)) yield return new WaitForSeconds(0.5f);
            yield return new WaitForSeconds(5f);
            var mgrType = FindAsmType("MapManager");
            if (mgrType == null) { Check("Navigator present", false); yield break; }
            const System.Reflection.BindingFlags sf = System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.NonPublic;
            var inst = mgrType.GetProperty("Instance", sf)?.GetValue(null);
            try { mgrType.GetMethod("PlayMap").Invoke(inst, new object[] { mapId, LiveMenu(), true }); }
            catch (Exception e) { Check("PlayMap", false, e.Message); yield break; }
            while (!(_mod.NavigatorState().Built && _mod.Box?.Object != null && _mod.Player != null))
            {
                if (Time.unscaledTime - t0 > 200f) { Check("map built", false, "timed out"); yield break; }
                yield return new WaitForSeconds(0.5f);
            }
        }
        else
        {
            float nextPress = 8f;
            while (_mod.Player == null || _mod.Box?.Object == null)
            {
                if (Time.unscaledTime - t0 > 150f) { Check("reached gameplay", false, "timed out"); yield break; }
                if (SceneManager.GetActiveScene().name == "MainMenu" && Time.unscaledTime - t0 > nextPress)
                {
                    nextPress = Time.unscaledTime - t0 + 20f;
                    try { _mod.Host.PauseMenu.changeScene(); } catch (Exception e) { L("changeScene failed: " + e.Message); }
                }
                yield return new WaitForSeconds(0.5f);
            }
        }
        yield return new WaitForSeconds(4f);
        var mv = _mod.Player;
        L("break test in " + (string.IsNullOrEmpty(mapId) ? "base game" : "map " + mapId) + ", scene '" + SceneManager.GetActiveScene().name + "', player " + (Vector2)mv.transform.position);
        _mod.Config.Enabled = true; _mod.Config.BreakTiles = true;
        if (!_mod.IsUnlocked()) _mod.SetUnlocked(true);
        yield return new WaitForSeconds(1.5f);
        Survey.Dump(L);
        Survey.DumpObjects(L);
        Survey.DumpUi(L);
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_SPIKES") == "1") { SpikeSurvey(); yield return SpikeShots(); yield break; }
        if (Environment.GetEnvironmentVariable("RECHARGE_GUN_SELFTEST_SURVEY") == "1") { yield return Shot("survey_hud", 0.5f); yield break; }
        yield return BreakTests();
    }

    private static Type FindAsmType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies()) { try { var t = asm.GetType(name, false); if (t != null) return t; } catch { } }
        return null;
    }

    private IEnumerator Finish()
    {
        int fails = 0;
        foreach (var r in _results) if (r.StartsWith("FAIL")) fails++;
        L("GUN SELFTEST DONE");
        L((fails == 0 ? "SELFTEST PASS" : "SELFTEST FAIL (" + fails + " failed)") + " - " + _results.Count + " checks");
        foreach (var r in _results) L("  " + r);
        try { File.WriteAllText(Path.Combine(_dir, "selftest.txt"), _report.ToString()); } catch { }
        yield return null;
    }
}
