using System.Reflection;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.RealtimeAsr;
using AIVTuber.Core.Runtime;
using AIVTuber.Tests.Auth;

namespace AIVTuber.Tests.Turns;

/// <summary>
/// A3-1 through the production entry points of a real <see cref="BotRuntime"/> with the v2 turn
/// manager enabled: <see cref="BotRuntime.WireRealtimePump"/> (partials/finals),
/// <see cref="BotRuntime.AcceptTalkLine"/>, <see cref="BotRuntime.WireOrchestrator"/> (speaking
/// lifecycle, commits) and the real <see cref="BotOrchestrator"/>. Only the vendor clients,
/// the sound card and the account are fakes.
/// </summary>
public sealed class RuntimeTurnV2Tests
{
    private sealed class ControlledTts : ITtsClient
    {
        public readonly TaskCompletionSource Release = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public bool StallAfterFirstChunk;

        public async IAsyncEnumerable<byte[]> StreamAsync(string text, string voiceId, string? emotion,
            [EnumeratorCancellation] CancellationToken ct = default)
        {
            yield return new byte[320];
            if (StallAfterFirstChunk) await Release.Task; // ignores cancellation on purpose
            yield return new byte[320];
        }
    }

    private sealed class Harness : IAsyncDisposable
    {
        public readonly RuntimeCloudGateTests.FakeCloudAccess Cloud = new();
        public readonly TaskCompletionSource LlmRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public readonly ControlledTts Tts = new();
        public RuntimeCloudGateTests.ScriptedLlm Llm = null!;
        public BotRuntime Runtime = null!;
        public BotOrchestrator Orchestrator = null!;
        public int Played;
        public int Stops;
        public readonly TaskCompletionSource FirstPlayed = new(TaskCreationOptions.RunContinuationsAsynchronously);
        private AudioPlayer _player = null!;

        public static Harness Create(bool stallLlm = false, bool stallTts = false)
        {
            var h = new Harness();
            h.Cloud.Grant();
            var config = new AppConfig();
            config.Asr.Streaming = false;
            config.Realtime.TurnManagerV2Enabled = true;
            config.Identity.SelfName = "小明";            // what the AI calls the streamer
            config.Interaction.WakeKeywords = ["可缇"];    // the AI's own name
            h.Runtime = new BotRuntime(config, Path.GetTempPath());
            h.Runtime.UseCloudAccess(h.Cloud);
            h._player = new AudioPlayer();
            h.Llm = new RuntimeCloudGateTests.ScriptedLlm("好的。", stallLlm ? h.LlmRelease.Task : null);
            h.Tts.StallAfterFirstChunk = stallTts;
            h.Orchestrator = new BotOrchestrator(
                new RuntimeCloudGateTests.CountingAsr(), h.Llm, h.Tts, h._player, new TtsConfig(), null, null,
                async (chunks, ct) =>
                {
                    await foreach (var _ in chunks.WithCancellation(ct))
                    {
                        Interlocked.Increment(ref h.Played);
                        h.FirstPlayed.TrySetResult();
                    }
                },
                () => Interlocked.Increment(ref h.Stops),
                triggerHotkeyAsync: null);
            Set(h.Runtime, "_orchestrator", h.Orchestrator);
            Set(h.Runtime, "_conversation", new ConversationManager(config.Llm));
            typeof(BotRuntime).GetMethod("EnsureTurnGate", BindingFlags.Instance | BindingFlags.NonPublic)!
                .Invoke(h.Runtime, null);
            h.Runtime.WireOrchestrator(h.Orchestrator); // the production subscriptions
            return h;
        }

        public TurnManagerV2 Manager =>
            (TurnManagerV2)typeof(BotRuntime).GetField("_turnManagerV2", BindingFlags.Instance | BindingFlags.NonPublic)!
                .GetValue(Runtime)!;

        public void Say(string text) =>
            Runtime.AcceptTalkLine(new TalkLine(TalkIdentity.Self, "小明", text, null));

        public async Task DrainAsync()
        {
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (Runtime.BackgroundTaskCount > 0 && DateTime.UtcNow < deadline) await Task.Delay(20);
        }

        public async ValueTask DisposeAsync()
        {
            LlmRelease.TrySetResult();
            Tts.Release.TrySetResult();
            await Runtime.DisposeAsync();
            _player.Dispose();
        }
    }

    private static void Set(BotRuntime runtime, string name, object value) =>
        typeof(BotRuntime).GetField(name, BindingFlags.Instance | BindingFlags.NonPublic)!.SetValue(runtime, value);

    private static async Task Until(Func<bool> condition, int timeoutMs = 5000)
    {
        var deadline = Environment.TickCount64 + timeoutMs;
        while (!condition())
        {
            if (Environment.TickCount64 > deadline) Assert.Fail("condition not met before timeout");
            await Task.Delay(10);
        }
    }

