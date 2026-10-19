using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A thin vertical drag bar between two grid panels. It shows a west-east resize cursor on hover.
/// The hosting page does the column resizing, from the pointer. Thumb.DragDelta is not reliable in
/// Uno-Skia, for the same reason as <see cref="DragResizer"/>.</summary>
public sealed partial class PanelSplitter : BorderBase
{
    public PanelSplitter()
        => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeWestEast);
}
