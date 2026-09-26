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

    private readonly AccountViewModel? _accountVm;

    public MainWindow(BotRuntime runtime, ConfigManager configManager, AccountViewModel? accountVm = null)
    {
        InitializeComponent();
        _accountVm = accountVm;

        _monitorVm = new MonitorViewModel(runtime, action => Dispatcher.BeginInvoke(action));
        _configVm = new ConfigViewModel(
            runtime.CurrentConfig,
            MicrophoneCapture.ListDevices(),
            configManager.Save,
            runtime.ApplyConfigAsync,
            () => runtime.GetVtsHotkeysAsync(),
            () => runtime.ContinuousVts, runtime.ConnectContinuousVtsAsync);
        _memoryVm = new MemoryViewModel(runtime, action => Dispatcher.BeginInvoke(action));

        ConsoleHost.Attach(_monitorVm, _configVm, _memoryVm);

        FirstRunHost.ConfigureSectionRequested += (_, section) =>
        {
            FirstRunHost.Visibility = Visibility.Collapsed;
            ConsoleHost.Visibility = Visibility.Visible;
            _ = section;
        };
        FirstRunHost.SkipRequested += (_, _) => ShowConsolePage();

        if (_accountVm is not null)
        {
            LoginHost.DataContext = _accountVm;
            AccountStrip.Visibility = Visibility.Visible;
            _accountVm.PropertyChanged += (_, _) => RefreshAccount();
            RefreshAccount();
        }
    }

    private void RefreshAccount()
    {
        if (_accountVm is not { } vm) return;
        LoginHost.Visibility = vm.ShowLogin ? Visibility.Visible : Visibility.Collapsed;
        AccountStatusText.Text = vm.IsSignedIn
            ? $"已登录 {vm.Username} · {vm.ValidUntilText} · 专属包 {vm.ProfileId} · {vm.VersionText}"
            : $"未登录：不会连接任何云端服务{(vm.ErrorText.Length > 0 ? "（" + vm.ErrorText + "）" : "")}";
        AccountActionButton.Content = vm.IsSignedIn ? "退出登录" : "登录";
    }

    private async void OnAccountAction(object sender, RoutedEventArgs e)
    {
        if (_accountVm is not { } vm) return;
        if (vm.IsSignedIn) await vm.LogoutAsync();
        else vm.ReopenLogin();
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
