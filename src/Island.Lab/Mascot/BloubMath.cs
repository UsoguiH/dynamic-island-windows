// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/math.ts and src/bot/repere.ts.

namespace Island.Lab.Mascot;

/// <summary>Small math helpers shared by the whole engine (math.ts).</summary>
public static class BloubMath
{
    public const double Tau = Math.PI * 2;

    /// <summary>
    /// Radius of the resting ball in viewBox units. It is the <c>scale</c> passed to the engine.
    /// Chosen, not measured: it is the working unit. Everything else is expressed as a
    /// fraction of this radius, which makes the video measurements independent of display size.
    /// </summary>
    public const double Rayon = 100;

    /// <summary>
    /// Half-side of the displayed viewBox. The margin beyond the radius houses the rings
    /// (orbit rings and the comet swoosh reach 1.4 radii, i.e. 140 &lt; 158).
    /// </summary>
    public const double DemiViewBox = 158;

    public static double Clamp(double v, double lo = 0, double hi = 1) => v < lo ? lo : v > hi ? hi : v;
    public static double Lerp(double a, double b, double t) => a + (b - a) * t;

    /// <summary>
    /// Measured on the video: transitions are exponential ease-outs with no body overshoot.
    /// The only spring effects are local (notification pastille pop) and live in their state.
    /// </summary>
    public static double EaseOutCubic(double t) => 1 - Math.Pow(1 - t, 3);
    public static double EaseInOutCubic(double t) => t < 0.5 ? 4 * Math.Pow(t, 3) : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double EaseOutQuint(double t) => 1 - Math.Pow(1 - t, 5);

    /// <summary>Periodic 1D noise: loops seamlessly over <paramref name="period"/>; drives gaze drift.</summary>
    public static double LoopNoise(double t, double period, double seed = 0)
    {
        double p = t / period * Tau;
        return 0.55 * Math.Sin(p + seed)
             + 0.3 * Math.Sin(2 * p + seed * 1.7 + 1.1)
             + 0.15 * Math.Sin(3 * p + seed * 2.3 + 2.4);
    }

    /// <summary>Deterministic PRNG (mulberry32), bit-identical to the TS version: same sequence every run.</summary>
    public static Func<double> CreateRng(uint seed)
    {
        uint a = seed;
        return () =>
        {
            unchecked
            {
                a += 0x6d2b79f5;
                uint t = (a ^ (a >> 15)) * (1 | a);
                t = (t + (t ^ (t >> 7)) * (61 | t)) ^ t;
                return (t ^ (t >> 14)) / 4294967296.0;
            }
        };
    }

    public static double Deg(double d) => d * Math.PI / 180;
}
