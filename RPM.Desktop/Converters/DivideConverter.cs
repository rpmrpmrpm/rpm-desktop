using System.Globalization;
using Microsoft.UI.Xaml.Data;

namespace RPM.Desktop.Converters;

/// <summary>Divides a double by the numeric ConverterParameter (e.g. a container width ÷ item
/// count) so items can be sized to fill a row evenly. Returns 0 for non-positive results.</summary>
public sealed partial class DivideConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var v = value is double d ? d : 0;
        var div = double.TryParse(parameter as string, NumberStyles.Any, CultureInfo.InvariantCulture, out var p) && p > 0 ? p : 1;
        var result = v / div;
        return result > 0 ? result : 0;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
