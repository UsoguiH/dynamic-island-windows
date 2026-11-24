using System.Numerics;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Island.Lab.Island;

/// <summary>
/// A session finished: he himself comes to your pointer and tells you. Nothing opens in the island.
/// <list type="number">
/// <item>he looks at your pointer first, crouches in his seat and flies out fast on an arc,
/// growing as he comes, in his own solid white, and touches down a little short of your pointer. His trail is Grok
/// Bot's rainbow tail: a bundle of its ribbons side by side along the way he has come, drawn in as he stops;</item>
/// <item>without stopping he springs up into a real backflip that carries him the rest of the way onto his spot: his
/// eyes go right round, up over the top, behind him and back. He lands below your pointer and to its left (never on
/// it), hard, and bounces once;</item>
/// <item>then, with a blink, the film's Finished takes over, exactly as it plays after you give the island a file, from
/// its first frame, in its own paint, at its own speed: its eyes shut and go down, the blue fills him, the green light
/// comes on, its eyes come up from under him and look up, nod and blink;</item>
/// <item>a drop of him buds off on a thread and swells, in one smooth move, into Grok Bot's message bubble: the
/// session's name, strong, with a dot of its colour, and "I'm done" under it. The lines fade in one after the other;</item>
/// <item>he keeps by your pointer a few seconds, watching it, the bubble trailing him; then the words go, the bubble
/// folds back into a drop and flows home into him, he looks home, crouches and flies back on a curve, smaller and
/// the light going out over the last stretch, and the island catches him.</item>
/// </list>
/// Every move is a function of time or a critically damped spring, so it is the same at 60 and 144 Hz.
/// Click-through, never in the way.
/// </summary>
public sealed class DoneFlight : StageScene
{
    readonly OrbFilm _film;
    readonly string _name;
    readonly Spring _squash = new(1, new SpringSpec(0.38f, 0.45f), 0.0005f);

    // where he is (his middle, before a hop lifts him), how fast (smoothed, as drawn), how big
    Vector2 _p, _prevC, _vs, _from, _seat, _fv;
    float _r, _trip = 1, _hopY;
    Vector2 _touch;   // where he touches down and springs into the backflip
    bool _begun, _crouched, _touched, _jumped, _landed, _bounced, _handed, _isFilm;
    double _leapAt = -1, _arrive = -1, _filmAt = -1, _homeAt = -1, _homeLeap = -1;
    int _side = -1, _below = 1;   // (-1: he keeps to the left of your pointer, looking up at it on his right, as in the film)
    float _light = 1;   // the film's light and grey, going out on his way home
    Vector2 _toward = new(0.7f, -0.7f);
    double _saidAt = -1, _closeAt = -1;
    (float W, float H, string Name)? _size;

    // his trail: where he was drawn, lately (Grok Bot's rainbow ribbons are braided along it)
    readonly List<(double T, Vector2 P, float R)> _hist = new();
    readonly Random _rng = new();
    float _hue0 = -1, _ret = 1;
    readonly float[] _hueK = new float[Bundle], _span = new float[Bundle], _drift = new float[Bundle], _lag = new float[Bundle], _len = new float[Bundle], _wid = new float[Bundle];
    readonly double[] _born = new double[Bundle];
    // the bubble: where its tight corner is, its size now (it springs between a drop, the three dots and the message)
    Vector2 _bc, _bcV;
    float _open, _openV, _words = 1;   // _open: 0 a drop, 1 the whole message
    bool _sayRight, _budded, _gulped;

    /// <summary>His radius at your pointer.</summary>
    const float Radius = 48;
    /// <summary>He crouches in his seat this long before he leaps.</summary>
    const float Crouch = 0.11f;
    /// <summary>The backflip: he touches down this far short of his spot, is on the ground this long, then in the air this long, as high as this (in his radii).</summary>
    const float Carry = 80, Kick = 0.08f, Air = 0.5f, Height = 0.95f;
    /// <summary>His eyes go right round, up over the top and behind him, over this long (from the take-off): a real backflip.</summary>
    const float FlipTime = 0.62f;
    /// <summary>From the take-off: the film's Finished takes over, from its first frame (he has landed and bounced, and his eyes are back in front; a blink hands over).</summary>
    const float FilmAfter = 0.74f;
    /// <summary>Seconds from the bubble budding to his setting off home.</summary>
    const float Speaks = 5.4f;
    const string Words = "I'm done";
    static readonly Color4 Milk = new(0.97f, 0.97f, 0.98f, 1);
    /// <summary>His own paint out here: one solid porcelain white, lit from the upper left (top, middle, rim).</summary>
    static readonly Color4[] White = [new(1, 1, 1, 1), new(0.961f, 0.961f, 0.973f, 1), new(0.80f, 0.805f, 0.84f, 1)];
    readonly Color4 _agent;
    readonly bool _hasAgent;
    readonly Color4[] _skin = new Color4[3];

    public DoneFlight(Gpu gpu, OrbFilm film, string name, uint? color = null) : base(gpu)
    {
        _film = film; _name = name;
        _hasAgent = color != null;
        _agent = color is { } c ? Canvas.Rgba(c) : White[1];
        Paint();
    }

