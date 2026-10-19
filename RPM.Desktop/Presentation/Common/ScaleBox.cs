using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Lays its children out at 1 / <see cref="Factor"/> of the room it is given and draws them
/// <see cref="Factor"/> times larger. They fill that room and take the space their scaled size needs.
/// This is a layout transform for a uniform scale, which WinUI does not have.
///
/// <para>Use it for a block that should grow as a whole: text, icons, padding and pictures together,
/// without the block knowing it has been scaled. Home's featured banner, the drawer under a row of
/// records, and the empty and locked panels on Pegasus home use it (see HomeTextSizes). The child lays
/// itself out in its own unscaled units. So a width it measures, or a column count it picks, is the
/// same arithmetic it does at a scale of 1.</para>
///
/// <para>This box owns its children's RenderTransform, and writes it on every arrange. A child that needs
/// a transform of its own must put it on an element inside the child. At a scale of 1 no transform is
/// set, so the child is drawn exactly as it would be without this box.</para>
///
/// <para>A picture inside is drawn larger than it was decoded for, unless whoever decodes it asks for the
/// scaled size. Home's drawer does (see HomeView.OpenAsync), and so do the banner's cover and marks (see
/// HomeView.BannerPixels).</para>
/// </summary>
public sealed partial class ScaleBox : Panel
{
    public static readonly DependencyProperty FactorProperty = DependencyProperty.Register(
        nameof(Factor), typeof(double), typeof(ScaleBox),
        new PropertyMetadata(1.0, (d, _) => ((ScaleBox)d).InvalidateMeasure()));

    /// <summary>How much larger the children are drawn than they are laid out. 1 draws them as they
    /// are. Any value that is not a positive number is treated as 1. The property is not called Scale,
    /// because UIElement already has a Scale, a composition vector, and this name would hide it.</summary>
    public double Factor
    {
        get => (double)GetValue(FactorProperty);
        set => SetValue(FactorProperty, value);
    }

    private double Effective => Factor is > 0 and var s && !double.IsInfinity(s) ? s : 1;

    protected override Size MeasureOverride(Size availableSize)
    {
        var s = Effective;

        // Infinity divided by the scale is still infinity, so an unbounded side stays unbounded.
        var inner = new Size(availableSize.Width / s, availableSize.Height / s);

        double width = 0, height = 0;
        foreach (var child in Children)
        {
            child.Measure(inner);
            width = Math.Max(width, child.DesiredSize.Width);
            height = Math.Max(height, child.DesiredSize.Height);
        }

        return new Size(width * s, height * s);
    }

    protected override Size ArrangeOverride(Size finalSize)
    {
        var s = Effective;

        var inner = new Rect(0, 0, finalSize.Width / s, finalSize.Height / s);
        foreach (var child in Children)
        {
            child.Arrange(inner);

            // Each child gets one transform. It is made once and reused on later arranges, because a
            // transform cannot be assumed to be shareable between two elements.
            if (s == 1)
                child.RenderTransform = null;
            else if (child.RenderTransform is ScaleTransform own)
                own.ScaleX = own.ScaleY = s;
            else
                child.RenderTransform = new ScaleTransform { ScaleX = s, ScaleY = s };
        }

        return finalSize;
    }
}
