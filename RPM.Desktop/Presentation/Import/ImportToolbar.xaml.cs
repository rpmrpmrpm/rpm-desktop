using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Import;

/// <summary>What the toolbar was asked to do. There is one value per entry, so the workbench routes them all
/// through a single switch instead of many separate handlers.</summary>
public enum QueueAction
{
    Recognize,
    NumberTracks,
    TagsFromFilename,
    TagsFromTags,
    Edit,
    Remove,
    NameFromRelease,
    Download,
    SplitAlbum,
    NumberDiscs,
    AddNowPlaying,
    PlayNow,
    Trash,
    RemoveDuplicates,
    RemoveUnreadable,
    ShowInFinder,
}

/// <summary>
/// The workbench's toolbar: one row, with the tools on the left and the view on the right.
///
/// <para>Every key here is a second way to reach a handler that the right-click menu also has. The rail
/// holds the nine tools that are used most while working through a queue. They are icons, and each one
/// says what it is when the pointer is on it (see <see cref="ToolKey"/>). The ⋯ menu holds the tools that
/// are used less often. The right-click menu has all of them, with full words.</para>
/// </summary>
public sealed partial class ImportToolbar : UserControl
{
    /// <summary>One tool on the rail: the key, what pressing it asks for, and the group hairline that
    /// stands in front of it (the first key of each group has one).</summary>
    private sealed record Tool(ToolKey Control, QueueAction Action, Border? Divider);

    /// <summary>The space between two neighbouring keys on the rail. This is the StackPanel's own spacing.</summary>
    private const double KeySpacing = 1;

    /// <summary>A group hairline with its margins: 1 wide, 5 either side.</summary>
    private const double DividerWidth = 11;

    /// <summary>True while a switch is being written in from outside, so putting the toolbar into a
    /// state does not raise that state back at whoever set it.</summary>
    private bool _loading;

    /// <summary>The rail, most worth showing first. Order IS priority: what does not fit is taken off
    /// the end and offered in the ⋯ menu instead.</summary>
    private readonly List<Tool> _tools;

    /// <summary>Whatever the rail could not show, in the same order. What the ⋯ menu is made of.</summary>
    private List<Tool> _overflow = [];

    private bool _canRemoveDuplicates;
    private bool _canRemoveUnreadable;

    public ImportToolbar()
    {
        InitializeComponent();

        _tools =
        [
            new(ActRecognize, QueueAction.Recognize, null),
            new(ActTagsFromFile, QueueAction.TagsFromFilename, DivTags),
            new(ActTagsFromRelease, QueueAction.NameFromRelease, null),
            new(ActTagsFromTags, QueueAction.TagsFromTags, null),
            new(ActNumber, QueueAction.NumberTracks, DivOrder),
            new(ActNumberDiscs, QueueAction.NumberDiscs, null),
            new(ActSplitAlbum, QueueAction.SplitAlbum, DivSplit),
            new(ActEdit, QueueAction.Edit, DivEdit),
            new(ActDownload, QueueAction.Download, DivFiles),
            new(ActShowInFinder, QueueAction.ShowInFinder, null),
            new(ActRemove, QueueAction.Remove, DivRemove),
            new(ActTrash, QueueAction.Trash, null),
            new(ActPlayNow, QueueAction.PlayNow, DivPlay),
            new(ActAddNowPlaying, QueueAction.AddNowPlaying, null),
        ];

        foreach (var tool in _tools)
            tool.Control.Click += (_, _) => Invoked?.Invoke(tool.Action);

        SetSubject(0, 0);
        SetTier(WorkbenchTier.Wide);
    }

    /// <summary>Raised with what was pressed. The workbench decides what it acts on.</summary>
    public event Action<QueueAction>? Invoked;

    /// <summary>The filter strip was asked for, or asked to go away.</summary>
    public event Action<bool>? FilterStripToggled;

    /// <summary>The tag panel was asked for, or asked to go away.</summary>
    public event Action<bool>? TagPanelToggled;

    /// <summary>The columns menu is about to open and has nothing in it. The workbench fills it: the
    /// columns belong to the grid, and the grid is the workbench's.</summary>
    public event Action<MenuFlyout>? ColumnsMenuOpening;

