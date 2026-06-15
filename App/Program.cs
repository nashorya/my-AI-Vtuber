using AIVTuber.Core.Config;
using AIVTuber.Core.Runtime;

namespace AIVTuber.App;

/// <summary>Main entry point: first-run wizard, then run the BotRuntime with a console loop.</summary>
public static class Program
{
    public static async Task Main(string[] args)
    {
        Console.OutputEncoding = System.Text.Encoding.UTF8;
        Console.WriteLine("=== AIVTuber ===\n");

        var configPath = Path.Combine(AppContext.BaseDirectory, "config.json");
        var configManager = new ConfigManager(configPath);
        var config = configManager.Load();

        if (IsFirstRun(config))
        {
            ConfigWizard.Run(config);
            configManager.Save(config);
            Console.WriteLine("配置已保存。请重启程序以使所有设置生效。按任意键退出...");
            Console.ReadKey();
            return;
        }

        await using var runtime = new BotRuntime(config, AppContext.BaseDirectory);
        await runtime.StartAsync();

        Console.WriteLine("\n=== AIVTuber 已启动 ===");
        Console.WriteLine("按 Ctrl+C 或输入 quit 退出\n");

        using var cts = new CancellationTokenSource();
        Console.CancelKeyPress += (_, e) => { e.Cancel = true; cts.Cancel(); };
        while (!cts.IsCancellationRequested)
        {
            if (Console.ReadLine()?.Trim().ToLowerInvariant() is "quit" or "exit")
            { cts.Cancel(); break; }
        }
        Console.WriteLine("正在关闭...");
    }

    private static bool IsFirstRun(AppConfig config)
        => string.IsNullOrEmpty(config.Llm.ApiKey) && string.IsNullOrEmpty(config.Tts.ApiKey);
}
