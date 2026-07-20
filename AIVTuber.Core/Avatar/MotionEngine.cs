namespace AIVTuber.Core.Avatar;

/// <summary>
/// Procedural "alive" motion layer. Pure logic — no UI.
/// Call <see cref="Tick"/> with real wall-clock delta; do not assume 60fps.
/// </summary>
public sealed class MotionEngine
{
    private MotionLayerConfig _cfg;

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

    // Head-tilt spring (v0.2). Two independent target contributors:
    //   - emotion tilt (transient, cleared when emotion ends)
    //   - listening tilt (persistent while SetListening(true))
    // Effective target = emotionTilt ?? listeningTilt ?? 0. Emotion wins when set.
    private readonly Spring1D _tiltSpring = new();
    private float _emotionTiltTarget;   // 0 when no emotion override
    private bool _hasEmotionTilt;
    private float _listeningTiltTarget;
    private bool _listening;

    public MotionEngine(MotionLayerConfig? config = null)
    {
        _cfg = config ?? new MotionLayerConfig();
    }

    /// <summary>Hot-reload motion params (breath/bounce/tilt/…). Preserves runtime state
    /// (_timeSec, smoothed RMS, tilt spring position/velocity).</summary>
    public void UpdateConfig(MotionLayerConfig config)
    {
        _cfg = config ?? new MotionLayerConfig();
        // Refresh spring coeffs against the current target with new stiffness/damping.
        UpdateTiltTarget();
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

        // Emotion tilt override (v0.2). Clamped to the configured max.
        if (ov?.TiltDeg is { } tiltDeg)
        {
            _emotionTiltTarget = Math.Clamp(tiltDeg, -_cfg.Tilt.MaxDeg, _cfg.Tilt.MaxDeg);
            _hasEmotionTilt = true;
        }
        else
        {
            _emotionTiltTarget = 0;
            _hasEmotionTilt = false;
        }
        UpdateTiltTarget();
    }

    /// <summary>Head-tilt angle for listening pose (v0.2). Set by PixelAvatarDriver.SetListening.</summary>
    public void SetListeningTilt(float tiltDeg)
    {
        _listeningTiltTarget = Math.Clamp(tiltDeg, -_cfg.Tilt.MaxDeg, _cfg.Tilt.MaxDeg);
        _listening = Math.Abs(tiltDeg) > 0.0001f;
        UpdateTiltTarget();
    }

    private void UpdateTiltTarget()
    {
        // Emotion wins over listening when set; otherwise listening; otherwise 0.
        var target = _hasEmotionTilt ? _emotionTiltTarget : (_listening ? _listeningTiltTarget : 0f);
        _tiltSpring.SetTarget(target, _cfg.Tilt.Stiffness, _cfg.Tilt.Damping);
    }

    /// <summary>Clear persistent overrides (sink / bounce / breath scale) when emotion ends.</summary>
    public void ClearPersistentOverride()
    {
        _bounceScale = 1f;
        _breathRateScale = 1f;
        _sinkPx = 0f;
        // Emotion tilt is transient — clear it so the spring returns to listening/0.
        _hasEmotionTilt = false;
        _emotionTiltTarget = 0;
        UpdateTiltTarget();
    }

    public MotionFrame Tick(double deltaMs)
    {
        if (deltaMs < 0) deltaMs = 0;
        if (deltaMs > 100) deltaMs = 100; // clamp hitch spikes

        _timeSec += deltaMs / 1000.0;
        var dt = (float)deltaMs;

        // Bounce envelope (attack / release on RMS) — frame-rate-independent exponential.
        // alpha = 1 - exp(-dt/tau) is the standard form; linear dt/attack is unstable at large dt.
        var target = Volatile.Read(ref _latestRms);
        var attackTau = Math.Max(1f, _cfg.Bounce.AttackMs);
        var releaseTau = Math.Max(1f, _cfg.Bounce.ReleaseMs);
        var tau = target > _smoothedRms ? attackTau : releaseTau;
        var alpha = 1f - MathF.Exp(-dt / tau);
        _smoothedRms += (target - _smoothedRms) * alpha;

        // Map RMS → upward bounce. Gain is configurable (v0.2: replaces hardcoded 4.0).
        var bounceY = -Math.Min(_cfg.Bounce.MaxPx, _smoothedRms * _cfg.Bounce.MaxPx * _cfg.Bounce.Gain) * _bounceScale;

        // Breath
        var period = Math.Max(1f, _cfg.Breath.PeriodMs / Math.Max(0.05f, _breathRateScale));
        var breathPhase = (_timeSec * 1000.0 / period) * Math.PI * 2.0;
        // Breath: pure vertical scale about the foot pivot. Y translation (AmpPx) is intentionally
        // disabled in v0.2 — any translation causes upper body to overlap or gap the lower body.
        // breathY = 0 keeps the feet pinned; ScaleY gives the torso-deformation look.
        var breathY = 0f;
        var breathScaleY = 1f + (float)(Math.Sin(breathPhase) * _cfg.Breath.ScaleAmp);

        // Drift: stacked incommensurate sines ≈ Perlin.
        // Horizontal (X) is intentionally unused — whole-body left/right motion reads as
        // "swaying"; only vertical micro-drift is applied when amp_px > 0.
        var t = _timeSec * _cfg.Drift.Speed;
        var driftY = (float)(
            Math.Sin(t * 1.31 + 1.7) * 0.5 +
            Math.Sin(t * 2.71 + 0.4) * 0.3 +
            Math.Sin(t * 4.11 + 2.1) * 0.2) * _cfg.Drift.AmpPx;

        // Sway = whole-image rotation about foot pivot. Disabled when amp_deg is 0.
        // True head-tilt (歪头) needs a separate head layer — not implemented in v0.1.
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

        // Advance the head-tilt spring (v0.2). Position is the live tilt angle in degrees.
        _tiltSpring.Update(dt / 1000.0);

        return new MotionFrame(
            OffsetX: shakeX, // only one-shot emotion shake; no idle horizontal drift
            OffsetY: breathY + bounceY + driftY + jumpY + _sinkPx,
            ScaleX: 1f,
            ScaleY: breathScaleY,
            RotationDeg: rotation,
            Alpha: 1f,
            TiltDeg: (float)_tiltSpring.Position);
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
    float Alpha,
    /// <summary>Head-layer tilt angle in degrees (v0.2+). Driven by a spring; 0 = upright.
    /// Consumed only when the pack has a head layer (Layers.Enabled).</summary>
    float TiltDeg = 0f);
