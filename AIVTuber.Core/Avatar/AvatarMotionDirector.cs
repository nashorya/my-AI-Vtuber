using System.Threading.Channels;

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
    private AvatarIntent? _intent;
    private long _generation = -1, _canceledThrough = -1;
    private double _intentAt, _listeningUntil, _lastSample;
    private readonly double _origin;
    private Dictionary<string, float> _previous = new();
    private Dictionary<string, float> _from = new();
    private float _rms, _mouth;
    private readonly bool _preview;
    private bool _returning;
    private double _returnAt;
    private long _produced, _sent;
    public event EventHandler<string>? Faulted;
    public long ProducedFrames => Interlocked.Read(ref _produced);
    public long SentFrames => Interlocked.Read(ref _sent);
    public long SupersededFrames => Math.Max(0, ProducedFrames - SentFrames - 2);

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
            _from = new(_previous);
            _intent = new AvatarIntent(new Dictionary<string, float>(intent.Targets),
                Math.Clamp(intent.TransitionMs, 100, 2000), Math.Clamp(intent.HoldMs, 0, 5000));
            _intentAt = Now;
        }
    }

    public void Cancel(long generation)
    {
        lock (_gate)
        {
            _canceledThrough = Math.Max(_canceledThrough, generation);
            if (generation != _generation) return;
            // Blend back from the current pose rather than snapping to idle.
            _from = new(_previous);
            _intent = new AvatarIntent(new Dictionary<string, float>(), 300, 0);
            _intentAt = Now;
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
            foreach (var binding in _bindings)
            {
                var name = binding.Channel;
                float baseline = name switch
                {
                    "eyeOpenL" or "eyeOpenR" => 1,
                    "mouthOpen" => _mouth,
                    "breath" => (float)(.5 + .5 * Math.Sin(now * 1.3)),
                    "gazeX" => (float)(.025 * Math.Sin(now * 1.1)),
                    "gazeY" => (float)(.015 * Math.Sin(now * .7)),
                    "bodyRoll" => (float)(.018 * Math.Sin(now * 1.6)),
                    "headRoll" when now < _listeningUntil => .035f,
                    _ => 0,
                };
                var value = baseline;
                if (_intent is not null && (_preview || name is not ("mouthOpen" or "breath")))
                {
                    var elapsed = (now - _intentAt) * 1000;
                    var target = _intent.Targets.GetValueOrDefault(name, baseline);
                    var start = _from.GetValueOrDefault(name, baseline);
                    if (elapsed <= _intent.TransitionMs)
                        value = Lerp(start, target, Ease(elapsed / _intent.TransitionMs));
                    else if (elapsed <= _intent.TransitionMs + _intent.HoldMs) value = target;
                    else value = Lerp(target, baseline, Ease((elapsed - _intent.TransitionMs - _intent.HoldMs) / 400));
                }
                pose[name] = value;
            }
            _previous = pose;
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
            return result;
        }
    }

    private static float Ease(double value) { var t = (float)Math.Clamp(value, 0, 1); return t * t * (3 - 2 * t); }
    private static float Lerp(float a, float b, float t) => a + (b - a) * t;

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
}
