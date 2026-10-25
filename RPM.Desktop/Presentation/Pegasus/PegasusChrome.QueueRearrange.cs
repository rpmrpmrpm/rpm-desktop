using RPM.Desktop.Presentation.RPaMp.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Rearranging the queue with the remote, as the Android TV app's drawer does it. The key beside the drawer's
/// close key turns rearranging on: the rows show their grips, a line under the head says how a song is moved,
/// and OK on a row picks it up as the row menu's Move does (see PegasusChrome.QueueRemote): Up and Down carry
/// it, OK puts it down. The key stays lit while rearranging is on. Pressing it again, Back, or closing the
/// drawer turns it off. A held OK still opens the row's menu.
///
/// <para>The grips are shown only while rearranging is on, or on a row being carried. The pointer's drag works
/// as it always has, whether rearranging is on or not.</para>
///
/// <para>The line under the head goes at the next key, as the TV's does: it is said once, when rearranging is
/// turned on, and is not needed while the listener does it.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>Whether OK on a queue row picks it up rather than playing it, and the grips are shown.</summary>
    private bool _queueRearranging;

    /// <summary>Whether the queue is being rearranged with the remote. Back turns it off before it closes the
    /// drawer (see PegasusPlayer.TakeBack).</summary>
    public bool QueueRearranging => _queueRearranging;

    private void QueueRearrange_Click(object sender, RoutedEventArgs e)
    {
        SetQueueRearranging(!_queueRearranging);
        _queueSeen = 0;
    }

    /// <summary>Turns rearranging off. False when it was not on, so Back goes on to the next thing.</summary>
    public bool EndQueueRearranging()
    {
        if (!_queueRearranging)
            return false;
        SetQueueRearranging(false);
        return true;
    }

    /// <summary>Turns rearranging on or off: the key's light, the line under the head, and every row's
    /// grip.</summary>
    private void SetQueueRearranging(bool on)
    {
        QueueRearrangeHint.Visibility = on ? Visibility.Visible : Visibility.Collapsed;
        if (_queueRearranging == on)
            return;

        _queueRearranging = on;
        PaintRearrangeKey();
        foreach (var row in _queueSlots)
            ShowGrip(row, on || ReferenceEquals(_queueMove?.Row, row));
    }

    /// <summary>The line under the head goes at the next key (see WakeForKeys).</summary>
    private void HideRearrangeHint() => QueueRearrangeHint.Visibility = Visibility.Collapsed;

    /// <summary>OK on a queue row: while rearranging, it picks the row up; otherwise the row plays.</summary>
    private void QueueRowOk(PlaylistTrack track)
    {
        if (_queueRearranging)
            StartQueueMove(track);
        else
            PlayQueued(track);
    }

    /// <summary>A row's grip, the first column of its line (see BuildQueue). Null on a row with no line.</summary>
    private static TextBlock? GripOf(Border row)
    {
        if (row.Child is RemoteLine { Content: Grid grid })
            foreach (var cell in grid.Children)
                if (cell is TextBlock grip && Grid.GetColumn(grip) == 0)
                    return grip;
        return null;
    }

    /// <summary>Shows or hides a row's grip. A hidden grip takes no room, so the sleeve stands at the row's
    /// left.</summary>
    private static void ShowGrip(Border row, bool shown)
    {
        if (GripOf(row) is { } grip)
            grip.Visibility = shown ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>The rearrange key lit while rearranging is on (the TV's #4DFFFFFF fill, the lit rim and a white
    /// glyph), so the mode shows from across the room wherever the focus is. Off, it is boxed as the close key
    /// beside it (see the drawer's head in PegasusChrome.xaml).</summary>
    private void PaintRearrangeKey()
    {
        var on = _queueRearranging;
        QueueRearrangeButton.Background = new SolidColorBrush(on
            ? Color.FromArgb(0x4D, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0x14, 0xFF, 0xFF, 0xFF));
        QueueRearrangeButton.BorderBrush = new SolidColorBrush(on
            ? Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xB3, 0xFF, 0xFF, 0xFF));
        QueueRearrangeButton.Foreground = new SolidColorBrush(on
            ? Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)
            : Color.FromArgb(0xE6, 0xFF, 0xFF, 0xFF));
    }
}
