namespace AIVTuber.Core.Bot;

/// <summary>
/// PK-mode speech gate: stay silent unless the utterance contains a wake keyword,
/// or a recent wake is still within the hold window.
/// </summary>
public sealed class WakeGate
{
    private long _awakeUntilMs;

    /// <summary>
    /// Returns whether LLM/TTS should run for <paramref name="text"/>.
    /// <paramref name="nowMs"/> should be monotonic (e.g. <see cref="Environment.TickCount64"/>).
    /// </summary>
    public bool ShouldSpeak(bool isPkMode, IReadOnlyList<string> keywords, double wakeHoldSec, string text, long nowMs)
    {
        if (!isPkMode) return true;

        if (ContainsWakeKeyword(keywords, text))
        {
            var holdMs = (long)(Math.Max(0, wakeHoldSec) * 1000.0);
            _awakeUntilMs = nowMs + holdMs;
            return true;
        }

        return nowMs < _awakeUntilMs;
    }

    public void Reset() => _awakeUntilMs = 0;

    public static bool ContainsWakeKeyword(IReadOnlyList<string> keywords, string text)
    {
        if (string.IsNullOrWhiteSpace(text) || keywords is null || keywords.Count == 0)
            return false;

        foreach (var raw in keywords)
        {
            var kw = raw?.Trim();
            if (string.IsNullOrEmpty(kw)) continue;
            if (text.Contains(kw, StringComparison.OrdinalIgnoreCase))
                return true;
        }

        return false;
    }
}
