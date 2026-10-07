// Bloub mascot engine — C# port.
// Original TypeScript: Bloub by Jérémy Perret (jeremy-prt), https://github.com/jeremy-prt/bloub
// Port of src/bot/engine.ts.
//
// MIT License
//
// Copyright (c) 2026 Jérémy Perret
//
// Permission is hereby granted, free of charge, to any person obtaining a copy
// of this software and associated documentation files (the "Software"), to deal
// in the Software without restriction, including without limitation the rights
// to use, copy, modify, merge, publish, distribute, sublicense, and/or sell
// copies of the Software, and to permit persons to whom the Software is
// furnished to do so, subject to the following conditions:
//
// The above copyright notice and this permission notice shall be included in all
// copies or substantial portions of the Software.
//
// THE SOFTWARE IS PROVIDED "AS IS", WITHOUT WARRANTY OF ANY KIND, EXPRESS OR
// IMPLIED, INCLUDING BUT NOT LIMITED TO THE WARRANTIES OF MERCHANTABILITY,
// FITNESS FOR A PARTICULAR PURPOSE AND NONINFRINGEMENT. IN NO EVENT SHALL THE
// AUTHORS OR COPYRIGHT HOLDERS BE LIABLE FOR ANY CLAIM, DAMAGES OR OTHER
// LIABILITY, WHETHER IN AN ACTION OF CONTRACT, TORT OR OTHERWISE, ARISING FROM,
// OUT OF OR IN CONNECTION WITH THE SOFTWARE OR THE USE OR OTHER DEALINGS IN THE
// SOFTWARE.

using System.Numerics;
using static Island.Lab.Mascot.BloubMath;

namespace Island.Lab.Mascot;

/// <summary>
/// A capsule (stadium) eye, centred on the origin with size <see cref="W"/> x <see cref="H"/>
/// (viewBox units, corner radius min(W, H) / 2), then placed by <see cref="Matrix"/>.
/// The eye is a HOLE in the body. Matrix uses System.Numerics row-vector convention, i.e. the SVG
/// <c>matrix(a,b,c,d,e,f)</c> maps to M11=a, M12=b, M21=c, M22=d, M31=e, M32=f.
/// </summary>
public readonly record struct BloubEye(float W, float H, Matrix3x2 Matrix, float Alpha);

/// <summary>A rendered decor dot, viewBox units.</summary>
/// <param name="Depth">Depth haze (0 = background colour, 1 = body colour); NaN = none.</param>
/// <param name="Shape">Optional closed polygon in BALL-RADIUS units centred on the origin; drawn translated to (X, Y), rotated by <paramref name="RotDeg"/>, scaled by the engine scale.</param>
public readonly record struct BloubDot(float X, float Y, float R, float Opacity, float Depth, Vector2[]? Shape, float RotDeg);

/// <summary>A circle in viewBox units.</summary>
public readonly record struct BloubCircle(float X, float Y, float R);

/// <summary>
/// One frame, as structured geometry in viewBox units (origin = ball centre, resting ball radius =
/// engine scale, 100 by default; the visible viewBox is +-158).
/// </summary>
public sealed class BloubFrame
{
    /// <summary>The 64 outline points; the outline is the closed Catmull-Rom spline through them (tension 1/6, see <see cref="BloubShape.ClosedPathControls"/>).</summary>
    public required Vector2[] Body { get; init; }
    public float BodyAlpha { get; init; }
    /// <summary>Eyes, as holes in the body (0, 1 or 2).</summary>
    public required BloubEye[] Eyes { get; init; }
    public required BloubDot[] Dots { get; init; }
    /// <summary>true = dots pass BEHIND the body (burst particles), drawn before it.</summary>
    public bool DotsBehind { get; init; }
    public required ArcRender[] Arcs { get; init; }
    /// <summary>Notification pastille (blue disc), drawn in front of the body.</summary>
    public BloubCircle? Notif { get; init; }
    /// <summary>Notch: a concentric disc subtracted from the body around the pastille.</summary>
    public BloubCircle? Notch { get; init; }
}

