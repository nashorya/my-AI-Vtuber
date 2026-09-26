namespace AIVTuber.Core.RealtimeAsr;

/// <summary>Tunables for <see cref="RealtimeAsrPump"/>. Defaults are safe structural values,
/// not measured provider limits.</summary>
public sealed record RealtimeAsrOptions
{
    /// <summary>Capacity of the capture→sender channel, in milliseconds of audio.
    /// Overflow tears down and rebuilds the session with an explicit break report —
    /// never silently DropOldest.</summary>
    public int BufferCapacityMs { get; init; } = 4000;

    /// <summary>Audio kept in a rolling pre-roll buffer at all times, replayed when a new
    /// session starts (cost-saving reconnects must not lose the start of an utterance).</summary>
    public int PrerollMs { get; init; } = 1000;

    /// <summary>Continuous silence after which the session is finished to save cost.
    /// 0 = never idle-disconnect.</summary>
    public int IdleDisconnectMs { get; init; } = 30000;

    /// <summary>Bytes per millisecond for 16 kHz mono PCM16.</summary>
    public const int BytesPerMs = 32;

    public static RealtimeAsrOptions Default { get; } = new();
}

/// <summary>Structural counters for the realtime path. Intentionally minimal — no new
/// telemetry infrastructure in this change (owned by a separate branch).</summary>
public sealed class RealtimeAsrMetrics
{
    public int SessionsStarted;
    public int IdleReconnects;
    public int OverflowBreaks;
    public int LateUpdateDrops;
    public long FramesSent;
    /// <summary>Cold-start latency (ms) of each session resumption after an idle disconnect,
    /// measured from first voiced frame to session ready. Capped at 32 samples.</summary>
    public readonly List<long> ColdStartSamples = [];
    /// <summary>Wall-clock instants (TickCount64) at which the first audio frame of a session
    /// was handed to the provider — the RT-02 acceptance anchor (asr_first_audio_sent).</summary>
    public readonly List<long> FirstAudioSentAt = [];

    internal void NoteColdStart(long ms)
    {
        if (ColdStartSamples.Count < 32) ColdStartSamples.Add(ms);
    }
}
