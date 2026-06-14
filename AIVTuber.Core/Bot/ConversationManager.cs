using System.Text;
using AIVTuber.Core.Config;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Bot;

/// <summary>
/// Manages conversation context with sliding window, summarization,
/// and memory injection (viewer profiles + relevant facts) into system prompt.
/// </summary>
public sealed class ConversationManager
{
    private readonly LlmConfig _llmConfig;
    private readonly int _maxHistoryTokens;
    private readonly List<Message> _history = [];
    private readonly object _lock = new();
    private const int TokensPerMessageOverhead = 4;

    // Memory injection fields
    private ViewerRepository? _viewerRepo;
    private FactRepository? _factRepo;

    public ConversationManager(LlmConfig llmConfig)
    {
        _llmConfig = llmConfig;
        _maxHistoryTokens = llmConfig.MaxHistoryTokens;
    }

    /// <summary>Inject memory repositories for context enrichment.</summary>
    public void SetMemory(ViewerRepository? viewerRepo, FactRepository? factRepo)
    {
        _viewerRepo = viewerRepo;
        _factRepo = factRepo;
    }

    public void AddUserMessage(string content)
    {
        lock (_lock) { _history.Add(new Message { Role = MessageRole.User, Content = content }); TrimHistory(); }
    }

    public void AddAssistantMessage(string content)
    {
        lock (_lock) { _history.Add(new Message { Role = MessageRole.Assistant, Content = content }); TrimHistory(); }
    }

    public List<Message> GetHistory()
    {
        lock (_lock) { return [.. _history]; }
    }

    /// <summary>
    /// Builds the complete message list including system prompt + memory context.
    /// Memory block includes: viewer profile (if uid known) + top-5 relevant facts.
    /// </summary>
    public List<Message> BuildMessages(string? viewerUid = null)
    {
        lock (_lock)
        {
            var messages = new List<Message>();
            var systemContent = BuildSystemPrompt(viewerUid);
            if (!string.IsNullOrWhiteSpace(systemContent))
                messages.Add(new Message { Role = MessageRole.System, Content = systemContent });

            messages.AddRange(_history);
            return messages;
        }
    }

    /// <summary>Builds system prompt with optional memory context injected.</summary>
    private string BuildSystemPrompt(string? viewerUid)
    {
        var prompt = new StringBuilder();

        if (!string.IsNullOrWhiteSpace(_llmConfig.SystemPrompt))
            prompt.AppendLine(_llmConfig.SystemPrompt);

        // Inject viewer profile
        if (_viewerRepo is not null && !string.IsNullOrEmpty(viewerUid))
        {
            var viewer = _viewerRepo.GetAsync(viewerUid, "bilibili").GetAwaiter().GetResult();
            if (viewer is not null)
            {
                prompt.AppendLine();
                prompt.AppendLine($"【观众档案】UID: {viewer.Uid}, 昵称: {viewer.Nickname ?? "未知"}, " +
                    $"互动次数: {viewer.InteractionCount}, 上次来访: {viewer.LastSeen}");
                if (!string.IsNullOrEmpty(viewer.Notes))
                    prompt.AppendLine($"备注: {viewer.Notes}");
            }
        }

        // Inject relevant facts
        if (_factRepo is not null && _history.Count > 0)
        {
            var lastMsg = _history.LastOrDefault(m => m.Role == MessageRole.User);
            if (lastMsg is not null)
            {
                var facts = _factRepo.SearchAsync(lastMsg.Content, null, 5).GetAwaiter().GetResult();
                if (facts.Count > 0)
                {
                    prompt.AppendLine();
                    prompt.AppendLine("【相关记忆】");
                    foreach (var (fact, sim) in facts.Take(5))
                        prompt.AppendLine($"- {fact.Content} (重要度:{fact.Importance}, 相似:{sim:F2})");
                }
            }
        }

        return prompt.ToString();
    }

    public int GetEstimatedTokenCount()
    {
        lock (_lock) { return _history.Sum(m => EstimateTokens(m.Content) + TokensPerMessageOverhead); }
    }

    public void Clear()
    {
        lock (_lock) { _history.Clear(); }
    }

    /// <summary>Replaces earliest messages with an LLM-generated summary to save tokens.</summary>
    public void ReplaceWithSummary(string summary)
    {
        lock (_lock)
        {
            if (_history.Count <= 2) return;
            _history.RemoveRange(0, _history.Count - 2);
            _history.Insert(0, new Message { Role = MessageRole.Assistant, Content = $"[对话摘要] {summary}" });
            TrimHistory();
        }
    }

    private void TrimHistory()
    {
        while (_history.Count > 1 && GetEstimatedTokenCountInternal() > _maxHistoryTokens)
            _history.RemoveAt(0);
    }

    private int GetEstimatedTokenCountInternal()
        => _history.Sum(m => EstimateTokens(m.Content) + TokensPerMessageOverhead);

    internal static int EstimateTokens(string text)
    {
        if (string.IsNullOrEmpty(text)) return 0;
        int tokens = 0;
        foreach (char c in text)
        {
            if (c > 0x4E00) tokens += 1;
            else if (!char.IsWhiteSpace(c)) tokens += 1;
        }
        return Math.Max(1, tokens * 3 / 4);
    }
}