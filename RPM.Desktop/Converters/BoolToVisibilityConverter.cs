using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Data;

namespace RPM.Desktop.Converters;

/// <summary>true → Visible, false → Collapsed. Parameter "Invert" flips it.</summary>
public sealed partial class BoolToVisibilityConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
    {
        var b = value is true;
        if (string.Equals(parameter as string, "Invert", StringComparison.OrdinalIgnoreCase))
            b = !b;
        return b ? Visibility.Visible : Visibility.Collapsed;
    }

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