/// <summary>
/// Where the bot looks when something external drives it (the mouse pointer).
///
/// <see cref="Yaw"/> and <see cref="Pitch"/> are ABSOLUTE directions (degrees) that replace the
/// pose's own as <see cref="Mix"/> rises. The ENGINE does the mixing because only it knows the
/// pose at this instant (a caller compensating for the expression would read its arrival value
/// while the morph runs, and the eyes would jump on every mood change). Absolute on both axes:
/// what characterises a mood during tracking is the SHAPE of its eyes, not where it looks.
///
/// <see cref="Mix"/>: how much the outside world commands the direction (0 = not at all).
/// <see cref="Wander"/>: what remains of the automatic drift — separate from Mix: when the pointer
/// moves, drift must die out (added together the bot would look like it hunts the cursor), but
/// without a pointer the head must stay turned AND keep living.
/// <see cref="Spin"/>: a turn taken ON THE WAY, in degrees, melted to 0 on arrival. The eyes live
/// on a sphere, so a turn takes them behind the ball and back; -360 is the same angle as 0.
/// </summary>
public readonly record struct Look(double Yaw, double Pitch, double Mix, double Spin, double Wander)
{
    public static readonly Look None = new(0, 0, 0, 0, 1);

    public static Look Lerp(Look a, Look b, double t) => new(
        BloubMath.Lerp(a.Yaw, b.Yaw, t), BloubMath.Lerp(a.Pitch, b.Pitch, t), BloubMath.Lerp(a.Mix, b.Mix, t),
        BloubMath.Lerp(a.Spin, b.Spin, t), BloubMath.Lerp(a.Wander, b.Wander, t));
}

/// <summary>
/// Clockless engine: <see cref="Sample"/> is a pure function of time.
///
/// Practical consequence: pause, resume, slow motion and jumping to an arbitrary date give exactly
/// the same image. External state only comes in through TIME-STAMPED setters, never through a
/// variable read during Sample. Not thread-safe: drive it from the render thread.
/// </summary>
public sealed class BloubEngine
{
    /// <summary>Resting ball radius, in viewBox units.</summary>
    public readonly double Scale;

    BloubState _cur;
    BloubState? _prev;
    /// <summary>FROZEN start pose, set only when a state change lands while a fade is in progress. See <see cref="SetState"/>.</summary>
    Pose? _departFige;
    double _tCur;
    double _tPrev;
    double _blinkAt = -10;
    double[]? _shape;
    double[]? _shapePrev;
    double _shapeAt = -10;
    BloubExpression? _expr;
    BloubExpression? _exprPrev;
    double _exprAt = -10;
    Look _look = Look.None;
    Look _lookPrev = Look.None;
    double _lookAt = -10;
    /// <summary>Current catch-up duration; see <see cref="LookMorph"/>, its default.</summary>
    double _lookMorph = 0.24;

    /// <summary>Duration of the morph when the body shape changes.</summary>
    public const double ShapeMorph = 0.45;

    /// <summary>
    /// Gaze catch-up duration towards its target. Shorter than ShapeMorph: tracking must look
    /// attentive, not viscous. Since the target is re-set on every mouse move, this duration gives
    /// tracking its inertia — the gaze never quite reaches a moving cursor.
    /// </summary>
    public const double LookMorph = 0.24;

    public BloubEngine(double scale = Rayon, BloubState initial = BloubState.Idle, double[]? shape = null, BloubExpression? expression = null)
    {
        Scale = scale;
        _cur = initial;
        _shape = shape;
        _expr = expression;
    }

    /// <summary>Convenience constructor with catalogue ids.</summary>
    public BloubEngine(double scale, BloubState initial, BloubShapeId? shape, BloubExpressionId? expression)
        : this(scale, initial, shape is { } s ? BloubSkins.Get(s) : null, expression is { } e ? BloubExpressions.Get(e) : null) { }

    public BloubState State => _cur;

    /// <summary>Resting expression (customiser). Like the shape, it glides to the new value.</summary>
    public void SetExpression(BloubExpression? expression, double now = 0)
    {
        if (ReferenceEquals(expression, _expr)) return;
        _exprPrev = _expr;
        _expr = expression;
        _exprAt = now;
    }

    public void SetExpression(BloubExpressionId? id, double now = 0) =>
        SetExpression(id is { } e ? BloubExpressions.Get(e) : null, now);

