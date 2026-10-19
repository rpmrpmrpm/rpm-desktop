using Microsoft.UI.Xaml.Data;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Converters;

/// <summary>
/// Masks a title on its way to a TextBlock, in the same way as the lyric sheet. See
/// <see cref="TitleCensor"/> for the rule and the reasoning behind it.
///
/// <para>This is a converter and not part of the model, on purpose. The binding is the last step before
/// the pixels. So nothing that compares, searches, looks up or writes a title ever sees the masked
/// string. <c>Text="{Binding Title, Converter={StaticResource Censor}}"</c>.</para>
/// </summary>
public sealed partial class TitleCensorConverter : IValueConverter
{
    public object Convert(object value, Type targetType, object parameter, string language)
        => TitleCensor.Shown(value as string) ?? "";

    public object ConvertBack(object value, Type targetType, object parameter, string language)
        => throw new NotSupportedException();
}
