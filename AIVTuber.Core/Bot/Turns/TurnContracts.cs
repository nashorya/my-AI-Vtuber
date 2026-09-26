namespace AIVTuber.Core.Bot.Turns;

/// <summary>Physical input source feeding the turn manager. Mirrors the priority model of
/// <see cref="RequestCoordinator"/>: manual (master stop) &gt; microphone (master) &gt; danmaku &gt; loopback (opponent).</summary>
internal enum TurnSource
{
    Loopback,
    Danmaku,
    Microphone,
    System,
    Manual,
}

/// <summary>States of the RT-04 turn manager v2. Observing → Candidate → Preparing → Ready → Speaking → Observing;
/// any in-flight state can be cancelled or expire back to Observing.</summary>
internal enum TurnState
{
    Observing,
    Candidate,
    Preparing,
    Ready,
    Speaking,
}

/// <summary>Cancellation causes, part of the TurnContext contract (plan §4.2).</summary>
internal enum TurnCancelReason
{
    None,
    StopCommand,
    HumanVoiceResumed,
    RetractedInvitation,
    MatchChanged,
    TwoWayTalkExpired,
    Superseded,
    Disposed,
    /// <summary>The account grant ended (sign-out, expiry, denial).</summary>
    AccessRevoked,
    /// <summary>The streamer paused the companion.</summary>
    Paused,
}

/// <summary>Graded invitation evidence (plan §5 RT-04 rule 2). Higher = stronger.</summary>
internal enum InvitationLevel
{
    None = 0,
    WeakMention = 1,
    /// <summary>No explicit invitation, but the input may continue a conversation with the AI
    /// (or its addressee is ambiguous). The turn goes to the main model, whose structured
    /// speak / pass / thought decision is the semantic judgement — no extra cloud call.</summary>
    SemanticDecision = 2,
    ContinuousDialogue = 3,
    ResponseToAiQuestion = 4,
    DirectQuestion = 5,
    NameCall = 6,
}

/// <summary>Machine-readable decision returned for every invitation judgement (RT-04 acceptance:
/// "邀请/沉默决策必须带简短机器可读原因").</summary>
internal sealed record TurnDecision(
    bool Respond,
    InvitationLevel Level,
    string ReasonCode,
    string Detail = "")
{
    public static TurnDecision Silence(InvitationLevel level, string reasonCode, string detail = "") =>
        new(false, level, reasonCode, detail);

    public static TurnDecision RespondAt(InvitationLevel level, string reasonCode, string detail = "") =>
        new(true, level, reasonCode, detail);
}

/// <summary>Reference to one input segment that participated in a turn decision.</summary>
internal sealed record InputSegmentRef(TurnSource Source, string SegmentId, TalkIdentity Identity, string Text);

/// <summary>Per-turn evidence summary recorded in <see cref="TurnContextV2"/>.</summary>
internal sealed record InvitationEvidence(InvitationLevel Level, string ReasonCode, string MatchedSignal);

/// <summary>Turn contract per plan §4.2. GenerationId is program-generated and opaque to the model.</summary>
internal sealed record TurnContextV2
{
    public required long TurnId { get; init; }
    public required long GenerationId { get; init; }
    public string? MatchId { get; init; }
    public required long InputSnapshotRevision { get; init; }
    public required InvitationEvidence Evidence { get; init; }
    public required IReadOnlyList<InputSegmentRef> InputSegments { get; init; }
    public IReadOnlyList<string> VisionSnapshotIds { get; init; } = [];
    public TurnCancelReason CancelReason { get; internal set; } = TurnCancelReason.None;
    /// <summary>True when the turn was already playing audio when it was cancelled. Suppressing
    /// output that has not started and interrupting audio that is playing are different acts.</summary>
    public bool WasSpeakingWhenCancelled { get; internal set; }

    public bool IsCancelled => CancelReason != TurnCancelReason.None;
}
