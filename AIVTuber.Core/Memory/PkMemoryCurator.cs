using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVTuber.Core.Pipeline;

namespace AIVTuber.Core.Memory;

/// <summary>
/// After a PK match ends, asks the LLM which original dialogue pairs to keep,
/// then persists only those (never rewrites opponent/assistant text into summaries).
/// </summary>
public sealed class PkMemoryCurator
{
    public const int MinCharsFallback = 4;

    private static readonly Regex JsonFenceRegex = new(
        @"```(?:json)?\s*(\{.*?\})\s*```",
        RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex JsonObjectRegex = new(
        @"\{[^{}]*""keep""\s*:\s*\[[^\]]*\][^{}]*\}",
        RegexOptions.Singleline | RegexOptions.Compiled);

    private readonly ILlmClient _llm;
    private readonly PkTurnRepository _repo;

    public PkMemoryCurator(ILlmClient llm, PkTurnRepository repo)
    {
        _llm = llm;
        _repo = repo;
    }

    /// <summary>
    /// Curates and persists turns for a finished match. Safe to call fire-and-forget.
    /// </summary>
    public async Task PersistMatchAsync(
        string matchId,
        string? opponentUid,
        string? opponentName,
        IReadOnlyList<BufferedPkTurn> turns,
        CancellationToken ct = default)
    {
        if (string.IsNullOrEmpty(matchId))
            return;

        await _repo.InsertMatchAsync(new PkMatch
        {
            MatchId = matchId,
            OpponentUid = opponentUid,
            OpponentName = opponentName,
            StartedAt = turns.Count > 0 ? turns[0].Ts : DateTime.UtcNow.ToString("o"),
            Status = "active",
        }).ConfigureAwait(false);

        if (turns.Count == 0)
        {
            await _repo.EndMatchAsync(matchId, DateTime.UtcNow.ToString("o")).ConfigureAwait(false);
            return;
        }

        var keep = await SelectKeepIndicesAsync(opponentName, turns, ct).ConfigureAwait(false);
        if (keep is null)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write("[PK记忆] 策展失败，降级为长度过滤落盘");
            keep = FallbackKeep(turns);
        }

        var selected = turns.Where(t => keep.Contains(t.Index)).ToList();
        var toStore = selected.Select(t => new PkTurn
        {
            Id = Guid.NewGuid().ToString("N"),
            MatchId = matchId,
            OpponentUid = opponentUid,
            OpponentName = opponentName,
            OpponentText = t.OpponentText,
            AssistantText = t.AssistantText,
            Source = t.Source,
            Ts = t.Ts,
        });

        await _repo.InsertTurnsAsync(toStore).ConfigureAwait(false);
        await _repo.EndMatchAsync(matchId, DateTime.UtcNow.ToString("o")).ConfigureAwait(false);
        AIVTuber.Core.Diagnostics.DebugLog.Write(
            $"[PK记忆] 落盘 {selected.Count}/{turns.Count} 对（对手 {opponentName ?? opponentUid ?? "?"}）");
    }

    internal static HashSet<int> FallbackKeep(IReadOnlyList<BufferedPkTurn> turns, int minChars = MinCharsFallback)
    {
        var keep = new HashSet<int>();
        foreach (var t in turns)
        {
            if (t.OpponentText.Trim().Length >= minChars && t.AssistantText.Trim().Length >= minChars)
                keep.Add(t.Index);
        }
        return keep;
    }

    /// <summary>Parses curator JSON; returns null when unusable.</summary>
    internal static HashSet<int>? ParseKeepIndices(string llmResponse, int turnCount)
    {
        var json = ExtractJson(llmResponse);
        if (json is null) return null;
        try
        {
            using var doc = JsonDocument.Parse(json);
            if (!doc.RootElement.TryGetProperty("keep", out var arr) || arr.ValueKind != JsonValueKind.Array)
                return null;
            var keep = new HashSet<int>();
            foreach (var el in arr.EnumerateArray())
            {
                if (el.ValueKind == JsonValueKind.Number && el.TryGetInt32(out var i)
                    && i >= 0 && i < turnCount)
                    keep.Add(i);
            }
            return keep;
        }
        catch (JsonException)
        {
            return null;
        }
    }

    internal static string? ExtractJson(string text)
    {
        if (string.IsNullOrWhiteSpace(text)) return null;
        var fence = JsonFenceRegex.Match(text);
        if (fence.Success) return fence.Groups[1].Value;
        var match = JsonObjectRegex.Match(text);
        if (match.Success) return match.Value;
        var trimmed = text.Trim();
        if (trimmed.StartsWith('{') && trimmed.Contains("\"keep\"")) return trimmed;
        return null;
    }

    private async Task<HashSet<int>?> SelectKeepIndicesAsync(
        string? opponentName,
        IReadOnlyList<BufferedPkTurn> turns,
        CancellationToken ct)
    {
        var sb = new StringBuilder();
        sb.AppendLine("你是 PK 对话记忆策展员。下面是本场「对面原话 / 助手回答」编号列表。");
        sb.AppendLine("选出值得下次再遇到同一主播时召回的编号。只保留有交锋、信息或可回击价值的对；丢掉寒暄、ASR 胡话、无意义短句。");
        sb.AppendLine("禁止改写或总结原文。只输出 JSON：{\"keep\":[0,2],\"reason\":\"...\"}");
        sb.AppendLine($"对手：{opponentName ?? "未知"}");
        sb.AppendLine("对话对：");
        foreach (var t in turns)
        {
            sb.Append('[').Append(t.Index).Append("] 对面：").Append(t.OpponentText)
              .Append(" | 你：").Append(t.AssistantText).AppendLine();
        }

        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = sb.ToString() },
        };

        try
        {
            using var timeout = CancellationTokenSource.CreateLinkedTokenSource(ct);
            timeout.CancelAfter(TimeSpan.FromSeconds(45));
            var response = new StringBuilder();
            await foreach (var token in _llm.StreamAsync(messages, "只输出 JSON，不要改写原文。", timeout.Token).ConfigureAwait(false))
                response.Append(token);
            return ParseKeepIndices(response.ToString(), turns.Count);
        }
        catch (Exception ex)
        {
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[PK记忆] 策展 LLM 失败: {ex.Message}");
            return null;
        }
    }
}
