using AIVTuber.Core.Bot.Pacing;

namespace AIVTuber.Tests.Pacing;

public sealed class ImmediatePacerTests
{
    [Fact]
    public async Task Segments_AreSynthesizedCommittedAndPlayedInOrder_InOnePlayback()
    {
        var kit = new PacingTestKit();
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("再见。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["你好。", "再见。"], kit.Committed);
        Assert.Equal(["你好。再见。"], kit.Played);
        Assert.Equal(1, kit.FirstPcm);
    }

    [Fact]
    public async Task NotSpeakableAnymore_BeforeAudio_NothingIsCommittedOrPlayed()
    {
        var kit = new PacingTestKit();
        kit.BeforeSynthChunk = _ => { kit.Speakable = false; return Task.CompletedTask; };
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.CompleteAsync(default);
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
    }

    [Fact]
    public async Task ProvidedAudio_IsPlayedWithoutSynthesizingAgain()
    {
        var kit = new PacingTestKit();
        var audio = PieceAudio.Start("已合成。", ct => One("已合成。"), _ => Task.CompletedTask, () => { }, _ => Task.CompletedTask, default);
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        pacer.Enqueue(new SpeechItem("已合成。", Audio: audio));
        await pacer.CompleteAsync(default);
        Assert.Empty(kit.Synthesized);
        Assert.Equal(["已合成。"], kit.Played);
    }

    [Fact]
    public async Task After_DelaysThePlaybackUntilThatTaskEnds()
    {
        var kit = new PacingTestKit();
        var gate = new TaskCompletionSource();
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None, after: gate.Task);
        await pacer.SubmitAsync(new SpeechItem("后说。"), default);
        await Task.Delay(100);
        Assert.Empty(kit.Played);
        gate.SetResult();
        await pacer.CompleteAsync(default);
        Assert.Equal(["后说。"], kit.Played);
    }

    [Fact]
    public async Task RejectedPiece_WithProvidedAudio_StopsItsSynthesis()
    {
        var kit = new PacingTestKit { Speakable = false };
        var cancelled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var audio = PieceAudio.Start("被拒绝。", ct => Endless(ct, cancelled), _ => Task.CompletedTask, () => { }, _ => Task.CompletedTask, default);
        await using var pacer = new ImmediatePacer(kit.Ports, CancellationToken.None);
        pacer.Enqueue(new SpeechItem("被拒绝。", Audio: audio));
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        await cancelled.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(kit.Played);
    }

    [Fact]
    public async Task CancelledTurn_DoesNotWaitForAfter_NorPlay()
    {
        var kit = new PacingTestKit();
        using var turn = new CancellationTokenSource();
        var never = new TaskCompletionSource();
        await using var pacer = new ImmediatePacer(kit.Ports, turn.Token, after: never.Task);
        await pacer.SubmitAsync(new SpeechItem("不会说。"), default);
        turn.Cancel();
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(kit.Played);
        Assert.Empty(kit.Synthesized);
    }

    [Fact]
    public async Task CancelledTurn_ChunksArrivingAfterwardAreNotPlayed()
    {
        var kit = new PacingTestKit();
        using var turn = new CancellationTokenSource();
        var release = new TaskCompletionSource();
        var ports = kit.Ports with { Synthesize = (text, _, _) => IgnoresCancellation(release.Task) };
        await using var pacer = new ImmediatePacer(ports, turn.Token);
        await pacer.SubmitAsync(new SpeechItem("一二"), default);
        await TestWait.Until(() => kit.FirstPcm == 1);
        turn.Cancel();
        release.SetResult();
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Equal(["一"], kit.Played);
    }

    /// <summary>A TTS stream that does not observe cancellation (vendor sockets often don't).</summary>
    private static async IAsyncEnumerable<byte[]> IgnoresCancellation(Task release)
    {
        yield return System.Text.Encoding.UTF8.GetBytes("一");
        await release;
        yield return System.Text.Encoding.UTF8.GetBytes("二");
    }

    private static async IAsyncEnumerable<byte[]> Endless(
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct, TaskCompletionSource cancelled)
    {
        using var reg = ct.Register(() => cancelled.TrySetResult());
        yield return new byte[] { 1, 2 };
        await Task.Delay(Timeout.Infinite, ct);
        yield return new byte[] { 3, 4 };
    }

    private static async IAsyncEnumerable<byte[]> One(string text)
    {
        await Task.Yield();
        yield return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
