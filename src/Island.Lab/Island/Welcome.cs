using System.Numerics;
using Island.Lab.Motion;
using Island.Lab.Render;
using Vortice.Direct2D1;
using Vortice.Direct2D1.Effects;
using Vortice.Mathematics;

namespace Island.Lab.Island;

/// <summary>
/// The welcome: the ORB intro (welcome-orb.html, <c>runIntro</c>, itself frame-matched to the reference video),
/// ported value for value, then Bloub flies up into the island.
/// <list type="number">
/// <item>a huge cream orb rises out of a blur from under the screen, its long eyes glancing right, then left;</item>
/// <item>it settles, the stars come out, it winks;</item>
/// <item>it tilts its head and shrinks away while the eyes roll over the top and round the back;</item>
/// <item>two little moons swing round it, and are swallowed: pop, a wobble;</item>
/// <item>one last look to the side… and up into the island.</item>
/// </list>
/// The timeline is the original's: the same tweens, at the same times, with the same GSAP eases
/// (<see cref="Tl"/>). Only the ending differs: the original shows its title, here he goes home.
/// </summary>
public sealed class Welcome : StageScene
{
    // ---- the animated values (the original's `I`), by index
    const int Top = 0, Rad = 1, Blur = 2, Yaw = 3, Pitch = 4, Roll = 5, WinkR = 6, Stars = 7, EyeL = 8, EyeT = 9,
              Sat = 10, SatAng = 11, SatOrb = 12, SatMerge = 13, Count = 14;
    readonly double[] _v = new double[Count];

    sealed class Tween { public double At, Dur, To, From; public int Prop; public Func<double, double> Ease = null!; public bool Started, Done; }
    readonly List<Tween> _tl = new();

    // Asked for faster than the original: the same timeline played at 1.35x, a shorter wait before it starts, and
    // he leaves for the island right after the pop instead of the original's last slow look to the side
    // (6.5 s became 4.1 s). The wobble and the pop are physics: they keep their real speed.
    const double Speed = 1.35, Delay = 0.2, End = 5.3, Pop = 4.88;
    const double D2R = Math.PI / 180;
    const int N = 192;                       // points round the body
    const float Split = 12.6f, EyeW = 0.205f, EyeH = 0.43f; // the "video" face
    static readonly Color4 Creme = Canvas.Rgba(0xF1EFE9), InkEye = Canvas.Rgba(0x0A0A0C), IslandInk = Canvas.Rgba(0xF2F2F7);

    readonly float _ss, _rf, _rcap;
    // The intro fills the screen as in the original (the orb rises from the bottom edge), but the night behind it
    // is only a light veil: your screen stays visible through it instead of going black.
    const float Veil = 0.7f;
    readonly float _cw, _ch;
    readonly Vector2 _card;
    Matrix3x2 _base = Matrix3x2.Identity;
    readonly float[] _ring = new float[N], _ringV = new float[N], _ringA = new float[N];
    static readonly float[] Cos = new float[N], Sin = new float[N];
    float _sc = 1, _scV;                     // the body's scale spring (the pop)
    double _prevYaw; float _yawVel;
    float _starsA;
    bool _popped, _flying;
    double _flyAt;

    readonly GaussianBlur _soft;
    readonly ID2D1LinearGradientBrush _sky;
    readonly ID2D1SolidColorBrush _fill;
    readonly ID2D1StrokeStyle _roundCap;
    readonly Spring _x, _y, _r, _scrim;
    readonly (float x, float y, float r, float b, float sp, float ph, float dp)[] _starField;
    Vector2 _cursor;

    static Welcome()
    {
        for (int i = 0; i < N; i++) { double a = i * Math.Tau / N; Cos[i] = (float)Math.Cos(a); Sin[i] = (float)Math.Sin(a); }
    }