    /// <summary>Effective expression at <paramref name="now"/>, morph in progress included.</summary>
    BloubExpression? ExprAtTime(double now)
    {
        var to = _expr;
        var from = _exprPrev;
        if (to is null || from is null) return to;
        double k = (now - _exprAt) / ShapeMorph;
        if (k >= 1) return to;
        return BloubExpressions.Blend(from, to, EaseOutQuint(Clamp(k)));
    }

    /// <summary>
    /// Shape chosen in the customiser (null = each state's own body). It only replaces the body on
    /// resting states (BaseBody); elsewhere the silhouette IS the animation. The change morphs: all
    /// shapes share the same angles, so radii are interpolated. Arrays are compared by reference
    /// (and the eye-fit table only knows the catalogue arrays): don't mutate them.
    /// </summary>
    public void SetShape(double[]? radii, double now = 0)
    {
        if (ReferenceEquals(radii, _shape)) return;
        _shapePrev = _shape;
        _shape = radii;
        _shapeAt = now;
    }

    public void SetShape(BloubShapeId? id, double now = 0) => SetShape(id is { } s ? BloubSkins.Get(s) : null, now);

    /// <summary>
    /// Effective shape at <paramref name="now"/>, morph included. Does NOT reset shapePrev at the
    /// end of the morph: Sample must stay a pure function of time, so re-reading a past date must
    /// give the intermediate image again.
    /// </summary>
    double[]? ShapeAtTime(double now)
    {
        var to = _shape;
        var from = _shapePrev;
        if (to is null || from is null) return to;
        double k = (now - _shapeAt) / ShapeMorph;
        if (k >= 1) return to;
        double t = EaseOutQuint(Clamp(k));
        // allocates only during the morph; outside it the array is returned as is
        var o = new double[to.Length];
        for (int i = 0; i < to.Length; i++) o[i] = Lerp(i < from.Length ? from[i] : to[i], to[i], t);
        return o;
    }

    /// <summary>
    /// New gaze target, null to return to the state's own. It starts from the CURRENT value, not
    /// the previous target: it is called on every pointer move, and restarting from the old target
    /// would make the gaze step back before each catch-up (tracking would tremble instead of glide).
    /// A non-finite target is refused and the last one kept: a single NaN would otherwise propagate
    /// to every frame and the bot would never rest again.
    /// </summary>
    public void SetLook(Look? look, double now, double morph = LookMorph)
    {
        if (look is { } l && !double.IsFinite(l.Yaw + l.Pitch + l.Mix + l.Spin + l.Wander)) return;
        _lookPrev = LookAtTime(now);
        _look = look ?? Look.None;
        _lookAt = now;
        _lookMorph = morph;
    }

    /// <summary>Effective gaze at <paramref name="now"/>, catch-up included.</summary>
    Look LookAtTime(double now)
    {
        double k = (now - _lookAt) / _lookMorph;
        if (k >= 1) return _look;
        return Look.Lerp(_lookPrev, _look, EaseOutQuint(Clamp(k)));
    }

    static Pose Posed(StateDef def, double t, double[]? shape, BloubExpression? expr)
    {
        var pose = def.Pose(t);
        // keep the pose (rotation, offset, squash) and only swap the profile
        if (def.BaseBody && shape is not null) pose = pose with { Sil = pose.Sil with { Radii = shape } };
        if (def.BaseFace && expr is not null) pose = pose with { Gaze = expr.Gaze, Split = expr.Split, Eyes = expr.Eyes };
        return pose;
    }

    /// <summary>
    /// Eye offset at <paramref name="now"/> for a given state, ball-radius units. READ from the
    /// table and interpolated, never recomputed: the table is queried on the BOUNDARIES of each
    /// morph (shapePrev/shape, exprPrev/expr) and interpolated with that morph's curve.
    /// </summary>
    (double x, double y) DecalageAtTime(double now, BloubState state)
    {
        (double x, double y) SurAxe(double debut, double duree, (double x, double y) a, (double x, double y) b)
        {
            if (a == b) return b;
            double k = (now - debut) / duree;
            if (k >= 1) return b;
            double t = EaseOutQuint(Clamp(k));
            return (Lerp(a.x, b.x, t), Lerp(a.y, b.y, t));
        }

        // expression axis, for each of the two shapes involved
        (double x, double y) ParForme(double[]? radii) => SurAxe(_exprAt, ShapeMorph,
            BloubEyeFit.DecalageDesYeux(radii, state, _exprPrev?.Id),
            BloubEyeFit.DecalageDesYeux(radii, state, _expr?.Id));

        // then the shape axis
        return SurAxe(_shapeAt, ShapeMorph, ParForme(_shapePrev), ParForme(_shape));
    }

