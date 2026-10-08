using System.Numerics;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Island.Lab.Island;

namespace Island.Lab.Render;

/// <summary>Draws one frame with D2D only: glow → body (gooey when splitting) → content layers (blur/scale/opacity) → bubble → Bloub.</summary>
public sealed class FrameRenderer : IDisposable
{
    readonly Gpu _gpu;
    readonly Canvas _canvas;
    readonly GaussianBlur _blur;
    readonly Mascot.BloubRenderer _bloub = new();
    readonly GaussianBlur _gooBlur;
    readonly ColorMatrix _gooThreshold;
    readonly Shadow _glow;

    public FrameRenderer(Gpu gpu)
    {
        _gpu = gpu;
        _canvas = new Canvas(gpu);
        _blur = new GaussianBlur(gpu.D2D) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Balanced };
        _gooBlur = new GaussianBlur(gpu.D2D) { StandardDeviation = 4.5f, BorderMode = BorderMode.Soft };
        _gooThreshold = new ColorMatrix(gpu.D2D)
        {
            // alpha' = 18·alpha − 8.5 → hard edge at alpha 0.5, with ~1 px of anti-aliasing
            Matrix = new Vortice.Mathematics.Matrix5x4(1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 1, 0, 0, 0, 0, 18, 0, 0, 0, -8.5f),

        };
        _gooThreshold.SetInputEffect(0, _gooBlur, true);
        _glow = new Shadow(gpu.D2D) { BlurStandardDeviation = 13f };
    }

    /// <summary>Corner exponent: 2 (circular) for capsules, up to ~2.6 for large continuous "squircle" corners.</summary>
    static (float r, float n) Corner(float r, float w, float h)
    {
        float minHalf = MathF.Min(w, h) / 2;
        float ratio = Math.Clamp(r / MathF.Max(minHalf, 0.001f), 0, 1);
        float n = 2f + 0.6f * (1 - ratio);
        return (MathF.Min(r * (1 + 0.35f * (n - 2)), minHalf), n);
    }

    public static (Vector2 c, float r) BubbleGeometry(IslandModel m)
    {
        var (w, _, _) = m.Live;
        float t = MathF.Max(0, m.Bubble.Value);
        float br = IslandModel.BubbleR * MathF.Sqrt(MathF.Min(t, 1.15f));
        float bx = IslandModel.CX + w / 2 - IslandModel.BubbleR + t * (IslandModel.BubbleR * 2 + IslandModel.BubbleGap);
        return (new Vector2(bx, m.Top + IslandModel.BubbleR), br);
    }

    /// <summary>True when the last <see cref="Render"/> drew and presented a frame (false: nothing changed, nothing drawn).</summary>
    public bool Presented { get; private set; }
    // ISLAND_PERF: time spent probing, issuing draws and flushing them to the GPU; areas redrawn
    public static double ProbeMs, DrawMs, FlushMs; public static int Regions;

    /// <summary>The previous frame is unknown (resize, device lost…): draw the next one in full.</summary>
    public void Invalidate() => _canvas.Invalidate();

    readonly List<Vortice.RawRect> _px = new();

    /// <summary>
    /// Draws a frame, but only what changed. A probe pass walks the whole scene without drawing (hit
    /// testing runs there too); if nothing on screen would change, the frame is skipped entirely. Otherwise
    /// each changed area is redrawn by its own pass, clipped to it, and only those rectangles are presented.
    /// </summary>
    public void Render(IslandModel m)
    {
        var surface = Area.Of(0, 0, _gpu.Width / _gpu.Scale, _gpu.Height / _gpu.Scale);
        long t0 = System.Diagnostics.Stopwatch.GetTimestamp();
        _canvas.BeginProbe();
        Scene(m, null);
        var dirty = _canvas.EndProbe(surface);
        long t1 = System.Diagnostics.Stopwatch.GetTimestamp();
        ProbeMs += (t1 - t0) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Presented = dirty != null;
        if (dirty == null)
        {
            // nothing drawn: now and then, check nothing changed that the probe failed to notice
            if (FrameCheck.On && ++_skippedChecks % 8 == 0) Verify(m, surface);
            return;
        }

        var ctx = _gpu.D2D;
        _passTarget = _gpu.BackBuffer;
        _gpu.BeginFrame();
        ctx.BeginDraw();
        ctx.Transform = Matrix3x2.Identity;
        if (_bodyCache == null || _bodyCached != _body.Hash) RenderBody(ctx);
        _canvas.PrepareSprites(dirty, _gpu.BackBuffer);
        float s = _gpu.Scale;
        _px.Clear();
        foreach (var d in dirty)
        {
            // snap the redrawn area to whole device pixels
            var px = new Vortice.RawRect((int)MathF.Floor(d.L * s), (int)MathF.Floor(d.T * s),
                Math.Min(_gpu.Width, (int)MathF.Ceiling(d.R * s)), Math.Min(_gpu.Height, (int)MathF.Ceiling(d.B * s)));
            var area = new Area(px.Left / s, px.Top / s, px.Right / s, px.Bottom / s);
            _px.Add(px);
            ctx.PushAxisAlignedClip(new Vortice.Mathematics.Rect(area.L, area.T, area.R - area.L, area.B - area.T), AntialiasMode.Aliased);
            ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
            _canvas.BeginDraw(area);
            Scene(m, ctx);
            ctx.Transform = Matrix3x2.Identity;
            ctx.PopAxisAlignedClip();
        }
        long t2 = System.Diagnostics.Stopwatch.GetTimestamp();
        var hr = ctx.EndDraw();
        long t3 = System.Diagnostics.Stopwatch.GetTimestamp();
        DrawMs += (t2 - t1) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        FlushMs += (t3 - t2) * 1000.0 / System.Diagnostics.Stopwatch.Frequency;
        Regions += dirty.Count;
        if (hr.Failure) { Diag.Log($"EndDraw failed 0x{hr.Code:X8} mode={m.Mode} layers={m.Layers.Count}"); _canvas.Invalidate(); }
        if (FrameCheck.On && _check != null) _gpu.BeforePresent ??= _check.CompareBuffers;
        _gpu.Present(_px);
        if (FrameCheck.On) Verify(m, surface);
    }

    /// <summary>What the current pass draws into (the frame, or the verify target).</summary>
    ID2D1Image _passTarget = null!;
    FrameCheck? _check;
    int _skippedChecks;
    public static int VerifyFrames => _checkStat?.Frames ?? 0;
    public static int VerifyBad => (_checkStat?.Bad ?? 0) + (_checkStat?.SwapBad ?? 0);
    static FrameCheck? _checkStat;

    /// <summary>ISLAND_VERIFY: redraw the whole frame elsewhere and compare it with the partial redraw.</summary>
    void Verify(IslandModel m, Area surface)
    {
        if (_bodyCache == null) return; // nothing drawn yet
        _check ??= _checkStat = new FrameCheck(_gpu) { Describe = _canvas.Describe };
        var ctx = _gpu.D2D;
        ctx.Target = _passTarget = _check.Target;
        ctx.BeginDraw();
        ctx.Transform = Matrix3x2.Identity;
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        _canvas.BeginDraw(surface);
        Scene(m, ctx);
        ctx.Transform = Matrix3x2.Identity;
        ctx.EndDraw();
        ctx.Target = _passTarget = _gpu.BackBuffer;
        _check.Note = $"mode={m.Mode} layers={m.Layers.Count} presented={Presented} regions={string.Join(" ", _px.Select(r => $"[{r.Left},{r.Top},{r.Right},{r.Bottom}]"))}";
        _check.Compare(_gpu.FrameTexture, m.Clock);
    }

    /// <summary>The whole scene. <paramref name="ctx"/> null = probe pass (record, don't draw).</summary>
    void Scene(IslandModel m, ID2D1DeviceContext? ctx)
    {
        var c = _canvas;
        var (w, h, r) = m.Live;
        // Liquid squash: fast horizontal motion slightly flattens the body.
        float squash = Math.Clamp(MathF.Abs(m.W.Velocity) * 0.0022f, 0, 2.5f);
        float hb = h - squash;
        float top = m.Top, cx = IslandModel.CX;
        m.BeginHits();

        // ---- Body: drawn entirely with D2D (one API owns the swap chain, so nothing can get out of sync).
        float rr = MathF.Min(r, MathF.Min(w, hb) / 2);
        var (bc, br) = BubbleGeometry(m);
        bool bubble = br > 0.3f;
        // Gooey only while the bubble is close to the body (bud-off / merge); crisp edges otherwise.
        bool gooey = bubble && (bc.X - br) - (cx + w / 2) < 14;
        float vis = m.Visibility.Value;
        float glowA = m.GlowA * (1 - 0.4f * m.Retract.Value) * vis;
        bool glow = glowA > 0.004f;
        var bodyBox = Area.Of(cx - w / 2, top, w, hb);
        ulong bodyKey = new FrameHash(60).P(cx).P(w).P(top).P(hb).P(rr).V;
        var bodyHash = new FrameHash(bodyKey).I(bubble ? 1 : 0).P(bc.X).P(bc.Y).P(br).I(gooey ? 1 : 0).A(vis)
            .A(glow ? glowA : 0).A(glow ? m.GlowR : 0).A(glow ? m.GlowG : 0).A(glow ? m.GlowB : 0);
        // the glow's blur and offset, and the goo's blur, reach past the shape
        var bodyBounds = (bubble ? bodyBox.Union(new Area(bc.X - br, bc.Y - br, bc.X + br, bc.Y + br)) : bodyBox)
            .Inflate((glow ? 44 : 0) + (gooey ? 16 : 0));
        if (gooey) c.MarkFull("goo");

        ID2D1Geometry? clip = null;
        try
        {
            if (ctx != null)
                clip = _gpu.D2DFactory.CreateRoundedRectangleGeometry(
                    new RoundedRectangle(new System.Drawing.RectangleF(cx - w / 2, top, w, hb), rr, rr));
            if (ctx == null) _body = new(cx - w / 2, top, w, hb, rr, bubble, bc, br, gooey, vis,
                new Vector4(m.GlowR, m.GlowG, m.GlowB, glow ? glowA : 0), bodyHash.V);
            // the body and its glow come from a cached image, re-rendered only when the shape changes
            if (c.Prim(bodyBounds.L, bodyBounds.T, bodyBounds.R - bodyBounds.L, bodyBounds.B - bodyBounds.T, bodyHash.V) && ctx != null)
                ctx.DrawImage(_bodyCache!);

            foreach (var layer in m.Layers)
            {
                float op = layer.Opacity.Value * m.Visibility.Value;
                if (op < 0.004f) continue;

                var (tw, th, _) = m.Geometry(layer.Mode);
                float x0 = cx - tw / 2, y0 = top;
                var xf = Matrix3x2.CreateScale(layer.Scale.Value, new Vector2(cx, top + MathF.Min(th, hb) / 2));
                float blur = layer.Blur.Value;

                // Only the settled, incoming layer is interactive.
                m.HitsEnabled = !layer.Leaving && layer.Opacity.Value > 0.6f && m.Retract.Value < 0.5f;
                ID2D1CommandList? list = null;
                if (blur > 0.35f)
                {
                    // a blurred layer is drawn whole (the blur spreads it), so the frame is redrawn in full
                    c.MarkFull("blur");
                    if (ctx != null)
                    {
                        list = ctx.CreateCommandList();
                        ctx.Target = list;
                    }
                    c.Transform = xf;
                    Scenes.Draw(c, m, layer.Mode, x0, y0, tw, th);
                    c.Transform = Matrix3x2.Identity;
                    if (ctx != null) { ctx.Target = _passTarget; list!.Close(); }
                }

                op *= 1 - m.Retract.Value;
                if (op < 0.004f) { list?.Dispose(); continue; }
                c.PushLayer(Math.Clamp(op, 0, 1), clip, bodyBox, bodyKey);
                if (blur > 0.35f)
                {
                    if (list != null)
                    {
                        _blur.SetInput(0, list, true);
                        _blur.StandardDeviation = blur * 0.55f;
                        c.Materialize();
                        ctx!.DrawImage(_blur.Output);
                        list.Dispose();
                    }
                }
                else
                {
                    c.Transform = xf;
                    Scenes.Draw(c, m, layer.Mode, x0, y0, tw, th);
                    c.Transform = Matrix3x2.Identity;
                }
                c.PopLayer();
            }
            m.HitsEnabled = false;
            m.EndHits();

            // Retracted: the line glows in the live activity's color (soft white when nothing runs).
            // Fade the line color in only once the shape is actually thin (no gray flash while folding).
            float rt = m.Retract.Value * Math.Clamp((14 - hb) / 8, 0, 1);
            if (rt > 0.01f)
            {
                bool tinted = m.GlowA.Target > 0.01f;
                var lineColor = tinted ? new Vortice.Mathematics.Color4(m.GlowR, m.GlowG, m.GlowB, 0.95f * rt)
                                       : new Vortice.Mathematics.Color4(1, 1, 1, 0.55f * rt);
                float lh = MathF.Max(hb, 2);
                c.Round(cx - w / 2, top, w, lh, lh / 2, lineColor);
            }

            if (br > 4)
                Scenes.BubbleContent(c, m, bc.X, bc.Y, br, Math.Clamp(m.Bubble.Value, 0, 1) * m.Visibility.Value);

            // Bloub: a light body with real eye holes, living at the face anchor (ball radius = 11 × size).
            float bop = m.Face.Opacity.Value * m.Visibility.Value;
            float teamOp = m.Visibility.Value; // each agent fades/shrinks itself when it should not show
            if (teamOp > 0.01f)
            {
                c.PushLayer(1, clip, bodyBox, bodyKey);
                m.Team.DrawBalls(c, m, _bloub, teamOp);
                c.PopLayer();
            }
            if (bop > 0.01f)
            {
                // Bloub lives inside the island: clip it to the body so it never pokes out while morphing.
                c.PushLayer(1, clip, bodyBox, bodyKey);
                // in the tab library Bloub has two moons: the far one passes behind him, the near one in front
                Scenes.LibraryMoons(c, m, front: false, bop);
                c.Bloub(_bloub, m.Bloub.Engine.Sample(m.Clock),
                    new Vector2(m.Face.X, m.Face.Y + m.BloubLift), 11f * m.Face.Size.Value * m.Bloub.Squish.Value, Canvas.Rgba(0xF2F2F7), bop);
                Scenes.LibraryMoons(c, m, front: true, bop);
                c.PopLayer();

                // Poke reaction speech bubble ("hehe", "stop poking me!") under Bloub in the larger states.
                if (m.Bloub.PokeLine is { } line && m.Face.Size.Value > 1.15f)
                {
                    float br2 = 11f * m.Face.Size.Value;
                    float tw = c.Measure(line, 12, Vortice.DirectWrite.FontWeight.SemiBold) + 20;
                    float bx = m.Face.X - tw / 2, by = m.Face.Y + br2 + 10;
                    if (bx < cx - w / 2 + 8) bx = cx - w / 2 + 8;
                    c.Round(bx, by, tw, 24, 12, Canvas.WithA(Canvas.Rgba(0xBF5AF2), bop));
                    c.Text(line, bx + tw / 2, by + 4, 12, Canvas.WhiteA(bop), Vortice.DirectWrite.FontWeight.SemiBold, 0.5f);
                }
            }
        }
        finally { clip?.Dispose(); }
    }

    /// <summary>What the body looked like in the probe pass (the real pass draws it from the cache).</summary>
    record struct BodyShape(float X, float Y, float W, float H, float R, bool Bubble, Vector2 BC, float BR, bool Gooey, float Vis, Vector4 Glow, ulong Hash);
    BodyShape _body;
    ID2D1Bitmap1? _bodyCache;
    ulong _bodyCached;

    /// <summary>Renders the black body (and the bubble), gooey while they bud apart, with its coloured glow, into the cache.</summary>
    void RenderBody(ID2D1DeviceContext ctx)
    {
        var b = _body;
        _bodyCache ??= ctx.CreateBitmap(new Vortice.Mathematics.SizeI(_gpu.Width, _gpu.Height), 0, 0, new BitmapProperties1(
            new Vortice.DCommon.PixelFormat(Vortice.DXGI.Format.B8G8R8A8_UNorm, Vortice.DCommon.AlphaMode.Premultiplied),
            96f * _gpu.Scale, 96f * _gpu.Scale, BitmapOptions.Target));
        _bodyCached = b.Hash;
        using var clip = _gpu.D2DFactory.CreateRoundedRectangleGeometry(
            new RoundedRectangle(new System.Drawing.RectangleF(b.X, b.Y, b.W, b.H), b.R, b.R));
        using var bodyList = ctx.CreateCommandList();
        ctx.Target = bodyList;
        var black = _canvas.Brush(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        ctx.FillGeometry(clip, black);
        if (b.Bubble) ctx.FillEllipse(new Ellipse(b.BC, b.BR, b.BR), black);
        bodyList.Close();
        ctx.Target = _bodyCache;
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));

        ID2D1Image bodyImage = bodyList;
        if (b.Gooey)
        {
            // Metaball trick: blur the union, then threshold alpha → liquid bridge between the blobs.
            _gooBlur.SetInput(0, bodyList, true);
            bodyImage = _gooThreshold.Output;
        }
        if (b.Glow.W > 0)
        {
            _glow.SetInput(0, bodyImage, true);
            _glow.Color = new Vector4(b.Glow.X, b.Glow.Y, b.Glow.Z, MathF.Min(1, b.Glow.W * 1.5f));
            ctx.DrawImage(_glow.Output, new Vector2(0, 6));
        }
        if (b.Vis < 0.999f) ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            MaskTransform = Matrix3x2.Identity, Opacity = b.Vis,
        }, null!);
        ctx.DrawImage(bodyImage);
        if (b.Vis < 0.999f) ctx.PopLayer();
        // let go of the inputs so the command list can be freed
        _gooBlur.SetInput(0, null, true);
        _glow.SetInput(0, null, true);
        ctx.Target = _gpu.BackBuffer;
    }

    public void Dispose() { _bodyCache?.Dispose(); _glow.Dispose(); _gooThreshold.Dispose(); _gooBlur.Dispose(); _bloub.Dispose(); _blur.Dispose(); _canvas.Dispose(); }
}
