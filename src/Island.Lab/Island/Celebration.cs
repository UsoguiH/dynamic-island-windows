using System.Numerics;
using Island.Lab.Mascot;
using Island.Lab.Motion;
using Island.Lab.Render;
using Vortice.Direct2D1;
using Vortice.DirectWrite;
using Vortice.Mathematics;

namespace Island.Lab.Island;

/// <summary>
/// A session finished: Bloub comes to tell you "I'm done!".
/// <list type="number">
/// <item>he crouches in the island and leaps out, arcing over to land beside your cursor;</item>
/// <item>a solid green ✓ badge SLAMS onto his top-left edge like a stamp, biting a notch out of the orb; he feels the tap,
/// the ✓ slashes in and punches, and the badge spins a full turn as he hops;</item>
/// <item>he turns to you, winks and says "I'm done!", with the session's name under him; a happy little hop, then he flies home wearing the badge.</item>
/// </list>
/// ~3.8 s, click-through, never in the way.
/// </summary>
public sealed class Celebration : StageScene
{
    enum Phase { Crouch, Fly, Show, Home }

    static readonly Color4 Green = Canvas.Rgba(0x30D158);

    readonly Spring _x = new(0, new SpringSpec(0.6f, 0.84f), 0.05f);
    readonly Spring _y = new(0, new SpringSpec(0.6f, 0.84f), 0.05f);
    readonly Spring _rad = new(0, new SpringSpec(0.5f, 0.8f), 0.02f);
    readonly Spring _squash = new(1, new SpringSpec(0.38f, 0.45f), 0.0005f);
    readonly Spring _hop = new(0, new SpringSpec(0.42f, 0.5f), 0.05f);
    readonly Spring _badge = new(0, new SpringSpec(0.24f, 0.5f), 0.001f);    // the ✓ badge: slams down from big, like a stamp
    readonly Spring _tilt = new(0, new SpringSpec(0.3f, 0.42f), 0.001f);     // …twisting as it hits
    readonly Spring _spin = new(0, new SpringSpec(0.55f, 0.62f), 0.001f);    // …and a full victory spin during the jump
    readonly Spring _tick = new(0, new SpringSpec(0.16f, 1f), 0.001f);       // the ✓ slashing in
    readonly Spring _bubble = new(0, new SpringSpec(0.4f, 0.48f), 0.001f);
    readonly Spring _tag = new(0, new SpringSpec(0.4f, 0.5f), 0.001f);         // the name tag, popping in just after the bubble
    readonly string _name;                                                    // which session finished

    Phase _phase = Phase.Crouch;
    double _ta = double.MaxValue, _th, _stampAt = double.MaxValue, _hitAt = double.MaxValue;
    bool _tracking;
    Vector2 _spot;
    const float R = 52;

    public Celebration(Gpu gpu, string name) : base(gpu)
    {
        _name = name;
        E.SetExpression(BloubExpressionId.Excite, 0);
    }

    /// <summary>Beside your cursor, toward the middle of the screen, with room for the bubble above.</summary>
    Vector2 Spot(Vector2 cursor)
    {
        float dx = cursor.X > W / 2 ? -100 : 100;
        return new Vector2(Math.Clamp(cursor.X + dx, R + 200, W - R - 90), Math.Clamp(cursor.Y - 20, R + 190, H - R - 60));
    }

