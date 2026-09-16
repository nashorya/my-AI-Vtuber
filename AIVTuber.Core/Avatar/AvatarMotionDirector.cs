using System.Threading.Channels;
using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

public interface IAvatarParameterBackend
{
    Task InjectAsync(IReadOnlyDictionary<string, float> values, CancellationToken ct);
}

/// <summary>Owns the only continuous writer. Sampling never waits for the network.</summary>
public sealed class AvatarMotionDirector : IAsyncDisposable
{
    private readonly IAvatarParameterBackend _backend;
    private readonly TimeProvider _clock;
    private readonly List<AvatarChannelBinding> _bindings;
    private readonly object _gate = new();
    private readonly CancellationTokenSource _life = new();
    private readonly Channel<IReadOnlyDictionary<string, float>> _frames = Channel.CreateBounded<IReadOnlyDictionary<string, float>>(
        new BoundedChannelOptions(1) { FullMode = BoundedChannelFullMode.DropOldest, SingleReader = true, SingleWriter = true });
    private Task? _sampleTask, _sendTask;
    private long _generation = -1, _canceledThrough = -1;
    private double _listeningUntil, _lastSample;
    private readonly double _origin;
    private Dictionary<string, float> _previous = new();
    private readonly Dictionary<string, ChannelPose> _active = new();
    private readonly Dictionary<string, float> _follow = new();
    private float _rms, _mouth;
    private readonly bool _preview;
    private bool _returning;
    private double _returnAt;
    private long _produced, _sent;
    private int _diagSkip;
    public event EventHandler<string>? Faulted;
    public long ProducedFrames => Interlocked.Read(ref _produced);
    public long SentFrames => Interlocked.Read(ref _sent);
    public long SupersededFrames => Math.Max(0, ProducedFrames - SentFrames - 2);
    public IReadOnlyDictionary<string, float> LastSemantic { get; private set; } = new Dictionary<string, float>();
    public IReadOnlyDictionary<string, float> LastSent { get; private set; } = new Dictionary<string, float>();

    public AvatarMotionDirector(IAvatarParameterBackend backend, IEnumerable<AvatarChannelBinding> bindings, TimeProvider? clock = null, bool preview = false)
    {
        _backend = backend;
        _preview = preview;
        _clock = clock ?? TimeProvider.System;
        _origin = _clock.GetTimestamp();
        // Deep snapshot: UI edits cannot change a running frame.
        _bindings = System.Text.Json.JsonSerializer.Deserialize<List<AvatarChannelBinding>>(
            System.Text.Json.JsonSerializer.Serialize(bindings.Where(b => b.Verified && b.IsValid)))!;
    }

    private double Now => (_clock.GetTimestamp() - _origin) / _clock.TimestampFrequency;
    public void Start()
    {
        if (_sampleTask is not null) return;
        _sampleTask = SampleLoopAsync();
        _sendTask = SendLoopAsync();
    }

    public void Submit(long generation, AvatarIntent intent)
    {
        lock (_gate)
        {
            if (generation <= _canceledThrough || generation < _generation || _returning) return;
            _generation = generation;
            var now = Now;
            var transition = Math.Clamp(intent.TransitionMs, 100, 2000);
            var hold = Math.Clamp(intent.HoldMs, 0, 5000);
            foreach (var (name, target) in intent.Targets)
            {
                _active[name] = new ChannelPose
                {
                    From = _previous.GetValueOrDefault(name),
                    Target = target,
                    StartedAt = now,
                    TransitionMs = transition,
                    HoldMs = hold,
                    Gesture = intent.Gesture
                };
            }
            if (intent.Gesture is null && intent.Targets.ContainsKey("headYaw") &&
                _active.TryGetValue("headYaw", out var look))
                look.Gesture = null;
            DebugLog.WriteThrottled("avatar-submit",
                $"[Avatar/VTS] submit gen={generation} specified={FormatTargets(intent.Targets)} gesture={intent.Gesture ?? "-"}");
        }
    }

