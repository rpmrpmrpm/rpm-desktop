using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Globalization;
using System.Runtime.CompilerServices;
using RPM.Core.Metadata.Providers;
using RPM.Core.Models;
using RPM.Core.Recording;
using RPM.Core.Ripping;
using RPM.Core.Splitting;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// One row of the track grid: a view of an <see cref="EditorTrack"/>, and nothing else.
///
/// <para>It holds no state of its own. <see cref="Sync"/> writes the model's values in. Typing in it
/// or ticking it raises <see cref="Edited"/>, and the dialog puts that change into the model, which
/// then calls <see cref="Sync"/> again. So a row cannot disagree with the track it shows.</para>
/// </summary>
[RpmBindable]
public sealed partial class SplitTrackVm : INotifyPropertyChanged
{
    /// <summary>The track this row shows. The row is matched by track, not by position. It stays with
    /// its track when tracks above it are removed, joined or split.</summary>
    public int Id { get; }

    public SplitTrackVm(int id) => Id = id;

    private uint _number;
    private string _title = "";
    private string _artist = "";
    private string _startText = "";
    private string _endText = "";
    private string _albumHintText = "";
    private bool _isPlaying;
    private bool _isSelected;
    private bool _isCurrent;
    private BoundarySource _source;
    private GridLength _albumColWidth = new(0);

    /// <summary>True while the dialog writes the model's values in. These writes are not user edits,
    /// so they are not sent back to the model.</summary>
    private bool _syncing;

    /// <summary>The user changed something in this row: a title, an artist or the tick.</summary>
    public event Action<SplitTrackVm>? Edited;

    /// <summary>Writes one track's state into the row. A property that has not changed raises no
    /// event, so the call is cheap. It runs for every row on every frame of a marker drag.</summary>
    public void Sync(EditorTrack track, uint number, bool current, bool playing, string albumHint,
        GridLength albumColWidth)
    {
        ArgumentNullException.ThrowIfNull(track);
        _syncing = true;
        try
        {
            Number = number;
            Title = track.Title;
            Artist = track.Artist;
            Source = track.Source;
            IsSelected = track.Ticked;
            IsCurrent = current;
            IsPlaying = playing;
            AlbumHintText = albumHint;
            AlbumColWidth = albumColWidth;
            SetTimes(track.Start, track.End);
        }
        finally
        {
            _syncing = false;
        }
    }

    private void RaiseEdited()
    {
        if (!_syncing)
            Edited?.Invoke(this);
    }

    public uint Number
    {
        get => _number;
        set { if (_number == value) return; _number = value; OnChanged(); OnChanged(nameof(NumberText)); }
    }

    public string NumberText => Number.ToString("00");

    public BoundarySource Source
    {
        get => _source;
        set { if (_source == value) return; _source = value; OnChanged(nameof(SourceBrush)); OnChanged(nameof(SourceTooltip)); }
    }