    /// <summary>The search field, for the shortcut that puts the caret in it.</summary>
    public TextBox Search => SearchBox;

    /// <summary>
    /// What the toolbar is about, so each key can go quiet when it is about nothing.
    ///
    /// <para>Most of the rail is switched on by one number: whether anything is ticked. Two keys are different,
    /// and the right-click menu has always asked more of them. DOWNLOAD is offered only where a file is not on
    /// the disk yet. If every selected file is already on the disk, DOWNLOAD would do nothing, and a key that
    /// does nothing looks broken. SPLIT works on ONE file. Splitting a selection is not possible, so SPLIT
    /// waits for a single row.</para>
    /// </summary>
    /// <param name="ticked">How many rows are ticked.</param>
    /// <param name="dataless">How many of those are in the cloud rather than on this disk.</param>
    public void SetSubject(int ticked, int dataless)
    {
        var live = ticked > 0;

        ActRecognize.CanAct = live;
        ActTagsFromFile.CanAct = live;
        ActTagsFromRelease.CanAct = live;
        ActTagsFromTags.CanAct = live;
        ActNumber.CanAct = live;
        ActNumberDiscs.CanAct = live;
        ActEdit.CanAct = live;
        ActShowInFinder.CanAct = live;
        ActRemove.CanAct = live;
        ActTrash.CanAct = live;
        ActPlayNow.CanAct = live;
        ActAddNowPlaying.CanAct = live;

        ActSplitAlbum.CanAct = ticked == 1;
        ActDownload.CanAct = dataless > 0;
    }

    /// <summary>Fits the rail to the room it has: as many keys as there is width for, in the order
    /// they are declared, and the rest in the ⋯ menu.</summary>
    private void Rail_SizeChanged(object sender, SizeChangedEventArgs e) => LayoutRail(e.NewSize.Width);

    private void LayoutRail(double available)
    {
        if (available <= 0)
            return;

        var keys = _tools
            .Select(t => new WorkbenchLayout.RailKey(t.Control.CollapsedWidth, t.Divider is not null))
            .ToList();

        // The widest label is used, because any key could be the one under the pointer.
        var reserve = _tools.Max(t => t.Control.LabelWidth);
        var fit = WorkbenchLayout.RailFits(
            keys, available, KeySpacing, DividerWidth, reserve, ActMore.CollapsedWidth);

        for (var i = 0; i < _tools.Count; i++)
        {
            var shown = i < fit;
            _tools[i].Control.Visibility = Vis(shown);
            // A hairline belongs to the group after it. It is removed when the group's first key is removed,
            // so the rail never ends with a divider.
            if (_tools[i].Divider is { } divider)
                divider.Visibility = Vis(shown);
        }

        _overflow = _tools.Skip(fit).ToList();
        ShowMoreKey();
    }

    /// <summary>The ⋯ key is there when it has something behind it, and not when it has not.</summary>
    private void ShowMoreKey() =>
        ActMore.Visibility = Vis(_overflow.Count > 0 || _canRemoveDuplicates || _canRemoveUnreadable);

    /// <summary>
    /// Fits the toolbar to the room it has. The tools do not change. They are icons at every width, and they
    /// show their names on hover. So only the search field changes. It gets shorter before it is removed.
    /// A field with no room to show the typed text is of no use.
    /// </summary>
    public void SetTier(WorkbenchTier tier)
    {
        SearchHost.Width = tier switch
        {
            WorkbenchTier.Wide => 200,
            WorkbenchTier.Regular => 150,
            _ => 110,
        };
    }

    /// <summary>Sets the filter switch to match where the strip is, and shows how many filters are on.
    /// The count is on the key, not in the strip, because the strip can be hidden. A filter left on
    /// while the strip is hidden would make the queue look smaller than it is.
    ///
    /// <para>The face of the key is the switch. It is lit while the strip is shown, and pressing the key shows
    /// or hides the strip. The count uses the same cyan as the chips, so a filter that is on still shows when
    /// the strip is hidden.</para></summary>
    public void SetFilterState(bool stripOpen, int filtersOn)
    {
        _loading = true;
        try
        {
            ViewFilter.IsChecked = stripOpen;
        }
        finally
        {
            _loading = false;
        }

        FilterCount.Text = filtersOn.ToString();
        FilterCount.Visibility = Vis(filtersOn > 0);
        ViewFilter.Background = Res(stripOpen ? "ChipOnFaceBrush" : "ToolKeyFaceBrush");
        ViewFilter.BorderBrush = Res(stripOpen ? "ChipOnRimBrush" : "ToolKeyRimBrush");
        ViewFilter.Foreground = Res(filtersOn > 0 ? "ChipOnInkBrush" : "ToolKeyInkBrush");
    }

