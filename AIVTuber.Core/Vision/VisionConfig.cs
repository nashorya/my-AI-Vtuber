namespace AIVTuber.Core.Vision;

/// <summary>
/// Vision observation settings (VIS-01/VIS-02). Everything defaults to OFF —
/// <see cref="Enabled"/> must be set explicitly by the operator.
/// Experimental starting values per plan VIS-02; they are not model or vendor limits.
/// </summary>
public sealed class VisionConfig
{
    public bool Enabled { get; set; } = false;

    /// <summary>doubao (first implementation) | qwen (reserved slot, not implemented).</summary>
    public string Provider { get; set; } = "doubao";

    /// <summary>Provider-specific vision model name; empty = reject (never guess a "latest" model).</summary>
    public string Model { get; set; } = string.Empty;

    public string ApiKey { get; set; } = string.Empty;
    /// <summary>Keys keyed by <see cref="Provider"/> so switching keeps the previous secret.</summary>
    public Dictionary<string, string> ApiKeys { get; set; } = new();

    /// <summary>API host, e.g. https://ark.cn-beijing.volces.com/api/v3 for Doubao Ark.
    /// Region/account must be configured as a set; do not mix CN/international domains.</summary>
    public string BaseUrl { get; set; } = "https://ark.cn-beijing.volces.com/api/v3";

    // --- Capture (VIS-01) ---

    /// <summary>Local capture polling rate in ms (~1-2 fps experiment start).</summary>
    public int CaptureIntervalMs { get; set; } = 500;

    /// <summary>Rectangular region of interest inside the target window (window-relative pixels).
    /// Null = whole window. Use this to crop chat boxes / notification areas.</summary>
    public VisionRoiConfig? Roi { get; set; }

    /// <summary>Opaque rectangles (window-relative) masked to solid black before upload.</summary>
    public List<VisionRoiConfig> Masks { get; set; } = [];

    /// <summary>Max long-edge pixels of the uploaded image.</summary>
    public int MaxLongEdge { get; set; } = 1280;

    /// <summary>JPEG encode quality (1-100).</summary>
    public int JpegQuality { get; set; } = 80;

    /// <summary>Hard cap on uploaded image bytes; frames above are re-encoded smaller / dropped.</summary>
    public int MaxUploadBytes { get; set; } = 1_500_000;

    /// <summary>Never persist raw frames to disk, never log base64. Kept configurable but the
    /// default and recommendation stay false.</summary>
    public bool SaveRawFrames { get; set; } = false;

    /// <summary>Bounded in-memory raw-frame cache. Delivery pipeline only ever holds
    /// latest-pending anyway; this is a defensive ceiling.</summary>
    public int RawFrameCacheCapacity { get; set; } = 2;

    // --- Background observation (VIS-02) ---

    /// <summary>Minimum spacing between two background VLM requests.</summary>
    public int MinRequestIntervalMs { get; set; } = 3000;

    /// <summary>Hard limit of concurrent background requests (plan: 1).</summary>
    public int MaxInflight { get; set; } = 1;

    /// <summary>pending_policy: only "latest_only" is honoured.</summary>
    public string PendingPolicy { get; set; } = "latest_only";

    /// <summary>How long an observation stays usable before expiring.</summary>
    public int SnapshotTtlMs { get; set; } = 15_000;

    /// <summary>Background VLM requests per rolling hour; 0 = unlimited.</summary>
    public int MaxRequestsPerHour { get; set; } = 600;

    /// <summary>Independent timeout for explicit "look at this now" requests.</summary>
    public int OnDemandTimeoutMs { get; set; } = 12_000;

    /// <summary>Skips VLM request while the frame hash is unchanged. Throttle signal only —
    /// HUD/subtitle churn means "same hash" never proves "facts unchanged".</summary>
    public bool HashThrottle { get; set; } = true;

    internal string VendorId => Provider.ToLowerInvariant() switch
    {
        "doubao" or "ark" => "doubao",
        "qwen" or "dashscope-vision" => "qwen",
        _ => Provider.ToLowerInvariant(),
    };

    public void RememberActiveKey()
    {
        if (string.IsNullOrWhiteSpace(ApiKey)) return;
        ApiKeys[VendorId] = ApiKey;
    }

    public bool TryActivateStoredKey(out string key)
    {
        key = ApiKeys.GetValueOrDefault(VendorId, string.Empty);
        if (string.IsNullOrWhiteSpace(key)) return false;
        ApiKey = key;
        return true;
    }
}

public sealed class VisionRoiConfig
{
    public int X { get; set; }
    public int Y { get; set; }
    public int Width { get; set; }
    public int Height { get; set; }
}
