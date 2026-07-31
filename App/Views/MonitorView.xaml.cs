using System.Windows;
using System.Windows.Controls;
using System.Collections.Specialized;
using System.Windows.Threading;
using AIVTuber.Core.Ui;
using AIVTuber.Core.ViewModels;

namespace AIVTuber.App.Views;

public partial class MonitorView : UserControl
{
    private readonly EventSubscription<MonitorViewModel> _eventSubscription = new();
    private readonly FollowLatestScrollPolicy _followLatestScrollPolicy = new();

    public MonitorView()
    {
        InitializeComponent();
        DataContextChanged += OnDataContextChanged;
        Loaded += (_, _) => SubscribeToEvents(DataContext as MonitorViewModel);
        Unloaded += (_, _) => _eventSubscription.Clear(DetachEventHandler);
    }

    private void OnDataContextChanged(object sender, DependencyPropertyChangedEventArgs e) =>
        SubscribeToEvents(e.NewValue as MonitorViewModel);

    private void SubscribeToEvents(MonitorViewModel? viewModel)
    {
        _eventSubscription.Reconcile(viewModel, AttachEventHandler, DetachEventHandler);
    }

    private void AttachEventHandler(MonitorViewModel viewModel) =>
        viewModel.OperationalEvents.CollectionChanged += OnOperationalEventsChanged;

    private void DetachEventHandler(MonitorViewModel viewModel) =>
        viewModel.OperationalEvents.CollectionChanged -= OnOperationalEventsChanged;

    private void OnOperationalEventsChanged(object? sender, NotifyCollectionChangedEventArgs e)
    {
        if (DataContext is MonitorViewModel { FollowLatest: true })
            ScrollToLatest();
    }

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

    private void OnAvatarEmotion(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        if (sender is FrameworkElement { Tag: string emotion })
            vm.TriggerAvatarEmotion(emotion);
    }

    private void OnAvatarIdle(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        if (sender is FrameworkElement { Tag: string state })
            vm.TriggerAvatarIdle(state);
    }

    private void OnAvatarSticker(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        var id = (sender as FrameworkElement)?.Tag as string ?? "sweat_laugh";
        vm.TriggerAvatarSticker(id);
    }

    private void OnAvatarRms(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        if (sender is FrameworkElement { Tag: string tag } &&
            float.TryParse(tag, System.Globalization.NumberStyles.Float,
                System.Globalization.CultureInfo.InvariantCulture, out var rms))
            vm.TriggerAvatarRms(rms);
    }

    private bool _avatarListeningOn;

    private void OnAvatarListening(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        _avatarListeningOn = !_avatarListeningOn;
        vm.TriggerAvatarListening(_avatarListeningOn);
        if (AvatarListeningButton is not null)
            AvatarListeningButton.Content = _avatarListeningOn ? "倾听 OFF" : "倾听 ON";
    }

    private void OnAvatarPose(object sender, RoutedEventArgs e)
    {
        if (DataContext is not MonitorViewModel vm) return;
        if (sender is FrameworkElement { Tag: string poseId })
            vm.TriggerAvatarPose(poseId);
    }

    private void OnOperationalEventScrollChanged(object sender, ScrollChangedEventArgs e)
    {
        if (_followLatestScrollPolicy.ShouldPauseFollowing(e.VerticalChange) &&
            DataContext is MonitorViewModel vm)
            vm.PauseFollowLatest();
    }

    private void OnReturnToLatest(object sender, RoutedEventArgs e)
    {
        if (DataContext is MonitorViewModel vm)
            vm.ReturnToLatest();
        ScrollToLatest();
    }

    private void OnContentViewportSizeChanged(object sender, SizeChangedEventArgs e)
    {
        MonitorPanels.Width = MonitorLayoutPolicy.ContentWidth(e.NewSize.Width);
    }

    private void ScrollToLatest()
    {
        _followLatestScrollPolicy.BeginProgrammaticScroll();
        OperationalEventScrollViewer.ScrollToTop();
        Dispatcher.BeginInvoke(
            _followLatestScrollPolicy.CompleteProgrammaticScroll,
            DispatcherPriority.Background);
    }
}
