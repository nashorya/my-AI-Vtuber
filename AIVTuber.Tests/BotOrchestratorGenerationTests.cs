using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorGenerationTests
{
    [Fact]
    public async Task NewTurn_SuppressesLateOldStateAudioAndActionAndAwaitsNewCommand()
    {
        var llm = new ControlledLlm();
        var tts = new FakeTts();
        using var player = new AudioPlayer();
        var played = new List<string>();
        var actions = new List<string>();
        var actionCompleted = NewSignal();
        var config = new VtsConfig
        {
            ActionMap = new Dictionary<string, string>
            {
                ["old-action"] = "old-hotkey",
                ["new-action"] = "new-hotkey",
            },
        };
        using var orchestrator = new BotOrchestrator(
            new FakeAsr(), llm, tts, player, new TtsConfig(), null, config,
            async (chunks, ct) =>
            {
                await foreach (var chunk in chunks.WithCancellation(ct))
                    played.Add(System.Text.Encoding.UTF8.GetString(chunk));
            },
            () => { },
            async (hotkey, ct) =>
            {
                if (hotkey == "new-hotkey") await actionCompleted.Task.WaitAsync(ct);
                actions.Add(hotkey);
            });
        var sentences = new List<string>();
        var emotions = new List<string>();
        var starts = 0;
        var stops = 0;
        orchestrator.OnSentenceReady += (_, sentence) => sentences.Add(sentence);
        orchestrator.OnEmotionDetected += (_, emotion) => emotions.Add(emotion);
        orchestrator.OnAiStartSpeaking += (_, _) => starts++;
        orchestrator.OnAiStopSpeaking += (_, _) => stops++;

        var oldTurn = orchestrator.ProcessTextAsync("old", []);
        await llm.OldStarted.Task;
        var newTurn = orchestrator.ProcessTextAsync("new", []);
        llm.ReleaseOld.TrySetResult();
        await llm.NewStarted.Task;

        Assert.False(newTurn.IsCompleted);
        actionCompleted.TrySetResult();
        await Task.WhenAll(oldTurn, newTurn);

        Assert.Equal(["new sentence."], sentences);
        Assert.Equal(["happy"], emotions);
        Assert.Equal(["new sentence."], played);
        Assert.Equal(["new-hotkey"], actions);
        Assert.Equal(1, starts);
        Assert.Equal(1, stops);
        Assert.False(orchestrator.IsProcessing);
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

        public void EmitEmotion(string emotion) => OnEmotionDetected?.Invoke(this, emotion);

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            if (userInput == "old")
            {
                OldStarted.TrySetResult();
                await ReleaseOld.Task;
                OnEmotionDetected?.Invoke(this, "sad");
                OnActionDetected?.Invoke(this, "old-action");
                OnSentenceReady?.Invoke(this, "old sentence.");
                yield return "old sentence.";
                yield break;
            }

            NewStarted.TrySetResult();
            OnEmotionDetected?.Invoke(this, "happy");
            OnActionDetected?.Invoke(this, "new-action");
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
