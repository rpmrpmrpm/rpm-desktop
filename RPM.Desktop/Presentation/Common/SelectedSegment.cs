using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// The app's "this one is chosen" mark, for a button that is one of a set. It is an RPM azure wash
/// behind a full-white glyph. The other buttons have no wash. The left pane's rail and the library's
/// list, wall and table switch both use it. It lives in one place because both are the same action
/// (pick one of these), so they must look the same.
/// </summary>
internal static class SelectedSegment
{
    private static readonly SolidColorBrush ActiveFill = new(Color.FromArgb(0x3A, 0x03, 0x71, 0xD0));
    private static readonly SolidColorBrush IdleFill = new(Color.FromArgb(0x00, 0, 0, 0));
    private static readonly SolidColorBrush ActiveInk = new(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF));
    private static readonly SolidColorBrush IdleInk = new(Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));

    public static void Paint(Button button, bool active)
    {
        button.Background = Wash(active);
        button.Foreground = Ink(active);
    }

    /// <summary>The wash and the ink on their own, for a button whose glyph has its own colour, so it
    /// cannot take both from <see cref="Paint"/>. The deck's home key is one example. Both ways use the
    /// same two brushes, so the mark looks the same wherever it is used.</summary>
    public static Brush Wash(bool active) => active ? ActiveFill : IdleFill;

    public static Brush Ink(bool active) => active ? ActiveInk : IdleInk;
}
