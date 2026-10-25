using RPM.Core.Playback;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The queue in the drawer under the TV remote. A held OK (or the Menu key) on a row opens what can be
/// done to it there: play it, play it next, move it, take it out. "Move" picks the row up: Up and Down
/// carry it through the queue, OK puts it down, Back puts it back where it was.
///
/// <para><b>The move is the drag's, driven by keys.</b> While the row is carried nothing in the queue
/// changes: the carried row and the rows it passes are drawn off their lines by their Translation, as a
/// pointer drag draws them (QueueGesture.Displaced), so the row keeps the focus and stays in the tree. Put
/// down, the rows are put in the new order and the queue is told in the same frame, exactly as a landing
/// drag does it (see TickDrop), so the change the queue reports finds the rows already saying it.</para>
///
/// <para><b>Ended by anything that is not OK.</b> Back, a pointer move, the drawer closing or leaving the
/// queue, and the queue changing from elsewhere all end a move, and only OK commits one. A row the
/// listener did not put down is not moved.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>A queue row picked up by "Move": the row, the line inside it that holds the focus, the
    /// slot it was picked up from, and the slot it is holding now. One object per move.</summary>
    private sealed class QueueMove(Border row, RemoteLine line, int from)
    {
        public Border Row { get; } = row;
        public RemoteLine Line { get; } = line;
        public int From { get; } = from;
        public int At { get; set; } = from;
    }

    private QueueMove? _queueMove;

    /// <summary>Whether a queue row is being carried by the keys. The shell gives the arrows and OK to
    /// the move while it is (see PegasusPlayer.QueueMoveKey).</summary>
    public bool QueueMoving => _queueMove is not null;

    /// <summary>
    /// What a held OK opens on a queue row: play it, play it next, move it, take it out. Not the song's
    /// own menu: its entries are about putting a song on, and this one is on already. Null when the track
    /// has left the queue since the row was built.
    /// </summary>
    private MenuFlyout? QueueMenu(PlaylistTrack track)
    {
        if (_amp is not { } amp || amp.Playlist.IndexOf(track) is var at && at < 0)
            return null;

        var playing = PlayingIndex();
        var menu = new MenuFlyout();
        menu.Items.Add(MenuEntry(Strings.Loc.Get("AlbumMenu_Play"), () => PlayQueued(track)));

        // Not on the row playing or on the one already after it, where it would change nothing.
        if (playing >= 0 && at != playing && at != playing + 1)
            menu.Items.Add(MenuEntry(Strings.Loc.Get("Viz_QueuePlayNext"), () => PlayQueuedNext(track)));

        if (amp.Playlist.Count > 1)
            menu.Items.Add(MenuEntry(Strings.Loc.Get("Viz_QueueMove"), () => StartQueueMove(track)));

        menu.Items.Add(new MenuFlyoutSeparator());
        menu.Items.Add(MenuEntry(Strings.Loc.Get("Viz_QueueRemove"), () => RemoveQueued(track)));
        return ForThisView(menu);
    }

    /// <summary>Where the track playing stands in the queue, or -1 where none is.</summary>
    private int PlayingIndex()
    {
        if (_amp is not { } amp)
            return -1;
        for (var i = 0; i < amp.Playlist.Count; i++)
            if (amp.Playlist[i].IsCurrent)
                return i;
        return -1;
    }

    /// <summary>
    /// "Play next": the track goes to just after the one playing, by the same Move the drag commits, which
    /// the queue answers by dropping the track it had readied to follow. The rows are built again, so the
    /// focus goes to the moved track's new row.
    /// </summary>
    private void PlayQueuedNext(PlaylistTrack track)
    {
        if (_amp is not { } amp)
            return;

        var from = amp.Playlist.IndexOf(track);
        var playing = PlayingIndex();
        if (from < 0 || playing < 0 || from == playing)
            return;

        var to = PlayNextSlot(from, playing);
        if (to != from)
            amp.Playlist.Move(from, to);

        FocusScreenLater(QueueLineFor(track));
        Announced?.Invoke(string.Format(Strings.Loc.Get("Viz_QueueMovedNext"), track.Title));
    }

    /// <summary>
    /// Where "Play next" moves a track from <paramref name="from"/> to, with the track at
    /// <paramref name="playing"/> playing: the slot just after it. A Move takes the track out before it
    /// puts it back, so one from above the playing track lands on the playing track's own slot, which the
    /// playing track has just left by moving up one.
    /// </summary>
    internal static int PlayNextSlot(int from, int playing) => from < playing ? playing : playing + 1;

    /// <summary>
    /// "Remove from queue": the queue's own removal, which stops playback when it is the track playing,
    /// as the main window's queue does. The rows are built again, and the focus goes to the row now
    /// standing where this one stood, or the last.
    /// </summary>
    private void RemoveQueued(PlaylistTrack track)
    {
        if (_amp is not { } amp)
            return;

        var at = amp.Playlist.IndexOf(track);
        if (at < 0)
            return;

        amp.RemoveAt([at]);

        var rows = _queueSlots.Count;
        if (rows > 0 && _queueSlots[Math.Min(at, rows - 1)] is { Child: RemoteLine line })
            FocusScreenLater(line);
    }

    /// <summary>"Move": picks the row up. Nothing in the queue changes until it is put down.</summary>
    private void StartQueueMove(PlaylistTrack track)
    {
        EndQueueMove(place: false);

        // A pointer drag in flight owns the rows' offsets.
        if (_dragRow is not null || _dropping)
            return;

        var from = -1;
        for (var i = 0; i < _queueSlots.Count; i++)
            if (_queueSlots[i] is { Tag: PlaylistTrack t } && ReferenceEquals(t, track))
                from = i;
        if (from < 0 || _queueSlots[from] is not { Child: RemoteLine line } row)
            return;

        _queueMove = new QueueMove(row, line, from);
        Lift(row, lifted: true);
        FocusScreenLater(line);   // the menu that chose this gives the focus back to the line as it closes
        Announced?.Invoke(string.Format(Strings.Loc.Get("Viz_QueueMoving"), track.Title));
    }

    /// <summary>Up or Down while a row is carried: one slot that way. False at an end of the queue, and
    /// the caller bounces.</summary>
    public bool StepQueueMove(int way)
    {
        if (_queueMove is not { } move)
            return false;

        var to = move.At + way;
        if (to < 0 || to >= _queueSlots.Count)
            return false;

        move.At = to;
        ShowQueueMove(move);
        _queueSeen = 0;
        return true;
    }

    /// <summary>
    /// Ends a move. <paramref name="place"/> puts the row down in the slot it holds and tells the queue;
    /// otherwise every row goes back to its own line and the queue is not told anything. Nothing when no
    /// row is carried.
    /// </summary>
    public void EndQueueMove(bool place)
    {
        if (_queueMove is not { } move)
            return;
        _queueMove = null;

        Lift(move.Row, lifted: false);

        // The panel may have been built again under the move; then there is nothing of it left to put down.
        var stillThere = move.From < _queueSlots.Count
                         && ReferenceEquals(_queueSlots[move.From], move.Row);

        if (!place || move.At == move.From || _amp is null || !stillThere)
        {
            ResetShifts();
            return;
        }

        // With headings in the drawer the row's mark moves with it, so the headings may stand elsewhere now:
        // the queue is told, the change builds the drawer again, and the focus goes to the track's new row.
        if (_queueHeadings)
        {
            ResetShifts();
            _amp.Playlist.Move(move.From, move.At);
            FocusScreenLater(QueueLineFor(move.Row.Tag as PlaylistTrack));
            return;
        }

        // As a landing drag does it: the rows in the new order, arranged now, the offsets dropped in the
        // same frame, then the queue told (see TickDrop for why all three land together).
        QueueRows.Children.RemoveAt(move.From);
        QueueRows.Children.Insert(move.At, move.Row);
        _queueSlots.RemoveAt(move.From);
        _queueSlots.Insert(move.At, move.Row);
        QueueRows.UpdateLayout();
        ResetShifts();
        _amp.Playlist.Move(move.From, move.At);

        // The row left the tree and came back, and the focus does not survive that.
        FocusScreenLater(move.Line);
    }

    /// <summary>A move ended by the panel being built again: the rows it was drawing are gone, so there
    /// is nothing to put back or to put down.</summary>
    private void DropQueueMove() => _queueMove = null;

    /// <summary>The carried row on the slot it holds, the rows it has passed one slot over, and the list
    /// scrolled so the carried row stands in the middle of it.</summary>
    private void ShowQueueMove(QueueMove move)
    {
        var pitch = QueueRowPitch();
        if (pitch <= 0)
            return;

        // Where each row's line starts: one pitch a slot, or further down past a heading (see
        // PegasusChrome.QueueHeadings).
        var tops = QueueSlotTops(pitch);
        for (var i = 0; i < _queueSlots.Count; i++)
        {
            var row = _queueSlots[i];
            Shift(row).Y = ReferenceEquals(row, move.Row)
                ? tops[move.At] - tops[move.From]
                : tops[i + QueueGesture.Displaced(i, move.From, move.At)] - tops[i];
        }

        // Worked out from the slot, as the drag's own edge scroll is: a bring-into-view reads the row's
        // place in the layout, which is still the slot it was picked up from.
        var target = tops[move.At] - ((QueueScroll.ViewportHeight - pitch) / 2);
        QueueScroll.ChangeView(null, Math.Clamp(target, 0, QueueScroll.ScrollableHeight), null, disableAnimation: false);
    }

    /// <summary>A carried row stands out from the rest as a dragged one does: a little faded, over its
    /// neighbours, and with its grip lit.</summary>
    private static void Lift(Border row, bool lifted)
    {
        row.Opacity = lifted ? 0.86 : 1;
        Canvas.SetZIndex(row, lifted ? 1 : 0);

        if (row.Child is RemoteLine { Content: Grid grid })
            foreach (var cell in grid.Children)
                if (cell is TextBlock grip && Grid.GetColumn(grip) == 0)
                    grip.Opacity = lifted ? 1 : 0.42;
    }
}
