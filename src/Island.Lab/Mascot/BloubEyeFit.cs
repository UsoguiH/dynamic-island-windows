// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/eyefit.ts.

using System.Collections.Concurrent;
using System.Runtime.CompilerServices;
using static Island.Lab.Mascot.BloubShape;

namespace Island.Lab.Mascot;

/// <summary>
/// Where to put the face on a customiser shape.
///
/// The eyes live on a sphere and <see cref="BloubShape.RadiusAtAngle"/> re-seats them on the real
/// outline pro rata of the local radius. That places their CENTRE correctly, but an eye has a size:
/// a shape narrow in its direction pushes it against the edge until the mask opens it outwards.
///
/// This module solves the problem ONCE (not per frame) and yields a table of offsets, one per
/// (shape, base-body state, expression). Solving inside the render loop reacted to everything
/// that moves at 60 fps and every variant trembled; a tabulated offset is only interpolated
/// between two constants by the engine, on the boundaries of each morph, which is monotonic by
/// construction. The offset is a common TRANSLATION of both eyes (an isometry).
///
/// Port note: the original builds the whole table at import. Here each shape's table is built
/// lazily on first lookup (thread-safe, deterministic), which keeps start-up instant; the values
/// are identical.
/// </summary>
public static class BloubEyeFit
{
    /// <summary>Solver reference radius. The returned offset is in units of this radius.</summary>
    const double R = 100;

    /// <summary>
    /// Maximum amplitudes of the resting life, read off <see cref="BloubFace.Life"/>: LoopNoise is
    /// bounded by 1, so these sums are exact bounds. They must be covered, or the correction is
    /// right on the nominal pose and wrong a second later.
    /// </summary>
    const double DeriveYaw = 5.5 + 1.6;
    const double DerivePitch = 4.2 + 1.3;
    /// <summary>Centre float, ball-radius units.</summary>
    const double DeriveX = 0.006;
    const double DeriveY = 0.007;

    readonly record struct Visage(HeadGaze Gaze, double Split, EyeCfg[] Eyes);

    /// <summary>
    /// A capsule ready to be measured: its axis segment, and what is needed to compute the
    /// clearance radius IN A GIVEN DIRECTION (support function of the transformed disc, an ellipse).
    /// </summary>
    readonly record struct Empreinte(double X, double Y, double Ax, double Ay, double R, double M0, double M1, double M2, double M3);

    /// <summary>Footprints of a face's two eyes placed on a profile (blink excluded).</summary>
    static List<Empreinte> Empreintes(Visage visage, Silhouette sil, double[] radii)
    {
        var o = new List<Empreinte>(2);
        var (p0, p1) = BloubFace.EyePoses(visage.Gaze, R, visage.Split);
        for (int i = 0; i < 2; i++)
        {
            var e = i == 0 ? p0 : p1;
            if (e.Depth <= 0.02) continue;
            var cfg = visage.Eyes[i];
            double phi = cfg.Tilt * Math.PI / 180;
            double cp = Math.Cos(phi), sp = Math.Sin(phi);
            double ax = e.A * cp + e.C * sp;
            double ay = e.B * cp + e.D * sp;
            double cx = -e.A * sp + e.C * cp;
            double cy = -e.B * sp + e.D * cp;

            double hw = Math.Max(cfg.W * R, 0.01) / 2;
            double hh = Math.Max(cfg.H * R, 0.01) / 2;
            double r = Math.Min(hw, hh);
            // the axis is the one of the larger dimension
            bool lng = hh > hw;
            double demi = lng ? hh - r : hw - r;
            // the local radius pro rata, exactly like the engine
            double fit = RadiusAtAngle(radii, Math.Atan2(e.Y, e.X) - sil.Rot);
            o.Add(new Empreinte(e.X * fit, e.Y * fit, (lng ? cx : ax) * demi, (lng ? cy : ay) * demi, r, ax, ay, cx, cy));
        }
        return o;
    }

    /// <summary>Closest approach between an outline and a segment: distance, and the unit vector from the outline to the segment.</summary>
    static (double d, double ux, double uy) Approche(PointD[] pts, double x0, double y0, double x1, double y1)
    {
        double sx = x1 - x0, sy = y1 - y0;
        double len2 = sx * sx + sy * sy;
        double best = double.PositiveInfinity, vx = 0, vy = 0;
        foreach (var p in pts)
        {
            double t = len2 > 0 ? ((p.X - x0) * sx + (p.Y - y0) * sy) / len2 : 0;
            t = t < 0 ? 0 : t > 1 ? 1 : t;
            double ex = x0 + t * sx - p.X;
            double ey = y0 + t * sy - p.Y;
            double d2 = ex * ex + ey * ey;
            if (d2 < best) { best = d2; vx = ex; vy = ey; }
        }
        double d = Math.Sqrt(best);
        return (d, d > 1e-9 ? vx / d : 0, d > 1e-9 ? vy / d : 0);
    }

