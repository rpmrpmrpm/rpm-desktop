using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Art;
using RPM.Core.Metadata.Providers;
using RPM.Core.Ripping;
using RPM.Desktop.Services;
using RPM.Desktop.Services.Library;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// A line of the rip list. There are two kinds, because a classical listing has two levels: the work, and
/// the movements under it. Every other track on a record is a track with no work over it. The list is
/// then one level deep, and it looks as it always did.
/// </summary>
[RpmBindable]
public abstract partial class RipRow : INotifyPropertyChanged
{
    public event PropertyChangedEventHandler? PropertyChanged;

    protected void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// A work, and the movements that follow it: the parent line of the tree.
///
/// <para>Its tick covers the whole work. A sonata is taken or left as one piece. Ticking three movements
/// one at a time to leave out an encore is not how people use a record. The tick also has a third state.
/// A half-ticked work is a real answer. Drawing it as off would tell the user that their movements are
/// not being ripped when some of them are.</para>
/// </summary>
[RpmBindable]
public sealed partial class RipWorkRow : RipRow
{
    public RipWorkRow(string title, IReadOnlyList<RipTrackRow> movements)
    {
        _title = title;
        Movements = movements;

        // The parent's tick is a reading of its children. It must be read again whenever one of them is
        // clicked. That includes Select all, which changes the tracks directly.
        foreach (var movement in movements)
            movement.PropertyChanged += (_, e) =>
            {
                if (e.PropertyName == nameof(RipTrackRow.IsChecked))
                    OnChanged(nameof(IsChecked));
            };
    }

    public IReadOnlyList<RipTrackRow> Movements { get; }

    /// <summary>
    /// The work's name, which can be edited. A catalogue's spelling is often wrong, so it must be
    /// correctable. The name is the work tag of every movement under it, so correcting it here corrects
    /// all of them. Emptying it removes the work from those movements. That is how a listing that grouped
    /// tracks which are not movements gets undone.
    /// </summary>
    public string Title
    {
        get => _title;
        set
        {
            if (_title == value)
                return;
            _title = value;

            var work = string.IsNullOrWhiteSpace(value) ? null : value;
            foreach (var movement in Movements)
                movement.Work = work;

            OnChanged();
        }
    }

    private string _title;

    /// <summary>All checked, none checked, or some checked. This is the third state.</summary>
    public bool? IsChecked =>
        Movements.All(m => m.IsChecked) ? true
        : Movements.Any(m => m.IsChecked) ? null
        : false;

    /// <summary>Ticks or clears every movement at once. If the work is not fully ticked, every
    /// movement is ticked. A click on a half-ticked work means "all of it", never "none of it".</summary>
    public void Toggle()
    {
        var check = IsChecked != true;
        foreach (var movement in Movements)
            movement.IsChecked = check;
    }

    /// <summary>How long the whole work runs, as the disc reports its movements.</summary>
    public string Duration => RipDiscDialog.Format(
        TimeSpan.FromSeconds(Movements.Sum(m => m.Seconds)));
}

/// <summary>One track on the disc, in the rip list. It is ticked or not, and its title can be edited. So
/// a disc that MusicBrainz does not know can still be named before it is ripped.</summary>
[RpmBindable]
public sealed partial class RipTrackRow(int number, string duration) : RipRow
{
    private bool _isChecked = true;
    private string _title = "";

    public int Number { get; } = number;
    public string Duration { get; } = duration;

    /// <summary>The track's length in seconds. A candidate release's listing is scored against it when
    /// the user looks up a pressing.</summary>
    public double Seconds { get; init; }

    public bool IsChecked
    {
        get => _isChecked;
        set { if (_isChecked == value) return; _isChecked = value; OnChanged(); }
    }

