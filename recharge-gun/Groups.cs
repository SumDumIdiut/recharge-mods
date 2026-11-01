using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using UnityEngine;
using UnityEngine.Tilemaps;

/// <summary>
/// Finds "the thing" a visible renderer belongs to, so a hit can switch off the whole object (all of its sprites,
/// colliders and scripts) instead of one sprite of it. Resolution happens only when something is hit, never per frame.
///
/// From the struck renderer's object we climb to the parent while the parent still looks like part of the same
/// object: it is not a scene root, it holds a small number of renderers (so it is not a container such as a zone or a
/// course folder), it is not a set of look-alike siblings (Flywheel (1), Flywheel (2) ... are separate things), and
/// it does not hold the player, the camera, the floating origin or tilemaps. An object that carries its own gameplay
/// script (teleporter, checkpoint, gate, spring, upgrade box ...) is the top of its group.
/// </summary>
internal static class Groups
{
    internal const int MaxRenderers = 40;

    /// <summary>Scripts that make an object the top of its own group (the climb stops there).</summary>
    private static readonly string[] OwnObjectScripts =
    {
        "checkpointScript", "upgradeBox", "spikeScript", "SpringScript", "startGate", "endGate", "TeleporterScript", "JiggleDropScript",
    };

    /// <summary>Invisible plumbing that may carry a sprite for the editor: never broken, and never part of a broken group's hit.</summary>
    private static readonly string[] SystemScripts =
    {
        "camZoneScript", "camSizeTrigger", "MusicController", "zoneChanger", "longFallColliderController", "TutorialTextTrigger",
        "LoadingPopupScript", "a1Statesetter", "Zone2to1TransitionController", "ZoneDisableLoadList", "OvergrowthLoadScript",
        "clonesScript", "clonesLoD", "FloatingNumberScript", "Utilities", "FloatingOrigin",
    };

    /// <summary>The live clone replays are the game's own recordings: they stay (as does anything under them).</summary>
    private static readonly string[] AncestorScripts = { "clonesScript", "clonesLoD" };

    private static readonly Dictionary<Transform, int> Counts = new Dictionary<Transform, int>();
    private static readonly Regex Tail = new Regex(@"\s*(\(\d+\)|\d+)\s*$", RegexOptions.Compiled);
    internal static string LastWhy = "";

    internal static void Reset() { Counts.Clear(); }

    private static bool Has(Transform t, string[] names)
    {
        foreach (var mb in t.GetComponents<MonoBehaviour>())
        {
            if (mb == null) continue;
            var n = mb.GetType().Name;
            foreach (var s in names) if (n == s) return true;
        }
        return false;
    }

    private static string Stem(string n)
    {
        string prev;
        do { prev = n; n = Tail.Replace(n, ""); } while (n != prev);
        return n.Trim().ToLowerInvariant();
    }

    private static bool HasPeer(Transform t)
    {
        var p = t.parent;
        if (p == null) return false;
        var s = Stem(t.name);
        for (int i = 0; i < p.childCount; i++)
        {
            var o = p.GetChild(i);
            if (o != t && Stem(o.name) == s) return true;
        }
        return false;
    }

    private static int RendererCount(Transform t)
    {
        if (Counts.TryGetValue(t, out var n)) return n;
        n = t.GetComponentsInChildren<Renderer>(true).Length;
        Counts[t] = n;
        return n;
    }

