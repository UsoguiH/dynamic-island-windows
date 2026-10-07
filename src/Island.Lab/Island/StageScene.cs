using System.Numerics;
using Island.Lab.Mascot;
using Island.Lab.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Island.Lab.Island;

/// <summary>
/// A full-screen, click-through moment where Bloub leaves the island (the intro, a "done" celebration…).
/// Provides the body with squash &amp; stretch and motion blur, a soft contact shadow, confetti / dust
/// particles, ripples and speech bubbles. Subclasses script the beats in <see cref="Update"/>.
/// </summary>
public abstract class StageScene : IDisposable
{
    protected readonly Gpu Gpu;
    protected readonly Canvas C;
    protected readonly ID2D1DeviceContext Ctx;
    protected readonly BloubRenderer Body = new() { PaperColor = new Color4(0.04f, 0.04f, 0.055f, 1), ShadeBottom = new Color4(0.5f, 0.5f, 0.53f, 1) };
    /// <summary>Bloub's body colour on this stage.</summary>
    protected Color4 Ink = Canvas.Rgba(0xF4F4F8);
    protected readonly BloubEngine E = new(100, BloubState.Idle, BloubShapeId.Cercle, BloubExpressionId.Neutre);
    protected readonly BloubCursorTracker Tracker = new();
    readonly DirectionalBlur _motion;
    readonly ID2D1RadialGradientBrush _shadow;
    protected readonly float W, H;

    protected double T;
    readonly HashSet<double> _fired = new();
    readonly Random _rng = new();

    /// <summary>Where Bloub lives in the island (stage DIPs) and his radius there; re-read every frame.</summary>
    public Func<(Vector2 pos, float radius)> Target = () => (Vector2.Zero, 11);
    /// <summary>Set when Bloub is back in the island: the host closes the stage.</summary>
    public bool Landed { get; protected set; }

    protected StageScene(Gpu gpu)
    {
        Gpu = gpu;
        Ctx = gpu.D2D;
        C = new Canvas(gpu);
        W = gpu.Width / gpu.Scale;
        H = gpu.Height / gpu.Scale;
        _motion = new DirectionalBlur(Ctx) { BorderMode = BorderMode.Soft, Optimization = DirectionalBlurOptimization.Balanced };
        using var stops = Ctx.CreateGradientStopCollection([new GradientStop(0, new Color4(0, 0, 0, 0.42f)), new GradientStop(1, new Color4(0, 0, 0, 0))]);
        _shadow = Ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
    }

    public void Frame(float dt, Vector2 cursor)
    {
        T += dt;
        Update(dt, cursor);
        StepParticles(dt);
        Gpu.BeginFrame();
        Ctx.BeginDraw();
        Ctx.Transform = Matrix3x2.Identity;
        Ctx.Clear(new Color4(0, 0, 0, 0));
        Draw();
        var hr = Ctx.EndDraw();
        if (hr.Failure) Diag.Log($"stage EndDraw failed 0x{hr.Code:X8}");
        Gpu.Present(0);
    }

    protected abstract void Update(float dt, Vector2 cursor);
    protected abstract void Draw();

    /// <summary>Runs <paramref name="a"/> once when the clock passes <paramref name="when"/>.</summary>
    protected void Beat(double when, Action a)
    {
        if (T >= when && _fired.Add(when)) a();
    }

    // ---------------------------------------------------------------- Bloub

    /// <summary>
    /// Draws Bloub at <paramref name="c"/>. <paramref name="squash"/> &lt; 1 flattens him onto his bottom
    /// (impact / crouch), &gt; 1 stretches him tall; fast motion stretches him along <paramref name="vel"/> and blurs.
    /// </summary>
    protected void DrawBloub(Vector2 c, float r, Vector2 vel, float squash = 1, float opacity = 1)
    {
        if (r < 0.5f || opacity < 0.01f) return;
        float speed = vel.Length();
        var xf = Matrix3x2.Identity;
        if (MathF.Abs(squash - 1) > 0.004f)
        {
            float sy = squash, sx = 1 + (1 - squash) * 0.8f;
            var foot = c + new Vector2(0, r);
            xf *= Matrix3x2.CreateTranslation(-foot) * Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(foot);
        }
        float stretch = 1 + MathF.Min(0.24f, speed * 0.00011f);
        if (stretch > 1.005f)
        {
            float ang = MathF.Atan2(vel.Y, vel.X);
            xf *= Matrix3x2.CreateTranslation(-c) * Matrix3x2.CreateRotation(-ang) * Matrix3x2.CreateScale(stretch, 1 / stretch)
                * Matrix3x2.CreateRotation(ang) * Matrix3x2.CreateTranslation(c);
        }
        var frame = E.Sample(T);
        float blur = MathF.Min(26, speed * 0.007f);
        if (blur > 0.6f)
        {
            using var list = Ctx.CreateCommandList();
            Ctx.Target = list;
            Ctx.Transform = xf;
            Body.Draw(Ctx, Gpu.D2DFactory, frame, c, r, Ink, opacity);
            Ctx.Transform = Matrix3x2.Identity;
            Ctx.Target = Gpu.BackBuffer;
            list.Close();
            _motion.SetInput(0, list, true);
            _motion.StandardDeviation = blur * Gpu.Scale;
            _motion.Angle = -MathF.Atan2(vel.Y, vel.X) * 180 / MathF.PI;
            Ctx.DrawImage(_motion.Output);
        }
        else
        {
            Ctx.Transform = xf;
            Body.Draw(Ctx, Gpu.D2DFactory, frame, c, r, Ink, opacity);
            Ctx.Transform = Matrix3x2.Identity;
        }
    }

