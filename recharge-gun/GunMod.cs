using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using Newtonsoft.Json;
using Recharge.ModApi;
using UnityEngine;
using UnityEngine.SceneManagement;

/// <summary>Persisted settings (config.json in the mod's data folder).</summary>
public class GunConfig
{
    public bool Enabled = true;
    /// <summary>Multiplier on every recoil kick (0 = no recoil).</summary>
    public float RecoilStrength = 1f;
    /// <summary>Upward speed cap for gun-jumps, as a fraction of the player's jump force.</summary>
    public float MaxRecoilSpeed = 0.75f;
    /// <summary>Boosting shots allowed per airtime (reset on ground / wall / spring).</summary>
    public int MaxAirBoosts = 3;
    public float FireRate = 4f;
    /// <summary>World units per second; 0 = automatic (scaled to the player's size).</summary>
    public float BulletSpeed = 0f;
    public float BulletLifetime = 1.5f;
    /// <summary>Bullets remove tiles (and spikes / plain decor) they hit, until the scene reloads.</summary>
    public bool BreakTiles = true;
    /// <summary>Extra cells cleared around the hit cell (0 = just the one).</summary>
    public int BreakRadius = 0;
    public bool LeftMouseFires = true;
    /// <summary>The held gun (Pistol, Blaster, Shotgun, Ray gun, Rocket launcher); per install, not per save.</summary>
    public string GunName = "Pistol";
}

public class GunMod : IRechargeMod
{
    public string Id => "recharge.gun";
    public string DisplayName => "Gun";
    public Version Version => new Version(1, 0, 0);

    internal static GunMod Instance;
    internal IRechargeHost Host;
    internal GunConfig Config = new GunConfig();
    internal GunBox Box;
    internal Hotbar Bar;
    internal GunController Gun;
    internal SelfTest Test;

    private Dictionary<string, bool> _unlocks = new Dictionary<string, bool>();
    private Movement _player;
    private float _nextFind, _playerFoundAt;
    private Action _upd, _late, _fixed;
    private Action<object> _sceneHandler;
    private bool _unloaded;

    public void OnLoad(IRechargeHost host)
    {
        Instance = this;
        Host = host;
        try { Config = host.LoadConfig<GunConfig>(Id) ?? new GunConfig(); } catch (Exception e) { host.LogWarning("config load failed: " + e.Message); }
        ClampConfig();
        Config.GunName = GunDefs.Find(Config.GunName).Name;
        host.SaveConfig(Id, Config);
        LoadUnlocks();
        host.Log("loaded; enabled=" + Config.Enabled + " fireRate=" + Config.FireRate + " recoil=" + Config.RecoilStrength + " breakTiles=" + Config.BreakTiles + " slot='" + SlotKey() + "' unlocked=" + IsUnlocked());

        Deco.Log = m => host.Log(m);
        Gun = new GunController(this);
        Box = new GunBox(this);
        Bar = new Hotbar(this);
        Test = new SelfTest(this);

        _upd = Update; _late = LateUpdate; _fixed = FixedTick;
        host.OnUpdate += _upd;
        host.OnLateUpdate += _late;
        host.OnFixedUpdate += _fixed;
        _sceneHandler = _ => { _player = null; _nextFind = 0f; Deco.Invalidate(); TileBreaker.Invalidate(); };
        host.Events.On(RechargeEvents.SceneLoaded, _sceneHandler);

        GunMenu.Install(this, host.PauseMenu);
        PauseMenuHelper.OnMenuReady(host, m => GunMenu.Install(this, m));

        Test.Begin();
    }

    public void OnUnload()
    {
        _unloaded = true;
        try
        {
            if (Host != null)
            {
                Host.OnUpdate -= _upd;
                Host.OnLateUpdate -= _late;
                Host.OnFixedUpdate -= _fixed;
                if (_sceneHandler != null) Host.Events.Off(RechargeEvents.SceneLoaded, _sceneHandler);
            }
            Test?.Dispose();
            Gun?.Dispose();
            Box?.Dispose();
            Bar?.Dispose();
        }
        catch (Exception e) { Host?.LogWarning("unload: " + e.Message); }
        if (Instance == this) Instance = null;
    }

