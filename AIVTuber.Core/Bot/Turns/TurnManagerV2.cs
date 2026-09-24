namespace AIVTuber.Core.Bot.Turns;

/// <summary>
/// RT-04 Turn Manager v2. Separates 听见 (input observation), 被邀请 (invitation evidence)
/// and 可以出声 (commit/playback boundary) into an explicit state machine:
/// Observing → Candidate → Preparing → Ready → Speaking → Observing, with Cancel/Expire
/// from any in-flight state.
///
/// Key rules (plan §5 RT-04):
/// - partials only identify candidates / prepare context, never trigger generation;
/// - a server-confirmed final dispatches immediately — the legacy fixed 350ms merge is NOT
///   applied; a small merge window exists only for cross-source conflict or pending merge
///   context, with the reason recorded;
/// - while the opponent is still talking the turn may prepare but not play;
/// - sustained two-way talk expires uncommitted answers and re-decides on new context —
///   late replies are not queued;
/// - human voice recovery suppresses/cancels uncommitted output, but brief noise
///   (below the noise gate) must not shred AI speech;
/// - stop commands cancel immediately;
/// - a PK match change invalidates in-flight turns and old-match context.
///
/// Single-lock serial decision boundary: all state transitions happen under <c>_sync</c>.
/// All timers go through the replaceable <see cref="ITurnScheduler"/> so tests are deterministic.
/// </summary>
internal sealed class TurnManagerV2 : IDisposable
{
    private readonly object _sync = new();
    private readonly ITurnClock _clock;
    private readonly ITurnScheduler _scheduler;
    private readonly TurnManagerOptions _options;

    private readonly List<(TalkLine Line, TurnSource Source)> _buffer = [];
    private readonly List<(long DeadlineMs, Action Fire)> _timers = [];

    private TurnState _state = TurnState.Observing;
    private TurnContextV2? _activeContext;
    private long _snapshotRevision;
    private long _turnSeq;
    private long _generationSeq;
    private readonly long _randomBase = (long)Random.Shared.Next(1, 1 << 20) << 20;
    private string? _matchId;
    private bool _disposed;

    // Voice activity (VAD observations), per physical source.
    private bool _micActive;
    private bool _loopbackActive;
    private long _micActiveSince;
    private long _loopbackActiveSince;
    private long _micLastSustainedEndMs;
    private long _lastOtherSourceFinalMs;
    private TurnSource? _lastFinalSource;

    // Continuity evidence for the classifier.
    private string? _lastAssistantMessage;
    private long _aiSpokeAtMs = -1;

    public TurnManagerV2(TurnManagerOptions options, ITurnClock? clock = null, ITurnScheduler? scheduler = null)
    {
        _options = options ?? new TurnManagerOptions();
        _clock = clock ?? new StopwatchTurnClock();
        _scheduler = scheduler ?? new TimerTurnScheduler(_clock);
    }

    public TurnState State { get { lock (_sync) return _state; } }
    public long ActiveGenerationId { get { lock (_sync) return _activeContext?.GenerationId ?? 0; } }

    /// <summary>A turn passed all invitation and playback gates; generation may start.
    /// The handler must call <see cref="CompleteTurn"/> or let cancellation handle it.</summary>
    public event Action<TurnContextV2, IReadOnlyList<TalkLine>>? TurnReady;

    /// <summary>An in-flight turn was cancelled or expired; uncommitted output must not play.</summary>
    public event Action<TurnContextV2, TurnCancelReason>? TurnCancelled;

    /// <summary>Every invitation/silence judgement, with a machine-readable reason.</summary>
    public event Action<TurnDecision>? DecisionRecorded;

    public event Action<string>? StatusChanged;

    // ------------------------------------------------------------------ input

