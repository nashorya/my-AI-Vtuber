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
        每段 speech 都要带标记。表格里有「表情」行时，每轮第一段开头必须先写一个表情词表明你此刻的情绪，
        情绪变化时在那一段开头换一个；其余每段至少写一个动作、姿态或看向。
        表情和动作、姿态是两回事，动作姿态不能代替表情，可以用逗号和表情组合在同一个标记里。
        不要输出 control 行；不要把【PASS】、心里话或思考过程写进 speech。
        """;
}
