using AIVTuber.Core.Bot;

namespace AIVTuber.Tests;

public class ConversationTurnGateTests
{
    [Fact]
    public void ContinuousInput_DispatchesByMaximumWait()
    {
        var now = DateTime.UtcNow;
        using var gate = new ConversationTurnGate(now: () => now);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        for (var i = 0; i < 5; i++)
        {
            gate.AddLine(new(TalkIdentity.Self, "搭档", $"第{i}句", null));
            now += TimeSpan.FromMilliseconds(200);
        }
        Assert.Empty(turns);
        gate.Tick();
        Assert.Equal(5, Assert.Single(turns).Count);
    }

    [Fact]
    public void NewInput_DoesNotInvalidateActiveTurn_AndRunsOnceAfterIt()
    {
        var now = DateTime.UtcNow;
        using var gate = new ConversationTurnGate(now: () => now);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        gate.AddLine(new(TalkIdentity.Self, "搭档", "大肥鱼你觉得呢", null));
        now += TimeSpan.FromMilliseconds(400);
        gate.Tick();
        var firstRevision = gate.ActiveTurnRevision;
        gate.AddLine(new(TalkIdentity.Opponent, "对方", "嗯嗯", null));
        now += TimeSpan.FromSeconds(5);
        gate.Tick();
        Assert.True(gate.CanCommit(firstRevision));
        Assert.Single(turns);
        gate.CompleteTurn(firstRevision);
        Assert.Equal(2, turns.Count);
        Assert.Equal("嗯嗯", Assert.Single(turns[1]).Text);
        Assert.False(gate.CanCommit(firstRevision));
        gate.CompleteTurn(firstRevision); // late completion cannot release the next turn
        Assert.True(gate.CanCommit(gate.ActiveTurnRevision));
    }

    [Fact]
    public void Stop_InvalidatesAndClearsPending_WithoutRequeueingOldInput()
    {
        using var gate = new ConversationTurnGate(TimeSpan.Zero);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        gate.AddLine(new(TalkIdentity.Self, "搭档", "第一个问题", null));
        var firstRevision = gate.ActiveTurnRevision;
        gate.AddLine(new(TalkIdentity.Opponent, "对方", "旧补充", null));
        gate.Clear();
        Assert.False(gate.CanCommit(firstRevision));
        gate.AddLine(new(TalkIdentity.Self, "搭档", "新问题", null));
        Assert.Single(turns);
        gate.CompleteTurn(firstRevision);
        Assert.Equal("新问题", Assert.Single(turns[1]).Text);
    }

    [Fact]
    public void CoalescedInputs_AreSortedByCaptureTime()
    {
        var now = DateTime.UtcNow;
        using var gate = new ConversationTurnGate(now: () => now);
        IReadOnlyList<TalkLine>? turn = null;
        gate.TurnReady += lines => turn = lines;
        gate.AddLine(new(TalkIdentity.Opponent, "对方", "回答", null, StartedAt: now));
        gate.AddLine(new(TalkIdentity.Self, "搭档", "问题", null, StartedAt: now.AddSeconds(-1)));
        now += TimeSpan.FromMilliseconds(400);
        gate.Tick();
        Assert.Equal(new[] { "问题", "回答" }, turn!.Select(l => l.Text));
    }

    [Fact]
    public async Task SingleInput_DispatchesWithoutAnotherAudioEvent()
    {
        using var gate = new ConversationTurnGate(TimeSpan.FromMilliseconds(20));
        var ready = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        gate.TurnReady += _ => ready.TrySetResult(true);
        gate.AddLine(new(TalkIdentity.Self, "搭档", "你怎么看", null));
        await ready.Task.WaitAsync(TimeSpan.FromSeconds(2));
    }

    [Fact]
    public void Disposal_PreventsCommitAndDispatch()
    {
        using var gate = new ConversationTurnGate(TimeSpan.Zero);
        var turns = 0;
        gate.TurnReady += _ => turns++;
        gate.AddLine(new(TalkIdentity.Self, "搭档", "你好", null));
        var revision = gate.ActiveTurnRevision;
        gate.Dispose();
        gate.AddLine(new(TalkIdentity.Self, "搭档", "还在吗", null));
        gate.CompleteTurn(revision);
        Assert.False(gate.CanCommit(revision));
        Assert.Equal(1, turns);
    }
}
