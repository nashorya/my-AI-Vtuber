using AIVTuber.Core.Config;

namespace AIVTuber.Tests;

public class ConfigManagerTests : IDisposable
{
    private readonly string _testDir;

    public ConfigManagerTests()
    {
        _testDir = Path.Combine(Path.GetTempPath(), $"AIVTuber_Test_{Guid.NewGuid():N}");
        Directory.CreateDirectory(_testDir);
    }

    private string ConfigPath => Path.Combine(_testDir, "config.json");

    [Fact]
    public void Save_AtomicallyReplacesFileWithoutLeavingTemporaryFile()
    {
        var manager = new ConfigManager(ConfigPath);
        manager.Save(new AppConfig { Llm = { Model = "one" } });
        manager.Save(new AppConfig { Llm = { Model = "two" } });

        Assert.Equal("two", manager.Load().Llm.Model);
        Assert.Empty(Directory.GetFiles(_testDir, ".config.json.*.tmp"));
    }

    [Fact]
    public void Load_CreatesDefaultConfig_WhenFileNotExists()
    {
        var manager = new ConfigManager(ConfigPath);
        var config = manager.Load();

        Assert.NotNull(config);
        Assert.NotNull(config.Audio);
        Assert.NotNull(config.Llm);
        Assert.NotNull(config.Tts);
        Assert.Equal(0, config.Audio.InputDeviceIndex);
        Assert.Equal(2, config.Audio.VadAggressiveness);
        Assert.False(config.Audio.UseLoopback);
        Assert.True(File.Exists(ConfigPath));
    }

    [Fact]
    public void Load_ReadsExistingConfig()
    {
        // Use snake_case to match our naming policy
        var json = """
        {
            "audio": {
                "input_device_index": 2,
                "use_loopback": true,
                "loopback_device_name": "Speaker",
                "vad_aggressiveness": 3,
                "pre_speech_padding_ms": 300,
                "post_speech_silence_ms": 800
            },
            "asr": {
                "provider": "xfyun",
                "api_key": "test-key",
                "app_id": "test-app"
            },
            "llm": {
                "base_url": "https://api.test.com",
                "api_key": "test-llm-key",
                "model": "test-model",
                "system_prompt": "Hello",
                "max_history_tokens": 2048
            }
        }
        """;
        File.WriteAllText(ConfigPath, json);

        var manager = new ConfigManager(ConfigPath);
        var config = manager.Load();

        Assert.Equal(2, config.Audio.InputDeviceIndex);
        Assert.True(config.Audio.UseLoopback);
        Assert.True(config.Audio.EnableLoopbackListen);
        Assert.Equal("Speaker", config.Audio.LoopbackDeviceName);
        Assert.Equal(3, config.Audio.VadAggressiveness);
        Assert.Equal(300, config.Audio.PreSpeechPaddingMs);
        Assert.Equal(800, config.Audio.PostSpeechSilenceMs);
        Assert.Equal("xfyun", config.Asr.Provider);
        Assert.Equal("test-key", config.Asr.ApiKey);
        Assert.Equal("https://api.test.com", config.Llm.BaseUrl);
        Assert.Equal(2048, config.Llm.MaxHistoryTokens);
    }

    [Fact]
    public void Save_WritesSnakeCaseJson()
    {
        var manager = new ConfigManager(ConfigPath);
        var config = new AppConfig
        {
            Audio = new AudioConfig { InputDeviceIndex = 5, UseLoopback = true },
            Llm = new LlmConfig { ApiKey = "my-key", Model = "gpt-4" }
        };

        manager.Save(config);

        Assert.True(File.Exists(ConfigPath));
        var json = File.ReadAllText(ConfigPath);
        // Verify snake_case property names are used
        Assert.Contains("input_device_index", json);
        Assert.Contains("use_loopback", json);
        Assert.Contains("api_key", json);

        // Verify round-trip via Load()
        var manager2 = new ConfigManager(ConfigPath);
        var loaded = manager2.Load();
        Assert.Equal(5, loaded.Audio.InputDeviceIndex);
        Assert.True(loaded.Audio.UseLoopback);
        Assert.Equal("my-key", loaded.Llm.ApiKey);
        Assert.Equal("gpt-4", loaded.Llm.Model);
    }

    [Fact]
    public void Load_PrefersExplicitEnableLoopbackListen_OverLegacyUseLoopback()
    {
        var json = """
        {
            "audio": {
                "use_loopback": true,
                "enable_loopback_listen": false
            }
        }
        """;
        File.WriteAllText(ConfigPath, json);

        var manager = new ConfigManager(ConfigPath);
        var config = manager.Load();

        Assert.True(config.Audio.UseLoopback);
        Assert.False(config.Audio.EnableLoopbackListen);
    }

    [Fact]
    public void GenerateTemplate_CreatesTemplateFile()
    {
        var manager = new ConfigManager(ConfigPath);
        var templatePath = Path.Combine(_testDir, "config.json.template");

        manager.GenerateTemplate(templatePath);

        Assert.True(File.Exists(templatePath));
        var content = File.ReadAllText(templatePath);
        Assert.Contains("<your-asr-api-key>", content);
        Assert.Contains("<your-llm-api-key>", content);
    }

    [Fact]
    public void Load_ReturnsDefaultsForMissingFields()
    {
        // Test partial config — missing fields should get defaults
        var json = """{ "audio": { "input_device_index": 3 } }""";
        File.WriteAllText(ConfigPath, json);

        var manager = new ConfigManager(ConfigPath);
        var config = manager.Load();

        Assert.Equal(3, config.Audio.InputDeviceIndex);
        Assert.Equal(2, config.Audio.VadAggressiveness); // default
        Assert.Equal("aliyun", config.Asr.Provider); // default
    }

    [Fact]
    public void Save_RoundTripsVtsActionMap()
    {
        var manager = new ConfigManager(ConfigPath);
        var config = new AppConfig();
        config.Vts.ActionMap["head_nod"] = "motion-hotkey";

        manager.Save(config);
        var loaded = manager.Load();

        Assert.Equal("motion-hotkey", loaded.Vts.ActionMap["head_nod"]);
        Assert.Contains("action_map", File.ReadAllText(ConfigPath));
    }

    public void Dispose()
    {
        try
        {
            if (Directory.Exists(_testDir))
                Directory.Delete(_testDir, true);
        }
        catch { }
    }
}
