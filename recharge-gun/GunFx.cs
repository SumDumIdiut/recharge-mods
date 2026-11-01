using System;
using System.Reflection;
using UnityEngine;

/// <summary>
/// Pixel-art sprites drawn in code (from the GunDef table), and the pooled bullets / sparks / pops / shot sound.
/// Everything is allocated once (Warmup); firing afterwards allocates nothing.
/// </summary>
internal class GunFx
{
    internal class Bullet
    {
        public Transform T; public SpriteRenderer R; public GunDef Def;
        public bool Active; public Vector2 Pos, Dir; public float Life, Lead, Speed, NextPuff;
        public Vector2 Origin; public float Travelled; public bool FlipBase;
        public int Id;
    }

    private class Spark
    {
        public Transform T; public SpriteRenderer R;
        public bool Active; public Vector2 Pos, Vel; public float Life, MaxLife, Size, Grav, Swirl; public Color Color;
    }

    private class Pop
    {
        public Transform T; public SpriteRenderer R;
        public bool Active; public float Life, MaxLife, S0, S1, Aspect; public Color Color;
        public Sprite[] Frames;                 // null = a single fixed sprite (the ring)
    }

    private readonly GunMod _mod;
    private GameObject _root;
    private Material _mat;
    private Sprite _sparkSp, _ringSp;
    private Bullet[] _bullets;
    private Spark[] _sparks;
    private Pop[] _pops;
    private int _sparkCursor, _popCursor;
    private AudioSource _audio;
    private AudioClip _shotClip, _hitClip;
    private bool _audioTried;
    private int _sortLayer; private int _sortOrder;
    private float _u = 60f;

    internal Bullet LastSpawned;
    internal int FreezeFrame = -1;          // self-test: hold the impact animation on this frame (-1 = play normally)
    internal Material Mat => _mat;
    internal Bullet[] Bullets => _bullets;
    internal int ActiveBullets { get { int n = 0; if (_bullets != null) foreach (var b in _bullets) if (b.Active) n++; return n; } }
    internal int ActiveParticles { get { int n = 0; if (_sparks != null) { foreach (var s in _sparks) if (s.Active) n++; foreach (var p in _pops) if (p.Active) n++; } return n; } }

    internal GunFx(GunMod mod) { _mod = mod; }

    internal void SetScale(float unit, int sortLayerId, int sortOrder)
    {
        _u = Mathf.Max(8f, unit);
        _sortLayer = sortLayerId; _sortOrder = sortOrder;
        if (_bullets != null)
            foreach (var b in _bullets) if (b.R != null) { b.R.sortingLayerID = sortLayerId; b.R.sortingOrder = sortOrder; }
        if (_sparks != null)
            foreach (var s in _sparks) if (s.R != null) { s.R.sortingLayerID = sortLayerId; s.R.sortingOrder = sortOrder + 1; }
        if (_pops != null)
            foreach (var p in _pops) if (p.R != null) { p.R.sortingLayerID = sortLayerId; p.R.sortingOrder = sortOrder + 2; }
    }

    internal Material MakeMaterial()
    {
        if (_mat != null) return _mat;
        var shader = Shader.Find("Universal Render Pipeline/2D/Sprite-Unlit-Default") ?? Shader.Find("Sprites/Default");
        _mat = new Material(shader) { name = "RechargeGunUnlit" };
        return _mat;
    }

    // ---------- textures ----------

    internal static Sprite MakeSprite(string[] rows, Vector2 pivot, out float aspect)
    {
        int h = rows.Length, w = 0;
        foreach (var r in rows) if (r.Length > w) w = r.Length;
        var tex = new Texture2D(w, h, TextureFormat.RGBA32, false) { filterMode = FilterMode.Point, wrapMode = TextureWrapMode.Clamp };
        var px = new Color32[w * h];
        for (int y = 0; y < h; y++)
            for (int x = 0; x < w; x++)
                px[(h - 1 - y) * w + x] = x < rows[y].Length ? GunDefs.Pal(rows[y][x]) : GunDefs.Pal('.');
        tex.SetPixels32(px);
        tex.Apply(false, true);
        aspect = (float)h / w;
        // pixelsPerUnit = width, so the sprite is exactly 1 world unit wide at scale 1; transforms set the size
        return Sprite.Create(tex, new Rect(0, 0, w, h), pivot, w);
    }

