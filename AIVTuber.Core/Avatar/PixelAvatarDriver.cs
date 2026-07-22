using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// In-process PNG avatar backend: owns <see cref="AvatarStateMachine"/> + <see cref="MotionEngine"/>
/// + optional <see cref="PoseController"/> (v0.6 whole-image poses).
/// UI pulls <see cref="Sample"/> each render frame (CompositionTarget.Rendering).
/// </summary>
public sealed class PixelAvatarDriver : IAvatarController
{
    private AvatarPackConfig _pack;
    private readonly string _assetsDirectory;
    private readonly AvatarStateMachine _sm;
    private readonly MotionEngine _motion;
    private readonly PoseController _poses;
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
        _poses = new PoseController(pack.Poses, pack.Poses?.List.Keys, rng);
        _emotionMap = emotionMap is null
            ? new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
            : new Dictionary<string, string>(emotionMap, StringComparer.OrdinalIgnoreCase);
    }

    public AvatarPackConfig Pack => _pack;
    public string AssetsDirectory => _assetsDirectory;
    public AvatarStateMachine StateMachine => _sm;
    public MotionEngine Motion => _motion;
    public PoseController Poses => _poses;

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
        _lastOverrideKey = null;
        _sm.SetEmotion(mapped, hold);

        // Optional emotion → whole-image pose (v0.6 emotion_hint).
        var hint = _pack.Poses?.Triggers.EmotionHint;
        if (hint is { Enabled: true } && hint.Map.Count > 0)
        {
            if (hint.Map.TryGetValue(emotion, out var poseId)
                || hint.Map.TryGetValue(mapped, out poseId))
            {
                if (!string.IsNullOrWhiteSpace(poseId))
                    SetPose(poseId);
            }
        }
    }

    public void SetListening(bool userSpeaking)
    {
        var listening = _pack.MotionLayer.Listening;
        _sm.SetBlinkIntervalScale(userSpeaking ? listening.BlinkIntervalScale : 1.0);

        // v0.6: listening = whole-image tilt pose (no layered rotation).
        if (_poses.HasPoses)
        {
            if (userSpeaking)
            {
                if (_poses.FullExpression && !IsAvatarSpeaking())
                    _poses.SetPose(PickListeningPose(), fromListening: true);
            }
            else
            {
                _poses.SetPose(PoseController.Front, fromListening: false);
            }
            DebugLog.Write($"[Avatar] SetListening({userSpeaking}) pose→{_poses.CurrentId}");
            return;
        }

        var deg = userSpeaking ? listening.TiltDeg : 0f;
        _motion.SetListeningTilt(deg);
        DebugLog.Write($"[Avatar] SetListening({userSpeaking}) tilt→{deg:0.#}° (legacy)");
    }

    /// <summary>Monitor QA: switch whole-image pose (v0.6).</summary>
    public void SetPose(string poseId)
    {
        if (!_poses.HasPoses)
        {
            DebugLog.Write($"[Avatar] SetPose ignored (no poses): {poseId}");
            return;
        }
        _poses.SetPose(poseId);
        DebugLog.Write($"[Avatar] SetPose({poseId})");
    }

    public void ShowSticker(string stickerId) => _sm.ShowSticker(stickerId);

    public void SetIdleState(string state) => _sm.SetIdleState(state);

    public void ReloadConfig(AvatarPackConfig newPack, IEnumerable<string>? availableStates = null)
    {
        ArgumentNullException.ThrowIfNull(newPack);
        _pack = newPack;
        _sm.UpdatePack(newPack, availableStates);
        _motion.UpdateConfig(newPack.MotionLayer);
        _poses.UpdateConfig(newPack.Poses ?? new PosesConfig(), newPack.Poses?.List.Keys);
        DebugLog.Write($"[Avatar] PixelAvatarDriver reloaded ({newPack.Meta.Name})");
        AvatarConfigReloaded?.Invoke(this, EventArgs.Empty);
    }

    public AvatarRenderSample Sample(double deltaMs)
    {
        var speaking = IsAvatarSpeaking();
        var pose = _poses.HasPoses
            ? _poses.Tick(deltaMs, speaking)
            : new PoseFrame(PoseController.Front, FullExpression: true, null, 1f);

        var frame = _sm.Tick(deltaMs);

        if (pose.FullExpression && frame.EmotionActive)
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
        return new AvatarRenderSample(frame, motion, pose);
    }

    private bool IsAvatarSpeaking()
    {
        var thresholds = _pack.MouthSync.Levels;
        float gate = 0.08f;
        if (thresholds is { Count: > 0 })
        {
            foreach (var t in thresholds)
            {
                if (t.Below > 0.001f && t.Below < 1f)
                {
                    gate = (float)t.Below;
                    break;
                }
            }
        }
        return _motion.SmoothedRms >= gate;
    }

    private string PickListeningPose()
    {
        if (_pack.Poses?.List.ContainsKey("tilt_right") == true) return "tilt_right";
        if (_pack.Poses?.List.ContainsKey("tilt_left") == true) return "tilt_left";
        foreach (var (id, def) in _pack.Poses?.List ?? [])
        {
            if (!def.FullExpression) return id;
        }
        return PoseController.Front;
    }

    private string MapEmotion(string emotion)
    {
        if (string.IsNullOrWhiteSpace(emotion)) return AvatarStateMachine.Neutral;

        if (_emotionMap.TryGetValue(emotion, out var mapped) && !string.IsNullOrWhiteSpace(mapped))
            return mapped;

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
    MotionFrame Motion,
    PoseFrame Pose);
