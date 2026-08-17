namespace AIVTuber.Core.Avatar;

/// <summary>
/// Backend-agnostic avatar control surface shared by the in-process PNG renderer and VTS.
/// Void members are hot-path/instant and must never throw; Task members are intents whose
/// backend failures matter — the orchestrator runs them inside its generation-managed
/// command queue so errors are reported per turn and superseded turns cannot fire them.
/// </summary>
public interface IAvatarController : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Lip-sync RMS sample (typically ~30ms). Thread-safe; may be called from audio thread.</summary>
    void OnRms(float rms);

    /// <summary>Apply an emotion for <paramref name="hold"/> (default 1500ms). Backends may
    /// degrade unknown names (pixel falls back to neutral); VTS throws on unmapped names so the
    /// command queue can surface them.</summary>
    Task SetEmotionAsync(string emotion, TimeSpan? hold = null, CancellationToken ct = default);

    /// <summary>Trigger a named semantic action (e.g. head_shake). Throws when the backend
    /// has no mapping for <paramref name="action"/>.</summary>
    Task TriggerActionAsync(string action, CancellationToken ct = default);

    /// <summary>Whole-image pose switch (front / tilt_* / side_*). No-op on backends without poses.</summary>
    void SetPose(string pose);

    /// <summary>Force the mouth closed, e.g. on interrupt or playback end. Best-effort.</summary>
    Task CloseMouthAsync(CancellationToken ct = default);

    /// <summary>Reserved: Realtime VAD listening pose. No-op in v0.1.</summary>
    void SetListening(bool userSpeaking);

    /// <summary>Show a sticker overlay by id (e.g. sweat_laugh).</summary>
    void ShowSticker(string stickerId);

    /// <summary>Idle / special body state such as sleep.</summary>
    void SetIdleState(string state);
}
