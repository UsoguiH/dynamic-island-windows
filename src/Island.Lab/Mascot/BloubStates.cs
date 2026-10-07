// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/states.ts.

using static Island.Lab.Mascot.BloubMath;
using static Island.Lab.Mascot.BloubDecor;
using static Island.Lab.Mascot.BloubShape;

namespace Island.Lab.Mascot;

/// <summary>The 15 animation states (TS ids in lowercase: idle, thinking, wink, ...).</summary>
public enum BloubState
{
    Idle, Thinking, Wink, Wide, Alert, Notify, Exclaim, Sleep, Egg, Hexagon, Play, Orbit,
    /// <summary>Interface transition (settings view entry), not a catalogue animation: outside <see cref="BloubStates.Sequence"/>.</summary>
    Swirl,
    Burst, Comet,
}

/// <summary>Configuration of one capsule eye.</summary>
/// <param name="W">Local width (short axis of the capsule), ball-radius units.</param>
/// <param name="H">Local height (long axis).</param>
/// <param name="Open">1 = open, 0 = closed.</param>
/// <param name="Tilt">
/// Own tilt of the capsule, degrees, positive = top leans right. Applied AFTER the sphere's
/// tangent frame. Without it both eyes necessarily lean the same way (head roll), and anger or
/// sadness, which need mirrored tilts, are out of reach.
/// </param>
public readonly record struct EyeCfg(double W, double H, double Open = 1, double Tilt = 0);

/// <summary>Notification pastille, ball-radius units.</summary>
public readonly record struct NotifSpec(double X, double Y, double R, double Notch);

/// <summary>A full pose: everything a state declares at a given local time.</summary>
public sealed record Pose
{
    /// <summary>Body silhouette, ball-radius units.</summary>
    public required Silhouette Sil { get; init; }
    /// <summary>Global offset of the body AND the eyes.</summary>
    public double OffX { get; init; }
    public double OffY { get; init; }
    public HeadGaze Gaze { get; init; }
    /// <summary>Half-spacing of the eyes on the sphere, degrees.</summary>
    public double Split { get; init; }
    /// <summary>[inner eye, outer eye].</summary>
    public required EyeCfg[] Eyes { get; init; }
    /// <summary>Eye opacity: used by faceless states.</summary>
    public double EyeAlpha { get; init; }
    public double BodyAlpha { get; init; }
    public required List<DotSpec> Dots { get; init; }
    public required List<ArcSpec> Arcs { get; init; }
    public NotifSpec? Notif { get; init; }
    /// <summary>true = the decor passes behind the body (burst particles).</summary>
    public bool DotsBehind { get; init; }
}

/// <summary>Static description of one state.</summary>
public sealed class StateDef
{
    public required BloubState Id { get; init; }
    /// <summary>Hold duration when the full sequence is played.</summary>
    public required double Duration { get; init; }
    /// <summary>
    /// Duration below which the animation is cut before resolving (the "!" does not come back,
    /// the body stays burst). Null = the state ignores time or loops.
    /// </summary>
    public double? MinDuration { get; init; }
    /// <summary>Duration of the entry morph.</summary>
    public required double Morph { get; init; }
    /// <summary>true = the entry is masked by a blink, as in the video.</summary>
    public required bool BlinkIn { get; init; }
    /// <summary>
    /// true = the body is the "resting" silhouette, so it can be replaced by the shape picked in
    /// the customiser. States that draw their own shape ("!", dots, egg, triangle...) are false:
    /// that shape IS the animation.
    /// </summary>
    public required bool BaseBody { get; init; }
    /// <summary>
    /// true = the state wears the resting face, so it can be replaced by the chosen expression.
    /// Only idle (and swirl): the other faced states have an expression measured off the video.
    /// </summary>
    public required bool BaseFace { get; init; }
    public required Func<double, Pose> Pose { get; init; }
}

/// <summary>The state catalogue.</summary>
public static class BloubStates
{
    public static string ToId(this BloubState s) => s.ToString().ToLowerInvariant();

    public static BloubState Parse(string id) => Enum.Parse<BloubState>(id, ignoreCase: true);

