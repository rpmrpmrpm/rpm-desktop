using RPM.Core.Diagnostics;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>One file to look up, as the first step lists it. Nothing here changes while the dialog is
/// open, so it is a plain object.</summary>
[RpmBindable]
public sealed partial class RecognizeTargetRow(string fileName, string currentName, bool needsName)
{
    public string FileName { get; } = fileName;

    /// <summary>The name the file has now. Recognition is about to replace it. It is also the only way to
    /// tell apart two files called <c>track04.mp3</c>.</summary>
    public string CurrentName { get; } = currentName;

    /// <summary>Whether this file is the reason the run is happening.</summary>
    public bool NeedsName { get; } = needsName;

    public string StateNote => Loc.Get(NeedsName ? "Recognize_TargetUnnamed" : "Recognize_TargetNamed");

    public Brush StateBrush => new SolidColorBrush(NeedsName
        ? Windows.UI.Color.FromArgb(0xFF, 0xFF, 0xD4, 0x66)
        : Windows.UI.Color.FromArgb(0x66, 0xFF, 0xFF, 0xFF));
}

/// <summary>The result for one track. It is not fixed at the end of the run. A line that the user
/// corrects by hand is no longer a failure, so it must stop looking like one.</summary>
public enum RecognitionState
{
    /// <summary>A layer found a name.</summary>
    Matched,
    /// <summary>Every layer looked and none of them knew the track. This is not an error.</summary>
    Miss,
    /// <summary>The file could not be looked up at all.</summary>
    Failed,
    /// <summary>The user typed the answer in, here in the report.</summary>
    Manual,
}

/// <summary>
/// One track's result. It can be edited, because the report is editable. A line corrected in the editor
/// must show what it says now, not what the lookup guessed.
/// </summary>
[RpmBindable]
public sealed partial class RecognitionRow(string fileName) : INotifyPropertyChanged
{
    private string _result = "";
    private string _detail = "";
    private string _sourceNote = "";
    private string _scoreNote = "";
    private bool _canEdit;
    private RecognitionState _state;

    public event PropertyChangedEventHandler? PropertyChanged;

    public string FileName { get; } = fileName;

    /// <summary>The queue row this line is about, so Edit has something to open. It is null only in the
    /// gallery, where the outcomes are made up.</summary>
    public TrackRow? Row { get; init; }

    /// <summary>The name now. It is filled in from the answer, then again from the editor.</summary>
    public string Result { get => _result; set => Set(ref _result, value); }

    /// <summary>The second line: the file's name, and its old name when the name has changed.</summary>
    public string Detail { get => _detail; set => Set(ref _detail, value); }

    /// <summary>Off while the run is still going. Editing a row while its neighbours are still being
    /// written would mean editing a list that is still moving.</summary>
    public bool CanEdit { get => _canEdit; set => Set(ref _canEdit, value); }

    /// <summary>Which layer answered, or that a person did.</summary>
    public string SourceNote { get => _sourceNote; set => Set(ref _sourceNote, value); }

    /// <summary>How sure that layer was. Blank when there is no number to show: a miss, a failure, a name
    /// typed by hand, or a layer that does not score its answers.</summary>
    public string ScoreNote { get => _scoreNote; set => Set(ref _scoreNote, value); }

    /// <summary>The state of the line. Setting it repaints the mark. A failure that is named by hand must
    /// not keep the red warning mark.</summary>
    public required RecognitionState State
    {
        get => _state;
        set
        {
            if (!Set(ref _state, value))
                return;
            Raise(nameof(Glyph));
            Raise(nameof(GlyphBrush));
        }
    }

    /// <summary>A warning, a tick, a pencil, or a dash for "looked and found nothing". A dash is not a
    /// failure.</summary>
    public string Glyph => _state switch
    {
        RecognitionState.Failed => "\uf071",
        RecognitionState.Matched => "\uf00c",
        RecognitionState.Manual => "\uf304",
        _ => "\uf068",
    };

    public Brush GlyphBrush => new SolidColorBrush(_state switch
    {
        RecognitionState.Failed => Windows.UI.Color.FromArgb(0xFF, 0xFF, 0x6B, 0x6B),
        RecognitionState.Matched => Windows.UI.Color.FromArgb(0xFF, 0x4C, 0xD9, 0x64),
        // The RPM cyan. A name the user typed is settled, but it is not the catalogue agreeing.
        RecognitionState.Manual => Windows.UI.Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF),
        _ => Windows.UI.Color.FromArgb(0x88, 0xFF, 0xFF, 0xFF),
    });

    private bool Set<T>(ref T field, T value, [CallerMemberName] string? name = null)
    {
        if (Equals(field, value))
            return false;
        field = value;
        Raise(name!);
        return true;
    }

    private void Raise(string name) => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
}

