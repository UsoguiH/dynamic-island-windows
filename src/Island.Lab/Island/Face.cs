using System.Numerics;
using Island.Lab.Motion;
using Island.Lab.Render;

namespace Island.Lab.Island;

public enum Expression { Neutral, Wide, Happy, Exclaim, Sleepy }

/// <summary>
/// Prototype of Bloub's face living inside the island: two tall pill eyes (Bloub's 0.186 × 0.412
/// proportions) with blinking, gaze, and a few expressions. The full Bloub engine port replaces this in P-4.
/// </summary>
public sealed class Face
{
    public readonly Spring X = new(400, SpringSpec.Island, 0.05f);
    public readonly Spring Y = new(25, SpringSpec.Island, 0.05f);
    public readonly Spring Size = new(1, SpringSpec.Island, 0.001f);
    public readonly Spring GazeX = new(0, SpringSpec.Gentle, 0.001f);
    public readonly Spring GazeY = new(0, SpringSpec.Gentle, 0.001f);
    public readonly Spring Wide = new(0, SpringSpec.Bounce, 0.001f);
    public readonly Spring Happy = new(0, SpringSpec.Content, 0.001f);
    public readonly Spring Sleepy = new(0, SpringSpec.Gentle, 0.001f);
    public readonly Spring Opacity = new(1, SpringSpec.Content, 0.001f);
    public readonly Spring Hop = new(0, SpringSpec.Bounce, 0.01f);

    float _blinkAt = 2f, _blink = 1f, _clock, _wanderAt;
    bool _doubleBlink;
    readonly Random _rng = new();

    public void SetExpression(Expression e)
    {
        Wide.To(e is Expression.Wide or Expression.Exclaim ? 1 : 0);
        Happy.To(e == Expression.Happy ? 1 : 0);
        Sleepy.To(e == Expression.Sleepy ? 1 : 0);
        if (e == Expression.Exclaim) { Hop.Velocity = -110; }
    }

    /// <summary>Look at a point (in DIPs) or wander when the target is null.</summary>
    public void Look(Vector2? target, float dt)
    {
        if (target is { } t)
        {
            GazeX.To(Math.Clamp((t.X - X) / 260f, -1, 1));
            GazeY.To(Math.Clamp((t.Y - Y) / 140f, -1, 1));
        }
        else if (_clock > _wanderAt)
        {
            _wanderAt = _clock + 2.5f + (float)_rng.NextDouble() * 3.5f;
            bool center = _rng.NextDouble() < 0.45;
            GazeX.To(center ? 0 : (float)(_rng.NextDouble() * 1.6 - 0.8));
            GazeY.To(center ? 0 : (float)(_rng.NextDouble() * 0.8 - 0.3));
        }
    }

    public void Step(float dt)
    {
        _clock += dt;
        foreach (var s in new[] { X, Y, Size, GazeX, GazeY, Wide, Happy, Sleepy, Opacity, Hop }) s.Step(dt);

        // Blink: a fast close (~70 ms) and a slower open (~130 ms); sometimes a double blink.
        float since = _clock - _blinkAt;
        if (since >= 0)
        {
            if (since < 0.07f) _blink = 1 - since / 0.07f;
            else if (since < 0.2f) _blink = (since - 0.07f) / 0.13f;
            else
            {
                _blink = 1;
                if (_doubleBlink) { _doubleBlink = false; _blinkAt = _clock + 0.12f; }
                else
                {
                    _blinkAt = _clock + 2.2f + (float)_rng.NextDouble() * 4.5f;
                    _doubleBlink = _rng.NextDouble() < 0.2;
                }
            }
        }
    }

    public void Blink() { _blinkAt = _clock; }

    public void Draw(Canvas c)
    {
        float s = Size, op = Opacity;
        if (op < 0.01f || s < 0.05f) return;

        float eyeH = 12.5f * s, eyeW = 5.6f * s, spacing = 13.5f * s;
        float gx = GazeX, gy = GazeY;
        float cx = X + gx * 4.2f * s, cy = Y + gy * 2.6f * s + Hop.Value;

        float wide = Wide, happy = Happy, sleepy = Sleepy;
        float open = Math.Clamp(_blink, 0, 1) * (1 - 0.82f * sleepy);
        float h = MathF.Max(eyeH * (1 + 0.16f * wide) * open, eyeW * 0.38f);
        var color = Canvas.WithA(Canvas.Rgba(0xF5F5F7), op);

        for (int i = -1; i <= 1; i += 2)
        {
            // The eye nearer the side we look toward is foreshortened, like on Bloub's sphere.
            float squeeze = 1 - 0.22f * MathF.Max(0, gx * i);
            float w = eyeW * squeeze * (1 + 0.1f * wide);
            float ex = cx + i * spacing / 2;

            if (happy < 0.99f)
            {
                var col = Canvas.WithA(color, op * (1 - happy));
                c.Round(ex - w / 2, cy - h / 2, w, h, MathF.Min(w, h) / 2, col);
            }
            if (happy > 0.01f)
            {
                // Happy "^ ^" eyes.
                float aw = eyeW * 1.15f, ah = eyeH * 0.32f, lw = eyeW * 0.55f;
                var col = Canvas.WithA(color, op * happy);
                c.Line(ex - aw, cy + ah * 0.6f, ex, cy - ah * 0.6f, lw, col);
                c.Line(ex, cy - ah * 0.6f, ex + aw, cy + ah * 0.6f, lw, col);
            }
        }
    }
}
