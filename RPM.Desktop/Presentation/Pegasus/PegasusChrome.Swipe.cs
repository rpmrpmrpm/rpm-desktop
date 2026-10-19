using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// A track row in the album drawer, pushed sideways: right to play it next, left to add it to a
/// playlist.
///
/// <para>On a full-screen visualizer there is no right-click menu and nothing on the row to press, so
/// the row itself carries both actions, one to each side. The mark under the row shows which action is
/// which before the finger has committed to anything.</para>
///
/// <para>Nothing happens until the row is pushed far enough to count (see <see cref="SwipeCommit"/>).
/// Past that point the row moves much less for each extra point of travel, which tells the user that
/// the gesture is accepted. Short of that point the row returns and the song is untouched.</para>
///
/// <para>A press that moves up or down is the list being scrolled, and the swipe gives up on the first
/// move that shows it, so that rows never fight the scroll. A press that goes nowhere is still a press:
/// it plays the album from that row.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How far a press travels before it is a swipe rather than a press, in points.</summary>
    private const double SwipeSlop = 6;

    /// <summary>How far a swipe must travel to take effect, before the drawer's scale is applied. About
    /// the width of a thumb: far enough that a shaking hand on a track name does not queue it.</summary>
    private const double SwipeCommit = 64;

    /// <summary>The furthest a row can travel, as a multiple of <see cref="SwipeCommit"/>. Travel past
    /// the commit distance is only resistance: the action is already certain.</summary>
    private const double SwipeStretch = 1.3;

    /// <summary>How quickly a released row returns to its place, as a rate per second.</summary>
    private const double SwipeHomeRate = 16;

    private Border? _swipeRow;
    private double _swipeFromX, _swipeFromY;
    private double _swipeAt;
    private bool _swipeOn;
    private bool _swipeShut;
    private bool _swipeHoming;

    /// <summary>The distance that counts as far enough, at the size the drawer is drawn now.</summary>
    private double SwipeReach => SwipeCommit * DrawerScale;

    /// <summary>
    /// The row, and the two marks under it. The marks are in a frame around the row, not inside the
    /// row, so that they stay still while the row moves over them.
    /// </summary>
    private Grid SwipeFrame(Border row, double scale)
    {
        var marks = new Grid
        {
            IsHitTestVisible = false,
            Margin = new Thickness(14 * scale, 0, 14 * scale, 0),
        };
        marks.Children.Add(Mark("", HorizontalAlignment.Left));    // play next
        marks.Children.Add(Mark("", HorizontalAlignment.Right));   // add to a playlist

        var frame = new Grid();
        frame.Children.Add(marks);
        frame.Children.Add(row);
        return frame;

        // Nothing is shown at rest. The marks lie directly under a row with a translucent colour, so
        // visible marks would show faintly through every row.
        TextBlock Mark(string glyph, HorizontalAlignment side) => new()
        {
            Text = glyph,
            FontFamily = (FontFamily)Resources["PegasusFa"],
            FontSize = 13 * scale,
            Opacity = 0,
            HorizontalAlignment = side,
            VerticalAlignment = VerticalAlignment.Center,
        };
    }

    /// <summary>The two marks under one row, if the row is in a frame that has them.</summary>
    private static (TextBlock? Next, TextBlock? List) MarksOf(Border row) =>
        row.Parent is Grid frame && frame.Children.Count > 0 && frame.Children[0] is Grid marks
        && marks.Children.Count == 2
            ? (marks.Children[0] as TextBlock, marks.Children[1] as TextBlock)
            : (null, null);

    private void DetailTrack_PointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row)
            return;

        // A row that is still returning from the last gesture is put back at once, so that only one row
        // moves at a time.
        if (_swipeRow is { } last && !ReferenceEquals(last, row))
            RestRow(last);

        _swipeRow = row;
        var at = e.GetCurrentPoint(AlbumScroll).Position;
        _swipeFromX = at.X;
        _swipeFromY = at.Y;
        _swipeAt = 0;
        _swipeOn = _swipeShut = _swipeHoming = false;
    }

    private void DetailTrack_PointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (_swipeRow is not { } row || !ReferenceEquals(sender, row) || _swipeShut)
            return;

        var at = e.GetCurrentPoint(AlbumScroll).Position;
        var dx = at.X - _swipeFromX;
        var dy = at.Y - _swipeFromY;

        if (!_swipeOn)
        {
            // Up or down first: this press belongs to the list. The decision is final for this press and
            // is not tested again on each move, because a scroll that drifts sideways would otherwise
            // start a swipe on a row.
            if (Math.Abs(dy) > SwipeSlop && Math.Abs(dy) >= Math.Abs(dx))
            {
                _swipeShut = true;
                return;
            }

            if (Math.Abs(dx) < SwipeSlop)
                return;

            _swipeOn = true;
            _swipeHoming = false;
            row.CapturePointer(e.Pointer);
            LightRow(null);   // a row that is being moved is not a row that is pointed at
        }

        _swipeAt = Resisted(dx);
        PaintSwipe(row);
        e.Handled = true;
    }

    /// <summary>Where the row goes for a pointer that has moved <paramref name="dx"/>. It follows the
    /// pointer up to the commit distance, then moves only about a third as far, up to a limit.</summary>
    private double Resisted(double dx)
    {
        var reach = SwipeReach;
        var far = Math.Abs(dx);
        if (far > reach)
            far = Math.Min(reach + ((far - reach) * 0.34), reach * SwipeStretch);
        return Math.Sign(dx) * far;
    }

    /// <summary>Draws the row where the gesture has put it, and the mark it uncovers.</summary>
    private void PaintSwipe(Border row)
    {
        Shift(row).X = _swipeAt;

        var (next, list) = MarksOf(row);
        var told = Math.Clamp(Math.Abs(_swipeAt) / SwipeReach, 0, 1);

        // The mark is fully opaque only when the gesture would take effect. A partly opaque mark means
        // that the swipe has not gone far enough yet.
        if (next is not null)
            next.Opacity = _swipeAt > 0 ? told : 0;
        if (list is not null)
            list.Opacity = _swipeAt < 0 ? told : 0;
    }

    /// <summary>Puts a row back in its place at once, with both marks hidden.</summary>
    private void RestRow(Border row)
    {
        _swipeAt = 0;
        PaintSwipe(row);
        if (ReferenceEquals(_swipeRow, row))
        {
            _swipeRow = null;
            _swipeOn = _swipeHoming = false;
        }
    }

    /// <summary>
    /// The press is over: the row does the action it was pushed far enough for, and returns to its
    /// place either way.
    ///
    /// <para>A press that never became a swipe plays the album from this row (see
    /// <see cref="PlayFromDrawer"/>). A press that did become a swipe must NOT play: a song queued as
    /// next and also started in the same gesture would be two actions for one gesture.</para>
    /// </summary>
    private void DetailTrack_Released(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not Border row)
            return;

        var swiped = _swipeOn && ReferenceEquals(_swipeRow, row);
        var at = _swipeAt;

        if (ReferenceEquals(_swipeRow, row))
        {
            row.ReleasePointerCaptures();
            _swipeOn = false;
            _swipeShut = true;
            _swipeHoming = swiped;
            if (!swiped)
                _swipeRow = null;
        }

        if (row.Tag is not int index)
            return;

        if (!swiped)
        {
            PlayFromDrawer(_albumTracks, index, AlbumSleeve());
            return;
        }

        if (at >= SwipeReach)
            QueueUpNext(index);
        else if (at <= -SwipeReach)
            AskWhichPlaylist(index);
    }

    /// <summary>The pointer was taken away during the gesture, for example by a window losing focus or
    /// a panel closing. Nothing is committed for a gesture that did not finish.</summary>
    private void DetailTrack_CaptureLost(object sender, PointerRoutedEventArgs e)
    {
        if (sender is Border row && ReferenceEquals(_swipeRow, row) && _swipeOn)
        {
            _swipeOn = false;
            _swipeHoming = true;
        }
    }

    /// <summary>One frame of a row returning to its place. It runs on the same tick as the rest of this
    /// control, so the speed follows the real frame rate.</summary>
    private void TickSwipe(double dt)
    {
        if (_swipeRow is not { } row || !_swipeHoming)
            return;

        _swipeAt -= _swipeAt * (1.0 - Math.Exp(-dt * SwipeHomeRate));
        if (Math.Abs(_swipeAt) < 0.5)
        {
            _swipeAt = 0;
            _swipeHoming = false;
            _swipeRow = null;
        }

        PaintSwipe(row);
    }

    /// <summary>
    /// Right swipe: the song is inserted after the song that is playing, and a message at the bottom of
    /// the screen says so.
    ///
    /// <para>This is the same insert as "play next" in the library window, on the same queue. The
    /// drawer does not keep a second queue. With nothing on the deck the song is appended, because
    /// "next" in an empty queue means the end.</para>
    /// </summary>
    private void QueueUpNext(int index)
    {
        if (_amp is null || index < 0 || index >= _albumTracks.Count)
            return;

        var track = _albumTracks[index];
        _amp.InsertNext([track]);
        Announced?.Invoke(string.Format(Strings.Loc.Get("Viz_QueuedNext"), TitleOf(track)));
    }

    /// <summary>The name to show for a song in a message: its title, or its file name when it has no
    /// title. A message must always name the song.</summary>
    private static string TitleOf(RPM.Core.Models.TrackInfo track) =>
        string.IsNullOrWhiteSpace(track.Title)
            ? Path.GetFileNameWithoutExtension(track.FilePath)
            : track.Title!;
}
