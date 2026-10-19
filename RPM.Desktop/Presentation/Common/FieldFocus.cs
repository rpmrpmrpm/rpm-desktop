using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Puts the keyboard focus in a text field that may still be on its way onto the screen. It does so at
/// once if the field can take focus, or else on the first frame when it can. Cmd+F and Ctrl+F use this.
///
/// <para>A search asked for on a screen without a field of its own first changes the screen. For example,
/// the home screen opens the collection, an album page closes, or the mini player's collection opens. The
/// field arrives at the end of that move. So the request is checked again on each frame, against facts: is
/// the field loaded, does it have a size, is it shown all the way up, and is it still the field that was
/// asked for. Nothing is timed. Pegasus's <c>TickFocus</c> works the same way.</para>
///
/// <para>The request is dropped when the field takes the focus, when the caller says its screen has gone,
/// when a newer request replaces it, or after <see cref="GiveUpFrames"/> frames. The text is selected when
/// the field takes the focus, so typing replaces the last search.</para>
/// </summary>
internal static class FieldFocus
{
    /// <summary>About a second at 60 frames a second. That is longer than any move in the app that brings a
    /// search field up. The pane fade takes 110 ms, and an album page closes in under 400 ms.</summary>
    private const int GiveUpFrames = 60;

    private static TextBox? _field;
    private static Func<bool>? _stillWanted;
    private static int _frames;

    /// <summary>
    /// Asks for the focus to go to <paramref name="field"/>. <paramref name="stillWanted"/> is asked on
    /// each frame while the field cannot take the focus yet. The request is dropped when it returns false,
    /// which means the screen the field is on has been left.
    /// </summary>
    public static void Give(TextBox field, Func<bool> stillWanted)
    {
        Drop();
        if (TryPlace(field))
            return;

        _field = field;
        _stillWanted = stillWanted;
        _frames = 0;
        CompositionTarget.Rendering += OnFrame;
    }

    private static void OnFrame(object? sender, object e)
    {
        if (_field is not { } field || _stillWanted is not { } stillWanted
            || ++_frames > GiveUpFrames || !stillWanted() || TryPlace(field))
            Drop();
    }

    private static bool TryPlace(TextBox field)
    {
        if (!CanTakeFocus(field) || !field.Focus(FocusState.Keyboard))
            return false;

        field.SelectAll();
        return true;
    }

    private static bool CanTakeFocus(Control control) =>
        control is { IsLoaded: true, IsEnabled: true, ActualWidth: > 0, ActualHeight: > 0 }
        && ShownAllTheWayUp(control);

    private static bool ShownAllTheWayUp(UIElement element)
    {
        for (DependencyObject? node = element; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is UIElement { Visibility: Visibility.Collapsed })
                return false;
        return true;
    }

    private static void Drop()
    {
        if (_field is null)
            return;

        CompositionTarget.Rendering -= OnFrame;
        _field = null;
        _stillWanted = null;
    }
}
