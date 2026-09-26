using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;

namespace AIVTuber.Tests;

/// <summary>
/// RT-04 Turn Manager v2 scenario tests. All timing is driven by a fake clock and a
/// manual scheduler — no real timers, no thread races.
/// </summary>
public class TurnManagerV2Tests
{
    private static TurnManagerOptions Options(string selfName = "可缇") => new()
    {
        SelfNames = [selfName, "大肥鱼"],
        CrossSourceMergeWindowMs = 250,
        OpponentQuietHoldMs = 400,
        TwoWayTalkExpiryMs = 4000,
        NoiseGateMs = 300,
        ConversationWindowMs = 45_000,
    };

    private sealed class Harness : IDisposable
    {
        public FakeTurnClock Clock = new();
        public ManualTurnScheduler Scheduler = new();
        public TurnManagerV2 Manager;
        public List<(TurnContextV2 Ctx, IReadOnlyList<TalkLine> Lines)> Ready = [];
        public List<(TurnContextV2 Ctx, TurnCancelReason Reason)> Cancelled = [];
        public List<TurnDecision> Decisions = [];

        public Harness(TurnManagerOptions? options = null)
        {
            Manager = new TurnManagerV2(options ?? Options(), Clock, Scheduler);
            Manager.TurnReady += (ctx, lines) => Ready.Add((ctx, lines));
            Manager.TurnCancelled += (ctx, reason) => Cancelled.Add((ctx, reason));
            Manager.DecisionRecorded += d => Decisions.Add(d);
        }

        public void Elapse(long ms) => Scheduler.Elapse(Clock, ms);

        public static TalkLine Mic(string text) => new(TalkIdentity.Self, "使用者", text, null);
        public static TalkLine Loop(string text) => new(TalkIdentity.Opponent, "对方", text, null);

        public void Dispose() => Manager.Dispose();
    }

    // ------------------------------------------------- scenario: 点名 vs 第三人称