    /// <summary>Ticked when the track should be identified from its audio.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set { if (_isSelected == value) return; _isSelected = value; OnChanged(); RaiseEdited(); }
    }

    /// <summary>The track being worked on. The stretch highlighted in the waveform is this row, and
    /// the split and combine tools act on this row.</summary>
    public bool IsCurrent
    {
        get => _isCurrent;
        set { if (_isCurrent == value) return; _isCurrent = value; OnChanged(); OnChanged(nameof(RowBrush)); }
    }

    /// <summary>The background tint of the current row. It uses the same cyan as the waveform uses for the
    /// current track, so the current track has one colour in both views. The recording itself is drawn
    /// in azure, and a different colour is needed here so the highlight stays visible on it.</summary>
    public Brush RowBrush => new SolidColorBrush(_isCurrent
        ? Color.FromArgb(0x2A, 0x04, 0xC8, 0xFF)
        : Color.FromArgb(0x00, 0x00, 0x00, 0x00));

    public string Title
    {
        get => _title;
        set { if (_title == value) return; _title = value; OnChanged(); RaiseEdited(); }
    }

    public string Artist
    {
        get => _artist;
        set { if (_artist == value) return; _artist = value; OnChanged(); RaiseEdited(); }
    }

    /// <summary>Start and End as editable "m:ss" text. Typing does not move anything. The dialog
    /// commits the text when the box loses focus or Enter is pressed. Then it moves that one
    /// edge.</summary>
    public string StartText
    {
        get => _startText;
        set { if (_startText == value) return; _startText = value; OnChanged(); }
    }

    public string EndText
    {
        get => _endText;
        set { if (_endText == value) return; _endText = value; OnChanged(); }
    }

    /// <summary>The length of this track on the adopted release, if there is one. It is shown to
    /// compare with a detected vinyl side, and the apply-timing button snaps the row to it.</summary>
    public string AlbumHintText
    {
        get => _albumHintText;
        set
        {
            if (_albumHintText == value) return;
            _albumHintText = value;
            OnChanged();
            OnChanged(nameof(HasAlbumHint));
            OnChanged(nameof(ApplyHintVisibility));
        }
    }

    public bool HasAlbumHint => _albumHintText.Length > 0;
    public Visibility ApplyHintVisibility => HasAlbumHint ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>Width of the album column for this row. The dialog sets the same value on every row: a
    /// real width when a release with track times is adopted, zero otherwise, so the column
    /// disappears instead of staying empty. It is set per row because a DataTemplate's
    /// ColumnDefinition cannot bind to anything except its item.</summary>
    public GridLength AlbumColWidth
    {
        get => _albumColWidth;
        set { if (_albumColWidth.Equals(value)) return; _albumColWidth = value; OnChanged(); }
    }

    /// <summary>True when this row is the one playing. Its button shows stop while it plays, so the
    /// control that started the sound is also the one that stops it.</summary>
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

    /// <summary>Font Awesome glyphs, written as escapes. The raw glyphs are private-use characters that
    /// some editors and pastes lose, and then the button is empty.</summary>
    public string PlayGlyph => _isPlaying ? "\uf04d" : "\uf04b";   // stop / play

    public string PlayTooltip => Loc.Get(_isPlaying ? "Split_StopTooltip" : "Split_PreviewTooltip");

    /// <summary>Sets the canonical times as text. Nothing changes when the text stays the same. This
    /// runs on every frame of a marker drag for every row. Times show whole seconds, so most calls
    /// change nothing.</summary>
    private void SetTimes(double start, double end)
    {
        StartText = Format(start);
        EndText = Format(end);
    }

    /// <summary>A small dot, coloured by where the boundary came from: a cue sheet, silence, the album,
    /// or a hand edit. The waveform draws its markers in the same colours.</summary>
    public Brush SourceBrush => new SolidColorBrush(SourceColor(Source));

    /// <summary>The four boundary colours, all from the RPM palette. The waveform uses the same
    /// colours, so the dot legend under the grid also explains the waveform.</summary>
    public static Color SourceColor(BoundarySource source) => source switch
    {
        // The palette's dark green is meant as a drawer tint behind artwork. On this near-black card a
        // dot of that colour cannot be seen, so a lighter green is used.
        BoundarySource.CueSheet => Color.FromArgb(0xFF, 0x1F, 0x8A, 0x46),
        BoundarySource.Silence => Color.FromArgb(0xFF, 0xEF, 0xAF, 0x12),        // RPM gold
        BoundarySource.DurationAligned => Color.FromArgb(0xFF, 0xD2, 0x31, 0x0E), // RPM vermilion
        _ => Color.FromArgb(0xFF, 0xE6, 0xE6, 0xE6),
    };

    public string SourceTooltip => Source switch
    {
        BoundarySource.CueSheet => Loc.Get("Split_SourceCue"),
        BoundarySource.Silence => Loc.Get("Split_SourceSilence"),
        BoundarySource.DurationAligned => Loc.Get("Split_SourceAligned"),
        _ => Loc.Get("Split_SourceManual"),
    };

    public static string Format(double seconds)
    {
        var t = TimeSpan.FromSeconds(Math.Max(0, seconds));
        return t.TotalHours >= 1 ? $"{(int)t.TotalHours}:{t.Minutes:00}:{t.Seconds:00}" : $"{t.Minutes}:{t.Seconds:00}";
    }

    public static double? Parse(string text)
    {
        var parts = text.Trim().Split(':');
        if (parts.Length is < 2 or > 3) return null;
        double result = 0;
        foreach (var p in parts)
        {
            if (!double.TryParse(p, NumberStyles.Float, CultureInfo.InvariantCulture, out var v) || v < 0)
                return null;
            result = result * 60 + v;
        }
        return result;
    }

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>One entry in the dot legend under the track list: a coloured dot and its meaning. Only
/// the boundary kinds that are in the grid are listed.</summary>
[RpmBindable]
public sealed partial class LegendEntry
{
    public LegendEntry(Brush dot, string label) { Dot = dot; Label = label; }
    public Brush Dot { get; }
    public string Label { get; }
}

