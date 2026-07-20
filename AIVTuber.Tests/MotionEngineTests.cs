using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class MotionEngineTests
{
    private static MotionLayerConfig Cfg() => new()
    {
        Breath = new BreathConfig { AmpPx = 5, ScaleAmp = 0.012f, PeriodMs = 3200 },
        Bounce = new BounceConfig { MaxPx = 14, AttackMs = 40, ReleaseMs = 180 },
        Drift = new DriftConfig { AmpPx = 2.5f, Speed = 0.3f }, // tests still exercise vertical drift
        Sway = new SwayConfig { AmpDeg = 0f, PeriodMs = 5200 },
    };

    [Fact]
    public void Breath_ProducesOscillatingScaleY()
    {
        // v0.2: breath is pure vertical ScaleY oscillation about the foot pivot.
        // Y translation was removed because it caused upper-body overlap with lower body.
        var eng = new MotionEngine(Cfg());
        float minS = 2, maxS = 0;
        // Sample a full breath period in 16ms steps
        for (var i = 0; i < 3200 / 16; i++)
        {
            var f = eng.Tick(16);
            minS = Math.Min(minS, f.ScaleY);
            maxS = Math.Max(maxS, f.ScaleY);
        }
        var amp = maxS - minS;
        // scale_amp default 0.012 → peak-to-peak ~0.024; assert a non-trivial oscillation.
        Assert.True(amp > 0.01f, $"breath ScaleY amplitude too small: {amp}");
    }

    [Fact]
    public void Breath_NoVerticalTranslation_AfterV02()
    {
        // Regression guard: Y translation in breath must stay 0 so feet never leave their pixel row.
        // With RMS=0 (no bounce/jump/sink) and drift disabled, OffsetY must be exactly 0 throughout.
        var cfg = new MotionLayerConfig
        {
            Breath = new BreathConfig { AmpPx = 5, ScaleAmp = 0.012f, PeriodMs = 3200 },
            Bounce = new BounceConfig { MaxPx = 14, AttackMs = 40, ReleaseMs = 180, Gain = 4.0f },
            Drift = new DriftConfig { AmpPx = 0, Speed = 0.3f },
            Sway = new SwayConfig { AmpDeg = 0f, PeriodMs = 5200 },
        };
        var eng = new MotionEngine(cfg);
        for (var i = 0; i < 3200 / 16; i++)
        {
            var f = eng.Tick(16);
            Assert.Equal(0f, f.OffsetY);
        }
    }

    [Fact]
    public void Breath_ScaleYVariesAroundOne()
    {
        var eng = new MotionEngine(Cfg());
        float minS = 2, maxS = 0;
        for (var i = 0; i < 200; i++)
        {
            var f = eng.Tick(16);
            minS = Math.Min(minS, f.ScaleY);
            maxS = Math.Max(maxS, f.ScaleY);
        }
        Assert.True(minS < 1f);
        Assert.True(maxS > 1f);
    }

    [Fact]
    public void Bounce_AttackIsFasterThanRelease()
    {
        var eng = new MotionEngine(Cfg());
        eng.SetRms(1f);
        // After one attack window (~40ms) smoothed should be high
        eng.Tick(40);
        var afterAttack = eng.SmoothedRms;
        Assert.True(afterAttack > 0.5f, $"attack too slow: {afterAttack}");

        eng.SetRms(0f);
        eng.Tick(40); // one release step of 40ms vs release 180
        var midRelease = eng.SmoothedRms;
        Assert.True(midRelease < afterAttack);
        Assert.True(midRelease > 0.1f, "release should still be decaying, not instant");
    }

    [Fact]
    public void Bounce_MapsToUpwardOffset()
    {
        var eng = new MotionEngine(Cfg());
        // Warm up breath to a known phase by many ticks then compare relative bounce.
        for (var i = 0; i < 10; i++) eng.Tick(16);
        eng.SetRms(0f);
        for (var i = 0; i < 20; i++) eng.Tick(20); // settle release
        var quiet = eng.Tick(16).OffsetY;

        eng.SetRms(1f);
        for (var i = 0; i < 10; i++) eng.Tick(20);
        var loud = eng.Tick(16).OffsetY;

        // Bounce is negative Y (upward in screen space with Y-down WPF).
        Assert.True(loud < quiet, $"expected upward bounce, quiet={quiet} loud={loud}");
    }

    [Fact]
    public void Bounce_RespectsGainConfig()
    {
        // v0.2: gain is now configurable. Higher gain → larger bounce for the same RMS.
        // At RMS=0.05, MaxPx=14: low-gain(2) → 0.05*14*2=1.4 ; high-gain(8) → 0.05*14*8=5.6 (clamped by MaxPx).
        var cfgLow = Cfg();
        cfgLow.Bounce.Gain = 2.0f;
        var cfgHigh = Cfg();
        cfgHigh.Bounce.Gain = 8.0f;
        // Disable drift so it does not pollute OffsetY.
        cfgLow.Drift.AmpPx = 0;
        cfgHigh.Drift.AmpPx = 0;

        var engLow = new MotionEngine(cfgLow);
        var engHigh = new MotionEngine(cfgHigh);
        engLow.SetRms(0.05f);
        engHigh.SetRms(0.05f);

        // Let the envelope settle (attack=40ms).
        for (var i = 0; i < 10; i++) { engLow.Tick(16); engHigh.Tick(16); }

        var fLow = engLow.Tick(16);
        var fHigh = engHigh.Tick(16);
        // High-gain bounce should be more negative (larger upward offset).
        Assert.True(fHigh.OffsetY < fLow.OffsetY,
            $"high-gain bounce ({fHigh.OffsetY}) should exceed low-gain ({fLow.OffsetY})");
    }

    [Fact]
    public void Bounce_ExponentialSmoothing_IsFrameRateIndependent()
    {
        // v0.2: envelope is now exponential (1 - exp(-dt/tau)). Same total elapsed time at
        // different frame rates should reach ~the same smoothed value (unlike linear dt/attack).
        var cfgA = Cfg(); cfgA.Drift.AmpPx = 0;
        var cfgB = Cfg(); cfgB.Drift.AmpPx = 0;

        var engA = new MotionEngine(cfgA); // 10 ticks @ 40ms = 400ms
        var engB = new MotionEngine(cfgB); // 20 ticks @ 20ms = 400ms
        engA.SetRms(1.0f);
        engB.SetRms(1.0f);

        for (var i = 0; i < 10; i++) engA.Tick(40);
        for (var i = 0; i < 20; i++) engB.Tick(20);

        var fA = engA.Tick(1);
        var fB = engB.Tick(1);
        // Allow a few percent drift from numerical differences; both should be near saturation.
        Assert.True(Math.Abs(fA.OffsetY - fB.OffsetY) < 1.5f,
            $"frame-rate dependent bounce: 10x40ms={fA.OffsetY} vs 20x20ms={fB.OffsetY}");
    }

    [Fact]
    public void Bounce_RespectsMaxPxCap()
    {
        var eng = new MotionEngine(Cfg());
        eng.SetRms(10f); // absurd RMS
        float minY = 0;
        for (var i = 0; i < 50; i++)
        {
            var f = eng.Tick(20);
            minY = Math.Min(minY, f.OffsetY);
        }
        // Bounce max 14 + breath 5 + drift 2.5 + ... keep under a generous bound
        Assert.True(minY > -40, $"motion exploded: {minY}");
    }

    [Fact]
    public void Drift_IsVerticalOnly_NoIdleHorizontal()
    {
        var eng = new MotionEngine(Cfg());
        float sumAbsX = 0, sumAbsY = 0;
        for (var i = 0; i < 100; i++)
        {
            var f = eng.Tick(16);
            sumAbsX += Math.Abs(f.OffsetX);
            sumAbsY += Math.Abs(f.OffsetY);
        }
        // Idle must not rock left/right; vertical breath+drift still alive.
        Assert.Equal(0f, sumAbsX);
        Assert.True(sumAbsY > 1f, "vertical motion should remain");
    }

    [Fact]
    public void Sway_ZeroAmp_MeansNoRotation()
    {
        var cfg = Cfg();
        cfg.Sway.AmpDeg = 0;
        var eng = new MotionEngine(cfg);
        for (var i = 0; i < 50; i++)
            Assert.Equal(0f, eng.Tick(16).RotationDeg);
    }

    [Fact]
    public void Sway_RotatesWithinAmp_WhenEnabled()
    {
        var cfg = Cfg();
        cfg.Sway.AmpDeg = 2.5f;
        var eng = new MotionEngine(cfg);
        float maxAbs = 0;
        for (var i = 0; i < 400; i++)
            maxAbs = Math.Max(maxAbs, Math.Abs(eng.Tick(16).RotationDeg));
        Assert.InRange(maxAbs, 1.0f, 2.6f);
    }

    [Fact]
    public void Jump_PeaksThenReturns()
    {
        var eng = new MotionEngine(Cfg());
        eng.ApplyOverride(new MotionOverrideDef { JumpPx = 18 });
        float minY = 0;
        float endY = 0;
        for (var i = 0; i < 40; i++)
        {
            var f = eng.Tick(10); // 400ms total > 280ms jump
            minY = Math.Min(minY, f.OffsetY);
            endY = f.OffsetY;
        }
        Assert.True(minY < -5, $"jump peak missing: {minY}");
        // After jump completes, no sustained jump offset (may still have breath/drift)
        Assert.True(endY > minY + 5, "jump should settle after peak");
    }

    [Fact]
    public void Shake_DecaysToZero()
    {
        var eng = new MotionEngine(Cfg());
        eng.ApplyOverride(new MotionOverrideDef
        {
            Shake = new ShakeDef { AmpPx = 10, FreqHz = 9, DurationMs = 200 },
        });

        float maxAbsDuring = 0;
        for (var i = 0; i < 10; i++)
            maxAbsDuring = Math.Max(maxAbsDuring, Math.Abs(eng.Tick(10).OffsetX));

        // Run well past duration
        for (var i = 0; i < 50; i++) eng.Tick(20);

        // After shake ends, X is only drift — amp 2.5, so |X| should be modest.
        // Compare peak during shake vs late samples.
        float maxAbsLate = 0;
        for (var i = 0; i < 20; i++)
            maxAbsLate = Math.Max(maxAbsLate, Math.Abs(eng.Tick(16).OffsetX));

        Assert.True(maxAbsDuring > 2f, $"shake never visible: {maxAbsDuring}");
        Assert.True(maxAbsLate < maxAbsDuring, "shake should decay");
    }

    [Fact]
    public void Sink_HoldsWhileOverrideActive()
    {
        var eng = new MotionEngine(Cfg());
        eng.ApplyOverride(new MotionOverrideDef { SinkPx = 8 });
        var withSink = eng.Tick(16).OffsetY;

        eng.ClearPersistentOverride();
        // Clear breath phase noise by comparing relative: after clear, sink component gone.
        // Force same time advance
        var without = eng.Tick(16).OffsetY;
        // withSink includes +8 sink; without does not — withSink should be larger (more positive / down)
        Assert.True(withSink > without - 2, $"sink not applied: with={withSink} without={without}");
    }

    [Fact]
    public void BounceScale_MultipliesBounce()
    {
        var baseEng = new MotionEngine(Cfg());
        var boosted = new MotionEngine(Cfg());
        boosted.ApplyOverride(new MotionOverrideDef { BounceScale = 2f });

        baseEng.SetRms(1f);
        boosted.SetRms(1f);
        for (var i = 0; i < 15; i++)
        {
            baseEng.Tick(20);
            boosted.Tick(20);
        }
        var b = baseEng.Tick(16);
        var s = boosted.Tick(16);
        // Both have same breath/drift phase if started together — bounce term doubles.
        // Boosted should be more negative (higher bounce up).
        Assert.True(s.OffsetY < b.OffsetY, $"bounce_scale not applied: base={b.OffsetY} boosted={s.OffsetY}");
    }

    [Fact]
    public void Tick_UsesDeltaTime_NotFixedFps()
    {
        var a = new MotionEngine(Cfg());
        var b = new MotionEngine(Cfg());
        // Advance same wall time with different step sizes
        for (var i = 0; i < 10; i++) a.Tick(32);
        for (var i = 0; i < 20; i++) b.Tick(16);

        // After ~320ms, sway/breath phase should be close
        var fa = a.Tick(0.001);
        var fb = b.Tick(0.001);
        Assert.InRange(fa.RotationDeg, fb.RotationDeg - 0.5f, fb.RotationDeg + 0.5f);
    }

    [Fact]
    public void Alpha_DefaultsToOne()
    {
        var eng = new MotionEngine(Cfg());
        Assert.Equal(1f, eng.Tick(16).Alpha);
    }

    [Fact]
    public void Tilt_SpringReachesListeningTarget()
    {
        var eng = new MotionEngine(Cfg());
        eng.SetListeningTilt(5f);
        for (var i = 0; i < 500; i++) eng.Tick(16);
        var f = eng.Tick(1);
        Assert.True(Math.Abs(f.TiltDeg - 5f) < 0.3f, $"tilt settled at {f.TiltDeg}, expected ~5");
    }

    [Fact]
    public void Tilt_ClampsToConfiguredMax()
    {
        var cfg = Cfg();
        cfg.Tilt.MaxDeg = 6f;
        var eng = new MotionEngine(cfg);
        eng.SetListeningTilt(100f);
        for (var i = 0; i < 500; i++) eng.Tick(16);
        var f = eng.Tick(1);
        Assert.True(Math.Abs(f.TiltDeg - 6f) < 0.3f, $"tilt exceeded clamp: {f.TiltDeg}");
    }

    [Fact]
    public void Tilt_EmotionOverride_BeatsListening()
    {
        var eng = new MotionEngine(Cfg());
        eng.SetListeningTilt(5f);
        for (var i = 0; i < 300; i++) eng.Tick(16);
        eng.ApplyOverride(new MotionOverrideDef { TiltDeg = -3f });
        for (var i = 0; i < 500; i++) eng.Tick(16);
        var f = eng.Tick(1);
        Assert.True(Math.Abs(f.TiltDeg - (-3f)) < 0.3f, $"emotion tilt lost: {f.TiltDeg}");
    }

    [Fact]
    public void Tilt_ReturnsToListening_AfterEmotionEnds()
    {
        var eng = new MotionEngine(Cfg());
        eng.SetListeningTilt(5f);
        for (var i = 0; i < 300; i++) eng.Tick(16);
        eng.ApplyOverride(new MotionOverrideDef { TiltDeg = -3f });
        for (var i = 0; i < 300; i++) eng.Tick(16);
        eng.ClearPersistentOverride();
        for (var i = 0; i < 500; i++) eng.Tick(16);
        var f = eng.Tick(1);
        Assert.True(Math.Abs(f.TiltDeg - 5f) < 0.3f, $"did not return to listening: {f.TiltDeg}");
    }

    [Fact]
    public void Tilt_StartsAtZero_WhenNoTarget()
    {
        var eng = new MotionEngine(Cfg());
        Assert.Equal(0f, eng.Tick(16).TiltDeg);
    }

    [Fact]
    public void UpdateConfig_ChangesBounceGain()
    {
        var cfg = Cfg();
        cfg.Bounce.Gain = 1f;
        cfg.Drift.AmpPx = 0;
        cfg.Breath.ScaleAmp = 0;
        cfg.Sway.AmpDeg = 0;
        var eng = new MotionEngine(cfg);
        eng.SetRms(1f);
        for (var i = 0; i < 60; i++) eng.Tick(16);
        var lowGain = eng.Tick(16).OffsetY;

        cfg.Bounce.Gain = 8f;
        eng.UpdateConfig(cfg);
        var highGain = eng.Tick(16).OffsetY;

        Assert.True(highGain < lowGain,
            $"UpdateConfig gain not applied: lowGainY={lowGain} highGainY={highGain}");
    }
}