    [Fact]
    public void NameCallWithQuestion_RespondsImmediately_NoFixedMergeDelay()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);

        var turn = Assert.Single(h.Ready);
        Assert.Equal(InvitationLevel.NameCall, turn.Ctx.Evidence.Level);
        Assert.Equal("name_call_question", turn.Ctx.Evidence.ReasonCode);
        Assert.True(h.Manager.CanCommit(turn.Ctx.GenerationId));
        Assert.Equal(TurnState.Ready, h.Manager.State);
    }

    [Fact]
    public void ThirdPersonMention_DoesNotRespond()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("他说可缇挺好玩"), TurnSource.Microphone);

        Assert.Empty(h.Ready);
        var decision = Assert.Single(h.Decisions);
        Assert.False(decision.Respond);
        Assert.Equal(InvitationLevel.WeakMention, decision.Level);
        Assert.Equal("third_person_mention", decision.ReasonCode);
        Assert.Equal(TurnState.Observing, h.Manager.State);
    }

    // ------------------------------------------------- scenario: AI 发问后无名字回应

    [Fact]
    public void ResponseAfterAiQuestion_NoNameStillResponds()
    {
        using var h = new Harness();
        h.Manager.NoteAssistantMessage("要继续吗？");
        h.Clock.Advance(1000);
        h.Manager.AddFinal(Harness.Mic("继续"), TurnSource.Microphone);

        var turn = Assert.Single(h.Ready);
        Assert.Equal(InvitationLevel.ResponseToAiQuestion, turn.Ctx.Evidence.Level);
        Assert.Equal("response_after_ai_question", turn.Ctx.Evidence.ReasonCode);
    }

    [Fact]
    public void NoNameNoEvidence_StaysSilent()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("今天天气不错"), TurnSource.Microphone);
        Assert.Empty(h.Ready);
        Assert.Equal("no_invitation_evidence", Assert.Single(h.Decisions).ReasonCode);
    }

    // ------------------------------------------------- scenario: 提交前取消（改口）

    [Fact]
    public void RetractionInFinal_PreventsTurn()
    {
        using var h = new Harness();
        h.Manager.ObservePartial(TurnSource.Microphone, "你觉得可以");
        h.Manager.AddFinal(Harness.Mic("你觉得可以，不，我不是问你"), TurnSource.Microphone);

        Assert.Empty(h.Ready);
        Assert.Equal("invitation_retracted", Assert.Single(h.Decisions).ReasonCode);
    }

    [Fact]
    public void RetractionAfterDispatch_CancelsBeforeCommit()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你觉得可以吗？"), TurnSource.Microphone);
        var generation = Assert.Single(h.Ready).Ctx.GenerationId;
        Assert.True(h.Manager.CanCommit(generation));

        // The user immediately retracts: the already-started generation must not commit.
        h.Manager.AddFinal(Harness.Mic("不，我不是问你"), TurnSource.Microphone);

        Assert.False(h.Manager.CanCommit(generation));
        Assert.Equal(TurnCancelReason.Superseded, Assert.Single(h.Cancelled).Reason);
        Assert.Empty(h.Ready.Skip(1)); // no replacement turn — retraction is silent
    }

    [Fact]
    public void RetractionInPartial_DropsCandidate()
    {
        using var h = new Harness();
        h.Manager.ObservePartial(TurnSource.Microphone, "可缇你来说两句");
        Assert.Equal(TurnState.Candidate, h.Manager.State);
        h.Manager.ObservePartial(TurnSource.Microphone, "可缇你来说两句，算了不用你回");
        Assert.Equal(TurnState.Observing, h.Manager.State);
        Assert.Empty(h.Ready);
    }

    // ------------------------------------------------- scenario: 麦克风 final 先到、对面还在说

    [Fact]
    public void MicFinalWhileOpponentSpeaking_PrepareButDoNotPlay()
    {
        using var h = new Harness();
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);

        Assert.Empty(h.Ready);
        Assert.Equal(TurnState.Preparing, h.Manager.State);

        // Opponent goes quiet; after the quiet hold the turn may dispatch.
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, false);
        h.Elapse(400);

        var turn = Assert.Single(h.Ready);
        Assert.True(h.Manager.CanCommit(turn.Ctx.GenerationId));
    }

    [Fact]
    public void OpponentKeepsTalking_TurnExpires_NoLateReplyQueue()
    {
        using var h = new Harness();
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        Assert.Equal(TurnState.Preparing, h.Manager.State);

        // Two-way continuous talk: the prepared turn expires instead of queueing a late reply.
        h.Elapse(4000);
        Assert.Equal(TurnState.Observing, h.Manager.State);
        Assert.Empty(h.Ready);
        Assert.Equal(TurnCancelReason.TwoWayTalkExpired, Assert.Single(h.Cancelled).Reason);

        // New context, new decision — the old answer is not replayed.
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, false);
        h.Elapse(400);
        h.Manager.AddFinal(Harness.Mic("可缇，现在可以说了吗？"), TurnSource.Microphone);
        var turn = Assert.Single(h.Ready);
        Assert.True(h.Manager.CanCommit(turn.Ctx.GenerationId));
    }

    // ------------------------------------------------- scenario: 生成中人声恢复

    [Fact]
    public void SustainedHumanVoiceDuringGeneration_Cancels()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        var generation = Assert.Single(h.Ready).Ctx.GenerationId;

        h.Manager.NoteVoiceActivity(TurnSource.Microphone, true);
        // While the voice is live, new playback is suppressed even before the noise gate.
        Assert.False(h.Manager.CanCommit(generation));

        h.Elapse(300); // sustained past the noise gate → full cancel
        Assert.False(h.Manager.CanCommit(generation));
        Assert.Equal(TurnCancelReason.HumanVoiceResumed, Assert.Single(h.Cancelled).Reason);
    }

    [Fact]
    public void BriefNoiseDuringGeneration_DoesNotShredAiSpeech()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        var generation = Assert.Single(h.Ready).Ctx.GenerationId;

        h.Manager.NoteVoiceActivity(TurnSource.Microphone, true);
        h.Elapse(100); // shorter than the noise gate
        h.Manager.NoteVoiceActivity(TurnSource.Microphone, false);

        h.Elapse(1000); // noise-gate timer deadline passes with no active voice
        Assert.True(h.Manager.CanCommit(generation));
        Assert.Empty(h.Cancelled);
    }

    // ------------------------------------------------- scenario: PK 切场

    [Fact]
    public void MatchChange_InFlightGenerationCannotCommit_AndOldMatchNotAppliedToNewOpponent()
    {
        using var h = new Harness();
        h.Manager.SetMatchId("match-1");
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        var (ctx, _) = Assert.Single(h.Ready);
        Assert.Equal("match-1", ctx.MatchId);
        Assert.True(h.Manager.CanCommit(ctx.GenerationId));

        h.Manager.NoteMatchChanged("match-2");

        Assert.False(h.Manager.CanCommit(ctx.GenerationId));
        Assert.Equal(TurnCancelReason.MatchChanged, Assert.Single(h.Cancelled).Reason);
        Assert.Equal(TurnState.Observing, h.Manager.State);

        // Old-match transcript stays buffered out: a new turn carries the new MatchId.
        h.Manager.AddFinal(Harness.Mic("可缇，继续吗？"), TurnSource.Microphone);
        Assert.Equal(2, h.Ready.Count);
        Assert.Equal("match-2", h.Ready[1].Ctx.MatchId);
    }

    // ------------------------------------------------- rule 5: no fixed 350ms merge

    [Fact]
    public void SameSourceFinals_DispatchImmediately_NoMerging()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        h.Manager.CompleteTurn(h.Ready[0].Ctx.GenerationId);

        h.Clock.Advance(1000);
        h.Manager.AddFinal(Harness.Mic("可缇，那然后呢？"), TurnSource.Microphone);

        Assert.Equal(2, h.Ready.Count); // both dispatched synchronously, zero merge delay
    }

    [Fact]
    public void CrossSourceConflict_UsesSmallMergeWindow_AndRecordsReason()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        Assert.Single(h.Ready); // mic final dispatched immediately

        h.Clock.Advance(100);
        h.Manager.AddFinal(Harness.Loop("对，我也想听听你怎么想"), TurnSource.Loopback);
        Assert.Single(h.Ready); // held in the small cross-source window

        h.Elapse(250);
        Assert.Equal(2, h.Ready.Count);
        Assert.Contains("merge=cross_source", h.Ready[1].Ctx.Evidence.MatchedSignal);
    }

    // ------------------------------------------------- rule 1: partials never generate

    [Fact]
    public void Partials_OnlyBuildCandidates_NeverDispatch()
    {
        using var h = new Harness();
        for (var i = 1; i <= 5; i++)
            h.Manager.ObservePartial(TurnSource.Microphone, $"可缇，你怎么看".PadRight(6 + i, '了'));
        h.Elapse(10_000);

        Assert.Empty(h.Ready);
        Assert.Equal(TurnState.Candidate, h.Manager.State);
        Assert.Empty(h.Decisions);
    }

    // ------------------------------------------------- stop command

    [Fact]
    public void StopCommand_CancelsImmediately()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        var generation = h.Ready[0].Ctx.GenerationId;

        h.Manager.NoteStopCommand();

        Assert.False(h.Manager.CanCommit(generation));
        Assert.Equal(TurnCancelReason.StopCommand, Assert.Single(h.Cancelled).Reason);
    }

    // ------------------------------------------------- TurnContext contract

    [Fact]
    public void TurnContext_CarriesProgramGeneratedIdsAndEvidence()
    {
        using var h = new Harness();
        h.Manager.SetMatchId("m9");
        h.Manager.AddFinal(Harness.Mic("大肥鱼你觉得呢？"), TurnSource.Microphone);

        var ctx = Assert.Single(h.Ready).Ctx;
        Assert.NotEqual(0, ctx.TurnId);
        Assert.NotEqual(0, ctx.GenerationId);
        Assert.NotEqual(ctx.TurnId, ctx.GenerationId);
        Assert.Equal("m9", ctx.MatchId);
        Assert.True(ctx.InputSnapshotRevision > 0);
        Assert.Single(ctx.InputSegments);
        Assert.Empty(ctx.VisionSnapshotIds);
        Assert.Equal(TurnCancelReason.None, ctx.CancelReason);
    }

    // ------------------------------------------------- config boundary

    [Fact]
    public void RealtimeConfig_DefaultsToLegacyBehaviour()
    {
        var config = new AppConfig();
        Assert.False(config.Realtime.TurnManagerV2Enabled);
        Assert.False(config.Realtime.SpeculativeGenerationEnabled);
    }

    [Fact]
    public void SpeculativeGeneration_DefaultOff_InOptions()
    {
        Assert.False(new TurnManagerOptions().SpeculativeGenerationEnabled);
    }
}