    /// <summary>
    /// Restarts on <paramref name="id"/> WITHOUT a previous state, like a fresh engine (what
    /// "rewind" means). SetState alone keeps the state being left in order to fade it.
    /// </summary>
    public void Reset(BloubState id, double now)
    {
        _cur = id;
        _prev = null;
        _departFige = null;
        _tCur = now;
        _tPrev = now;
        _blinkAt = -10;
    }

    /// <summary>Origin of the current fade: the frozen pose if any, else the state being left at its own elapsed time (still animating, on purpose).</summary>
    Pose? Origine(double now, double[]? shape, BloubExpression? expr)
    {
        if (_departFige is not null) return _departFige;
        if (_prev is not { } prev) return null;
        return Posed(BloubStates.Get(prev), Math.Max(0, now - _tPrev), shape, expr);
    }

    /// <summary>Composite pose at <paramref name="now"/>, fade included: exactly what Sample blends, before resting life and gaze.</summary>
    Pose PoseComposee(double now)
    {
        var def = BloubStates.Get(_cur);
        var shape = ShapeAtTime(now);
        var expr = ExprAtTime(now);
        var pose = Posed(def, Math.Max(0, now - _tCur), shape, expr);
        double since = now - _tCur;
        if (since >= def.Morph) return pose;
        var origine = Origine(now, shape, expr);
        if (origine is null) return pose;
        return BlendPose(origine, pose, EaseOutQuint(Clamp(since / def.Morph)));
    }

    /// <summary>
    /// Time-stamped state change. The engine keeps only ONE slot of history, so a change landing
    /// during a fade used to blend from the FULL pose of the state being left instead of the
    /// partly blended frame on screen. So the current composite pose is frozen and blended from —
    /// continuous by construction. And ONLY in that case: freezing on every change would stop the
    /// outgoing state's own animation dead for the whole fade.
    /// </summary>
    public void SetState(BloubState id, double now)
    {
        if (id == _cur) return;
        double morph = BloubStates.Get(_cur).Morph;
        bool enPleinFondu = _prev is not null && now - _tCur < morph;
        _departFige = enPleinFondu ? PoseComposee(now) : null;
        _prev = _cur;
        _tPrev = _tCur;
        _cur = id;
        _tCur = now;
        // In the video, every shape change is masked by a blink.
        if (BloubStates.Get(id).BlinkIn) _blinkAt = now;
    }

    static EyeCfg LerpEye(EyeCfg a, EyeCfg b, double t) =>
        new(Lerp(a.W, b.W, t), Lerp(a.H, b.H, t), Lerp(a.Open, b.Open, t), Lerp(a.Tilt, b.Tilt, t));

