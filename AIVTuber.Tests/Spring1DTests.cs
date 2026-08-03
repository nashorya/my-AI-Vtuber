using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class Spring1DTests
{
    private const double Epsilon = 0.15;

    [Fact]
    public void ReachesTarget_WhenCriticallyDamped()
    {
        // Critical damping: damping = 2*sqrt(stiffness). With stiffness=100, critical ~20.
        // Should reach target ~without overshoot.
        var s = new Spring1D();
        s.SetTarget(10.0, stiffness: 100, damping: 20);
        for (var i = 0; i < 1000; i++) s.Update(0.016); // 16s simulated
        Assert.True(Math.Abs(s.Position - 10.0) < Epsilon, $"position={s.Position}");
    }

    [Fact]
    public void SlightOvershoot_WhenUnderdamped()
    {
        // Underdamped (damping < critical): should overshoot target at least once before settling.
        var s = new Spring1D();
        s.SetTarget(10.0, stiffness: 200, damping: 5); // low damping → bouncy
        var maxPos = 0.0;
        for (var i = 0; i < 500; i++)
        {
            s.Update(0.016);
            maxPos = Math.Max(maxPos, s.Position);
        }
        // Overshoot: peak must exceed the 10.0 target.
        Assert.True(maxPos > 10.0 + 0.3, $"expected overshoot > 10.3, got peak {maxPos}");
    }

    [Fact]
    public void VelocityContinuity_OnTargetChange()
    {
        // When target changes mid-motion, the spring must NOT snap to the new target.
        // It continues from its current position+velocity (smooth transition).
        var s = new Spring1D();
        s.SetTarget(10.0, stiffness: 100, damping: 14);
        // Advance partway — pick up positive velocity (still approaching target, not yet overshooting).
        for (var i = 0; i < 15; i++) s.Update(0.016);
        var posMid = s.Position;
        var velMid = s.Velocity;
        Assert.True(velMid > 0.1, $"expected positive velocity mid-motion, got {velMid}");

        // Switch target back to 0. Position should NOT jump to 0.
        s.SetTarget(0.0, stiffness: 100, damping: 14);
        Assert.Equal(posMid, s.Position); // unchanged immediately after SetTarget
        s.Update(0.016);
        // Position moved only slightly (continuity), not snapped to 0.
        Assert.True(Math.Abs(s.Position - posMid) < 2.0, $"position jumped: {s.Position} vs {posMid}");
    }

    [Fact]
    public void Update_IsFrameRateIndependent()
    {
        // Same total elapsed time, different dt slices → ~same final position.
        var sA = new Spring1D();
        var sB = new Spring1D();
        sA.SetTarget(10.0, stiffness: 100, damping: 14);
        sB.SetTarget(10.0, stiffness: 100, damping: 14);

        // 0.5s total: 30×16.67ms vs 500×1ms
        for (var i = 0; i < 30; i++) sA.Update(0.01667);
        for (var i = 0; i < 500; i++) sB.Update(0.001);

        Assert.True(Math.Abs(sA.Position - sB.Position) < Epsilon,
            $"frame-rate dependent: 30x16ms={sA.Position} vs 500x1ms={sB.Position}");
    }

    [Fact]
    public void Update_WithZeroDt_IsNoOp()
    {
        var s = new Spring1D();
        s.SetTarget(5.0, stiffness: 100, damping: 14);
        s.Update(0); // must not throw or advance
        Assert.Equal(0.0, s.Position);
    }

    [Fact]
    public void SnapTo_TeleportsPosition_AndZeroesVelocity()
    {
        var s = new Spring1D();
        s.SetTarget(10.0, stiffness: 100, damping: 14);
        for (var i = 0; i < 50; i++) s.Update(0.016);
        Assert.NotEqual(0.0, s.Velocity);

        s.SnapTo(-3.0);
        Assert.Equal(-3.0, s.Position);
        Assert.Equal(0.0, s.Velocity);
        Assert.Equal(-3.0, s.Target);
    }

    [Fact]
    public void Update_ClampsLargeDt_AvoidsBlowup()
    {
        // A 1-second dt (huge hitch) must not produce NaN or explode.
        var s = new Spring1D();
        s.SetTarget(5.0, stiffness: 120, damping: 14);
        s.Update(1.0);
        Assert.False(double.IsNaN(s.Position) || double.IsInfinity(s.Position));
        Assert.True(Math.Abs(s.Position) < 1000, $"position blew up: {s.Position}");
    }
}