    static Color4 Mix(Color4 a, Color4 b, float t) => new(a.R + (b.R - a.R) * t, a.G + (b.G - a.G) * t, a.B + (b.B - a.B) * t, 1);

    /// <summary>His paint out here, for himself and for the film's Bloub alike: his own solid white, whichever agent finished (its colour is only the dot in his bubble).</summary>
    void Paint()
    {
        for (int i = 0; i < 3; i++) _skin[i] = White[i];
        Ink = _skin[1]; Body.ShadeBottom = _skin[2]; Body.ShadeHighlight = 1;
    }

    // ---------------------------------------------------------------- curves

    static float Smooth(float a, float b, float x) { float u = Math.Clamp((x - a) / (b - a), 0, 1); return u * u * (3 - 2 * u); }
    static float CubicOut(float u) { u = Math.Clamp(u, 0, 1); return 1 - (1 - u) * (1 - u) * (1 - u); }
    static float InOutCubic(float u) { u = Math.Clamp(u, 0, 1); return u < 0.5f ? 4 * u * u * u : 1 - MathF.Pow(-2 * u + 2, 3) / 2; }
    static float Smoother(float u) { u = Math.Clamp(u, 0, 1); return u * u * u * (u * (u * 6 - 15) + 10); }
    /// <summary>Back-out: a small part flying to its place passes it by about 10% and settles (animations.md 2.4).</summary>
    static float BackOut(float u) { u = Math.Clamp(u, 0, 1); float v = u - 1; return 1 + 2.70158f * v * v * v + 1.70158f * v * v; }

    static Vector2 Bez(Vector2 a, Vector2 c, Vector2 b, float s) { float o = 1 - s; return o * o * a + 2 * o * s * c + s * s * b; }

    /// <summary>The control point that bends a trip into an arc: it bows above the straight line (like a thrown ball), or out to his side when the trip is straight down or up.</summary>
    Vector2 Arc(Vector2 from, Vector2 to, float amount)
    {
        var d = to - from; float len = d.Length();
        if (len < 30) return (from + to) / 2;
        var n = new Vector2(d.Y, -d.X) / len;
        if (MathF.Abs(n.Y) < 0.25f) n = new Vector2(_side, 0); else if (n.Y > 0) n = -n;
        return (from + to) / 2 + n * MathF.Min(len * amount, 190);
    }

    // ---------------------------------------------------------------- where he goes

    /// <summary>
    /// Below your pointer and to one side of it, never on it: to its left (he looks up at it on his right, the light on
    /// his other shoulder and his words beyond it, as in the film), or to its right when the screen ends.
    /// </summary>
    Vector2 Spot(Vector2 cursor)
    {
        var (bw, _, _) = BubbleSize();
        float ox = Radius + 18, oy = Radius + 22, words = Radius + 14 + bw + 10;
        if (_side < 0 && cursor.X - ox - words < 0) _side = 1;
        else if (_side > 0 && cursor.X - ox - words > 90) _side = -1;
        if (_below > 0 && cursor.Y + oy + Radius + 10 > H) _below = -1;
        else if (_below < 0 && cursor.Y + oy + Radius + 70 < H) _below = 1;
        var at = cursor + new Vector2(_side * ox, _below * oy);
        at.Y = MathF.Max(at.Y, _seat.Y + Radius + 46);   // (out of the island's way)
        return new Vector2(Math.Clamp(at.X, Radius + 8, W - Radius - 8), Math.Clamp(at.Y, Radius + 8, H - Radius - 8));
    }

    Vector2 Centre => _p - new Vector2(0, _hopY);

    /// <summary>A look at something <paramref name="d"/> away from him (his eyes saturate within about two of his widths).</summary>
    static Look LookAt(Vector2 d)
    {
        float nx = Math.Clamp(d.X / 90f, -1, 1), ny = Math.Clamp(d.Y / 80f, -1, 1);
        return new Look(nx * BloubGaze.YawMax, BloubGaze.Pitch - ny * BloubGaze.PitchMax, 1, 0, 0);
    }

    // ---------------------------------------------------------------- the script

