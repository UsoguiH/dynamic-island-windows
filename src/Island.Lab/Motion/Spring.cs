namespace Island.Lab.Motion;

/// <summary>SwiftUI-style spring parameters: response (seconds) and damping fraction.</summary>
public readonly record struct SpringSpec(float Response, float Damping)
{
    public static readonly SpringSpec Island = new(0.50f, 0.78f);
    public static readonly SpringSpec IslandSnappy = new(0.38f, 0.86f);
    public static readonly SpringSpec Bounce = new(0.45f, 0.62f);
    public static readonly SpringSpec Content = new(0.42f, 1.00f);
    public static readonly SpringSpec Press = new(0.25f, 0.90f);
    public static readonly SpringSpec Gentle = new(0.70f, 1.00f);
    public static readonly SpringSpec Quick = new(0.30f, 1.00f);
}

/// <summary>
/// Damped harmonic spring (mass = 1), integrated with fixed 1 ms sub-steps so it is stable
/// at any frame rate. Retargeting keeps the current velocity, so interrupted animations
/// never jump.
/// </summary>
public sealed class Spring
{
    public float Value;
    public float Velocity;
    public float Target;
    public float Epsilon;

    float _k, _c;
    float _pendingTarget;
    float _pendingDelay = -1;

    public Spring(float value, SpringSpec spec, float epsilon = 0.01f)
    {
        Value = Target = value;
        Epsilon = epsilon;
        Configure(spec);
    }

    public SpringSpec Spec { get; private set; }

    public void Configure(SpringSpec spec)
    {
        Spec = spec;
        float w = 2f * MathF.PI / spec.Response;
        _k = w * w;
        _c = 4f * MathF.PI * spec.Damping / spec.Response;
    }

    /// <summary>Sets a new target, optionally after a delay (seconds).</summary>
    public void To(float target, float delay = 0f)
    {
        if (delay <= 0f)
        {
            Target = target;
            _pendingDelay = -1;
        }
        else
        {
            _pendingTarget = target;
            _pendingDelay = delay;
        }
    }

    public void To(float target, SpringSpec spec, float delay = 0f)
    {
        Configure(spec);
        To(target, delay);
    }

    public void Snap(float value)
    {
        Value = Target = value;
        Velocity = 0;
        _pendingDelay = -1;
    }

    public bool Settled => _pendingDelay < 0 && MathF.Abs(Value - Target) < Epsilon && MathF.Abs(Velocity) < Epsilon * 10;

    public void Step(float dt)
    {
        if (_pendingDelay >= 0)
        {
            _pendingDelay -= dt;
            if (_pendingDelay < 0) Target = _pendingTarget;
        }
        if (Settled) { Value = Target; Velocity = 0; return; }

        int n = Math.Max(1, (int)MathF.Ceiling(dt / 0.001f));
        float h = dt / n;
        for (int i = 0; i < n; i++)
        {
            float a = -_k * (Value - Target) - _c * Velocity;
            Velocity += a * h;
            Value += Velocity * h;
        }
    }

    public static implicit operator float(Spring s) => s.Value;
}
