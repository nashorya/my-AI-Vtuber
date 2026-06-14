using AIVTuber.Core.Memory;

namespace AIVTuber.Core.LiveStream;

/// <summary>
/// Selects which danmaku message to respond to from a queue.
/// Priority: regular viewers (interaction_count > 5) > contains question mark > random.
/// Pauses selection while the AI is currently speaking.
/// </summary>
public sealed class DanmakuSelector
{
    private readonly Queue<Danmaku> _queue = new();
    private readonly ViewerRepository? _viewerRepo;
    private readonly object _lock = new();
    private DateTime _lastSelectionTime = DateTime.MinValue;
    private readonly int _intervalSec;
    private bool _isSpeaking;

    /// <summary>Fires when a danmaku is selected for AI response.</summary>
    public event EventHandler<Danmaku>? OnDanmakuSelected;

    public DanmakuSelector(int selectionIntervalSec = 8, ViewerRepository? viewerRepo = null)
    {
        _intervalSec = selectionIntervalSec;
        _viewerRepo = viewerRepo;
    }

    /// <summary>Enqueue a received danmaku for potential selection.</summary>
    public void Enqueue(Danmaku danmaku)
    {
        lock (_lock) { _queue.Enqueue(danmaku); }
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
    /// Attempts to select the next danmaku for response. The queue is drained and
    /// the selection slot claimed under the lock, but priority scoring (which may
    /// hit the DB) runs outside the lock and without blocking, so danmaku ingestion
    /// is never stalled and the thread pool is never starved by sync-over-async.
    /// </summary>
    public async Task TrySelectNextAsync()
    {
        List<Danmaku> candidates;
        lock (_lock)
        {
            if (_isSpeaking) return;
            if ((DateTime.UtcNow - _lastSelectionTime).TotalSeconds < _intervalSec) return;
            if (_queue.Count == 0) return;

            // Claim the slot now (so concurrent callers bail on the interval check),
            // then snapshot the queue. No DB/IO is performed while holding the lock.
            _lastSelectionTime = DateTime.UtcNow;
            candidates = new List<Danmaku>(_queue.Count);
            while (_queue.Count > 0) candidates.Add(_queue.Dequeue());
        }

        try
        {
            Danmaku? selected = null;
            int bestPriority = int.MinValue;
            foreach (var d in candidates)
            {
                int priority = await GetPriorityAsync(d).ConfigureAwait(false);
                if (priority > bestPriority) { bestPriority = priority; selected = d; }
            }

            if (selected is not null)
                OnDanmakuSelected?.Invoke(this, selected);
            // Note: interaction recording is handled by the OnDanmakuSelected subscriber
            // (Program.cs) to avoid double-counting.
        }
        catch (Exception ex)
        {
            Console.Error.WriteLine($"[弹幕] 选择失败: {ex.Message}");
        }
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

    /// <summary>Number of danmaku waiting in queue.</summary>
    public int QueueCount
    {
        get { lock (_lock) { return _queue.Count; } }
    }

    /// <summary>Clear all queued danmaku.</summary>
    public void Clear()
    {
        lock (_lock) { _queue.Clear(); }
    }
}