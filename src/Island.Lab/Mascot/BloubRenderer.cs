// Bloub mascot — Direct2D renderer.
// Rendering rules ported from src/components/BloubBot.vue of Bloub by Jérémy Perret (jeremy-prt),
// https://github.com/jeremy-prt/bloub. Copyright (c) 2026 Jérémy Perret. MIT License (full text in BloubEngine.cs).

using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Mathematics;

namespace Island.Lab.Mascot;

/// <summary>
/// Draws a <see cref="BloubFrame"/> with Direct2D, mirroring the SVG of BloubBot.vue:
/// <list type="number">
/// <item>back halves of the arcs (occluded by the body),</item>
/// <item>burst particles when <see cref="BloubFrame.DotsBehind"/>,</item>
/// <item>the body: an opaque "paper" backing of the exact body shape, then the body colour with the
///   eyes and the notification notch cut out as real geometric holes (CombineMode.Exclude),</item>
/// <item>front dots, the notification pastille, front halves of the arcs.</item>
/// </list>
/// The paper backing is what keeps a ring passing behind the ball from re-appearing inside the eyes
/// (as in the original). On the island the paper is black, so the holes read as black; set
/// <see cref="PaperColor"/> to transparent for see-through holes.
///
/// Device-dependent resources (brushes, stroke style, gradient brushes, the teardrop geometry) are
/// cached and rebuilt when the context or factory changes. Per frame it only creates the few small
/// geometries that change every frame. Must be used on the thread that owns the context.
/// </summary>
public sealed class BloubRenderer : IDisposable
{
    /// <summary>Resting ball radius of the frames in viewBox units (the engine's <see cref="BloubEngine.Scale"/>).</summary>
    public float EngineScale { get; set; } = (float)BloubMath.Rayon;

    /// <summary>Colour seen through the eye holes and used for the particles' depth haze (the page "paper"). Default: opaque black.</summary>
    public Color4 PaperColor { get; set; } = new(0f, 0f, 0f, 1f);

    /// <summary>Notification pastille colour (default: the measured #2496e8 blue).</summary>
    public Color4 NotifColor { get; set; } = Rgb(BloubDecor.NotifBlue);

    ID2D1DeviceContext? _ctx;
    ID2D1Factory1? _factory;
    ID2D1SolidColorBrush? _brush;
    ID2D1StrokeStyle? _round;
    readonly Dictionary<(uint, uint, uint), ID2D1LinearGradientBrush> _gradients = new();
    readonly Dictionary<Vector2[], ID2D1PathGeometry> _shapes = new(ReferenceEqualityComparer.Instance);
    Vector2[] _c1 = new Vector2[BloubShape.ProfileSamples];
    Vector2[] _c2 = new Vector2[BloubShape.ProfileSamples];
    readonly List<ID2D1Geometry> _holes = new(4);