    sealed record Epreuve(List<Empreinte> Empreintes, List<Empreinte> Reference, PointD[] Contour, PointD[] CalContour);

    /// <summary>Resting centre float, viewBox units, added to the capsule radius.</summary>
    static readonly double Flottement = double.Hypot(DeriveX, DeriveY) * R;

    /// <summary>Margin of the tightest capsule, and the direction that clears it.</summary>
    static (double marge, double ux, double uy) Pire(PointD[] pts, List<Empreinte> emps, double tx, double ty)
    {
        double marge = double.PositiveInfinity, ux = 0, uy = 0;
        foreach (var e in emps)
        {
            double x = e.X + tx, y = e.Y + ty;
            var a = Approche(pts, x - e.Ax, y - e.Ay, x + e.Ax, y + e.Ay);
            // support function of the ellipse in the approach direction
            double rayon = e.R * double.Hypot(e.M0 * a.ux + e.M1 * a.uy, e.M2 * a.ux + e.M3 * a.uy) + Flottement;
            if (a.d - rayon < marge) { marge = a.d - rayon; ux = a.ux; uy = a.uy; }
        }
        return (marge, ux, uy);
    }

    /// <summary>Probed directions and bisection steps (their product is the table's build cost).</summary>
    const int Directions = 12;
    const int Dichotomie = 8;

    /// <summary>
    /// The offset to put on both eyes for this shape, state and expression. Directional search:
    /// probe a ring of directions and bisect the distance along each one, keeping the smallest
    /// translation that fits. The target margin is the one the ORIGINAL profile gave, capped by
    /// what the shape offers at its centre; when nothing fits, aim for the least bad.
    /// </summary>
    static (double x, double y) Resous(List<Epreuve> epreuves)
    {
        if (epreuves.Count == 0) return (0, 0);

        double Marge(double tx, double ty)
        {
            double m = double.PositiveInfinity;
            foreach (var ep in epreuves) m = Math.Min(m, Pire(ep.Contour, ep.Empreintes, tx, ty).marge);
            return m;
        }

        // Required margin: the tightest the original profile tolerates, over every test.
        double requis = double.PositiveInfinity;
        foreach (var ep in epreuves) requis = Math.Min(requis, Pire(ep.CalContour, ep.Reference, 0, 0).marge);

        // The travel must be able to reach the body's centre (wide's 87-unit capsules only fit
        // near the middle of a triangle).
        double mx = 0, my = 0;
        var emps = epreuves[0].Empreintes;
        foreach (var e in emps)
        {
            mx -= e.X / emps.Count;
            my -= e.Y / emps.Count;
        }
        double course = Math.Max(0.35 * R, double.Hypot(mx, my) * 1.25);

        // Cap the demand: what the shape offers at its centre, always reachable.
        requis = Math.Min(requis, Marge(mx, my));

        // Already fine (the circle, any wide enough shape). The capsule must also FIT, otherwise a
        // shape where nothing fits satisfies the first condition degenerately.
        double depart = Marge(0, 0);
        if (depart >= requis && depart >= 0) return (0, 0);
        double cible = Math.Max(requis, 0);

        double meilleurX = 0, meilleurY = 0, meilleureNorme = double.PositiveInfinity;
        // fallback when nothing fits: the translation that clears the most, probed on the way
        double secoursX = 0, secoursY = 0, secours = depart;

        for (int d = 0; d < Directions; d++)
        {
            double a = (double)d / Directions * Math.PI * 2;
            double ux = Math.Cos(a), uy = Math.Sin(a);
            if (Marge(ux * course, uy * course) < cible)
            {
                // no solution this way, but maybe a better clearance
                foreach (double k in new[] { 0.3, 0.6, 1 })
                {
                    double m = Marge(ux * course * k, uy * course * k);
                    if (m > secours)
                    {
                        secours = m;
                        secoursX = ux * course * k;
                        secoursY = uy * course * k;
                    }
                }
                continue;
            }
            // the shortest distance that fits, along this direction
            double bas = 0, haut = course;
            for (int i = 0; i < Dichotomie; i++)
            {
                double mid = (bas + haut) / 2;
                if (Marge(ux * mid, uy * mid) >= cible) haut = mid;
                else bas = mid;
            }
            if (haut < meilleureNorme)
            {
                meilleureNorme = haut;
                meilleurX = ux * haut;
                meilleurY = uy * haut;
            }
        }

        double x = double.IsPositiveInfinity(meilleureNorme) ? secoursX : meilleurX;
        double y = double.IsPositiveInfinity(meilleureNorme) ? secoursY : meilleurY;
        // returned in BALL-RADIUS units (the engine rescales); rounded like toFixed(6)
        return (Math.Round(x / R, 6, MidpointRounding.AwayFromZero), Math.Round(y / R, 6, MidpointRounding.AwayFromZero));
    }

