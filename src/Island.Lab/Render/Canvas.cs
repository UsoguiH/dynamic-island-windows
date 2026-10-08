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

    // ================================================================ frame diffing
    // Each frame runs the scene twice. The probe pass draws nothing: every primitive records a hash of
    // what it would draw and its on-screen bounds. Only when some hash changed does the real pass run,
    // clipped to the union of the changed bounds, and primitives entirely outside it are skipped. A still
    // island costs no drawing and no GPU work; a blinking Bloub redraws only the few pixels around him.

    /// <summary>Probe pass: primitives are recorded, not drawn.</summary>
    public bool Probing { get; private set; }
    /// <summary>ISLAND_STATS=1: count which primitives change, to find what keeps the island redrawing.</summary>
    public static readonly bool Stats = Environment.GetEnvironmentVariable("ISLAND_STATS") == "1";
    public static readonly Dictionary<string, int> StatCounts = new();
    /// <summary>Real pass: primitives entirely outside this area (DIPs) are skipped.</summary>
    public Area? Cull;
    List<(ulong h, Area b)> _prims = new(256), _prev = new(256);
    bool _full;
    ulong _layer, _xfHash;
    Matrix3x2 _xf = Matrix3x2.Identity;
    Area? _clip;
    // what each push did to D2D: 0 nothing (probe, or a layer that couldn't change the pixels being redrawn), 1 a clip, 2 a layer
    // Clips and layers reach D2D lazily: only once something inside them is actually drawn in the area
    // being redrawn (a layer costs an offscreen surface; most areas touch only a few of them).
    sealed class Scope
    {
        public ulong Layer; public Area? Clip; public IDisposable? Owned;
        public byte Kind;            // 0 nothing for D2D, 1 an axis-aligned clip, 2 a layer
        public bool Pushed;
        public Rect Rect; public LayerParameters1 Params; public Matrix3x2 Xf;
    }
    readonly List<Scope> _stack = new();
    int _live; // scopes [0, _live) are pushed to D2D

    void ResetState()
    {
        _xf = Matrix3x2.Identity; _xfHash = 0; _layer = 0; _clip = null; Cull = null; _bloubAt = 0;
        while (_stack.Count > 0) { var e = _stack[^1]; _stack.RemoveAt(_stack.Count - 1); e.Owned?.Dispose(); }
        _live = 0;
    }

    public void BeginProbe() { ResetState(); Probing = true; _prims.Clear(); _full = false; _bloubs.Clear(); }

    /// <summary>The whole surface must be redrawn this frame (blurred transitions, goo).</summary>
    public void MarkFull(string why) { if (Probing) { _full = true; if (Stats) Count("full: " + why); } }

    static void Count(string key) => StatCounts[key] = StatCounts.GetValueOrDefault(key) + 1;

    readonly List<Area> _dirty = new();

    /// <summary>
    /// Ends the probe pass: the areas that changed since the last drawn frame (merged into a few
    /// rectangles), or null when nothing did.
    /// </summary>
    public List<Area>? EndProbe(Area surface)
    {
        Probing = false;
        _dirty.Clear();
        if (_full) _dirty.Add(surface);
        else if (_prims.Count == _prev.Count)
        {
            for (int i = 0; i < _prims.Count; i++)
                if (_prims[i].h != _prev[i].h) { _dirty.Add(_prev[i].b); _dirty.Add(_prims[i].b); }
        }
        else
        {
            // something appeared or vanished: everything between the common head and tail changed
            int n = _prims.Count, m = _prev.Count, head = 0, tail = 0;
            while (head < n && head < m && _prims[head].h == _prev[head].h) head++;
            while (tail < n - head && tail < m - head && _prims[n - 1 - tail].h == _prev[m - 1 - tail].h) tail++;
            for (int i = head; i < n - tail; i++) _dirty.Add(_prims[i].b);
            for (int i = head; i < m - tail; i++) _dirty.Add(_prev[i].b);
        }
        if (Stats && !_full && _prims.Count == _prev.Count)
            for (int i = 0; i < _prims.Count; i++)
                if (_prims[i].h != _prev[i].h) Count($"#{i} ({_prims[i].b.L:0},{_prims[i].b.T:0} .. {_prims[i].b.R:0},{_prims[i].b.B:0})");
        (_prev, _prims) = (_prims, _prev);
        Merge(_dirty, surface);
        return _dirty.Count == 0 ? null : _dirty;
    }

    /// <summary>
    /// Clips the areas to the surface and merges them into a few rectangles: two are merged when redrawing
    /// their union costs about as much as redrawing both (each separate area costs a pass over the scene).
    /// </summary>
    static void Merge(List<Area> a, Area surface)
    {
        const float PassCost = 48 * 48; // one more pass ≈ redrawing this many extra square DIPs
        for (int i = a.Count - 1; i >= 0; i--)
        {
            var c = a[i].Intersect(surface);
            if (c.Empty) a.RemoveAt(i); else a[i] = c;
        }
        if (a.Count > 400)
        {
            var u = a[0];
            foreach (var x in a) u = u.Union(x);
            a.Clear(); a.Add(u); return;
        }
        static float Size(Area x) => (x.R - x.L) * (x.B - x.T);
        static bool Worth(Area p, Area q) => p.Overlaps(q) || Size(p.Union(q)) <= Size(p) + Size(q) + PassCost;
        // grow each area by swallowing every later one worth merging; repeat until nothing merges
        for (bool again = true; again;)
        {
            again = false;
            for (int i = 0; i < a.Count; i++)
                for (bool grew = true; grew;)
                {
                    grew = false;
                    for (int j = a.Count - 1; j > i; j--)
                        if (Worth(a[i], a[j])) { a[i] = a[i].Union(a[j]); a.RemoveAt(j); grew = again = true; }
                }
        }
        // at most a handful of passes: merge the cheapest pairs
        while (a.Count > 6)
        {
            int bi = 0, bj = 1; float best = float.MaxValue;
            for (int i = 0; i < a.Count; i++)
                for (int j = i + 1; j < a.Count; j++)
                {
                    float cost = Size(a[i].Union(a[j])) - Size(a[i]) - Size(a[j]);
                    if (cost < best) { best = cost; bi = i; bj = j; }
                }
            a[bi] = a[bi].Union(a[bj]); a.RemoveAt(bj);
        }
    }

    /// <summary>Diagnostics: the primitives recorded this frame and last frame that touch <paramref name="a"/>.</summary>
    public string Describe(Area a)
    {
        var sb = new System.Text.StringBuilder();
        for (int i = 0; i < _prev.Count; i++)
            if (_prev[i].b.Overlaps(a))
                sb.Append($" #{i}({_prev[i].b.L:0},{_prev[i].b.T:0},{_prev[i].b.R:0},{_prev[i].b.B:0}){(i < _prims.Count && _prims[i].h == _prev[i].h ? "=" : "*")}");
        return sb.ToString();
    }

    /// <summary>Forgets the last frame, so the next one is drawn in full.</summary>
    public void Invalidate() => _prev.Clear();

    /// <summary>Starts the real pass, skipping primitives outside <paramref name="cull"/>.</summary>
    public void BeginDraw(Area cull) { ResetState(); Cull = cull; }

    /// <summary>
    /// Every primitive goes through here with its local bounds and its own hash. Probe: records it and
    /// returns false. Real pass: true when it touches the area being redrawn.
    /// </summary>
    public bool Prim(float x, float y, float w, float h, ulong own)
    {
        var b = Bounds(x, y, w, h).Inflate(2);
        if (_clip is { } c) b = b.Intersect(c);
        if (Probing) { _prims.Add((FrameHash.Mix(FrameHash.Mix(_layer, _xfHash), own), b)); return false; }
        if (b.Empty || (Cull is { } k && !b.Overlaps(k))) return false;
        Materialize();
        return true;
    }

    Area Bounds(float x, float y, float w, float h)
    {
        if (_xf.IsIdentity) return Area.Of(x, y, w, h);
        var a = Vector2.Transform(new(x, y), _xf); var b = Vector2.Transform(new(x + w, y), _xf);
        var c = Vector2.Transform(new(x, y + h), _xf); var d = Vector2.Transform(new(x + w, y + h), _xf);
        return new(MathF.Min(MathF.Min(a.X, b.X), MathF.Min(c.X, d.X)), MathF.Min(MathF.Min(a.Y, b.Y), MathF.Min(c.Y, d.Y)),
                   MathF.Max(MathF.Max(a.X, b.X), MathF.Max(c.X, d.X)), MathF.Max(MathF.Max(a.Y, b.Y), MathF.Max(c.Y, d.Y)));
    }

    static FrameHash H(int kind) => new((ulong)kind);

    /// <summary>The drawing transform (use this instead of Ctx.Transform, so the probe pass sees it too).</summary>
    public Matrix3x2 Transform
    {
        get => _xf;
        set
        {
            _xf = value;
            _xfHash = value.IsIdentity ? 0 : H(90).F(value.M11).F(value.M12).F(value.M21).F(value.M22).F(value.M31).F(value.M32).V;
            if (!Probing) Ctx.Transform = value;
        }
    }

    Scope Push(Area? bounds, ulong key, IDisposable? owned = null)
    {
        var e = new Scope { Layer = _layer, Clip = _clip, Owned = owned, Xf = _xf };
        _stack.Add(e);
        _layer = FrameHash.Mix(_layer, key);
        if (bounds is { } b) _clip = _clip is { } c ? c.Intersect(b) : b;
        return e;
    }

    void Pop()
    {
        var e = _stack[^1];
        _stack.RemoveAt(_stack.Count - 1);
        if (_live > _stack.Count) _live = _stack.Count;
        // pop D2D's clip or layer before releasing a mask this canvas owns
        if (e.Pushed) { if (e.Kind == 1) Ctx.PopAxisAlignedClip(); else Ctx.PopLayer(); }
        _layer = e.Layer; _clip = e.Clip; e.Owned?.Dispose();
    }

    /// <summary>Pushes to D2D the clips and layers that are still pending, in order (before anything is drawn in them).</summary>
    public void Materialize()
    {
        if (_live == _stack.Count) return;
        bool moved = false;
        for (int i = _live; i < _stack.Count; i++)
        {
            var e = _stack[i];
            if (e.Kind == 0) continue;
            if (!moved || Ctx.Transform != e.Xf) { Ctx.Transform = e.Xf; moved = true; }
            if (e.Kind == 1) Ctx.PushAxisAlignedClip(e.Rect, AntialiasMode.Aliased);
            else Ctx.PushLayer(e.Params, null!);
            e.Pushed = true;
        }
        if (moved) Ctx.Transform = _xf;
        _live = _stack.Count;
    }

    /// <summary>An axis-aligned clip (local coordinates).</summary>
    public void PushClip(float x, float y, float w, float h)
    {
        var e = Push(Bounds(x, y, w, h), H(91).P(x).P(y).P(w).P(h).V);
        if (!Probing) { e.Kind = 1; e.Rect = new Rect(x, y, w, h); }
    }

    public void PopClip() => Pop();

    /// <summary>
    /// An opacity layer, optionally masked by a geometry. <paramref name="maskBounds"/> (local) bounds the mask
    /// and <paramref name="maskKey"/> identifies its shape, so content redraws when the mask changes. The D2D
    /// layer is pushed only once something in it is drawn in the area being redrawn (an empty layer changes
    /// nothing); then always, even where its mask couldn't change a pixel: text antialiases differently in a layer.
    /// </summary>
    public void PushLayer(float opacity, ID2D1Geometry? mask = null, Area? maskBounds = null, ulong maskKey = 0, IDisposable? owned = null)
    {
        var clip = maskBounds is { } mb ? Bounds(mb.L, mb.T, mb.R - mb.L, mb.B - mb.T) : (Area?)null;
        var e = Push(clip, H(92).A(opacity).I((long)maskKey).V, owned);
        if (Probing || (mask == null && opacity >= 0.999f)) return;
        e.Kind = 2;
        e.Params = new LayerParameters1
        {
            ContentBounds = new Rect(-1e6f, -1e6f, 2e6f, 2e6f),
            GeometricMask = mask!,
            MaskAntialiasMode = AntialiasMode.PerPrimitive,
            MaskTransform = Matrix3x2.Identity,
            Opacity = opacity,
            LayerOptions = LayerOptions1.None,
        };
    }

    public void PopLayer() => Pop();

    /// <summary>Clips what follows to a rounded rectangle (anti-aliased), until <see cref="PopLayer"/>.</summary>
    public void PushRoundClip(float x, float y, float w, float h, float r)
    {
        ulong key = H(93).P(x).P(y).P(w).P(h).P(r).V;
        if (Probing) { PushLayer(1, null, Area.Of(x, y, w, h), key); return; }
        var geo = Factory.CreateRoundedRectangleGeometry(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r));
        PushLayer(1, geo, Area.Of(x, y, w, h), key, geo);
    }

    // ================================================================ Bloub sprites
    // Each Bloub drawn in a frame gets a sprite: a bitmap holding just him, rendered (between BeginDraw and
    // the first pass, when no clip or layer is pushed) only when he changed, and otherwise copied pixel for
    // pixel. A spinner orbiting an agent then redraws only the dots, not the agent under them. The sprite
    // keeps his exact sub-pixel position, so the copy matches drawing him directly.

    sealed class Sprite : IDisposable
    {
        public ID2D1Bitmap1? Bitmap;
        public int W, H;           // bitmap size, device pixels
        public ulong Hash;         // what it holds (0: nothing)
        public Vector2 Origin;     // top-left, DIPs (whole device pixels)
        public void Dispose() { Bitmap?.Dispose(); Bitmap = null; Hash = 0; }
    }

    record struct BloubCall(Mascot.BloubRenderer R, Mascot.BloubFrame F, Vector2 Center, float Radius, Color4 Color,
        Color4? ShadeBottom, float ShadeHighlight, ulong Hash, Area Bounds, bool Cacheable);

    readonly List<BloubCall> _bloubs = new();
    readonly List<Sprite> _sprites = new();
    int _bloubAt;

    /// <summary>Bloub (or an agent), drawn by <paramref name="r"/>; the probe hashes the whole frame.</summary>
    public void Bloub(Mascot.BloubRenderer r, Mascot.BloubFrame f, Vector2 center, float radius, Color4 color, float opacity)
    {
        if (opacity <= 0.002f || radius <= 0) return;
        float ext = radius * 2.3f + 4;
        int at = _bloubAt++;
        if (Probing)
        {
            ulong look = Look(at, r, f, center, radius, color);
            Prim(center.X - ext, center.Y - ext, ext * 2, ext * 2, FrameHash.Mix(look, new FrameHash(71).A(opacity).V));
            _bloubs.Add(new(r, f, center, radius, color, r.ShadeBottom, r.ShadeHighlight, look,
                Area.Of(center.X - ext, center.Y - ext, ext * 2, ext * 2), _xf.IsIdentity));
            return;
        }
        if (!Prim(center.X - ext, center.Y - ext, ext * 2, ext * 2, 0)) return;
        if (at < _sprites.Count && at < _bloubs.Count && _sprites[at] is { Bitmap: { } bmp } sp && sp.Hash == _bloubs[at].Hash && _bloubs[at].Cacheable && _xf.IsIdentity)
        {
            float sc = _gpu.Scale;
            Ctx.DrawBitmap(bmp, new Rect(sp.Origin.X, sp.Origin.Y, sp.W / sc, sp.H / sc), Math.Clamp(opacity, 0, 1),
                Vortice.Direct2D1.BitmapInterpolationMode.NearestNeighbor, new Rect(0, 0, sp.W, sp.H));
            return;
        }
        r.Draw(Ctx, Factory, f, center, radius, color, opacity);
    }

    // A Bloub's outline is 64 points: hashing them on a grid, some point crosses a grid line nearly every
    // frame even when the whole shape drifts by a thousandth of a pixel. Instead each Bloub keeps the shape
    // it was last drawn with, and only counts as changed once a point has moved more than this from it.
    const float Tolerance = 1 / 16f; // DIPs: the same precision as every other primitive
    sealed class Drawn { public ulong Style, Look; public readonly List<Vector2> Pts = new(); }
    readonly List<Drawn> _drawn = new();
    readonly List<Vector2> _pts = new();

    ulong Look(int at, Mascot.BloubRenderer r, Mascot.BloubFrame f, Vector2 center, float radius, Color4 color)
    {
        while (_drawn.Count <= at) _drawn.Add(new Drawn());
        var d = _drawn[at];
        ulong style = r.StyleHash(f, radius, color);
        r.Outline(f, center, radius, _pts);
        if (d.Look != 0 && d.Style == style && d.Pts.Count == _pts.Count)
        {
            float t2 = Tolerance * Tolerance;
            bool same = true;
            for (int i = 0; i < _pts.Count && same; i++) same = Vector2.DistanceSquared(d.Pts[i], _pts[i]) <= t2;
            if (same) return d.Look;
        }
        var h = new FrameHash(style);
        foreach (var p in _pts) h = h.P(p.X).P(p.Y);
        d.Style = style; d.Look = h.V == 0 ? 1 : h.V;
        d.Pts.Clear(); d.Pts.AddRange(_pts);
        return d.Look;
    }

    /// <summary>
    /// Renders the sprites of the Bloubs that changed and touch the areas about to be redrawn. Call after
    /// BeginDraw and before any pass (nothing may be pushed while the target is switched).
    /// </summary>
    public void PrepareSprites(List<Area> dirty, ID2D1Image frameTarget)
    {
        float sc = _gpu.Scale;
        while (_sprites.Count < _bloubs.Count) _sprites.Add(new Sprite());
        for (int i = 0; i < _bloubs.Count; i++)
        {
            var b = _bloubs[i];
            var sp = _sprites[i];
            if (!b.Cacheable || sp.Hash == b.Hash) continue;
            bool needed = false;
            foreach (var d in dirty) if (d.Overlaps(b.Bounds)) { needed = true; break; }
            if (!needed) continue;

            int x0 = (int)MathF.Floor(b.Bounds.L * sc) - 1, y0 = (int)MathF.Floor(b.Bounds.T * sc) - 1;
            int w = (int)MathF.Ceiling(b.Bounds.R * sc) + 1 - x0, h = (int)MathF.Ceiling(b.Bounds.B * sc) + 1 - y0;
            if (sp.Bitmap == null || sp.W < w || sp.H < h)
            {
                sp.Bitmap?.Dispose();
                sp.W = Math.Max(w, sp.W); sp.H = Math.Max(h, sp.H);
                sp.Bitmap = Ctx.CreateBitmap(new SizeI(sp.W, sp.H), 0, 0, new BitmapProperties1(
                    new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
                    96f * sc, 96f * sc, BitmapOptions.Target));
            }
            sp.Origin = new Vector2(x0 / sc, y0 / sc);
            Ctx.Target = sp.Bitmap;
            Ctx.Transform = Matrix3x2.Identity;
            Ctx.Clear(new Color4(0, 0, 0, 0));
            var (bottom, hi) = (b.R.ShadeBottom, b.R.ShadeHighlight);
            b.R.ShadeBottom = b.ShadeBottom; b.R.ShadeHighlight = b.ShadeHighlight;
            b.R.Draw(Ctx, Factory, b.F, b.Center - sp.Origin, b.Radius, b.Color, 1);
            b.R.ShadeBottom = bottom; b.R.ShadeHighlight = hi;
            sp.Hash = b.Hash;
        }
        Ctx.Target = frameTarget;
        // forget sprites of Bloubs no longer drawn
        for (int i = _bloubs.Count; i < _sprites.Count; i++) _sprites[i].Dispose();
    }

    /// <summary>A rounded rectangle filled with a linear gradient from (a, c0) to (b, c1).</summary>
    public void GradientRound(float x, float y, float w, float h, float r, Vector2 a, Vector2 b, Color4 c0, Color4 c1)
    {
        if (!Prim(x, y, w, h, Probing ? H(14).P(x).P(y).P(w).P(h).P(r).P(a.X).P(a.Y).P(b.X).P(b.Y).C(c0).C(c1).V : 0)) return;
        using var g = Gradient(a, b, c0, c1);
        Ctx.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), g);
    }

    public void DashedRound(float x, float y, float w, float h, float r, float width, Color4 c)
    {
        if (!Prim(x, y, w, h, Probing ? H(1).P(x).P(y).P(w).P(h).P(r).P(width).C(c).V : 0)) return;
        Ctx.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c), width, _dash);
    }

    public void StrokeRound(float x, float y, float w, float h, float r, float width, Color4 c)
    {
        if (!Prim(x, y, w, h, Probing ? H(2).P(x).P(y).P(w).P(h).P(r).P(width).C(c).V : 0)) return;
        Ctx.DrawRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c), width);
    }

    public void StrokeCircle(float cx, float cy, float r, float width, Color4 c)
    {
        if (!Prim(cx - r - width, cy - r - width, (r + width) * 2, (r + width) * 2, Probing ? H(3).P(cx).P(cy).P(r).P(width).C(c).V : 0)) return;
        Ctx.DrawEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c), width);
    }

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
        float tw = Measure(text, size, weight);
        if (align != 0f) x -= tw * align;
        if (!Prim(x, y, tw + 2, size * 1.6f, Probing ? H(4).S(text).F(x).F(y).P(size).I((int)weight).C(color).V : 0)) return;
        Ctx.DrawText(text, Format(size, weight), new Rect(x, y, maxWidth, size * 1.6f), Brush(color),
            DrawTextOptions.Clip | DrawTextOptions.EnableColorFont);
    }

    public void Round(float x, float y, float w, float h, float r, Color4 c)
    {
        if (!Prim(x, y, w, h, Probing ? H(5).P(x).P(y).P(w).P(h).P(r).C(c).V : 0)) return;
        Ctx.FillRoundedRectangle(new RoundedRectangle(new System.Drawing.RectangleF(x, y, w, h), r, r), Brush(c));
    }

    public void Circle(float cx, float cy, float r, Color4 c)
    {
        if (!Prim(cx - r, cy - r, r * 2, r * 2, Probing ? H(6).P(cx).P(cy).P(r).C(c).V : 0)) return;
        Ctx.FillEllipse(new Ellipse(new Vector2(cx, cy), r, r), Brush(c));
    }

    public void Line(float x1, float y1, float x2, float y2, float width, Color4 c)
    {
        float hw = width / 2 + 1;
        if (!Prim(MathF.Min(x1, x2) - hw, MathF.Min(y1, y2) - hw, MathF.Abs(x2 - x1) + hw * 2, MathF.Abs(y2 - y1) + hw * 2,
                Probing ? H(7).P(x1).P(y1).P(x2).P(y2).P(width).C(c).V : 0)) return;
        Ctx.DrawLine(new Vector2(x1, y1), new Vector2(x2, y2), Brush(c), width, _round);
    }

    /// <summary>Progress ring starting at 12 o'clock, clockwise.</summary>
    public void Ring(float cx, float cy, float r, float width, float progress, Color4 track, Color4 fill)
    {
        float e = r + width;
        if (!Prim(cx - e, cy - e, e * 2, e * 2, Probing ? H(8).P(cx).P(cy).P(r).P(width).I((long)MathF.Round(Math.Clamp(progress, 0, 1) * 262144)).C(track).C(fill).V : 0)) return;
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
        float l = float.MaxValue, t = float.MaxValue, rr = float.MinValue, b = float.MinValue;
        var ph = H(9).C(c);
        foreach (var p in pts) { l = MathF.Min(l, p.X); t = MathF.Min(t, p.Y); rr = MathF.Max(rr, p.X); b = MathF.Max(b, p.Y); if (Probing) ph = ph.P(p.X).P(p.Y); }
        if (!Prim(l, t, rr - l, b - t, ph.V)) return;
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
        if (!Prim(cx - size, cy - size, size * 2, size * 2, Probing ? H(10).I(glyph).F(cx).F(cy).P(size).C(color).V : 0)) return;
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
        float h = l.Metrics.Height;
        if (!Prim(x, y, width + 2, h + 2, Probing ? H(11).S(text).F(x).F(y).P(width).P(size).I((int)weight).C(color).V : 0)) return h;
        Ctx.DrawTextLayout(new Vector2(x, y), l, Brush(color), DrawTextOptions.EnableColorFont);
        return h;
    }

    ID2D1LinearGradientBrush? _shimmer;
    readonly Dictionary<uint, ID2D1RadialGradientBrush> _glows = new();

    /// <summary>Text with a bright band sweeping across it (the "working…" shimmer). phase 0..1 = band position.</summary>
    public void ShimmerText(string text, float x, float y, float size, float alpha, float phase,
        FontWeight weight = FontWeight.Regular, float align = 0f)
    {
        float tw = Measure(text, size, weight);
        if (align != 0f) x -= tw * align;
        if (!Prim(x, y, tw + 2, size * 1.6f, Probing ? H(12).S(text).F(x).F(y).P(size).A(alpha).A(phase).I((int)weight).V : 0)) return;
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
        if (!Prim(cx - rx, cy - ry, rx * 2, ry * 2, Probing ? H(13).P(cx).P(cy).P(rx).P(ry).I(rgb).A(alpha).V : 0)) return;
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
        ResetState();
        foreach (var sp in _sprites) sp.Dispose();
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
