using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using RPM.Core.Ripping;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One unfinished item in staging, waiting to be finished or thrown away: a ripped disc, or a
/// vinyl recording that still needs splitting.</summary>
[RpmBindable]
public sealed partial class PendingRipRow(string folder, string name, string detail, PendingKind kind)
{
    public string Folder { get; } = folder;
    public string Name { get; } = name;

    /// <summary>Artist, track count and size. This is enough to recognise the disc without opening
    /// it.</summary>
    public string Detail { get; } = detail;

    /// <summary>Which flow finishes it: a rip is imported, and a vinyl recording is split
    /// first.</summary>
    public PendingKind Kind { get; } = kind;

    /// <summary>Vinyl only. How far the session got. This decides whether a click splits the recording
    /// or imports it. A recording that has already been cut is imported like any other folder of
    /// tracks.</summary>
    public PendingVinylStage Stage { get; init; }

    /// <summary>The icon for the row. A record and a disc are different things to finish, so the row
    /// shows which one. It is the same card with two types. There is no second source card for
    /// something that is already pending. The icons are Font Awesome record-vinyl and compact-disc,
    /// written as escapes, because the raw glyphs are private-use code points that do not survive every
    /// paste.
    /// </summary>
    public string Glyph => Kind == PendingKind.Vinyl ? "\uf8d9" : "\uf51f";
}

/// <summary>What the user chose in the popup.</summary>
public enum AddMusicAction
{
    /// <summary>Cancelled.</summary>
    None,
    /// <summary>Import <see cref="AddMusicOutcome.Folder"/> as-is (picked folder, pending
    /// rip).</summary>
    Import,
    /// <summary>Import an audio CD: the caller opens the disc window.</summary>
    RipCd,
    /// <summary>Import a video DVD's audio: the caller opens the disc window.</summary>
    RipDvd,
    /// <summary>Record a record: the caller opens the vinyl window.</summary>
    RecordVinyl,
    /// <summary>Split the vinyl session in <see cref="AddMusicOutcome.Folder"/>, then import
    /// it.</summary>
    SplitVinylSession,
}

/// <summary>The result of the popup. Two ContentDialogs cannot be open at once. So anything that needs
/// another dialog is handed back to the caller to open, and is not opened here.</summary>
public sealed record AddMusicOutcome(AddMusicAction Action, string? Folder = null);

/// <summary>The Add music popup. The user picks where the music comes from: a folder, an audio CD, a
/// video DVD, a record on a turntable, or a disc that was already ripped and never imported.
///
/// <para>This is a chooser, and only that. Each source that has its own flow also has its own window.
/// Ripping a disc and recording a record are examples. Each of those deserves its own header, rather
/// than being a step inside this window. The popup returns an <see cref="AddMusicAction"/>, and the
/// caller opens that window. A second ContentDialog cannot be stacked on this one.</para>
///
/// <para>Every path ends with a folder for the import queue to analyse. So nothing downstream needs to
/// know which card was clicked.</para>
/// </summary>
public sealed partial class AddMusicDialog : ContentDialog
{
    private enum Face { Source, Pending }

    private readonly FolderPickerService _folderPicker;
    private readonly RipperService _ripper;
    private readonly string _stagingRoot;

    private readonly ObservableCollection<PendingRipRow> _pending = [];

    private Face _face = Face.Source;

    /// <summary>What the user chose. Cancelling leaves it <see cref="AddMusicAction.None"/>.</summary>
    private AddMusicOutcome _outcome = new(AddMusicAction.None);

    public AddMusicDialog(FolderPickerService folderPicker, RipperService ripper, string stagingRoot)
    {
        _folderPicker = folderPicker;
        _ripper = ripper;
        _stagingRoot = stagingRoot;

        InitializeComponent();
        // The source kicker uses the header font (Cabin) and is shown in lowercase.
        SourceKicker.Text = SourceKicker.Text.ToLowerInvariant();
        // The five source tiles fill the width, so the dialog needs that width. The height is short,
        // because the disc steps have their own window. The dialog has one row of tiles, with enough
        // room below for the pending list to be read before the dialog is made taller.
        Shell.AttachResize(this, minWidth: 720, minHeight: 400, maxWidth: 900, defaultHeight: 470);

        PendingList.ItemsSource = _pending;

        // This window opens a confirmation over itself. On WinUI that hides this window and shows it
        // again later. Using the host means the first fill does not run a second time when the window
        // comes back.
        this.WhenFirstLoaded(OnLoaded);
    }

