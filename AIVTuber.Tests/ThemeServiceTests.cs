using AIVTuber.Core.Ui;

namespace AIVTuber.Tests;

public class ThemeServiceTests
{
    [Fact]
    public void ThemeService_StartsDark_AndTogglesBetweenThemes()
    {
        var service = new ThemeService();

        Assert.Equal(AppTheme.Dark, service.CurrentTheme);

        service.Toggle();
        Assert.Equal(AppTheme.Light, service.CurrentTheme);

        service.Toggle();
        Assert.Equal(AppTheme.Dark, service.CurrentTheme);
    }

    [Fact]
    public void SetTheme_RaisesOneNotificationOnlyWhenThemeChanges()
    {
        var service = new ThemeService();
        var changes = new List<AppTheme>();
        service.ThemeChanged += theme => changes.Add(theme);

        service.SetTheme(AppTheme.Dark);
        service.SetTheme(AppTheme.Light);
        service.SetTheme(AppTheme.Light);

        Assert.Equal(AppTheme.Light, service.CurrentTheme);
        Assert.Equal([AppTheme.Light], changes);
    }
}
