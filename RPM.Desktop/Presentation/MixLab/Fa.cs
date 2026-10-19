#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// The handful of Font Awesome glyphs the workbench draws its keys with, named.
///
/// <para>They are named because a code point is hard to read, and a raw glyph does not survive being
/// edited. The private-use characters these glyphs use are damaged by any tool that edits the file
/// without knowing what they are. So the escapes in the code are the source of truth. The names say
/// what each glyph is for.</para>
///
/// <para>The font is the app's own. See the <c>FaIcon</c> style in Generic.xaml. A plain
/// <see cref="Microsoft.UI.Xaml.Controls.FontIcon"/> falls back to Segoe MDL2, which is a Windows
/// system font. Outside Windows, the platform substitutes a different glyph for each one, so the row is
/// drawn with whatever font it finds.</para>
/// </summary>
internal static class Fa
{
    public static readonly FontFamily Face =
        new("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

    // ── transport ──
    public const string Play = "\uF04B";
    public const string Pause = "\uF04C";
    public const string Next = "\uF051";
    public const string Undo = "\uF0E2";

    /// <summary>Two arrows chasing each other in a circle (fa-sync): the same changeover, again. It is
    /// written as an escape, like every glyph here (see the note above). A raw private-use character is
    /// damaged by the next tool that edits this file.</summary>
    public const string Repeat = "\uF021";

    /// <summary>Where a deck's needle is sent: the mix point.</summary>
    public const string Flag = "\uF024";

    // ── looking ──

    /// <summary>A magnifier with a plus in it, and one with a minus in it. The two zoom keys are the
    /// pair that most people already know. Any other icon in that position has to be learned.</summary>
    public const string ZoomIn = "\uF00E";

    public const string ZoomOut = "\uF010";

    /// <summary>Arrows drawn inward: close in on the changeover.</summary>
    public const string Compress = "\uF066";

    /// <summary>Arrows drawn outward: the whole record, which is as far out as the picture
    /// goes.</summary>
    public const string Expand = "\uF065";

    /// <summary>Sights on the needle: the view follows it.</summary>
    public const string Crosshairs = "\uF05B";
}

/// <summary>
/// The condensed control set, fetched by key. WinUI sizes a box, a dropdown and a key for touch input.
/// A workbench row needs them to be as tall as the text in them. See the Compact*Style entries in
/// Generic.xaml.
/// </summary>
internal static class Condensed
{
    public static Style? Radio => Find("CompactRadioStyle");
    public static Style? Button => Find("CompactButtonStyle");

    /// <summary>
    /// A text box on the row's height.
    ///
    /// <para>It is built here instead of styled, because a Height setter alone is not enough. A TextBox
    /// template has its own minimum height, <c>TextControlThemeMinHeight</c>. If the outer box is
    /// shorter than that minimum, the text is laid out against the taller inner box and hangs below the
    /// line. The minimum comes from a resource. It is set to zero on this one control only, so the
    /// other text boxes in the app do not change.</para>
    /// </summary>
    public static Microsoft.UI.Xaml.Controls.TextBox Box(double width)
    {
        var box = new Microsoft.UI.Xaml.Controls.TextBox
        {
            Width = width,
            Style = Find("CompactTextBoxStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Floor(box);
        return box;
    }

    /// <summary>The same for a dropdown. Its template uses the same minimum height.</summary>
    public static Microsoft.UI.Xaml.Controls.ComboBox Drop(double width)
    {
        var drop = new Microsoft.UI.Xaml.Controls.ComboBox
        {
            Width = width,
            Style = Find("CompactComboStyle"),
            VerticalAlignment = VerticalAlignment.Center,
            VerticalContentAlignment = VerticalAlignment.Center,
        };
        Floor(drop);
        return drop;
    }

    private static void Floor(Microsoft.UI.Xaml.Controls.Control control)
    {
        control.Resources["TextControlThemeMinHeight"] = 0d;
        control.Resources["ComboBoxMinHeight"] = 0d;
    }

    private static Style? Find(string key) =>
        Application.Current.Resources.TryGetValue(key, out var style) ? style as Style : null;
}
#endif
#endif
