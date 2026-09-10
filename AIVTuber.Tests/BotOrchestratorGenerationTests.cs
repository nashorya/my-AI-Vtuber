using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorGenerationTests
{
    [Fact]
    public async Task HeldTurn_DropsIncomingTextWithoutCancel()
    {
        var llm = new ControlledLlm();
        var tts = new FakeTts();
        using var player = new AudioPlayer();
        var played = new List<string>();
        using var orchestrator = new BotOrchestrator(
            new FakeAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var chunk in chunks.WithCancellation(ct))
                    played.Add(System.Text.Encoding.UTF8.GetString(chunk));
            },
            () => { },
            triggerHotkeyAsync: null);
        var sentences = new List<string>();
        orchestrator.OnSentenceReady += (_, sentence) => sentences.Add(sentence);

        var oldTurn = orchestrator.ProcessTextAsync("old", []);
        await llm.OldStarted.Task;
        var dropped = orchestrator.ProcessTextAsync("new", []);
        await dropped;
        llm.ReleaseOld.TrySetResult();
        await oldTurn;

        Assert.False(llm.NewStarted.Task.IsCompleted);
        Assert.Equal(["old sentence."], sentences);
        Assert.Equal(["old sentence."], played);
        Assert.False(orchestrator.IsProcessing);
    }

    [Fact]
    public async Task Interrupt_CancelsHeldTurn()
    {
        var llm = new ControlledLlm();
        var tts = new FakeTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new FakeAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct)) { }
            },
            () => { },
            triggerHotkeyAsync: null);

        var turn = orchestrator.ProcessTextAsync("old", []);
        await llm.OldStarted.Task;
        orchestrator.Interrupt();
        await turn;

        Assert.False(orchestrator.IsProcessing);
        Assert.False(llm.NewStarted.Task.IsCompleted);
    }

    private static TaskCompletionSource NewSignal() =>
        new(TaskCreationOptions.RunContinuationsAsynchronously);

    private sealed class ControlledLlm : ILlmClient
    {
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;
        public TaskCompletionSource OldStarted { get; } = NewSignal();
        public TaskCompletionSource ReleaseOld { get; } = NewSignal();
        public TaskCompletionSource NewStarted { get; } = NewSignal();

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (userInput == "old")
            {
                OldStarted.TrySetResult();
                await ReleaseOld.Task.WaitAsync(cancellationToken);
                OnEmotionDetected?.Invoke(this, "sad");
                OnSentenceReady?.Invoke(this, "old sentence.");
                yield return "old sentence.";
                yield break;
            }

            NewStarted.TrySetResult();
            OnSentenceReady?.Invoke(this, "new sentence.");
            yield return "new sentence.";
        }
    }

    private sealed class FakeTts : ITtsClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text,
            string voiceId,
            string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return System.Text.Encoding.UTF8.GetBytes(text);
            await Task.CompletedTask;
        }
    }

    private sealed class FakeAsr : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default) =>
            Task.FromResult(new AsrResult("unused"));

        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
            IAsyncEnumerable<byte[]> audioStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