    [Fact]
    public async Task RuntimePartials_ReachTheTurnManager_AsOneCandidate_AndOnlyTheFinalGenerates()
    {
        await using var h = Harness.Create();
        var factory = new FakeRealtimeAsrSessionFactory();
        await using var pump = new RealtimeAsrPump(AudioSource.Microphone, factory,
            options: new RealtimeAsrOptions { IdleDisconnectMs = 0, PrerollMs = 300 });
        h.Runtime.WireRealtimePump(pump); // the same subscription StartAudio makes
        pump.OnCapturedFrame(RealtimeAsrTestHelpers.Frame(30), voicedHint: true);
        await Until(() => factory.Sessions.Count == 1 && factory.Last.Started);
        var session = factory.Last;

        TranscriptUpdate Update(int revision, string text, bool final) => new(
            AudioSource.Microphone, session.CaptureEpoch, session.ProviderSessionId, "seg-1", revision, text,
            final, 0, 900, Environment.TickCount64);
        session.Emit(Update(0, "可缇", false));
        session.Emit(Update(1, "可缇你", false));
        session.Emit(Update(2, "可缇你觉得呢", false));

        await Until(() => h.Manager.State == TurnState.Candidate);
        Assert.Equal(1, h.Manager.CandidateCount);
        await Task.Delay(200);
        Assert.Equal(0, h.Llm.Calls); // partials never start generation

        session.Emit(Update(3, "可缇你觉得呢", true));
        await Until(() => h.Llm.Calls == 1);
        await h.DrainAsync();
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(0, h.Manager.CandidateCount);
    }

    [Fact]
    public async Task StreamerName_DoesNotCallTheAi_TheAiNameDoes()
    {
        await using var h = Harness.Create();

        h.Say("小明");
        await Task.Delay(300);
        Assert.Equal(0, h.Llm.Calls);

        h.Say("可缇");
        await Until(() => h.Llm.Calls == 1);
    }

    [Fact]
    public async Task Relogin_DoesNotReviveAnOldV2Reply()
    {
        await using var h = Harness.Create(stallLlm: true);

        h.Say("可缇，你好吗？");
        await h.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Cloud.Revoke("已退出登录");
        h.Cloud.Grant();
        h.LlmRelease.SetResult();
        await h.DrainAsync();

        Assert.Equal(0, h.Played);
    }

    [Fact]
    public async Task InputHeldWhileTheOpponentTalks_IsNotDispatchedAfterSignOutAndRelogin()
    {
        await using var h = Harness.Create();
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);   // the opponent is still talking
        h.Say("可缇，你怎么看？");
        await Until(() => h.Manager.State == TurnState.Preparing);

        h.Cloud.Revoke("已退出登录");
        h.Cloud.Grant();                                           // new login before the opponent stops
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, false);
        await Task.Delay(1000);                                    // past the quiet hold

        Assert.Equal(0, h.Llm.Calls); // input from the old grant is not answered under the new one
    }

    [Fact]
    public async Task InputHeldWhileTheOpponentTalks_IsNotDispatchedAfterPauseAndResume()
    {
        await using var h = Harness.Create();
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);
        h.Say("可缇，你怎么看？");
        await Until(() => h.Manager.State == TurnState.Preparing);

        h.Runtime.SetCompanionPaused(true);
        h.Runtime.SetCompanionPaused(false);
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, false);
        await Task.Delay(1000);

        Assert.Equal(0, h.Llm.Calls);
    }

    [Fact]
    public async Task Pause_DropsTheInFlightV2Reply_AndBlocksNewTurns()
    {
        await using var h = Harness.Create(stallLlm: true);

        h.Say("可缇，你好吗？");
        await h.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        h.Runtime.SetCompanionPaused(true);
        h.LlmRelease.SetResult();
        await h.DrainAsync();
        h.Say("可缇，还在吗？");
        await Task.Delay(300);

        Assert.Equal(0, h.Played);
        Assert.Equal(1, h.Llm.Calls);
        Assert.Equal(TurnState.Observing, h.Manager.State);
    }

    [Fact]
    public async Task ExplicitStopAfterFirstAudio_StopsNow_AndTheNextTurnStillWorks()
    {
        await using var h = Harness.Create(stallTts: true);

        h.Say("可缇，讲个故事");
        await h.FirstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopsBefore = h.Stops;

        h.Runtime.StopSpeaking();

        Assert.True(h.Stops > stopsBefore, "local playback was not stopped");
        h.Tts.Release.SetResult();
        await h.DrainAsync();
        var playedAfterStop = h.Played;
        Assert.Equal(1, playedAfterStop); // the second chunk of the stopped reply never plays

        h.Say("可缇，换个话题");
        await Until(() => h.Llm.Calls == 2 && h.Played > playedAfterStop);
    }

    [Fact]
    public async Task SupersedingInputBeforeAudio_CancelsTheRealGeneration()
    {
        await using var h = Harness.Create(stallLlm: true);

        h.Say("可缇，你觉得呢？");
        await h.Llm.Entered.Task.WaitAsync(TimeSpan.FromSeconds(5));
        var stopsBefore = h.Stops;

        h.Say("可缇，算了，换个问题"); // arrives while the first answer is still uncommitted

        await Until(() => h.Stops > stopsBefore); // the cancel reached the pipeline, not just a log line
        h.LlmRelease.SetResult();
        await h.DrainAsync();
        Assert.Equal(2, h.Llm.Calls);
    }

    [Fact]
    public async Task NewInputWhileSpeaking_DoesNotCutTheCurrentAudio_AndIsAnsweredAfterwards()
    {
        await using var h = Harness.Create(stallTts: true);

        h.Say("可缇，讲个笑话");
        await h.FirstPlayed.Task.WaitAsync(TimeSpan.FromSeconds(5));
        await Until(() => h.Manager.State == TurnState.Speaking);
        var stopsBefore = h.Stops;

        h.Say("今天真热");
        h.Say("可缇，还有吗？");
        await Task.Delay(300);

        Assert.Equal(stopsBefore, h.Stops);
        Assert.Equal(1, h.Llm.Calls);

        h.Tts.Release.SetResult();
        await Until(() => h.Llm.Calls == 2); // the buffered call is judged once the reply ends
    }
}
