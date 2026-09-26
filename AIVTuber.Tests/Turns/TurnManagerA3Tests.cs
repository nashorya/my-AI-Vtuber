using AIVTuber.Core.Bot;
using AIVTuber.Core.Bot.Turns;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Tests.Turns;

/// <summary>
/// A3-1 (TURN-02/03/04/06) on the v2 turn manager with a fake clock and manual scheduler.
/// Runtime wiring of the same behaviour is covered by <see cref="RuntimeTurnV2Tests"/>.
/// </summary>
public sealed class TurnManagerA3Tests
{
    private sealed class Harness : IDisposable
    {
        public readonly FakeTurnClock Clock = new();
        public readonly ManualTurnScheduler Scheduler = new();
        public readonly TurnManagerV2 Manager;
        public readonly List<(TurnContextV2 Ctx, IReadOnlyList<TalkLine> Lines)> Ready = [];
        public readonly List<(TurnContextV2 Ctx, TurnCancelReason Reason)> Cancelled = [];
        public readonly List<TurnDecision> Decisions = [];
        public readonly List<TalkLine> Released = [];

        public Harness(params string[] aiNames)
        {
            Clock.Advance(100); // a real monotonic clock never reads 0
            Manager = new TurnManagerV2(new TurnManagerOptions
            {
                SelfNames = aiNames.Length > 0 ? aiNames : ["可缇"],
                CrossSourceMergeWindowMs = 250,
                OpponentQuietHoldMs = 400,
                TwoWayTalkExpiryMs = 4000,
                NoiseGateMs = 300,
                ConversationWindowMs = 45_000,
            }, Clock, Scheduler);
            Manager.TurnReady += (ctx, lines) => Ready.Add((ctx, lines));
            Manager.TurnCancelled += (ctx, reason) => Cancelled.Add((ctx, reason));
            Manager.DecisionRecorded += d => Decisions.Add(d);
            Manager.LinesReleased += lines => Released.AddRange(lines);
        }

        public void Elapse(long ms) => Scheduler.Elapse(Clock, ms);

        /// <summary>Advances in small steps so every due timer gets its turn.</summary>
        public void Run(long ms, long step = 50)
        {
            for (long t = 0; t < ms; t += step) Elapse(step);
        }

        public static TalkLine Mic(string text) => new(TalkIdentity.Self, "小明", text, null);
        public static TalkLine Loop(string text) => new(TalkIdentity.Opponent, "对方", text, null);

        public void Dispose() => Manager.Dispose();
    }

    // ── TURN-02: partial revisions are one candidate ─────────────────────────

    [Fact]
    public void SameSegmentRevisions_AreOneCandidate_AndTheFinalDispatchesOnce()
    {
        using var h = new Harness();

        h.Manager.ObservePartial(TurnSource.Microphone, 7, "seg-1", 0, "可缇");
        h.Manager.ObservePartial(TurnSource.Microphone, 7, "seg-1", 1, "可缇你");
        h.Manager.ObservePartial(TurnSource.Microphone, 7, "seg-1", 2, "可缇你觉得呢");
        h.Manager.ObservePartial(TurnSource.Microphone, 7, "seg-1", 1, "可缇你"); // late, older revision

        Assert.Equal(1, h.Manager.CandidateCount);
        Assert.Equal(TurnState.Candidate, h.Manager.State);
        Assert.Empty(h.Decisions); // partials are not graded as invitations
        Assert.Empty(h.Ready);

        h.Manager.AddFinal(Harness.Mic("可缇你觉得呢"), TurnSource.Microphone);

        Assert.Single(h.Ready);
        Assert.Equal(0, h.Manager.CandidateCount);
    }

    [Fact]
    public void RetractionPartial_DropsOnlyThatCandidate_AndCancelsTheUncommittedTurn()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你觉得可以吗？"), TurnSource.Microphone);
        var generation = h.Ready[0].Ctx.GenerationId;
        h.Manager.ObservePartial(TurnSource.Loopback, 1, "op-1", 0, "我这把");

        h.Manager.ObservePartial(TurnSource.Microphone, 2, "seg-2", 0, "不是问你");

