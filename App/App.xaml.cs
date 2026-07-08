using System.Windows;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;
using Wpf.Ui.Appearance;

namespace AIVTuber.App;

public partial class App : Application
{
    private BotRuntime? _runtime;
    private MainWindow? _window;

    protected override void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        ApplicationThemeManager.Apply(ApplicationTheme.Dark);

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

        var firstRun = IsFirstRun(config);

        _runtime = new BotRuntime(config, AppContext.BaseDirectory);

        // Always show the window — even with no keys — so the user can configure in the UI.
        // Then init in the background.
        _window = new MainWindow(_runtime, configManager);
        _window.Show();
        if (firstRun) _window.ShowConfigPage(); // land on the config tab to enter keys in the UI

        _ = InitializeAsync(); // fire-and-forget; errors caught by global handlers
    }

    protected override async void OnExit(ExitEventArgs e)
    {
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
            ShowFatalError("启动失败（窗口保留，可在配置页修改后重启）", ex);
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

    private static bool IsFirstRun(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
