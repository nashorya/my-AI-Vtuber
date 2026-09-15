using System.Text.RegularExpressions;
using System.Text.Json;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot;

internal enum ReplyKind { Speak, InnerThought, Pass, Invalid }

internal readonly record struct ClassifiedReply(
    ReplyKind Kind,
    string Spoken,
    string Thought,
    IReadOnlyList<string> StagedControls,
    AIVTuber.Core.Avatar.AvatarIntent? AvatarIntent = null)
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
    private static readonly Regex PassOnlyRegex = new(
        @"\A(?:pass|\[\s*pass\s*\]|【\s*pass\s*】)[.!。！\s]*\z",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private static readonly Regex PassTagRegex = new(
        @"\[\s*pass\s*\]|【\s*pass\s*】",
        RegexOptions.Compiled | RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);

    public static ClassifiedReply ClassifyStructured(string? raw)
    {
        if (string.IsNullOrWhiteSpace(raw)) return ClassifiedReply.Invalid;
        var body = raw.Trim();
        if (body.StartsWith("```json", StringComparison.OrdinalIgnoreCase) && body.EndsWith("```"))
            body = body[7..^3].Trim();
        else if (body.StartsWith("```") && body.EndsWith("```") && body.Length >= 6)
            body = body[3..^3].Trim();

        // Backwards compatibility only for silence. Unstructured prose must never
        // bypass the explicit response decision, including malformed JSON fragments.
        var silent = Classify(body);
        if (silent.Kind is ReplyKind.Pass or ReplyKind.InnerThought)
            return new ClassifiedReply(ReplyKind.Pass, "", "", []);
        try
        {
            using var document = JsonDocument.Parse(body);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object) return ClassifiedReply.Invalid;
            var fields = root.EnumerateObject().ToArray();
            if (fields.Count(p => p.Name == "respond") != 1 ||
                fields.Count(p => p.Name is "speech" or "reply") != 1 ||
                !root.TryGetProperty("respond", out var respond) ||
                respond.ValueKind is not (JsonValueKind.True or JsonValueKind.False) ||
                !TryReadSpokenField(root, out var speech))
                return ClassifiedReply.Invalid;
            if (!respond.GetBoolean())
                return new ClassifiedReply(ReplyKind.Pass, "", "", []);
            var spoken = Classify(speech);
            if (spoken.Kind != ReplyKind.Speak) return spoken with { StagedControls = [] };
            // Do not read a nested/misplaced envelope as dialogue.
            if (spoken.Spoken.StartsWith('{') || spoken.Spoken.StartsWith('[') || spoken.Spoken.Contains("```"))
                return ClassifiedReply.Invalid;
            return spoken;
        }
        catch (JsonException) { return ClassifiedReply.Invalid; }
    }

    public static ClassifiedReply ClassifyTurn(string raw, AIVTuber.Core.Avatar.AvatarReplyPlan? avatarPlan, bool requireStructuredReply)
    {
        if (avatarPlan is not null && IsExtractedAvatarReply(avatarPlan))
            return Classify(avatarPlan.Reply) with { AvatarIntent = avatarPlan.Intent };
        if (!requireStructuredReply) return Classify(raw);
        var structured = ClassifyStructured(raw);
        if (structured.Kind != ReplyKind.Invalid) return structured;
        var fallback = Classify(raw);
        return fallback.Kind == ReplyKind.Speak && !LooksLikeJson(raw) ? fallback : structured;
    }

    private static bool LooksLikeJson(string raw)
    {
        var body = raw.Trim();
        if (body.StartsWith("```", StringComparison.Ordinal)) return true;
        return body.StartsWith('{') || body.StartsWith('[');
    }

    private static bool IsExtractedAvatarReply(AIVTuber.Core.Avatar.AvatarReplyPlan plan) =>
        plan.Diagnostic is null ||
        (!plan.Diagnostic.Contains("不是 JSON", StringComparison.Ordinal) &&
         !plan.Diagnostic.Contains("缺少", StringComparison.Ordinal));

    private static bool TryReadSpokenField(JsonElement root, out string? speech)
    {
        speech = null;
        if (root.TryGetProperty("speech", out var value) && value.ValueKind == JsonValueKind.String)
        {
            speech = value.GetString();
            return true;
        }
        if (root.TryGetProperty("reply", out value) && value.ValueKind == JsonValueKind.String)
        {
            speech = value.GetString();
            return true;
        }
        return false;
    }
    private static readonly Regex ControlTagRegex = new(
        @"\[(?:emotion|action|pose):[^\]\r\n]+\]",
        RegexOptions.Compiled | RegexOptions.IgnoreCase);

    private static readonly Regex SpeakStageRegex = new(
        @"\*[^*\n]+\*|\([^)\n]+\)",
        RegexOptions.Compiled);

    private static readonly Regex FullwidthThoughtRegex = new(
        @"（[^（）\n]+）",
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

        if (PassOnlyRegex.IsMatch(body))
            return new ClassifiedReply(ReplyKind.Pass, "", "", controls);

        if (PassTagRegex.IsMatch(body))
            return ClassifiedReply.Invalid;

        if (body.StartsWith('（') && body.EndsWith('）'))
        {
            var inner = body[1..^1];
            if (inner.Length > 0 && !inner.Contains('（') && !inner.Contains('）'))
                return new ClassifiedReply(ReplyKind.InnerThought, "", inner, controls);
        }

        var spokenBody = FullwidthThoughtRegex.Replace(body, "").Trim();
        if (spokenBody.Contains('（') || spokenBody.Contains('）'))
            return ClassifiedReply.Invalid;

        var spoken = SpeakStageRegex.Replace(spokenBody, "").Trim();
        spoken = LlmClient.StripPartialTags(spoken).Trim();
        if (PassOnlyRegex.IsMatch(spoken))
            return new ClassifiedReply(ReplyKind.Pass, "", "", []);
        if (!LlmClient.IsSpeakableText(spoken))
            return ClassifiedReply.Invalid;

        return new ClassifiedReply(ReplyKind.Speak, spoken, "", controls);
    }
}
