namespace AIVTuber.Core.Vision;

/// <summary>
/// VIS-01 capture orchestration. Owns the confirmed target, validates window identity on every
/// tick (handle reuse / process / title / size changes bump <see cref="WindowTarget.CaptureEpoch"/>),
/// exposes availability states, performs hash-throttled change detection and keeps at most
/// <see cref="VisionConfig.RawFrameCacheCapacity"/> raw frames in memory. Raw frames are never
/// written to disk and never logged, regardless of <see cref="VisionConfig.SaveRawFrames"/>
/// staying false (the flag exists for explicit operator-requested diagnostics only).
/// </summary>
public sealed class WindowCaptureService : IDisposable
{
    private readonly IWindowCaptureSource _source;
    private readonly IVisionClock _clock;
    private readonly VisionConfig _config;
    private readonly object _sync = new();
    private readonly Queue<CapturedFrame> _rawCache;

    private WindowTarget? _target;
    private ulong _lastDeliveredHash;
    private bool _hasLastHash;
    private bool _paused;
    private int _cacheCapacity;

    /// <summary>Raised when the capture state becomes/recoveres from unavailable.</summary>
    public event Action<CaptureStatus>? AvailabilityChanged;

    /// <summary>Raised for every successfully captured changed frame (bounded by cache capacity).</summary>
    public event Action<CapturedFrame>? FrameCaptured;

    public CaptureStatus LastStatus { get; private set; } = CaptureStatus.WindowClosed;
    /// <summary>Monotonic ms of the last completed local change check, changed or not.</summary>
    public long LastCaptureCheckedAt { get; private set; } = long.MinValue;

    public WindowCaptureService(IWindowCaptureSource source, VisionConfig config, IVisionClock? clock = null)
    {
        _source = source;
        _config = config;
        _clock = clock ?? new StopwatchVisionClock();
        _cacheCapacity = Math.Max(1, config.RawFrameCacheCapacity);
        _rawCache = new Queue<CapturedFrame>(_cacheCapacity);
    }

    public WindowTarget? CurrentTarget
    {
        get { lock (_sync) return _target; }
    }

    /// <summary>Confirms a window for capture. Caller (UI) must have shown a preview and got
    /// explicit operator consent — this service refuses nothing here but is only fed
    /// operator-selected handles.</summary>
    public WindowTarget SelectWindow(long hwnd)
    {
        var identity = _source.ProbeWindow(hwnd)
            ?? throw new ArgumentException("window does not exist", nameof(hwnd));
        lock (_sync)
        {
            _target = new WindowTarget
            {
                SourceWindowId = $"win-{identity.ProcessName}-{identity.Hwnd}",
                Identity = identity,
            };
            _hasLastHash = false;
            _rawCache.Clear();
            return _target;
        }
    }

    public void Pause() { lock (_sync) _paused = true; }
    public void Resume() { lock (_sync) _paused = false; }

    /// <summary>One capture tick. Called by the worker loop at
    /// <see cref="VisionConfig.CaptureIntervalMs"/>; never captures while paused.</summary>
    public CaptureResult Tick()
    {
        WindowTarget? target;
        lock (_sync)
        {
            if (_paused || _target is null)
            {
                SetStatus(CaptureStatus.WindowClosed);
                return CaptureResult.Fail(CaptureStatus.WindowClosed, "paused or no target");
            }
            target = _target;
        }

        // Identity validation happens inside the source's Capture as well; probe first so we can
        // bump the epoch before any frame is produced for the old identity.
        var current = _source.ProbeWindow(target.Identity.Hwnd);
        if (current is null)
        {
            SetStatus(CaptureStatus.WindowClosed);
            return CaptureResult.Fail(CaptureStatus.WindowClosed, "window is gone");
        }
        lock (_sync)
        {
            if (!ReferenceEquals(target, _target)) return CaptureResult.Fail(CaptureStatus.IdentityChanged, "target switched");
            if (target.ApplyIdentityChange(current))
            {
                // Title/size/process change or handle reuse: everything captured before is void.
                _hasLastHash = false;
                _rawCache.Clear();
            }
        }

        var result = _source.Capture(target, _config);
        LastCaptureCheckedAt = _clock.NowMs;
        if (result.Status != CaptureStatus.Ok || result.Frame is null)
        {
            SetStatus(result.Status);
            return result;
        }

        var frame = result.Frame;
        frame = new CapturedFrame
        {
            FrameId = frame.FrameId,
            SourceWindowId = frame.SourceWindowId,
            CaptureEpoch = frame.CaptureEpoch,
            CapturedAtMs = _clock.NowMs,
            Jpeg = frame.Jpeg,
            ContentHash = frame.ContentHash,
            ChangedSinceLast = !_hasLastHash || frame.ContentHash != _lastDeliveredHash,
        };

        SetStatus(CaptureStatus.Ok);
        if (_config.HashThrottle && !frame.ChangedSinceLast)
        {
            // Same hash: throttle signal only. Do NOT emit a frame and do NOT treat the
            // previous observation as freshly confirmed.
            return CaptureResult.Ok(frame);
        }

        lock (_sync)
        {
            if (!ReferenceEquals(target, _target) || target.CaptureEpoch != frame.CaptureEpoch)
                return CaptureResult.Fail(CaptureStatus.IdentityChanged, "target changed during capture");
            _lastDeliveredHash = frame.ContentHash;
            _hasLastHash = true;
            _rawCache.Enqueue(frame);
            while (_rawCache.Count > _cacheCapacity)
                _rawCache.Dequeue();
        }
        FrameCaptured?.Invoke(frame);
        return CaptureResult.Ok(frame);
    }

    /// <summary>Fresh capture for on-demand "look now" requests. Bypasses the hash throttle but
    /// still runs every identity/availability check.</summary>
    public CaptureResult CaptureFresh()
    {
        var hadHash = _config.HashThrottle;
        CaptureResult result;
        lock (_sync)
        {
            // Temporarily disable throttling for this capture.
            _config.HashThrottle = false;
        }
        try
        {
            result = Tick();
        }
        finally
        {
            lock (_sync) _config.HashThrottle = hadHash;
        }
        return result;
    }

    public int CachedRawFrameCount { get { lock (_sync) return _rawCache.Count; } }

    private void SetStatus(CaptureStatus status)
    {
        if (status == LastStatus) return;
        LastStatus = status;
        AvailabilityChanged?.Invoke(status);
    }

    public void Dispose() => _source.Dispose();
}