/// <summary>
/// What is being split, kept apart from where it came from. An album file in the import queue and a
/// vinyl recording in staging are the same job: one long file, cut into tracks. So they share this
/// dialog.
/// </summary>
/// <param name="FilePath">The file to cut.</param>
/// <param name="Current">Its <see cref="TrackInfo"/>, for tag fallbacks on the cut tracks.</param>
/// <param name="Candidate">Pre-detected album-file hints (cue sheet), when the caller has them.</param>
/// <param name="Vinyl">Set when the file is a recording of a record, not an album file. Vinyl carries
/// three things at once, so it is one value and not three flags: the output format must be chosen
/// (a recording has no codec yet), the needle noise at the edge of each side must be trimmed, and no
/// track may cross a side change.</param>
public sealed record SplitSubject(
    string FilePath,
    TrackInfo Current,
    string? Album = null,
    string? AlbumArtist = null,
    AlbumFileCandidate? Candidate = null,
    VinylContext? Vinyl = null);

/// <summary>What makes a split a vinyl split.</summary>
/// <param name="SideJoinSeconds">Timeline offsets where one recorded side gives way to the
/// next.</param>
public sealed record VinylContext(IReadOnlyList<double> SideJoinSeconds);

/// <summary> Review before cutting, for an album that is in one file. The dialog finds the boundaries
/// offline (from a cue sheet, otherwise from silence). It can then look up the album and its tracks
/// online, lets the user edit everything, and cuts the file. The original file is kept in `Originals/`.
///
/// <para>Used for an album file in the import queue, and for a recorded vinyl session (see <see
/// cref="SplitSubject"/>). The vinyl case adds a format picker and boundaries at side flips.</para>
///
/// <para><b>How this screen is built.</b> All the editable state lives in one value, <see
/// cref="SplitEditorState"/>: the tracks, the cursor, the selection, the track being worked on, and the
/// track that is playing. Each gesture (a dragged marker, a pressed tool, a typed name, a key) makes a
/// NEW state. <see cref="Apply"/> takes that state and draws it in one pass. The rows, the waveform and
/// the toolbar only show the state. They are never a source of it, so no sequence of clicks can leave
/// two of them disagreeing. The sound is not part of the state, because a playback device is not a
/// value. The state does record <i>which</i> track is playing, so the row's stop button and the
/// waveform's playhead come from the same decision.</para> </summary>
public sealed partial class SplitAlbumDialog : ContentDialog
{
    private readonly SplitSubject _subject;
    private readonly AlbumSplitAnalyzer _analyzer;
    private readonly PlaybackService _playback;
    private readonly CatalogService _catalog;

    /// <summary>The library's default naming template for a vinyl session, so the cut tracks are filed
    /// into folders in the queue. It is null for an in-place album split. That split keeps its files
    /// flat beside the original, because the original is already filed and must not move.</summary>
    private readonly RPM.Core.Import.NamingTemplate? _layout;
    private readonly DispatcherQueue _dispatcher;
    private readonly ObservableCollection<SplitTrackVm> _rows = new();
    private readonly ObservableCollection<LegendEntry> _legend = new();

    /// <summary>The screen as one value. Nothing else here stores the tracks.</summary>
    private SplitEditorState _state = SplitEditorState.Empty;

    private SplitAnalysis? _analysis;

    /// <summary>The adopted release's track lengths, in track order. They are shown beside each row and
    /// used by the apply-timing button. Null until a release is adopted.</summary>
    private IReadOnlyList<double>? _albumLengths;

