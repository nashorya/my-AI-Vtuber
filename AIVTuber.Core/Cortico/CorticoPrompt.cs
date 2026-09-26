namespace AIVTuber.Core.Cortico;

/// <summary>
/// Composes the Cortico part of the system prompt for the reply protocol actually parsed.
/// Both protocols keep the same Cortico script grammar inside the spoken text; only the envelope
/// differs, and it matches <see cref="CorticoReplyAdapter"/>:
/// legacy — the reply is the script itself, 【PASS】 alone to stay silent, one full-width
/// parenthesis note alone for a private thought; v2 — NDJSON decision/speech/end events whose
/// speech text carries the script, with no control lines (Cortico owns every VTS write).
/// </summary>
public static class CorticoPrompt
{
    public static string For(string? replyProtocol, string grammar) =>
        IsV2(replyProtocol) ? grammar + "\n" + V2Envelope : grammar + "\n" + LegacyEnvelope;

    internal static bool IsV2(string? replyProtocol) =>
        (replyProtocol ?? "").Trim().Equals("v2", StringComparison.OrdinalIgnoreCase);

    public const string LegacyEnvelope = """
        【Cortico 回复格式】
        不接话时只输出【PASS】；只在心里想时只输出一对全角括号包住的一句话，例如（先听他们说完）。
        回应时直接输出要朗读的台本，动作写成上面的台本标记，例如：<微笑>好呀【点头】那就这么定了。
        不要输出 JSON、代码块、Markdown、说明文字或思考过程；【PASS】和心里话不能和台词混在一起。
        """;

    public const string V2Envelope = """
        【Cortico 与输出协议 v2】
        仍按【输出协议 v2】逐行输出 decision / speech / end 事件。动作和表情写在 speech 的 text 里，
        用上面的台本标记，例如 {"v":2,"type":"speech","seq":0,"text":"<微笑>好呀【点头】"}。
        不要输出 control 行；不要把【PASS】、心里话或思考过程写进 speech。
        """;
}
