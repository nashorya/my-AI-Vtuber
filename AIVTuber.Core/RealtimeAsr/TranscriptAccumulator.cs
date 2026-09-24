namespace AIVTuber.Core.RealtimeAsr;

/// <summary>Why an update was (not) accepted.</summary>
public enum TranscriptAcceptance
{
    /// <summary>Applied. For finals this also means it was newly committed.</summary>
    Accepted = 0,
    /// <summary>Update belongs to an older capture epoch (reconnect / device restart).</summary>
    RejectedStaleEpoch = 1,
    /// <summary>Revision older than the newest already applied for this segment.</summary>
    RejectedStaleRevision = 2,
    /// <summary>The segment was already finalized; a second final is never committed.</summary>
    RejectedDuplicateFinal = 3,
    /// <summary>Non-final update arrived after the segment was finalized.</summary>
    RejectedAfterFinal = 4,
}

/// <summary>
/// Applies realtime transcript updates with plan §4.1 / §RT-02 semantics:
/// same-sentence updates replace (never append), stale revisions/epochs are rejected,
/// and a final segment is committed exactly once. Instances are single-threaded
/// (owned by one pump).
/// </summary>
public sealed class TranscriptAccumulator
{
    private sealed class SegmentState
    {
        public int LastRevision = -1;
        public bool FinalCommitted;
        public string Snapshot = string.Empty;
    }

    private readonly Dictionary<string, SegmentState> _segments = new(StringComparer.Ordinal);
    private readonly List<TranscriptUpdate> _committedFinals = [];
    private long _maxEpoch = -1;

    /// <summary>Finals committed so far, in commit order. Each appears exactly once.</summary>
    public IReadOnlyList<TranscriptUpdate> CommittedFinals => _committedFinals;

    /// <summary>
    /// Applies one update. When the return is <see cref="TranscriptAcceptance.Accepted"/> and
    /// the update is final, <paramref name="committedFinal"/> carries the newly committed update.
    /// </summary>
    public TranscriptAcceptance Apply(TranscriptUpdate update, out TranscriptUpdate? committedFinal)
    {
        committedFinal = null;
        if (update.CaptureEpoch < _maxEpoch) return TranscriptAcceptance.RejectedStaleEpoch;
        if (update.CaptureEpoch > _maxEpoch)
        {
            // Newer epoch: segments from older epochs stay in history as non-trigger record,
            // but same segment ids across epochs must never collide.
            _maxEpoch = update.CaptureEpoch;
        }

        var key = $"{update.CaptureEpoch}:{update.ProviderSessionId}:{update.SegmentId}";
        if (!_segments.TryGetValue(key, out var state))
        {
            state = new SegmentState();
            _segments[key] = state;
        }

        if (state.FinalCommitted)
            return update.IsFinal
                ? TranscriptAcceptance.RejectedDuplicateFinal
                : TranscriptAcceptance.RejectedAfterFinal;

        if (update.Revision <= state.LastRevision)
            return TranscriptAcceptance.RejectedStaleRevision;

        state.LastRevision = update.Revision;
        state.Snapshot = update.TextSnapshot;

        if (!update.IsFinal) return TranscriptAcceptance.Accepted;

        state.FinalCommitted = true;
        _committedFinals.Add(update);
        committedFinal = update;
        return TranscriptAcceptance.Accepted;
    }

    /// <summary>The current best snapshot for a not-yet-final segment, or null.</summary>
    public string? CurrentCandidate(string providerSessionId, string segmentId)
        => _segments.TryGetValue($"{_maxEpoch}:{providerSessionId}:{segmentId}", out var s)
           && !s.FinalCommitted
            ? s.Snapshot
            : null;
}
