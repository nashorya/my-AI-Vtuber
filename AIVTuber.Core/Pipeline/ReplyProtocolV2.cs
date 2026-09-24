using System.Text;
using System.Text.Json;
using AIVTuber.Core.Avatar;

namespace AIVTuber.Core.Pipeline;

/// <summary>Event kinds of the versioned NDJSON reply protocol (RT-05 / plan section 4.3).</summary>
public enum ReplyStreamEventKind
{
    /// <summary>{"v":2,"type":"decision","mode":"speak"|"pass"|"thought"} — must be the first event.</summary>
    Decision,
    /// <summary>{"v":2,"type":"speech","seq":N,"text":"…"} — only valid after decision=speak.</summary>
    Speech,
    /// <summary>Whitelisted control event (avatar targets / emotion). Never blocks speech release.</summary>
    Control,
    /// <summary>{"v":2,"type":"end"} — must be the last event.</summary>
    End,
    /// <summary>Fail-closed terminal event. The stream must be dropped after this; already
    /// submitted segments keep playing (played audio cannot be recalled) but nothing new
    /// may be released to TTS, subtitles or motion.</summary>
    ProtocolError,
}

/// <summary>The turn-level response decision. "thought" is an application-level private
/// character note, not model chain-of-thought.</summary>
public enum ReplyDecisionMode { Speak, Pass, Thought }

/// <summary>One validated protocol event. Structural validation (JSON shape, ordering,
/// seq, size caps, channel whitelist) happens in the parser; content isolation
/// (parenthesis thoughts, control tags, speakability) is applied by the consumer.</summary>
public sealed record ReplyStreamEvent(
    ReplyStreamEventKind Kind,
    ReplyDecisionMode Decision = ReplyDecisionMode.Speak,
    int Seq = -1,
    string Text = "",
    AvatarIntent? Motion = null,
    string? ControlKind = null,
    string? Error = null)
{
    public static ReplyStreamEvent DecisionEvent(ReplyDecisionMode mode, string thought = "") =>
        new(ReplyStreamEventKind.Decision, mode, Text: thought);
    public static ReplyStreamEvent SpeechEvent(int seq, string text) =>
        new(ReplyStreamEventKind.Speech, Seq: seq, Text: text);
    public static ReplyStreamEvent MotionEvent(AvatarIntent intent) =>
        new(ReplyStreamEventKind.Control, Motion: intent, ControlKind: "avatar");
    public static ReplyStreamEvent EmotionEvent(string value) =>
        new(ReplyStreamEventKind.Control, Text: value, ControlKind: "emotion");
    public static ReplyStreamEvent EndEvent() => new(ReplyStreamEventKind.End);
    public static ReplyStreamEvent ErrorEvent(string reason) =>
        new(ReplyStreamEventKind.ProtocolError, Error: reason);
}

/// <summary>
/// Incremental NDJSON parser for reply protocol v2. Feed raw model text chunks as they
/// arrive; complete lines are validated independently (no half-JSON decoding in v1 of
/// this parser — the first speakable segment is released at line granularity, which is
/// enough to pipeline ahead of EOF). Every violation fails closed exactly once.
/// </summary>
public sealed class ReplyProtocolV2Parser
{
    public const int MaxSegmentChars = 300;
    public const int MaxSegments = 8;
    public const int MaxThoughtChars = 200;
    public const int MaxEmotionChars = 50;

    private readonly StringBuilder _line = new();
    private readonly HashSet<string> _allowedChannels;
    private ReplyDecisionMode? _decision;
    private bool _ended;
    private bool _failed;
    private int _nextSeq;

    public ReplyProtocolV2Parser(IEnumerable<string>? allowedChannels)
        => _allowedChannels = [.. (allowedChannels ?? [])];

    public bool Failed => _failed;
    public bool Ended => _ended;

    /// <summary>Feed a raw content chunk; returns zero or more fully validated events.</summary>
    public IReadOnlyList<ReplyStreamEvent> Feed(string chunk)
    {
        var events = new List<ReplyStreamEvent>();
        if (_failed || string.IsNullOrEmpty(chunk)) return events;
        foreach (var c in chunk)
        {
            if (c == '\n')
            {
                var line = _line.ToString();
                _line.Clear();
                if (line.EndsWith('\r')) line = line[..^1];
                events.AddRange(ProcessLine(line));
            }
            else
            {
                _line.Append(c);
                if (_line.Length > 65536)
                {
                    _failed = true;
                    events.Add(ReplyStreamEvent.ErrorEvent("协议错误：单行超过长度上限"));
                    return events;
                }
            }
        }
        return events;
    }

