using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The album page fitting itself to the card it is drawn in. The ladder is
/// <see cref="AlbumPageDensity"/>, and this is the page taking one rung of it.
///
/// <para>Nothing here is announced. A reader who never resizes the window sees one layout and no sign
/// that there was a choice. A reader who drags the window narrow sees the columns give way, least
/// valuable first. At the end the groove, and then the papers, step behind a tab beside the tracks
/// instead of leaving the card.</para>
///
/// <para><b>The two panels the page is drawn as</b> are the album (its sleeve, its name and its songs)
/// and the papers (what the catalogues say). The groove is a third column beside them and is not part
/// of that pair. It is the app's own wall for the record. It keeps its full height below the foot where
/// the suggestions sit, and the record's colour wash stops above it.</para>
/// </summary>
public sealed partial class AlbumDetailView
{
    /// <summary>The space the page's scrolling columns keep clear on their right for a scrollbar: the
    /// album head's margin, the tracklist's padding and the papers' margin, all 10. It is read from the
    /// ladder, not written again here. The rung's arithmetic subtracts this lane when it works out what
    /// the join costs (see AlbumPageRung.JoinAir). A second copy of the ten could come to disagree with
    /// the ladder.</summary>
    private const double ScrollLane = AlbumPageDensity.ScrollLane;

    /// <summary>
    /// The space between two panels stacked one under the other, and between the last of them and the
    /// row of suggestions along the foot.
    ///
    /// <para>It is wider than the gap between two COLUMNS, on purpose. Side by side, the whole length
    /// of the gap tells one column from its neighbour. Stacked, only the height of one gap says where
    /// the record stops and the catalogue text begins. At a column's gap that read as one long page
    /// rather than three sections. This is the page's own margin and a little more: enough that the
    /// break is the first thing seen, but not enough to cost a scroll.</para>
    /// </summary>
    private const double StackAir = 36;

    /// <summary>
    /// The leading above a section heading: the space a 12-point line has above its text inside its own
    /// box.
    ///
    /// <para>It is taken off the top of the papers column, so the first heading there, "About the
    /// album", has its capitals on the same line as the top edge of the sleeve. Boxes aligned at the
    /// top of a page only look aligned if their ink is level. A picture's top edge is its ink, but the
    /// top of a line of type is a few points of empty space above the letters.</para>
    /// </summary>
    private const double HeadingLead = 3;

    /// <summary>
    /// The same space above the groove's NAME. The name is a different font at a different size, so it
    /// needs a different number.
    ///
    /// <para>The name is set in Thiamine at size 72, in the 36-high box that every pane in the app uses
    /// for its name. The line box is 0.485em (34.9) and is centred in the 36. The ink of an ascender starts a further 0.300em
    /// less 0.247em down the line, so the ink of the word starts 4.4 below the top of the box. The
    /// heading across from it has its ink 0.75 below the top of the column, which is what remains of a
    /// 12-point line's leading once <see cref="HeadingLead"/>'s three points are taken off. The
    /// difference is this, rounded to the half point the page is laid out on. Without it, the name sits
    /// about three pixels lower than the heading across from it.</para>
    ///
    /// <para>It is taken off the WHOLE column rather than off the name, the same way HeadingLead is
    /// taken off the papers stack. The column keeps its own spacing and its foot does not move. A Grid
    /// stretched in its row grows by what its top margin lifts. It is zero where the groove stands
    /// under the record instead of beside it, because there is nothing across from it to line up
    /// with.</para>
    /// </summary>
    private const double GrooveNameLead = 3.5;

    /// <summary>Half the width of the fold key on the join. The key is 36 wide: the shared glow key at
    /// the home screen's row size, with no slot or border round it. The key is centred across a
    /// hairline, not aligned to an edge, so it is the only key on the page that needs its own half. If
    /// the key's Width changes in the markup, change this too.</summary>
    private const double JoinKeyHalf = 18;

    /// <summary>How much of the join's hairline is left out behind the fold key, centred on it. It is
    /// enough to clear the twelve-point chevron with some air on each side, and less than the key's own
    /// 44, so the line still reads as running up to the key.</summary>
    private const double JoinGap = 28;

    private void GrooveEdge_SizeChanged(object sender, SizeChangedEventArgs e) => CutJoinGap();

