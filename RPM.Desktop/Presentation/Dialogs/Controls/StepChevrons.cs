using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>A step indicator for wizards: a row of interlocking chevrons. The right point of each step
/// fits into the notch on the left of the next step. The active step is a brighter frost with white
/// ink. Completed steps use the body well's fill with normal ink. Steps still to come are outline only,
/// with dim ink.
///
/// <para>Usage: <c>chevrons.SetSteps("Source", "Disc", "Rip"); chevrons.Current = 0;</c></para>
/// </summary>
public sealed partial class StepChevrons : BorderBase
{
    private const double ChevronHeight = 30;
    private const double ChevronNotch = 11; // How deep the point and the notch are

    private sealed record Step(
        int Index, Grid Container, Path Shape, TextBlock Num, TextBlock Label, bool IsFirst, bool IsLast);

    private readonly StackPanel _host;
    private readonly List<Step> _steps = new();
    private int _current;

    public StepChevrons()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        _host = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 0 };
        Child = _host;
    }

    /// <summary>Replaces the step labels, rebuilding the ribbon.</summary>
    public void SetSteps(params string[] labels)
    {
        _host.Children.Clear();
        _steps.Clear();
        for (var i = 0; i < labels.Length; i++)
            AddChevron(i, labels[i], isFirst: i == 0, isLast: i == labels.Length - 1);
        Paint();
    }

    /// <summary>Zero-based index of the active step.</summary>
    public int Current
    {
        get => _current;
        set { _current = value; Paint(); }
    }

    private void AddChevron(int index, string label, bool isFirst, bool isLast)
    {
        // Stretch.None is required. A Path defaults to Stretch.Fill, which asks for all the available
        // width. The grid would then grow, SizeChanged would fire again, and the ribbon would keep
        // growing. With None the geometry is used as it is, and the shape sizes to its content.
        var shape = new Path { StrokeThickness = 1, Stretch = Stretch.None };

        var num = new TextBlock { Text = (index + 1).ToString(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };
        var lbl = new TextBlock { Text = (label ?? "").ToUpperInvariant(), FontSize = 13, VerticalAlignment = VerticalAlignment.Center };

        // Inset the content past the notch and the point so text never sits on the angled edges.
        var content = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            Spacing = 8,
            VerticalAlignment = VerticalAlignment.Center,
            HorizontalAlignment = HorizontalAlignment.Center,
            Margin = new Thickness(isFirst ? 16 : 16 + ChevronNotch, 0, isLast ? 16 : 16 + ChevronNotch, 0),
        };
        content.Children.Add(num);
        content.Children.Add(lbl);

        var grid = new Grid { Height = ChevronHeight };
        // Every step after the first overlaps to the left by the notch depth, so the previous step's
        // point fits into this step's notch.
        if (!isFirst)
            grid.Margin = new Thickness(-ChevronNotch, 0, 0, 0);

        // The shape sits in a Canvas, which reports a zero desired size. Otherwise the Path's bounds
        // would feed back into the grid's measure, and the chevron would grow wider on every layout
        // pass. The grid's width comes only from the text.
        var canvas = new Canvas();
        canvas.Children.Add(shape);
        grid.Children.Add(canvas);
        grid.Children.Add(content);

        var step = new Step(index, grid, shape, num, lbl, isFirst, isLast);
        grid.SizeChanged += (_, _) => UpdateGeometry(step);
        _host.Children.Add(grid);
        _steps.Add(step);
    }

    /// <summary>Sets the outline. The first step has a flat left edge, and the last step has a flat
    /// right edge. Every other step has a convex point on its right edge and a concave notch on its
    /// left edge.</summary>
    private static void UpdateGeometry(Step step)
    {
        double width = step.Container.ActualWidth, height = step.Container.ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        const double notch = ChevronNotch;
        var points = new List<Point>
        {
            new(0, 0),
            new(step.IsLast ? width : width - notch, 0),
        };
        if (!step.IsLast)
            points.Add(new Point(width, height / 2));           // Convex point on the right
        points.Add(new Point(step.IsLast ? width : width - notch, height));
        points.Add(new Point(0, height));
        if (!step.IsFirst)
            points.Add(new Point(notch, height / 2));           // Concave notch on the left

        var figure = new PathFigure { StartPoint = points[0], IsClosed = true, IsFilled = true };
        for (var i = 1; i < points.Count; i++)
            figure.Segments.Add(new LineSegment { Point = points[i] });

        var geometry = new PathGeometry();
        geometry.Figures.Add(figure);
        step.Shape.Data = geometry;
    }

    private void Paint()
    {
        // No accent colour. The active step uses a brighter frost with white ink, to match the frosted
        // buttons.
        var accent = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));   // active fill
        var onAccent = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)); // active ink
        var text = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF));
        var dim = new SolidColorBrush(Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF));
        var border = new SolidColorBrush(Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF));   // upcoming outline
        var well = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF));     // completed fill
        var clear = new SolidColorBrush(Microsoft.UI.Colors.Transparent);

        foreach (var step in _steps)
        {
            var active = step.Index == _current;
            var completed = step.Index < _current;

            step.Shape.Fill = active ? accent : completed ? well : clear;
            step.Shape.Stroke = active || completed ? null : border;

            var ink = active ? onAccent : completed ? text : dim;
            step.Num.Foreground = ink;
            step.Label.Foreground = ink;

            var weight = active ? Microsoft.UI.Text.FontWeights.Bold : Microsoft.UI.Text.FontWeights.Normal;
            step.Num.FontWeight = weight;
            step.Label.FontWeight = weight;
        }
    }
}
