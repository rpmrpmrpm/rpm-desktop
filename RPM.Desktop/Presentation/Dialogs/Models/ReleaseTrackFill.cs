using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Files;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Dialogs.Models;

/// <summary>The optional second step of the release lookup, as a value that the compiler can check.
///
/// <para>A lookup that also names tracks is a different task from a lookup that only identifies a
/// record. It holds files and writes to them. Where it writes depends on who opened it. A queue row is
/// a copy from the import store, and a library row is a file that is already filed. None of this can be
/// worked out inside the window. A default of "no thanks" would mean that every new caller silently
/// gets the wrong half. So the step is a required argument of <see
/// cref="ReleaseLookupDialog.ShowAsync"/>. Every caller must state its answer, and <see cref="None"/>
/// is a stated answer.</para>
/// </summary>
public sealed class TrackMetadataStep
{
    private readonly IReadOnlyList<TrackRow>? _rows;
    private readonly Func<Task>? _commit;

    private TrackMetadataStep(IReadOnlyList<TrackRow>? rows, bool ticked, Func<Task>? commit)
    {
        _rows = rows;
        _commit = commit;
        Ticked = ticked;
    }

    /// <summary>This lookup identifies a record and nothing else. This covers the split window, the
    /// disc ripper, and a single track's own editor. There is no second page, and no tick that offers
    /// one.</summary>
    public static readonly TrackMetadataStep None = new(null, false, null);

    /// <summary>This lookup can go on to name these files.
    /// </summary>
    /// <param name="rows">The tracks the step is about, as the caller holds them. The step fills them
    /// in place. They are the same objects that <paramref name="commit"/> writes afterwards.</param>
    /// <param name="ticked">Whether the offer starts accepted. True where naming the tracks is the
    /// whole task ("Track metadata from release"). False where it is a second thing the user may also
    /// want, such as linking a pressing or editing a record.</param>
    /// <param name="commit">What this context does with the rows once they are filled: tags are written
    /// and the files re-filed for the library, or the import queue writes back to its store. Required,
    /// because a step with nowhere to put its answer would be a window that appears to work.</param>
    public static TrackMetadataStep For(IReadOnlyList<TrackRow> rows, bool ticked, Func<Task> commit) =>
        new(rows, ticked, commit);

    /// <summary>Whether there is a second page to offer at all.</summary>
    public bool Offered => _rows is { Count: > 0 };

    /// <summary>Whether the offer starts accepted.</summary>
    public bool Ticked { get; }

    /// <summary>The rows the step fills. Empty when nothing was offered.</summary>
    public IReadOnlyList<TrackRow> Rows => _rows ?? [];

    /// <summary>Writes what the rows now say to wherever this context keeps them. The caller that
    /// opened the window calls this after the window has closed, not from inside it. So a long write
    /// over two hundred files does not run behind a dialog that is still closing.</summary>
    public Task CommitAsync() => _commit?.Invoke() ?? Task.CompletedTask;
}

/// <summary>How much of a listing landed on the files, and how much was asked about.</summary>
/// <param name="Named">Rows that took something from the release.</param>
/// <param name="Total">Rows that were shown.</param>
public sealed record TrackFill(int Named, int Total);

/// <summary>A release's listing, laid against the tracks a record actually holds.
///
/// <para>The pairing is <see cref="ReleaseTrackMatcher"/>'s and the writing is <see
/// cref="ApplyTrackFields"/>; what is here is the step between them, which is the part a person looks
/// at. One row per file, in the record's own running order, each showing what the file says now and
/// what the release would make it say. A file the release has no row for keeps a row of its own,
/// marked, rather than being left out. That mark shows the user that they picked a single, not the
/// album.</para>
///
/// <para>The other way round needs no mark: a release with more tracks than the record has files is the
/// ordinary case of naming half a record, and the surplus listing rows are simply not shown.</para>
/// </summary>
public sealed class ReleaseTrackFill
{
    private ReleaseTrackFill(
        IReadOnlyList<ReleaseTrackFillRow> lines, IReadOnlyDictionary<TagFieldKind, string> albumFields)
    {
        Lines = new ObservableCollection<ReleaseTrackFillRow>(lines);
        AlbumFields = albumFields;
    }

    /// <summary>One line per file, in the record's running order. This is what the grid
    /// draws.</summary>
    public ObservableCollection<ReleaseTrackFillRow> Lines { get; }

    /// <summary>What the release says about the record rather than about any track: its name, the
    /// artist it is filed under, the year, the label, and the number of discs. It is written to every
    /// file when the album box is ticked.</summary>
    public IReadOnlyDictionary<TagFieldKind, string> AlbumFields { get; }