    /// <summary>
    /// Leaves a gap in the join's hairline behind the fold key, or closes the gap when the key is down.
    /// </summary>
    /// <remarks>
    /// The key's centre is worked out from the line's own height and margins, not read off the key.
    /// Both span the same three rows of the page's grid. The key is centred in those rows. The line
    /// overhangs them by the page's top padding above and its foot padding below (the negative margins
    /// set in Frame). So the key's centre is half the rows' height down from their top, which is
    /// <c>(height + above - below) / 2</c> down the line.
    ///
    /// <para>It runs from three places, because three things change the answer: the line's size (its
    /// SizeChanged), its margins (Frame, after it writes them; a rung can change the padding without
    /// changing the line's height, and then no SizeChanged comes), and whether the key is up
    /// (ShowGrooveKeys).</para>
    ///
    /// <para>The gap is drawn as two pieces, above and below the key. They move together during a
    /// groove fold, because GrooveGlide carries the line's Grid and the key with it. So the gap stays
    /// behind the key while it slides.</para>
    /// </remarks>
    private void CutJoinGap()
    {
        var height = GrooveEdge.ActualHeight;

        if (GrooveFoldJoin.Visibility != Visibility.Visible || height <= JoinGap)
        {
            GrooveEdgeAbove.Clip = null;
            GrooveEdgeBelow.Visibility = Visibility.Collapsed;
            return;
        }

        var centre = (height - GrooveEdge.Margin.Top + GrooveEdge.Margin.Bottom) / 2;
        var top = Math.Clamp(centre - JoinGap / 2, 0, height);
        var foot = Math.Clamp(centre + JoinGap / 2, 0, height);
        var width = Math.Max(1, GrooveEdge.ActualWidth);

        GrooveEdgeAbove.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, top) };
        GrooveEdgeBelow.Clip = new RectangleGeometry
        {
            Rect = new Windows.Foundation.Rect(0, foot, width, height - foot),
        };
        GrooveEdgeBelow.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// How wide either of the two panels may be on a card this wide. The value is the artist page's own
    /// column, taken from the same arithmetic that page uses (ArtistPageLayout.Columns), not copied as
    /// a number. A reader who goes from an artist to one of their records must not see the column of
    /// writing change width. The two pages are drawn in the same card, so the same question has the
    /// same answer on both.
    ///
    /// <para>AboutMax is the artist column's ceiling, not its width, so it is not used for a card that
    /// has a column. The artist page only reaches 520 on a panel about 1300 wide. Below that, its
    /// column is two fifths of the card. Using the ceiling would give the two pages different widths
    /// on the same card.</para>
    ///
    /// <para>A folded card (too narrow for the artist page to have a column at all) gets the ceiling,
    /// which caps nothing. The panels there are already under it, held up by their own floors. A cap of
    /// zero would answer a different question. The answer to this one is that a card that narrow has no
    /// cap.</para>
    /// </summary>
    private static double PanelCap(double card)
    {
        var (folded, width) = RPM.Core.Library.ArtistPageLayout.Columns(card);
        return folded ? RPM.Core.Library.ArtistPageLayout.AboutMax : width;
    }

    /// <summary>How far the record behind the sleeve stands out past its right edge, as a share of the
    /// sleeve. It is read from the ladder, where the sleeve's own arithmetic is (see
    /// AlbumPageDensity.SleeveEdge). The stage must be as wide as the pair the ladder sized. Two copies
    /// of this number could disagree.</summary>
    private const double DiscShare = AlbumPageDensity.DiscShare;

    /// <summary>Which rung the page stands on, so a resize that changes nothing does nothing.</summary>
    private AlbumPageRung _rung;

    /// <summary>
    /// Where the groove stands on the page: beside the record, under it, or nowhere.
    ///
    /// <para>It is one value with three places, not a "stacked" flag and a "hidden" flag. Two flags
    /// could make a fourth state that means nothing: stacked AND hidden. That would be a panel in the
    /// stack using the stack's spacing while drawing nothing. The place is worked out from the rung and
    /// from whether the record has a groove at all, in one function (see <see
    /// cref="GroovePlaceWanted"/>). That function is the only code that decides it.</para>
    /// </summary>
    private enum GroovePlace
    {
        /// <summary>Beside the record, in the third column. This is what the markup holds, so it is
        /// listed first. The page starts here, and nothing has to move to make that true.</summary>
        Column,

        /// <summary>Under the record, in the stack the papers also fold into.</summary>
        Stacked,

        /// <summary>Not on the page. The groove keeps its COLUMN, collapsed, with the column folded to
        /// nothing. See <see cref="PlaceGroove"/> for why it is not removed from the tree.</summary>
        Off,
    }

    private GroovePlace _groovePlace;

    /// <summary>Whether the record on screen has a groove at all. Written by <see cref="ShowGroove"/>,
    /// and read by everything that measures the page.</summary>
    private bool _grooveOff;

    /// <summary>
    /// Whether the groove is folded away. This is a different question from whether the record has one.
    /// </summary>
    /// <remarks>
    /// There are two facts, not one, because they answer differently and the page does different things
    /// with them. A record with no groove offers no way to open one, since there is nothing behind the
    /// key. A folded groove is the reader's own choice, or the page's choice for a window that shares
    /// the screen. The key at the right edge brings it back.
    ///
    /// <para>Both end at the same place, <see cref="GroovePlace.Off"/>, which is already the state the
    /// column is folded to nothing in. So this adds a reason, not a new mechanism.</para>
    /// </remarks>
    private bool _grooveShut;

    private bool _papersStacked;
    /// <summary>What the row of suggestions is doing. It is the reader's choice where they have made
    /// one, and the ladder's choice where they have not (see
    /// SettingsService.AlbumSimilarOpen).</summary>
    private SimilarState _similarRow = SimilarState.Open;

    private bool _topReviewAllowed = true;

    private static Services.Shell.SettingsService Settings
        => App.Services.GetRequiredService<Services.Shell.SettingsService>();

    private void Page_SizeChanged(object sender, SizeChangedEventArgs e) => ApplyDensity();

    /// <summary>Puts the page on the widest rung this card can hold, and drops whatever the card's
    /// height cannot hold.</summary>
    private void ApplyDensity()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (width <= 0 || height <= 0)
            return;

        // The fold is decided before the width pass, which reads it. The fold is one of the two things
        // that decide where the groove goes, and ApplyWidth's own early return asks that question. The
        // rung is worked out here, not inside ApplyWidth, because the fold depends on the rung: a page
        // with no column for the groove has no fold. So the two must be decided in this order.
        var rung = AlbumPageDensity.For(width);
        _grooveShut = GrooveShutWanted(rung.GrooveHasColumn);

        LayOut(rung, width, height);
    }

    /// <summary>
    /// Every pass that shapes the page, in order, for whatever the fold is currently set to.
    /// </summary>
    /// <remarks>
    /// <b>One method, because the slide lays the page out too, and it must get the same page.</b> The
    /// slide ran ApplyWidth and CapPanels alone, which is the width part only. The page it animated
    /// over differed from the settled page in everything the other two passes write. Without this, the
    /// tracklist is 44 points shorter for the length of the move. That is two songs dropping off the
    /// end of the list, and coming back when the move ends.
    ///
    /// <para>The keys are in here, not inside ApplyWidth, for a reason of the same kind. Whether the
    /// fold is offered at all depends on the window's share of the screen (see ShowGrooveKeys). That
    /// changes on every pixel of a drag. ApplyWidth returns early whenever the rung and the groove's
    /// place are both unchanged. So dragging a maximised window past the share left the page with no
    /// key on it, although the groove could still be folded.</para>
    /// </remarks>
    /// <summary>The same pass for a caller that has set the fold by hand and wants the page laid out at
    /// whatever rung this width gives. This is the glide. It runs only where there is a key to press,
    /// so only where the groove has a column.</summary>
    private void LayOut(double width, double height) => LayOut(AlbumPageDensity.For(width), width, height);

    private void LayOut(AlbumPageRung rung, double width, double height)
    {
        ApplyWidth(rung);

        // After the rung, because it reads _rung to know whether the papers have a column to cap, and
        // OUTSIDE ApplyWidth, because that returns early on an unchanged rung. The cap is a function of
        // the card and the card changes on every pixel of a drag; written in there it would be set once
        // per rung and be wrong for every width in between.
        CapPanels(width);
        ApplyStacking();
        ShowGrooveKeys();
        ApplyHeight(height);
    }

    /// <summary>Where the groove belongs on a card this wide: the rung's answer, narrowed by whether
    /// the record has a groove at all. This is the one place that decides it.</summary>
    private GroovePlace GroovePlaceWanted(AlbumPageRung rung)
        => _grooveOff || _grooveShut ? GroovePlace.Off
            : rung.GrooveHasColumn ? GroovePlace.Column
            : GroovePlace.Stacked;

    /// <summary>Whether the groove stands under the record rather than beside it. While anything is
    /// stacked, the page scrolls as a whole (see <c>PageScrolls</c>).</summary>
    private bool GrooveStacked => _groovePlace is GroovePlace.Stacked;

    /// <summary>Whether the groove has its own column right now. It is safe to ask before the panel has
    /// been moved, because both halves are written before the placement is. That is why <see
    /// cref="Frame"/> can use it.</summary>
    private bool GrooveHasColumn => _rung.GrooveHasColumn && !_grooveOff && !_grooveShut;

    private void ApplyWidth(AlbumPageRung rung)
    {
        // Both the rung and the groove's wanted place are checked, because the second can change
        // without the first. A record opened on a borrowed shelf takes the groove off the page at
        // whatever width the card is already at. A guard on the rung alone would say nothing has moved
        // and leave the column standing.
        if (rung == _rung && GroovePlaceWanted(rung) == _groovePlace)
            return;

        var coverChanged = Math.Abs(rung.Cover - _rung.Cover) > 0.5;
        _rung = rung;

        // Zero once the papers have no column. This is what made the record's own panel come out
        // narrower than the panels under it.
        //
        // A Grid spends the spacing BETWEEN its columns, whether or not the columns have any width.
        // With the papers and the groove folded to nothing, the album column is still followed by two
        // gaps it cannot use. So it stops two gaps short of the page, while the stack underneath, which
        // spans to the far side of them, does not. The left edges match and the right edges differ, so
        // the tracklist looks like a panel with a wider margin down one side than the other.
        //
        // This is asked of the papers, not of "is anything stacked", because it is a question about
        // COLUMNS. On the rung where the groove has folded and the papers have not, there really are
        // two columns side by side, and the gap between them is real.
        //
        // Zero again where the groove is off the page altogether. Then there are two columns, and they
        // need one gap between them. The grid cannot spend that gap without also spending a second one
        // after the last column. So the papers carry that gap as a margin of their own, and the grid
        // spends none. Without it, the panels stop a gap short of the card's right edge while the
        // ground reaches it, and the page looks shifted left. The margin is zero in every other state,
        // including when the papers are stacked. Down there they stand under the record at full width,
        // and a left margin would be an indent nothing accounts for.
        var gridGaps = rung.AboutHasColumn && !_grooveOff && !_grooveShut;
        PageGrid.ColumnSpacing = gridGaps ? rung.Gap : 0;
        PapersColumn.Margin = new Thickness(
            rung.AboutHasColumn && !gridGaps ? rung.Gap : 0, 0, 0, 0);
        PageGrid.RowSpacing = rung.Gap;

        // The record's ground reaches the CARD's edges: the page's own padding is given back on three
        // sides. On the fourth it stops between the last of the papers and the first of the groove.
        // That side is an EDGE of the page like the other three, so it has the same margin. The divider
        // stands Pad past the last of the papers, and the groove starts Pad past the divider. The
        // papers already keep a lane for their scrollbar, so the ground reaches past its column by what
        // is left of that Pad once the lane is counted. Both sides of the join then have the page's own
        // air, which is the only thing a reader can see about where a colour ends. Half of the leftover
        // gap would be 16 points, less than every other edge of the page, and the seam would look
        // pinched.
        //
        // On a card with no groove column there is no join to stop at, so the ground takes the whole
        // page. It is SPANNED across all three columns to do this, rather than reaching further right
        // by the gap. The third column is zero wide there, but the spacing before it is still spent. A
        // ground that guessed at that number would leave a stripe of bare card, or paint over the panel
        // beside it.
        _joinEdge = GrooveHasColumn ? AlbumPageDensity.JoinEdge : rung.Pad;

        // The three ground layers span every column on every rung, so there is nothing to switch here.
        // Narrowing them to two columns whenever the groove has a column would stop the record's colour
        // at the join.

        Frame();

        // Both panels are capped at the width of the ARTIST page's own column. The cap is read from
        // that page's arithmetic (see ArtistPageLayout.AboutMax), not written again here. Both pages
        // use the same reason, and it is about reading rather than balance. Past about 500 points a
        // paragraph is too long to find the start of the next line, which is why newspapers use
        // columns. A tracklist and an encyclopedia article are both paragraphs.
        //
        // It is a cap, not a width. This page has three columns where the artist page has two. The
        // panels take their share until they reach the cap, and everything past it goes to the groove.
        AlbumCol.Width = new GridLength(rung.AlbumStar, GridUnitType.Star);
        AlbumCol.MinWidth = rung.AlbumMin;

        PapersCol.MinWidth = rung.AboutHasColumn ? rung.AboutMin : 0;
        PapersCol.Width = rung.AboutHasColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // The caps are NOT written here. See CapPanels. The caps answer to the card's width, and this
        // method answers to the rung. The rung changes at five widths, and the card changes at every
        // width.

        GrooveCol.MinWidth = GrooveHasColumn ? rung.GrooveMin : 0;
        GrooveCol.Width = GrooveHasColumn ? new GridLength(1, GridUnitType.Star) : new GridLength(0);

        // The sleeve's cap has changed, so the sleeve must be measured again, even if its column is
        // exactly as wide as before.
        if (coverChanged)
            FitAlbumColumn();

        PlaceGroove(GroovePlaceWanted(rung));
        ShowGrooveKeys();
        PlacePapers(stacked: !rung.AboutHasColumn);
    }

    /// <summary>Where the divider between the record and the groove stands: halfway across what is left
    /// of the gap to the groove, once both panels have their scrollbar lane. On a card with no groove
    /// column, it is the page's own padding instead. It is set with the rung and read by <see
    /// cref="Frame"/>.</summary>
    private double _joinEdge;

    /// <summary>
    /// The page's own margins: the air around the columns, and the ground and its corner bled back out
    /// through that air to the card's edges.
    ///
    /// <para>It is one method, because the padding and the ground's margin are one measurement seen
    /// from two sides. The padding pushes the columns in. The ground's negative margin pushes the
    /// colour back out by the same numbers. So the record's wash reaches the card's edges however much
    /// air the page carries. If the two were written separately, they could be changed apart, and the
    /// result would be a stripe of bare card down one side that nobody could explain.</para>
    ///
    /// <para>The margin is 28 on three sides and 18 under the foot, on every rung. See
    /// AlbumPageRung.Pad for why a side with a scroll lane needs fewer points to look level with one
    /// that has none. The page uses the same margin at every size. There is nothing left to trade, so a
    /// short card does not give up air.</para>
    /// </summary>
    /// <summary>
    /// The ceiling each of the two panels has on a card this wide. The question is different in the two
    /// layouts the page has.
    ///
    /// <para><b>With the groove beside the record, there is no ceiling on either.</b> The three columns
    /// divide the card in the shares they are written with. The record and its papers take two thirds
    /// of it, and the groove has the last third, at every width. A ceiling here would not narrow a line
    /// of writing. It would pass the width to the groove. Star columns that reach their maximum stop
    /// taking width, and the columns still asking share what is left. The groove is the only one of the
    /// three with no maximum. So a maximised card would draw the groove at half its width and the
    /// record at a third.</para>
    ///
    /// <para><b>With no groove column, the RECORD's panel keeps the width it has beside the groove</b>,
    /// and the papers take everything the fold frees. On a folded page, the papers should take the
    /// freed width, not the tracks.</para>
    ///
    /// <para>So a fold does not touch the record's panel at all. The sleeve, the name, the buttons, and
    /// the tracklist with its durations anchored to its right edge are the same page at the same width
    /// before, during and after the fold. The glide has nothing to carry on that side. What grows is
    /// the column of writing, which is the one that gains from a wider card.</para>
    ///
    /// <para>The reading cap is still the whole rule on the rungs that seat no groove at all. There is
    /// no fold there, so there is nothing to measure against.</para>
    /// </summary>
    private void CapPanels(double width)
    {
        var cap = PanelCap(width);

        // No PANEL is ever held to a width of its own. The ceilings below are on the columns. Holding
        // the panels is what put air on the folded page. The things inside the panels that a fold must
        // not re-cut ask for their open-state width one at a time instead: the sleeve through
        // SleeveRoom, and the suggestion tiles through SimilarJustifyWidth.
        AlbumColumn.MaxWidth = double.PositiveInfinity;
        PapersColumn.MaxWidth = double.PositiveInfinity;

        if (GrooveHasColumn)
        {
            AlbumCol.MaxWidth = double.PositiveInfinity;
            PapersCol.MaxWidth = double.PositiveInfinity;
            return;
        }

        // AlbumRoom is the width the record's column has with the groove beside it. It returns null on
        // a rung that seats no groove at all. That is the one case here that is not a fold: nothing
        // moves there, so the old rule applies, and the reading cap is all there is.
        //
        // No gap is added here, and none is needed. The record's column is first in the row and has no
        // margin in either layout. It is the PAPERS that carry the join's gap as a left margin, once
        // the grid stops spending it as column spacing (see ApplyWidth). The papers are not capped here
        // at all.
        //
        // A folded papers column is capped at zero, not left alone. It is zero wide either way. A
        // stated zero cannot come back as a stale width the next time the column is unfolded.
        if (AlbumPageDensity.AlbumRoom(width, _rung) is { } albumRoom)
        {
            AlbumCol.MaxWidth = albumRoom;
            PapersCol.MaxWidth = _rung.AboutHasColumn ? double.PositiveInfinity : 0;
            return;
        }

        AlbumCol.MaxWidth = double.PositiveInfinity;
        PapersCol.MaxWidth = _rung.AboutHasColumn ? cap : 0;
    }

    private void Frame()
    {
        // All four sides are the page's own margin, and they are the same number (see
        // AlbumPageDensity.PageAir). The foot is a field of its own, because it is the one side paid
        // for by the tracklist's own height. It can change apart from the other three, but it does not today.
        PageGrid.Padding = new Thickness(_rung.Pad, _rung.Pad, _rung.Pad, _rung.PadFoot);

        // The way back sits in the card's own corner, the same distance in from the left as the page
        // starts from the top. The four edges of the page are one number, so the close button needs no
        // offset of its own.
        CloseButton.Margin = new Thickness(0);

        // The record's ground reaches the CARD's edges on all four sides, with the page's own padding
        // given back. The colour covers the whole room. The divider marks where the record's page ends
        // (see GrooveEdge, which stands at the join).
        var ground = new Thickness(-_rung.Pad, -_rung.Pad, -_rung.Pad, -_rung.PadFoot);
        PageGround.Margin = ground;
        PageWash.Margin = ground;

        // The join with the pane on the left is part of the ground, so it takes the ground's shape. Its
        // corner must be the page's corner, not a corner of its own a few points inside.
        PageCorner.Margin = ground;

        // The divider stands at the JOIN. These are its own three numbers, not the ground's, and the
        // two can change apart. Its left is zero, because it is a hairline that hangs off the right of
        // the first two columns. It is only drawn on a card that has a join (see PlaceGroove).
        GrooveEdge.Margin = new Thickness(0, -_rung.Pad, -_joinEdge, -_rung.PadFoot);

        // The fold key sits ON that line, centred across it. It is a child of the groove's CELL, so its
        // margin is measured from the cell's left edge. The grid spends the rung's Gap between the
        // papers and the cell. The line stands _joinEdge past the papers, and the key is half its own
        // width to the left of the line. All three change with the rung, which is why this is not a
        // number in the markup.
        //
        // The air on each side of the key is not symmetrical arithmetic, but it comes out even. The
        // papers stop a scroll lane short of their own cell, and the groove starts JoinAir into its own
        // cell. At every rung that leaves about ten points of air on both sides of the key.
        GrooveFoldJoin.Margin = GrooveHasColumn
            ? new Thickness(-(_rung.Gap - _joinEdge + JoinKeyHalf), 0, 0, 0)
            : new Thickness(0);

        // The gap behind the key is measured from the line's margins, which were written just above.
        CutJoinGap();

        // Left: the other half of the join. The grid spends its own Gap between any two columns, and
        // that is less than the page's margin. So the groove carries the rest as a margin of its own.
        // This is the rung's arithmetic (see AlbumPageRung.JoinAir), because it is air the rung must
        // ask the card for. It is zero where the groove sits behind a tab. There is no join then, and a
        // margin would leave a stripe down the inside of the strip.
        //
        // Top: zero. The groove's name starts where the record's page starts, because the four edges of
        // the page are one number (see AlbumPageDensity.PageAir).
        //
        // The left is about standing BESIDE the record, so a groove standing under it does not take it.
        // It is the groove's half of the join, and underneath the record there is no join to have half
        // of.
        //
        // Asked of the RUNG rather than of _groovePlace, because this method runs before the one that
        // moves the panel and would otherwise read where the groove was a moment ago. The rung already
        // knows whether the groove has a column, which is the same question a frame earlier. The
        // suggestions row's own lane, along its FOOT rather than down a side. It is the one pane on
        // this page that scrolls sideways. Written here off the same number so that a lane widened for
        // the columns is widened for the row in the same edit; it is not in ApplyStacking with the
        // other three, because those give their lane up when the page starts scrolling and this one
        // never does: the row scrolls sideways in every state the page has.
        SimilarScroller.Padding = new Thickness(0, 0, 0, AlbumPageDensity.SimilarBarLane);

        // The top is the name's own leading, so the groove's name and the papers' first heading ink on
        // the same line. The three columns then start on ONE line, not three (see GrooveNameLead). The
        // left is the groove's half of the join.
        GrooveColumn.Margin = GrooveHasColumn
            ? new Thickness(_rung.JoinAir, -GrooveNameLead, 0, 0)
            : new Thickness(0);
    }

    /// <summary>
    /// Where the page's foot is. This decides whether the page keeps the card's bottom left corner.
    ///
    /// <para>The four ground layers reach the card's edges (see <see cref="Frame"/>), so they wear the
    /// card's rounding. All four must use the same shape. A corner on one of them and not the others
    /// draws a curve inside a square. The library's message band can sit under this page when there is
    /// news about the shelf. While it does, the card's bottom edge is the band's: the page ends square
    /// on top of it, and the band carries the corner (its own 0,0,0,8).</para>
    ///
    /// <para>It is set from one place, MainPage.SyncLibraryBand. That is the only code that shows or
    /// hides the band, and it asks the band itself rather than remembering what it did. Nothing here
    /// writes these corners back, so opening another record cannot undo them. The two events (the band
    /// changing, and a record opening) cannot land in an order that leaves the wrong corner
    /// showing.</para>
    /// </summary>
    public void StandOn(CardFoot foot)
    {
        var corners = CardCorners.RoundedLeft(foot);

        PageGround.CornerRadius = corners;
        PageWash.CornerRadius = corners;
        PageVeil.CornerRadius = corners;
        PageCorner.CornerRadius = corners;
    }

    // ── the sleeve, and the record standing out of it ──

    /// <summary>The album panel's head has been measured, so the sleeve can be drawn at what the head
    /// can actually have, not at what the rung expected.</summary>
    private void AlbumHead_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitAlbumColumn();

    /// <summary>The column the head stands in has been measured too. That is the other half of the same
    /// question: how much room the head has is the row's answer, not the ladder's estimate. It is asked
    /// here, not computed in <see cref="ApplyHeight"/>, because a row's height is known only after the
    /// layout pass that gives it one.
    /// </summary>
    private void AlbumColumn_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitAlbumColumn();

    /// <summary>The ladder's ceiling for the head of the album column. The live pass below takes the
    /// lower of this and the measured room, without asking the ladder again.</summary>
    private double _factsCap = double.PositiveInfinity;

    /// <summary>
    /// The head of the album column, fitted to the row it actually got.
    ///
    /// <para>Two things are written here and nowhere else: how tall the head may stand before it
    /// scrolls inside itself, and how big the sleeve at the top of it is drawn. They are one method
    /// because they answer one question, which is what the head is allowed. Keeping them in one place
    /// stops the sleeve being drawn taller than the row, which would cut the buttons below it.</para>
    ///
    /// <para>The room is the ROW's, measured. The ladder's estimate only decides whether the
    /// suggestions are drawn at all. Where the page scrolls, there is no ceiling: the column is as tall
    /// as what is in it.</para>
    /// </summary>
    private void FitAlbumColumn()
    {
        var stacked = GrooveStacked || _papersStacked;
        var room = stacked || AlbumColumn.ActualHeight <= 0
            ? double.PositiveInfinity
            : AlbumPageDensity.HeadRoom(AlbumColumn.ActualHeight);

        // Floored, because a row can be shorter than the head is worth drawing at all, and a negative
        // ceiling is not a ceiling. Below the floor, the head scrolls inside what little room there is.
        FactsScroller.MaxHeight = Math.Max(AlbumPageDensity.FactsFloor, Math.Min(_factsCap, room));
        FitName();
        FitCover(AlbumHead.ActualWidth, room);
    }

    /// <summary>
    /// Cuts the record's name to the width the head can give it, so a long name ends in an ellipsis
    /// instead of running off both edges of the panel. See <see cref="AlbumPageDensity.NameWidth"/> for
    /// why the stack it sits in cannot do this by itself.
    ///
    /// <para>Called on every size pass, and again when the page learns whether the record is explicit.
    /// The explicit badge takes part in the arithmetic and is written after the name, so the name is
    /// fitted again then. Guarded on half a point, for the same reason <see cref="FitCover"/> is. The
    /// write re-lays the head, the head raises SizeChanged, and the second pass must give the same
    /// answer and stop.</para>
    /// </summary>
    private void FitName()
    {
        var headWidth = AlbumHead.ActualWidth;
        if (headWidth <= 0)
            return;

        var width = AlbumPageDensity.NameWidth(
            headWidth, ExplicitMark.Visibility == Visibility.Visible);
        if (Math.Abs(TitleText.MaxWidth - width) <= 0.5)
            return;

        TitleText.MaxWidth = width;
    }

    /// <summary>
    /// The sleeve square at whatever the album column can give it, up to the rung's cap. The record
    /// behind it is a third wider again.
    ///
    /// <para>The sleeve's room comes from the ladder (AlbumPageDensity.SleeveEdge): the panel, less the
    /// air it keeps on either side, less what the record standing out to the right takes, capped at the
    /// rung. The air is a SHARE of the panel. A narrower window therefore draws a smaller sleeve with
    /// the same margins, not the same sleeve pressed against the panel's edges. The sleeve is never
    /// less than the lane the back key stands in (AlbumPageDensity.BackKey), which is where the top
    /// band of the sleeve runs. Nothing else on the panel moves for it. The head stays pinned to the
    /// top of the column, and the height the smaller sleeve does not take goes to the tracklist.</para>
    ///
    /// <para>Guarded on half a point. Without the guard, the write below raises SizeChanged again and
    /// the two passes would loop. Setting the stage's width re-lays the stack, the stack raises
    /// SizeChanged, and the second pass computes the same number and returns.</para>
    /// </summary>

    /// <summary>
    /// The width the sleeve is measured against: the head's own width, less whatever a folded groove
    /// has handed to the record's column.
    /// </summary>
    /// <remarks>
    /// The sleeve is a share of the panel it stands in. Folding the groove makes that panel wider. If
    /// the sleeve were measured against it, a press of the fold key would redraw the cover at a new
    /// size before the slide. It is the same picture of the same record either way. The room the fold
    /// frees is room for the page, not a reason to re-cut the cover. So the share is taken against the
    /// column the page HAS (see <see cref="AlbumPageDensity.AlbumRoom"/>), and the fold cannot change
    /// it.
    ///
    /// <para>The difference is read off the COLUMN and taken off the HEAD. That is exact whatever
    /// margins stand between the two, because both numbers are widths of the same column, so what is
    /// between them cancels.</para>
    ///
    /// <para>It is never wider than the head. The arithmetic repeats the grid's own, and a repeat that
    /// came out a point high would draw a sleeve the panel cannot hold.</para>
    /// </remarks>
    private double SleeveRoom(double headWidth)
    {
        if (AlbumPageDensity.AlbumRoom(ActualWidth, _rung) is not { } room)
            return headWidth;

        var freed = Math.Max(0, AlbumColumn.ActualWidth - room);
        return Math.Min(headWidth, headWidth - freed);
    }

    private void FitCover(double headWidth, double headRoom)
    {
        if (headWidth <= 0 || _rung.Cover <= 0)
            return;

        // What is in the head besides the sleeve, asked of the head itself. A record with a two-line
        // title needs a different height from one with a one-line title, and the sleeve pays for the
        // difference. DesiredSize is used, not ActualHeight. A head standing in a row too short for it
        // is arranged at the row's height, so ActualHeight would report the very number this is meant
        // to fix.
        var rest = AlbumHead.DesiredSize.Height - CoverStage.DesiredSize.Height;

        var edge = AlbumPageDensity.SleeveEdge(SleeveRoom(headWidth), headRoom, rest, _rung.Cover);
        if (Math.Abs(CoverBox.Width - edge) <= 0.5)
            return;

        CoverBox.Width = edge;
        CoverBox.Height = edge;
        // The record is round because it is clipped round, not because of what is in the picture. So
        // the corner radius is set wherever the edge is written: half the edge, on the box the scan is
        // drawn in.
        CoverDiscClip.Width = edge;
        CoverDiscClip.Height = edge;
        CoverDiscClip.CornerRadius = new CornerRadius(edge / 2);
        CoverStage.Width = edge * (1 + DiscShare);

        // The pictures in the box are cut to its exact size, so a new size needs new cuts. The cuts
        // wait until the size holds still (see AlbumDetailView.StageArt).
        ScheduleStageRecut();
    }

    /// <summary>
    /// The edge in PHYSICAL pixels that the sleeve and the record behind it are cut to, when the
    /// sleeve's box has no size yet. It is the widest the page ever draws them, times the display's
    /// scale, on the ladder of decode sizes. Once the box has a size, the pictures are cut to exactly
    /// that instead, and cut again when it changes (see <see cref="StageEdge"/>).
    ///
    /// <para>The cut uses the Skia scaler, not the framework decoder. See <see
    /// cref="RemoteImage.DecodeSmoothAsync"/>, which passes it through <see
    /// cref="ArtScaler.SamplingFor(double)"/>. At the factor a 640 pixel thumbnail makes against a
    /// 305-point sleeve, that is a Mitchell cubic filter. A bitmap handed over whole and shrunk at draw
    /// time is the same picture sampled, not resized. The other large sleeves in the app, such as the
    /// wall's tiles, the list's rows and the playlist dialog's preview, are cut to the size shown. This
    /// page does the same.</para>
    ///
    /// <para>It is not measured off <see cref="CoverBox"/>, which holds the size the sleeve actually
    /// has. That number is written by the card's size pass, and the read that needs it starts on the
    /// record being opened. Asking a control what width it has been given yet is a question with an
    /// ordering in it.</para>
    /// </summary>
    private int SleeveDecodeEdge()
        => ArtScaler.DecodeEdgeFor(AlbumPageDensity.WidestCover, XamlRoot?.RasterizationScale ?? 1);

    /// <summary>
    /// Takes the record's groove off the page, or puts it back, and lays the page out again for it.
    ///
    /// <para>The groove is off for a record on a borrowed shelf that its owner has not linked to a
    /// release. The groove is reached through that link, and the reader cannot make the link. Without
    /// it the column would be empty space. The rule is stated in AlbumDetailView.ShowPressing.</para>
    ///
    /// <para>The two doors into the groove go with it. They sit in the menu under the sleeve, not in
    /// the column, so they stay when the column goes. A review written through one of them would be
    /// published with nowhere on this page to show it. The comment item also focuses a box inside the
    /// hidden panel.</para>
    /// </summary>
    private void ShowGroove(bool shown)
    {
        if (_grooveOff != !shown)
        {
            _grooveOff = !shown;

            // The rung has not changed, and the width pass returns early when the rung is unchanged.
            // Its guard also checks where the groove belongs (see ApplyWidth), so this is a real pass.
            // On a page that has not been measured yet it does nothing. A record opened before the card
            // has a size is drawn in that state.
            ApplyDensity();
        }

        // Always, and after the pass. The keys and the two doors depend on the FOLD as well as on this,
        // so a call that changed nothing here must still leave them correct. On a page with no size
        // yet, the pass above did nothing at all.
        ShowGrooveKeys();
    }

    /// <summary>
    /// Whether the groove should be folded right now. A window that has the screen does not fold it.
    /// Under that, the reader's answer applies if they have given one, and otherwise the window's share
    /// of the screen decides.
    /// </summary>
    /// <remarks>
    /// <b>THE FOLD IS A QUESTION ABOUT THE COLUMN.</b> What it saves is horizontal room. A groove
    /// beside the record takes a third of the page's width. On a card that shares a screen, that width
    /// is worth more to the tracklist. A card too narrow to give the groove a column has this settled
    /// already: the groove goes UNDER the record, where it costs no width and the page scrolls further.
    /// So the groove is never folded there, either by the page or by an answer the reader gave
    /// somewhere else. <see cref="ShowGrooveKeys"/> offers no key on the same question. In that layout
    /// the groove always appears under the album information, and no fold button is shown.
    ///
    /// <para><b>A window with <see cref="AlbumPageDensity.GrooveWindowShare"/> of the screen does not
    /// fold it either.</b> There the groove is part of the record's page, not a column the page can
    /// spare, and the fold is not offered at all. Same rule as above: the fold exists where the column
    /// is worth arguing about, and nowhere else.</para>
    ///
    /// <para>A fold the reader made in a smaller or wider window does not carry over to those layouts.
    /// It is kept, not cleared, so it still applies when the reader comes back to a card that has the
    /// column and shares the screen.</para>
    ///
    /// <para>Where the fold applies, the page keeps the same shape as the row of suggestions (see
    /// <c>SettingsService.AlbumSimilarOpen</c>). A fold the reader made by hand holds. Until the reader
    /// has made one, the page decides for itself.</para>
    ///
    /// <para>A screen that cannot be measured leaves the groove open and the fold offered. This is what
    /// happens on every platform except Windows at present. It is the right state to be in: a failed
    /// reading is no reason to take a column off the reader's page or to take away the key.</para>
    /// </remarks>
    /// <param name="hasColumn">Whether this card is wide enough to stand the groove beside the record.
    /// False is the stacked page, which has no fold at all.</param>
    private static bool GrooveShutWanted(bool hasColumn)
    {
        if (!hasColumn)
            return false;

        var share = WindowShare();

        if (share is GrooveShare.Given)
            return false;

        if (Settings.AlbumGrooveOpen is { } answered)
            return !answered;

        return share is GrooveShare.Shared;
    }

    /// <summary>
    /// What the window's share of the screen says about the groove. There are three stated answers, not
    /// a bool and a null. The third, no reading at all, is different from the other two, and every head
    /// but Windows gives it.
    /// </summary>
    private enum GrooveShare
    {
        /// <summary>The window has at least <see cref="AlbumPageDensity.GrooveWindowShare"/> of the
        /// screen. The reader has given the app the screen, so the groove is on the page and there is
        /// no key to fold it with.</summary>
        Given,

        /// <summary>The window shares the screen with something else. The fold is offered, and a page
        /// nobody has answered for starts folded.</summary>
        Shared,

        /// <summary>The screen could not be read. The fold is offered and the groove stays
        /// open.</summary>
        Unknown,
    }

    /// <summary>The share, asked in one place so that the fold and the key cannot disagree about
    /// it.</summary>
    private static GrooveShare WindowShare()
        => Services.Shell.ScreenShare.Widths() is not { } widths ? GrooveShare.Unknown
            : AlbumPageDensity.GrooveOpensItself(widths.Window, widths.Screen) ? GrooveShare.Given
            : GrooveShare.Shared;

    private readonly GrooveGlide _grooveGlide = new();

    /// <summary>
    /// The reader folds the groove away, or asks for it back. That answer holds from then on, at every
    /// width and for every record.
    /// </summary>
    /// <remarks>
    /// <b>The slide is set up here, because only here are both ends of it known.</b> The page is laid
    /// out for one of the two states at a time, so the other state's widths must be measured, not
    /// predicted. The columns are stars with minimums and caps, and working out what the Grid would do
    /// with them would mean writing a second copy of the Grid.
    ///
    /// <para>So the page is laid out both ways, in one turn of the UI thread, with no frame drawn in
    /// between. UpdateLayout measures and arranges, but does not render. The two widths of each column
    /// are kept. Then the page is put into the MOVE layout: every column at the widest it gets during
    /// the slide. The glide clips the rest back. See <see cref="GrooveGlide"/> for the geometry.</para>
    ///
    /// <para>Settling is done two ways, on purpose. The compositor's batch says when the slide is over.
    /// A window that has stopped producing frames never sends that signal, for example a minimised
    /// window or a machine that is asleep. So a timer settles the page a little after the slide was
    /// due. Whichever comes first does the work, and the other finds it already done.</para>
    /// </remarks>
    private void FoldGroove(bool shut)
    {
        // A similar-albums slide still running lands first, in the state the reader last asked for. The
        // groove's move measures this page and clips the similar block, and the two moves cannot share
        // one element. ShowSimilar, inside the pass, stops the similar slide.
        if (_similarSlide.Running)
            ApplyDensity();

        Settings.AlbumGrooveOpen = !shut;

        if (!StartGrooveSlide(shut))
        {
            // Nothing to slide: this head has no compositor for it, the page has no size yet, or the
            // groove is not on screen to move. The page goes straight to the settled state, with no
            // slide at all.
            SettleGroove();
        }
    }

    /// <summary>Where an element stands in the page's own grid. A panel that a fold MOVES is measured
    /// this way, because its width is the same at both ends.</summary>
    private double LeftOf(FrameworkElement element)
        => element.TransformToVisual(PageGrid)
            .TransformPoint(new Windows.Foundation.Point(0, 0)).X;

    /// <summary>The page as it stands once the fold is over. It is the one pass everything else goes
    /// through. It is safe to run twice: <see cref="ApplyWidth"/> returns early on a page already in
    /// that state.</summary>
    private void SettleGroove()
    {
        _grooveGlide.Stop();
        DropOverhangs();
        ApplyDensity();
    }

    /// <summary>
    /// Takes off the negative right margins the move lays the page out with, and nothing else.
    /// </summary>
    /// <remarks>
    /// <b>Only the right edge.</b> These three panels also carry margins that are not the move's. The
    /// papers' left margin is the join's gap, on every layout where the grid does not spend that gap as
    /// column spacing. The row of suggestions carries the page's own air on its top (see ApplyWidth and
    /// ApplyStacking). Clearing the whole Thickness would remove those.
    ///
    /// <para>It must run before <see cref="StartGrooveSlide"/> measures. Otherwise the folded page is
    /// measured with its gap already gone, and the glide would carry the papers to the wrong
    /// place.</para>
    /// </remarks>
    private void DropOverhangs()
    {
        AlbumColumn.Margin = new Thickness(AlbumColumn.Margin.Left, 0, 0, 0);
        PapersColumn.Margin = new Thickness(PapersColumn.Margin.Left, 0, 0, 0);
        SimilarBlock.Margin = new Thickness(
            SimilarBlock.Margin.Left, SimilarBlock.Margin.Top, 0, SimilarBlock.Margin.Bottom);

    }

    /// <summary>
    /// Puts a width on the papers panel while the groove slides, once a frame.
    /// </summary>
    /// <remarks>
    /// <b>A margin, not a Width.</b> The panel has to be wider than the cell it is in. The move is laid
    /// out with the columns where the OPEN page has them, and a negative right margin is how everything
    /// else in this move overhangs its cell. A margin is also the only one of the two that re-measures
    /// what is inside. The Grid gives the child the cell's width less its margins, so a margin of minus
    /// eighty gives the panel eighty more points to lay its paragraphs out in, and they re-wrap. An
    /// explicit Width does neither. The panel would stay at its open width for the whole slide, while
    /// the text inside it was re-wrapped for the wider width and cut off by the cards it is in.
    ///
    /// <para>Cleared by <see cref="DropOverhangs"/>, which removes the right edge from all three things
    /// this move overhangs.</para>
    /// </remarks>
    private void WidenPapers(double width)
        => PapersColumn.Margin = new Thickness(
            PapersColumn.Margin.Left, 0, -Math.Max(0, width - _papersOpen), 0);

    /// <summary>How wide the papers panel is with the groove beside it. That is the width of the cell
    /// the move lays it out in. <see cref="WidenPapers"/> measures its overhang from this.</summary>
    private double _papersOpen;

    /// <summary>
    /// Measures both ends of the fold, puts the page in the layout the slide runs over, and starts it.
    /// </summary>
    /// <returns>False where there is nothing to slide, which leaves the caller to settle at
    /// once.</returns>
    private bool StartGrooveSlide(bool shut)
    {
        // Nothing is measured yet, there is no groove on this record, or the groove is not a column on
        // this card. A stacked groove folds under the record, not beside it, so there is no room for it
        // to slide out of.
        //
        // THE RUNG, not GrooveHasColumn. That property also depends on the groove's state right now,
        // and right now is the state being left. On the way back it is false, because the groove IS
        // shut. The guard then turned every unfold away, and the column arrived in one frame with no
        // slide. That is the direction a reader watches, because they wait for it. The question here is
        // the card's alone, and the rung is where that is written.
        if (ActualWidth <= 0 || _grooveOff || !_rung.GrooveHasColumn)
            return false;

        var setup = System.Diagnostics.Stopwatch.StartNew();

        _grooveGlide.Stop();
        DropOverhangs();

        // The state at this end: how wide each thing a fold makes wider is right now, and WHERE the
        // papers stand. The papers are the one panel a fold moves rather than widens. The record's
        // column takes the freed room and pushes them along in front of it (see CapPanels).
        UpdateLayout();
        var albumHere = AlbumColumn.ActualWidth;
        var papersHere = PapersColumn.ActualWidth;
        var papersAtHere = LeftOf(PapersColumn);
        var similarHere = SimilarBlock.ActualWidth;
        var grooveHere = GrooveColumn.ActualWidth;
        var tracksHere = TrackScroller.ActualHeight;

        // The other end, found by laying the page out there and reading it. Nothing is drawn between
        // the two: UpdateLayout measures and arranges, and both happen inside this one call.
        _grooveShut = shut;
        LayOut(ActualWidth, ActualHeight);
        UpdateLayout();
        var albumThere = AlbumColumn.ActualWidth;
        var papersThere = PapersColumn.ActualWidth;
        var papersAtThere = LeftOf(PapersColumn);
        var similarThere = SimilarBlock.ActualWidth;
        var grooveThere = GrooveColumn.ActualWidth;
        var tracksThere = TrackScroller.ActualHeight;

        var wide = Math.Max(grooveHere, grooveThere);
        if (wide <= 1)
        {
            _grooveShut = shut;
            return false;
        }

        // The move layout: the columns in their OPEN arrangement, whichever way the fold is going, so
        // the groove still has a cell to slide out of. The three things that change width are laid out
        // at the WIDER of their two widths, overhanging their cells by the difference. The glide cuts
        // the overhang off and then uncovers it.
        //
        // So the page is measured and wrapped ONCE, here. Nothing on it is laid out again until the
        // slide is over. The re-wrap that costs is done at the START of the move, under a clip that is
        // already sliding. A page that kept its old shape and re-wrapped at the END would do it after
        // everything had stopped, when the reader is looking at the page.
        _grooveShut = false;
        LayOut(ActualWidth, ActualHeight);

        var albumGrow = Math.Abs(albumThere - albumHere);
        var similarGrow = Math.Abs(similarThere - similarHere);

        // The papers are the one panel that is really resized rather than uncovered, so the glide gets
        // its two widths, not the difference. The panel is left-aligned and is given a width once a
        // frame. It overhangs its own cell into the groove's cell by a margin, as the other panels do.
        // The grid's columns do not move, so nothing else on the page is laid out again.
        var papersOpen = shut ? papersHere : papersThere;
        var papersFolded = shut ? papersThere : papersHere;
        _papersOpen = papersOpen;

        // Signed, and always how far right the papers stand with the groove FOLDED. On the way out that
        // is less than here. On the way back it is here less than there. The move layout puts their
        // cell where the OPEN page has it. Without this value they keep that place for the whole slide,
        // and arrive at the folded place in one frame. For example, the papers held 656 for 220 ms and
        // then jumped to 1046.
        var papersTravel = shut ? papersAtThere - papersAtHere : papersAtHere - papersAtThere;

        // Only the RIGHT of each is the move's own. The row of suggestions carries the page's air on
        // its top (see ApplyStacking), so the top is left as it is.
        AlbumColumn.Margin = new Thickness(0, 0, -albumGrow, 0);
        SimilarBlock.Margin = new Thickness(
            SimilarBlock.Margin.Left, SimilarBlock.Margin.Top, -similarGrow, SimilarBlock.Margin.Bottom);
        UpdateLayout();
        // The move layout has to match the settled page in every respect except the groove's width. The
        // record's column is held to one width across a fold (see CapPanels), so its tracklist is the
        // same height at both ends and in between. Any difference means a pass is missing from the
        // move.
        var tracksMoving = TrackScroller.ActualHeight;
        if (Math.Abs(tracksMoving - tracksHere) > 1 || Math.Abs(tracksThere - tracksHere) > 1)
        {
            RPM.Core.Diagnostics.Scope.Said(
                "panes",
                $"groove tracklist is {tracksHere:F0} on the page, {tracksThere:F0} at the other end " +
                $"and {tracksMoving:F0} while it moves, WHICH SHOULD BE ONE NUMBER");
        }

        var started = _grooveGlide.Start(
            new GrooveMove(
                GrooveColumn, wide,
                GrooveEdge, GrooveFoldJoin,
                AlbumColumn, albumGrow,
                PapersColumn, papersOpen, papersFolded, WidenPapers, papersTravel,
                // Half the column's growth, because the head is CENTRED in it. The sleeve, the name,
                // the stars and the buttons are the one thing in the album panel that a wider column
                // moves rather than merely uncovers, and the sleeve is not re-cut for it (see
                // SleeveRoom). It moves over the same p as the rest, so it cannot arrive early.
                AlbumHead, albumGrow / 2,
                SimilarBlock, similarGrow,
                Folding: shut),
            SettleGroove);

        var ends = new GrooveEnds(
            shut ? albumHere : albumThere, shut ? albumThere : albumHere,
            papersOpen, papersFolded, papersTravel);

        if (!started)
        {
            _grooveShut = shut;
            SayFold(shut, ends, setup.Elapsed.TotalMilliseconds, slid: false);
            return false;
        }

        SayFold(shut, ends, setup.Elapsed.TotalMilliseconds, slid: true);

        // The deadline, for a window that stops producing frames before the batch completes. A little
        // past the slide, so it never lands on top of a slide that is going to finish on its own.
        _grooveSettle ??= new DispatcherTimer();
        _grooveSettle.Stop();
        _grooveSettle.Interval = TimeSpan.FromMilliseconds(GrooveGlide.Ms + 120);
        _grooveSettle.Tick -= GrooveSettle_Tick;
        _grooveSettle.Tick += GrooveSettle_Tick;
        _grooveSettle.Start();
        return true;
    }

    private DispatcherTimer? _grooveSettle;

    /// <summary>What the two panels measure at each end of the fold, open state first. The papers' pair
    /// is the one to watch. They should be the same number, because a fold moves that panel and does
    /// not widen it. A difference of even twenty points means a paragraph re-breaks in the frame the
    /// key goes down.</summary>
    private readonly record struct GrooveEnds(
        double AlbumOpen, double AlbumFolded,
        double PapersOpen, double PapersFolded,
        double PapersTravel);

    /// <summary>
    /// The cost of the press on the drawing thread, before the compositor takes over, and the
    /// measurements at each end of the fold.
    /// </summary>
    /// <remarks>
    /// The slide itself is not made of frames this thread delivers, so there is no per-frame reading to
    /// take. See MainPage.Panes.SayLanding, which does the same for its own compositor move. What CAN
    /// be measured is the setup. The page is laid out three times, once at each end of the fold and
    /// then in the move layout, all in the one turn between the press and the first frame of the slide.
    /// On this page a layout pass covers a tracklist, two encyclopedia panels and a row of suggestions.
    /// If the setup is too long, the slide is smooth but the press is not, which looks like one jerky
    /// move.
    ///
    /// <para>A slide that never started is always reported, because a fold that arrives in one frame is
    /// the fault this check exists to catch. See the guard in <see cref="StartGrooveSlide"/>, which
    /// must not turn every unfold away.</para>
    ///
    /// <para>Read with <c>rpmscope log --channel panes</c>.</para>
    /// </remarks>
    private static void SayFold(bool shut, GrooveEnds ends, double setupMs, bool slid)
    {
        RPM.Core.Diagnostics.Scope.Said(
            "panes",
            $"groove {(shut ? "fold" : "unfold")}: " +
            $"album {ends.AlbumOpen:F0} to {ends.AlbumFolded:F0}, " +
            $"papers {ends.PapersOpen:F0} to {ends.PapersFolded:F0} " +
            $"travelling {ends.PapersTravel:F0}, " +
            $"{setupMs:F0}ms of layout before the slide" +
            (slid ? "" : ", and NO SLIDE") +
            (slid && setupMs > SetupBarMs ? " (OVER THE BAR)" : ""));
    }

    /// <summary>How long the page may spend laying itself out between the press and the first frame of
    /// the slide before it is reported. About one frame at sixty frames a second, and a little more.
    /// Past this the reader has pressed a key and seen nothing move for a frame.</summary>
    private const double SetupBarMs = 20;

    private void GrooveSettle_Tick(object? sender, object e)
    {
        _grooveSettle?.Stop();
        if (_grooveGlide.Running)
            SettleGroove();
    }

    private void GrooveFold_Click(object sender, RoutedEventArgs e) => FoldGroove(shut: true);

    private void GrooveReopen_Click(object sender, RoutedEventArgs e) => FoldGroove(shut: false);

    /// <summary>
    /// The two keys, and the two doors into the groove that go with them.
    /// </summary>
    /// <remarks>
    /// The key that folds stands on the join between the record and the groove. The key that brings it
    /// back stands at the page's right edge, because the column the first key sits beside has no width
    /// once it has been folded. Only one of the two is shown at a time.
    ///
    /// <para>Neither is offered on a record that has no groove at all, such as a borrowed record its
    /// owner has not linked. A key that opens nothing is worse than no key.</para>
    ///
    /// <para><b>Neither is offered on a card with no column for the groove.</b> On such a card the
    /// groove stands under the record and cannot be folded. See <see cref="GrooveShutWanted"/>, which
    /// is the same rule seen from the other side. For this reason the fold key is not shown in the
    /// groove's own heading. The stacked page cannot offer a fold.</para>
    ///
    /// <para>The menu items "write a review" and "write a comment" are hidden while the groove is
    /// folded, for the same reason they are hidden when it is missing. A review written through them
    /// would be published with nowhere on this page to appear, and the comment item focuses a box
    /// inside a panel that is not shown. One press on the edge key brings all of them back.</para>
    /// </remarks>
    private void ShowGrooveKeys()
    {
        var folded = _grooveShut && !_grooveOff;
        var open = !_grooveShut && !_grooveOff;

        // The fold is offered only where the groove has a column to give up. Two cases have no column
        // to argue about, so there is no key: a card too narrow to stand the groove beside the record,
        // where it stands under it, and a window that has the screen, where the groove is part of the
        // record's page. Both are the same rule that keeps the groove open in the first place (see
        // GrooveShutWanted).
        var foldable = open && _rung.GrooveHasColumn && WindowShare() is not GrooveShare.Given;

        // The key stands on the join, which is the only place there is one. A foldable groove has a
        // column (see above), and a column has a join.
        GrooveFoldJoin.Visibility = foldable ? Visibility.Visible : Visibility.Collapsed;
        GrooveReopen.Visibility = folded ? Visibility.Visible : Visibility.Collapsed;

        // The line is cut behind the key only while the key is shown.
        CutJoinGap();

        WriteReviewItem.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        WriteCommentItem.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Moves the groove between its own column, the strip beside the tracks, and off the page.
    ///
    /// <para>It is the same object in all three places. The panels, the sort key, the pager and
    /// everything already loaded travel with it. A groove rebuilt on resize would put a reader who was
    /// three pages into the reviews back on the first page, just for widening the window.</para>
    ///
    /// <para><b>Off the page is a collapse, never a removal.</b> In Uno, an <c>x:Name</c> is resolved
    /// against the live tree. A panel removed from one parent and not added to another makes every
    /// named child inside it read as null, and the next line that touches one throws. So the groove
    /// keeps its column, with the column folded to nothing and the panel collapsed. It stays in the
    /// column, not in the stack, for a second reason: a collapsed panel left in the stack is still a
    /// child of it, and the stack spends its own top margin on having children.</para>
    /// </summary>
    private void PlaceGroove(GroovePlace place)
    {
        if (place == _groovePlace)
            return;

        // Written before anything moves. A layout pass started from inside the move then finds the page
        // already saying where it is going.
        var was = _groovePlace;
        _groovePlace = place;

        // The host depends on where the groove is: the stack when it stands under the record, and the
        // page's own grid in the other two places. The remove and the add happen back to back, with
        // nothing between them that can start a layout pass over an unparented panel.
        if (place is GroovePlace.Stacked && was is not GroovePlace.Stacked)
        {
            PageGrid.Children.Remove(GrooveColumn);
            StackedPanels.Children.Add(GrooveColumn);
        }
        else if (place is not GroovePlace.Stacked && was is GroovePlace.Stacked)
        {
            StackedPanels.Children.Remove(GrooveColumn);
            Grid.SetColumn(GrooveColumn, 2);
            Grid.SetRow(GrooveColumn, 0);
            Grid.SetRowSpan(GrooveColumn, 3);
            PageGrid.Children.Add(GrooveColumn);
        }

        // It keeps its own name and its own tabs wherever it stands. Under the tracklist, the name says
        // where the reader is. In a column, the heading above the page already says it. An unnamed
        // panel of reviews with nothing but a gap above it reads as more of the record's page.
        GrooveHeadingBox.Visibility = Visibility.Visible;
        GrooveTabHost.Visibility = Visibility.Visible;

        // The divider is only ever drawn where there is a column beside the record to divide from.
        GrooveEdge.Visibility = place is GroovePlace.Column ? Visibility.Visible : Visibility.Collapsed;

        GrooveColumn.Visibility = place is GroovePlace.Off ? Visibility.Collapsed : Visibility.Visible;
        TracksPane.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Moves the papers between their column and the stack under the record.
    ///
    /// <para>It is the same object either way, for the same reason as the groove. What is loaded into
    /// it travels with it. A panel rebuilt on resize would put a reader back at the top of a biography
    /// they had scrolled through.</para>
    /// </summary>
    private void PlacePapers(bool stacked)
    {
        if (stacked == _papersStacked)
            return;
        _papersStacked = stacked;

        if (stacked)
        {
            PageGrid.Children.Remove(PapersColumn);

            // FIRST in the stack, always. It is the column that stood nearer the record, and the order
            // things fell out of the row is the order they should read in, going down. Inserted, not
            // added, because the groove may already be in the stack: it loses its column on a wider
            // card than the papers do, so by the time the papers arrive the stack is not empty.
            StackedPanels.Children.Insert(0, PapersColumn);
        }
        else
        {
            StackedPanels.Children.Remove(PapersColumn);
            Grid.SetColumn(PapersColumn, 1);
            Grid.SetRow(PapersColumn, 0);
            PageGrid.Children.Add(PapersColumn);
        }

        PapersColumn.Visibility = Visibility.Visible;
    }

    /// <summary>
    /// Whether the page scrolls, and everything that follows from it.
    ///
    /// <para>It scrolls exactly when something stands underneath the columns rather than beside them.
    /// Then it scrolls for EVERYTHING. The tracklist gives up its own scroller and draws its whole
    /// length, the head of the album column gives up its cap, and one bar down the side of the page
    /// covers all of it. A pane that scrolls inside a page that scrolls would give two bars for one
    /// gesture, and the wheel would go to whichever bar the pointer is over.</para>
    ///
    /// <para>It is written on every layout pass, not only when the page crosses from one state to the
    /// other. The cost is small: six properties, and on most passes they already hold the values they
    /// need. The alternative is a flag for the transition, which goes wrong the first time the page is
    /// laid out in a state nobody moved into.</para>
    /// </summary>
    private void ApplyStacking()
    {
        var stacked = GrooveStacked || _papersStacked;

        PageScroller.VerticalScrollMode = stacked ? ScrollMode.Enabled : ScrollMode.Disabled;
        PageScroller.VerticalScrollBarVisibility =
            stacked ? ScrollBarVisibility.Auto : ScrollBarVisibility.Disabled;

        // Both rows are set. If the second row is left as a star, the columns keep claiming the full
        // height of the viewport, and the stack underneath starts one screen down. The page scrolls,
        // but a reader sees a record with a hole under it.
        //
        // The gap between the row of columns and whatever stands under it, down to the foot. It is set
        // here, after ApplyWidth, and not with the rung's RowSpacing, because it answers the same
        // question as this method. Panels that sit beside each other want a column's gap. Panels that
        // sit under each other want a break. The grid's RowSpacing is spent between three rows, and the
        // middle one is usually empty. That gave two gaps under a page with one thing under its
        // columns, and two gaps under a page with nothing under them at all. That air is the space at
        // the foot of a record with no suggestions, and it kept the suggestions off a card that could
        // have held them.
        //
        // So each row carries its own air as a top margin, and a row with nothing in it carries none.
        // The suggestions are Collapsed when they are not drawn, which spends nothing, margin included.
        // The stack has to be ASKED whether anything has been moved into it, because an empty panel is
        // still a panel, and its margin would still be spent.
        var rowAir = stacked ? StackAir : _rung.Gap;

        PageGrid.RowSpacing = 0;
        StackedPanels.Spacing = StackAir;
        StackedPanels.Margin = new Thickness(
            0, StackedPanels.Children.Count > 0 ? rowAir : 0, 0, 0);
        SimilarBlock.Margin = new Thickness(0, rowAir, 0, 0);

        ColumnsRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);
        TracksRow.Height = stacked ? GridLength.Auto : new GridLength(1, GridUnitType.Star);

        // EVERY pane that scrolls, not only the tracklist. This is the same mistake as the first one,
        // one level down. A panel standing in the stack is measured with no height to fill. A vertical
        // StackPanel offers its children infinite height, and a ScrollViewer asked how tall it would
        // like to be, with unlimited room, answers with its viewport, which is nothing. So both panels
        // were in the tree and both had the right width, and both drew nothing at all.
        //
        // So a pane gives up its own scrolling while the page is scrolling. This is the same rule as
        // "one bar for one gesture" above. The panes that keep a bar are the panes that must collapse.
        foreach (var pane in Panes)
        {
            pane.VerticalScrollMode = stacked ? ScrollMode.Disabled : ScrollMode.Enabled;
            pane.VerticalScrollBarVisibility =
                stacked ? ScrollBarVisibility.Disabled : ScrollBarVisibility.Auto;
        }

        // The lanes are the room kept clear for the scroll bars, so they go with the bars.
        //
        // Every pane that keeps a bar also keeps a lane clear on its right, so the bar is not on top of
        // the writing. See AlbumPageDensity.ScrollLane, which is the bar's own width plus the air that
        // keeps it off the words. When stacked, none of the panes has a bar. The page's own bar is
        // outside all of them. The lane would then be empty space on one side of a panel that has the
        // page margin on the other side. It reads as a page that has shifted left. On the head of the
        // album column, whose sleeve and title are CENTRED in the room they are given, it reads as a
        // title off centre by half the lane.
        //
        // The rung's own arithmetic still counts this lane where it applies (see AlbumPageRung.JoinAir,
        // which subtracts it from the join). That only happens with three columns showing, where the
        // lane is paid for and the bar is there to use it.
        //
        // The list below matches Panes, but the change is made in margins rather than scroll modes. A
        // pane that keeps a bar and no lane draws the bar over its own cards, which is what the
        // groove's two panes did. The suggestions row's own bar is not in this list. It runs along the
        // row's foot and stays in every state, because the row scrolls sideways whatever the page does
        // (see Frame). The lane still applies to the row: it is the line the cards above it end on, so
        // the row is placed against it below.
        var lane = stacked ? 0 : ScrollLane;

        // A bar in a lane at the very edge of its column leans on the writing, with all the air on its
        // other side. So each scroller hangs past its own column far enough to put its bar in the
        // MIDDLE of the air beside it. The writing inside gives the same amount back, so the bar moves
        // and nothing else does (see AlbumPageDensity.BarLift). What each one stands beside is
        // different. The papers have the groove's divider a couple of points past their column. The
        // songs have the first encyclopedia card a column gap away. Both are zero when stacked, because
        // there is no bar to place and nothing beside it.
        var trackLift = stacked ? 0 : AlbumPageDensity.BarLift(_rung.Gap);

        // The suggestions line up with the columns above them, at both ends. The left edge is the album
        // column's own edge. The right edge is the line the cards above end on: the lane short of the
        // column where there is a lane, and the column itself where there is none. What is written is
        // the CARD's padding, given back on the left and taken off the lane on the right. The plate
        // behind a suggestion is eight points wider than its picture at each end, and the pictures are
        // the part that must line up with the writing above them.
        //
        // The row's own bar runs along its FOOT, and neither number affects it (see Frame). It scrolls
        // sideways in every state, so it is the one pane here that keeps its lane when the page starts
        // scrolling.
        var similarPad = Models.SimilarTileMetrics.Padding;
        SimilarScroller.Margin = new Thickness(-similarPad, 0, lane - similarPad, 0);

        AlbumHead.Margin = new Thickness(0, 0, lane, 0);

        // The lift is set on the CUT, not on the scroller inside it, and this is required. TrackCut
        // carries the tracklist's inset clip (see ShowTrackHems), and an InsetClip clips to the
        // element's own box. A scroller hung past its cut would have everything past that box clipped,
        // including the bar. The lift is six to twelve points, and the bar's box is twelve wide. On
        // every rung the bar would be almost or completely invisible: gone at the widest, and a
        // two-point sliver at the narrowest. Moved out to the cut, the clip covers the box the scroller
        // actually occupies, and the bar stands in the air the lift makes.
        TrackCut.Margin = new Thickness(0, 0, -trackLift, 0);
        TrackScroller.Padding = new Thickness(0, 0, lane + trackLift, 0);

        // The groove's two panes had no lane and drew their bars over their own cards. They follow the
        // same rule as the rest, against their own neighbour. The edge to the right of the groove is
        // the card's edge, one page margin away. Every edge of the page uses the page's margin, 28
        // points.
        var grooveLift = stacked ? 0 : AlbumPageDensity.BarLift(_rung.Pad);

        ReviewScroller.Margin = new Thickness(0, 0, -grooveLift, 0);
        ReviewScroller.Padding = new Thickness(0, 0, lane + grooveLift, 0);
        CommentScroller.Margin = new Thickness(0, 0, -grooveLift, 0);
        CommentScroller.Padding = new Thickness(0, 0, lane + grooveLift, 0);

        // The papers' two insets follow the same rule, so they are set in one place. The top inset is
        // not about stacking at all.
        FitPapersColumn();
    }

    /// <summary>
    /// The papers column's insets: the bar's lane down its right side, and the leading above its first
    /// heading, taken off its top.
    ///
    /// <para>The lane is the rule the rest of the page's panes follow (see <see cref="ApplyStacking"/>,
    /// which calls this). A pane that keeps a bar keeps room for it, and hangs past its own column far
    /// enough to put the bar in the middle of the air beside it.</para>
    ///
    /// <para>The top inset is its own rule, which is why this is a method. The lift is the leading
    /// above a HEADING: the few points of empty space a 12-point line has inside its own box. It is
    /// taken off so that the column's first heading INKS on the same line as the sleeve's top edge. So
    /// it is right only while the column's first block is a heading. While the catalogues are still
    /// loading, every heading-led block is hidden, and the first thing in the column is the pressing
    /// well. That is a bordered card, whose top edge is its ink, not its leading. Taking three points
    /// off it would cut three points of the card's own border off the top, through the scroller's clip.
    /// A reader would see a card hanging off the top of the page while the releases load.</para>
    ///
    /// <para>So the lift is ASKED for, not assumed. It comes from a fact that holds whenever it is
    /// asked: is any of <see cref="HeadingBlocks"/> showing. When stacked, the lift is zero. The papers
    /// then stand under the record with the stack's own air above them, and nothing across from them
    /// needs to line up.</para>
    /// </summary>
    private void FitPapersColumn()
    {
        var stacked = GrooveStacked || _papersStacked;
        var lane = stacked ? 0 : ScrollLane;
        var lift = stacked ? 0 : AlbumPageDensity.BarLift(_joinEdge);
        var head = stacked || !LeadsWithHeading ? 0 : -HeadingLead;

        PapersScroller.Margin = new Thickness(0, 0, -lift, 0);
        PapersStack.Margin = new Thickness(0, head, lane + lift, 0);
    }

    /// <summary>
    /// Every block in the papers column that leads with a section heading, in the order they appear in
    /// the column. They are the first four blocks. When one of them is showing, the column starts with
    /// a heading. When none is showing, the column starts with a card.
    ///
    /// <para>Listed once, because this list is the one place that defines them. A fifth heading-led
    /// block added to the column but left off this list would sit three points low at the top of the
    /// column, and nobody would notice or find the cause.</para>
    /// </summary>
    private FrameworkElement[] HeadingBlocks =>
        _headingBlocks ??= [AlbumPapersBlock, ArtistPapersBlock, LinksBlock, TopReviewBlock];

    private FrameworkElement[]? _headingBlocks;

    private bool LeadsWithHeading =>
        Array.Exists(HeadingBlocks, block => block.Visibility == Visibility.Visible);

    /// <summary>
    /// Watches the heading-led blocks as they show and hide, and fits the column's top again each time.
    ///
    /// <para>The blocks are watched, not told. They are shown from three sources that do not know about
    /// each other: the cache check on the frame the page opens, the catalogues' answer some seconds
    /// later, and the groove's own load for the top review. A fourth source could be added later. Any
    /// of them changes which block is first in the column, so each one would have to remember to say
    /// so. This code asks the blocks instead.</para>
    ///
    /// <para>Called once, from the constructor. The blocks are part of the page's markup and outlive
    /// every record opened in it.</para>
    /// </summary>
    private void WatchPapersHead()
    {
        foreach (var block in HeadingBlocks)
            block.RegisterPropertyChangedCallback(VisibilityProperty, (_, _) => FitPapersColumn());
    }

    /// <summary>
    /// Every pane on this page that scrolls inside itself while the page does not.
    ///
    /// <para>The list is kept in one place because the rule is about the KIND of thing, not about any
    /// one pane. Anything with its own scroll bar has to give it up when the page starts scrolling.
    /// Otherwise it is a second bar fighting for the wheel where it is a column, and an empty pane
    /// where it is stacked. A pane added to this page but left off this list will draw blank on a
    /// narrow card.</para>
    /// </summary>
    private ScrollViewer[] Panes =>
        _panes ??= [TrackScroller, FactsScroller, PapersScroller, ReviewScroller, CommentScroller];

    private ScrollViewer[]? _panes;

    /// <summary>
    /// Opens the groove on one of its tabs, wherever the groove is at the moment. The page's own
    /// actions, write a review and write a comment, go through here. They do not use the groove's own
    /// tab strip, which the reader may have to scroll to reach.
    ///
    /// <para>The groove carries its own tab strip wherever it stands, so there is one thing to
    /// ask.</para>
    /// </summary>
    private void OpenGrooveTab(string tab) => _grooveStrip?.Select(tab);

    // ── what a card with no height to spare gives up ──

    private void ApplyHeight(double height)
    {
        // A page that scrolls has no height to give up. The whole ladder below exists because the page
        // must fit one screen at once. Once something stands under the columns, the page has its own
        // length. On a card too short for the suggestions, the reader scrolls down to reach them. So
        // the ladder is not consulted at all. An answer that is worked out and then thrown away would
        // look live to the next reader, so it is not computed.
        if (GrooveStacked || _papersStacked)
        {
            _topReviewAllowed = true;
            ShowSimilar();
            ShowTopReview();

            // Its scrolling is already off (see Panes); the cap has to go with it, or the head keeps a
            // ceiling it can no longer scroll inside and the sleeve is cut off at it.
            _factsCap = double.PositiveInfinity;
            FitAlbumColumn();
            return;
        }

        // The air above and below comes from the rung, which the width pass sets before this method
        // runs. A short card does not give it up.
        ShowSimilar();
        _topReviewAllowed = AlbumPageDensity.ShowsTopReview(height, _rung, _similarRow);
        ShowTopReview();

        // The ladder's ceiling, which is an estimate of a row this method cannot see yet. The row's own
        // answer is taken in FitAlbumColumn, and the lower of the two is what the head gets.
        _factsCap = AlbumPageDensity.FactsCap(height, _rung, _similarRow);
        FitAlbumColumn();
    }

    /// <summary>
    /// Which of its three states the suggestions row is in on a card this tall.
    ///
    /// <para>Asked as a fact rather than kept as one: the record either has suggestions or it does not,
    /// the reader has either said which way they want the row or they have not, and the card either
    /// holds it open or it does not. Nothing here remembers a previous answer, so a window dragged
    /// shorter and back finds the same page it left.</para>
    /// </summary>
    private SimilarState SimilarRowState()
    {
        if (!_hasSimilar)
            return SimilarState.Gone;

        // The reader's own answer, where they have given one. Opening the row on a short card is a
        // choice to spend the sleeve's room on it, and the page keeps that choice.
        if (Settings.AlbumSimilarOpen is { } wanted)
            return wanted ? SimilarState.Open : SimilarState.Shut;

        // Nobody has said. A page that carries its own scrolling has room for anything; otherwise it is
        // open on a card tall enough to stand it open and shut on one that is not.
        return GrooveStacked || _papersStacked || AlbumPageDensity.HoldsOpenSimilar(ActualHeight, _rung)
            ? SimilarState.Open
            : SimilarState.Shut;
    }

    // ── the release card ──

    /// <summary>
    /// Which shape the release card is standing in.
    ///
    /// <para>Three, not two. The first two are where a key GOES. Neither is the right answer on a
    /// record with no key at all (see <see cref="LinkOffer"/>, which removes the key on a borrowed
    /// shelf). Asked as one question, so the card cannot be put in the shape for a key it does not
    /// show.</para>
    /// </summary>
    private enum PressingShape
    {
        /// <summary>Not measured yet. Every other value is a real answer, so there is no false one to
        /// start from.</summary>
        Unmeasured,

        /// <summary>The key stands at the right of the facts, on their own line.</summary>
        KeyBeside,

        /// <summary>The card is too narrow for that, and the key is centred underneath.</summary>
        KeyUnder,

        /// <summary>There is no key. The facts take the whole card, and the row a key would have stood
        /// in is not reserved.</summary>
        NoKey,
    }

    private PressingShape _pressingShape;

    /// <summary>The air between the facts and the key when the key stands under them. The card's own
    /// padding on either side is set in the markup (PressingWell). This air is not, for the reason
    /// given in FitPressingWell: air declared in the markup is spent in both shapes, and this air
    /// exists in only one of them.</summary>
    private const double PressingKeyAir = 9;

    private void PressingWell_SizeChanged(object sender, SizeChangedEventArgs e)
        => FitPressingWell(e.NewSize.Width);

    /// <summary>
    /// The release card, in the shape its own width can hold: the facts with the key beside them, the
    /// facts with the key centred underneath, or, on a record whose key is not offered, the facts
    /// alone.
    ///
    /// <para>Measured off the card, not off the rung. The papers column is a share of the page, and its
    /// width changes with every pixel of a drag. A rung steps at five widths, so it is the wrong thing
    /// to ask. The key's own width comes from the button's text, which is translated, so this file
    /// cannot hold it as a number.</para>
    ///
    /// <para>Called on the card's own resize, and again whenever the key appears or disappears (see
    /// AlbumDetailView.ShowPressing). Taking a button away does not change the width of the card it was
    /// in, so a resize is not enough to hear about it.</para>
    /// </summary>
    private void FitPressingWell(double width)
    {
        if (width <= 0)
            return;

        var inner = width - PressingWell.Padding.Left - PressingWell.Padding.Right
            - PressingWell.BorderThickness.Left - PressingWell.BorderThickness.Right;

        var shape = LinkPressingButton.Visibility != Visibility.Visible
            ? PressingShape.NoKey
            : AlbumPageDensity.PressingKeyBeside(
                inner, LinkPressingButton.DesiredSize.Width, PressingGrid.ColumnSpacing)
                ? PressingShape.KeyBeside
                : PressingShape.KeyUnder;

        if (shape == _pressingShape)
            return;
        _pressingShape = shape;

        // The facts keep a column to themselves only where something stands in the other one.
        Grid.SetColumnSpan(PressingFacts, shape is PressingShape.KeyBeside ? 1 : 2);

        // The air over the key, and only where there IS a key under the facts. A Grid spends its
        // RowSpacing between the rows it has declared, not the rows it has filled. With the key beside
        // the facts, the second row is empty. Its air is spent on nothing, and it lands as extra
        // padding along the bottom edge. Otherwise the card is nine points deep at the top and eighteen
        // at the foot. The value is set here, not in the markup, because it belongs to one of the three
        // shapes, and this method is where the shape is decided.
        PressingGrid.RowSpacing = shape is PressingShape.KeyUnder ? PressingKeyAir : 0;

        var under = shape is PressingShape.KeyUnder;
        Grid.SetRow(LinkPressingButton, under ? 1 : 0);
        Grid.SetColumn(LinkPressingButton, under ? 0 : 1);
        Grid.SetColumnSpan(LinkPressingButton, under ? 2 : 1);
        LinkPressingButton.HorizontalAlignment =
            under ? HorizontalAlignment.Center : HorizontalAlignment.Right;
    }

    /// <summary>Whether this record HAS suggestions, which is the loader's answer; whether there is
    /// room for them is this file's. The row is drawn when both say so.</summary>
    private bool _hasSimilar;

    /// <summary>
    /// The row of suggestions: this record has some, and the card is tall enough to show them.
    ///
    /// <para>The row is asked about height only. Nothing shares a column now: a panel with no column of
    /// its own stands underneath at full width. On a page that scrolls, the height always says
    /// yes.</para>
    /// </summary>
    private void ShowSimilar()
    {
        // Every layout pass lands a slide in flight. The pass is about to lay the row out in the state
        // the reader last asked for, and that is the state the slide was heading to, so stopping it
        // here only cuts the motion short. See SimilarToggle_Click for why the layout and the asked-for
        // state differ while a slide runs.
        _similarSlide.Stop();

        _similarRow = SimilarRowState();

        SimilarBlock.Visibility = _similarRow == SimilarState.Gone
            ? Visibility.Collapsed
            : Visibility.Visible;

        var open = _similarRow == SimilarState.Open;

        SimilarScroller.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
        PointSimilarChevron(open);
    }

    /// <summary>Down while the row is shut and up while it is open, which is what an expander points
    /// everywhere else: the chevron points at where the row will go when it is pressed.</summary>
    private void PointSimilarChevron(bool open)
        => SimilarChevron.Glyph = SeriesChevron.Glyph = open ? "\uf077" : "\uf078";

    private readonly SimilarSlide _similarSlide = new();

    /// <summary>
    /// The reader opening or shutting the row. What is written down is what they asked for, and the row
    /// slides up into the room it takes or down out of the room it gives back.
    /// </summary>
    /// <remarks>
    /// <b>Two states, which differ while a slide runs.</b> <see cref="SimilarRowState"/> is what the
    /// reader has ASKED for, and it changes the moment the key is pressed. <c>_similarRow</c> is what
    /// the page is LAID OUT for, and only <see cref="ShowSimilar"/> writes it, inside a layout pass.
    /// The page is laid out open for the whole of a slide in both directions: an open lays the page out
    /// first and then slides the row up into it, and a close slides the row down first and lays the
    /// page out shut when it lands. So during a close the two disagree. The press reads the asked-for
    /// state, so a second press mid-slide reverses the slide instead of repeating it, and the chevron
    /// follows the press rather than the layout.
    ///
    /// <para><b>The rest of the page never moves during a slide.</b> The columns take their new height
    /// in one step at the start of an open and at the end of a close. A slide that re-laid the page out
    /// once a frame would measure the tracklist, both papers panels and the record's head sixty times a
    /// second; see GrooveGlide for what that costs on this page.</para>
    ///
    /// <para><b>On a page that scrolls too.</b> There the heading does not move by itself. The block's
    /// top is fixed in the page. Opening makes the page longer below what is on screen, and shutting
    /// makes it shorter, so the scroller pulls its offset back. So an open also scrolls the page just
    /// far enough to show the whole row (never upward), and both directions count the change in offset
    /// as part of the distance. Seen from the card, the result is the same as on a page that does not
    /// scroll: the heading rises by the row on an open at the foot of the page, and drops by it on a
    /// close.</para>
    /// </remarks>
    private void SimilarToggle_Click(object sender, RoutedEventArgs e) => FootTab(FootShows.Similar);

    /// <summary>The fold itself, whichever heading pressed it: the foot holds one row at a time (see
    /// AlbumDetailView.Series), and that row opens and shuts as this one always has.</summary>
    private void ToggleFoot()
    {
        // A groove fold in flight lands first. It clips this block on its right while it slides, and
        // the two moves cannot share one element.
        if (_grooveGlide.Running)
            SettleGroove();

        var opening = SimilarRowState() != SimilarState.Open;
        Settings.AlbumSimilarOpen = opening;
        PointSimilarChevron(opening);

        if (opening)
            OpenSimilar();
        else
            CloseSimilar();
    }

    /// <summary>Whether the page carries its own scrolling, which it does once anything stands under
    /// the columns (see ApplyStacking).</summary>
    private bool PageScrolls => GrooveStacked || _papersStacked;

    /// <summary>What a similar-albums slide translates. The block alone on a page that does not scroll,
    /// where nothing else moves. The page's whole grid on one that does, because there the change in
    /// the scroller's offset moves everything on screen (see SimilarSlide).</summary>
    private FrameworkElement SimilarCarried => PageScrolls ? PageGrid : SimilarBlock;

    private void OpenSimilar()
    {
        // Still laid out open: this press reverses a close that has not landed yet. The page is already
        // right, and a close never scrolls. So the slide in flight is sent back up to rest from
        // wherever it has got to (see SimilarSlide.Retarget, which explains why it is not stopped and
        // started again).
        if (_similarRow == SimilarState.Open)
        {
            if (_similarSlide.Retarget(0, onLanded: static () => { }))
                SayReversed(opening: true);
            return;
        }

        _similarSlide.Stop();

        // Measure where the heading is drawn on the shut page, then lay out the open page. UpdateLayout
        // measures and arranges without drawing, so no frame of the open page is shown before the slide
        // puts the block back where it was.
        UpdateLayout();
        var shutOffset = PageScroller.VerticalOffset;
        var shutTop = LayoutTop(SimilarBlock) - shutOffset;
        ApplyDensity();
        UpdateLayout();

        // On a page that scrolls, the row has just been laid out below what was on screen. Scroll down
        // far enough that its foot, and the page's padding under it, are in view, and no further. Never
        // scroll up. The offset is worked out from the grid's own height, not read back from the
        // scroller, because this turn cannot be sure the scroller's extent is up to date. It is zero on
        // a page that does not scroll.
        var openSlot = Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(SimilarBlock);
        var offset = shutOffset;
        if (PageScrolls)
        {
            var viewport = PageScroller.ViewportHeight;
            var deepest = Math.Max(0, PageGrid.ActualHeight - viewport);
            var showsRow = openSlot.Bottom + PageGrid.Padding.Bottom - viewport;
            offset = Math.Min(deepest, Math.Max(shutOffset, showsRow));

            if (offset > shutOffset + 0.5)
                PageScroller.ChangeView(null, offset, null, disableAnimation: true);
        }

        // How far the heading has moved on screen: through the page, plus whatever the scroll took it.
        var rise = shutTop - (openSlot.Y - offset);

        // The open page is already laid out, so landing has nothing left to do.
        _similarSlide.Start(SimilarCarried, SimilarBlock, rise, 0, onLanded: static () => { });
        SaySimilar(opening: true, slid: rise, moved: null, scroll: ScrollNote(offset));
    }

    private void CloseSimilar()
    {
        // Not laid out open, so there is nothing drawn to slide away.
        if (_similarRow != SimilarState.Open)
        {
            _similarSlide.Stop();
            ApplyDensity();
            return;
        }

        // How far the heading drops on screen once the page is laid out shut. Predicted rather than
        // measured, because measuring means laying the page out shut, and the page has to stay laid out
        // open until the slide lands. LandSimilarClose checks the prediction.
        //
        // The row is its own height and the gap the block spends above it, both of which go when it is
        // collapsed. On a page that does not scroll, the block's bottom is pinned to the card's foot,
        // so its top drops by exactly that. On a page that scrolls, its top stays where it is in the
        // page and the page gets that much shorter; the scroller then keeps its offset inside the
        // shorter page, and the heading drops on screen by however much offset that costs. Scrolled to
        // the foot, that is the whole row again; scrolled further up, less or nothing.
        var row = SimilarScroller.ActualHeight + SimilarScroller.Margin.Top + SimilarScroller.Margin.Bottom
            + SimilarBlock.Spacing;
        var offset = PageScroller.VerticalOffset;
        var fall = PageScrolls
            ? offset - Math.Min(offset, Math.Max(0, PageGrid.ActualHeight - row - PageScroller.ViewportHeight))
            : row;

        var openTop = ShownTop(SimilarBlock);

        // A press mid-open reverses it: the slide in flight is sent down to the fall from wherever it
        // has got to, and lands the way a close always does.
        if (_similarSlide.Retarget(fall, onLanded: () => LandSimilarClose(openTop, fall, offset)))
        {
            SayReversed(opening: false);
            return;
        }

        if (!_similarSlide.Start(SimilarCarried, SimilarBlock, 0, fall, onLanded: () => LandSimilarClose(openTop, fall, offset)))
        {
            ApplyDensity();
            SaySimilar(opening: false, slid: 0, moved: null, scroll: ScrollNote(null));
        }
    }

    /// <summary>The ring line for a press that reversed a slide in flight. How far it has to go back is
    /// the compositor's to know on WinUI, so it is not given; a reversed close still says its fall and
    /// the heading's real move when it lands.</summary>
    private void SayReversed(bool opening)
        => RPM.Core.Diagnostics.Scope.Said(
            "panes",
            $"similar {(opening ? "open" : "close")}: reversed the slide in flight ({ScrollNote(null)})");

    /// <summary>
    /// The page laid out shut, once the row has slid down out of view, and the prediction the slide was
    /// drawn to checked against where the heading actually lands.
    /// </summary>
    /// <remarks>
    /// <b>On a page that scrolls, the offset is set here rather than left to the scroller.</b> The
    /// slide drew the whole page <paramref name="fall"/> points lower than its offset put it. This
    /// assumed that the scroller will pull its offset back by that much once the page gets shorter. It
    /// does, while the page is still at its foot. A reader who scrolls up during the slide moves the
    /// page away from the foot. The scroller then pulls back less, or nothing. Taking the translation
    /// off then lifts the whole page by the difference in one frame. So the offset is written as
    /// exactly what leaves the page where the slide left it: the offset as the slide ends, less the
    /// fall.
    /// </remarks>
    /// <param name="pressOffset">The page's offset when the close was pressed, for the ring line: what
    /// the reader scrolled while the slide ran is the difference.</param>
    private void LandSimilarClose(double openTop, double fall, double pressOffset)
    {
        // Read before the page is laid out shut, which is what makes the scroller pull back.
        var drawnOffset = PageScroller.VerticalOffset;
        var blockTop = LayoutTop(SimilarBlock);

        ApplyDensity();
        UpdateLayout();

        if (!PageScrolls)
        {
            SaySimilar(opening: false, slid: fall, moved: ShownTop(SimilarBlock) - openTop, scroll: ScrollNote(null));
            return;
        }

        var keep = Math.Max(0, drawnOffset - fall);
        if (Math.Abs(PageScroller.VerticalOffset - keep) > 0.5)
            PageScroller.ChangeView(null, keep, null, disableAnimation: true);

        // How far the heading is drawn from where the slide left it. It is worked out from layout and
        // the offset asked for, because the scroller does not report a ChangeView in the same turn. It
        // is the heading's own move through the page, plus whatever part of the fall the offset could
        // not take back. It is zero unless the page was scrolled so near its top that the offset had
        // nothing left to give.
        var jump = (LayoutTop(SimilarBlock) - blockTop) + (drawnOffset - fall - keep);
        RPM.Core.Diagnostics.Scope.Said(
            "panes",
            $"similar close: slid {fall:F0}, page kept at offset {keep:F0} " +
            $"(scrolled {pressOffset - drawnOffset:F0} during the slide), landing jump {jump:F0}" +
            (Math.Abs(jump) > 1 ? ", WHICH SHOULD BE NOUGHT" : ""));
    }

    /// <summary>
    /// The top of the slot layout gave an element in its parent, the page's grid. Layout only, so a
    /// slide's translation is never counted, on either head.
    /// </summary>
    /// <remarks>
    /// TransformToVisual is not used here, because on the WinUI head, read in the same turn the slide
    /// ended, it still counted the compositor's last translation. Every close logged "slid 214, heading
    /// moved 428" while the frames showed the heading landing where the slide left it. The slot
    /// includes the element's top margin, which is the same in both states, so the difference of two
    /// slots is the distance the element moved.
    /// </remarks>
    private static double LayoutTop(FrameworkElement element)
        => Microsoft.UI.Xaml.Controls.Primitives.LayoutInformation.GetLayoutSlot(element).Y;

    /// <summary>Where an element's top is drawn in the card: its place in the page less however far the
    /// page is scrolled. The same as <see cref="LayoutTop"/> on a page that does not scroll.</summary>
    private double ShownTop(FrameworkElement element) => LayoutTop(element) - PageScroller.VerticalOffset;

    /// <summary>What the scroller says, for the ring: nothing on a page that does not scroll; on one
    /// that does, the offset this turn asked for (when it asked for one) and the offset the scroller
    /// reports right now. The two differ when a ChangeView has not been applied yet in the same turn.
    /// That is the one value about the open on a scrolling page that is not known yet.</summary>
    private string ScrollNote(double? asked)
        => !PageScrolls ? "page fixed"
            : asked is { } a
                ? $"page scrolls, offset {a:F0} asked, {PageScroller.VerticalOffset:F0} read"
                : $"page scrolls, offset {PageScroller.VerticalOffset:F0}";

    /// <summary>
    /// One line on the panes channel for each press: the direction, how far the block slid, and what
    /// the page's scroller was doing. A close also reports how far the heading really moved once the
    /// page was laid out shut. The two numbers match when the prediction is right. When they differ,
    /// the heading jumps by the difference as the slide lands. A slide of zero is a row that opened or
    /// shut in one step.
    /// </summary>
    /// <remarks>Read with <c>rpmscope log --channel panes</c>.</remarks>
    private static void SaySimilar(bool opening, double slid, double? moved, string scroll)
        => RPM.Core.Diagnostics.Scope.Said(
            "panes",
            $"similar {(opening ? "open" : "close")}: slid {slid:F0}" +
            (moved is { } m
                ? $", heading moved {m:F0}" + (Math.Abs(m - slid) > 1 ? ", WHICH SHOULD BE ONE NUMBER" : "")
                : "") +
            $" ({scroll})");

    private bool _hasTopReview;

    private void ShowTopReview()
        => TopReviewBlock.Visibility = _hasTopReview && _topReviewAllowed
            ? Visibility.Visible
            : Visibility.Collapsed;
}
