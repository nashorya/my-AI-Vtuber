using AIVTuber.Core.Memory;

namespace AIVTuber.Core.LiveStream;

/// <summary>
/// Selects which danmaku message to respond to from a backlog.
/// Priority: regular viewers > contains question mark > random.
/// Pauses selection while the AI is currently speaking.
/// Non-selected messages stay in the backlog and are re-ranked next round, so the
/// bot works through a busy chat instead of discarding everything on each pick.
/// Messages older than <c>maxAgeSec</c> are dropped (so the bot never answers stale
/// danmaku), and the backlog is capped at <c>maxQueueSize</c> (oldest dropped first).
/// </summary>
public sealed class DanmakuSelector
{
    private readonly List<Danmaku> _items = [];
    private readonly ViewerRepository? _viewerRepo;
    private readonly object _lock = new();
    private DateTime _lastSelectionTime = DateTime.MinValue;
    private readonly int _intervalSec;
    private readonly int _maxAgeSec;
    private readonly int _maxQueueSize;
    private bool _isSpeaking;

    /// <summary>Fires when a danmaku is selected for AI response.</summary>
    public event EventHandler<Danmaku>? OnDanmakuSelected;

    public DanmakuSelector(
        int selectionIntervalSec = 8,
        ViewerRepository? viewerRepo = null,
        int maxAgeSec = 60,
        int maxQueueSize = 50)
    {
        _intervalSec = selectionIntervalSec;
        _viewerRepo = viewerRepo;
        _maxAgeSec = maxAgeSec;
        _maxQueueSize = maxQueueSize;
    }

    /// <summary>Enqueue a received danmaku for potential selection.</summary>
    public void Enqueue(Danmaku danmaku)
    {
        lock (_lock)
        {
            _items.Add(danmaku);
            // Cap the backlog, dropping the oldest first.
            if (_maxQueueSize > 0)
                while (_items.Count > _maxQueueSize) _items.RemoveAt(0);
        }
    }

    /// <summary>Set whether the AI is currently speaking. Pauses selection when true.</summary>
    public void SetSpeaking(bool isSpeaking)
    {
        lock (_lock) { _isSpeaking = isSpeaking; }
        if (!isSpeaking) TrySelectNext();
    }

    /// <summary>
    /// Attempts to select the next danmaku for response. Respects the minimum
    /// interval and speaking state. Fire-and-forget wrapper around
    /// <see cref="TrySelectNextAsync"/> for event-handler call sites.
    /// </summary>
    public void TrySelectNext() => _ = TrySelectNextAsync();

    /// <summary>
    /// Attempts to select the next danmaku for response. Expired messages are pruned
    /// and the selection slot is claimed under the lock, but priority scoring (which
    /// may hit the DB) runs outside the lock and without blocking. The chosen message
    /// is removed; the rest of the backlog is kept for the next round.
    /// </summary>
    public async Task TrySelectNextAsync()
    {
        List<Danmaku> snapshot;
        lock (_lock)
        {
            PruneExpired();
            if (_isSpeaking) return;
            if ((DateTime.UtcNow - _lastSelectionTime).TotalSeconds < _intervalSec) return;
            if (_items.Count == 0) return;

            // Claim the slot now (so concurrent callers bail on the interval check),
            // then snapshot the backlog. Items are NOT removed yet — only the one we
            // finally select is removed, so losers survive to the next round.
            _lastSelectionTime = DateTime.UtcNow;
            snapshot = new List<Danmaku>(_items);
        }

        Danmaku? selected = null;
        try
        {
            int bestPriority = int.MinValue;
            foreach (var d in snapshot)
            {
                int priority = await GetPriorityAsync(d).ConfigureAwait(false);
                if (priority > bestPriority) { bestPriority = priority; selected = d; }
            }
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[弹幕] 选择失败: {ex.Message}");
            return;
        }

        if (selected is null) return;
        lock (_lock) { _items.Remove(selected); }
        OnDanmakuSelected?.Invoke(this, selected);
        // Note: interaction recording is handled by the OnDanmakuSelected subscriber
        // (Program.cs) to avoid double-counting.
    }

    /// <summary>Removes messages older than the configured max age. Caller must hold the lock.</summary>
    private void PruneExpired()
    {
        if (_maxAgeSec <= 0) return;
        var cutoff = DateTime.UtcNow.AddSeconds(-_maxAgeSec);
        _items.RemoveAll(d => d.Timestamp < cutoff);
    }

    /// <summary>Calculate priority score for a danmaku message (async, off the lock).</summary>
    private async Task<int> GetPriorityAsync(Danmaku danmaku)
    {
        int priority = 0;

        // Regular viewer bonus.
        if (_viewerRepo is not null)
        {
            try
            {
                if (await _viewerRepo.IsRegularAsync(danmaku.Uid, danmaku.Platform).ConfigureAwait(false))
                    priority += 10;
            }
            catch { /* ignore DB errors in priority scoring */ }
        }

        // Contains question mark
        if (danmaku.Content.Contains('?') || danmaku.Content.Contains('？'))
            priority += 5;

        // Small random bonus to avoid always picking same type
        priority += Random.Shared.Next(0, 3);

        return priority;
    }

    /// <summary>Number of danmaku waiting in the backlog.</summary>
    public int QueueCount
    {
        get { lock (_lock) { return _items.Count; } }
    }

    /// <summary>Snapshot of the current backlog (oldest first), for display.</summary>
    public IReadOnlyList<Danmaku> Snapshot()
    {
        lock (_lock) { return _items.ToArray(); }
    }

    /// <summary>Clear all queued danmaku.</summary>
    public void Clear()
    {
        lock (_lock) { _items.Clear(); }
    }
}
