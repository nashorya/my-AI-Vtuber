using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class LifecycleConcurrencyVerificationTests
{
    [Fact]
    public void RewireTwentyTimes_LeavesOnlyLiveSharedSubscriptions()
    {
        var llm = new CountingLlmClient();
        using var player = new AudioPlayer();
        BotOrchestrator? current = null;

        for (var i = 0; i < 20; i++)
        {
            current?.Dispose();
            current = CreateOrchestrator(llm, player);
            Assert.Equal((1, 1, 1), llm.HandlerCounts);
        }

        current!.Dispose();
        Assert.Equal((0, 0, 0), llm.HandlerCounts);
    }

    [Fact]
    public void Dispose_RemovesEverySharedLlmSubscription()
    {
        var llm = new CountingLlmClient();
        using var player = new AudioPlayer();
        var orchestrator = CreateOrchestrator(llm, player);

        Assert.Equal((1, 1, 1), llm.HandlerCounts);

        orchestrator.Dispose();

        Assert.Equal((0, 0, 0), llm.HandlerCounts);
    }

    [Fact]
    public void Dispose_IsIdempotentAndDoesNotDetachTheNextGeneration()
    {
        var llm = new CountingLlmClient();
        using var player = new AudioPlayer();
        var old = CreateOrchestrator(llm, player);
        var current = CreateOrchestrator(llm, player);
        Assert.Equal((2, 2, 2), llm.HandlerCounts);

        old.Dispose();
        old.Dispose();

        Assert.Equal((1, 1, 1), llm.HandlerCounts);
        current.Dispose();
        Assert.Equal((0, 0, 0), llm.HandlerCounts);
    }

    [Fact]
    public void Dispose_RacingWithEmissionLeavesNoPostDisposeDeliveries()
    {
        var llm = new CountingLlmClient();
        using var player = new AudioPlayer();
        var orchestrator = CreateOrchestrator(llm, player);
        var deliveries = 0;
        orchestrator.OnSentenceReady += (_, _) => Interlocked.Increment(ref deliveries);

        Parallel.Invoke(
            orchestrator.Dispose,
            () => Parallel.For(0, 100, _ => llm.EmitSentence("race")));
        var afterDispose = Volatile.Read(ref deliveries);

        Parallel.For(0, 100, _ => llm.EmitSentence("late"));

        Assert.Equal(afterDispose, Volatile.Read(ref deliveries));
        Assert.Equal((0, 0, 0), llm.HandlerCounts);
    }

    [Fact]
    public void DisposedOrchestrator_IsCollectibleWhileSharedDependenciesRemainAlive()
    {
        var llm = new CountingLlmClient();
        using var player = new AudioPlayer();
        var weak = CreateDisposedWeakReference(llm, player);

        for (var attempt = 0; attempt < 5 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(
            weak.IsAlive,
            $"Disposed orchestrator remained rooted after 5 GC attempts; LLM handlers={llm.HandlerCounts}.");
        GC.KeepAlive(llm);
        GC.KeepAlive(player);
    }

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedWeakReference(CountingLlmClient llm, AudioPlayer player)
    {
        var orchestrator = CreateOrchestrator(llm, player);
        var weak = new WeakReference(orchestrator);
        orchestrator.Dispose();
        return weak;
    }

    private static BotOrchestrator CreateOrchestrator(CountingLlmClient llm, AudioPlayer player)
        => new(new StubAsrClient(), llm, new StubTtsClient(), player, new TtsConfig());

    private sealed class CountingLlmClient : ILlmClient
    {
        private EventHandler<string>? _sentenceReady;
        private EventHandler<string>? _emotionDetected;
        private EventHandler<string>? _actionDetected;
        private EventHandler<string>? _poseDetected;

        public (int Sentence, int Emotion, int Action) HandlerCounts =>
            (Count(_sentenceReady), Count(_emotionDetected), Count(_actionDetected));

        public event EventHandler<string>? OnSentenceReady
        {
            add => _sentenceReady += value;
            remove => _sentenceReady -= value;
        }

        public event EventHandler<string>? OnEmotionDetected
        {
            add => _emotionDetected += value;
            remove => _emotionDetected -= value;
        }

        public event EventHandler<string>? OnActionDetected
        {
            add => _actionDetected += value;
            remove => _actionDetected -= value;
        }

        public event EventHandler<string>? OnPoseDetected
        {
            add => _poseDetected += value;
            remove => _poseDetected -= value;
        }

        public void EmitSentence(string sentence) => _sentenceReady?.Invoke(this, sentence);

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        private static int Count(Delegate? handlers) => handlers?.GetInvocationList().Length ?? 0;
    }

    private sealed class StubAsrClient : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default)
            => Task.FromResult(new AsrResult(string.Empty));

        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(
            IAsyncEnumerable<byte[]> audioStream,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }

    private sealed class StubTtsClient : ITtsClient
    {
        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text,
            string voiceId,
            string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }
    }
}
