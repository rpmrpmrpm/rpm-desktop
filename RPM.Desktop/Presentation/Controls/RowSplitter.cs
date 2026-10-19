using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>A thin horizontal drag bar between two stacked grid rows. It is <see cref="PanelSplitter"/>
/// turned on its side, and it is used to drag the art display taller or shorter. It shows a north-south
/// resize cursor. The hosting page does the row resizing from the pointer, for the same reason as
/// PanelSplitter: Thumb.DragDelta is not reliable in Uno-Skia.</summary>
public sealed partial class RowSplitter : BorderBase
{
    public RowSplitter()
        => ProtectedCursor = InputSystemCursor.Create(InputSystemCursorShape.SizeNorthSouth);
}
