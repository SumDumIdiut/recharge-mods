using System;
using Recharge.ModApi;
using TMPro;
using UnityEngine;
using UnityEngine.UI;

/// <summary>The "Gun" row on the pause menu's mod page: on/off, recoil strength, bullets break tiles, re-lock.</summary>
internal static class GunMenu
{
    internal static void Install(GunMod mod, pauseMenuScript menu)
    {
        if (menu == null) return;
        try
        {
            var panel = PauseMenuHelper.AddPanelRow(menu, "GunPanel", mod.DisplayName);
            if (panel == null || panel.transform.Find("GunRoot") != null) return;
            var font = FindTitleFont(panel);
            Build(mod, panel, font);
        }
        catch (Exception e) { mod.Host.LogWarning("pause-menu row failed: " + e.Message); }
    }

    private static TMP_FontAsset FindTitleFont(GameObject panel)
    {
        var title = panel.transform.Find("Settings") ?? (panel.transform.childCount > 0 ? panel.transform.GetChild(0) : null);
        return title != null ? title.GetComponent<TMP_Text>()?.font : null;
    }

    // Panel geometry as the example mod does it: native size x1.15, title and Close on the panel's edges, rows in between.
    private const float NativeSizeScale = 1.15f, EdgeMargin = 35f;

    private static void Build(GunMod mod, GameObject panel, TMP_FontAsset font)
    {
        var nativeSize = panel.GetComponent<RectTransform>().sizeDelta;
        if (nativeSize.x < 10f || nativeSize.y < 10f) nativeSize = new Vector2(700f, 620f);
        var size = nativeSize * NativeSizeScale;
        float top = size.y / 2f - EdgeMargin;
        PanelLayout.Apply(panel, size);
        PauseMenuHelper.NormalizePanelLayout(panel, top, -top); // the "Gun" title centred at the top, Close at the bottom

        var root = PanelWidgets.CreateContainer(panel.transform, "GunRoot", Vector2.zero);
        var cfg = mod.Config;
        float y = top - 48f;

        var status = PanelWidgets.CreateLabel(root, font, new Vector2(0, y), new Vector2(560, 26), "", 16f, new Color(1f, 0.85f, 0.4f));
        Action refresh = () => status.text = mod.IsUnlocked() ? "Gun unlocked" : "Locked - buy the free box at your spawn";
        refresh();
        y -= 26f;
        PanelWidgets.CreateDivider(root, new Vector2(0, y), 560);

        // Gun picker: Prev / Next cycle the five guns (saved in config, applied at once). Words, not < >: the game's font has no arrow glyphs.
        y -= 42f;
        var pickLabel = PanelWidgets.CreateLabel(root, font, new Vector2(0, y), new Vector2(300, 40), "", 22f, Color.white);
        Action showPick = () => pickLabel.text = mod.CurrentGun.Name;
        Action<int> step = d =>
        {
            int n = GunDefs.All.Length;
            int i = (GunDefs.IndexOf(mod.CurrentGun) + d + n) % n;
            mod.SetGun(GunDefs.All[i]);
            showPick();
        };
        var prev = PanelWidgets.CreateButton(root, font, "GunPrev", new Vector2(-215, y), new Vector2(110, 40), "Prev", 18f);
        var next = PanelWidgets.CreateButton(root, font, "GunNext", new Vector2(215, y), new Vector2(110, 40), "Next", 18f);
        prev.GetComponent<Button>().onClick.AddListener(() => step(-1));
        next.GetComponent<Button>().onClick.AddListener(() => step(1));
        showPick();

        y -= 48f;
        PanelWidgets.CreateToggle(root, font, new Vector2(25, y), "Gun on", cfg.Enabled, v => { cfg.Enabled = v; mod.SaveConfig(); });
        y -= 36f;
        PanelWidgets.CreateToggle(root, font, new Vector2(25, y), "Bullets break tiles", cfg.BreakTiles, v => { cfg.BreakTiles = v; mod.SaveConfig(); });
        y -= 36f;
        PanelWidgets.CreateToggle(root, font, new Vector2(25, y), "Left mouse fires", cfg.LeftMouseFires, v => { cfg.LeftMouseFires = v; mod.SaveConfig(); });

        y -= 42f;
        PanelWidgets.CreateLabel(root, font, new Vector2(0, y), new Vector2(560, 26), "Recoil strength", 18f, new Color(1f, 1f, 1f, 0.65f));
        y -= 36f;
        // A number box (percent, 0 - 10000), applied when you press Enter or leave the box; anything else snaps back to the saved value.
        var recoil = PanelWidgets.CreateInputField(root, font, new Vector2(-20, y), new Vector2(160, 36), "100", 20f);
        recoil.name = "RecoilInput";
        recoil.contentType = TMP_InputField.ContentType.IntegerNumber;
        recoil.characterLimit = 5;
        PanelWidgets.CreateLabel(root, font, new Vector2(80, y), new Vector2(40, 36), "%", 20f, Color.white);
        Action showRecoil = () => recoil.SetTextWithoutNotify(Mathf.RoundToInt(cfg.RecoilStrength * 100f).ToString());
        showRecoil();
        recoil.onEndEdit.AddListener(t =>
        {
            if (int.TryParse(t, out var pct)) { cfg.RecoilStrength = Mathf.Clamp(pct, 0, 10000) / 100f; mod.SaveConfig(); }
            showRecoil();
        });

        y -= 38f;
        PanelWidgets.CreateDivider(root, new Vector2(0, y), 560);
        y -= 44f;
        var relock = PanelWidgets.CreateButton(root, font, "Relock", new Vector2(0, y), new Vector2(240, 46), "Re-lock gun", 20f);
        relock.GetComponent<Button>().onClick.AddListener(() =>
        {
            mod.Box?.Relock();
            refresh();
        });
        y -= 52f;
        var help = PanelWidgets.CreateLabel(root, font, new Vector2(0, y), new Vector2(520, 60), "Re-lock puts the Unlock gun box back to buyable.\nFire: left mouse, F or right trigger.\nGuns: keys 1-5, or LB / RB on a controller.", 14f, new Color(1f, 1f, 1f, 0.6f));
        help.textWrappingMode = TextWrappingModes.Normal;

        // keep the status line honest when the menu is reopened after buying
        root.gameObject.AddComponent<RefreshOnEnable>().Action = () => { refresh(); showPick(); showRecoil(); };
    }

    private class RefreshOnEnable : MonoBehaviour
    {
        public Action Action;
        private void OnEnable() { Action?.Invoke(); }
    }
}
