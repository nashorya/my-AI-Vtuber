namespace AIVTuber.Core.Cortico;

/// <summary>
/// The Cortico sidecar as a visual follower. It decides when each piece may start (upstream
/// pacing: boundary pauses, blocking gestures) and drives the rig from the app's real playback;
/// it never synthesizes or plays audio itself.
/// </summary>
public interface ICorticoPerformance
{
    /// <summary>Cortico script grammar and vocabulary for the system prompt.</summary>
    string ScriptGrammar { get; }

    /// <summary>Longest time the host may legitimately hold a ready piece (one beat), in ms.</summary>
    int MaxHoldMs { get; }

    /// <summary>False once the sidecar exited or its 1 s heartbeat lapsed for more than 2 s.</summary>
    bool IsAlive { get; }

    /// <summary>Starts one reply. Host requests for this reply arrive on <paramref name="handler"/>.</summary>
    Task<ICorticoStage> BeginAsync(ICorticoAudioHandler handler, CancellationToken ct);

    /// <summary>Stops every performance begun before this call: queued pieces, held states, gestures.</summary>
    Task InterruptAsync(CancellationToken ct);
}

/// <summary>Host → app requests for one reply. Called on the IPC reader; must not block.</summary>
public interface ICorticoAudioHandler
{
    /// <summary>Start synthesizing this clean text and report PCM for it.</summary>
    void Synth(long pieceId, string text);
    /// <summary>Upstream no longer wants this piece: stop its synthesis and drop its audio.</summary>
    void CancelSynth(long pieceId);
    /// <summary>Upstream's moment to speak this piece.</summary>
    void Play(long pieceId);
    /// <summary>Upstream cut this piece while it was playing.</summary>
    void Stop(long pieceId);
    /// <summary>A beat fired (liveness for the hold watchdog).</summary>
    void Cue();
    /// <summary>The rig side ended this reply (model switch): finish the current piece, the rest voice-only.</summary>
    void Aborted(string reason);
}

/// <summary>One reply being performed. Dispose always; disposing an unfinished stage interrupts it.</summary>
public interface ICorticoStage : IAsyncDisposable
{
    /// <summary>Performs one approved script segment (appended after the earlier ones).</summary>
    Task FeedAsync(string script, CancellationToken ct);
    /// <summary>No more segments: waits until everything fed has been performed.</summary>
    Task CompleteAsync(CancellationToken ct);
    Task PcmAsync(long pieceId, int sampleRate, byte[] pcm);
    Task SynthEndAsync(long pieceId);
    Task SynthErrorAsync(long pieceId, string message);
    /// <summary>The piece's first audio actually reached the sound card.</summary>
    Task StartedAsync(long pieceId);
    Task EndedAsync(long pieceId);
    Task StoppedAsync(long pieceId);
}