    /// <summary>Runs the popup and reports what the user chose. Anything needing a further window (a
    /// disc, the vinyl recorder, the split step) comes back as an action for the caller to open,
    /// because a second ContentDialog cannot stack on this one.</summary>
    public static async Task<AddMusicOutcome> ShowAsync(XamlRoot xamlRoot,
        FolderPickerService folderPicker, RipperService ripper, string stagingRoot)
    {
        var dialog = new AddMusicDialog(folderPicker, ripper, stagingRoot) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._outcome;
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        RenderFace();
        await LoadPendingAsync();

        // Ripping is only offered when there is a drive to rip from. With no optical drive, the cards
        // say so now, rather than failing later in another window.
        var drives = await _ripper.ListDrivesAsync();
        var hasDrive = drives.Count > 0;

        CdCard.IsEnabled = hasDrive;
        DvdCard.IsEnabled = hasDrive;

        if (!hasDrive)
        {
            CdCardHint.Text = Loc.Get("AddMusic_NoDrive");
            DvdCardHint.Text = Loc.Get("AddMusic_NoDrive");
        }
    }

    // ── Source ───────────────────────────────────────────────────────────────────────────────

    private async void Folder_Click(object sender, RoutedEventArgs e)
    {
        var path = await _folderPicker.PickFolderAsync();
        if (path is null)
            return;

        Choose(AddMusicAction.Import, path);
    }

    // A disc and a record each open their own window, and that cannot stack on this one. So this popup
    // closes with the answer, and the caller opens the window.
    private void Cd_Click(object sender, RoutedEventArgs e) => Choose(AddMusicAction.RipCd);

    private void Dvd_Click(object sender, RoutedEventArgs e) => Choose(AddMusicAction.RipDvd);

    private void Vinyl_Click(object sender, RoutedEventArgs e) => Choose(AddMusicAction.RecordVinyl);

    private void Choose(AddMusicAction action, string? folder = null)
    {
        _outcome = new AddMusicOutcome(action, folder);
        Hide();
    }

    // ── Pending rips ─────────────────────────────────────────────────────────────────────────

    /// <summary>Reads the staging folder from disk, on a background thread, because it reads the
    /// details of every ripped file. It shows what it finds on the card, so the user can see there is
    /// something waiting before clicking into it.
    /// </summary>
    private async Task LoadPendingAsync()
    {
        var rips = await Task.Run(() => PendingRips.Scan(_stagingRoot));

        _pending.Clear();
        foreach (var rip in rips)
        {
            var parts = new List<string>();
            if (rip.Artist is { Length: > 0 }) parts.Add(rip.Artist);

            // Each type needs something different, so the text says what. A rip has tracks ready to
            // import. An uncut recording has sides waiting to be split. A cut recording has tracks,
            // like any rip. A kept recording was already imported, and is only kept on disk.
            parts.Add(rip.Stage switch
            {
                PendingVinylStage.Kept => Loc.Format("AddMusic_PendingVinylKept", Size(rip.SizeBytes)),
                PendingVinylStage.Unsplit => Loc.Format(
                    rip.SideCount == 1 ? "AddMusic_PendingVinylSide" : "AddMusic_PendingVinylSides",
                    rip.SideCount, Size(rip.SizeBytes)),
                // A rip that stopped early knows how many tracks it was planned to have. So it shows "3
                // of 12", not a bare 3 that looks like a finished album.
                _ when rip.PlannedTrackCount > rip.TrackCount => Loc.Format(
                    "AddMusic_PendingTracksOf", rip.TrackCount, rip.PlannedTrackCount, Size(rip.SizeBytes)),
                _ => Loc.Format(
                    rip.TrackCount == 1 ? "AddMusic_PendingTrack" : "AddMusic_PendingTracks",
                    rip.TrackCount, Size(rip.SizeBytes)),
            });

            _pending.Add(new PendingRipRow(
                rip.Folder,
                rip.Name is { Length: > 0 } named ? named : Unnamed(rip),
                string.Join(" · ", parts),
                rip.Kind)
            {
                Stage = rip.Stage,
            });
        }

        PendingCard.IsEnabled = _pending.Count > 0;
        PendingCardHint.Text = _pending.Count == 0
            ? Loc.Get("AddMusic_PendingNone")
            : Loc.Format("AddMusic_PendingHint", _pending.Count);

        UpdateImportEnabled();
    }

