using System.Text;
using AIVTuber.Core.Config;
using AIVTuber.Core.LiveStream;
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
    private PkTurnRepository? _pkTurnRepo;
    private IdentityConfig? _identity;
    private PkOpponent? _livePk;

    public ConversationManager(LlmConfig llmConfig)
    {
        _llmConfig = llmConfig;
        _maxHistoryTokens = llmConfig.MaxHistoryTokens;
    }

    /// <summary>Inject memory repositories for context enrichment.</summary>
    public void SetMemory(ViewerRepository? viewerRepo, FactRepository? factRepo, PkTurnRepository? pkTurns = null)
    {
        _viewerRepo = viewerRepo;
        _factRepo = factRepo;
        _pkTurnRepo = pkTurns;
    }

    public void SetIdentity(IdentityConfig? identity)
    {
        lock (_lock) { _identity = identity; }
    }

    /// <summary>
    /// Pins the live PK opponent into the per-turn system context so the model
    /// knows who is on the other side even when WakeGate drops the announce line.
    /// </summary>
    public void SetLivePkOpponent(PkOpponent? opponent)
    {
        lock (_lock) { _livePk = opponent; }
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
    public List<Message> BuildMessages(
        string? viewerUid = null,
        string? query = null,
        IReadOnlyList<string>? subjectUids = null)
    {
        lock (_lock)
        {
            var messages = new List<Message>();

            if (!string.IsNullOrWhiteSpace(_llmConfig.SystemPrompt))
                messages.Add(new Message { Role = MessageRole.System, Content = _llmConfig.SystemPrompt });

            var context = BuildMemoryContext(viewerUid, query, subjectUids);
            if (!string.IsNullOrEmpty(context))
                messages.Add(new Message { Role = MessageRole.System, Content = context.TrimEnd() });

            messages.AddRange(_history);
            return messages;
        }
    }

    /// <summary>Builds viewer profile + relevant facts context block. Empty when no memory available.</summary>
    private string BuildMemoryContext(string? viewerUid, string? query, IReadOnlyList<string>? subjectUids)
    {
        var sb = new StringBuilder();

        if (_livePk is { } pk)
        {
            sb.Append("【当前PK】对方主播：").Append(pk.Username);
            if (pk.FollowerCount > 0)
                sb.Append("，粉丝 ").Append(pk.FollowerCount);
            if (pk.RoomId != 0)
                sb.Append("，房间 ").Append(pk.RoomId);
            if (!string.IsNullOrEmpty(pk.Uid))
                sb.Append("，UID ").Append(pk.Uid);
            sb.AppendLine();
        }

        var profileUids = new HashSet<string>(StringComparer.Ordinal);
        if (!string.IsNullOrEmpty(viewerUid)) profileUids.Add(viewerUid);
        if (subjectUids is not null)
        {
            foreach (var uid in subjectUids)
                if (!string.IsNullOrEmpty(uid)) profileUids.Add(uid);
        }

        if (_viewerRepo is not null)
        {
            foreach (var uid in profileUids)
            {
                var viewer = _viewerRepo.GetAsync(uid, "bilibili").GetAwaiter().GetResult();
                if (viewer is null) continue;
                sb.AppendLine($"【档案】UID: {viewer.Uid}, 昵称: {viewer.Nickname ?? "未知"}, " +
                    $"互动次数: {viewer.InteractionCount}, 上次来访: {viewer.LastSeen}");
                if (!string.IsNullOrEmpty(viewer.Notes))
                    sb.AppendLine($"备注: {viewer.Notes}");
            }
        }

        AppendRelevantFacts(sb, query, profileUids);
        AppendOpponentPkTurns(sb);
        return sb.Length > 0 ? sb.AppendLine().ToString() : string.Empty;
    }

    /// <summary>
    /// Retrieves top relevant facts for the latest user turn (and the viewer when known)
    /// and appends them so the reply LLM can ground on stored memory — not just extract/store.
    /// </summary>
    private void AppendRelevantFacts(StringBuilder sb, string? queryOverride, HashSet<string> subjectUids)
    {
        if (_factRepo is null) return;

        var query = !string.IsNullOrWhiteSpace(queryOverride) ? queryOverride : LatestUserText();
        if (string.IsNullOrWhiteSpace(query) && subjectUids.Count == 0)
            return;

        if (string.IsNullOrWhiteSpace(query))
            query = " ";

        var byId = new Dictionary<string, Fact>(StringComparer.Ordinal);
        try
        {
            if (!string.IsNullOrWhiteSpace(queryOverride) || !string.IsNullOrWhiteSpace(LatestUserText()))
            {
                foreach (var (fact, _) in _factRepo.SearchAsync(query, subjectUid: null, topK: 5)
                             .GetAwaiter().GetResult())
                    byId[fact.Id] = fact;
            }

            var selfUid = _identity?.SelfUid;
            if (!string.IsNullOrEmpty(selfUid))
                subjectUids.Add(selfUid);
            var opponentUid = _livePk?.Uid;
            if (!string.IsNullOrEmpty(opponentUid))
                subjectUids.Add(opponentUid);

            foreach (var uid in subjectUids)
            {
                foreach (var (fact, _) in _factRepo.SearchAsync(query, uid, topK: 3)
                             .GetAwaiter().GetResult())
                    byId[fact.Id] = fact;
            }
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[Memory] 检索事实失败: {ex.Message}");
            return;
        }

        if (byId.Count == 0) return;

        sb.AppendLine("【相关记忆】");
        foreach (var fact in byId.Values.Take(6))
        {
            sb.Append("- ").Append(fact.Content);
            if (!string.IsNullOrEmpty(fact.SubjectUid))
                sb.Append("（UID: ").Append(fact.SubjectUid).Append('）');
            sb.AppendLine();
            _ = _factRepo.UpdateWeightAsync(fact.Id, 1);
        }
    }

    private void AppendOpponentPkTurns(StringBuilder sb)
    {
        var uid = _livePk?.Uid;
        if (_pkTurnRepo is null || string.IsNullOrEmpty(uid)) return;
        try
        {
            var turns = _pkTurnRepo.ListByOpponentAsync(uid, 3).GetAwaiter().GetResult();
            if (turns.Count == 0) return;
            sb.AppendLine("【对手往期对谈】");
            foreach (var turn in turns)
            {
                if (string.Equals(turn.AssistantText.Trim(), "【PASS】", StringComparison.Ordinal))
                    continue;
                sb.Append("- 对面：").Append(turn.OpponentText);
                if (!string.IsNullOrWhiteSpace(turn.AssistantText))
                    sb.Append(" / 你：").Append(turn.AssistantText);
                sb.AppendLine();
            }
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[Memory] 检索PK对谈失败: {ex.Message}");
        }
    }

    private string LatestUserText()
    {
        for (var i = _history.Count - 1; i >= 0; i--)
        {
            if (_history[i].Role == MessageRole.User && !string.IsNullOrWhiteSpace(_history[i].Content))
                return _history[i].Content;
        }
        return string.Empty;
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