    protected override void Update(float dt, Vector2 cursor)
    {
        var (home, r0) = Target();
        if (!_begun)
        {
            _begun = true; _seat = home; _p = home; _prevC = home; _r = r0;
            _squash.To(0.8f, new SpringSpec(0.12f, 1f));               // he gathers himself…
        }
        var spot = Spot(cursor);

        // ---- out to your pointer, fast: an arc down (smooth off the mark, still moving as he touches down) to a point
        // a little short of his spot, beside it on the side he comes from
        if (_leapAt < 0 && T >= Crouch)
        {
            _leapAt = T; _from = _p;
            float dx = spot.X - _from.X;
            _touch = spot + new Vector2(-(MathF.Abs(dx) > 40 ? MathF.Sign(dx) : -_side) * Carry, 0);
            _touch.X = Math.Clamp(_touch.X, Radius + 8, W - Radius - 8);
            _trip = Math.Clamp(0.3f + Vector2.Distance(_from, _touch) / 3600f, 0.34f, 0.52f);
            _arrive = _leapAt + _trip;
            _squash.To(1, new SpringSpec(0.26f, 0.55f));               // …and stretches as he leaps
        }
        // ---- the backflip, the rest of the way: he springs up off the ground at once, his eyes go up over the top
        // and behind him, at the top the light bursts on, then, while his eyes are away, the film's Finished takes over
        // (its eyes come up from under him and roll up toward your pointer); he lands on his spot hard and bounces once.
        double jump = _arrive < 0 ? -1 : _arrive + Kick, land = jump + Air;
        const float H2 = 0.16f, Air2 = Air * 0.41f;    // the bounce: a sixth of the height, its time by the square root (animations.md 2.8)
        if (_homeAt < 0 && _leapAt >= 0 && T < _arrive)
        {
            float u = (float)((T - _leapAt) / _trip);
            _p = Bez(_from, Arc(_from, _touch, 0.3f), _touch, 0.6f * Smoother(u) + 0.4f * u);
            _r = r0 + (Radius - r0) * Smoother(MathF.Min(1, u * 1.15f));
        }
        else if (_homeAt < 0 && _arrive >= 0 && T < land)
        {
            _r = Radius;
            _p = Vector2.Lerp(_touch, spot, (float)Math.Clamp((T - jump) / Air, 0, 1));   // (the flip carries him onto his spot)
        }
        else if (_homeAt < 0 && _arrive >= 0)
        {
            // there: he keeps by your pointer, a critically damped follow
            _r = Radius;
            const float w = 9;
            int n = Math.Max(1, (int)MathF.Ceiling(dt * 120)); float h = dt / n;
            for (int i = 0; i < n; i++) { _fv += (-2 * w * _fv - w * w * (_p - spot)) * h; _p += _fv * h; }
        }
        var me = Centre;
        if (_homeAt < 0) _toward = (cursor - me) / MathF.Max(1, _r);
        if (_arrive >= 0 && _homeAt < 0)
        {
            if (!_touched && T >= _arrive) { _touched = true; _squash.To(0.9f, new SpringSpec(0.06f, 1f)); E.SetExpression(BloubExpressionId.Excite, T); }
            if (!_jumped && T >= jump) { _jumped = true; _squash.To(1, new SpringSpec(0.24f, 0.5f)); }
            if (!_landed && T >= land) { _landed = true; _squash.Velocity -= 3.4f; }
            if (!_bounced && T >= land + Air2) { _bounced = true; _squash.Velocity -= 1.3f; }
            if (!_handed && T >= jump + FilmAfter - 0.1) { _handed = true; E.Blink(T); }   // (his eyes shut as the film's open: the hand-over)
            float u1 = (float)((T - jump) / Air), u2 = (float)((T - land) / Air2);
            _hopY = u1 > 0 && u1 < 1 ? 4 * Height * Radius * u1 * (1 - u1) : u2 > 0 && u2 < 1 ? 4 * H2 * Radius * u2 * (1 - u2) : 0;
            if (!_isFilm && T >= jump + FilmAfter)
            {
                _isFilm = true; _filmAt = T;
                _film.Toward = _toward;
                _film.Turn = false;                                     // (exactly as in the film: only mirrored when your pointer is on his other side)
                _film.Play(OrbFilm.Clip.Done, 0, stay: true);
                _film.Out = true;
            }
        }
        if (_isFilm) _film.Toward = _homeAt < 0 || (home - me).Length() > _r * 1.5f ? (_homeAt < 0 ? _toward : (home - me) / MathF.Max(1, _r)) : _film.Toward;

        // ---- his eyes before the film has them: as he springs up they go right round, up over the top and behind him
        // (the film then brings its own up from under him, so they come all the way round)
        if (!_isFilm)
        {
            if (_arrive >= 0 && T >= jump)
            {
                float k = InOutCubic((float)((T - jump) / FlipTime));
                var at = LookAt(cursor - me);
                E.SetLook(at with { Pitch = at.Pitch - 360 * (1 - k) }, T, 1 / 240.0);   // (rising all the way: up over the top, behind him)
            }
            else if (_arrive >= 0 && T >= _arrive) E.SetLook(LookAt(cursor - me) with { Pitch = -8 }, T, 0.06);   // (touching down: a glance down)
            else E.SetLook(LookAt(cursor - me), T, _leapAt < 0 ? 0.35 : BloubEngine.LookMorph);
        }

        // he speaks once Finished's eyes are in
        if (_saidAt < 0 && _isFilm && (_film.EyesIn || T > _filmAt + 1.2)) _saidAt = T;

        // ---- home: the bubble back in him, he looks home, crouches, and flies back on a curve
        if (_homeAt < 0 && (_saidAt >= 0 && T > _saidAt + Speaks || T > 13)) { _homeAt = T; _hopY = 0; }
        if (_homeAt >= 0)
        {
            if (!_isFilm) E.SetLook(LookAt(home - me), T);
            if (!_crouched && T >= _homeAt + 0.12) { _crouched = true; _squash.To(0.88f, new SpringSpec(0.11f, 1f)); }
            if (_homeLeap < 0 && T >= _homeAt + 0.24)
            {
                _homeLeap = T; _from = _p;
                _trip = Math.Clamp(0.48f + Vector2.Distance(_from, home) / 3200f, 0.52f, 0.76f);
                _squash.To(1, new SpringSpec(0.28f, 0.55f));
            }
            if (_homeLeap >= 0)
            {
                float u = (float)((T - _homeLeap) / _trip), s = Smoother(u);
                _p = Bez(_from, Arc(_from, home, 0.22f), home, s);
                _r = Radius + (r0 - Radius) * MathF.Pow(s, 0.85f);
                _light = 1 - Smooth(0.45f, 0.92f, s);
                _film.Homing = 1 - _light;
                if (u >= 1) { _p = home; _r = r0; _film.Homing = 1; _film.Turn = true; Landed = true; }
            }
        }

        StepBubble(dt);
        _squash.Step(dt);
        // his speed as drawn (the hop included), smoothed over a few frames: for the stretch
        var c = Centre;
        if (dt > 0) _vs = Vector2.Lerp(_vs, (c - _prevC) / dt, 1 - MathF.Exp(-dt / 0.03f));
        _prevC = c;
        _hist.Add((T, c, _r));
        while (_hist.Count > 0 && T - _hist[0].T > TrailSpan + 0.05) _hist.RemoveAt(0);
        // the tail comes out while he flies fast (in 0.35 s) and is drawn back in as he slows (over 0.5 s), as Grok Bot's trails are
        _ret = Math.Clamp(_ret + (_vs.Length() > 420 ? -dt / 0.35f : dt / 0.5f), 0, 1);
        if (_ret >= 1) _hue0 = -1;   // (all drawn in: the next time he flies, a new tail is thrown out)
    }

