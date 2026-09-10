using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class DualPartyTurnGateTests
{
    [Fact]
    public void FailedRecognition_ReleasesGateForOtherInputs()
    {
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30), () => clock.Now);
        IReadOnlyList<TalkLine>? turn = null;
        gate.TurnReady += lines => turn = lines;
        gate.SetMicSpeaking(true);
        Assert.Throws<IOException>((Action)(() =>
        {
            using var pending = gate.BeginRecognition(false);
            throw new IOException("ASR unavailable");
        }));
        gate.AddLine(new(TalkIdentity.Danmaku, "观众", "在吗", null));
        clock.Now += TimeSpan.FromSeconds(1);
        gate.Tick();
        Assert.Single(turn!);
    }

    [Fact]
    public void OverlappingRecognition_WaitsForAllSegmentsAndPreservesCaptureOrder()
    {
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30), () => clock.Now);
        IReadOnlyList<TalkLine>? turn = null;
        gate.TurnReady += lines => turn = lines;
        gate.SetMicSpeaking(true);
        using var first = gate.BeginRecognition(false);
        gate.SetMicSpeaking(true);
        using var second = gate.BeginRecognition(false);
        gate.AddLine(new(TalkIdentity.Self, "我", "第二句", null, StartedAt: clock.Now.AddSeconds(1)));
        second.Dispose();
        clock.Now += TimeSpan.FromSeconds(2);
        gate.Tick();
        Assert.Null(turn);
        gate.AddLine(new(TalkIdentity.Self, "我", "第一句", null, StartedAt: clock.Now.AddSeconds(-2)));
        first.Dispose();
        clock.Now += TimeSpan.FromSeconds(1);
        gate.Tick();
        Assert.Equal(new[] { "第一句", "第二句" }, turn!.Select(l => l.Text));
    }

    [Fact]
    public void CompletedAsr_DoesNotClearNewSpeech_AndInvalidatesOldAnswer()
    {
        var clock = new Clock();
        using var gate = new DualPartyTurnGate(TimeSpan.FromMilliseconds(30), () => clock.Now);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        gate.AddLine(new(TalkIdentity.Self, "我", "你好", null));
        clock.Now += TimeSpan.FromSeconds(1);
        gate.Tick();
        var revision = gate.ActiveTurnRevision;
        Assert.True(gate.CanCommit(revision));
        gate.SetMicSpeaking(true);
        using var pending = gate.BeginRecognition(false);
        gate.SetMicSpeaking(true); // next utterance has not ended
        pending.Dispose();
        Assert.False(gate.CanCommit(revision));
        gate.Requeue(turns[0]);
        gate.SetAiSpeaking(false);
        clock.Now += TimeSpan.FromSeconds(1);
        gate.Tick();
        Assert.Single(turns);
        gate.SetMicSpeaking(false);
        clock.Now += TimeSpan.FromSeconds(1);
        gate.Tick();
        Assert.Equal(2, turns.Count);
    }

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
