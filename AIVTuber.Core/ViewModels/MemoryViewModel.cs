using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using AIVTuber.Core.Memory;
using AIVTuber.Core.Runtime;

namespace AIVTuber.Core.ViewModels;

public sealed class MemoryViewModel : INotifyPropertyChanged
{
    private readonly BotRuntime _runtime;
    private readonly Action<Action> _dispatch;

    public ObservableCollection<FactRowViewModel> Facts { get; } = [];
    public ObservableCollection<ViewerRowViewModel> Viewers { get; } = [];

    private string _factSearch = string.Empty;
    public string FactSearch
    {
        get => _factSearch;
        set { _factSearch = value; OnPropertyChanged(); _ = RefreshFactsAsync(); }
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
    {
        _runtime = runtime;
        _dispatch = dispatch;
    }

    public async Task LoadAsync()
    {
        await RefreshFactsAsync();
        await RefreshViewersAsync();
    }

    public async Task RefreshFactsAsync()
    {
        try
        {
            var all = await _runtime.FactRepository.GetAllAsync();
            var query = _factSearch.Trim();
            var filtered = string.IsNullOrEmpty(query)
                ? all
                : all.Where(f => f.Content.Contains(query, StringComparison.OrdinalIgnoreCase)
                              || (f.SubjectUid?.Contains(query, StringComparison.OrdinalIgnoreCase) ?? false))
                     .ToList();

            _dispatch(() =>
            {
                Facts.Clear();
                foreach (var f in filtered)
                    Facts.Add(new FactRowViewModel(f, DeleteFactAsync));
            });
        }
        catch (Exception ex)
        {
            _dispatch(() => StatusMessage = $"加载失败: {ex.Message}");
        }
    }

    public async Task RefreshViewersAsync()
    {
        try
        {
            var all = await _runtime.ViewerRepository.GetAllAsync();
            _dispatch(() =>
            {
                Viewers.Clear();
                foreach (var v in all)
                    Viewers.Add(new ViewerRowViewModel(v));
            });
        }
        catch (Exception ex)
        {
            _dispatch(() => StatusMessage = $"加载观众失败: {ex.Message}");
        }
    }

    private async Task DeleteFactAsync(string factId)
    {
        await _runtime.FactRepository.DeleteAsync(factId);
        await RefreshFactsAsync();
    }

    public async Task ForceExtractAsync()
    {
        if (Extracting) return;
        _dispatch(() => { Extracting = true; StatusMessage = "正在提取记忆..."; });
        try
        {
            await _runtime.ForceExtractMemoryAsync();
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
    public string Id { get; } = fact.Id;
    public string Content { get; } = fact.Content;
    public int Importance { get; } = fact.Importance;
    public string ImportanceStars { get; } = new string('★', fact.Importance) + new string('☆', Math.Max(0, 5 - fact.Importance));
    public string SubjectUid { get; } = fact.SubjectUid ?? "—";
    public string CreatedAt { get; } = TryFormatDate(fact.CreatedAt);
    public string LastAccessed { get; } = TryFormatDate(fact.LastAccessed);
    public int AccessCount { get; } = fact.AccessCount;
    public string Expires { get; } = fact.Expires == "stable" ? "永久" : fact.Expires ?? "永久";

    public async void Delete() => await deleteCallback(Id);

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