    /// <summary>The release the user picked, kept for its cover art. A vinyl recording has no tags, so
    /// the chosen release is the only source of the album's cover.</summary>
    private ReleaseSearchResult? _chosen;
    private bool _busy;
    private CancellationTokenSource? _cts;
    private CancellationTokenSource? _adoptCts;
    private SplitExecution? _result;

    /// <summary>Moves the playhead across the waveform while a preview plays. 60 ms moves it by less
    /// than a pixel at any useful zoom. The canvas only redraws when the playhead moves.</summary>
    private readonly DispatcherTimer _playTimer = new();

    public SplitAlbumDialog(SplitSubject subject, AlbumSplitAnalyzer analyzer, PlaybackService playback,
        CatalogService catalog, RPM.Core.Import.NamingTemplate? layout = null)
    {
        InitializeComponent();
        _subject = subject;
        _analyzer = analyzer;
        _playback = playback;
        _catalog = catalog;
        _layout = layout;
        _dispatcher = DispatcherQueue.GetForCurrentThread();
        TrackList.ItemsSource = _rows;
        LegendList.ItemsSource = _legend;
        BuildLegend();
        // The same rule as the rip window's button: name the catalogues that can answer right now.
        ToolTipService.SetToolTip(LookupButton,
            Loc.Format("Split_LookUpTooltip", CatalogSources.Describe(catalog)));
        HookWave();
        ApplySnapLook();
        SyncAlbumColumn();
        KeyDown += OnKeyDown;
        _playTimer.Interval = TimeSpan.FromMilliseconds(60);
        _playTimer.Tick += OnPlayTick;
        // The waveform is the main work area, so the dialog is large. A side squeezed into a strip is
        // hard to work with. The minimum size is what the two panes need at their own minimums, plus
        // the header, the toolbar and the footer. Nothing scrolls in this dialog, so a smaller card
        // would cut off the bottom instead of letting the user reach it.
        // It opens at 860 wide on any screen, because the header, the toolbar and the grid columns
        // are laid out for that width. A dialog stretched to a big monitor only spreads the fields
        // thinner. The grip can still make it wider for a record with long titles.
        Shell.AttachResize(this, minWidth: 860, minHeight: 700, maxWidth: 1600, defaultHeight: 780,
            defaultWidth: 860);

        // A vinyl recording is titled as a recording. An album file in the library is titled as a file.
        bool vinyl = subject.Vinyl is not null;
        Shell.Title = Loc.Get(vinyl ? "Split_TitleRecording" : "Split_Title");
        IntroText.Text = Loc.Get(vinyl ? "Split_IntroRecording" : "Split_Intro");
        // Recognition works per track. The button is only shown when an identification service can
        // answer.
        RecognizeButton.Visibility = _analyzer.CanIdentify ? Visibility.Visible : Visibility.Collapsed;
        FormatPanel.Visibility = subject.Vinyl is not null ? Visibility.Visible : Visibility.Collapsed;
        InitRestore();
        // A track that ends on its own must reset its own button. Otherwise its row keeps offering to
        // stop sound that has already stopped.
        _playback.Changed += OnPlaybackChanged;
        _playback.TrackEnded += OnPlaybackChanged;

        // Subscribe through the host, not through Loaded and Closed directly. This window opens the
        // release lookup over itself. On WinUI that hides this window and shows it again afterwards.
        // If the handlers were hooked directly, detection would run a second time when the window
        // came back, and teardown would stop playback and cancel detection while the lookup is open.
        // See DialogHost.
        this.WhenFirstLoaded(async (_, _) => await DetectAsync());
        this.WhenFinished((_, _) =>
        {
            _playTimer.Stop();
            _playTimer.Tick -= OnPlayTick;
            _playback.Changed -= OnPlaybackChanged;
            _playback.TrackEnded -= OnPlaybackChanged;
            _cts?.Cancel();
            _adoptCts?.Cancel();
            _playback.Stop();
            StopRestoreWork();
        });
    }