    // ---------------------------------------------------------------- drawing

    protected override void Draw()
    {
        if (!_begun || _r < 1) return;
        var c = Centre;
        Trail();
        Neck(c, _r);
        float sq = _squash.Value;
        var xf = Matrix3x2.Identity;
        if (MathF.Abs(sq - 1) > 0.003f)
        {
            float sy = sq, sx = 1 + (1 - sq) * 0.8f;
            var foot = c + new Vector2(0, _r);
            xf *= Matrix3x2.CreateTranslation(-foot) * Matrix3x2.CreateScale(sx, sy) * Matrix3x2.CreateTranslation(foot);
        }
        float speed = _vs.Length(), stretch = 1 + MathF.Min(0.13f, speed * 0.00008f);
        if (stretch > 1.004f)
        {
            float ang = MathF.Atan2(_vs.Y, _vs.X);
            xf *= Matrix3x2.CreateTranslation(-c) * Matrix3x2.CreateRotation(-ang) * Matrix3x2.CreateScale(stretch, 1 / stretch)
                * Matrix3x2.CreateRotation(ang) * Matrix3x2.CreateTranslation(c);
        }
        Ctx.Transform = xf;
        if (_isFilm)
        {
            var (_, _, _, _, body) = OrbFilm.OutLight(_toward);
            _film.DrawOut(Ctx, C.Factory, C.Brush(new Color4(0, 0, 0, 1)), c, _r / body, 1, wash: true);   // (exactly as after a file: its own paint, its blue, its light, its eyes)
        }
        else
        {
            Body.Draw(Ctx, Gpu.D2DFactory, E.Sample(T), c, _r, Ink, 1);
        }
        Ctx.Transform = Matrix3x2.Identity;
        Say();
    }

    // ---------------------------------------------------------------- his trail: Grok Bot's rainbow tail

    // Grok Bot's trails (from the avatar's own script on x.ai/news/designing-grok-bot: what it throws out as it spins at
    // its Done), laid along the way he has come: a bundle of four ribbons side by side (a sixth of his radius apart, as
    // its orbits are, with a little gap between), thrown out one after another (55 to 105 ms apart) from under him,
    // each growing in over 0.34 s and fading in over 0.26 s as Grok Bot's do, their heads a little staggered. Each ribbon is full width at its head and half at its
    // tail, round at both ends, smooth along his curve; its colour runs 45 to 95 degrees round the wheel along it
    // (saturation 56%, lightness 56% at the tail to 67% at the head), each its own part of the wheel, drifting as it goes.
    // It comes out as he flies and is drawn back in (shorter and thinner) as he slows.
    const int Bundle = 4;
    const float TrailSpan = 0.26f;   // seconds of his way behind him

    float Rnd(float a, float b) => a + (float)_rng.NextDouble() * (b - a);

    /// <summary>A colour of Grok Bot's rainbow: <paramref name="hue"/> in degrees, at the saturation and lightness it uses.</summary>
    static Color4 Hsl(float hue, float s, float l, float a = 1)
    {
        float h = ((hue % 360) + 360) % 360, c = (1 - MathF.Abs(2 * l - 1)) * s, x = c * (1 - MathF.Abs(h / 60 % 2 - 1)), m = l - c / 2;
        var (r, g, b) = h < 60 ? (c, x, 0f) : h < 120 ? (x, c, 0f) : h < 180 ? (0f, c, x) : h < 240 ? (0f, x, c) : h < 300 ? (x, 0f, c) : (c, 0f, x);
        return new Color4(r + m, g + m, b + m, a);
    }