    /// <summary>The name of this row, which the user can edit. On a movement it is the movement alone,
    /// for example "Andante Cantabile". The work it belongs to is shown once above the run, and is not
    /// typed again in every row. <see cref="FullTitle"/> is what the file gets.</summary>
    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnChanged(); OnChanged(nameof(FullTitle)); }
    }

    /// <summary>
    /// The work this row is a movement of, when the picked listing groups it under one. It is null for an
    /// ordinary track, which is most records.
    ///
    /// <para>It is kept apart from the title, because the two are separate tags and separate facts. A
    /// player that understands works shows the heading once and the movements under it. A player that
    /// does not still gets a title that says what the track is.</para>
    /// </summary>
    public string? Work
    {
        get => _work;
        set
        {
            if (_work == value) return;
            _work = value;
            OnChanged();
            OnChanged(nameof(FullTitle));
            OnChanged(nameof(Indent));
            OnChanged(nameof(JoinVisibility));
            OnChanged(nameof(LeaveVisibility));
        }
    }

    /// <summary>A movement sits under its work, not beside it. The indent shows which level of the
    /// listing this line is on. An ordinary track has no indent.</summary>
    public Thickness Indent => new(_work is null ? 0 : 22, 0, 0, 0);

    /// <summary>
    /// Whether this disc is entered as a classical one. When it is, every line gets a grouping handle.
    ///
    /// <para>A catalogue that knows the record has already grouped it. This is for the other case: the
    /// structure is in the booklet and nowhere else, and the only way in is by hand.</para>
    /// </summary>
    public bool Classical
    {
        get => _classical;
        set
        {
            if (_classical == value) return;
            _classical = value;
            OnChanged();
            OnChanged(nameof(JoinVisibility));
            OnChanged(nameof(LeaveVisibility));
        }
    }

    private bool _classical;

    /// <summary>Shown on a line that is part of no work: "Make this a movement of the work
    /// above".</summary>
    public Visibility JoinVisibility => _classical && _work is null
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>Shown on a line that is part of a work: "Take this back out of its work".</summary>
    public Visibility LeaveVisibility => _classical && _work is not null
        ? Visibility.Visible
        : Visibility.Collapsed;

    /// <summary>The full name of the track, for example "Sonata In C Major, KV. 330: Allegretto". This
    /// goes into the title tag and the file name. It is built the same way the catalogues build it, so a
    /// rip and a lookup cannot disagree.</summary>
    public string FullTitle => ReleaseTrackInfo.TitleOf(_work, _title);

    private string? _work;

    /// <summary>
    /// The catalogue's full answer for this track, when a pressing has been picked.
    ///
    /// <para>It is kept as one value, not copied into twenty properties. None of it is edited here,
    /// because the list edits titles and ticks only. The rip reads it when it builds each track's plan.
    /// That is the one moment any of it can reach the file. A CD carries no tags, and nothing looks a
    /// finished rip up again.</para>
    /// </summary>
    public ReleaseTrackInfo? Catalogue
    {
        get => _catalogue;
        set
        {
            _catalogue = value;
            OnChanged();
            OnChanged(nameof(IsExplicit));
        }
    }

    private ReleaseTrackInfo? _catalogue;

    /// <summary>Whether this line shows the E mark. The user sees what will be stamped before the disc is
    /// ripped, not after.</summary>
    public bool IsExplicit => _catalogue?.Advisory == RPM.Core.Models.ContentAdvisory.Explicit;
}

/// <summary>Chooses the template for each of the two levels of the list. One row type cannot hold both. A
/// work has no number and no length of its own to print, and a movement has no children.</summary>
public sealed partial class RipRowTemplateSelector : DataTemplateSelector
{
    public DataTemplate? Work { get; set; }
    public DataTemplate? Track { get; set; }

    protected override DataTemplate? SelectTemplateCore(object item)
        => item is RipWorkRow ? Work : Track;

    protected override DataTemplate? SelectTemplateCore(object item, DependencyObject container)
        => SelectTemplateCore(item);
}