    /// <summary>Close the stream. Missing end, mid-line truncation and finish_reason=length
    /// all fail closed — a truncated protocol must never be treated as complete.</summary>
    public IReadOnlyList<ReplyStreamEvent> Complete(string? finishReason)
    {
        if (_failed) return [];
        var leftover = _line.ToString().Trim();
        _line.Clear();
        if (leftover.Length > 0)
            return Fail($"协议错误：流在行中间被截断（末行不完整：{Preview(leftover)}）");
        if (!_ended)
        {
            if (string.Equals(finishReason, "length", StringComparison.OrdinalIgnoreCase))
                return Fail("协议错误：finish_reason=length，输出预算把协议截断（无 end 事件）");
            return Fail("协议错误：流结束但缺少 end 事件");
        }
        return [];
    }

    private static string Preview(string text) => text.Length <= 60 ? text : text[..60] + "…";

    private IReadOnlyList<ReplyStreamEvent> Fail(string reason)
    {
        _failed = true;
        return [ReplyStreamEvent.ErrorEvent(reason)];
    }

    private IReadOnlyList<ReplyStreamEvent> ProcessLine(string line)
    {
        if (_failed) return [];
        if (_ended)
            return line.Trim().Length == 0
                ? []
                : Fail("协议错误：end 之后仍有额外输出");
        if (line.Trim().Length == 0) return [];

        try
        {
            using var document = JsonDocument.Parse(line);
            return ProcessEvent(document.RootElement);
        }
        catch (JsonException) { return Fail($"协议错误：某行不是完整 JSON（{Preview(line)}）"); }
        catch (InvalidOperationException) { return Fail($"协议错误：事件字段类型不正确（{Preview(line)}）"); }
    }

    private IReadOnlyList<ReplyStreamEvent> ProcessEvent(JsonElement root)
    {
        if (_failed) return [];
        if (root.ValueKind != JsonValueKind.Object)
            return Fail("协议错误：事件不是 JSON 对象");
        if (!root.TryGetProperty("v", out var version) || version.ValueKind != JsonValueKind.Number || version.GetInt32() != 2)
            return Fail("协议错误：缺少 v:2 版本字段");
        if (!root.TryGetProperty("type", out var type) || type.ValueKind != JsonValueKind.String)
            return Fail("协议错误：缺少 type 字段");

        switch (type.GetString())
        {
            case "decision":
                if (_decision.HasValue) return Fail("协议错误：重复 decision");
                if (!root.TryGetProperty("mode", out var mode) || mode.ValueKind != JsonValueKind.String)
                    return Fail("协议错误：decision 缺少 mode");
                var text = ReadOptionalString(root, "text");
                if (text is { Length: > MaxThoughtChars }) return Fail("协议错误：thought 备注过长");
                return mode.GetString() switch
                {
                    "speak" => SetDecision(ReplyDecisionMode.Speak, ""),
                    "pass" => SetDecision(ReplyDecisionMode.Pass, ""),
                    "thought" => SetDecision(ReplyDecisionMode.Thought, text ?? ""),
                    _ => Fail("协议错误：未知 decision mode"),
                };

            case "speech":
                if (_decision != ReplyDecisionMode.Speak)
                    return Fail("协议错误：speech 只能出现在 decision=speak 之后");
                if (!root.TryGetProperty("seq", out var seq) || seq.ValueKind != JsonValueKind.Number)
                    return Fail("协议错误：speech 缺少 seq");
                if (seq.GetInt32() != _nextSeq)
                    return Fail($"协议错误：speech seq 不连续（期望 {_nextSeq}，实际 {seq.GetInt32()}）");
                if (!root.TryGetProperty("text", out var textProp) || textProp.ValueKind != JsonValueKind.String)
                    return Fail("协议错误：speech 缺少 text 字符串");
                var body = textProp.GetString() ?? "";
                if (body.Length > MaxSegmentChars) return Fail("协议错误：speech 段超过长度上限");
                _nextSeq++;
                if (_nextSeq > MaxSegments) return Fail("协议错误：speech 段数超过上限");
                return [ReplyStreamEvent.SpeechEvent(seq.GetInt32(), body)];

            case "control":
                if (!_decision.HasValue) return Fail("协议错误：control 出现在 decision 之前");
                if (!root.TryGetProperty("kind", out var kind) || kind.ValueKind != JsonValueKind.String)
                    return Fail("协议错误：control 缺少 kind");
                switch (kind.GetString())
                {
                    case "emotion":
                        var value = ReadOptionalString(root, "value");
                        if (string.IsNullOrWhiteSpace(value)) return Fail("协议错误：emotion 缺少 value");
                        if (value.Length > MaxEmotionChars) return Fail("协议错误：emotion 名称过长");
                        return [ReplyStreamEvent.EmotionEvent(value.Trim())];
                    case "avatar":
                        if (!root.TryGetProperty("targets", out _))
                            return Fail("协议错误：avatar 控制缺少 targets 对象");
                        // The control line itself is the avatar payload (targets + optional
                        // transitionMs/holdMs); extra protocol fields are ignored by the validator.
                        var intent = AvatarReplyProtocol.TryParseAvatarPayload(root, _allowedChannels, out var error);
                        return intent is null
                            ? Fail("协议错误：avatar 控制被拒绝——" + error)
                            : [ReplyStreamEvent.MotionEvent(intent)];
                    default:
                        return Fail("协议错误：未知 control kind（白名单外）");
                }

            case "end":
                if (!_decision.HasValue) return Fail("协议错误：end 之前缺少 decision");
                _ended = true;
                return [ReplyStreamEvent.EndEvent()];

            default:
                return Fail("协议错误：未知事件类型");
        }
    }