    /// <summary>Blends two poses. Decor cross-fades in opacity, not geometry.</summary>
    static Pose BlendPose(Pose a, Pose b, double t)
    {
        double outT = 1 - t;
        var dots = new List<DotSpec>(a.Dots.Count + b.Dots.Count);
        foreach (var d in a.Dots) dots.Add(d with { Opacity = d.Opacity * outT });
        foreach (var d in b.Dots) dots.Add(d with { Opacity = d.Opacity * t });
        var arcs = new List<ArcSpec>(a.Arcs.Count + b.Arcs.Count);
        foreach (var r in a.Arcs) arcs.Add(r with { Id = "a" + r.Id, Opacity = r.Opacity * outT });
        foreach (var r in b.Arcs) arcs.Add(r with { Id = "b" + r.Id, Opacity = r.Opacity * t });
        return new Pose
        {
            Sil = BloubShape.Blend(a.Sil, b.Sil, t),
            OffX = Lerp(a.OffX, b.OffX, t),
            OffY = Lerp(a.OffY, b.OffY, t),
            Gaze = new HeadGaze(Lerp(a.Gaze.Yaw, b.Gaze.Yaw, t), Lerp(a.Gaze.Pitch, b.Gaze.Pitch, t), Lerp(a.Gaze.Roll, b.Gaze.Roll, t)),
            Split = Lerp(a.Split, b.Split, t),
            Eyes = [LerpEye(a.Eyes[0], b.Eyes[0], t), LerpEye(a.Eyes[1], b.Eyes[1], t)],
            EyeAlpha = Lerp(a.EyeAlpha, b.EyeAlpha, t),
            BodyAlpha = Lerp(a.BodyAlpha, b.BodyAlpha, t),
            Dots = dots,
            Arcs = arcs,
            // the pastille belongs to only one of the two states, it does not blend
            Notif = t < 0.5 ? a.Notif : b.Notif,
            DotsBehind = t < 0.5 ? a.DotsBehind : b.DotsBehind,
        };
    }

    /// <summary>Float copies of the (static) dot polygons, cached by reference so the renderer can cache their geometry.</summary>
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PointD[], Vector2[]> FloatShapes = new();

    static Vector2[] FloatShape(PointD[] poly) => FloatShapes.GetValue(poly, p =>
    {
        var o = new Vector2[p.Length];
        for (int i = 0; i < p.Length; i++) o[i] = new Vector2((float)p[i].X, (float)p[i].Y);
        return o;
    });

