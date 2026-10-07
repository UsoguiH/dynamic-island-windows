// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/decor.ts.

using System.Numerics;
using static Island.Lab.Mascot.BloubMath;

namespace Island.Lab.Mascot;

/// <summary>
/// A decor dot as declared by a state (ball-radius units) and, once rendered by the engine, in
/// viewBox units. Default colour is the body's.
/// </summary>
public sealed record DotSpec
{
    public double X { get; init; }
    public double Y { get; init; }
    public double R { get; init; }
    public double Opacity { get; init; }
    /// <summary>Depth haze: 0 = melted into the background, 1 = full body colour. Null = not hazed.</summary>
    public double? Depth { get; init; }
    /// <summary>
    /// Non-circular shape (closed polygon), in ball-radius units centred on the origin (the slanted
    /// "!" dot is a teardrop, not a disc). When set, <see cref="R"/> is not used for drawing.
    /// </summary>
    public PointD[]? Shape { get; init; }
    /// <summary>Rotation applied to <see cref="Shape"/>, degrees.</summary>
    public double Rot { get; init; }
}

/// <summary>Parameters of a 3D ring/arc (ball-radius units).</summary>
public sealed record ArcSeed
{
    /// <summary>Semi-major axis.</summary>
    public double A { get; init; }
    /// <summary>Flattening b/a: measured &lt;= 0.45, orbit planes are seen edge-on.</summary>
    public double K { get; init; }
    /// <summary>Screen tilt of the major axis, radians.</summary>
    public double Tilt { get; init; }
    /// <summary>Turns per second.</summary>
    public double Speed { get; init; }
    public double Phase { get; init; }
    /// <summary>Fraction of the turn actually drawn.</summary>
    public double Sweep { get; init; }
    public double Hue { get; init; }
    public double HueSpan { get; init; }
    public double Width { get; init; }
    public double Cx { get; init; }
    public double Cy { get; init; }
}

/// <summary>What a state declares: geometry stays in ball-radius units; the engine rasterises.</summary>
public sealed record ArcSpec(string Id, ArcSeed Seed, double T, double Opacity);

/// <summary>A rasterised arc, in viewBox units.</summary>
public sealed class ArcRender
{
    public required string Id { get; init; }
    /// <summary>Open polylines in front of the body (drawn after it).</summary>
    public required List<Vector2[]> Front { get; init; }
    /// <summary>Open polylines behind the body (drawn before it, so the body occludes them).</summary>
    public required List<Vector2[]> Back { get; init; }
    public float Width { get; init; }
    public float Opacity { get; init; }
    /// <summary>Hue gradient along the stroke, userSpaceOnUse from <see cref="GradStart"/> to <see cref="GradEnd"/>.</summary>
    public Vector2 GradStart { get; init; }
    public Vector2 GradEnd { get; init; }
    /// <summary>Three evenly spaced stops, 0xRRGGBB.</summary>
    public required uint[] GradStops { get; init; }
}

/// <summary>Decor catalogue: rings, swoosh, thinking dots, burst particles, comet, notification.</summary>
public static class BloubDecor
{
    /* ------------------------------------------------------------------ colours */