    public static async Task<SplitExecution?> ShowAsync(
        XamlRoot xamlRoot, SplitSubject subject, AlbumSplitAnalyzer analyzer, PlaybackService playback,
        CatalogService catalog, RPM.Core.Import.NamingTemplate? layout = null)
    {
        var dialog = new SplitAlbumDialog(subject, analyzer, playback, catalog, layout) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        return dialog._result;
    }

    /// <summary>Splitting an album file already sitting in the import queue.</summary>
    public static Task<SplitExecution?> ShowAsync(
        XamlRoot xamlRoot, TrackRow row, AlbumSplitAnalyzer analyzer, PlaybackService playback,
        CatalogService catalog) =>
        ShowAsync(xamlRoot, new SplitSubject(row.FilePath, row.Current, row.Album, row.AlbumArtist,
            row.SplitCandidate), analyzer, playback, catalog);

    private AlbumFileCandidate Candidate =>
        _subject.Candidate
        ?? AlbumFileDetector.Detect(_subject.Current)
        ?? new AlbumFileCandidate(_subject.FilePath, _subject.Current.DurationSeconds,
            AlbumFileDetector.FindSiblingCue(_subject.FilePath), AlbumFileHint.None);

    private RipFormat? TargetFormat => _subject.Vinyl is not null
        ? (RipFormat)Math.Max(0, FormatCombo.SelectedIndex)
        : null;

    /// <summary>The music on each recorded side, found during analysis. An album file has none of
    /// these: it has no needle noise to trim and no sides to keep tracks inside.</summary>
    private IReadOnlyList<SideRegion> _regions = [];

    /// <summary>
    /// Holds a plan to the music that was actually recorded. Vinyl only, in two passes. The regions
    /// keep each track inside its own side and away from the flip. Then each track is trimmed to its
    /// own sound. A detected boundary sits in the middle of the gap between two tracks, so without
    /// the trim every track would start and end on silence.
    /// </summary>
    private SplitPlan HoldToSides(SplitPlan plan, SplitAnalysis analysis)
    {
        if (_regions.Count == 0)
            return plan;
        var held = SideRegions.Apply(plan, _regions, _analyzer.SilenceOptions.MinTrackSeconds);
        return SideRegions.TrimTracksToMusic(held, analysis.EnvelopeDb, analysis.WindowSeconds,
            _analyzer.SilenceOptions);
    }

    // ── analysis ──

    private async Task DetectAsync()
    {
        if (_busy) return;
        SetBusy(true);
        _cts = new CancellationTokenSource();
        try
        {
            var progress = NewProgress();
            var candidate = Candidate;
            var analysis = await Task.Run(() => _analyzer.AnalyzeOfflineAsync(candidate, progress, _cts.Token));

            // The needle drop, the lead-in groove and the run-out are not music. Find the music on each
            // side, from the envelope the analysis already built, and keep the cut inside it.
            _regions = _subject.Vinyl is { } vinyl
                ? SideRegions.Detect(analysis.EnvelopeDb, analysis.WindowSeconds, analysis.Plan.TotalSeconds,
                    vinyl.SideJoinSeconds, _analyzer.SilenceOptions)
                : [];
            _analysis = analysis with { Plan = HoldToSides(analysis.Plan, analysis) };
            Wave.SetAudio(_analysis.Peaks, _analysis.EnvelopeDb, _analysis.WindowSeconds,
                _analysis.Plan.TotalSeconds);
            LoadPlan(_analysis.Plan);
            SetFinalStatus(Loc.Format("Split_TracksFound", _state.Count));
        }
        catch (OperationCanceledException) { }
        catch (Exception ex)
        {
            SetFinalStatus(Loc.Format("Split_Failed", Faults.Note("split", "splitting the album", ex).Say()));
        }
        finally
        {
            SetBusy(false);
        }
    }

    /// <summary> Progress is reported in two steps. <see cref="Progress{T}"/> posts the callback to the
    /// UI thread, and the callback then queues the text update. So a report raised during analysis can
    /// arrive <i>after</i> the text written when analysis ends. Then "Scanning audio…" would stay on
    /// screen over a finished track list. Each run takes an epoch number. A write from an older run is
    /// dropped, so it cannot have the last word. </summary>
    private int _statusEpoch;