/// <summary>
/// How this window learns whether the account supports rpm. The live window asks the accounts service.
/// The gallery gives a fixed answer. The two versions of this window (the one a supporter sees and the
/// one everybody else sees) must be shown without a real account.
/// </summary>
/// <param name="Read">The account's standing, or null when it could not be read at all.</param>
public sealed record SupportStanding(Func<CancellationToken, Task<SupporterService.Status?>> Read)
{
    /// <summary>What the app uses: the real account, asked over the network.</summary>
    public static SupportStanding FromAccount(SupporterService supporter) => new(supporter.GetAsync);

    /// <summary>A standing that is simply given. It is used only in the gallery. Nothing that can start a
    /// real run should be told what the server would have said.</summary>
    public static SupportStanding Fixed(SupporterService.Status? status) =>
        new(_ => Task.FromResult(status));
}

/// <summary>
/// One window for the whole run: what will be looked up, which layer does it, what each track came back
/// as, and how to correct a result that is wrong.
///
/// <para>
/// The run shows which files it acts on and what each one became. The advanced layer is included with rpm
/// support. It is not a setting in this window. A supporter's run uses it when the basic lookup fails.
/// Other runs say so in one sentence at the foot of the window.
/// </para>
/// </summary>
public sealed partial class RecognizeDialog : ContentDialog
{
    private readonly MainModel _model;
    private readonly SupportStanding _standing;
    private readonly IReadOnlyList<TrackRow> _targets;
    private readonly Func<IReadOnlyList<TrackRow>, ArtAsk, Task<MassEditLookup?>>? _lookup;

    /// <summary>Where a name ends up: the import store, or the files on the shelf. It is passed in, not
    /// assumed, because this window is opened from both lists. See RecognitionSink.</summary>
    private readonly RecognitionSink _sink;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();
    private readonly ObservableCollection<RecognitionRow> _rows = [];
    private readonly ObservableCollection<RecognizeTargetRow> _targetRows = [];

    private enum Phase { Choose, Working, Report }

    private CancellationTokenSource? _cts;
    private Phase _phase = Phase.Choose;
    private SupporterService.Status? _support;

    /// <summary>Set when the report takes over the list. A live outcome still in flight on the dispatcher
    /// would otherwise be added to the finished list as a duplicate.</summary>
    private bool _reported;

    public RecognizeDialog(
        MainModel model, SupportStanding standing, IReadOnlyList<TrackRow> targets,
        Func<IReadOnlyList<TrackRow>, ArtAsk, Task<MassEditLookup?>>? lookup = null,
        RecognitionSink? sink = null)
    {
        _model = model;
        _standing = standing;
        _targets = targets;
        _lookup = lookup;
        _sink = sink ?? model.QueueSink;

        InitializeComponent();
        Shell.AttachResize(this, minWidth: 620, minHeight: 460, maxWidth: 900, defaultHeight: 640);

        ResultList.ItemsSource = _rows;
        TargetList.ItemsSource = _targetRows;
        // This window opens the track details over itself. On WinUI this hides the window and shows it
        // again. The host is used so that the run does not start over when the window comes back.
        this.WhenFirstLoaded(OnLoaded);
    }

    /// <summary>Opens the dialog over an explicit set of rows: the ticked ones, or the one that was
    /// right-clicked. The run acts only on these rows, and the first step lists exactly these.</summary>
    /// <param name="lookup">How the editors opened from the report look up a release catalogue. Null
    /// leaves them without a lookup button. The gallery passes null.</param>
    /// <param name="sink">Where the answers go. When omitted, they go to the import store (see
    /// <see cref="MainModel.QueueSink"/>).</param>
    public static async Task ShowAsync(
        XamlRoot xamlRoot, MainModel model, SupportStanding standing, IReadOnlyList<TrackRow> targets,
        Func<IReadOnlyList<TrackRow>, ArtAsk, Task<MassEditLookup?>>? lookup = null,
        RecognitionSink? sink = null)
    {
        var dialog = new RecognizeDialog(model, standing, targets, lookup, sink) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
    }

