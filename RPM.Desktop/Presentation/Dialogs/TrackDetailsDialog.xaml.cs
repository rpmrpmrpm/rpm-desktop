using System.Diagnostics;
using System.Globalization;
using RPM.Core.Analysis;
using RPM.Core.Import;
using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Dialogs.Controls;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>What the window gives back to the caller besides the row it filled in: a copy of the cover
/// that a tag cannot carry, and any field the user wants the whole record to agree on.</summary>
/// <param name="FolderArtBytes">The full-size scan for the album folder's cover image, when the lookup
/// found one bigger than the copy embedded in the file.</param>
/// <param name="ApplyToAlbum">The album-wide fields whose marker the user switched on. Their values are
/// written to every track on the record, not just to this file. Empty is the usual case. A plain edit
/// here changes one file only, which makes this window the tool for repairing a record that was split
/// in two.</param>
public sealed record TrackDetailsResult(
    byte[]? FolderArtBytes = null,
    IReadOnlyList<TagFieldKind>? ApplyToAlbum = null);

/// <summary> Per-file editor for the full set of wanted-side tags, split into tabs: Basic (naming and
/// ordering fields), Credits (people and publishing), Lyrics, and Artwork (view, change, export or
/// remove the embedded front cover). Edits go into the TrackRow only. The file is written on Apply.
///
/// <para>It has the same release lookup as the mass edit. One track is the most common thing to
/// correct, and this was the only editor that could not ask a catalogue. The pressing the user picks
/// fills the boxes and brings the sleeve and the disc label with it.</para> </summary>
public sealed partial class TrackDetailsDialog : ContentDialog
{
    /// <summary> How big the cover square is here.
    ///
    /// <para>It is larger than the album editor's 148, which has to fit two covers side by side in half
    /// a window. This tab has one picture in it, and the cover is what the tab is for. Both editors
    /// build the well from the same class, so the two look the same at both sizes.</para> </summary>
    private const double ArtEdge = 220;

    private readonly PictureWell _artWell = new(ArtEdge);
    private readonly TrackRow _row;
    private readonly Dictionary<TagFieldKind, FieldInput> _byKind = new();
    private readonly Dictionary<TagFieldKind, AlbumMarker> _markers = new();
    private readonly Func<ArtAsk, Task<MassEditLookup?>>? _lookup;

    /// <summary>The other way to answer "what record is this": point at one the library already holds.
    /// It is null where there is no library to point into, such as the gallery or a queue with no
    /// mirror open. The button is then the catalogue search alone, not a menu with one entry.</summary>
    private readonly Func<ArtAsk, Task<MassEditLookup?>>? _pickAlbum;

    /// <summary>The record's other tracks, as rows. An album-scope field is compared against them, so
    /// the marker can say that the rest of the album disagrees. Empty for a queue row, or for a file
    /// with no siblings. In that case there is no marker at all.</summary>
    private readonly IReadOnlyList<TrackRow> _siblings = [];

    private byte[]? _artBytes;      // The art on show: the existing art or a pending new one
    private string _artMime = "image/jpeg";
    private bool _artChanged;
    private bool _artRemoved;
    private bool _saved;
    private byte[]? _folderArtBytes;   // Full-size scan for the album folder, if the lookup found one

    /// <summary>The album folder, when this file has one. It is null for a queue row. A queue row is
    /// not filed yet, so it has no siblings to disagree with it, and no album markers appear.</summary>
    private readonly string? _albumFolder;

    /// <summary>The analysis of this track, when it has been analysed and the reading still describes
    /// the file on disk. Otherwise it is null, and the Sound tab says so instead of showing a column of
    /// dashes.
    ///
    /// <para>The tempo is counted the way the listener counts it, not the way it was measured. The
    /// library applies the stored octave on the way out. It is not readonly, because the octave is one
    /// of the things this window changes.</para></summary>
    private TrackAnalysis? _analysis;

    /// <summary>Where this file sits in the library, as the mirror names it. It is null for a queue
    /// row, and for a file outside the collection. Nothing on this tab can be stored against a null
    /// location.</summary>
    private readonly string? _relativePath;

    /// <summary>How this record is counted against how it was measured: 1 unless somebody has changed
    /// it. See <see cref="TempoOctave"/>.</summary>
    private double _bpmOctave = 1;

    /// <summary>The measured-tempo row, the line under it that offers the other count, and the button
    /// that takes that offer. They are held so that taking the offer redraws the two numbers, not the
    /// whole tab.</summary>
    private TextBlock? _tempoReading;
    private TextBlock? _octaveLine;
    private Button? _octaveButton;

    /// <summary>The library's own row for this file. It gives the two facts that tags cannot carry: how
    /// loud the record is, and how close it comes to full scale. It is null for a file outside the
    /// library.</summary>
    private readonly LibraryTrack? _mirror;

    /// <summary>The copy button on the Sound tab, and the value it would copy. It is null for a track
    /// with no reading to offer, because then there is nothing for the button to copy.</summary>
    private Button? _useMeasured;
    private AnalysisTagWrite _measured = AnalysisTagWrite.Nothing;

    /// <summary> What a release listing can answer. The fields it says nothing about, such as a
    /// comment, a BPM or a remixer, are not shown here and are never changed.
    ///
    /// <para>The credits and the classical block are included, because MusicBrainz does carry them. A
    /// recording's conductor and orchestra are relationships on the recording. Its work, movement and
    /// composer are relationships on the composition it performs. Discogs gives none of these, so those
    /// boxes keep the values the file already had.</para> </summary>
    private static readonly TagFieldKind[] LookupTargets =
    [
        TagFieldKind.Album, TagFieldKind.AlbumArtist, TagFieldKind.Artist,
        TagFieldKind.Year, TagFieldKind.DiscCount, TagFieldKind.Publisher, TagFieldKind.Genre,
        TagFieldKind.Subgenre,

        TagFieldKind.Composers, TagFieldKind.Conductor, TagFieldKind.Ensemble,
        TagFieldKind.Soloists, TagFieldKind.Lyricist, TagFieldKind.Arranger,

        TagFieldKind.Work, TagFieldKind.MovementName, TagFieldKind.Movement,

        TagFieldKind.ArtistSort, TagFieldKind.AlbumArtistSort, TagFieldKind.ComposerSort,

        TagFieldKind.OriginalYear, TagFieldKind.Media,
        TagFieldKind.CatalogNumber, TagFieldKind.Barcode, TagFieldKind.Isrc,

        TagFieldKind.RecordingId, TagFieldKind.ReleaseId, TagFieldKind.ReleaseGroupId,
        TagFieldKind.ArtistId, TagFieldKind.ReleaseArtistId,
    ];