    static EyeCfg[] Pair(double w, double h) => [new(w, h), new(w, h)];

    static Pose Base(
        Silhouette? sil = null, double offX = 0, double offY = 0, HeadGaze? gaze = null, double split = BloubFace.EyeSplit,
        EyeCfg[]? eyes = null, double eyeAlpha = 1, double bodyAlpha = 1, List<DotSpec>? dots = null,
        List<ArcSpec>? arcs = null, NotifSpec? notif = null, bool dotsBehind = false) => new()
        {
            Sil = sil ?? Circle(1),
            OffX = offX,
            OffY = offY,
            Gaze = gaze ?? BloubFace.RestGaze,
            Split = split,
            Eyes = eyes ?? Pair(BloubFace.EyeW, BloubFace.EyeH),
            EyeAlpha = eyeAlpha,
            BodyAlpha = bodyAlpha,
            Dots = dots ?? [],
            Arcs = arcs ?? [],
            Notif = notif,
            DotsBehind = dotsBehind,
        };

    /* --------------------------------------------------- non-radial shapes */

    /// <summary>
    /// Bar of the upright "!": convex hull of two circles. Measured: top circle (0, -0.505)
    /// r 0.132, bottom circle (0, +0.130) r 0.075, straight flanks — hence conical (ratio 1.76).
    /// </summary>
    const double BarUprightCy = -0.1875;
    static readonly double[] BarUprightProfile = ProfileFromPolygon(HullOfCircles(0, -0.505, 0.132, 0, 0.13, 0.075), 0, BarUprightCy);

    /// <summary>Bar of the slanted "!": a pure capsule (constant width 0.269, length 0.776).</summary>
    static readonly double[] BarItalicProfile = ProfileFromPolygon(HullOfCircles(0, -0.2535, 0.1345, 0, 0.2535, 0.1345), 0, 0);

    /// <summary>
    /// The slanted "!" dot is not a disc: it is a teardrop, round end (r 0.118) towards the bar and
    /// a tapered point opposite, length 0.300 along the glyph axis. Centred on the round end.
    /// </summary>
    public static readonly PointD[] Tear = HullOfCircles(0, 0, 0.118, 0, 0.172, 0.012).ToArray();

    /// <summary>
    /// The triangle does not spin on itself: its centre describes a circle of radius 0.213 around
    /// the origin (measured). That offset makes it look like it tumbles instead of pivoting.
    /// </summary>
    const double TriOrbit = 0.213;

    static Silhouette SpinningTriangle(double rot) => FromProfile(ProfileName.Triangle) with
    {
        Rot = rot,
        Cx = -TriOrbit * Math.Sin(rot),
        Cy = TriOrbit * Math.Cos(rot),
    };

    /// <summary>Pulse wave travelling through the three dots from left to right.</summary>
    static double DotPulse(double t, int index)
    {
        double p = ((t - index * 0.5) / 1.5 % 1 + 1) % 1;
        double k = p < 0.5 ? 0.5 - 0.5 * Math.Cos(p * Tau) : 0;
        return Clamp(k * 2);
    }

