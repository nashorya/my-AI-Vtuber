using System.Text.RegularExpressions;

namespace AIVTuber.Core.Persona;

/// <summary>
/// Program-side check of a generated persona. The persona describes who the character is; when
/// to speak, silence, private thoughts, length and output format belong to the program. A persona
/// that carries such rules competes with the reply protocol and the model starts answering in the
/// persona's format instead (2026-10-02 session log).
/// </summary>
internal static class PersonaValidator
{
    private static readonly string[] Forbidden =
        ["PASS", "【", "】", "括号", "心里话", "字以内", "输出", "必须", "严禁"];

    private const string BehaviourPrefix = "行为方式：";
    private const int MaxBehaviours = 2;
    private const int MaxExamples = 2;

    // Lines the character says are quoted; only the persona's own wording is checked.
    private static readonly Regex Quoted = new("“[^”]*”|\"[^\"]*\"", RegexOptions.Compiled);

    /// <summary>Null when the persona is acceptable; otherwise the reason, phrased for a retry prompt.</summary>
    public static string? Validate(PersonaComposeResult result)
    {
        var persona = result.Persona ?? "";
        var name = result.AiNames.FirstOrDefault()?.Trim() ?? "";
        if (name.Length == 0 || !persona.Contains(name, StringComparison.Ordinal))
            return "人设里没有写出 AI 的名字";
        var title = result.StreamerTitle?.Trim() ?? "";
        if (title.Length > 0 && !persona.Contains(title, StringComparison.Ordinal))
            return $"人设里没有用「{title}」称呼主播";

        var unquoted = Quoted.Replace(persona, "");
        if (Forbidden.FirstOrDefault(w => unquoted.Contains(w, StringComparison.OrdinalIgnoreCase)) is { } word)
            return $"人设里出现了说话规则或格式要求（「{word}」），这些由程序负责，不要写";

        var lines = persona.Split('\n').Select(l => l.Trim()).ToArray();
        var behaviours = lines.Where(l => l.StartsWith(BehaviourPrefix, StringComparison.Ordinal))
            .SelectMany(l => l[BehaviourPrefix.Length..].Split('；', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries))
            .Count();
        if (behaviours > MaxBehaviours) return $"行为方式超过 {MaxBehaviours} 条";
        if (lines.Count(l => l.StartsWith("- ", StringComparison.Ordinal)) > MaxExamples)
            return $"例子超过 {MaxExamples} 个";
        return null;
    }
}
