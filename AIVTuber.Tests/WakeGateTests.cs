using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public sealed class WakeGateTests
{
    [Fact]
    public void NormalMode_AlwaysAllows()
    {
        var gate = new WakeGate();
        Assert.True(gate.ShouldSpeak(false, ["小娜"], 0, "随便说点什么", 1000));
    }

    [Fact]
    public void PkMode_BlocksWithoutKeyword()
    {
        var gate = new WakeGate();
        Assert.False(gate.ShouldSpeak(true, ["小娜"], 0, "对面在骂人", 1000));
    }

    [Fact]
    public void PkMode_AllowsWhenKeywordPresent()
    {
        var gate = new WakeGate();
        Assert.True(gate.ShouldSpeak(true, ["小娜"], 0, "小娜你怎么看", 1000));
    }

    [Fact]
    public void PkMode_KeywordMatch_IsCaseInsensitive()
    {
        var gate = new WakeGate();
        Assert.True(gate.ShouldSpeak(true, ["AI"], 0, "ai 出来一下", 1000));
    }

    [Fact]
    public void PkMode_HoldWindow_AllowsFollowUp()
    {
        var gate = new WakeGate();
        Assert.True(gate.ShouldSpeak(true, ["小娜"], 30, "小娜在吗", 1000));
        Assert.True(gate.ShouldSpeak(true, ["小娜"], 30, "那你怎么看", 1000 + 10_000));
        Assert.False(gate.ShouldSpeak(true, ["小娜"], 30, "那你怎么看", 1000 + 31_000));
    }

    [Fact]
    public void PkMode_EmptyKeywords_NeverWakes()
    {
        var gate = new WakeGate();
        Assert.False(gate.ShouldSpeak(true, [], 45, "随便", 1000));
    }

    [Fact]
    public void Reset_ClearsHoldWindow()
    {
        var gate = new WakeGate();
        Assert.True(gate.ShouldSpeak(true, ["小娜"], 60, "小娜", 1000));
        gate.Reset();
        Assert.False(gate.ShouldSpeak(true, ["小娜"], 60, "接着说", 1100));
    }
}
