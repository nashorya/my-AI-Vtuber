using System.Runtime.CompilerServices;
using System.Text;
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
/// Adapts both reply protocols to Cortico script segments, incrementally: a segment is released
/// as soon as it is complete, never after waiting for the whole LLM reply.
/// What leaves here as Script never contains the protocol envelope, PASS, thoughts, legacy
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

    public static async IAsyncEnumerable<CorticoReplyEvent> FromV2(
        IAsyncEnumerable<ReplyStreamEvent> events, [EnumeratorCancellation] CancellationToken ct = default)
    {
        await foreach (var ev in events.WithCancellation(ct).ConfigureAwait(false))
        {
            switch (ev.Kind)
            {
                case ReplyStreamEventKind.Decision:
                    if (ev.Decision == ReplyDecisionMode.Pass) yield return new(CorticoReplyKind.Pass);
                    else if (ev.Decision == ReplyDecisionMode.Thought) yield return new(CorticoReplyKind.Thought, ev.Text);
                    break;
                case ReplyStreamEventKind.Speech:
                    // Same content isolation as the non-Cortico v2 pipeline: a segment that is only
                    // PASS or only a note is skipped (never performed); anything unsafe ends the turn.
                    var segment = Sanitize(ev.Text);
                    if (segment is { Kind: CorticoReplyKind.Script or CorticoReplyKind.Invalid } approved)
                    {
                        yield return approved;
                        if (approved.Kind == CorticoReplyKind.Invalid) yield break;
                    }
                    break;
                case ReplyStreamEventKind.Control:
                    // Cortico is the only VTS writer; actions arrive as script markup instead.
                    AIVTuber.Core.Diagnostics.DebugLog.Write($"[Cortico] 忽略 v2 control 行（{ev.ControlKind}）：动作应写在台本标记里");
                    break;
                case ReplyStreamEventKind.ProtocolError:
                    yield return CorticoReplyEvent.Invalid(ev.Error ?? "协议错误");
                    yield break;
                case ReplyStreamEventKind.End:
                    yield break;
            }
        }
    }

    public static async IAsyncEnumerable<CorticoReplyEvent> FromLegacy(
        IAsyncEnumerable<string> tokens, [EnumeratorCancellation] CancellationToken ct = default)
    {
        var reader = new LegacyScriptReader();
        await foreach (var token in tokens.WithCancellation(ct).ConfigureAwait(false))
        {
            foreach (var ev in reader.Feed(token))
            {
                yield return ev;
                if (ev.Kind == CorticoReplyKind.Invalid) yield break;
            }
        }
        foreach (var ev in reader.Complete())
            yield return ev;
    }
}

/// <summary>
/// Incremental reader for the legacy Cortico reply (plain script). The turn decision is taken
/// from the first characters: 【PASS】 alone is silence and a lone full-width note is a thought —
/// both only confirmed at the end of the reply, and never performed. Anything else is speech and
/// is released sentence by sentence (outside any bracket), so performing starts before the
/// model finishes.
/// </summary>
internal sealed class LegacyScriptReader
{
    private static readonly Regex PassBlockRegex = new(@"\A\s*(?:【\s*pass\s*】|\[\s*pass\s*\])", RegexOptions.IgnoreCase | RegexOptions.CultureInvariant);
    private const string SentenceEnds = "。！？!?…\n";
    private const string Trailers = "。！？!?…\"'”’」』）》)";

    private enum Mode { Undecided, PassPending, ThoughtPending, Speak, Done }

    private readonly StringBuilder _buffer = new();
    private Mode _mode = Mode.Undecided;
    private string _thought = "";

    public IEnumerable<CorticoReplyEvent> Feed(string token)
    {
        if (_mode == Mode.Done || string.IsNullOrEmpty(token)) yield break;
        _buffer.Append(token);
        foreach (var ev in Advance(final: false)) yield return ev;
    }

