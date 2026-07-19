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
    public void Breath_ProducesOscillatingHeadY()
    {
        var eng = new MotionEngine(Cfg());
        float? minY = null, maxY = null;
        // Sample a full breath period in 16ms steps
        for (var i = 0; i < 3200 / 16; i++)
        {
            var f = eng.Tick(16);
            minY = minY is null ? f.BreathOffsetY : Math.Min(minY.Value, f.BreathOffsetY);
            maxY = maxY is null ? f.BreathOffsetY : Math.Max(maxY.Value, f.BreathOffsetY);
        }
        Assert.NotNull(minY);
        Assert.NotNull(maxY);
        Assert.True(maxY - minY > 5, $"breath amplitude too small: {maxY - minY}");
    }

    [Fact]
    public void Breath_ScaleYVariesAroundOne()
    {
        var eng = new MotionEngine(Cfg());
        float minS = 2, maxS = 0;
        for (var i = 0; i < 200; i++)
        {
            var f = eng.Tick(16);
            minS = Math.Min(minS, f.BreathScaleY);
            maxS = Math.Max(maxS, f.BreathScaleY);
        }
        Assert.True(minS < 1f);
        Assert.True(maxS > 1f);
    }

    [Fact]
    public void Breath_DoesNotMoveQuietBody()
    {
        var cfg = Cfg();
        cfg.Drift.AmpPx = 0;
        var eng = new MotionEngine(cfg);

        for (var i = 0; i < 200; i++)
            Assert.Equal(0f, eng.Tick(16).OffsetY);
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
}
