namespace AIVTuber.Core.Avatar;

/// <summary>
/// Whole-image pose switcher (v0.6). No layering / rotation — each pose is a full standee.
/// Cross-fade only; motion layer still applies to the composite.
/// </summary>
public sealed class PoseController
{
    public const string Front = "front";

    private PosesConfig _cfg;
    private readonly Random _rng;
    private readonly HashSet<string> _available;

    private string _current;
    private string? _fadeFrom;
    private double _fadeElapsedMs;
    private double _fadeDurationMs;
    private bool _fading;

    private double _idleCooldownMs;
    private double _nonFrontHoldMs;
    private bool _holdingIdlePose;
    private bool _listeningPoseActive;

    public PoseController(PosesConfig? config, IEnumerable<string>? availableIds = null, Random? rng = null)
    {
        _cfg = config ?? new PosesConfig();
        _rng = rng ?? Random.Shared;
        _available = availableIds is null
            ? new HashSet<string>(_cfg.List.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(availableIds, StringComparer.OrdinalIgnoreCase);
        _current = ResolveDefault();
        ScheduleIdleCooldown();
    }

    public string CurrentId => _current;

    public bool FullExpression =>
        !_cfg.List.TryGetValue(_current, out var def) || def.FullExpression;

    public bool HasPoses => _available.Count > 0 && _cfg.List.Count > 0;

    public void UpdateConfig(PosesConfig config, IEnumerable<string>? availableIds = null)
    {
        _cfg = config ?? new PosesConfig();
        _available.Clear();
        foreach (var id in availableIds ?? _cfg.List.Keys)
            _available.Add(id);
        if (!_available.Contains(_current))
            _current = ResolveDefault();
    }

    /// <summary>Manual / listening / idle request. Starts a cross-fade when the id changes.</summary>
    public void SetPose(string poseId, bool fromListening = false)
    {
        if (string.IsNullOrWhiteSpace(poseId)) return;
        if (!_available.Contains(poseId) && !_cfg.List.ContainsKey(poseId))
            return;

        _listeningPoseActive = fromListening && !IsFrontId(poseId);

        if (string.Equals(_current, poseId, StringComparison.OrdinalIgnoreCase) && !_fading)
            return;

        _fadeFrom = _current;
        _current = poseId;
        _fadeDurationMs = Math.Max(1, _cfg.Transition.Ms > 0 ? _cfg.Transition.Ms : 180);
        _fadeElapsedMs = 0;
        _fading = true;

        if (IsFrontId(poseId))
        {
            _holdingIdlePose = false;
            _nonFrontHoldMs = 0;
            _listeningPoseActive = false;
            ScheduleIdleCooldown();
        }
        else if (fromListening)
        {
            _holdingIdlePose = false;
            _nonFrontHoldMs = 0;
        }
        else
        {
            _holdingIdlePose = true;
            _nonFrontHoldMs = _rng.Next(2000, 4001);
        }
    }

    public PoseFrame Tick(double deltaMs, bool avatarSpeaking)
    {
        if (deltaMs < 0) deltaMs = 0;
        if (deltaMs > 250) deltaMs = 16.67;

        if (_fading)
        {
            _fadeElapsedMs += deltaMs;
            if (_fadeElapsedMs >= _fadeDurationMs)
            {
                _fading = false;
                _fadeFrom = null;
                _fadeElapsedMs = 0;
            }
        }

        // Listening pose stays until cleared; idle hold returns to front.
        if (_holdingIdlePose && !_listeningPoseActive && !IsFrontId(_current) && !_fading)
        {
            _nonFrontHoldMs -= deltaMs;
            if (_nonFrontHoldMs <= 0)
            {
                _holdingIdlePose = false;
                SetPose(Front);
            }
        }

        var idle = _cfg.Triggers.IdleRandom;
        if (idle.Enabled
            && IsFrontId(_current)
            && !_fading
            && !_listeningPoseActive
            && !avatarSpeaking)
        {
            _idleCooldownMs -= deltaMs;
            if (_idleCooldownMs <= 0)
            {
                var next = PickRandomNonFront();
                if (next is not null)
                    SetPose(next);
                else
                    ScheduleIdleCooldown();
            }
        }
        else if (!IsFrontId(_current) || avatarSpeaking || _listeningPoseActive)
        {
            // Don't burn idle timer while away from front / speaking / listening.
        }
        else
        {
            // Front but speaking ended mid-countdown — keep countdown
        }

        var fadeT = _fading
            ? (float)Math.Clamp(_fadeElapsedMs / Math.Max(1.0, _fadeDurationMs), 0, 1)
            : 1f;

        var fullExpr = FullExpression;
        return new PoseFrame(_current, fullExpr, _fading ? _fadeFrom : null, fadeT);
    }

    private string ResolveDefault()
    {
        var d = string.IsNullOrWhiteSpace(_cfg.Default) ? Front : _cfg.Default;
        if (_available.Contains(d) || _cfg.List.ContainsKey(d)) return d;
        if (_available.Count > 0) return _available.First();
        if (_cfg.List.Count > 0) return _cfg.List.Keys.First();
        return Front;
    }

    private void ScheduleIdleCooldown()
    {
        var iv = _cfg.Triggers.IdleRandom.IntervalMs;
        var lo = iv is { Length: > 0 } ? Math.Max(500, iv[0]) : 45000;
        var hi = iv is { Length: > 1 } ? Math.Max(lo, iv[1]) : Math.Max(lo, 90000);
        _idleCooldownMs = _rng.Next(lo, hi + 1);
    }

    private string? PickRandomNonFront()
    {
        var pool = _cfg.Triggers.IdleRandom.Poses;
        IEnumerable<string> candidates;
        if (pool is { Length: > 0 })
        {
            candidates = pool.Where(id =>
                !IsFrontId(id)
                && _cfg.List.ContainsKey(id)
                && (_available.Count == 0 || _available.Contains(id)));
        }
        else
        {
            // Default idle pool: head tilts only — never side_left / side_right.
            candidates = _cfg.List.Keys.Where(k =>
                (k.StartsWith("tilt_", StringComparison.OrdinalIgnoreCase))
                && (_available.Count == 0 || _available.Contains(k)));
        }

        var opts = candidates.ToList();
        if (opts.Count == 0) return null;
        return opts[_rng.Next(opts.Count)];
    }

    private static bool IsFrontId(string id) =>
        string.Equals(id, Front, StringComparison.OrdinalIgnoreCase);
}

/// <summary>Per-frame pose snapshot for the renderer.</summary>
public readonly record struct PoseFrame(
    string PoseId,
    bool FullExpression,
    string? FadeFromPoseId,
    float FadeT);
