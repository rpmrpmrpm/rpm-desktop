using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Main;

/// <summary>How much of the library header fits. The header holds a 72 pt heading and a row of
/// controls beside it. Both have fixed sizes. On a narrow window the two need more room than the panel
/// has. Then the heading is clipped mid-word and the controls run on under it.
///
/// <para>The room is given up in one fixed order. The header would rather lose a control than split the
/// heading across lines. So the two widest controls go first, while everything is still on one row: the
/// cover-size slider, then the view switch. Only when neither is enough do the controls move down onto a
/// line of their own, under the subtitle. The heading and the subtitle are what the panel says about
/// itself, and neither shares its width with a row of buttons. That line spans the whole panel, so all
/// the controls return to it. From there the controls leave one at a time, least useful first: the
/// slider, the sort picker, grouping, the favourites heart, then the search field (narrowed once before
/// it goes), and last the view switch again. Any control can leave. A control with no room is better
/// left out than left hanging over the edge.</para>
///
/// <para>Every width in the ladder is a fixed number from the markup, not a value measured after a
/// change. So a given panel width lands on the same rung however it got there. This stops the header
/// from flapping. Hiding a control changes what the header wants, but not what it is offered.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>The gap between the header's controls (LibraryChrome's own spacing).</summary>
    private const double HeaderGap = 12;

    /// <summary>The search field's resting width, and the one width it gives up at the last
    /// stage.</summary>
    private const double SearchSlotWidth = 150;
    private const double SearchSlotNarrowWidth = 104;

    /// <summary>A single glyph button in its frame: 32 wide, filling it, and the hairline either
    /// side.</summary>
    private const double GlyphSlotWidth = 34;

    /// <summary>The three-way view switch: 3 × 32 and the frame's own hairline either side.</summary>
    private const double ViewToggleWidth = 98;

    /// <summary>The cover-size cluster: small square, 96 of slider, big square, at 7 apart.</summary>
    private const double SizeClusterWidth = 131;

    /// <summary>The last rung of the ladder: the chrome on its own line, with nothing else left on
    /// it.</summary>
    private const int HeaderStageFloor = 10;

    /// <summary>The rung the header is on. Rungs 0 to 2 keep the chrome beside the heading. Rung 1
    /// drops the cover-size slider, and rung 2 also drops the view switch, so the header stays on one
    /// line. Rung 3 puts the chrome on a line of its own with every control back on it. Each rung above
    /// 3 drops one more control. -1 means the ladder has not been walked yet.
    /// </summary>
    private int _headerStage = -1;

    /// <summary>The width of the heading text, measured once from its font. It is not read from its
    /// column, because the column clips the text. Zero until the font has loaded enough to give the
    /// word a width.</summary>
    private double _headerTitleWidth;

    /// <summary>True while the chrome is on a line of its own under the heading.</summary>
    private bool HeaderIsStacked => _headerStage >= 3;

    // ── what each rung still holds ── Each control has one predicate here, rather than a comparison
    // written out at every use. The rung numbers mean nothing on their own, and a control's whole rule
    // is on one line here.

    private static bool WantsSizeCluster(int stage) => stage is 0 or 3;
    private static bool WantsSortSlot(int stage) => stage <= 4;
    private static bool WantsGroupSlot(int stage) => stage <= 5;
    private static bool WantsFavoritesSlot(int stage) => stage <= 6;
    private static bool WantsSearchSlot(int stage) => stage <= 8;
    private static bool WantsNarrowSearch(int stage) => stage == 8;
    private static bool WantsViewToggle(int stage) => stage != 2 && stage <= 9;

    /// <summary>Whether the wall's own slot (the sort picker and the cover-size slider) still holds
    /// anything. Read by <see cref="ShowSizeSlot"/>, which hands the slot its column.</summary>
    private bool HeaderWallSlotWanted => WantsSizeCluster(_headerStage) || WantsSortSlot(_headerStage);

    /// <summary>Puts the header on the widest rung its panel can hold. Called on every layout of the
    /// header (every window resize), and again whenever the view changes, because the view decides which
    /// controls are wanted at all.</summary>
    private void ApplyHeaderDensity(bool force = false)
    {
        if (_view is not { } view)
            return;

        var available = LibraryHeading.ActualWidth;
        if (available <= 0)
            return;

        if (_headerTitleWidth <= 0)
        {
            _headerTitleWidth = MeasureHeaderTitle();
            if (_headerTitleWidth <= 0)
                return;   // the face has not been measured yet; the next layout asks again
        }

        var stage = 0;
        while (stage < HeaderStageFloor && HeaderRowWidth(stage, view) > available)
            stage++;

        if (stage == _headerStage && !force)
            return;
        _headerStage = stage;

        // The chrome either sits beside the heading or on a line of its own under the subtitle. On that
        // line it spans the whole panel, right-aligned, so it ends where the heading's row ends.
        var stacked = HeaderIsStacked;
        Grid.SetRow(LibraryChrome, stacked ? 2 : 0);
        Grid.SetColumn(LibraryChrome, stacked ? 0 : 1);
        Grid.SetColumnSpan(LibraryChrome, stacked ? 2 : 1);
        LibraryChrome.HorizontalAlignment = stacked ? HorizontalAlignment.Right : HorizontalAlignment.Stretch;
        // A Grid's columns belong to the whole grid, not to one row. If the chrome kept its Auto
        // column, that column would still take up width in the heading's row after the chrome had left
        // it, and the word would be clipped against an empty column. When stacked, the heading takes both
        // columns back.
        Grid.SetColumnSpan(LibraryTitleBox, stacked ? 2 : 1);
        LibraryChrome.VerticalAlignment = stacked ? VerticalAlignment.Top : VerticalAlignment.Center;
        LibraryChrome.Margin = stacked ? new Thickness(0, 0, 0, 12) : default;
        // The subtitle's bottom margin is the gap between the header and the collection below it. With
        // the chrome under it, that gap belongs to the chrome, so the subtitle only needs its own line.
        LibrarySubheading.Margin = new Thickness(0, -4, 0, stacked ? 6 : 12);

        SearchSlot.Width = WantsNarrowSearch(stage) ? SearchSlotNarrowWidth : SearchSlotWidth;
        SearchSlot.Visibility = Shown(WantsSearchSlot(stage));
        ViewToggle.Visibility = Shown(WantsViewToggle(stage));
        SizeCluster.Visibility = Shown(WantsSizeCluster(stage));
        SortSlot.Visibility = Shown(WantsSortSlot(stage));
        RefreshHeaderChromeVisibility(view);

        // With both of the wall's controls hidden, the slot would be an empty column with a gap in front
        // of it.
        if (ViewIsWall)
            ShowSizeSlot();

        // The chrome's right edge has moved, so the space the record must avoid is measured again.
        UpdateChromeAvoidance(animate: false);

        static Visibility Shown(bool wanted) => wanted ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What the header wants at a given rung: the chrome, plus the heading beside it while the
    /// two still share a line. On the rungs where the chrome has a line of its own, it has the full
    /// width of the panel, so the rung costs only the row.</summary>
    private double HeaderRowWidth(int stage, LibraryViewMode view)
    {
        var chrome = 0d;
        var parts = 0;

        void Take(double w)
        {
            chrome += w;
            parts++;
        }

        if (WantsSearchSlot(stage))
            Take(WantsNarrowSearch(stage) ? SearchSlotNarrowWidth : SearchSlotWidth);
        if (WantsFavoritesSlot(stage) && view is LibraryViewMode.Wall or LibraryViewMode.List)
            Take(GlyphSlotWidth);
        if (WantsGroupSlot(stage) && view == LibraryViewMode.List)
            Take(GlyphSlotWidth);
        if (WantsViewToggle(stage))
            Take(ViewToggleWidth);
        if (WantsSortSlot(stage) && view == LibraryViewMode.Wall)
            Take(GlyphSlotWidth);
        if (WantsSizeCluster(stage) && view == LibraryViewMode.Wall)
            Take(SizeClusterWidth);

        var row = parts == 0 ? 0 : chrome + HeaderGap * (parts - 1);
        // Rungs 0 to 2 keep the heading beside the chrome, so they also pay for the heading's width.
        return stage <= 2 ? row + HeaderGap + _headerTitleWidth : row;
    }

    /// <summary>The two toggles the view owns that the ladder can take away. The favourites heart belongs
    /// to both album views, grouping to the album list alone, and either can be dropped to make
    /// room.</summary>
    private void RefreshHeaderChromeVisibility(LibraryViewMode view)
    {
        FavoritesSlot.Visibility =
            WantsFavoritesSlot(_headerStage) && view is LibraryViewMode.Wall or LibraryViewMode.List
                ? Visibility.Visible
                : Visibility.Collapsed;
        GroupSlot.Visibility = WantsGroupSlot(_headerStage) && view == LibraryViewMode.List
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The heading's width with nothing limiting it. The word sits in a star column that clips
    /// it, so the arranged width is the column's and says nothing about the type. This measures the
    /// text against open space instead, once, and then hands the layout pass back.</summary>
    private double MeasureHeaderTitle()
    {
        try
        {
            LibraryTitleText.Measure(new Windows.Foundation.Size(
                double.PositiveInfinity, double.PositiveInfinity));
            var width = LibraryTitleText.DesiredSize.Width;
            LibraryTitleText.InvalidateMeasure();
            return width > 1 ? width : 0;
        }
        catch
        {
            return 0;
        }
    }
}