    /// <summary>The face to cover: the expression's if the state accepts it, its own otherwise.</summary>
    static Visage VisageDe(StateDef def, Pose pose, BloubExpression? expr) =>
        def.BaseFace && expr is not null ? new(expr.Gaze, expr.Split, expr.Eyes) : new(pose.Gaze, pose.Split, pose.Eyes);

    /// <summary>Dates to sample in a state: a single one if its pose does not move.</summary>
    static double[] Dates(StateDef def)
    {
        var a = def.Pose(0);
        var b = def.Pose(def.Duration);
        bool same = a.Gaze == b.Gaze && a.Split == b.Split && a.Eyes.AsSpan().SequenceEqual(b.Eyes)
            && a.Sil.Rot == b.Sil.Rot && a.Sil.Cx == b.Sil.Cx && a.Sil.Cy == b.Sil.Cy && a.Sil.Sx == b.Sil.Sx && a.Sil.Sy == b.Sil.Sy;
        if (same) return [0];
        const int n = 3;
        return Enumerable.Range(0, n).Select(i => (double)i / (n - 1) * def.Duration).ToArray();
    }

    /// <summary>The offset of a shape on a state and an expression, drift included.</summary>
    static (double x, double y) DecalagePour(StateDef def, double[] radii, BloubExpression? expr)
    {
        var epreuves = new List<Epreuve>();
        foreach (double t in Dates(def))
        {
            var pose = def.Pose(t);
            var contour = ToPoints(pose.Sil with { Radii = radii }, R);
            var calContour = ToPoints(pose.Sil, R);
            var v = VisageDe(def, pose, expr);
            // The four corners of the drift bound the nominal pose (their centre).
            foreach (double dy in new[] { -DeriveYaw, DeriveYaw })
            {
                foreach (double dp in new[] { -DerivePitch, DerivePitch })
                {
                    var c = v with { Gaze = new HeadGaze(v.Gaze.Yaw + dy, v.Gaze.Pitch + dp, v.Gaze.Roll) };
                    epreuves.Add(new Epreuve(Empreintes(c, pose.Sil, radii), Empreintes(c, pose.Sil, pose.Sil.Radii), contour, calContour));
                }
            }
        }
        return Resous(epreuves);
    }

    /// <summary>Key of an entry: the state, and the expression when the state accepts one.</summary>
    static string Clef(BloubState state, BloubExpressionId? expr) => $"{state}|{expr}";

    static Dictionary<string, (double x, double y)> BuildFor(double[] radii)
    {
        var par = new Dictionary<string, (double, double)>();
        foreach (var def in BloubStates.All)
        {
            if (!def.BaseBody) continue;
            var exprs = def.BaseFace ? new BloubExpression?[] { null }.Concat(BloubExpressions.All) : [null];
            foreach (var expr in exprs)
                par[Clef(def.Id, expr?.Id)] = DecalagePour(def, radii, expr);
        }
        return par;
    }

    /// <summary>Catalogue shapes, by REFERENCE of their radii array (the engine's own identity convention).</summary>
    static readonly ConditionalWeakTable<double[], Lazy<Dictionary<string, (double x, double y)>>> Tables = BuildTables();

    static ConditionalWeakTable<double[], Lazy<Dictionary<string, (double x, double y)>>> BuildTables()
    {
        var t = new ConditionalWeakTable<double[], Lazy<Dictionary<string, (double x, double y)>>>();
        foreach (var (_, radii) in BloubSkins.All)
        {
            var r = radii;
            t.Add(r, new Lazy<Dictionary<string, (double x, double y)>>(() => BuildFor(r), LazyThreadSafetyMode.ExecutionAndPublication));
        }
        return t;
    }

    /// <summary>
    /// Offset to apply to both eyes for this shape on this state, in ball-radius units. Zero for a
    /// shape not in the catalogue (covers null and custom arrays) and for the circle by construction.
    /// </summary>
    public static (double x, double y) DecalageDesYeux(double[]? radii, BloubState state, BloubExpressionId? expr)
    {
        if (radii is null || !Tables.TryGetValue(radii, out var lazy)) return (0, 0);
        var par = lazy.Value;
        // a state without a resting face has a single entry, whatever the expression
        if (par.TryGetValue(Clef(state, expr), out var v)) return v;
        if (par.TryGetValue(Clef(state, null), out v)) return v;
        return (0, 0);
    }

    /// <summary>Builds every shape's table now (optional; call off the UI thread at start-up to avoid a first-use hitch).</summary>
    public static void Warm()
    {
        foreach (var (_, radii) in BloubSkins.All)
            if (Tables.TryGetValue(radii, out var lazy)) _ = lazy.Value;
    }
}
