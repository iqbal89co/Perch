namespace Perch.Core;

/// <summary>
/// A damped spring that moves Value toward Target. Slightly under-damped by default, which gives the
/// island its soft overshoot. Step with the real frame time so motion is the same at 60 or 240 Hz.
/// </summary>
public sealed class Spring
{
    public double Value { get; set; }
    public double Target { get; set; }
    public double Velocity { get; set; }
    public double Stiffness { get; set; } = 380;
    public double DampingRatio { get; set; } = 0.78;

    public Spring(double initial)
    {
        Value = initial;
        Target = initial;
    }

    public bool IsSettled => Math.Abs(Target - Value) < 0.05 && Math.Abs(Velocity) < 0.05;

    /// <summary>Advance by dt seconds. Large steps are split so a stalled frame can't explode.</summary>
    public void Step(double dt)
    {
        if (dt <= 0) return;
        dt = Math.Min(dt, 0.1);
        const double maxSub = 1.0 / 240.0;
        var steps = (int)Math.Ceiling(dt / maxSub);
        var h = dt / steps;
        var damping = 2 * DampingRatio * Math.Sqrt(Stiffness);
        for (var i = 0; i < steps; i++)
        {
            var force = Stiffness * (Target - Value) - damping * Velocity;
            Velocity += force * h;
            Value += Velocity * h;
        }
        if (IsSettled)
        {
            Value = Target;
            Velocity = 0;
        }
    }

    public void SnapToTarget()
    {
        Value = Target;
        Velocity = 0;
    }
}