    /// <summary>What to call something that nothing has named: it has no tags on its audio and no title
    /// in its manifest. The folder name is a guid, and a guid says nothing. So the row shows what the
    /// thing is and when it was ripped. That is how the user remembers it.</summary>
    private static string Unnamed(PendingRip rip)
    {
        var when = rip.RippedUtc.ToLocalTime().ToString("m", System.Globalization.CultureInfo.CurrentCulture);
        return Loc.Format(rip.Source switch
        {
            RipSource.Vinyl => "AddMusic_PendingUnnamedVinyl",
            RipSource.Cd => "AddMusic_PendingUnnamedCd",
            RipSource.Dvd => "AddMusic_PendingUnnamedDvd",
            _ => rip.Kind == PendingKind.Vinyl ? "AddMusic_PendingUnnamedVinyl" : "AddMusic_PendingUnnamedDisc",
        }, when);
    }

    private void Pending_Click(object sender, RoutedEventArgs e) => GoTo(Face.Pending);

    private void Pending_SelectionChanged(object sender, SelectionChangedEventArgs e) => UpdateImportEnabled();

    private async void PendingDelete_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not Button { Tag: string folder }) return;

        var confirm = await PromptDialog.ShowAsync(
            XamlRoot,
            Loc.Get("AddMusic_PendingDeleteTitle"),
            Loc.Get("AddMusic_PendingDeleteBody"),
            primaryText: Loc.Get("AddMusic_PendingDelete"),
            closeText: Loc.Get("Common_Cancel"),
            defaultButton: ContentDialogButton.Close);

        if (confirm != ContentDialogResult.Primary)
            return;

        PendingErrorText.Visibility = Visibility.Collapsed;
        try
        {
            PendingRips.Delete(_stagingRoot, folder);
        }
        catch (Exception ex)
        {
            PendingErrorText.Text = Loc.Format("AddMusic_PendingDeleteFailed", Faults.Note("rip", "deleting a pending rip", ex).Say());
            PendingErrorText.Visibility = Visibility.Visible;
        }

        await LoadPendingAsync();

        // The last one just went. There is nothing left on this face to be on.
        if (_pending.Count == 0)
            GoTo(Face.Source);
    }

    /// <summary>Formats a size for display, such as "12 MB".</summary>
    private static string Size(long bytes)
    {
        string[] units = ["B", "KB", "MB", "GB"];
        double value = bytes;
        int unit = 0;
        while (value >= 1024 && unit < units.Length - 1) { value /= 1024; unit++; }
        return unit <= 1 ? $"{value:0} {units[unit]}" : $"{value:0.#} {units[unit]}";
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────

    private void Import_Click(object sender, RoutedEventArgs e)
    {
        if (_face != Face.Pending || PendingList.SelectedItem is not PendingRipRow row)
            return;

        // A rip is already a set of tracks on the same volume as the library. It goes into the import
        // queue, the same as a picked folder. A vinyl recording that is still one long piece of audio
        // must be split first, and the caller opens the split step. Once the recording has been cut,
        // its tracks import like any other folder.
        Choose(row.Kind == PendingKind.Vinyl && row.Stage != PendingVinylStage.TracksCut
            ? AddMusicAction.SplitVinylSession
            : AddMusicAction.Import, row.Folder);
    }

    private void Back_Click(object sender, RoutedEventArgs e) => GoTo(Face.Source);

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    private void GoTo(Face face)
    {
        _face = face;
        RenderFace();
    }

    private void RenderFace()
    {
        SourceFace.Visibility = _face == Face.Source ? Visibility.Visible : Visibility.Collapsed;
        PendingFace.Visibility = _face == Face.Pending ? Visibility.Visible : Visibility.Collapsed;

        // The source screen is a chooser. Every card is its own answer, and there is no earlier screen
        // to go back to. The pending list is the only screen with these buttons.
        bool pending = _face == Face.Pending;
        BackButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;
        ImportButton.Visibility = pending ? Visibility.Visible : Visibility.Collapsed;

        UpdateImportEnabled();
    }

    private void UpdateImportEnabled() =>
        ImportButton.IsEnabled = _face == Face.Pending && PendingList.SelectedItem is not null;
}
