// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/skins.ts.

using static Island.Lab.Mascot.BloubShape;

namespace Island.Lab.Mascot;

/// <summary>Customiser body shapes. Member names are the original (French) TS ids.</summary>
public enum BloubShapeId
{
    /// <summary>Circle (the reference ball).</summary>
    Cercle,
    /// <summary>Pebble: circle deformed by two low harmonics.</summary>
    Galet,
    Squircle,
    /// <summary>Lying capsule.</summary>
    Capsule,
    Triangle,
    /// <summary>Hexagon, vertices left/right (flat top and bottom).</summary>
    Hexagone,
    /// <summary>Cloud: union of bumps.</summary>
    Nuage,
    /// <summary>Droplet: big disc at the bottom, tapered point at the top.</summary>
    Goutte,
}

/// <summary>Customiser colour ids (original palette).</summary>
public enum BloubColorId { Encre, Creme, Brun, Rouge, Orange, Ambre, Vert, Turquoise, Bleu, Violet, Rose, Gris }

/// <summary>
/// Shapes and colours offered by the customiser. Unlike the animation silhouettes (profiles),
/// these are NOT measured off the video: they are built analytically. A chosen shape only
/// replaces the body on states flagged <see cref="StateDef.BaseBody"/>.
/// </summary>
public static class BloubSkins
{
    public static string ToId(this BloubShapeId s) => s.ToString().ToLowerInvariant();

    /// <summary>Scales the radii so the maximum is <paramref name="max"/>: every shape weighs the same to the eye.</summary>
    static double[] Normalize(double[] radii, double max = 1)
    {
        double peak = radii.Max();
        if (peak <= 0) return radii;
        double k = max / peak;
        return radii.Select(r => r * k).ToArray();
    }

    static double[] Pebble() => Normalize(
        Angles.Select(a => 1 + 0.075 * Math.Cos(2 * a + 0.5) + 0.035 * Math.Cos(3 * a + 2.1)).ToArray(), 1.02);

    static double[] Cloud() => Normalize(UnionOfCirclesProfile(
    [
        (-0.44, 0.2, 0.54),
        (0.46, 0.2, 0.5),
        (0.02, 0.3, 0.6),
        (-0.24, -0.3, 0.48),
        (0.3, -0.24, 0.44),
    ]), 1.02);

    static double[] Droplet() => Normalize(ProfileFromPolygon(HullOfCircles(0, 0.28, 0.66, 0, -0.96, 0.05), 0, 0), 1.04);

    static double[] CapsuleProfile() => ProfileFromPolygon(HullOfCircles(-0.42, 0, 0.62, 0.42, 0, 0.62), 0, 0);

    /// <summary>
    /// Radii for each shape. These arrays are the identity the engine and the eye-fit table key on
    /// (by reference, like the original): never mutate them.
    /// </summary>
    static readonly double[][] Radii =
    [
        Filled(1),
        Pebble(),
        // 1.15 and not 1.02: on a superellipse the max radius is the diagonal, so normalising on it
        // gives a shape that looks smaller than the circle.
        Normalize(SuperellipseProfile(4.2), 1.15),
        CapsuleProfile(),
        // -90deg: a vertex at the top of the screen (y points down)
        RegularPolygonProfile(3, 1.12, 0.34, -90),
        // 0deg: vertices left and right, so flat top and bottom edges
        RegularPolygonProfile(6, 1.04, 0.26, 0),
        Cloud(),
        Droplet(),
    ];

    public static double[] Get(BloubShapeId id) => Radii[(int)id];

    public static IEnumerable<(BloubShapeId id, double[] radii)> All =>
        Enum.GetValues<BloubShapeId>().Select(id => (id, Radii[(int)id]));

    public const BloubShapeId DefaultShape = BloubShapeId.Cercle;

    /// <summary>Original customiser palette, 0xRRGGBB.</summary>
    public static uint ColorHex(BloubColorId id) => id switch
    {
        BloubColorId.Encre => 0x0a0a0c,
        BloubColorId.Brun => 0x8b5e3c,
        BloubColorId.Rouge => 0xe8483f,
        BloubColorId.Orange => 0xf08a24,
        BloubColorId.Ambre => 0xf0b429,
        BloubColorId.Vert => 0x3ecf8e,
        BloubColorId.Turquoise => 0x2fbfa0,
        BloubColorId.Bleu => 0x3b93f0,
        BloubColorId.Violet => 0x8b5cf6,
        BloubColorId.Rose => 0xe152b0,
        BloubColorId.Gris => 0xa3a3a3,
        _ => 0xf1efe9, // creme
    };
}