    static Vector2 CatmullRom(Vector2 p0, Vector2 p1, Vector2 p2, Vector2 p3, float t)
    {
        float t2 = t * t, t3 = t2 * t;
        return 0.5f * (2 * p1 + (-p0 + p2) * t + (2 * p0 - 5 * p1 + 4 * p2 - p3) * t2 + (-p0 + 3 * p1 - 3 * p2 + p3) * t3);
    }

    void Trail()
    {
        float u = _ret, out_ = 1 - u * u * (3 - 2 * u);
        if (out_ < 0.01f || _hist.Count < 3) return;
        if (_hue0 < 0)
        {
            _hue0 = Rnd(0, 360);
            var order = Enumerable.Range(0, Bundle).OrderBy(_ => _rng.Next()).ToArray();
            double at = T;
            foreach (int k in order) { _born[k] = at; at += Rnd(0.055f, 0.105f); }
            for (int k = 0; k < Bundle; k++)
            {
                _hueK[k] = _hue0 + 360f * k / Bundle + Rnd(-14, 14);
                _span[k] = Rnd(45, 95) * (_rng.NextDouble() < 0.5 ? 1 : -1);
                _drift[k] = Rnd(18, 42) * (_rng.NextDouble() < 0.5 ? 1 : -1);
                _lag[k] = Rnd(0, 0.45f); _len[k] = Rnd(0.72f, 1); _wid[k] = Rnd(5.6f, 7.4f) / 114f * 2;
            }
        }
        // his way behind him, newest first, made smooth (a Catmull-Rom curve through where he was drawn, every 3 px)
        var raw = new List<(Vector2 P, float R, double At)>();
        for (int i = _hist.Count - 1; i >= 0; i--)
        {
            var (t, p, r) = _hist[i];
            if (T - t > TrailSpan) break;
            if (raw.Count > 0 && Vector2.Distance(raw[^1].P, p) < 1f) continue;
            raw.Add((p, r, t));
        }
        if (raw.Count < 3) return;
        var path = new List<(Vector2 P, float R, float D, double At)> { (raw[0].P, raw[0].R, 0, raw[0].At) };
        for (int i = 0; i + 1 < raw.Count; i++)
        {
            var p0 = raw[Math.Max(i - 1, 0)].P; var p1 = raw[i].P; var p2 = raw[i + 1].P; var p3 = raw[Math.Min(i + 2, raw.Count - 1)].P;
            int n = Math.Max(1, (int)MathF.Ceiling(Vector2.Distance(p1, p2) / 3));
            for (int k = 1; k <= n; k++)
            {
                var q = CatmullRom(p0, p1, p2, p3, (float)k / n);
                path.Add((q, raw[i].R + (raw[i + 1].R - raw[i].R) * k / n, path[^1].D + Vector2.Distance(path[^1].P, q), raw[i].At + (raw[i + 1].At - raw[i].At) * k / n));
            }
        }
        float total = path[^1].D;
        if (total < 0.5f * _r) return;
        var nrm = new Vector2[path.Count];
        for (int i = 0; i < path.Count; i++)
        {
            var d = path[Math.Min(i + 1, path.Count - 1)].P - path[Math.Max(i - 1, 0)].P;
            float len = d.Length(); nrm[i] = len > 0.01f ? new Vector2(-d.Y, d.X) / len : Vector2.UnitY;
        }
        for (int k = 0; k < Bundle; k++)
        {
            // this ribbon: from a little behind his middle (its head) back along his way, as long as it is out
            float life = (float)(T - _born[k]);
            if (life <= 0) continue;
            float g0 = Math.Min(life / 0.34f, 1), grow = g0 * g0 * (3 - 2 * g0), alpha = Math.Min(1, life / 0.26f);
            float lane = (k - (Bundle - 1) / 2f) * 0.26f;
            float from = _lag[k] * _r, to = from + (total - from) * _len[k] * out_;
            // (it only reaches back as far as he was when it was thrown out)
            float reach = from;
            foreach (var q in path) if (q.At >= _born[k] && q.D <= to) reach = MathF.Max(reach, q.D);
            to = reach;
            var pts = new List<Vector2>(); var off = new List<Vector2>();
            float wHead = _wid[k] * 1.7f * grow * (1 - 0.72f * u * u);   // (in his radius as he is now, as Grok Bot's are)
            for (int i = path.Count - 1; i >= 0; i--)          // (tail first, as Grok Bot keeps them)
            {
                var (pp, pr, pd, _) = path[i];
                if (pd < from || pd > to) continue;
                float along = (to - pd) / MathF.Max(1, to - from);                 // 0 at the tail, 1 at the head
                pts.Add(pp + nrm[i] * lane * _r);
                off.Add(nrm[i] * (wHead * _r * (0.5f + 0.5f * along) / 2));
            }
            int a = pts.Count;
            if (a < 2) continue;
            float hue = _hueK[k] + _drift[k] * life;
            var g = new GradientStop[5];
            for (int q = 0; q < 5; q++) { float f = q / 4f; g[q] = new GradientStop(f, Hsl(hue + f * _span[k], 0.56f, 0.56f + 0.11f * f, alpha)); }
            using var stops = Ctx.CreateGradientStopCollection(g);
            using var brush = Ctx.CreateLinearGradientBrush(new LinearGradientBrushProperties(pts[0], pts[a - 1]), stops);
            using var geo = Gpu.D2DFactory.CreatePathGeometry();
            using (var sink = geo.Open())
            {
                sink.BeginFigure(pts[0] + off[0], FigureBegin.Filled);
                for (int t = 1; t < a; t++) sink.AddLine(pts[t] + off[t]);
                Cap(sink, pts[a - 1], off[a - 1]);                                  // (round at its head)
                for (int t = a - 1; t >= 0; t--) sink.AddLine(pts[t] - off[t]);
                Cap(sink, pts[0], -off[0]);                                         // (and at its tail)
                sink.EndFigure(FigureEnd.Closed);
                sink.Close();
            }
            Ctx.FillGeometry(geo, brush);
        }
    }

