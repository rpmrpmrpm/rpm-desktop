using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Startup;

/// <summary>
/// What the splash window shows: the rpm wordmark, centred and filling the window, as the Android TV app
/// opens with it: dimmed, with a band of light sweeping across it (<see cref="Controls.SweptMark"/>). On
/// Windows nothing else: no background and no edge, so the mark stands on the desktop.
///
/// <para>This is a control and not markup inside the window, so any window can show the same card. It
/// reads no service and not the library, because it is built before the container is.</para>
/// </summary>
public sealed partial class SplashCard : UserControl
{
    public SplashCard()
    {
        InitializeComponent();

        // The band loops until the card's window closes; nothing waits for the end of a pass.
        Loaded += (_, _) => Mark.Start();
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
    public void DrawOwnGlass()
    {
        Plate.Visibility = Visibility.Visible;
        DrawEdge();
    }

    /// <summary>Draws the card's one-pixel edge, for a card with glass behind it. A clear window has
    /// none.</summary>
    public void DrawEdge() =>
        Edge.BorderBrush = new Microsoft.UI.Xaml.Media.SolidColorBrush(Windows.UI.Color.FromArgb(0x2E, 0xFF, 0xFF, 0xFF));
}
