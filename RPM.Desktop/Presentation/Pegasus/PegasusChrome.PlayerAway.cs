using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The mini player going away over the collection, and coming back.
///
/// <para>The card hides itself after five seconds when nothing is playing, and comes back with a fade
/// and a slide from the edge it stands at. The pointer at that very edge of the screen brings it back
/// until the next scroll. Scrolling down sends it away, even while something is playing.</para>
///
/// <para><b>Where.</b> On every screen the collection shows, home, the wall and radio: all three run the
/// whole height of the layer under the card, so taking the card away uncovers the screen. Radio keeps
/// home's rules exactly. Not over a record's own screen or the tuner, which take the collection
/// layer down with the card in it.</para>
///
/// <para><b>It goes</b> after five seconds of nothing playing (on home; see below for the other screens),
/// and at once when the screen is scrolled down past <see cref="FirstFocusAllowance"/>, whatever is
/// playing. <b>It comes back</b> when
/// something comes on, when the collection changes screen (home to the wall or back) or comes up again,
/// when the screen is scrolled back to its top, and from the top edge of the screen until the screen is
/// next scrolled. Scrolling up short of the top does not bring it back. Back at the top counts as
/// arriving again: with nothing playing the card goes five seconds later, as when the screen first
/// comes up.</para>
///
/// <para><b>The allowance.</b> The remote's first focus on a screen scrolls it a little to bring that
/// element into view, and the card went with it on any scroll over half a point: home opened from the
/// remote without its mini player. Within <see cref="FirstFocusAllowance"/> of the top the screen still
/// counts as at its top, as the TV's home does (FOBO 7569f0da1).</para>
///
/// <para><b>Nothing playing</b> is the card holding the app's mark: no record on the deck and no station
/// tuned (<see cref="LibraryPlayerOn"/>). A paused record or a paused station does not start the five
/// seconds; it goes only on a scroll down.</para>
///
/// <para><b>The mark is shown on home only</b> (<see cref="MarkKeptOff"/>). On the wall, radio, the
/// artists and genres lists and their pages, a card with nothing on it stays down, and comes in only when
/// something starts playing.</para>
///
/// <para><b>Who writes what.</b> The top edge of the screen sets <see cref="_peek"/>; a screen's scroll
/// clears it and, going down, sets <see cref="_scrolledAway"/>; neither touches the card. The tick is the
/// only place that reads the facts and moves the card, so the order those arrive in does not matter:
/// every frame asks again whether the card should be up. Write the card's opacity, shift or hit test from
/// a handler and that stops being true.</para>
///
/// <para><b>Where it stands</b> is the settings' Mini player row (<see cref="MiniPlayerPlace"/>): at the
/// top in the middle, at the bottom in the middle, in a bottom corner, or not at all, as on the TV. A card
/// at the bottom slides in from the bottom edge, and the pointer at the bottom edge brings it back, in the
/// same way as the top edge does for a card at the top. The rest of the rules do not change with the
/// place, as on the TV, which shows its card by the same top-of-screen rule wherever it stands: every
/// screen runs the whole height under the card, the remote's first focus on a screen still scrolls it
/// from its top (<see cref="FirstFocusAllowance"/>), and a scroll down is still somebody looking further
/// into the screen, whichever edge the card is on. Off takes the card out of the layout, so the remote
/// cannot stop on it either.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How long the card shows the app's mark before it goes, in seconds.</summary>
    private const double PlayerAwayAfter = 5.0;

    /// <summary>How long the card takes to slide in or out.</summary>
    private const double PlayerSlideMs = 300;

    /// <summary>Seconds the card has held the app's mark on this screen this time. Counts only while it
    /// may go, and stops at <see cref="PlayerAwayAfter"/>.</summary>
    private double _markSeen;

    /// <summary>The pointer has reached the top edge since the screen was last scrolled. Set by the edge,
    /// cleared by a scroll, and cleared by the tick when the screen changes and when something comes
    /// on.</summary>
    private bool _peek;

    /// <summary>The screen has been scrolled down since the card last came back for something coming on
    /// or for the screen changing. Set by a scroll, cleared by the tick.</summary>
    private bool _scrolledAway;

    /// <summary>Whether something was playing on the last tick, so the frame something comes on can be
    /// told from the frames after it.</summary>
    private bool _wasOn;

    /// <summary>Whether the screen in front was at its top on the last tick, so the frame it gets back
    /// there can be told from the frames it stays there.</summary>
    private bool _wasAtTop = true;

    /// <summary>How far down the screen in front may be scrolled, in points, and still count as at its top
    /// for the card: the scroll the remote's first focus makes to bring that element into view (the TV's
    /// NUDGE, 120).</summary>
    private const double FirstFocusAllowance = 120;

    /// <summary>How far home is scrolled down, summed from the moves its scroller reports
    /// (HomeView.Scrolled), which are exact differences of its offset. Back to 0 whenever home says it is
    /// at its top, so a move missed while another screen was in front cannot leave it adrift for
    /// long.</summary>
    private double _homeScrolled;

    /// <summary>How far the screen in front is scrolled down, read off its scroller: home's (summed, see
    /// <see cref="_homeScrolled"/>), the wall's, radio's or the artists and genres screens'. A home not
    /// built yet is at its top.</summary>
    private double CollectionScrolled => _collection switch
    {
        CollectionScreen.Home => _home is null || _home.IsAtTop ? 0 : _homeScrolled,
        CollectionScreen.Radio => RadioScroll.VerticalOffset,
        CollectionScreen.Artists or CollectionScreen.Genres or CollectionScreen.Artist or CollectionScreen.Genre
            or CollectionScreen.Playlists or CollectionScreen.Search => ArtistsGenresScroll.VerticalOffset,
        _ => LibraryScroll.VerticalOffset,
    };

    /// <summary>Whether the screen in front is at its top for the card: scrolled no further than
    /// <see cref="FirstFocusAllowance"/>.</summary>
    private bool CollectionAtTop => CollectionScrolled <= FirstFocusAllowance;

    /// <summary>The screen the three figures above were gathered on. A different screen in front starts
    /// them again, the way a fresh visit does.</summary>
    private CollectionScreen _awayOn;

    /// <summary>Where the card is between gone (0) and up (1), before the ease. Linear in time, so a
    /// reversal halfway carries on from where it is.</summary>
    private double _playerShown = 1;

    /// <summary>The card holds the app's mark and the screen is not home: the card stays down. True under
    /// a record's screen as well, so coming back from one to the wall or a page does not show the card
    /// for the length of a slide.</summary>
    private bool MarkKeptOff => !LibraryPlayerOn && _collection != CollectionScreen.Home;

    /// <summary>Whether the collection is the screen in front, on any of its screens: up, and no record's
    /// own screen or the tuner over it.</summary>
    private bool CollectionInFront => LibraryUp && !RecordUp && !TunerUp;

    /// <summary>
    /// Whether the card floats over the collection's screen in front: home, the album wall, radio, and the
    /// artists and genres lists, the Android TV app's hasMiniPlayer. Not the search screen or radio's station
    /// search, which cover the screen (it lay over the search field), nor an artist's or a genre's page or the
    /// playlists screen, where the TV has none either. Under a record's screen or the tuner the card's own
    /// rules apply, as before.
    /// </summary>
    private bool MiniPlayerHere =>
        !CollectionInFront
        || (_collection is CollectionScreen.Home or CollectionScreen.Wall or CollectionScreen.Radio
                or CollectionScreen.Artists or CollectionScreen.Genres
            && !RadioSearchOpen);

    /// <summary>
    /// One frame: whether the card should be up, and one step of it getting there.
    /// </summary>
    private void TickPlayerAway(double dt)
    {
        // The key legend keeps clear of the card, so it is ticked with it (see PegasusChrome.Legend), and
        // before the check below: it goes when the collection does.
        TickLegend(dt);

        // The settings' Mini player row changed: the card takes its new place now, behind the open
        // panel, rather than on the next visit.
        if (MiniPlaceWanted != _miniPlace)
            PlaceMiniPlayer();

        if (!LibraryUp)
            return;

        var on = LibraryPlayerOn;
        var inFront = CollectionInFront;
        var atTop = CollectionAtTop;

        if (!inFront || _awayOn != _collection)
        {
            // Under a record's screen the card is up, and a screen arrived at (or come back to) starts
            // from the beginning.
            _awayOn = _collection;
            _markSeen = 0;
            _peek = false;
            _scrolledAway = false;
        }
        else if (on)
        {
            _markSeen = 0;

            // Something coming on brings the card back, however it went away.
            if (!_wasOn)
            {
                _scrolledAway = false;
                _peek = false;
            }
        }
        else if (!_peek && CollectionIsReady)
        {
            // Counted only once the screen is ready: home with its pictures in, the wall read. Until then
            // Pegasus's opening cover can be over it (see PegasusPlayer.Show), for as long as four
            // seconds. If the count started when the collection came up, most of the five seconds would
            // pass behind the cover.
            _markSeen = Math.Min(_markSeen + dt, PlayerAwayAfter);
        }

        // Back at the top of the screen: the same fresh start as arriving on it. Read off the scroller
        // each frame rather than told by a scroll event, so a screen that gets to its top any way at all
        // (the wheel, the bar, a search putting the wall back) brings the card back.
        if (inFront && atTop && !_wasAtTop)
        {
            _scrolledAway = false;
            _peek = false;
            _markSeen = 0;
        }

        _wasOn = on;
        _wasAtTop = atTop;

        // The remote's focus on the card's own keys or its cover is somebody reaching for it, the way the
        // pointer at the top edge is: the card comes back while the focus is there.
        var focused = KeyboardDriven && FocusWithin(LibraryPlayer);

        // The album carousel's letter wheel covers the card while it is open, as the TV's covers its mini player.
        // Set Off in the settings, the card is never up. Nor on a screen the TV gives no mini player (see
        // MiniPlayerHere).
        var up = !MarkKeptOff && !CarouselLettersOpen && _miniPlace != MiniPlayerPlace.Off && MiniPlayerHere
                 && (!inFront || _peek || focused || (!_scrolledAway && (on || _markSeen < PlayerAwayAfter)));

        // Out of reach as soon as it starts going, in reach as soon as it starts coming: a card on its
        // way out must not take a press meant for the row it is uncovering. Said on the ring at the same
        // moment, with the facts it was decided on, so a card that went or came when it should not have
        // can be read back afterwards.
        if (LibraryPlayer.IsHitTestVisible != up)
        {
            LibraryPlayer.IsHitTestVisible = up;
            SayPlayerAway(up ? "coming" : "going");
        }

        var to = up ? 1.0 : 0.0;
        if (_playerShown == to)
            return;

        var step = dt / (PlayerSlideMs / 1000);
        _playerShown = up ? Math.Min(1, _playerShown + step) : Math.Max(0, _playerShown - step);
        PlacePlayer();
    }

    /// <summary>
    /// Writes the card's opacity and shift from <see cref="_playerShown"/>, on the app's move curve
    /// (<see cref="PaneGlide.EaseAt"/>): ease-out coming in, the same curve backwards going out. Gone
    /// means faded out and moved clear of the edge it stands at, which is its margin on that edge and its
    /// own height: lifted above the top of the screen, or lowered below the bottom. Read here rather than
    /// kept, because a resize changes both.
    /// </summary>
    private void PlacePlayer()
    {
        var eased = PaneGlide.EaseAt(_playerShown);
        var away = MiniAtTop
            ? -(LibraryPlayer.Margin.Top + LibraryPlayer.ActualHeight)
            : LibraryPlayer.Margin.Bottom + LibraryPlayer.ActualHeight;

        LibraryPlayer.Opacity = eased;
        LibraryPlayerSlide.Y = (1 - eased) * away;
    }

    /// <summary>Where the card stands now, as <see cref="PlaceMiniPlayer"/> last put it.</summary>
    private MiniPlayerPlace _miniPlace = MiniPlayerPlace.Top;

    /// <summary>Where the settings say the card stands. At the top while there are no settings to
    /// ask.</summary>
    private MiniPlayerPlace MiniPlaceWanted => Settings?.MiniPlayerPlace ?? MiniPlayerPlace.Top;

    /// <summary>Whether the card stands at the top of the screen (Off counts as the top, as on the TV: it is
    /// never shown, so the edge only decides where the pointer's edge strip waits).</summary>
    private bool MiniAtTop => _miniPlace is MiniPlayerPlace.Top or MiniPlayerPlace.Off;

    /// <summary>
    /// Puts the card where the settings say: its alignment and its margins, the edge strip that brings it
    /// back, and whether it is in the layout at all. Called from <see cref="LayOutLibraryTop"/>, which
    /// lays out the rest of what stands round the edges, and from the tick when the setting changes.
    ///
    /// <para>The margins are the wall's inset (<see cref="LibraryEdgeInset"/>) on the edges the card
    /// stands against: at the top in the middle, clear of the back key and the tools in the two top
    /// corners; at the bottom in the middle; and in a bottom corner, the same inset from the side as from
    /// the bottom. The pill hangs below the card's lower edge inside the card's grid (see
    /// SizeLibraryPlayer), so at the bottom the inset is measured to the pill's foot.</para>
    /// </summary>
    private void PlaceMiniPlayer()
    {
        var was = _miniPlace;
        _miniPlace = MiniPlaceWanted;
        const double inset = LibraryEdgeInset;

        (LibraryPlayer.HorizontalAlignment, LibraryPlayer.VerticalAlignment, LibraryPlayer.Margin) = _miniPlace switch
        {
            MiniPlayerPlace.Bottom => (HorizontalAlignment.Center, VerticalAlignment.Bottom, new Thickness(inset, 0, inset, inset)),
            MiniPlayerPlace.BottomLeft => (HorizontalAlignment.Left, VerticalAlignment.Bottom, new Thickness(inset, 0, 0, inset)),
            MiniPlayerPlace.BottomRight => (HorizontalAlignment.Right, VerticalAlignment.Bottom, new Thickness(0, 0, inset, inset)),
            _ => (HorizontalAlignment.Center, VerticalAlignment.Top, new Thickness(inset, inset, inset, 0)),
        };

        // The pointer's edge strip waits at the edge the card comes in from.
        PlayerPeekEdge.VerticalAlignment = MiniAtTop ? VerticalAlignment.Top : VerticalAlignment.Bottom;

        // Off takes the card out of the layout, so the remote's search cannot find its stop either (see
        // RemoteSearch, which leaves out what is collapsed). Coming back, its lines are measured again: a
        // line measured while it was collapsed is measured at nothing, and never scrolls.
        var off = _miniPlace == MiniPlayerPlace.Off;
        LibraryPlayer.Visibility = off ? Visibility.Collapsed : Visibility.Visible;
        if (off)
        {
            _playerShown = 0;
            LibraryPlayer.IsHitTestVisible = false;
        }
        else if (was == MiniPlayerPlace.Off)
        {
            FitLibraryPlayerLines(remeasure: true);
        }

        PlacePlayer();
        if (was != _miniPlace)
            SayPlayerAway("placed " + _miniPlace);
    }

    /// <summary>The collection coming up: the card is up (down where it would hold the mark off home) and
    /// the count starts again. Called from <see cref="ShowLibrary"/>, so every visit starts with the card
    /// in place.</summary>
    private void ResetPlayerAway()
    {
        _awayOn = _collection;
        _markSeen = 0;
        _peek = false;
        _scrolledAway = false;
        _wasAtTop = CollectionAtTop;
        var up = !MarkKeptOff && _miniPlace != MiniPlayerPlace.Off;
        _playerShown = up ? 1 : 0;
        LibraryPlayer.IsHitTestVisible = up;
        PlacePlayer();
        SayPlayerAway("up, count reset");
    }

    /// <summary>The pointer at the edge of the screen the card stands at (see PlaceMiniPlayer). Said once
    /// per reveal, not on every move.</summary>
    private void PlayerPeekEdge_Pointer(object sender, PointerRoutedEventArgs e)
    {
        if (_peek)
            return;
        _peek = true;
        SayPlayerAway("edge reached");
    }

    /// <summary>The screen in front moved, by <paramref name="moved"/> points, positive down: home's
    /// scroller (HomeView.Scrolled) or the wall's (LibraryScroll_ViewChanged). Any move ends a reveal
    /// from the top edge; a move down past <see cref="FirstFocusAllowance"/> sends the card away whatever
    /// is playing. A move down within it (the remote's first focus bringing its element into view) leaves
    /// the card where it is.</summary>
    private void Collection_Scrolled(double moved)
    {
        if (_collection == CollectionScreen.Home)
            _homeScrolled = _home is null || _home.IsAtTop ? 0 : Math.Max(0, _homeScrolled + moved);

        var down = moved > 0 && !CollectionAtTop;
        if (!_peek && (!down || _scrolledAway))
            return;

        _peek = false;
        if (down)
            _scrolledAway = true;
        SayPlayerAway(down ? "scrolled down" : "scrolled, reveal ended");
    }

    /// <summary>One line on the home channel: what happened, and the facts the card is decided
    /// on.</summary>
    private void SayPlayerAway(string what) =>
        RPM.Core.Diagnostics.Scope.Said(
            "home",
            $"mini player {what}: on {_collection}, mark seen {_markSeen:0.0}s, mark kept off {MarkKeptOff}, peek {_peek}, "
            + $"scrolled away {_scrolledAway}, in front {CollectionInFront}, "
            + $"playing {LibraryPlayerOn}, shown {_playerShown:0.00}, at {_miniPlace}");
}

/// <summary>
/// Where the mini player stands over the collection, as the settings' Mini player row names it (the
/// Android TV app's MiniPlace): at the top in the middle, at the bottom in the middle, in the bottom left
/// or bottom right corner, or not at all. Stored by name (SettingsService.PegasusMiniPlayer), so keep the
/// names. The order is the order the row steps through.
/// </summary>
public enum MiniPlayerPlace
{
    Top,
    Bottom,
    BottomLeft,
    BottomRight,
    Off,
}