    protected override void Update(float dt, Vector2 cursor)
    {
        var (home, r0) = Target();

        switch (_phase)
        {
            case Phase.Crouch:
                _x.Snap(home.X); _y.Snap(home.Y); _rad.Snap(r0);
                Beat(0.0, () => _squash.To(0.72f, new SpringSpec(0.13f, 1f)));
                Beat(0.15, () =>
                {
                    _phase = Phase.Fly;                                                     // leap out of the island
                    _spot = Spot(cursor);
                    _x.To(_spot.X); _y.To(_spot.Y); _rad.To(R);
                    _y.Velocity = -500;                                                     // a little hop up first: a real arc
                    _squash.Snap(1.2f); _squash.To(1, new SpringSpec(0.3f, 0.6f));
                    E.SetState(BloubState.Wide, T);
                });
                break;
            case Phase.Fly:
                if (T > 0.55 && Vector2.Distance(new(_x, _y), _spot) < 6 || T > 1.2)
                {
                    _phase = Phase.Show;                                                    // touchdown
                    _ta = T;
                    _squash.Snap(0.78f); _squash.To(1);
                    E.SetState(BloubState.Idle, T);
                    E.SetExpression(BloubExpressionId.Heureux, T);
                }
                break;
            case Phase.Show:
                // Bloub: feels the tap and glances up, turns to you with a hop, "I'm done!", winks
                Beat(_ta + 0.2, () => E.SetLook(new Look(-24, 12, 1, 0, 0), T, 0.18));
                Beat(_ta + 0.33, () => { _squash.Snap(0.88f); _squash.To(1); E.SetExpression(BloubExpressionId.Surpris, T); });   // tap!
                Beat(_ta + 0.8, () => { _tracking = true; E.SetExpression(BloubExpressionId.Heureux, T); _bubble.To(1); _tag.To(1); _hop.Velocity = -430; _squash.Snap(0.84f); _squash.To(1); });
                Beat(_ta + 1.0, () => E.SetState(BloubState.Wink, T));
                Beat(_ta + 2.1, () => E.SetState(BloubState.Idle, T));
                Beat(_ta + 2.45, () => { _bubble.To(0, new SpringSpec(0.22f, 1f)); _tag.To(0, new SpringSpec(0.22f, 1f)); });
                Beat(_ta + 2.6, () => { _tracking = false; _squash.To(0.74f, new SpringSpec(0.13f, 1f)); E.SetLook(new Look(0, 26, 1, 0, 0), T, 0.15); });
                Beat(_ta + 2.75, () =>
                {
                    _phase = Phase.Home;                                                     // fly home, wearing the badge
                    _th = T;
                    _squash.Snap(1.18f); _squash.To(1, new SpringSpec(0.3f, 0.6f));
                    _x.Configure(new SpringSpec(0.5f, 0.86f)); _y.Configure(new SpringSpec(0.45f, 0.9f)); _rad.Configure(new SpringSpec(0.45f, 0.95f));
                    _y.Velocity = -1100;
                    E.SetState(BloubState.Wide, T);
                    E.SetExpression(BloubExpressionId.Excite, T);
                });

                // the ✓ (only the badge moves here): it SLAMS down like a stamp, punches a notch in the orb,
                // the ✓ slashes in and punches, spins a full turn as he hops, and bounces with the wink
                Beat(_ta + 0.22, () => { _stampAt = T; _badge.Snap(2.8f); _badge.To(1); _tilt.Snap(-1.4f); _tilt.To(0); });
                Beat(_ta + 0.335, () => _hitAt = T);                   // (Beat runs one action per time value: keep these off Bloub's beats)
                Beat(_ta + 0.46, () => _tick.To(1));
                Beat(_ta + 0.62, () => { _badge.Velocity += 11; _tilt.Velocity -= 5; });
                Beat(_ta + 0.805, () => { _badge.Velocity += 5; _spin.To(MathF.Tau); });
                Beat(_ta + 1.25, () => { _badge.Velocity += 6; _tilt.Velocity += 4; });
                if (_tracking) Tracker.Update(E, T, cursor - new Vector2(_x, _y), new Vector2(W * 0.3f, H * 0.3f));
                break;
            case Phase.Home:
                _x.To(home.X); _y.To(home.Y); _rad.To(r0);
                if (T > _th + 0.25 && Vector2.Distance(new(_x, _y), home) < 1.5f && MathF.Abs(_rad - r0) < 0.8f) Landed = true;
                if (T > _th + 1.8) Landed = true;
                break;
        }
        foreach (var s in (Spring[])[_x, _y, _rad, _squash, _hop, _badge, _tilt, _spin, _tick, _bubble, _tag]) s.Step(dt);
    }

    Vector2 Center => new(_x, _y - MathF.Max(0, -_hop));

    /// <summary>
    /// Where the badge sits: on his top-left edge, half on the orb (like the agent balls' "done" mark),
    /// following his squash so it rides along with the body.
    /// </summary>
    Vector2 BadgeAt()
    {
        float r = MathF.Max(0, _rad), sq = _squash;
        var c = Center;
        var p = c + new Vector2(-0.707f, -0.707f) * r * 0.97f;
        var foot = c + new Vector2(0, r);
        return foot + (p - foot) * new Vector2(1 + (1 - sq) * 0.8f, sq);
    }

    protected override void Draw()
    {
        float r = MathF.Max(0, _rad);
        var c = Center;
        DrawBloub(c, r, new Vector2(_x.Velocity, _y.Velocity + _hop.Velocity), _squash);
        DrawParticles();
        Notch(r);
        Badge(r);
        NameTag(new Vector2(_x, _y + r + 14), MathF.Max(0, _tag));
        Bubble(c + new Vector2(r * 0.35f, -r * 0.9f), "I'm done!", null, MathF.Max(0, _bubble), right: true);
    }

