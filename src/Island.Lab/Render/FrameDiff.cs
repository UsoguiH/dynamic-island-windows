using System.Numerics;
using Vortice.Mathematics;

namespace Island.Lab.Render;

/// <summary>An axis-aligned rectangle (left, top, right, bottom), in DIPs.</summary>
public readonly record struct Area(float L, float T, float R, float B)
{
    public static Area Of(float x, float y, float w, float h) => new(x, y, x + w, y + h);
    public bool Empty => R <= L || B <= T;
    public Area Union(Area o) => Empty ? o : o.Empty ? this : new(MathF.Min(L, o.L), MathF.Min(T, o.T), MathF.Max(R, o.R), MathF.Max(B, o.B));
    public Area Intersect(Area o) => new(MathF.Max(L, o.L), MathF.Max(T, o.T), MathF.Min(R, o.R), MathF.Min(B, o.B));
    public bool Overlaps(Area o) => L < o.R && o.L < R && T < o.B && o.T < B;
    public Area Inflate(float d) => new(L - d, T - d, R + d, B + d);
}

/// <summary>
/// A running 64-bit hash of what a primitive draws. Positions are quantised to 1/16 DIP and alphas to
/// 1/1024: steps far below what anti-aliasing can show, so an unchanged hash means unchanged pixels.
/// </summary>
public struct FrameHash
{
    public ulong V;
    public FrameHash(ulong seed) => V = seed * 0x9E3779B97F4A7C15UL + 0xcbf29ce484222325UL;

    public FrameHash I(long x)
    {
        V ^= (ulong)x + 0x9E3779B97F4A7C15UL + (V << 6) + (V >> 2);
        V *= 0x100000001B3UL;
        V ^= V >> 31;
        return this;
    }
    /// <summary>A position or size, in DIPs.</summary>
    public FrameHash P(float v) => I(float.IsFinite(v) ? (long)MathF.Round(v * 16) : long.MinValue);
    /// <summary>An alpha, a ratio or a matrix coefficient.</summary>
    public FrameHash A(float v) => I(float.IsFinite(v) ? (long)MathF.Round(v * 1024) : long.MinValue);
    /// <summary>An exact value: text snaps its glyphs to the pixel grid, so even a tiny move can shift it a whole pixel.</summary>
    public FrameHash F(float v) => I(BitConverter.SingleToInt32Bits(v));
    public FrameHash C(Color4 c) => I((long)MathF.Round(c.R * 512) | (long)MathF.Round(c.G * 512) << 12 | (long)MathF.Round(c.B * 512) << 24 | (long)MathF.Round(c.A * 1024) << 36);
    public FrameHash S(string s) => I(s.GetHashCode());
    public FrameHash M(Matrix3x2 m) => A(m.M11).A(m.M12).A(m.M21).A(m.M22).P(m.M31).P(m.M32);

    public static ulong Mix(ulong a, ulong b) => new FrameHash(a).I((long)b).V;
}
