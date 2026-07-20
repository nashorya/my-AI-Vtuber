using AIVTuber.Core.Diagnostics;

namespace AIVTuber.Core.Avatar;

/// <summary>
/// Watches <c>avatar.json</c> for changes and invokes a reload callback after debounce.
/// </summary>
internal sealed class AvatarConfigWatcher : IDisposable
{
    private readonly FileSystemWatcher _watcher;
    private readonly Func<Task> _onReload;
    private readonly TimeSpan _debounce;
    private readonly object _gate = new();
    private CancellationTokenSource? _debounceCts;
    private bool _disposed;

    public AvatarConfigWatcher(string assetsDir, Func<Task> onReload, TimeSpan debounce)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(assetsDir);
        _onReload = onReload ?? throw new ArgumentNullException(nameof(onReload));
        _debounce = debounce <= TimeSpan.Zero ? TimeSpan.FromMilliseconds(300) : debounce;

        _watcher = new FileSystemWatcher(assetsDir)
        {
            Filter = "avatar.json",
            NotifyFilter = NotifyFilters.LastWrite | NotifyFilters.FileName | NotifyFilters.Size | NotifyFilters.CreationTime,
            IncludeSubdirectories = false,
        };
        _watcher.Changed += OnFsEvent;
        _watcher.Created += OnFsEvent;
        _watcher.Renamed += OnRenamed;
        _watcher.EnableRaisingEvents = true;
    }

    private void OnRenamed(object sender, RenamedEventArgs e)
    {
        if (string.Equals(e.Name, "avatar.json", StringComparison.OrdinalIgnoreCase) ||
            string.Equals(Path.GetFileName(e.FullPath), "avatar.json", StringComparison.OrdinalIgnoreCase))
            ScheduleReload();
    }

    private void OnFsEvent(object sender, FileSystemEventArgs e) => ScheduleReload();

    private void ScheduleReload()
    {
        CancellationTokenSource cts;
        lock (_gate)
        {
            if (_disposed) return;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            cts = new CancellationTokenSource();
            _debounceCts = cts;
        }

        _ = DebounceAndFireAsync(cts.Token);
    }

    private async Task DebounceAndFireAsync(CancellationToken ct)
    {
        try
        {
            await Task.Delay(_debounce, ct).ConfigureAwait(false);
            await _onReload().ConfigureAwait(false);
        }
        catch (OperationCanceledException)
        {
            // Debounced away or disposed.
        }
        catch (Exception ex)
        {
            DebugLog.Write($"[Avatar] config reload failed: {ex.Message}");
        }
    }

    public void Dispose()
    {
        lock (_gate)
        {
            if (_disposed) return;
            _disposed = true;
            _debounceCts?.Cancel();
            _debounceCts?.Dispose();
            _debounceCts = null;
            _watcher.EnableRaisingEvents = false;
            _watcher.Changed -= OnFsEvent;
            _watcher.Created -= OnFsEvent;
            _watcher.Renamed -= OnRenamed;
            _watcher.Dispose();
        }
    }
}