    public void Cancel(long generation)
    {
        lock (_gate)
        {
            _canceledThrough = Math.Max(_canceledThrough, generation);
            if (generation != _generation) return;
            var now = Now;
            foreach (var name in _active.Keys.ToArray())
            {
                // Blend toward each channel's own rest pose: 0 is "neutral" only for
                // signed channels; eyelids rest open and must not be cancelled shut.
                var binding = _bindings.FirstOrDefault(b => b.Channel == name);
                _active[name] = new ChannelPose
                {
                    From = _previous.GetValueOrDefault(name),
                    Target = binding is null ? 0 : RestTarget(binding, now),
                    StartedAt = now,
                    TransitionMs = 300,
                    HoldMs = 0
                };
            }
            _rms = 0;
        }
    }

    public void OnRms(float rms) { lock (_gate) _rms = float.IsFinite(rms) ? Math.Clamp(rms, 0, 1) : 0; }
    public void NoteListening() { lock (_gate) _listeningUntil = Now + 1; }

    public IReadOnlyDictionary<string, float> Sample()
    {
        lock (_gate)
        {
            var now = Now;
            var dt = Math.Clamp(now - _lastSample, 0, .1);
            _lastSample = now;
            _mouth += (_rms - _mouth) * (float)(1 - Math.Exp(-dt / (_rms > _mouth ? .045 : .09)));
            var pose = new Dictionary<string, float>();
            var specified = new Dictionary<string, bool>();
            foreach (var binding in _bindings)
            {
                var name = binding.Channel;
                var baseline = RestTarget(binding, now);
                var value = baseline;
                specified[name] = _active.ContainsKey(name);
                if (_active.TryGetValue(name, out var channel) && (_preview || name is not ("mouthOpen" or "breath")))
                {
                    var elapsed = (now - channel.StartedAt) * 1000;
                    if (name == "headYaw" && channel.Gesture == "摇头")
                        value = HeadYawShake(elapsed, channel.Target, baseline);
                    else if (elapsed <= channel.TransitionMs)
                        value = Lerp(channel.From, channel.Target, Ease(elapsed / channel.TransitionMs));
                    else if (elapsed <= channel.TransitionMs + channel.HoldMs) value = channel.Target;
                    else
                    {
                        value = Lerp(channel.Target, baseline, Ease((elapsed - channel.TransitionMs - channel.HoldMs) / 400));
                        if (elapsed >= channel.TransitionMs + channel.HoldMs + 400)
                            _active.Remove(name);
                    }
                    if (name == "headYaw" && channel.Gesture == "摇头" && elapsed >= 2080)
                        _active.Remove(name);
                }
                pose[name] = value;
            }
            FollowHeadWithBody(pose, dt);
            _previous = pose;
            LastSemantic = new Dictionary<string, float>(pose);
            // Staggered deterministic cadence; eyelid intent remains effective during blinks.
            var phase = now % 4.7;
            var blink = phase < .18 ? (float)Math.Abs(phase / .09 - 1) : 1;
            var result = new Dictionary<string, float>();
            foreach (var binding in _bindings)
            {
                var value = pose[binding.Channel];
                if (binding.Channel is "eyeOpenL" or "eyeOpenR") value *= blink;
                var mapped = binding.Map(value);
                if (_returning) mapped = Lerp(mapped, binding.Neutral, Ease((now - _returnAt) / .3));
                result[binding.InputId] = mapped;
            }
            LastSent = result;
            if ((_diagSkip++ % 15) == 0)
            {
                DebugLog.WriteThrottled("avatar-sample",
                    "[Avatar/VTS] specified=" + string.Join(',', specified.Where(p => p.Value).Select(p => p.Key)) +
                    " semantic=" + FormatTargets(pose) +
                    " sent=" + FormatTargets(result));
            }
            return result;
        }
    }

    /// <summary>Idle pose a channel relaxes to when no intent holds it. 0 is neutral
    /// only for signed channels; eyelids rest open, breath/gaze/idle sway move on their own.</summary>
    private float RestTarget(AvatarChannelBinding binding, double now) => binding.Channel switch
    {
        "eyeOpenL" or "eyeOpenR" => EyeRest(binding),
        "mouthOpen" => _mouth,
        "breath" => (float)(.5 + .5 * Math.Sin(now * 1.3)),
        "gazeX" => (float)(.025 * Math.Sin(now * 1.1)),
        "gazeY" => (float)(.015 * Math.Sin(now * .7)),
        "bodyRoll" => (float)(.018 * Math.Sin(now * 1.6)),
        "headRoll" when now < _listeningUntil => .035f,
        _ => 0,
    };