    /// <summary>Partial (not server-final) transcript. Used only for candidate identification
    /// and context preparation — never starts generation (speculative generation is a P1
    /// feature and stays disabled here). A retraction inside a partial drops the candidate.</summary>
    public void ObservePartial(TurnSource source, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_sync)
        {
            if (_disposed) return;
            if (InvitationClassifier.ContainsRetraction(text))
            {
                CancelLocked(TurnCancelReason.RetractedInvitation);
                return;
            }
            if (_state == TurnState.Observing)
            {
                _state = TurnState.Candidate;
                StatusChanged?.Invoke($"候选：{source} partial");
            }
        }
    }

    /// <summary>Server-confirmed final transcript. Dispatches immediately unless a merge
    /// window or opponent-still-talking hold applies (each with a recorded reason).</summary>
    public void AddFinal(TalkLine line, TurnSource source)
    {
        if (string.IsNullOrWhiteSpace(line.Text)) return;
        lock (_sync)
        {
            if (_disposed) return;

            // Rule 6: input arriving while a turn is Ready (generation not yet committed)
            // supersedes it — expire the uncommitted answer and re-decide with new context.
            // No queue of late replies. While Speaking (audio already playing) the new input
            // only buffers; interrupting live speech is governed by voice recovery / stop.
            if (_state == TurnState.Ready)
                CancelLocked(TurnCancelReason.Superseded);

            var now = _clock.NowMs;
            _buffer.Add((line, TurnSource: source));

            var crossSourceConflict = _lastFinalSource is { } prev && prev != source &&
                now - _lastOtherSourceFinalMs <= _options.CrossSourceMergeWindowMs;
            _lastFinalSource = source;
            _lastOtherSourceFinalMs = now;

            if (crossSourceConflict)
            {
                // Small window only for two-source conflict (rule 5), reason recorded.
                ScheduleLocked(_options.CrossSourceMergeWindowMs, () => { lock (_sync) EvaluateLocked("cross_source"); });
                StatusChanged?.Invoke("两路输入接近，小窗口合并");
                return;
            }

            EvaluateLocked("final_confirmed");
        }
    }

    /// <summary>Grades buffered input and either dispatches the turn, holds it (Preparing),
    /// or returns to Observing.</summary>
    private void EvaluateLocked(string mergeReason)
    {
        if (_disposed || _buffer.Count == 0) return;
        var now = _clock.NowMs;
        var decision = InvitationClassifier.Classify(
            _buffer.Select(b => (b.Line, b.Source)).ToArray(),
            _options.SelfNames,
            _lastAssistantMessage,
            now,
            _aiSpokeAtMs,
            _options.ConversationWindowMs);
        DecisionRecorded?.Invoke(decision);

        if (!decision.Respond)
        {
            _buffer.Clear();
            _state = TurnState.Observing;
            StatusChanged?.Invoke($"旁听：{decision.ReasonCode}");
            return;
        }

        // Rule 4: explicit invitation + complete text may start generation, but if the
        // opponent (loopback) is still talking we only prepare (Preparing), not dispatch.
        if (_loopbackActive)
        {
            _state = TurnState.Preparing;
            // The context exists from the moment the turn is accepted, so expiry/cancel
            // observers always receive the affected TurnContext.
            CreateContextLocked(decision, mergeReason);
            // Hold until the loopback side has been quiet for the quiet-hold window.
            var deadline = _clock.NowMs + _options.OpponentQuietHoldMs;
            ScheduleAtLocked(deadline, () => { lock (_sync) DispatchIfOpponentQuietLocked(); });
            // Hard expiry so a continuously talking opponent cannot pin the turn forever.
            ScheduleLocked(_options.TwoWayTalkExpiryMs, () =>
            {
                lock (_sync)
                {
                    if (_state == TurnState.Preparing)
                        CancelLocked(TurnCancelReason.TwoWayTalkExpired);
                }
            });
            StatusChanged?.Invoke("对面仍在说：准备但暂不出声");
            return;
        }

        DispatchLocked(decision, mergeReason);
    }

    private void DispatchIfOpponentQuietLocked()
    {
        if (_disposed || _state != TurnState.Preparing) return;
        if (_loopbackActive)
        {
            // Still talking — keep holding, with the same expiry budget.
            ScheduleLocked(_options.OpponentQuietHoldMs, () => { lock (_sync) DispatchIfOpponentQuietLocked(); });
            return;
        }
        var decision = InvitationClassifier.Classify(
            _buffer.Select(b => (b.Line, b.Source)).ToArray(),
            _options.SelfNames,
            _lastAssistantMessage,
            _clock.NowMs,
            _aiSpokeAtMs,
            _options.ConversationWindowMs);
        DecisionRecorded?.Invoke(decision);
        if (decision.Respond)
            DispatchLocked(decision, "opponent_quiet_after_hold");
        else
        {
            _buffer.Clear();
            _state = TurnState.Observing;
        }
    }

    /// <summary>Builds and stores the turn context from the buffered input (does not fire
    /// <see cref="TurnReady"/>).</summary>
    private void CreateContextLocked(TurnDecision decision, string mergeReason)
    {
        var segments = _buffer
            .Select(b => new InputSegmentRef(b.Source, $"seg-{b.Line.StartedAt.Ticks:x8}", b.Line.Identity, b.Line.Text))
            .ToArray();
        _snapshotRevision++;
        _activeContext = new TurnContextV2
        {
            TurnId = ++_turnSeq,
            // Program-generated, unique across the manager's lifetime; never model-controlled.
            GenerationId = ++_generationSeq + (long)_randomBase,
            MatchId = _matchId,
            InputSnapshotRevision = _snapshotRevision,
            Evidence = new InvitationEvidence(decision.Level, decision.ReasonCode, $"{decision.Detail};merge={mergeReason}"),
            InputSegments = segments,
            VisionSnapshotIds = [], // vision evidence arrives with VIS-02; none wired yet
        };
    }

    private void DispatchLocked(TurnDecision decision, string mergeReason)
    {
        var lines = _buffer.Select(b => b.Line).ToArray();
        CreateContextLocked(decision, mergeReason);
        _buffer.Clear();
        _state = TurnState.Ready;
        StatusChanged?.Invoke($"回合就绪：{decision.ReasonCode}");
        TurnReady?.Invoke(_activeContext!, lines);
    }

    // ------------------------------------------------------------------ commit / cancel

    /// <summary>Commit gate checked before any public output (subtitle/TTS/motion).
    /// Replaces the legacy revision-only check: voice recovery, match change, retraction
    /// and stop all invalidate the generation here.</summary>
    public bool CanCommit(long generationId)
    {
        lock (_sync)
        {
            if (_disposed) return false;
            if (_activeContext is not { } ctx || ctx.GenerationId != generationId) return false;
            if (ctx.IsCancelled) return false;
            if (_state is not (TurnState.Ready or TurnState.Speaking)) return false;
            // Human voice currently active: suppress NEW playback immediately (rule 7).
            if (_micActive || _loopbackActive) return false;
            return true;
        }
    }

    /// <summary>Signals that playback of this generation started.</summary>
    public void NoteSpeakingStarted(long generationId)
    {
        lock (_sync)
        {
            if (_activeContext is { GenerationId: var g } && g == generationId && _state == TurnState.Ready)
                _state = TurnState.Speaking;
        }
    }

    /// <summary>The turn finished normally (played out or model chose silence).</summary>
    public void CompleteTurn(long generationId)
    {
        lock (_sync)
        {
            if (_activeContext is not { GenerationId: var g } || g != generationId) return;
            _aiSpokeAtMs = _clock.NowMs;
            _state = TurnState.Observing;
            _activeContext = null;
            _buffer.Clear();
        }
    }

    /// <summary>Records the assistant's last spoken reply (question-then-answer continuity).</summary>
    public void NoteAssistantMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_sync)
        {
            _lastAssistantMessage = message;
            _aiSpokeAtMs = _clock.NowMs;
        }
    }

    private void CancelLocked(TurnCancelReason reason)
    {
        if (_disposed) return;
        _buffer.Clear();
        _scheduler.Cancel();
        _timers.Clear();
        if (_activeContext is { } ctx)
        {
            if (ctx.IsCancelled) return;
            ctx.CancelReason = reason;
            _activeContext = null;
            StatusChanged?.Invoke($"回合取消：{reason}");
            TurnCancelled?.Invoke(ctx, reason);
        }
        _state = TurnState.Observing;
    }

    // ------------------------------------------------------------------ external observations

    /// <summary>VAD voice activity from a physical source. Sustained human voice cancels
    /// uncommitted generations; sub-noise-gate blips neither cancel AI speech nor block commits.</summary>
    public void NoteVoiceActivity(TurnSource source, bool active)
    {
        lock (_sync)
        {
            if (_disposed) return;
            var now = _clock.NowMs;
            if (source == TurnSource.Microphone)
            {
                if (active && !_micActive)
                {
                    _micActive = true;
                    _micActiveSince = now;
                    // Brief noise should not shred AI speech: only cancel if the voice
                    // persists past the noise gate.
                    ScheduleLocked(_options.NoiseGateMs, () =>
                    {
                        lock (_sync)
                        {
                            if (!_micActive) return;
                            if (_state is TurnState.Ready or TurnState.Preparing or TurnState.Speaking)
                                CancelLocked(TurnCancelReason.HumanVoiceResumed);
                        }
                    });
                }
                else if (!active && _micActive)
                {
                    _micActive = false;
                    if (now - _micActiveSince >= _options.NoiseGateMs)
                        _micLastSustainedEndMs = now;
                }
            }
            else if (source == TurnSource.Loopback)
            {
                if (active && !_loopbackActive)
                {
                    _loopbackActive = true;
                    _loopbackActiveSince = now;
                }
                else if (!active && _loopbackActive)
                {
                    _loopbackActive = false;
                }
            }
        }
    }

    /// <summary>Stop command / button: cancel everything in flight immediately.</summary>
    public void NoteStopCommand()
    {
        lock (_sync) CancelLocked(TurnCancelReason.StopCommand);
    }

    /// <summary>PK match boundary: in-flight turns and buffered context from the old match
    /// must not be applied to the new opponent.</summary>
    public void NoteMatchChanged(string? newMatchId)
    {
        lock (_sync)
        {
            CancelLocked(TurnCancelReason.MatchChanged);
            _matchId = newMatchId;
        }
    }

    /// <summary>Sets the current match identity without treating it as a change.</summary>
    public void SetMatchId(string? matchId)
    {
        lock (_sync) _matchId = matchId;
    }

    // ------------------------------------------------------------------ timers

    private void ScheduleLocked(long delayMs, Action fire) =>
        ScheduleAtLocked(_clock.NowMs + Math.Max(1, delayMs), fire);

    private void ScheduleAtLocked(long deadlineMs, Action fire)
    {
        _timers.Add((deadlineMs, fire));
        RescheduleLocked();
    }

    /// <summary>Keeps at most one real timer: the earliest pending deadline. Its callback
    /// fires every due action under the lock. This is what makes the manager testable —
    /// a manual scheduler plus a fake clock reproduces every timing decision exactly.</summary>
    private void RescheduleLocked()
    {
        if (_disposed || _timers.Count == 0)
        {
            _scheduler.Cancel();
            return;
        }
        var earliest = _timers.Min(t => t.DeadlineMs);
        _scheduler.Schedule(earliest, () =>
        {
            List<Action> due;
            lock (_sync)
            {
                if (_disposed) return;
                var now = _clock.NowMs;
                due = [.. _timers.Where(t => t.DeadlineMs <= now).Select(t => t.Fire)];
                _timers.RemoveAll(t => t.DeadlineMs <= now);
                RescheduleLocked();
            }
            foreach (var fire in due) fire();
        });
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            CancelLocked(TurnCancelReason.Disposed);
            _scheduler.Dispose();
        }
    }
}

