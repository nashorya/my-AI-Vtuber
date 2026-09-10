using System.Text.RegularExpressions;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot;

internal enum ReplyKind { Speak, InnerThought, Pass, Invalid }

internal readonly record struct ClassifiedReply(
    ReplyKind Kind,
    string Spoken,
    string Thought,
    IReadOnlyList<string> StagedControls)
{
    public static ClassifiedReply Invalid { get; } = new(ReplyKind.Invalid, "", "", []);
}

/// <summary>
/// Classifies a finished LLM reply before any public side effect.
/// Protocol (【PASS】 / （心里话）) is recognized on the raw body; existing
/// <see cref="LlmClient.StripActionText"/> must not run first because it deletes both marks.
/// </summary>
internal static class ReplyClassifier
{
    private static readonly Regex ControlTagRegex = new(
        @"\[(?:emotion|action|pose):[^\]\r\n]+\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpeakStageRegex = new(
        @"\*[^*\n]+\*|\([^)\n]+\)",
        RegexOptions.Compiled);

    public static ClassifiedReply Classify(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw))
            return ClassifiedReply.Invalid;

        var controls = new List<string>();
        foreach (Match m in ControlTagRegex.Matches(raw))
            controls.Add(m.Value);

        var body = ControlTagRegex.Replace(raw, "").Trim();
        if (body.Length == 0)
            return ClassifiedReply.Invalid;

        if (body == "【PASS】")
            return new ClassifiedReply(ReplyKind.Pass, "", "", controls);

        if (body.Contains("【PASS】", StringComparison.Ordinal))
            return ClassifiedReply.Invalid;

        if (body.StartsWith('（') && body.EndsWith('）'))
        {
            var inner = body[1..^1];
            if (inner.Length == 0 || inner.Contains('（') || inner.Contains('）'))
                return ClassifiedReply.Invalid;
            return new ClassifiedReply(ReplyKind.InnerThought, "", inner, controls);
        }

        if (body.Contains('（') || body.Contains('）'))
            return ClassifiedReply.Invalid;

        var spoken = SpeakStageRegex.Replace(body, "").Trim();
        spoken = LlmClient.StripPartialTags(spoken).Trim();
        if (!LlmClient.IsSpeakableText(spoken))
            return ClassifiedReply.Invalid;

        return new ClassifiedReply(ReplyKind.Speak, spoken, "", controls);
    }
}
