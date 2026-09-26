namespace AIVTuber.Core.Diagnostics;

/// <summary>Monotonic clock abstraction for realtime tracing. Monotonic milliseconds are
/// the primary timeline; wall time is kept only for correlating with other logs.</summary>
public interface IRealtimeClock
{
    /// <summary>Monotonic, non-decreasing milliseconds (e.g. Environment.TickCount64).</summary>
    long MonotonicMs { get; }

    /// <summary>Wall-clock UTC stamp, correlation only — never used for ordering.</summary>
    DateTimeOffset UtcNow { get; }
}

/// <summary>Production clock: Environment.TickCount64 (monotonic, survives short system clock
/// adjustments) + DateTime.UtcNow for correlation.</summary>
public sealed class SystemRealtimeClock : IRealtimeClock
{
    public static readonly SystemRealtimeClock Instance = new();
    public long MonotonicMs => Environment.TickCount64;
    public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
}

/// <summary>Test clock: manually advanced so tests can assert strict event ordering.</summary>
public sealed class FakeRealtimeClock : IRealtimeClock
{
    private long _ms;
    public long MonotonicMs => _ms;
    public DateTimeOffset UtcNow { get; set; } = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);
    public void Advance(long deltaMs) => _ms += deltaMs;
    public void Set(long ms) => _ms = ms;
}

/// <summary>A single tracing event. Carries only timing + names — by design there is no
/// payload field for audio, transcripts, images or secrets.</summary>
public sealed record RealtimeTraceEvent(
    string TurnId,
    string Event,
    long MonotonicMs,
    DateTimeOffset WallUtc);

/// <summary>
/// Lightweight chain tracer for the realtime pipeline (RT-00). One TurnId threads through
/// ASR → LLM → TTS → playback. Disabled by default (config realtime.trace_enabled=false);
/// a shared <see cref="Disabled"/> instance makes marks near-free no-ops.
///
/// Concurrency note: <see cref="CurrentTurnId"/> is a single volatile slot, not an AsyncLocal,
/// because turn boundaries are set on the audio/VAD threads and consumed on coordinator
/// thread-pool threads where execution context does not flow. This matches the product's
/// single-active-turn pipeline; RT-04's turn manager should replace it with explicit
/// TurnContext propagation.
/// </summary>
public sealed class RealtimeTrace
{
    public static class Events
    {
        // Input / VAD (capture timeline, not ASR request time)
        public const string InputFirst = "input_first";
        public const string InputLastVoiced = "input_last_voiced";
        // ASR session lifecycle
        public const string AsrConnectStart = "asr_connect_start";
        public const string AsrReady = "asr_ready";
        public const string AsrFirstAudioSent = "asr_first_audio_sent";
        public const string AsrFirstPartial = "asr_first_partial";
        public const string AsrSegmentFinal = "asr_segment_final";
        // Turn boundaries
        public const string TurnCandidate = "turn_candidate";
        public const string TurnCommitReady = "turn_commit_ready";
        // LLM
        public const string LlmRequest = "llm_request";
        public const string LlmFirstContent = "llm_first_content";
        public const string LlmFirstSpeechSegment = "llm_first_speech_segment";
        public const string LlmDone = "llm_done";
        // TTS
        public const string TtsRequest = "tts_request";
        public const string TtsFirstEncodedAudio = "tts_first_encoded_audio";
        public const string TtsFirstPcm = "tts_first_pcm";
        /// <summary>RT-06 bidi: flush ack = audio delivered for the turn (not played).</summary>
        public const string TtsFlushAcked = "tts_flush_acked";
        // Playback (device consumption; distinguish from enqueue)
        public const string PlaybackFirst = "playback_first";
        public const string PlaybackEnd = "playback_end";
        // Cancellation
        public const string CancelRequested = "cancel_requested";
        /// <summary>Vendor confirmed the cancel (RT-06 bidi: task_cancel ack).</summary>
        public const string CancelAcked = "cancel_acked";
        /// <summary>RT-06 bidi: cancel ack timed out; old socket dropped, connection epoch rebuilt,
        /// all packets from the old connection are void.</summary>
        public const string CancelEpochRebuild = "cancel_epoch_rebuild";
        public const string PlaybackStopped = "playback_stopped";
        // Vision
        public const string VisionCapture = "vision_capture";
        public const string VisionResult = "vision_result";
        public const string SnapshotUsed = "snapshot_used";
    }

    /// <summary>Shared no-op instance: Enabled=false, Mark is a no-op.</summary>
    public static readonly RealtimeTrace Disabled = new(enabled: false, SystemRealtimeClock.Instance);

    private readonly IRealtimeClock _clock;
    private readonly object _lock = new();
    private readonly List<RealtimeTraceEvent> _events = [];
    private long _turnSequence;

    public RealtimeTrace(bool enabled, IRealtimeClock clock)
    {
        Enabled = enabled;
        _clock = clock ?? throw new ArgumentNullException(nameof(clock));
    }

    public bool Enabled { get; }

    /// <summary>TurnId of the turn currently in flight (see concurrency note on the class).</summary>
    public string? CurrentTurnId { get; private set; }

    /// <summary>Starts a new turn: allocates a TurnId and makes it current.
    /// Does not reset the timeline — the same turn's earlier marks stay queryable.</summary>
    public string BeginTurn()
    {
        var id = $"t{Interlocked.Increment(ref _turnSequence)}";
        CurrentTurnId = id;
        return id;
    }

    /// <summary>Records an event for the current turn. No-op when disabled.</summary>
    public void Mark(string eventName) => Mark(CurrentTurnId, eventName);

    /// <summary>Records an event for an explicit turn. No-op when disabled.</summary>
    public void Mark(string? turnId, string eventName)
    {
        if (!Enabled) return;
        var stamp = new RealtimeTraceEvent(
            turnId ?? "none",
            eventName,
            _clock.MonotonicMs,
            _clock.UtcNow);
        lock (_lock) _events.Add(stamp);
        DebugLog.Write($"[RT-trace] turn={stamp.TurnId} {stamp.Event} mono={stamp.MonotonicMs}");
    }

    /// <summary>Snapshot of recorded events (test/inspection only; bounded by turn count).</summary>
    public IReadOnlyList<RealtimeTraceEvent> EventsSnapshot
    {
        get { lock (_lock) return _events.ToArray(); }
    }

    /// <summary>Monotonic stamp of the first occurrence of <paramref name="eventName"/> for
    /// <paramref name="turnId"/>, or null when absent.</summary>
    public long? FirstMonotonicMs(string turnId, string eventName)
    {
        lock (_lock)
        {
            foreach (var e in _events)
                if (e.TurnId == turnId && e.Event == eventName)
                    return e.MonotonicMs;
        }
        return null;
    }
}
