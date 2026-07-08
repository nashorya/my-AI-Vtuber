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
    /// Builds the complete message list. The system prompt is fixed (never changes per-turn)
    /// so DeepSeek's prefix cache stays valid. Viewer profile and memory facts are injected
    /// as a prefix on the latest user message instead, keeping them out of the cached prefix.
    /// </summary>
    /// <summary>
    /// Builds the complete message list. The main system prompt is always first (fixed content)
    /// so DeepSeek's prefix cache stays valid across turns. Memory context (viewer profile +
    /// relevant facts) is injected as a second system message immediately after, keeping it
    /// invisible to the LLM as "user speech" while still varying per-turn without breaking cache.
    /// </summary>
    public List<Message> BuildMessages(string? viewerUid = null)
    {
        lock (_lock)
        {
            var messages = new List<Message>();

            if (!string.IsNullOrWhiteSpace(_llmConfig.SystemPrompt))
                messages.Add(new Message { Role = MessageRole.System, Content = _llmConfig.SystemPrompt });

            // Second system message carries dynamic memory. Placing it here (after the long
            // static prompt) preserves prefix cache on the static part while still grounding
            // the LLM with current viewer context. It is NOT in user role, so the LLM won't
            // interpret or recite it as user speech.
            var context = BuildMemoryContext(viewerUid);
            if (!string.IsNullOrEmpty(context))
                messages.Add(new Message { Role = MessageRole.System, Content = context.TrimEnd() });

            messages.AddRange(_history);
            return messages;
        }
    }

    /// <summary>Builds viewer profile + relevant facts context block. Empty when no memory available.</summary>
    private string BuildMemoryContext(string? viewerUid)
    {
        var sb = new StringBuilder();

        if (_viewerRepo is not null && !string.IsNullOrEmpty(viewerUid))
        {
            var viewer = _viewerRepo.GetAsync(viewerUid, "bilibili").GetAwaiter().GetResult();
            if (viewer is not null)
            {
                sb.AppendLine($"【观众档案】UID: {viewer.Uid}, 昵称: {viewer.Nickname ?? "未知"}, " +
                    $"互动次数: {viewer.InteractionCount}, 上次来访: {viewer.LastSeen}");
                if (!string.IsNullOrEmpty(viewer.Notes))
                    sb.AppendLine($"备注: {viewer.Notes}");
            }
        }

        return sb.Length > 0 ? sb.AppendLine().ToString() : string.Empty;
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
