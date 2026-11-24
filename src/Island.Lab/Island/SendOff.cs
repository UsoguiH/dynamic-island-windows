using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;
using Island.Lab.Render;

namespace Island.Lab.Island;

/// <summary>
/// A new agent is made of him (design/new-agent.html, the "Send off" state): Bloub takes the middle of the island
/// in one hop, shuts his eyes and draws out sideways until only a thread holds the two halves; it parts, and there
/// are two of him. His own face stays with the left one. The new one has no face until it wakes: its eyes open on
/// him, a light in the agent's colour comes on at its shoulder, it turns to you, hops because it cannot wait,
/// gathers itself and goes out through the top. He watches it go, blinks, and hops home.
/// While it plays it is drawn in place of the island's own Bloub (one face, never two): the hand-over hides in a
/// blink at both ends, as the film states do (see <see cref="OrbFilm"/>).
/// </summary>
public sealed class SendOff
{
    const float Grow = 1.5f, Sep = 1.15f;                    // how much he grows on the stage; how far from the middle each of the two ends up (in his radii)
    const float T0 = 0.38f, T1 = T0 + 0.14f, T2 = T1 + 0.4f, Born = T2 + 0.42f, Back = Born + 1.2f, Home = Back + 0.4f, End = Home + 0.32f;
    const float Go = 0.68f, Gone = 1.05f;                    // the new one, counted from when it is born: when it leaps, when it is out

    public bool Active { get; private set; }
    /// <summary>While it plays, the island's own Bloub is not drawn at all.</summary>
    public bool Covers => Active;
    /// <summary>The new one has left through the top.</summary>
    public bool Left => Active && _t > Born + Gone;

    float _t, _lid;
    uint _color;
    bool _kicked, _home, _ending, _kickDue, _homeDue;
    Vector2 _gaze;

    /// <summary>Starts it: <paramref name="color"/> is the new agent's; <paramref name="gaze"/> is where he was looking (-1..1), so his eyes carry over.</summary>
    public void Play(uint color, Vector2 gaze)
    {
        Active = true; _t = 0; _lid = 0; _color = color; _gaze = gaze;
        _kicked = _home = _ending = _kickDue = _homeDue = false;
    }

    public void Stop() => Active = false;

    /// <summary>True once, as the new one leaves the ground (the island feels it go).</summary>
    public bool TakeKick() { bool k = _kickDue; _kickDue = false; return k; }
    /// <summary>True once, as he lands back in his place (what comes next can begin).</summary>
    public bool TakeHome() { bool h = _homeDue; _homeDue = false; return h; }

    public void Step(float dt)
    {
        if (!Active) return;
        if (_ending) { _lid -= dt / 0.1f; if (_lid <= 0) { _lid = 0; Active = false; } return; }
        _lid = MathF.Min(1, _lid + dt / 0.12f);
        _t += dt;
        if (!_kicked && _t > Born + Go + (Gone - Go) * 0.7f) { _kicked = _kickDue = true; }
        if (!_home && _t > Home - 0.05f) { _home = _homeDue = true; }
        if (_t > End) _ending = true;
    }

    /// <summary>
    /// Draws it. <paramref name="home"/> and <paramref name="r"/>: where the island's Bloub sits and his radius;
    /// <paramref name="stage"/>: the middle of the island, where it happens; <paramref name="top"/>: the island's
    /// top edge (the new one goes out through it). All in island DIPs.
    /// </summary>
    public void Draw(Canvas c, Vector2 home, float r, Vector2 stage, float top, float opacity, ID2D1Geometry? island, Area islandBox, ulong islandKey)
    {
        if (!Active || r <= 0 || opacity <= 0.004f) return;
        float t = _t, lid = _lid; uint col = _color; var gaze = _gaze;
        c.PushLayer(Math.Clamp(opacity, 0, 1), island, islandBox, islandKey);
        ulong hash = new FrameHash(91).A(t).A(lid).P(home.X).P(home.Y).P(r).P(stage.X).P(stage.Y).P(top).V;
        c.Custom(islandBox.L, islandBox.T, islandBox.R - islandBox.L, islandBox.B - islandBox.T, hash,
            (ctx, brush) => Paint(ctx, c.Factory, brush, t, lid, home, r, stage, top, col, gaze));
        c.PopLayer();
    }

