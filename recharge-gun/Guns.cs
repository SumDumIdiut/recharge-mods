using System;
using UnityEngine;

/// <summary>
/// One gun = one set: sprite, muzzle flash, bullet, impact effect and handling numbers.
/// All art is pixel rows drawn into point-filtered textures at warmup (no asset files).
/// </summary>
internal class GunDef
{
    internal const float PivotX = 0.1f;          // the gun sprite's pivot (where it sits on the player), as a fraction of its width

    internal string Name;
    // ---- the gun sprite (faces right; the barrel's centre row is BarrelRow pixels from the top) ----
    internal string[] Art;                       // pixel grid from GunArt
    internal float BarrelRow = 2f;
    internal float MuzzleX = 1f;                 // barrel tip, as a fraction of the sprite width
    internal float LenMul = 1f;                  // world length relative to 1.15 x player height

    // ---- handling ----
    internal float FireRate = 5f;                // shots / s at the default fire-rate setting
    internal float RecoilMul = 1f;
    internal float SpeedMul = 1f;
    internal float LifeMul = 1f;
    internal int Pellets = 1;
    internal float SpreadDeg = 0f;               // angle between neighbouring pellets
    internal int BreakExtra = 0;                 // extra tile-break radius (cells)

    // ---- bullet ----
    internal string[][] BulletFrames;            // 1-2 frames, cycled while flying (flame flicker, plasma pulse, wave phase)
    internal float BulletLen = 0.4f;             // x player height
    internal bool Wavy, Smoke;

    // ---- muzzle flash ----
    internal string[][] FlashFrames;             // 2-3 frames, played over FlashDur
    internal float FlashSize = 0.55f;            // x player height (width)
    internal float FlashPivotX = 0.5f;
    internal float FlashDur = 0.07f;
    internal bool FlashPulse;
    internal int MuzzleSmoke;                    // grey puffs at the muzzle

    // ---- impact ----
    internal string[][] ImpactFrames;            // 3-5 frames, played over PopLife (null = none)
    internal float PopFrom = 0.4f, PopTo = 0.8f, PopLife = 0.15f;
    internal Color PopColor = Color.white;
    internal bool Ring; internal Color RingColor; internal float RingTo = 1.2f;
    internal int SparkCount = 5; internal Color SparkA, SparkB;
    internal float SparkSpeed = 9f, SparkLife = 0.25f, SparkSize = 1f, SparkGravity = 1f, SparkSwirl = 0f;

    // ---- runtime sprites ----
    internal Sprite GunSp; internal Sprite[] BulletSps, FlashSps, PopSps;
    internal float GunAspect, BulletAspect, FlashAspect, PopAspect;
    internal Sprite BulletSp => BulletSps != null ? BulletSps[0] : null;
    internal Sprite FlashSp => FlashSps != null ? FlashSps[0] : null;
}

internal static class GunDefs
{
    internal static readonly GunDef[] All = Build();

    internal static GunDef Find(string name)
    {
        if (!string.IsNullOrEmpty(name))
            foreach (var g in All) if (string.Equals(g.Name, name, StringComparison.OrdinalIgnoreCase)) return g;
        return All[0];
    }

    internal static int IndexOf(GunDef d) { for (int i = 0; i < All.Length; i++) if (All[i] == d) return i; return 0; }