    /// <summary>The session's name under him, so you know which one finished: white on a solid dark pill.</summary>
    void NameTag(Vector2 top, float k)
    {
        if (k < 0.02f || _name.Length == 0) return;
        float w = MathF.Min(C.Measure(_name, 14, FontWeight.SemiBold), 220) + 26, h = 28;
        var pivot = top + new Vector2(0, h / 2);
        Ctx.Transform = Matrix3x2.CreateTranslation(-pivot) * Matrix3x2.CreateScale(k) * Matrix3x2.CreateTranslation(pivot);
        float a = Math.Clamp(k * 1.6f, 0, 1);
        C.Round(top.X - w / 2, top.Y, w, h, h / 2, new Color4(0.11f, 0.11f, 0.13f, a));
        C.Text(_name, top.X, top.Y + 4.5f, 14, new Color4(1, 1, 1, a), FontWeight.SemiBold, 0.5f, 220);
        Ctx.Transform = Matrix3x2.Identity;
    }

    /// <summary>
    /// The badge "takes space" from the orb: a clean notch is punched out of him around it (a transparent gap,
    /// like the agent balls' badge cut-out). It appears at the moment of impact, overshoots, and heals as he leaves.
    /// </summary>
    void Notch(float r)
    {
        if (T < _hitAt || r < 1) return;
        float fade = Math.Clamp((r - 12) / 14, 0, 1);
        float hit = Math.Clamp((float)(T - _hitAt) / 0.08f, 0, 1);
        float cut = r * 0.27f * Math.Clamp(_badge, 0, 1.3f) * 1.32f * hit * fade;
        if (cut < 0.5f) return;
        var at = BadgeAt();
        Ctx.PrimitiveBlend = PrimitiveBlend.Copy;
        Ctx.FillEllipse(new Ellipse(at, cut, cut), C.Brush(new Color4(0, 0, 0, 0)));
        Ctx.PrimitiveBlend = PrimitiveBlend.SourceOver;
    }

    /// <summary>
    /// The green ✓ badge, like the agent balls' "done" mark: a flat, solid green dot with a white ✓
    /// that draws itself. No glow, no shadow, no gloss.
    /// </summary>
    void Badge(float r)
    {
        float k = MathF.Max(0, _badge);
        if (k < 0.01f || r < 1) return;
        float br = r * 0.27f * k;
        var at = BadgeAt();
        float fade = Math.Clamp((r - 12) / 14, 0, 1);              // melts away as he shrinks back into the island
        fade *= Math.Clamp((float)(T - _stampAt) / 0.06f, 0, 1);   // the stamp fades in as it comes down
        if (fade <= 0) return;

        Ctx.Transform = Matrix3x2.CreateRotation(_tilt + _spin) * Matrix3x2.CreateTranslation(at);
        Ctx.FillEllipse(new Ellipse(Vector2.Zero, br, br), C.Brush(Canvas.WithA(Green, fade)));
        // the ✓, drawn progressively along its two strokes
        float t = Math.Clamp(_tick, 0, 1);
        if (t > 0.01f)
        {
            Vector2 p0 = new Vector2(-0.42f, 0.02f) * br, p1 = new Vector2(-0.12f, 0.32f) * br, p2 = new Vector2(0.44f, -0.3f) * br;
            float l1 = Vector2.Distance(p0, p1), l2 = Vector2.Distance(p1, p2), d = t * (l1 + l2);
            using var style = Gpu.D2DFactory.CreateStrokeStyle(new StrokeStyleProperties { StartCap = CapStyle.Round, EndCap = CapStyle.Round, LineJoin = LineJoin.Round });
            using var path = Gpu.D2DFactory.CreatePathGeometry();
            using (var sink = path.Open())
            {
                sink.BeginFigure(p0, FigureBegin.Hollow);
                sink.AddLine(d <= l1 ? Vector2.Lerp(p0, p1, d / l1) : p1);
                if (d > l1) sink.AddLine(Vector2.Lerp(p1, p2, (d - l1) / l2));
                sink.EndFigure(FigureEnd.Open);
                sink.Close();
            }
            Ctx.DrawGeometry(path, C.Brush(new Color4(1, 1, 1, fade)), br * 0.22f, style);
        }
        Ctx.Transform = Matrix3x2.Identity;
    }
}
