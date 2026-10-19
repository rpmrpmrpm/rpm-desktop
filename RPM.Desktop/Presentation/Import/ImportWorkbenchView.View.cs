using System.Collections;
using System.Collections.Specialized;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services.Platform;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Windows.System;

using RPM.Desktop.Presentation.Grids;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The VIEW half of the workbench: what is on screen rather than what is done to the files.
///
/// <para>The filter strip, which columns are up, the line along the foot, and the keys that reach all
/// of it without the pointer. None of it changes a file, which is why it is one file away from
/// <c>.Actions</c>, where everything does.</para>
/// </summary>
public sealed partial class ImportWorkbenchView
{
    // ── the filter strip ────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Puts the strip where it belongs and tells the toolbar's key about it.
    ///
    /// <para>The strip is hidden until it is asked for. The pane opens with one row of controls and the
    /// grid. The toolbar's filter key shows the strip. A second row must not appear on its own because of
    /// what is in the queue. Once the user has chosen, the answer is kept.</para>
    /// </summary>
    private void RefreshFilterStrip()
    {
        var open = Settings.ImportFilterStripOpen ?? false;
        QueueToolbar.SetFilterState(open, QueueFilterBar.FiltersOn);
        // Never while the grid is hidden. During a scan or an import, an animation takes the place of the
        // list, and there are no rows to narrow until the list is back.
        var grid = Model?.QueueTreeVisibility == Visibility.Visible;
        QueueFilterBand.Visibility = open && grid ? Visibility.Visible : Visibility.Collapsed;
    }

    private void FilterStripToggled(bool open)
    {
        Settings.ImportFilterStripOpen = open;
        RefreshFilterStrip();
    }

    // ── the status line ─────────────────────────────────────────────────────────────────────────

    /// <summary>What the foot of the pane says about the queue. The sentence itself is
    /// <see cref="QueueStatus"/>'s; this only puts it on screen.</summary>
    private void RefreshStatus()
    {
        if (Model is { } m)
            QueueStatusText.Text = QueueStatus.Describe(m.QueueChipCounts);
    }

    // ── which columns are up ────────────────────────────────────────────────────────────────────

    /// <summary>Puts the remembered columns back. Columns without a Tag are not optional (the tick, the two
    /// mark columns and the name), and they are never changed.</summary>
    private void ApplyColumns()
    {
        if (_queueColumns is not { } columns)
            return;

        var hidden = ImportColumns.Parse(Settings.ImportHiddenColumns);
        foreach (var column in columns.All)
        {
            if (QueueGrid.IsOptional(column))
                columns.Show(column, !hidden.Contains(column.Id));
        }
    }

    /// <summary>The columns menu, built each time it opens. Only the grid's columns know what can be
    /// offered, and the menu is opened rarely.</summary>
    private void BuildColumnsMenu(MenuFlyout menu)
    {
        menu.Items.Clear();

        if (_queueColumns is not { } columns)
            return;

        foreach (var column in columns.All)
        {
            if (!QueueGrid.IsOptional(column) || column.Header.Length == 0)
                continue;

            var item = new ToggleMenuFlyoutItem
            {
                Text = column.Header,
                IsChecked = column.IsVisible,
                FontSize = 12,
            };
            item.Click += (_, _) => SetColumnShown(column.Id, item.IsChecked);
            menu.Items.Add(item);
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        var all = new MenuFlyoutItem { Text = Loc.Get("Queue_ColumnsShowAll"), FontSize = 12 };
        all.Click += (_, _) =>
        {
            Settings.ImportHiddenColumns = null;
            ApplyColumns();
        };
        menu.Items.Add(all);
    }

    private void SetColumnShown(string id, bool shown)
    {
        var hidden = ImportColumns.Parse(Settings.ImportHiddenColumns);
        if (shown)
            hidden.Remove(id);
        else
            hidden.Add(id);

        Settings.ImportHiddenColumns = ImportColumns.Format(hidden);
        ApplyColumns();
    }

    /// <summary>
    /// The grid asks each row whether it is ticked at the moment the row is drawn. There is only one
    /// record of the ticks, so there is nothing to keep in step.
    ///
    /// <para>The grid asks through <c>RpmGrid.Washed</c>. What remains in this file is the two presses in
    /// the foot of the pane.</para>
    /// </summary>
    private void SetAllTicks(bool on)
    {
        Model?.SetAllQueueChecked(on);
        RefreshQueueGrid();
    }

    /// <summary>The two presses in the foot of the pane. They are about the whole queue, not about
    /// whatever is in hand, which is why they are down there rather than among the tools.</summary>
    private void QueueSelectAll_Click(object sender, RoutedEventArgs e) => SetAllTicks(true);

    private void QueueSelectNone_Click(object sender, RoutedEventArgs e) => SetAllTicks(false);

    private static bool Shift() => HeldKeys.Now.HasFlag(Modifier.Shift);

    // ── the keys ────────────────────────────────────────────────────────────────────────────────

    /// <summary>
    /// The keys the queue answers for itself, only while the grid has the focus: Delete with the caret
    /// in the search box is a character, not forty rows leaving the queue. The grid takes the arrows,
    /// Home, End and the run of the selection; these are about the whole queue. Ctrl or Cmd+F, which
    /// reaches the search from anywhere in the pane, is the window's (see ShellPage.TakeShortcut and
    /// SearchTarget).
    /// </summary>
    private void QueueGrid_KeyDown(Windows.System.VirtualKey key, RPM.DataGrid.Model.GridModifiers modifiers)
    {
        switch (key)
        {
            // Ctrl+A ticks everything, Ctrl+Shift+A unticks it: the same pair the grid's own menu
            // offers, said the way every list says it.
            case VirtualKey.A when modifiers.HasFlag(RPM.DataGrid.Model.GridModifiers.Control):
                SetAllTicks(!modifiers.HasFlag(RPM.DataGrid.Model.GridModifiers.Shift));
                break;

            // Off the list, not off the disk. Trashing files is on the menu, behind its own words.
            case VirtualKey.Delete:
                QueueToolbar_Invoked(QueueAction.Remove);
                break;
        }
    }

    // There is no double click on a queue row, on purpose. A click here ticks the row. A double click
    // would tick it and untick it, then do something else on top. That is two actions for one gesture,
    // and the second one runs after the first has been undone. Playing a queued row is on the rail and
    // on the row's own menu, where it happens once.

    /// <summary>The queue's search field. Cmd or Ctrl+F moves focus here while the keyboard is in this pane
    /// (see MainPage.FocusSearch).</summary>
    internal TextBox SearchField => QueueToolbar.Search;
}
