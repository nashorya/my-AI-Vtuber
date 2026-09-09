using System.Net;

namespace AIVTuber.Core.Pipeline;

/// <summary>
/// Gemini-only transport tweaks. Official Gemini goes through a local HTTP proxy;
/// other OpenAI-compatible providers stay direct so Bilibili / ASR / TTS are untouched.
/// </summary>
internal static class LlmTransport
{
    public const string GeminiProxy = "http://127.0.0.1:7897";

    public static bool IsGemini(string? baseUrl)
    {
        if (string.IsNullOrWhiteSpace(baseUrl)) return false;
        if (Uri.TryCreate(baseUrl.Trim(), UriKind.Absolute, out var uri))
            return uri.Host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
        return baseUrl.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase);
    }

    public static Uri? ResolveProxy(string? baseUrl)
        => IsGemini(baseUrl) ? new Uri(GeminiProxy) : null;

    public static bool IncludeThinkingDisabled(string? baseUrl)
        => !IsGemini(baseUrl);

    public static string ResolveChatCompletionsUrl(string baseUrl)
    {
        var trimmed = (baseUrl ?? "").Trim().TrimEnd('/');
        if (trimmed.Length == 0)
            throw new ArgumentException("LLM base URL is empty.", nameof(baseUrl));

        if (trimmed.EndsWith("/chat/completions", StringComparison.OrdinalIgnoreCase))
            return trimmed;

        if (IsGemini(trimmed))
        {
            if (trimmed.EndsWith("/openai", StringComparison.OrdinalIgnoreCase))
                return trimmed + "/chat/completions";
            if (trimmed.Contains("/openai/", StringComparison.OrdinalIgnoreCase))
                return trimmed + "/chat/completions";
            if (trimmed.EndsWith("/v1beta", StringComparison.OrdinalIgnoreCase))
                return trimmed + "/openai/chat/completions";
            return trimmed + "/v1beta/openai/chat/completions";
        }

        if (trimmed.EndsWith("/v1", StringComparison.OrdinalIgnoreCase))
            return trimmed + "/chat/completions";
        return trimmed + "/v1/chat/completions";
    }

    public static HttpClientHandler CreateHandler(string? baseUrl)
    {
        var handler = new HttpClientHandler();
        var proxy = ResolveProxy(baseUrl);
        if (proxy is null)
        {
            handler.UseProxy = false;
            return handler;
        }

        handler.Proxy = new WebProxy(proxy);
        handler.UseProxy = true;
        return handler;
    }
}
