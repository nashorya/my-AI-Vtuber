using System.Windows;
using System.Windows.Controls;

namespace AIVTuber.App.Views;

public partial class FirstRunView : UserControl
{
    public event EventHandler<ConfigSection>? ConfigureSectionRequested;
    public event EventHandler? SkipRequested;

    public FirstRunView() => InitializeComponent();

    private void OnConfigureSection(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: string section }
            && Enum.TryParse<ConfigSection>(section, out var configSection))
            ConfigureSectionRequested?.Invoke(this, configSection);
    }

    private void OnSkip(object sender, RoutedEventArgs e) => SkipRequested?.Invoke(this, EventArgs.Empty);
}
