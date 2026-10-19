using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Dialogs;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Presentation.Main.Models;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The import workbench: the queue's tree, the bar over it, the tag panel beside it, and everything
/// the three of them do.
///
/// <para>It is its own control because this is where a user sorts a set of files before they are added to
/// the library. A pane that does that much should not be a large part of the main window's markup. What it
/// needs from the window is listed in <see cref="IImportWorkbenchHost"/>.</para>
///
/// <para>The files are split by what each one does. This file is the control itself. <c>.Actions</c> holds
/// what the menu and the toolbar do to the files. <c>.View</c> holds what is on screen: the filter strip,
/// the columns, the status line and the keys. <c>.Grid</c> holds the grid and the dragging of rows, and
/// <c>.TagPanel</c> holds the panel on the left.</para>
/// </summary>
public sealed partial class ImportWorkbenchView : UserControl
{
    public ImportWorkbenchView()
    {
        InitializeComponent();
        Loaded += (_, _) =>
        {
            RestoreTagPanel();
            ApplyColumns();
        };
        // The pane is dragged from a sliver to the width of the screen, so the bench refits itself
        // rather than being laid out once for whatever width it happened to open at.
        SizeChanged += OnBenchSizeChanged;
        QueueTagPanel.CloseRequested += () => ToggleTagPanel();
    }

    /// <summary>The window this sits in. Set once by the page, which is also what supplies the model
    /// and the XamlRoot: a control has neither until it is in the tree.</summary>
    public IImportWorkbenchHost? Host { get; set; }

    private MainModel? Model => Host?.Model;

    private XamlRoot? Root => Host?.Root;

    /// <summary>The switch between the queue rows and the invitation. The code that drives the pane says
    /// which one it wants, and this control shows it.</summary>
    public void ShowQueue(bool hasRows)
    {
        QueueFilled.Visibility = hasRows ? Visibility.Visible : Visibility.Collapsed;
        QueueEmpty.Visibility = hasRows ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The grid itself. The page sets its item source and scrolls rows into view. The source belongs
    /// to the model. Scrolling to a row is requested by the window.</summary>

    /// <summary>Wires the page's model up to the bar, the panel and the counts. Called whenever the
    /// page attaches or re-attaches its model.</summary>
    public void Attach()
    {
        QueueFilterBar.FilterChanged -= FilterBar_FilterChanged;
        QueueFilterBar.FilterChanged += FilterBar_FilterChanged;
        QueueToolbar.Invoked -= QueueToolbar_Invoked;
        QueueToolbar.Invoked += QueueToolbar_Invoked;
        QueueToolbar.FilterStripToggled -= FilterStripToggled;
        QueueToolbar.FilterStripToggled += FilterStripToggled;
        QueueToolbar.TagPanelToggled -= TagPanelToggled;
        QueueToolbar.TagPanelToggled += TagPanelToggled;
        QueueToolbar.ColumnsMenuOpening -= BuildColumnsMenu;
        QueueToolbar.ColumnsMenuOpening += BuildColumnsMenu;
        QueueTagPanel.Applied -= QueueTagPanel_Applied;
        QueueTagPanel.Applied += QueueTagPanel_Applied;
        AttachRpmGrid();
        Refresh();
    }

    /// <summary>Re-reads everything the queue's own chrome is made of: the chip counts, the lists, the
    /// toolbar's enabled state, the status line and what the tag panel is editing.</summary>
    public void Refresh()
    {
        if (Model is not { } m)
            return;
        QueueFilterBar.SetCounts(m.QueueChipCounts);
        QueueFilterBar.SetSources(m.QueueSourceNames);
        QueueFilterBar.SetKinds(m.QueueKinds);
        QueueFilterBar.Filter = m.QueueFilter;
        RefreshFilterStrip();
        RefreshQueueToolbar();
        RefreshStatus();
        // This runs before the panel is filled. A scan starting hides the panel, and this is the one call
        // made whenever the model reports a change.
        ApplyTagPanelVisibility();
        RefreshTagPanel();
    }

    // ── the seam with the window ────────────────────────────────────────────────────────────────
    // The workbench cannot do these three things itself, because each one opens a window, and only one
    // window can be open at a time. They are named here so that the handlers below are easy to read.

    private Task<bool> EnsureSignedInAsync() =>
        Host?.EnsureSignedInAsync() ?? Task.FromResult(false);

    private Task<MassEditLookup?> LookUpReleaseForRowsAsync(
        IReadOnlyList<TrackRow> rows, Dialogs.ArtAsk ask, Dialogs.Models.TrackMetadataStep step,
        bool wantDiscArt = true) =>
        Host?.LookUpReleaseForRowsAsync(rows, ask, wantDiscArt, step)
        ?? Task.FromResult<MassEditLookup?>(null);

    private Task<Dialogs.Models.TrackFill?> NameTracksFromReleaseAsync(
        IReadOnlyList<TrackRow> rows, ReleaseRef? settled, AlbumMeta? known,
        Dialogs.Models.TrackMetadataStep step) =>
        Host?.NameTracksFromReleaseAsync(rows, settled, known, step)
        ?? Task.FromResult<Dialogs.Models.TrackFill?>(null);

    /// <summary>
    /// What a filled row means here: nothing on disk.
    ///
    /// <para>A queue row is a copy from the import store. It has no file of its own yet, because the file
    /// moves later, at import. So the answer is written back to the store and goes no further. Nothing
    /// listens to these objects, so the write-back must be requested by hand.</para>
    /// </summary>
    private Dialogs.Models.TrackMetadataStep QueueTrackStep(IReadOnlyList<TrackRow> rows, bool ticked)
        => Dialogs.Models.TrackMetadataStep.For(rows, ticked, commit: () =>
        {
            Model?.PersistQueueRows(rows);
            return Task.CompletedTask;
        });

    /// <summary>Everything known about one queued row. No disc label: there is no album folder to put
    /// a disc.png beside until the file is imported.</summary>
    private async void Details_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.DataContext is ImportNode { Row: { } row } && Root is { } root)
            await Dialogs.TrackDetailsDialog.ShowAsync(
                root, row,
                // No track step: one file is what this window edits, field by field.
                wantArt => LookUpReleaseForRowsAsync(
                    [row], wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: false));
    }

    private void FilterBar_FilterChanged(RPM.Core.Library.QueueFilter filter)
    {
        if (Model is { } m)
            m.QueueFilter = filter;
    }

    /// <summary>Both buttons that ask for music, the one on the bottom bar and the one in the empty pane, come
    /// through here. The popup belongs to the page. It goes on to open the disc and vinyl windows, and only
    /// one dialog can be open at a time.</summary>
    private void AddMusic_Click(object sender, RoutedEventArgs e) => Host?.RequestAddMusic();
}
