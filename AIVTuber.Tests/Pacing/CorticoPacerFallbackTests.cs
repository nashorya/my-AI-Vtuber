using AIVTuber.Core.Bot.Pacing;
using AIVTuber.Core.Cortico;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoPacerFallbackTests
{
    [Fact]
    public async Task HeartbeatLost_TheRestPlaysVoiceOnly_InOrder_WithAWarning()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("第一句。"), default);
        await pacer.SubmitAsync(new SpeechItem("第二句。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:2"); });
        cortico.IsAlive = false;
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["第一句。第二句。"], kit.Played);
        Assert.Equal(["第一句。", "第二句。"], kit.Committed);
        Assert.Equal(2, kit.Synthesized.Count);           // already synthesized audio is reused
        Assert.Contains(kit.Warnings, w => w.Contains("皮套异常"));
        Assert.True(cortico.Interrupts >= 1);
    }

    [Fact]
    public async Task HoldBeyondBudget_IsAStall_ButCueResetsTheTimer()
    {
        var kit = new PacingTestKit();
        long now = 0;
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 1000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None, clock: () => now,
            watchInterval: Timeout.InfiniteTimeSpan);
        await pacer.SubmitAsync(new SpeechItem("等一下。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        now += 2500;
        Assert.Null(pacer.CheckStall());                  // 1000 + 2000 not exceeded
        ((ICorticoAudioHandler)pacer).Cue();              // a legitimate beat fired
        now += 2900;
        Assert.Null(pacer.CheckStall());
        now += 200;
        Assert.Equal("皮套超时未开口", pacer.CheckStall());
    }

    [Fact]
    public async Task Aborted_WhilePlaying_FinishesTheCurrentPieceThenTheRestVoiceOnly()
    {
        var kit = new PacingTestKit { HoldPlayback = new TaskCompletionSource() };
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("正在说的这句。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("started:1"); });
        await pacer.SubmitAsync(new SpeechItem("后面这句。"), default);
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        await Task.Delay(100);
        Assert.Equal(["正在说的这句。"], kit.Committed);   // nothing new starts while the current piece plays
        var hold = kit.HoldPlayback!; kit.HoldPlayback = null; hold.SetResult();
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["正在说的这句。", "后面这句。"], kit.Played);
    }

    [Fact]
    public async Task PlayAfterFallback_IsIgnored_NoTextIsSpokenTwice()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("只说一次。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        ((ICorticoAudioHandler)pacer).Play(1);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["只说一次。"], kit.Played);
        Assert.Equal(["只说一次。"], kit.Committed);
    }

    [Fact]
    public async Task SidecarDeadBeforeTheFirstSegment_TheWholeReplyIsVoiceOnly()
    {
        var kit = new PacingTestKit();
        var cortico = new ThrowingCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("<微笑>你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("再见。"), default);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["你好。再见。"], kit.Played);
    }

    [Fact]
    public async Task SegmentsNotYetRequested_AreSpokenAfterTheRequestedOnes()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("甲。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        // The host received the second segment but has not asked to synthesize it yet.
        cortico.Current!.Cut();
        await pacer.SubmitAsync(new SpeechItem("乙。"), default);
        ((ICorticoAudioHandler)pacer).Aborted("model-changed");
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["甲。乙。"], kit.Played);
    }

    private sealed class ThrowingCortico : ICorticoPerformance
    {
        public string ScriptGrammar => "";
        public int MaxHoldMs => 1000;
        public bool IsAlive => false;
        public Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct) =>
            throw new IOException("Cortico sidecar exited");
        public Task InterruptAsync(CancellationToken ct) => Task.CompletedTask;
    }
}
