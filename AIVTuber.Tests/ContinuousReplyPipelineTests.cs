#pragma warning disable CS0067
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public class ContinuousReplyPipelineTests
{
    private sealed class Sink : IAvatarMotionSink
    {
        public int Submits, Cancels;
        public long LastGeneration;
        public void Submit(long generation, AvatarIntent intent) { Submits++; LastGeneration = generation; }
        public void Cancel(long generation) { if (generation == LastGeneration) Cancels++; }
        public void OnRms(float rms) { }
    }
    private sealed class Llm(string reply) : ILlmClient, IAvatarReplySource
    {
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
        public bool Fail;
        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            Texts.Add(text);
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
    [InlineData("你好", 1, 1)]
    [InlineData("（想吃蛋糕）", 1, 0)]
    [InlineData("【PASS】", 0, 0)]
    public async Task ReplyKindControlsSpeechAndAvatarIndependently(string reply, int expectedMoves, int expectedTts)
    {
        var llm = new Llm(reply); var tts = new Tts(); var sink = new Sink();
        using var player = new AudioPlayer(); var hotkeys = 0;
        using var orchestrator = new BotOrchestrator(new Asr(), llm, tts, player, new(), null,
            new VtsConfig { EmotionMap = new() { ["happy"] = "one" }, ActionMap = new() { ["wave"] = "two" } },
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { },
            (_, _) => { hotkeys++; return Task.CompletedTask; });
        var subtitles = new List<string>(); ClassifiedReply? committed = null;
        orchestrator.OnSentenceReady += (_, text) => subtitles.Add(text);
        orchestrator.OnReplyCommitted += (_, r) => committed = r;
        orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, firstRead) =>
        {
            await foreach (var _ in chunks.WithCancellation(ct))
            {
                Assert.Equal(0, sink.Submits); // TTS network data does not start motion.
                firstRead();
            }
        });
        await orchestrator.ProcessTextAsync("hello", [], bypassWake: true);
        Assert.Equal(expectedMoves, sink.Submits); Assert.Equal(expectedTts, tts.Texts.Count); Assert.Equal(0, hotkeys);
        Assert.All(tts.Texts.Concat(subtitles), text => { Assert.DoesNotContain("avatar", text); Assert.DoesNotContain("targets", text); });
        if (expectedTts == 0) Assert.Empty(subtitles);
        if (reply == "【PASS】") Assert.Equal(ReplyKind.Pass, committed!.Value.Kind);
    }
    [Theory]
    [InlineData(true)]
    [InlineData(false)]
    public async Task FailedOrUncommittedTurnDoesNotMove(bool ttsFails)
    {
        var llm = new Llm("你好"); var tts = new Tts { Fail = ttsFails }; var sink = new Sink();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(new Asr(), llm, tts, player, new(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { }, null);
        orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, start) => { await foreach (var _ in chunks.WithCancellation(ct)) start(); });
        await orchestrator.ProcessTextAsync("hello", [], bypassWake: true, canCommit: () => ttsFails);
        Assert.Equal(0, sink.Submits);
    }
    [Fact]
    public async Task RewireTwentyTimesDoesNotAccumulateSubscriptions()
    {
        var llm = new Llm("（想吃蛋糕）"); var sink = new Sink();
        using var player = new AudioPlayer();
        for (var i = 0; i < 20; i++)
        {
            using var orchestrator = new BotOrchestrator(new Asr(), llm, new Tts(), player, new(), null, null,
                async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { }, null);
            orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, start) => { await foreach (var _ in chunks.WithCancellation(ct)) start(); });
            Assert.Equal(1, llm.Subscriptions);
            await orchestrator.ProcessTextAsync("hello", [], bypassWake: true);
        }
        Assert.Equal(0, llm.Subscriptions); Assert.Equal(20, sink.Submits);
    }
    [Fact]
    public async Task CancelledLateLlmEventCannotMove()
    {
        var llm = new Llm("（想吃蛋糕）"); var sink = new Sink(); using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(new Asr(), llm, new Tts(), player, new(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } }, () => { }, null);
        orchestrator.ConfigureContinuousControl(sink, async (chunks, ct, start) => { await foreach (var _ in chunks.WithCancellation(ct)) start(); });
        llm.EmitAfterCancellation = true;
        var processing = orchestrator.ProcessTextAsync("hello", [], bypassWake: true);
        await llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(2));
        await Task.Run(orchestrator.Interrupt).WaitAsync(TimeSpan.FromSeconds(3));
        await processing;
        Assert.Equal(0, sink.Submits);
    }
}
