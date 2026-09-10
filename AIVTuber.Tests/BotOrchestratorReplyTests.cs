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

        public async IAsyncEnumerable<byte[]> StreamAsync(
            string text,
            string voiceId,
            string? emotion,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            yield return System.Text.Encoding.UTF8.GetBytes(text);
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
