using System.Windows;
using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.App;

public partial class App : Application
{
    private BotRuntime? _runtime;

    protected override async void OnStartup(StartupEventArgs e)
    {
        base.OnStartup(e);

        var configPath = System.IO.Path.Combine(AppContext.BaseDirectory, "config.json");
        var configManager = new ConfigManager(configPath);
        var config = configManager.Load();

        if (IsFirstRun(config))
        {
            MessageBox.Show("尚未配置 API key，请先编辑 config.json（Phase 2 将提供配置页）。",
                "AIVTuber", MessageBoxButton.OK, MessageBoxImage.Information);
            Shutdown();
            return;
        }

        _runtime = new BotRuntime(config, AppContext.BaseDirectory);
        await _runtime.StartAsync();

        var window = new MainWindow(_runtime);
        window.Show();
    }

    protected override async void OnExit(ExitEventArgs e)
    {
        if (_runtime is not null) await _runtime.DisposeAsync();
        base.OnExit(e);
    }

    private static bool IsFirstRun(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
