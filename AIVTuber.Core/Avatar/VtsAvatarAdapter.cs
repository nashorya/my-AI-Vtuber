using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// Adapts the <see cref="VtsClient"/> path to <see cref="IAvatarController"/>.
/// Lip-sync → mouth parameter (fire-safe, hot path); emotion/action → hotkey via
/// <see cref="VtsConfig.EmotionMap"/>/<see cref="VtsConfig.ActionMap"/>, throwing on unknown
/// names so the orchestrator's command queue reports them. Poses / stickers / idle states are
/// no-ops (VTS has no such channels).
/// </summary>
public sealed class VtsAvatarAdapter : IAvatarController
{
    private readonly VtsClient _vts;
    private readonly VtsConfig _config;
    private bool _rmsErrorLogged;
    private int _started;

    public VtsAvatarAdapter(VtsClient vts, VtsConfig config)
    {
        _vts = vts;
        _config = config;
    }

    public Task StartAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _started, 1);
        return Task.CompletedTask;
    }

    public void OnRms(float rms)
    {
        if (Volatile.Read(ref _started) == 0) return;
        // Fire-and-forget; VtsClient serializes sends internally. Must never throw at ~30ms.
        _ = SetMouthSafeAsync(rms);
    }

    public Task SetEmotionAsync(string emotion, TimeSpan? hold = null, CancellationToken ct = default)
    {
        _ = hold; // VTS hotkeys carry their own duration; hold is a pixel-backend concept.
        return TriggerMappedHotkeyAsync(_config.EmotionMap, emotion, "emotion", ct);
    }

    public Task TriggerActionAsync(string action, CancellationToken ct = default) =>
        TriggerMappedHotkeyAsync(_config.ActionMap, action, "action", ct);

    public void SetPose(string pose)
        => DebugLog.Write($"[Avatar/VTS] SetPose('{pose}') ignored (no VTS pose channel)");

    public Task CloseMouthAsync(CancellationToken ct = default) => _vts.CloseMouthAsync();

    public void SetListening(bool userSpeaking) => _ = userSpeaking;

    public void ShowSticker(string stickerId)
        => DebugLog.Write($"[Avatar/VTS] ShowSticker('{stickerId}') ignored (VTS has no sticker channel)");

    public void SetIdleState(string state)
        => DebugLog.Write($"[Avatar/VTS] SetIdleState('{state}') ignored");

    private Task TriggerMappedHotkeyAsync(
        IReadOnlyDictionary<string, string> map, string name, string kind, CancellationToken ct)
    {
        if (string.IsNullOrWhiteSpace(name)) return Task.CompletedTask;
        if (!TryMapHotkey(map, name, out var hotkeyId))
            throw new InvalidOperationException($"[VTS] unknown {kind}: {name}");
        return _vts.TriggerHotkeyAsync(hotkeyId, ct);
    }

    private async Task SetMouthSafeAsync(float rms)
    {
        try
        {
            await _vts.SetMouthAsync(rms).ConfigureAwait(false);
            _rmsErrorLogged = false;
        }
        catch (Exception ex)
        {
            // Log only the first failure per outage to avoid spamming the ~30ms RMS loop.
            if (!_rmsErrorLogged)
            {
                _rmsErrorLogged = true;
                DebugLog.Write($"[Avatar/VTS] mouth inject failed: {ex.Message}");
            }
        }
    }

    private static bool TryMapHotkey(
        IReadOnlyDictionary<string, string> map, string name, out string hotkeyId)
    {
        if (map.TryGetValue(name, out hotkeyId!) && !string.IsNullOrWhiteSpace(hotkeyId))
            return true;

        foreach (var pair in map)
        {
            if (pair.Key.Equals(name, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrWhiteSpace(pair.Value))
            {
                hotkeyId = pair.Value;
                return true;
            }
        }

        hotkeyId = string.Empty;
        return false;
    }

    public async ValueTask DisposeAsync()
    {
        try { await _vts.CloseMouthAsync().ConfigureAwait(false); }
        catch { /* ignore */ }
    }
}
