using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The room at the head of an open playlist: the list's own colours, drifting.
///
/// <para><b>This is the same element that the home screen's cards use</b>, on the same shared clock. See
/// <see cref="CardRoom"/> and <c>CardRoomBeat</c>. So there is very little in this file. What a room costs,
/// when it redraws, what it does on a resize, and how it leaves the clock are all decided by that element,
/// and they are written down there. What is here is which colours to use, which pose, and whether the
/// header is on screen at all.</para>
///
/// <para><b>The room is redrawn many times a second.</b> So it has no cache, no debounce and no wait mark.
/// Each of those only pays off for a picture that is slow to make and is drawn once. An earlier design drew
/// a raymarched volume once per list. That took about 185 ms a frame over eight pool workers. It was
/// affordable once per list opened, and at no rate at all.</para>
/// </summary>
public sealed partial class PlaylistView
{
    /// <summary>The most pixels that one frame of the header's room may use.
    /// </summary>
    /// <remarks>
    /// <b>This value is why <see cref="CardRoom.Most"/> exists.</b> Every room is drawn at a tenth of each
    /// side. A card's size is set by the row it sits in. But the header is as wide as the pane, so its
    /// tenth grows with the window. A 2,400-point header is 240 by 26, which is 6,240 pixels. A 3,600-point
    /// header is 9,360 pixels. A frame costs about its pixel count and almost nothing else, at about 1.4
    /// microseconds a pixel on the WinUI head. So the larger header costs about 13 ms per frame. The
    /// clock's whole budget is about 11 ms. Past that, the 20 ms timer does not fit in two system ticks,
    /// and the rate drops. See <c>CardRoomBeat.Beat</c>, where that arithmetic is.
    ///
    /// <para>3,600 pixels costs about 5 ms. That is a little above the 2,660 pixels the room was tuned at
    /// on the banner's plate. Below the cap nothing changes. A 1,000-point header is 100 by 24, and it is
    /// drawn at its own tenth.</para>
    /// </remarks>
    private const int RoomBudget = 3600;

    /// <summary>Whether the pane this view lives in is the one on screen. The host pushes this in with <see
    /// cref="PaneShown"/>. An element is not told anything when an ancestor collapses.</summary>
    private bool _paneShown;

    /// <summary>The host saying whether the playlists pane is on screen.
    /// </summary>
    /// <remarks>
    /// <b>The host pushes this, and it has to.</b> A collapsed ancestor raises nothing on its children, and
    /// it does not measure them down to zero. So the room cannot work this out for itself. Left to
    /// <c>IsLoaded</c> and its own size, it would draw thirty-two frames a second behind a hidden pane for
    /// as long as the window is open. The home screen's banner room has the same problem, and it is handled
    /// the same way.
    ///
    /// <para>Home is the case that makes this necessary, and not just tidy. Every other way of leaving the
    /// playlists pane calls <see cref="ExitDetail"/>, which settles this as a side effect. Home does not
    /// call it. Home covers the card rather than replacing what the pane is reading. The tracklist is still
    /// there to come back to.</para>
    /// </remarks>
    public void PaneShown(bool shown)
    {
        _paneShown = shown;
        SettleRoom();
    }

    /// <summary>Whether the room should be drawing, worked out from what is true now.
    /// </summary>
    /// <remarks>Three facts are needed, not one. The pane must be the one on screen. The view must be
    /// drilled into a list rather than showing its tree. And the room itself must be up, not taken down
    /// between two lists by <see cref="Undress"/>. The third fact matters for the same reason as the first.
    /// A collapsed ancestor keeps its children's measured size, so a room under one would keep drawing. The
    /// value is derived on every arrival, and not set by whichever handler noticed. That is the same rule
    /// that <c>CardRoom.Settle</c> keeps one level down.
    /// </remarks>
    private void SettleRoom()
        => DetailRoomArt.Shown =
            _paneShown && _inDetail && DetailRoom.Visibility == Visibility.Visible;

    /// <summary>The room's own rectangle, so that nothing it draws can land outside the header.
    /// </summary>
    /// <remarks>
    /// <b>A Grid does not clip.</b> The room is an element, not a background brush. An element whose
    /// content wants more room than its slot can spill out of the slot. The picture is also magnified about
    /// ten times, from a bitmap whose edge rows are what the scaler samples at the boundary. Neither
    /// problem can be reasoned about while a frame of it is moving. An edge that is off by a pixel becomes
    /// a line that moves. Clipped, the header's rectangle is the whole area where the animation may be. The
    /// animation must stay inside the framing of the header.
    ///
    /// <para><b>The clip is set off the layout pass, not inside it.</b> <c>SizeChanged</c> is raised during
    /// arrange. Writing <see cref="UIElement.Clip"/> from there invalidates the render of an element that
    /// is being arranged. The app crashed in <c>Microsoft.UI.Xaml.dll</c> when this was done during
    /// arrange. So the clip is set on the dispatcher queue, after the pass.</para>
    /// </remarks>
    private void DetailRoom_SizeChanged(object sender, SizeChangedEventArgs e)
        => DispatcherQueue.TryEnqueue(() =>
        {
            // The size is read again, rather than trusting the one the event carried. By the time this
            // runs, the drag may have moved on. A clip a few frames behind the element would cut a strip
            // off one edge of the room.
            if (DetailRoom.ActualWidth <= 0 || DetailRoom.ActualHeight <= 0)
            {
                DetailRoom.Clip = null;
                return;
            }

            DetailRoom.Clip = new RectangleGeometry
            {
                Rect = new Rect(0, 0, DetailRoom.ActualWidth, DetailRoom.ActualHeight),
            };
        });

    /// <summary>A list has been opened. This paints the room in its colours and poses it.
    /// </summary>
    /// <remarks>This is called from the fill, once the cover is in hand. The cover is where the colours
    /// come from. A room painted before it would take the colours of the last list.
    ///
    /// <para>Setting the palette is the whole of the redraw. <c>CardRoom</c> sees that the colours it
    /// painted are not the colours it now holds, and it draws the next frame in the new ones.</para>
    /// </remarks>
    private void OpenRoom(byte[]? cover, string name)
    {
        DetailRoomArt.Most = RoomBudget;

        // The same list is the same room, on every machine and after every restart. It uses the NAME, not
        // the row id. A generated list is built again with a new id each time it is asked for. If its
        // header started at a different moment each time, it would look like a different list.
        //
        // The value goes in Apart, and not in a seed of its own. CardRoom already has this value for this
        // purpose: a place along the drift, added to where the colours alone would put it. Two lists in one
        // colourway are two rooms. CardRoom does the same for the five framed cards.
        DetailRoomArt.Apart = Math.Abs(string.GetHashCode(name, StringComparison.Ordinal)) % 4096;
        DetailRoomArt.Palette = CoverRoom.Of(cover);

        DetailRoom.Visibility = Visibility.Visible;
        SettleRoom();
    }
}
