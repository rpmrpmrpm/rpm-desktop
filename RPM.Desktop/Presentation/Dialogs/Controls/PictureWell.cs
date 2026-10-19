using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>The square where a picture is edited. It has a dark rounded well. The record glyph shows
/// while the well is empty. The picture is clipped to the same rounded corner. A thin edge is drawn
/// over the top, whichever of the two is showing.
///
/// <para>This is the same empty-cover square that the library's wall and album page draw for a record
/// with no sleeve, so "no picture" looks the same everywhere. The album editor and the per-file editor
/// both edit a cover. Keeping one class stops them from drawing two different versions of the
/// square.</para>
/// </summary>
internal sealed class PictureWell
{
    private static readonly Brush WellFill = new SolidColorBrush(Color.FromArgb(0x33, 0x00, 0x00, 0x00));
    private static readonly Brush WellEdge = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));
    private static readonly Brush GlyphInk = new SolidColorBrush(Color.FromArgb(0x40, 0xFF, 0xFF, 0xFF));

    /// <summary>Hang this in the layout.</summary>
    public Grid Root { get; }

    /// <summary>The picture itself. <see cref="Show"/> owns whether it or the glyph is
    /// visible.</summary>
    public Image Thumb { get; }

    /// <summary>The record glyph, shown while there is no picture.</summary>
    public FontIcon Placeholder { get; }

    /// <param name="edge">The width and the height. A cover is square, and a well that is not square
    /// would letterbox it.</param>
    public PictureWell(double edge)
    {
        Thumb = new Image { Stretch = Stretch.UniformToFill };
        Placeholder = new FontIcon
        {
            Glyph = "\uf51f",   // record
            Style = DenseForm.Res<Style>("FaIcon"),
            // Scales with the well, so the glyph looks the same at any size.
            FontSize = Math.Round(edge * 0.27),
            Foreground = GlyphInk,
        };

        Root = new Grid { Width = edge, Height = edge };
        Root.Children.Add(new Border { CornerRadius = new CornerRadius(8), Background = WellFill });
        Root.Children.Add(new Border { CornerRadius = new CornerRadius(8), Child = Thumb });
        Root.Children.Add(Placeholder);
        // Added last, so it is drawn on top. The edge frames the picture and is not covered by it.
        Root.Children.Add(new Border
        {
            CornerRadius = new CornerRadius(8),
            BorderThickness = new Thickness(1),
            BorderBrush = WellEdge,
            IsHitTestVisible = false,
        });
    }

    /// <summary>Adds an element drawn over the picture and under the well's edge, such as the CD
    /// overlay on a disc.</summary>
    public void AddOverPicture(UIElement element) => Root.Children.Insert(Root.Children.Count - 1, element);

    /// <summary>Puts a picture in the well, or empties it. Null shows the glyph.</summary>
    public void Show(ImageSource? source)
    {
        Thumb.Source = source;
        Placeholder.Visibility = source is null ? Visibility.Visible : Visibility.Collapsed;
    }
}
