using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Config;
using AIVTuber.Core.Bot;

namespace AIVTuber.Core.Memory;

/// <summary>
/// Periodically extracts facts from conversation using LLM.
/// Fires every N turns (configurable), sends recent conversation + existing facts
/// to LLM and parses JSON output into Fact objects for storage.
/// </summary>
public sealed class MemoryExtractor
{
    private readonly ILlmClient _llm;
    private readonly FactRepository _factRepo;
    private readonly MemoryConfig _config;
    private readonly ConversationManager _conversation;
    private int _turnCount;
    private bool _extracting;

    private static readonly Regex JsonBlockRegex = new(@"```json\s*(\{.*?\})\s*```", RegexOptions.Singleline | RegexOptions.Compiled);
    private static readonly Regex RawJsonRegex = new(@"\{[^{}]*""facts""\s*:\s*\[.*?\]\s*\}", RegexOptions.Singleline | RegexOptions.Compiled);

    private const string ExtractionPrompt = @"你是直播间AI的记忆整理员。从对话片段中提取值得长期记住的事实。

【值得记住】关于具体人的稳定信息：身份、喜好、经历、关系、约定、纠正过的错误
【不要记住】寒暄客套、玩梗刷屏、一次性话题、AI自己说的话、礼物感谢

规则：
1. 每条事实独立成立，不依赖上下文
2. 第三人称，主语写明 UID，不写""他/她""
3. 时效性信息标注日期
4. 没有值得记的返回空数组，宁缺毋滥

对话片段：
{conversation}

相关旧记忆：
{existing_facts}

输出JSON：
{""facts"":[{""subject_uid"":""..."",""content"":""..."",""importance"":3,""expires"":""stable"",""relation_to_old"":""new""}]}";

    public MemoryExtractor(ILlmClient llm, FactRepository factRepo, MemoryConfig config, ConversationManager conversation)
    {
        _llm = llm;
        _factRepo = factRepo;
        _config = config;
        _conversation = conversation;
    }

    /// <summary>
    /// Called after each user turn. Triggers extraction every N turns.
    /// </summary>
    public async Task OnTurnAsync()
    {
        _turnCount++;
        if (_turnCount % _config.ExtractEveryNTurns != 0) return;
        if (_extracting) return; // Don't overlap extractions
        _extracting = true;

        try { await ExtractFactsAsync().ConfigureAwait(false); }
        catch (Exception ex) { Console.Error.WriteLine($"[MemoryExtractor] Error: {ex.Message}"); }
        finally { _extracting = false; }
    }

    /// <summary>Force extraction regardless of turn count.</summary>
    public async Task ExtractFactsAsync()
    {
        var history = _conversation.GetHistory();
        if (history.Count == 0) return;

        // Build conversation text
        var sb = new StringBuilder();
        foreach (var msg in history)
            sb.AppendLine($"{msg.Role}: {msg.Content}");

        // Get relevant existing facts for context
        var recentContent = history.TakeLast(3).Select(m => m.Content);
        var existingFacts = new List<(Fact fact, float similarity)>();
        foreach (var content in recentContent)
        {
            var results = await _factRepo.SearchAsync(content, null, 3).ConfigureAwait(false);
            existingFacts.AddRange(results);
        }
        existingFacts = existingFacts.DistinctBy(f => f.fact.Id).Take(10).ToList();

        var factsText = existingFacts.Count == 0
            ? "（无相关旧记忆）"
            : string.Join("\n", existingFacts.Select(f => $"- [{f.fact.Id}] {f.fact.Content}"));

        var prompt = ExtractionPrompt
            .Replace("{conversation}", sb.ToString())
            .Replace("{existing_facts}", factsText);

        // Use LLM to extract facts
        var messages = new List<Message>
        {
            new() { Role = MessageRole.System, Content = prompt }
        };

        var responseBuilder = new StringBuilder();
        await foreach (var token in _llm.StreamAsync(messages, "", CancellationToken.None))
            responseBuilder.Append(token);

        var response = responseBuilder.ToString();
        await ParseAndStoreFactsAsync(response).ConfigureAwait(false);
    }

    internal async Task ParseAndStoreFactsAsync(string llmResponse)
    {
        // Try to extract JSON from the response
        var jsonText = ExtractJson(llmResponse);
        if (jsonText is null) return;

        try
        {
            var result = JsonSerializer.Deserialize<ExtractionResult>(jsonText);
            if (result?.Facts is null || result.Facts.Count == 0) return;

            foreach (var factData in result.Facts)
            {
                if (string.IsNullOrWhiteSpace(factData.Content)) continue;

                var fact = new Fact
                {
                    SubjectUid = factData.SubjectUid,
                    Content = factData.Content,
                    Importance = factData.Importance,
                    Expires = string.IsNullOrEmpty(factData.Expires) ? "stable" : factData.Expires,
                    RelationToOld = factData.RelationToOld
                };
                await _factRepo.InsertOrMergeAsync(fact).ConfigureAwait(false);
            }
        }
        catch (JsonException) { /* ignore malformed responses */ }
    }

    internal static string? ExtractJson(string text)
    {
        // Try ```json ... ``` block first
        var match = JsonBlockRegex.Match(text);
        if (match.Success) return match.Groups[1].Value;

        // Try raw JSON
        match = RawJsonRegex.Match(text);
        if (match.Success) return match.Value;

        return null;
    }

    private sealed class ExtractionResult
    {
        [System.Text.Json.Serialization.JsonPropertyName("facts")]
        public List<FactData>? Facts { get; set; }
    }

    private sealed class FactData
    {
        [System.Text.Json.Serialization.JsonPropertyName("subject_uid")]
        public string? SubjectUid { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("content")]
        public string Content { get; set; } = "";

        [System.Text.Json.Serialization.JsonPropertyName("importance")]
        public int Importance { get; set; } = 3;

        [System.Text.Json.Serialization.JsonPropertyName("expires")]
        public string? Expires { get; set; }

        [System.Text.Json.Serialization.JsonPropertyName("relation_to_old")]
        public string? RelationToOld { get; set; }
    }
}