    /// <summary>How the album fields read on screen: "Album · Artist · 1973 · Harvest".</summary>
    public string AlbumFieldsSummary => string.Join("   ·   ",
        new[] { TagFieldKind.Album, TagFieldKind.AlbumArtist, TagFieldKind.Year, TagFieldKind.Publisher }
            .Where(AlbumFields.ContainsKey)
            .Select(k => AlbumFields[k]));

    /// <summary>How many lines the release actually has an answer for.</summary>
    public int MatchedCount => Lines.Count(l => l.Matched);

    /// <summary>Lays a listing against a set of files.
    /// </summary>
    /// <param name="rows">The files, in whatever order the caller was holding them.</param>
    /// <param name="listing">The release's tracklist.</param>
    /// <param name="albumFields">What the release says about the record, from <see
    /// cref="AlbumFieldsOf"/>.</param>
    public static ReleaseTrackFill Pair(
        IReadOnlyList<TrackRow> rows, IReadOnlyList<ReleaseTrackInfo> listing,
        IReadOnlyDictionary<TagFieldKind, string> albumFields)
    {
        // Use the record's own running order, not the order the grid passed the rows in. When no file
        // has a track number, the matcher pairs files by position, so the order it gets decides the
        // result. See RunningOrder.
        var order = RunningOrder(rows);
        var places = order.Select(i => rows[i]).Select(PlaceOf).ToList();
        var matched = ReleaseTrackMatcher.Match(places, listing, out var discNumbersIgnored);

        var lines = new List<ReleaseTrackFillRow>(order.Count);
        for (var at = 0; at < order.Count; at++)
        {
            var row = rows[order[at]];
            lines.Add(new ReleaseTrackFillRow(
                row,
                // The matcher treats the files' disc numbers as unusable, so they are wrong on the
                // files too. This is the pass that corrects them. Setting the place's disc to 0 tells
                // ApplyTrackFields to write the listing's disc number.
                discNumbersIgnored ? places[at] with { Disc = 0 } : places[at],
                matched[at],
                at + 1));
        }

        return new ReleaseTrackFill(lines, albumFields);
    }

    /// <summary>Writes the ticked lines onto the files they stand for, and the album fields onto all of
    /// them.
    ///
    /// <para>The values in the grid are what get written, not the listing. A title corrected in the
    /// grid is the value used. A title emptied in the grid leaves the file's own title alone, because a
    /// blank never overwrites a name. Everything the grid does not show, such as the ISRC, the work and
    /// its movement, the conductor and the recording id, comes straight from the listing row that the
    /// line was paired with.</para>
    /// </summary>
    /// <param name="albumFields">Whether the record's own fields are written as well.</param>
    /// <returns>How many files took a value.</returns>
    public TrackFill Apply(bool albumFields)
    {
        var named = 0;
        foreach (var line in Lines)
        {
            var wrote = false;

            if (line is { IsSelected: true, Listing: { } track })
            {
                ApplyTrackFields(
                    line.Row,
                    track with { Title = line.Title, Artist = Blank(line.Artist) },
                    line.Place);
                wrote = true;
            }

            if (albumFields)
                foreach (var (kind, value) in AlbumFields)
                {
                    TagFields.Get(kind).Set(line.Row, value);
                    wrote = true;
                }

            if (!wrote)
                continue;

            line.Row.MarkManual();
            named++;
        }

        return new TrackFill(named, Lines.Count);
    }

    /// <summary>Ticks or unticks every line the release has an answer for. A line with no answer has no
    /// box to tick.</summary>
    public void TickAll(bool ticked)
    {
        foreach (var line in Lines.Where(l => l.Matched))
            line.IsSelected = ticked;
    }

    /// <summary>What a pressing says about the record itself: its name, the artist it is filed under,
    /// the year, the label, and the number of discs it spans.
    ///
    /// <para>The track artist is not included here. A listing names a track's own artist where it
    /// differs from the release artist. If the release artist were stamped on every row, a compilation
    /// with various artists would be filed under one name.</para>
    /// </summary>
    public static IReadOnlyDictionary<TagFieldKind, string> AlbumFieldsOf(
        ReleaseSearchResult? release, ReleaseDetail? detail)
    {
        var fields = new Dictionary<TagFieldKind, string>();

        void Set(TagFieldKind kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                fields[kind] = value.Trim();
        }
        void SetNumber(TagFieldKind kind, uint value)
        {
            if (value != 0)
                fields[kind] = value.ToString();
        }

        Set(TagFieldKind.Album, release?.Title ?? detail?.Title);
        Set(TagFieldKind.AlbumArtist, release?.Artist ?? detail?.Artist);
        SetNumber(TagFieldKind.Year, release?.Year is > 0 ? release.Year : detail?.Year ?? 0);
        Set(TagFieldKind.Publisher, release?.Label ?? detail?.Label);
        SetNumber(TagFieldKind.DiscCount, detail?.DiscCount ?? 0);

        return fields;
    }