    public static Color4 Rgb(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    /// <summary>
    /// Draws the frame centred on <paramref name="center"/>.
    /// </summary>
    /// <param name="ctx">Target context; its current transform is honoured and restored.</param>
    /// <param name="factory">The factory that created <paramref name="ctx"/>'s device.</param>
    /// <param name="f">Frame from <see cref="BloubEngine.Sample"/>.</param>
    /// <param name="center">Ball centre, in the context's current units (DIPs).</param>
    /// <param name="ballRadiusPx">Radius of the RESTING ball in DIPs. Rings reach 1.4x this radius.</param>
    /// <param name="bodyColor">Body ("ink") colour, e.g. #F2F2F7 on the black island.</param>
    /// <param name="opacity">Whole-mascot opacity (applied as a group through a layer when &lt; 1).</param>
    public void Draw(ID2D1DeviceContext ctx, ID2D1Factory1 factory, BloubFrame f, Vector2 center, float ballRadiusPx, Color4 bodyColor, float opacity)
    {
        if (opacity <= 0.002f || ballRadiusPx <= 0) return;
        Ensure(ctx, factory);

        float s = ballRadiusPx / EngineScale;
        var saved = ctx.Transform;
        var world = Matrix3x2.CreateScale(s) * Matrix3x2.CreateTranslation(center) * saved;
        ctx.Transform = world;

        // Flattening tolerance for geometry combines, expressed in viewBox units (D2D's default is
        // 0.25 device pixels).
        ctx.GetDpi(out float dpiX, out _);
        float devPerUnit = s * MathF.Sqrt(MathF.Abs(saved.GetDeterminant())) * dpiX / 96f;
        float tol = 0.25f / MathF.Max(devPerUnit, 1e-3f);

        // Group opacity, so overlapping parts (paper over back rings, etc.) don't double-blend.
        bool layer = opacity < 0.998f;
        if (layer)
        {
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f),
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = opacity,
                LayerOptions = LayerOptions1.None,
            }, null!);
        }

        try
        {
            // 1. back half of the orbits: drawn before the body, so occluded by it
            foreach (var arc in f.Arcs) StrokeRuns(ctx, factory, arc, arc.Back);

            // 2. burst particles pass behind the core
            if (f.DotsBehind) DrawDots(ctx, f, bodyColor, world);

            // 3. body
            DrawBody(ctx, factory, f, bodyColor, tol);

            // 4. front dots
            if (!f.DotsBehind) DrawDots(ctx, f, bodyColor, world);

            // 5. notification pastille (the notch was cut from the body above)
            if (f.Notif is { } n)
                ctx.FillEllipse(new Ellipse(new Vector2(n.X, n.Y), n.R, n.R), Brush(NotifColor));

            // 6. front half of the orbits
            foreach (var arc in f.Arcs) StrokeRuns(ctx, factory, arc, arc.Front);
        }
        finally
        {
            if (layer) ctx.PopLayer();
            ctx.Transform = saved;
        }
    }

    void DrawBody(ID2D1DeviceContext ctx, ID2D1Factory1 factory, BloubFrame f, Color4 ink, float tol)
    {
        float alpha = f.BodyAlpha;
        if (alpha <= 0.002f || f.Body.Length < 3) return;

        using var body = BuildBody(factory, f.Body);

        // Opaque backing in the paper colour (what the eye holes show).
        if (PaperColor.A > 0)
            ctx.FillGeometry(body, Brush(WithA(PaperColor, PaperColor.A * alpha)), null);

        var inkBrushColor = WithA(ink, ink.A * alpha);

        // Holes: eyes (capsules placed by their matrix) and the notch.
        _holes.Clear();
        try
        {
            foreach (var e in f.Eyes)
            {
                if (e.Alpha <= 0.002f) continue;
                _holes.Add(EyeGeometry(factory, e));
            }
            if (f.Notch is { } n && n.R > 0)
                _holes.Add(factory.CreateEllipseGeometry(new Ellipse(new Vector2(n.X, n.Y), n.R, n.R)));

            if (_holes.Count == 0)
            {
                ctx.FillGeometry(body, InkBrush(ctx, ink, alpha), null);
                return;
            }

            using var group = factory.CreateGeometryGroup(FillMode.Winding, _holes.ToArray(), (uint)_holes.Count);
            using var holed = factory.CreatePathGeometry();
            using (var sink = holed.Open())
            {
                body.CombineWithGeometry(group, CombineMode.Exclude, tol, sink);
                sink.Close();
            }
            ctx.FillGeometry(holed, InkBrush(ctx, ink, alpha), null);

            // A partially transparent eye (fading in/out, or near the sphere's limb) is a partial
            // hole: the SVG mask leaves (1 - alpha) of the body colour there.
            int h = 0;
            foreach (var e in f.Eyes)
            {
                if (e.Alpha <= 0.002f) continue;
                var eyeGeo = _holes[h++];
                if (e.Alpha >= 0.998f) continue;
                using var part = factory.CreatePathGeometry();
                using (var sink = part.Open())
                {
                    body.CombineWithGeometry(eyeGeo, CombineMode.Intersect, tol, sink);
                    sink.Close();
                }
                ctx.FillGeometry(part, Brush(WithA(ink, ink.A * alpha * (1 - e.Alpha))), null);
            }
        }
        finally
        {
            foreach (var g in _holes) g.Dispose();
            _holes.Clear();
        }
    }

    /// <summary>A capsule (stadium) centred on the origin, transformed by the eye matrix.</summary>
    static ID2D1Geometry EyeGeometry(ID2D1Factory1 factory, BloubEye e)
    {
        float hw = e.W / 2, hh = e.H / 2, r = MathF.Min(hw, hh);
        using var rr = factory.CreateRoundedRectangleGeometry(
            new RoundedRectangle(new System.Drawing.RectangleF(-hw, -hh, e.W, e.H), r, r));
        var m = e.Matrix;
        return factory.CreateTransformedGeometry(rr, ref m);
    }

    /// <summary>Closed Catmull-Rom spline through the 64 outline points (same as the original's closedPath).</summary>
    ID2D1PathGeometry BuildBody(ID2D1Factory1 factory, Vector2[] pts)
    {
        int n = pts.Length;
        if (_c1.Length < n) { _c1 = new Vector2[n]; _c2 = new Vector2[n]; }
        BloubShape.ClosedPathControls(pts, _c1.AsSpan(0, n), _c2.AsSpan(0, n));
        var geo = factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.SetFillMode(FillMode.Winding);
        sink.BeginFigure(pts[0], FigureBegin.Filled);
        for (int i = 0; i < n; i++)
            sink.AddBezier(new BezierSegment { Point1 = _c1[i], Point2 = _c2[i], Point3 = pts[(i + 1) % n] });
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    void DrawDots(ID2D1DeviceContext ctx, BloubFrame f, Color4 ink, Matrix3x2 world)
    {
        foreach (var d in f.Dots)
        {
            // Colour follows the body; `Depth` (particles) melts it into the paper as they recede.
            Color4 c = float.IsNaN(d.Depth) ? ink : MixRgb(PaperColor, ink, d.Depth);
            var brush = Brush(WithA(c, ink.A * d.Opacity));
            if (d.Shape is { } poly)
            {
                // path in ball-radius units centred on the origin: translate(x y) rotate(rot) scale(R)
                var geo = ShapeGeometry(poly);
                ctx.Transform = Matrix3x2.CreateScale(EngineScale)
                    * Matrix3x2.CreateRotation(d.RotDeg * MathF.PI / 180f)
                    * Matrix3x2.CreateTranslation(d.X, d.Y) * world;
                ctx.FillGeometry(geo, brush, null);
                ctx.Transform = world;
            }
            else
            {
                ctx.FillEllipse(new Ellipse(new Vector2(d.X, d.Y), d.R, d.R), brush);
            }
        }
    }

    ID2D1PathGeometry ShapeGeometry(Vector2[] poly)
    {
        if (_shapes.TryGetValue(poly, out var g)) return g;
        g = _factory!.CreatePathGeometry();
        using (var sink = g.Open())
        {
            sink.SetFillMode(FillMode.Winding);
            sink.BeginFigure(poly[0], FigureBegin.Filled);
            for (int i = 1; i < poly.Length; i++) sink.AddLine(poly[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        // The engine hands out the same cached array for a given polygon; the bound is only a safety net.
        if (_shapes.Count >= 32)
        {
            foreach (var old in _shapes.Values) old.Dispose();
            _shapes.Clear();
        }
        _shapes[poly] = g;
        return g;
    }

    void StrokeRuns(ID2D1DeviceContext ctx, ID2D1Factory1 factory, ArcRender arc, List<Vector2[]> runs)
    {
        if (arc.Opacity <= 0.002f || runs.Count == 0) return;
        bool any = false;
        foreach (var r in runs) if (r.Length >= 2) { any = true; break; }
        if (!any) return;

        using var geo = factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            foreach (var run in runs)
            {
                // a lone "M" sub-path draws nothing in SVG either
                if (run.Length < 2) continue;
                sink.BeginFigure(run[0], FigureBegin.Hollow);
                for (int i = 1; i < run.Length; i++) sink.AddLine(run[i]);
                sink.EndFigure(FigureEnd.Open);
            }
            sink.Close();
        }
        var brush = Gradient(ctx, arc);
        ctx.DrawGeometry(geo, brush, arc.Width, _round);
    }

    ID2D1LinearGradientBrush Gradient(ID2D1DeviceContext ctx, ArcRender arc)
    {
        var key = (arc.GradStops[0], arc.GradStops[1], arc.GradStops[2]);
        if (!_gradients.TryGetValue(key, out var b))
        {
            using var stops = ctx.CreateGradientStopCollection(
            [
                new GradientStop(0f, Rgb(key.Item1)),
                new GradientStop(0.5f, Rgb(key.Item2)),
                new GradientStop(1f, Rgb(key.Item3)),
            ]);
            b = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(arc.GradStart, arc.GradEnd), stops);
            _gradients[key] = b;
        }
        b.StartPoint = arc.GradStart;
        b.EndPoint = arc.GradEnd;
        b.Opacity = arc.Opacity;
        return b;
    }

    // ---- 3D shading (island addition): the body is lit like a sphere — bright upper-left, the ink
    // colour in the middle, and a darker (or tinted) bottom. Brushes are in viewBox units, so they
    // follow the ball's transform automatically.

    /// <summary>When true the body is drawn as a lit sphere instead of a flat fill.</summary>
    public bool Shading { get; set; } = true;

    /// <summary>Optional bottom tint for the shading (e.g. blue while working); null = darkened ink.</summary>
    public Color4? ShadeBottom { get; set; }

    /// <summary>How far the lit top goes towards white (0.85 for the white Bloub, ~0.4 keeps a coloured ball coloured).</summary>
    public float ShadeHighlight { get; set; } = 0.85f;

    readonly Dictionary<(uint, uint, uint), ID2D1RadialGradientBrush> _shaded = new();

    // 6 bits per channel: colours animating between two tints reuse a few dozen brushes, not hundreds.
    static uint Key(Color4 c) => ((uint)(Math.Clamp(c.R, 0, 1) * 63) << 12) | ((uint)(Math.Clamp(c.G, 0, 1) * 63) << 6) | (uint)(Math.Clamp(c.B, 0, 1) * 63);

    static Color4 Mix(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    ID2D1Brush InkBrush(ID2D1DeviceContext ctx, Color4 ink, float alpha)
    {
        if (!Shading) return Brush(WithA(ink, ink.A * alpha));
        var bottom = ShadeBottom ?? Mix(ink, new Color4(0, 0, 0, 1), 0.42f);
        var key = (Key(ink), Key(bottom), (uint)(ShadeHighlight * 32));
        if (!_shaded.TryGetValue(key, out var b))
        {
            if (_shaded.Count > 160) { foreach (var v in _shaded.Values) v.Dispose(); _shaded.Clear(); }
            var top = Mix(ink, new Color4(1, 1, 1, 1), ShadeHighlight);
            using var stops = ctx.CreateGradientStopCollection(
            [
                new GradientStop(0f, top),
                new GradientStop(0.3f, new Color4(ink.R, ink.G, ink.B, 1)),
                new GradientStop(1f, bottom),
            ]);
            b = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(
                new Vector2(-30, -42), Vector2.Zero, 165, 165), stops);
            _shaded[key] = b;
        }
        b.Opacity = ink.A * alpha;
        return b;
    }

    ID2D1SolidColorBrush Brush(Color4 c)
    {
        _brush!.Color = c;
        return _brush;
    }

    static Color4 WithA(Color4 c, float a) => new(c.R, c.G, c.B, a);

    static Color4 MixRgb(Color4 from, Color4 to, float t) =>
        new(from.R + (to.R - from.R) * t, from.G + (to.G - from.G) * t, from.B + (to.B - from.B) * t, to.A);

    void Ensure(ID2D1DeviceContext ctx, ID2D1Factory1 factory)
    {
        if (!ReferenceEquals(ctx, _ctx))
        {
            DisposeDeviceResources();
            _ctx = ctx;
            _brush = ctx.CreateSolidColorBrush(new Color4(1f, 1f, 1f, 1f));
        }
        if (!ReferenceEquals(factory, _factory))
        {
            DisposeFactoryResources();
            _factory = factory;
            _round = factory.CreateStrokeStyle(new StrokeStyleProperties
            {
                StartCap = CapStyle.Round,
                EndCap = CapStyle.Round,
                DashCap = CapStyle.Round,
                LineJoin = LineJoin.Round,
            });
        }
    }

    void DisposeDeviceResources()
    {
        foreach (var g in _gradients.Values) g.Dispose();
        _gradients.Clear();
        foreach (var g in _shaded.Values) g.Dispose();
        _shaded.Clear();
        _brush?.Dispose();
        _brush = null;
        _ctx = null;
    }

    void DisposeFactoryResources()
    {
        foreach (var g in _shapes.Values) g.Dispose();
        _shapes.Clear();
        _round?.Dispose();
        _round = null;
        _factory = null;
    }

    public void Dispose()
    {
        DisposeDeviceResources();
        DisposeFactoryResources();
    }
}
