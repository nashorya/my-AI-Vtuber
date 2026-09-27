using AIVTuber.Core.Bot.Pacing;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class CorticoPacerTests
{
    [Fact]
    public async Task EachPiece_IsSynthesizedOnce_PlayedWhenCorticoAsks_AndCommittedWithCleanText()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("<微笑>你好。"), default);
        await pacer.SubmitAsync(new SpeechItem("【点头】再见。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["你好。", "再见。"], kit.Synthesized);
        Assert.Equal(["你好。", "再见。"], kit.Played);
        Assert.Equal(["你好。", "再见。"], kit.Committed);
        Assert.Equal(["<微笑>你好。", "【点头】再见。"], cortico.Feeds);
        lock (cortico.Log)
        {
            Assert.Contains("started:1", cortico.Log);
            Assert.True(cortico.Log.IndexOf("started:1") < cortico.Log.IndexOf("ended:1"));
        }
    }

    [Fact]
    public async Task NothingIsCommittedOrPlayed_BeforeCorticoAsksToPlay()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico { HoldPlay = true, MaxHoldMs = 60_000 };
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("synthEnd:1"); });
        await Task.Delay(100);
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
    }

    [Fact]
    public async Task Play_WhenTheTurnMayNoLongerSpeak_ReportsStopped_AndEndsTheTurn()
    {
        var kit = new PacingTestKit { Speakable = false };
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("你好。"), default);
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(kit.Committed);
        Assert.Empty(kit.Played);
        lock (cortico.Log) Assert.Contains("stopped:1", cortico.Log);
    }

    [Fact]
    public async Task Play_BeforeSynthesisFinishes_PlaysWhatArrivedAndStreamsTheRest()
    {
        var kit = new PacingTestKit();
        var release = new TaskCompletionSource();
        var cortico = new FakeCortico();
        var ports = kit.Ports with { Synthesize = (text, _, ct) => TwoChunks(text, release.Task, ct) };
        await using var pacer = new CorticoPacer(cortico, ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("前半后半"), default);
        await TestWait.Until(() => { lock (cortico.Log) return cortico.Log.Contains("started:1"); });
        release.SetResult();
        await pacer.CompleteAsync(default);
        Assert.Equal(["前半后半"], kit.Played);
    }

    [Fact]
    public async Task ActionOnlySegment_WaitsForTheNextSpokenOne()
    {
        var kit = new PacingTestKit();
        var cortico = new FakeCortico();
        await using var pacer = new CorticoPacer(cortico, kit.Ports, CancellationToken.None);
        await pacer.SubmitAsync(new SpeechItem("【点头】"), default);
        Assert.Equal(0, cortico.Begins);
        await pacer.SubmitAsync(new SpeechItem("好的。"), default);
        await pacer.CompleteAsync(default);
        Assert.Equal(["【点头】好的。"], cortico.Feeds);
    }

    private static async IAsyncEnumerable<byte[]> TwoChunks(string text, Task release,
        [System.Runtime.CompilerServices.EnumeratorCancellation] CancellationToken ct = default)
    {
        yield return System.Text.Encoding.UTF8.GetBytes(text[..2]);
        await release.WaitAsync(ct);
        yield return System.Text.Encoding.UTF8.GetBytes(text[2..]);
    }
}
