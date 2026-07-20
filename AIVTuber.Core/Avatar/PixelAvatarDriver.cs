using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// In-process PNG avatar backend: owns <see cref="AvatarStateMachine"/> + <see cref="MotionEngine"/>.
/// UI pulls <see cref="Sample"/> each render frame (CompositionTarget.Rendering).
/// </summary>
public sealed class PixelAvatarDriver : IAvatarController
{
    private AvatarPackConfig _pack;
    private readonly string _assetsDirectory;
    private readonly AvatarStateMachine _sm;
    private readonly MotionEngine _motion;
    private readonly Dictionary<string, string> _emotionMap;
    private int _started;
    private string? _lastOverrideKey;
    private bool _hadEmotion;

    public PixelAvatarDriver(
        AvatarPackConfig pack,
        string assetsDirectory,
        IEnumerable<string>? availableStates = null,
        IReadOnlyDictionary<string, string>? emotionMap = null,
        Random? rng = null)
    {
        _pack = pack;
        _assetsDirectory = assetsDirectory;
        _sm = new AvatarStateMachine(pack, availableStates, rng);
        _motion = new MotionEngine(pack.MotionLayer);
        _emotionMap = emotionMap is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(emotionMap, StringComparer.OrdinalIgnoreCase);
    }

    public AvatarPackConfig Pack => _pack;
    public string AssetsDirectory => _assetsDirectory;
    public AvatarStateMachine StateMachine => _sm;
    public MotionEngine Motion => _motion;

    /// <summary>Raised after <see cref="ReloadConfig"/> swaps pack/motion config (UI should refresh).</summary>
    public event EventHandler? AvatarConfigReloaded;

    public Task StartAsync(CancellationToken ct = default)
    {
        Interlocked.Exchange(ref _started, 1);
        DebugLog.Write($"[Avatar] PixelAvatarDriver started ({_pack.Meta.Name})");
        return Task.CompletedTask;
    }

    public void OnRms(float rms)
    {
        _sm.OnRms(rms);
        _motion.SetRms(rms);
    }

    public void SetEmotion(string emotion, TimeSpan? hold = null)
    {
        var mapped = MapEmotion(emotion);
        // Force motion override re-apply so jump/shake one-shots fire again on re-trigger.
        _lastOverrideKey = null;
        _sm.SetEmotion(mapped, hold);
    }

    public void SetListening(bool userSpeaking)
    {
        // v0.2 listening pose: tilt the head toward the configured listening angle and blink
        // more frequently (concentration cue). Spring back + normal blink rate on release.
        var listening = _pack.MotionLayer.Listening;
        _motion.SetListeningTilt(userSpeaking ? listening.TiltDeg : 0f);
        _sm.SetBlinkIntervalScale(userSpeaking ? listening.BlinkIntervalScale : 1.0);
    }

    public void ShowSticker(string stickerId) => _sm.ShowSticker(stickerId);

    public void SetIdleState(string state) => _sm.SetIdleState(state);

    /// <summary>
    /// Hot-reload pack + motion config without restarting the driver. Preserves SM timers
    /// and motion spring state. Raises <see cref="AvatarConfigReloaded"/> for the UI.
    /// </summary>
    public void ReloadConfig(AvatarPackConfig newPack, IEnumerable<string>? availableStates = null)
    {
        ArgumentNullException.ThrowIfNull(newPack);
        _pack = newPack;
        _sm.UpdatePack(newPack, availableStates);
        _motion.UpdateConfig(newPack.MotionLayer);
        DebugLog.Write($"[Avatar] PixelAvatarDriver reloaded ({newPack.Meta.Name})");
        AvatarConfigReloaded?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>
    /// Advance logic by <paramref name="deltaMs"/> and return a render snapshot.
    /// Call from the UI thread with real frame delta (not a fixed 16.6ms assumption).
    /// </summary>
    public AvatarRenderSample Sample(double deltaMs)
    {
        var frame = _sm.Tick(deltaMs);

        // Sync motion overrides when emotion starts / changes / ends.
        if (frame.EmotionActive)
        {
            var key = frame.BodyState + "|" + (frame.MotionOverride?.GetHashCode() ?? 0);
            if (!_hadEmotion || key != _lastOverrideKey)
            {
                _motion.ApplyOverride(frame.MotionOverride);
                _lastOverrideKey = key;
            }
            _hadEmotion = true;
        }
        else if (_hadEmotion)
        {
            _motion.ClearPersistentOverride();
            _hadEmotion = false;
            _lastOverrideKey = null;
        }

        var motion = _motion.Tick(deltaMs);
        return new AvatarRenderSample(frame, motion);
    }

    private string MapEmotion(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return AvatarStateMachine.Neutral;

        if (_emotionMap.TryGetValue(emotion, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped;

        // Case-insensitive map lookup already handled by comparer; try direct state name.
        if (_pack.States.ContainsKey(emotion))
            return emotion;

        DebugLog.Write($"[Avatar] unmapped emotion '{emotion}' → neutral");
        return AvatarStateMachine.Neutral;
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}

/// <summary>One render-frame snapshot for the WPF window.</summary>
public readonly record struct AvatarRenderSample(
    StateMachineFrame State,
    MotionFrame Motion);
