using System.Numerics;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace Island.Lab.Island;

/// <summary>
/// Full-screen welcome (replica of welcome-orb.mp4, with Bloub as the orb), then Bloub flies into the island:
/// <list type="number">
/// <item>0–0.4 s  night sky fades in (dark gradient + faint twinkling stars);</item>
/// <item>0.2–1.4 s a huge lit sphere rises from the bottom (motion-blurred), eyes glancing up-right → left → centre;</item>
/// <item>~1.55 s a wink;</item>
/// <item>2.05 s  fast zoom-out to a small ball while the eyes spin around the back of the sphere;</item>
/// <item>2.5–3.05 s two satellite balls swing round him in 3D (one short orbit);</item>
/// <item>3.05 s  the satellites are absorbed → pop;</item>
/// <item>3.55 s  he looks right at you and winks;</item>
/// <item>4.2 s   he flies up into the island while the sky fades; the island catches him.</item>
/// </list>
/// Everything is springs + the Bloub engine (a pure function of time), so slow motion is exact.
/// <remarks>Shortened by cutting beats (pauses, a second glance, the idle wait), not by speeding the motion up.</remarks>
/// </summary>
public sealed class Welcome : StageScene
{
    readonly BloubRenderer _bloub = new() { PaperColor = new Color4(0.04f, 0.04f, 0.055f, 1), ShadeBottom = new Color4(0.5f, 0.5f, 0.53f, 1) };
    readonly BloubEngine _e = new(100, BloubState.Idle, BloubShapeId.Cercle, BloubExpressionId.Neutre);
    readonly DirectionalBlur _motion;
    readonly ID2D1LinearGradientBrush _sky;
    readonly ID2D1RadialGradientBrush _moon;
    readonly ID2D1SolidColorBrush _solid;

    readonly Spring _x, _y, _rad, _scrim, _stars, _orbitR, _dotScale;
    readonly (Vector2 p, float r, float a, float speed, float phase)[] _starField;

    bool _flying;

    // Beat times (s): zoom-out, satellites in, satellites absorbed, fly into the island.
    const double Zoom = 2.05, Sat = Zoom + 0.45, Absorb = Zoom + 1.0, WinkAt = Absorb + 0.5, Fly = Absorb + 1.12;

