using AIVTuber.Core.Audio;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>How the final flag on a <see cref="TranscriptUpdate"/> was derived.</summary>
public enum TranscriptFinalKind
{
    /// <summary>Not a final update.</summary>
    None = 0,
    /// <summary>The provider explicitly marked this utterance result as final/stable.</summary>
    VendorFinal = 1,
    /// <summary>The client closed the audio endpoint and snapshotted the latest candidate text
    /// as a presumed final. This is NOT a vendor final — providers whose streaming mode lacks
    /// sentence-level finals (observed for some Volcano/豆包 result modes) use this path, and
    /// consumers must treat it as a lower-confidence commit. See plan §RT-03.</summary>
    ClientEndpointSnapshot = 2,
}

/// <summary>Identity snapshot of the PK opponent taken when the audio was captured, not when
/// the transcript update is consumed. Late results after a match switch must not be re-labelled
/// with a newer opponent.</summary>
public sealed record OpponentSnapshot(string? Name, string? Uid, string? MatchId);

/// <summary>
/// One realtime recognition update for a single sentence/segment, per plan §4.1.
/// Same-sentence updates REPLACE the previous snapshot (never append); <see cref="Revision"/>
/// increases within a sentence; a final is committed exactly once.
/// </summary>
/// <param name="Source">Which physical audio source produced this audio.</param>
/// <param name="CaptureEpoch">Per-source counter bumped on device restart, route change,
/// mute gap, buffer overflow teardown — anything that breaks the capture timeline.</param>
/// <param name="ProviderSessionId">Distinguishes reconnects / server-side sessions.</param>
/// <param name="SegmentId">Stable provider sentence id (not a hash of the text).</param>
/// <param name="Revision">Monotonic within one SegmentId.</param>
/// <param name="TextSnapshot">Full current snapshot of this sentence.</param>
/// <param name="IsFinal">True only for an explicit final (vendor or marked client endpoint).</param>
/// <param name="AudioStartMs">Audio-timeline start of this sentence, ms on the local
/// monotonic clock (<see cref="Environment.TickCount64"/>).</param>
/// <param name="AudioEndMs">Audio-timeline end, same clock.</param>
/// <param name="ReceivedAt">Monotonic receive time of this update.</param>
public sealed record TranscriptUpdate(
    AudioSource Source,
    long CaptureEpoch,
    string ProviderSessionId,
    string SegmentId,
    int Revision,
    string TextSnapshot,
    bool IsFinal,
    long AudioStartMs,
    long AudioEndMs,
    long ReceivedAt,
    OpponentSnapshot? OpponentSnapshot = null,
    TranscriptFinalKind FinalKind = TranscriptFinalKind.None);