    /// <summary>Where a file sits on the record, as the matcher reads it.</summary>
    public static TrackPlace PlaceOf(TrackRow row) => new(
        uint.TryParse(Blank(row.Disc), out var d) ? d : 0,
        uint.TryParse(Blank(row.Track), out var n) ? n : 0,
        Blank(row.Title) ?? Blank(row.Current.Title));

    /// <summary>The order these files sit in on the record, as indices into the list that was passed
    /// in.
    ///
    /// <para>When the files carry track numbers, those give the order: disc first, then track number,
    /// then the path to break a tie. When they carry none, the paths give the order. A folder of files
    /// is nearly always in its running order. The names are compared the way a person reads them, so
    /// "Track 2" comes before "Track 10".</para>
    ///
    /// <para><b>A set with no track numbers is ordered by path alone, disc tags included.</b> A disc
    /// number cannot place a file that has no track number. It only says which pile the file is in.
    /// Disc tags on such files are often left over from an earlier source, so they are not reliable.
    /// Sorting on them puts files out of sequence at the end of the record, and every file after the
    /// first of them then takes its neighbour's name.</para>
    ///
    /// <para>The whole path is used, not only the file name. A set filed as one folder per disc keeps
    /// its discs together and in order, without needing a tag to say so.</para>
    /// </summary>
    public static IReadOnlyList<int> RunningOrder(IReadOnlyList<TrackRow> rows)
    {
        var indices = Enumerable.Range(0, rows.Count);

        return rows.Any(r => uint.TryParse(Blank(r.Track), out var n) && n > 0)
            ? [.. indices
                .OrderBy(i => uint.TryParse(Blank(rows[i].Disc), out var d) ? d : 0)
                .ThenBy(i => uint.TryParse(Blank(rows[i].Track), out var n) ? n : uint.MaxValue)
                .ThenBy(i => rows[i].FilePath, NaturalOrder.Comparer)]
            : [.. indices.OrderBy(i => rows[i].FilePath, NaturalOrder.Comparer)];
    }

    /// <summary>Writes one listing row onto one queue or library row. Only what the listing actually
    /// answers is written. A field that the listing has nothing to say about keeps the file's value.
    ///
    /// <para>The track number is written only where the row has none. Matching by order is how an
    /// untagged rip is named, and a rip with no numbers needs them as much as it needs its titles. A
    /// row that already has a number was matched by that number, so writing it again would change
    /// nothing.</para>
    ///
    /// <para>The disc number is a separate question, and it must not be answered with the track number.
    /// A multi-disc set can be ripped as one folder with track numbers 1 to 12 and then 1 to 12 again,
    /// and with no disc numbers. Those files match correctly and take their titles, but they have no
    /// disc. So the record still holds two track 1s and two track 2s, and nothing downstream can tell
    /// the halves apart. The disc number is written whenever the file does not say which disc it is on
    /// and the listing does, whatever the track number is doing.</para>
    /// </summary>
    public static void ApplyTrackFields(TrackRow row, ReleaseTrackInfo track, TrackPlace place)
    {
        void Set(TagFieldKind kind, string? value)
        {
            if (!string.IsNullOrWhiteSpace(value))
                TagFields.Get(kind).Set(row, value.Trim());
        }
        void SetNumber(TagFieldKind kind, uint value)
        {
            if (value != 0)
                TagFields.Get(kind).Set(row, value.ToString());
        }

        Set(TagFieldKind.Title, track.Title);
        Set(TagFieldKind.Artist, track.Artist);
        Set(TagFieldKind.ArtistSort, track.ArtistSort);
        Set(TagFieldKind.Isrc, track.Isrc);
        Set(TagFieldKind.Work, track.Work);
        Set(TagFieldKind.MovementName, track.MovementName);
        SetNumber(TagFieldKind.Movement, track.MovementNumber);
        SetNumber(TagFieldKind.MovementCount, track.MovementCount);
        Set(TagFieldKind.Composers, track.Composers);
        Set(TagFieldKind.ComposerSort, track.ComposerSort);
        Set(TagFieldKind.Conductor, track.Conductor);
        Set(TagFieldKind.Ensemble, track.Ensemble);
        Set(TagFieldKind.Soloists, track.Soloists);
        Set(TagFieldKind.Lyricist, track.Lyricist);
        Set(TagFieldKind.Arranger, track.Arranger);

        // The name of the disc this track is on. This is a per-track value, although it describes the
        // disc. A listing is read one row at a time, and a two-disc set names each half on its own
        // rows.
        Set(TagFieldKind.DiscSubtitle, track.DiscSubtitle);

        // The ids are per file. Each one belongs to one recording or one artist, for this track only.
        // So the mass edit does not offer them for a selection, and a per-row listing is the only place
        // where they can be written correctly.
        Set(TagFieldKind.RecordingId, track.RecordingId);
        Set(TagFieldKind.ArtistId, track.ArtistId);

        // Unknown is not an answer, so it never overwrites an existing value. A catalogue that says
        // nothing about a track's lyrics must not turn a file marked explicit into a file marked as not
        // explicit.
        if (track.Advisory != RPM.Core.Models.ContentAdvisory.Unknown)
            Set(TagFieldKind.Advisory, TagFields.AdvisoryValues.ToValue(track.Advisory));

        if (place.Track == 0)
            SetNumber(TagFieldKind.TrackNo, track.DiscPosition == 0 ? track.Position : track.DiscPosition);

        if (place.Disc == 0)
            SetNumber(TagFieldKind.Disc, track.Disc);

        row.MarkManual();
    }

