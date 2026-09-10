namespace AIVTuber.Core.Bot;

internal enum TalkIdentity { Self, Opponent, Danmaku, System }

internal readonly record struct TalkLine(
    TalkIdentity Identity,
    string SpeakerName,
    string Text,
    string? SubjectUid);

/// <summary>
/// Buffers labeled utterances and emits one turn after both enabled voice
/// channels are quiet and the AI is not speaking.
/// </summary>
internal sealed class DualPartyTurnGate : IDisposable
{
    private readonly TimeSpan _dualSilence;
    private readonly Func<DateTime> _now;
    private readonly object _sync = new();
    private readonly List<TalkLine> _buf = [];
    private bool _micSpeaking;
    private bool _loopbackSpeaking;
    private bool _aiSpeaking;
    private DateTime? _quietSince;
    private CancellationTokenSource? _arm;
    private bool _disposed;

    public DualPartyTurnGate(TimeSpan dualSilence, Func<DateTime>? now = null)
    {
        _dualSilence = dualSilence < TimeSpan.Zero ? TimeSpan.Zero : dualSilence;
        _now = now ?? (() => DateTime.UtcNow);
    }

    public event Action<IReadOnlyList<TalkLine>>? TurnReady;

    public void SetMicSpeaking(bool speaking) => SetFlag(ref _micSpeaking, speaking);
    public void SetLoopbackSpeaking(bool speaking) => SetFlag(ref _loopbackSpeaking, speaking);
    public void SetAiSpeaking(bool speaking) => SetFlag(ref _aiSpeaking, speaking);

    public void AddLine(TalkLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text)) return;
        lock (_sync)
        {
            if (_disposed) return;
            _buf.Add(line);
        }
        Tick();
        Arm();
    }

    public void Clear()
    {
        lock (_sync)
        {
            _buf.Clear();
            _quietSince = null;
            _arm?.Cancel();
        }
    }

    public void Tick()
    {
        IReadOnlyList<TalkLine>? flush = null;
        lock (_sync)
        {
            if (_disposed) return;
            if (_aiSpeaking || _micSpeaking || _loopbackSpeaking)
            {
                _quietSince = null;
                return;
            }
            if (_buf.Count == 0)
            {
                _quietSince = null;
                return;
            }

            var now = _now();
            _quietSince ??= now;
            if (now - _quietSince.Value < _dualSilence)
                return;

            flush = _buf.ToArray();
            _buf.Clear();
            _quietSince = null;
        }

        if (flush is { Count: > 0 })
            TurnReady?.Invoke(flush);
    }

    private void SetFlag(ref bool field, bool value)
    {
        lock (_sync)
        {
            if (_disposed) return;
            field = value;
            if (value)
            {
                _quietSince = null;
                _arm?.Cancel();
                return;
            }
        }

        Tick();
        Arm();
    }

    private void Arm()
    {
        CancellationToken token;
        lock (_sync)
        {
            if (_disposed || _aiSpeaking || _micSpeaking || _loopbackSpeaking || _buf.Count == 0)
                return;
            _arm?.Cancel();
            _arm = new CancellationTokenSource();
            token = _arm.Token;
        }

        _ = Task.Run(async () =>
        {
            try
            {
                if (_dualSilence > TimeSpan.Zero)
                    await Task.Delay(_dualSilence, token).ConfigureAwait(false);
                if (!token.IsCancellationRequested)
                    Tick();
            }
            catch (OperationCanceledException) { }
        }, token);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _arm?.Cancel();
            _arm?.Dispose();
            _buf.Clear();
        }
    }
}
