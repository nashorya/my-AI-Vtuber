using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests;

/// <summary>
/// Cortico as the only performance layer, through the real <see cref="BotOrchestrator"/>, for both
/// reply protocols. The legacy player throws if it is ever used.
/// </summary>
public sealed class CorticoOrchestratorTests
{
    private const string Speak = "{\"v\":2,\"type\":\"decision\",\"mode\":\"speak\"}\n";
    private const string End = "{\"v\":2,\"type\":\"end\"}\n";
    private static string Seg(int seq, string text) =>
        "{\"v\":2,\"type\":\"speech\",\"seq\":" + seq + ",\"text\":" + System.Text.Json.JsonSerializer.Serialize(text) + "}\n";

    private sealed class Run : IDisposable
    {
        public readonly AudioPlayer Player = new();
        public readonly FakeCortico Engine = new();
        public readonly BotOrchestrator Orchestrator;
        public readonly List<string> Captions = [];
        public readonly List<string> Committed = [];
        public readonly List<string> Errors = [];
        public int Starts, Stops;

        public Run(ChunkedLlm llm)
        {
            Orchestrator = new BotOrchestrator(new UnusedAsr(), llm, new UnusedTts(), Player, new TtsConfig(), null, null,
                (_, _) => throw new Exception("Legacy playback must not run"), () => { }, null)
            { Cortico = Engine };
            Orchestrator.OnSentenceReady += (_, t) => { lock (Captions) Captions.Add(t); };
            Orchestrator.OnReplyCommitted += (_, r) => { lock (Committed) Committed.Add($"{r.Kind}:{r.Spoken}{r.Thought}"); };
            Orchestrator.OnError += (_, e) => { lock (Errors) Errors.Add(e); };
            Orchestrator.OnAiStartSpeaking += (_, _) => Starts++;
            Orchestrator.OnAiStopSpeaking += (_, _) => Stops++;
        }

        public Task Say(Func<bool>? canCommit = null) =>
            Orchestrator.ProcessTextAsync("你好", [], bypassWake: true, canCommit: canCommit);

        public void Dispose() { Orchestrator.Dispose(); Player.Dispose(); }
    }

    // ── legacy protocol ────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("【PASS】")]
    [InlineData("（先听他们说）")]
    public async Task Legacy_SilentReply_NeverReachesThePerformanceLayer(string raw)
    {
        using var run = new Run(new ChunkedLlm("legacy", raw));
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
        Assert.Empty(run.Engine.Feeds);
        Assert.Empty(run.Captions);
        Assert.Equal(0, run.Starts);
    }

    [Fact]
    public async Task Legacy_ScriptIsPerformed_OnlyCleanSpeechIsPublished()
    {
        using var run = new Run(new ChunkedLlm("legacy", "<微笑>你好【点头】"));
        await run.Say();
        Assert.Equal(["<微笑>你好【点头】"], run.Engine.Feeds);
        Assert.Equal(["你好"], run.Captions);
        Assert.Equal(["Speak:你好"], run.Committed);
        Assert.Equal(1, run.Starts);
        Assert.Equal(1, run.Stops);
    }

    [Fact]
    public async Task Legacy_FirstSentenceIsPerformed_BeforeTheModelFinishes()
    {
        var release = new TaskCompletionSource();
        var llm = new ChunkedLlm("legacy", "<微笑>你好呀。", "【点头】今天也加油。") { HoldAfterFirst = release.Task };
        using var run = new Run(llm);
        var turn = run.Say();
        await run.Engine.FirstFeed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(llm.Finished);
        Assert.Equal(["你好呀。"], run.Engine.Heard);
        release.SetResult();
        await turn;
        Assert.Equal(["<微笑>你好呀。", "【点头】今天也加油。"], run.Engine.Feeds);
        Assert.Equal(1, run.Engine.Begins); // one performance, one player
    }

    [Theory]
    [InlineData("{\"respond\":true,\"speech\":\"你好\"}")]
    [InlineData("```json\n{\"respond\":true}\n```")]
    public async Task Legacy_ProtocolEnvelopeIsNeverSpoken(string raw)
    {
        using var run = new Run(new ChunkedLlm("legacy", raw));
        await run.Say();
        Assert.Empty(run.Engine.Feeds);
        Assert.Single(run.Errors);
    }

    [Fact]
    public async Task Legacy_TagsThoughtsAndVoiceTagsNeverReachTheScript()
    {
        using var run = new Run(new ChunkedLlm("legacy", "（其实有点困）[emotion:happy]好呀[laugh]，<微笑>走吧。"));
        await run.Say();
        var fed = Assert.Single(run.Engine.Feeds);
        Assert.DoesNotContain("困", fed);
        Assert.DoesNotContain("[", fed);
        Assert.DoesNotContain("emotion", fed);
        Assert.Contains("<微笑>", fed);
        Assert.Equal(["好呀，走吧。"], run.Captions);
    }

