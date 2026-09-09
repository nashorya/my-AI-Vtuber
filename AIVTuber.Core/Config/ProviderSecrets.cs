namespace AIVTuber.Core.Config;

internal readonly record struct LlmPreset(string Id, string BaseUrl, string Model);

/// <summary>
/// Per-vendor API key slots so switching DeepSeek ↔ Gemini (or TTS/ASR providers)
/// does not overwrite the previous key.
/// </summary>
internal static class ProviderSecrets
{
    public const string DeepSeek = "deepseek";
    public const string Gemini = "gemini";
    public const string Custom = "custom";

    public static readonly LlmPreset DeepSeekPreset =
        new(DeepSeek, "https://api.deepseek.com", "deepseek-chat");

    public static readonly LlmPreset GeminiPreset =
        new(Gemini, "https://generativelanguage.googleapis.com/v1beta/openai", "gemini-2.5-flash");

    public static string LlmVendor(string? baseUrl)
    {
        var raw = (baseUrl ?? "").Trim();
        if (Uri.TryCreate(raw, UriKind.Absolute, out var uri))
        {
            var host = uri.Host;
            if (host.Equals("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase) ||
                host.EndsWith(".googleapis.com", StringComparison.OrdinalIgnoreCase) &&
                host.Contains("generativelanguage", StringComparison.OrdinalIgnoreCase))
                return "gemini";
            if (host.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
                return "deepseek";
            if (host.Contains("openai.com", StringComparison.OrdinalIgnoreCase))
                return "openai";
            return string.IsNullOrEmpty(host) ? "default" : host.ToLowerInvariant();
        }

        if (raw.Contains("generativelanguage.googleapis.com", StringComparison.OrdinalIgnoreCase))
            return "gemini";
        if (raw.Contains("deepseek", StringComparison.OrdinalIgnoreCase))
            return DeepSeek;
        return string.IsNullOrEmpty(raw) ? "default" : raw.ToLowerInvariant();
    }

    public static LlmPreset? TryPreset(string? provider) => Slot(provider, "") switch
    {
        DeepSeek => DeepSeekPreset,
        Gemini => GeminiPreset,
        _ => null,
    };

    /// <summary>Key slot: known provider wins; otherwise infer from the Base URL host.</summary>
    public static string InferLlmVendor(string? provider, string? baseUrl)
    {
        var slot = Slot(provider, "");
        if (slot is DeepSeek or Gemini) return slot;
        return LlmVendor(baseUrl);
    }

    /// <summary>
    /// Dropdown value. A known host always maps to that vendor so older configs
    /// (no <c>provider</c> field) stay in sync with the Base URL.
    /// </summary>
    public static string NormalizeLlmProvider(string? provider, string? baseUrl)
    {
        var inferred = LlmVendor(baseUrl);
        if (inferred is DeepSeek or Gemini) return inferred;
        var slot = Slot(provider, "");
        return slot is DeepSeek or Gemini or Custom ? slot : Custom;
    }

    public static void ApplyLlmProvider(LlmConfig llm, string provider)
    {
        llm.RememberActiveKey();
        Remember(llm.Models, llm.VendorId, llm.Model);
        llm.Provider = Slot(provider, DeepSeek);
        if (TryPreset(llm.Provider) is { } preset)
        {
            llm.BaseUrl = preset.BaseUrl;
            llm.Model = TryActivate(llm.Models, llm.Provider, out var model) && !string.IsNullOrWhiteSpace(model)
                ? model
                : preset.Model;
        }

        llm.ActivateStoredKey();
    }

    public static string Slot(string? provider, string fallback)
        => string.IsNullOrWhiteSpace(provider) ? fallback : provider.Trim().ToLowerInvariant();

    public static void Remember(IDictionary<string, string> keys, string vendor, string? apiKey)
    {
        if (string.IsNullOrWhiteSpace(vendor) || string.IsNullOrWhiteSpace(apiKey)) return;
        keys[vendor] = apiKey;
    }

    public static bool TryActivate(IDictionary<string, string> keys, string vendor, out string key)
    {
        foreach (var kv in keys)
        {
            if (string.Equals(kv.Key, vendor, StringComparison.OrdinalIgnoreCase) &&
                !string.IsNullOrEmpty(kv.Value))
            {
                key = kv.Value;
                return true;
            }
        }

        key = "";
        return false;
    }
}
