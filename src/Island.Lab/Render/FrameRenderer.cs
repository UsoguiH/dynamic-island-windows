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

    public void Render(IslandModel m)
    {
        var (w, h, r) = m.Live;
        // Liquid squash: fast horizontal motion slightly flattens the body.
        float squash = Math.Clamp(MathF.Abs(m.W.Velocity) * 0.0022f, 0, 2.5f);
        float hb = h - squash;
        float top = m.Top, cx = IslandModel.CX;

        var ctx = _gpu.D2D;
        _gpu.BeginFrame();
        ctx.BeginDraw();
        ctx.Transform = Matrix3x2.Identity;
        ctx.Clear(new Vortice.Mathematics.Color4(0, 0, 0, 0));
        m.BeginHits();

        // ---- Body: drawn entirely with D2D (one API owns the swap chain, so nothing can get out of sync).
        float rr = MathF.Min(r, MathF.Min(w, hb) / 2);
        using var clip = _gpu.D2DFactory.CreateRoundedRectangleGeometry(
            new RoundedRectangle(new System.Drawing.RectangleF(cx - w / 2, top, w, hb), rr, rr));
        var (bc, br) = BubbleGeometry(m);
        bool bubble = br > 0.3f;
        // Gooey only while the bubble is close to the body (bud-off / merge); crisp edges otherwise.
        bool gooey = bubble && (bc.X - br) - (cx + w / 2) < 14;

        using var bodyList = ctx.CreateCommandList();
        ctx.Target = bodyList;
        var black = _canvas.Brush(new Vortice.Mathematics.Color4(0, 0, 0, 1));
        ctx.FillGeometry(clip, black);
        if (bubble) ctx.FillEllipse(new Ellipse(bc, br, br), black);
        ctx.Target = _gpu.BackBuffer;
        bodyList.Close();

        ID2D1Image bodyImage = bodyList;
        if (gooey)
        {
            // Metaball trick: blur the union, then threshold alpha → liquid bridge between the blobs.
            _gooBlur.SetInput(0, bodyList, true);
            bodyImage = _gooThreshold.Output;
        }

        float vis = m.Visibility.Value;
        float glowA = m.GlowA * (1 - 0.4f * m.Retract.Value) * vis;
        if (glowA > 0.004f)
        {
            _glow.SetInput(0, bodyImage, true);
            _glow.Color = new Vector4(m.GlowR, m.GlowG, m.GlowB, MathF.Min(1, glowA * 1.5f));
            ctx.DrawImage(_glow.Output, new Vector2(0, 6));
        }
        if (vis < 0.999f) ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
            MaskTransform = Matrix3x2.Identity, Opacity = vis,
        }, null);
        ctx.DrawImage(bodyImage);
        if (vis < 0.999f) ctx.PopLayer();

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
                list = ctx.CreateCommandList();
                ctx.Target = list;
                ctx.Transform = xf;
                Scenes.Draw(_canvas, m, layer.Mode, x0, y0, tw, th);
                ctx.Transform = Matrix3x2.Identity;
                ctx.Target = _gpu.BackBuffer;
                list.Close();
            }

            op *= 1 - m.Retract.Value;
            if (op < 0.004f) { list?.Dispose(); continue; }
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
                GeometricMask = clip,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = Math.Clamp(op, 0, 1),
                LayerOptions = LayerOptions1.None,
            }, null);

            if (list != null)
            {
                _blur.SetInput(0, list, true);
                _blur.StandardDeviation = blur * 0.55f;
                ctx.DrawImage(_blur.Output);
                list.Dispose();
            }
            else
            {
                ctx.Transform = xf;
                Scenes.Draw(_canvas, m, layer.Mode, x0, y0, tw, th);
                ctx.Transform = Matrix3x2.Identity;
            }
            ctx.PopLayer();
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
            _canvas.Round(cx - w / 2, top, w, lh, lh / 2, lineColor);
        }

        if (br > 4)
            Scenes.BubbleContent(_canvas, m, bc.X, bc.Y, br, Math.Clamp(m.Bubble.Value, 0, 1) * m.Visibility.Value);

        // Bloub: a light body with real eye holes, living at the face anchor (ball radius = 11 × size).
        float bop = m.Face.Opacity.Value * m.Visibility.Value;
        float teamOp = m.Visibility.Value; // each agent fades/shrinks itself when it should not show
        if (teamOp > 0.01f)
        {
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
                GeometricMask = clip,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
            }, null);
            m.Team.DrawBalls(_canvas, m, _bloub, teamOp);
            ctx.PopLayer();
        }
        if (bop > 0.01f)
        {
            // Bloub lives inside the island: clip it to the body so it never pokes out while morphing.
            ctx.PushLayer(new LayerParameters1
            {
                ContentBounds = new Vortice.RawRectF(-1e6f, -1e6f, 1e6f, 1e6f),
                GeometricMask = clip,
                MaskAntialiasMode = AntialiasMode.PerPrimitive,
                MaskTransform = Matrix3x2.Identity,
                Opacity = 1,
            }, null);
            // in the tab library Bloub has two moons: the far one passes behind him, the near one in front
            Scenes.LibraryMoons(_canvas, m, front: false, bop);
            _bloub.Draw(ctx, _gpu.D2DFactory, m.Bloub.Engine.Sample(m.Clock),
                new Vector2(m.Face.X, m.Face.Y + m.BloubLift), 11f * m.Face.Size.Value * m.Bloub.Squish.Value, Canvas.Rgba(0xF2F2F7), bop);
            Scenes.LibraryMoons(_canvas, m, front: true, bop);
            ctx.PopLayer();

            // Poke reaction speech bubble ("hehe", "stop poking me!") under Bloub in the larger states.
            if (m.Bloub.PokeLine is { } line && m.Face.Size.Value > 1.15f)
            {
                float br2 = 11f * m.Face.Size.Value;
                float tw = _canvas.Measure(line, 12, Vortice.DirectWrite.FontWeight.SemiBold) + 20;
                float bx = m.Face.X - tw / 2, by = m.Face.Y + br2 + 10;
                if (bx < cx - w / 2 + 8) bx = cx - w / 2 + 8;
                _canvas.Round(bx, by, tw, 24, 12, Canvas.WithA(Canvas.Rgba(0xBF5AF2), bop));
                _canvas.Text(line, bx + tw / 2, by + 4, 12, Canvas.WhiteA(bop), Vortice.DirectWrite.FontWeight.SemiBold, 0.5f);
            }
        }
        var hr = ctx.EndDraw();
        if (hr.Failure) Diag.Log($"EndDraw failed 0x{hr.Code:X8} mode={m.Mode} layers={m.Layers.Count}");
        _gpu.Present();
    }

    public void Dispose() { _glow.Dispose(); _gooThreshold.Dispose(); _gooBlur.Dispose(); _bloub.Dispose(); _blur.Dispose(); _canvas.Dispose(); }
}
