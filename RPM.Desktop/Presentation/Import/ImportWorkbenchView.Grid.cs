using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RPM.DataGrid.Model;
using RPM.DataGrid.Tree;
using RPM.Desktop.Presentation.Grids;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The queue's grid: what it shows, and what a gesture in it means.
///
/// <para>The highlight is not kept in step with the ticks. It IS the ticks, read from each row at the
/// moment the row is drawn, so there is no second copy to keep up to date. Expanding a record inserts
/// rows, and this grid does not treat an insert as a selection.</para>
/// </summary>
public sealed partial class ImportWorkbenchView
{
    private TreeGridSource? _queueSource;
    private GridColumns? _queueColumns;

    /// <summary>What was ticked when the selection last changed, so the next change can say what it
    /// moved rather than what it now is.</summary>
    private readonly SpanSet _wasSelected = new();

    private readonly SpanSet _nowSelected = new();

    private readonly List<Span> _moved = [];

    /// <summary>Builds the grid over the queue's tree. Does nothing twice.</summary>
    private void AttachRpmGrid()
    {
        if (_queueSource is not null || Model is not { } model)
            return;

        _queueSource = new TreeGridSource(model.QueueTree);
        _queueColumns = QueueGrid.Columns();

        QueueGridView.Columns = _queueColumns;
        QueueGridView.Source = _queueSource;
        QueueGridView.Metrics = new DataGrid.Paint.GridMetrics { RowHeight = 22, HeaderHeight = 22 };

        // One record, asked of the row when the row is drawn.
        QueueGridView.Washed = (_, row) => row is ImportNode { IsChecked: true };

        QueueGridView.TickPressed += OnQueueTick;
        QueueGridView.ExpanderPressed += OnQueueExpander;
        QueueGridView.ButtonPressed += OnQueueButton;
        QueueGridView.ContextRequestedAt += OnQueueContext;
        QueueGridView.HeaderMenuRequested += OnQueueHeaderMenu;
        QueueGridView.KeyPressed += QueueGrid_KeyDown;
        QueueGridView.Selection.SelectionChanged += OnQueueSelection;
        QueueGridView.Selection.SweepEnded += OnQueueSweepEnded;
        QueueGridView.RowDragStarting = QueueBlockFor;
        QueueGridView.RowsDropped += OnQueueRowsDropped;

        // The toolbar's counts are SQL over the whole queue. While the pointer is down they are
        // numbers about a selection the user is still making, so they are not read until it is made.
        model.QueueChromeHeld = () => QueueGridView.Selection.IsSweeping;

        ApplyColumns();
    }

    /// <summary>The node the keyboard is on, which is what the toolbar falls back to when nothing is
    /// ticked.</summary>
    private ImportNode? QueueFocusedNode() =>
        _queueSource?.Peek(QueueGridView.Selection.Focus) as ImportNode;

    /// <summary>Draws the grid again. Called when something outside it changed what a row says.</summary>
    private void RefreshQueueGrid() => QueueGridView.Invalidate();

    /// <summary>
    /// The playing track moved. One repaint: the mark is read off the row when the row is drawn, so
    /// there is no viewport to walk and nothing to raise on.
    /// </summary>
    public void MarkPlayingChanged() => QueueGridView.Invalidate();

    /// <summary>Brings a queue row into view without building the rows in between.</summary>
    public void RevealQueueRow(int index) => QueueGridView.ShowRow(index);

    /// <summary>
    /// The art drawer slid, so the content below it moved. Scroll by the same amount to keep a row on
    /// the screen line it was on.
    /// </summary>
    public void CompensateScroll(double delta) => QueueGridView.ScrollBy(delta);

    /// <summary>A tick was pressed. The node owns what that means, a record's tick standing for every
    /// file under it included.</summary>
    private void OnQueueTick(int row)
    {
        if (_queueSource?.Row(row) is not ImportNode { CanTick: true } node)
            return;

        node.IsChecked = node.IsChecked != true;
        RefreshQueueGrid();
    }

