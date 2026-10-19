using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The licence of a Wikipedia text, shown as a small link under it: "CC BY-SA 4.0". It opens the
/// licence.
///
/// <para>The text on Wikipedia is shared under CC BY-SA 4.0. That licence asks that a reuse names the
/// licence and links to it. "Read on Wikipedia" names the source, and this link names the licence. It sits
/// on its own line under the link row, at the right, because the row is already full at the track info
/// pane's width. Its visibility follows the source link's, which is bound in XAML. So it is shown exactly
/// when there is a Wikipedia text above it.</para>
/// </summary>
public sealed partial class CcBySaLink : RpmLink
{
    private static readonly Uri Licence = new("https://creativecommons.org/licenses/by-sa/4.0/");

    public CcBySaLink()
    {
        Content = Loc.Get("Wikipedia_Licence");
        ToolTipService.SetToolTip(this, Loc.Get("Wikipedia_LicenceTip"));
        FontSize = 10;
        Opacity = 0.7;
        HorizontalAlignment = HorizontalAlignment.Right;
        Click += OnClick;
    }

    private static async void OnClick(object sender, RoutedEventArgs e)
    {
        try
        {
            await Windows.System.Launcher.LaunchUriAsync(Licence);
        }
        catch
        {
            // Opening the browser can fail or be refused. In that case the page stays as it is.
        }
    }
}
