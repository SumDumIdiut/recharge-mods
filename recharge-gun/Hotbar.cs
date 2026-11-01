using System;
using System.Collections.Generic;
using System.Text;
using TMPro;
using UnityEngine;
using UnityEngine.InputSystem;
using UnityEngine.UI;

/// <summary>
/// Bottom-left HUD strip: one slot per gun with its pixel-art icon and number 1-5; the held gun is lit.
/// Keys 1-5 pick a gun; on a controller the d-pad left/right cycle (or the shoulder buttons if the game
/// binds the d-pad to something in gameplay). Shown only while the gun is unlocked, on and visible, and
/// hidden in pause / menus and while the game has its HUD tucked away. Every pick goes through
/// GunMod.SetGun, the same call the pause-menu picker uses; the highlight just follows the held gun.
/// </summary>
internal class Hotbar
{
    private const int Slots = 5;
    private readonly GunMod _mod;
    private GameObject _root;
    private CanvasGroup _group;
    private Image[] _bg, _border, _icon;
    private TMP_Text[] _num;
    private int _lit = -1;
    private bool _visible;
    private bool _scanned;
    private bool _padUsesDpad = true;       // false: the game binds the d-pad in gameplay, use the shoulders
    private Key[] _keys = { Key.Digit1, Key.Digit2, Key.Digit3, Key.Digit4, Key.Digit5 };

    internal bool Visible => _visible;
    internal int Lit => _lit;
    internal string InputInfo = "";
    internal GameObject Root => _root;

    internal Hotbar(GunMod mod) { _mod = mod; }

    internal void Dispose() { if (_root != null) UnityEngine.Object.Destroy(_root); _root = null; }

    // ---------- input bindings check ----------

    private void ScanBindings()
    {
        if (_scanned) return;
        var asset = InputSystem.actions;
        if (asset == null) return;
        _scanned = true;
        var sb = new StringBuilder();
        bool digitClash = false, dpadClash = false, shoulderClash = false;
        foreach (var map in asset.actionMaps)
        {
            bool ui = map.name == "UI";
            foreach (var a in map.actions)
                foreach (var b in a.bindings)
                {
                    var p = b.effectivePath ?? "";
                    if (p.Length == 0) continue;
                    if (!ui) sb.Append(map.name).Append('/').Append(a.name).Append('=').Append(p).Append("; ");
                    var lp = p.ToLowerInvariant();
                    if (lp.StartsWith("<keyboard>/") && lp.Length > 11)
                    {
                        var k = lp.Substring(11);
                        if (k == "1" || k == "2" || k == "3" || k == "4" || k == "5" || k.StartsWith("digit")) digitClash = true;
                    }
                    if (!ui && lp.StartsWith("<gamepad>/dpad")) dpadClash = true;
                    if (!ui && (lp.Contains("shoulder"))) shoulderClash = true;
                }
        }
        if (digitClash) _keys = new[] { Key.Numpad1, Key.Numpad2, Key.Numpad3, Key.Numpad4, Key.Numpad5 };
        _padUsesDpad = !dpadClash;
        InputInfo = "game bindings (non-UI): " + sb + "| digit keys " + (digitClash ? "CLASH -> numpad 1-5" : "free -> keys 1-5") + ", d-pad " + (dpadClash ? "USED in gameplay -> controller uses LB/RB" : "free -> d-pad left/right") + (dpadClash && shoulderClash ? " (shoulders also bound!)" : "");
        _mod.Host.Log("hotbar: " + InputInfo);
    }

    // ---------- build ----------

    // The game's own HUD look (found in the scene, nothing is copied to disk): the "Foreground Screen_0" 9-sliced panel
    // of the power display and upgrade screens, its "Canvas monitor font", and the orange of its numbers.
    internal static readonly Color Orange = new Color(1f, 0.416f, 0f, 1f);
    private Image[] _frame, _under;
    private TMP_Text _label;
    internal string StyleInfo = "";
    private const float Slot = 40f, Gap = 4f, MarginX = 10f, MarginY = 22f;

