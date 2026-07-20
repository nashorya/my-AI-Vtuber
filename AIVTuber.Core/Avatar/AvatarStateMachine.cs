using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// Pure avatar body/sticker state machine (no UI).
/// Priority: special(sleep) &gt; emotion(hold) &gt; blink(short) &gt; mouth(RMS) &gt; neutral.
/// Stickers are an independent overlay channel (max one at a time).
/// </summary>
public sealed class AvatarStateMachine
{
    public const string Neutral = "neutral";
    public const string Blink = "blink";
    public static readonly TimeSpan DefaultEmotionHold = TimeSpan.FromMilliseconds(1500);

    private readonly AvatarPackConfig _pack;
    private readonly HashSet<string> _available;
    private readonly Random _rng;
    private readonly object _lock = new();

    // RMS snapshot (written from audio thread via OnRms, read on Tick)
    private float _latestRms;
    private string _mouthState = Neutral;
    private string _heldMouthState = Neutral;
    private double _mouthHoldRemainingMs;

    // Emotion
    private string? _emotionState;
    private double _emotionRemainingMs;
    private MotionOverrideDef? _emotionOverride;

    // Special / idle (sleep)
    private string? _specialState;

    // Blink schedule
    private double _blinkCooldownMs;
    private double _blinkRemainingMs;
    private bool _pendingDoubleBlink;
    // v0.2: scales the blink interval (e.g. 0.7 while listening → blinks more often).
    private double _blinkIntervalScale = 1.0;

    // Fade
    private string _currentState = Neutral;
    private string? _fadeFromState;
    private double _fadeElapsedMs;
    private double _fadeDurationMs;
    private bool _fading;

    // Sticker channel
    private ActiveSticker? _sticker;

    public AvatarStateMachine(
        AvatarPackConfig pack,
        IEnumerable<string>? availableStates = null,
        Random? rng = null)
    {
        _pack = pack;
        _available = availableStates is null
            ? new HashSet<string>(pack.States.Keys, StringComparer.OrdinalIgnoreCase)
            : new HashSet<string>(availableStates, StringComparer.OrdinalIgnoreCase);
        _rng = rng ?? Random.Shared;

        if (!_available.Contains(Neutral) && pack.States.ContainsKey(Neutral))
            _available.Add(Neutral);

        ScheduleNextBlink(immediate: true);
    }

    public string CurrentState
    {
        get { lock (_lock) return _currentState; }
    }

    public MotionOverrideDef? ActiveMotionOverride
    {
        get { lock (_lock) return _emotionOverride; }
    }

    public bool EmotionActive
    {
        get { lock (_lock) return _emotionState is not null && _emotionRemainingMs > 0; }
    }

    /// <summary>Thread-safe RMS ingest.</summary>
    public void OnRms(float rms) => Volatile.Write(ref _latestRms, Math.Clamp(rms, 0f, 2f));

    /// <summary>Scales the random blink interval (v0.2). 1.0 = default; 0.7 = blink more often
    /// (listening pose). Affects the NEXT scheduled blink, not the current countdown.</summary>
    public void SetBlinkIntervalScale(double scale)
    {
        var clamped = Math.Clamp(scale, 0.1, 5.0);
        lock (_lock) _blinkIntervalScale = clamped;
    }