    internal static Color32 Pal(char c)
    {
        switch (c)
        {
            case 'D': return new Color32(44, 46, 60, 255);      // darkest grey
            case 'L': return new Color32(240, 244, 252, 255);   // highlight
            case 'O': return new Color32(255, 190, 95, 255);    // pale orange
            case 'q': return new Color32(150, 40, 110, 255);    // dark pink
            case 'u': return new Color32(82, 50, 30, 255);      // dark brown
            case 'k': return new Color32(24, 22, 30, 255);      // outline
            case 'l': return new Color32(205, 210, 220, 255);   // light metal
            case 'g': return new Color32(120, 128, 146, 255);   // grey
            case 'd': return new Color32(70, 74, 92, 255);      // dark grey
            case 'o': return new Color32(235, 140, 40, 255);    // orange
            case 'n': return new Color32(200, 85, 20, 255);     // deep orange
            case 'r': return new Color32(210, 50, 40, 255);     // red
            case 'R': return new Color32(255, 120, 95, 255);    // light red
            case 'G': return new Color32(90, 255, 140, 255);    // green glow
            case 'h': return new Color32(30, 130, 75, 255);     // dark green
            case 'y': return new Color32(255, 225, 80, 255);    // yellow
            case 'w': return new Color32(255, 255, 240, 255);   // white
            case 'p': return new Color32(255, 90, 185, 255);    // pink
            case 'P': return new Color32(255, 175, 225, 255);   // light pink
            case 't': return new Color32(70, 225, 215, 255);    // teal
            case 'T': return new Color32(30, 140, 155, 255);    // dark teal
            case 'b': return new Color32(125, 78, 48, 255);     // brown
            case 'B': return new Color32(180, 125, 80, 255);    // light brown
            case 's': return new Color32(165, 165, 170, 255);   // smoke
            case 'S': return new Color32(95, 95, 100, 255);     // dark smoke
            case 'K': return new Color32(0, 0, 0, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'A': return new Color32(190, 71, 71, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'a': return new Color32(251, 242, 54, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'C': return new Color32(217, 87, 99, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'c': return new Color32(172, 50, 50, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'E': return new Color32(95, 205, 228, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'e': return new Color32(106, 190, 48, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'F': return new Color32(35, 57, 50, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'f': return new Color32(99, 155, 255, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'H': return new Color32(154, 25, 25, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'I': return new Color32(219, 210, 33, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'i': return new Color32(50, 60, 57, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'J': return new Color32(210, 210, 210, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'j': return new Color32(251, 94, 37, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'M': return new Color32(40, 40, 40, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'm': return new Color32(182, 182, 182, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'N': return new Color32(153, 229, 80, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'Q': return new Color32(158, 153, 153, 255);   // from the hand-drawn ray gun / rocket launcher
            case 'U': return new Color32(23, 28, 27, 255);   // from the hand-drawn ray gun / rocket launcher
            default: return new Color32(0, 0, 0, 0);
        }
    }

    private static GunDef[] Build()
    {
        return new[]
        {
            // 1. Pistol: slide, grip, trigger guard; star-burst flash, yellow tracer, spark-burst impact
            new GunDef
            {
                Name = "Pistol",
                Art = GunArt.PistolGun, BarrelRow = GunArt.PistolGunBarrelRow, MuzzleX = GunArt.PistolGunMuzzle, LenMul = 1.25f,
                FireRate = 5f, RecoilMul = 0.8f, SpeedMul = 1f,
                BulletFrames = GunArt.PistolBullet, BulletLen = 0.32f,
                FlashFrames = GunArt.PistolFlash, FlashSize = 0.5f, FlashPivotX = 0.15f, FlashDur = 0.07f,
                ImpactFrames = GunArt.PistolImpact, PopFrom = 0.5f, PopTo = 0.6f, PopLife = 0.2f,
                SparkCount = 6, SparkA = new Color(1f, 0.85f, 0.4f), SparkB = new Color(1f, 0.6f, 0.2f), SparkSpeed = 9f, SparkLife = 0.25f,
            },
            // 2. Blaster: chunky body with a green glow strip; green glow flash, plasma bolt, ring-pop impact
            new GunDef
            {
                Name = "Blaster",
                Art = GunArt.BlasterGun, BarrelRow = GunArt.BlasterGunBarrelRow, MuzzleX = GunArt.BlasterGunMuzzle, LenMul = 1.45f,
                FireRate = 7f, RecoilMul = 0.6f, SpeedMul = 1.3f,
                BulletFrames = GunArt.BlasterBullet, BulletLen = 0.5f,
                FlashFrames = GunArt.BlasterFlash, FlashSize = 0.6f, FlashPivotX = 0.3f, FlashDur = 0.08f,
                ImpactFrames = GunArt.BlasterImpact, PopFrom = 0.9f, PopTo = 1.15f, PopLife = 0.3f,
                SparkCount = 4, SparkA = new Color(0.4f, 1f, 0.55f), SparkB = new Color(0.8f, 1f, 0.85f), SparkSpeed = 7f, SparkLife = 0.25f,
            },
            // 3. Shotgun: double barrel, wooden stock; wide cone flash, 5 pellets, dust puff impact
            new GunDef
            {
                Name = "Shotgun",
                Art = GunArt.ShotgunGun, BarrelRow = GunArt.ShotgunGunBarrelRow, MuzzleX = GunArt.ShotgunGunMuzzle, LenMul = 1.65f,
                FireRate = 1.6f, RecoilMul = 1.6f, SpeedMul = 0.9f, LifeMul = 0.45f,
                Pellets = 5, SpreadDeg = 6f,
                BulletFrames = GunArt.ShotgunBullet, BulletLen = 0.13f,
                FlashFrames = GunArt.ShotgunFlash, FlashSize = 0.95f, FlashPivotX = 0.08f, FlashDur = 0.1f, MuzzleSmoke = 2,
                ImpactFrames = GunArt.ShotgunImpact, PopFrom = 0.45f, PopTo = 0.6f, PopLife = 0.3f,
                SparkCount = 2, SparkA = new Color(0.65f, 0.55f, 0.45f), SparkB = new Color(0.65f, 0.55f, 0.45f), SparkSpeed = 4f, SparkLife = 0.35f, SparkSize = 1.6f, SparkGravity = 0.3f,
            },
            // 4. Ray gun: bubble chamber and fins in pink and teal; pulsing flash, wavy beam, sparkle swirl
            new GunDef
            {
                Name = "Ray gun",
                Art = GunArt.RaygunGun, BarrelRow = GunArt.RaygunGunBarrelRow, MuzzleX = GunArt.RaygunGunMuzzle, LenMul = 1.15f,
                FireRate = 4f, RecoilMul = 0.8f, SpeedMul = 1.1f,
                BulletFrames = GunArt.RaygunBullet, BulletLen = 1.0f, Wavy = true,
                FlashFrames = GunArt.RaygunFlash, FlashSize = 0.5f, FlashPivotX = 0.35f, FlashDur = 0.1f, FlashPulse = true,
                ImpactFrames = GunArt.RaygunImpact, PopFrom = 0.7f, PopTo = 0.9f, PopLife = 0.35f,
                SparkCount = 8, SparkA = new Color(1f, 0.4f, 0.75f), SparkB = new Color(0.3f, 0.9f, 0.85f), SparkSpeed = 6f, SparkLife = 0.5f, SparkGravity = 0f, SparkSwirl = 9f,
            },
            // 5. Rocket launcher: tube, sights, red warhead tip; smoky flash, finned rocket with a flickering flame, explosion with debris
            new GunDef
            {
                Name = "Rocket launcher",
                Art = GunArt.RocketGun, BarrelRow = GunArt.RocketGunBarrelRow, MuzzleX = GunArt.RocketGunMuzzle, LenMul = 1.8f,
                FireRate = 1.0f, RecoilMul = 2.2f, SpeedMul = 0.5f, LifeMul = 2.2f,
                BreakExtra = 1, MuzzleSmoke = 6,
                BulletFrames = GunArt.RocketBullet, BulletLen = 0.75f, Smoke = true,
                FlashFrames = GunArt.RocketFlash, FlashSize = 0.9f, FlashPivotX = 0.3f, FlashDur = 0.15f,
                ImpactFrames = GunArt.RocketImpact, PopFrom = 1.5f, PopTo = 2.1f, PopLife = 0.5f,
                Ring = true, RingColor = new Color(1f, 0.6f, 0.2f), RingTo = 3.4f,
                SparkCount = 14, SparkA = new Color(0.5f, 0.45f, 0.4f), SparkB = new Color(1f, 0.55f, 0.15f), SparkSpeed = 14f, SparkLife = 0.55f, SparkSize = 1.3f,
            },
        };
    }
}
