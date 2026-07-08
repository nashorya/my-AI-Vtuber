using System.Windows;
using System.Windows.Controls;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class MemoryPlaceholderView : UserControl
{
    public MemoryPlaceholderView() => InitializeComponent();

    private MemoryViewModel Vm => (MemoryViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e)
        => await Vm.LoadAsync();

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await Vm.RefreshFactsAsync();
        await Vm.RefreshViewersAsync();
    }

    private async void OnExtractClicked(object sender, RoutedEventArgs e)
        => await Vm.ForceExtractAsync();

    private void OnToggleViewClicked(object sender, RoutedEventArgs e)
    {
        bool showingFacts = FactsPanel.Visibility == Visibility.Visible;
        FactsPanel.Visibility = showingFacts ? Visibility.Collapsed : Visibility.Visible;
        ViewersPanel.Visibility = showingFacts ? Visibility.Visible : Visibility.Collapsed;
    }

    private async void OnDeleteFactClicked(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement fe && fe.DataContext is FactRowViewModel row)
        {
            row.Delete();
            await Task.Delay(200);
            await Vm.RefreshFactsAsync();
        }
    }
}
