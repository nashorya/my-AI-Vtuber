using System.Net.Http;
using System.Windows;
using AIVTuber.Core;
using AIVTuber.Core.Audio;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;
using AIVTuber.Core.Voice;
using AIVTuber.App.Views;

namespace AIVTuber.App;

public partial class MainWindow : Wpf.Ui.Controls.FluentWindow
{
    private readonly MonitorViewModel _monitorVm;
    private readonly ConfigViewModel _configVm;
    private readonly MemoryViewModel _memoryVm;

    private readonly AccountViewModel? _accountVm;
    private static readonly HttpClient VoiceCatalogHttp = new() { Timeout = TimeSpan.FromSeconds(10) };

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
            () => runtime.ContinuousVts, runtime.ConnectContinuousVtsAsync)
        {
            Profile = runtime.Profile,
            ReadEffectiveConfig = () => runtime.CurrentConfig,
            ReadEffectiveRevision = () => runtime.ActiveConfigRevision,
            ReadApplyNotice = () => runtime.LastApplyNotice,
        };
        _configVm.VoiceNotice = configManager.LastLoadNotice;
        _memoryVm = new MemoryViewModel(runtime, action => Dispatcher.BeginInvoke(action));

        // Distribution builds get the streamer console; public builds keep the developer console.
        Func<Action<object>, StreamerConsoleController>? streamerFactory = runtime.DistributionMode
            ? sink => new StreamerConsoleController(
                runtime, _monitorVm, _configVm, _accountVm,
                runtime.CreateVoicePreview(),
                runtime.CreateVoiceCatalog(provider => VoiceCatalogService.DefaultSource(provider, VoiceCatalogHttp)),
                sink,
                text => Dispatcher.BeginInvoke(() => { try { Clipboard.SetText(text); } catch { /* clipboard busy */ } }),
                $"v{AppVersion.Current}")
            : null;
        ConsoleHost.Attach(_monitorVm, _configVm, _memoryVm, streamerFactory);

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
        // The standard WPF WebView2 is an HwndHost and paints above any WPF element in the same
        // area regardless of z-order (airspace). Login and console are therefore never shown
        // together: the web layer is collapsed while the login page is up (U04).
        var showLogin = vm.ShowLogin;
        var wasShowingLogin = LoginHost.Visibility == Visibility.Visible;
        LoginHost.Visibility = showLogin ? Visibility.Visible : Visibility.Collapsed;
        ConsoleHost.Visibility = showLogin ? Visibility.Collapsed : Visibility.Visible;
        if (showLogin && !wasShowingLogin) LoginHost.FocusFirstField();
        else if (!showLogin && wasShowingLogin) ConsoleHost.Focus();

        // Profile id, version and credential revision live in 帮助 → 诊断信息, not here.
        AccountStatusText.Text = vm.IsSignedIn
            ? $"已登录 {vm.Username}{(vm.ValidUntilText.Length > 0 ? " · " + vm.ValidUntilText : "")}"
            : $"未登录：陪播不会连接任何云端服务{(vm.ErrorText.Length > 0 ? "（" + vm.ErrorText + "）" : "")}";
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
        if (_accountVm is { ShowLogin: true }) return; // never under the login page (airspace)
        ConsoleHost.Visibility = Visibility.Visible;
    }
}
