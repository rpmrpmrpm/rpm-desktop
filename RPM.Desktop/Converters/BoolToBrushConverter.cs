using Microsoft.UI.Xaml.Data;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Converters;

/// <summary>
/// Returns <see cref="TrueBrush"/> for true and <see cref="FalseBrush"/> for false. It lights the
/// current RPaMp playlist row in bright green, and the other rows stay in a dim green.
/// </summary>
public sealed partial class BoolToBrushConverter : IValueConverter
{
    public Brush TrueBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0xFF, 0x00, 0xF1, 0x5E));
    public Brush FalseBrush { get; set; } = new SolidColorBrush(Color.FromArgb(0xFF, 0x4E, 0x8A, 0x6E));

    public object Convert(object value, Type targetType, object parameter, string language)
        => value is true ? TrueBrush : FalseBrush;

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
