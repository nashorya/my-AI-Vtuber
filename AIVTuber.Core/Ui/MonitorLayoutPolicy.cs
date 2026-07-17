namespace AIVTuber.Core.Ui;

/// <summary>Defines the monitor panels' side-by-side width contract.</summary>
public static class MonitorLayoutPolicy
{
    public const double PrimaryPanelWidth = 620;
    public const double SecondaryPanelWidth = 300;
    public const double PanelGap = 12;
    public const double SideBySideWidth = PrimaryPanelWidth + SecondaryPanelWidth + PanelGap;

    public static bool ShouldStackPanels(double availableWidth) => availableWidth < SideBySideWidth;

    public static double ContentWidth(double availableWidth) => Math.Max(0, availableWidth);
}