    private async void OnLoaded(object sender, RoutedEventArgs e)
    {
        foreach (var row in _targets)
        {
            _targetRows.Add(new RecognizeTargetRow(
                Path.GetFileName(row.FilePath),
                NameOf(row.Artist, row.Title) ?? Loc.Get("Recognize_TargetNoName"),
                row.NeedsRecognition));
        }

        IntroText.Text = Loc.Format("Recognize_Intro", _targets.Count);

        // Two cases where the button does nothing: a run is already going and owns these rows, or no
        // identification backend is set up.
        if (_model.IsRecognizing || !_model.IdentificationAvailable)
        {
            StartButton.IsEnabled = false;
            BusyText.Text = Loc.Get(_model.IsRecognizing ? "Recognize_AlreadyRunning" : "Recognize_Unavailable");
            BusyText.Visibility = Visibility.Visible;
        }

        await RefreshSupportAsync();
    }

    /// <summary>The text "Artist - Title", or whichever half exists, or null when the file says
    /// nothing.</summary>
    private static string? NameOf(string? artist, string? title)
    {
        var a = string.IsNullOrWhiteSpace(artist) ? null : artist.Trim();
        var t = string.IsNullOrWhiteSpace(title) ? null : title.Trim();
        return (a, t) switch
        {
            (null, null) => null,
            (null, _) => t,
            (_, null) => a,
            _ => $"{a} · {t}",
        };
    }

