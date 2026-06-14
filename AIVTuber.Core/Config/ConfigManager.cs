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
        return JsonSerializer.Deserialize<AppConfig>(json, JsonOptions) ?? new AppConfig();
    }

    /// <summary>
    /// Saves the given config to config.json.
    /// </summary>
    public void Save(AppConfig config)
    {
        var json = JsonSerializer.Serialize(config, JsonOptions);
        File.WriteAllText(_configPath, json);
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