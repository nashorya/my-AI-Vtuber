using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class DualPartyTurnGateTests
{
    private sealed class Clock
    {
        public DateTime Now { get; set; } = new(2026, 9, 10, 0, 0, 0, DateTimeKind.Utc);
    }

    [Fact]
    public void Flushes_OnlyAfterBothSilent()
    {
        IReadOnlyList<TalkLine>? got = null;
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30), () => clock.Now);
        gate.TurnReady += lines => got = lines;
        gate.SetMicSpeaking(true);
        gate.AddLine(new TalkLine(TalkIdentity.Self, "我", "你好", null));
        gate.Tick();
        Assert.Null(got);

        gate.SetMicSpeaking(false);
        gate.SetLoopbackSpeaking(false);
        gate.Tick();
        Assert.Null(got);

        clock.Now += TimeSpan.FromMilliseconds(40);
        gate.Tick();
        Assert.NotNull(got);
        Assert.Equal("你好", got![0].Text);
    }

    [Fact]
    public void DoesNotFlush_WhileAiSpeaking()
    {
        IReadOnlyList<TalkLine>? got = null;
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(20), () => clock.Now);
        gate.TurnReady += lines => got = lines;
        gate.SetAiSpeaking(true);
        gate.AddLine(new TalkLine(TalkIdentity.Opponent, "对面", "哈喽", "u2"));
        gate.SetMicSpeaking(false);
        gate.SetLoopbackSpeaking(false);
        clock.Now += TimeSpan.FromMilliseconds(50);
        gate.Tick();
        Assert.Null(got);

        gate.SetAiSpeaking(false);
        clock.Now += TimeSpan.FromMilliseconds(50);
        gate.Tick();
        Assert.NotNull(got);
    }

    [Fact]
    public void AddLine_WhenAlreadyQuiet_FlushesAfterSilence()
    {
        IReadOnlyList<TalkLine>? got = null;
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30), () => clock.Now);
        gate.TurnReady += lines => got = lines;
        gate.AddLine(new TalkLine(TalkIdentity.Danmaku, "路人", "加油", "u-d"));
        gate.Tick();
        Assert.Null(got);
        clock.Now += TimeSpan.FromMilliseconds(40);
        gate.Tick();
        Assert.NotNull(got);
        Assert.Equal("加油", got![0].Text);
    }
}
