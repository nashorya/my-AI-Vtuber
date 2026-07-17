using System.Windows;
using System.Windows.Controls;
using AIVTuber.App.Views;

[assembly: CollectionBehavior(DisableTestParallelization = true)]

namespace AIVTuber.App.Tests;

public sealed class LayoutSmokeTests
{
    [Fact]
    public void PrimaryViews_LoadResourcesAndArrangeAtSupportedSizes()
    {
        RunOnSta(() =>
        {
            var application = new AIVTuber.App.App();
            application.InitializeComponent();

            foreach (var themeName in new[] { "Light", "Dark" })
            {
                var themeResources = new ResourceDictionary
                {
                    Source = new Uri($"pack://application:,,,/AIVTuber;component/Resources/Themes/{themeName}.xaml", UriKind.Absolute)
                };
                application.Resources.MergedDictionaries.Add(themeResources);
                foreach (var resourceKey in new[] { "DeckPanelBrush", "DeckPanelBorderBrush", "DeckAccentBrush" })
                    Assert.True(themeResources.Contains(resourceKey), $"Missing theme resource: {resourceKey}");

                foreach (var available in new[] { new Size(760, 520), new Size(980, 680), new Size(1440, 900) })
                {
                    foreach (var createView in new Func<UserControl>[]
                             { () => new FirstRunView(), () => new ConfigView(), () => new MonitorView(), () => new MemoryView() })
                    {
                        var view = createView();
                        view.Measure(available);
                        view.Arrange(new Rect(available));
                        view.UpdateLayout();

                        Assert.Equal(available.Width, view.ActualWidth);
                        Assert.Equal(available.Height, view.ActualHeight);
                        Assert.False(double.IsNaN(view.DesiredSize.Width));
                        Assert.False(double.IsNaN(view.DesiredSize.Height));
                    }
                }
                application.Resources.MergedDictionaries.Remove(themeResources);
            }
        });
    }

    private static void RunOnSta(Action action)
    {
        Exception? failure = null;
        var thread = new Thread(() =>
        {
            try { action(); }
            catch (Exception ex) { failure = ex; }
            finally { Application.Current?.Shutdown(); }
        });
        thread.SetApartmentState(ApartmentState.STA);
        thread.Start();
        thread.Join();

        if (failure is not null) throw new Xunit.Sdk.XunitException(failure.ToString());
    }
}