    // ---- the motion (the same numbers as the design page)

    static float Lerp(float a, float b, float t) => a + (b - a) * t;
    static float Seg(float t, float a, float b) => Math.Clamp((t - a) / (b - a), 0, 1);
    static float EaseOut(float t) => 1 - MathF.Pow(1 - t, 3);
    static float EaseIn(float t) => t * t * t;
    static float EaseIO(float t) => t < 0.5f ? 4 * t * t * t : 1 - MathF.Pow(-2 * t + 2, 3) / 2;
    static float EaseBack(float t) => 1 + 3.2f * MathF.Pow(t - 1, 3) + 2.2f * MathF.Pow(t - 1, 2);
    /// <summary>What is left of a knock: a swing that dies away.</summary>
    static float Ring(float t, float f, float d) => t <= 0 ? 0 : MathF.Exp(-d * t) * MathF.Sin(MathF.Tau * f * t);
    static float Pulse(float t, float a, float b, float c, float d) => t < a || t > d ? 0 : t < b ? (t - a) / (b - a) : t < c ? 1 : (d - t) / (d - c);

    static readonly Color4 Ink = new(0, 0, 0, 1), DotInk = Canvas.Rgba(0x07070A);

    static void Paint(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, float t, float lid, Vector2 home, float R, Vector2 stage, float top, uint color, Vector2 gaze)
    {
        float U = R * Grow, tx = stage.X - home.X, ty = stage.Y - home.Y, gx = gaze.X, gy = gaze.Y;
        float lidK = 0.06f + 0.94f * Math.Clamp(lid, 0, 1);   // (the hand-over to and from the island's Bloub happens while his eyes are shut)

        if (t < T1)
        {
            // he takes the middle of the island in one hop, growing as he comes; then he draws himself in, eyes shutting
            float p = EaseIO(Seg(t, 0.07f, T0 - 0.05f)), air = MathF.Sin(MathF.PI * p), an = EaseIO(Seg(t, T0, T1)), dir = tx < 0 ? -1 : 1;
            float s = 0.12f * MathF.Sin(MathF.PI * Seg(t, 0, 0.1f)) - 0.1f * air + 0.14f * Ring(t - (T0 - 0.05f), 3.6f, 9) + 0.11f * an;
            float k = Math.Clamp(Seg(t, 0, 0.1f) + Seg(t, T0 - 0.06f, T0), 0, 1);
            var at = home + new Vector2(tx * p, ty * p - 0.8f * U * air * (1 - 0.3f * p));
            float unit = R * Lerp(1, Grow, p);
            var m = Pose(at, unit, s, 0.2f * air * dir);
            using var body = Ball(fac, m, 0, 0, 1, 1);
            Lit(ctx, body, X(m, -0.30f, -0.42f), 1.65f * unit);
            Mask(ctx, body);
            Eyes(ctx, fac, brush, m, gx * 0.26f * (1 - k) + 0.16f * dir * air, Lerp(0.03f + gy * 0.18f, 0.03f, k), 0.296f, 0.252f, 0.565f * (1 - 0.82f * an) * lidK);
            ctx.PopLayer();
        }
        else if (t < Born + 0.05f)
        {
            // he draws out; a thread; it parts; and the new one wakes
            float q = Seg(t, T1, T2), tau = t - T2; bool parted = tau >= 0;
            float w = parted ? MathF.Exp(-7 * tau) * MathF.Cos(MathF.Tau * 3.2f * tau) : 0;
            float d = parted ? Sep + 0.1f * Ring(tau, 2.6f, 6.5f) : Sep * EaseIO(q), r = parted ? 1 : 1 - 0.12f * MathF.Sin(MathF.PI * q), v = Lerp(0.5f, 0.08f, EaseIn(Seg(q, 0.3f, 1)));
            // one long body is lit as one; parted, each is lit by itself
            float own = parted ? EaseOut(Seg(tau, 0, 0.16f)) : 0, L = Lerp(d * 0.749f + 1, 1, own), sq = 0.11f * (1 - EaseOut(Seg(q, 0, 0.3f)));
            var m = Pose(stage, U, sq, 0);
            float hO = parted ? Lerp(0.1f, 0.565f * (1 + 0.18f * MathF.Exp(-5 * tau)), EaseOut(Seg(tau, 0.03f, 0.13f))) : 0.1f;
            float hisX = -d + 0.26f * 0.8f * EaseIO(Seg(tau, 0.1f, 0.32f));   // his own face stays with the left half: eyes shut for the effort, then open, and on the new one
            if (!parted)
            {
                using var whole = Goo(fac, m, -d, r, d, r, v);
                Lit(ctx, whole, X(m, -0.30f * L, -0.42f * L), 1.65f * L * U);
                Mask(ctx, whole);
                Eyes(ctx, fac, brush, m, hisX, 0.03f, 0.296f, 0.252f, hO);
                ctx.PopLayer();
            }
            else
            {
                using var his = Ball(fac, m, -d, 0, 1 + 0.13f * w, 1 - 0.13f * w);
                using var its = Ball(fac, m, d, 0, 1 + 0.13f * w, 1 - 0.13f * w);
                Lit(ctx, his, X(m, -d * own - 0.30f * L, -0.42f * L), 1.65f * L * U);
                Lit(ctx, its, X(m, d * own - 0.30f * L, -0.42f * L), 1.65f * L * U);
                if (tau < 0.14f)
                {
                    // (what is left of the thread goes back into each)
                    float u = tau / 0.14f, x = Lerp(0.03f, 0.22f, EaseOut(u)), rr = 0.075f * (1 - u);
                    brush.Color = Canvas.Rgba(0xD9D9DE);
                    using var a = Ball(fac, m, -x, 0, rr, rr); ctx.FillGeometry(a, brush);
                    using var b = Ball(fac, m, x, 0, rr, rr); ctx.FillGeometry(b, brush);
                }
                Mask(ctx, his);
                Eyes(ctx, fac, brush, m, hisX, 0.03f, 0.296f, 0.252f, hO);
                ctx.PopLayer();
                if (tau > 0.14f)
                {
                    // the new one has no face until it wakes
                    Mask(ctx, its);
                    Eyes(ctx, fac, brush, m, d - 0.26f * 0.8f, 0.03f, 0.296f, 0.252f * EaseOut(Seg(tau, 0.14f, 0.2f)), MathF.Max(0.06f, 0.565f * EaseBack(Seg(tau, 0.2f, 0.36f))));
                    ctx.PopLayer();
                }
            }
        }
        else
        {
            // he watches it go, blinks, and comes home in one hop
            float sg = t - Born, back = EaseIO(Seg(t, Back, Home)), air = MathF.Sin(MathF.PI * back), up = EaseIO(Seg(sg, 0.68f, 0.98f)), k = Seg(t, Back + 0.1f, Home);
            float x0 = tx - Sep * U, dir = x0 > 0 ? -1 : 1;
            float s = -0.05f * MathF.Sin(MathF.PI * Seg(sg, 0.68f, 0.9f)) + 0.1f * MathF.Sin(MathF.PI * Seg(t, Back - 0.1f, Back)) - 0.08f * air + 0.13f * Ring(t - Home, 3.6f, 9);
            float shut = Pulse(sg, 1.04f, 1.08f, 1.12f, 1.18f);
            var at = home + new Vector2(x0 * (1 - back), ty * (1 - back) - 0.8f * U * air);
            float unit = R * Lerp(Grow, 1, back);
            var m = Pose(at, unit, s, 0.2f * air * dir);
            using var body = Ball(fac, m, 0, 0, 1, 1);
            Lit(ctx, body, X(m, -0.30f, -0.42f), 1.65f * unit);
            Mask(ctx, body);
            Eyes(ctx, fac, brush, m, Lerp(0.26f * Lerp(0.8f, 0.5f, up), gx * 0.26f, k), Lerp(0.03f - 0.3f * up, 0.03f + gy * 0.18f, k), 0.296f, 0.252f, 0.565f * (1 - 0.88f * shut) * lidK);
            ctx.PopLayer();
        }

        // the new one: its light comes on, it turns from its maker to you with a blink, hops, gathers itself, and goes out through the top
        float n = t - Born;
        if (n >= 0 && n <= Gone)
        {
            float turn = EaseIO(Seg(n, 0.12f, 0.3f)), hp = MathF.Sin(MathF.PI * Seg(n, 0.24f, 0.46f)), cr = EaseIO(Seg(n, 0.5f, 0.66f)), p = Seg(n, Go, Gone), st = EaseOut(Seg(p, 0, 0.22f));
            float tre = n < Go ? 0.012f * MathF.Sin(MathF.Tau * 24 * n) * Seg(n, 0.58f, 0.66f) : 0;
            float s = 0.06f * MathF.Sin(MathF.PI * Seg(n, 0.16f, 0.26f)) - 0.06f * hp + (n < Go ? 0.1f * Ring(n - 0.46f, 3.6f, 9) : 0) + 0.12f * cr * (1 - st) - 0.18f * st + tre;
            float shut = Pulse(n, 0.16f, 0.2f, 0.24f, 0.3f), look = EaseOut(Seg(n, 0.5f, Go)), lag = EaseOut(Seg(p, 0, 0.3f));
            float far = stage.Y - top + 2.3f * U;   // how far up is out
            var at = stage + new Vector2(Sep * U, -0.3f * U * hp - far * MathF.Pow(p, 2.2f));
            var m = Pose(at, U, s, 0);
            using var body = Ball(fac, m, 0, 0, 1, 1);
            Lit(ctx, body, X(m, -0.30f, -0.42f), 1.65f * U);
            Mask(ctx, body);
            Eyes(ctx, fac, brush, m, -0.26f * 0.8f * (1 - turn), 0.03f - 0.3f * look + 0.34f * lag, 0.296f, 0.252f, 0.565f * (1 + 0.1f * look) * (1 - 0.88f * shut));
            ctx.PopLayer();
            Light(ctx, fac, brush, m, -0.92f, -0.6f, 0.257f * EaseBack(Seg(n, 0.04f, 0.2f)), color, n);
        }
    }

