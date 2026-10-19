using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using Path = Microsoft.UI.Xaml.Shapes.Path;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Dialogs.Controls;

/// <summary>Corner resize affordance (three diagonal hatch lines) for the resizable dialog.
/// Place bottom-right and call <see cref="Attach"/>.</summary>
public sealed partial class DialogResizeGrip : BorderBase
{
    public DialogResizeGrip()
    {
        Width = 24;
        Height = 24;
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
        Child = BuildHatch();
    }

    private static Path BuildHatch()
    {
        var geo = new PathGeometry();
        AddLine(geo, 21, 9, 9, 21);
        AddLine(geo, 21, 14, 14, 21);
        AddLine(geo, 21, 18, 18, 21);
        return new Path
        {
            Stretch = Stretch.None,
            Stroke = new SolidColorBrush(Color.FromArgb(0xFF, 0xA9, 0xA9, 0xA9)),
            StrokeThickness = 1.6,
            Data = geo,
        };
    }

    private static void AddLine(PathGeometry geo, double x1, double y1, double x2, double y2)
    {
        var fig = new PathFigure { StartPoint = new Point(x1, y1) };
        fig.Segments.Add(new LineSegment { Point = new Point(x2, y2) });
        geo.Figures.Add(fig);
    }

    public void Attach(DragResizer resizer)
    {
        PointerPressed += resizer.GripPressed;
        PointerMoved += resizer.GripMoved;
        PointerReleased += resizer.GripReleased;
        PointerCaptureLost += resizer.GripCaptureLost;
    }
}