    /// <summary>Puts the tag-panel switch where the panel actually is.</summary>
    public void SetTagPanelOpen(bool open)
    {
        _loading = true;
        try
        {
            ViewTags.IsChecked = open;
        }
        finally
        {
            _loading = false;
        }

        ViewTags.Background = Res(open ? "ChipOnFaceBrush" : "ToolKeyFaceBrush");
        ViewTags.BorderBrush = Res(open ? "ChipOnRimBrush" : "ToolKeyRimBrush");
    }

    /// <summary>Which clean-ups have anything to clean up. The counts come from the marks the grid is already
    /// showing, so the menu never offers to remove a row that has no mark. If neither clean-up applies and
    /// nothing overflows, the ⋯ key is hidden.</summary>
    public void SetCleanUp(bool duplicates, bool unreadable)
    {
        // Remove duplicates is not offered at the moment. The "Hide duplicates" filter on the filter strip
        // removes the same rows from view without deleting anything. Ticks cannot reach rows that are hidden,
        // so a delete option would be a permanent version of the same choice. The action, the store call and
        // the tests are still in the code. Add the entry back to show it again.
        _ = duplicates;
        _canRemoveDuplicates = false;
        _canRemoveUnreadable = unreadable;
        ShowMoreKey();
    }

    private static Visibility Vis(bool on) => on ? Visibility.Visible : Visibility.Collapsed;

    private static Brush Res(string key) => (Brush)Application.Current.Resources[key];

    /// <summary>
    /// Builds the ⋯ menu out of whatever the rail could not show, plus the two clean-ups.
    ///
    /// <para>It is built in code, not declared in markup, because what belongs in it depends on how wide the
    /// pane is. A menu written in markup would have to be kept in step with the rail by hand. The first thing
    /// to drift would be a key that appears in both places.</para>
    /// </summary>
    private void MoreMenu_Opening(object? sender, object e)
    {
        MoreFlyout.Items.Clear();

        foreach (var tool in _overflow)
            MoreFlyout.Items.Add(Entry(tool.Control.Label, tool.Control.Glyph, tool.Control.CanAct, tool.Action));

        if (_overflow.Count > 0 && (_canRemoveDuplicates || _canRemoveUnreadable))
            MoreFlyout.Items.Add(new MenuFlyoutSeparator());

        if (_canRemoveDuplicates)
            MoreFlyout.Items.Add(Entry(
                Loc.Get("Queue_RemoveDuplicates"), "\uf24d", true, QueueAction.RemoveDuplicates));
        if (_canRemoveUnreadable)
            MoreFlyout.Items.Add(Entry(
                Loc.Get("Queue_RemoveUnreadable"), "\uf071", true, QueueAction.RemoveUnreadable));
    }

    private MenuFlyoutItem Entry(string text, string glyph, bool enabled, QueueAction action)
    {
        var item = new MenuFlyoutItem
        {
            Text = text,
            FontSize = 12,
            IsEnabled = enabled,
            Icon = new FontIcon
            {
                Glyph = glyph,
                FontSize = 12,
                FontFamily = (FontFamily)Resources["Fa"],
            },
        };
        item.Click += (_, _) => Invoked?.Invoke(action);
        return item;
    }

    private void ColumnsMenu_Opening(object? sender, object e) => ColumnsMenuOpening?.Invoke(ColumnsFlyout);

    private void Filter_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            FilterStripToggled?.Invoke(ViewFilter.IsChecked == true);
    }

    private void Tags_Click(object sender, RoutedEventArgs e)
    {
        if (!_loading)
            TagPanelToggled?.Invoke(ViewTags.IsChecked == true);
    }

}
