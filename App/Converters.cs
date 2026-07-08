using System.Globalization;
using System.Windows.Data;
using System.Windows.Media;
using AIVTuber.Core.Runtime;

namespace AIVTuber.App;

[ValueConversion(typeof(PipelineState), typeof(string))]
internal sealed class PipelineStateToLabelConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => (PipelineState)value switch
        {
            PipelineState.Listening => "监听中",
            PipelineState.Thinking  => "思考中",
            PipelineState.Speaking  => "说话中",
            _                       => "闲置",
        };

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(string), typeof(string))]
internal sealed class KeySetStatusConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? "已设置 ✓" : "未设置";

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(string), typeof(Brush))]
internal sealed class KeySetBrushConverter : IValueConverter
{
    private static readonly Brush Set   = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Unset = new SolidColorBrush(Color.FromRgb(0x88, 0x88, 0x88));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is string s && s.Length > 0 ? Set : Unset;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(bool))]
internal sealed class InverseBoolConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => value is false;
}

/// <summary>Maps mic RMS [0,1] to pixel width [0,60] for the level bar.</summary>
[ValueConversion(typeof(float), typeof(double))]
internal sealed class MicLevelToWidthConverter : IValueConverter
{
    private const double MaxWidth = 60.0;

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
    {
        double level = value is float f ? f : 0.0;
        // Amplify: typical speech RMS is 0.01–0.1, scale so 0.05 fills ~half the bar
        level = Math.Min(level * 10.0, 1.0);
        return Math.Max(level * MaxWidth, 0.0);
    }

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}

[ValueConversion(typeof(bool), typeof(Brush))]
internal sealed class BoolToGreenGrayBrushConverter : IValueConverter
{
    private static readonly Brush Green = new SolidColorBrush(Color.FromRgb(0x4C, 0xAF, 0x50));
    private static readonly Brush Gray  = new SolidColorBrush(Color.FromRgb(0x55, 0x55, 0x55));

    public object Convert(object value, Type targetType, object parameter, CultureInfo culture)
        => value is true ? Green : Gray;

    public object ConvertBack(object value, Type targetType, object parameter, CultureInfo culture)
        => throw new NotSupportedException();
}
