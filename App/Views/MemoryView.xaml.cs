using System.Windows;
using System.Windows.Controls;
using AIVTuber.Core.ViewModels;
using Wpf.Ui.Controls;

namespace AIVTuber.App.Views;

public partial class MemoryView : UserControl
{
    private bool _deleteDialogOpen;

    public MemoryView() => InitializeComponent();

    private MemoryViewModel Vm => (MemoryViewModel)DataContext;

    private async void OnLoaded(object sender, RoutedEventArgs e) => await Vm.LoadAsync();

    private async void OnRefreshClicked(object sender, RoutedEventArgs e)
    {
        await Vm.RefreshFactsAsync();
        await Vm.RefreshViewersAsync();
    }

    private async void OnExtractClicked(object sender, RoutedEventArgs e) => await Vm.ForceExtractAsync();

    private async void OnTabSelectionChanged(object sender, SelectionChangedEventArgs e)
    {
        if (!IsLoaded || e.AddedItems.OfType<TabViewItem>().FirstOrDefault() is not { Tag: string tab }) return;
        await Vm.ActivateTabAsync(tab == "Facts" ? MemoryTab.Facts : MemoryTab.Viewers);
    }

    private async void OnDeleteFactClicked(object sender, RoutedEventArgs e)
    {
        if (_deleteDialogOpen || sender is not FrameworkElement { Tag: FactRowViewModel row }) return;
        _deleteDialogOpen = true;
        try
        {
            var dialog = new ContentDialog(DialogPresenter)
            {
                Title = "删除事实",
                Content = "确定删除这条事实记忆吗？此操作不可撤销。",
                PrimaryButtonText = "删除",
                CloseButtonText = "取消",
                PrimaryButtonAppearance = ControlAppearance.Danger
            };

            if (await dialog.ShowAsync() == ContentDialogResult.Primary)
                await row.DeleteAsync();
        }
        finally { _deleteDialogOpen = false; }
    }
}
