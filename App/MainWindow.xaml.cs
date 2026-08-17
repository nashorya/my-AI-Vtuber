using System.Windows;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.App.Views;

namespace AIVTuber.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MonitorViewModel _monitorVm;
    private readonly ConfigViewModel _configVm;
    private readonly MemoryViewModel _memoryVm;

    public MainWindow(BotRuntime runtime, ConfigManager configManager)
    {
        InitializeComponent();

        _monitorVm = new MonitorViewModel(runtime, action => Dispatcher.BeginInvoke(action));
        _configVm = new ConfigViewModel(
            runtime.CurrentConfig,
            MicrophoneCapture.ListDevices(),
            configManager.Save,
            runtime.ApplyConfigAsync,
            () => runtime.GetVtsHotkeysAsync());
        _memoryVm = new MemoryViewModel(runtime, action => Dispatcher.BeginInvoke(action));

        ConsoleHost.Attach(_monitorVm, _configVm, _memoryVm);

        FirstRunHost.ConfigureSectionRequested += (_, section) =>
        {
            FirstRunHost.Visibility = Visibility.Collapsed;
            ConsoleHost.Visibility = Visibility.Visible;
            _ = section;
        };
        FirstRunHost.SkipRequested += (_, _) => ShowConsolePage();
    }

    public void ShowFirstRunPage()
    {
        ConsoleHost.Visibility = Visibility.Collapsed;
        FirstRunHost.Visibility = Visibility.Visible;
    }

    public void ShowConfigPage(ConfigSection section = ConfigSection.QuickSetup)
    {
        ShowConsolePage();
        _ = section;
    }

    private void ShowConsolePage()
    {
        FirstRunHost.Visibility = Visibility.Collapsed;
        ConsoleHost.Visibility = Visibility.Visible;
    }
}
