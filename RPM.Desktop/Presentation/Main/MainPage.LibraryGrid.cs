using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RPM.Core.Library;
using RPM.DataGrid.Model;
using RPM.Desktop.Presentation.Grids;
using RPM.Desktop.Presentation.Main.Library.Models;
using Windows.System;

namespace RPM.Desktop.Presentation.Main;

/// <summary>
/// The library data table: what it shows, and what a gesture in it means.
///
/// <para>Sorting and filtering are done by the database, not by the grid. The source can hold a million
/// rows, and this code never holds more than the pages on screen. So a header click asks the database
/// for the collection in a new order. It does not re-order rows that were never all in memory. The grid
/// says which column was clicked, and the source says what that means.</para>
/// </summary>
public sealed partial class MainPage
{
    private LibraryTableSource? _library;

    /// <summary>The column the table is sorted by, and which way. It is kept here because the sort is
    /// done by the source. The header only draws what it is told.</summary>
    private string? _librarySort;

    private bool _librarySortDescending;

    private void AttachLibraryGrid(LibraryTableSource source)
    {
        _library = source;

        LibraryGridView.Columns ??= LibraryGrid.Columns();
        LibraryGridView.Source = source.Source;
        LibraryGridView.Metrics = new DataGrid.Paint.GridMetrics { RowHeight = 24, HeaderHeight = 22 };

        LibraryGridView.Selection.SelectionChanged -= LibrarySelectionChanged;
        LibraryGridView.Selection.SelectionChanged += LibrarySelectionChanged;

        LibraryGridView.RowActivated -= LibraryRowActivated;
        LibraryGridView.RowActivated += LibraryRowActivated;

        LibraryGridView.HeaderClicked -= LibraryHeaderClicked;
        LibraryGridView.HeaderClicked += LibraryHeaderClicked;

        LibraryGridView.FilterRequested -= LibraryFilterRequested;
        LibraryGridView.FilterRequested += LibraryFilterRequested;

        LibraryGridView.HeaderMenuRequested -= LibraryHeaderMenu;
        LibraryGridView.HeaderMenuRequested += LibraryHeaderMenu;

        LibraryGridView.ContextRequestedAt -= LibraryContextRequested;
        LibraryGridView.ContextRequestedAt += LibraryContextRequested;

        LibraryGridView.KeyPressed -= LibraryKeyPressed;
        LibraryGridView.KeyPressed += LibraryKeyPressed;

        LibraryGridView.ShowFilters(source.FilteredColumns);
    }

    /// <summary>Double-click plays the track, through the same command the row menu uses.</summary>
    private void LibraryRowActivated(int row)
    {
        if (LibraryRowAt(row) is { } track)
            Model?.PlayCommand.Execute(track);
    }

    private LibraryTableRow? LibraryRowAt(int row) => _library?.Source.Peek(row) as LibraryTableRow;

    /// <summary>
    /// A header was clicked, so the table is asked for in a different order.
    ///
    /// <para>There are three states, as in every sortable table: ascending, then descending, then no
    /// sort. With no sort, a virtualised source uses the store's own order, which is the cheapest query
    /// it has.</para>
    /// </summary>
    private void LibraryHeaderClicked(GridColumn column)
    {
        if (_librarySort != column.Id)
            SortLibraryBy(column.Id, descending: false);
        else if (!_librarySortDescending)
            SortLibraryBy(column.Id, descending: true);
        else
            SortLibraryBy(null, descending: false);

        RememberTableSort();
    }

    private void SortLibraryBy(string? column, bool descending)
    {
        _librarySort = column;
        _librarySortDescending = descending;

        _library?.Source.SetSort(column is null ? [] : [new SortBy(column, descending)]);
        LibraryGridView.ShowSort(column, descending);
    }

