namespace AIVTuber.Core.Avatar;

/// <summary>Fans out avatar commands to multiple backends (e.g. VTS + pixel).</summary>
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

    public void SetEmotion(string emotion, TimeSpan? hold = null)
    {
        foreach (var b in _backends) b.SetEmotion(emotion, hold);
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
