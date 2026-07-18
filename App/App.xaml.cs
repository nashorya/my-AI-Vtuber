using System.Windows;
using AIVTuber.App.Views;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.Ui;
using Wpf.Ui.Appearance;

namespace AIVTuber.App;

public partial class App : Application
{
    private BotRuntime? _runtime;
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
            ShowFatalError("UI 线程错误", args.Exception);
            args.Handled = true;
        };
        AppDomain.CurrentDomain.UnhandledException += (_, args) =>
        {
            ShowFatalError("未处理异常", args.ExceptionObject as Exception);
        };
        TaskScheduler.UnobservedTaskException += (_, args) =>
        {
            ShowFatalError("后台任务错误", args.Exception);
            args.SetObserved();
        };

        var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
        var configManager = new ConfigManager(configPath);
        var config = LoadConfigSafe(configManager, configPath);
        if (config is null)
        {
            Shutdown(1);
            return;
        }

        var firstRun = FirstRunGuidance.NeedsGuidance(config);

        _runtime = new BotRuntime(config, AppContext.BaseDirectory);

        // Always show the window — even with no keys — so the user can configure in the UI.
        // Then init in the background.
        _window = new MainWindow(_runtime, configManager);
        _window.Show();
        if (firstRun) _window.ShowFirstRunPage();

        _ = InitializeAsync(); // fire-and-forget; errors caught by global handlers
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        try { _avatarWindow?.Close(); } catch { /* ignore */ }
        _avatarWindow = null;
        if (_runtime is not null) await _runtime.DisposeAsync();
        base.OnExit(e);
    }

    private async Task InitializeAsync()
    {
        try
        {
            await _runtime!.StartAsync();
            Dispatcher.Invoke(OpenAvatarWindowIfNeeded);
        }
        catch (Exception ex)
        {
            // Keep the window open (the user can fix config in the UI) — don't kill the app.
            ShowFatalError("启动失败（窗口保留，可在配置页修改后重启）", ex);
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
            ShowFatalError("形象窗口打开失败", ex);
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
            MessageBox.Show(
                $"无法加载配置文件：{ex.Message}\n\n路径：{configPath}",
                "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Error);
            return null;
        }
    }

    private static void ShowFatalError(string title, Exception? ex)
    {
        MessageBox.Show(
            $"{title}\n\n{ex?.ToString() ?? "未知错误"}",
            "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Error);
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
