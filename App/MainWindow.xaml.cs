using System.Windows;
using AIVTuber.Core.Runtime;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App;

public partial class MainWindow : Window
{
    public MainWindow(BotRuntime runtime)
    {
        InitializeComponent();
        // Marshal VM updates onto the UI thread via the window's Dispatcher.
        var vm = new MonitorViewModel(runtime, action => Dispatcher.Invoke(action));
        MonitorView.DataContext = vm;
    }
}