    public Welcome(Gpu gpu) : base(gpu)
    {
        var ctx = Ctx;
        _motion = new DirectionalBlur(ctx) { BorderMode = BorderMode.Soft, Optimization = DirectionalBlurOptimization.Balanced };
        using (var stops = ctx.CreateGradientStopCollection(
                   [new GradientStop(0, Canvas.Rgba(0x0B0B0F)), new GradientStop(0.55f, Canvas.Rgba(0x111114)), new GradientStop(1, Canvas.Rgba(0x232326))]))
            _sky = ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(0, H)), stops);
        using (var stops = ctx.CreateGradientStopCollection(
                   [new GradientStop(0, Canvas.Rgba(0xFFFFFF)), new GradientStop(0.5f, Canvas.Rgba(0xE6E6EA)), new GradientStop(1, Canvas.Rgba(0x9C9CA2))]))
            _moon = ctx.CreateRadialGradientBrush(new RadialGradientBrushProperties(Vector2.Zero, Vector2.Zero, 1, 1), stops);
        _solid = ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));

        float big = BigRadius;
        _x = new Spring(W / 2, new SpringSpec(0.5f, 0.86f), 0.05f);
        _y = new Spring(H + big + 60, new SpringSpec(0.62f, 0.8f), 0.05f);
        _rad = new Spring(big, new SpringSpec(0.42f, 0.86f), 0.02f);
        _scrim = new Spring(0, new SpringSpec(0.38f, 1f), 0.001f);
        _stars = new Spring(0, new SpringSpec(0.9f, 1f), 0.001f);
        _orbitR = new Spring(0, new SpringSpec(0.5f, 0.8f), 0.001f);
        _dotScale = new Spring(0, SpringSpec.Bounce, 0.001f);

        var rng = new Random(7);
        _starField = new (Vector2, float, float, float, float)[110];
        for (int i = 0; i < _starField.Length; i++)
            _starField[i] = (new Vector2((float)rng.NextDouble() * W, (float)rng.NextDouble() * H),
                0.5f + (float)rng.NextDouble() * 0.9f, 0.12f + (float)rng.NextDouble() * 0.4f,
                0.6f + (float)rng.NextDouble() * 2.2f, (float)rng.NextDouble() * 6.28f);

        _e.SetLook(new Look(30, -18, 1, 0, 0), 0, 0.001);
    }

    float BigRadius => H * 0.56f;
    float SmallRadius => H * 0.1f;

    protected override void Update(float dt, Vector2 cursor)
    {
        double t = T;

        // ---- Script (each beat fires once).
        Beat(0.00, () => { _scrim.To(1); _stars.To(1, 0.25f); });
        Beat(0.22, () => _y.To(H * 0.40f + BigRadius));                                            // rise
        Beat(0.88, () => _e.SetLook(new Look(14, 12, 1, 0, 0), t, 0.25));                           // glance up-right
        Beat(1.10, () => _e.SetLook(new Look(-19, 5, 1, 0, 0), t, 0.3));                            // look left
        Beat(1.36, () => { _e.SetLook(null, t, 0.3); _y.To(H * 0.43f + BigRadius, new SpringSpec(0.6f, 1f)); }); // settle
        Beat(1.55, () => _e.SetState(BloubState.Wink, t));                                         // wink
        Beat(1.89, () => _e.SetState(BloubState.Idle, t));
        Beat(Zoom, () =>
        {
            _rad.To(SmallRadius); _y.To(H * 0.5f, new SpringSpec(0.46f, 0.88f));                   // zoom out
            _x.Velocity = -W * 0.35f;                                                               // a little swing left on the way
        });
        Beat(Sat, () => { _dotScale.To(1); _orbitR.To(1); });                                       // satellites appear
        Beat(Sat + 0.1, () => _rad.To(SmallRadius * 1.16f, new SpringSpec(1.4f, 1f)));             // grows slowly
        Beat(Absorb, () => { _orbitR.To(0, new SpringSpec(0.22f, 1f)); _dotScale.To(0, new SpringSpec(0.22f, 1f)); });  // absorbed
        Beat(Absorb + 0.2, () => { _rad.To(SmallRadius * 1.3f, SpringSpec.Bounce); _rad.Velocity += SmallRadius * 4; }); // pop
        Beat(WinkAt, () => _e.SetState(BloubState.Wink, t));                                       // looks at you… and winks
        Beat(WinkAt + 0.55, () => _e.SetState(BloubState.Idle, t));
        Beat(Fly, () =>
        {
            _flying = true;                                                                         // fly into the island
            _scrim.To(0, new SpringSpec(0.5f, 1f), 0.05f);
            _stars.To(0, new SpringSpec(0.35f, 1f));
            _rad.Configure(new SpringSpec(0.5f, 0.92f));
            _x.Configure(new SpringSpec(0.55f, 0.82f));
            _y.Configure(new SpringSpec(0.5f, 0.86f));
            _e.SetState(BloubState.Wide, t);
            _y.Velocity = H * 0.35f; // a small crouch before the jump
        });

        // Spin: the eyes go round the back of the sphere while he zooms out, then reappear up-left.
        if (t >= Zoom && t < Zoom + 1.2)
        {
            double k = BloubMath.Clamp((t - Zoom) / 1.05);
            double e = BloubMath.EaseInOutCubic(k);
            _e.SetLook(new Look(-16, 14, e, 360 * (1 - e), 1 - e), t, 1 / 240.0);
        }

        if (_flying)
        {
            var (p, r) = Target();
            if (t > Fly + 0.08) { _x.To(p.X); _y.To(p.Y); _rad.To(r); }
            if (t > Fly + 0.2 && Vector2.Distance(new(_x, _y), p) < 1.5f && MathF.Abs(_rad - r) < 0.8f) Landed = true;
            if (t > Fly + 1.95) Landed = true;
        }
        else if (t > Zoom + 1.2)
        {
            // Interactive while he waits: he watches your cursor.
            Tracker.Update(_e, t, cursor - new Vector2(_x, _y), new Vector2(W * 0.35f, H * 0.35f));
        }

        foreach (var s in (Spring[])[_x, _y, _rad, _scrim, _stars, _orbitR, _dotScale]) s.Step(dt);
    }

    protected override void Draw()
    {
        var ctx = Ctx;
        double t = T;

        // Sky
        if (_scrim > 0.002f)
        {
            _sky.Opacity = MathF.Min(1, _scrim) * 0.97f;
            ctx.FillRectangle(new Rect(0, 0, W, H), _sky);
        }
        if (_stars > 0.002f)
        {
            foreach (var s in _starField)
            {
                float tw = 0.55f + 0.45f * MathF.Sin((float)t * s.speed + s.phase);
                _solid.Color = new Color4(1, 1, 1, s.a * tw * MathF.Min(1, _stars));
                ctx.FillEllipse(new Ellipse(s.p, s.r, s.r), _solid);
            }
        }

        float rad = MathF.Max(1, _rad);
        var c = new Vector2(_x, _y);
        var vel = new Vector2(_x.Velocity, _y.Velocity);
        float speed = vel.Length();

        // Satellites: two balls on a tilted ring; the back one is drawn before Bloub, the front one after.
        float orbit = MathF.Max(0, _orbitR), ds = MathF.Max(0, _dotScale);
        float theta = (float)(t - Sat) * 2.4f + 2.2f;
        float tilt = 0.55f - 0.42f * Math.Clamp((float)(t - Sat) / 0.6f, 0, 1);
        var dots = new (Vector2 p, float r, float depth)[2];
        for (int i = 0; i < 2; i++)
        {
            float a = theta + i * MathF.PI;
            float depth = MathF.Sin(a);
            var p = c + new Vector2(MathF.Cos(a) * rad * 1.75f, depth * rad * 1.75f * tilt + rad * 0.12f) * orbit;
            dots[i] = (p, rad * 0.17f * ds * (1 + 0.18f * depth), depth);
        }
        if (ds > 0.01f) foreach (var d in dots) if (d.depth < 0) Moon(ctx, d.p, d.r);

        // Bloub: lit body, stretched along fast motion, motion-blurred while rising / flying.
        float stretch = 1 + MathF.Min(0.22f, speed * 0.00009f);
        var xf = Matrix3x2.Identity;
        if (stretch > 1.005f)
        {
            float ang = MathF.Atan2(vel.Y, vel.X);
            xf = Matrix3x2.CreateTranslation(-c) * Matrix3x2.CreateRotation(-ang) * Matrix3x2.CreateScale(stretch, 1 / stretch)
               * Matrix3x2.CreateRotation(ang) * Matrix3x2.CreateTranslation(c);
        }
        float blur = MathF.Min(28, speed * 0.0075f);
        var frame = _e.Sample(t);
        if (blur > 0.6f)
        {
            using var list = ctx.CreateCommandList();
            ctx.Target = list;
            ctx.Transform = xf;
            _bloub.Draw(ctx, Gpu.D2DFactory, frame, c, rad, Canvas.Rgba(0xF4F4F8), 1);
            ctx.Transform = Matrix3x2.Identity;
            ctx.Target = Gpu.BackBuffer;
            list.Close();
            _motion.SetInput(0, list, true);
            _motion.StandardDeviation = blur * Gpu.Scale;
            _motion.Angle = -MathF.Atan2(vel.Y, vel.X) * 180 / MathF.PI;
            ctx.DrawImage(_motion.Output);
        }
        else
        {
            ctx.Transform = xf;
            _bloub.Draw(ctx, Gpu.D2DFactory, frame, c, rad, Canvas.Rgba(0xF4F4F8), 1);
            ctx.Transform = Matrix3x2.Identity;
        }

        if (ds > 0.01f) foreach (var d in dots) if (d.depth >= 0) Moon(ctx, d.p, d.r);
    }

    /// <summary>A small lit sphere (the satellites).</summary>
    void Moon(ID2D1DeviceContext ctx, Vector2 p, float r)
    {
        if (r < 0.3f) return;
        _moon.Center = p + new Vector2(-0.35f, -0.45f) * r;
        _moon.RadiusX = _moon.RadiusY = r * 1.8f;
        ctx.FillEllipse(new Ellipse(p, r, r), _moon);
    }

    public override void Dispose()
    {
        _bloub.Dispose(); _motion.Dispose(); _sky.Dispose(); _moon.Dispose(); _solid.Dispose();
        base.Dispose();
    }
}
