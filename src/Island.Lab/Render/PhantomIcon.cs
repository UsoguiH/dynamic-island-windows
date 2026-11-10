using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Island.Lab.Render;

/// <summary>
/// The "checking… → done" icon of a toast: the Phantom beat, ported number for number from
/// <c>src/components/PhantomIcon.tsx</c> of the "new app" project (itself measured frame by frame from a
/// recording of Phantom). Drawn on a 30-unit box around its centre.
/// <list type="bullet">
/// <item>waiting: three round lilac dots in fixed columns float up and down in a soft, slow wave;</item>
/// <item>the hand-over (always the same length): the dots cut out, quick whiter dots spread and meet in a
///   streak, the green coin cuts in squashed, its dark ring turns edge-on and unrolls into the tick, a white
///   shine runs round the rim, and the sparkle grows on the top right. The tick has landed
///   <see cref="CheckSeconds"/> after the hand-over starts.</item>
/// </list>
/// Times in the tables are the recording's milliseconds; the hand-over starts at <see cref="RecD"/>.
/// </summary>
public static class PhantomIcon
{
    /// <summary>How long after the hand-over the tick has landed.</summary>
    public const float CheckSeconds = 0.617f;
    /// <summary>How long the hand-over runs in all (the sparkle is the last thing to settle).</summary>
    public const float AfterSeconds = 1.0f;

    const float RecD = 383, Coin = 633;
    const float Half = 650, Lift = 3.4f, Lag = 210;          // the wait: a soft sine wave
    static readonly float[] DotX = [-6.1f, 0, 6.1f];
    const float TickW = 2.15f;

    static readonly Color4 Lilac = Canvas.Rgba(0xAEA9E0), QuickC = Canvas.Rgba(0xD9D7F2), White = Canvas.Rgba(0xFFFFFF),
                           Green = Canvas.Rgba(0x29B881), Ink = Canvas.Rgba(0x04210F);

