// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/expressions.ts.

using static Island.Lab.Mascot.BloubMath;

namespace Island.Lab.Mascot;

/// <summary>The 16 resting expressions. Member names are the original (French) TS ids.</summary>
public enum BloubExpressionId
{
    /// <summary>Neutral: the pose measured frame by frame on the reference video.</summary>
    Neutre,
    /// <summary>Attentive.</summary>
    Attentif,
    /// <summary>Surprised.</summary>
    Surpris,
    /// <summary>Excited.</summary>
    Excite,
    /// <summary>Happy: squinted arched eyes, tops converge slightly.</summary>
    Heureux,
    /// <summary>Laughing.</summary>
    Hilare,
    /// <summary>Angry: eye tops converge strongly + narrowed eyes.</summary>
    Colere,
    /// <summary>Sad: tops diverge and the gaze drops.</summary>
    Triste,
    /// <summary>Frightened.</summary>
    Effraye,
    /// <summary>Suspicious: one eye clearly more closed than the other.</summary>
    Mefiant,
    /// <summary>Confused: asymmetric sizes AND tilts.</summary>
    Confus,
    /// <summary>Curious: head roll carries the curiosity.</summary>
    Curieux,
    /// <summary>Proud.</summary>
    Fier,
    /// <summary>Shy.</summary>
    Timide,
    /// <summary>Jaded / bored: horizontal slits, gaze off to the side.</summary>
    Blase,
    /// <summary>Sleepy: half-closed lids via <c>open</c> (the same vertical squash as a blink).</summary>
    Somnolent,
    /// <summary>"Bof" (meh): the shrug. Eyes roll up and away, head tilted. Island addition, not in the original set.</summary>
    Bof,
}

/// <summary>
/// Resting expression. The face is only two capsules, so everything comes down to four levers:
/// head orientation, eye spacing, eye proportions, and each eye's own tilt. The last one is
/// what allows anger and sadness: they need MIRRORED tilts, impossible with head roll alone.
/// </summary>
public sealed record BloubExpression(BloubExpressionId Id, HeadGaze Gaze, double Split, EyeCfg[] Eyes);

public static class BloubExpressions
{
    public static string ToId(this BloubExpressionId e) => e.ToString().ToLowerInvariant();

    /// <summary><c>tilt</c> in degrees, positive = top of the capsule leans right.</summary>
    static EyeCfg Eye(double w, double h, double tilt = 0, double open = 1) => new(w, h, open, tilt);

    /// <summary>Both eyes identical, mirrored tilts if <paramref name="tilt"/> is given.</summary>
    static EyeCfg[] Pair(double w, double h, double tilt = 0, double open = 1) => [Eye(w, h, tilt, open), Eye(w, h, -tilt, open)];

    public static readonly BloubExpression[] All =
    [
        new(BloubExpressionId.Neutre, BloubFace.RestGaze, BloubFace.EyeSplit, [Eye(BloubFace.EyeW, BloubFace.EyeH), Eye(BloubFace.EyeW, BloubFace.EyeH)]),
        new(BloubExpressionId.Attentif, new(4, 5, -4), 16, Pair(0.21, 0.44)),
        new(BloubExpressionId.Surpris, new(3, -3, 0), 19, Pair(0.45, 0.47)),
        new(BloubExpressionId.Excite, new(6, -14, 0), 19.5, Pair(0.4, 0.56, -10)),
        new(BloubExpressionId.Heureux, new(5, 9, 0), 17, Pair(0.27, 0.17, 14)),
        new(BloubExpressionId.Hilare, new(4, 14, 0), 18, Pair(0.34, 0.13, 20)),
        new(BloubExpressionId.Colere, new(3, 7, 0), 17, Pair(0.34, 0.15, 30)),
        new(BloubExpressionId.Triste, new(3, -13, 0), 16, Pair(0.22, 0.4, -28)),
        new(BloubExpressionId.Effraye, new(2, -20, 0), 20.5, Pair(0.4, 0.6)),
        new(BloubExpressionId.Mefiant, new(12, 6, -6), 16, [Eye(0.21, 0.4), Eye(0.22, 0.15)]),
        // The squinted eye is deliberately flat (ratio 1.6): close to 1 it would be round and its tilt invisible.
        new(BloubExpressionId.Confus, new(-14, 3, 8), 16.5, [Eye(0.2, 0.44, -18), Eye(0.28, 0.17, 14)]),
        new(BloubExpressionId.Curieux, new(16, -9, -15), 16.5, [Eye(0.24, 0.46, -8), Eye(0.2, 0.38, -8)]),
        new(BloubExpressionId.Fier, new(5, 17, 0), 17, Pair(0.3, 0.15, 18)),
        new(BloubExpressionId.Timide, new(-19, -14, -7), 14, Pair(0.17, 0.3)),
        new(BloubExpressionId.Blase, new(-22, 2, 0), 16, Pair(0.3, 0.12)),
        new(BloubExpressionId.Somnolent, new(6, -9, -3), 16, Pair(0.2, 0.42, 0, 0.42)),
        new(BloubExpressionId.Bof, new(-22, 24, 12), 15.5, Pair(0.2, 0.34)),
    ];

    /// <summary>Catalogue instance for an id (stable reference, as the engine compares by reference).</summary>
    public static BloubExpression Get(BloubExpressionId id) => All[(int)id];

    public const BloubExpressionId Default = BloubExpressionId.Neutre;

    static EyeCfg LerpEye(EyeCfg a, EyeCfg b, double t) =>
        new(Lerp(a.W, b.W, t), Lerp(a.H, b.H, t), Lerp(a.Open, b.Open, t), Lerp(a.Tilt, b.Tilt, t));

    /// <summary>Interpolates two expressions: changes glide.</summary>
    public static BloubExpression Blend(BloubExpression a, BloubExpression b, double t) => new(
        b.Id,
        new HeadGaze(Lerp(a.Gaze.Yaw, b.Gaze.Yaw, t), Lerp(a.Gaze.Pitch, b.Gaze.Pitch, t), Lerp(a.Gaze.Roll, b.Gaze.Roll, t)),
        Lerp(a.Split, b.Split, t),
        [LerpEye(a.Eyes[0], b.Eyes[0], t), LerpEye(a.Eyes[1], b.Eyes[1], t)]);
}
