using System.Windows;
using System.Windows.Controls;
using AIVTuber.App.WebUi;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class WebConsoleView : UserControl
{
    private WebConsoleHost? _host;
    private MonitorViewModel? _monitor;
    private ConfigViewModel? _config;
    private MemoryViewModel? _memory;

    public WebConsoleView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    public void Attach(MonitorViewModel monitor, ConfigViewModel config, MemoryViewModel memory)
    {
        _monitor = monitor;
        _config = config;
        _memory = memory;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        if (_host is not null) return;
        if (_monitor is null || _config is null || _memory is null)
        {
            FallbackText.Text = "控制台未绑定。";
            return;
        }

        var wwwroot = WebConsoleHost.ResolveWwwroot();
        if (wwwroot is null)
        {
            FallbackText.Text = "找不到 WebUi/wwwroot。";
            return;
        }

        try
        {
            _host = new WebConsoleHost(Browser, _monitor, _config, _memory, wwwroot);
            await _host.InitializeAsync();
            FallbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            FallbackText.Text = "WebView2 初始化失败。请安装 Edge WebView2 Runtime。\n" + ex.Message;
            AIVTuber.Core.Diagnostics.DebugLog.Write($"[WebConsole] init failed: {ex}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _host?.Dispose();
        _host = null;
    }
}
