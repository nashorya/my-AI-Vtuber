namespace AIVTuber.Core.Bot;

/// <summary>
/// Coalesces completed transcripts, independently of VAD/ASR activity. Once dispatched,
/// a turn owns generation/playback until completion; later input belongs to the next turn.
/// </summary>
internal sealed class ConversationTurnGate : IDisposable
{
    private readonly object _sync = new();
    private readonly List<TalkLine> _buffer = [];
    private readonly Func<DateTime> _now;
    private readonly TimeSpan _mergeWindow;
    private readonly TimeSpan _maxWait;
    private readonly Timer _timer;
    private DateTime _firstInput;
    private DateTime _lastInput;
    private bool _busy;
    private bool _disposed;
    private long _revision;
    public long ActiveTurnRevision { get; private set; }
    public event Action<IReadOnlyList<TalkLine>>? TurnReady;
    public event Action<string>? StatusChanged;

    public ConversationTurnGate(TimeSpan? mergeWindow = null, TimeSpan? maxWait = null,
        Func<DateTime>? now = null)
    {
        _mergeWindow = mergeWindow ?? TimeSpan.FromMilliseconds(350);
        _maxWait = maxWait ?? TimeSpan.FromSeconds(1);
        if (_mergeWindow < TimeSpan.Zero || _maxWait < TimeSpan.Zero)
            throw new ArgumentOutOfRangeException(nameof(mergeWindow));
        _now = now ?? (() => DateTime.UtcNow);
        _timer = new Timer(_ => Tick(), null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    public void AddLine(TalkLine line)
    {
        if (string.IsNullOrWhiteSpace(line.Text)) return;
        lock (_sync)
        {
            if (_disposed) return;
            var now = _now();
            if (_buffer.Count == 0) _firstInput = now;
            _lastInput = now;
            _buffer.Add(line.StartedAt == default ? line with { StartedAt = now } : line);
        }
        Tick();
    }

    public bool CanCommit(long revision)
    {
        lock (_sync) return !_disposed && _busy && revision == _revision;
    }

    public void CompleteTurn(long revision)
    {
        lock (_sync)
        {
            // Clear invalidates commit, but the cancelled owner must still release its slot.
            if (_disposed || revision != ActiveTurnRevision) return;
            _busy = false;
        }
        Tick();
    }

    public void Clear()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _buffer.Clear();
            _revision++;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Tick()
    {
        IReadOnlyList<TalkLine>? turn = null;
        lock (_sync)
        {
            if (_disposed || _busy || _buffer.Count == 0) return;
            var deadline = new[] { _lastInput + _mergeWindow, _firstInput + _maxWait }.Min();
            var remaining = deadline - _now();
            if (remaining > TimeSpan.Zero)
            {
                _timer.Change(remaining < TimeSpan.FromMilliseconds(1) ? TimeSpan.FromMilliseconds(1) : remaining,
                    Timeout.InfiniteTimeSpan);
                return;
            }
            turn = _buffer.OrderBy(l => l.StartedAt).ToArray();
            _buffer.Clear();
            _busy = true;
            ActiveTurnRevision = ++_revision;
            _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
        StatusChanged?.Invoke("判断是否被邀请回应");
        TurnReady?.Invoke(turn);
    }

    public void Dispose()
    {
        lock (_sync)
        {
            if (_disposed) return;
            _disposed = true;
            _buffer.Clear();
            _timer.Dispose();
        }
    }
}
