using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Avatar;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Tests;

public sealed class BotOrchestratorReplyTests
{
    [Theory]
    [InlineData("{\"respond\":false,\"speech\":\"你好[emotion:happy]\"}", false)]
    [InlineData("{\"respond\":true,\"speech\":\"我觉得可以。\"}", true)]
    [InlineData("{\"respond\":true,\"speech\":\"没说完", false)]
    [InlineData("PASS", false)]
    [InlineData("[PASS]", false)]
    public async Task StructuredDecision_ControlsAllPublicEffects(string raw, bool speaks)
    {
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        var audio = new List<byte>();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), new FixedLlm(raw), tts, player, new TtsConfig(), null, null,
            async (chunks, ct) =>
            {
                await foreach (var chunk in chunks.WithCancellation(ct)) audio.AddRange(chunk);
            }, () => { }, triggerHotkeyAsync: null);
        var captions = new List<string>();
        var starts = 0;
        var emotions = 0;
        orchestrator.OnSentenceReady += (_, text) => captions.Add(text);
        orchestrator.OnAiStartSpeaking += (_, _) => starts++;
        orchestrator.OnEmotionDetected += (_, _) => emotions++;
        await orchestrator.ProcessTextAsync("大肥鱼，你觉得呢？", [], bypassWake: true, requireStructuredReply: true);
        Assert.Equal(speaks ? 1 : 0, tts.CallCount);
        Assert.Equal(speaks ? 1 : 0, starts);
        Assert.Equal(0, emotions);
        if (speaks)
        {
            Assert.Equal("我觉得可以。", Assert.Single(captions));
            Assert.Equal("我觉得可以。", System.Text.Encoding.UTF8.GetString(audio.ToArray()));
        }
        else
        {
            Assert.Empty(audio);
            Assert.Empty(captions);
        }
    }

    [Fact]
    public async Task StructuredDecision_KeepsSpeakableProse()
    {
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), new FixedLlm("你好"), tts, player, new TtsConfig(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
            () => { }, triggerHotkeyAsync: null);
        var captions = new List<string>();
        orchestrator.OnSentenceReady += (_, text) => captions.Add(text);
        await orchestrator.ProcessTextAsync("大肥鱼，你觉得呢？", [], bypassWake: true, requireStructuredReply: true);
        Assert.Equal(1, tts.CallCount);
        Assert.Equal("你好", Assert.Single(captions));
    }

    [Fact]
    public async Task NewTranscriptDuringSynthesis_DoesNotCancelInvitedReply()
    {
        using var gate = new ConversationTurnGate(TimeSpan.Zero);
        var turns = new List<IReadOnlyList<TalkLine>>();
        gate.TurnReady += turns.Add;
        gate.AddLine(new(TalkIdentity.Self, "搭档", "大肥鱼，你觉得呢？", null));
        var revision = gate.ActiveTurnRevision;
        var tts = new CountingTts
        {
            BeforeChunk = () => gate.AddLine(new(TalkIdentity.Opponent, "对方", "嗯嗯", null))
        };
        using var player = new AudioPlayer();
        var played = 0;
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), new FixedLlm("{\"respond\":true,\"speech\":\"我觉得可以。\"}"),
            tts, player, new TtsConfig(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) played++; },
            () => { }, triggerHotkeyAsync: null);
        await orchestrator.ProcessTextAsync("大肥鱼，你觉得呢？", [], bypassWake: true,
            canCommit: () => gate.CanCommit(revision), requireStructuredReply: true);
        Assert.Equal(1, played);
        Assert.Single(turns);
        gate.CompleteTurn(revision);
        Assert.Equal("嗯嗯", Assert.Single(turns[1]).Text);
    }

    [Fact]
    public async Task InvitedAvatarPlan_SpeaksAndSubmitsHeadYaw()
    {
        var motion = new CaptureMotion();
        var llm = new PlannedLlm("好呀。", new AvatarReplyPlan("好呀。",
            new AvatarIntent(new Dictionary<string, float> { ["headYaw"] = .5f })));
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
            () => { }, triggerHotkeyAsync: null);
        orchestrator.ConfigureContinuousControl(motion, PlayAndStart);
        await orchestrator.ProcessTextAsync("摇摇头呗", [], bypassWake: true, requireStructuredReply: true);
        Assert.Equal(1, tts.CallCount);
        Assert.Equal(.5f, motion.Last!.Targets["headYaw"]);
    }

    [Fact]
    public async Task HeadShakeAsk_InfersMotionWhenModelOmitsAvatar()
    {
        var motion = new CaptureMotion();
        var llm = new PlannedLlm("好呀。", new AvatarReplyPlan("好呀。", null));
        var tts = new CountingTts();
        using var player = new AudioPlayer();
        using var orchestrator = new BotOrchestrator(
            new UnusedAsr(), llm, tts, player, new TtsConfig(), null, null,
            async (chunks, ct) => { await foreach (var _ in chunks.WithCancellation(ct)) { } },
            () => { }, triggerHotkeyAsync: null);
        orchestrator.ConfigureContinuousControl(motion, PlayAndStart);
        await orchestrator.ProcessTextAsync("大肥鱼，摇摇头呗", [], bypassWake: true, requireStructuredReply: true);
        Assert.Equal(1, tts.CallCount);
        Assert.Equal(.5f, motion.Last!.Targets["headYaw"]);
    }

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

    [Fact]
    public async Task MixedThoughtAndSpeak_CallsTtsWithSpokenOnly()
    {
        var llm = new FixedLlm("（又叫我）谁叫我？我在听。");
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
        var spoken = new List<string>();
        orchestrator.OnReplyCommitted += (_, r) => committed = r;
        orchestrator.OnSentenceReady += (_, s) => spoken.Add(s);

        await orchestrator.ProcessTextAsync("使用者（纳什）：喂喂喂，大肥鱼", [], bypassWake: true);

        Assert.Equal(ReplyKind.Speak, committed?.Kind);
        Assert.Equal("谁叫我？我在听。", committed?.Spoken);
        Assert.Equal(1, tts.CallCount);
        Assert.Contains("谁叫我？我在听。", spoken);
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

    private sealed class PlannedLlm(string text, AvatarReplyPlan plan) : ILlmClient, IAvatarReplySource
    {
        public event EventHandler<string>? OnSentenceReady;
        public event EventHandler<string>? OnEmotionDetected;
        public event EventHandler<string>? OnActionDetected;
        public event EventHandler<string>? OnPoseDetected;
        public event EventHandler<AvatarReplyPlan>? OnAvatarPlanReady;

        public async IAsyncEnumerable<string> StreamAsync(
            List<Message> history,
            string userInput,
            [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            OnAvatarPlanReady?.Invoke(this, plan);
            OnSentenceReady?.Invoke(this, text);
            yield return text;
            await Task.CompletedTask;
        }
    }

    private static async Task PlayAndStart(IAsyncEnumerable<byte[]> chunks, CancellationToken ct, Action start)
    {
        await foreach (var _ in chunks.WithCancellation(ct)) start();
    }

    private sealed class CaptureMotion : IAvatarMotionSink
    {
        public AvatarIntent? Last;
        public void Submit(long generation, AvatarIntent intent) => Last = intent;
        public void Cancel(long generation) { }
        public void OnRms(float rms) { }
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
