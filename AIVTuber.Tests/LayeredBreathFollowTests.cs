using AIVTuber.Core.Avatar;

namespace AIVTuber.Tests;

public class LayeredBreathFollowTests
{
    [Theory]
    [InlineData(1180, 535, 1.0, 0.0)]
    [InlineData(1180, 535, 1.008, -5.16)] // (1180-535)*0.008 = 5.16 upward
    [InlineData(1180, 535, 0.992, 5.16)]
    public void HeadTranslateY_UsesPivotMinusCut(double pivotY, double cutY, double scaleY, double expected)
    {
        var y = LayeredBreathFollow.HeadTranslateY(pivotY, cutY, scaleY);
        Assert.Equal(expected, y, precision: 6);
    }

    [Fact]
    public void HeadTranslateY_DoesNotUseCanvasBottomOrNeckPivot()
    {
        // Old wrong formula used (canvasH - neckPivotY); must not match for v0.5 numbers.
        const double canvasH = 1254;
        const double neckPivotY = 500;
        const double pivotY = 1180;
        const double cutY = 535;
        const double scaleY = 1.008;

        var correct = LayeredBreathFollow.HeadTranslateY(pivotY, cutY, scaleY);
        var oldWrong = -(canvasH - neckPivotY) * (scaleY - 1.0);

        Assert.NotEqual(oldWrong, correct);
        Assert.Equal(-(pivotY - cutY) * (scaleY - 1.0), correct);
    }
}
