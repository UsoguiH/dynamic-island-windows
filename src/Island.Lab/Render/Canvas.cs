using System.Numerics;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;
using Island.Lab.Motion;

namespace Island.Lab.Render;

/// <summary>Thin immediate-mode drawing helper over Direct2D/DirectWrite.</summary>
public sealed class Canvas : IDisposable
{
    public readonly ID2D1DeviceContext Ctx;
    readonly Gpu _gpu;
    readonly ID2D1SolidColorBrush _brush;
    readonly ID2D1StrokeStyle _round;
    readonly Dictionary<(float, FontWeight), IDWriteTextFormat> _formats = new();
    readonly Dictionary<(string, float, FontWeight), float> _widths = new();
    readonly ID2D1StrokeStyle _dash;
    const string Font = "Segoe UI";

    public Canvas(Gpu gpu)
    {
        _gpu = gpu;
        Ctx = gpu.D2D;
        _brush = Ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _round = gpu.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round, LineJoin = LineJoin.Round,
        });
        _dash = gpu.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties
        {
            StartCap = CapStyle.Round, EndCap = CapStyle.Round, DashCap = CapStyle.Round,
            LineJoin = LineJoin.Round, DashStyle = DashStyle.Custom,
        }, [2.5f, 2.5f]);
    }

    public void DashedRound(float x, float y, float w, float h, float r, float width, Color4 c) =>
        Ctx.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c), width, _dash);

    public void StrokeRound(float x, float y, float w, float h, float r, float width, Color4 c) =>
        Ctx.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c), width);

    public void StrokeCircle(float cx, float cy, float r, float width, Color4 c) =>
        Ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c), width);

    public ID2D1Factory1 Factory => _gpu.D2DFactory;

    public ID2D1SolidColorBrush Brush(Color4 c) { _brush.Color = c; return _brush; }

    public static Color4 Rgba(uint rgb, float a = 1f) =>
        new(((rgb >> 16) & 0xFF) / 255f, ((rgb >> 8) & 0xFF) / 255f, (rgb & 0xFF) / 255f, a);

    public static readonly Color4 White = new(1, 1, 1, 1);
    public static Color4 WithA(Color4 c, float a) => new(c.R, c.G, c.B, a);
    public static Color4 WhiteA(float a) => new(1, 1, 1, a);

    IDWriteTextFormat Format(float size, FontWeight weight)
    {
        if (_formats.TryGetValue((size, weight), out var f)) return f;
        f = _gpu.DWrite.CreateTextFormat(Font, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
        f.WordWrapping = WordWrapping.NoWrap;
        _formats[(size, weight)] = f;
        return f;
    }

    public float Measure(string text, float size, FontWeight weight = FontWeight.Regular)
    {
        if (_widths.TryGetValue((text, size, weight), out var w)) return w;
        using var layout = _gpu.DWrite.CreateTextLayout(text, Format(size, weight), 4000, 200);
        w = layout.Metrics.WidthIncludingTrailingWhitespace;
        if (_widths.Count > 4000) _widths.Clear();
        _widths[(text, size, weight)] = w;
        return w;
    }

    readonly Dictionary<(string, float, FontWeight, float), string> _fits = new();

    /// <summary>Shortens text with an ellipsis so it fits in maxWidth.</summary>
    public string Fit(string text, float size, FontWeight weight, float maxWidth)
    {
        if (Measure(text, size, weight) <= maxWidth) return text;
        var key = (text, size, weight, maxWidth);
        if (_fits.TryGetValue(key, out var f)) return f;
        int lo = 0, hi = text.Length;
        while (lo < hi)
        {
            int mid = (lo + hi + 1) / 2;
            if (Measure(text[..mid].TrimEnd() + "…", size, weight) <= maxWidth) lo = mid; else hi = mid - 1;
        }
        f = text[..lo].TrimEnd() + "…";
        if (_fits.Count > 2000) _fits.Clear();
        _fits[key] = f;
        return f;
    }

    /// <summary>Draws text with its top-left at (x, y). align: 0 left, 0.5 center, 1 right (x is the anchor).</summary>
    public void Text(string text, float x, float y, float size, Color4 color,
        FontWeight weight = FontWeight.Regular, float align = 0f, float maxWidth = 2000f)
    {
        if (maxWidth < 2000f) text = Fit(text, size, weight, maxWidth);
        if (align != 0f) x -= Measure(text, size, weight) * align;
        Ctx.DrawText(text, Format(size, weight), new Rect(x, y, maxWidth, size * 1.6f), Brush(color),
            DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
    }

    public void Round(float x, float y, float w, float h, float r, Color4 c) =>
        Ctx.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c));

    public void Circle(float cx, float cy, float r, Color4 c) =>
        Ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c));

    public void Line(float x1, float y1, float x2, float y2, float width, Color4 c) =>
        Ctx.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), Brush(c), width, _round);

    /// <summary>Progress ring starting at 12 o'clock, clockwise.</summary>
    public void Ring(float cx, float cy, float r, float width, float progress, Color4 track, Color4 fill)
    {
        Ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(track), width);
        progress = Math.Clamp(progress, 0f, 1f);
        if (progress <= 0.001f) return;
        if (progress >= 0.999f) { Ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(fill), width); return; }
        float a = progress * MathF.Tau - MathF.PI / 2;
        using var geo = Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(new Vector2(cx, cy - r), FigureBegin.Hollow);
            sink.AddArc(new ArcSegment(new Vector2(cx + r * MathF.Cos(a), cy + r * MathF.Sin(a)), new Size(r, r), 0,
                SweepDirection.Clockwise, progress > 0.5f ? ArcSize.Large : ArcSize.Small));
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        Ctx.DrawGeometry(geo, Brush(fill), width, _round);
    }

    public void Polygon(Color4 c, params Vector2[] pts)
    {
        using var geo = Factory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(pts[0], FigureBegin.Filled);
            for (int i = 1; i < pts.Length; i++) sink.AddLine(pts[i]);
            sink.EndFigure(FigureEnd.Closed);
            sink.Close();
        }
        Ctx.FillGeometry(geo, Brush(c));
    }

    /// <summary>A rounded triangle "play" glyph pointing right, centered at (cx, cy).</summary>
    public void PlayGlyph(float cx, float cy, float s, Color4 c)
    {
        float h = s, w = s * 0.88f;
        Polygon(c, new(cx - w * 0.42f, cy - h / 2), new(cx + w * 0.58f, cy), new(cx - w * 0.42f, cy + h / 2));
    }

    public void PauseGlyph(float cx, float cy, float s, Color4 c)
    {
        float w = s * 0.28f, gap = s * 0.2f;
        Round(cx - gap / 2 - w, cy - s / 2, w, s, w * 0.3f, c);
        Round(cx + gap / 2, cy - s / 2, w, s, w * 0.3f, c);
    }

    public void SkipGlyph(float cx, float cy, float s, Color4 c, bool forward)
    {
        float d = forward ? 1 : -1;
        float w = s * 0.5f;
        Polygon(c, new(cx - d * w, cy - s / 2.6f), new(cx, cy), new(cx - d * w, cy + s / 2.6f));
        Polygon(c, new(cx, cy - s / 2.6f), new(cx + d * w, cy), new(cx, cy + s / 2.6f));
    }

    // ---- Icons (Segoe MDL2 Assets, built into Windows 10) and wrapped paragraphs.
    readonly Dictionary<float, IDWriteTextFormat> _icons = new();
    readonly Dictionary<(float, FontWeight), IDWriteTextFormat> _wrapFormats = new();
    readonly Dictionary<(string, float, float, FontWeight), IDWriteTextLayout> _layouts = new();

    /// <summary>Draws one icon glyph centred on (cx, cy).</summary>
    public void Icon(char glyph, float cx, float cy, float size, Color4 color)
    {
        if (!_icons.TryGetValue(size, out var f))
        {
            f = _gpu.DWrite.CreateTextFormat("Segoe MDL2 Assets", FontWeight.Normal, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            f.TextAlignment = TextAlignment.Center;
            f.ParagraphAlignment = ParagraphAlignment.Center;
            f.WordWrapping = WordWrapping.NoWrap;
            _icons[size] = f;
        }
        Ctx.DrawText(glyph.ToString(), f, new Rect(cx - size, cy - size, size * 2, size * 2), Brush(color));
    }

    IDWriteTextLayout Layout(string text, float size, float width, FontWeight weight)
    {
        var key = (text, size, width, weight);
        if (_layouts.TryGetValue(key, out var l)) return l;
        if (!_wrapFormats.TryGetValue((size, weight), out var f))
        {
            f = _gpu.DWrite.CreateTextFormat(Font, weight, Vortice.DirectWrite.FontStyle.Normal, FontStretch.Normal, size);
            f.WordWrapping = WordWrapping.Wrap;
            _wrapFormats[(size, weight)] = f;
        }
        if (_layouts.Count > 300) { foreach (var v in _layouts.Values) v.Dispose(); _layouts.Clear(); }
        l = _gpu.DWrite.CreateTextLayout(text, f, width, 100000);
        _layouts[key] = l;
        return l;
    }

    /// <summary>Height of a wrapped paragraph.</summary>
    public float WrappedHeight(string text, float size, float width, FontWeight weight = FontWeight.Regular) =>
        Layout(text, size, width, weight).Metrics.Height;

    /// <summary>Width of the widest line of a wrapped paragraph.</summary>
    public float WrappedWidth(string text, float size, float width, FontWeight weight = FontWeight.Regular) =>
        Layout(text, size, width, weight).Metrics.WidthIncludingTrailingWhitespace;

    /// <summary>Draws a wrapped paragraph with its top-left at (x, y); returns its height.</summary>
    public float Wrapped(string text, float x, float y, float width, float size, Color4 color, FontWeight weight = FontWeight.Regular)
    {
        var l = Layout(text, size, width, weight);
        Ctx.DrawTextLayout(new Vector2(x, y), l, Brush(color), DrawTextOptions.EnableColorFont);
        return l.Metrics.Height;
    }

    ID2D1LinearGradientBrush? _shimmer;
    readonly Dictionary<uint, ID2D1RadialGradientBrush> _glows = new();

    /// <summary>Text with a bright band sweeping across it (the "working…" shimmer). phase 0..1 = band position.</summary>
    public void ShimmerText(string text, float x, float y, float size, float alpha, float phase,
        FontWeight weight = FontWeight.Regular, float align = 0f)
    {
        float tw = Measure(text, size, weight);
        if (align != 0f) x -= tw * align;
        if (_shimmer == null)
        {
            using var stops = Ctx.CreateGradientStopCollection(
                [new GradientStop(0, WhiteA(0.5f)), new GradientStop(0.5f, WhiteA(1f)), new GradientStop(1, WhiteA(0.5f))]);
            _shimmer = Ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(Vector2.Zero, Vector2.One), stops);
        }
        float band = 46, bx = x - band + (tw + band * 2) * phase;
        _shimmer.StartPoint = new Vector2(bx - band / 2, 0);
        _shimmer.EndPoint = new Vector2(bx + band / 2, 0);
        _shimmer.Opacity = alpha;
        Ctx.DrawText(text, Format(size, weight), new Rect(x, y, 2000, size * 1.6f), _shimmer,
            DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
    }

    /// <summary>A soft elliptical glow (colour at the centre fading to transparent).</summary>
    public void Glow(float cx, float cy, float rx, float ry, uint rgb, float alpha)
    {
        if (alpha <= 0.003f) return;
        if (!_glows.TryGetValue(rgb, out var b))
        {
            var c = Rgba(rgb);
            using var stops = Ctx.CreateGradientStopCollection(
                [new GradientStop(0, WithA(c, 1)), new GradientStop(0.45f, WithA(c, 0.45f)), new GradientStop(1, WithA(c, 0))]);
            b = Ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
            _glows[rgb] = b;
        }
        b.Center = new Vector2(cx, cy);
        b.RadiusX = rx; b.RadiusY = ry;
        b.Opacity = alpha;
        Ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), rx, ry), b);
    }

    public ID2D1LinearGradientBrush Gradient(Vector2 a, Vector2 b, Color4 c0, Color4 c1)
    {
        using var stops = Ctx.CreateGradientStopCollection([new GradientStop(0, c0), new GradientStop(1, c1)]);
        return Ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(a, b), stops);
    }

    public void Dispose()
    {
        foreach (var f in _formats.Values) f.Dispose();
        foreach (var g in _glows.Values) g.Dispose();
        foreach (var f in _icons.Values) f.Dispose();
        foreach (var f in _wrapFormats.Values) f.Dispose();
        foreach (var l in _layouts.Values) l.Dispose();
        _shimmer?.Dispose();
        _round.Dispose();
        _dash.Dispose();
        _brush.Dispose();
    }
}

