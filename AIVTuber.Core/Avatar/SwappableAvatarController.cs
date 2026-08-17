namespace AIVTuber.Core.Avatar;

/// <summary>
/// Stable facade the orchestrator holds for its whole lifetime while <c>BotRuntime</c> swaps
/// the inner backend underneath it: the pixel driver is created after the first orchestrator
/// (startup order), and VTS reconnects can replace the adapter without an orchestrator rebuild.
/// All members no-op while no inner controller is attached.
/// </summary>
public sealed class SwappableAvatarController : IAvatarController
{
    private volatile IAvatarController? _inner;

    /// <summary>Attach a new inner controller (or null to detach) and return the previous one
    /// so the owner can dispose it.</summary>
    public IAvatarController? Swap(IAvatarController? inner) =>
        Interlocked.Exchange(ref _inner, inner);

    public IAvatarController? Inner => _inner;

    public Task StartAsync(CancellationToken ct = default) =>
        _inner?.StartAsync(ct) ?? Task.CompletedTask;

    public void OnRms(float rms) => _inner?.OnRms(rms);

    public Task SetEmotionAsync(string emotion, TimeSpan? hold = null, CancellationToken ct = default) =>
        _inner?.SetEmotionAsync(emotion, hold, ct) ?? Task.CompletedTask;

    public Task TriggerActionAsync(string action, CancellationToken ct = default) =>
        _inner?.TriggerActionAsync(action, ct) ?? Task.CompletedTask;

    public void SetPose(string pose) => _inner?.SetPose(pose);

    public Task CloseMouthAsync(CancellationToken ct = default) =>
        _inner?.CloseMouthAsync(ct) ?? Task.CompletedTask;

    public void SetListening(bool userSpeaking) => _inner?.SetListening(userSpeaking);

    public void ShowSticker(string stickerId) => _inner?.ShowSticker(stickerId);

    public void SetIdleState(string state) => _inner?.SetIdleState(state);

    public ValueTask DisposeAsync() =>
        Interlocked.Exchange(ref _inner, null)?.DisposeAsync() ?? ValueTask.CompletedTask;
}