    /// <summary>A half circle from <paramref name="p"/> + <paramref name="off"/> round to <paramref name="p"/> - <paramref name="off"/>, bulging out of the ribbon's end.</summary>
    static void Cap(ID2D1GeometrySink sink, Vector2 p, Vector2 off)
    {
        float rr = MathF.Max(off.Length(), 0.2f);
        sink.AddArc(new ArcSegment { Point = p - off, Size = new Size(rr, rr), RotationAngle = 0, SweepDirection = SweepDirection.CounterClockwise, ArcSize = ArcSize.Small });
    }

    // ---------------------------------------------------------------- the message bubble

    // Grok Bot's message bubble, measured from xAI's page: text 14 px on a 20 px line, 12 x 8 px of padding, corners
    // 18 px with the one nearest the sender 6 px, 300 px wide at most, no tail. Here a touch larger (15 px text), since
    // it is read from across the screen.
    const float NameSize = 17, TextSize = 15, Line1 = 24, Line = 21, PadX = 16, PadY = 10, Rad = 19, Tight = 6, MaxW = 320, Bud = 13, Tag = 9, TagGap = 8;
    /// <summary>Seconds from the budding: the thread lets go and the drop starts to swell into the message.</summary>
    const float OpenAt = 0.2f;

    (float W, float H, string Name) BubbleSize() => _size ??= Measure();

    /// <summary>The message: the session's name, strong (bold, larger, full black) with a dot of its colour; "I'm done" under it, quieter.</summary>
    (float W, float H, string Name) Measure()
    {
        float tag = _hasAgent ? Tag + TagGap : 0;
        string name = C.Fit(_name, NameSize, FontWeight.Bold, MaxW - 2 * PadX - tag);
        return (MathF.Max(C.Measure(name, NameSize, FontWeight.Bold) + tag, C.Measure(Words, TextSize)) + 2 * PadX, Line1 + Line + 2 * PadY, name);
    }

    /// <summary>Where the bubble's tight corner rests (beside him, on the side away from your pointer, below the light; the other side when the screen ends), and the point on his rim it buds from.</summary>
    (Vector2 Corner, Vector2 Rim) BubbleSeat()
    {
        var (bw, bh, _) = BubbleSize();
        var c = Centre; float r = _r;
        float rightX = c.X + r + 14, leftX = c.X - r - 14;
        bool roomRight = rightX + bw <= W - 8, roomLeft = leftX - bw >= 8;
        if (!_budded) _sayRight = _side > 0 ? roomRight || !roomLeft : !roomLeft && roomRight;
        else if (_sayRight && !roomRight && roomLeft) _sayRight = false;
        else if (!_sayRight && !roomLeft && roomRight) _sayRight = true;
        var corner = new Vector2(_sayRight ? rightX : leftX, c.Y + 0.22f * r);
        if (corner.Y - bh < 6) corner.Y = bh + 6;
        var d = corner - c; float len = d.Length();
        return (corner, c + (len > 1 ? d / len : new Vector2(0, -1)) * r * 0.78f);
    }