    /// <summary>Samples the frame at absolute time <paramref name="now"/> (seconds, same clock as the setters).</summary>
    public BloubFrame Sample(double now)
    {
        double R = Scale;
        var def = BloubStates.Get(_cur);
        var shape = ShapeAtTime(now);
        var expr = ExprAtTime(now);
        var pose = Posed(def, Math.Max(0, now - _tCur), shape, expr);
        var decalage = DecalageAtTime(now, _cur);

        // --- transition -------------------------------------------------------
        double since = now - _tCur;
        // The previous state is never purged: `since < morph` is enough to ignore it once the fade
        // is over, and forgetting it would make the engine non-replayable.
        var origine = since < def.Morph ? Origine(now, shape, expr) : null;
        if (origine is not null)
        {
            // Exponential ease-out: the curve measured on the video. The ratio is clamped: re-reading
            // a date BEFORE the state change would give a negative ratio that the ease extrapolates.
            double ratio = EaseOutQuint(Clamp(since / def.Morph));
            pose = BlendPose(origine, pose, ratio);
            // The eye offset follows the SAME curve as the silhouette that motivates it.
            if (_prev is { } quitte)
            {
                var avant = DecalageAtTime(now, quitte);
                decalage = (Lerp(avant.x, decalage.x, ratio), Lerp(avant.y, decalage.y, ratio));
            }
        }

        // --- resting life -----------------------------------------------------
        bool alive = pose.EyeAlpha > 0.01;
        var look = LookAtTime(now);
        var life = BloubFace.Life(now, wander: alive ? look.Wander : 0, blink: alive);

        // Both aims REPLACE the pose's instead of adding to it, and the spin is subtracted on the
        // way. Drift is added AFTER the mix, otherwise the target would cancel it along with the pose.
        var gaze = new HeadGaze(
            Lerp(pose.Gaze.Yaw, look.Yaw, look.Mix) + life.DYaw - look.Spin,
            Lerp(pose.Gaze.Pitch, look.Pitch, look.Mix) + life.DPitch,
            // roll follows nothing: the bot's head is tilted -13deg in the video, and rolling it
            // with the cursor breaks that signature
            pose.Gaze.Roll + life.DRoll);

        // blink triggered by the state change, on top of the schedule
        double forced = Clamp((now - _blinkAt) / 0.2);
        double forcedLid = forced < 1 ? Math.Abs(forced * 2 - 1) : 1;
        double lid = Math.Min(life.Lid, forcedLid);

        double offX = pose.OffX + life.DriftX;
        double offY = pose.OffY + life.DriftY;

        // --- body -------------------------------------------------------------
        var sil = pose.Sil with { Cx = pose.Sil.Cx + offX, Cy = pose.Sil.Cy + offY, Sy = pose.Sil.Sy * life.Breath };
        var pts = BloubShape.ToPoints(sil, R);
        var body = new Vector2[pts.Length];
        for (int i = 0; i < pts.Length; i++) body[i] = new Vector2((float)pts[i].X, (float)pts[i].Y);

        // --- eyes -------------------------------------------------------------
        // The eyes live on a sphere of radius 1; as soon as the silhouette is not a circle they are
        // brought back pro rata of the real radius in their direction, or they overflow.
        double BodyRadius(double x, double y) => BloubShape.RadiusAtAngle(pose.Sil.Radii, Math.Atan2(y, x) - pose.Sil.Rot);

        var eyes = new List<BloubEye>(2);
        if (pose.EyeAlpha > 0.01)
        {
            var (p0, p1) = BloubFace.EyePoses(gaze, R, pose.Split);
            for (int i = 0; i < 2; i++)
            {
                var e = i == 0 ? p0 : p1;
                if (e.Depth <= 0.02) continue;
                var cfg = pose.Eyes[i];
                double fit = BodyRadius(e.X, e.Y);
                // Eye's own tilt: compose the tangent frame with a rotation in the eye's plane
                // (Basis x Rot). That is what allows mirrored tilts between the two eyes.
                double phi = cfg.Tilt * Math.PI / 180;
                double cp = Math.Cos(phi), sp = Math.Sin(phi);
                double ax = e.A * cp + e.C * sp;
                double ay = e.B * cp + e.D * sp;
                double cx2 = -e.A * sp + e.C * cp;
                double cy2 = -e.B * sp + e.D * cp;
                // The blink applies AFTER all that: a vertical squash on screen, not along the capsule axis.
                double k = BloubFace.BlinkScale(Math.Min(lid, cfg.Open));
                eyes.Add(new BloubEye(
                    (float)Math.Max(cfg.W * R, 0.01),
                    (float)Math.Max(cfg.H * R, 0.01),
                    new Matrix3x2(
                        (float)ax, (float)(ay * k),
                        (float)cx2, (float)(cy2 * k),
                        (float)(e.X * fit + (offX + decalage.x) * R),
                        (float)(e.Y * fit + (offY + decalage.y) * R)),
                    (float)(pose.EyeAlpha * Clamp(e.Depth / 0.12))));
            }
        }

        // --- decor ------------------------------------------------------------
        var dots = new List<BloubDot>(pose.Dots.Count);
        foreach (var p in pose.Dots)
        {
            if (!(p.Opacity > 0.01 && p.R > 0.0005)) continue;
            Vector2[]? shapePts = p.Shape is { } poly ? FloatShape(poly) : null;
            dots.Add(new BloubDot((float)((p.X + offX) * R), (float)((p.Y + offY) * R), (float)(p.R * R), (float)p.Opacity,
                p.Depth is { } d ? (float)d : float.NaN, shapePts, (float)p.Rot));
        }

        // the pastille sits on the outline, so it follows the shape too
        BloubCircle? notif = null, notch = null;
        if (pose.Notif is { } n)
        {
            double nFit = BodyRadius(n.X, n.Y);
            float nx = (float)((n.X * nFit + offX) * R);
            float ny = (float)((n.Y * nFit + offY) * R);
            notif = new BloubCircle(nx, ny, (float)(n.R * R));
            notch = new BloubCircle(nx, ny, (float)(n.Notch * R));
        }

        // States declare arcs in ball-radius units; only the engine knows the viewBox scale.
        var arcs = new List<ArcRender>(pose.Arcs.Count);
        foreach (var a in pose.Arcs)
            if (a.Opacity > 0.01) arcs.Add(BloubDecor.ArcRender(a.Seed, a.T, R, a.Id, a.Opacity));

        return new BloubFrame
        {
            Body = body,
            BodyAlpha = (float)pose.BodyAlpha,
            Eyes = eyes.ToArray(),
            Dots = dots.ToArray(),
            DotsBehind = pose.DotsBehind,
            Arcs = arcs.ToArray(),
            Notif = notif,
            Notch = notch,
        };
    }
}
