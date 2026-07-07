using System.Windows;
using System.Windows.Controls;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class ConfigView : UserControl
{
    public ConfigView() => InitializeComponent();

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;

        // PasswordBox.Password isn't bindable; copy values before saving.
        // An empty box means "keep the existing key".
        if (!string.IsNullOrEmpty(LlmKey.Password)) vm.Working.Llm.ApiKey = LlmKey.Password;
        if (!string.IsNullOrEmpty(TtsKey.Password)) vm.Working.Tts.ApiKey = TtsKey.Password;
        if (!string.IsNullOrEmpty(AsrKey.Password)) vm.Working.Asr.ApiKey = AsrKey.Password;
        if (!string.IsNullOrEmpty(BiliSessdata.Password)) vm.Working.Bilibili.Sessdata = BiliSessdata.Password;
        if (!string.IsNullOrEmpty(BiliJct.Password)) vm.Working.Bilibili.BiliJct = BiliJct.Password;
        if (!string.IsNullOrEmpty(BiliBuvid3.Password)) vm.Working.Bilibili.Buvid3 = BiliBuvid3.Password;

        SaveButton.IsEnabled = false;
        try { await vm.SaveAsync(); }
        finally { SaveButton.IsEnabled = true; }
    }

    private async void OnQueryVtsHotkeys(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        await vm.QueryVtsHotkeysAsync();
    }

    private async void OnRefreshLoopbackSources(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        await vm.RefreshLoopbackSourcesAsync();
    }

    private void OnRefreshOutputDevices(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        vm.RefreshOutputDevices();
    }

    private void OnAddEmotionRow(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        vm.AddEmotionRow();
    }

    private void OnRemoveEmotionRow(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        if (sender is FrameworkElement { Tag: EmotionMapRow row }) vm.RemoveEmotionRow(row);
    }
}