    [Fact]
    public async Task Legacy_PassAfterSpeech_StopsFurtherPerformance_AndIsReported()
    {
        using var run = new Run(new ChunkedLlm("legacy", "好的。", "【PASS】"));
        await run.Say();
        Assert.Equal(["好的。"], run.Engine.Feeds);
        Assert.Single(run.Errors);
    }

    // ── protocol v2 ────────────────────────────────────────────────────────────────

    [Theory]
    [InlineData("pass")]
    [InlineData("thought")]
    public async Task V2_PassAndThought_AreNeverPerformed(string mode)
    {
        var decision = "{\"v\":2,\"type\":\"decision\",\"mode\":\"" + mode + "\",\"text\":\"别说出来\"}\n";
        using var run = new Run(new ChunkedLlm("v2", decision, End));
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
        Assert.Empty(run.Captions);
        Assert.DoesNotContain(run.Committed, c => c.StartsWith("Speak", StringComparison.Ordinal));
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task V2_SegmentsStreamIntoCortico_BeforeTheModelFinishes_WithMarkupKept()
    {
        var release = new TaskCompletionSource();
        var llm = new ChunkedLlm("v2", Speak + Seg(0, "<微笑>你好呀。"),
            "{\"v\":2,\"type\":\"control\",\"kind\":\"emotion\",\"value\":\"happy\"}\n" + Seg(1, "【点头】我在。") + End)
        { HoldAfterFirst = release.Task };
        using var run = new Run(llm);
        var turn = run.Say();
        await run.Engine.FirstFeed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.False(llm.Finished);
        release.SetResult();
        await turn;
        Assert.Equal(["<微笑>你好呀。", "【点头】我在。"], run.Engine.Feeds);
        Assert.Equal(["你好呀。", "我在。"], run.Captions);
        Assert.Equal(1, run.Engine.Begins);
        Assert.Equal(1, run.Starts);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task V2_PassOrThoughtOnlySegment_IsSkipped_NotPerformed()
    {
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "好的。") + Seg(1, "【PASS】") + Seg(2, "（别说出来）") + Seg(3, "走吧。") + End));
        await run.Say();
        Assert.Equal(["好的。", "走吧。"], run.Engine.Feeds);
        Assert.Equal(["好的。", "走吧。"], run.Captions);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task V2_RawProtocolInsideSpeech_EndsTheTurn_AndIsNeverSpoken()
    {
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "好的。") + Seg(1, "{\"v\":2,\"type\":\"end\"}") + Seg(2, "不该出现。") + End));
        await run.Say();
        Assert.Equal(["好的。"], run.Engine.Feeds);
        Assert.Single(run.Errors);
    }

    [Fact]
    public async Task V2_ActionOnlySegment_WaitsForTheNextSpokenOne()
    {
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "【点头】") + Seg(1, "对。") + End));
        await run.Say();
        Assert.Equal(["【点头】对。"], run.Engine.Feeds);
    }

    [Fact]
    public async Task V2_ActionsWithoutAnySpeech_AreNotPerformed()
    {
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "【点头】") + End));
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
    }

    [Fact]
    public async Task HumanResumesBeforeAudio_NothingIsCommittedOrStarted()
    {
        var valid = true;
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "<微笑>你好") + End));
        run.Engine.BeforeAuthorize = () => valid = false;
        await run.Say(() => valid);
        Assert.Empty(run.Committed);
        Assert.Empty(run.Captions);
        Assert.Equal(0, run.Starts);
        Assert.Empty(run.Errors); // a superseded turn is not a fault
    }

    [Fact]
    public async Task Stop_ReachesThePerformanceLayer_AndReleasesTheStage()
    {
        using var run = new Run(new ChunkedLlm("v2", Speak + Seg(0, "讲个很长的故事。") + End));
        run.Engine.HoldPlayback = true;
        var turn = run.Say();
        await run.Engine.Playing.Task.WaitAsync(TimeSpan.FromSeconds(5));

        await run.Orchestrator.BeginInterrupt().WaitAsync(TimeSpan.FromSeconds(5));
        await turn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(run.Engine.Interrupts >= 1, "stop must reach Cortico, not only the app player");
        Assert.Equal(1, run.Engine.Disposed);
        Assert.Empty(run.Errors);
    }

    private sealed class UnusedTts : ITtsClient
    {
        public IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion, CancellationToken ct = default) =>
            throw new Exception("App TTS must not be called by the orchestrator in Cortico mode");
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
