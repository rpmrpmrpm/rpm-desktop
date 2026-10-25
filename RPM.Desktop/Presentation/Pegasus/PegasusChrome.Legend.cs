using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The key legend: a small card that says what the remote's keys do on the screen just opened, as the
/// Android TV app's ControlsLegend does.
///
/// <para><b>What it shows.</b> Two to four keys, each its glyph and what it does there, in one row (see
/// <see cref="LegendKeys"/>). They are the keys the screens' own code has: Back on home opens the side
/// menu and on every other screen leaves it, a held OK opens a row's menu where the rows have one, the
/// carousel turns on Left and Right and opens its letters on Up, the artists list's letters are entered
/// with Right from its last column, and radio's wheel opens on Right held for two seconds.</para>
///
/// <para><b>When.</b> A screen gets its legend once, as it is opened: the screen Pegasus opens on, and
/// every screen opened with the remote after it. A screen come back to with Back gets none, and neither
/// does one shown again as the player, the side menu or a record's page closes over it. Somebody who has
/// just opened a screen is the one who may not know its keys. Coming back to it, they have been there. A
/// screen opened with the mouse gets none either: the legend names the remote's keys. The settings'
/// General section turns it off (Remote key hints).</para>
///
/// <para><b>How long.</b> It fades in, stays five seconds from when it started, and fades out. Any key
/// sends it away at once, and the key still does what it does: the legend takes no focus and no keys.
/// So does the pointer moving. It waits for the screen to be ready (home with its pictures in, the wall
/// read), and it goes as soon as anything comes over its screen: the side menu, the player, another
/// screen.</para>
///
/// <para><b>Where.</b> In the bottom right corner, out of the way of the screens' own keys, or wherever
/// the mini player is not (see <see cref="PlaceLegend"/>).</para>
///
/// <para><b>Who writes what.</b> The keys, the pointer, Back and the collection coming up only set
/// flags. The tick (<see cref="TickLegend"/>) reads those flags and the screen in front, and it is the
/// only place that shows the legend, moves it or takes it down, as for the mini player (see
/// PegasusChrome.PlayerAway).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>The screens the legend can be up for, as far as their keys differ. None is the player
    /// (or the tuner) in front, where there is no legend.</summary>
    private enum LegendScreen
    {
        None,
        Home,
        Wall,
        Radio,
        Artists,
        Genres,
        Artist,
        Genre,
        Playlists,
        Search,
        Record,
    }

    /// <summary>How long the legend takes to come up, how long it stays from then, and how long it takes to
    /// go: the TV's figures.</summary>
    private const double LegendInSeconds = 0.18;
    private const double LegendHeldSeconds = 5;
    private const double LegendOutSeconds = 0.22;

    /// <summary>The screen in front on the last tick, so the tick can tell when another one comes to the
    /// front.</summary>
    private LegendScreen _legendLast;

    /// <summary>The screen just opened, whose legend waits for the screen to be ready (and for nothing to
    /// be over it). None when no legend is waiting.</summary>
    private LegendScreen _legendDue;

    /// <summary>The screen the legend on show is for. None while no legend is shown.</summary>
    private LegendScreen _legendFor;

    /// <summary>Where the legend is between gone (0) and shown (1), before the ease. Linear in time, so a
    /// legend sent away while it is still coming in goes from where it is.</summary>
    private double _legendShown;

    /// <summary>Seconds since the legend on show started to come up.</summary>
    private double _legendAge;

    /// <summary>The legend on show has been told to go: a key, the pointer, its time, or its screen no
    /// longer in front.</summary>
    private bool _legendAway;

    /// <summary>Back was pressed since the last tick. A screen that comes to the front on the same tick
    /// was come back to, and gets no legend. Set by the host (see <see cref="LegendBack"/>), cleared by
    /// the tick.</summary>
    private bool _legendBack;

    /// <summary>The collection came up as the screen Pegasus opens on (see ShowLibrary). Its first screen
    /// gets its legend whatever was used to open Pegasus. Cleared by the tick when it has seen it.</summary>
    private bool _legendOpening;

    /// <summary>Where the pointer was at its last move while a legend was shown, in Root's points. NaN
    /// until the first move after the legend came up.</summary>
    private Point _legendPointerAt = new(double.NaN, double.NaN);

    /// <summary>Whether Root has been asked to tell the legend about the pointer (see
    /// <see cref="ShowLegend"/>).</summary>
    private bool _legendHearsPointer;

    /// <summary>
    /// The host's Back key, before it acts (see PegasusPlayer.TakeBack). The screen it brings to the front,
    /// if any, is one come back to, and gets no legend.
    /// </summary>
    public void LegendBack() => _legendBack = true;

    /// <summary>A key, or the pointer taking over from the keys (see <see cref="KeyboardDriven"/>): the
    /// legend on show goes. A legend that is waiting for its screen is left waiting.</summary>
    private void LegendInput()
    {
        if (_legendFor != LegendScreen.None)
            _legendAway = true;
    }

    /// <summary>The screen in front, for the legend: none while the collection is down (the player is in
    /// front) or the tuner is up, a record's page while one is open, and otherwise the collection's own
    /// screen.</summary>
    private LegendScreen LegendFront =>
        !LibraryUp || TunerUp ? LegendScreen.None
        : RecordUp ? LegendScreen.Record
        : _collection switch
        {
            CollectionScreen.Home => LegendScreen.Home,
            CollectionScreen.Radio => LegendScreen.Radio,
            CollectionScreen.Artists => LegendScreen.Artists,
            CollectionScreen.Genres => LegendScreen.Genres,
            CollectionScreen.Artist => LegendScreen.Artist,
            CollectionScreen.Genre => LegendScreen.Genre,
            CollectionScreen.Playlists => LegendScreen.Playlists,
            CollectionScreen.Search => LegendScreen.Search,
            _ => LegendScreen.Wall,
        };

    /// <summary>Whether a legend may be on show now: the screen is ready and nothing is over it. Not while
    /// the side menu or a panel of it is open, a collection is being switched to, or the carousel's
    /// letters or radio's wheel or search cover the screen.</summary>
    private bool LegendMayShow =>
        LibraryUp && !TunerUp && !LibraryMenuOpen && !SwitchingCollection
        && !CarouselLettersOpen && !RadioWheelOpen && !RadioSearchOpen
        && (RecordUp || CollectionIsReady);

    /// <summary>
    /// One frame of the legend: a screen opened is given its legend once it may show, a legend whose
    /// screen is no longer in front or may no longer show is sent away, and the one on show takes one
    /// step of its fade.
    /// </summary>
    private void TickLegend(double dt)
    {
        var front = LegendFront;
        if (front != _legendLast)
        {
            // Another screen in front. It gets a legend if it was opened (not come back to with Back) with
            // the remote, or if it is the screen Pegasus opened on. A legend still waiting for the last
            // screen is dropped with it.
            var opened = !_legendBack && (KeyboardDriven || _legendOpening);
            _legendDue = front != LegendScreen.None && opened ? front : LegendScreen.None;
            _legendLast = front;
            _legendOpening = false;
        }
        _legendBack = false;

        var may = LegendMayShow;
        if (_legendDue != LegendScreen.None && may)
        {
            var due = _legendDue;
            _legendDue = LegendScreen.None;
            if (Settings?.KeyLegend != false)
                ShowLegend(due);
        }

        if (_legendFor == LegendScreen.None)
            return;

        if (!may || _legendFor != front)
            _legendAway = true;

        _legendAge += dt;
        if (_legendAge >= LegendHeldSeconds)
            _legendAway = true;

        _legendShown = _legendAway
            ? Math.Max(0, _legendShown - (dt / LegendOutSeconds))
            : Math.Min(1, _legendShown + (dt / LegendInSeconds));

        if (_legendShown <= 0 && _legendAway)
        {
            _legendFor = LegendScreen.None;
            KeyLegend.Opacity = 0;
            KeyLegend.Visibility = Visibility.Collapsed;
            return;
        }

        KeyLegend.Opacity = QuadInOut(_legendShown);
        PlaceLegend();
    }

    /// <summary>The TV's QuadInOut: slow at both ends.</summary>
    private static double QuadInOut(double t) =>
        t < 0.5 ? 2 * t * t : 1 - (Math.Pow((-2 * t) + 2, 2) / 2);

    /// <summary>Builds the legend for <paramref name="screen"/> at this window's scale and starts it
    /// coming up.</summary>
    private void ShowLegend(LegendScreen screen)
    {
        ListenToPointerForLegend();

        var scale = QueueScale;
        var font = (FontFamily)Resources["PegasusFa"];
        KeyLegendKeys.Children.Clear();
        KeyLegendKeys.Spacing = 28 * scale;
        KeyLegendKeys.Margin = new Thickness(20 * scale, 14 * scale, 20 * scale, 14 * scale);

        foreach (var (glyph, label) in LegendKeys(screen))
        {
            var key = new StackPanel { Orientation = Orientation.Horizontal, Spacing = 10 * scale };
            key.Children.Add(new TextBlock
            {
                Text = glyph,
                FontFamily = font,
                FontSize = 20 * scale,
                Foreground = LegendInk,
                VerticalAlignment = VerticalAlignment.Center,
            });
            key.Children.Add(new TextBlock
            {
                Text = Loc.Get(label),
                FontSize = 20 * scale,
                FontWeight = FontWeights.SemiBold,
                Foreground = LegendInk,
                MaxLines = 1,
                TextWrapping = TextWrapping.NoWrap,
                VerticalAlignment = VerticalAlignment.Center,
            });
            KeyLegendKeys.Children.Add(key);
        }

        var corner = new CornerRadius(7 * scale);
        KeyLegendFrost.CornerRadius = corner;
        KeyLegendGlass.CornerRadius = corner;

        _legendFor = screen;
        _legendAge = 0;
        _legendAway = false;
        _legendPointerAt = new Point(double.NaN, double.NaN);
        KeyLegend.Visibility = Visibility.Visible;
        PlaceLegend();
        RPM.Core.Diagnostics.Scope.Said("window", $"key legend: up for {screen}");
    }

    /// <summary>The legend's words and glyphs: the TV's Ink.Words, white.</summary>
    private static readonly SolidColorBrush LegendInk = new(Microsoft.UI.Colors.White);

    // The keys' glyphs, from the app's Font Awesome, as the TV's legend draws them: Back (undo), the arrows
    // (arrows-alt), Left and Right (arrows-alt-h), Up, Right, and OK (dot-circle).
    private const string LegendBackGlyph = "";
    private const string LegendArrowsGlyph = "";
    private const string LegendSidewaysGlyph = "";
    private const string LegendUpGlyph = "";
    private const string LegendRightGlyph = "";
    private const string LegendOkGlyph = "";

    /// <summary>
    /// The keys worth saying on <paramref name="screen"/>, as the screens' own code has them, each a
    /// glyph and the key of what it does. The TV's lists (legendOf), where Pegasus has the same keys:
    /// the wall's grid and the genre page add the held OK's menu, which Pegasus has on their cards and
    /// songs; the artists list shows its letters only while it has them (in name order).
    /// </summary>
    private IEnumerable<(string Glyph, string Label)> LegendKeys(LegendScreen screen)
    {
        var move = (LegendArrowsGlyph, "Tv_LegendMove");
        var open = (LegendOkGlyph, "Playlists_Reopen");
        var heldMenu = (LegendOkGlyph, "Tv_LegendHoldMenu");
        var back = (LegendBackGlyph, "Album_Back");

        return screen switch
        {
            // Back on home opens the side menu (see PegasusPlayer.TakeBack).
            LegendScreen.Home => [(LegendBackGlyph, "Viz_Menu"), move, open],
            LegendScreen.Wall => _carouselOn
                ? [(LegendSidewaysGlyph, "Tv_LegendTurn"), (LegendUpGlyph, "Tv_LegendLetters"), open, back]
                : [move, open, heldMenu, back],
            LegendScreen.Search => [move, open, heldMenu, back],
            LegendScreen.Radio => [(LegendOkGlyph, "Tv_LegendTune"), heldMenu, (LegendRightGlyph, "Tv_LegendHoldModes"), back],
            LegendScreen.Artists => _ladderLetters.Count > 0
                ? [move, open, (LegendRightGlyph, "Tv_LegendLetters"), back]
                : [move, open, back],
            LegendScreen.Record => [move, (LegendOkGlyph, "AlbumMenu_Play"), heldMenu, back],
            LegendScreen.Artist or LegendScreen.Genre => [move, open, heldMenu, back],
            _ => [move, open, back],
        };
    }

    /// <summary>The air between the legend and the mini player, where the legend has to stand above the
    /// card to keep clear of it.</summary>
    private const double LegendCardGap = 16;

    /// <summary>
    /// Where the legend stands: the bottom right corner, the wall's inset in from both edges, as on the
    /// TV.
    ///
    /// <para>Clear of the mini player, wherever the settings put it (see <see cref="MiniPlayerPlace"/>),
    /// on the screens it stands over (not a record's page). With the card in the bottom right corner the
    /// legend takes the bottom left, as on the TV. A card at the bottom in the middle is wide, and a legend
    /// of four keys in the bottom right can run under it; there the TV moves the legend to the top right,
    /// but Pegasus's top right holds the wall's tools, so the legend stands in the bottom right above the
    /// card's height instead, and only when the two would meet. The card's place is the setting's, not
    /// whether the card is up at this moment, because the card can come back while the legend is
    /// shown.</para>
    ///
    /// <para>Asked on every frame the legend is on show, because its own width is only known once it has
    /// been measured, and a resize moves both.</para>
    /// </summary>
    private void PlaceLegend()
    {
        const double edge = LibraryEdgeInset;
        var cardHere = _legendFor != LegendScreen.Record && _miniPlace != MiniPlayerPlace.Off;
        var cardAtBottom = cardHere && !MiniAtTop;

        var left = cardAtBottom && _miniPlace == MiniPlayerPlace.BottomRight;
        var bottom = edge;

        if (cardAtBottom)
        {
            // The two spans across the screen, the legend's and the card's, from their alignments.
            var width = Root.ActualWidth;
            var legendWidth = KeyLegend.ActualWidth;
            var legendLeft = left ? edge : width - edge - legendWidth;
            var cardWidth = LibraryPlayer.ActualWidth;
            var cardLeft = _miniPlace switch
            {
                MiniPlayerPlace.BottomLeft => edge,
                MiniPlayerPlace.BottomRight => width - edge - cardWidth,
                _ => (width - cardWidth) / 2,
            };

            var meet = legendLeft < cardLeft + cardWidth + LegendCardGap
                       && cardLeft < legendLeft + legendWidth + LegendCardGap;
            if (meet)
                bottom = edge + LibraryPlayer.ActualHeight + LegendCardGap;
        }

        var align = left ? HorizontalAlignment.Left : HorizontalAlignment.Right;
        if (KeyLegend.HorizontalAlignment != align)
            KeyLegend.HorizontalAlignment = align;

        var margin = left ? new Thickness(edge, 0, 0, bottom) : new Thickness(0, 0, edge, bottom);
        if (!KeyLegend.Margin.Equals(margin))
            KeyLegend.Margin = margin;
    }

    /// <summary>Asks Root, once, to tell the legend about every pointer move and press, those a control
    /// under the pointer has handled as well.</summary>
    private void ListenToPointerForLegend()
    {
        if (_legendHearsPointer)
            return;
        _legendHearsPointer = true;
        Root.AddHandler(PointerMovedEvent, new PointerEventHandler(Legend_Pointer), handledEventsToo: true);
        Root.AddHandler(PointerPressedEvent, new PointerEventHandler(Legend_Pointer), handledEventsToo: true);
    }

    /// <summary>How far the pointer has to move, in points, before the legend counts it as somebody using
    /// the pointer. A screen scrolling under a cursor that stands still raises moves at the same place,
    /// and those do not count (as in PegasusPlayer.PointerReallyUsed).</summary>
    private const double LegendPointerMoves = 4;

    /// <summary>The pointer moved or was pressed: somebody is using the mouse, and the legend of the
    /// remote's keys goes. The first move only says where the pointer is.</summary>
    private void Legend_Pointer(object sender, PointerRoutedEventArgs e)
    {
        if (_legendFor == LegendScreen.None || _legendAway)
            return;

        var point = e.GetCurrentPoint(Root);
        var at = point.Position;
        var was = _legendPointerAt;
        _legendPointerAt = at;

        var pressed = point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed
                      || point.Properties.IsMiddleButtonPressed;
        if (!pressed && double.IsNaN(was.X))
            return;

        var dx = at.X - was.X;
        var dy = at.Y - was.Y;
        if (pressed || (dx * dx) + (dy * dy) > LegendPointerMoves * LegendPointerMoves)
            _legendAway = true;
    }
}
