using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Import;

/// <summary>
/// The tag panel beside the import queue: when it is there, how wide, and what it is editing.
///
/// <para>The panel edits the ticked rows. The ticks are the queue's only selection. The panel is given a
/// fresh copy of them whenever the ticks change, because the ticked rows are read from the store, not
/// taken from the grid's own objects.</para>
/// </summary>
public sealed partial class ImportWorkbenchView
{
    /// <summary>
    /// How wide the panel is. This is one fixed number, not a range, because the panel holds a fixed form
    /// and a wider panel would not show more. The number comes from the panel itself: two cover wells and
    /// their insets. See <see cref="ImportTagPanel.NaturalWidth"/>.
    /// </summary>
    private const double TagPanelWidth = ImportTagPanel.NaturalWidth;

    private SettingsService Settings => App.Services.GetRequiredService<SettingsService>();

    /// <summary>
    /// Puts the panel on screen, or takes it off. DISPLAY only: it does not touch what the user asked
    /// for.
    ///
    /// <para>These are two different questions. "Does the user want the tag editor" is answered once, by
    /// pressing its switch. "Is there room for it now" is answered again on every resize of the pane. If
    /// the second answer were saved as the setting, a narrow moment would look as if the user never wanted
    /// the panel.</para>
    /// </summary>
    private void ShowTagPanel(bool open)
    {
        QueueTagColumn.Width = new GridLength(open ? TagPanelWidth : 0);
        QueueTagPanel.Visibility = open ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>
    /// Whether the panel could be on screen at all right now, whatever the user asked for.
    ///
    /// <para>There are two reasons it could not be shown. There is no room for it, because the pane is
    /// narrower than the panel plus a usable grid. Or there is nothing for it to edit. While a folder is
    /// being scanned, the queue fills underneath, and those rows are not ready to be edited. An editor on
    /// a set that changes every second would write to whichever rows were ticked when the pointer moved.
    /// The ticks and Import give the same answer (see <see cref="MainModel.CanTickQueue"/>). So the pane is
    /// either being worked in or being filled, never both.</para>
    /// </summary>
    private bool TagPanelHasRoom(double paneWidth)
        => WorkbenchLayout.FitsTagPanel(paneWidth, TagPanelWidth) && Model?.CanTickQueue != false;

    /// <summary>Shows the panel exactly when it is both wanted and possible. The one place those two
    /// are put together, so a scan and a drag cannot disagree about what is on screen.</summary>
    private void ApplyTagPanelVisibility()
        => ShowTagPanel(Settings.ImportTagPanelOpen && TagPanelHasRoom(ActualWidth));

    /// <summary>What the user asked for: remembered, and shown if it is possible.</summary>
    private void WantTagPanel(bool open)
    {
        Settings.ImportTagPanelOpen = open;
        ApplyTagPanelVisibility();
        QueueToolbar.SetTagPanelOpen(open);
    }

    /// <summary>Puts the panel back where it was left last session.</summary>
    private void RestoreTagPanel() => WantTagPanel(Settings.ImportTagPanelOpen);

    /// <summary>Shows the panel if it is hidden, and hides it if it is shown. The toolbar's switch and the
    /// panel's own close button both come through here, so there is one definition of the panel's state.</summary>
    private void ToggleTagPanel()
    {
        WantTagPanel(!Settings.ImportTagPanelOpen);
        RefreshTagPanel();
    }

    /// <summary>The toolbar's switch was pressed. It already carries the state it wants, so this only
    /// has to make the panel agree with it.</summary>
    private void TagPanelToggled(bool open)
    {
        if (open != Settings.ImportTagPanelOpen)
            ToggleTagPanel();
    }

    /// <summary>Gives the panel the ticked rows. They are read from the store, not taken from the grid,
    /// because most of the ticked rows are paged out and were never made into objects.</summary>
    private void RefreshTagPanel()
    {
        if (Model is not { } m || QueueTagColumn.Width.Value <= 0)
            return;
        // The panel is given the rows it works on, and also a way to look things up about them. The panel
        // cannot open a window itself. The lookup is about what it is editing now, not about the ticks as
        // they were when the panel was built.
        // The panel has no track step. It writes its own fields to the rows it holds and saves them on
        // Apply. A fill that ran behind that would be written twice, or not at all.
        // Whether the ticks are one record, and the disc label that record already has. The label cannot be
        // read from a row, because it belongs to the record and is kept in the store until the import gives
        // the record a folder. This is set before the rows, because giving the rows builds the wells, and
        // this decides whether there is a disc well among them.
        var selected = m.SelectedQueueRowsSnapshot();
        var record = m.WholeQueueRecord(selected);
        QueueTagPanel.Record = record;

        // The disc picture is fetched only when there is a place to show it. Asking a catalogue for a
        // picture with nowhere to go is slow and wasted.
        QueueTagPanel.Lookup = (rows, wantArt) => LookUpReleaseForRowsAsync(
            rows, wantArt, Dialogs.Models.TrackMetadataStep.None, wantDiscArt: record is not null);

        // This is also how the panel looks for a picture, because the panel cannot open the search window.
        QueueTagPanel.FindArt = FindArtForPanelAsync;

        QueueTagPanel.SetRows(selected);
    }

    /// <summary>
    /// The cover search, over the words the panel is showing.
    ///
    /// <para>This is the same search window that the album editor opens from its two pictures, and it opens
    /// the same way. A cover search comes from the cover well, and a disc search from the disc well. A
    /// chosen cover also gives back the disc that came with it, if the source had one.</para>
    /// </summary>
    private async Task<FoundArt?> FindArtForPanelAsync(
        RPM.Fingerprint.Catalog.CoverKind kind, string? artist, string? album)
    {
        if (XamlRoot is null)
            return null;

        // Every source the search window uses goes through the rpm API with this user's session. When the
        // user is signed out the results are empty, so the user is asked to sign in first.
        if (!await Dialogs.SignInGate.AllowAsync(XamlRoot, Loc.Get("Login_ForCovers")))
            return null;

        var covers = App.Services.GetRequiredService<RPM.Core.Metadata.Providers.ICoverSearchApi>();
        var dialog = new Dialogs.CoverArtSearchDialog(covers, artist, album, kind) { XamlRoot = XamlRoot };
        await DialogHost.ShowAsync(dialog);

        return dialog.Picked is { Length: > 0 } picked ? new FoundArt(picked, dialog.PickedDisc) : null;
    }

    /// <summary>
    /// The panel wrote its fields. The rows are copies from the store, so nothing is listening to them.
    /// They are saved here, and the tree is rebuilt because an edited album or artist can now belong
    /// somewhere else.
    ///
    /// <para>This saves the panel's own rows, the ones it just wrote to. Saving a fresh copy of the ticked
    /// rows would write the old values back over the edit, and Apply would appear to do nothing.</para>
    /// </summary>
    private void QueueTagPanel_Applied(TagPanelEdit edit)
    {
        if (Model is not { } m)
            return;

        // Save the disc label first, while the rows still show which record they belong to. Saving the
        // fields can rename that record, and the label is keyed by row for that reason.
        if (edit.DiscArt is { Length: > 0 } label)
            m.SetQueueAlbumIntent(edit.Rows, new RPM.Core.Library.QueueAlbumIntent(DiscArt: label));

        m.PersistQueueRows(edit.Rows);

        // Named, not counted. A press that saved a cover and a disc label looks the same as one that saved a
        // genre if only the count is shown. The status is also the only sign on screen that tells a record
        // whose art is being saved from one whose art is still unapplied in a well.
        m.SetStatus(Loc.Format(
            (edit.Cover, edit.DiscArt) switch
            {
                ({ Length: > 0 }, { Length: > 0 }) => "Queue_TagPanelAppliedBoth",
                ({ Length: > 0 }, _) => "Queue_TagPanelAppliedCover",
                (_, { Length: > 0 }) => "Queue_TagPanelAppliedDisc",
                _ => "Queue_TagPanelApplied",
            },
            edit.Rows.Count));
    }

    // ── the Import button ───────────────────────────────────────────────────────────────────────

    /// <summary>
    /// Import, asked from the Import button. Files are filed only after the pending edits in the tag editor
    /// have been answered: applied, or thrown away, on the record.
    ///
    /// <para>The panel writes to copies of the rows and saves them on Apply. An import that ran while the
    /// boxes were still changed would file the old values, and the edit would be left in a panel whose
    /// rows had already left the queue. Both cannot be allowed, so the press asks the user first.</para>
    ///
    /// <para>The question is asked before the import starts, and it is answered on the UI thread. Nothing
    /// about the queue changes between the question and the import, because the dialog is modal.
    /// <see cref="MainModel.CanImport"/> is read again after the dialog closes, because a scan may have
    /// started while the dialog was open.</para>
    /// </summary>
    private async void ChinImport_Click(object sender, RoutedEventArgs e)
    {
        if (Model is not { } m)
            return;

        switch (ImportGate.For(m.CanImport, QueueTagColumn.Width.Value > 0, QueueTagPanel.HasPendingEdits))
        {
            case ImportPress.Nothing:
                return;

            case ImportPress.AskAboutTags:
                switch (await AskAboutUnsavedTagsAsync())
                {
                    case UnsavedTags.Apply:
                        // Goes through the panel's own Apply, so the edit is saved, and the status line reports what
                        // was saved, just as a press of the Apply button would.
                        QueueTagPanel.ApplyPendingEdits();
                        break;
                    case UnsavedTags.Discard:
                        QueueTagPanel.DiscardPendingEdits();
                        break;
                    default:
                        return;
                }

                // Checked again after the dialog closes. The dialog is modal, but the queue underneath is not:
                // a scan may have started while the dialog was open. CanImport is the one answer to whether
                // anything can be filed.
                if (!m.CanImport)
                    return;
                break;
        }

        m.ImportCommand.Execute(null);
    }

    /// <summary>The question itself. Escape and the close button both mean that the import is cancelled.
    /// That is the safe choice: nothing is written and nothing is thrown away.</summary>
    private async Task<UnsavedTags> AskAboutUnsavedTagsAsync()
        => await Dialogs.PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("Queue_UnsavedTagsTitle"),
            Loc.Get("Queue_UnsavedTagsBody"),
            primaryText: Loc.Get("Queue_UnsavedTagsApply"),
            secondaryText: Loc.Get("Queue_UnsavedTagsDiscard"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Primary) switch
        {
            ContentDialogResult.Primary => UnsavedTags.Apply,
            ContentDialogResult.Secondary => UnsavedTags.Discard,
            _ => UnsavedTags.Cancel,
        };

    // ── the room the bench has ──────────────────────────────────────────────────────────────────

    private WorkbenchTier _tier = WorkbenchTier.Wide;

    /// <summary>
    /// Fits the bench to the pane. The pane can be dragged from a sliver to the width of the screen,
    /// so this runs on every size change and is cheap: three property reads and, only when the tier
    /// actually changes, a handful of Visibility writes.
    ///
    /// <para>The tag panel is stepped aside, not closed, when there is no room for it. The user opened it,
    /// and dragging the window narrower does not mean the user has changed their mind. The chosen width is
    /// kept, and the panel comes back when there is room. A scan steps the panel aside in the same way, and
    /// it comes back when the scan is done.</para>
    /// </summary>
    private void OnBenchSizeChanged(object sender, SizeChangedEventArgs e)
    {
        var width = e.NewSize.Width;

        var tier = WorkbenchLayout.TierFor(width);
        if (tier != _tier)
        {
            _tier = tier;
            QueueToolbar.SetTier(tier);
        }

        ShowTagPanel(Settings.ImportTagPanelOpen && TagPanelHasRoom(width));
    }
}