    public Welcome(Gpu gpu) : base(gpu)
    {
        _cw = W; _ch = H; _card = Vector2.Zero;
        float cw = _cw, ch = _ch;
        _ss = MathF.Min(cw, ch * 0.62f); _rf = 0.225f * _ss; _rcap = 0.6f * _ss;
        float rb = MathF.Max(0.94f * _ss, 0.58f * cw);

        _soft = new GaussianBlur(Ctx) { BorderMode = BorderMode.Soft, Optimization = GaussianBlurOptimization.Speed };
        using (var stops = Ctx.CreateGradientStopCollection(
                   [new GradientStop(0, Canvas.Rgba(0x08070C)), new GradientStop(0.45f, Canvas.Rgba(0x121216)), new GradientStop(1, Canvas.Rgba(0x262626))]))
            _sky = Ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(new Vector2(0, 0), new Vector2(0, ch)), stops);
        _fill = Ctx.CreateSolidColorBrush(new Color4(1, 1, 1, 1));
        _roundCap = gpu.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });

        _x = new Spring(W / 2, new SpringSpec(0.55f, 0.82f), 0.05f);
        _y = new Spring(H / 2, new SpringSpec(0.5f, 0.86f), 0.05f);
        _r = new Spring(_rf, new SpringSpec(0.5f, 0.92f), 0.02f);
        _scrim = new Spring(0, new SpringSpec(0.3f, 1f), 0.001f);

        // the stars: little clusters, and a few faint loners
        var rng = new Random(7);
        float Rand(float a, float b) => a + (float)rng.NextDouble() * (b - a);
        var stars = new List<(float, float, float, float, float, float, float)>();
        int clusters = (int)MathF.Round(cw * ch / 70000f) + 6;
        for (int c = 0; c < clusters; c++)
        {
            float cx = Rand(0, 1), cy = Rand(0, 1); int n = 1 + rng.Next(4);
            for (int i = 0; i < n; i++)
                stars.Add((cx + Rand(-0.025f, 0.025f), cy + Rand(-0.02f, 0.02f), Rand(0.5f, 1.4f), Rand(0.35f, 1), Rand(0.6f, 2.4f), Rand(0, MathF.Tau), Rand(0.2f, 1)));
        }
        for (int i = 0; i < 26; i++)
            stars.Add((Rand(0, 1), Rand(0, 1), Rand(0.4f, 1), Rand(0.15f, 0.5f), Rand(0.4f, 1.2f), Rand(0, MathF.Tau), Rand(0.1f, 0.5f)));
        _starField = stars.ToArray();

        // ---- where everything starts
        double Hc = ch; // (the timeline's H is the card's height)
        _v[Top] = Hc * 1.06; _v[Rad] = rb; _v[Blur] = Math.Max(24, rb * 0.06); _v[Yaw] = 0.5; _v[Pitch] = -0.04;
        _v[EyeL] = 1.9; _v[EyeT] = 1.25; _v[SatAng] = 2.7; _v[SatOrb] = 1.3;
        _prevYaw = _v[Yaw];

        // ---- the timeline, as written in the original
        double rf = _rf;
        To(Top, Hc * 0.395, 0.78, Tl.Power3Out, 0.08);
        To(Blur, 0, 0.5, Tl.Power2Out, 0.1);
        To(Yaw, 0.58, 0.5, Tl.SineInOut, 0.1); To(Pitch, 0.02, 0.5, Tl.SineInOut, 0.1);
        To(Yaw, -0.5, 0.5, Tl.Power2InOut, 0.68);
        To(Top, Hc * 0.48, 0.55, Tl.SineInOut, 1.1);
        To(Yaw, 0, 0.45, Tl.BackOut(1.6), 1.35); To(Pitch, 0, 0.45, Tl.BackOut(1.6), 1.35);
        To(Stars, 1, 1.4, Tl.Power1Out, 1.0);
        To(WinkR, 1, 0.06, Tl.Power2In, 1.96);
        To(WinkR, 0, 0.1, Tl.Power2Out, 2.04);
        To(Roll, -7, 0.24, Tl.SineInOut, 2.16); To(Yaw, 0.14, 0.24, Tl.SineInOut, 2.16);
        To(Rad, rf * 0.93, 0.62, Tl.ExpoOut, 2.42); To(Top, Hc * 0.48 - rf * 0.93, 0.62, Tl.ExpoOut, 2.42);
        To(EyeL, 1, 0.5, Tl.Power2Out, 2.42); To(EyeT, 1, 0.5, Tl.Power2Out, 2.42);
        To(Pitch, 2.35, 0.46, Tl.Power2In, 2.38); To(Yaw, 0.5, 0.46, Tl.Power2In, 2.38); To(Roll, 0, 0.46, Tl.Power2In, 2.38);
        To(Pitch, 0.26, 0.6, Tl.Power2Out, 2.95); To(Yaw, -0.36, 0.6, Tl.Power2Out, 2.95);
        To(Sat, 1, 0.4, Tl.BackOut(2), 3.14);
        To(SatAng, 3.75, 1.6, Tl.SineInOut, 3.14); To(SatOrb, 1.95, 1.6, Tl.SineInOut, 3.14);
        To(Rad, rf, 1.5, Tl.SineInOut, 3.2); To(Top, Hc * 0.5 - rf, 1.5, Tl.SineInOut, 3.2);
        To(Yaw, 0, 0.8, Tl.SineInOut, 3.95); To(Pitch, 0, 0.8, Tl.SineInOut, 3.95);
        To(SatMerge, 1, 0.16, Tl.Power2In, 4.72);
        To(Yaw, 0.2, 0.35, Tl.SineInOut, 5.25);
        To(Yaw, 0, 0.45, Tl.SineInOut, 5.75);
        _tl.Sort((a, b) => a.At.CompareTo(b.At)); // (a stable order: List.Sort is not, but equal times never share a property)
    }

    void To(int prop, double to, double dur, Func<double, double> ease, double at) =>
        _tl.Add(new Tween { Prop = prop, To = to, Dur = dur, Ease = ease, At = at });

    /// <summary>Plays the timeline up to <paramref name="t"/>: a tween starts from whatever its value is when its turn comes, and a later one wins.</summary>
    void Play(double t)
    {
        foreach (var w in _tl)
        {
            if (w.Done || t < w.At) continue;
            if (!w.Started) { w.Started = true; w.From = _v[w.Prop]; }
            double k = Math.Clamp((t - w.At) / w.Dur, 0, 1);
            _v[w.Prop] = w.From + (w.To - w.From) * w.Ease(k);
            if (k >= 1) w.Done = true;
        }
    }

    protected override void Update(float dt, Vector2 cursor)
    {
        _cursor = cursor;
        dt = MathF.Min(dt, 1 / 24f);
        double t = (T - Delay) * Speed;

        Beat(0.0, () => _scrim.To(1));
        if (!_flying)
        {
            Play(t);
            if (!_popped && t >= Pop) { _popped = true; _scV += 9; Wobble(0.1f); }
            if (t >= End) TakeOff();
        }
        else
        {
            var (p, r) = Target();
            double f = T - _flyAt;
            if (f > 0.08) { _x.To(p.X); _y.To(p.Y); _r.To(r); }
            _v[Pitch] += (0.2 - _v[Pitch]) * Math.Min(1, dt * 6); // he looks where he is going: up
            if (f > 0.2 && Vector2.Distance(new(_x, _y), p) < 1.5f && MathF.Abs(_r - r) < 0.8f) Landed = true;
            if (f > 1.95) Landed = true;
            _x.Step(dt); _y.Step(dt); _r.Step(dt);
        }
        _scrim.Step(dt);

        // how fast the head turns bends the eyes
        _yawVel = (float)((_v[Yaw] - _prevYaw) / Math.Max(dt, 1e-3)); _prevYaw = _v[Yaw];

        // the body: a ring of springs (the wobble) and a scale spring (the pop), stepped at 120 Hz like the original
        int steps = Math.Max(1, (int)MathF.Ceiling(dt * 120));
        float h = dt / steps;
        for (int s = 0; s < steps; s++)
        {
            _scV += (-360 * (_sc - 1) - 10 * _scV) * h; _sc += _scV * h;
            for (int i = 0; i < N; i++)
            {
                float l = _ring[(i - 1 + N) % N] + _ring[(i + 1) % N] - 2 * _ring[i];
                _ringA[i] = 5200 * l - 260 * _ring[i] - 6.5f * _ringV[i];
            }
            for (int i = 0; i < N; i++) { _ringV[i] += _ringA[i] * h; _ring[i] = Math.Clamp(_ring[i] + _ringV[i] * h, -0.32f, 0.32f); }
        }

        float target = _flying ? 0 : (float)_v[Stars];
        _starsA += (target - _starsA) * MathF.Min(1, dt * 4);
    }

    void Wobble(float amp)
    {
        for (int i = 0; i < N; i++) _ringV[i] += amp * 6 * MathF.Sin(i * MathF.Tau / N * 3 + (float)T * 7);
    }

    void TakeOff()
    {
        _flying = true; _flyAt = T;
        _x.Snap(_card.X + _cw / 2); _y.Snap(_card.Y + (float)(_v[Top] + _v[Rad])); _r.Snap((float)_v[Rad]);
        _y.Velocity = _ch * 0.5f; // a small crouch before the jump
        _scrim.To(0, new SpringSpec(0.5f, 1f), 0.05f);
    }

    protected override void Draw()
    {
        var ctx = Ctx;
        float scrim = Math.Clamp(_scrim, 0, 1);

        float R = _flying ? MathF.Max(0.5f, _r) : (float)_v[Rad];
        float fly = _flying ? Math.Clamp((float)(T - _flyAt) / 0.5f, 0, 1) : 0;
        var body = Lerp(Creme, IslandInk, fly);

        // ---- the night (a light veil over your screen), its stars, and (until he leaves) the orb
        if (scrim > 0.002f || !_flying)
        {
            _base = Matrix3x2.CreateTranslation(_card);
            ctx.Transform = _base;
            _sky.Opacity = scrim * Veil;
            ctx.FillRectangle(new Rect(0, 0, _cw, _ch), _sky);
            if (_starsA > 0.01f)
            {
                float px = (_cursor.X - W / 2) * -0.012f, py = (_cursor.Y - H / 2) * -0.012f;
                foreach (var s in _starField)
                {
                    _fill.Color = new Color4(1, 1, 1, s.b * (0.55f + 0.45f * MathF.Sin((float)T * s.sp + s.ph)) * _starsA * scrim);
                    ctx.FillEllipse(new Ellipse(new Vector2(s.x * _cw + px * s.dp, s.y * _ch + py * s.dp), s.r, s.r), _fill);
                }
            }
            if (!_flying)
            {
                var o = new Vector2(_cw / 2, (float)(_v[Top] + _v[Rad]));
                Satellites(o, R, body, back: true);
                float blur = (float)_v[Blur];
                if (blur > 0.2f)
                {
                    // out of the blur he comes: the whole orb, eyes and all, through one Gaussian
                    using var list = ctx.CreateCommandList();
                    ctx.Target = list;
                    Orb(o, R, body);
                    ctx.Target = Gpu.BackBuffer;
                    list.Close();
                    _soft.SetInput(0, list, true);
                    _soft.StandardDeviation = blur * Gpu.Scale;
                    ctx.Transform = Matrix3x2.Identity; // (the list already holds the card's offset)
                    ctx.DrawImage(_soft.Output);
                    ctx.Transform = _base;
                }
                else Orb(o, R, body);
                Satellites(o, R, body, back: false);
            }
            ctx.Transform = _base = Matrix3x2.Identity;
        }

        // ---- on his way to the island: free of the card, over your screen
        if (_flying) Orb(new Vector2(_x, _y), R, body);
    }

    static Color4 Lerp(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    /// <summary>The body and, clipped to it, the two eyes.</summary>
    void Orb(Vector2 o, float R, Color4 body)
    {
        var ctx = Ctx;
        // everything scales from his foot (the pop)
        var group = Matrix3x2.CreateScale(_sc, _sc, new Vector2(0, R)) * Matrix3x2.CreateTranslation(o) * _base;
        ctx.Transform = group;

        ID2D1Geometry shape = Wobbling ? BodyPath(R) : Gpu.D2DFactory.CreateEllipseGeometry(new Ellipse(Vector2.Zero, R, R));
        _fill.Color = body;
        ctx.FillGeometry(shape, _fill);

        // ---- the face: two long pills on the sphere, turned by the head (yaw, pitch, roll)
        float faceR = MathF.Min(R, _rcap), oy = -(R - faceR);
        double yaw = _v[Yaw], pitch = 32 * D2R + _v[Pitch], roll = _v[Roll] * D2R;
        float curve = Math.Clamp((float)_v[Yaw] * 0.1f + _yawVel * 0.02f, -0.1f, 0.1f) * faceR;

        Span<double> r = [0, 0, 1], i = [1, 0, 0], a = [0, 1, 0];
        Turn(r, i, yaw); Turn(a, r, pitch); Turn(i, a, roll);

        ctx.PushLayer(new LayerParameters1
        {
            ContentBounds = new Rect(-R * 1.4f, -R * 1.4f, R * 2.8f, R * 2.8f), GeometricMask = shape,
            MaskAntialiasMode = AntialiasMode.PerPrimitive, MaskTransform = Matrix3x2.Identity, Opacity = 1, LayerOptions = LayerOptions1.None,
        }, null!);
        for (int k = 0; k < 2; k++)
        {
            double side = Split * (k == 0 ? -1 : 1) * D2R;
            Span<double> p = [r[0], r[1], r[2]], s = [i[0], i[1], i[2]];
            Turn(p, s, side);
            float depth = (float)p[2], alpha = Math.Clamp(depth / 0.12f, 0, 1);
            if (alpha <= 0.002f) continue;
            float lid = k == 0 ? 1 - (float)_v[WinkR] : 1, v = 0.06f + 0.94f * lid;
            float w = EyeW * faceR * (float)_v[EyeT], hgt = EyeH * faceR * (float)_v[EyeL];
            var m = new Matrix3x2((float)s[0], (float)s[1] * v, (float)a[0], (float)a[1] * v, (float)p[0] * faceR, (float)p[1] * faceR + oy);
            ctx.Transform = m * group;
            _fill.Color = Canvas.WithA(InkEye, alpha);
            Pill(w, hgt, curve);
        }
        ctx.PopLayer();
        ctx.Transform = _base;
        shape.Dispose();
    }

    /// <summary>Rotates the pair (e, t) by n: e' = e·cos + t·sin, t' = t·cos − e·sin (the original's <c>rp</c>).</summary>
    static void Turn(Span<double> e, Span<double> t, double n)
    {
        double c = Math.Cos(n), s = Math.Sin(n);
        for (int k = 0; k < 3; k++) { double ek = e[k], tk = t[k]; e[k] = ek * c + tk * s; t[k] = tk * c - ek * s; }
    }

    /// <summary>An eye: the centre line of a pill, stroked round; <paramref name="c"/> bends it (the original's <c>eyeD</c>).</summary>
    void Pill(float w, float h, float c)
    {
        var ctx = Ctx;
        float sw = MathF.Min(w, h);
        bool tall = h >= w;
        float L = MathF.Max(0.3f, tall ? h - w : w - h) / 2;
        Vector2 p0 = tall ? new(0, -L) : new(-L, 0), p1 = tall ? new(0, L) : new(L, 0);
        if (MathF.Abs(c) < 0.05f) { ctx.DrawLine(p0, p1, _fill, sw, _roundCap); return; }
        using var geo = Gpu.D2DFactory.CreatePathGeometry();
        using (var sink = geo.Open())
        {
            sink.BeginFigure(p0, FigureBegin.Hollow);
            sink.AddQuadraticBezier(new QuadraticBezierSegment { Point1 = tall ? new Vector2(2 * c, 0) : new Vector2(0, 2 * c), Point2 = p1 });
            sink.EndFigure(FigureEnd.Open);
            sink.Close();
        }
        ctx.DrawGeometry(geo, _fill, sw, _roundCap);
    }

    bool Wobbling
    {
        get { for (int i = 0; i < N; i += 4) if (MathF.Abs(_ring[i]) > 0.0004f) return true; return false; }
    }

    /// <summary>The wobbling outline: the ring's points joined by Catmull-Rom curves (the original's <c>bodyPath</c>).</summary>
    ID2D1Geometry BodyPath(float R)
    {
        Span<Vector2> P = stackalloc Vector2[N];
        for (int i = 0; i < N; i++) { float rr = R * (1 + _ring[i]); P[i] = new Vector2(Cos[i] * rr, Sin[i] * rr); }
        var geo = Gpu.D2DFactory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(P[0], FigureBegin.Filled);
        for (int i = 0; i < N; i++)
        {
            Vector2 p0 = P[(i - 1 + N) % N], p1 = P[i], p2 = P[(i + 1) % N], p3 = P[(i + 2) % N];
            sink.AddBezier(new BezierSegment { Point1 = p1 + (p2 - p0) / 6, Point2 = p2 - (p3 - p1) / 6, Point3 = p2 });
        }
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    /// <summary>The two moons on their tilted orbit; the far half of the orbit is drawn before the orb, the near half after.</summary>
    void Satellites(Vector2 o, float R0, Color4 body, bool back)
    {
        float k = (float)_v[Sat], m = (float)_v[SatMerge];
        if (_popped || k <= 0.001f) return;
        const float tx = 0.38f, tz = -0.08f, oyS = 0.22f, size = 0.25f;
        float rO = (float)_v[SatOrb], R = R0 * _sc;
        for (int n = 0; n < 2; n++)
        {
            float a = (float)_v[SatAng] + n * MathF.PI;
            float ox = MathF.Cos(a) * rO, oz = MathF.Sin(a) * rO;
            float y = -oz * MathF.Sin(tx), z = oz * MathF.Cos(tx);
            float x = ox * MathF.Cos(tz) - y * MathF.Sin(tz); y = ox * MathF.Sin(tz) + y * MathF.Cos(tz);
            float e = k * (1 - m);
            float r = size * R * (0.85f + 0.15f * z / rO) * MathF.Min(1, k * 1.2f) * (1 - m * 0.55f);
            bool behind = z < 0 && e > 0.3f;
            if (behind != back || r <= 0.2f) continue;
            _fill.Color = body;
            Ctx.FillEllipse(new Ellipse(new Vector2(o.X + x * R * e, o.Y + (y + oyS) * R * e), r, r), _fill);
        }
    }

    public override void Dispose()
    {
        _soft.Dispose(); _sky.Dispose(); _fill.Dispose(); _roundCap.Dispose();
        base.Dispose();
    }
}

/// <summary>The GSAP eases the original timeline names (power1 = quadratic, power2 = cubic, power3 = quartic).</summary>
static class Tl
{
    public static double Power1Out(double t) => 1 - (1 - t) * (1 - t);
    public static double Power2In(double t) => t * t * t;
    public static double Power2Out(double t) => 1 - Math.Pow(1 - t, 3);
    public static double Power2InOut(double t) => t < 0.5 ? 4 * t * t * t : 1 - Math.Pow(-2 * t + 2, 3) / 2;
    public static double Power3Out(double t) => 1 - Math.Pow(1 - t, 4);
    public static double SineInOut(double t) => -(Math.Cos(Math.PI * t) - 1) / 2;
    public static double ExpoOut(double t) => t >= 1 ? 1 : 1 - Math.Pow(2, -10 * t);
    public static Func<double, double> BackOut(double s) => t => (t - 1) * (t - 1) * ((s + 1) * (t - 1) + s) + 1;
}
