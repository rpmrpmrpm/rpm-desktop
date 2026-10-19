using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using RPM.DataGrid.Model;
using RPM.Desktop.Strings;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Grids;

/// <summary>
/// The menu a column header opens: what the table is sorted by, what this column is narrowed to, and
/// which columns are on it at all.
///
/// <para>Built each time it opens, not kept up to date. Only the columns know what can be offered,
/// and the menu is opened rarely.</para>
///
/// <para>Shared by both grids, so a header behaves the same on the library and on the queue.</para>
/// </summary>
internal static class GridHeaderMenu
{
    /// <summary>What the host has to be able to do for the menu to mean anything.</summary>
    /// <param name="SortBy">Sorts by a column, or clears the sort when the column is null.</param>
    /// <param name="SortedColumn">The column the table is sorted by now, or null.</param>
    /// <param name="SortedDescending">Which way.</param>
    /// <param name="Filter">Opens the column's filter, or null when this grid does not filter.</param>
    /// <param name="ClearFilter">Takes a column's filter off.</param>
    /// <param name="IsFiltered">Whether a column is narrowed right now.</param>
    /// <param name="IsOptional">Whether a column may be hidden.</param>
    public sealed record Actions(
        Action<string?, bool> SortBy,
        string? SortedColumn,
        bool SortedDescending,
        Action<GridColumn, Point>? Filter,
        Action<GridColumn>? ClearFilter,
        Func<GridColumn, bool> IsFiltered,
        Func<GridColumn, bool> IsOptional);

    public static void Show(RpmGrid grid, GridColumn column, Point at, Actions actions)
    {
        ArgumentNullException.ThrowIfNull(grid);
        ArgumentNullException.ThrowIfNull(column);
        ArgumentNullException.ThrowIfNull(actions);

        if (grid.Columns is not { } columns)
            return;

        var menu = new MenuFlyout();

        // What the chosen item does, run once the menu has closed rather than inside the click.
        // Otherwise an item that opens another popup (Filter) closes at once: the mouse release
        // that chose the item reaches the new popup's dismiss layer.
        Action? chosen = null;
        menu.Closed += (_, _) =>
        {
            var run = chosen;
            chosen = null;
            run?.Invoke();
        };

        void Add(string text, Action click)
        {
            var item = new MenuFlyoutItem { Text = text, FontSize = 12 };
            item.Click += (_, _) => chosen = click;
            menu.Items.Add(item);
        }

        if (column.CanSort)
        {
            Add(Loc.Get("Grid_SortAscending"), () => actions.SortBy(column.Id, false));
            Add(Loc.Get("Grid_SortDescending"), () => actions.SortBy(column.Id, true));

            if (actions.SortedColumn == column.Id)
                Add(Loc.Get("Grid_ClearSort"), () => actions.SortBy(null, false));

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (column.CanFilter && actions.Filter is { } filter)
        {
            Add(Loc.Get("Grid_Filter"), () => filter(column, at));

            if (actions.IsFiltered(column) && actions.ClearFilter is { } clear)
                Add(Loc.Get("Grid_ClearFilter"), () => clear(column));

            menu.Items.Add(new MenuFlyoutSeparator());
        }

        if (actions.IsOptional(column))
            Add(Loc.Get("Grid_HideColumn"), () => columns.Show(column, false));

        // Every column, so one that was hidden can be put back. This is the only way back, because a
        // hidden column has no header to right-click.
        var all = new MenuFlyoutSubItem { Text = Loc.Get("Grid_Columns"), FontSize = 12 };
        foreach (var candidate in columns.All)
        {
            if (!actions.IsOptional(candidate) || candidate.Header.Length == 0)
                continue;

            var item = new ToggleMenuFlyoutItem
            {
                Text = candidate.Header,
                IsChecked = candidate.IsVisible,
                FontSize = 12,
            };

            var shown = candidate;
            item.Click += (_, _) => chosen = () => columns.Show(shown, item.IsChecked);
            all.Items.Add(item);
        }

        if (all.Items.Count > 0)
            menu.Items.Add(all);

        Add(Loc.Get("Grid_ShowAllColumns"), () =>
        {
            foreach (var candidate in columns.All)
                columns.Show(candidate, true);
        });

        // A separator at the end of the menu has nothing after it, so it is removed.
        while (menu.Items.Count > 0 && menu.Items[^1] is MenuFlyoutSeparator)
            menu.Items.RemoveAt(menu.Items.Count - 1);

        if (menu.Items.Count == 0)
            return;

        menu.ShowAt(grid, new FlyoutShowOptions
        {
            Position = at,
            Placement = FlyoutPlacementMode.BottomEdgeAlignedLeft,
        });
    }
}
