using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A performer: head and shoulders with a microphone in front of them.
///
/// <para>Font Awesome has no such glyph. The closest ones are <c>user-music</c> (a head with a music note)
/// and <c>user-headset</c> (a call-centre headset, which looks like support, not a stage). So this icon is
/// built from the same font. It uses the body of <c>user-music</c>, whose right shoulder is already cut
/// back to leave room for a second mark. The note is removed and Font Awesome's <c>microphone</c> glyph is
/// put in its place. Both come from fa-solid-900.ttf at their native 512-unit scale. This keeps the icon in
/// line with the app's other icons: same grid, same stroke weight, same corner radii. The gap between the
/// microphone and the shoulder is the gap Font Awesome itself cut.</para>
///
/// <para>The artwork is in Themes/Generic.xaml as this control's template. It paints in
/// <see cref="Microsoft.UI.Xaml.Controls.Control.Foreground"/>, like a glyph does. The default size is
/// 16x16. Set Width and Height to change it.</para>
/// </summary>
public partial class ArtistIcon : Control
{
    public ArtistIcon()
    {
        DefaultStyleKey = typeof(ArtistIcon);
        IsTabStop = false;   // A picture, not a control the user presses. See ExplicitBadge.
    }
}
