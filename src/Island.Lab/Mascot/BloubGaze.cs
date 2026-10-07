// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Copyright (c) 2026 Jérémy Perret. Licensed under the MIT License (full text in BloubEngine.cs).
// Port of src/ui/gaze.ts (cursor-following rule) plus the pointer plumbing of BloubBot.vue.

using static Island.Lab.Mascot.BloubMath;

namespace Island.Lab.Mascot;

/// <summary>
/// Where the bot looks when it follows the cursor. Pure: the pointer enters already normalised.
/// </summary>
public static class BloubGaze
{
    /// <summary>
    /// Head angles, degrees. CHOSEN, not measured. Wide enough to stand out from the resting drift
    /// (+-7deg yaw, +-5.5 pitch), restrained enough that no eye goes behind the sphere's limb.
    /// </summary>
    public const double YawMax = 16;
    public const double PitchMax = 13;

    /// <summary>Gaze height with the cursor at the centre: slightly above the equator (attentive). ABSOLUTE value.</summary>
    public const double Pitch = 10;

    /// <summary>
    /// Direction the head settles to in the original's settings view (looking LEFT, towards the
    /// panel). Not a mirror of the image: the eyes really go round the sphere.
    /// </summary>
    public const double Turn = 26;

    /// <summary>Full turn travelled ON THE WAY (lands exactly, since -360deg == 0).</summary>
    public const double Spin = 360;

    /// <summary>Duration of the turn.</summary>
    public const double TurnTime = 1.1;

    /// <summary>
    /// Moods the bot goes through while following the cursor. All have ZERO roll (the selection
    /// criterion): yaw and pitch are neutralised by tracking (absolute), roll is not, and a mood
    /// change would make the eyes jump.
    /// </summary>
    public static readonly BloubExpressionId[] Humeurs =
    [
        BloubExpressionId.Surpris, BloubExpressionId.Heureux, BloubExpressionId.Hilare,
        BloubExpressionId.Excite, BloubExpressionId.Fier, BloubExpressionId.Blase,
    ];

    /// <summary>Duration of the "tour" arrival script.</summary>
    public const double TourTime = 1.5;

    /// <summary>
    /// "The tour": the ball seems to spin on itself. Mix stays ZERO; only Spin melts, which takes
    /// the eyes BEHIND the ball before bringing them back exactly where the expression puts them.
    /// Only meant for a circle body (on other shapes the eyes follow the profile and hop).
    /// Feed with <c>engine.SetLook(TourLook(t), now, 1/60.0)</c> every frame.
    /// </summary>
    public static Look TourLook(double t) => new(0, 0, 0, Spin * (1 - EaseInOutCubic(Clamp(t / TourTime))), 1);

    /// <summary>
    /// Gaze target. <paramref name="tour"/> drives everything: it raises the hold on the pose (Mix)
    /// and melts the travelled turn (Spin). At 0 the state's pose rules alone; at 1 the head looks
    /// at the cursor.
    /// </summary>
    /// <param name="nx">Horizontal pointer offset from the bot centre, -1..1 (right positive).</param>
    /// <param name="ny">Vertical offset, -1..1, screen direction (down positive).</param>
    /// <param name="tour">Arrival progress, 0..1.</param>
    /// <param name="pointer">false = no known pointer: the head stays turned, but drift comes back.</param>
    /// <param name="turn">Resting yaw offset. The original uses -<see cref="Turn"/> (look left at the panel); 0 = centred.</param>
    public static Look LookTarget(double nx, double ny, double tour, bool pointer, double turn = -Turn) => new(
        turn + nx * YawMax,
        // positive pitch = looking up, while screen y goes down
        Pitch - ny * PitchMax,
        tour,
        Spin * (1 - tour),
        pointer ? 0 : 1);
}

/// <summary>
/// Pointer-tracking driver, ported from BloubBot.vue's <c>aim()</c>/<c>release()</c>: call
/// <see cref="Update"/> once per frame (before <see cref="BloubEngine.Sample"/>) with the cursor
/// position relative to the mascot centre. Tracking only applies on resting-face states.
/// </summary>
public sealed class BloubCursorTracker
{
    bool _aiming;
    double _turnSince;

    /// <summary>
    /// Resting yaw when tracking. The web original uses -26 (the head looks towards a side panel);
    /// 0 keeps the head facing forward, which suits an island mascot.
    /// </summary>
    public double TurnYaw { get; set; } = 0;

    /// <summary>When false, tracking starts already settled instead of doing the 360-degree "tour" on arrival.</summary>
    public bool SpinOnArrival { get; set; } = false;

    /// <param name="engine">The engine to drive.</param>
    /// <param name="now">Engine clock, seconds.</param>
    /// <param name="cursorDelta">Cursor minus mascot centre (any unit), or null when there is no pointer.</param>
    /// <param name="halfRange">Distance (same unit) at which the gaze saturates (the original uses half the window size).</param>
    /// <param name="follow">false = release the gaze back to the state's pose.</param>
    public void Update(BloubEngine engine, double now, System.Numerics.Vector2? cursorDelta, System.Numerics.Vector2 halfRange, bool follow = true)
    {
        // Gaze is only driven on RESTING-FACE states; elsewhere the gaze pose IS the animation.
        if (!follow || !BloubStates.Get(engine.State).BaseFace)
        {
            Release(engine, now);
            return;
        }
        if (!_aiming) _turnSince = SpinOnArrival ? now : now - BloubGaze.TurnTime;
        double hx = Math.Max(1e-6, halfRange.X), hy = Math.Max(1e-6, halfRange.Y);
        double nx = cursorDelta is { } d ? Clamp(d.X / hx, -1, 1) : 0;
        double ny = cursorDelta is { } d2 ? Clamp(d2.Y / hy, -1, 1) : 0;
        engine.SetLook(BloubGaze.LookTarget(nx, ny, EaseOutQuint(Clamp((now - _turnSince) / BloubGaze.TurnTime)), cursorDelta is not null, TurnYaw), now);
        _aiming = true;
    }

    /// <summary>Releases the gaze (same duration as the way out: the head comes back smoothly).</summary>
    public void Release(BloubEngine engine, double now)
    {
        if (!_aiming) return;
        engine.SetLook(null, now, BloubGaze.TurnTime);
        _aiming = false;
    }
}
