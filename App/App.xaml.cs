using System.Windows;
using AIVTuber.App.Views;
using AIVTuber.Core;
using AIVTuber.Core.Auth;
using AIVTuber.Core.Config;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.Ui;
using AIVTuber.Core.ViewModels;
using Wpf.Ui.Appearance;

namespace AIVTuber.App;

public partial class App : Application
{
    private BotRuntime? _runtime;
    private AuthApiClient? _authApi;
    private CloudLicense? _license;
    private MainWindow? _window;
    private AvatarWindow? _avatarWindow;
    private readonly ThemeService _themeService = new();
    private ResourceDictionary? _themeResources;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        _themeService.ThemeChanged += ApplyTheme;
        ApplyTheme(_themeService.CurrentTheme);

        // Global exception handlers so the process never dies silently.
        DispatcherUnhandledException += (_, args) =>
        {
            ShowFatalError("程序遇到了问题", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ShowFatalError("程序遇到了问题", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatalError("后台任务没有完成", args.Exception);
            args.SetObserved();
        };

        // Private packages carry distribution/profile.json; public builds do not (DIST-01).
        DistributionProfile? profile;
        try
        {
            profile = DistributionProfile.TryLoad(AppPaths.ContentRoot);
            if (profile is not null)
                CredentialRevisionGuard.Check(profile,
                    System.IO.Path.Combine(AppPaths.ContentRoot, CredentialRevisionGuard.StateFileName));
        }
        catch (DistributionProfileException ex)
        {
            MessageBox.Show(ex.Message, "AIVTuber 专属包", MessageBoxButton.OK, MessageBoxImage.Error);
            Shutdown(1);
            return;
        }

        var configPath = System.IO.Path.Combine(AppPaths.ContentRoot, "config.json");
        var configManager = new ConfigManager(configPath) { Profile = profile };
        var config = LoadConfigSafe(configManager, configPath);
        if (config is null)
        {
            Shutdown(1);
            return;
        }

        var firstRun = profile is null && FirstRunGuidance.NeedsGuidance(config);

        Console.WriteLine($"[App] content root: {AppPaths.ContentRoot}");
        Console.WriteLine($"[App] BaseDirectory: {AppContext.BaseDirectory}");

        Console.WriteLine($"[App] version: {AppVersion.Informational}");
        _runtime = new BotRuntime(config, AppPaths.ContentRoot);

        AccountViewModel? accountVm = null;
        if (profile is not null)
        {
            Console.WriteLine($"[App] distribution {profile.Describe()}");
            _authApi = new AuthApiClient(profile.AuthServerUri);
            _license = new CloudLicense(_authApi, profile.ProfileId, profile.CredentialRevision, AppVersion.Current);
            // Until login succeeds the runtime starts devices only; every cloud entry is gated.
            _runtime.UseCloudAccess(_license, profile);
            accountVm = new AccountViewModel(_license, profile.ProfileId, profile.Account,
                action => Dispatcher.BeginInvoke(action));
        }

        // Always show the window — even with no keys — so the user can configure in the UI.
        // Then init in the background.
        _window = new MainWindow(_runtime, configManager, accountVm);
        _window.Show();
        if (firstRun) _window.ShowFirstRunPage();

        _ = InitializeAsync(); // fire-and-forget; errors caught by global handlers
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try { _avatarWindow?.Close(); } catch { /* ignore */ }
        _avatarWindow = null;
        if (_license is not null)
        {
            // Local stop first; the server-side logout is best effort and bounded.
            try { await _license.LogoutAsync().WaitAsync(TimeSpan.FromSeconds(3)); } catch { /* ignore */ }
            await _license.DisposeAsync();
        }
        _authApi?.Dispose();
        if (_runtime is not null) await _runtime.DisposeAsync();
        base.OnExit(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _runtime!.StartAsync();
        }
        catch (Exception ex)
        {
            // Keep the window open (the user can fix config in the UI) — don't kill the app.
            ShowFatalError("启动没有完全完成（窗口保留，可检查设置后重启）", ex);
        }
        finally
        {
            // Pixel avatar may already be inited before a later StartAsync failure (e.g. VAD).
            Dispatcher.Invoke(OpenAvatarWindowIfNeeded);
        }
    }

    private void OpenAvatarWindowIfNeeded()
    {
        if (_runtime?.PixelAvatar is null) return;
        if (_avatarWindow is not null) return;

        try
        {
            _avatarWindow = new AvatarWindow(_runtime.PixelAvatar, _runtime.CurrentConfig.Avatar);
            _avatarWindow.Closed += (_, _) => _avatarWindow = null;
            _avatarWindow.Show();
        }
        catch (Exception ex)
        {
            ShowFatalError("形象窗口没有打开", ex);
        }
    }

    private static AppConfig? LoadConfigSafe(ConfigManager configManager, string configPath)
    {
        try
        {
            return configManager.Load();
        }
        catch (Exception ex)
        {
            // The path and parser detail go to the redacted diagnostic log, not the dialog (U06).
            var error = UserErrorMapper.Custom("config_unreadable", ErrorArea.Settings,
                "设置文件无法读取，可能已经损坏。", "把安装目录里的 config.json 改名备份后重新打开程序；仍有问题请联系支持",
                retryable: false, technicalDetail: $"{configPath}: {ex}");
            MessageBox.Show(
                $"{error.UserMessage}\n\n{error.SuggestedAction}。\n诊断编号：{error.DiagnosticId}",
                "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private static int _fatalDialogOpen;

    /// <summary>Shows what happened and a diagnostic id; the stack trace goes to the redacted
    /// diagnostic log (U06). Normal cancellations are not errors. While one dialog is open,
    /// further errors are logged but do not stack more dialogs.</summary>
    private static void ShowFatalError(string title, Exception? ex)
    {
        var error = ex is null
            ? UserErrorMapper.Custom("unknown", ErrorArea.App, UserErrorMapper.UnknownMessage, "重试", true, title)
            : UserErrorMapper.FromException(ex, ErrorArea.App);
        if (error is null) return;
        if (Interlocked.Exchange(ref _fatalDialogOpen, 1) == 1) return;
        try
        {
            MessageBox.Show(
                $"{title}\n\n{error.UserMessage}\n诊断编号：{error.DiagnosticId}\n\n可以在「账号与帮助 → 复制诊断信息」里把详细信息发给支持。",
                "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Error);
        }
        finally
        {
            Interlocked.Exchange(ref _fatalDialogOpen, 0);
        }
    }

    internal void ToggleTheme() => _themeService.Toggle();

    private void ApplyTheme(AppTheme theme)
    {
        if (_themeResources is not null)
            Resources.MergedDictionaries.Remove(_themeResources);

        _themeResources = new ResourceDictionary
        {
            Source = new Uri(
                theme == AppTheme.Light ? "Resources/Themes/Light.xaml" : "Resources/Themes/Dark.xaml",
                UriKind.Relative)
        };
        Resources.MergedDictionaries.Add(_themeResources);
        ApplicationThemeManager.Apply(theme == AppTheme.Light ? ApplicationTheme.Light : ApplicationTheme.Dark);
    }
}
