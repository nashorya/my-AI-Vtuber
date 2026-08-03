using System.Text;

namespace AIVTuber.Core.Pipeline;

internal sealed class StreamingControlTagParser
{
    private const int MaxTagLength = 256;
    private const string ActionPrefix = "[action:";
    private const string EmotionPrefix = "[emotion:";
    private const string PosePrefix = "[pose:";
    private const string ActionStem = "[action";
    private const string EmotionStem = "[emotion";
    private const string PoseStem = "[pose";

    private readonly Action<string> _onAction;
    private readonly Action<string> _onEmotion;
    private readonly Action<string> _onPose;
    private readonly HashSet<string> _actions = new(StringComparer.OrdinalIgnoreCase);
    private readonly HashSet<string> _poses = new(StringComparer.OrdinalIgnoreCase);
    private readonly StringBuilder _candidate = new();
    private bool _discardingMalformedTag;

    public StreamingControlTagParser(
        Action<string> onAction,
        Action<string> onEmotion,
        Action<string>? onPose = null)
    {
        _onAction = onAction;
        _onEmotion = onEmotion;
        _onPose = onPose ?? (_ => { });
    }

    public string Consume(string chunk)
    {
        if (string.IsNullOrEmpty(chunk)) return string.Empty;

        var output = new StringBuilder(chunk.Length);
        foreach (var character in chunk)
            Consume(character, output);
        return output.ToString();
    }

    public string Complete()
    {
        if (_discardingMalformedTag || IsControlTagCandidate(_candidate))
        {
            ResetCandidate();
            return string.Empty;
        }

        var remaining = _candidate.ToString();
        ResetCandidate();
        return remaining;
    }

    private void Consume(char character, StringBuilder output)
    {
        if (_discardingMalformedTag)
        {
            if (character is ']' or '\r' or '\n')
            {
                ResetCandidate();
                if (character is '\r' or '\n') output.Append(character);
            }
            return;
        }

        if (_candidate.Length == 0)
        {
            if (character == '[') _candidate.Append(character);
            else output.Append(character);
            return;
        }

        _candidate.Append(character);
        if (IsPotentialPrefix(_candidate)) return;

        var candidate = _candidate.ToString();
        var prefix = GetCompletedPrefix(candidate);
        if (prefix is null)
        {
            if (StartsWithControlStem(candidate))
            {
                _candidate.Clear();
                _discardingMalformedTag = character is not (']' or '\r' or '\n');
                if (character is '\r' or '\n') output.Append(character);
                return;
            }
            output.Append(candidate);
            ResetCandidate();
            return;
        }

        if (character == ']')
        {
            Emit(prefix, candidate[prefix.Length..^1].Trim());
            ResetCandidate();
        }
        else if (character is '\r' or '\n')
        {
            ResetCandidate();
            output.Append(character);
        }
        else if (_candidate.Length > MaxTagLength)
        {
            _candidate.Clear();
            _discardingMalformedTag = true;
        }
    }

    private void Emit(string prefix, string value)
    {
        if (value.Length == 0) return;
        if (prefix == ActionPrefix)
        {
            if (_actions.Add(value)) _onAction(value);
            return;
        }

        if (prefix == PosePrefix)
        {
            if (_poses.Add(value)) _onPose(value);
            return;
        }

        _onEmotion(value);
    }

    private static bool IsPotentialPrefix(StringBuilder candidate)
    {
        var value = candidate.ToString();
        return ActionPrefix.StartsWith(value, StringComparison.Ordinal) ||
               EmotionPrefix.StartsWith(value, StringComparison.Ordinal) ||
               PosePrefix.StartsWith(value, StringComparison.Ordinal);
    }

    private static bool IsControlTagCandidate(StringBuilder candidate)
    {
        if (candidate.Length == 0) return false;
        var value = candidate.ToString();
        return StartsWithControlStem(value) || GetCompletedPrefix(value) is not null;
    }

    private static string? GetCompletedPrefix(string candidate)
    {
        if (candidate.StartsWith(ActionPrefix, StringComparison.Ordinal)) return ActionPrefix;
        if (candidate.StartsWith(EmotionPrefix, StringComparison.Ordinal)) return EmotionPrefix;
        if (candidate.StartsWith(PosePrefix, StringComparison.Ordinal)) return PosePrefix;
        return null;
    }

    private static bool StartsWithControlStem(string candidate) =>
        candidate.StartsWith(ActionStem, StringComparison.Ordinal) ||
        candidate.StartsWith(EmotionStem, StringComparison.Ordinal) ||
        candidate.StartsWith(PoseStem, StringComparison.Ordinal);

    private void ResetCandidate()
    {
        _candidate.Clear();
        _discardingMalformedTag = false;
    }
}
