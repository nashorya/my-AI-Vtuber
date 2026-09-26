using AIVTuber.Core.Config;

namespace AIVTuber.Core.Runtime;

/// <summary>
/// Cloud-only inference gate (RT-00). When realtime.inference_mode = "cloud_only" the app
/// must not launch the Python ASR sidecar, download speech/vision weights, or load local
/// inference models. This gate is the single source of truth so runtime code and tests
/// agree on what is forbidden in that mode. Legacy mode keeps all existing behaviour.
/// </summary>
public static class CloudOnlyGate
{
    /// <summary>Local vector embedding (bge-small-zh ONNX) must not load in cloud-only mode;
    /// memory retrieval degrades visibly to non-vector search instead.</summary>
    public static bool ShouldLoadLocalEmbedding(RealtimeConfig realtime) => !realtime.IsCloudOnly;

    /// <summary>Python ASR sidecar may only start in legacy mode for the local provider;
    /// cloud-only forbids it for every provider.</summary>
    public static bool ShouldStartAsrSidecar(RealtimeConfig realtime, string asrProvider) =>
        !realtime.IsCloudOnly && IsLocalAsrProvider(asrProvider);

    public static bool IsLocalAsrProvider(string asrProvider) =>
        string.Equals(asrProvider?.Trim(), "local", StringComparison.OrdinalIgnoreCase);

    /// <summary>Visible violations for the current config (empty = OK). Used at startup and
    /// by tests; each string is operator-readable, no secrets included.</summary>
    public static IReadOnlyList<string> Validate(RealtimeConfig realtime, AsrConfig asr)
    {
        var violations = new List<string>();
        if (realtime.IsCloudOnly)
        {
            if (IsLocalAsrProvider(asr.Provider))
                violations.Add(
                    "cloud_only 模式禁止本地 ASR sidecar：asr.provider=local 需改为云端 provider（如 aliyun/minimax）");
        }
        return violations;
    }
}
