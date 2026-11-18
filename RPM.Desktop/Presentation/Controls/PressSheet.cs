using Microsoft.UI.Input;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>
/// A transparent sheet laid over something drawn elsewhere, which takes the press for it. The artwork
/// header's cover is drawn behind the shell's content and gets no pointer input, so the shell puts one of
/// these over it.
///
/// <para>A subclass, not a plain Grid, because <c>ProtectedCursor</c> is protected and only a control can
/// set its own cursor. The owner says whether a press does anything now (<see cref="SetActive"/>), so the
/// hand shows only where a press will act.</para>
/// </summary>
public partial class PressSheet : Grid
{
    private bool _active;

    public PressSheet()
    {
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent);
    }

    /// <summary>Shows the hand cursor when a press will act, and the arrow when it will not.</summary>
    public void SetActive(bool active)
    {
        if (active == _active)
            return;

        _active = active;
        ProtectedCursor = InputSystemCursor.Create(active ? InputSystemCursorShape.Hand : InputSystemCursorShape.Arrow);
    }
}
