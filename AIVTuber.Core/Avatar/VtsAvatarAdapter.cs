using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// Adapts the existing <see cref="VtsClient"/> path to <see cref="IAvatarController"/>.
/// Lip-sync → mouth parameter; emotion → hotkey via <see cref="VtsConfig.EmotionMap"/>.
/// Stickers / idle states are no-ops (VTS has no sticker channel).
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
        // Fire-and-forget; VtsClient serializes sends internally.
        _ = SetMouthSafeAsync(rms);
    }

    public void SetEmotion(string emotion, TimeSpan? hold = null)
    {
        _ = hold;
        if (Volatile.Read(ref _started) == 0) return;
        if (string.IsNullOrWhiteSpace(emotion)) return;

        if (!TryMapHotkey(_config.EmotionMap, emotion, out var hotkeyId))
        {
            DebugLog.Write($"[Avatar/VTS] unknown emotion hotkey mapping: {emotion}");
            return;
        }

        _ = TriggerHotkeySafeAsync(hotkeyId);
    }

    public void SetListening(bool userSpeaking) => _ = userSpeaking;

    public void ShowSticker(string stickerId)
        => DebugLog.Write($"[Avatar/VTS] ShowSticker('{stickerId}') ignored (VTS has no sticker channel)");

    public void SetIdleState(string state)
        => DebugLog.Write($"[Avatar/VTS] SetIdleState('{state}') ignored");

    private async Task SetMouthSafeAsync(float rms)
    {
        try
        {
            await _vts.SetMouthAsync(rms).ConfigureAwait(false);
            _rmsErrorLogged = false;
        }
        catch (Exception ex)
        {
            if (!_rmsErrorLogged)
            {
                _rmsErrorLogged = true;
                DebugLog.Write($"[Avatar/VTS] mouth inject failed: {ex.Message}");
            }
        }
    }

    private async Task TriggerHotkeySafeAsync(string hotkeyId)
    {
        try
        {
            await _vts.TriggerHotkeyAsync(hotkeyId).ConfigureAwait(false);
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[Avatar/VTS] hotkey failed: {ex.Message}");
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