    private static string? Blank(string? s) => string.IsNullOrWhiteSpace(s) ? null : s.Trim();
}

/// <summary>One file in the track step: where it sits on the record, what it says now, and what the
/// release would make it say.
/// </summary>
[RpmBindable]
public sealed partial class ReleaseTrackFillRow : INotifyPropertyChanged
{
    private string _title;
    private string _artist;
    private bool _isSelected;

    internal ReleaseTrackFillRow(TrackRow row, TrackPlace place, ReleaseTrackInfo? listing, int position)
    {
        Row = row;
        Place = place;
        Listing = listing;
        Position = place.Track != 0 ? (int)place.Track : position;

        _title = listing?.Title ?? "";
        _artist = listing?.Artist ?? "";
        // A file the release has nothing for is shown and left alone: there is no answer to accept.
        _isSelected = listing is not null;
    }

    internal TrackRow Row { get; }
    internal TrackPlace Place { get; }
    internal ReleaseTrackInfo? Listing { get; }

    /// <summary>Where this file sits on the record: its own track number, or its place in the running
    /// order when it carries none.</summary>
    public int Position { get; }

    /// <summary>True when the release has a track for this file.</summary>
    public bool Matched => Listing is not null;

    /// <summary>The line's name, such as "Track 7". A file with no title yet has no other name to
    /// show.</summary>
    public string PlaceText => Loc.Format("Lookup_TrackNumber", Position);

    /// <summary>What the file says now, so the change is visible rather than assumed.</summary>
    public string CurrentText
    {
        get
        {
            var title = string.IsNullOrWhiteSpace(Row.Title) ? Row.Current.Title : Row.Title;
            return string.IsNullOrWhiteSpace(title) ? Loc.Get("Lookup_TrackUntitled") : title;
        }
    }

    /// <summary>The title the release gives, editable. Emptied, it leaves the file's own
    /// alone.</summary>
    public string Title
    {
        get => _title;
        set => Set(ref _title, value);
    }

    /// <summary>The track's own artist, where the release names one, such as a compilation's row or a
    /// guest credit. Blank when every track is by the release artist.</summary>
    public string Artist
    {
        get => _artist;
        set => Set(ref _artist, value);
    }

    /// <summary>Whether this file takes the release's answer.</summary>
    public bool IsSelected
    {
        get => _isSelected;
        set
        {
            if (!Matched)
                return;   // There is nothing to accept, so the box cannot be turned on
            Set(ref _isSelected, value);
        }
    }

    /// <summary>The release's length for this track, where it says one.</summary>
    public string LengthText => Listing is { LengthSeconds: > 0 } listed
        ? TimeSpan.FromSeconds(listed.LengthSeconds!.Value)
                  .ToString(listed.LengthSeconds >= 3600 ? @"h\:mm\:ss" : @"m\:ss")
        : "";

    /// <summary>The warning triangle in front of the line: this record has more tracks than the release
    /// does, so this one is not on it.</summary>
    public Visibility WarningVisibility => Matched ? Visibility.Collapsed : Visibility.Visible;

    /// <summary>A line with nothing to accept has no box.</summary>
    public Visibility TickVisibility => Matched ? Visibility.Visible : Visibility.Collapsed;

    /// <summary>The boxes are only editable where there is an answer in them.</summary>
    public bool IsEditable => Matched;

    public event PropertyChangedEventHandler? PropertyChanged;

    private void Set(ref string field, string value, [CallerMemberName] string? name = null)
    {
        value ??= "";
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }

    private void Set(ref bool field, bool value, [CallerMemberName] string? name = null)
    {
        if (field == value)
            return;
        field = value;
        PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}