    private IReadOnlyList<ReplyStreamEvent> SetDecision(ReplyDecisionMode mode, string thought)
    {
        _decision = mode;
        return [ReplyStreamEvent.DecisionEvent(mode, thought)];
    }

    private static string? ReadOptionalString(JsonElement root, string name)
        => root.TryGetProperty(name, out var v) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;
}

/// <summary>Prompt fragments for protocol v2. The invitation semantics mirror
/// <see cref="AIVTuber.Core.Bot.IdentityPrompt.InvitationPolicy"/>; only the output
/// format section changes (NDJSON events instead of one JSON object).</summary>
public static class ReplyProtocolV2
{
    public static string Prompt(IEnumerable<string> allowedChannels)
    {
        var allowed = new HashSet<string>(allowedChannels, StringComparer.Ordinal);
        var channels = AvatarChannels.All.Where(c => c.AiControlled && allowed.Contains(c.Name)).ToArray();
        var channelList = channels.Length == 0
            ? "当前没有可用动作通道，省略 avatar 控制行。"
            : "可用通道（0 中立，0.4~0.6 明显，±1 极限）：" + string.Join("；", channels.Select(
                c => $"{c.Name}（{c.Label}）{(c.Unipolar ? "0 到 1" : "-1~1")}"));
        return """

            【输出协议 v2——逐行 NDJSON，每行一个完整 JSON 对象，不要代码块、不要 Markdown】
            第一行必须是 decision：{"v":2,"type":"decision","mode":"speak"}；本轮不想出声用 "mode":"pass"；只在心里想用 "mode":"thought"（可附 "text":"一句私有备注"）。
            mode=speak 时，之后每行输出一段完整可朗读的话：{"v":2,"type":"speech","seq":0,"text":"……"}，seq 从 0 连续递增。
            每段是独立、语义完整、立刻可朗读的短句；第一段尽量简短，全轮合计不超过80字，一段顶十段别啰嗦。
            需要表情：{"v":2,"type":"control","kind":"emotion","value":"happy"}。
            需要动作：{"v":2,"type":"control","kind":"avatar","targets":{"headYaw":0.5}}。
            """ + channelList + """

            控制行不占 seq，不要插在 speech 之前太早；最后一行必须是 {"v":2,"type":"end"}。
            pass 或 thought 之后不得再输出 speech；speak 之后不得改回 pass。
            speech 的 text 里不要出现 JSON、括号心里话、思考过程或控制标记。
            """;
    }
}