    private void OnQueueExpander(int row)
    {
        if (_queueSource?.Row(row) is ImportNode node)
            node.IsExpanded = !node.IsExpanded;
    }

    private async void OnQueueButton(int row, string column)
    {
        if (column != "details" || _queueSource?.Row(row) is not ImportNode { Row: { } track } || Root is not { } root)
            return;

        await Dialogs.TrackDetailsDialog.ShowAsync(
            root, track,
            wantArt => LookUpReleaseForRowsAsync(
                [track], wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: false));
    }

    /// <summary>
    /// The header's menu. This pane sorts and filters through its own toolbar, not through the grid. The
    /// queue order is set by hand, and a column sort would give a second answer to the same question. So the
    /// menu only offers which columns are shown.
    /// </summary>
    private void OnQueueHeaderMenu(GridColumn column, Windows.Foundation.Point at) =>
        GridHeaderMenu.Show(QueueGridView, column, at, new GridHeaderMenu.Actions(
            SortBy: (_, _) => { },
            SortedColumn: null,
            SortedDescending: false,
            Filter: null,
            ClearFilter: null,
            IsFiltered: _ => false,
            IsOptional: QueueGrid.IsOptional));

    private void OnQueueContext(int row, Windows.Foundation.Point at)
    {
        if (Resources["QueueRowMenu"] is not MenuFlyout menu)
            return;

        PrepareQueueContextMenu(row < 0 ? null : _queueSource?.Row(row) as ImportNode);

        menu.ShowAt(QueueGridView, new FlyoutShowOptions
        {
            Position = at,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    /// <summary>
    /// The reader chose rows, so the ticks follow.
    ///
    /// <para>A plain click means "these rows and no others". It applies to the WHOLE queue, not only the part
    /// on screen. It clears every tick first. Otherwise a click could leave three hundred rows ticked below
    /// the fold, which would be a selection the reader cannot see and did not ask for. Import would then act
    /// on those rows. Ctrl and shift add to the ticks, so they only mark what they touched.</para>
    ///
    /// <para>The whole-queue clear belongs to the PRESS, not to each row that a drag crosses. The
    /// <see cref="GridSelection.IsSweeping"/> value tells the two apart. The press has already cleared every
    /// tick outside the run, so each step of the sweep only has to change the rows that the run gained or
    /// lost. Clearing on every step would mean a full-table UPDATE and a walk of the whole tree for each row
    /// the pointer crosses.</para>
    /// </summary>
    /// <summary>The drag is over, so the counts the toolbar shows are worth reading again. One pass,
    /// however many rows the drag crossed.</summary>
    private void OnQueueSweepEnded() => Model?.RaiseHeldQueueChrome();

    private void OnQueueSelection()
    {
        if (_queueSource is not { } source || Model is not { } model)
            return;

        var selection = QueueGridView.Selection;

        _nowSelected.Clear();
        foreach (var span in selection.Spans)
            _nowSelected.Add(span.First, span.Last);

        if (selection.LastModifiers == GridModifiers.None && !selection.IsSweeping)
        {
            // One statement in the store for the whole queue, against one write per row.
            model.SetAllQueueChecked(false);
            _wasSelected.Clear();
        }
        else
        {
            _wasSelected.Difference(_nowSelected, _moved);
            Tick(_moved, on: false);
        }

        _nowSelected.Difference(_wasSelected, _moved);
        Tick(_moved, on: true);

        _wasSelected.CopyFrom(_nowSelected);
        RefreshQueueGrid();

        void Tick(List<Span> spans, bool on)
        {
            foreach (var span in spans)
            {
                for (var i = span.First; i <= span.Last; i++)
                {
                    // Peek, not fetch. A shift-select reaching rows nobody has scrolled to must not
                    // pull every page of the queue through the store, and a row that is not held has
                    // no tick to write.
                    if (source.Peek(i) is ImportNode { CanTick: true } node)
                        node.IsChecked = on;
                }
            }
        }
    }

    // ── arranging a record by hand ──────────────────────────────────────────────────────────────
    //
    // A drag works inside ONE record and nowhere else. A drag sets the order of the tracks. Which record a
    // file belongs to is a metadata question, answered by editing its fields or by Recognize. A gesture that
    // could do both would sometimes do the wrong one. A drop just past the last track of a record would give
    // those files the album, artist and year of the next record.

    /// <summary>
    /// The rows a drag from <paramref name="row"/> carries, and the record it is kept inside.
    ///
    /// <para>The walk starts at the row that was pressed and goes outward. It does not scan the whole list.
    /// A queue can hold twenty thousand rows and a record has about a dozen. The walk stops at the first row
    /// that belongs to another record or is not loaded yet.</para>
    ///
    /// <para>A ticked row carries the whole ticked set, which is the selection. So dragging one of a dozen
    /// ticked tracks moves all of them. An unticked row is moved alone.</para>
    /// </summary>
    private GridDragBlock? QueueBlockFor(int row)
    {
        if (_queueSource is not { } source
            || source.Peek(row) is not ImportNode { Row: not null } pressed
            || pressed.Parent is not { GroupArtist: not null, GroupAlbum: not null } record)
        {
            return null;
        }

        var first = row;
        while (first - 1 >= 0 && IsTrackOf(record, source.Peek(first - 1)))
            first--;

        var last = row;
        while (last + 1 < source.Count && IsTrackOf(record, source.Peek(last + 1)))
            last++;

        var wholeSet = pressed.IsChecked == true;
        var carried = new SpanSet();

        for (var i = first; i <= last; i++)
        {
            var take = wholeSet
                ? source.Peek(i) is ImportNode { IsChecked: true }
                : i == row;

            if (take)
                carried.Add(i, i);
        }

        return carried.IsEmpty ? null : new GridDragBlock(first, last, carried);

        static bool IsTrackOf(ImportNode record, object? row) =>
            row is ImportNode { Row: not null } node && ReferenceEquals(node.Parent, record);
    }

    /// <summary>The rows were let go somewhere they were not. The store is told an order; it is never
    /// told a track number.</summary>
    private void OnQueueRowsDropped(GridDragBlock block, int slot)
    {
        if (_queueSource is not { } source || Model is not { } model)
            return;

        if (source.Peek(block.First) is not ImportNode { Parent: { } record } firstRow)
            return;

        var ids = new List<long>();
        foreach (var i in block.Carried.Indices())
        {
            if (source.Peek(i) is ImportNode node)
                ids.Add(node.QueueId);
        }

        // What the record will read like, worked out from the indices rather than read back out of
        // the store: the rows are the objects on screen, and the tree is told which slots they sit in
        // now.
        var settled = new List<ImportNode>();
        foreach (var i in block.Settled(slot))
        {
            if (source.Peek(i) is ImportNode node)
                settled.Add(node);
        }

        var (above, below) = block.Neighbours(slot);

        model.ReorderQueueRows(
            record,
            ids,
            above is { } a && source.Peek(a) is ImportNode up ? up.QueueId : null,
            below is { } b && source.Peek(b) is ImportNode down ? down.QueueId : null,
            firstRow.LeafOffset,
            settled);

        RefreshQueueGrid();
    }

    private void DetachRpmGrid()
    {
        QueueGridView.TickPressed -= OnQueueTick;
        QueueGridView.ExpanderPressed -= OnQueueExpander;
        QueueGridView.ButtonPressed -= OnQueueButton;
        QueueGridView.ContextRequestedAt -= OnQueueContext;
        QueueGridView.HeaderMenuRequested -= OnQueueHeaderMenu;
        QueueGridView.KeyPressed -= QueueGrid_KeyDown;
        QueueGridView.Selection.SelectionChanged -= OnQueueSelection;
        QueueGridView.Selection.SweepEnded -= OnQueueSweepEnded;
        QueueGridView.RowDragStarting = null;
        QueueGridView.RowsDropped -= OnQueueRowsDropped;

        if (Model is { } model)
            model.QueueChromeHeld = null;

        QueueGridView.Detach();
        _queueSource?.Dispose();
        _queueSource = null;
    }
}