    // ---- painting (the island Bloub's own look: a white ball lit from the upper left, eyes that show the island behind him)

    /// <summary>His own space (his radius is 1, his base is at y = 1) to island DIPs: squashed about his base, turned, sized and placed.</summary>
    static Matrix3x2 Pose(Vector2 centre, float unit, float squash, float turn) =>
        Matrix3x2.CreateTranslation(0, -1) * Matrix3x2.CreateScale(1 + squash, 1 - squash) * Matrix3x2.CreateRotation(turn) * Matrix3x2.CreateTranslation(0, 1)
        * Matrix3x2.CreateScale(unit) * Matrix3x2.CreateTranslation(centre);

    static Vector2 X(Matrix3x2 m, float x, float y) => Vector2.Transform(new Vector2(x, y), m);

    /// <summary>The white ball's paint (see BloubRenderer.InkBrush), with its light at <paramref name="light"/>.</summary>
    static void Lit(ID2D1DeviceContext ctx, ID2D1Geometry shape, Vector2 light, float radius)
    {
        using var stops = ctx.CreateGradientStopCollection([new GradientStop(0, new Color4(0.992f, 0.992f, 0.995f, 1)), new GradientStop(0.3f, Canvas.Rgba(0xF2F2F7)), new GradientStop(1, new Color4(0.55f, 0.55f, 0.562f, 1))]);
        using var g = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(light, Vector2.Zero, radius, radius), stops);
        ctx.FillGeometry(shape, g);
    }

    static void Mask(ID2D1DeviceContext ctx, ID2D1Geometry shape) => ctx.PushLayer(new LayerParameters1
    {
        ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f), GeometricMask = shape, MaskAntialiasMode = AntialiasMode.PerPrimitive,
        MaskTransform = Matrix3x2.Identity, Opacity = 1, LayerOptions = LayerOptions1.None,
    }, null!);

    /// <summary>Part of an ellipse, from angle a0 to a1 (either way round), as curves; the pen is already at its start.</summary>
    static void ArcTo(ID2D1GeometrySink s, Matrix3x2 m, float cx, float cy, float rx, float ry, float a0, float a1)
    {
        int n = Math.Max(1, (int)MathF.Ceiling(MathF.Abs(a1 - a0) / (MathF.PI / 4)));
        float da = (a1 - a0) / n, k = 4f / 3 * MathF.Tan(da / 4);
        for (int i = 0; i < n; i++)
        {
            float a = a0 + i * da, b = a + da, ca = MathF.Cos(a), sa = MathF.Sin(a), cb = MathF.Cos(b), sb = MathF.Sin(b);
            s.AddBezier(new BezierSegment
            {
                Point1 = X(m, cx + rx * (ca - k * sa), cy + ry * (sa + k * ca)),
                Point2 = X(m, cx + rx * (cb + k * sb), cy + ry * (sb - k * cb)),
                Point3 = X(m, cx + rx * cb, cy + ry * sb),
            });
        }
    }

    static ID2D1PathGeometry Ball(ID2D1Factory1 fac, Matrix3x2 m, float cx, float cy, float rx, float ry)
    {
        var geo = fac.CreatePathGeometry();
        using var s = geo.Open();
        s.BeginFigure(X(m, cx + rx, cy), FigureBegin.Filled);
        ArcTo(s, m, cx, cy, rx, ry, 0, MathF.Tau);
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return geo;
    }

    /// <summary>Two balls on one line and the bridge between them, as one shape (v: how thick the bridge is).</summary>
    static ID2D1PathGeometry Goo(ID2D1Factory1 fac, Matrix3x2 m, float x1, float r1, float x2, float r2, float v)
    {
        float d = x2 - x1;
        if (d < 0.02f) { float rr = MathF.Max(r1, r2); return Ball(fac, m, (x1 + x2) / 2, 0, rr, rr); }
        bool lap = d < r1 + r2;
        float u1 = lap ? MathF.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1)) : 0, u2 = lap ? MathF.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1)) : 0;
        float ms = MathF.Acos(Math.Clamp((r1 - r2) / d, -1, 1)), a1 = u1 + (ms - u1) * v, a3 = MathF.PI - u2 - (MathF.PI - u2 - ms) * v;
        Vector2 p1 = new(x1 + r1 * MathF.Cos(a1), r1 * MathF.Sin(a1)), p3 = new(x2 + r2 * MathF.Cos(a3), r2 * MathF.Sin(a3));
        float k = MathF.Min(v * 2.4f, Vector2.Distance(p1, p3) / (r1 + r2)) * MathF.Min(1, d * 2 / (r1 + r2)), h1 = r1 * k, h2 = r2 * k;
        Vector2 q1 = new(p1.X + h1 * MathF.Cos(a1 - MathF.PI / 2), p1.Y + h1 * MathF.Sin(a1 - MathF.PI / 2)), q3 = new(p3.X + h2 * MathF.Cos(a3 + MathF.PI / 2), p3.Y + h2 * MathF.Sin(a3 + MathF.PI / 2));
        var geo = fac.CreatePathGeometry();
        using var s = geo.Open();
        s.SetFillMode(FillMode.Winding);
        s.BeginFigure(X(m, p1.X, p1.Y), FigureBegin.Filled);
        s.AddBezier(new BezierSegment { Point1 = X(m, q1.X, q1.Y), Point2 = X(m, q3.X, q3.Y), Point3 = X(m, p3.X, p3.Y) });
        ArcTo(s, m, x2, 0, r2, r2, a3, -a3);
        s.AddBezier(new BezierSegment { Point1 = X(m, q3.X, -q3.Y), Point2 = X(m, q1.X, -q1.Y), Point3 = X(m, p1.X, -p1.Y) });
        ArcTo(s, m, x1, 0, r1, r1, -a1, a1 - MathF.Tau);
        s.EndFigure(FigureEnd.Closed);
        s.Close();
        return geo;
    }

    static void Eyes(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Matrix3x2 m, float cx, float cy, float gap, float w, float h)
    {
        brush.Color = Ink;
        float hh = MathF.Max(h, w * 0.16f);
        Eye(ctx, fac, brush, m, cx - gap, cy, w, hh);
        Eye(ctx, fac, brush, m, cx + gap, cy, w, hh);
    }

    /// <summary>One eye: a capsule; as it shuts it becomes a lower lid, flat on top and round underneath.</summary>
    static void Eye(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Matrix3x2 m, float x, float y, float w, float h)
    {
        if (w < 0.01f || h < 0.005f) return;
        float lid = Math.Clamp((0.75f - h / w) / 0.35f, 0, 1);
        float mn = MathF.Min(w, h) / 2, rb = mn * Lerp(0.62f, 1, Math.Clamp(MathF.Abs(MathF.Log(w / h)) / 0.45f, 0, 1));
        float rt = Lerp(rb, mn * 0.2f, lid), bx = Lerp(rb, w / 2, lid), by = Lerp(rb, h * 0.95f, lid);
        // (corners that do not fit are made smaller together)
        float f = MathF.Min(1, MathF.Min(w / (2 * MathF.Max(rt, 1e-4f)), MathF.Min(w / (2 * MathF.Max(bx, 1e-4f)), h / MathF.Max(rt + by, 1e-4f))));
        rt *= f; bx *= f; by *= f;
        float l = x - w / 2, r = x + w / 2, tp = y - h / 2, bt = y + h / 2, hp = MathF.PI / 2;
        using var geo = fac.CreatePathGeometry();
        using (var s = geo.Open())
        {
            s.BeginFigure(X(m, l + rt, tp), FigureBegin.Filled);
            s.AddLine(X(m, r - rt, tp));
            ArcTo(s, m, r - rt, tp + rt, rt, rt, -hp, 0);
            s.AddLine(X(m, r, bt - by));
            ArcTo(s, m, r - bx, bt - by, bx, by, 0, hp);
            s.AddLine(X(m, l + bx, bt));
            ArcTo(s, m, l + bx, bt - by, bx, by, hp, MathF.PI);
            s.AddLine(X(m, l, tp + rt));
            ArcTo(s, m, l + rt, tp + rt, rt, rt, MathF.PI, MathF.PI + hp);
            s.EndFigure(FigureEnd.Closed);
            s.Close();
        }
        ctx.FillGeometry(geo, brush);
    }

    /// <summary>The round light at its shoulder, with the gap cut round it (the island is black behind); its three dots run: it is working.</summary>
    static void Light(ID2D1DeviceContext ctx, ID2D1Factory1 fac, ID2D1SolidColorBrush brush, Matrix3x2 m, float x, float y, float r, uint color, float tt)
    {
        if (r < 0.004f) return;
        float gap = r + 0.065f * MathF.Min(1, r / 0.1f);
        brush.Color = Ink;
        using (var g = Ball(fac, m, x, y, gap, gap)) ctx.FillGeometry(g, brush);
        brush.Color = Canvas.Rgba(color);
        using (var g = Ball(fac, m, x, y, r, r)) ctx.FillGeometry(g, brush);
        brush.Color = DotInk;
        float k = r / 0.257f;
        for (int i = -1; i <= 1; i++)
        {
            float up = MathF.Max(0, MathF.Sin(tt * 7 - (i + 1) * 0.9f));
            using var dot = Ball(fac, m, x + i * 0.1f * k, y, 0.037f * k, 0.037f * k * (1 + 0.9f * up * up));
            ctx.FillGeometry(dot, brush);
        }
    }
}
