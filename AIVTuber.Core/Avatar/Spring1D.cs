namespace AIVTuber.Core.Avatar;

/// <summary>
/// Frame-rate-independent second-order spring for smooth 1D motion (e.g. head tilt).
/// Uses semi-implicit Euler: stable for the stiffness/damping ranges used by the avatar.
/// Target changes do NOT reset velocity — the spring continues smoothly, allowing overshoot.
/// </summary>
public sealed class Spring1D
{
    /// <summary>Current position (degrees, pixels, etc. — whatever unit the caller uses).</summary>
    public double Position { get; private set; }

    /// <summary>Current velocity (units/sec).</summary>
    public double Velocity { get; private set; }

    /// <summary>Current target. Update via <see cref="SetTarget"/>.</summary>
    public double Target { get; private set; }

    private double _stiffness = 120.0;
    private double _damping = 14.0;

    /// <summary>Updates the target and spring constants. Velocity is preserved so the motion
    /// continues smoothly across target changes (e.g. listening on → emotion tilt → off).</summary>
    public void SetTarget(double target, double stiffness, double damping)
    {
        Target = target;
        _stiffness = Math.Max(0.1, stiffness);
        _damping = Math.Max(0.0, damping);
    }

    /// <summary>Advances the spring by <paramref name="dtSeconds"/>. Safe to call with dt=0
    /// (no-op). Large dt is clamped internally to avoid instability on hitches.</summary>
    public void Update(double dtSeconds)
    {
        if (dtSeconds <= 0) return;
        // Clamp dt to avoid integration blowup on frame hitches (250ms matches the renderer's clamp).
        var dt = Math.Min(dtSeconds, 0.25);

        // Semi-implicit Euler: update velocity first, then position using the NEW velocity.
        // This is more stable than explicit Euler for springs.
        var accel = -_stiffness * (Position - Target) - _damping * Velocity;
        Velocity += accel * dt;
        Position += Velocity * dt;
    }

    /// <summary>Hard reset: teleports position, zeroes velocity. Use on Reset()/mode switches
    /// where continuity is not wanted.</summary>
    public void SnapTo(double position)
    {
        Position = position;
        Velocity = 0;
        Target = position;
    }
}
