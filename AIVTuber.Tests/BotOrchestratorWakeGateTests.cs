using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorWakeGateTests
{
    [Fact]
    public async Task ProcessTextAsync_PkAnnounceWithoutKeyword_DoesNotCallLlmOrTts()
    {
        var llm = new CountingLlm();
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

        var gate = new WakeGate();
        orchestrator.ShouldSpeak = probe => gate.ShouldSpeak(
            isPkMode: true,
            keywords: ["小娜"],
            wakeHoldSec: 45,
            text: probe,
            nowMs: 1_000);

        var pkAnnounce = "（PK 开始了，对手是 对面主播，有 1000 个粉丝）";
        await orchestrator.ProcessTextAsync(pkAnnounce, []);

        Assert.Equal(0, llm.CallCount);
        Assert.Equal(0, tts.CallCount);
        Assert.False(orchestrator.IsProcessing);
    }

    [Fact]
    public async Task ProcessTextAsync_PkModeWithKeyword_CallsLlm()
    {
        var llm = new CountingLlm();
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

        var gate = new WakeGate();
        orchestrator.ShouldSpeak = probe => gate.ShouldSpeak(
            isPkMode: true,
            keywords: ["小娜"],
            wakeHoldSec: 45,
            text: probe,
            nowMs: 1_000);

        await orchestrator.ProcessTextAsync("（弹幕 观众：小娜怎么看）", [], wakeProbe: "小娜怎么看");

        Assert.Equal(1, llm.CallCount);
        Assert.False(orchestrator.IsProcessing);
    }

    private sealed class CountingLlm : ILlmClient
    {
        public int CallCount { get; private set; }
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            CallCount++;
            OnSentenceReady?.Invoke(this, "ok");
            yield return "ok";
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
            cancellationToken.ThrowIfCancellationRequested();
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
