using System.Globalization;

namespace AIVTuber.Core.Vision;

/// <summary>Clock abstraction so timing behaviour (intervals, TTL, budget window) is testable.</summary>
public interface IVisionClock
{
    long NowMs { get; }
}

public sealed class StopwatchVisionClock : IVisionClock
{
    private static long Ms => System.Diagnostics.Stopwatch.GetTimestamp() * 1000
        / System.Diagnostics.Stopwatch.Frequency;
    public long NowMs => Ms;
}

/// <summary>Identity of a captured window at probe time. HWND alone is not identity:
/// handles get reused and windows get destroyed.</summary>
public sealed record WindowIdentity(
    long Hwnd,
    int ProcessId,
    string ProcessName,
    string WindowTitle,
    int Width,
    int Height)
{
    /// <summary>Identity change that forces a CaptureEpoch bump and re-confirmation.</summary>
    public bool SameWindowAs(WindowIdentity other) =>
        Hwnd == other.Hwnd && ProcessId == other.ProcessId &&
        string.Equals(WindowTitle, other.WindowTitle, StringComparison.OrdinalIgnoreCase) &&
        Width == other.Width && Height == other.Height;
}

/// <summary>A confirmed capture target. Epoch increments whenever the window identity changes
/// (title/size/process/handle reuse); results tagged with an old epoch are void.</summary>
public sealed class WindowTarget
{
    public required string SourceWindowId { get; init; }
    public required WindowIdentity Identity { get; init; }
    public long CaptureEpoch { get; private set; } = 1;

    /// <summary>Returns true when the epoch changed.</summary>
    public bool ApplyIdentityChange(WindowIdentity current)
    {
        if (Identity.SameWindowAs(current)) return false;
        CaptureEpoch++;
        return true;
    }
}

public enum CaptureStatus
{
    Ok,
    WindowClosed,
    Minimized,
    BlackFrame,
    PermissionFailure,
    DeviceLost,
    IdentityChanged,
}

/// <summary>A processed frame ready (and only ready) for upload. Raw pixels are not kept.</summary>
public sealed class CapturedFrame
{
    public required string FrameId { get; init; }
    public required string SourceWindowId { get; init; }
    public required long CaptureEpoch { get; init; }
    public required long CapturedAtMs { get; init; }
    /// <summary>JPEG bytes, already ROI-cropped, masked, downscaled and size-capped.</summary>
    public required byte[] Jpeg { get; init; }
    /// <summary>64-bit content hash. Throttle signal only — "same hash" does NOT prove
    /// "facts unchanged" (HUD/subtitles churn inside stable regions).</summary>
    public required ulong ContentHash { get; init; }
    public required bool ChangedSinceLast { get; init; }
    public string Evidence => $"frame:{FrameId}";
}

public sealed record CaptureResult(CaptureStatus Status, CapturedFrame? Frame, string? Detail = null)
{
    public static CaptureResult Ok(CapturedFrame frame) => new(CaptureStatus.Ok, frame);
    public static CaptureResult Fail(CaptureStatus status, string detail) => new(status, null, detail);
}

/// <summary>
/// Platform capture abstraction (VIS-01). Implementations capture ONLY the configured window —
/// never the whole desktop and never a substitute window. The production implementation is
/// <see cref="GdiWindowCaptureSource"/> (per-HWND GDI capture); Windows Graphics Capture
/// (CreateForWindow interop, plan [D10]) is the intended upgrade path behind this same interface.
/// </summary>
public interface IWindowCaptureSource : IDisposable
{
    /// <summary>Reads current window identity, or null when the handle is gone.</summary>
    WindowIdentity? ProbeWindow(long hwnd);

    /// <summary>Captures the window now. Returns a failure status for minimized / closed /
    /// permission failures / black frames instead of substituting another image.</summary>
    CaptureResult Capture(WindowTarget target, VisionConfig config);
}