    public static readonly StateDef[] All =
    [
        new()
        {
            Id = BloubState.Idle, Duration = 2.4, Morph = 0.45, BlinkIn = false, BaseFace = true, BaseBody = true,
            Pose = _ => Base(),
        },

        new()
        {
            Id = BloubState.Thinking, Duration = 2.6, Morph = 0.4, BaseFace = false, BaseBody = false, BlinkIn = true,
            Pose = t =>
            {
                double mid = DotPulse(t, 1);
                // The side dots come out of the ball's flanks: in the video they stay merged with it
                // for 1-2 frames before detaching.
                double emerge = 0.3 + 0.7 * EaseOutCubic(Clamp(t / 0.3));
                return Base(
                    // the ball BECOMES the middle dot: the morph stays continuous
                    sil: Circle(DotR * (1 + (DotPeak - 1) * mid)) with { Cx = DotX[1] },
                    eyeAlpha: 0,
                    dots: new[] { 0, 2 }.Select(i =>
                    {
                        double k = DotPulse(t, i);
                        return new DotSpec { X = DotX[i] * emerge, Y = 0, R = DotR * (1 + (DotPeak - 1) * k), Opacity = 0.55 + 0.45 * k };
                    }).ToList());
            },
        },

        new()
        {
            Id = BloubState.Wink, Duration = 1.6, Morph = 0.3, BlinkIn = true, BaseFace = false, BaseBody = true,
            Pose = _ => Base(
                gaze: new HeadGaze(-5.37, 4.55, 6.7),
                split: 16.25,
                // The closed eye is not the open eye squashed: it is a horizontal dash WIDER than
                // the open eye (0.447 vs 0.236).
                eyes: [new(0.236, 0.464), new(0.447, 0.089)]),
        },

        new()
        {
            Id = BloubState.Wide, Duration = 1.8, Morph = 0.55, BlinkIn = true, BaseFace = false, BaseBody = true,
            Pose = _ => Base(gaze: new HeadGaze(6.92, -21.96, 11.6), split: 18.43, eyes: Pair(0.356, 0.875)),
        },

        new()
        {
            // the "!" is back in place at 1.6 + 0.4
            Id = BloubState.Alert, Duration = 2.4, MinDuration = 2, Morph = 0.45, BaseFace = false, BaseBody = false, BlinkIn = false,
            Pose = t =>
            {
                // Measured travel: -0.087 -> +0.732 in 1.5 s, ease-in-out, micro-overshoot.
                double p = Clamp(t / 1.5);
                double travel = EaseInOutCubic(p) * 0.82 - 0.087;
                double back = t > 1.6 ? Clamp((t - 1.6) / 0.4) : 0;
                double x = travel * (1 - back) + 0.1 * back;
                // Secondary 2.5 Hz vibration, bar and dot in phase opposition.
                double buzz = Math.Sin(t * 2.5 * Tau) * 0.005;
                double tilt = 17.7 * Math.PI / 180;
                return Base(
                    sil: new Silhouette { Radii = (double[])BarItalicProfile.Clone(), Rot = tilt, Cx = x, Cy = -0.325 - buzz },
                    eyeAlpha: 0,
                    dots:
                    [
                        new DotSpec
                        {
                            // the dot follows the glyph axis, 0.580 from the bar centre
                            X = x - Math.Sin(tilt) * 0.58,
                            Y = -0.325 + Math.Cos(tilt) * 0.58 + buzz * 2.8,
                            R = 0.118,
                            Shape = Tear,
                            Rot = tilt * 180 / Math.PI,
                            Opacity = 1,
                        },
                    ]);
            },
        },

        new()
        {
            Id = BloubState.Notify, Duration = 2.2, Morph = 0.5, BlinkIn = true, BaseFace = false, BaseBody = true,
            Pose = t =>
            {
                // Blue dot pop: +14 % peak around 0.3 s, then settles.
                double p = Clamp(t / 0.45);
                double pop = 1 + (NotifPop - 1) * Math.Sin(p * Math.PI) * (1 - p * 0.35);
                double r = NotifR * (p < 1 ? pop : 1);
                double a = NotifAngle * Math.PI / 180;
                return Base(
                    // the gaze goes away from the pastille
                    gaze: new HeadGaze(-21.94, -5.82, -12.2),
                    split: 18.89,
                    eyes: Pair(0.505, 0.498),
                    notif: new NotifSpec(Math.Cos(a) * NotifDist, Math.Sin(a) * NotifDist, r, r + NotifMargin));
            },
        },

        new()
        {
            Id = BloubState.Exclaim, Duration = 2, Morph = 0.45, BaseFace = false, BaseBody = false, BlinkIn = false,
            Pose = _ => Base(
                sil: new Silhouette { Radii = (double[])BarUprightProfile.Clone(), Cy = BarUprightCy },
                eyeAlpha: 0,
                dots: [new DotSpec { X = -0.012, Y = 0.526, R = 0.113, Opacity = 1 }]),
        },

        new()
        {
            Id = BloubState.Sleep, Duration = 2.4, Morph = 0.5, BaseFace = false, BaseBody = false, BlinkIn = false,
            // Measured vertical bounce: +-0.19 around +0.11, period 0.6 s.
            Pose = t => Base(sil: Circle(0.1585) with { Cy = 0.11 + Math.Sin(t * (Tau / 0.6)) * 0.19 }, eyeAlpha: 0),
        },

        new()
        {
            Id = BloubState.Egg, Duration = 1.8, Morph = 0.4, BaseFace = false, BaseBody = false, BlinkIn = true,
            Pose = _ => Base(
                sil: FromProfile(ProfileName.Egg),
                gaze: new HeadGaze(19.97, 26.01, -17.1),
                // the eyes close in like the body
                split: 11.07,
                eyes: Pair(0.164, 0.385)),
        },

        new()
        {
            Id = BloubState.Hexagon, Duration = 1.6, Morph = 0.4, BaseFace = false, BaseBody = false, BlinkIn = true,
            Pose = _ => Base(
                sil: FromProfile(ProfileName.Hexagon),
                gaze: new HeadGaze(23.11, 24.42, -13.3),
                split: 13.37,
                eyes: Pair(0.177, 0.411)),
        },

        new()
        {
            Id = BloubState.Play, Duration = 2, Morph = 0.5, BaseFace = false, BaseBody = false, BlinkIn = true,
            Pose = t =>
            {
                // The triangle stays almost still while the bouquet crosses it.
                double fade = Clamp(t / 0.35) * Clamp((2.2 - t) / 0.5);
                return Base(
                    sil: SpinningTriangle(0),
                    gaze: new HeadGaze(12, -8, -6),
                    split: 15,
                    eyes: Pair(0.18, 0.34),
                    // the bouquet sweeps right to left over the triangle
                    arcs: Swoosh.Select((s, i) => new ArcSpec($"sw{i}", s with { Cx = 0.45 - t * 0.42 }, t, fade)).ToList());
            },
        },

        new()
        {
            // the body has finished relaxing from the triangle to the ball at 1.6 + 0.9
            Id = BloubState.Orbit, Duration = 3.4, MinDuration = 2.5, Morph = 0.6, BaseFace = false, BaseBody = false, BlinkIn = false,
            Pose = t =>
            {
                // Measured rotation: ramp over 0.35 s, then 1.25 turn/s (counter-clockwise).
                double ramp = EaseInOutCubic(Clamp(t / 0.35));
                double rot = -Tau * 1.25 * t * ramp;
                // The body relaxes from the triangle back to the ball during the orbit.
                double back = EaseInOutCubic(Clamp((t - 1.6) / 0.9));
                var tri = SpinningTriangle(rot);
                var radii = new double[ProfileSamples];
                for (int i = 0; i < ProfileSamples; i++) radii[i] = tri.Radii[i] + (1 - tri.Radii[i]) * back;
                var sil = new Silhouette { Radii = radii, Rot = rot, Cx = tri.Cx * (1 - back), Cy = tri.Cy * (1 - back) };
                double fade = Clamp(t / 0.8) * Clamp((3.6 - t) / 0.9);
                return Base(
                    sil: sil,
                    // the eyes race around the sphere ~3x faster than the silhouette
                    gaze: new HeadGaze(BloubFace.RestGaze.Yaw + Math.Sin(t * 6.5) * 65 * (1 - back), -4 + back * 32, -13),
                    eyes: Pair(0.18, 0.34 + back * 0.07),
                    // the rings come in one by one over 0.8 s
                    arcs: Rings.Select((s, i) => new ArcSpec($"rg{i}", s, t, fade * Clamp((t - i * 0.13) / 0.3))).ToList());
            },
        },

        new()
        {
            // Settings-view entry. The ONLY state not measured off the video: it is CHOSEN. It
            // borrows orbit's vocabulary (same rings) but cuts short: 1 s instead of 3.4, half the
            // rings, no triangle. Both flags true are the whole point: BaseBody lets the chosen
            // shape morph instead of jumping, BaseFace lets cursor tracking apply from frame one.
            // Deliberately NOT in Sequence.
            Id = BloubState.Swirl,
            // a bit longer than the gaze turn (TURN_TIME, 1.1 s): the eyes must have landed
            // before the rings fade out
            Duration = 1.3, MinDuration = 1.3, Morph = 0.3, BaseFace = true, BaseBody = true,
            // the shape morph is masked by a blink, like everywhere else
            BlinkIn = true,
            Pose = t => Base(
                // three of orbit's six rings: half the bouquet is enough to recognise it
                arcs: Rings.Take(3).Select((s, i) => new ArcSpec($"sw{i}", s, t,
                    // they come in one after another then fade before the end of the block,
                    // so the return to rest happens on an already clean image
                    Clamp((t - i * 0.06) / 0.14) * Clamp((1.22 - t) / 0.34))).ToList()),
        },

        new()
        {
            // the body is rebuilt at 1.7 + 0.7
            Id = BloubState.Burst, Duration = 2.6, MinDuration = 2.4, Morph = 0.4, BaseFace = false, BaseBody = false, BlinkIn = false,
            Pose = t =>
            {
                // Measured collapse: 1.0 -> 0.166 in 0.7 s, ease-out, no bounce.
                double collapse = 1 - 0.834 * EaseOutQuint(Clamp(t / 0.7));
                double regrow = EaseOutQuint(Clamp((t - 1.7) / 0.7));
                return Base(
                    sil: Circle(collapse + (1 - collapse) * regrow),
                    eyeAlpha: Clamp((t - 1.85) / 0.4),
                    dots: ParticleDots(t, 1),
                    dotsBehind: true);
            },
        },

        new()
        {
            // The dot rebuilds at 1.85 + 0.6 = 2.45, 0.05 s after the video's cut: that remainder
            // finishes during the next fade, as in the reference.
            Id = BloubState.Comet, Duration = 2.4, MinDuration = 2.4, Morph = 0.45, BaseFace = false, BaseBody = false, BlinkIn = false,
            Pose = t =>
            {
                double collapse = 1 - (1 - CometDot) * EaseOutQuint(Clamp(t / 0.55));
                double regrow = EaseOutQuint(Clamp((t - 1.85) / 0.6));
                double fade = Clamp((t - 0.15) / 0.25) * Clamp((1.95 - t) / 0.3);
                return Base(
                    // The dot drifts 0.035 down then comes back up (measured wobble).
                    sil: Circle(collapse + (1 - collapse) * regrow) with { Cy = Math.Sin(Clamp(t / 1.7) * Math.PI) * 0.035 },
                    eyeAlpha: Clamp((t - 2) / 0.35),
                    arcs: CometRibbons.Select((s, i) => new ArcSpec($"cm{i}", s, t, fade)).ToList());
            },
        },
    ];

