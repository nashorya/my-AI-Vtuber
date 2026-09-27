namespace AIVTuber.Core.Cortico;

/// <summary>
/// Composes the Cortico part of the system prompt: the script grammar inside protocol v2 speech
/// text. Replies are NDJSON decision/speech/end events whose speech text carries the script,
/// with no control lines (Cortico owns every rig write). Matches <see cref="CorticoReplyAdapter"/>.
/// </summary>
public static class CorticoPrompt
{
    public static string For(string grammar) => grammar + "\n" + V2Envelope;

    public const string V2Envelope = """
        【Cortico 与输出协议 v2】
        仍按【输出协议 v2】逐行输出 decision / speech / end 事件。动作和表情写在 speech 的 text 里，
        用上面的台本标记，例如 {"v":2,"type":"speech","seq":0,"text":"<微笑>好呀【点头】"}。
        不要输出 control 行；不要把【PASS】、心里话或思考过程写进 speech。
        """;
}
