using System.Runtime.CompilerServices;
using AIVTuber.Core.Bot.Pacing;
using AIVTuber.Core.Cortico;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests.Pacing;

public sealed class PacingConcurrencyTests
{
    [Fact]
    public async Task Fallback_PublishesOldQueueBeforeConcurrentSubmitAndComplete()
    {
        var kit = new PacingTestKit();
        var host = new FakeCortico { HoldPlay = true };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var ports = kit.Ports with { Warn = _ => { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); } };
        await using var pacer = new CorticoPacer(host, ports, default, watchInterval: Timeout.InfiniteTimeSpan);
        await pacer.SubmitAsync(new SpeechItem("第一句。"), default);
        var abort = Task.Run(() => ((ICorticoAudioHandler)pacer).Aborted("model-changed"));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            await pacer.SubmitAsync(new SpeechItem("第二句。"), default);
            // Complete may race the diagnostic callback as well as Submit.
            await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["第一句。", "第二句。"], kit.Committed);
        }
        finally { release.Set(); await abort; }
    }

    [Fact]
    public async Task WatchdogDuringPlayGate_DoesNotStartASecondPlayer()
    {
        var kit = new PacingTestKit();
        var host = new FakeCortico { HoldPlay = true };
        using var entered = new ManualResetEventSlim();
        using var release = new ManualResetEventSlim();
        var fallback = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var calls = 0;
        var ports = kit.Ports with
        {
            CanSpeak = () =>
            {
                if (Interlocked.Increment(ref calls) == 1)
                { entered.Set(); Assert.True(release.Wait(TimeSpan.FromSeconds(5))); }
                return true;
            },
            Warn = _ => fallback.TrySetResult()
        };
        await using var pacer = new CorticoPacer(host, ports, default, watchInterval: TimeSpan.FromMilliseconds(10));
        await pacer.SubmitAsync(new SpeechItem("第一句。"), default);
        await pacer.SubmitAsync(new SpeechItem("第二句。"), default);
        var play = Task.Run(() => ((ICorticoAudioHandler)pacer).Play(1));
        try
        {
            Assert.True(entered.Wait(TimeSpan.FromSeconds(5)));
            host.IsAlive = false;
            await fallback.Task.WaitAsync(TimeSpan.FromSeconds(3));
        }
        finally { release.Set(); await play; }
        await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
        Assert.Equal(["第一句。", "第二句。"], kit.Committed);
        Assert.Equal(["第一句。第二句。"], kit.Played);
    }

    [Fact]
    public async Task BlockedVisualReport_DoesNotBlockLocalAudio_AndFallbackKeepsSynthesisAlive()
    {
        var blocked = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var resume = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = 0;
        var reports = 0;
        var audio = PieceAudio.Start("test", ct => Chunks(resume.Task, ct),
            _ => { Interlocked.Increment(ref reports); entered.TrySetResult(); return blocked.Task; },
            () => {}, _ => { Interlocked.Increment(ref finished); return Task.CompletedTask; }, default);
        try
        {
            await entered.Task.WaitAsync(TimeSpan.FromSeconds(3));
            audio.StopForwarding();
            resume.SetResult();
            var read = Task.Run(async () =>
            {
                var bytes = new List<byte>();
                await foreach (var chunk in audio.ReadAllAsync(default)) bytes.AddRange(chunk);
                return bytes;
            });
            Assert.Equal(new byte[] { 1, 2, 3, 4 }, await read.WaitAsync(TimeSpan.FromSeconds(3)));
            Assert.Equal(1, reports);
            Assert.Equal(0, finished);
        }
        finally { audio.Cancel(); blocked.TrySetResult(); resume.TrySetResult(); }
    }

    [Fact]
    public async Task BlockedFeedAcknowledgement_DoesNotPreventVoiceOnlyCompletion()
    {
        var kit = new PacingTestKit();
        var acknowledgement = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var host = new FakeCortico { HoldPlay = true, FeedAcknowledgement = acknowledgement.Task };
        await using var pacer = new CorticoPacer(host, kit.Ports, default,
            watchInterval: TimeSpan.FromMilliseconds(10));
        try
        {
            var submit = pacer.SubmitAsync(new SpeechItem("第一句。"), default);
            host.IsAlive = false;
            await submit.WaitAsync(TimeSpan.FromSeconds(3));
            await pacer.SubmitAsync(new SpeechItem("第二句。"), default);
            await pacer.CompleteAsync(default).WaitAsync(TimeSpan.FromSeconds(3));
            Assert.Equal(["第一句。", "第二句。"], kit.Committed);
        }
        finally { acknowledgement.TrySetException(new IOException("Late IPC failure")); }
    }

    private static async IAsyncEnumerable<byte[]> Chunks(Task resume,
        [EnumeratorCancellation] CancellationToken ct)
    {
        yield return [1, 2];
        await resume.WaitAsync(ct);
        yield return [3, 4];
    }
}
