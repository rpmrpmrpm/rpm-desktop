using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Startup;

/// <summary>
/// What the splash window shows: the rpm mark with the tagline under its descender, centred.
///
/// <para>This is a control and not markup inside the window, so any window can show the same card and
/// the placement code is in one place. It reads no service and not the library, because it is built
/// before the container is.</para>
/// </summary>
public sealed partial class SplashCard : UserControl
{
    /// <summary>Where the P's descender stands inside the mark's path, as a fraction of the path's own
    /// bounding box. The letterforms run from x 18 to x 525 and the descender is the stem at x 132. It is
    /// a fraction and not a pixel count, so the line stays under the letter if the mark is resized. The
    /// parked header uses the same placement.</summary>
    private const double DescenderLeftFraction = (132 - 18) / (525.0 - 18);

    /// <summary>How far under the descender's foot the line box starts. One pixel, so the line is
    /// attached to the letter and not spaced away from it.</summary>
    private const double DescenderGap = 1;

    public SplashCard()
    {
        InitializeComponent();

        // The leading "re-" is a little darker, and only where the translation starts with it. The
        // hyphen is an English stylisation, and the other locales spell the prefix as part of the
        // word.
        var line = RPM.Desktop.Strings.Loc.Get("Art_ParkedTagline");
        var split = line.StartsWith("re-", StringComparison.Ordinal) ? 3
                  : line.StartsWith("re", StringComparison.Ordinal) ? 2
                  : 0;
        TaglineHead.Text = line[..split];
        TaglineTail.Text = line[split..];

        // The tagline is placed again on Loaded. The head's width is measured in the tagline's own
        // font, and a measurement made before the control has a resolved font uses the fallback font.
        PlaceTagline();
        Loaded += (_, _) => PlaceTagline();
    }

    /// <summary>
    /// Paints the card's own glass, for a head that cannot borrow the system's.
    /// </summary>
    /// <remarks>
    /// This is only used when asked for. On Windows the glass is desktop acrylic BEHIND the window, and
    /// a fill painted here would be a dark rectangle over it. On macOS the Skia surface cannot be seen
    /// through at all, so the card paints a plate that imitates the Windows glass. See
    /// <see cref="Services.Platform.MacWindowCard"/> for why the window itself cannot be transparent on
    /// macOS, and the markup on the <c>Plate</c> grid for what the plate imitates and how to tune it.
    /// </remarks>
    public void DrawOwnGlass() => Plate.Visibility = Visibility.Visible;

    /// <summary>Puts the tagline under the mark's descender. The darker head extends to the LEFT of the
    /// stem, so the d (the first letter of the word itself) sits under it.</summary>
    private void PlaceTagline()
    {
        var width = Mark.Width;
        var height = Mark.Height;
        if (double.IsNaN(width) || double.IsNaN(height) || width <= 0 || height <= 0)
            return;

        Tagline.Margin = new Thickness(
            Math.Max(0, width * DescenderLeftFraction - HeadWidth()), height + DescenderGap, 0, 0);
    }

    /// <summary>How wide the darker head ("re-") is, in the tagline's own font. It is measured because
    /// the alignment is offset by it, and it changes with the translation. A locale that does not start
    /// with those letters has no head and no offset.</summary>
    private double HeadWidth()
    {
        if (TaglineHead.Text.Length == 0)
            return 0;

        var probe = new TextBlock
        {
            Text = TaglineHead.Text,
            FontSize = Tagline.FontSize,
            FontFamily = Tagline.FontFamily,
            FontWeight = Tagline.FontWeight,
            FontStyle = Tagline.FontStyle,
        };
        probe.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        return probe.DesiredSize.Width;
    }
}