    internal static Sprite[] MakeFrames(string[][] frames, Vector2 pivot, out float aspect)
    {
        var res = new Sprite[frames.Length];
        aspect = 1f;
        for (int i = 0; i < frames.Length; i++) res[i] = MakeSprite(frames[i], pivot, out aspect);
        return res;
    }

    internal void Warmup()
    {
        if (_root != null && _bullets != null) return;
        MakeMaterial();
        _root = new GameObject("RechargeGunFx");
        UnityEngine.Object.DontDestroyOnLoad(_root);

        foreach (var d in GunDefs.All)
        {
            if (d.GunSp == null)
            {
                d.GunSp = MakeSprite(d.Art, new Vector2(GunDef.PivotX, 1f - d.BarrelRow / d.Art.Length), out d.GunAspect);
                d.BulletSps = MakeFrames(d.BulletFrames, new Vector2(0.5f, 0.5f), out d.BulletAspect);
                d.FlashSps = MakeFrames(d.FlashFrames, new Vector2(d.FlashPivotX, 0.5f), out d.FlashAspect);
                if (d.ImpactFrames != null) d.PopSps = MakeFrames(d.ImpactFrames, new Vector2(0.5f, 0.5f), out d.PopAspect);
            }
        }
        _sparkSp = MakeSprite(new[] { "ww", "ww" }, new Vector2(0.5f, 0.5f), out _);
        _ringSp = MakeSprite(new[]
        {
            "..wwwww..", ".w.....w.", "w.......w", "w.......w", "w.......w", "w.......w", "w.......w", ".w.....w.", "..wwwww..",
        }, new Vector2(0.5f, 0.5f), out _);

        _bullets = new Bullet[48];
        for (int i = 0; i < _bullets.Length; i++)
        {
            var go = new GameObject("bullet");
            go.transform.SetParent(_root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = GunDefs.All[0].BulletSps[0]; r.sharedMaterial = _mat; r.sortingLayerID = _sortLayer; r.sortingOrder = _sortOrder;
            go.SetActive(false);
            _bullets[i] = new Bullet { T = go.transform, R = r, Id = i };
        }
        _sparks = new Spark[160];
        for (int i = 0; i < _sparks.Length; i++)
        {
            var go = new GameObject("spark");
            go.transform.SetParent(_root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = _sparkSp; r.sharedMaterial = _mat; r.sortingLayerID = _sortLayer; r.sortingOrder = _sortOrder + 1;
            go.SetActive(false);
            _sparks[i] = new Spark { T = go.transform, R = r };
        }
        _pops = new Pop[16];
        for (int i = 0; i < _pops.Length; i++)
        {
            var go = new GameObject("pop");
            go.transform.SetParent(_root.transform, false);
            var r = go.AddComponent<SpriteRenderer>();
            r.sprite = _ringSp; r.sharedMaterial = _mat; r.sortingLayerID = _sortLayer; r.sortingOrder = _sortOrder + 2;
            go.SetActive(false);
            _pops[i] = new Pop { T = go.transform, R = r };
        }
        _audio = _root.AddComponent<AudioSource>();
        _audio.playOnAwake = false; _audio.spatialBlend = 0f;
    }

    internal void Dispose()
    {
        if (_root != null) UnityEngine.Object.Destroy(_root);
        _root = null; _bullets = null; _sparks = null; _pops = null;
    }

    // ---------- bullets ----------

    internal Bullet Spawn(GunDef def, Vector2 pivot, Vector2 dir, float lead, float lifetime, float speed)
    {
        if (_bullets == null || _root == null) Warmup();
        Bullet pick = null;
        foreach (var b in _bullets) if (!b.Active) { pick = b; break; }
        if (pick == null)
        {
            // pool exhausted: recycle the oldest
            pick = _bullets[0];
            foreach (var b in _bullets) if (b.Life < pick.Life) pick = b;
        }
        pick.Def = def; pick.Speed = speed; pick.NextPuff = 0f;
        pick.Active = true; pick.Pos = pivot; pick.Origin = pivot; pick.Dir = dir; pick.Lead = lead; pick.Life = lifetime; pick.Travelled = 0f;
        pick.FlipBase = dir.x < -0.0001f;
        pick.R.sprite = def.BulletSps[0]; pick.R.flipY = pick.FlipBase;
        pick.T.gameObject.SetActive(true);
        float len = _u * def.BulletLen;
        pick.T.localScale = new Vector3(len, len * def.BulletAspect, 1f);
        pick.T.position = new Vector3(pivot.x + dir.x * lead, pivot.y + dir.y * lead, 0f);
        pick.T.rotation = Quaternion.Euler(0f, 0f, Mathf.Atan2(dir.y, dir.x) * Mathf.Rad2Deg);
        LastSpawned = pick;
        return pick;
    }

    internal void Kill(Bullet b)
    {
        b.Active = false;
        if (b.T != null) b.T.gameObject.SetActive(false);
    }

    internal void Place(Bullet b)
    {
        b.T.position = new Vector3(b.Pos.x, b.Pos.y, 0f);
        // frame cycle (rocket flame flicker, plasma pulse, beam phase): no allocation, just a sprite swap
        var fr = b.Def.BulletSps;
        if (fr.Length > 1) b.R.sprite = fr[((int)(Time.time * (b.Def.Wavy ? 18f : 14f)) + b.Id) % fr.Length];
    }

    // ---------- particles ----------

    internal void Burst(Vector2 at, int count, Color color, float speedUnits, float life, Vector2 bias, float size = 1f, float grav = 1f, float swirl = 0f)
    {
        if (_sparks == null) return;
        for (int i = 0; i < count; i++)
        {
            var s = _sparks[_sparkCursor]; _sparkCursor = (_sparkCursor + 1) % _sparks.Length;
            var a = UnityEngine.Random.value * Mathf.PI * 2f;
            var v = new Vector2(Mathf.Cos(a), Mathf.Sin(a)) * (speedUnits * UnityEngine.Random.Range(0.4f, 1f)) + bias * speedUnits * 0.6f;
            s.Active = true; s.Pos = at; s.Vel = v; s.MaxLife = s.Life = life * UnityEngine.Random.Range(0.6f, 1f);
            s.Size = _u * UnityEngine.Random.Range(0.06f, 0.12f) * size; s.Color = color; s.Grav = grav; s.Swirl = swirl;
            s.R.color = color;
            s.T.position = new Vector3(at.x, at.y, 0f); s.T.localScale = new Vector3(s.Size, s.Size, 1f);
            s.T.gameObject.SetActive(true);
        }
    }

    internal void StepSparks(float dt)
    {
        if (dt <= 0f) return;
        if (_sparks != null)
            foreach (var s in _sparks)
            {
                if (!s.Active) continue;
                s.Life -= dt;
                if (s.Life <= 0f) { s.Active = false; s.T.gameObject.SetActive(false); continue; }
                s.Vel.y -= _u * 6f * s.Grav * dt;
                if (s.Swirl != 0f)
                {
                    float c = Mathf.Cos(s.Swirl * dt), sn = Mathf.Sin(s.Swirl * dt);
                    s.Vel = new Vector2(s.Vel.x * c - s.Vel.y * sn, s.Vel.x * sn + s.Vel.y * c);
                }
                s.Pos += s.Vel * dt;
                float k = s.Life / s.MaxLife;
                s.T.position = new Vector3(s.Pos.x, s.Pos.y, 0f);
                s.T.localScale = new Vector3(s.Size * k, s.Size * k, 1f);
                var col = s.Color; col.a = k; s.R.color = col;
            }
        if (_pops != null)
            foreach (var p in _pops)
            {
                if (!p.Active) continue;
                p.Life -= dt;
                if (p.Life <= 0f) { p.Active = false; p.T.gameObject.SetActive(false); continue; }
                float k = 1f - p.Life / p.MaxLife;                      // 0 -> 1
                float sz = Mathf.Lerp(p.S0, p.S1, Mathf.Sqrt(k));
                p.T.localScale = new Vector3(sz, sz * p.Aspect, 1f);
                if (p.Frames != null)
                {
                    int fi = Mathf.Min(p.Frames.Length - 1, (int)(k * p.Frames.Length));
                    if (FreezeFrame >= 0) fi = Mathf.Min(p.Frames.Length - 1, FreezeFrame);
                    p.R.sprite = p.Frames[fi];
                    var c2 = p.Color; c2.a = k < 0.7f ? 1f : 1f - (k - 0.7f) / 0.3f; p.R.color = c2;
                }
                else { var col = p.Color; col.a = 1f - k * k; p.R.color = col; }
            }
    }

    private void StartPop(Sprite[] frames, Sprite single, float aspect, Vector2 at, float s0, float s1, float life, Color color)
    {
        var p = _pops[_popCursor]; _popCursor = (_popCursor + 1) % _pops.Length;
        p.Active = true; p.Life = p.MaxLife = life; p.S0 = s0; p.S1 = s1; p.Aspect = aspect; p.Color = color;
        p.Frames = frames;
        p.R.sprite = frames != null ? frames[0] : single; p.R.color = color;
        p.T.position = new Vector3(at.x, at.y, 0f); p.T.localScale = new Vector3(s0, s0 * aspect, 1f);
        p.T.gameObject.SetActive(true);
    }

    /// <summary>The gun's own impact effect: a pop sprite (and ring) plus a few sparks.</summary>
    internal void Impact(GunDef d, Vector2 at, Vector2 normal)
    {
        if (_pops == null) return;
        if (d.PopSps != null) StartPop(d.PopSps, null, d.PopAspect, at + normal * (_u * d.PopTo * 0.28f), _u * d.PopFrom, _u * d.PopTo, d.PopLife, d.PopColor);
        if (d.Ring) StartPop(null, _ringSp, 1f, at, _u * 0.3f, _u * d.RingTo, d.PopLife * 1.2f, d.RingColor);
        int half = d.SparkCount / 2;
        Burst(at, d.SparkCount - half, d.SparkA, _u * d.SparkSpeed, d.SparkLife, normal, d.SparkSize, d.SparkGravity, d.SparkSwirl);
        Burst(at, half, d.SparkB, _u * d.SparkSpeed, d.SparkLife, normal, d.SparkSize, d.SparkGravity, d.SparkSwirl);
    }

    /// <summary>Grey puffs at the muzzle (shotgun, rocket).</summary>
    internal void MuzzlePuffs(GunDef d, Vector2 muzzle, Vector2 aim)
    {
        if (d.MuzzleSmoke <= 0) return;
        Burst(muzzle, d.MuzzleSmoke, new Color(0.7f, 0.7f, 0.72f), _u * 2.5f, 0.45f, aim, 1.8f, -0.25f);
    }

    // ---------- sound ----------

    private void FindClips()
    {
        _audioTried = true;
        try
        {
            var audio = Singleton<Audio>.Instance;
            if (audio == null) { _audioTried = false; return; }
            var lib = typeof(Audio).GetField("audioLibrary", BindingFlags.Instance | BindingFlags.NonPublic)?.GetValue(audio) as Array;
            if (lib == null) return;
            foreach (var entry in lib)
            {
                var t = entry.GetType();
                var id = (Audio.AUDIO_ID)t.GetField("id").GetValue(entry);
                var clips = t.GetField("clips").GetValue(entry) as AudioClip[];
                if (clips == null || clips.Length == 0) continue;
                if (id == Audio.AUDIO_ID.BoxHit && _shotClip == null)
                {
                    _shotClip = clips[0];
                    var group = t.GetField("mixerGroup").GetValue(entry) as UnityEngine.Audio.AudioMixerGroup;
                    if (_audio != null) _audio.outputAudioMixerGroup = group;
                }
                if (id == Audio.AUDIO_ID.MetalLand && _hitClip == null) _hitClip = clips[0];
            }
            _mod.Host.Log("shot sound: " + (_shotClip != null ? "game's BoxHit '" + _shotClip.name + "'" : "none found"));
        }
        catch (Exception e) { _mod.Host.LogWarning("audio lookup failed: " + e.Message); }
    }

    internal void PlayShot()
    {
        if (_audio == null) return;
        if (!_audioTried) FindClips();
        if (_shotClip == null) return;
        _audio.pitch = UnityEngine.Random.Range(1.55f, 1.85f);
        _audio.PlayOneShot(_shotClip, 0.5f);
    }

    internal void PlayHit()
    {
        if (_audio == null || _hitClip == null) return;
        _audio.pitch = UnityEngine.Random.Range(1.6f, 2.1f);
        _audio.PlayOneShot(_hitClip, 0.25f);
    }
}