    private static void FindHudArt(out Sprite panel, out float ppuMul, out TMP_FontAsset font)
    {
        panel = null; ppuMul = 1f; font = null;
        try
        {
            var cd = Singleton<CurrencyDisplayController>.Instance;
            var root = cd != null ? (Component)cd : null;
            var imgs = root != null ? root.GetComponentsInChildren<Image>(true) : new Image[0];
            foreach (var im in imgs)
                if (im != null && im.sprite != null && im.sprite.name == "Foreground Screen_0" && im.type == Image.Type.Sliced) { panel = im.sprite; ppuMul = im.pixelsPerUnitMultiplier; break; }
            if (panel == null)
                foreach (var im in UnityEngine.Object.FindObjectsByType<Image>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (im != null && im.sprite != null && im.sprite.name == "Foreground Screen_0" && im.gameObject.scene.IsValid()) { panel = im.sprite; ppuMul = im.pixelsPerUnitMultiplier; break; }
            var txts = root != null ? root.GetComponentsInChildren<TMP_Text>(true) : new TMP_Text[0];
            foreach (var t in txts) if (t != null && t.font != null && t.font.name == "Canvas monitor font") { font = t.font; break; }
            if (font == null)
                foreach (var t in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                    if (t != null && t.font != null && t.font.name == "Canvas monitor font" && t.gameObject.scene.IsValid()) { font = t.font; break; }
        }
        catch { }
    }

    private void Build()
    {
        FindHudArt(out var panel, out var ppuMul, out var font);
        if (font == null) font = FindFont();
        StyleInfo = "panel sprite " + (panel != null ? panel.name + " (9-sliced, border " + panel.border + ", ppu multiplier " + ppuMul + ")" : "NOT FOUND, flat fallback") + ", font " + (font != null ? font.name : "none") + ", accent " + Orange;
        _mod.Host.Log("hotbar style: " + StyleInfo);
        _root = new GameObject("RechargeGunHotbar");
        UnityEngine.Object.DontDestroyOnLoad(_root);
        var canvas = _root.AddComponent<Canvas>();
        canvas.renderMode = RenderMode.ScreenSpaceOverlay;
        canvas.sortingOrder = 100;
        var scaler = _root.AddComponent<CanvasScaler>();
        // the same scaler settings as the game's HUD canvas, so it grows and shrinks with the game's own UI
        scaler.uiScaleMode = CanvasScaler.ScaleMode.ScaleWithScreenSize;
        scaler.referenceResolution = new Vector2(800, 600);
        scaler.matchWidthOrHeight = 0.5f;
        try
        {
            var hud = Singleton<CurrencyDisplayController>.Instance != null ? Singleton<CurrencyDisplayController>.Instance.GetComponentInParent<Canvas>() : null;
            var hs = hud != null ? hud.rootCanvas.GetComponent<CanvasScaler>() : null;
            if (hs != null && hs.uiScaleMode == CanvasScaler.ScaleMode.ScaleWithScreenSize) { scaler.referenceResolution = hs.referenceResolution; scaler.matchWidthOrHeight = hs.matchWidthOrHeight; }
        }
        catch { }
        _group = _root.AddComponent<CanvasGroup>();
        _group.interactable = false; _group.blocksRaycasts = false;

        _bg = new Image[Slots]; _border = new Image[Slots]; _icon = new Image[Slots]; _num = new TMP_Text[Slots]; _frame = new Image[Slots]; _under = new Image[Slots];
        for (int i = 0; i < Slots; i++)
        {
            var slot = NewRect("Slot" + (i + 1), _root.transform);
            slot.anchorMin = slot.anchorMax = slot.pivot = Vector2.zero;
            slot.sizeDelta = new Vector2(Slot, Slot);
            slot.anchoredPosition = new Vector2(MarginX + i * (Slot + Gap), MarginY);
            // orange outline, only on the held gun
            var fr = NewRect("Frame", slot, true); fr.offsetMin = new Vector2(-2.5f, -2.5f); fr.offsetMax = new Vector2(2.5f, 2.5f);
            _frame[i] = Add<Image>(fr); StylePanel(_frame[i], panel, ppuMul); _frame[i].color = Orange; _frame[i].raycastTarget = false;
            var bgr = NewRect("Panel", slot, true);
            _bg[i] = Add<Image>(bgr); StylePanel(_bg[i], panel, ppuMul); _bg[i].raycastTarget = false;
            _border[i] = _frame[i];
            var ic = NewRect("Icon", slot, true); ic.offsetMin = new Vector2(5, 9); ic.offsetMax = new Vector2(-5, -9);
            _icon[i] = Add<Image>(ic);
            _icon[i].sprite = GunDefs.All[i].GunSp;
            _icon[i].preserveAspect = true;
            _icon[i].raycastTarget = false;
            var ur = NewRect("Base", slot); ur.anchorMin = new Vector2(0, 0); ur.anchorMax = new Vector2(1, 0); ur.pivot = new Vector2(0.5f, 0);
            ur.offsetMin = new Vector2(5, 3.5f); ur.offsetMax = new Vector2(-5, 6.5f);
            _under[i] = Add<Image>(ur); _under[i].color = Orange; _under[i].raycastTarget = false;
            var nr = NewRect("Num", slot);
            nr.anchorMin = new Vector2(0, 1); nr.anchorMax = new Vector2(0, 1); nr.pivot = new Vector2(0, 1);
            nr.anchoredPosition = new Vector2(5, -3); nr.sizeDelta = new Vector2(16, 12);
            var t = nr.gameObject.AddComponent<TextMeshProUGUI>();
            if (font != null) t.font = font;
            t.text = (i + 1).ToString(); t.fontSize = 10; t.raycastTarget = false;
            t.alignment = TextAlignmentOptions.TopLeft; t.enableWordWrapping = false;
            _num[i] = t;
        }
        var lr = NewRect("GunName", _root.transform);
        lr.anchorMin = lr.anchorMax = lr.pivot = Vector2.zero;
        lr.anchoredPosition = new Vector2(MarginX, 5f); lr.sizeDelta = new Vector2(Slots * (Slot + Gap), 14f);
        _label = lr.gameObject.AddComponent<TextMeshProUGUI>();
        if (font != null) _label.font = font;
        _label.fontSize = 9.5f; _label.color = Orange; _label.raycastTarget = false;
        _label.alignment = TextAlignmentOptions.MidlineLeft; _label.enableWordWrapping = false; _label.overflowMode = TextOverflowModes.Overflow;
        _lit = -1;
        _root.SetActive(false);
    }

    private static void StylePanel(Image im, Sprite panel, float ppuMul)
    {
        if (panel != null) { im.sprite = panel; im.type = Image.Type.Sliced; im.pixelsPerUnitMultiplier = ppuMul > 0f ? ppuMul : 1f; im.fillCenter = true; }
        else im.color = new Color(0.1f, 0.1f, 0.12f, 0.9f);
    }

    private static TMP_FontAsset FindFont()
    {
        try
        {
            foreach (var t in UnityEngine.Object.FindObjectsByType<TMP_Text>(FindObjectsInactive.Include, FindObjectsSortMode.None))
                if (t != null && t.font != null && t.gameObject.scene.IsValid()) return t.font;
            return TMP_Settings.defaultFontAsset;
        }
        catch { return null; }
    }

    private static RectTransform NewRect(string name, Transform parent, bool stretch = false)
    {
        var go = new GameObject(name, typeof(RectTransform));
        var rt = go.GetComponent<RectTransform>();
        rt.SetParent(parent, false);
        if (stretch) { rt.anchorMin = Vector2.zero; rt.anchorMax = Vector2.one; rt.offsetMin = rt.offsetMax = Vector2.zero; }
        return rt;
    }

    private static T Add<T>(RectTransform rt) where T : Component => rt.gameObject.AddComponent<T>();

    // ---------- state ----------

    private bool HudHidden()
    {
        try
        {
            var cd = Singleton<CurrencyDisplayController>.Instance;
            if (cd == null) return false;
            if (cd.activeTab == CurrencyDisplayController.CurrencyTabs.none) return true;
            var cv = cd.GetComponentInParent<Canvas>();
            if (cv != null && (!cv.enabled || !cv.gameObject.activeInHierarchy)) return true;
            var cg = cd.GetComponentInParent<CanvasGroup>();
            if (cg != null && cg.alpha < 0.05f) return true;
        }
        catch { }
        return false;
    }

    private bool MenuOpen()
    {
        try { var m = _mod.Host.PauseMenu; return m != null && (m.menuOpen || m.settingsMenuOpen); } catch { return false; }
    }

    internal bool Wanted()
    {
        if (!_mod.Config.Enabled || !_mod.IsUnlocked()) return false;
        if (_mod.Gun == null || !_mod.Gun.Visible) return false;
        if (Time.timeScale <= 0f || MenuOpen()) return false;
        if (HudHidden()) return false;
        return true;
    }

    internal void Hide() { if (_root != null && _visible) { _visible = false; _root.SetActive(false); } }

    internal void Tick()
    {
        ScanBindings();
        bool want = Wanted();
        if (want && _root == null) Build();
        if (_root != null && want != _visible) { _visible = want; _root.SetActive(want); }
        if (!want) { _visible = false; return; }
        _visible = true;
        // input
        var kb = Keyboard.current;
        if (kb != null)
            for (int i = 0; i < Slots; i++)
                if (kb[_keys[i]].wasPressedThisFrame) { Pick(i); break; }
        var pad = Gamepad.current;
        if (pad != null)
        {
            var l = _padUsesDpad ? pad.dpad.left : pad.leftShoulder;
            var r = _padUsesDpad ? pad.dpad.right : pad.rightShoulder;
            if (l.wasPressedThisFrame) Cycle(-1);
            else if (r.wasPressedThisFrame) Cycle(1);
        }
        Refresh();
    }

    internal void Pick(int i)
    {
        if (i < 0 || i >= GunDefs.All.Length) return;
        _mod.SetGun(GunDefs.All[i]);
    }

    private void Cycle(int d)
    {
        int n = GunDefs.All.Length;
        Pick((GunDefs.IndexOf(_mod.CurrentGun) + d + n) % n);
    }

    internal void Refresh()
    {
        if (_root == null) return;
        int cur = GunDefs.IndexOf(_mod.CurrentGun);
        if (cur == _lit) return;
        _lit = cur;
        for (int i = 0; i < Slots; i++)
        {
            bool on = i == cur;
            _frame[i].enabled = on;
            _under[i].enabled = on;
            _bg[i].color = on ? Color.white : new Color(0.72f, 0.72f, 0.76f, 1f);
            _icon[i].color = on ? Color.white : new Color(1f, 1f, 1f, 0.6f);
            _num[i].color = on ? Orange : new Color(0.85f, 0.85f, 0.85f, 1f);
            ((RectTransform)_icon[i].transform.parent).localScale = on ? new Vector3(1.06f, 1.06f, 1f) : Vector3.one;
        }
        if (_label != null) _label.text = _mod.CurrentGun.Name.ToLowerInvariant();
    }
}
