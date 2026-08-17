namespace AIVTuber.Core.Avatar;

/// <summary>Fans out avatar commands to multiple backends (e.g. pixel + VTS). Async intents
/// run backends in constructor order; a throwing backend stops later ones, so pass the
/// never-throwing backend (pixel) first.</summary>
public sealed class CompositeAvatarController : IAvatarController
{
    private readonly IAvatarController[] _backends;

    public CompositeAvatarController(params IAvatarController[] backends)
    {
        _backends = backends ?? throw new ArgumentNullException(nameof(backends));
    }

    public IReadOnlyList<IAvatarController> Backends => _backends;

    public async Task StartAsync(CancellationToken ct = default)
    {
        foreach (var b in _backends)
            await b.StartAsync(ct).ConfigureAwait(false);
    }

    public void OnRms(float rms)
    {
        foreach (var b in _backends) b.OnRms(rms);
    }

    public async Task SetEmotionAsync(string emotion, TimeSpan? hold = null, CancellationToken ct = default)
    {
        foreach (var b in _backends)
            await b.SetEmotionAsync(emotion, hold, ct).ConfigureAwait(false);
    }

    public async Task TriggerActionAsync(string action, CancellationToken ct = default)
    {
        foreach (var b in _backends)
            await b.TriggerActionAsync(action, ct).ConfigureAwait(false);
    }

    public void SetPose(string pose)
    {
        foreach (var b in _backends) b.SetPose(pose);
    }

    public async Task CloseMouthAsync(CancellationToken ct = default)
    {
        foreach (var b in _backends)
            await b.CloseMouthAsync(ct).ConfigureAwait(false);
    }

    public void SetListening(bool userSpeaking)
    {
        foreach (var b in _backends) b.SetListening(userSpeaking);
    }

    public void ShowSticker(string stickerId)
    {
        foreach (var b in _backends) b.ShowSticker(stickerId);
    }

    public void SetIdleState(string state)
    {
        foreach (var b in _backends) b.SetIdleState(state);
    }

    public async ValueTask DisposeAsync()
    {
        foreach (var b in _backends)
            await b.DisposeAsync().ConfigureAwait(false);
    }
}
