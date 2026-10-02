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
/// Events are raised only after the lock is released (outbox), so handlers may take their
/// own locks, call back into the manager or stop audio without deadlocking the state machine.
/// Every timer is bound to the turn / voice episode / buffer that created it, so a stale
/// timer never acts on a later turn.
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
    private List<Action> _outbox = [];

    // Partial candidates, one per (source, capture epoch, segment). Revisions of the same
    // segment replace each other; they are one candidate, never extra "invitations".
    private readonly Dictionary<(TurnSource Source, long Epoch, string Segment), (int Revision, string Text)> _candidates = new();

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
    private long _micEpisode;
    private long _bufferEpoch;
    private long _lastOtherSourceFinalMs;
    private TurnSource? _lastFinalSource;
    private long _lastOpponentFinalMs = -1;

    // Continuity evidence for the classifier.
    private string? _lastAssistantMessage;
    private long _aiSpokeAtMs = -1;
    private TurnSource[] _lastAiTurnSources = [];

    public TurnManagerV2(TurnManagerOptions options, ITurnClock? clock = null, ITurnScheduler? scheduler = null)
    {
        _options = options ?? new TurnManagerOptions();
        _clock = clock ?? new StopwatchTurnClock();
        _scheduler = scheduler ?? new TimerTurnScheduler(_clock);
    }

    public TurnState State { get { lock (_sync) return _state; } }
    public long ActiveGenerationId { get { lock (_sync) return _activeContext?.GenerationId ?? 0; } }
    /// <summary>Open partial candidates (one per source/segment, whatever its revision count).</summary>
    public int CandidateCount { get { lock (_sync) return _candidates.Count; } }

    /// <summary>A turn passed all invitation and playback gates; generation may start.
    /// The handler must call <see cref="CompleteTurn"/> or let cancellation handle it.</summary>
    public event Action<TurnContextV2, IReadOnlyList<TalkLine>>? TurnReady;

    /// <summary>An in-flight turn was cancelled or expired; uncommitted output must not play.</summary>
    public event Action<TurnContextV2, TurnCancelReason>? TurnCancelled;

    /// <summary>Every invitation/silence judgement, with a machine-readable reason.</summary>
    public event Action<TurnDecision>? DecisionRecorded;

    public event Action<string>? StatusChanged;

    /// <summary>Buffered lines dropped without being dispatched (silence decision, cancel,
    /// expiry). The owner must release any bookkeeping it holds for them.</summary>
    public event Action<IReadOnlyList<TalkLine>>? LinesReleased;

    // ------------------------------------------------------------------ input

    /// <summary>Partial transcript without segment identity (kept for callers that have none).</summary>
    public void ObservePartial(TurnSource source, string text) =>
        ObservePartial(source, captureEpoch: 0, segmentId: "", revision: -1, text);

    /// <summary>Partial (not server-final) transcript. Used only for candidate identification
    /// and context preparation — never starts generation (speculative generation is a P1
    /// feature and stays disabled here). Revisions of one segment update one candidate; a
    /// retraction inside a partial drops the candidate and any uncommitted turn.</summary>
    public void ObservePartial(TurnSource source, long captureEpoch, string segmentId, int revision, string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return;
        lock (_sync)
        {
            if (_disposed) return;
            var key = (source, captureEpoch, segmentId);
            if (revision >= 0 && _candidates.TryGetValue(key, out var existing) && existing.Revision > revision)
                return; // out-of-order older revision
            if (InvitationClassifier.ContainsRetraction(text))
            {
                _candidates.Remove(key);
                CancelLocked(TurnCancelReason.RetractedInvitation);
                if (_candidates.Count == 0 && _state == TurnState.Candidate) _state = TurnState.Observing;
            }
            else
            {
                var isNew = !_candidates.ContainsKey(key);
                _candidates[key] = (revision, text);
                if (_state == TurnState.Observing)
                {
                    _state = TurnState.Candidate;
                    PostStatusLocked($"候选：{source} partial");
                }
                else if (isNew)
                {
                    PostStatusLocked($"候选更新：{source} partial");
                }
            }
        }
        FlushOutbox();
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

            // The final closes this source's open partial segment(s).
            foreach (var key in _candidates.Keys.Where(k => k.Source == source).ToArray())
                _candidates.Remove(key);

            var now = _clock.NowMs;
            _buffer.Add((line, TurnSource: source));
            if (source == TurnSource.Loopback) _lastOpponentFinalMs = now;

            var crossSourceConflict = _lastFinalSource is { } prev && prev != source &&
                now - _lastOtherSourceFinalMs <= _options.CrossSourceMergeWindowMs;
            _lastFinalSource = source;
            _lastOtherSourceFinalMs = now;

            if (crossSourceConflict)
            {
                // Small window only for two-source conflict (rule 5), reason recorded. Bound
                // to this buffer: once it is evaluated or dropped, the timer does nothing.
                var bufferEpoch = _bufferEpoch;
                ScheduleLocked(_options.CrossSourceMergeWindowMs, () =>
                {
                    if (_bufferEpoch == bufferEpoch) EvaluateLocked("cross_source");
                });
                PostStatusLocked("两路输入接近，小窗口合并");
            }
            else
            {
                EvaluateLocked("final_confirmed");
            }
        }
        FlushOutbox();
    }

    /// <summary>Grades buffered input and either dispatches the turn, holds it (Preparing),
    /// or returns to Observing.</summary>
    private void EvaluateLocked(string mergeReason)
    {
        if (_disposed || _buffer.Count == 0) return;
        // Audio of the current turn is playing: new input is kept and judged when it ends
        // (CompleteTurn) instead of starting a second turn over it. Stop and sustained human
        // voice still cancel through their own paths.
        if (_state == TurnState.Speaking)
        {
            PostStatusLocked("AI 正在说：新输入已记录，说完再判断");
            return;
        }
        var decision = ClassifyLocked();
        PostLocked(() => DecisionRecorded?.Invoke(decision));

        if (!decision.Respond)
        {
            ReleaseBufferLocked();
            _state = _candidates.Count > 0 ? TurnState.Candidate : TurnState.Observing;
            PostStatusLocked($"旁听：{decision.ReasonCode}");
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
            var turnId = _activeContext!.TurnId;
            // Hold until the loopback side has been quiet for the quiet-hold window.
            ScheduleForTurnLocked(_options.OpponentQuietHoldMs, turnId, DispatchIfOpponentQuietLocked);
            // Hard expiry so a continuously talking opponent cannot pin the turn forever.
            ScheduleForTurnLocked(_options.TwoWayTalkExpiryMs, turnId, () =>
            {
                if (_state == TurnState.Preparing)
                    CancelLocked(TurnCancelReason.TwoWayTalkExpired);
            });
            PostStatusLocked("对面仍在说：准备但暂不出声");
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
            ScheduleForTurnLocked(_options.OpponentQuietHoldMs, _activeContext!.TurnId, DispatchIfOpponentQuietLocked);
            return;
        }
        var decision = ClassifyLocked();
        PostLocked(() => DecisionRecorded?.Invoke(decision));
        if (decision.Respond)
            DispatchLocked(decision, "opponent_quiet_after_hold");
        else
        {
            ReleaseBufferLocked();
            _activeContext = null;
            _state = TurnState.Observing;
        }
    }

    private TurnDecision ClassifyLocked() => InvitationClassifier.Classify(
        _buffer.Select(b => (b.Line, b.Source)).ToArray(),
        new ClassifierContext(
            _options.SelfNames,
            _lastAssistantMessage,
            _clock.NowMs,
            _aiSpokeAtMs,
            _options.ConversationWindowMs,
            _lastAiTurnSources,
            _lastOpponentFinalMs));

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
        ClearBufferLocked();
        _state = TurnState.Ready;
        PostStatusLocked($"回合就绪：{decision.ReasonCode}");
        var ctx = _activeContext!;
        PostLocked(() => TurnReady?.Invoke(ctx, lines));
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

    /// <summary>Physical sources that contributed to the active turn.</summary>
    private TurnSource[] ActiveSourcesLocked() =>
        _activeContext?.InputSegments.Select(s => s.Source).Distinct().ToArray() ?? [];

    /// <summary>The turn finished normally (played out or model chose silence).</summary>
    public void CompleteTurn(long generationId)
    {
        lock (_sync)
        {
            if (_activeContext is not { GenerationId: var g } || g != generationId) return;
            _aiSpokeAtMs = _clock.NowMs;
            _state = _candidates.Count > 0 ? TurnState.Candidate : TurnState.Observing;
            _activeContext = null;
            // Input that arrived while this turn was speaking is evaluated now, not dropped.
            if (_buffer.Count > 0) EvaluateLocked("after_turn");
        }
        FlushOutbox();
    }

    /// <summary>Records the assistant's last spoken reply (question-then-answer continuity).</summary>
    public void NoteAssistantMessage(string message)
    {
        if (string.IsNullOrWhiteSpace(message)) return;
        lock (_sync)
        {
            _lastAssistantMessage = message;
            _aiSpokeAtMs = _clock.NowMs;
            var sources = ActiveSourcesLocked();
            if (sources.Length > 0) _lastAiTurnSources = sources;
        }
    }

    private void CancelLocked(TurnCancelReason reason)
    {
        if (_disposed) return;
        ReleaseBufferLocked();
        if (_activeContext is { } ctx)
        {
            if (ctx.IsCancelled) return;
            ctx.CancelReason = reason;
            ctx.WasSpeakingWhenCancelled = _state == TurnState.Speaking;
            _activeContext = null;
            PostStatusLocked($"回合取消：{reason}");
            PostLocked(() => TurnCancelled?.Invoke(ctx, reason));
        }
        _state = _candidates.Count > 0 && reason != TurnCancelReason.RetractedInvitation
            ? TurnState.Candidate
            : TurnState.Observing;
    }

    /// <summary>Drops buffered lines that will not be dispatched and tells the owner.</summary>
    private void ReleaseBufferLocked()
    {
        if (_buffer.Count == 0) { ClearBufferLocked(); return; }
        var released = _buffer.Select(b => b.Line).ToArray();
        ClearBufferLocked();
        PostLocked(() => LinesReleased?.Invoke(released));
    }

    private void ClearBufferLocked()
    {
        _buffer.Clear();
        _bufferEpoch++;
    }

    private void PostLocked(Action action) => _outbox.Add(action);

    private void PostStatusLocked(string status) => PostLocked(() => StatusChanged?.Invoke(status));

    /// <summary>Raises queued events outside the lock, in the order they were produced.</summary>
    private void FlushOutbox()
    {
        List<Action> pending;
        lock (_sync)
        {
            if (_outbox.Count == 0) return;
            pending = _outbox;
            _outbox = [];
        }
        foreach (var action in pending) action();
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
                    var episode = ++_micEpisode;
                    // Brief noise should not shred AI speech: only cancel if THIS voice episode
                    // persists past the noise gate (a later short blip is a new episode).
                    ScheduleLocked(_options.NoiseGateMs, () =>
                    {
                        if (!_micActive || _micEpisode != episode) return;
                        if (_state is TurnState.Ready or TurnState.Preparing or TurnState.Speaking)
                            CancelLocked(TurnCancelReason.HumanVoiceResumed);
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
        FlushOutbox();
    }

    /// <summary>Stop command / button: cancel everything in flight immediately.</summary>
    public void NoteStopCommand() => Cancel(TurnCancelReason.StopCommand);

    /// <summary>Hard cancel for an external reason (stop, pause, lost account access):
    /// the in-flight turn and every buffered or candidate input are dropped.</summary>
    public void Cancel(TurnCancelReason reason)
    {
        lock (_sync)
        {
            _candidates.Clear();
            CancelLocked(reason);
            _state = TurnState.Observing;
        }
        FlushOutbox();
    }

    /// <summary>PK match boundary: in-flight turns and buffered context from the old match
    /// must not be applied to the new opponent.</summary>
    public void NoteMatchChanged(string? newMatchId)
    {
        lock (_sync)
        {
            _candidates.Clear();
            CancelLocked(TurnCancelReason.MatchChanged);
            _matchId = newMatchId;
            _state = TurnState.Observing;
        }
        FlushOutbox();
    }

    /// <summary>Sets the current match identity without treating it as a change.</summary>
    public void SetMatchId(string? matchId)
    {
        lock (_sync) _matchId = matchId;
    }

    // ------------------------------------------------------------------ timers

    private void ScheduleLocked(long delayMs, Action fire) =>
        ScheduleAtLocked(_clock.NowMs + Math.Max(1, delayMs), fire);

    /// <summary>Schedules an action that only runs while the same turn is still active.</summary>
    private void ScheduleForTurnLocked(long delayMs, long turnId, Action fire) =>
        ScheduleLocked(delayMs, () =>
        {
            if (_activeContext?.TurnId == turnId) fire();
        });

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
                // Timer actions run under the same lock as every other transition.
                foreach (var fire in due)
                    if (!_disposed) fire();
                RescheduleLocked();
            }
            FlushOutbox();
        });
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            CancelLocked(TurnCancelReason.Disposed);
            _disposed = true;
            _timers.Clear();
            _scheduler.Dispose();
        }
        FlushOutbox();
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
