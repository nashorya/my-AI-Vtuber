namespace AIVTuber.Core.Avatar;

/// <summary>
/// Backend-agnostic avatar control surface shared by the in-process PNG renderer and VTS.
/// </summary>
public interface IAvatarController : IAsyncDisposable
{
    Task StartAsync(CancellationToken ct = default);

    /// <summary>Lip-sync RMS sample (typically ~30ms). Thread-safe; may be called from audio thread.</summary>
    void OnRms(float rms);

    /// <summary>Switch to an emotion face for <paramref name="hold"/> (default 1500ms).</summary>
    void SetEmotion(string emotion, TimeSpan? hold = null);

    /// <summary>Reserved: Realtime VAD listening pose. No-op in v0.1.</summary>
    void SetListening(bool userSpeaking);

    /// <summary>Show a sticker overlay by id (e.g. sweat_laugh).</summary>
    void ShowSticker(string stickerId);

    /// <summary>Idle / special body state such as sleep.</summary>
    void SetIdleState(string state);
}
