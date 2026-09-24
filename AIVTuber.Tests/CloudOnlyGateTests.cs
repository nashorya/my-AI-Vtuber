using AIVTuber.Core.Config;
using AIVTuber.Core.Pipeline;
using AIVTuber.Core.Runtime;
using System.Reflection;

namespace AIVTuber.Tests;

public sealed class CloudOnlyGateTests
{
    private static RealtimeConfig CloudOnly() => new() { InferenceMode = "cloud_only" };
    private static RealtimeConfig Legacy() => new();

    [Fact]
    public void Local_embedding_is_forbidden_in_cloud_only_mode()
    {
        Assert.False(CloudOnlyGate.ShouldLoadLocalEmbedding(CloudOnly()));
        Assert.True(CloudOnlyGate.ShouldLoadLocalEmbedding(Legacy()));
    }

    [Fact]
    public void Python_asr_sidecar_is_forbidden_in_cloud_only_mode()
    {
        Assert.False(CloudOnlyGate.ShouldStartAsrSidecar(CloudOnly(), "local"));
        Assert.False(CloudOnlyGate.ShouldStartAsrSidecar(CloudOnly(), "aliyun"));
        // Legacy mode keeps the existing sidecar behaviour for local provider.
        Assert.True(CloudOnlyGate.ShouldStartAsrSidecar(Legacy(), "local"));
        Assert.False(CloudOnlyGate.ShouldStartAsrSidecar(Legacy(), "aliyun"));
    }

    [Fact]
    public void Validate_reports_local_asr_violation_in_cloud_only_mode()
    {
        Assert.Empty(CloudOnlyGate.Validate(Legacy(), new AsrConfig { Provider = "local" }));
        Assert.Empty(CloudOnlyGate.Validate(CloudOnly(), new AsrConfig { Provider = "aliyun" }));
        var violations = CloudOnlyGate.Validate(CloudOnly(), new AsrConfig { Provider = "Local" });
        var violation = Assert.Single(violations);
        Assert.Contains("cloud_only", violation);
        Assert.Contains("sidecar", violation);
    }

    [Fact]
    public async Task BotRuntime_cloud_only_skips_local_embedding_with_visible_degradation()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rt-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(baseDir, "models", "bge-small-zh"));
        await File.WriteAllTextAsync(Path.Combine(baseDir, "models", "bge-small-zh", "model.onnx"), "fake");
        await File.WriteAllTextAsync(Path.Combine(baseDir, "models", "bge-small-zh", "vocab.txt"), "fake");

        var config = new AppConfig { Realtime = new RealtimeConfig { InferenceMode = "cloud_only" } };
        await using var runtime = new BotRuntime(config, baseDir);
        await runtime.InitMemoryAsync();

        Assert.True(runtime.MemoryVectorSearchDegraded);
        // No EmbeddingEngine may be constructed in cloud-only mode.
        var embedding = typeof(BotRuntime).GetField("_embedding", BindingFlags.Instance | BindingFlags.NonPublic)!
            .GetValue(runtime);
        Assert.Null(embedding);
        // The database is neither deleted nor wiped by the gate.
        Assert.True(File.Exists(Path.Combine(baseDir, "memory.db")));
    }

    [Fact]
    public async Task BotRuntime_cloud_only_blocks_asr_sidecar_startup()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rt-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(baseDir);
        var config = new AppConfig
        {
            Asr = new AsrConfig { Provider = "local" },
            Realtime = new RealtimeConfig { InferenceMode = "cloud_only" },
        };
        await using var runtime = new BotRuntime(config, baseDir);

        // Pretend InitPipeline selected the local client; the gate must fire before any
        // process spawn, so the call returns quickly without touching Python.
        typeof(BotRuntime).GetField("_asr", BindingFlags.Instance | BindingFlags.NonPublic)!
            .SetValue(runtime, new LocalAsrClient("http://localhost:8765"));

        var errors = new List<string>();
        runtime.PipelineError += (_, msg) => errors.Add(msg);

        var sw = System.Diagnostics.Stopwatch.StartNew();
        await runtime.StartLocalAsrServerAsync();
        sw.Stop();

        Assert.True(runtime.CloudOnlySidecarSkipped);
        Assert.False(runtime.LocalAsrReachable);
        Assert.Contains(errors, m => m.Contains("cloud_only"));
        // Early return: far below any realistic sidecar health-wait (60s budget).
        Assert.True(sw.Elapsed < TimeSpan.FromSeconds(5), $"gate took {sw.Elapsed}");
    }

    [Fact]
    public async Task BotRuntime_legacy_mode_still_attempts_embedding_from_model_dir()
    {
        var baseDir = Path.Combine(Path.GetTempPath(), $"rt-gate-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(baseDir, "models", "bge-small-zh"));
        // Garbage model content: load fails and falls back, but the gate itself must not be
        // the reason (degraded flag stays false → legacy path unchanged).
        await File.WriteAllTextAsync(Path.Combine(baseDir, "models", "bge-small-zh", "model.onnx"), "fake");
        await File.WriteAllTextAsync(Path.Combine(baseDir, "models", "bge-small-zh", "vocab.txt"), "fake");

        await using var runtime = new BotRuntime(new AppConfig(), baseDir);
        await runtime.InitMemoryAsync();
        Assert.False(runtime.MemoryVectorSearchDegraded);
    }
}
