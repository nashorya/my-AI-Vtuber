using AIVTuber.Core.Config;

namespace AIVTuber.Tests;

public sealed class RealtimeConfigTests
{
    private static string TempConfigPath()
        => Path.Combine(Path.GetTempPath(), $"cfg-{Guid.NewGuid():N}.json");

    [Fact]
    public void Defaults_are_all_legacy_and_off()
    {
        var rt = new AppConfig().Realtime;
        Assert.Equal(1, rt.SchemaVersion);
        Assert.Equal("legacy", rt.InferenceMode);
        Assert.False(rt.IsCloudOnly);
        Assert.False(rt.StreamingAsrEnabled);
        Assert.False(rt.TurnManagerV2Enabled);
        Assert.False(rt.SpeculativeGenerationEnabled);
        Assert.False(rt.TraceEnabled);
    }

    [Fact]
    public void Config_without_realtime_section_gets_safe_defaults()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
        {
          "audio": { "input_device_index": 0 },
          "asr": { "provider": "aliyun" }
        }
        """);
        var config = new ConfigManager(path).Load();
        Assert.NotNull(config.Realtime);
        Assert.False(config.Realtime.TraceEnabled);
        Assert.False(config.Realtime.IsCloudOnly);
        Assert.Equal(RealtimeConfig.CurrentSchemaVersion, config.Realtime.SchemaVersion);
    }

    [Fact]
    public void Migration_is_idempotent_and_preserves_explicit_values()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
        {
          "realtime": {
            "inference_mode": "cloud_only",
            "trace_enabled": true
          }
        }
        """);
        var manager = new ConfigManager(path);
        var first = manager.Load();
        Assert.True(first.Realtime.IsCloudOnly);
        Assert.True(first.Realtime.TraceEnabled);
        // Missing schema_version counts as pre-versioning v0 → normalized to 1, values kept.
        Assert.Equal(1, first.Realtime.SchemaVersion);
        // Saving then reloading (double migration) must be stable.
        manager.Save(first);
        var second = manager.Load();
        Assert.True(second.Realtime.IsCloudOnly);
        Assert.True(second.Realtime.TraceEnabled);
        Assert.Equal(1, second.Realtime.SchemaVersion);
    }

    [Fact]
    public void Future_schema_version_is_preserved_not_downgraded()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
        {
          "realtime": { "schema_version": 99, "trace_enabled": false }
        }
        """);
        var config = new ConfigManager(path).Load();
        Assert.Equal(99, config.Realtime.SchemaVersion);
        Assert.False(config.Realtime.TraceEnabled);
    }

    [Fact]
    public void Existing_sections_keep_their_semantics_after_migration()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
        {
          "audio": { "use_loopback": true },
          "asr": { "provider": "minimax", "streaming": true }
        }
        """);
        var config = new ConfigManager(path).Load();
        // Legacy use_loopback migration still applies alongside the realtime migration.
        Assert.True(config.Audio.EnableLoopbackListen);
        Assert.True(config.Asr.Streaming);
        Assert.Equal("minimax", config.Asr.Provider);
    }
}