    /// <summary>Soft contact shadow on a floor at <paramref name="floorY"/>; fades and widens as Bloub rises.</summary>
    protected void DrawShadow(float x, float floorY, float r, float height, float opacity = 1)
    {
        float k = Math.Clamp(1 - height / (r * 4), 0, 1) * opacity;
        if (k < 0.01f) return;
        float rx = r * (1.05f + 0.35f * (1 - k)), ry = r * 0.22f;
        _shadow.Center = new Vector2(x, floorY);
        _shadow.RadiusX = rx; _shadow.RadiusY = ry;
        _shadow.Opacity = k;
        Ctx.FillEllipse(new Ellipse(new Vector2(x, floorY), rx, ry), _shadow);
    }

    // ---------------------------------------------------------------- particles + ripples

    struct Particle
    {
        public Vector2 P, V;
        public float Rot, Spin, Size, Life, Age, Gravity, Drag;
        public Color4 Col;
        public bool Paper; // confetti strip (else a soft dot)
    }

    readonly List<Particle> _parts = new();
    readonly List<(Vector2 p, float age, float r, Color4 col)> _ripples = new();

    float Rand(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>A confetti burst: strips of paper flung up and out, then fluttering down.</summary>
    protected void Confetti(Vector2 at, uint[] colors, int n = 70, float power = 1)
    {
        for (int i = 0; i < n; i++)
        {
            float ang = Rand(-MathF.PI * 0.95f, -MathF.PI * 0.05f);
            float sp = Rand(380, 980) * power;
            _parts.Add(new Particle
            {
                P = at + new Vector2(Rand(-10, 10), Rand(-10, 10)),
                V = new Vector2(MathF.Cos(ang), MathF.Sin(ang)) * sp,
                Rot = Rand(0, 6.28f), Spin = Rand(-14, 14), Size = Rand(5, 10), Life = Rand(1.4f, 2.3f),
                Gravity = 1250, Drag = 1.9f, Col = Canvas.Rgba(colors[_rng.Next(colors.Length)]), Paper = true,
            });
        }
    }

    /// <summary>Little puffs kicked sideways along the floor on impact.</summary>
    protected void Dust(Vector2 at, float r, int n = 16)
    {
        for (int i = 0; i < n; i++)
        {
            float dir = i % 2 == 0 ? -1 : 1;
            _parts.Add(new Particle
            {
                P = at + new Vector2(Rand(-r * 0.6f, r * 0.6f), Rand(-4, 2)),
                V = new Vector2(dir * Rand(120, 420), Rand(-170, -30)),
                Size = Rand(2, 5.5f), Life = Rand(0.35f, 0.7f), Gravity = 380, Drag = 3.2f,
                Col = new Color4(1, 1, 1, Rand(0.35f, 0.7f)),
            });
        }
    }

    /// <summary>Tiny sparkles twinkling around a point.</summary>
    protected void Sparkles(Vector2 at, float radius, Color4 col, int n = 12)
    {
        for (int i = 0; i < n; i++)
        {
            float a = Rand(0, 6.28f), d = Rand(radius * 0.8f, radius * 1.6f);
            _parts.Add(new Particle
            {
                P = at + new Vector2(MathF.Cos(a), MathF.Sin(a)) * d,
                V = new Vector2(MathF.Cos(a), MathF.Sin(a)) * Rand(30, 90),
                Size = Rand(1.5f, 3.2f), Life = Rand(0.5f, 1.1f), Gravity = 0, Drag = 1.5f, Col = col,
            });
        }
    }

    protected void Ripple(Vector2 at, float r, Color4 col) => _ripples.Add((at, 0, r, col));

    void StepParticles(float dt)
    {
        for (int i = _parts.Count - 1; i >= 0; i--)
        {
            var p = _parts[i];
            p.Age += dt;
            if (p.Age >= p.Life) { _parts.RemoveAt(i); continue; }
            p.V *= MathF.Exp(-p.Drag * dt);
            p.V.Y += p.Gravity * dt;
            if (p.Paper) p.V.X += MathF.Sin(p.Age * 9 + p.Rot) * 60 * dt; // flutter
            p.P += p.V * dt;
            p.Rot += p.Spin * dt;
            _parts[i] = p;
        }
        for (int i = _ripples.Count - 1; i >= 0; i--)
        {
            var r = _ripples[i];
            r.age += dt;
            if (r.age > 0.9f) _ripples.RemoveAt(i); else _ripples[i] = r;
        }
    }

    protected void DrawParticles()
    {
        foreach (var (p, age, r, col) in _ripples)
        {
            float k = age / 0.9f, e = 1 - (1 - k) * (1 - k) * (1 - k);
            float rx = r * (1 + e * 2.2f), ry = rx * 0.24f;
            Ctx.DrawEllipse(new Ellipse(p, rx, ry), C.Brush(Canvas.WithA(col, (1 - k) * 0.7f)), 2.4f * (1 - k) + 0.6f);
        }
        foreach (var p in _parts)
        {
            float fade = Math.Clamp((p.Life - p.Age) / 0.35f, 0, 1);
            var col = Canvas.WithA(p.Col, p.Col.A * fade);
            if (p.Paper)
            {
                // a strip of paper turning in 3D: its width flips with a cosine
                float flip = MathF.Cos(p.Age * 11 + p.Rot * 2);
                Ctx.Transform = Matrix3x2.CreateRotation(p.Rot) * Matrix3x2.CreateTranslation(p.P);
                float w = p.Size * MathF.Max(0.15f, MathF.Abs(flip)), h = p.Size * 0.55f;
                Ctx.FillRectangle(new Rect(-w / 2, -h / 2, w, h), C.Brush(flip > 0 ? col : Canvas.WithA(Darker(col), col.A)));
                Ctx.Transform = Matrix3x2.Identity;
            }
            else C.Circle(p.P.X, p.P.Y, p.Size * (0.6f + 0.4f * fade), col);
        }
    }

    static Color4 Darker(Color4 c) => new(c.R * 0.72f, c.G * 0.72f, c.B * 0.72f, c.A);

    // ---------------------------------------------------------------- speech bubble

    /// <summary>A white speech bubble whose tail points at <paramref name="tail"/>; <paramref name="scale"/> pops it in.</summary>
    protected void Bubble(Vector2 tail, string line1, string? line2, float scale, bool right = true)
    {
        if (scale < 0.02f) return;
        float w1 = C.Measure(line1, 19, FontWeight.SemiBold), w2 = line2 != null ? C.Measure(line2, 14) : 0;
        float bw = MathF.Max(w1, w2) + 40, bh = line2 != null ? 70 : 46;
        var anchor = tail + new Vector2(right ? 14 : -14, -18);
        float bx = right ? anchor.X - 22 : anchor.X - bw + 22, by = anchor.Y - bh;
        var pivot = new Vector2(right ? bx + 22 : bx + bw - 22, by + bh);
        Ctx.Transform = Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateScale(scale) * Matrix3x2.CreateTranslation(pivot);
        var a = Math.Clamp(scale * 1.6f, 0, 1);
        var fill = new Color4(0.97f, 0.97f, 0.98f, a);
        C.Round(bx, by, bw, bh, 22, fill);
        // tail
        using (var geo = Gpu.D2DFactory.CreatePathGeometry())
        {
            using (var sink = geo.Open())
            {
                var t0 = new Vector2(pivot.X - 9, by + bh - 1);
                sink.BeginFigure(t0, FigureBegin.Filled);
                sink.AddLine(new Vector2(pivot.X + 9, by + bh - 1));
                sink.AddLine(tail - new Vector2(0, 4) + new Vector2(right ? -6 : 6, 0));
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            Ctx.FillGeometry(geo, C.Brush(fill));
        }
        C.Text(line1, bx + 20, by + 11, 19, new Color4(0.08f, 0.08f, 0.1f, a), FontWeight.SemiBold);
        if (line2 != null) C.Text(line2, bx + 20, by + 40, 14, new Color4(0.35f, 0.35f, 0.4f, a));
        Ctx.Transform = Matrix3x2.Identity;
    }

    public virtual void Dispose()
    {
        Body.Dispose(); _motion.Dispose(); _shadow.Dispose(); C.Dispose();
    }
}
