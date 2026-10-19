using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using Windows.UI;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The small cloud shown in front of a track's name on a track line. It draws the same five
/// pictures as the cloud badge in the corner of an album tile on the album wall, but in one control. Five
/// stacked <c>FontIcon</c>s with five visibility bindings would do the same job with more elements.
///
/// <para>This uses one element and one write per state change, which suits a tracklist. A record's lines
/// are re-badged together whenever the cloud state changes. Five hidden glyphs on every line would mean
/// five elements measured and arranged per line, for one mark that is shown.</para>
///
/// <para>It has no visibility logic of its own. Whether a line shows a mark at all is decided by the record
/// (all its tracks agree, so the tile already shows it) and by the player (the equaliser uses this same
/// slot). Both live on the line's model, which binds this control's <see cref="Visibility"/>.</para>
/// </summary>
public sealed partial class CloudBadge : Grid
{
    private static readonly FontFamily Solid =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    /// <summary>The outline face. It is used for the one hollow cloud: a track chosen for the cloud that is
    /// not uploaded yet.</summary>
    private static readonly FontFamily Outline =
        new("ms-appx:///Assets/Fonts/fa-regular-400.ttf#Font Awesome 5 Pro Regular");

    /// <summary>The soft white that every settled mark uses. It is visible, but not strong enough to
    /// compete with the title beside it.</summary>
    private static readonly Brush Quiet = new SolidColorBrush(Color.FromArgb(0xD8, 0xFF, 0xFF, 0xFF));

    /// <summary>The outline cloud is fainter still. It is the weakest of the five marks.</summary>
    private static readonly Brush Faint = new SolidColorBrush(Color.FromArgb(0xB0, 0xFF, 0xFF, 0xFF));

    /// <summary>The app's cyan. It marks a track that is in the cloud and nowhere else, so the track uses
    /// no space on this disk. The album wall's tiles use the same colour.</summary>
    private static readonly Brush CloudOnly = Palette("RpmCyanBrush", Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF));

    /// <summary>The palette's red. It marks a track that was meant to be uploaded and has stopped
    /// trying.</summary>
    private static readonly Brush Failed = Palette("RpmRedBrush", Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B));

    /// <summary>The palette's gold. It marks a track that is uploaded, but not with everything it should
    /// have. This is a caution, not a failure. It uses the same tone as the warning message band.</summary>
    private static readonly Brush Incomplete = Palette("RpmGoldBrush", Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12));

    /// <summary>A brush from the app's palette, found by the name the theme uses for it.
    ///
    /// <para>The brush is read from resources, so the four colours this control draws stay the same as the
    /// rest of the app. A hex value typed into a control would be a fifth colour that nobody knows to
    /// change. The fallback is the same value as in the dictionary. It is used by the design harness and by
    /// any test that builds a badge before the theme is merged.</para>
    /// </summary>
    private static Brush Palette(string key, Color fallback) =>
        Application.Current?.Resources is { } resources
        && resources.TryGetValue(key, out var found) && found is Brush brush
            ? brush
            : new SolidColorBrush(fallback);

    private readonly FontIcon _icon;

    public CloudBadge()
    {
        VerticalAlignment = VerticalAlignment.Center;
        _icon = new FontIcon { Glyph = "\uf0c2", FontFamily = Solid, FontSize = 10, Foreground = Quiet };
        Children.Add(_icon);
    }

    /// <summary>What the cloud holds of this track. <see cref="AlbumCloudBadge.None"/> draws the resting
    /// cloud. The host hides the control.</summary>
    public AlbumCloudBadge Badge
    {
        get => (AlbumCloudBadge)GetValue(BadgeProperty);
        set => SetValue(BadgeProperty, value);
    }

    public static readonly DependencyProperty BadgeProperty =
        DependencyProperty.Register(nameof(Badge), typeof(AlbumCloudBadge), typeof(CloudBadge),
            new PropertyMetadata(AlbumCloudBadge.None, OnBadgeChanged));

    /// <summary>The mark's opacity while it pulses. The shared badge animation writes this through the
    /// line's model. So a track that is going up and its album's tile pulse together.</summary>
    public double Pulse
    {
        get => (double)GetValue(PulseProperty);
        set => SetValue(PulseProperty, value);
    }

    public static readonly DependencyProperty PulseProperty =
        DependencyProperty.Register(nameof(Pulse), typeof(double), typeof(CloudBadge),
            new PropertyMetadata(1.0, (d, e) => ((CloudBadge)d).Opacity = (double)e.NewValue));

    /// <summary>The glyph's size. Track lines use 10. This is a property, not a constant, so a bigger list
    /// can ask for a bigger mark without a second control.</summary>
    public double GlyphSize
    {
        get => (double)GetValue(GlyphSizeProperty);
        set => SetValue(GlyphSizeProperty, value);
    }

    public static readonly DependencyProperty GlyphSizeProperty =
        DependencyProperty.Register(nameof(GlyphSize), typeof(double), typeof(CloudBadge),
            new PropertyMetadata(10.0, (d, e) => ((CloudBadge)d)._icon.FontSize = (double)e.NewValue));

    private static void OnBadgeChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        var badge = (CloudBadge)d;
        var icon = badge._icon;

        // The glyphs are cloud, cloud-arrow-up, cloud-slash and link. They are the same glyphs, with the
        // same meanings, as the corner badge on the album wall.
        (icon.Glyph, icon.FontFamily, icon.Foreground) = (AlbumCloudBadge)e.NewValue switch
        {
            AlbumCloudBadge.Full => ("\uf0c2", Solid, Quiet),
            AlbumCloudBadge.Offloaded => ("\uf0c2", Solid, CloudOnly),
            AlbumCloudBadge.Partial => ("\uf0c2", Outline, Faint),
            AlbumCloudBadge.Working => ("\uf0ee", Solid, Quiet),
            AlbumCloudBadge.Leaving => ("\uf4b4", Solid, Quiet),

            // Failed and Incomplete keep the plain cloud shape and change only the colour. The bundled font
            // has only the shapes above. A code point that is not in the font draws an empty box. Red: the
            // track should be uploaded but is not, and nothing will happen on its own. Gold: the track is
            // uploaded, but not everything it should have is. A different shape would suggest that it is
            // not uploaded at all.
            AlbumCloudBadge.Failed => ("\uf0c2", Solid, Failed),
            AlbumCloudBadge.Incomplete => ("\uf0c2", Solid, Incomplete),

            // The track is borrowed from another computer, and it plays over the network rather than from
            // the store. This is not a cloud, because the track is on another person's disk. The mark shows
            // a network link instead.
            AlbumCloudBadge.PeerLink => ("\uf0c1", Solid, Quiet),
            _ => ("\uf0c2", Solid, Quiet),
        };
    }
}
