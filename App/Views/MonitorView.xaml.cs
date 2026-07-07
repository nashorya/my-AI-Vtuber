using System.Windows;
using System.Windows.Controls;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class MonitorView : UserControl
{
    public MonitorView() => InitializeComponent();

    private void OnMicMuteClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorViewModel vm)
            vm.ToggleMicMute();
    }

    private void OnStopSpeakingClicked(object sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorViewModel vm)
            vm.StopSpeaking();
    }

    private void OnRestartLocalAsr(object sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorViewModel vm)
            vm.RestartLocalAsrServer();
    }
}