        Assert.False(h.Manager.CanCommit(generation));
        Assert.Equal(TurnCancelReason.RetractedInvitation, Assert.Single(h.Cancelled).Reason);
        Assert.Equal(1, h.Manager.CandidateCount); // the opponent's own candidate is untouched
    }

    // ── TURN-03 / TURN-04: who is being called ───────────────────────────────

    [Theory]
    [InlineData("可缇")]
    [InlineData("可缇。")]
    [InlineData("可缇你觉得呢")]
    [InlineData("你觉得呢可缇")]
    public void AiName_IsACall_WithOrWithoutPunctuation(string text)
    {
        using var h = new Harness();

        h.Manager.AddFinal(Harness.Mic(text), TurnSource.Microphone);

        Assert.Equal(InvitationLevel.NameCall, Assert.Single(h.Ready).Ctx.Evidence.Level);
    }

    [Theory]
    [InlineData("他说可缇挺好玩")]
    [InlineData("我刚跟可缇")]
    public void ThirdPersonMention_IsNotACall(string text)
    {
        using var h = new Harness();

        h.Manager.AddFinal(Harness.Mic(text), TurnSource.Microphone);

        Assert.Empty(h.Ready);
        Assert.Equal(InvitationLevel.WeakMention, Assert.Single(h.Decisions).Level);
    }

    [Fact]
    public void StreamerName_IsNotAnAiName()
    {
        var config = new AppConfig();
        config.Identity.SelfName = "小明";
        config.Interaction.WakeKeywords = ["可缇", " ", "Kt"];

        var names = BotRuntime.AiCallNames(config);
        Assert.Equal(["可缇", "Kt"], names);

        using var h = new Harness([.. names]);
        h.Manager.AddFinal(Harness.Mic("小明"), TurnSource.Microphone);
        Assert.Empty(h.Ready);
        h.Manager.AddFinal(Harness.Mic("可缇"), TurnSource.Microphone);
        Assert.Single(h.Ready);
    }

    [Fact]
    public void AnswerToTheAisQuestion_CountsOnlyFromThePartyItAsked()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，讲个故事"), TurnSource.Microphone);
        var first = h.Ready[0].Ctx.GenerationId;
        h.Manager.NoteAssistantMessage("要继续吗？");
        h.Manager.CompleteTurn(first);
        h.Clock.Advance(1000);

        h.Manager.AddFinal(Harness.Loop("继续"), TurnSource.Loopback); // the opponent did not get the question
        Assert.Single(h.Ready);
        Assert.NotEqual(InvitationLevel.ResponseToAiQuestion, h.Decisions[^1].Level);

        h.Clock.Advance(1000);
        h.Manager.AddFinal(Harness.Mic("继续"), TurnSource.Microphone); // no name needed
        Assert.Equal(2, h.Ready.Count);
        Assert.Equal(InvitationLevel.ResponseToAiQuestion, h.Ready[1].Ctx.Evidence.Level);
    }

    [Fact]
    public void YouQuestion_RightAfterTheOpponentSpoke_IsLeftToTheModel_NotForcedAsQuestionToAi()
    {
        using var h = new Harness();
        h.Manager.NoteAssistantMessage("好的。");
        h.Clock.Advance(2000);
        h.Manager.AddFinal(Harness.Loop("我这把打得怎么样"), TurnSource.Loopback);
        h.Clock.Advance(500);

        h.Manager.AddFinal(Harness.Mic("你觉得呢？"), TurnSource.Microphone);

        var turn = h.Ready.Single();
        Assert.Equal(InvitationLevel.SemanticDecision, turn.Ctx.Evidence.Level);
        Assert.Equal("ambiguous_addressee", turn.Ctx.Evidence.ReasonCode);
    }

    [Fact]
    public void UnmarkedContinuation_InsideTheConversation_GoesToTheModel_OutsideItStaysSilent()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，晚饭吃什么"), TurnSource.Microphone);
        h.Manager.NoteAssistantMessage("吃火锅吧。");
        h.Manager.CompleteTurn(h.Ready[0].Ctx.GenerationId);
        h.Clock.Advance(3000);

        h.Manager.AddFinal(Harness.Mic("我昨天刚吃过"), TurnSource.Microphone);
        Assert.Equal(InvitationLevel.SemanticDecision, h.Ready[^1].Ctx.Evidence.Level);
        h.Manager.CompleteTurn(h.Ready[^1].Ctx.GenerationId);

        h.Clock.Advance(120_000);
        h.Manager.AddFinal(Harness.Mic("今天天气不错"), TurnSource.Microphone);
        Assert.Equal(2, h.Ready.Count);
        Assert.Equal("no_invitation_evidence", h.Decisions[^1].ReasonCode);
    }

    // ── TURN-06: timers bound to their turn / episode ────────────────────────

    [Fact]
    public void OldExpiryTimer_DoesNotCancelALaterTurn()
    {
        using var h = new Harness();
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone); // turn 1 Preparing, expiry at 4000
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, false);
        h.Run(400);
        var turn1 = Assert.Single(h.Ready).Ctx;
        h.Manager.CompleteTurn(turn1.GenerationId);

        h.Run(600);                                            // t = 1000
        h.Manager.NoteVoiceActivity(TurnSource.Loopback, true);
        h.Manager.AddFinal(Harness.Mic("可缇，那你觉得呢？"), TurnSource.Microphone); // turn 2 Preparing, expiry at 5000
        h.Run(3400);                                           // t = 4400: turn 1's old deadline has passed

        Assert.Empty(h.Cancelled);
        Assert.Equal(TurnState.Preparing, h.Manager.State);

        h.Run(800);                                            // t = 5200: turn 2's own deadline
        var cancelled = Assert.Single(h.Cancelled);
        Assert.Equal(TurnCancelReason.TwoWayTalkExpired, cancelled.Reason);
        Assert.NotEqual(turn1.TurnId, cancelled.Ctx.TurnId);
    }

    [Fact]
    public void OldNoiseGateTimer_DoesNotTreatANewShortBlipAsSustainedVoice()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你怎么看？"), TurnSource.Microphone);
        var generation = h.Ready[0].Ctx.GenerationId;

        h.Manager.NoteVoiceActivity(TurnSource.Microphone, true);  // t = 0, gate at 300
        h.Elapse(100);
        h.Manager.NoteVoiceActivity(TurnSource.Microphone, false); // 100 ms blip
        h.Elapse(150);
        h.Manager.NoteVoiceActivity(TurnSource.Microphone, true);  // t = 250: new blip, gate at 550
        h.Elapse(60);                                              // t = 310: first episode's gate passes
        h.Manager.NoteVoiceActivity(TurnSource.Microphone, false); // second blip lasted 60 ms
        h.Run(1000);

        Assert.Empty(h.Cancelled);
        Assert.True(h.Manager.CanCommit(generation));
    }

    // ── context bookkeeping and lock discipline ──────────────────────────────

    [Fact]
    public void LinesNotAnswered_AreReleased_SoTheyStayInHistory()
    {
        using var h = new Harness();
        var line = Harness.Mic("今天天气不错");

        h.Manager.AddFinal(line, TurnSource.Microphone);

        Assert.Empty(h.Ready);
        Assert.Equal(line, Assert.Single(h.Released));
    }

    [Fact]
    public void InputWhileSpeaking_IsJudgedAfterTheTurn_NotStartedOverIt()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，讲个笑话"), TurnSource.Microphone);
        var first = h.Ready[0].Ctx.GenerationId;
        h.Manager.NoteSpeakingStarted(first);

        h.Manager.AddFinal(Harness.Mic("可缇，还有吗？"), TurnSource.Microphone);
        Assert.Single(h.Ready);
        Assert.True(h.Manager.CanCommit(first)); // the playing turn is not superseded
        Assert.Empty(h.Cancelled);

        h.Manager.CompleteTurn(first);
        Assert.Equal(2, h.Ready.Count);
    }

    [Fact]
    public void Events_AreRaisedOutsideTheStateLock()
    {
        using var h = new Harness();
        var otherThreadGotIn = false;
        h.Manager.TurnReady += (_, _) =>
            otherThreadGotIn = Task.Run(() => h.Manager.State).Wait(TimeSpan.FromSeconds(2));

        h.Manager.AddFinal(Harness.Mic("可缇，你好"), TurnSource.Microphone);

        Assert.True(otherThreadGotIn, "TurnReady ran while the manager lock was held");
    }

    [Fact]
    public void ExternalCancel_DropsCandidatesAndTheTurn()
    {
        using var h = new Harness();
        h.Manager.AddFinal(Harness.Mic("可缇，你好"), TurnSource.Microphone);
        h.Manager.ObservePartial(TurnSource.Loopback, 1, "op", 0, "我说");

        h.Manager.Cancel(TurnCancelReason.AccessRevoked);

        Assert.Equal(TurnCancelReason.AccessRevoked, Assert.Single(h.Cancelled).Reason);
        Assert.Equal(0, h.Manager.CandidateCount);
        Assert.Equal(TurnState.Observing, h.Manager.State);
    }
}