    // the quick dots: [ms, x, y, r, stretch]
    static readonly (float From, float[][] Pts)[] Quick =
    [
        (500, [[500, -5.4f, -4.3f, 1.5f, 1], [533, -5.4f, -4.3f, 1.5f, 1], [550, -7.0f, -6.1f, 2.1f, 1], [583, -7.0f, -4.5f, 2.1f, 1], [600, -7.0f, -4.3f, 2.1f, 1], [617, -7.0f, -4.3f, 2.1f, 1]]),
        (500, [[500, -0.8f, -1.6f, 1.5f, 1], [533, -0.8f, -2.3f, 1.6f, 1], [550, -0.8f, -2.8f, 2.1f, 1], [583, -0.8f, -3.1f, 2.1f, 1], [600, -0.7f, -3.3f, 2.2f, 1], [617, -0.7f, -3.3f, 2.2f, 1]]),
        (500, [[500, 4.2f, 3.9f, 1.5f, 1], [533, 4.2f, 4.3f, 1.6f, 1], [550, 6.1f, 6.0f, 2.1f, 1], [583, 6.2f, 2.6f, 2.2f, 1], [600, 7.6f, -1.4f, 1.8f, 1.6f], [617, 7.6f, -1.4f, 1.8f, 1.6f]]),
        (533, [[533, -1.5f, -7.6f, 1.5f, 1], [550, -1.8f, -10.2f, 2.1f, 1], [583, 3.9f, -6.8f, 2.3f, 1], [600, 7.6f, -1.4f, 1.8f, 1.6f], [617, 7.6f, -1.4f, 1.8f, 1.6f]]),
    ];
    // the tick's five points, frame by frame: [ms, a.x, a.y, v.x, v.y, c1.x, c1.y, c2.x, c2.y, e.x, e.y]
    static readonly float[][] Tick =
    [
        [700, -6.2f, 5.0f, -4.2f, 6.6f, -3.5f, 6.3f, -2.8f, 6.0f, -2.2f, 5.6f],
        [733, -6.0f, 4.6f, -3.8f, 6.4f, -3.2f, 6.1f, -2.6f, 5.8f, -2.0f, 5.4f],
        [750, -6.6f, -7.0f, -5.0f, 4.8f, -4.5f, 4.3f, -4.1f, 3.8f, -3.6f, 3.4f],
        [767, -5.6f, -1.8f, -3.2f, 4.6f, -2.0f, 0.2f, -0.6f, -4.2f, 0.4f, -8.6f],
        [783, -6.0f, -1.0f, -3.5f, 4.5f, -2.2f, 0.6f, -0.8f, -3.6f, 0.4f, -7.6f],
        [800, -6.3f, 1.0f, -4.4f, 4.2f, -1.2f, 1.1f, 1.9f, -2.9f, 5.1f, -6],
        [817, -6.3f, 0.4f, -4.4f, 4.2f, -1.5f, 0.8f, 1.5f, -2.6f, 4.4f, -6],
        [833, -6.3f, 0.4f, -2.8f, 2.2f, 0.3f, -0.6f, 3.5f, -2.5f, 6.4f, -2.0f],
        [867, -6.8f, 0.7f, -3.4f, 2.4f, 0.3f, -1.0f, 3.6f, -2.8f, 7.0f, -0.8f],
        [900, -6.8f, 0.4f, -3.8f, 2.6f, 0.2f, -0.9f, 3.6f, -2.8f, 6.6f, -1.6f],
        [933, -6.75f, 0.2f, -3.0f, 3.6f, 0.5f, 0.2f, 3.7f, -2.4f, 6.8f, -3.5f],
        [967, -6.69f, 0, -2.21f, 4.5f, 0.84f, 1.22f, 3.89f, -2.07f, 6.94f, -5.35f],
        [1000, -6.69f, 0, -2.21f, 4.5f, 0.84f, 1.22f, 3.89f, -2.07f, 6.94f, -5.35f],
    ];
    static readonly float[][] Shine = [[633, 38], [667, 72], [700, 128], [733, 135], [750, 176], [767, 218], [783, 230], [800, 276], [817, 300], [833, 312]];
    static readonly float[][] ShineA = [[633, 1], [810, 1], [833, 0]];
    static readonly float[][] Run = [[633, 12, 51], [667, 13.5f, 51], [700, 13.5f, 40.5f], [733, 13.5f, 40.5f]];
    static readonly float[][] Ring = [[633, 0, 1], [667, 0, 1], [683, 0.25f, 0.66f], [700, 0.4f, 0.3f], [733, 0.5f, 0.24f]];
    static readonly float[][] Head = [[633, -170], [667, -155], [700, -140]];
    static readonly float[][] HeadA = [[633, 1], [667, 1], [690, 0]];
    static readonly float[][] Spark = [[833, 0.2f], [867, 0.35f], [900, 0.55f], [933, 0.85f], [967, 1.08f], [983, 1]];
    static readonly float[][] SparkA = [[833, 0.7f], [900, 1]];

    /// <summary>Row values at <paramref name="ms"/>: straight lines between the rows, held before the first and after the last.</summary>
    static void At(float[][] rows, float ms, Span<float> into)
    {
        int n = rows.Length;
        if (ms <= rows[0][0]) { rows[0].AsSpan(1).CopyTo(into); return; }
        if (ms >= rows[n - 1][0]) { rows[n - 1].AsSpan(1).CopyTo(into); return; }
        for (int i = 1; i < n; i++)
        {
            if (ms > rows[i][0]) continue;
            float[] a = rows[i - 1], b = rows[i];
            float k = b[0] > a[0] ? (ms - a[0]) / (b[0] - a[0]) : 1;
            for (int j = 1; j < a.Length; j++) into[j - 1] = a[j] + (b[j] - a[j]) * k;
            return;
        }
    }
    static float At(float[][] rows, float ms) { Span<float> v = stackalloc float[1]; At(rows, ms, v); return v[0]; }

    /// <summary>
    /// Draws the icon centred on <paramref name="center"/>, <paramref name="size"/> across.
    /// <paramref name="t"/>: seconds since the toast appeared; <paramref name="go"/>: seconds since the
    /// hand-over (negative while still waiting).
    /// </summary>
    public static void Draw(ID2D1DeviceContext ctx, ID2D1Factory1 factory, ID2D1SolidColorBrush brush, Vector2 center, float size, float t, float go, float opacity)
    {
        if (opacity <= 0.003f) return;
        var was = ctx.Transform;
        var world = Matrix3x2.CreateScale(size / 30f) * Matrix3x2.CreateTranslation(center) * was;
        ctx.Transform = world;
        try
        {
            if (go < 0) { Waiting(ctx, brush, t, opacity); return; }
            HandOver(ctx, factory, brush, world, RecD + go * 1000, opacity);
        }
        finally { ctx.Transform = was; }
    }

