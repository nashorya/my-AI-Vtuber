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
    private readonly MemoryView _memoryView;
    private readonly FirstRunView _firstRunView;
    private bool _navigationReady;
    private bool _showFirstRun;

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
            runtime.ApplyConfigAsync,
            () => runtime.GetVtsHotkeysAsync());

        _memoryView = new MemoryView();
        _memoryView.DataContext = new MemoryViewModel(
            runtime, action => Dispatcher.Invoke(action));

        _firstRunView = new FirstRunView();
        _firstRunView.ConfigureSectionRequested += (_, section) => ShowConfigPage(section);
        _firstRunView.SkipRequested += (_, _) => ShowMonitorPage();
    }

    private void OnNavigationLoaded(object sender, RoutedEventArgs e)
    {
        var svc = new PageService();
        svc.Register(typeof(MonitorView), _monitorView);
        svc.Register(typeof(ConfigView), _configView);
        svc.Register(typeof(MemoryView), _memoryView);
        svc.Register(typeof(FirstRunView), _firstRunView);
        RootNavigation.SetPageService(svc);
        _navigationReady = true;
        RootNavigation.Navigate(_showFirstRun ? typeof(FirstRunView) : typeof(MonitorView));
    }

    public void ShowFirstRunPage()
    {
        _showFirstRun = true;
        if (_navigationReady) RootNavigation.Navigate(typeof(FirstRunView));
    }

    public void ShowConfigPage(ConfigSection section = ConfigSection.QuickSetup)
    {
        _showFirstRun = false;
        _configView.ShowSection(section);
        if (_navigationReady) RootNavigation.Navigate(typeof(ConfigView));
    }

    private void ShowMonitorPage()
    {
        _showFirstRun = false;
        if (_navigationReady) RootNavigation.Navigate(typeof(MonitorView));
    }

    private void OnThemeToggle(object sender, RoutedEventArgs e)
        => ((App)Application.Current).ToggleTheme();
}