    private IProgress<SplitAnalysisProgress> NewProgress()
    {
        int epoch = ++_statusEpoch;
        return new Progress<SplitAnalysisProgress>(p => _dispatcher.TryEnqueue(() =>
        {
            if (epoch != _statusEpoch)
                return;
            StatusText.Text = p.Phase switch
            {
                "detecting" => Loc.Get("Split_StatusDetecting"),
                "fingerprinting" => Loc.Format("Split_StatusFingerprinting", p.Step + 1, p.Total),
                "lookingup" => Loc.Get("Split_StatusLookingUp"),
                "aligning" => Loc.Get("Split_StatusAligning"),
                _ => p.Phase,
            };
        }));
    }

    /// <summary>The last word on a run. It retires the epoch, so nothing still in flight can overwrite
    /// the text.</summary>
    private void SetFinalStatus(string text)
    {
        _statusEpoch++;
        StatusText.Text = text;
    }

    /// <summary>Takes a new plan (from detection, from a second detection, or a release's names laid
    /// over the boundaries) as the tracks to edit. It also fills the header fields.</summary>
    private void LoadPlan(SplitPlan plan)
    {
        // Anything playing is a preview of a track that is being replaced. Stop it, so the audio does
        // not keep running against boundaries that no longer exist.
        StopPreview();
        Apply(_state.FromPlan(plan));

        if (!string.IsNullOrEmpty(plan.Album)) AlbumBox.Text = plan.Album;
        else if (string.IsNullOrEmpty(AlbumBox.Text) && !string.IsNullOrEmpty(_subject.Album)) AlbumBox.Text = _subject.Album;
        if (!string.IsNullOrEmpty(plan.AlbumArtist)) AlbumArtistBox.Text = plan.AlbumArtist;
        else if (string.IsNullOrEmpty(AlbumArtistBox.Text)) AlbumArtistBox.Text = _subject.AlbumArtist;
        if (plan.Year != 0) YearBox.Text = plan.Year.ToString();
        if (!string.IsNullOrEmpty(plan.Country)) CountryBox.Text = plan.Country;
    }

    // ── the one way anything changes ────────────────────────────────────────────────────────
    //
    // A gesture makes a new state. This takes it and redraws. There is no other path: no handler
    // changes a row, the waveform or the toolbar directly. So what is on screen is always one function
    // of what the state says.

    /// <summary>
    /// Takes the state that an edit produced. A refused edit (null) changes nothing, and it shows a
    /// message when there is one to show. The tools that can refuse are aimed by sight, and a silent
    /// refusal looks like a broken button.
    /// </summary>
    private void Apply(SplitEditorState? next, string? refusedKey = null)
    {
        if (next is null)
        {
            if (refusedKey is not null)
                SetFinalStatus(Loc.Get(refusedKey));
            return;
        }
        if (ReferenceEquals(next, _state))
            return;   // the edit changed nothing (a cursor that did not move, a name retyped the same)

        var previous = _state;
        _state = next;
        Render(previous);
    }

    /// <summary>True while the dialog writes model values into the controls. The change events from
    /// those writes are not the user's own edits.</summary>
    private bool _rendering;

    /// <summary>
    /// Draws the state. The rows and the waveform's track list are only rebuilt when they can have
    /// changed. A state that differs only in the cursor shares its track list with the previous state,
    /// so moving the cursor does not rebuild the rows many times a second.
    /// </summary>
    private void Render(SplitEditorState previous)
    {
        bool tracksChanged = !ReferenceEquals(previous.Tracks, _state.Tracks)
            || previous.CurrentId != _state.CurrentId
            || previous.PlayingId != _state.PlayingId;

        _rendering = true;
        try
        {
            if (tracksChanged)
            {
                SyncRows();
                SyncWaveTracks();
                Wave.SetGuides(_state.AlbumGuides(_albumLengths));
            }
            Wave.CursorSeconds = _state.Cursor;
            Wave.Selection = _state.Selection;
        }
        finally
        {
            _rendering = false;
        }

        UpdateSelectAllState();
        UpdateRecognizeEnabled();
        UpdateWaveReadout();
        UpdateToolState();
        RenderTools();
    }