    /// <summary>
    /// Reads the account's standing. This decides everything: a supporter's run uses the advanced layer
    /// when the basic lookup fails, and nobody else's run does. There is no control for it. See
    /// <see cref="RecognitionReach"/> for why the choice is not the user's.
    ///
    /// <para>An unknown standing (signed out, or offline) is not the same as "no". The server makes the
    /// final decision. A guess made here could deny a supporter because of a dropped request. The window
    /// says the standing could not be read and lets the run find out.</para>
    /// </summary>
    private async Task RefreshSupportAsync()
    {
        _support = await _standing.Read(CancellationToken.None);

        SupportText.Text = _support switch
        {
            { IsSupporter: true, Until: { } until } => Loc.Format("Recognize_ReachAdvancedUntil", until.LocalDateTime.ToString("d")),
            { IsSupporter: true } => Loc.Get("Recognize_ReachAdvanced"),
            { IsSupporter: false } => Loc.Get("Recognize_ReachBasic"),
            _ => Loc.Get("Recognize_SupportUnknown"),
        };

        // The mark shows which of the two this is: the advanced layer's own fingerprint, or a lock. The
        // lock is the correct label for a layer this account does not have.
        var locked = _support is { IsSupporter: false };
        ReachIcon.Glyph = locked ? "\uf023" : "\uf5f0";

        // One sentence, shown only when it is true. A supporter never sees it. Neither does a user whose
        // standing the server could not confirm.
        SupportPitch.Visibility = locked && _model.AdvancedLayerAvailable
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>The page where rpm support is bought. It is the same page as the cloud settings page and
    /// the storage-full window use. See <see cref="SupporterLinks"/>.</summary>
    private void Support_Click(object sender, RoutedEventArgs e) => SupporterLinks.OpenBecomeSupporter();

    private async void Start_Click(object sender, RoutedEventArgs e)
    {
        switch (_phase)
        {
            case Phase.Working:
                _cts?.Cancel();   // mid-run, this button is how you stop it
                return;

            case Phase.Report:
                BackToChoice();   // "Recognize more": a second pass is one click, no reopen
                return;
        }

        // The account decides, not a control on this window. See RecognitionReach. It is read from the
        // standing the footer is showing, so a run started after a refresh uses the advanced layer
        // exactly as far as the window says.
        var reach = RecognitionReach.For(_support, _model.AdvancedLayerAvailable);

        _phase = Phase.Working;
        _cts = new CancellationTokenSource();
        _reported = false;
        _rows.Clear();

        ChooseStep.Visibility = Visibility.Collapsed;
        ChooseHead.Visibility = Visibility.Collapsed;
        RunStep.Visibility = Visibility.Visible;
        RunHead.Visibility = Visibility.Visible;
        WorkingBar.Visibility = Visibility.Visible;
        WorkingBar.Value = 0;
        FatalText.Visibility = Visibility.Collapsed;
        AdvancedNoteText.Visibility = Visibility.Collapsed;
        EditAllButton.Visibility = Visibility.Collapsed;
        RunHeadline.Text = Loc.Format("Recognize_RunHeadline", _targets.Count);
        StartButtonText.Text = Loc.Get("Common_Cancel");
        StartButtonIcon.Glyph = "\uf00d";
        CloseButton.IsEnabled = false;

        var progress = new Progress<RecognitionProgress>(p => _dispatcher.TryEnqueue(() =>
        {
            WorkingBar.Value = p.Total == 0 ? 0 : (double)p.Done / p.Total;
            WorkingText.Text = string.IsNullOrEmpty(p.CurrentFile)
                ? ""
                : Loc.Format("Recognize_Working", Math.Min(p.Done + 1, p.Total), p.Total, p.CurrentFile);
        }));

        // Each answer is added to the list as it arrives. The run is something the user watches, not a
        // bar that fills and then shows a full screen of results.
        var live = new Progress<RecognitionOutcome>(o => _dispatcher.TryEnqueue(() =>
        {
            if (!_reported)
                _rows.Add(ToRow(o));
        }));

        RecognitionReport report;
        try
        {
            report = await _model.RunRecognitionAsync(reach, progress, _targets, live, _cts.Token, _sink);
        }
        finally
        {
            _cts?.Dispose();
            _cts = null;
        }

        await ShowReportAsync(report);
    }

    private static RecognitionRow ToRow(RecognitionOutcome o)
    {
        var was = NameOf(o.PreviousArtist, o.PreviousTitle);
        var file = o.FileName;

        return new RecognitionRow(file)
        {
            Row = o.Row,
            Result = o.Error is not null
                ? Loc.Get("RecogResult_Failed")
                : o.Matched
                    ? NameOf(o.Artist, o.Title) ?? Loc.Get("RecogResult_NoMatch")
                    : Loc.Get("RecogResult_NoMatch"),
            // The file's name is always shown. The old name is shown only when the answer changed it,
            // because "was X, is X" says nothing.
            Detail = o.Matched && was is not null && was != NameOf(o.Artist, o.Title)
                ? $"{file} · {Loc.Format("Recognize_Was", was)}"
                : o.Error is { } fault
                    ? $"{file} · {fault.Say()}"
                    : file,
            State = o.Error is not null ? RecognitionState.Failed
                : o.Matched ? RecognitionState.Matched
                : RecognitionState.Miss,
            SourceNote = SourceNote(o),
            ScoreNote = o.Matched && o.Score > 0 ? Loc.Format("Recognize_Score", (int)Math.Round(o.Score * 100)) : "",
        };
    }

    private async Task ShowReportAsync(RecognitionReport report)
    {
        // The report is rebuilt from the run's results, not left as the live list found it. The live rows
        // arrive in two steps (Progress<T>, then the dispatcher). The last one or two arrive after the
        // run has returned, so the list is a preview and the report is the record. A count taken from
        // the preview could be too low, for example "edit all 6" for seven tracks.
        _reported = true;
        _rows.Clear();
        foreach (var outcome in report.Outcomes)
            _rows.Add(ToRow(outcome));

        RunHeadline.Text = Loc.Format("RecogResult_Recognized", report.Recognized, report.Attempted);
        WorkingText.Text = "";
        WorkingBar.Visibility = Visibility.Collapsed;

        // Two different things can want this line: the run stopped, or the run carried on with less than
        // it started with (the advanced layer was taken away during the run). A run that has both
        // problems shows both.
        var trouble = string.Join("\n", new[] { report.Fatal, report.Note }.Where(t => !string.IsNullOrEmpty(t)));
        FatalText.Text = trouble;
        FatalText.Visibility = trouble.Length == 0 ? Visibility.Collapsed : Visibility.Visible;

        // The AudD note is shown only when AudD was used in the run, with the counts of what happened to
        // the tracks sent to it.
        AdvancedNoteText.Text = Loc.Format("Recognize_AdvancedNote", report.SentToAudD, report.IdentifiedByAudD);
        AdvancedNoteText.Visibility = report.SentToAudD > 0 ? Visibility.Visible : Visibility.Collapsed;

        foreach (var row in _rows)
            row.CanEdit = row.Row is not null;

        // Correcting an album's worth of names in one window, when there is more than one to correct.
        var editable = EditableRows();
        EditAllButtonText.Text = Loc.Format("Recognize_EditAll", editable.Count);
        EditAllButton.Visibility = editable.Count > 1 ? Visibility.Visible : Visibility.Collapsed;

        _phase = Phase.Report;
        StartButtonText.Text = Loc.Get("Recognize_Again");
        StartButtonIcon.Glyph = "\uf577";
        CloseButton.IsEnabled = true;

        // A run can show that support has lapsed. The server reports it during the run. The footer must
        // agree with the report by the time the report is shown.
        await RefreshSupportAsync();
    }

    /// <summary>Which layer answered. A miss shows nothing, because there is no layer to name.</summary>
    private static string SourceNote(RecognitionOutcome o)
    {
        if (o.Error is not null || !o.Matched)
            return "";

        return o.Source switch
        {
            "own-db" => Loc.Get("RecogResult_SourceOwnDb"),
            "acoustid" => Loc.Get("RecogResult_SourceAcoustId"),
            "audd" => Loc.Get("RecogResult_SourceAudD"),
            _ => "",
        };
    }

    // ── correcting what came back ─────────────────────────────────────────────────────────────────

    /// <summary>The rows a report line can hand to an editor: everything that was actually looked at,
    /// named or not. A track the lookup could not name is the one most likely to need typing in by hand,
    /// so it is not left out.</summary>
    private List<TrackRow> EditableRows() => [.. _rows.Select(r => r.Row).OfType<TrackRow>()];

    /// <summary>Edit for one line. It opens the app's ordinary track editor over the row that recognition
    /// wrote to. Whatever the user leaves in it is what the queue keeps. So a wrong answer is corrected
    /// here, not imported and repaired in the library later.</summary>
    private async void EditRow_Click(object sender, RoutedEventArgs e)
    {
        if (sender is not FrameworkElement { DataContext: RecognitionRow line } || line.Row is not { } row)
            return;
        if (XamlRoot is null)
            return;

        // Null means the window was closed, not that an edit was made. Writing the row back anyway would
        // mark a file the user had walked away from as checked, and relabel its line as if a decision had
        // been made.
        if (await TrackDetailsDialog.ShowAsync(
                XamlRoot, row, _lookup is null ? null : wantArt => _lookup([row], wantArt)) is null)
            return;

        // A name typed by hand is the user vouching for it. That is what makes a row importable.
        row.MarkManual();
        await _sink.Write([row]);
        Settle(line, row);
    }

    /// <summary>Puts a hand-edited row's own values back on its line: its current name, its file, and a
    /// mark that says a person settled it rather than a catalogue.</summary>
    private static void Settle(RecognitionRow line, TrackRow row)
    {
        var named = NameOf(row.Artist, row.Title);
        line.Result = named ?? Loc.Get("RecogResult_NoMatch");
        line.Detail = line.FileName;
        line.SourceNote = named is null ? "" : Loc.Get("Recognize_SourceManual");
        line.ScoreNote = "";
        line.State = named is null ? RecognitionState.Miss : RecognitionState.Manual;
    }

    /// <summary>Edit for every looked-at row in one window. It is the mass edit that the queue's own menu
    /// opens. It is reached from the report, so a batch that all came back wrong for the same record can
    /// be corrected once.</summary>
    private async void EditAll_Click(object sender, RoutedEventArgs e)
    {
        if (XamlRoot is null || EditableRows() is not { Count: > 0 } rows)
            return;

        if (!await MassEdit.EditQueueRowsAsync(
                XamlRoot, rows, _lookup is null ? null : wantArt => _lookup(rows, wantArt)))
            return;

        foreach (var row in rows)
            row.MarkManual();
        await _sink.Write(rows);

        foreach (var line in _rows)
        {
            if (line.Row is { } row)
                Settle(line, row);
        }
    }

    private void Close_Click(object sender, RoutedEventArgs e)
    {
        _cts?.Cancel();
        Hide();
    }

    /// <summary>Goes back to the list, so a second pass over the names the first pass could not find is
    /// one click.</summary>
    private void BackToChoice()
    {
        _phase = Phase.Choose;
        RunStep.Visibility = Visibility.Collapsed;
        RunHead.Visibility = Visibility.Collapsed;
        ChooseStep.Visibility = Visibility.Visible;
        ChooseHead.Visibility = Visibility.Visible;
        EditAllButton.Visibility = Visibility.Collapsed;
        StartButtonText.Text = Loc.Get("Recognize_Start");
        StartButtonIcon.Glyph = "\uf577";

        // The name of each file may have changed during the run that just finished.
        for (var i = 0; i < _targets.Count && i < _targetRows.Count; i++)
        {
            var row = _targets[i];
            _targetRows[i] = new RecognizeTargetRow(
                Path.GetFileName(row.FilePath),
                NameOf(row.Artist, row.Title) ?? Loc.Get("Recognize_TargetNoName"),
                row.NeedsRecognition);
        }
    }
}
