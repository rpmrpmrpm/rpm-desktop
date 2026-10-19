using RPM.Desktop.Presentation.Main.Library;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.RightPane;

/// <summary>The playlists tree, measured with the album list's own stopwatch. This is off unless
/// <c>RPM_LIST_DIAG=tree</c> is set in the environment.
///
/// <para><b>Why the tree needs a measurement.</b> The tree has about a hundred rows of twenty-four points,
/// and it scrolls badly. That combination means the cost is not the amount of work. Something is being done
/// far more often than it should be. Reading the markup cannot settle this. A reading of the markup can
/// give a confident answer that is wrong. So the question is measured. See <see cref="ListDiag"/>, the
/// instrument that is used here.</para>
///
/// <para><b>Three questions, in the order they should be asked:</b></para>
///
/// <list type="number">
/// <item>Is the UI thread being held? The beat's own lateness shows this. It is shown the moment it
/// happens, not averaged into a summary.</item>
/// <item>Is the list VIRTUALIZING? <c>churn</c> compared with how many rows fit on screen answers this. A
/// hundred rows realised to show forty means the list is not virtualising. That alone would explain the
/// cost.</item>
/// <item>Is it laying out more often than it is scrolled? <c>layouts</c> compared with <c>view</c> answers
/// this. Many layouts per scroll notch means something is invalidating measure on its own.</item>
/// </list>
///
/// <para>Nothing here is on the normal path. Every entry point is gated by <see cref="ListDiag.OnTree"/>,
/// which is false unless the environment names this list.</para>
/// </summary>
public sealed partial class PlaylistView
{
    private bool _treeWatched;

    /// <summary>Hooks the tree up to the watch. This is called from Loaded, which is the first moment when
    /// the containers and the scroller inside the ListView exist to be found.</summary>
    private void WatchTree()
    {
        if (!ListDiag.OnTree || _treeWatched)
            return;
        _treeWatched = true;

        // One in and one out for each container that the panel realises or recycles. This answers the
        // virtualisation question, which the markup cannot answer.
        Tree.ContainerContentChanging += (_, args) =>
        {
            if (args.InRecycleQueue)
                ListDiag.Cleared();
            else
                ListDiag.Realized();
        };

        // Every layout pass that the tree takes part in. Compared with the scroll count below, this shows
        // whether the list lays out because it was scrolled, or for some other reason.
        Tree.LayoutUpdated += (_, _) => ListDiag.Laid();

        if (Scroller(Tree) is { } scroller)
            scroller.ViewChanged += (_, _) =>
                ListDiag.View(scroller.VerticalOffset, scroller.ExtentHeight);

        ListDiag.Start("tree", TreeState);
    }

    /// <summary>What the tree holds when a line is written: how many rows it has been given, how many of
    /// them currently have a container, and how many would fit on screen. The last two read together show
    /// whether it virtualizes at all.</summary>
    private string TreeState()
    {
        var rows = (DataContext as PlaylistModel)?.Visible.Count ?? 0;

        var live = 0;
        for (var i = 0; i < rows; i++)
            if (Tree.ContainerFromIndex(i) is not null)
                live++;

        // Taken from a realised container, not from the markup's number. The view already needs this value
        // for its drag arithmetic. One answer is better than two that can drift apart.
        var height = TreeRowHeight();
        var room = height > 0 && Scroller(Tree) is { } scroller && scroller.ViewportHeight > 0
            ? (int)Math.Ceiling(scroller.ViewportHeight / height)
            : 0;

        return $"rows {rows}, containers {live}, room for {room}";
    }

    /// <summary>The ScrollViewer inside a control's template. The album list walks the tree to find it,
    /// because a list's scroller is not a named part. There is no other way to reach it.</summary>
    private static ScrollViewer? Scroller(DependencyObject? root)
    {
        if (root is null)
            return null;
        if (root is ScrollViewer found)
            return found;

        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
            if (Scroller(VisualTreeHelper.GetChild(root, i)) is { } scroller)
                return scroller;

        return null;
    }
}