    /// <summary>Redraws everything from the state it already holds. Used when something changes what a
    /// row shows without changing the tracks, such as the hints from an adopted release.</summary>
    private void RenderAll() => Render(SplitEditorState.Empty);

    /// <summary>
    /// Brings the rows in line with the tracks. Rows are matched by track identity, not by position.
    ///
    /// <para>A row is created for a new track, removed when its track is gone, and otherwise kept and
    /// updated in place. So a row being typed in keeps its track when tracks above it are deleted.
    /// Matching by position would show a different track in each row after a deletion. A recycling
    /// list handles that case badly.</para>
    /// </summary>
    private void SyncRows()
    {
        var tracks = _state.Tracks;
        var width = AlbumColWidth;

        for (int i = 0; i < tracks.Count; i++)
        {
            var track = tracks[i];
            if (i >= _rows.Count || _rows[i].Id != track.Id)
            {
                int found = -1;
                for (int j = i + 1; j < _rows.Count; j++)
                {
                    if (_rows[j].Id != track.Id) continue;
                    found = j;
                    break;
                }
                if (found > 0)
                {
                    // The rows between here and it belong to tracks that are gone.
                    for (int j = found - 1; j >= i; j--)
                        RemoveRow(j);
                }
                else
                {
                    var row = new SplitTrackVm(track.Id);
                    row.Edited += OnRowEdited;
                    _rows.Insert(i, row);
                }
            }
            _rows[i].Sync(track, (uint)(i + 1), track.Id == _state.CurrentId, track.Id == _state.PlayingId,
                AlbumHint(i), width);
        }
        while (_rows.Count > tracks.Count)
            RemoveRow(_rows.Count - 1);
    }

    private void RemoveRow(int index)
    {
        _rows[index].Edited -= OnRowEdited;
        _rows.RemoveAt(index);
    }

    /// <summary>Hands the editor the tracks as they now stand, and keeps its highlight with
    /// them.</summary>
    private void SyncWaveTracks() =>
        Wave.SetTracks(
            _state.Tracks
                .Select((t, i) => new WaveTrack(t.Start, t.End, (uint)(i + 1),
                    string.IsNullOrWhiteSpace(t.Title) ? null : t.Title,
                    Argb(SplitTrackVm.SourceColor(t.Source))))
                .ToList(),
            _state.CurrentIndex);

    /// <summary>Skia takes a colour as one packed number; XAML keeps it as four bytes.</summary>
    private static uint Argb(Color c) => ((uint)c.A << 24) | ((uint)c.R << 16) | ((uint)c.G << 8) | c.B;

    /// <summary>The user typed in a row or ticked it. The change goes into the model quietly. A redraw
    /// here would disturb the typing. Nothing else on screen depends on a name, except the labels on
    /// the waveform.</summary>
    private void OnRowEdited(SplitTrackVm row)
    {
        if (_rendering)
            return;
        _state = _state.Rename(row.Id, row.Title, row.Artist).Tick(row.Id, row.IsSelected);
        SyncWaveTracks();
        UpdateSelectAllState();
        UpdateRecognizeEnabled();
    }

    // ── the album's own track lengths, beside the rows ──

    /// <summary>The album column only earns its width when a release with track times is
    /// adopted.</summary>
    private bool ShowAlbumHints => _albumLengths is { } lengths && lengths.Any(l => l > 0);

    private GridLength AlbumColWidth => new(ShowAlbumHints ? 92 : 0);

    /// <summary>Matches hints to rows by position. A manual insert can shift the mapping, so a hint
    /// that no longer lines up is only a hint, not a guarantee.</summary>
    private string AlbumHint(int index) =>
        _albumLengths is { } lengths && index < lengths.Count && lengths[index] > 0
            ? SplitTrackVm.Format(lengths[index])
            : "";

    private void RefreshAlbumHints()
    {
        SyncAlbumColumn();
        RenderAll();
    }

    /// <summary>The header's album column follows the rows' album column. It is also set at startup.
    /// Without that, the heading would sit over a column the rows do not have.</summary>
    private void SyncAlbumColumn()
    {
        HeaderAlbumCol.Width = AlbumColWidth;
        HeaderAlbumLabel.Visibility = ShowAlbumHints ? Visibility.Visible : Visibility.Collapsed;
    }

