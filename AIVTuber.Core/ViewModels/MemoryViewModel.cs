using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

public sealed class MemoryViewModel : INotifyPropertyChanged
{
    private readonly IMemoryDataSource _memory;
    private readonly Action<Action> _dispatch;
    private readonly object _searchLock = new();
    private CancellationTokenSource? _factSearchCancellation;
    private CancellationTokenSource? _viewerSearchCancellation;
    private CancellationTokenSource? _pkSearchCancellation;
    private readonly MemoryQueryCoordinator _factQueries = new();
    private readonly MemoryQueryCoordinator _viewerQueries = new();
    private readonly MemoryQueryCoordinator _pkQueries = new();

    public ObservableCollection<FactRowViewModel> Facts { get; } = [];
    public ObservableCollection<ViewerRowViewModel> Viewers { get; } = [];
    public ObservableCollection<PkTurnRowViewModel> PkTurns { get; } = [];

    private FactRowViewModel? _selectedFact;
    public FactRowViewModel? SelectedFact
    {
        get => _selectedFact;
        set { if (_selectedFact == value) return; _selectedFact = value; OnPropertyChanged(); }
    }

    private ViewerRowViewModel? _selectedViewer;
    public ViewerRowViewModel? SelectedViewer
    {
        get => _selectedViewer;
        set { if (_selectedViewer == value) return; _selectedViewer = value; OnPropertyChanged(); }
    }

    private string _factSearch = string.Empty;
    public string FactSearch
    {
        get => _factSearch;
        set
        {
            if (_factSearch == value) return;
            _factSearch = value;
            OnPropertyChanged();
            ScheduleFactSearch();
        }
    }

    private string _viewerSearch = string.Empty;
    public string ViewerSearch
    {
        get => _viewerSearch;
        set
        {
            if (_viewerSearch == value) return;
            _viewerSearch = value;
            OnPropertyChanged();
            ScheduleViewerSearch();
        }
    }