    /// <summary>
    /// A column's filter was asked for: a way of matching and a box to type in.
    ///
    /// <para>This is not a tick-list of the column's values. A list of every distinct artist in a
    /// million-row library is a scan the user would have to wait for. Usually the user is looking for a
    /// word, not one of eighty thousand rows in a popup. The three matches are the ones a spreadsheet
    /// offers.</para>
    /// </summary>
    private void LibraryFilterRequested(GridColumn column, Windows.Foundation.Point at)
    {
        if (_library is not { } source)
            return;

        var current = source.FilterOn(column.Id);

        var mode = new ComboBox { Width = 104 };
        mode.Items.Add(Strings.Loc.Get("Filter_StartsWith"));
        mode.Items.Add(Strings.Loc.Get("Filter_Contains"));
        mode.Items.Add(Strings.Loc.Get("Filter_EndsWith"));

        // This runs after the items are added. Selecting an index on an empty box would be out of range.
        // Contains is the default, because it finds a word wherever it sits.
        mode.SelectedIndex = current is null ? (int)TextMatchMode.Contains : (int)current.Mode;

        var text = new TextBox
        {
            Width = 172,
            Margin = new Thickness(6, 0, 0, 0),
            Text = current?.Text ?? string.Empty,
            PlaceholderText = column.Header,
        };

        var clear = new Button
        {
            Style = Application.Current.Resources["DenseButtonStyle"] as Style,
            Content = Strings.Loc.Get("Filter_Clear"),
        };
        var apply = new Button
        {
            Style = Application.Current.Resources["DenseButtonStyle"] as Style,
            Content = Strings.Loc.Get("Filter_Apply"),
            Margin = new Thickness(6, 0, 0, 0),
        };

        var row = new StackPanel { Orientation = Orientation.Horizontal };
        row.Children.Add(mode);
        row.Children.Add(text);

        var buttons = new StackPanel
        {
            Orientation = Orientation.Horizontal,
            HorizontalAlignment = HorizontalAlignment.Right,
            Margin = new Thickness(0, 6, 0, 0),
        };
        buttons.Children.Add(clear);
        buttons.Children.Add(apply);

        var panel = new StackPanel { Padding = new Thickness(10) };
        panel.Children.Add(row);
        panel.Children.Add(buttons);

        // The same sizing as the data-entry dialogs: 24 px inputs and 12 pt text. The resources are
        // merged onto the popup's own root, because a control's template reads these keys from its
        // ancestors, and the popup is not under the page.
        panel.Resources.MergedDictionaries.Add(new ResourceDictionary
        {
            Source = new Uri("ms-appx:///Themes/DenseForm.xaml"),
        });

        var flyout = new Flyout
        {
            Content = panel,
            FlyoutPresenterStyle = FilterPresenterStyle(),
        };

        void Set(TextMatch? match)
        {
            source.SetFilter(column.Id, match);
            LibraryGridView.ShowFilters(source.FilteredColumns);
            flyout.Hide();
        }

        clear.Click += (_, _) => Set(null);
        apply.Click += (_, _) => Set(TextMatch.Of((TextMatchMode)Math.Max(0, mode.SelectedIndex), text.Text));

        // Enter applies the filter. Nothing typed clears the filter, which gives the same result as
        // Clear. This stops the box from setting a filter that matches every row.
        text.KeyDown += (_, e) =>
        {
            if (e.Key != VirtualKey.Enter)
                return;

            e.Handled = true;
            Set(TextMatch.Of((TextMatchMode)Math.Max(0, mode.SelectedIndex), text.Text));
        };

        flyout.Opened += (_, _) => text.Focus(FocusState.Programmatic);

        // Shown on the next turn, not now. The header menu is one of the two ways in. A flyout opened
        // from inside a menu item's click is closed again by the menu's own dismissal on the way out.
        DispatcherQueue.TryEnqueue(() => flyout.ShowAt(LibraryGridView, new FlyoutShowOptions
        {
            Position = at,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        }));
    }

    /// <summary>The flyout's own chrome, sized to the two rows in it rather than to a touch
    /// target.</summary>
    private static Style FilterPresenterStyle()
    {
        var style = new Style(typeof(FlyoutPresenter));
        style.Setters.Add(new Setter(Control.PaddingProperty, new Thickness(0)));
        style.Setters.Add(new Setter(FrameworkElement.MinWidthProperty, 0d));
        style.Setters.Add(new Setter(FrameworkElement.MinHeightProperty, 0d));
        style.Setters.Add(new Setter(Control.CornerRadiusProperty, new CornerRadius(8)));
        return style;
    }

    private void LibraryHeaderMenu(GridColumn column, Windows.Foundation.Point at)
    {
        if (_library is not { } source)
            return;

        GridHeaderMenu.Show(LibraryGridView, column, at, new GridHeaderMenu.Actions(
            SortBy: SortLibraryBy,
            SortedColumn: _librarySort,
            SortedDescending: _librarySortDescending,
            Filter: LibraryFilterRequested,
            ClearFilter: c =>
            {
                source.SetFilter(c.Id, null);
                LibraryGridView.ShowFilters(source.FilteredColumns);
            },
            IsFiltered: c => source.FilterOn(c.Id) is not null,
            IsOptional: LibraryGrid.IsOptional));
    }

    private void LibraryContextRequested(int row, Windows.Foundation.Point at)
    {
        if (Resources["LibraryRowMenu"] is not MenuFlyout menu)
            return;

        PrepareLibraryContextMenu(LibraryRowAt(row));

        menu.ShowAt(LibraryGridView, new FlyoutShowOptions
        {
            Position = at,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }

    private void LibraryKeyPressed(VirtualKey key, GridModifiers modifiers)
    {
        // Delete removes the local files, which can be recovered. Removing them from the cloud as well
        // is on the menu, behind its own confirmation.
        if (key == VirtualKey.Delete)
            DeleteLibrarySelection();
    }

}
