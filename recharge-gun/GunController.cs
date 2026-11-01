using System;
using System.Reflection;
using UnityEngine;
using UnityEngine.InputSystem;

/// <summary>
/// The held gun: sprite, aiming, firing, bullets and recoil.
///
/// Recoil is written into the game's own Movement state (the same fields SpringScript's hitSpring uses):
/// sideways kicks go into <c>momentum</c> (Movement adds it to Velocity every FixedUpdate and decays it with
/// its own momentumDecay), upward gun-jumps set <c>Velocity.y</c> like a spring does. Nothing writes the
/// Rigidbody2D directly, so Movement's next FixedUpdate integrates it instead of overwriting it. Recoil is
/// skipped during a dash or any cutscene mode (spring / death / long fall), and a wall jump's sideways
/// lock blocks the horizontal part, so dash, wall jump, double jump and respawn are untouched.
/// </summary>
internal class GunController
{
    private static readonly FieldInfo DeadField = typeof(Movement).GetField("isDead", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private readonly GunMod _mod;
    internal readonly GunFx Fx;

    private Movement _mv;
    private Rigidbody2D _body;
    private BoxCollider2D _col;
    private GameObject _gunGo, _flashGo;
    private SpriteRenderer _gunSr, _flashSr;
    private float _unit = 60f;
    private bool _visible;

    private enum AimMode { Mouse, Stick }
    private AimMode _mode = AimMode.Mouse;
    private Vector2 _mouseLast, _stickAim = Vector2.right, _aim = Vector2.right;
    private float _nextFire, _flashUntil;
    private int _airBoosts;
    private int _shotNo, _logAfterIn, _logShot;
    private Vector2 _logBefore;
    private ContactFilter2D _filter;
    private readonly RaycastHit2D[] _hits = new RaycastHit2D[12];
    private bool _filterReady;
    private int _playerLayer = -1;

    // self-test hooks
    internal Vector2? AimOverride;
    internal bool Verbose;
    internal int Shots, Hits, Breaks;
    internal Vector2 LastHitPoint; internal string LastHitName = "";
    internal Action<string> ShotLog;

    internal Func<Vector2> ScreenAimFn;     // scripted "mouse": screen position, goes through the real mouse maths
    internal float FlashBoost;
    internal int FlashFrameOverride = -1;     // self-test: hold the muzzle flash on one frame
    private float _flashStart;
    internal Vector2 Aim => _aim;
    internal GunDef Def => _def;
    private GunDef _def = GunDefs.All[0];
    internal bool Visible => _visible;
    internal GameObject GunObject => _gunGo;
    internal float Unit => _unit;
    internal Vector2 PivotPoint => Pivot();
    internal void ResetCooldown() { _nextFire = 0f; }

    /// <summary>Switch the held gun right now.</summary>
    internal void SetGun(GunDef def)
    {
        if (def == null) return;
        _def = def;
        if (Fx.Bullets == null) Fx.Warmup();
        if (_gunSr != null) _gunSr.sprite = def.GunSp;
        if (_flashSr != null) { _flashSr.sprite = def.FlashSps[0]; _flashGo.SetActive(false); }
    }

    private float GunLen => _unit * 1.15f * _def.LenMul;
    private float MuzzleDist => GunLen * (_def.MuzzleX - GunDef.PivotX);
    private Vector2 GunOrigin(Vector2 aim) => Pivot() + aim * (_unit * 0.18f);

    /// <summary>The barrel tip in world space for the given aim.</summary>
    internal Vector2 MuzzleWorld(Vector2 aim) => GunOrigin(aim) + aim * MuzzleDist;

    internal GunController(GunMod mod) { _mod = mod; Fx = new GunFx(mod); }

    internal void Dispose()
    {
        if (_gunGo != null) UnityEngine.Object.Destroy(_gunGo);
        Fx.Dispose();
    }

    private bool Enabled => _mod.Config.Enabled && _mod.IsUnlocked();

    internal void OnUnlocked() { _nextFire = Time.time + 0.3f; }

    // ---------- build ----------

    internal void Attach(Movement mv)
    {
        _mv = mv;
        _body = mv.GetComponent<Rigidbody2D>();
        _col = mv.GetComponent<BoxCollider2D>();
        TileBreaker.Invalidate();
        Deco.Invalidate();
        Fx.Warmup();

        _unit = 60f;
        // the standing collider's height (the live collider shrinks during a dash)
        var stand = Recharge.ModApi.Reflect.TryGetField<Vector2>(mv, "defaultColliderSize", Vector2.zero);
        float h = stand.y > 1f ? stand.y : (_col != null ? _col.size.y : 60f);
        _unit = Mathf.Max(16f, h * Mathf.Abs(mv.transform.lossyScale.y));
        // sorting: just above the player's sprite
        var playerSr = FindPlayerSprite(mv);
        int layer = playerSr != null ? playerSr.sortingLayerID : 0;
        int order = playerSr != null ? playerSr.sortingOrder + 1 : 10;
        Fx.SetScale(_unit, layer, order);

        if (_gunGo != null) UnityEngine.Object.Destroy(_gunGo);
        _gunGo = new GameObject("RechargeGun");
        // NOT a child of the player: the player flips by localScale.x = -1, and a mirrored parent mirrors the aim.
        // The gun lives in plain world space and is placed from the aim every frame.
        _gunSr = _gunGo.AddComponent<SpriteRenderer>();
        _def = GunDefs.Find(_mod.Config.GunName);
        _gunSr.sprite = _def.GunSp; _gunSr.sharedMaterial = Fx.MakeMaterial();
        _gunSr.sortingLayerID = layer; _gunSr.sortingOrder = order;
        _flashGo = new GameObject("MuzzleFlash");
        _flashSr = _flashGo.AddComponent<SpriteRenderer>();
        _flashSr.sprite = _def.FlashSps[0]; _flashSr.sharedMaterial = Fx.MakeMaterial();
        _flashSr.sortingLayerID = layer; _flashSr.sortingOrder = order + 1;
        _flashGo.SetActive(false);
        _gunGo.SetActive(false);
        _visible = false;

        _playerLayer = mv.gameObject.layer;
        BuildFilter();
        _mod.Host.Log("gun attached to player (unit " + _unit.ToString("0.#") + ", sorting layer " + layer + " order " + order + ", player layer " + LayerMask.LayerToName(_playerLayer) + ", mask 0x" + _filter.layerMask.value.ToString("X") + ")");
    }

    private static SpriteRenderer FindPlayerSprite(Movement mv)
    {
        foreach (var r in mv.GetComponentsInChildren<SpriteRenderer>(true))
            if (r != null && r.sprite != null && r.gameObject.name != "RechargeGun" && r.gameObject.name != "MuzzleFlash") return r;
        return null;
    }

    private void BuildFilter()
    {
        // the layers the player itself collides with (Ground etc.); triggers stay in so spikes can be hit
        int mask = _playerLayer >= 0 ? Physics2D.GetLayerCollisionMask(_playerLayer) : Physics2D.AllLayers;
        _filter = new ContactFilter2D { useTriggers = true };
        _filter.SetLayerMask(mask);
        _filterReady = true;
    }

    // ---------- per frame ----------

    private static bool IsDead(Movement mv)
    {
        try { return DeadField != null && (bool)DeadField.GetValue(mv); } catch { return false; }
    }

    private Vector2 Pivot()
    {
        var c = _col != null ? (Vector2)_col.bounds.center : (Vector2)_mv.transform.position;
        return c + new Vector2(0f, _unit * 0.05f);
    }

    internal void Tick(Movement mv)
    {
        if (_gunGo == null || _mv != mv) return;
        bool visible = Enabled && !IsDead(mv) && Time.timeScale > 0f && SceneIsGameplay();
        if (visible != _visible) { _visible = visible; _gunGo.SetActive(visible); if (!visible) _flashGo.SetActive(false); }

        if (_mv.onGround || _mv.OnWall) _airBoosts = 0;

        Deco.Watch(_unit);
        StepBullets(Time.deltaTime);
        Fx.StepSparks(Time.deltaTime);

        if (_flashGo.activeSelf && Time.time > _flashUntil) _flashGo.SetActive(false);

        if (!visible) return;
        UpdateAim();
        if (FireHeld() && Time.time >= _nextFire) TryFire();
    }

    private static bool SceneIsGameplay() => UnityEngine.SceneManagement.SceneManager.GetActiveScene().name != "MainMenu";

    private bool FireHeld()
    {
        if (AimOverride.HasValue) return false;   // scripted run: no real input
        var cfg = _mod.Config;
        var mouse = Mouse.current; var kb = Keyboard.current; var pad = Gamepad.current;
        if (cfg.LeftMouseFires && mouse != null && mouse.leftButton.isPressed) return true;
        if (kb != null && kb.fKey.isPressed) return true;
        if (pad != null && pad.rightTrigger.ReadValue() > 0.5f) return true;
        return false;
    }

    private void UpdateAim()
    {
        if (AimOverride.HasValue) { _aim = AimOverride.Value.normalized; return; }
        if (ScreenAimFn != null) { AimAtScreen(ScreenAimFn()); return; }
        var mouse = Mouse.current; var pad = Gamepad.current;
        if (mouse != null)
        {
            var mp = mouse.position.ReadValue();
            if ((mp - _mouseLast).sqrMagnitude > 4f || mouse.leftButton.isPressed) _mode = AimMode.Mouse;
            _mouseLast = mp;
        }
        else if (pad != null) _mode = AimMode.Stick;

        bool stickActive = false;
        if (pad != null)
        {
            var s = pad.rightStick.ReadValue();
            if (s.sqrMagnitude > 0.35f * 0.35f) { _mode = AimMode.Stick; _stickAim = s.normalized; stickActive = true; }
        }

        if (_mode == AimMode.Mouse && mouse != null)
        {
            AimAtScreen(mouse.position.ReadValue());
        }
        else if (_mode == AimMode.Stick)
        {
            if (stickActive) _aim = _stickAim;
            else _aim = _mv.facingRight ? Vector2.right : Vector2.left;
        }
    }

    private void AimAtScreen(Vector2 mp)
    {
        var cam = Camera.main;
        if (cam == null) return;
        var world = (Vector2)cam.ScreenToWorldPoint(new Vector3(mp.x, mp.y, Mathf.Abs(cam.transform.position.z)));
        var d = world - Pivot();
        if (d.sqrMagnitude > 1f) _aim = d.normalized;
    }

    internal void LateTick(Movement mv)
    {
        if (_gunGo == null || !_visible || _mv != mv) return;
        float len = GunLen;
        var origin = GunOrigin(_aim);
        float ang = Mathf.Atan2(_aim.y, _aim.x) * Mathf.Rad2Deg;
        bool left = _aim.x < -0.0001f;
        var gt = _gunGo.transform;
        gt.position = new Vector3(origin.x, origin.y, 0f);
        gt.rotation = Quaternion.Euler(0f, 0f, ang);
        gt.localScale = new Vector3(len, len * _def.GunAspect, 1f);
        // upright on both sides: aiming left the sprite is turned over, so mirror it back
        _gunSr.flipY = left;

        if (_flashGo.activeSelf)
        {
            var m = origin + _aim * MuzzleDist;
            var ft = _flashGo.transform;
            ft.position = new Vector3(m.x, m.y, 0f);
            ft.rotation = Quaternion.Euler(0f, 0f, ang);
            // animation: the flash's 2-3 frames spread over its (unboosted) duration; the last one holds
            var fl = _def.FlashSps;
            int fi = Mathf.Min(fl.Length - 1, (int)((Time.time - _flashStart) / _def.FlashDur * fl.Length));
            if (FlashFrameOverride >= 0) fi = Mathf.Min(fl.Length - 1, FlashFrameOverride);
            _flashSr.sprite = fl[Mathf.Max(0, fi)];
            float fs = _unit * _def.FlashSize;
            if (_def.FlashPulse) fs *= 1f + 0.3f * Mathf.Sin(Time.time * 70f);
            ft.localScale = new Vector3(fs, fs * _def.FlashAspect, 1f);
            _flashSr.flipY = left;
        }
    }

    // ---------- firing ----------

    internal bool CanFire => _gunGo != null && _mv != null && _visible;

    /// <summary>Fires now if the rate limit allows (used by input and by the self-test).</summary>
    internal bool TryFire()
    {
        if (!CanFire || Time.time < _nextFire) return false;
        if (AimOverride.HasValue) _aim = AimOverride.Value.normalized;
        var cfg = _mod.Config;
        var def = _def;
        _nextFire = Time.time + 1f / Mathf.Max(0.3f, def.FireRate * cfg.FireRate / 4f);
        var pivot = GunOrigin(_aim);
        float lead = MuzzleDist;
        float speed = (cfg.BulletSpeed > 0f ? cfg.BulletSpeed : _unit * 70f) * def.SpeedMul;
        float life = cfg.BulletLifetime * def.LifeMul;

        GunFx.Bullet center = null;
        // pellet 0 flies exactly along the aim; the others fan out either side of it
        for (int i = 0; i < def.Pellets; i++)
        {
            float off = i == 0 ? 0f : ((i + 1) / 2) * def.SpreadDeg * ((i & 1) == 1 ? 1f : -1f);
            var dir = off == 0f ? _aim : Rot(_aim, off);
            var nb = Fx.Spawn(def, pivot, dir, lead, life, speed);
            if (i == 0) center = nb;
        }
        Fx.LastSpawned = center;
        Fx.MuzzlePuffs(def, pivot + _aim * lead, _aim);
        _flashGo.SetActive(true); _flashStart = Time.time; _flashUntil = Time.time + def.FlashDur + FlashBoost;
        Fx.PlayShot();
        Shots++; _shotNo++;

        var before = _body != null ? (Vector2)_body.linearVelocity : Vector2.zero;
        var vBefore = _mv.Velocity; var mBefore = _mv.momentum;
        string recoil = Recoil(_aim, def.RecoilMul);
        var vAfter = _mv.Velocity; var mAfter = _mv.momentum;
        _logBefore = before; _logShot = _shotNo; _logAfterIn = 2;
        if (Verbose || ShotLog != null)
        {
            var msg = "shot #" + _shotNo + " [" + def.Name + "] aim=" + _aim.ToString("F2") + " onGround=" + _mv.onGround + " | body vel before=" + before.ToString("F0") +
                      " | Velocity " + vBefore.ToString("F0") + " -> " + vAfter.ToString("F0") + ", momentum " + mBefore.ToString("F0") + " -> " + mAfter.ToString("F0") +
                      " | " + recoil + " | bullet speed " + speed.ToString("F0") + "/s";
            if (Verbose) _mod.Host.Log(msg);
            ShotLog?.Invoke(msg);
        }
        return true;
    }

    /// <summary>Self-test: send one real bullet from a given point (same stepping, hits and breaking as a shot).</summary>
    internal void FireFrom(Vector2 origin, Vector2 dir)
    {
        var cfg = _mod.Config; var def = _def;
        float speed = (cfg.BulletSpeed > 0f ? cfg.BulletSpeed : _unit * 70f) * def.SpeedMul;
        Fx.Spawn(def, origin, dir.normalized, 0f, cfg.BulletLifetime * def.LifeMul, speed);
    }

    private static Vector2 Rot(Vector2 v, float deg)
    {
        float r = deg * Mathf.Deg2Rad, c = Mathf.Cos(r), sn = Mathf.Sin(r);
        return new Vector2(v.x * c - v.y * sn, v.x * sn + v.y * c);
    }


    internal void FixedTick()
    {
        if (_logAfterIn > 0 && _body != null && --_logAfterIn == 0 && (Verbose || ShotLog != null))
        {
            var msg = "shot #" + _logShot + " result: body vel " + _logBefore.ToString("F0") + " -> " + ((Vector2)_body.linearVelocity).ToString("F0") + " two physics steps later";
            if (Verbose) _mod.Host.Log(msg);
            ShotLog?.Invoke(msg);
        }
    }

    private const float HorizontalKick = 0.6f;    // x run speed
    private const float UpKick = 0.55f;           // x jump force per boosting shot
    private const float DownKick = 0.15f;         // x jump force when shooting up in the air

    private string Recoil(Vector2 aim, float gunMul)
    {
        var cfg = _mod.Config;
        float s = cfg.RecoilStrength * gunMul;
        if (s <= 0.001f) return "recoil off";
        var mv = _mv;
        if (mv.dashActive) return "no recoil (dashing)";
        if (mv.cutsceneMode != Movement.cutsceneModes.none) return "no recoil (" + mv.cutsceneMode + ")";

        var push = -aim;
        var notes = "";
        float runMax = mv.runSpeed * 10f;
        if (Mathf.Abs(push.x) > 0.05f)
        {
            if (mv.wallJumpMovementLockRemaining > 0f) notes += "x skipped (wall-jump lock) ";
            else
            {
                // No speed cap: the strength (up to 10000%) decides how hard you're pushed.
                float mx = mv.momentum.x + push.x * s * HorizontalKick * runMax;
                mv.momentum = new Vector2(mx, mv.momentum.y);
                notes += "x kick ";
            }
        }
        float jump = Mathf.Max(1f, mv.jumpForce);
        // No upward cap and no limit on boosts per jump: every shot down in the air adds lift.
        if (push.y > 0.05f)
        {
            if (mv.onGround) notes += "no lift (on ground)";
            else
            {
                float vy = mv.Velocity.y;
                float target = Mathf.Max(vy, 0f) + push.y * s * UpKick * jump;
                if (target > vy) { mv.Velocity = new Vector2(mv.Velocity.x, target); _airBoosts++; notes += "lift #" + _airBoosts; }
                else notes += "no lift";
            }
        }
        else if (push.y < -0.05f && !mv.onGround)
        {
            mv.Velocity = new Vector2(mv.Velocity.x, mv.Velocity.y + push.y * s * DownKick * jump);
            notes += "down kick";
        }
        return notes.Length > 0 ? notes : "no vertical kick";
    }

    // ---------- bullets ----------

    private void StepBullets(float dt)
    {
        if (dt <= 0f || !_filterReady || Fx.Bullets == null) return;
        foreach (var b in Fx.Bullets)
        {
            if (!b.Active) continue;
            float dist = b.Speed * dt + b.Lead;
            b.Lead = 0f;
            bool stopped = false;
            int n = Physics2D.Raycast(b.Pos, b.Dir, _filter, _hits, dist);
            for (int i = 0; i < n; i++)
            {
                var h = _hits[i];
                var c = h.collider;
                if (c == null || c.CompareTag("Player") || c.attachedRigidbody == _body) continue;
                if (c.isTrigger && c.GetComponent<spikeScript>() == null && c.GetComponentInParent<spikeScript>() == null) continue;
                // hit: stop the bullet here (decoration on the way is cleared first)
                if (_mod.Config.BreakTiles && Deco.Sweep(b.Pos, h.point, _unit, Fx) > 0) { Breaks++; if (Verbose) _mod.Host.Log("bullet " + b.Id + ": " + Deco.LastInfo); }
                b.Pos = h.point;
                Hits++; LastHitPoint = h.point; LastHitName = c.name;
                Fx.Place(b);
                Fx.Impact(b.Def, h.point, h.normal);
                bool broke = TileBreaker.Hit(c, h.point, h.normal, _mod.Config, b.Def.BreakExtra, Fx, Verbose ? (Action<string>)(m => _mod.Host.Log("bullet " + b.Id + ": " + m)) : null, b.Dir);
                if (broke) Breaks++;
                if (Verbose)
                    _mod.Host.Log("bullet " + b.Id + " path " + b.Origin.ToString("F0") + " -> " + h.point.ToString("F0") + " (" + (h.point - b.Origin).magnitude.ToString("F0") + " units) hit '" + c.name + "' layer " + LayerMask.LayerToName(c.gameObject.layer) + (broke ? " BROKE" : ""));
                Fx.Kill(b);
                stopped = true;
                break;
            }
            if (stopped) continue;
            if (_mod.Config.BreakTiles && Deco.Sweep(b.Pos, b.Pos + b.Dir * dist, _unit, Fx) > 0) { Breaks++; if (Verbose) _mod.Host.Log("bullet " + b.Id + ": " + Deco.LastInfo); }
            b.Pos += b.Dir * dist;
            b.Travelled += dist;
            b.Life -= dt;
            if (b.Life <= 0f)
            {
                if (Verbose) _mod.Host.Log("bullet " + b.Id + " expired after " + b.Travelled.ToString("F0") + " units, no hit");
                Fx.Kill(b);
                continue;
            }
            Fx.Place(b);
            if (b.Def.Smoke && Time.time >= b.NextPuff)
            {
                b.NextPuff = Time.time + 0.022f;
                Fx.Burst(b.Pos - b.Dir * (_unit * 0.25f), 1, new Color(0.75f, 0.75f, 0.78f), _unit * 1.2f, 0.45f, Vector2.zero, 1.7f, -0.2f);
            }
        }
    }
}
