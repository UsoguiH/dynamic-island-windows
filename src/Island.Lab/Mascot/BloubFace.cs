// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/bot/face.ts.

using static Island.Lab.Mascot.BloubMath;

namespace Island.Lab.Mascot;

/// <summary>Head orientation, in degrees.</summary>
/// <param name="Yaw">Positive = looks right.</param>
/// <param name="Pitch">Positive = looks up.</param>
/// <param name="Roll">Head tilt.</param>
public readonly record struct HeadGaze(double Yaw, double Pitch, double Roll);

/// <summary>An eye projected from the sphere: centre plus the 2x2 tangent matrix [a b c d] (SVG matrix order).</summary>
public readonly record struct EyePose(double X, double Y, double A, double B, double C, double D, double Depth);

/// <summary>Resting life offsets (added to the current state's pose).</summary>
public readonly record struct Liveliness(double DYaw, double DPitch, double DRoll, double Lid, double DriftX, double DriftY, double Breath);

/// <summary>
/// The eyes are painted on a sphere, not laid flat.
///
/// Measured on the video: the eye nearest the edge is 0.69 times the width of the other and
/// 0.663 times its area — exactly the depth factor (z = 0.669) of a sphere point at that
/// distance from the centre. So a real head orientation is modelled: each eye takes the
/// sphere's tangent frame, projected orthographically; compression and tilt follow by
/// themselves, which gives the volume. The constants come from fitting the model to the
/// positions and sizes measured frame by frame (~1 px residual on a 190 px radius).
/// </summary>
public static class BloubFace
{
    /// <summary>Half-spacing of the eyes on the sphere, degrees (total separation ~31deg).</summary>
    public const double EyeSplit = 15.46;
    /// <summary>Resting eye size, in ball-radius units.</summary>
    public const double EyeW = 0.186;
    public const double EyeH = 0.412;

    /// <summary>Resting head orientation, fitted on the reference frames.</summary>
    public static readonly HeadGaze RestGaze = new(28.49, 28.62, -13);

    /// <summary>Rotates two vectors of an orthonormal frame within their common plane.</summary>
    static ((double, double, double), (double, double, double)) Spin(
        (double x, double y, double z) u, (double x, double y, double z) v, double angle)
    {
        double c = Math.Cos(angle), s = Math.Sin(angle);
        return ((u.x * c + v.x * s, u.y * c + v.y * s, u.z * c + v.z * s),
                (v.x * c - u.x * s, v.y * c - u.y * s, v.z * c - u.z * s));
    }

    /// <summary>
    /// Head frame then the two eyes. Screen frame: x right, y down, z towards the viewer.
    /// Index 0 is the inner eye, index 1 the outer eye.
    /// </summary>
    public static (EyePose inner, EyePose outer) EyePoses(HeadGaze gaze, double scale, double split = EyeSplit)
    {
        (double, double, double) f = (0, 0, 1), right = (1, 0, 0), down = (0, 1, 0);
        // yaw: forward tips towards right
        (f, right) = Spin(f, right, Deg(gaze.Yaw));
        // pitch: forward tips upwards (away from down)
        (down, f) = Spin(down, f, Deg(gaze.Pitch));
        // roll: the head tilts within its own plane
        (right, down) = Spin(right, down, Deg(gaze.Roll));

        EyePose Build(double side)
        {
            var (ef, er) = Spin(f, right, Deg(split * side));
            return new EyePose(ef.Item1 * scale, ef.Item2 * scale, er.Item1, er.Item2, down.Item1, down.Item2, ef.Item3);
        }
        return (Build(-1), Build(1));
    }

    static readonly Func<double> BlinkRng = CreateRng(0x5eed);

    /// <summary>Pre-drawn blink schedule: deterministic and stateless.</summary>
    static readonly double[] Blinks = BuildBlinks();

    static double[] BuildBlinks()
    {
        var o = new List<double>();
        double t = 1.4;
        while (t < 900)
        {
            o.Add(t);
            // 1.9 to 4.6 s between blinks, plus an occasional double blink
            t += 1.9 + BlinkRng() * 2.7;
            if (BlinkRng() < 0.18)
            {
                o.Add(t);
                t += 0.24;
            }
        }
        return o.ToArray();
    }

    /// <summary>Measured: 1 to 2 frames at 10 fps.</summary>
    const double BlinkDur = 0.18;

    static double BlinkLid(double t)
    {
        // Same result as the original linear scan (blinks never overlap: spacing >= 0.24 > 0.18),
        // but jumps straight to the last blink that started at or before t.
        int idx = Array.BinarySearch(Blinks, t);
        if (idx < 0) idx = ~idx - 1;
        if (idx < 0) return 1;
        double k = (t - Blinks[idx]) / BlinkDur;
        if (k >= 0 && k <= 1)
            // fast closing, slightly slower reopening
            return k < 0.45 ? 1 - k / 0.45 : (k - 0.45) / 0.55;
        return 1;
    }

    /// <summary>
    /// Resting life: slow gaze drift, blinks. Pure function of time (no internal state), so
    /// pause, resume and seeking always give the same image. Values are OFFSETS.
    /// </summary>
    public static Liveliness Life(double t, double wander = 1, bool blink = true, bool floating = true)
    {
        // Mutually prime periods: the drift never visibly repeats.
        return new Liveliness(
            DYaw: (LoopNoise(t, 11.3, 0.4) * 5.5 + LoopNoise(t, 3.7, 2.1) * 1.6) * wander,
            DPitch: (LoopNoise(t, 9.1, 1.3) * 4.2 + LoopNoise(t, 4.3, 0.7) * 1.3) * wander,
            DRoll: LoopNoise(t, 13.7, 3.2) * 2.2 * wander,
            Lid: blink ? BlinkLid(t) : 1,
            // At rest the video is almost still (centre stable to +-0.003): all the life goes
            // through gaze and blinks. Keep just enough to not freeze the picture.
            DriftX: floating ? LoopNoise(t, 7.9, 1.9) * 0.006 : 0,
            DriftY: floating ? LoopNoise(t, 5.3, 0.3) * 0.007 : 0,
            // Width is constant; only the height breathes very slightly.
            Breath: floating ? 1 + Math.Sin(t / 3.4 * Math.PI * 2) * 0.005 : 1);
    }

    /// <summary>
    /// A blink is a VERTICAL squash in screen space around the eye centre (bbox width preserved,
    /// height drops to ~0.35), not a shrink along the capsule's tilted axis.
    /// </summary>
    public static double BlinkScale(double lid) => 0.06 + 0.94 * Clamp(lid);
}