    private void FollowHeadWithBody(Dictionary<string, float> pose, double dt)
    {
        // Official VTS rigs wire body outputs to the face inputs at ~0.9 semantic, so a
        // head turn reads as the whole person turning. Match that feel through the
        // independent body inputs, weaker on pitch/roll, still gated off tiny looks.
        Couple(pose, dt, "headYaw", "bodyYaw", .8f, .15f);
        Couple(pose, dt, "headPitch", "bodyPitch", .45f, .2f);
        Couple(pose, dt, "headRoll", "bodyRoll", .35f, .2f);
    }

    private void Couple(Dictionary<string, float> pose, double dt,
        string head, string body, float ratio, float minAbs)
    {
        if (!pose.ContainsKey(body)) return;
        if (_active.ContainsKey(body))
        {
            _follow[body] = pose[body];
            return;
        }
        var desired = 0f;
        if (_active.TryGetValue(head, out var headChannel))
        {
            if (headChannel.Gesture == "摇头")
                desired = pose.GetValueOrDefault(head) * .18f;
            else if (Math.Abs(headChannel.Target) >= minAbs)
                desired = headChannel.Target * ratio;
        }
        var current = _follow.GetValueOrDefault(body, pose[body]);
        var alpha = (float)(1 - Math.Exp(-dt / .28));
        current += (desired - current) * alpha;
        _follow[body] = current;
        if (body == "bodyRoll")
            pose[body] = current + pose[body];
        else
            pose[body] = current;
    }

    private static float HeadYawShake(double elapsedMs, float target, float baseline)
    {
        var amp = Math.Clamp(Math.Abs(target), 0, .95f);
        if (amp == 0) return baseline;
        const double duration = 1800;
        if (elapsedMs <= 0) return baseline;
        if (elapsedMs >= duration) return Lerp(0, baseline, Ease((elapsedMs - duration) / 280));
        var t = elapsedMs / duration;
        return Math.Sign(target) * amp * (float)Math.Sin(t * Math.PI * 3);
    }

    private static string FormatTargets(IReadOnlyDictionary<string, float> values)
        => string.Join(',', values.Select(p => p.Key + "=" + p.Value.ToString("0.##")));

    private static float Ease(double value) { var t = (float)Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;
    private static float EyeRest(AvatarChannelBinding binding)
    {
        // Some rigs reserve the upper end for wide/surprised eyes. Idle uses the
        // calibrated neutral opening, not necessarily the maximum opening.
        var normalized = (binding.Neutral - binding.Minimum) / (binding.Maximum - binding.Minimum);
        return binding.Inverted ? 1 - normalized : normalized;
    }

    private async Task SampleLoopAsync()
    {
        try
        {
            using var timer = new PeriodicTimer(TimeSpan.FromSeconds(1d / 30), _clock);
            while (await timer.WaitForNextTickAsync(_life.Token).ConfigureAwait(false))
            {
                var frame = Sample();
                if (frame.Count == 0) continue;
                Interlocked.Increment(ref _produced);
                _frames.Writer.TryWrite(frame);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        finally { _frames.Writer.TryComplete(); }
    }

    private async Task SendLoopAsync()
    {
        try
        {
            await foreach (var frame in _frames.Reader.ReadAllAsync(_life.Token).ConfigureAwait(false))
            {
                await _backend.InjectAsync(frame, _life.Token).ConfigureAwait(false);
                Interlocked.Increment(ref _sent);
            }
        }
        catch (OperationCanceledException) when (_life.IsCancellationRequested) { }
        catch (Exception ex) { _life.Cancel(); Faulted?.Invoke(this, ex.Message); }
    }

    public async Task ReturnToNeutralAsync()
    {
        lock (_gate) { _returning = true; _returnAt = Now; }
        await Task.Delay(TimeSpan.FromMilliseconds(350)).ConfigureAwait(false);
        await DisposeAsync();
    }

    public async ValueTask DisposeAsync()
    {
        Halt();
        if (_sampleTask is not null) await _sampleTask.ConfigureAwait(false);
        if (_sendTask is not null) await _sendTask.ConfigureAwait(false);
    }
    public void Halt() => _life.Cancel();

    private sealed class ChannelPose
    {
        public float From;
        public float Target;
        public double StartedAt;
        public int TransitionMs;
        public int HoldMs;
        public string? Gesture;
    }
}
