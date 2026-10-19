using RPM.Core.Layout;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Main;

/// <summary>The library rail's one moving part: the tabs move down to get out from under the spinning
/// record.
///
/// <para>The header chrome moves sideways to avoid the disc (see MainPage.AlbumWall's
/// UpdateChromeAvoidance), because it has room to move that way. The rail has no such room. It is at
/// the right edge of the window, and the disc hangs half over that edge. So the tabs move down
/// instead. They move back up when the deck is idle and the record has moved clear of them.</para>
/// </summary>
public sealed partial class MainPage
{
    /// <summary>Clear space kept between the disc's outer edge and the first tab. It is larger than the
    /// chrome's gap because these are buttons. The disc takes clicks wherever it overlaps, so the tabs
    /// need clear space, not only no overlap.</summary>
    private const double RailRecordGap = 14;

    /// <summary>How far the tabs are currently pushed down, in px. Kept so that a re-measure with the
    /// same answer does not restart the slide.</summary>
    private double _railDuck = -1;

    /// <summary>Listens to everything that can move the disc or the rail under it: the artwork's geometry
    /// (the drawer being dragged, the window resized), the deck becoming idle or active, and the pane
    /// itself being laid out at a new height.</summary>
    private void WatchRailForRecord()
    {
        var stage = Art;
        stage.GeometryChanged += OnArtGeometryForRail;
        stage.IdleChanged += OnArtIdleForRail;
        LibraryRail.SizeChanged += OnRailSizeChanged;
        Unloaded += (_, _) =>
        {
            stage.GeometryChanged -= OnArtGeometryForRail;
            stage.IdleChanged -= OnArtIdleForRail;
            LibraryRail.SizeChanged -= OnRailSizeChanged;
        };

        DuckRailForRecord(animate: false);

        void OnArtGeometryForRail() => DuckRailForRecord();
        void OnArtIdleForRail(bool _) => DuckRailForRecord();
        // Not animated: a window resize re-measures on every frame of the drag. A slide that follows
        // each frame would never settle while the pointer is held down.
        void OnRailSizeChanged(object sender, SizeChangedEventArgs e) => DuckRailForRecord(animate: false);
    }

    /// <summary>Moves the tabs to where the record leaves room for them.</summary>
    private void DuckRailForRecord(bool animate = true)
    {
        if (XamlRoot?.Content is not { } root || LibraryRail.ActualHeight <= 0)
            return;

        var stage = Art;

        // The rail's top edge in window coordinates, which is the space the disc's geometry is measured
        // in. The rail's own translate is not included. It is the stack inside the rail that moves, and
        // the rail itself never moves.
        var railTop = LibraryRail.TransformToVisual(root)
            .TransformPoint(new Windows.Foundation.Point(0, 0)).Y;

        var drop = RailDuck.Drop(
            discBottom: stage.IsIdle ? 0 : stage.RecordShadowBottom,
            railTop: railTop,
            gap: RailRecordGap,
            tabsHeight: LibraryRail.TabHeight,
            railHeight: LibraryRail.ActualHeight);

        if (Math.Abs(drop - _railDuck) < 0.5)
            return;
        _railDuck = drop;
        LibraryRail.Duck(drop, animate);
    }
}
