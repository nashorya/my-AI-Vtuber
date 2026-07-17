namespace AIVTuber.Core.Ui;

public enum AppTheme
{
    Light,
    Dark
}

/// <summary>Tracks the running application's visual theme without owning WPF resources.</summary>
public sealed class ThemeService
{
    public AppTheme CurrentTheme { get; private set; } = AppTheme.Dark;

    public event Action<AppTheme>? ThemeChanged;

    public void Toggle() => SetTheme(CurrentTheme == AppTheme.Dark ? AppTheme.Light : AppTheme.Dark);

    public void SetTheme(AppTheme theme)
    {
        if (CurrentTheme == theme)
            return;

        CurrentTheme = theme;
        ThemeChanged?.Invoke(theme);
    }
}