    private void ClampConfig()
    {
        Config.FireRate = Mathf.Clamp(Config.FireRate, 0.5f, 30f);
        Config.RecoilStrength = Mathf.Clamp(Config.RecoilStrength, 0f, 100f); // 0 - 10000%
        Config.MaxRecoilSpeed = Mathf.Clamp(Config.MaxRecoilSpeed, 0.1f, 2f);
        Config.MaxAirBoosts = Mathf.Clamp(Config.MaxAirBoosts, 0, 20);
        Config.BulletLifetime = Mathf.Clamp(Config.BulletLifetime, 0.2f, 5f);
        Config.BreakRadius = Mathf.Clamp(Config.BreakRadius, 0, 3);
    }

    internal GunDef CurrentGun => GunDefs.Find(Config.GunName);

    /// <summary>Pick the held gun: saved in config.json, applied immediately.</summary>
    internal void SetGun(GunDef def)
    {
        if (def == null) return;
        Config.GunName = def.Name;
        SaveConfig();
        Gun?.SetGun(def);
    }

    internal void SaveConfig()
    {
        ClampConfig();
        try { Host?.SaveConfig(Id, Config); } catch (Exception e) { Host?.LogWarning("config save failed: " + e.Message); }
    }

    // ---- the game's player (the title screen keeps its own Movement alive) ----

    internal Movement Player => _player;

    private static readonly FieldInfo IsOnMainMenu = typeof(Movement).GetField("isOnMainMenu", BindingFlags.Instance | BindingFlags.NonPublic | BindingFlags.Public);

    private static Movement FindGamePlayer()
    {
        foreach (var mv in UnityEngine.Object.FindObjectsByType<Movement>(FindObjectsSortMode.None))
        {
            if (mv == null) continue;
            if (IsOnMainMenu != null && (bool)IsOnMainMenu.GetValue(mv)) continue;
            return mv;
        }
        return null;
    }

    private void Update()
    {
        if (_unloaded) return;
        try
        {
            if (_player == null && Time.unscaledTime >= _nextFind)
            {
                _nextFind = Time.unscaledTime + 0.5f;
                _player = FindGamePlayer();
                if (_player != null)
                {
                    _playerFoundAt = Time.unscaledTime;
                    Host.Log("gameplay player found in scene '" + SceneManager.GetActiveScene().name + "' at " + (Vector2)_player.transform.position + " respawnPoint=" + _player.respawnPoint);
                    Gun.Attach(_player);
                    Box.OnPlayerFound(_player);
                }
            }
            if (_player != null)
            {
                Box.Tick(_player, Time.unscaledTime - _playerFoundAt);
                Gun.Tick(_player);
                Bar.Tick();
            }
            else Bar.Hide();
        }
        catch (Exception e) { Host.LogError("update: " + e); }
    }

    private void LateUpdate()
    {
        if (_unloaded) return;
        try
        {
            Box.LateTick();
            if (_player != null) Gun.LateTick(_player);
        }
        catch (Exception e) { Host.LogError("late update: " + e); }
    }

    private void FixedTick()
    {
        if (_unloaded) return;
        try { Gun.FixedTick(); } catch (Exception e) { Host.LogError("fixed update: " + e); }
    }

    // ---- unlock state, per save slot ----

    private string UnlockFile => Path.Combine(Host.ModDataDir(Id), "unlocks.json");

    private void LoadUnlocks()
    {
        try
        {
            if (File.Exists(UnlockFile))
                _unlocks = JsonConvert.DeserializeObject<Dictionary<string, bool>>(File.ReadAllText(UnlockFile)) ?? new Dictionary<string, bool>();
        }
        catch (Exception e) { Host.LogWarning("unlocks.json unreadable, starting locked: " + e.Message); _unlocks = new Dictionary<string, bool>(); }
    }

    private void SaveUnlocks()
    {
        try { File.WriteAllText(UnlockFile, JsonConvert.SerializeObject(_unlocks, Formatting.Indented)); }
        catch (Exception e) { Host.LogWarning("saving unlocks failed: " + e.Message); }
    }

    internal bool IsUnlocked() => _unlocks.TryGetValue(SlotKey(), out var v) && v;

    internal void SetUnlocked(bool value)
    {
        var key = SlotKey();
        _unlocks[key] = value;
        SaveUnlocks();
        Host.Log((value ? "UNLOCKED" : "re-locked") + " gun for save slot '" + key + "'");
    }

    private Type _mapSaves;
    private float _mapSavesNext;

