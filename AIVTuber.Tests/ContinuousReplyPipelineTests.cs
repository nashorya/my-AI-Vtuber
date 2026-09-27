#pragma warning disable CS0067
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests;

public class ContinuousReplyPipelineTests
{
    private sealed class Sink : IAvatarMotionSink
    {
        public int Submits, Cancels, AudioSamples;
        public long LastGeneration;
        public void Submit(long generation, AvatarIntent intent) { Submits++; LastGeneration = generation; }
        public void Cancel(long generation) { if (generation == LastGeneration) Cancels++; }
        public void OnRms(float rms) { if (rms > 0) AudioSamples++; }
    }
    private sealed class Llm(string reply) : ILlmClient, IReplyProtocolStream, IAvatarReplySource
    {
    public string ReplyProtocol => "v2";
    public IAsyncEnumerable<ReplyStreamEvent> StreamEventsAsync(List<Message> history, string userInput,
        CancellationToken cancellationToken = default) =>
        AIVTuber.Tests.Cortico.LegacyAsV2.Events(StreamAsync(history, userInput, cancellationToken), cancellationToken);
        public int Subscriptions;
        private EventHandler<AvatarReplyPlan>? _plan;
        public event EventHandler<AvatarReplyPlan>? OnAvatarPlanReady { add { _plan += value; Subscriptions++; } remove { _plan -= value; Subscriptions--; } }
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;
        public bool EmitAfterCancellation;
        public TaskCompletionSource Entered = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public async IAsyncEnumerable<string> StreamAsync(List<Message> history, string userInput, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Entered.TrySetResult();
            if (EmitAfterCancellation)
                while (!cancellationToken.IsCancellationRequested) await Task.Delay(5);
            _plan?.Invoke(this, new(reply, new(new Dictionary<string, float> { ["headRoll"] = .4f })));
            OnEmotionDetected?.Invoke(this, "happy");
            OnActionDetected?.Invoke(this, "wave");
            yield return reply;
            await Task.CompletedTask;
        }
    }
    private sealed class Tts : ITtsClient
    {
        public readonly List<string> Texts = [];
        public readonly List<string?> Emotions = [];
        public bool Fail;
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
            Emotions.Add(emotion);
            if (Fail) throw new IOException("synthesis failed");
            yield return new byte[40];
            await Task.CompletedTask;
        }
    }
    private sealed class Asr : IAsrClient
    {
        public Task<AsrResult> RecognizeAsync(byte[] pcm16k, CancellationToken cancellationToken = default) => Task.FromResult(new AsrResult("unused"));
        public async IAsyncEnumerable<AsrResult> StreamRecognizeAsync(IAsyncEnumerable<byte[]> audioStream, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        { await Task.CompletedTask; yield break; }
    }
    [Theory]
    [InlineData("speak", 1, 1)]
    [InlineData("thought", 0, 0)]
    [InlineData("pass", 0, 0)]
    public async Task ReplyKindControlsSpeechAndAvatarIndependently(string reply, int expectedMoves, int expectedTts)
    {
        var llm = reply switch
        {
            "speak" => new ChunkedLlm("v2", [V2.Speak, V2.Emotion("happy"), V2.Avatar("headRoll", .4f), V2.Seg(0, "你好"), V2.End], ["headRoll"]),
            "thought" => new ChunkedLlm("v2", V2.Thought("想吃蛋糕"), V2.End),
            _ => new ChunkedLlm("v2", V2.Pass, V2.End),
        };
        var tts = new Tts(); var sink = new Sink();
        using var player = new AudioPlayer(); var hotkeys = 0;
        using var orchestrator = new BotOrchestrator(new Asr(), llm, tts, player, new(), null,
            new VtsConfig { EmotionMap = new() { ["happy"] = "one" }, ActionMap = new() { ["wave"] = "two" } },
            async (chunks, ct, firstPcm) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { },
            (_, _) => { hotkeys++; return Task.CompletedTask; });
        var subtitles = new List<string>(); ClassifiedReply? committed = null;
        orchestrator.OnSentenceReady += (_, text) => subtitles.Add(text);
        orchestrator.OnReplyCommitted += (_, r) => committed = r;
        orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, firstRead) =>
        {
            await foreach (var _ in chunks.WithCancellation(ct))
            {
                Assert.Equal(0, sink.Submits); // TTS network data does not start motion.
                firstRead?.Invoke();
            }
        });
        await orchestrator.ProcessTextAsync("hello", [], bypassWake: true);
        Assert.Equal(expectedMoves, sink.Submits); Assert.Equal(expectedTts, tts.Texts.Count); Assert.Equal(0, hotkeys);
        if (expectedTts > 0) Assert.Equal("happy", Assert.Single(tts.Emotions));
        Assert.All(tts.Texts.Concat(subtitles), text => { Assert.DoesNotContain("avatar", text); Assert.DoesNotContain("targets", text); });
        if (expectedTts == 0) Assert.Empty(subtitles);
        if (reply == "pass") Assert.Equal(ReplyKind.Pass, committed!.Value.Kind);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrUncommittedTurnDoesNotMove(bool ttsFails)
    {
        var llm = new Llm("你好"); var tts = new Tts { Fail = ttsFails }; var sink = new Sink();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(new Asr(), llm, tts, player, new(), null, null,
            async (chunks, ct, firstPcm) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { }, null);
        orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, start) => { await foreach (var _ in chunks.WithCancellation(ct)) start?.Invoke(); });
        await orchestrator.ProcessTextAsync("hello", [], bypassWake: true, canCommit: () => ttsFails);
        Assert.Equal(0, sink.Submits);
    }
    [Fact]
    public async Task RewireTwentyTimesDoesNotAccumulateSubscriptions()
    {
        var llm = new Llm("（想吃蛋糕）"); var sink = new Sink();
        using var player = new AudioPlayer();
        using var vts = new AIVTuber.Core.Vts.VtsClient(new());
        // Exercise the real audio event subscribers without opening a Windows sound card.
        void EmitAudio() => ((EventHandler<float>?)typeof(AudioPlayer)
            .GetField(nameof(AudioPlayer.RmsUpdated), System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!
            .GetValue(player))?.Invoke(player, .4f);
        for (var i = 0; i < 20; i++)
        {
            using var orchestrator = new BotOrchestrator(new Asr(), llm, new Tts(), player, new(), vts, null,
                async (chunks, ct, firstPcm) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { }, null);
            orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, start) => { await foreach (var _ in chunks.WithCancellation(ct)) start?.Invoke(); });
            EmitAudio();
            Assert.Equal(i + 1, sink.AudioSamples);
            await orchestrator.ProcessTextAsync("hello", [], bypassWake: true);
        }
        EmitAudio(); Assert.Equal(20, sink.AudioSamples);
    }
}
