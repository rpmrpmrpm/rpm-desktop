using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.LeftPane.Controls;

/// <summary>
/// Lays children out left to right and starts a new line when the next one will not fit.
///
/// <para>Used for rows whose length depends on the content: an artist's genres, or the links to
/// outside pages under a record. Each row holds short pills of unknown width and count, in a pane
/// the user can make narrower. Other framework panels either clip to one line or stack the items
/// in a single column.</para>
/// </summary>
public partial class WrapRow : Panel
{
    public static readonly DependencyProperty HorizontalSpacingProperty =
        DependencyProperty.Register(nameof(HorizontalSpacing), typeof(double), typeof(WrapRow),
            new PropertyMetadata(6.0, OnSpacingChanged));

    public static readonly DependencyProperty VerticalSpacingProperty =
        DependencyProperty.Register(nameof(VerticalSpacing), typeof(double), typeof(WrapRow),
            new PropertyMetadata(6.0, OnSpacingChanged));

    public double HorizontalSpacing
    {
        get => (double)GetValue(HorizontalSpacingProperty);
        set => SetValue(HorizontalSpacingProperty, value);
    }

    public double VerticalSpacing
    {
        get => (double)GetValue(VerticalSpacingProperty);
        set => SetValue(VerticalSpacingProperty, value);
    }

    private static void OnSpacingChanged(DependencyObject d, DependencyPropertyChangedEventArgs e) =>
        ((WrapRow)d).InvalidateMeasure();

    protected override Size MeasureOverride(Size available)
    {
        // Each child is measured against the full width, not the space left on the current line. The
        // answer must not depend on where the child lands. Otherwise the last pill on a line measures
        // small, fits, and is then arranged wider than it was measured.
        var ceiling = double.IsInfinity(available.Width) ? double.PositiveInfinity : available.Width;
        var childLimit = new Size(ceiling, double.PositiveInfinity);

        double lineWidth = 0, lineHeight = 0, width = 0, height = 0;

        foreach (var child in Children)
        {
            child.Measure(childLimit);
            var size = child.DesiredSize;

            if (lineWidth > 0 && lineWidth + HorizontalSpacing + size.Width > ceiling)
            {
                width = Math.Max(width, lineWidth);
                height += lineHeight + VerticalSpacing;
                lineWidth = size.Width;
                lineHeight = size.Height;
                continue;
            }

            lineWidth += lineWidth > 0 ? HorizontalSpacing + size.Width : size.Width;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        width = Math.Max(width, lineWidth);
        height += lineHeight;

        return new Size(double.IsInfinity(ceiling) ? width : Math.Min(width, ceiling), height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0, y = 0, lineHeight = 0;

        foreach (var child in Children)
        {
            var size = child.DesiredSize;

            if (x > 0 && x + size.Width > final.Width)
            {
                x = 0;
                y += lineHeight + VerticalSpacing;
                lineHeight = 0;
            }

            child.Arrange(new Rect(x, y, size.Width, size.Height));
            x += size.Width + HorizontalSpacing;
            lineHeight = Math.Max(lineHeight, size.Height);
        }

        return new Size(final.Width, y + lineHeight);
    }
}
