using AIVTuber.Core.Audio;

namespace AIVTuber.Core.RealtimeAsr;

/// <summary>
/// A single realtime (bidirectional streaming) ASR session for one physical audio source,
/// per plan §4.1 / §RT-02. One sender, one receiver; all coordination happens through the
/// owning pump, never by concurrent callers.
/// </summary>
public interface IRealtimeAsrSession : IAsyncDisposable
{
    /// <summary>Provider-side session id (voice id / request id), for reconnect dedup.</summary>
    string ProviderSessionId { get; }

    /// <summary>Connects and waits for application-level readiness (distinct from connect).</summary>
    Task StartAsync(CancellationToken cancellationToken);

    /// <summary>Sends one 16 kHz mono PCM16 frame. May await network I/O — called only from
    /// the pump's consumer loop, never from the capture callback.</summary>
    ValueTask SendFrameAsync(ReadOnlyMemory<byte> pcm16k, CancellationToken cancellationToken);

    /// <summary>Signals graceful end-of-audio (protocol end message). Subsequent results may
    /// still arrive on <see cref="ReadUpdatesAsync"/> until the provider closes the session.</summary>
    ValueTask FinishAudioAsync(CancellationToken cancellationToken);

    /// <summary>Aborts the session without waiting for further results.</summary>
    Task CancelAsync();

    /// <summary>Update stream for this session. Exactly one reader.</summary>
    IAsyncEnumerable<TranscriptUpdate> ReadUpdatesAsync(CancellationToken cancellationToken);
}

/// <summary>Creates per-source sessions. Each call returns an independent session with its own
/// socket/buffer/cancellation — two simultaneous sources never share state.</summary>
public interface IRealtimeAsrSessionFactory
{
    IRealtimeAsrSession Create(AudioSource source, long captureEpoch, OpponentSnapshot? opponent);
}
