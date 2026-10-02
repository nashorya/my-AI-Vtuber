using System.Windows;
using System.Windows.Controls;
using AIVTuber.App.WebUi;
using AIVTuber.Core.Diagnostics;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class WebConsoleView : UserControl
{
    private WebConsoleHost? _host;
    private MonitorViewModel? _monitor;
    private ConfigViewModel? _config;
    private MemoryViewModel? _memory;
    private Func<Action<object>, StreamerConsoleController>? _streamerFactory;
    private StreamerConsoleController? _streamer;

    public WebConsoleView()
    {
        InitializeComponent();
        Loaded += OnLoaded;
        Unloaded += OnUnloaded;
    }

    /// <param name="streamerFactory">Distribution builds: builds the streamer console controller
    /// around a sink that posts to the page. When set, the streamer page is loaded instead of the
    /// developer console.</param>
    public void Attach(MonitorViewModel monitor, ConfigViewModel config, MemoryViewModel memory,
        Func<Action<object>, StreamerConsoleController>? streamerFactory = null)
    {
        _monitor = monitor;
        _config = config;
        _memory = memory;
        _streamerFactory = streamerFactory;
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
            FallbackText.Text = "安装目录缺少界面文件（WebUi/wwwroot），请重新解压安装包。";
            return;
        }

        try
        {
            _streamer ??= _streamerFactory?.Invoke(payload => _host?.PostFromController(payload));
            _host = new WebConsoleHost(Browser, _monitor, _config, _memory, wwwroot, _streamer);
            await _host.InitializeAsync();
            FallbackText.Visibility = Visibility.Collapsed;
        }
        catch (Exception ex)
        {
            var error = UserErrorMapper.FromException(ex, ErrorArea.App);
            FallbackText.Text = "界面组件没有加载成功，请安装或修复 Microsoft Edge WebView2 Runtime 后重启。" +
                (error is null ? "" : $"\n诊断编号 {error.DiagnosticId}");
        }
    }

    private void OnUnloaded(object sender, RoutedEventArgs e)
    {
        _host?.Dispose();
        _host = null;
    }
}
