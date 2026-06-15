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
