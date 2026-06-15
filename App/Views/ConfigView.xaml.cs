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

        // PasswordBox doesn't support binding; copy values into the working config before saving.
        // An empty key box means "keep the existing key" (don't overwrite).
        if (!string.IsNullOrEmpty(LlmKey.Password)) vm.Working.Llm.ApiKey = LlmKey.Password;
        if (!string.IsNullOrEmpty(TtsKey.Password)) vm.Working.Tts.ApiKey = TtsKey.Password;
        if (!string.IsNullOrEmpty(AsrKey.Password)) vm.Working.Asr.ApiKey = AsrKey.Password;

        SaveButton.IsEnabled = false;
        try { await vm.SaveAsync(); }
        finally { SaveButton.IsEnabled = true; }
    }
}
