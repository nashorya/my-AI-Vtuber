using System.Text.RegularExpressions;
using AIVTuber.Core.Bot;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Cortico;

internal enum CorticoReplyKind { Script, Pass, Thought, Invalid }

/// <summary>One adapted reply event. <see cref="Text"/> is an approved Cortico script segment
/// (Script), the private note (Thought) or the reason (Invalid).</summary>
internal readonly record struct CorticoReplyEvent(CorticoReplyKind Kind, string Text = "")
{
    public static CorticoReplyEvent Script(string script) => new(CorticoReplyKind.Script, script);
    public static CorticoReplyEvent Invalid(string reason) => new(CorticoReplyKind.Invalid, reason);
}

/// <summary>
/// Content isolation for Cortico: turns one approved v2 speech segment into a Cortico script
/// segment. What leaves here as Script never contains the protocol envelope, PASS, thoughts, legacy
/// [emotion:]/[action:] tags or TTS bracket tags — only speech plus Cortico action markup.
/// </summary>
internal static class CorticoReplyAdapter
{
    private static readonly Regex SquareTagRegex = new(@"\[[^\]\r\n]{0,32}\]", RegexOptions.Compiled);

    /// <summary>Content isolation for one segment: Script for performable text, Pass/Thought for a
    /// segment that is only 【PASS】 or only a private note (never performed; the caller decides
    /// whether that is allowed there), Invalid for anything else that must not be performed, and
    /// null for an empty or punctuation-only fragment.</summary>
    internal static CorticoReplyEvent? Sanitize(string text)
    {
        var body = text.Trim();
        if (body.Length == 0) return null;
        if (!body.Any(char.IsLetterOrDigit)) return null; // e.g. a closing quote after a sentence
        if (body.StartsWith('{') || body.StartsWith("```", StringComparison.Ordinal) ||
            body.Contains("\"type\"", StringComparison.Ordinal) || body.Contains("\"respond\"", StringComparison.Ordinal))
            return CorticoReplyEvent.Invalid("回复里出现协议包装（JSON），不能朗读");
        var classified = ReplyClassifier.Classify(body);
        switch (classified.Kind)
        {
            case ReplyKind.Pass:
                return new CorticoReplyEvent(CorticoReplyKind.Pass);
            case ReplyKind.InnerThought:
                return new CorticoReplyEvent(CorticoReplyKind.Thought, classified.Thought);
            case ReplyKind.Invalid:
                return CorticoReplyEvent.Invalid("台词未通过内容隔离（PASS/括号/标记混在台词里或不完整）");
        }
        // Legacy control tags were already split off by the classifier (Cortico owns motion;
        // they are dropped, never spoken). TTS voice tags and unknown square tags are removed too.
        var script = SquareTagRegex.Replace(classified.Spoken, "").Trim();
        return script.Length == 0 ? null : CorticoReplyEvent.Script(script);
    }
}
