using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorReplyTests
{
    [Fact]
    public async Task Pass_DoesNotCallTtsOrStartSpeaking()
    {
        var llm = new FixedLlm("【PASS】");
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct)) { }
            },
            () => { },
            triggerHotkeyAsync: null);
        var starts = 0;
        var spoken = new List<string>();
        ClassifiedReply? committed = null;
        orchestrator.OnAiStartSpeaking += (_, _) => starts++;
        orchestrator.OnSentenceReady += (_, s) => spoken.Add(s);
        orchestrator.OnReplyCommitted += (_, r) => committed = r;

        await orchestrator.ProcessTextAsync("使用者（小明）：在吗", [], bypassWake: true);

        Assert.Equal(ReplyKind.Pass, committed?.Kind);
        Assert.Equal(0, tts.CallCount);
        Assert.Equal(0, starts);
        Assert.Empty(spoken);
    }

    [Fact]
    public async Task InnerThought_DoesNotCallTts()
    {
        var llm = new FixedLlm("（先听他们说完）");
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct)) { }
            },
            () => { },
            triggerHotkeyAsync: null);
        ClassifiedReply? committed = null;
        orchestrator.OnReplyCommitted += (_, r) => committed = r;

        await orchestrator.ProcessTextAsync("对方主播（笑笑）：哈喽", [], bypassWake: true);

        Assert.Equal(ReplyKind.InnerThought, committed?.Kind);
        Assert.Equal("先听他们说完", committed?.Thought);
        Assert.Equal(0, tts.CallCount);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task NewSpeechBeforePlayback_DiscardsReplyWithoutPublicEffects(bool duringTts)
    {
        var valid = true;
        var llm = new FixedLlm("你好");
        var tts = new CountingTts { BeforeChunk = () => valid = false };
        using var player = new AudioPlayer();
        var played = 0;
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct)) played++;
            }, () => { }, triggerHotkeyAsync: null);
        var committed = 0;
        var starts = 0;
        var subtitles = 0;
        orchestrator.OnReplyCommitted += (_, _) => committed++;
        orchestrator.OnAiStartSpeaking += (_, _) => starts++;
        orchestrator.OnSentenceReady += (_, _) => subtitles++;
        if (!duringTts) valid = false;
        await orchestrator.ProcessTextAsync("你好", [], bypassWake: true, canCommit: () => valid);
        Assert.Equal(0, played);
        Assert.Equal(0, committed);
        Assert.Equal(0, starts);
        Assert.Equal(0, subtitles);
        Assert.Equal(duringTts ? 1 : 0, tts.CallCount);
    }

    [Fact]
    public async Task NewSpeechAfterPlaybackStarts_DoesNotInterruptAudio()
    {
        var valid = true;
        var tts = new CountingTts { AfterFirstChunk = () => valid = false };
        using var player = new AudioPlayer();
        var played = 0;
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), new FixedLlm("你好"), tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var _ in chunks.WithCancellation(ct)) played++;
            }, () => { }, triggerHotkeyAsync: null);
        var committed = 0;
        orchestrator.OnReplyCommitted += (_, _) => committed++;
        await orchestrator.ProcessTextAsync("你好", [], bypassWake: true, canCommit: () => valid);
        Assert.Equal(2, played);
        Assert.Equal(1, committed);
    }

    private sealed class FixedLlm(string text) : ILlmClient
    {
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            OnSentenceReady?.Invoke(this, text);
            yield return text;
            await Task.CompletedTask;
        }
    }

    private sealed class CountingTts : ITtsClient
    {
        public int CallCount { get; private set; }
        public Action? BeforeChunk { get; init; }
        public Action? AfterFirstChunk { get; init; }

        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text,
            string voiceId,
            string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            BeforeChunk?.Invoke();
            yield return System.Text.Encoding.UTF8.GetBytes(text);
            if (AfterFirstChunk is not null)
            {
                AfterFirstChunk();
                yield return System.Text.Encoding.UTF8.GetBytes(text);
            }
            await Task.CompletedTask;
        }
    }

    private sealed class UnusedAsr : IAsrClient
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
