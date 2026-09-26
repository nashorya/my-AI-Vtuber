using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

/// <summary>AUTH-04 / R05 (main): local playback must stop without waiting for cloud work
/// that is slow to honour cancellation, and late chunks from that work must not play.</summary>
public sealed class InterruptLocalStopTests
{
    [Fact]
    public async Task Interrupt_StopsPlaybackBeforeUncooperativeTtsFinishes()
    {
        var release = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var firstPlayed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var stopped = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var played = 0;
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new NoAsr(), new OneLineLlm("你好。"), new StallingTts(release.Task), player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct))
                {
                    Interlocked.Increment(ref played);
                    firstPlayed.TrySetResult();
                }
            },
            () => stopped.TrySetResult(),
            triggerHotkeyAsync: null);

        try
        {
            var turn = orchestrator.ProcessTextAsync("说点什么", [], bypassWake: true);
            await firstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));

            var interrupt = Task.Run(orchestrator.Interrupt);
            var stoppedInTime = await Task.WhenAny(stopped.Task, Task.Delay(TimeSpan.FromSeconds(2))) == stopped.Task;
            Assert.True(stoppedInTime, "playback was not stopped while the TTS stream ignored cancellation");

            release.TrySetResult();
            await interrupt.WaitAsync(TimeSpan.FromSeconds(5));
            await turn.WaitAsync(TimeSpan.FromSeconds(5));
            Assert.Equal(1, Volatile.Read(ref played));
        }
        finally
        {
            // Never leave the stalled stream blocking orchestrator.Dispose.
            release.TrySetResult();
        }
    }

    private sealed class StallingTts(Task release) : ITtsClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            yield return new byte[320];
            await release; // a provider that ignores cancellation until its socket gives up
            yield return new byte[320];
        }
    }

    private sealed class OneLineLlm(string text) : ILlmClient
    {
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected { add { } remove { } }
        public event EventHandler<string>? OnActionDetected { add { } remove { } }
        public event EventHandler<string>? OnPoseDetected { add { } remove { } }

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history, string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            OnSentenceReady?.Invoke(this, text);
            yield return text;
            await Task.CompletedTask;
        }
    }

    private sealed class NoAsr : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AsrResult(""));

        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
            IAsyncEnumerable<byte[]> audioStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
