using System.Windows;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.App.Views;

namespace AIVTuber.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MonitorView _monitorView;
    private readonly ConfigView _configView;
    private readonly MemoryPlaceholderView _memoryView = new();

    public MainWindow(BotRuntime runtime, ConfigManager configManager)
    {
        InitializeComponent();

        _monitorView = new MonitorView();
        _monitorView.DataContext = new MonitorViewModel(
            runtime, action => Dispatcher.Invoke(action));

        _configView = new ConfigView();
        _configView.DataContext = new ConfigViewModel(
            runtime.CurrentConfig,
            MicrophoneCapture.ListDevices(),
            configManager.Save,
            runtime.ApplyConfigAsync);
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        var svc = new PageService();
        svc.Register(typeof(MonitorView), _monitorView);
        svc.Register(typeof(ConfigView), _configView);
        svc.Register(typeof(MemoryPlaceholderView), _memoryView);
        RootNavigation.SetPageService(svc);
        RootNavigation.Navigate(typeof(MonitorView));
    }

    public void ShowConfigPage() => RootNavigation.Navigate(typeof(ConfigView));
}