    public void SetEmotion(string emotion, TimeSpan? hold = null)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return;
        var state = emotion.Trim();
        lock (_lock)
        {
            if (!IsKnownState(state))
            {
                DebugLog.Write($"[Avatar] unknown emotion '{state}' → neutral");
                state = Neutral;
            }

            if (string.Equals(state, Neutral, StringComparison.OrdinalIgnoreCase))
            {
                ClearEmotion_NoLock();
                return;
            }

            _emotionState = state;
            _emotionRemainingMs = (hold ?? DefaultEmotionHold).TotalMilliseconds;
            _pack.States.TryGetValue(state, out var def);
            _emotionOverride = def?.MotionOverride;
        }
    }

    public void SetIdleState(string state)
    {
        lock (_lock)
        {
            if (string.IsNullOrWhiteSpace(state) ||
                string.Equals(state, Neutral, StringComparison.OrdinalIgnoreCase))
            {
                _specialState = null;
                return;
            }

            if (!IsKnownState(state))
            {
                DebugLog.Write($"[Avatar] unknown idle state '{state}' ignored");
                return;
            }

            _specialState = state;
            // Sleep etc. clears transient emotion so special wins cleanly.
            ClearEmotion_NoLock();
        }
    }

    public void ShowSticker(string stickerId)
    {
        if (string.IsNullOrWhiteSpace(stickerId)) return;
        lock (_lock)
        {
            if (!_pack.Stickers.Items.TryGetValue(stickerId, out var item))
            {
                DebugLog.Write($"[Avatar] unknown sticker '{stickerId}'");
                return;
            }

            // New sticker replaces old.
            _sticker = new ActiveSticker(
                stickerId,
                item.Scale,
                _pack.Stickers.DefaultDurationMs,
                _pack.Stickers.Animation.In,
                _pack.Stickers.Animation.Out,
                elapsedMs: 0);
        }
    }

    /// <summary>Advance timers and resolve the body + sticker frame for this tick.</summary>
    public StateMachineFrame Tick(double deltaMs)
    {
        if (deltaMs < 0) deltaMs = 0;
        if (deltaMs > 100) deltaMs = 100;

        lock (_lock)
        {
            UpdateMouth_NoLock(deltaMs);
            UpdateEmotion_NoLock(deltaMs);
            UpdateBlink_NoLock(deltaMs);
            var desired = ResolveDesired_NoLock();
            UpdateTransition_NoLock(desired, deltaMs);
            var sticker = UpdateSticker_NoLock(deltaMs);

            float fadeT = 1f;
            string? fadeFrom = null;
            if (_fading && _fadeDurationMs > 0)
            {
                fadeT = (float)Math.Clamp(_fadeElapsedMs / _fadeDurationMs, 0, 1);
                fadeFrom = _fadeFromState;
                if (fadeT >= 1f)
                {
                    _fading = false;
                    _fadeFromState = null;
                    fadeFrom = null;
                    fadeT = 1f;
                }
            }

            return new StateMachineFrame(
                BodyState: _currentState,
                FadeFromState: fadeFrom,
                FadeT: fadeT,
                MotionOverride: _emotionOverride,
                EmotionActive: _emotionState is not null && _emotionRemainingMs > 0,
                MouthState: _heldMouthState,
                Rms: Volatile.Read(ref _latestRms),
                Sticker: sticker);
        }
    }

    private void UpdateMouth_NoLock(double deltaMs)
    {
        var rms = Volatile.Read(ref _latestRms);
        var target = ResolveMouthState(rms);
        _mouthState = target;

        var targetRank = MouthRank(target);
        var heldRank = MouthRank(_heldMouthState);

        if (targetRank > heldRank)
        {
            // Open wider immediately; restart hold so brief dips don't collapse the mouth.
            _heldMouthState = target;
            _mouthHoldRemainingMs = _pack.MouthSync.HoldMs;
        }
        else if (targetRank < heldRank)
        {
            // Debounce fall-back: keep higher mouth for hold_ms.
            if (_mouthHoldRemainingMs > 0)
                _mouthHoldRemainingMs -= deltaMs;
            if (_mouthHoldRemainingMs <= 0)
            {
                _heldMouthState = target;
                _mouthHoldRemainingMs = 0;
            }
        }
        else
        {
            // Same level — keep hold armed so a later dip still debounces.
            _heldMouthState = target;
            _mouthHoldRemainingMs = _pack.MouthSync.HoldMs;
        }
    }

    private string ResolveMouthState(float rms)
    {
        var levels = _pack.MouthSync.Levels;
        if (levels.Count == 0) return Neutral;

        foreach (var level in levels)
        {
            if (rms < level.Below)
                return IsKnownState(level.State) ? level.State : Neutral;
        }

        var last = levels[^1].State;
        return IsKnownState(last) ? last : Neutral;
    }

    private int MouthRank(string state)
    {
        var levels = _pack.MouthSync.Levels;
        for (var i = 0; i < levels.Count; i++)
        {
            if (string.Equals(levels[i].State, state, StringComparison.OrdinalIgnoreCase))
                return i;
        }
        return 0;
    }

    private void UpdateEmotion_NoLock(double deltaMs)
    {
        if (_emotionState is null) return;
        _emotionRemainingMs -= deltaMs;
        if (_emotionRemainingMs <= 0)
            ClearEmotion_NoLock();
    }

    private void ClearEmotion_NoLock()
    {
        _emotionState = null;
        _emotionRemainingMs = 0;
        _emotionOverride = null;
    }

    private void UpdateBlink_NoLock(double deltaMs)
    {
        if (_blinkRemainingMs > 0)
        {
            _blinkRemainingMs -= deltaMs;
            if (_blinkRemainingMs <= 0)
            {
                _blinkRemainingMs = 0;
                if (_pendingDoubleBlink)
                {
                    _pendingDoubleBlink = false;
                    _blinkRemainingMs = BlinkDurationMs();
                }
                else
                {
                    ScheduleNextBlink(immediate: false);
                }
            }
            return;
        }

        _blinkCooldownMs -= deltaMs;
        if (_blinkCooldownMs <= 0)
        {
            // Blink can run during mouth/speech; blocked only by special/emotion (resolved later).
            _blinkRemainingMs = BlinkDurationMs();
            _pendingDoubleBlink = _rng.NextDouble() < BlinkDoubleChance();
            // If double, second blink is scheduled when first ends.
        }
    }

    private string ResolveDesired_NoLock()
    {
        // special > emotion > blink > mouth > neutral
        if (_specialState is not null && IsKnownState(_specialState))
            return _specialState;

        if (_emotionState is not null && _emotionRemainingMs > 0 && IsKnownState(_emotionState))
            return _emotionState;

        // Blink temporarily above mouth so speaking still blinks.
        if (_blinkRemainingMs > 0 && IsKnownState(Blink))
            return Blink;

        // During emotion we never reach here; mouth only when face unlocked.
        if (!string.Equals(_heldMouthState, Neutral, StringComparison.OrdinalIgnoreCase)
            && IsKnownState(_heldMouthState))
            return _heldMouthState;

        return IsKnownState(Neutral) ? Neutral : (_available.FirstOrDefault() ?? Neutral);
    }

    private void UpdateTransition_NoLock(string desired, double deltaMs)
    {
        if (string.Equals(desired, _currentState, StringComparison.OrdinalIgnoreCase))
        {
            if (_fading)
            {
                _fadeElapsedMs += deltaMs;
                if (_fadeElapsedMs >= _fadeDurationMs)
                {
                    _fading = false;
                    _fadeFromState = null;
                }
            }
            return;
        }

        // Start transition into desired.
        var from = _currentState;
        _currentState = desired;
        _pack.States.TryGetValue(desired, out var def);
        var type = def?.Transition.Type ?? "cut";
        var ms = def?.Transition.Ms ?? 0;

        if (string.Equals(type, "fade", StringComparison.OrdinalIgnoreCase) && ms > 0)
        {
            _fading = true;
            _fadeFromState = from;
            _fadeDurationMs = ms;
            _fadeElapsedMs = 0;
        }
        else
        {
            _fading = false;
            _fadeFromState = null;
            _fadeDurationMs = 0;
            _fadeElapsedMs = 0;
        }
    }

    private StickerFrame? UpdateSticker_NoLock(double deltaMs)
    {
        if (_sticker is null) return null;

        var s = _sticker;
        s.ElapsedMs += deltaMs;
        var total = Math.Max(1, s.DurationMs);
        const double inMs = 180;
        const double outMs = 200;

        float scale = s.BaseScale;
        float alpha = 1f;

        if (s.ElapsedMs < inMs && string.Equals(s.AnimIn, "pop", StringComparison.OrdinalIgnoreCase))
        {
            // pop: 0.6 → 1.05 → 1.0 over ~180ms
            var u = s.ElapsedMs / inMs;
            scale = s.BaseScale * (u < 0.6
                ? Lerp(0.6f, 1.05f, (float)(u / 0.6))
                : Lerp(1.05f, 1.0f, (float)((u - 0.6) / 0.4)));
        }

        var remaining = total - s.ElapsedMs;
        if (remaining < outMs && string.Equals(s.AnimOut, "fade", StringComparison.OrdinalIgnoreCase))
        {
            alpha = (float)Math.Clamp(remaining / outMs, 0, 1);
        }

        if (s.ElapsedMs >= total)
        {
            _sticker = null;
            return null;
        }

        _sticker = s;
        return new StickerFrame(s.Id, scale, alpha, _pack.Stickers.Anchor.X, _pack.Stickers.Anchor.Y);
    }

    private void ScheduleNextBlink(bool immediate)
    {
        var (min, max) = BlinkIntervalMs();
        if (max < min) (min, max) = (max, min);
        _blinkCooldownMs = immediate
            ? _rng.Next(min, Math.Max(min + 1, max + 1))
            : _rng.Next(min, Math.Max(min + 1, max + 1));
        _blinkRemainingMs = 0;
        _pendingDoubleBlink = false;
    }

    private (int min, int max) BlinkIntervalMs()
    {
        if (_pack.States.TryGetValue(Blink, out var def) && def.Auto?.IntervalMs is { Length: >= 2 } iv)
            return ((int)(iv[0] * _blinkIntervalScale), (int)(iv[1] * _blinkIntervalScale));
        return ((int)(2000 * _blinkIntervalScale), (int)(6000 * _blinkIntervalScale));
    }

    private int BlinkDurationMs()
    {
        if (_pack.States.TryGetValue(Blink, out var def) && def.Auto is not null)
            return Math.Max(1, def.Auto.DurationMs);
        return 120;
    }

    private double BlinkDoubleChance()
    {
        if (_pack.States.TryGetValue(Blink, out var def) && def.Auto is not null)
            return def.Auto.DoubleBlinkChance;
        return 0.15;
    }

    private bool IsKnownState(string name)
    {
        if (_available.Count > 0)
            return _available.Contains(name);
        // Empty available set (e.g. unit tests with no asset filter) → trust pack keys.
        return _pack.States.ContainsKey(name);
    }

    private static float Lerp(float a, float b, float t) => a + (b - a) * Math.Clamp(t, 0f, 1f);

    private sealed class ActiveSticker
    {
        public ActiveSticker(string id, float baseScale, int durationMs, string animIn, string animOut, double elapsedMs)
        {
            Id = id;
            BaseScale = baseScale;
            DurationMs = durationMs;
            AnimIn = animIn;
            AnimOut = animOut;
            ElapsedMs = elapsedMs;
        }

        public string Id { get; }
        public float BaseScale { get; }
        public int DurationMs { get; }
        public string AnimIn { get; }
        public string AnimOut { get; }
        public double ElapsedMs { get; set; }
    }
}

public readonly record struct StateMachineFrame(
    string BodyState,
    string? FadeFromState,
    float FadeT,
    MotionOverrideDef? MotionOverride,
    bool EmotionActive,
    string MouthState,
    float Rms,
    StickerFrame? Sticker);

public readonly record struct StickerFrame(
    string Id,
    float Scale,
    float Alpha,
    float AnchorX,
    float AnchorY);