    /// <summary>The save slot: the base game's save (normal / hard), or the active Navigator map's own save.</summary>
    internal string SlotKey()
    {
        try
        {
            var id = ActiveMapId();
            if (!string.IsNullOrEmpty(id)) return "map:" + id;
        }
        catch { /* Navigator not there or changed shape: base game */ }
        return globalStats.difficultyLevel == 1 ? "base-hard" : "base";
    }

    internal bool NavigatorMapActive => !string.IsNullOrEmpty(SafeMapId());

    private string SafeMapId() { try { return ActiveMapId(); } catch { return null; } }

    private string ActiveMapId()
    {
        // Navigator (recharge.maps) loads after this mod, so keep looking until its types turn up
        if (_mapSaves == null && Time.realtimeSinceStartup >= _mapSavesNext)
        {
            _mapSavesNext = Time.realtimeSinceStartup + 1f;
            foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
            {
                Type t = null;
                try { t = asm.GetType("MapSaves", false); } catch { }
                if (t != null) { _mapSaves = t; break; }
            }
        }
        if (_mapSaves == null) return null;
        const BindingFlags f = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
        var p = _mapSaves.GetProperty("ActiveMapId", f);
        if (p != null) return p.GetValue(null) as string;
        return _mapSaves.GetField("ActiveMapId", f)?.GetValue(null) as string;
    }

    // ---- Navigator build state (read by reflection; Navigator is optional and not modified) ----

    internal struct NavState { public bool Active, Built, HasSpawn; public string MapId; public Vector2 Spawn; }

    private static Type FindType(string name)
    {
        foreach (var asm in AppDomain.CurrentDomain.GetAssemblies())
        {
            try { var t = asm.GetType(name, false); if (t != null) return t; } catch { }
        }
        return null;
    }

    private Type _mapManager, _mapWorld;
    private float _nvNext;

    /// <summary>
    /// Active = a Navigator map is chosen or loaded (so the base-game spawn is not the one to use yet).
    /// Built = MapManager has finished building that map; Spawn = the map's own spawn point in live coordinates.
    /// </summary>
    internal NavState NavigatorState()
    {
        var st = new NavState();
        try
        {
            if ((_mapManager == null || _mapWorld == null) && Time.realtimeSinceStartup >= _nvNext) { _nvNext = Time.realtimeSinceStartup + 1f; _mapManager = FindType("MapManager"); _mapWorld = FindType("MapWorld"); }
            if (_mapManager == null || _mapWorld == null) return st;
            const BindingFlags sf = BindingFlags.Static | BindingFlags.Public | BindingFlags.NonPublic;
            const BindingFlags inf = BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic;
            var active = SafeMapId();
            var pending = _mapManager.GetProperty("Pending", sf)?.GetValue(null) as bool? ?? false;
            st.Active = !string.IsNullOrEmpty(active) || pending;
            if (!st.Active) return st;
            var mgr = _mapManager.GetProperty("Instance", sf)?.GetValue(null);
            var world = mgr != null ? _mapManager.GetField("_world", inf)?.GetValue(mgr) : null;
            if (world == null || pending) return st;
            st.MapId = _mapWorld.GetField("MapId", inf)?.GetValue(world) as string;
            if (string.IsNullOrEmpty(st.MapId) || st.MapId != active) return st;
            st.Built = true;

            var group = _mapWorld.GetField("Group", inf)?.GetValue(world);
            var overlay = (bool)_mapWorld.GetField("Overlay", inf).GetValue(world);
            var origin = (Vector2)_mapWorld.GetField("Origin", inf).GetValue(world);
            if (group == null) return st;
            var gt = group.GetType();
            float? sx = gt.GetField("SpawnX", inf)?.GetValue(group) as float?;
            float? sy = gt.GetField("SpawnY", inf)?.GetValue(group) as float?;
            bool keep = (gt.GetField("KeepSpawn", inf)?.GetValue(group) as bool?) ?? false;
            // an overlay map without a placed spawn keeps the game's own start: the respawn point
            if (overlay && keep && sx == null) return st;
            float x = sx ?? (float)gt.GetField("StartX", inf).GetValue(group);
            float y = sy ?? (float)gt.GetField("StartY", inf).GetValue(group);
            var fo = Singleton<FloatingOrigin>.Instance != null ? (Vector2)Singleton<FloatingOrigin>.Instance.currentOrigin : Vector2.zero;
            st.Spawn = origin + new Vector2(x, y) + fo;
            st.HasSpawn = true;
        }
        catch (Exception e) { Host?.LogWarning("Navigator state unreadable: " + e.Message); }
        return st;
    }
}
