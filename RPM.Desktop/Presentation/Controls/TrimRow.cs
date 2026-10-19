using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>Lays children out left to right, like a horizontal StackPanel. The exception is the child
/// marked with <see cref="ShrinksProperty"/>. That child is measured last, against the width that the
/// others leave.
///
/// <para>A horizontal StackPanel measures every child with unlimited width. So a TextBlock inside one never
/// trims. Its <c>CharacterEllipsis</c> has no width to trim to, and the cell around the panel cuts the text
/// off mid-letter instead. A track line needs two things. The title must trim, and the explicit badge must
/// sit directly after the title. A Grid cannot give either. A <c>*</c> column moves the badge to the far
/// edge. An <c>Auto</c> column is also measured with unlimited width.</para>
///
/// <para>Spacing and visibility work as in a StackPanel. A collapsed child takes no room and no gap. Every
/// child is arranged over the full height of the row, so its own VerticalAlignment places it.</para>
/// </summary>
public sealed partial class TrimRow : Panel
{
    public static readonly DependencyProperty SpacingProperty =
        DependencyProperty.Register(nameof(Spacing), typeof(double), typeof(TrimRow),
            new PropertyMetadata(0.0, (d, _) => ((TrimRow)d).InvalidateMeasure()));

    public double Spacing
    {
        get => (double)GetValue(SpacingProperty);
        set => SetValue(SpacingProperty, value);
    }

    /// <summary>Marks the child that gets the leftover width. The first visible child that is marked wins.
    /// A row with no marked child measures exactly as a horizontal StackPanel does.</summary>
    public static readonly DependencyProperty ShrinksProperty =
        DependencyProperty.RegisterAttached("Shrinks", typeof(bool), typeof(TrimRow),
            new PropertyMetadata(false));

    public static bool GetShrinks(UIElement element) => (bool)element.GetValue(ShrinksProperty);

    public static void SetShrinks(UIElement element, bool value) => element.SetValue(ShrinksProperty, value);

    /// <summary>The width that the shrinking child may measure against. This is what is available, less the
    /// other children and the gaps between the visible ones. Unlimited stays unlimited, so a row inside a
    /// parent that does not limit it behaves like a StackPanel. The result is never negative.
    /// </summary>
    internal static double Room(double available, double othersWidth, int visibleCount, double spacing)
    {
        if (double.IsPositiveInfinity(available))
            return double.PositiveInfinity;
        var gaps = Math.Max(0, visibleCount - 1) * spacing;
        return Math.Max(0, available - othersWidth - gaps);
    }

    protected override Size MeasureOverride(Size available)
    {
        var unlimited = new Size(double.PositiveInfinity, available.Height);
        UIElement? shrinker = null;
        double othersWidth = 0, height = 0;
        var visible = 0;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            visible++;
            if (shrinker is null && GetShrinks(child))
            {
                shrinker = child;
                continue;
            }
            child.Measure(unlimited);
            othersWidth += child.DesiredSize.Width;
            height = Math.Max(height, child.DesiredSize.Height);
        }

        var width = othersWidth + Math.Max(0, visible - 1) * Spacing;
        if (shrinker is not null)
        {
            shrinker.Measure(new Size(Room(available.Width, othersWidth, visible, Spacing), available.Height));
            width += shrinker.DesiredSize.Width;
            height = Math.Max(height, shrinker.DesiredSize.Height);
        }

        return new Size(width, height);
    }

    protected override Size ArrangeOverride(Size final)
    {
        double x = 0;
        var first = true;

        foreach (var child in Children)
        {
            if (child.Visibility == Visibility.Collapsed)
                continue;
            if (!first)
                x += Spacing;
            first = false;

            var width = child.DesiredSize.Width;
            child.Arrange(new Rect(x, 0, width, final.Height));
            x += width;
        }

        return final;
    }
}