    /// <param name="albumFolder">Where this file lives, when it is already filed. If it is null, the
    /// disc-art half of the artwork tab is hidden. A disc.png belongs to an album folder, and a queue
    /// row has no folder until it is imported.</param>
    public TrackDetailsDialog(
        TrackRow row, Func<ArtAsk, Task<MassEditLookup?>>? lookup = null, string? albumFolder = null,
        Func<ArtAsk, Task<MassEditLookup?>>? pickAlbum = null)
    {
        _row = row;
        _lookup = lookup;
        _pickAlbum = pickAlbum;
        _albumFolder = albumFolder;
        _siblings = LoadSiblings(row, albumFolder);
        InitializeComponent();
        LookupButton.Visibility = lookup is null && pickAlbum is null ? Visibility.Collapsed : Visibility.Visible;
        FileNameText.Text = Path.GetFileName(row.FilePath);
        ToolTipService.SetToolTip(FileNameText, row.FilePath);
        // The floor is set by the tallest tab, not by the shortest one. The three form tabs have their
        // own scrollers. Lyrics and artwork still fill the space, so a window that could be dragged
        // shorter than the artwork tab would clip it. The rail strip is 8 px taller than the pill
        // chips, so the floor is set higher to match. The floor is also set by the tab strip. Six tabs
        // and the lookup button share one line. The words on the tabs are longer in German and French
        // than in English, so a strip that does not fit cuts a tab off the end.
        Shell.AttachResize(this, minWidth: 740, minHeight: 568, maxWidth: 1000, defaultHeight: 628);

        ArtWellHost.Child = _artWell.Root;
        ArtPane.Width = ArtEdge;

        _analysis = LoadAnalysis(row.FilePath);
        _relativePath = RelativePathOf(row.FilePath);
        _bpmOctave = LoadBpmOctave(_relativePath);
        _mirror = LoadMirrorRow(row.FilePath);
        OfferAnalysis();
        BuildBasicTab();
        BuildCreditsTab();
        BuildWorkTab();
        BuildSoundTab();
        LyricsBox.Text = _row.Lyrics;
        AssertEveryFieldPlaced();

        BuildTabs();
        _ = LoadArtAsync();
    }

    /// <summary> Every field in the catalogue is on one of the tabs.
    ///
    /// <para>The mass edit and the album editor build themselves by walking <see
    /// cref="TagFields.All"/>, so a field added to the catalogue appears in both of them. This window
    /// does not work that way. Its tabs are laid out by hand, because the order and the pairing are
    /// design decisions (for example "Track 3 of 12" on one row), and a loop cannot work them out. The
    /// cost is that a field can be registered, reach the other two windows, and write correctly to the
    /// file, yet never appear here. That is how the advisory flag and the release country went
    /// missing.</para>
    ///
    /// <para>So the layout stays hand-written, and the omission is made loud. This check runs in debug
    /// builds only. A missing field is a mistake in this file, and the developer running the debug
    /// build is the one who can fix it.</para> </summary>
    [Conditional("DEBUG")]
    private void AssertEveryFieldPlaced()
    {
        var placed = new HashSet<TagFieldKind>(_byKind.Keys)
        {
            TagFieldKind.Lyrics,   // Has its own tab, not a row in a group
        };

        var missing = TagFields.All
            .Where(f => !placed.Contains(f.Kind))
            .Select(f => f.Kind.ToString())
            .ToList();

        Debug.Assert(missing.Count == 0,
            $"TrackDetailsDialog does not show: {string.Join(", ", missing)}. " +
            "Add each to BuildBasicTab, BuildCreditsTab or BuildWorkTab.");
    }

    /// <summary> Fills the tempo and the key from the app's own analysis, when the file has neither.
    ///
    /// <para>No store sells either of these, but RPM already measures both, because beat-matching needs
    /// them. The app never wrote them anywhere a tag reader could see. So the numbers are offered here,
    /// in the window whose job is to settle a file's tags. They are written only when the user saves.
    /// Filling a box is a suggestion. Rewriting the file behind the user's back would be a decision,
    /// and it would change the modified time that the analysis cache and the library sync both use as a
    /// key.</para>
    ///
    /// <para>Cached analyses only. Opening a window must never decode a file. A track that has not been
    /// analysed yet gets empty boxes.</para> </summary>
    private void OfferAnalysis()
    {
        if (_analysis is not { } analysis)
            return;

        // Only what the analyser is sure of, and only where the file says nothing. This is the same
        // rule the background writer uses, taken from the same place, so the box and the file cannot
        // disagree about what was measured. A tempo that was guessed is worse than no tempo. An empty
        // box says nobody has measured the track. A wrong 87 says the record is 87.
        var wanted = AnalysisTags.Wanted(analysis, ParseBpm(_row.Bpm), _row.MusicalKey);
        if (wanted.Bpm > 0)
            _row.Bpm = wanted.Bpm.ToString(CultureInfo.InvariantCulture);
        if (wanted.Key is { Length: > 0 } key)
            _row.MusicalKey = key;
    }

    private static uint ParseBpm(string? value) =>
        uint.TryParse(value?.Trim(), NumberStyles.Integer, CultureInfo.InvariantCulture, out var n) ? n : 0;

