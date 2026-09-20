using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Cortico;

namespace AIVTuber.Tests;

public sealed class CorticoOrchestratorTests
{
    [Theory]
    [InlineData("【PASS】")]
    [InlineData("（先听他们说）")]
    public async Task SilentReply_NeverReachesPerformanceEngine(string raw)
    {
        using var player = new AudioPlayer();
        var engine = new FakePerformance();
        using var orchestrator = Create(player, raw, engine);
        await orchestrator.ProcessTextAsync("你好", [], bypassWake: true);
        Assert.Equal(0, engine.Calls);
    }

    [Fact]
    public async Task ScriptGoesToEngine_OnlyCleanSpeechIsPublished_AndDefaultPlayerIsUnused()
    {
        using var player = new AudioPlayer();
        var engine = new FakePerformance();
        using var orchestrator = Create(player, "<微笑>你好【点头】", engine);
        var captions = new List<string>();
        var starts = 0;
        orchestrator.OnSentenceReady += (_, text) => captions.Add(text);
        orchestrator.OnAiStartSpeaking += (_, _) => starts++;
        await orchestrator.ProcessTextAsync("你好", [], bypassWake: true);
        Assert.Equal("<微笑>你好【点头】", engine.Script);
        Assert.Equal(new[] { "你好" }, captions);
        Assert.Equal(1, starts);
    }

    [Fact]
    public async Task HumanResumesBeforeAudio_NoHistoryCaptionOrSpeakingEvent()
    {
        using var player = new AudioPlayer();
        var valid = true;
        var engine = new FakePerformance { BeforeAuthorize = () => valid = false };
        using var orchestrator = Create(player, "<微笑>你好", engine);
        var commits = 0;
        orchestrator.OnReplyCommitted += (_, _) => commits++;
        await orchestrator.ProcessTextAsync("你好", [], bypassWake: true, canCommit: () => valid);
        Assert.False(engine.Allowed);
        Assert.Equal(0, commits);
    }

    private static BotOrchestrator Create(AudioPlayer player, string raw, ICorticoPerformance engine)
    {
        var result = new BotOrchestrator(new UnusedAsr(), new FixedLlm(raw), new CountingTts(),
            player, new TtsConfig(), null, null,
            (_, _) => throw new Exception("Legacy playback must not run"), () => {}, null);
        result.Cortico = engine;
        return result;
    }

    private sealed class FakePerformance : ICorticoPerformance
    {
        public string Prompt => "";
        public int Calls;
        public string? Script;
        public bool Allowed;
        public Action? BeforeAuthorize;
        public Task<string> PrepareAsync(string script, CancellationToken ct) => Task.FromResult("你好");
        public Task PerformAsync(string script, Func<bool> authorize, Action started, CancellationToken ct)
        {
            Calls++; Script = script; BeforeAuthorize?.Invoke(); Allowed = authorize();
            if (Allowed) started();
            return Task.CompletedTask;
        }
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