    /// <summary>The player, camera, HUD / UI, the gun's own objects, floating numbers and invisible systems are never broken.</summary>
    internal static bool Inert(Transform t)
    {
        try
        {
            var mv = GunMod.Instance != null ? GunMod.Instance.Player : null;
            Transform playerRoot = null;
            if (mv != null) { playerRoot = mv.transform.root; if (playerRoot.childCount > 12) playerRoot = mv.transform; }
            for (var p = t; p != null; p = p.parent)
            {
                if (p == playerRoot) { LastWhy = "player"; return true; }
                if (p.name.StartsWith("Recharge") || p.name == "FloatingTextSystem") { LastWhy = "gun/fx object"; return true; }
                if (p.GetComponent<Camera>() != null) { LastWhy = "camera"; return true; }
                if (p.GetComponent<Canvas>() != null && p.GetComponent<RectTransform>() != null && !(p.GetComponent<Canvas>().renderMode == RenderMode.WorldSpace)) { LastWhy = "UI canvas"; return true; }
                if (p is RectTransform && p.GetComponentInParent<Canvas>() != null && p.GetComponentInParent<Canvas>().renderMode != RenderMode.WorldSpace) { LastWhy = "UI"; return true; }
                if (p.GetComponent<upgradeBox>() != null && p.name == "RechargeGunBox") { LastWhy = "gun box"; return true; }
                if (p == t && Has(p, SystemScripts)) { LastWhy = "invisible system script on '" + p.name + "'"; return true; }
                if (Has(p, AncestorScripts)) { LastWhy = "live clone replay"; return true; }
            }
        }
        catch { LastWhy = "error"; return true; }
        return false;
    }

    private static bool Forbidden(Transform g)
    {
        if (g.parent == null) { LastWhy = "scene root"; return true; }
        if (g.GetComponentInChildren<Movement>(true) != null) { LastWhy = "holds the player"; return true; }
        if (g.GetComponentInChildren<Camera>(true) != null) { LastWhy = "holds the camera"; return true; }
        if (g.GetComponentInChildren<FloatingOrigin>(true) != null) { LastWhy = "holds the floating origin"; return true; }
        if (g.GetComponentInChildren<Tilemap>(true) != null) { LastWhy = "holds tilemaps"; return true; }
        if (g.GetComponentInChildren<RectTransform>(true) != null && g.GetComponentInParent<Canvas>() != null && g.GetComponentInParent<Canvas>().renderMode != RenderMode.WorldSpace) { LastWhy = "UI"; return true; }
        foreach (var mb in g.GetComponentsInChildren<MonoBehaviour>(true))
        {
            if (mb == null) continue;
            var n = mb.GetType().Name;
            if (n == "FloatingOrigin" || n == "clonesScript" || n == "clonesLoD" || n == "pauseMenuScript") { LastWhy = "holds " + n; return true; }
            if (n == "upgradeBox" && mb.gameObject.name == "RechargeGunBox") { LastWhy = "holds the gun box"; return true; }
        }
        return false;
    }

    /// <summary>The root to switch off for a hit on a renderer, or null if it must stay.</summary>
    internal static Transform Resolve(Transform start)
    {
        LastWhy = "";
        if (start == null) return null;
        if (Inert(start)) return null;
        var cur = start;
        for (int guard = 0; guard < 32; guard++)
        {
            if (Has(cur, OwnObjectScripts)) break;
            if (HasPeer(cur)) break;
            var p = cur.parent;
            if (p == null) break;
            if (RendererCount(p) > MaxRenderers) break;
            if (Forbidden(p)) break;
            if (Has(p, SystemScripts)) break;
            cur = p;
        }
        LastWhy = "";
        if (cur.parent == null) { LastWhy = "scene root"; return null; }
        if (Forbidden(cur)) return null;
        return cur;
    }

    /// <summary>For a collider hit: the nearest object (self or up to two parents) that has something visible.</summary>
    internal static Transform VisibleStart(Collider2D col)
    {
        var t = col.transform;
        for (int i = 0; i < 3 && t != null; i++, t = t.parent)
        {
            var r = t.GetComponent<Renderer>();
            if (r != null && !(r is TilemapRenderer) && r.enabled) return t;
            if (i == 0) foreach (var c in t.GetComponentsInChildren<Renderer>(false)) if (c != null && c.enabled && !(c is TilemapRenderer)) return t;
        }
        return null;
    }
}