    void StepBubble(float dt)
    {
        if (_saidAt < 0 || T < _saidAt || _gulped) return;
        float since = (float)(T - _saidAt);
        var (corner, rim) = BubbleSeat();
        if (!_budded) { _budded = true; _bc = rim; _bcV = default; }
        if (_closeAt < 0 && T > _saidAt + Speaks - 0.62) _closeAt = T;
        float shut = _closeAt < 0 ? -1 : (float)(T - _closeAt);
        // out: the drop travels to its place on its thread, lets go, and swells into the message (both ways at once,
        // one critically damped move: nothing loads, nothing bounces). Back: the words go, it shrinks to a drop and
        // flows home into him (aiming well inside him, so it is swallowed).
        var bcTo = shut < 0.24f ? corner : Centre + (rim - Centre) * 0.5f;
        float openTo = shut >= 0 ? (shut < 0.1f ? 1 : 0) : since < OpenAt ? 0 : 1;
        _words = shut < 0 ? 1 : MathF.Max(0, 1 - shut / 0.1f);
        float ok = shut < 0 ? 11f : 15f, pk = shut < 0 ? 12f : 16f;
        int n = Math.Max(1, (int)MathF.Ceiling(dt * 120)); float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            _bcV += (-2 * pk * _bcV - pk * pk * (_bc - bcTo)) * h; _bc += _bcV * h;
            _openV += (-2 * ok * _openV - ok * ok * (_open - openTo)) * h; _open += _openV * h;
        }
        _open = Math.Clamp(_open, 0, 1);
        // back home in him: he swallows it with a little squish
        if (shut > 0.3f && Vector2.Distance(Dot().C, Centre) < _r * 0.86f) { _gulped = true; _squash.Velocity -= 1.4f; }
    }

    /// <summary>The bubble's box (left, top, width, height) at its size now.</summary>
    (float X, float Y, float W, float H) Box()
    {
        var (fw, fh, _) = BubbleSize();
        // the width leads a touch and the height follows, so it opens like a breath, not a box being drawn
        float ow = Smoother(MathF.Min(1, _open * 1.08f)), oh = Smoother(_open);
        float w = Bud + (fw - Bud) * ow, h = Bud + (fh - Bud) * oh;
        return (_sayRight ? _bc.X : _bc.X - w, _bc.Y - h, w, h);
    }

    /// <summary>The drop it is while it is small: its middle and radius (it swells in as it leaves him).</summary>
    (Vector2 C, float R) Dot()
    {
        var (x, y, w, h) = Box();
        float since = (float)(T - _saidAt), grow = _closeAt < 0 ? 0.25f + 0.75f * BackOut(since / 0.18f) : 1;
        return (new Vector2(x + w / 2, y + h / 2), MathF.Min(w, h) / 2 * grow);
    }

    bool Showing => _budded && !_gulped;

    /// <summary>The thread between him and the drop as it buds off (and as it flows back): two circles joined smoothly, thinning to nothing as they part.</summary>
    void Neck(Vector2 c, float r)
    {
        if (!Showing) return;
        var (dc, dr) = Dot();
        float d = Vector2.Distance(c, dc);
        float since = (float)(T - _saidAt), shut = _closeAt < 0 ? -1 : (float)(T - _closeAt);
        float v = 0.42f * (shut < 0 ? 1 - Smooth(0.08f, OpenAt, since) : Smooth(0.22f, 0.32f, shut)) * (1 - Smooth(0.02f, 0.2f, _open));
        if (d < r * 0.5f) return;
        using var geo = Metaball(Gpu.D2DFactory, c, r, dc, dr, v);
        if (geo != null) Ctx.FillGeometry(geo, C.Brush(_skin[1]));
    }

    /// <summary>The joining shape of two circles (the paper.js metaball): a smooth bridge from one to the other; <paramref name="v"/> is how wide it holds (0.5 full, 0 a hairline).</summary>
    static ID2D1PathGeometry? Metaball(ID2D1Factory1 fac, Vector2 c1, float r1, Vector2 c2, float r2, float v, float handle = 2.4f)
    {
        float d = Vector2.Distance(c1, c2);
        if (r1 <= 0 || r2 <= 0 || v <= 0.004f || d <= MathF.Abs(r1 - r2) + 0.5f) return null;
        float u1 = 0, u2 = 0;
        if (d < r1 + r2)
        {
            u1 = MathF.Acos(Math.Clamp((r1 * r1 + d * d - r2 * r2) / (2 * r1 * d), -1, 1));
            u2 = MathF.Acos(Math.Clamp((r2 * r2 + d * d - r1 * r1) / (2 * r2 * d), -1, 1));
        }
        float ang = MathF.Atan2(c2.Y - c1.Y, c2.X - c1.X), spread = MathF.Acos(Math.Clamp((r1 - r2) / d, -1, 1));
        float a1 = ang + u1 + (spread - u1) * v, a2 = ang - u1 - (spread - u1) * v;
        float a3 = ang + MathF.PI - u2 - (MathF.PI - u2 - spread) * v, a4 = ang - MathF.PI + u2 + (MathF.PI - u2 - spread) * v;
        static Vector2 P(Vector2 c, float a, float r) => c + new Vector2(MathF.Cos(a), MathF.Sin(a)) * r;
        Vector2 p1 = P(c1, a1, r1), p2 = P(c1, a2, r1), p3 = P(c2, a3, r2), p4 = P(c2, a4, r2);
        float total = r1 + r2, k = MathF.Min(v * handle, Vector2.Distance(p1, p3) / total) * MathF.Min(1, d * 2 / total);
        Vector2 h1 = P(p1, a1 - MathF.PI / 2, r1 * k), h2 = P(p2, a2 + MathF.PI / 2, r1 * k), h3 = P(p3, a3 + MathF.PI / 2, r2 * k), h4 = P(p4, a4 - MathF.PI / 2, r2 * k);
        var geo = fac.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(p1, FigureBegin.Filled);
        sink.AddBezier(new BezierSegment { Point1 = h1, Point2 = h3, Point3 = p3 });
        sink.AddLine(p4);
        sink.AddBezier(new BezierSegment { Point1 = h4, Point2 = h2, Point3 = p2 });
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }

    void Say()
    {
        if (!Showing) return;
        var (fw, fh, name) = BubbleSize();
        var (x, y, w, h) = Box();
        // a drop of him, turning to the bubble's white as it swells
        var fill = Mix(_skin[1], Milk, Smooth(0.04f, 0.4f, _open));
        if (_open < 0.01f)
        {
            var (dc, dr) = Dot();
            Ctx.FillEllipse(new Ellipse(dc, dr, dr), C.Brush(fill));
            return;
        }
        float round = MathF.Min(Rad, MathF.Min(w, h) / 2);
        float tight = round + (MathF.Min(Tight, round) - round) * Smooth(0.55f, 1, _open);   // (round while it is small; the corner nearest him tightens as it opens)
        float tl = round, tr = round, br = _sayRight ? round : tight, bl = _sayRight ? tight : round;
        // a soft shadow under it, so it reads on a light wallpaper too (no outline)
        float sa = Smooth(0.2f, 0.8f, _open);
        if (sa > 0.01f)
            for (int i = 4; i >= 1; i--)
                using (var s = Rounded(x - i * 1.6f, y - i * 1.6f + 4, w + i * 3.2f, h + i * 3.2f, tl + i * 1.6f, tr + i * 1.6f, br + i * 1.6f, bl + i * 1.6f))
                    Ctx.FillGeometry(s, C.Brush(new Color4(0, 0, 0, 0.032f * sa)));
        using (var shape = Rounded(x, y, w, h, tl, tr, br, bl))
            Ctx.FillGeometry(shape, C.Brush(fill));

        // the message, line by line, each fading in and rising 4 px, 0.08 s apart, as the bubble finishes opening:
        // the session (its colour and its name, strong), then "I'm done". Clipped to the bubble while it grows.
        float since = (float)(T - _saidAt) - OpenAt;
        var ink = new Color4(0.04f, 0.04f, 0.06f, 1);
        float Line_(int i) => Smoother((since - 0.2f - i * 0.08f) / 0.3f) * _words;
        float a1 = Line_(0), a2 = Line_(1);
        if (a1 < 0.01f) return;
        Ctx.PushAxisAlignedClip(new Rect(x, y, w, h), AntialiasMode.Aliased);
        float lx = x + PadX, ty = y + PadY + (1 - a1) * 4, nx = lx;
        if (_hasAgent) { Ctx.FillEllipse(new Ellipse(new Vector2(lx + Tag / 2, ty + Line1 / 2 + 0.5f), Tag / 2, Tag / 2), C.Brush(Canvas.WithA(_agent, a1))); nx += Tag + TagGap; }
        C.Text(name, nx, ty, NameSize, Canvas.WithA(ink, a1), FontWeight.Bold);
        if (a2 > 0.01f) C.Text(Words, lx, y + PadY + Line1 + (1 - a2) * 4, TextSize, Canvas.WithA(ink, 0.55f * a2));
        Ctx.PopAxisAlignedClip();
    }

    /// <summary>A rectangle with its own radius at each corner (top-left, top-right, bottom-right, bottom-left).</summary>
    ID2D1PathGeometry Rounded(float x, float y, float w, float h, float tl, float tr, float br, float bl)
    {
        const float K = 0.5523f;   // (a quarter circle as one curve)
        var geo = C.Factory.CreatePathGeometry();
        using var sink = geo.Open();
        sink.BeginFigure(new Vector2(x + tl, y), FigureBegin.Filled);
        sink.AddLine(new Vector2(x + w - tr, y));
        sink.AddBezier(new BezierSegment { Point1 = new Vector2(x + w - tr + tr * K, y), Point2 = new Vector2(x + w, y + tr - tr * K), Point3 = new Vector2(x + w, y + tr) });
        sink.AddLine(new Vector2(x + w, y + h - br));
        sink.AddBezier(new BezierSegment { Point1 = new Vector2(x + w, y + h - br + br * K), Point2 = new Vector2(x + w - br + br * K, y + h), Point3 = new Vector2(x + w - br, y + h) });
        sink.AddLine(new Vector2(x + bl, y + h));
        sink.AddBezier(new BezierSegment { Point1 = new Vector2(x + bl - bl * K, y + h), Point2 = new Vector2(x, y + h - bl + bl * K), Point3 = new Vector2(x, y + h - bl) });
        sink.AddLine(new Vector2(x, y + tl));
        sink.AddBezier(new BezierSegment { Point1 = new Vector2(x, y + tl - tl * K), Point2 = new Vector2(x + tl - tl * K, y), Point3 = new Vector2(x + tl, y) });
        sink.EndFigure(FigureEnd.Closed);
        sink.Close();
        return geo;
    }
}
