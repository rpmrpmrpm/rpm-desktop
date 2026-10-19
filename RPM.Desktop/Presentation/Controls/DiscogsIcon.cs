using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The Discogs mark: concentric arcs around a centre hole, like a record seen from above.
///
/// <para>This is a control and not a glyph, because Font Awesome has no Discogs icon. Its brands set does
/// not include Discogs. The artwork is Discogs' own, kept unchanged at <c>Assets/Svg/discogs.svg</c>. The
/// template in Themes/Generic.xaml draws the same path.</para>
///
/// <para>Paints in <see cref="Microsoft.UI.Xaml.Controls.Control.Foreground"/>, like a glyph does. The
/// default size is 16x16. Set Width and Height to change it. The mark is one colour by design. Tinting it
/// to match the surface it sits on is the correct use of it, not a change to it.</para>
/// </summary>
public partial class DiscogsIcon : Control
{
    public DiscogsIcon()
    {
        DefaultStyleKey = typeof(DiscogsIcon);
        IsTabStop = false;   // A picture, not a control the user presses. See ExplicitBadge.
    }
}