    /// <summary>
    /// Rings are not flat colours: the video shows a full hue wheel at constant lightness, with
    /// a gradient along each stroke. Measured: S 45-62 %, L 50-67 %. Returns 0xRRGGBB.
    /// </summary>
    public static uint Wheel(double hue, double s = 0.55, double l = 0.62)
    {
        double h = (hue % 360 + 360) % 360;
        double c = (1 - Math.Abs(2 * l - 1)) * s;
        double x = c * (1 - Math.Abs(h / 60 % 2 - 1));
        double m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0.0)
            : h < 120 ? (x, c, 0.0)
            : h < 180 ? (0.0, c, x)
            : h < 240 ? (0.0, x, c)
            : h < 300 ? (x, 0.0, c)
            : (c, 0.0, x);
        // Math.round in JS rounds .5 up; values here are never negative.
        static uint Hex(double v) => (uint)Math.Floor(v * 255 + 0.5);
        return (Hex(r + m) << 16) | (Hex(g + m) << 8) | Hex(b + m);
    }

    /* --------------------------------------------------------- 3D elliptical arc */

    /// <summary>
    /// Projects a tilted 3D circle orthographically. The circle lives in the plane spanned by u
    /// (in the screen) and v (diving into depth). The z component splits the arc in two: the back
    /// half is drawn before the body so it occludes it — that real depth sort is what makes the
    /// rings read as orbits rather than flat drawing.
    /// </summary>
    public static ArcRender ArcRender(ArcSeed seed, double t, double scale, string id, double opacity = 1)
    {
        double spin = seed.Phase + t * seed.Speed * Tau;
        double cu = Math.Cos(seed.Tilt), su = Math.Sin(seed.Tilt);
        double kz = Math.Sqrt(Math.Max(0, 1 - seed.K * seed.K));

        const int N = 64;
        double span = seed.Sweep * Tau;
        var front = new List<Vector2[]>();
        var back = new List<Vector2[]>();
        var run = new List<Vector2>(N + 1);
        bool? prev = null;

        void Flush()
        {
            if (run.Count == 0 || prev is null) return;
            (prev.Value ? back : front).Add(run.ToArray());
            run.Clear();
        }

        for (int i = 0; i <= N; i++)
        {
            double th = spin + (double)i / N * span;
            double ct = Math.Cos(th), st = Math.Sin(th);
            // u = (cos tilt, sin tilt, 0); v = (-sin tilt * k, cos tilt * k, kz)
            double x = seed.A * (ct * cu + st * -su * seed.K) + seed.Cx;
            double y = seed.A * (ct * su + st * cu * seed.K) + seed.Cy;
            double z = seed.A * st * kz;
            bool behind = z < 0;
            if (behind != prev) Flush(); // a new "M" sub-path starts
            run.Add(new Vector2((float)(x * scale), (float)(y * scale)));
            prev = behind;
        }
        Flush();

        double gx = Math.Cos(seed.Tilt) * seed.A * scale;
        double gy = Math.Sin(seed.Tilt) * seed.A * scale;
        return new ArcRender
        {
            Id = id,
            Front = front,
            Back = back,
            Width = (float)(seed.Width * scale),
            Opacity = (float)opacity,
            GradStart = new Vector2((float)(seed.Cx * scale - gx), (float)(seed.Cy * scale - gy)),
            GradEnd = new Vector2((float)(seed.Cx * scale + gx), (float)(seed.Cy * scale + gy)),
            GradStops = [Wheel(seed.Hue), Wheel(seed.Hue + seed.HueSpan * 0.5), Wheel(seed.Hue + seed.HueSpan)],
        };
    }

    /* ---------------------------------------------------------------- rings */

    /// <summary>
    /// 6 rings, semi-major axis 1.30-1.40 (clearly larger than the ball), flattening always
    /// &lt;= 0.45, thickness 0.055, ~3.3 turns/s.
    /// </summary>
    public static readonly ArcSeed[] Rings = BuildRings();

    static ArcSeed[] BuildRings()
    {
        var rng = CreateRng(0xa11ce);
        var o = new ArcSeed[6];
        for (int i = 0; i < 6; i++)
        {
            // Object-literal evaluation order of the original is preserved (one RNG draw per field).
            double a = 1.3 + rng() * 0.1;
            double k = 0.05 + rng() * 0.4;
            double tilt = (double)i / 6 * Math.PI + rng() * 0.5;
            double speed = 3 + rng() * 0.7;
            double phase = rng() * Tau;
            double sweep = 0.6 + rng() * 0.25;
            double hue = i * 360.0 / 6 + rng() * 30;
            double hueSpan = 60 + rng() * 60;
            double width = 0.05 + rng() * 0.012;
            o[i] = new ArcSeed { A = a, K = k, Tilt = tilt, Speed = speed, Phase = phase, Sweep = sweep, Hue = hue, HueSpan = hueSpan, Width = width, Cx = 0, Cy = 0.1 };
        }
        return o;
    }

    /// <summary>
    /// Bouquet of nested arcs that sweeps the triangle just before the orbits. Seen almost
    /// edge-on (hence the hairpin shape), rmax 1.37.
    /// </summary>
    public static readonly ArcSeed[] Swoosh = Enumerable.Range(0, 4).Select(i => new ArcSeed
    {
        A = 0.78 + i * 0.2,
        K = 0.05 + i * 0.02,
        Tilt = -0.62 + i * 0.05,
        Speed = 0.3,
        Phase = 0.06 * i,
        Sweep = 0.4,
        Hue = 95 + i * 62,
        HueSpan = 100,
        Width = 0.05,
        Cx = 0,
        Cy = -0.12,
    }).ToArray();

    /* ------------------------------------------------------------- three dots */

    /// <summary>Measured x: -0.557 / -0.013 / +0.532, y = 0.</summary>
    public static readonly double[] DotX = [-0.557, -0.013, 0.532];
    public const double DotR = 0.165;
    public const double DotPeak = 1.25;

    /* ------------------------------------------------------------ particles */

    /// <summary>5 particles, a new one every 0.2 s, lifetime 0.55 s.</summary>
    static readonly (double birth, double angle, double rho)[] Particles = BuildParticles();

    static (double, double, double)[] BuildParticles()
    {
        var rng = CreateRng(0xbeef);
        var o = new (double, double, double)[5];
        for (int i = 0; i < 5; i++)
        {
            double angle = rng() * Tau;
            double rho = 0.58 + rng() * 0.18;
            o[i] = (i * 0.2, angle, rho);
        }
        return o;
    }

    /// <summary>
    /// Particles do not fly straight: they spiral towards the centre (radius x0.75 per frame,
    /// angle +100 deg/s) while growing, and pass behind the core where they are swallowed.
    /// </summary>
    public static List<DotSpec> ParticleDots(double t, double scale)
    {
        var o = new List<DotSpec>();
        foreach (var p in Particles)
        {
            double u = t - p.birth;
            if (u < 0 || u > 0.62) continue;
            double rho = p.rho * Math.Pow(0.75, u * 10);
            double a = p.angle + u * 100 * Math.PI / 180;
            o.Add(new DotSpec
            {
                X = Math.Cos(a) * rho * scale,
                Y = Math.Sin(a) * rho * scale,
                R = (0.04 + 0.028 * Clamp(u / 0.55)) * scale,
                Depth = Clamp(1 - rho / 0.8),
                Opacity = Clamp(u / 0.06) * Clamp((0.62 - u) / 0.08),
            });
        }
        return o;
    }

    /* ------------------------------------------------------------------ comet */

    /// <summary>
    /// Counter-intuitively, the dot does not cross the screen: it stays in the centre and the
    /// trail orbits it. Ellipse a = 0.85, b = 0.15, major axis tilted +34deg, 4 ribbons, ~210 deg/s.
    /// </summary>
    public static readonly ArcSeed[] CometRibbons = BuildComet();

    static ArcSeed[] BuildComet()
    {
        var rng = CreateRng(0xc0e7);
        var o = new ArcSeed[4];
        for (int i = 0; i < 4; i++)
        {
            double d = i - 1.5;
            double a = 0.85 * (1 + d * 0.03);
            // same flattening to +-5 %: the ribbons form a tight beam
            double k = 0.15 / 0.85 * (1 + d * 0.16);
            double tilt = 34 * Math.PI / 180 + d * 0.035;
            double speed = 210.0 / 360;
            // measured phase shift: 10 to 20 degrees between ribbons, no more
            double phase = -i * 0.045 + rng() * 0.012;
            double sweep = 0.34;
            double hue = i * 85 + rng() * 20;
            o[i] = new ArcSeed { A = a, K = k, Tilt = tilt, Speed = speed, Phase = phase, Sweep = sweep, Hue = hue, HueSpan = 80, Width = 0.095, Cx = 0, Cy = 0 };
        }
        return o;
    }

    /// <summary>Radius of the comet's dot, measured at 0.129.</summary>
    public const double CometDot = 0.129;

    /* --------------------------------------------------- notification pastille */

    /// <summary>Blue measured to the pixel (#2496e8).</summary>
    public const uint NotifBlue = 0x2496e8;
    /// <summary>The pastille sits exactly on the circumference, at -42deg.</summary>
    public const double NotifAngle = -42;
    public const double NotifDist = 1.003;
    /// <summary>Resting radius; the pop peaks 14 % above.</summary>
    public const double NotifR = 0.15;
    public const double NotifPop = 1.14;
    /// <summary>The notch is a disc concentric to the pastille, subtracted from the body; constant margin (0.054 R).</summary>
    public const double NotifMargin = 0.054;
}