    /// <summary> The stored analysis for this file, or null when the file has never been analysed, when
    /// the reading no longer describes the file, or when there is no library to hold one.
    ///
    /// <para>Cached readings only. Opening a window must never decode a file. A track that has not been
    /// analysed yet gets empty boxes and a Sound tab that says so.</para> </summary>
    private static TrackAnalysis? LoadAnalysis(string filePath)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root || !File.Exists(filePath))
            return null;

        var relative = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        long length, modified;
        try
        {
            var file = new FileInfo(filePath);
            length = file.Length;
            modified = file.LastWriteTimeUtc.Ticks;
        }
        catch (IOException)
        {
            return null;   // The file has gone since the check above, so there is nothing to read.
        }

        // If the question is not answered, the boxes stay empty. A refusal has shown the problem screen
        // over the app.
        return library.Read(library.Db, "a track's stored analysis",
                db => db.GetTrackAnalysis(relative, length, modified, filePath))
            .Match(found => found, _ => null);
    }

    /// <summary>Where a file sits under the library root, in the form the mirror uses for its row keys.
    /// It is null when there is no open library, or when the file is outside it.</summary>
    private static string? RelativePathOf(string filePath)
    {
        try
        {
            var library = App.Services.GetRequiredService<LibraryService>();
            if (!library.IsOpen || library.Root is not { } root)
                return null;

            var relative = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
            return relative.StartsWith("..", StringComparison.Ordinal) ? null : relative;
        }
        catch
        {
            return null;
        }
    }

    /// <summary>How this record is counted, as the mirror has it. It is 1 for anything the mirror does
    /// not hold. That is also the value for every track that nobody has corrected.</summary>
    private static double LoadBpmOctave(string? relativePath)
    {
        if (relativePath is null)
            return 1;

        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen)
            return 1;

        // If the question is not answered, use the uncorrected count. A refusal has shown the problem
        // screen over the app.
        return library.Read(library.Db, "a track's tempo octave", db => db.BpmOctave(relativePath))
            .Match(octave => octave, _ => 1);
    }

    /// <summary>This file's row in the library mirror, for the facts that no tag carries. It is null
    /// for a queue row, and for a file previewed from outside the collection.</summary>
    private static LibraryTrack? LoadMirrorRow(string filePath)
    {
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root)
            return null;

        var relative = Path.GetRelativePath(root, filePath).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return null;

        // If the question is not answered, no mirror facts are shown. A refusal has shown the problem
        // screen over the app.
        return library.Read(library.Db, "a track's row", db => db.FindByRelativePath(relative))
            .Match(row => row, _ => null);
    }

    /// <summary>The other tracks on this file's record, read straight from the mirror. The read is
    /// cheap, because it covers one folder's rows. It is what makes a split album visible. Without
    /// these rows, the window has nothing to compare an album name with, and every field looks equally
    /// fine.</summary>
    private static IReadOnlyList<TrackRow> LoadSiblings(TrackRow row, string? albumFolder)
    {
        if (albumFolder is null)
            return [];
        var library = App.Services.GetRequiredService<LibraryService>();
        if (!library.IsOpen || library.Root is not { } root)
            return [];

        var relative = Path.GetRelativePath(root, albumFolder).Replace(Path.DirectorySeparatorChar, '/');
        if (relative.StartsWith("..", StringComparison.Ordinal))
            return [];

        // If the question is not answered, the markers stay off. A refusal has shown the problem screen
        // over the app.
        return library.Read(library.Db, "a track's album siblings", db => db.FolderTracks(relative))
            .Match<IReadOnlyList<TrackRow>>(
                tracks => [.. tracks
                    .Select(t => t.ToTrackInfo(root))
                    .Where(t => !string.Equals(t.FilePath, row.FilePath, StringComparison.OrdinalIgnoreCase))
                    .Select(t => new TrackRow(t))],
                _ => []);
    }

    // ── tabs ──

    /// <summary> Puts the shared rail strip above the panels and opens on Basic.
    ///
    /// <para>On a track that belongs to a composition, the Work tab comes second, in front of Credits.
    /// The first thing anyone opening a classical file wants to correct is which piece and which
    /// movement it is. On a song that tab is almost always empty. The tabs never change, only their
    /// order. A Beatles track has a Work tab too, because a song can have a composer and no
    /// work.</para> </summary>
    private void BuildTabs()
    {
        var basic = new RailTabStrip.Tab(MetadataTabs.Basic, "Details_TabBasic", "\uf02b");
        var credits = new RailTabStrip.Tab(MetadataTabs.Credits, "Details_TabCredits", "\uf0c0");
        var work = new RailTabStrip.Tab(MetadataTabs.Work, "Details_TabWork", "\uf02d");
        // Sliders: the readings, as against the writing on the sleeve.
        var sound = new RailTabStrip.Tab(MetadataTabs.Sound, "Details_TabSound", "\uf1de");
        var lyrics = new RailTabStrip.Tab(MetadataTabs.Lyrics, "Details_Lyrics", "\uf036");
        var artwork = new RailTabStrip.Tab(MetadataTabs.Artwork, "Details_Art", "\uf03e");

        var strip = IsPartOfAWork
            ? new RailTabStrip(basic, work, credits, sound, lyrics, artwork)
            : new RailTabStrip(basic, credits, work, sound, lyrics, artwork);

        strip.Register(MetadataTabs.Basic, PanelBasic);
        strip.Register(MetadataTabs.Credits, PanelCredits);
        strip.Register(MetadataTabs.Work, PanelWork);
        strip.Register(MetadataTabs.Sound, PanelSound);
        strip.Register(MetadataTabs.Lyrics, PanelLyrics);
        strip.Register(MetadataTabs.Artwork, PanelArt);

        TabStripHost.Child = strip;
        strip.Select(MetadataTabs.Basic);
    }

    /// <summary>Whether this file is a movement of something. This decides the order above. It is read
    /// from the file, not from the album, because this window opens on a track. A track that names a
    /// work is part of one, whatever the record around it looks like.</summary>
    private bool IsPartOfAWork => !string.IsNullOrWhiteSpace(_row.Work);

    // ── fields ──
    //
    // The dense form used by the app (see DenseForm): a group name over a hairline rule, then rows of
    // label and input on a 24 px pitch. The labels are right-aligned in a fixed gutter, so every input
    // in the tab starts on one vertical line. Fields are grouped by what they describe, not in the
    // order they were listed. Numbers that belong together share a row.

    /// <summary>What the file is, next to the record it came from.</summary>
    private void BuildBasicTab()
    {
        var track = new DenseFormGroup();
        AddField(track, TagFieldKind.Title);
        AddField(track, TagFieldKind.Artist);
        AddPair(track, TagFieldKind.TrackNo, TagFieldKind.TrackCount);
        AddPair(track, TagFieldKind.Disc, TagFieldKind.DiscCount);
        // This is on Basic, not with the codes. People look for it on purpose, when they can see the
        // explicit marker on the row and want to correct it.
        AddField(track, TagFieldKind.Advisory);
        // These fields are facts about the file, not credits and not measurements. A code the track
        // carries, the group it is filed under, and a note about it all belong here.
        AddField(track, TagFieldKind.Isrc);
        AddField(track, TagFieldKind.Grouping);
        AddField(track, TagFieldKind.Comment);
        track.AddTo(BasicLeft, TagFields.Groups.Track);

        var release = new DenseFormGroup();
        AddField(release, TagFieldKind.Album);
        AddField(release, TagFieldKind.AlbumArtist);
        AddField(release, TagFieldKind.Year);
        AddField(release, TagFieldKind.Genre);
        AddField(release, TagFieldKind.Subgenre);
        AddField(release, TagFieldKind.ReleaseType);
        AddField(release, TagFieldKind.DiscSubtitle);
        release.AddTo(BasicRight, TagFields.Groups.Release);
    }

    /// <summary>Who made it, and who owns it.</summary>
    private void BuildCreditsTab()
    {
        var credits = new DenseFormGroup();
        AddField(credits, TagFieldKind.Composers);
        AddField(credits, TagFieldKind.Conductor);
        AddField(credits, TagFieldKind.Ensemble);
        AddField(credits, TagFieldKind.Soloists);
        AddField(credits, TagFieldKind.Lyricist);
        AddField(credits, TagFieldKind.Arranger);
        AddField(credits, TagFieldKind.RemixedBy);
        credits.AddTo(CreditsLeft, TagFields.Groups.Credits);

        // This is the second column. Who played on a record and who put it out are two different
        // questions. The tab reads as two answers, not as one long list with a rule through it.
        var publishing = new DenseFormGroup();
        AddField(publishing, TagFieldKind.Publisher);
        AddField(publishing, TagFieldKind.Copyright);
        publishing.AddTo(CreditsRight, TagFields.Groups.Publishing);
    }

    /// <summary> What the app measured: the tempo and the key as tags, with the readings they came from
    /// beside them, and how loud the record is.
    ///
    /// <para>The tempo and the key are tags, but they are not credits and they do not come from a
    /// sleeve. RPM works both out itself, for every track the DJ puts on,
    /// because beat-matching needs them. This tab is where that work is shown and can be
    /// corrected.</para>
    ///
    /// <para>The readings are shown, not only the answers. A tempo of 128 means something different
    /// when the grid is confident and the record is in four than when the analyser was picking between
    /// halves. A key is worth exactly as much as the confidence beside it. Everything below the two
    /// boxes is read-only. It is what was measured, and typing into it would make it untrue.</para>
    /// </summary>
    private void BuildSoundTab()
    {
        var measured = new DenseFormGroup();
        AddField(measured, TagFieldKind.Bpm);
        AddField(measured, TagFieldKind.MusicalKey);
        measured.AddTo(SoundLeft, TagFields.Groups.Measured);

        var reading = new DenseFormGroup();
        if (_analysis is { } analysis)
        {
            // The wheel position goes beside the key. It is what a DJ actually mixes on. It is not a
            // tag: the file carries "Am" and the deck shows "8A".
            //
            // A reading the app will not write on its own is marked on the same line. Without that
            // mark, a track whose key was too close to call showed a key here and an empty box beside
            // it, and nothing on the tab connected the two.
            if (analysis.Key.IsKnown)
            {
                var line = $"{analysis.Key.Camelot}  ·  {analysis.Key.Name}";
                if (analysis.KeyConfidence < AnalysisTags.MinKeyConfidence)
                    line += $"  ·  {Loc.Get("Details_TooCloseToCall")}";
                reading.Add(null, DenseForm.Label("Details_Camelot"), Reading(line, analysis.KeyConfidence));
            }

            if (analysis.Bpm > 0)
            {
                // TempoLine keeps its own confidence, because the row is redrawn when the record is
                // counted the other way, and the confidence changes with it.
                _tempoReading = Reading(TempoLine(analysis));
                reading.Add(null, DenseForm.Label("Details_MeasuredTempo"), _tempoReading);
            }

            if (analysis.BeatTimes.Count > 0)
                reading.Add(null, DenseForm.Label("Details_BeatGrid"),
                    Reading(
                        Loc.Format("Details_BeatGridValue", analysis.BeatsPerBar, analysis.FirstDownbeat + 1),
                        analysis.DownbeatConfidence));

            reading.Add(null, DenseForm.Label("Details_Energy"),
                Reading(analysis.Intensity.ToString("0.00", CultureInfo.CurrentCulture)));

            // Which of the two readers answered. The model and the DSP passes sometimes disagree about
            // tempo by an octave on some records, so "what did this analyse as" cannot be answered
            // without this.
            reading.Add(null, DenseForm.Label("Details_ReadBy"),
                Reading(Loc.Get(analysis.Modelled ? "Details_ReadByModel" : "Details_ReadByDsp")));
        }
        else
        {
            reading.Add(null, DenseForm.Label("Details_Analysis"), Reading(Loc.Get("Details_NotAnalysed")));
        }
        reading.AddTo(SoundRight, TagFields.Groups.Reading);

        // How loud it is. This is the library's own measurement, and it is the one value on this tab
        // that is not a tag and never will be. It is written nowhere in the file, only in the mirror.
        var level = new DenseFormGroup();
        if (_mirror is { LoudnessLufs: { } lufs })
            level.Add(null, DenseForm.Label("Details_Loudness"),
                Reading($"{lufs.ToString("0.0", CultureInfo.CurrentCulture)} {Loc.Get("Details_LufsUnit")}"));
        if (_mirror is { TruePeak: { } peak })
            level.Add(null, DenseForm.Label("Details_TruePeak"),
                Reading(peak.ToString("0.000", CultureInfo.CurrentCulture)));
        level.AddTo(SoundRight, TagFields.Groups.Level);

        BuildSoundFooter();
    }

    /// <summary>
    /// The hint under the two boxes, and the button that copies the measured values into them. This is
    /// the repair for a file that someone else tagged as 87 BPM.
    ///
    /// <para>It offers everything the analyser found, including the readings the app will not write on
    /// its own. The confidence is shown on screen a few rows away, and pressing the button is a
    /// decision. A button that refused to copy the key shown beside it would be the app arguing with
    /// the person who holds the record.</para>
    ///
    /// <para>The button is only shown when pressing it would change something. See <see
    /// cref="ShowUseMeasuredIfItWouldChangeAnything"/>.</para> </summary>
    private void BuildSoundFooter()
    {
        var panel = new StackPanel { Spacing = 8, Margin = new Thickness(0, 4, 0, 0) };

        BuildOctaveOffer(panel);

        if (_analysis is { } analysis && AnalysisTags.Detected(analysis) is { Any: true } available)
        {
            _measured = available;
            _useMeasured = new Button
            {
                Style = DenseForm.Res<Style>("DenseButtonStyle"),
                Content = Loc.Get("Details_UseMeasured"),
                HorizontalAlignment = HorizontalAlignment.Left,
            };
            _useMeasured.Click += (_, _) =>
            {
                if (_measured.Bpm > 0 && _byKind.TryGetValue(TagFieldKind.Bpm, out var bpm))
                    bpm.Write(_measured.Bpm.ToString(CultureInfo.InvariantCulture));
                if (_measured.Key is { Length: > 0 } key
                    && _byKind.TryGetValue(TagFieldKind.MusicalKey, out var box))
                    box.Write(key);
                ShowUseMeasuredIfItWouldChangeAnything();
            };
            panel.Children.Add(_useMeasured);

            // The boxes can be typed into as well as filled by the button. So the button follows what
            // is in them now, not what was in them when the window opened.
            WatchForMeasured(TagFieldKind.Bpm);
            WatchForMeasured(TagFieldKind.MusicalKey);
            ShowUseMeasuredIfItWouldChangeAnything();
        }

        panel.Children.Add(new TextBlock
        {
            Text = Loc.Get("Details_SoundHint"),
            FontSize = 11,
            Foreground = DenseForm.Res<Brush>("DenseLabelInkBrush"),
            TextWrapping = TextWrapping.Wrap,
        });
        SoundLeft.Children.Add(panel);
    }

    /// <summary>Keeps the button in step with a box that somebody is typing in. Type the measured tempo
    /// by hand and the button goes away. Clear the box and it comes back.</summary>
    private void WatchForMeasured(TagFieldKind kind)
        => Watch(kind, ShowUseMeasuredIfItWouldChangeAnything);

    private void Watch(TagFieldKind kind, Action onChanged)
    {
        if (_byKind.TryGetValue(kind, out var field) && field.Control is TextBox box)
            box.TextChanged += (_, _) => onChanged();
    }

    /// <summary>What a reading row shows: the value, and how sure the reader was.</summary>
    private static string ReadingText(string value, double? confidence) =>
        confidence is { } c
            ? $"{value}   {Loc.Format("Details_Confidence", c.ToString("0.00", CultureInfo.CurrentCulture))}"
            : value;

    /// <summary>The measured tempo as the tab shows it: the number the app counts this record at. That
    /// is the analyser's value, unless somebody has changed it.</summary>
    private static string TempoLine(TrackAnalysis analysis) =>
        ReadingText(
            $"{analysis.Bpm.ToString("0", CultureInfo.CurrentCulture)} {Loc.Get("Details_BpmUnit")}",
            analysis.BpmConfidence);

    /// <summary> The line that says this record could be counted another way, and the button that
    /// counts it that way.
    ///
    /// <para>Whether a record is 70 or 140 is a question of counting, not of speed. Both are true of
    /// it. Nothing in the audio settles it, and a beat model picks one. The app does not fold tempos
    /// into a fixed range on the way to disk. Across 1697 annotated tracks that fold corrected 73
    /// readings and broke 126, so the app asks the user instead. The genre in the box on the Basic tab
    /// makes the question worth asking, because it is the only context there is. See <see
    /// cref="TempoOctave"/>.</para>
    ///
    /// <para>Only ever a question. Nothing appears here on a record read at a tempo its own kind of
    /// music is counted at, which is the great majority of them.</para> </summary>
    private void BuildOctaveOffer(StackPanel panel)
    {
        if (_analysis is not { Bpm: > 0 } || _relativePath is null)
            return;

        _octaveLine = new TextBlock
        {
            FontSize = 11,
            Foreground = DenseForm.Res<Brush>("DenseLabelInkBrush"),
            TextWrapping = TextWrapping.Wrap,
        };

        _octaveButton = new Button
        {
            Style = DenseForm.Res<Style>("DenseButtonStyle"),
            HorizontalAlignment = HorizontalAlignment.Left,
        };
        _octaveButton.Click += (_, _) => TakeOctave();

        panel.Children.Add(_octaveLine);
        panel.Children.Add(_octaveButton);

        // The genre is the whole of the context. It is a box on another tab, which the person may still
        // be about to fill in.
        Watch(TagFieldKind.Genre, RefreshOctaveOffer);
        RefreshOctaveOffer();
    }

    /// <summary>What the offer says right now, given the genre in the box and what has already been
    /// answered. Both controls are hidden together when there is nothing to ask.</summary>
    private void RefreshOctaveOffer()
    {
        if (_octaveLine is null || _octaveButton is null || _analysis is not { Bpm: > 0 } analysis)
            return;

        // If the user has already answered, the offer is the way back, whatever the genre says now. An
        // answer that cannot be undone is worse than no answer.
        if (_bpmOctave is > 0 and not 1)
        {
            double measured = analysis.Bpm / _bpmOctave;
            Offer(
                Loc.Format("Details_BpmCounted", Round(analysis.Bpm), Round(measured)),
                Round(measured),
                octave: 1);
            return;
        }

        string? genre = ReadField(TagFieldKind.Genre) ?? _row.Genre;
        if (TempoOctave.OtherCount(analysis.Bpm, genre) is not { } other)
        {
            _octaveLine.Visibility = Visibility.Collapsed;
            _octaveButton.Visibility = Visibility.Collapsed;
            return;
        }

        Offer(
            Loc.Format("Details_BpmOtherCount", Round(other)),
            Round(other),
            octave: other / analysis.Bpm);

        void Offer(string line, string number, double octave)
        {
            _octaveLine.Text = line;
            _octaveLine.Visibility = Visibility.Visible;
            _octaveButton.Content = Loc.Format("Details_BpmUseOtherCount", number);
            _octaveButton.Visibility = Visibility.Visible;
            _pendingOctave = octave;
        }

        static string Round(double bpm) => bpm.ToString("0", CultureInfo.CurrentCulture);
    }

    /// <summary>Which way the button on screen would count the record. It is held beside the button,
    /// not captured in its handler, because the offer changes as the genre box changes.</summary>
    private double _pendingOctave = 1;

    /// <summary> Counts the record the other way. It stores the factor against the track and redraws
    /// the tempo.
    ///
    /// <para>The factor is stored against the track, not in its analysis. So it survives a re-tag, a
    /// new analyser, and a reading taken from the shared pool. See <see
    /// cref="LibraryDatabase.SetBpmOctave"/>. The tags are a separate decision and stay separate. The
    /// "use what was measured" button below appears again as soon as this changes what it would
    /// write.</para> </summary>
    private void TakeOctave()
    {
        if (_relativePath is null || _analysis is not { Bpm: > 0 } analysis)
            return;

        double octave = _pendingOctave;
        if (octave <= 0)
            return;

        double change = octave / (_bpmOctave <= 0 ? 1 : _bpmOctave);
        if (Math.Abs(change - 1) < 1e-9)
            return;

        try
        {
            var library = App.Services.GetRequiredService<LibraryService>();
            if (!library.IsOpen)
                return;
            library.Db.SetBpmOctave(_relativePath, octave);
        }
        catch
        {
            return;   // If the mirror does not answer, the tempo stays as it was.
        }

        _bpmOctave = octave;
        _analysis = GridEdit.Octave(analysis, change);

        if (_tempoReading is not null)
            _tempoReading.Text = TempoLine(_analysis);

        // The value the copy button would write has just changed by a factor of two.
        _measured = AnalysisTags.Detected(_analysis);
        RefreshOctaveOffer();
        ShowUseMeasuredIfItWouldChangeAnything();
    }

    /// <summary> The button is shown when pressing it would change something, and hidden when it would
    /// not.
    ///
    /// <para>That is most of the time. The boxes are filled from the analysis when the window opens, so
    /// on an ordinary track the button would only offer to write what is already written. A control
    /// that does nothing is worse than no control. It looks broken, or it looks like something the user
    /// has failed to understand.</para> </summary>
    private void ShowUseMeasuredIfItWouldChangeAnything()
    {
        if (_useMeasured is null)
            return;

        bool tempoDiffers = _measured.Bpm > 0
            && ReadField(TagFieldKind.Bpm) is { } bpm && ParseBpm(bpm) != _measured.Bpm;
        bool keyDiffers = _measured.Key is { Length: > 0 } key
            && ReadField(TagFieldKind.MusicalKey) is { } inBox
            && !string.Equals(inBox.Trim(), key, StringComparison.OrdinalIgnoreCase);

        _useMeasured.Visibility = tempoDiffers || keyDiffers ? Visibility.Visible : Visibility.Collapsed;
    }

    /// <summary>What is in a field's control right now. It is null when the window does not show the
    /// field.</summary>
    private string? ReadField(TagFieldKind kind) =>
        _byKind.TryGetValue(kind, out var field) ? field.Read() : null;

    /// <summary>A measured value in a form row: what was read, and, where there is one, how sure the
    /// reader was. It is not a box. Nothing here can be edited, because it is not a field someone
    /// filled in. It is what the audio turned out to be.</summary>
    private static TextBlock Reading(string value, double? confidence = null) => new()
    {
        Text = ReadingText(value, confidence),
        FontSize = 11.5,
        Foreground = DenseForm.Res<Brush>("DenseValueInkBrush"),
        VerticalAlignment = VerticalAlignment.Center,
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    /// <summary> What the track is a performance <i>of</i>, how it sorts, which pressing it came from,
    /// and what MusicBrainz calls it.
    ///
    /// <para>These share a tab because they share an audience. A pop single needs none of them. A
    /// symphony cannot be filed without them: a movement with no work reads as four unrelated tracks
    /// called "Allegro", and a composer with no sort name files under L for Ludwig. Putting them on
    /// Basic would use that tab's height for rows that most records leave empty. Leaving them out
    /// altogether would stop a library from holding classical music properly.</para> </summary>
    private void BuildWorkTab()
    {
        var work = new DenseFormGroup();
        AddField(work, TagFieldKind.Work);
        AddField(work, TagFieldKind.MovementName);
        AddPair(work, TagFieldKind.Movement, TagFieldKind.MovementCount);
        work.AddTo(WorkLeft, TagFields.Groups.Work);

        var sorting = new DenseFormGroup();
        AddField(sorting, TagFieldKind.TitleSort);
        AddField(sorting, TagFieldKind.ArtistSort);
        AddField(sorting, TagFieldKind.AlbumArtistSort);
        AddField(sorting, TagFieldKind.ComposerSort);
        sorting.AddTo(WorkLeft, TagFields.Groups.Sorting);

        var pressing = new DenseFormGroup();
        AddField(pressing, TagFieldKind.OriginalYear);
        AddField(pressing, TagFieldKind.Media);
        AddField(pressing, TagFieldKind.CatalogNumber);
        AddField(pressing, TagFieldKind.Barcode);
        AddField(pressing, TagFieldKind.ReleaseCountry);
        pressing.AddTo(WorkRight, TagFields.Groups.Pressing);

        var identifiers = new DenseFormGroup();
        AddField(identifiers, TagFieldKind.RecordingId);
        AddField(identifiers, TagFieldKind.ReleaseId);
        AddField(identifiers, TagFieldKind.ReleaseGroupId);
        AddField(identifiers, TagFieldKind.ArtistId);
        AddField(identifiers, TagFieldKind.ReleaseArtistId);
        identifiers.AddTo(WorkRight, TagFields.Groups.Identifiers);
    }

    /// <summary> One field's row. An album-scope field gets a marker in the trailing lane. The marker
    /// is amber when the rest of the record does not carry the value in the box. It also has a switch
    /// that says "write this to all of them".
    ///
    /// <para>The switch is off by default. Correcting one file's album name is how a record that was
    /// split in two gets repaired, so a plain edit here changes this file and nothing else. Turn the
    /// switch on only when the file in your hands is the correct one.</para> </summary>
    private void AddField(DenseFormGroup group, TagFieldKind kind)
    {
        var tag = TagFields.Get(kind);
        group.Add(null, DenseForm.Label(tag.LabelKey), Box(tag),
            topAlign: tag.Multiline, trailing: MarkerFor(tag));
    }

    /// <summary>Two numbers that are one fact, such as "Track 3 of 12", on one row. Two rows would use
    /// up the height that this layout tries to save.</summary>
    private void AddPair(DenseFormGroup group, TagFieldKind first, TagFieldKind second)
    {
        var tag = TagFields.Get(first);
        var line = DenseForm.Pair(
            Box(tag), DenseForm.Joiner("MassEdit_Of"), Box(TagFields.Get(second)));
        // The marker follows the SECOND box. A disc number belongs to this file, but a disc count
        // belongs to the record, and the count is what a whole album has to agree on.
        group.Add(null, DenseForm.Label(tag.LabelKey), line, trailing: MarkerFor(TagFields.Get(second)));
    }

    /// <summary>The album marker for a field, or null when there is nothing for it to say. That is the
    /// case for a track-level field, or for a file with no siblings to disagree with it.</summary>
    private AlbumMarker? MarkerFor(TagField tag)
    {
        if (tag.Scope is TagScope.Track || _siblings.Count == 0)
            return null;

        var values = _siblings.Select(tag.Get).Select(v => v.Trim()).ToList();
        var mine = tag.Get(_row).Trim();
        var others = values.Where(v => !string.Equals(v, mine, StringComparison.Ordinal)).ToList();

        string? disagreement = null;
        if (others.Count > 0)
        {
            var distinct = others.Distinct(StringComparer.Ordinal).ToList();
            disagreement = distinct.Count == 1
                ? Loc.Format("Edit_AlbumDisagrees", others.Count, distinct[0])
                : Loc.Get("Edit_AlbumDisagreesMany");
        }

        var marker = new AlbumMarker(disagreement, canArm: true);
        _markers[tag.Kind] = marker;
        return marker;
    }

    /// <summary>Each input is filed under its field. Save reads the values back this way, and the
    /// release lookup uses it to find the field its answer belongs in.</summary>
    private Control Box(TagField tag)
    {
        var value = tag.Get(_row);

        Control input;
        Func<string> read;
        Action<string> write;
        if (tag.Options is { Count: > 0 } options)
        {
            var choice = MassEditDialog.ChoiceBox(options, value);
            choice.IsEnabled = true;   // This window writes every field it shows, so nothing needs arming.
            input = choice;
            read = () => MassEditDialog.ReadChoice(choice, options);
            write = v => MassEditDialog.WriteChoice(choice, options, v);
        }
        else if (tag.SuggestionsFor is { } stylesFor)
        {
            // The styles under the genre this track is in. They follow the genre box beside them.
            var combo = MassEditDialog.SuggestBox(
                stylesFor(_byKind.TryGetValue(TagFieldKind.Genre, out var g)
                    ? MassEditDialog.ReadSuggest((ComboBox)g.Control)
                    : _row.Genre),
                value);
            if (_byKind.TryGetValue(TagFieldKind.Genre, out var genre) && genre.Control is ComboBox genreBox)
                MassEditDialog.FollowGenre(combo, genreBox, stylesFor);
            input = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else if (tag.Suggestions is { Count: > 0 } suggestions)
        {
            var combo = MassEditDialog.SuggestBox(suggestions, value);
            input = combo;
            read = () => MassEditDialog.ReadSuggest(combo);
            write = v => MassEditDialog.SetSuggestValue(combo, v);
        }
        else
        {
            var box = BoxCore(value, tag.Multiline, tag.Numeric);
            input = box;
            read = () => box.Text;
            write = v => box.Text = v;
        }

        if (TagFields.Resolve(tag.Width) is { } w)
            input.Narrow(w);

        _byKind[tag.Kind] = new FieldInput(input, read, write);
        return input;
    }

    /// <summary>One field's control, and how to get the value in and out of it. It is a box for most
    /// fields and a list for a choice field. Save and the release lookup both go through this, so
    /// neither assumes a TextBox.</summary>
    private sealed record FieldInput(Control Control, Func<string> Read, Action<string> Write);

    private static TextBox BoxCore(string value, bool multiline = false, bool numeric = false)
    {
        var box = new TextBox { Text = value };
        if (multiline)
        {
            box.AcceptsReturn = true;
            box.TextWrapping = TextWrapping.Wrap;
            box.Height = 72;
        }
        if (numeric)
            box.AcceptDigitsOnly();
        return box;
    }

    // ── the release lookup ──

    /// <summary>Opens the release lookup and fills every box that the pressing has an answer for,
    /// including the cover and the disc label. Unlike the mass edit, there is no tick box here. This
    /// window writes every field it holds, so the button is always enabled when a catalogue was
    /// offered.</summary>
    /// <summary> The lookup button. There are two ways to answer the same question, "what record is
    /// this track from?", so it opens a menu rather than choosing one.
    ///
    /// <para>A stray file usually belongs to a record already in the library. The library can answer
    /// that with no network and no risk of landing on the wrong pressing of something the user owns. A
    /// catalogue is for a record the user does not have yet. Whichever source answers, the boxes are
    /// filled the same way. Nothing is written until Save. A lookup that found the wrong record is
    /// undone by closing the window.</para> </summary>
    private void LookUpRelease_Click(object sender, RoutedEventArgs e)
    {
        // Only one source is available, so there is no menu. Go straight to it.
        if (_pickAlbum is null)
        {
            _ = RunLookupAsync(_lookup);
            return;
        }
        if (_lookup is null)
        {
            _ = RunLookupAsync(_pickAlbum);
            return;
        }

        var menu = new MenuFlyout();
        menu.Items.Add(Entry("Lookup_FromLibrary", "\uf51f", _pickAlbum));
        menu.Items.Add(Entry("Lookup_FromCatalogue", "\uf002", _lookup));
        menu.ShowAt(LookupButton);

        MenuFlyoutItem Entry(string key, string glyph, Func<ArtAsk, Task<MassEditLookup?>> source)
        {
            var item = new MenuFlyoutItem
            {
                Text = Loc.Get(key),
                FontSize = 12,
                MinHeight = 26,
                Padding = new Thickness(11, 3, 11, 4),
                Icon = new FontIcon
                {
                    Glyph = glyph,
                    Style = (Style)Application.Current.Resources["FaMenuIcon"],
                },
            };
            item.Click += (_, _) => _ = RunLookupAsync(source);
            return item;
        }
    }

    /// <summary>Puts what the chosen source returned into the boxes, including the cover.</summary>
    private async Task RunLookupAsync(Func<ArtAsk, Task<MassEditLookup?>>? source)
    {
        if (source is null)
            return;

        // A search over throttled catalogues, and then a picture fetch, can take a long time. A second
        // click would start the whole thing again behind the first one.
        LookupButton.IsEnabled = false;
        try
        {
            // The cover this track already shows decides where the overwrite question in the release
            // window starts. See ArtAsk.
            if (await source(_artBytes is { Length: > 0 } ? ArtAsk.Held : ArtAsk.Missing) is not { } found)
                return;

            foreach (var kind in LookupTargets)
                if (found.Fields.TryGetValue(kind, out var value) && _byKind.TryGetValue(kind, out var input))
                    input.Write(value);

            if (found.Art is { Embedded.Length: > 0 } art)
            {
                _artBytes = art.Embedded;
                _artMime = art.Mime;
                _artChanged = true;
                _artRemoved = false;
                // The tag gets the scaled copy. The caller gets the full-size scan for the folder.
                _folderArtBytes = art.Folder;
                await ShowArtAsync();
            }
        }
        catch (Exception ex)
        {
            // A source that does not answer leaves the form as it was, but it says so. A button that
            // silently does nothing is the hardest kind of broken to track down.
            RPM.Core.Diagnostics.Faults.Note("track-details", "lookup", ex);
        }
        finally
        {
            LookupButton.IsEnabled = true;
        }
    }

    // ── cover art ──

    private async Task LoadArtAsync()
    {
        if (_row.PendingArt is { } pending)
        {
            if (pending.Length > 0)
            {
                _artBytes = pending;
                _artMime = _row.PendingArtMime ?? "image/jpeg";
            }
        }
        else
        {
            var loaded = await Task.Run(() => TagReader.LoadArt(_row.FilePath));
            if (loaded is { } art)
            {
                _artBytes = art.Bytes;
                _artMime = art.Mime;
            }
        }
        await ShowArtAsync();
    }

    private async Task ShowArtAsync()
    {
        var has = _artBytes is { Length: > 0 };
        RemoveArtButton.IsEnabled = has;
        if (!has)
        {
            _artWell.Show(null);
            // The square shows the record glyph, not the words. The words are on the info line, as in
            // the album editor, where this panel's look comes from.
            ArtInfoText.Text = Loc.Get(_artRemoved ? "Details_RemoveArt" : "Details_NoArt");
            return;
        }

        // The square shows a thumbnail. The info line reports the real art. The dimensions come from a
        // bounds-only decode of the original bytes, because they describe what is embedded in the file,
        // not what this preview decoded. Use MemoryStream with AsRandomAccessStream. Do not use
        // InMemoryRandomAccessStream: it calls CryptographicBuffer.CreateFromByteArray, which Uno does
        // not implement. Using it throws on every cover, and the panel shows nothing.
        var (preview, size) = await Task.Run(() =>
            (ArtScaler.Downscale(_artBytes!, ArtScaler.TileEdge),
             SkiaSharp.SKBitmap.DecodeBounds(_artBytes)));
        var stream = Presentation.Common.BitmapBytes.Stream(preview);
        var bmp = new BitmapImage();
        await bmp.SetSourceAsync(stream);
        _artWell.Show(bmp);
        ArtInfoText.Text = $"{size.Width}×{size.Height} · {_artMime} · {Loc.Format("Unit_KB", _artBytes!.Length / 1024)}"
                           + (_artChanged ? $"\n{Loc.Get("Details_ArtPendingNote")}" : "");
    }

    private async void ChangeArt_Click(object sender, RoutedEventArgs e)
    {
        if (await PickImageAsync() is not { } picked)
            return;
        _artBytes = picked;
        // Read the type from the bytes, not from the file name. A .jpg that is really a PNG would
        // otherwise be written into the tag under a MIME type that no reader can decode.
        _artMime = IsPng(picked) ? "image/png" : "image/jpeg";
        _artChanged = true;
        _artRemoved = false;
        await ShowArtAsync();
    }

    /// <summary>The PNG file signature. It is a more reliable test than the file extension.</summary>
    private static bool IsPng(byte[] bytes) =>
        bytes is [0x89, (byte)'P', (byte)'N', (byte)'G', ..];

    /// <summary> Fetches the cover for the track as the track describes itself, so a file with no art
    /// does not have to be found in a browser first.
    ///
    /// <para>The lookup is made by artist and album, not by release id. The art archive is keyed by
    /// pressing, and it has no scan for many of them. The by-name lookup answers for most records
    /// anyone owns. What lands is the embedded size, scaled for a tag, because that is where this
    /// window's art goes.</para> </summary>

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();

    /// <summary>The file picker for the cover. It returns null when the user closes it, or when the
    /// machine has no picker to open.</summary>
    private static async Task<byte[]?> PickImageAsync()
    {
        try
        {
            var picker = Services.Shell.Pickers.OpenFile(
                Windows.Storage.Pickers.PickerLocationId.PicturesLibrary, ".jpg", ".jpeg", ".png");
            return await picker.PickSingleFileAsync() is { } file ? File.ReadAllBytes(file.Path) : null;
        }
        catch
        {
            return null;   // No picker is available, or the file could not be read.
        }
    }

    private async void RemoveArt_Click(object sender, RoutedEventArgs e)
    {
        _artBytes = null;
        _artChanged = false;
        _artRemoved = true;
        await ShowArtAsync();
    }

    // ── save/cancel ──

    private void Save_Click(object sender, RoutedEventArgs e)
    {
        _saved = true;
        // Put every box back on the row, field by field. This is one loop over the catalogue, so it
        // does not need twenty lines that must be kept in step with the catalogue.
        foreach (var (kind, input) in _byKind)
            TagFields.Get(kind).Set(_row, input.Read());
        _row.Lyrics = LyricsBox.Text;

        if (_artRemoved)
            _row.SetPendingArt(Array.Empty<byte>(), null);
        else if (_artChanged && _artBytes is { Length: > 0 })
            _row.SetPendingArt(_artBytes, _artMime);

        Hide();
    }

    private void Cancel_Click(object sender, RoutedEventArgs e) => Hide();

    /// <summary>Opens the editor over <paramref name="row"/>, and fills the row in place. It returns
    /// null when the window is closed without saving. Otherwise it returns the full-size scan that the
    /// folder's cover needs, and the album-wide fields that the user switched on.</summary>
    /// <param name="lookup">How to ask the release catalogues. Null hides the lookup button. That
    /// happens for a queue row with nothing to search on, or for a caller with no catalogue.</param>
    public static async Task<TrackDetailsResult?> ShowAsync(
        XamlRoot xamlRoot, TrackRow row, Func<ArtAsk, Task<MassEditLookup?>>? lookup = null,
        string? albumFolder = null, Func<ArtAsk, Task<MassEditLookup?>>? pickAlbum = null)
    {
        var dialog = new TrackDetailsDialog(row, lookup, albumFolder, pickAlbum) { XamlRoot = xamlRoot };
        await DialogHost.ShowAsync(dialog);
        if (!dialog._saved)
            return null;
        return new TrackDetailsResult(
            dialog._folderArtBytes,
            [.. dialog._markers.Where(m => m.Value.IsArmed).Select(m => m.Key)]);
    }
}
