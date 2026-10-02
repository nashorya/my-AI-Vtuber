using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Tests.Cortico;

namespace AIVTuber.Tests;

/// <summary>
/// The one v2 reply pipeline with Cortico pacing, through the real <see cref="BotOrchestrator"/>:
/// approved script segments reach Cortico, the app synthesizes and plays each piece when Cortico
/// asks, and only what reaches the player becomes captions and history.
/// </summary>
public sealed class CorticoOrchestratorTests
{
    private sealed class Run : IDisposable
    {
        public readonly AudioPlayer Player = new();
        public readonly FakeCortico Engine = new();
        public readonly TextTts Tts = new();
        public readonly List<byte> Audio = [];
        public readonly BotOrchestrator Orchestrator;
        public readonly List<string> Captions = [];
        public readonly List<string> Committed = [];
        public readonly List<string> Errors = [];
        public TaskCompletionSource? Hold;
        public int Starts, Stops, PlayerStops;

        public Run(ChunkedLlm llm)
        {
            Orchestrator = new BotOrchestrator(new UnusedAsr(), llm, Tts, Player, new TtsConfig(), null, null,
                TestPlay.Into(Audio, hold: () => Hold?.Task ?? Task.CompletedTask), () => Interlocked.Increment(ref PlayerStops), null)
            { Cortico = Engine };
            Orchestrator.OnSentenceReady += (_, t) => { lock (Captions) Captions.Add(t); };
            Orchestrator.OnReplyCommitted += (_, r) => { lock (Committed) Committed.Add($"{r.Kind}:{r.Spoken}{r.Thought}"); };
            Orchestrator.OnError += (_, e) => { lock (Errors) Errors.Add(e); };
            Orchestrator.OnAiStartSpeaking += (_, _) => Starts++;
            Orchestrator.OnAiStopSpeaking += (_, _) => Stops++;
        }

        public string Heard { get { lock (Audio) return System.Text.Encoding.UTF8.GetString(Audio.ToArray()); } }

        public Task Say(Func<bool>? canCommit = null) =>
            Orchestrator.ProcessTextAsync("你好", [], bypassWake: true, canCommit: canCommit);

        public void Dispose() { Orchestrator.Dispose(); Player.Dispose(); }
    }

    [Theory]
    [InlineData("pass")]
    [InlineData("thought")]
    public async Task PassAndThought_AreNeverPerformed(string mode)
    {
        using var run = new Run(new ChunkedLlm("v2", mode == "pass" ? V2.Pass : V2.Thought("别说出来"), V2.End));
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
        Assert.Empty(run.Captions);
        Assert.Empty(run.Tts.Texts);
        Assert.DoesNotContain(run.Committed, c => c.StartsWith("Speak", StringComparison.Ordinal));
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task Segments_ArePacedByCortico_PlayedByTheApp_AndCommittedWithCleanText()
    {
        using var run = new Run(new ChunkedLlm("v2", V2.Speak + V2.Seg(0, "<微笑>你好呀。"), V2.Seg(1, "【点头】我在。") + V2.End));
        await run.Say();
        Assert.Equal(["<微笑>你好呀。", "【点头】我在。"], run.Engine.Feeds);
        Assert.Equal(["你好呀。", "我在。"], run.Captions);
        Assert.Equal("你好呀。我在。", run.Heard);
        Assert.Equal(2, run.Tts.Texts.Count);
        Assert.Equal(1, run.Engine.Begins);
        Assert.Equal(1, run.Starts);
        Assert.Equal(1, run.Stops);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task FirstSegment_ReachesCortico_BeforeTheModelFinishes()
    {
        var release = new TaskCompletionSource();
        var llm = new ChunkedLlm("v2", V2.Speak + V2.Seg(0, "你好呀。"), V2.Seg(1, "我在。") + V2.End) { HoldAfterFirst = release.Task };
        using var run = new Run(llm);
        var turn = run.Say();
        await TestWait.Until(() => { lock (run.Engine.Feeds) return run.Engine.Feeds.Count == 1; });
        Assert.False(llm.Finished);
        release.SetResult();
        await turn;
        Assert.Equal("你好呀。我在。", run.Heard);
    }

    [Fact]
    public async Task PassOrThoughtOnlySegment_IsSkipped_NotPerformed()
    {
        using var run = new Run(V2.Say("好的。", "【PASS】", "（别说出来）", "走吧。"));
        await run.Say();
        Assert.Equal(["好的。", "走吧。"], run.Engine.Feeds);
        Assert.Equal(["好的。", "走吧。"], run.Captions);
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task RawProtocolInsideSpeech_EndsTheTurn_AndIsNeverSpoken()
    {
        using var run = new Run(V2.Say("好的。", "{\"v\":2,\"type\":\"end\"}", "不该出现。"));
        await run.Say();
        Assert.Equal(["好的。"], run.Engine.Feeds);
        Assert.Equal("好的。", run.Heard);
        Assert.Single(run.Errors);
    }

    [Fact]
    public async Task ActionOnlySegment_WaitsForTheNextSpokenOne()
    {
        using var run = new Run(V2.Say("【点头】", "对。"));
        await run.Say();
        Assert.Equal(["【点头】对。"], run.Engine.Feeds);
    }

    [Fact]
    public async Task ActionsWithoutAnySpeech_AreNotPerformed()
    {
        using var run = new Run(V2.Say("【点头】"));
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
    }

    [Fact]
    public async Task HumanResumesBeforeAudio_NothingIsCommittedOrPlayed()
    {
        var valid = true;
        using var run = new Run(V2.Say("<微笑>你好"));
        run.Engine.HoldPlay = true;
        var turn = run.Say(() => valid);
        await TestWait.Until(() => { lock (run.Engine.Log) return run.Engine.Log.Contains("synthEnd:1"); });
        valid = false;
        run.Engine.Current!.Handler.Play(1);
        await run.Orchestrator.BeginInterrupt().WaitAsync(TimeSpan.FromSeconds(5));
        await turn.WaitAsync(TimeSpan.FromSeconds(5));
        Assert.Empty(run.Committed);
        Assert.Empty(run.Captions);
        Assert.Equal("", run.Heard);
        Assert.Equal(0, run.Starts);
        Assert.Empty(run.Errors); // a superseded turn is not a fault
    }

    [Fact]
    public async Task Stop_SilencesThePlayerFirst_AndReachesCortico()
    {
        using var run = new Run(V2.Say("讲个很长的故事。"));
        run.Hold = new TaskCompletionSource();
        var turn = run.Say();
        await TestWait.Until(() => { lock (run.Engine.Log) return run.Engine.Log.Contains("started:1"); });

        await run.Orchestrator.BeginInterrupt().WaitAsync(TimeSpan.FromSeconds(5));
        await turn.WaitAsync(TimeSpan.FromSeconds(5));

        Assert.True(run.PlayerStops >= 1, "the app player is stopped");
        Assert.True(run.Engine.Interrupts >= 1, "stop must reach Cortico, not only the app player");
        Assert.Empty(run.Errors);
    }

    [Fact]
    public async Task DeadSidecar_TheReplyIsSpokenVoiceOnly_WithAWarning()
    {
        using var run = new Run(V2.Say("<微笑>你好。", "再见。"));
        run.Engine.IsAlive = false;
        await run.Say();
        Assert.Equal(0, run.Engine.Begins);
        Assert.Equal("你好。再见。", run.Heard);
        Assert.Equal(["你好。", "再见。"], run.Captions);
        Assert.Contains(run.Errors, e => e.Contains("皮套异常"));
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
