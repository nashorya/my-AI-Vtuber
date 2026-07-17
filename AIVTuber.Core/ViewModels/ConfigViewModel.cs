using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Vts;

namespace AIVTuber.Core.ViewModels;

/// <summary>One editable row of the emotion → VTS-hotkey mapping table shown in the Config tab.</summary>
public sealed class EmotionMapRow : INotifyPropertyChanged
{
    private string _emotion = "";
    private string _hotkeyId = "";

    /// <summary>The word the LLM emits as "[emotion:word]" — must match exactly (case-sensitive).</summary>
    public string Emotion { get => _emotion; set { _emotion = value; OnChanged(); } }
    public string HotkeyId { get => _hotkeyId; set { _hotkeyId = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>One semantic LLM action mapped to a VTS hotkey.</summary>
public sealed class ActionMapRow : INotifyPropertyChanged
{
    private string _action = "";
    private string _hotkeyId = "";

    public string Action { get => _action; set { _action = value; OnChanged(); } }
    public string HotkeyId { get => _hotkeyId; set { _hotkeyId = value; OnChanged(); } }

    public event PropertyChangedEventHandler? PropertyChanged;
    private void OnChanged([CallerMemberName] string? name = null) => PropertyChanged?.Invoke(this, new(name));
}

/// <summary>Visible result of the draft persistence and runtime-apply sequence.</summary>
public enum ConfigSaveState
{
    Unchanged,
    Draft,
    Saving,
    Applied,
    SavedButApplyFailed,
    ValidationFailed
}

/// <summary>
/// Config tab view-model. Holds an editable deep-copy of the running config (so edits don't
/// touch the live config until saved), exposes the input-device list (injected so it stays
/// testable — WPF passes MicrophoneCapture.ListDevices()), and on save persists + hot-applies
/// via injected delegates (ConfigManager.Save + BotRuntime.ApplyConfigAsync).
/// </summary>
public sealed class ConfigViewModel : INotifyPropertyChanged
{
    private readonly Action<AppConfig> _save;
    private readonly Func<AppConfig, Task> _applyAsync;
    private readonly Func<Task<List<VtsHotkeyInfo>>> _getVtsHotkeys;
    private AppConfig _original;

    public AppConfig Working { get; private set; }
    public IReadOnlyList<string> InputDevices { get; }

    public ConfigViewModel(AppConfig current, IReadOnlyList<string> inputDevices,
                           Action<AppConfig> save, Func<AppConfig, Task> applyAsync,
                           Func<Task<List<VtsHotkeyInfo>>>? getVtsHotkeys = null)
    {
        _original = ConfigManager.Clone(current);
        Working = ConfigManager.Clone(current);
        InputDevices = inputDevices;
        _save = save;
        _applyAsync = applyAsync;
        _getVtsHotkeys = getVtsHotkeys ?? (() => Task.FromResult(new List<VtsHotkeyInfo>()));
        RefreshLoopbackSources();
        RefreshOutputDevices();

        EmotionRows = new ObservableCollection<EmotionMapRow>(
            Working.Vts.EmotionMap.Select(kv => new EmotionMapRow { Emotion = kv.Key, HotkeyId = kv.Value }));
        ActionRows = new ObservableCollection<ActionMapRow>(
            Working.Vts.ActionMap.Select(kv => new ActionMapRow { Action = kv.Key, HotkeyId = kv.Value }));
        EmotionRows.CollectionChanged += OnEmotionRowsChanged;
        ActionRows.CollectionChanged += OnActionRowsChanged;
        AttachRows(EmotionRows);
        AttachRows(ActionRows);
    }

    // ── Emotion → VTS hotkey mapping (Config tab editor) ─────────────────────

    /// <summary>Editable rows backing <see cref="VtsConfig.EmotionMap"/>; flushed to
    /// Working.Vts.EmotionMap by <see cref="SaveAsync"/>.</summary>
    public ObservableCollection<EmotionMapRow> EmotionRows { get; }
    public ObservableCollection<ActionMapRow> ActionRows { get; }

    public void AddEmotionRow() => EmotionRows.Add(new EmotionMapRow());

    public void RemoveEmotionRow(EmotionMapRow row) => EmotionRows.Remove(row);
    public void AddActionRow() => ActionRows.Add(new ActionMapRow());
    public void RemoveActionRow(ActionMapRow row) => ActionRows.Remove(row);

    private void SyncEmotionMap()
    {
        Working.Vts.EmotionMap = EmotionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Emotion) && !string.IsNullOrWhiteSpace(r.HotkeyId))
            .ToDictionary(r => r.Emotion.Trim(), r => r.HotkeyId.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    private void SyncActionMap()
    {
        Working.Vts.ActionMap = ActionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Action) && !string.IsNullOrWhiteSpace(r.HotkeyId))
            .GroupBy(r => r.Action.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().HotkeyId.Trim(), StringComparer.OrdinalIgnoreCase);
    }

    /// <summary>Adds all VTS animation hotkeys to the LLM action allow-list.</summary>
    public void ImportAnimationHotkeys()
    {
        var existingIds = ActionRows.Select(r => r.HotkeyId).ToHashSet(StringComparer.Ordinal);
        var imported = 0;
        foreach (var hotkey in HotkeyList.Where(h =>
                     h.Type.Equals("TriggerAnimation", StringComparison.OrdinalIgnoreCase)))
        {
            if (!existingIds.Add(hotkey.HotkeyId)) continue;
            var alias = BuildActionAlias(hotkey);
            alias = MakeUniqueActionAlias(alias);
            ActionRows.Add(new ActionMapRow { Action = alias, HotkeyId = hotkey.HotkeyId });
            imported++;
        }
        Status = imported == 0 ? "没有新的动画热键可导入" : $"已导入 {imported} 个动画动作，请保存应用";
    }

    private string MakeUniqueActionAlias(string alias)
    {
        var existing = ActionRows.Select(r => r.Action).ToHashSet(StringComparer.OrdinalIgnoreCase);
        if (!existing.Contains(alias)) return alias;
        var suffix = 2;
        while (existing.Contains($"{alias}_{suffix}")) suffix++;
        return $"{alias}_{suffix}";
    }

    internal static string BuildActionAlias(VtsHotkeyInfo hotkey)
    {
        var source = !string.IsNullOrWhiteSpace(hotkey.HotkeyName)
            ? hotkey.HotkeyName
            : Path.GetFileNameWithoutExtension(Path.GetFileNameWithoutExtension(hotkey.File));
        if (string.IsNullOrWhiteSpace(source)) return "action";

        var chars = source.Trim().Select(ch =>
            char.IsLetterOrDigit(ch) || ch is '_' or '-' ? ch : '_').ToArray();
        var alias = new string(chars).Trim('_');
        return string.IsNullOrWhiteSpace(alias) ? "action" : alias;
    }

    // ── Loopback source picker ────────────────────────────────────────────────

    /// <summary>A runnable process exposed in the loopback source picker.</summary>
    public sealed record LoopbackSource(string DisplayName, string ProcessName)
    {
        public override string ToString() => DisplayName;
        public static readonly LoopbackSource All = new("全部音源（扬声器混音）", "");
    }

    private List<LoopbackSource> _loopbackSources = [LoopbackSource.All];
    public List<LoopbackSource> LoopbackSources
    {
        get => _loopbackSources;
        private set => SetField(ref _loopbackSources, value);
    }

    private LoopbackSource _selectedLoopbackSource = LoopbackSource.All;
    public LoopbackSource SelectedLoopbackSource
    {
        get => _selectedLoopbackSource;
        set
        {
            SetField(ref _selectedLoopbackSource, value);
            Working.Audio.LoopbackProcessName = value?.ProcessName ?? "";
        }
    }

    /// <summary>
    /// Async: reads PE FileDescription for each process (like Task Manager) so the picker
    /// shows friendly Chinese/English names instead of raw exe names.
    /// Safe to call from the UI thread — heavy work runs on a thread pool thread.
    /// </summary>
    public async Task RefreshLoopbackSourcesAsync()
    {
        var sources = await Task.Run(BuildLoopbackSources);
        LoopbackSources = sources;
        RestoreSelection();
    }

    /// <summary>Fast sync version — only uses process names, no file I/O. Called from constructor.</summary>
    public void RefreshLoopbackSources()
    {
        LoopbackSources = BuildLoopbackSourcesFast();
        RestoreSelection();
    }

    private void RestoreSelection()
    {
        var current = Working.Audio.LoopbackProcessName;
        _selectedLoopbackSource = LoopbackSources
            .FirstOrDefault(s => s.ProcessName.Equals(current, StringComparison.OrdinalIgnoreCase))
            ?? LoopbackSource.All;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedLoopbackSource)));
    }

    private static List<LoopbackSource> BuildLoopbackSources()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<LoopbackSource>();

        foreach (var proc in Process.GetProcesses())
        {
            try
            {
                if (!seen.Add(proc.ProcessName)) continue;

                // Try to get window title and file description
                string windowTitle = "";
                string fileDesc = "";
                try { windowTitle = proc.MainWindowTitle; } catch { }
                try
                {
                    var fileName = proc.MainModule?.FileName;
                    if (fileName is not null)
                    {
                        var vi = FileVersionInfo.GetVersionInfo(fileName);
                        fileDesc = vi.FileDescription ?? "";
                    }
                }
                catch { }

                // Skip background-only processes (no window, no description) — they rarely produce user-facing audio
                if (string.IsNullOrWhiteSpace(windowTitle) && string.IsNullOrWhiteSpace(fileDesc))
                    continue;

                // Priority: window title > file description > process name
                string label = !string.IsNullOrWhiteSpace(windowTitle) ? windowTitle
                             : !string.IsNullOrWhiteSpace(fileDesc) ? fileDesc
                             : proc.ProcessName;
                string display = $"{label}  ({proc.ProcessName})";

                list.Add(new LoopbackSource(display, proc.ProcessName));
            }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }

        list.Sort((a, b) => string.Compare(a.DisplayName, b.DisplayName, StringComparison.CurrentCultureIgnoreCase));
        list.Insert(0, LoopbackSource.All);
        return list;
    }

    private static List<LoopbackSource> BuildLoopbackSourcesFast()
    {
        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var list = new List<LoopbackSource>();
        foreach (var proc in Process.GetProcesses())
        {
            try { if (seen.Add(proc.ProcessName)) list.Add(new LoopbackSource(proc.ProcessName, proc.ProcessName)); }
            catch { }
            finally { try { proc.Dispose(); } catch { } }
        }
        list.Sort((a, b) => string.Compare(a.ProcessName, b.ProcessName, StringComparison.OrdinalIgnoreCase));
        list.Insert(0, LoopbackSource.All);
        return list;
    }

    // ── Virtual mic output device picker ─────────────────────────────────────

    private List<string> _outputDevices = [];
    public List<string> OutputDevices
    {
        get => _outputDevices;
        private set => SetField(ref _outputDevices, value);
    }

    private string _selectedOutputDevice = "";
    public string SelectedOutputDevice
    {
        get => _selectedOutputDevice;
        set
        {
            SetField(ref _selectedOutputDevice, value);
            Working.Audio.VirtualMicDeviceName = value ?? "";
        }
    }

    public void RefreshOutputDevices()
    {
        try
        {
            OutputDevices = VirtualMicMixer.ListRenderDevices().ToList();
        }
        catch
        {
            OutputDevices = [];
        }
        var current = Working.Audio.VirtualMicDeviceName;
        _selectedOutputDevice = OutputDevices.FirstOrDefault(d =>
            d.Contains(current, StringComparison.OrdinalIgnoreCase)) ?? current;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SelectedOutputDevice)));
    }

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private ConfigSaveState _saveState = ConfigSaveState.Unchanged;
    public ConfigSaveState SaveState
    {
        get => _saveState;
        private set
        {
            if (_saveState == value) return;
            _saveState = value;
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveState)));
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(SaveStateText)));
        }
    }
    public string SaveStateText => SaveState switch
    {
        ConfigSaveState.Unchanged => "未修改",
        ConfigSaveState.Draft => "有未保存的草稿",
        ConfigSaveState.Saving => "正在保存并应用",
        ConfigSaveState.Applied => "已保存并应用",
        ConfigSaveState.SavedButApplyFailed => "已保存，运行时应用失败",
        ConfigSaveState.ValidationFailed => "请修正字段错误",
        _ => ""
    };

    private string _validationMessage = "";
    public string ValidationMessage { get => _validationMessage; private set => SetField(ref _validationMessage, value); }
    public bool HasValidationErrors => !string.IsNullOrEmpty(ValidationMessage);

    /// <summary>Whether the draft differs from the last loaded or successfully persisted configuration.</summary>
    public bool IsDirty => !ConfigsEqual(BuildCandidate(), _original);

    private bool _isSaving;
    /// <summary>True while a save+apply is in flight. Bind the button's IsEnabled to its inverse.</summary>
    public bool IsSaving { get => _isSaving; private set => SetField(ref _isSaving, value); }

    private List<VtsHotkeyInfo> _hotkeyList = [];
    /// <summary>Hotkeys fetched from VTS; populated by <see cref="QueryVtsHotkeysAsync"/>.</summary>
    public List<VtsHotkeyInfo> HotkeyList { get => _hotkeyList; private set => SetField(ref _hotkeyList, value); }

    private bool _isQueryingHotkeys;
    public bool IsQueryingHotkeys { get => _isQueryingHotkeys; private set => SetField(ref _isQueryingHotkeys, value); }

    /// <summary>Fetches the hotkey list from VTS and populates <see cref="HotkeyList"/>.</summary>
    public async Task QueryVtsHotkeysAsync()
    {
        if (IsQueryingHotkeys) return;
        IsQueryingHotkeys = true;
        try
        {
            HotkeyList = await _getVtsHotkeys();
            Status = HotkeyList.Count == 0 ? "VTS 未连接或当前模型无热键" : $"已加载 {HotkeyList.Count} 个热键";
        }
        catch (Exception ex)
        {
            Status = $"查询热键失败: {ex.Message}";
        }
        finally
        {
            IsQueryingHotkeys = false;
        }
    }

    /// <summary>Persist the working copy, then hot-apply it. Re-entrant calls (e.g. a double-click)
    /// are dropped while one is in flight, so ApplyConfigAsync never rebuilds modules concurrently.</summary>
    public async Task SaveAsync()
    {
        if (IsSaving) return;
        if (!ValidateDraft())
        {
            SaveState = ConfigSaveState.ValidationFailed;
            Status = ValidationMessage;
            return;
        }
        IsSaving = true;
        SaveState = ConfigSaveState.Saving;
        try
        {
            SyncEmotionMap();
            SyncActionMap();
            var persistedCandidate = ConfigManager.Clone(Working);
            var runtimeCandidate = ConfigManager.Clone(Working);
            _save(persistedCandidate);
            _original = ConfigManager.Clone(persistedCandidate);
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
            try
            {
                await _applyAsync(runtimeCandidate);
                Status = $"已保存并应用 · {DateTime.Now:HH:mm}";
                SaveState = ConfigSaveState.Applied;
            }
            catch (Exception ex)
            {
                Status = $"已保存，但应用失败: {ex.Message}";
                SaveState = ConfigSaveState.SavedButApplyFailed;
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Restores the isolated draft without touching persisted or running configuration.</summary>
    public void DiscardChanges()
    {
        Working = ConfigManager.Clone(_original);
        ReplaceRows(EmotionRows, Working.Vts.EmotionMap.Select(kv => new EmotionMapRow { Emotion = kv.Key, HotkeyId = kv.Value }));
        ReplaceRows(ActionRows, Working.Vts.ActionMap.Select(kv => new ActionMapRow { Action = kv.Key, HotkeyId = kv.Value }));
        ValidationMessage = "";
        SaveState = ConfigSaveState.Unchanged;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(Working)));
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    /// <summary>Called by the view after a draft-bound control changes so the save bar stays current.</summary>
    public void NotifyDraftChanged()
    {
        if (!IsSaving && IsDirty) SaveState = ConfigSaveState.Draft;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(IsDirty)));
    }

    private bool ValidateDraft()
    {
        var errors = new List<string>();
        ValidateMappings(EmotionRows.Select(r => (r.Emotion, r.HotkeyId)), "情绪", errors);
        ValidateMappings(ActionRows.Select(r => (r.Action, r.HotkeyId)), "动作", errors);
        if (Working.Vts.Port is < 1 or > 65535) errors.Add("VTS 端口必须在 1 到 65535 之间。");
        if (Working.Tts.Speed is < 0.5 or > 2.0) errors.Add("语速必须在 0.5 到 2.0 之间。");
        ValidationMessage = string.Join(" ", errors);
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(nameof(HasValidationErrors)));
        return errors.Count == 0;
    }

    private static void ValidateMappings(IEnumerable<(string Alias, string HotkeyId)> rows, string kind, List<string> errors)
    {
        var aliases = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        foreach (var (alias, hotkeyId) in rows)
        {
            if (string.IsNullOrWhiteSpace(alias) || string.IsNullOrWhiteSpace(hotkeyId))
            {
                errors.Add($"{kind}别名和热键 ID 不能为空。");
                continue;
            }
            if (!aliases.Add(alias.Trim())) errors.Add($"{kind}别名不能重复（不区分大小写）。");
        }
    }

    private AppConfig BuildCandidate()
    {
        var candidate = ConfigManager.Clone(Working);
        candidate.Vts.EmotionMap = EmotionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Emotion) && !string.IsNullOrWhiteSpace(r.HotkeyId))
            .GroupBy(r => r.Emotion.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().HotkeyId.Trim(), StringComparer.OrdinalIgnoreCase);
        candidate.Vts.ActionMap = ActionRows
            .Where(r => !string.IsNullOrWhiteSpace(r.Action) && !string.IsNullOrWhiteSpace(r.HotkeyId))
            .GroupBy(r => r.Action.Trim(), StringComparer.OrdinalIgnoreCase)
            .ToDictionary(g => g.Key, g => g.Last().HotkeyId.Trim(), StringComparer.OrdinalIgnoreCase);
        return candidate;
    }

    private static bool ConfigsEqual(AppConfig left, AppConfig right)
        => JsonSerializer.Serialize(left) == JsonSerializer.Serialize(right);

    private void OnEmotionRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateRowSubscriptions<EmotionMapRow>(e);

    private void OnActionRowsChanged(object? sender, System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        => UpdateRowSubscriptions<ActionMapRow>(e);

    private void UpdateRowSubscriptions<T>(System.Collections.Specialized.NotifyCollectionChangedEventArgs e)
        where T : INotifyPropertyChanged
    {
        if (e.OldItems is not null)
            foreach (T row in e.OldItems) row.PropertyChanged -= OnMappingRowPropertyChanged;
        if (e.NewItems is not null)
            foreach (T row in e.NewItems) row.PropertyChanged += OnMappingRowPropertyChanged;
        NotifyDraftChanged();
    }

    private void AttachRows<T>(IEnumerable<T> rows) where T : INotifyPropertyChanged
    {
        foreach (var row in rows)
        {
            row.PropertyChanged += OnMappingRowPropertyChanged;
        }
    }

    private void OnMappingRowPropertyChanged(object? sender, PropertyChangedEventArgs e) => NotifyDraftChanged();

    private void ReplaceRows<T>(ObservableCollection<T> destination, IEnumerable<T> values)
        where T : INotifyPropertyChanged
    {
        foreach (var row in destination) row.PropertyChanged -= OnMappingRowPropertyChanged;
        destination.Clear();
        foreach (var value in values) destination.Add(value);
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