/// <summary>One track that has finished ripping. It is shown under the progress bar while the rest of the
/// disc runs. It points at the staging file, so its play button previews the temporary file in place.
/// Nothing joins the RPaMp playlist, and nothing is moved or deleted. While this row is the one playing,
/// the button is a stop button.</summary>
[RpmBindable]
public sealed partial class RippedTrackRow(int number, string title, string duration, string filePath, double seconds)
    : INotifyPropertyChanged
{
    private bool _isPlaying;
    private int _repairedBlocks;

    public int Number { get; } = number;
    public string Title { get; } = title;
    public string Duration { get; } = duration;

    /// <summary>The staging file to play, and its length. It is passed straight to the shared player. It
    /// is empty on a skipped row, because a skipped track has no file.</summary>
    public string FilePath { get; } = filePath;
    public double Seconds { get; } = seconds;

    /// <summary>Why this track is not here, or <see cref="RipTrackResult.Ripped"/> for one that
    /// is.</summary>
    public RipTrackResult Skip { get; init; } = RipTrackResult.Ripped;

    public bool IsSkipped => Skip != RipTrackResult.Ripped;

    /// <summary>The number of blocks the drive would not read. They were filled with silence. This is set
    /// once the rip ends, so a track that can be played but clicks says so, and does not pass for
    /// clean.</summary>
    public int RepairedBlocks
    {
        get => _repairedBlocks;
        set
        {
            if (_repairedBlocks == value) return;
            _repairedBlocks = value;
            OnChanged();
            OnChanged(nameof(DamageGlyph));
            OnChanged(nameof(DamageVisibility));
            OnChanged(nameof(DamageTooltip));
        }
    }

    /// <summary>A skipped row has nothing to preview, so it shows no play button at all, rather than a
    /// dead one.</summary>
    public Visibility PlayVisibility => IsSkipped ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>The row is greyed out, so a gap in the album reads as a gap at a glance.</summary>
    public double RowOpacity => IsSkipped ? 0.55 : 1;

    /// <summary>The Font Awesome exclamation-triangle, written as an escape. The raw glyph is a
    /// private-use code point that does not survive every paste.</summary>
    public string DamageGlyph => "\uf071";

    public Visibility DamageVisibility =>
        IsSkipped || _repairedBlocks > 0 ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>What the disc did to this track, in one line: why it was skipped, or how much of it was
    /// patched with silence.</summary>
    public string DamageTooltip => Skip switch
    {
        RipTrackResult.SkippedByHand => Loc.Get("Rip_SkippedByHand"),
        RipTrackResult.SkippedUnreadable => Loc.Get("Rip_SkippedUnreadable"),
        RipTrackResult.SkippedStalled => Loc.Get("Rip_SkippedStalled"),
        _ => Loc.Format("Rip_Repaired", _repairedBlocks),
    };

    public bool IsPlaying
    {
        get => _isPlaying;
        set
        {
            if (_isPlaying == value) return;
            _isPlaying = value;
            OnChanged();
            OnChanged(nameof(PlayGlyph));
            OnChanged(nameof(PlayTooltip));
        }
    }

    /// <summary>The Font Awesome stop and play glyphs, written as escapes. The raw glyphs are private-use
    /// code points that do not survive every paste.</summary>
    public string PlayGlyph => _isPlaying ? "\uf04d" : "\uf04b";

    public string PlayTooltip => Loc.Get(_isPlaying ? "Split_StopTooltip" : "Split_PreviewTooltip");

    public event PropertyChangedEventHandler? PropertyChanged;

    private void OnChanged([CallerMemberName] string? name = null) =>
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>Which kind of disc is in the drive. The window is shared. A track list is a track list,
/// whether its rows came from a CD's table of contents or a DVD's chapter marks. This decides what the
/// window is called, which fields it shows, and how the disc is read.</summary>
public enum DiscKind
{
    Cd,
    Dvd,
}

/// <summary>
/// Imports a disc. It reads what is in the drive, lets the user correct the names, and rips the disc into
/// a staging folder that the import queue picks up.
///
/// <para>It is its own window, not a step inside Add music, and it is named for the disc it rips.
/// Recording a record works the same way. Add music is a chooser. What happens after the choice belongs
/// to whatever was chosen.</para>
///
/// <para>There are two faces, not two steps: the disc (a form) and the rip (a progress report). The rip
/// itself belongs to <see cref="RipJobService"/> and outlives this window. Closing the window in the
/// middle of a rip leaves the drive spinning, and the deck's chip shows the job.</para>
/// </summary>
public sealed partial class RipDiscDialog : ContentDialog
{
    private enum Face { Disc, Rip }

    private readonly FolderPickerService _folderPicker;
    private readonly RipperService _ripper;
    private readonly CatalogService _catalog;

    /// <summary>The rip itself. It outlives this window. The rip face is a view of it, and never its
    /// owner.</summary>
    private readonly RipJobService _rips;
    private readonly string _stagingRoot;

    /// <summary>The library's default naming template. A rip lands in the queue already arranged in the
    /// folders it will keep, not as a flat pile.</summary>
    private readonly RPM.Core.Import.NamingTemplate _stagingLayout;

    /// <summary>The disc's tracks, in the order they come off the disc. The rip is built from these. The
    /// list on screen is a view of them, never the other way round.</summary>
    private readonly ObservableCollection<RipTrackRow> _tracks = [];

    /// <summary>What the list draws: the same tracks, with a work line inserted above each run of
    /// movements. It is rebuilt from <see cref="_tracks"/> whenever the works change. See
    /// RebuildRows.</summary>
    private readonly ObservableCollection<RipRow> _rows = [];
    private readonly List<DiscDrive> _drives = [];

    private readonly DiscKind _kind;
    private Face _face = Face.Disc;
    private DiscInfo? _disc;
    private DvdInfo? _dvd;
    private DvdTitle? _dvdTitle;

    /// <summary>The sleeve for the release the disc resolved to. It is found automatically for a CD, or
    /// it is the release the user picked in the lookup. It is embedded in every ripped track, because a
    /// disc has no cover of its own.</summary>
    private Sleeve? _sleeve;
    private byte[]? _discArt;
    private readonly ArtworkService _artwork = App.Services.GetRequiredService<ArtworkService>();

    /// <summary>The release the user picked in the lookup. It is kept so its disc structure is available.
    /// A multi-disc set fills the disc picker, and the chosen disc's track list names this CD.</summary>
    private ReleaseDetail? _release;

    /// <summary>Which disc of a multi-disc set the disc in the drive is, and how many discs the set has.
    /// It is written to every ripped track. 0 means a single-disc release, so no disc tags are written.
    /// Set from the picker.</summary>
    private uint _discNumber;
    private uint _discCount;
    private string? _discSubtitle;

    /// <summary>Set while the disc picker is being filled, so filling it is not read as the user choosing
    /// a disc.</summary>
    private bool _settingDisc;

    private CancellationTokenSource? _lookupCts;

    /// <summary>True while the rip shown in this window is running. It is read from the job and never
    /// kept here, so a window opened halfway through a rip is as correct as the one that started
    /// it.</summary>
    private bool Busy => _rips.IsRunning;

    /// <summary>Set once this window has a rip to watch. That tells a job ending apart from the idle
    /// state it started in. An idle job with nothing to import is a rip that was cancelled.</summary>
    private bool _started;

    public RipDiscDialog(DiscKind kind, FolderPickerService folderPicker, RipperService ripper,
        CatalogService catalog, RipJobService rips, string stagingRoot,
        RPM.Core.Import.NamingTemplate stagingLayout)
    {
        _kind = kind;
        _folderPicker = folderPicker;
        _ripper = ripper;
        _catalog = catalog;
        _rips = rips;
        _stagingRoot = stagingRoot;
        _stagingLayout = stagingLayout;

        InitializeComponent();

        // The window shows the disc type it is importing: a compact disc for a CD, and a film for a DVD.
        bool isCd = kind == DiscKind.Cd;
        Shell.Title = Loc.Get(isCd ? "Rip_TitleCd" : "Rip_TitleDvd");
        Shell.TitleGlyph = isCd ? "\uf51f" : "\uf008";

        Shell.AttachResize(this, minWidth: 760, minHeight: 440, maxWidth: 1140, defaultHeight: 600,
            defaultWidth: 900);

        // The search the button will actually run is named here, not in the XAML. Discogs joins the rpm
        // database only after the user has connected an account. A tooltip that promised both would
        // describe a search that does not happen.
        IconName.Set(LookupButton,
            Loc.Format("AddMusic_LookUpTooltip", CatalogSources.Describe(catalog)));

        TrackList.ItemsSource = _rows;
        // The rows belong to the job, not to this window. The deck's chip lists the same rows, and they
        // survive the window being closed in the middle of the disc.
        RippedList.ItemsSource = _rips.Tracks;

        // A DVD's audio is a decoded file with the drive's own error correction behind it. A second read
        // has nothing to catch. Verification is a CD-only check.
        VerifyCheck.Visibility = isCd ? Visibility.Visible : Visibility.Collapsed;
        VerifyHint.Visibility = isCd ? Visibility.Visible : Visibility.Collapsed;
        TracksKicker.Text = Loc.Get(isCd ? "AddMusic_TracksKicker" : "AddMusic_ChaptersKicker");

        _rips.Changed += OnRipChanged;
        _rips.Finished += OnRipFinished;

        // This goes through the host, not through Loaded and Closed directly. This window opens the
        // release lookup over itself. On WinUI that hides this window and shows it again afterwards. A
        // plain Closed handler would drop the rip subscriptions while the lookup is open, and never take
        // them back. See DialogHost.
        this.WhenFirstLoaded(OnLoaded);
        this.WhenFinished((_, _) =>
        {
            _lookupCts?.Cancel();
            _rips.Changed -= OnRipChanged;
            _rips.Finished -= OnRipFinished;
            // A preview that is playing is not stopped here. The track the user is auditioning is allowed
            // to play out. The import is a move on the same volume, so the open file handle follows the
            // file and the sound carries on. The paths that DO destroy the staging files, cancel and
            // failure, stop the preview themselves.
        });
    }

    /// <summary>Imports a disc. Nothing is returned. A finished rip hands its own staging folder to the
    /// import queue (see <see cref="RipJobService.Finished"/>). A rip left running carries on without
    /// this window.</summary>
    public static async Task ShowAsync(XamlRoot xamlRoot, DiscKind kind,
        FolderPickerService folderPicker, RipperService ripper, CatalogService catalog, RipJobService rips,
        string stagingRoot, RPM.Core.Import.NamingTemplate stagingLayout)
    {
        var dialog = new RipDiscDialog(kind, folderPicker, ripper, catalog, rips, stagingRoot, stagingLayout)
            { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
    }

    /// <summary>
    /// The same window, opened from anywhere in the app. It resolves what <see cref="ShowAsync"/> is
    /// given, including where a rip stages.
    ///
    /// <para>Rips stage inside the library's own .rpm folder. That is on the same volume as the
    /// destination, so importing a ripped disc is a move and not a copy across devices. It is also why
    /// nothing can be opened before a library exists.</para>
    /// </summary>
    public static async Task ShowForAsync(XamlRoot? xamlRoot, DiscKind kind)
    {
        var settings = App.Services.GetRequiredService<SettingsService>();
        if (xamlRoot is null || settings.LibraryPath is not { } libraryPath)
            return;

        var staging = Path.Combine(libraryPath, ".rpm");
        Directory.CreateDirectory(staging);

        await ShowAsync(xamlRoot, kind,
            App.Services.GetRequiredService<FolderPickerService>(),
            App.Services.GetRequiredService<RipperService>(),
            App.Services.GetRequiredService<CatalogService>(),
            App.Services.GetRequiredService<RipJobService>(),
            staging,
            settings.DefaultTemplate);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        // A rip that is already running owns both the drive and this window. Opening the window in the
        // middle of a rip goes straight to the rip face, and does not offer a disc that cannot be read. A
        // rip that ended early does not count. Its tally is on the deck's chip, and the drive is free for
        // the next disc.
        if (_rips.State is RipJobState.Running or RipJobState.Failed)
        {
            _started = true;
            GoTo(Face.Rip);
            OnRipChanged();
            return;
        }

        RenderFace();
        await ScanDrivesAsync();
    }

    // ── Navigation ───────────────────────────────────────────────────────────────────────────

    private void Rip_Click(object sender, RoutedEventArgs e)
    {
        switch (_face)
        {
            case Face.Disc:
                GoTo(Face.Rip);
                StartRip();
                break;

            // Minimise. The rip belongs to the app, not to this window. Closing the window only puts the
            // job on the deck's chip. The drive keeps spinning and the tracks keep landing.
            case Face.Rip when Busy:
                Hide();
                break;
        }
    }

    private void Back_Click(object sender, RoutedEventArgs e)
    {
        if (Busy)
            return;
        GoTo(Face.Disc);
    }

    private void Cancel_Click(object sender, RoutedEventArgs e)
    {
        // While a rip is running, this is the destructive ending. Everything the rip made is deleted,
        // which is what the label says. Keeping the tracks is Stop. Leaving the rip running is the green
        // button. Pressing this once the drive is idle closes the window.
        if (Busy)
        {
            _rips.Cancel();
            return;
        }

        Hide();
    }

    private void GoTo(Face face)
    {
        _face = face;
        RenderFace();
    }

    private void RenderFace()
    {
        DiscFace.Visibility = _face == Face.Disc ? Visibility.Visible : Visibility.Collapsed;
        RipFace.Visibility = _face == Face.Rip ? Visibility.Visible : Visibility.Collapsed;

        // Back only applies on the rip face, and only once the drive has stopped. It also needs a disc of
        // this window's own to go back to. A window opened onto someone else's rip has no form behind it.
        bool canGoBack = _face == Face.Rip && !Busy && (_disc is not null || _dvd is not null);
        BackButton.Visibility = canGoBack ? Visibility.Visible : Visibility.Collapsed;

        // On the rip face the green button stops meaning "go". It means "leave it running", which is the
        // only thing left to do there.
        RipButton.Visibility = _face == Face.Disc || Busy ? Visibility.Visible : Visibility.Collapsed;
        RipButtonText.Text = Loc.Get(_face == Face.Rip ? "AddMusic_RipBackground" : "AddMusic_Rip");
        // A compact disc icon starts a rip. A minimise icon leaves a running one on the deck's chip.
        RipButtonIcon.Glyph = _face == Face.Rip ? "\uf2d1" : "\uf51f";

        // Cancel closes the window everywhere except mid-rip. Mid-rip, it is the ending that throws away
        // the ripped files, so it shows a bin icon. Stop sits right beside it and keeps them.
        CancelButtonText.Text = Loc.Get(Busy ? "Rip_CancelRip" : "Common_Cancel");
        CancelButtonIcon.Glyph = Busy ? "\uf1f8" : "\uf00d";
        ToolTipService.SetToolTip(CancelButton, Busy ? Loc.Get("Rip_CancelTooltip") : null);

        UpdateRipEnabled();
    }

    private void UpdateRipEnabled()
    {
        if (_face == Face.Rip)
        {
            RipButton.IsEnabled = Busy;
            return;
        }

        var haveDisc = _kind == DiscKind.Dvd ? _dvdTitle is not null : _disc is not null;
        RipButton.IsEnabled = haveDisc && _tracks.Any(t => t.IsChecked);
    }

    /// <summary>The drive whose disc turned out to be CSS-scrambled with no libdvdcss present. The same
    /// disc can be read again as soon as one is installed.</summary>
    private DiscDrive? _pendingCssDrive;

    /// <summary>
    /// Takes a libdvdcss the user has downloaded, installs it, and reads the disc again.
    ///
    /// <para>What is reported is a COMPATIBILITY check, not a safety check. The wording keeps that clear.
    /// rpm can tell that a file is a 64-bit library that exports the entry points it calls. That catches
    /// the mistake people actually make, which is the 32-bit build. That build cannot load at all. It cannot
    /// tell whether the binary is trustworthy. Nothing signs these files, and there is no official
    /// Windows build to compare against.</para>
    /// </summary>
    private async void ChooseLibDvdCss_Click(object sender, RoutedEventArgs e)
    {
        var path = await _folderPicker.PickNativeLibraryAsync();
        if (string.IsNullOrEmpty(path))
            return;

        var result = CssLibraryInstaller.Install(path);
        if (!result.Compatible)
        {
            // The inspector gives its own description. It knows whether the problem is the architecture,
            // a missing entry point, or the wrong file entirely. Each one needs a different next step.
            SetDiscBusy(false, result.Problem ?? Loc.Get("AddMusic_DvdNeedsLibDvdCssWindows"));
            ChooseLibDvdCssButton.Visibility = Visibility.Visible;
            return;
        }

        ChooseLibDvdCssButton.Visibility = Visibility.Collapsed;
        SetDiscBusy(true, Loc.Get("AddMusic_LibDvdCssInstalled"));

        // The user goes straight back to the read they were attempting. Installing the library and
        // ripping the disc is then one step, not two.
        if (_pendingCssDrive is { } drive)
            await ReadDvdAsync(drive);
    }

    /// <summary>What the disc line says when reading a drive or a disc fails. It is the same reading a
    /// failed rip gets (see <see cref="RipJobService.FailureKey"/>). The exception is logged.</summary>
    private static string DiscTroubleKey(Exception ex)
    {
        Faults.Note("rip", "reading the disc", ex);
        return RipJobService.FailureKey(ex);
    }

    private void SetDiscBusy(bool busy, string status)
    {
        DiscBusyBar.IsIndeterminate = busy;
        DiscBusyBar.Visibility = busy ? Visibility.Visible : Visibility.Collapsed;
        DiscStatusText.Text = status;
        DiscStatusText.Visibility = string.IsNullOrEmpty(status) ? Visibility.Collapsed : Visibility.Visible;
        RescanButton.IsEnabled = !busy;
    }

    internal static string Format(TimeSpan duration) => duration.TotalHours >= 1
        ? duration.ToString(@"h\:mm\:ss")
        : duration.ToString(@"m\:ss");
}
