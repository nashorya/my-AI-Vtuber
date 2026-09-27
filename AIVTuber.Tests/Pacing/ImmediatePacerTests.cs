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

    private static async IAsyncEnumerable<byte[]> One(string text)
    {
        await Task.Yield();
        yield return System.Text.Encoding.UTF8.GetBytes(text);
    }
}
