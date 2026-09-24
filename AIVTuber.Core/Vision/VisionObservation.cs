namespace AIVTuber.Core.Vision;

/// <summary>
/// Vision observation snapshot contract (plan section 4.4). Model-generated descriptions are
/// untrusted observations, never objective truth and never instructions.
/// </summary>
public sealed record VisionObservation
{
    public required string FrameId { get; init; }
    public required string SourceWindowId { get; init; }
    public required long CaptureEpoch { get; init; }
    /// <summary>Monotonic ms when the frame was captured (screen time, not model time).</summary>
    public required long CapturedAt { get; init; }
    /// <summary>Monotonic ms when the model returned this observation.</summary>
    public required long ObservedAt { get; init; }
    /// <summary>Monotonic ms after which this observation must not be used.</summary>
    public required long ValidUntil { get; init; }

    public string SceneSummary { get; init; } = string.Empty;
    public IReadOnlyList<string> VisibleFacts { get; init; } = [];
    public IReadOnlyList<string> UncertainFacts { get; init; } = [];
    public IReadOnlyList<string> SalientChanges { get; init; } = [];

    /// <summary>Frame/region reference; raw pixels are not retained by design.</summary>
    public string Evidence { get; init; } = string.Empty;

    public string Model { get; init; } = string.Empty;
    public string Provider { get; init; } = string.Empty;
    public string RequestId { get; init; } = string.Empty;
}

/// <summary>A stored observation plus staleness marking.</summary>
public sealed record VisionSnapshot(VisionObservation Observation, bool Stale)
{
    /// <summary>True when the observation is still within its validity window.</summary>
    public bool IsValid(long nowMs) => !Stale && nowMs <= Observation.ValidUntil;
}

/// <summary>Result of an on-demand "look now" request.</summary>
public enum VisionOnDemandStatus
{
    Ok,
    /// <summary>Timed out — be honest: "didn't get a fresh look", never answer from stale state.</summary>
    Timeout,
    CaptureUnavailable,
    NotEnabled,
    BudgetExhausted,
    Failed,
}

public sealed record VisionOnDemandResult(VisionOnDemandStatus Status, VisionObservation? Observation, string Detail = "")
{
    public static VisionOnDemandResult Ok(VisionObservation o) => new(VisionOnDemandStatus.Ok, o);
    public static VisionOnDemandResult Fail(VisionOnDemandStatus s, string detail) => new(s, null, detail);
}