/// <summary>Explicit priorities and timing budgets. Defaults preserve the existing product
/// intent: master (microphone/manual) above danmaku above loopback opponent — mirroring
/// <c>RequestCoordinator.PriorityOf</c>.</summary>
internal sealed class TurnManagerOptions
{
    /// <summary>AI name + aliases used for invitation grading.</summary>
    public IReadOnlyList<string> SelfNames { get; set; } = [];

    /// <summary>P0 speculative generation is context-preparation only and stays disabled.
    /// This flag exists so P1 can enable it behind the same config boundary.</summary>
    public bool SpeculativeGenerationEnabled { get; set; } = false;

    /// <summary>Small merge window applied only on two-source conflict (rule 5), never the
    /// legacy unconditional 350ms.</summary>
    public int CrossSourceMergeWindowMs { get; set; } = 250;

    /// <summary>How long the opponent must be quiet before a Preparing turn may play.</summary>
    public int OpponentQuietHoldMs { get; set; } = 400;

    /// <summary>Upper bound for a Preparing turn while the opponent keeps talking.</summary>
    public int TwoWayTalkExpiryMs { get; set; } = 4000;

    /// <summary>Voice shorter than this counts as noise: it does not cancel AI speech.</summary>
    public int NoiseGateMs { get; set; } = 300;

    /// <summary>Window within which an AI reply keeps the conversation "continuous".</summary>
    public long ConversationWindowMs { get; set; } = 45_000;

    // Source priorities (rule 8). Defaults mirror RequestCoordinator.PriorityOf —
    // master/manual highest, loopback opponent lowest (but never starved: every final
    // is graded and dispatched on its own merits).
    public int MasterPriority { get; set; } = 3;
    public int DanmakuPriority { get; set; } = 1;
    public int OpponentPriority { get; set; } = 0;
}
