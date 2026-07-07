using System.Net.Http.Headers;
using System.Runtime.CompilerServices;
using System.Text;
using System.Text.RegularExpressions;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// LLM client compatible with OpenAI Chat Completions API format.
/// Supports streaming output with sentence boundary detection and emotion tag parsing.
/// </summary>
public sealed class LlmClient : ILlmClient, IDisposable
{
    private readonly HttpClient _httpClient;
    private readonly string _baseUrl;
    private readonly string _apiKey;
    private readonly string _model;
    private readonly string _systemPrompt;

    private static readonly Regex SentenceBoundaryRegex = new(@"[。！？；，,.!?;\n]", RegexOptions.Compiled);
    private static readonly Regex EmotionTagRegex = new(@"\[emotion:(\w+)\]", RegexOptions.Compiled);
    // Strips *action text* and （动作）/(action) that LLMs sometimes emit as stage directions.
    // Asterisk form: any non-newline chars between * … * on the same line.
    // Parenthesis form: no length cap — long action descriptions like （她翻了个白眼，靠在椅背上）must also be stripped.
    private static readonly Regex ActionTextRegex = new(@"\*[^*\n]+\*|[（(][^）)\n]+[）)]|【[^】\n]+】", RegexOptions.Compiled);
    // Strips unclosed [ tags at end of a sentence fragment (cut mid-tag during streaming).
    private static readonly Regex PartialTagRegex = new(@"\[[^\]]*$", RegexOptions.Compiled);

    public event EventHandler<string>? OnSentenceReady;
    public event EventHandler<string>? OnEmotionDetected;

    public LlmClient(string baseUrl, string apiKey, string model, string systemPrompt)
    {
        _baseUrl = baseUrl.TrimEnd('/');
        _apiKey = apiKey;
        _model = model.Trim();
        _systemPrompt = systemPrompt;
        _httpClient = new HttpClient { Timeout = TimeSpan.FromMinutes(5) };
    }

    public async IAsyncEnumerable<string> StreamAsync(
        List<Message> history,
        string userInput,
        [EnumeratorCancellation] CancellationToken cancellationToken = default)
    {
        var messages = BuildMessages(history, userInput);

        var requestBody = new
        {
            model = _model,
            messages = messages,
            stream = true,
            // Safety cap only — brevity is enforced by the system prompt ("一句顶十句别啰嗦").
            // Must stay wide enough that a normal short reply finishes naturally (EOS) WITH its
            // trailing [emotion:xxx] tag intact; a tight cap (e.g. 128) hard-truncates mid-sentence
            // and drops the emotion tag that drives VTS expression + TTS emotion.
            max_tokens = 256
        };

        var json = JsonSerializer.Serialize(requestBody, JsonOptions);
        var request = new HttpRequestMessage(HttpMethod.Post, $"{_baseUrl}/v1/chat/completions")
        {
            Content = new StringContent(json, Encoding.UTF8, "application/json")
        };
        request.Headers.Authorization = new AuthenticationHeaderValue("Bearer", _apiKey);

        using var response = await _httpClient.SendAsync(
            request, HttpCompletionOption.ResponseHeadersRead, cancellationToken);
        response.EnsureSuccessStatusCode();

        await using var stream = await response.Content.ReadAsStreamAsync(cancellationToken);
        using var reader = new StreamReader(stream);

        var buffer = new StringBuilder();

        while (!cancellationToken.IsCancellationRequested)
        {
            var line = await reader.ReadLineAsync(cancellationToken);
            if (string.IsNullOrEmpty(line)) continue;
            if (!line.StartsWith("data: ")) continue;

            var data = line[6..];
            if (data == "[DONE]") break;

            SseChunk? chunk;
            try
            {
                chunk = JsonSerializer.Deserialize<SseChunk>(data, JsonOptions);
            }
            catch (JsonException)
            {
                continue;
            }

            if (chunk?.Choices is null || chunk.Choices.Count == 0) continue;

            var delta = chunk.Choices[0].Delta;
            if (delta?.Content is null) continue;

            var token = delta.Content;
            buffer.Append(token);
            yield return token;

            // Check for sentence boundaries and emit complete sentences
            var currentText = buffer.ToString();
            var emotionMatch = EmotionTagRegex.Match(currentText);
            while (emotionMatch.Success)
            {
                OnEmotionDetected?.Invoke(this, emotionMatch.Groups[1].Value);
                currentText = currentText.Remove(emotionMatch.Index, emotionMatch.Length);
                buffer.Clear();
                buffer.Append(currentText);
                emotionMatch = EmotionTagRegex.Match(currentText);
            }

            if (ContainsSentenceBoundary(currentText, out var sentence, out var remainder))
            {
                var trimmed = StripActionText(StripEmotionTags(sentence)).Trim();
                if (!string.IsNullOrWhiteSpace(trimmed))
                {
                    OnSentenceReady?.Invoke(this, trimmed);
                }
                buffer.Clear();
                buffer.Append(remainder);
            }
        }

        // Emit any remaining text as a sentence
        var remaining = StripActionText(StripEmotionTags(buffer.ToString())).Trim();
        if (!string.IsNullOrWhiteSpace(remaining))
        {
            OnSentenceReady?.Invoke(this, remaining);
        }
    }

    private List<object> BuildMessages(List<Message> history, string userInput)
    {
        var messages = new List<object>();

        // System prompt
        if (!string.IsNullOrWhiteSpace(_systemPrompt))
        {
            messages.Add(new { role = "system", content = _systemPrompt });
        }

        // History
        foreach (var msg in history)
        {
            messages.Add(new { role = msg.Role.ToString().ToLowerInvariant(), content = msg.Content });
        }

        // Current user input
        messages.Add(new { role = "user", content = userInput });

        return messages;
    }

    /// <summary>
    /// Removes any complete [emotion:xxx] tags from the text. Partial/incomplete tags
    /// (still being streamed) are left untouched so they can be stripped once complete.
    /// Used by the TTS pipeline so emotion tags are never spoken aloud.
    /// </summary>
    internal static string StripEmotionTags(string text)
        => EmotionTagRegex.Replace(text, string.Empty);

    internal static string StripActionText(string text)
        => ActionTextRegex.Replace(text, string.Empty);

    /// <summary>
    /// Removes partial/unclosed tags left at the end of a sentence that was cut mid-tag
    /// (e.g. "你好[emotion:开心，" where the sentence boundary landed inside the tag).
    /// </summary>
    internal static string StripPartialTags(string text)
        => PartialTagRegex.Replace(text, string.Empty);

    internal static bool ContainsSentenceBoundary(string text, out string sentence, out string remainder)
    {
        var match = SentenceBoundaryRegex.Match(text);
        if (match.Success)
        {
            var splitIndex = match.Index + match.Length;
            sentence = text[..splitIndex];
            remainder = text[splitIndex..];
            return true;
        }

        sentence = remainder = string.Empty;
        return false;
    }

    public void Dispose()
    {
        _httpClient.Dispose();
    }

    // SSE response models
    private sealed class SseChunk
    {
        [JsonPropertyName("choices")]
        public List<SseChoice>? Choices { get; set; }
    }

    private sealed class SseChoice
    {
        [JsonPropertyName("delta")]
        public SseDelta? Delta { get; set; }

        [JsonPropertyName("finish_reason")]
        public string? FinishReason { get; set; }
    }

    private sealed class SseDelta
    {
        [JsonPropertyName("content")]
        public string? Content { get; set; }
    }

    private static readonly JsonSerializerOptions JsonOptions = new()
    {
        PropertyNamingPolicy = JsonNamingPolicy.SnakeCaseLower,
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
    };
}