/// <summary>iOS-style numeric text transition: each changed character rolls vertically.</summary>
public sealed class RollingText
{
    readonly List<(char cur, char prev, Spring t)> _slots = new();

    public void Set(string s)
    {
        while (_slots.Count < s.Length) _slots.Add((' ', ' ', new Spring(1, SpringSpec.Quick, 0.002f)));
        for (int i = 0; i < s.Length; i++)
        {
            var (cur, _, t) = _slots[i];
            if (cur != s[i])
            {
                _slots[i] = (s[i], cur, t);
                t.Snap(0);
                t.To(1);
            }
        }
    }

    public void Step(float dt) { foreach (var s in _slots) s.t.Step(dt); }

    public void Draw(Canvas c, float x, float y, float size, Color4 color, FontWeight weight)
    {
        float digitW = c.Measure("0", size, weight);
        float colonW = c.Measure(":", size, weight);
        float travel = size * 0.7f;
        foreach (var (cur, prev, t) in _slots)
        {
            float w = cur == ':' ? colonW : digitW;
            float p = Math.Clamp(t.Value, 0, 1);
            if (p < 1 && prev != ' ')
                c.Text(prev.ToString(), x, y - travel * p, size, Canvas.WithA(color, color.A * (1 - p)), weight);
            c.Text(cur.ToString(), x, y + travel * (1 - p), size, Canvas.WithA(color, color.A * p), weight);
            x += w;
        }
    }
}