    static void Waiting(ID2D1DeviceContext ctx, ID2D1SolidColorBrush brush, float t, float opacity)
    {
        for (int i = 0; i < 3; i++)
        {
            // each dot floats between its top and its bottom on a sine, a little behind the one on its left
            float p = (t * 1000 + 2 * Half - i * Lag) / Half % 2;
            float f = p < 1 ? p : 2 - p, e = (1 - MathF.Cos(MathF.PI * f)) / 2;
            brush.Color = Canvas.WithA(Lilac, (1 - 0.2f * e) * opacity);
            ctx.FillEllipse(new Ellipse(new Vector2(DotX[i], -Lift + 2 * Lift * e), 2.1f, 2.1f), brush);
        }
    }

    static void HandOver(ID2D1DeviceContext ctx, ID2D1Factory1 factory, ID2D1SolidColorBrush brush, Matrix3x2 world, float rec, float opacity)
    {
        Span<float> v = stackalloc float[10];

        // ---- the quick dots, just before the coin
        if (rec < Coin)
        {
            for (int i = 0; i < Quick.Length; i++)
            {
                if (rec < Quick[i].From) continue;
                At(Quick[i].Pts, rec, v);
                var col = i == 3 ? White : i == 2 ? Mix(QuickC, White, Math.Clamp((rec - 583) / 17, 0, 1)) : QuickC;
                brush.Color = Canvas.WithA(col, opacity);
                ctx.Transform = Matrix3x2.CreateRotation(48 * MathF.PI / 180) * Matrix3x2.CreateTranslation(v[0], v[1]) * world;
                ctx.FillEllipse(new Ellipse(Vector2.Zero, v[2] * v[3], v[2]), brush);
            }
            ctx.Transform = world;
            return;
        }

        // ---- the coin: cut in, squashed, round a moment later
        float sq = rec < 655 ? 0.86f : rec < 667 ? 0.86f + 0.14f * EaseOut((rec - 655) / 12) : 1;
        var coin = Matrix3x2.CreateScale(1, sq) * world;
        ctx.Transform = coin;
        brush.Color = Canvas.WithA(Green, opacity);
        ctx.FillEllipse(new Ellipse(Vector2.Zero, 15, 15), brush);
        using var round = factory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });

        // the shine running clockwise round the rim (cut to the coin)
        float shineA = rec < Coin ? 0 : At(ShineA, rec);
        if (shineA > 0.01f)
        {
            using var disc = factory.CreateEllipseGeometry(new Ellipse(Vector2.Zero, 15, 15));
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Rect(-16, -16, 32, 32), GeometricMask = disc, MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity, Opacity = 1, LayerOptions = LayerOptions1.None,
            }, null!);
            ctx.Transform = Matrix3x2.CreateRotation(At(Shine, rec) * MathF.PI / 180) * coin;
            using (var arc = factory.CreatePathGeometry())
            {
                using (var sink = arc.Open())
                {
                    sink.BeginFigure(new Vector2(12.4f, -8.7f), FigureBegin.Hollow);
                    sink.AddArc(new ArcSegment(new Vector2(12.4f, 8.7f), new Size(15.15f, 15.15f), 0, SweepDirection.Clockwise, ArcSize.Small));
                    sink.EndFigure(FigureEnd.Open);
                    sink.Close();
                }
                brush.Color = Canvas.WithA(White, 0.95f * shineA * opacity);
                ctx.DrawGeometry(arc, brush, 3.6f, round);
            }
            ctx.Transform = coin;
            ctx.PopLayer();
        }

        // the ring: open on the left with its head a dot, then it spins edge-on into a ")"
        if (rec < 750)
        {
            At(Ring, rec, v);
            ctx.Transform = Matrix3x2.CreateScale(v[1], 1) * Matrix3x2.CreateTranslation(v[0], 0) * coin;
            At(Run, rec, v);
            float a0 = MathF.PI + v[0] / 8.6f, a1 = MathF.PI + v[1] / 8.6f;
            brush.Color = Canvas.WithA(Ink, opacity);
            using (var run = factory.CreatePathGeometry())
            {
                using (var sink = run.Open())
                {
                    const int Seg = 36;
                    sink.BeginFigure(new Vector2(8.6f * MathF.Cos(a0), 8.6f * MathF.Sin(a0)), FigureBegin.Hollow);
                    for (int i = 1; i <= Seg; i++) { float a = a0 + (a1 - a0) * i / Seg; sink.AddLine(new Vector2(8.6f * MathF.Cos(a), 8.6f * MathF.Sin(a))); }
                    sink.EndFigure(FigureEnd.Open);
                    sink.Close();
                }
                ctx.DrawGeometry(run, brush, TickW, round);
            }
            ctx.FillEllipse(new Ellipse(new Vector2(-8.6f, 0), TickW / 2, TickW / 2), brush); // (where the stroke begins)
            float headA = At(HeadA, rec);
            if (headA > 0.01f)
            {
                // (the head's position is eased between its three marks on the ring)
                Span<float> h0 = stackalloc float[2];
                HeadAt(rec, h0);
                brush.Color = Canvas.WithA(Ink, headA * opacity);
                ctx.FillEllipse(new Ellipse(new Vector2(h0[0], h0[1]), 1.5f, 1.5f), brush);
            }
            ctx.Transform = coin;
        }

        // the tick: its points move frame by frame
        if (rec >= 700)
        {
            At(Tick, rec, v);
            using var tick = factory.CreatePathGeometry();
            using (var sink = tick.Open())
            {
                sink.BeginFigure(new Vector2(v[0], v[1]), FigureBegin.Hollow);
                sink.AddLine(new Vector2(v[2], v[3]));
                sink.AddBezier(new BezierSegment { Point1 = new Vector2(v[4], v[5]), Point2 = new Vector2(v[6], v[7]), Point3 = new Vector2(v[8], v[9]) });
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            brush.Color = Canvas.WithA(Ink, opacity);
            ctx.DrawGeometry(tick, brush, TickW, round);
        }

        // the sparkle on the top right of the rim
        if (rec >= 833)
        {
            ctx.Transform = Matrix3x2.CreateScale(At(Spark, rec)) * Matrix3x2.CreateTranslation(10.3f, -9.2f) * coin;
            using var star = factory.CreatePathGeometry();
            using (var sink = star.Open())
            {
                sink.BeginFigure(new Vector2(0, -5.6f), FigureBegin.Filled);
                sink.AddBezier(new BezierSegment { Point1 = new(0.47f, -1.21f), Point2 = new(1.21f, -0.47f), Point3 = new(5.6f, 0) });
                sink.AddBezier(new BezierSegment { Point1 = new(1.21f, 0.47f), Point2 = new(0.47f, 1.21f), Point3 = new(0, 5.6f) });
                sink.AddBezier(new BezierSegment { Point1 = new(-0.47f, 1.21f), Point2 = new(-1.21f, 0.47f), Point3 = new(-5.6f, 0) });
                sink.AddBezier(new BezierSegment { Point1 = new(-1.21f, -0.47f), Point2 = new(-0.47f, -1.21f), Point3 = new(0, -5.6f) });
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            brush.Color = Canvas.WithA(White, At(SparkA, rec) * opacity);
            ctx.FillGeometry(star, brush);
        }
        ctx.Transform = world;
    }

    static void HeadAt(float rec, Span<float> into)
    {
        // straight lines between the three marks (as the original moves it), not along the arc
        float[][] marks = Head;
        Vector2 P(float deg) => new(8.6f * MathF.Cos(deg * MathF.PI / 180), 8.6f * MathF.Sin(deg * MathF.PI / 180));
        Vector2 p;
        if (rec <= marks[0][0]) p = P(marks[0][1]);
        else if (rec >= marks[2][0]) p = P(marks[2][1]);
        else
        {
            int i = rec <= marks[1][0] ? 1 : 2;
            float k = (rec - marks[i - 1][0]) / (marks[i][0] - marks[i - 1][0]);
            p = Vector2.Lerp(P(marks[i - 1][1]), P(marks[i][1]), k);
        }
        into[0] = p.X; into[1] = p.Y;
    }

    static float EaseOut(float u) { u = Math.Clamp(u, 0, 1); return 1 - (1 - u) * (1 - u); }
    static Color4 Mix(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);
}
