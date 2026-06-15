using System.Windows;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App;

public partial class MainWindow : Window
{
    public MainWindow(BotRuntime runtime, ConfigManager configManager)
    {
        InitializeComponent();

        // Marshal VM updates onto the UI thread via the window's Dispatcher.
        MonitorView.DataContext = new MonitorViewModel(runtime, action => Dispatcher.Invoke(action));

        ConfigView.DataContext = new ConfigViewModel(
            runtime.CurrentConfig,
            MicrophoneCapture.ListDevices(),
            configManager.Save,
            runtime.ApplyConfigAsync);
    }

    /// <summary>Switch to the config tab (used on first run so the user can enter keys in the UI).</summary>
    public void ShowConfigTab() => Tabs.SelectedIndex = 1;
}