    static readonly StateDef[] ById = BuildIndex();

    static StateDef[] BuildIndex()
    {
        var a = new StateDef[Enum.GetValues<BloubState>().Length];
        foreach (var s in All) a[(int)s.Id] = s;
        return a;
    }

    public static StateDef Get(BloubState id) => ById[(int)id];

    /// <summary>Local time at which each state is most readable (used for thumbnails).</summary>
    public static double PoseTime(BloubState id) => id switch
    {
        BloubState.Idle => 1, BloubState.Thinking => 1.1, BloubState.Wink => 0.8, BloubState.Wide => 0.8,
        BloubState.Alert => 0.75, BloubState.Notify => 0.9, BloubState.Exclaim => 0.8, BloubState.Sleep => 0.45,
        BloubState.Egg => 0.8, BloubState.Hexagon => 0.8, BloubState.Play => 0.9, BloubState.Orbit => 1.2,
        BloubState.Swirl => 0.5, BloubState.Burst => 0.45, _ => 1.15,
    };

    /// <summary>Playback order of the full sequence, copied from the reference video.</summary>
    public static readonly BloubState[] Sequence =
    [
        BloubState.Idle, BloubState.Thinking, BloubState.Wink, BloubState.Wide, BloubState.Alert, BloubState.Notify,
        BloubState.Exclaim, BloubState.Sleep, BloubState.Egg, BloubState.Hexagon, BloubState.Play, BloubState.Orbit,
        BloubState.Burst, BloubState.Comet,
    ];
}
