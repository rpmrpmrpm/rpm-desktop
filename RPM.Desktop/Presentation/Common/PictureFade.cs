using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Main;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Fades a picture in when it arrives: <c>ui:PictureFade.Source="{x:Bind Picture, Mode=OneWay}"</c> on an
/// <see cref="Image"/>, in place of <c>Source</c>. A playlist folder's cover comes in this way, as on the
/// phones (0.2 seconds, easing out), so its frame never appears over an empty square.
///
/// <para><b>Only an arrival fades.</b> A picture that replaces another one (a recycled tile shown for
/// another folder) is set at once: a fade from one picture through nothing to the next would flash.</para>
///
/// <para>The fade runs on the compositor, on the element's own visual, and ends at full opacity, so a fade
/// that is cut short by a recycle leaves nothing half drawn. On Uno an element's XAML opacity and its
/// visual's opacity are one value (see <see cref="TileGlide"/>), so only an element whose XAML opacity is one
/// may be faded here.</para>
/// </summary>
public static class PictureFade
{
    /// <summary>How long a picture takes to come in, as on the phones.</summary>
    public const double Ms = 200;

    public static readonly DependencyProperty SourceProperty = DependencyProperty.RegisterAttached(
        "Source", typeof(ImageSource), typeof(PictureFade), new PropertyMetadata(null, OnSourceChanged));

    public static ImageSource? GetSource(Image image) => (ImageSource?)image.GetValue(SourceProperty);

    public static void SetSource(Image image, ImageSource? value) => image.SetValue(SourceProperty, value);

    private static void OnSourceChanged(DependencyObject d, DependencyPropertyChangedEventArgs e)
    {
        if (d is not Image image)
            return;

        image.Source = e.NewValue as ImageSource;
        if (e.OldValue is null && e.NewValue is not null)
            In(image);
    }

    /// <summary>Fades <paramref name="element"/> in from nothing over <see cref="Ms"/>.</summary>
    public static void In(UIElement element)
    {
        var visual = ElementCompositionPreview.GetElementVisual(element);
        var compositor = visual.Compositor;
        var fade = compositor.CreateScalarKeyFrameAnimation();
        fade.InsertKeyFrame(0f, 0f);
        fade.InsertKeyFrame(1f, 1f, PaneGlide.Ease(compositor));
        fade.Duration = TimeSpan.FromMilliseconds(Ms);
        visual.StartAnimation("Opacity", fade);
    }
}
