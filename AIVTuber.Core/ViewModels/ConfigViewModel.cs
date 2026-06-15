using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Text.Json;
using AIVTuber.Core.Config;

namespace AIVTuber.Core.ViewModels;

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

    public AppConfig Working { get; }
    public IReadOnlyList<string> InputDevices { get; }

    public ConfigViewModel(AppConfig current, IReadOnlyList<string> inputDevices,
                           Action<AppConfig> save, Func<AppConfig, Task> applyAsync)
    {
        Working = Clone(current);
        InputDevices = inputDevices;
        _save = save;
        _applyAsync = applyAsync;
    }

    private string _status = "";
    public string Status { get => _status; private set => SetField(ref _status, value); }

    private bool _isSaving;
    /// <summary>True while a save+apply is in flight. Bind the button's IsEnabled to its inverse.</summary>
    public bool IsSaving { get => _isSaving; private set => SetField(ref _isSaving, value); }

    /// <summary>Persist the working copy, then hot-apply it. Re-entrant calls (e.g. a double-click)
    /// are dropped while one is in flight, so ApplyConfigAsync never rebuilds modules concurrently.</summary>
    public async Task SaveAsync()
    {
        if (IsSaving) return;
        IsSaving = true;
        try
        {
            _save(Working);
            try
            {
                await _applyAsync(Working);
                Status = $"已保存并应用 · {DateTime.Now:HH:mm}";
            }
            catch (Exception ex)
            {
                Status = $"已保存，但应用失败: {ex.Message}";
            }
        }
        finally
        {
            IsSaving = false;
        }
    }

    /// <summary>Deep-copy via JSON round-trip (same options as the config file).</summary>
    internal static AppConfig Clone(AppConfig c)
    {
        var json = JsonSerializer.Serialize(c, ConfigManager.JsonOptions);
        return JsonSerializer.Deserialize<AppConfig>(json, ConfigManager.JsonOptions)
               ?? throw new InvalidOperationException("Failed to clone AppConfig via JSON round-trip.");
    }

    public event PropertyChangedEventHandler? PropertyChanged;

    private void SetField<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (EqualityComparer<T>.Default.Equals(field, value)) return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
