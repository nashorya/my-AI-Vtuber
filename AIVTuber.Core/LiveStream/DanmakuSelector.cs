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
    /// Attempts to select the next danmaku for response.
    /// Respects the minimum interval and speaking state.
    /// </summary>
    public void TrySelectNext()
    {
        lock (_lock)
        {
            if (_isSpeaking) return;
            if ((DateTime.UtcNow - _lastSelectionTime).TotalSeconds < _intervalSec) return;
            if (_queue.Count == 0) return;

            var candidates = new List<(Danmaku danmaku, int priority)>();
            while (_queue.Count > 0)
            {
                var d = _queue.Dequeue();
                int priority = GetPriority(d);
                candidates.Add((d, priority));
            }

            if (candidates.Count == 0) return;

            // Sort by priority descending, pick highest
            candidates.Sort((a, b) => b.priority.CompareTo(a.priority));
            var selected = candidates[0].danmaku;

            _lastSelectionTime = DateTime.UtcNow;
            OnDanmakuSelected?.Invoke(this, selected);

            // Optionally update viewer stats
            if (_viewerRepo is not null)
                _ = _viewerRepo.RecordInteractionAsync(selected.Uid, selected.Platform, selected.Username);
        }
    }

    /// <summary>Calculate priority score for a danmaku message.</summary>
    private int GetPriority(Danmaku danmaku)
    {
        int priority = 0;

        // Regular viewer (async check, approximate with sync result for quick scoring)
        if (_viewerRepo is not null)
        {
            try
            {
                var isRegular = _viewerRepo.IsRegularAsync(danmaku.Uid, danmaku.Platform).GetAwaiter().GetResult();
                if (isRegular) priority += 10;
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