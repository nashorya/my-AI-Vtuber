namespace AIVTuber.Core.Avatar;

/// <summary>
/// Procedural "alive" motion layer. Pure logic — no UI.
/// Call <see cref="Tick"/> with real wall-clock delta; do not assume 60fps.
/// </summary>
public sealed class MotionEngine
{
    private readonly MotionLayerConfig _cfg;

    private double _timeSec;
    private float _smoothedRms;
    private float _latestRms;
    private float _bounceScale = 1f;
    private float _breathRateScale = 1f;
    private float _sinkPx;

    private float _shakeAmp;
    private float _shakeFreq;
    private float _shakeDurationMs;
    private float _shakeRemainingMs;

    private float _jumpPx;
    private float _jumpDurationMs = 280f;
    private float _jumpElapsedMs = float.MaxValue;

    public MotionEngine(MotionLayerConfig? config = null)
    {
        _cfg = config ?? new MotionLayerConfig();
    }

    /// <summary>Latest RMS (0..~1). Safe to call from the audio thread.</summary>
    public void SetRms(float rms) => Volatile.Write(ref _latestRms, Math.Clamp(rms, 0f, 2f));

    /// <summary>Apply motion overrides from the active emotion state (or clear when null).</summary>
    public void ApplyOverride(MotionOverrideDef? ov)
    {
        _bounceScale = ov?.BounceScale ?? 1f;
        _breathRateScale = ov?.BreathRateScale ?? 1f;
        _sinkPx = ov?.SinkPx ?? 0f;

        if (ov?.Shake is { } shake)
        {
            _shakeAmp = shake.AmpPx;
            _shakeFreq = Math.Max(0.01f, shake.FreqHz);
            _shakeDurationMs = Math.Max(1, shake.DurationMs);
            _shakeRemainingMs = _shakeDurationMs;
        }

        if (ov?.JumpPx is { } jump && jump != 0)
        {
            _jumpPx = jump;
            _jumpElapsedMs = 0;
            _jumpDurationMs = 280f;
        }
    }

    /// <summary>Clear persistent overrides (sink / bounce / breath scale) when emotion ends.</summary>
    public void ClearPersistentOverride()
    {
        _bounceScale = 1f;
        _breathRateScale = 1f;
        _sinkPx = 0f;
    }

    public MotionFrame Tick(double deltaMs)
    {
        if (deltaMs < 0) deltaMs = 0;
        if (deltaMs > 100) deltaMs = 100; // clamp hitch spikes

        _timeSec += deltaMs / 1000.0;
        var dt = (float)deltaMs;

        // Bounce envelope (attack / release on RMS)
        var target = Volatile.Read(ref _latestRms);
        var attack = Math.Max(1f, _cfg.Bounce.AttackMs);
        var release = Math.Max(1f, _cfg.Bounce.ReleaseMs);
        if (target > _smoothedRms)
            _smoothedRms += (target - _smoothedRms) * Math.Min(1f, dt / attack);
        else
            _smoothedRms += (target - _smoothedRms) * Math.Min(1f, dt / release);

        // Map RMS → upward bounce; factor keeps typical speech RMS near max_px.
        var bounceY = -Math.Min(_cfg.Bounce.MaxPx, _smoothedRms * _cfg.Bounce.MaxPx * 4f) * _bounceScale;

        // Breath
        var period = Math.Max(1f, _cfg.Breath.PeriodMs / Math.Max(0.05f, _breathRateScale));
        var breathPhase = (_timeSec * 1000.0 / period) * Math.PI * 2.0;
        var breathY = (float)(Math.Sin(breathPhase) * _cfg.Breath.AmpPx);
        var breathScaleY = 1f + (float)(Math.Sin(breathPhase) * _cfg.Breath.ScaleAmp);

        // Drift: stacked incommensurate sines ≈ Perlin
        var t = _timeSec * _cfg.Drift.Speed;
        var driftX = (float)(
            Math.Sin(t * 1.0) * 0.5 +
            Math.Sin(t * 2.17) * 0.3 +
            Math.Sin(t * 3.93) * 0.2) * _cfg.Drift.AmpPx;
        var driftY = (float)(
            Math.Sin(t * 1.31 + 1.7) * 0.5 +
            Math.Sin(t * 2.71 + 0.4) * 0.3 +
            Math.Sin(t * 4.11 + 2.1) * 0.2) * _cfg.Drift.AmpPx;

        // Sway
        var swayPeriod = Math.Max(1f, _cfg.Sway.PeriodMs);
        var swayPhase = (_timeSec * 1000.0 / swayPeriod) * Math.PI * 2.0;
        var rotation = (float)(Math.Sin(swayPhase) * _cfg.Sway.AmpDeg);

        // Shake (linear amplitude decay)
        float shakeX = 0;
        if (_shakeRemainingMs > 0)
        {
            var env = Math.Clamp(_shakeRemainingMs / _shakeDurationMs, 0f, 1f);
            shakeX = (float)(Math.Sin(_timeSec * _shakeFreq * Math.PI * 2.0) * _shakeAmp * env);
            _shakeRemainingMs -= dt;
            if (_shakeRemainingMs <= 0)
            {
                _shakeRemainingMs = 0;
                _shakeAmp = 0;
            }
        }

        // Jump (single up-down parabola)
        float jumpY = 0;
        if (_jumpElapsedMs < _jumpDurationMs)
        {
            _jumpElapsedMs += dt;
            var u = Math.Clamp(_jumpElapsedMs / _jumpDurationMs, 0f, 1f);
            jumpY = -_jumpPx * 4f * u * (1f - u);
        }

        return new MotionFrame(
            OffsetX: driftX + shakeX,
            OffsetY: breathY + bounceY + driftY + jumpY + _sinkPx,
            ScaleX: 1f,
            ScaleY: breathScaleY,
            RotationDeg: rotation,
            Alpha: 1f);
    }

    /// <summary>Expose smoothed RMS for tests.</summary>
    internal float SmoothedRms => _smoothedRms;

    /// <summary>Expose jump remaining for tests (ms until jump animation ends; large when idle).</summary>
    internal float JumpElapsedMs => _jumpElapsedMs;
}

/// <summary>Per-frame transform applied to the avatar root.</summary>
public readonly record struct MotionFrame(
    float OffsetX,
    float OffsetY,
    float ScaleX,
    float ScaleY,
    float RotationDeg,
    float Alpha);