    public IEnumerable<CorticoReplyEvent> Complete()
    {
        if (_mode == Mode.Done) yield break;
        foreach (var ev in Advance(final: true)) yield return ev;
        _mode = Mode.Done;
    }

    private IEnumerable<CorticoReplyEvent> Advance(bool final)
    {
        if (_mode == Mode.Undecided)
        {
            var text = _buffer.ToString().TrimStart();
            if (text.Length == 0) { if (final) yield return CorticoReplyEvent.Invalid("空回复"); yield break; }
            var first = text[0];
            if (first is '{' or '`')
            {
                _mode = Mode.Done;
                yield return CorticoReplyEvent.Invalid("回复是 JSON/代码块，不是 Cortico 台本");
                yield break;
            }
            if (first is '【' or '[')
            {
                var close = text.IndexOf(first == '【' ? '】' : ']');
                if (close < 0) { if (!final) yield break; }
                else if (PassBlockRegex.IsMatch(text)) _mode = Mode.PassPending;
                else _mode = Mode.Speak;
                if (close < 0) _mode = Mode.Speak;
            }
            else if (first is '（')
            {
                var close = text.IndexOf('）');
                if (close < 0 && !final) yield break;
                if (close >= 0 && text[(close + 1)..].Trim().Length == 0)
                {
                    _mode = Mode.ThoughtPending;
                    _thought = text[1..close];
                }
                else _mode = Mode.Speak;
            }
            else _mode = Mode.Speak;
        }

        if (_mode == Mode.PassPending)
        {
            if (!final) yield break;
            var rest = PassBlockRegex.Replace(_buffer.ToString(), "").Trim().Trim('。', '.', '！', '!');
            _mode = Mode.Done;
            yield return rest.Length == 0
                ? new CorticoReplyEvent(CorticoReplyKind.Pass)
                : CorticoReplyEvent.Invalid("【PASS】和台词混在一起");
            yield break;
        }

        if (_mode == Mode.ThoughtPending)
        {
            var text = _buffer.ToString().Trim();
            var close = text.IndexOf('）');
            if (text[(close + 1)..].Trim().Length == 0)
            {
                if (!final) yield break;
                _mode = Mode.Done;
                yield return new CorticoReplyEvent(CorticoReplyKind.Thought, _thought);
                yield break;
            }
            _mode = Mode.Speak; // a leading note followed by speech: the note is stripped below
        }

        if (_mode != Mode.Speak) yield break;
        while (TakeSentence(final) is { } sentence)
        {
            if (CorticoReplyAdapter.Sanitize(sentence) is not { } ev) continue;
            if (ev.Kind == CorticoReplyKind.Thought) continue; // an inline note between sentences
            if (ev.Kind == CorticoReplyKind.Pass)
                ev = CorticoReplyEvent.Invalid("【PASS】和台词混在一起"); // same verdict as the whole-reply classifier
            yield return ev;
            if (ev.Kind == CorticoReplyKind.Invalid) { _mode = Mode.Done; yield break; }
        }
    }

    /// <summary>Removes and returns the next complete sentence (bracket-aware), or the remainder at the end.</summary>
    private string? TakeSentence(bool final)
    {
        var depth = 0;
        for (var i = 0; i < _buffer.Length; i++)
        {
            var c = _buffer[i];
            if (c is '【' or '（' or '(' or '<' or '＜' or '[') depth++;
            else if (c is '】' or '）' or ')' or '>' or '＞' or ']') depth = Math.Max(0, depth - 1);
            else if (depth == 0 && SentenceEnds.Contains(c))
            {
                var end = i + 1;
                while (end < _buffer.Length && Trailers.Contains(_buffer[end])) end++;
                // Released at once; a closing mark that arrives later is a punctuation-only
                // fragment and is dropped by Sanitize.
                var sentence = _buffer.ToString(0, end);
                _buffer.Remove(0, end);
                return sentence;
            }
        }
        if (!final || _buffer.Length == 0) return null;
        var rest = _buffer.ToString();
        _buffer.Clear();
        return rest;
    }
}
