using System.Windows;
using System.Windows.Controls;
using System.Windows.Controls.Primitives;
using System.Windows.Input;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class ConfigView : UserControl
{
    public static readonly RoutedUICommand SaveCommand = new("保存并应用", nameof(SaveCommand), typeof(ConfigView));
    public static readonly RoutedUICommand DiscardCommand = new("放弃草稿", nameof(DiscardCommand), typeof(ConfigView));

    public ConfigView()
    {
        InitializeComponent();
        CommandBindings.Add(new CommandBinding(SaveCommand, async (_, _) => await SaveDraftAsync(), (_, e) => e.CanExecute = DataContext is ConfigViewModel { IsSaving: false }));
        CommandBindings.Add(new CommandBinding(DiscardCommand, (_, _) => DiscardDraft(), (_, e) => e.CanExecute = DataContext is ConfigViewModel { IsSaving: false }));
        AddHandler(TextBoxBase.TextChangedEvent, new TextChangedEventHandler(OnDraftControlChanged));
        AddHandler(Selector.SelectionChangedEvent, new SelectionChangedEventHandler(OnDraftSelectionChanged));
        AddHandler(ToggleButton.CheckedEvent, new RoutedEventHandler(OnDraftControlChanged));
        AddHandler(ToggleButton.UncheckedEvent, new RoutedEventHandler(OnDraftControlChanged));
    }

    public void ShowSection(ConfigSection section) => SettingsSections.SelectedIndex = (int)section;

    private async void OnSave(object sender, RoutedEventArgs e)
    {
        await SaveDraftAsync();
    }

    private async Task SaveDraftAsync()
    {
        if (DataContext is not ConfigViewModel vm) return;

        // PasswordBox.Password isn't bindable; copy values before saving.
        // An empty box means "keep the existing key".
        if (!string.IsNullOrEmpty(LlmKey.Password)) vm.Working.Llm.ApiKey = LlmKey.Password;
        if (!string.IsNullOrEmpty(TtsKey.Password)) vm.Working.Tts.ApiKey = TtsKey.Password;
        if (!string.IsNullOrEmpty(AsrKey.Password)) vm.Working.Asr.ApiKey = AsrKey.Password;
        if (!string.IsNullOrEmpty(ObsPassword.Password)) vm.Working.Obs.Password = ObsPassword.Password;
        if (!string.IsNullOrEmpty(BiliSessdata.Password)) vm.Working.Bilibili.Sessdata = BiliSessdata.Password;
        if (!string.IsNullOrEmpty(BiliJct.Password)) vm.Working.Bilibili.BiliJct = BiliJct.Password;
        if (!string.IsNullOrEmpty(BiliBuvid3.Password)) vm.Working.Bilibili.Buvid3 = BiliBuvid3.Password;

        vm.NotifyDraftChanged();
        await vm.SaveAsync();
    }

    private void OnDiscard(object sender, RoutedEventArgs e) => DiscardDraft();

    private void OnDraftControlChanged(object sender, RoutedEventArgs e)
        => (DataContext as ConfigViewModel)?.NotifyDraftChanged();

    private void OnDraftSelectionChanged(object sender, SelectionChangedEventArgs e)
        => (DataContext as ConfigViewModel)?.NotifyDraftChanged();

    private void DiscardDraft()
    {
        if (DataContext is not ConfigViewModel vm) return;
        vm.DiscardChanges();
        LlmKey.Password = "";
        TtsKey.Password = "";
        AsrKey.Password = "";
        ObsPassword.Password = "";
        BiliSessdata.Password = "";
        BiliJct.Password = "";
        BiliBuvid3.Password = "";
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

    private void OnAddActionRow(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigViewModel vm) vm.AddActionRow();
    }

    private void OnRemoveActionRow(object sender, RoutedEventArgs e)
    {
        if (DataContext is not ConfigViewModel vm) return;
        if (sender is FrameworkElement { Tag: ActionMapRow row }) vm.RemoveActionRow(row);
    }

    private void OnImportAnimationHotkeys(object sender, RoutedEventArgs e)
    {
        if (DataContext is ConfigViewModel vm) vm.ImportAnimationHotkeys();
    }
}

public enum ConfigSection
{
    QuickSetup,
    AiEngine,
    Voice,
    LiveIntegrations,
    CharacterAndActions,
    Advanced
}
