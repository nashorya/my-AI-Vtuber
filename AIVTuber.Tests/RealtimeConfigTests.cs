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

    [Fact]
    public void RT02_RT06_Vision_keys_round_trip_through_save_load()
    {
        var path = TempConfigPath();
        File.WriteAllText(path, """
        {
          "realtime": {
            "streaming_asr_enabled": true,
            "buffer_capacity_ms": 2500,
            "preroll_ms": 700,
            "idle_disconnect_ms": 15000,
            "send_packet_ms": 160,
            "turn_manager_v2_enabled": true
          },
          "tts": {
            "transport": "bidi",
            "bidi_host": "wss://example.test/ws/v1/t2a_v2_bidi",
            "bidi_cancel_ack_timeout_ms": 1500,
            "bidi_max_backlog_seconds": 25,
            "bidi_seconds_per_char_estimate": 0.08,
            "bidi_keep_alive_interval_ms": 5000
          },
          "asr": {
            "provider": "tencent_realtime",
            "secret_id": "sid-1",
            "resource_id": "res-1",
            "hotwords": ["可缇"]
          },
          "vision": {
            "enabled": true,
            "min_request_interval_ms": 1000,
            "max_inflight": 1,
            "max_requests_per_hour": 120
          }
        }
        """);
        var manager = new ConfigManager(path);
        var loaded = manager.Load();
        Assert.True(loaded.Realtime.StreamingAsrEnabled);
        Assert.Equal(2500, loaded.Realtime.BufferCapacityMs);
        Assert.Equal(700, loaded.Realtime.PrerollMs);
        Assert.Equal(15000, loaded.Realtime.IdleDisconnectMs);
        Assert.Equal(160, loaded.Realtime.SendPacketMs);
        Assert.True(loaded.Realtime.TurnManagerV2Enabled);
        Assert.Equal("bidi", loaded.Tts.Transport);
        Assert.Equal("wss://example.test/ws/v1/t2a_v2_bidi", loaded.Tts.BidiHost);
        Assert.Equal(1500, loaded.Tts.BidiCancelAckTimeoutMs);
        Assert.Equal(25, loaded.Tts.BidiMaxBacklogSeconds);
        Assert.Equal(0.08, loaded.Tts.BidiSecondsPerCharEstimate);
        Assert.Equal(5000, loaded.Tts.BidiKeepAliveIntervalMs);
        Assert.Equal("tencent_realtime", loaded.Asr.Provider);
        Assert.Equal("sid-1", loaded.Asr.SecretId);
        Assert.Equal("res-1", loaded.Asr.ResourceId);
        Assert.Equal(["可缇"], loaded.Asr.Hotwords);
        Assert.True(loaded.Vision.Enabled);
        Assert.Equal(1000, loaded.Vision.MinRequestIntervalMs);
        Assert.Equal(120, loaded.Vision.MaxRequestsPerHour);

        // Round-trip: saving and reloading keeps the explicit values (no migration drift).
        manager.Save(loaded);
        var second = manager.Load();
        Assert.Equal(2500, second.Realtime.BufferCapacityMs);
        Assert.Equal(0.08, second.Tts.BidiSecondsPerCharEstimate);
        Assert.Equal("sid-1", second.Asr.SecretId);
        Assert.Equal(1000, second.Vision.MinRequestIntervalMs);
    }

    [Fact]
    public void Template_lists_every_realtime_tts_asr_vision_key_and_stays_legacy_off()
    {
        var templatePath = Path.Combine(BaseDirectoryOrRepoRoot(), "config.json.template");
        using var doc = System.Text.Json.JsonDocument.Parse(File.ReadAllText(templatePath));
        var root = doc.RootElement;

        // realtime: RT-02 keys present, defaults legacy/off.
        var realtime = root.GetProperty("realtime");
        foreach (var key in new[]
        {
            "schema_version", "inference_mode", "streaming_asr_enabled", "buffer_capacity_ms",
            "preroll_ms", "idle_disconnect_ms", "send_packet_ms", "turn_manager_v2_enabled",
            "speculative_generation_enabled", "trace_enabled",
        })
            Assert.True(realtime.TryGetProperty(key, out _), $"template realtime missing '{key}'");
        Assert.Equal("legacy", realtime.GetProperty("inference_mode").GetString());
        Assert.False(realtime.GetProperty("streaming_asr_enabled").GetBoolean());
        Assert.False(realtime.GetProperty("turn_manager_v2_enabled").GetBoolean());
        Assert.False(realtime.GetProperty("speculative_generation_enabled").GetBoolean());
        Assert.False(realtime.GetProperty("trace_enabled").GetBoolean());

        // tts: RT-01/RT-06 keys present, default transport legacy.
        var tts = root.GetProperty("tts");
        foreach (var key in new[]
        {
            "transport", "bidi_host", "bidi_cancel_ack_timeout_ms", "bidi_max_backlog_seconds",
            "bidi_seconds_per_char_estimate", "bidi_keep_alive_interval_ms",
        })
            Assert.True(tts.TryGetProperty(key, out _), $"template tts missing '{key}'");
        Assert.Equal("legacy", tts.GetProperty("transport").GetString());

        // asr: RT-02/03 keys present.
        var asr = root.GetProperty("asr");
        foreach (var key in new[] { "secret_id", "resource_id", "hotwords", "api_keys" })
            Assert.True(asr.TryGetProperty(key, out _), $"template asr missing '{key}'");

        // vision: fully off by default with the VIS-02 budget keys listed.
        var vision = root.GetProperty("vision");
        foreach (var key in new[]
        {
            "enabled", "provider", "model", "api_keys", "capture_interval_ms", "min_request_interval_ms",
            "max_inflight", "pending_policy", "save_raw_frames", "max_requests_per_hour", "on_demand_timeout_ms",
        })
            Assert.True(vision.TryGetProperty(key, out _), $"template vision missing '{key}'");
        Assert.False(vision.GetProperty("enabled").GetBoolean());
    }

    private static string BaseDirectoryOrRepoRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !File.Exists(Path.Combine(dir.FullName, "AIVTuber.slnx")))
            dir = dir.Parent;
        return dir?.FullName ?? AppContext.BaseDirectory;
    }
}
