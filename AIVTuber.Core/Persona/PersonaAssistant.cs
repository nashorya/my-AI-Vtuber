using System.Text;
using System.Text.Json;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Persona;

/// <summary>
/// Turns a streamer's few words about their AI into a short persona, asking only for what is
/// missing. Which questions are asked is decided here, not by the model: the model reports what
/// the description already says and suggests answers.
/// </summary>
internal sealed class PersonaAssistant(Func<ILlmClient> createLlm)
{
    public const int MaxDescriptionChars = 2000;
    private const string FallbackTitle = "主播";
    private const string GiveUp = "没生成好，请改改描述再试";

    private static readonly string[] DefaultNames = ["小鱼", "团子"];
    private static readonly string[] DefaultTitles = ["主播", "老大"];
    private static readonly string[] DefaultStyles = ["温柔", "爱吐槽", "元气"];
    private static readonly JsonSerializerOptions Json = new() { PropertyNameCaseInsensitive = true };

    public async Task<PersonaCheckResult> CheckAsync(PersonaDraftInput input, CancellationToken ct)
    {
        EnsureLength(input.Description);
        var reply = await AskJsonAsync<CheckReply>(PersonaPrompts.Check, DescriptionBlock(input.Description),
            _ => null, ct).ConfigureAwait(false);
        var foundName = Blank(reply.AiName);
        var foundTitle = Blank(reply.StreamerTitle);
        var questions = new List<PersonaQuestion>();
        if (input.ExistingAiNames.Count == 0 && foundName is null)
            questions.Add(new("aiName", "它叫什么名字？", Suggest(reply.Suggestions?.AiName, DefaultNames)));
        if (string.IsNullOrWhiteSpace(input.ExistingStreamerTitle) && foundTitle is null)
            questions.Add(new("streamerTitle", "它怎么称呼你？", Suggest(reply.Suggestions?.StreamerTitle, DefaultTitles)));
        if (reply.NeedsStyle)
            questions.Add(new("style", "它说话更像哪种？", Suggest(reply.Suggestions?.Style, DefaultStyles)));
        return new PersonaCheckResult(questions, foundName, foundTitle);
    }

    public async Task<PersonaComposeResult> ComposeAsync(
        PersonaDraftInput input, PersonaCheckResult check, IReadOnlyList<PersonaAnswer> answers, CancellationToken ct)
    {
        EnsureLength(input.Description);
        string? Answer(string id) => Blank(answers.FirstOrDefault(a => a.QuestionId == id)?.Text);
        var name = Answer("aiName") ?? input.ExistingAiNames.Select(Blank).FirstOrDefault(n => n is not null) ?? check.FoundAiName;
        var title = Answer("streamerTitle") ?? Blank(input.ExistingStreamerTitle) ?? check.FoundStreamerTitle ?? FallbackTitle;
        var style = Answer("style");

        var prompt = new StringBuilder(DescriptionBlock(input.Description)).AppendLine()
            .AppendLine($"名字：{name ?? "由你按描述取一个"}")
            .AppendLine($"称呼主播：{title}");
        if (style is not null) prompt.AppendLine($"说话风格：{style}");

        PersonaComposeResult? Shape(ComposeReply r)
        {
            var names = new List<string>();
            if (name is not null) names.Add(name);
            foreach (var alias in r.AiNames ?? [])
                names.AddRange((alias ?? "").Split([',', '，'], StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries));
            names = names.Distinct(StringComparer.Ordinal).ToList();
            var supplemented = (r.Supplemented ?? []).Where(s => !string.IsNullOrWhiteSpace(s)).ToList();
            if (name is null && names.Count > 0) supplemented.Add(names[0]); // the generator picked the name
            return new PersonaComposeResult((r.Persona ?? "").Trim(), names, title, supplemented);
        }

        var result = await AskJsonAsync<ComposeReply>(PersonaPrompts.Compose, prompt.ToString().TrimEnd(),
            r => PersonaValidator.Validate(Shape(r)!), ct).ConfigureAwait(false);
        return Shape(result)!;
    }

    /// <summary>One request, plus one retry when the reply is not JSON or the check rejects it.</summary>
    private async Task<T> AskJsonAsync<T>(string system, string user, Func<T, string?> reject, CancellationToken ct)
        where T : class
    {
        string? reason = null;
        for (var attempt = 0; attempt < 2; attempt++)
        {
            var history = new List<Message> { new() { Role = MessageRole.System, Content = system } };
            if (reason is not null)
                history.Add(new() { Role = MessageRole.System, Content = $"上次结果不合格：{reason}。请按要求重写。" });
            var text = await CompleteAsync(history, user, ct).ConfigureAwait(false);
            var parsed = Parse<T>(text);
            reason = parsed is null ? "没有只返回要求的 JSON" : reject(parsed);
            if (reason is null) return parsed!;
        }
        throw new PersonaAssistantException(GiveUp);
    }

    private async Task<string> CompleteAsync(List<Message> history, string user, CancellationToken ct)
    {
        var llm = createLlm();
        try
        {
            var sb = new StringBuilder();
            await foreach (var token in llm.StreamAsync(history, user, ct).ConfigureAwait(false))
                sb.Append(token);
            return sb.ToString();
        }
        finally
        {
            (llm as IDisposable)?.Dispose();
        }
    }

    private static T? Parse<T>(string text) where T : class
    {
        // The object may come wrapped in a code fence or a sentence: take the outermost braces.
        var start = text.IndexOf('{');
        var end = text.LastIndexOf('}');
        if (start < 0 || end <= start) return null;
        try { return JsonSerializer.Deserialize<T>(text[start..(end + 1)], Json); }
        catch (JsonException) { return null; }
    }

    private static void EnsureLength(string description)
    {
        if ((description ?? "").Length > MaxDescriptionChars)
            throw new PersonaAssistantException($"描述太长了，请精简到 {MaxDescriptionChars} 字以内");
    }

    private static string DescriptionBlock(string description) =>
        "主播的描述：\n" + (string.IsNullOrWhiteSpace(description) ? "（空）" : description.Trim());

    private static IReadOnlyList<string> Suggest(IEnumerable<string>? fromModel, IEnumerable<string> defaults)
    {
        var list = (fromModel ?? []).Select(Blank).OfType<string>().Distinct().Take(4).ToList();
        foreach (var d in defaults)
        {
            if (list.Count >= 2) break;
            if (!list.Contains(d)) list.Add(d);
        }
        return list;
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    private sealed class CheckReply
    {
        public string? AiName { get; set; }
        public string? StreamerTitle { get; set; }
        public bool NeedsStyle { get; set; }
        public SuggestionSet? Suggestions { get; set; }
    }

    private sealed class SuggestionSet
    {
        public List<string>? AiName { get; set; }
        public List<string>? StreamerTitle { get; set; }
        public List<string>? Style { get; set; }
    }

    private sealed class ComposeReply
    {
        public string? Persona { get; set; }
        public List<string?>? AiNames { get; set; }
        public string? StreamerTitle { get; set; }
        public List<string>? Supplemented { get; set; }
    }
}
