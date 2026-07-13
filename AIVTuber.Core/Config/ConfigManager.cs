using System.Text.Json;
using System.Text.Json.Serialization;

namespace AIVTuber.Core.Config;

/// <summary>
/// Snake_case JSON naming policy for config file serialization.
/// Converts PascalCase to snake_case (e.g., InputDeviceIndex -> input_device_index).
/// </summary>
internal sealed class SnakeCaseNamingPolicy : JsonNamingPolicy
{
    public override string ConvertName(string name)
    {
        if (string.IsNullOrEmpty(name)) return name;

        var sb = new System.Text.StringBuilder(name.Length + 4);
        for (int i = 0; i < name.Length; i++)
        {
            char c = name[i];
            if (char.IsUpper(c))
            {
                if (i > 0) sb.Append('_');
                sb.Append(char.ToLowerInvariant(c));
            }
            else
            {
                sb.Append(c);
            }
        }
        return sb.ToString();
    }
}

/// <summary>
/// Reads and writes config.json with tolerant defaults.
/// Uses snake_case property names to match the implementation plan format.
/// </summary>
public sealed class ConfigManager
{
    public static readonly JsonSerializerOptions JsonOptions = new()
    {
        WriteIndented = true,
        PropertyNamingPolicy = new SnakeCaseNamingPolicy(),
        DefaultIgnoreCondition = JsonIgnoreCondition.WhenWritingNull,
        Encoder = System.Text.Encodings.Web.JavaScriptEncoder.UnsafeRelaxedJsonEscaping,
    };

    private readonly string _configPath;

    /// <summary>Creates an ownership-safe deep snapshot using the persisted JSON contract.</summary>
    public static AppConfig Snapshot(AppConfig config)
    {
        ArgumentNullException.ThrowIfNull(config);
        var json = JsonSerializer.Serialize(config, JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions)
               ?? throw new InvalidOperationException("Failed to snapshot AppConfig.");
    }

    public ConfigManager(string configPath)
    {
        _configPath = configPath;
    }

    /// <summary>
    /// Loads config.json. If the file does not exist, creates a template and returns default config.
    /// </summary>
    public AppConfig Load()
    {
        if (!File.Exists(_configPath))
        {
            var defaultConfig = new AppConfig();
            Save(defaultConfig);
            return defaultConfig;
        }

        var json = File.ReadAllText(_configPath);
        var config = JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
        ApplyLegacyCompatibility(json, config);
        return config;
    }

    /// <summary>
    /// Saves the given config to config.json.
    /// </summary>
    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
    }

    private static void ApplyLegacyCompatibility(string json, AppConfig config)
    {
        using var doc = JsonDocument.Parse(json);
        if (!doc.RootElement.TryGetProperty("audio", out var audio) ||
            audio.ValueKind != JsonValueKind.Object)
            return;

        // Older configs used use_loopback for PK/PC-audio listening. The runtime now uses
        // enable_loopback_listen, so migrate only when the new key is absent.
        if (!audio.TryGetProperty("enable_loopback_listen", out _) &&
            audio.TryGetProperty("use_loopback", out var legacyUseLoopback) &&
            legacyUseLoopback.ValueKind is JsonValueKind.True or JsonValueKind.False)
        {
            config.Audio.EnableLoopbackListen = legacyUseLoopback.GetBoolean();
        }
    }

    /// <summary>
    /// Generates a config.json.template next to the config path for user reference.
    /// </summary>
    public void GenerateTemplate(string templatePath)
    {
        var template = new AppConfig();
        // Add placeholder hints for sensitive fields
        template.Asr.ApiKey = "<your-asr-api-key>";
        template.Llm.ApiKey = "<your-llm-api-key>";
        template.Tts.ApiKey = "<your-tts-api-key>";
        template.Bilibili.Sessdata = "<your-sessdata>";

        var json = JsonSerializer.Serialize(template, JsonOptions);
        File.WriteAllText(templatePath, json);
    }
}