    // ── dot legend + select-all ─────────────────────────────────────────────────────────────

    /// <summary>The dot legend is a key. It lists what each colour means, including kinds that have not
    /// yet appeared in the grid. The cue sheet entry only applies to a file with a cue sheet, so a
    /// vinyl recording never lists it.</summary>
    private void BuildLegend()
    {
        _legend.Clear();
        var kinds = _subject.Vinyl is null
            ? new[] { BoundarySource.CueSheet, BoundarySource.Silence, BoundarySource.DurationAligned, BoundarySource.Manual }
            : new[] { BoundarySource.Silence, BoundarySource.DurationAligned, BoundarySource.Manual };
        foreach (var src in kinds)
            _legend.Add(new LegendEntry(LegendBrush(src), Loc.Get(LegendKey(src))));
    }

    private static Brush LegendBrush(BoundarySource src) => new SolidColorBrush(SplitTrackVm.SourceColor(src));

    private static string LegendKey(BoundarySource src) => src switch
    {
        BoundarySource.CueSheet => "Split_LegendCue",
        BoundarySource.Silence => "Split_LegendSilence",
        BoundarySource.DurationAligned => "Split_LegendAlbum",
        _ => "Split_LegendManual",
    };

    private void SelectAll_Click(object sender, RoutedEventArgs e) =>
        Apply(_state.TickAll(SelectAllBox.IsChecked == true));

    /// <summary>Keeps the header checkbox in step: checked only when every row is ticked. It is set in
    /// code, so the change does not fire the click handler that a user tap would run.</summary>
    private void UpdateSelectAllState() => SelectAllBox.IsChecked = _state.AllTicked;

    private void UpdateRecognizeEnabled() =>
        RecognizeButton.IsEnabled = !_busy && _analyzer.CanIdentify && _state.TickedTracks.Count > 0;

    /// <summary>The plan as the screen shows it now: the state's tracks and the header fields, on top
    /// of what the analysis found about the file.</summary>
    private SplitPlan BuildPlan()
    {
        uint.TryParse(YearBox.Text, out var year);
        return _analysis!.Plan with
        {
            Album = Blank(AlbumBox.Text),
            AlbumArtist = Blank(AlbumArtistBox.Text),
            Year = year,
            Country = Blank(CountryBox.Text),
            Tracks = _state.ToPlanTracks(),
        };
    }

    // ── header reflow ──
    //
    // Four fields (album, artist, year, country) and the Look up button do not fit on one line until
    // the window is fairly wide. Squeezing them onto one line leaves the album and artist boxes too
    // narrow to read. So by default the header has two rows: album across the top, the rest below it.
    // It changes back to one line only when there is room for all of it.

    private bool _headerWide;

    private void Header_SizeChanged(object sender, SizeChangedEventArgs e)
    {
        bool wide = e.NewSize.Width >= HeaderOneLineWidth;
        if (wide == _headerWide && e.PreviousSize.Width != 0)
            return;   // already in this arrangement; nothing to move
        _headerWide = wide;
        ApplyHeaderLayout(wide);
    }

    /// <summary>Header width at or above which all four fields sit on one line. Below it the window
    /// is not wide enough to give the album and artist boxes usable room side by side.</summary>
    private const double HeaderOneLineWidth = 1040;

    private void ApplyHeaderLayout(bool wide)
    {
        // Wide: Album | Artist | Year | Country on one row. Narrow: Album across the top, the other
        // three below it. Format is in the footer, next to Split. Year and Country keep their columns
        // (2 and 3) in both layouts. Only their row changes.
        Grid.SetColumnSpan(AlbumPanel, wide ? 1 : 4);

        Grid.SetRow(ArtistPanel, wide ? 0 : 1);
        Grid.SetColumn(ArtistPanel, wide ? 1 : 0);
        Grid.SetColumnSpan(ArtistPanel, wide ? 1 : 2);

        Grid.SetRow(YearPanel, wide ? 0 : 1);
        Grid.SetRow(CountryPanel, wide ? 0 : 1);
    }

}
