using System.Reflection;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Vts;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorLifecycleTests
{
    [Fact]
    public void Orchestrator_never_subscribes_to_llm_events_and_dispose_is_idempotent()
    {
        // Protocol v2 replies arrive as a stream; the legacy per-sentence LLM events are not used.
        var llm = new RecordingLlmClient();
        using var player = new AudioPlayer();
        var orchestrator = CreateOrchestrator(llm, player);

        Assert.Equal((0, 0, 0), llm.HandlerCounts);

        orchestrator.Dispose();
        orchestrator.Dispose();

        Assert.Equal((0, 0, 0), llm.HandlerCounts);
    }

    [Fact]
    public void Dispose_detaches_audio_player_handlers_when_vts_is_present()
    {
        var llm = new RecordingLlmClient();
        using var player = new AudioPlayer();
        using var vts = new VtsClient(new VtsConfig());
        var rmsBefore = HandlerCount(player, "RmsUpdated");
        var finishedBefore = HandlerCount(player, "PlaybackFinished");
        var orchestrator = CreateOrchestrator(llm, player, vts);

        Assert.Equal(rmsBefore + 1, HandlerCount(player, "RmsUpdated"));
        Assert.Equal(finishedBefore + 1, HandlerCount(player, "PlaybackFinished"));

        orchestrator.Dispose();

        Assert.Equal(rmsBefore, HandlerCount(player, "RmsUpdated"));
        Assert.Equal(finishedBefore, HandlerCount(player, "PlaybackFinished"));
    }

    [Fact]
    public void Disposed_orchestrator_is_collectible_while_publishers_remain_alive()
    {
        var llm = new RecordingLlmClient();
        using var player = new AudioPlayer();
        var weak = CreateDisposedWeakReference(llm, player);

        Assert.Equal((0, 0, 0), llm.HandlerCounts);
        for (var attempt = 0; attempt < 5 && weak.IsAlive; attempt++)
        {
            GC.Collect();
            GC.WaitForPendingFinalizers();
            GC.Collect();
        }

        Assert.False(weak.IsAlive);
        GC.KeepAlive(llm);
        GC.KeepAlive(player);
    }

    private static BotOrchestrator CreateOrchestrator(
        RecordingLlmClient llm,
        AudioPlayer player,
        VtsClient? vts = null)
        => new(new NoopAsrClient(), llm, new NoopTtsClient(), player, new TtsConfig(), vts);

    [MethodImpl(MethodImplOptions.NoInlining)]
    private static WeakReference CreateDisposedWeakReference(RecordingLlmClient llm, AudioPlayer player)
    {
        var orchestrator = CreateOrchestrator(llm, player);
        var weak = new WeakReference(orchestrator);
        orchestrator.Dispose();
        return weak;
    }

    private static int HandlerCount(object publisher, string eventName)
    {
        var field = publisher.GetType().GetField(eventName, BindingFlags.Instance | BindingFlags.NonPublic);
        Assert.NotNull(field);
        return (field.GetValue(publisher) as Delegate)?.GetInvocationList().Length ?? 0;
    }

    private sealed class NoopAsrClient : IAsrClient
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

    private sealed class NoopTtsClient : ITtsClient
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

    private sealed class RecordingLlmClient : ILlmClient, IReplyProtocolStream
    {
        public string ReplyProtocol => "v2";
        public IAsyncEnumerable<ReplyStreamEvent> StreamEventsAsync(List<Message> history, string userInput,
            CancellationToken cancellationToken = default) =>
            AIVTuber.Tests.Cortico.LegacyAsV2.Events(StreamAsync(history, userInput, cancellationToken), cancellationToken);
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

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            await Task.CompletedTask;
            yield break;
        }

        public void RaiseSentence(string value) => _sentenceReady?.Invoke(this, value);
        public void RaiseEmotion(string value) => _emotionDetected?.Invoke(this, value);
        public void RaiseAction(string value) => _actionDetected?.Invoke(this, value);

        private static int Count(Delegate? handlers) => handlers?.GetInvocationList().Length ?? 0;
    }
}