    private bool _factsLoading;
    public bool FactsLoading
    {
        get => _factsLoading;
        private set { if (_factsLoading == value) return; _factsLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(FactsEmpty)); }
    }
    private bool _viewersLoading;
    public bool ViewersLoading
    {
        get => _viewersLoading;
        private set { if (_viewersLoading == value) return; _viewersLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(ViewersEmpty)); }
    }
    public bool FactsEmpty => !FactsLoading && string.IsNullOrEmpty(FactsError) && Facts.Count == 0;
    public bool ViewersEmpty => !ViewersLoading && string.IsNullOrEmpty(ViewersError) && Viewers.Count == 0;

    private string _pkSearch = string.Empty;
    public string PkSearch
    {
        get => _pkSearch;
        set
        {
            if (_pkSearch == value) return;
            _pkSearch = value;
            OnPropertyChanged();
            SchedulePkSearch();
        }
    }

    private bool _pkLoading;
    public bool PkLoading
    {
        get => _pkLoading;
        private set { if (_pkLoading == value) return; _pkLoading = value; OnPropertyChanged(); OnPropertyChanged(nameof(PkEmpty)); }
    }
    public bool PkEmpty => !PkLoading && string.IsNullOrEmpty(PkError) && PkTurns.Count == 0;

    private string _pkError = string.Empty;
    public string PkError
    {
        get => _pkError;
        private set { if (_pkError == value) return; _pkError = value; OnPropertyChanged(); OnPropertyChanged(nameof(PkEmpty)); }
    }

    private string _factsError = string.Empty;
    public string FactsError
    {
        get => _factsError;
        private set { if (_factsError == value) return; _factsError = value; OnPropertyChanged(); OnPropertyChanged(nameof(FactsEmpty)); }
    }

    private string _viewersError = string.Empty;
    public string ViewersError
    {
        get => _viewersError;
        private set { if (_viewersError == value) return; _viewersError = value; OnPropertyChanged(); OnPropertyChanged(nameof(ViewersEmpty)); }
    }

    private bool _extracting;
    public bool Extracting
    {
        get => _extracting;
        private set { _extracting = value; OnPropertyChanged(); }
    }

    private string _statusMessage = string.Empty;
    public string StatusMessage
    {
        get => _statusMessage;
        private set { _statusMessage = value; OnPropertyChanged(); }
    }

    public MemoryViewModel(BotRuntime runtime, Action<Action> dispatch)
        : this(new RuntimeMemoryDataSource(runtime), dispatch)
    {
    }

    public MemoryViewModel(IMemoryDataSource memory, Action<Action> dispatch)
    {
        _memory = memory;
        _dispatch = dispatch;
    }

    public async Task LoadAsync()
    {
        await RefreshFactsAsync();
        await RefreshViewersAsync();
        await RefreshPkTurnsAsync();
    }

    public Task RefreshFactsAsync() => RefreshFactsAsync(NextFactGeneration());

    private async Task RefreshFactsAsync(long generation)
    {
        SetIfCurrentFactGeneration(generation, () => { FactsLoading = true; FactsError = string.Empty; });
        try
        {
            var all = await _memory.GetFactsAsync();
            var query = _factSearch.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(f => f.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || (f.SubjectUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                     .ToList();

            _dispatch(() =>
            {
                if (!_factQueries.IsCurrent(generation)) return;
                var selectedId = SelectedFact?.Id;
                Facts.Clear();
                foreach (var f in filtered)
                    Facts.Add(new FactRowViewModel(f, DeleteFactAsync));
                SelectedFact = selectedId is null ? null : Facts.FirstOrDefault(f => f.Id == selectedId);
                FactsLoading = false;
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                if (!_factQueries.IsCurrent(generation)) return;
                FactsLoading = false;
                FactsError = $"加载失败: {ex.Message}";
            });
        }
    }

    public Task RefreshViewersAsync() => RefreshViewersAsync(NextViewerGeneration());

    private async Task RefreshViewersAsync(long generation)
    {
        SetIfCurrentViewerGeneration(generation, () => { ViewersLoading = true; ViewersError = string.Empty; });
        try
        {
            var all = await _memory.GetViewersAsync();
            var query = _viewerSearch.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(v => v.Uid.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || (v.Nickname?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                              || v.Platform.Contains(query, StringComparison.OrdinalIgnoreCase)).ToList();
            _dispatch(() =>
            {
                if (!_viewerQueries.IsCurrent(generation)) return;
                var selectedUid = SelectedViewer?.Uid;
                var selectedPlatform = SelectedViewer?.Platform;
                Viewers.Clear();
                foreach (var v in filtered)
                    Viewers.Add(new ViewerRowViewModel(v));
                SelectedViewer = selectedUid is null
                    ? null
                    : Viewers.FirstOrDefault(v => v.Uid == selectedUid && v.Platform == selectedPlatform);
                ViewersLoading = false;
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                if (!_viewerQueries.IsCurrent(generation)) return;
                ViewersLoading = false;
                ViewersError = $"加载观众失败: {ex.Message}";
            });
        }
    }

    public Task DeleteFactByIdAsync(string factId) => DeleteFactAsync(factId);
    public Task DeletePkTurnByIdAsync(string turnId) => DeletePkTurnAsync(turnId);

    public Task RefreshPkTurnsAsync() => RefreshPkTurnsAsync(NextPkGeneration());

    private async Task RefreshPkTurnsAsync(long generation)
    {
        SetIfCurrentPkGeneration(generation, () => { PkLoading = true; PkError = string.Empty; });
        try
        {
            var all = await _memory.GetPkTurnsAsync();
            var query = _pkSearch.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(t =>
                        (t.OpponentName?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || (t.OpponentUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false)
                        || t.OpponentText.Contains(query, StringComparison.OrdinalIgnoreCase)
                        || t.AssistantText.Contains(query, StringComparison.OrdinalIgnoreCase))
                    .ToList();

            _dispatch(() =>
            {
                if (!_pkQueries.IsCurrent(generation)) return;
                PkTurns.Clear();
                foreach (var t in filtered)
                    PkTurns.Add(new PkTurnRowViewModel(t, DeletePkTurnAsync));
                PkLoading = false;
            });
        }
        catch (Exception ex)
        {
            _dispatch(() =>
            {
                if (!_pkQueries.IsCurrent(generation)) return;
                PkLoading = false;
                PkError = $"加载失败: {ex.Message}";
            });
        }
    }

    private async Task DeletePkTurnAsync(string turnId)
    {
        try
        {
            await _memory.DeletePkTurnAsync(turnId);
            await RefreshPkTurnsAsync();
        }
        catch (Exception ex)
        {
            _dispatch(() => PkError = $"删除失败: {ex.Message}");
        }
    }

    private async Task DeleteFactAsync(string factId)
    {
        try
        {
            await _memory.DeleteFactAsync(factId);
            await RefreshFactsAsync();
        }
        catch (Exception ex)
        {
            _dispatch(() => FactsError = $"删除失败: {ex.Message}");
        }
    }

    /// <summary>Snapshot for the Web memory page (facts + viewers + status).</summary>
    public object BuildWebSnapshot()
    {
        return new
        {
            facts = Facts.Select(f => new
            {
                id = f.Id,
                content = f.Content,
                importance = f.Importance,
                importanceStars = f.ImportanceStars,
                subjectUid = f.SubjectUid,
                lastAccessed = f.LastAccessed,
                expires = f.Expires,
            }).ToList(),
            viewers = Viewers.Select(v => new
            {
                uid = v.Uid,
                platform = v.Platform,
                nickname = v.Nickname,
                interactionCount = v.InteractionCount,
                lastSeen = v.LastSeen,
                notes = v.Notes,
            }).ToList(),
            pkTurns = PkTurns.Select(t => new
            {
                id = t.Id,
                opponentName = t.OpponentName,
                opponentUid = t.OpponentUid,
                opponentText = t.OpponentText,
                assistantText = t.AssistantText,
                source = t.Source,
                ts = t.Ts,
            }).ToList(),
            factSearch = FactSearch,
            viewerSearch = ViewerSearch,
            pkSearch = PkSearch,
            factsLoading = FactsLoading,
            viewersLoading = ViewersLoading,
            pkLoading = PkLoading,
            factsEmpty = FactsEmpty,
            viewersEmpty = ViewersEmpty,
            pkEmpty = PkEmpty,
            factsError = FactsError,
            viewersError = ViewersError,
            pkError = PkError,
            extracting = Extracting,
            statusMessage = StatusMessage,
        };
    }

    private long NextFactGeneration() => _factQueries.Begin();
    private long NextViewerGeneration() => _viewerQueries.Begin();
    private long NextPkGeneration() => _pkQueries.Begin();

    public Task ActivateTabAsync(MemoryTab tab)
    {
        CancelPendingSearches();
        _factQueries.Invalidate();
        _viewerQueries.Invalidate();
        _pkQueries.Invalidate();
        return tab switch
        {
            MemoryTab.Viewers => RefreshViewersAsync(),
            MemoryTab.PkTurns => RefreshPkTurnsAsync(),
            _ => RefreshFactsAsync(),
        };
    }

    private void SetIfCurrentFactGeneration(long generation, Action action)
        => _dispatch(() => { if (_factQueries.IsCurrent(generation)) action(); });

    private void SetIfCurrentViewerGeneration(long generation, Action action)
        => _dispatch(() => { if (_viewerQueries.IsCurrent(generation)) action(); });

    private void SetIfCurrentPkGeneration(long generation, Action action)
        => _dispatch(() => { if (_pkQueries.IsCurrent(generation)) action(); });

    private void ScheduleFactSearch()
    {
        CancellationTokenSource cancellation;
        long generation = NextFactGeneration();
        lock (_searchLock)
        {
            _factSearchCancellation?.Cancel();
            _factSearchCancellation?.Dispose();
            cancellation = _factSearchCancellation = new CancellationTokenSource();
        }
        _ = DebounceAsync(RefreshFactsAsync, generation, cancellation.Token);
    }

    private void ScheduleViewerSearch()
    {
        CancellationTokenSource cancellation;
        long generation = NextViewerGeneration();
        lock (_searchLock)
        {
            _viewerSearchCancellation?.Cancel();
            _viewerSearchCancellation?.Dispose();
            cancellation = _viewerSearchCancellation = new CancellationTokenSource();
        }
        _ = DebounceAsync(RefreshViewersAsync, generation, cancellation.Token);
    }

    private void SchedulePkSearch()
    {
        CancellationTokenSource cancellation;
        long generation = NextPkGeneration();
        lock (_searchLock)
        {
            _pkSearchCancellation?.Cancel();
            _pkSearchCancellation?.Dispose();
            cancellation = _pkSearchCancellation = new CancellationTokenSource();
        }
        _ = DebounceAsync(RefreshPkTurnsAsync, generation, cancellation.Token);
    }

    private void CancelPendingSearches()
    {
        lock (_searchLock)
        {
            _factSearchCancellation?.Cancel();
            _viewerSearchCancellation?.Cancel();
            _pkSearchCancellation?.Cancel();
        }
    }

    private static async Task DebounceAsync(Func<long, Task> refresh, long generation, CancellationToken cancellationToken)
    {
        try
        {
            await Task.Delay(TimeSpan.FromMilliseconds(275), cancellationToken).ConfigureAwait(false);
            await refresh(generation).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested) { }
    }

    public async Task ForceExtractAsync()
    {
        if (Extracting) return;
        _dispatch(() => { Extracting = true; StatusMessage = "正在提取记忆..."; });
        try
        {
            await _memory.ForceExtractAsync();
            await RefreshFactsAsync();
            _dispatch(() => StatusMessage = "提取完成");
        }
        catch (Exception ex)
        {
            _dispatch(() => StatusMessage = $"提取失败: {ex.Message}");
        }
        finally
        {
            _dispatch(() => Extracting = false);
        }
    }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnPropertyChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

public sealed class FactRowViewModel(Fact fact, Func<string, Task> deleteCallback)
{
    private int _deleting;
    public string Id { get; } = fact.Id;
    public string Content { get; } = fact.Content;
    public int Importance { get; } = fact.Importance;
    public string ImportanceStars { get; } = new string('★', fact.Importance) + new string('☆', Math.Max(0, 5 - fact.Importance));
    public string SubjectUid { get; } = fact.SubjectUid ?? "—";
    public string CreatedAt { get; } = TryFormatDate(fact.CreatedAt);
    public string LastAccessed { get; } = TryFormatDate(fact.LastAccessed);
    public int AccessCount { get; } = fact.AccessCount;
    public string Expires { get; } = fact.Expires == "stable" ? "永久" : fact.Expires ?? "永久";

    public async Task DeleteAsync()
    {
        if (Interlocked.Exchange(ref _deleting, 1) != 0) return;
        try { await deleteCallback(Id); }
        finally { Volatile.Write(ref _deleting, 0); }
    }

    private static string TryFormatDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        return DateTime.TryParse(iso, out var dt) ? dt.ToLocalTime().ToString("MM-dd HH:mm") : iso;
    }
}

public enum MemoryTab
{
    Facts,
    Viewers,
    PkTurns
}

public sealed class PkTurnRowViewModel(PkTurn turn, Func<string, Task> deleteCallback)
{
    private int _deleting;
    public string Id { get; } = turn.Id;
    public string OpponentName { get; } = string.IsNullOrWhiteSpace(turn.OpponentName) ? "—" : turn.OpponentName;
    public string OpponentUid { get; } = turn.OpponentUid ?? "—";
    public string OpponentText { get; } = turn.OpponentText;
    public string AssistantText { get; } = turn.AssistantText;
    public string Source { get; } = turn.Source;
    public string Ts { get; } = TryFormatDate(turn.Ts);

    public async Task DeleteAsync()
    {
        if (Interlocked.Exchange(ref _deleting, 1) != 0) return;
        try { await deleteCallback(Id); }
        finally { Volatile.Write(ref _deleting, 0); }
    }

    private static string TryFormatDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        return DateTime.TryParse(iso, out var dt) ? dt.ToLocalTime().ToString("MM-dd HH:mm") : iso;
    }
}

public sealed class ViewerRowViewModel(Viewer viewer)
{
    public string Uid { get; } = viewer.Uid;
    public string Platform { get; } = viewer.Platform;
    public string Nickname { get; } = viewer.Nickname ?? "—";
    public int InteractionCount { get; } = viewer.InteractionCount;
    public string LastSeen { get; } = TryFormatDate(viewer.LastSeen);
    public string FirstSeen { get; } = TryFormatDate(viewer.FirstSeen);
    public string Notes { get; } = viewer.Notes ?? string.Empty;

    private static string TryFormatDate(string? iso)
    {
        if (string.IsNullOrEmpty(iso)) return "—";
        return DateTime.TryParse(iso, out var dt) ? dt.ToLocalTime().ToString("MM-dd HH:mm") : iso;
    }
}
