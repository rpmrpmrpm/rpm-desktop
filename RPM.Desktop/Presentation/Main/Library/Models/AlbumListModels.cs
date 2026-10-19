using System.ComponentModel;
using System.Runtime.CompilerServices;
using RPM.Core.Library;
using RPM.Desktop.Strings;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Main.Library.Models;

/// <summary>
/// One entry in the album list: a RECORD, its sleeve beside its tracks, with who made it on the line
/// under its name.
///
/// <para>Every entry is one kind of thing and has one rough height. The list's virtualization depends on
/// this. An artist heading as an item of its own would be a row about 50 points tall among records about
/// 270 points tall. A layout that guesses unrealized heights from the mean of the realized ones would see
/// that mean move each time a record is realized. The extent and the realization band move with
/// it.</para>
///
/// <para>A record fills itself in the first time it is realized. The cover and the tracklist each need a
/// disk read. A library of ten thousand records must not do ten thousand reads to show the first screen.
/// Until then the block shows its heading with an empty tracklist. This is also what the block looks like
/// just before its own tracks arrive.</para>
/// </summary>
[RpmBindable]
public sealed partial class AlbumListEntry : INotifyPropertyChanged, RPM.Desktop.Presentation.Common.ICloudBadged
{
    private AlbumListEntry(
        string folder, string title, string year, string artist, int trackCount, int headerLines)
    {
        Folder = folder;
        Title = title;
        ArtistName = artist;
        TrackCount = trackCount;
        HeaderLines = headerLines;
        _subtitle = Line(artist, null, year);
    }

    /// <summary>The height of one track line. It is read from the same resource that sizes the row
    /// (AlbumTrackLineHeight in Generic.xaml), so the reserved height and the drawn height always match.
    /// Two numbers that must agree should come from one place.</summary>
    private static double TrackLineHeight =>
        Microsoft.UI.Xaml.Application.Current?.Resources
            .TryGetValue("AlbumTrackLineHeight", out var line) == true && line is double height
            ? height
            : 22;

    /// <summary>How many tracks the record holds. This is known from the library's skeleton, before any
    /// track has been read off the disk.</summary>
    public int TrackCount { get; }

    /// <summary>How many lines the tracklist holds that are not tracks, counted in track lines. A
    /// multi-disc set's dividers and a classical record's work headers each take two track lines, which
    /// is the height the template gives them. Known from the library's skeleton, like the track
    /// count.</summary>
    public int HeaderLines { get; }

    /// <summary>
    /// How tall the tracklist will be once it arrives. The height is reserved from the moment the block
    /// is laid out, so the block does not grow when its tracks land.
    ///
    /// <para>Without this, the list shifts under the pointer while it is being scrolled. Each block that
    /// comes into view is a placeholder a couple of hundred points tall, and it becomes a full record
    /// after a disk read. Each one moves everything below it. Half a screen of blocks doing this at once
    /// is the jitter.</para>
    ///
    /// <para>The height is counted in track lines: a track is one line, and a divider is two. The columns
    /// are cut at half the units (see <see cref="AlbumTracklist.ColumnCut"/>). Lines two units tall cannot
    /// always be cut evenly. For example, three dividers in a row split four against two. So a record
    /// with any dividers reserves one line more than half. The room a block cannot use is an empty line at
    /// the foot of its tracklist. The room it turns out to need, and did not reserve, is the list moving
    /// under the pointer. Only the second one must be avoided.</para>
    /// </summary>
    public double ReservedTrackHeight =>
        (Math.Ceiling((TrackCount + HeaderLines) / 2d) + (HeaderLines > 0 ? 1 : 0)) * TrackLineHeight;

    /// <summary>One record, who made it, how many tracks it holds, and how many lines besides.</summary>
    public static AlbumListEntry Album(
        string folder, string title, string year, string artist, int trackCount, int headerLines = 0)
        => new(folder, title, year, artist, trackCount, headerLines);

    /// <summary>Who made this record. It is shown on the record's own line under its name, not in a
    /// heading over the shelf, so a record says who made it wherever it is seen. A rebuild reuses an
    /// entry only while what it says still holds. That includes an album whose artist has
    /// changed.</summary>
    public string ArtistName { get; }

    private bool _isExplicit;

    /// <summary>True when at least one track on the record has explicit lyrics. The E is shown beside the
    /// title. The list sets this on every rebuild, like the group heading. A reused entry outlives a tag
    /// edit, so the value must be refreshed.</summary>
    public bool IsExplicit
    {
        get => _isExplicit;
        set { if (_isExplicit == value) return; _isExplicit = value; OnChanged(); }
    }

    private bool _isFavorite;

    /// <summary>True when the listener has marked the record as a favourite. The heart sticker on the
    /// corner of its sleeve shows this. It is the same sticker the wall's tiles show. The list sets this on
    /// every rebuild, like the E. A reused entry outlives the click that marked it.</summary>
    public bool IsFavorite
    {
        get => _isFavorite;
        set { if (_isFavorite == value) return; _isFavorite = value; OnChanged(); }
    }

    private string? _groupHeading;

    /// <summary>The name of the group this record OPENS. The group is by artist, genre or year, depending
    /// on how the list is gathered. This is null on every record after the first of its group. The value
    /// is positional, so it is decided again whenever the list is rebuilt or the grouping
    /// changes.</summary>
    public string? GroupHeading
    {
        get => _groupHeading;
        set
        {
            if (_groupHeading == value) return;
            _groupHeading = value;
            OnChanged();
            OnChanged(nameof(HasGroupHeading));
        }
    }

    public bool HasGroupHeading => _groupHeading is not null;

    private bool _hasRule;

    /// <summary>Whether this record draws the rule that divides it from the record BELOW it. Every record
    /// draws it except the last record of the list and the last record of each group. A rule under the
    /// bottom record would divide nothing. A rule over a group's heading would be a second line right above
    /// the heading's own. The value is positional, so it is decided again on each rebuild and on every
    /// change of grouping.
    ///
    /// <para>The rule is drawn below the record, not above it. The seam is 50 points on every record, so
    /// all the blocks have the same height. The record that draws nothing in its seam must be one that
    /// nobody notices. If the rule were above, the first record on the shelf would show an empty seam
    /// under the panel's header. Below, it is the last record, at the very bottom, where the shelf's own
    /// padding already is. The value starts false. A record is the last one until something is put after
    /// it.</para></summary>
    public bool HasRule
    {
        get => _hasRule;
        set { if (_hasRule == value) return; _hasRule = value; OnChanged(); }
    }

    /// <summary>The album's folder, relative to the library root. This is its identity everywhere in the
    /// app.</summary>
    public string Folder { get; }

    public string Title { get; }

    private string _subtitle;

    /// <summary>What the record is, under its name: who made it, its genre and its year, as far as each is
    /// known. The artist and the year come from the skeleton. The genre only arrives with the tracks, so
    /// this is read again once they land.</summary>
    public string Subtitle
    {
        get => _subtitle;
        private set { if (_subtitle == value) return; _subtitle = value; OnChanged(); }
    }

    private BitmapImage? _cover;

    public BitmapImage? Cover
    {
        get => _cover;
        set { _cover = value; OnChanged(); OnChanged(nameof(HasCover)); }
    }

    public bool HasCover => _cover is not null;

    private bool _coverMissing;

    /// <summary>True when the read came back with no cover: no art in the folder, in the tags, or anywhere
    /// else that answers for this record. It is set only after the sleeve has actually been requested. See
    /// <see cref="FigureSeed"/>.</summary>
    public bool CoverMissing
    {
        get => _coverMissing;
        set
        {
            if (_coverMissing == value)
                return;
            _coverMissing = value;
            OnChanged(nameof(FigureSeed));
        }
    }

    /// <summary>
    /// What the made sleeve is drawn from. This is null while there is nothing to draw.
    ///
    /// <para>The block is realized long before its sleeve is read. A figure drawn from the folder name
    /// alone would be drawn for every record on the shelf, and thrown away again as soon as the real
    /// cover arrives. The wall follows the same rule. See
    /// <see cref="RPM.Desktop.Converters.SleeveFigureConverter"/>.</para>
    /// </summary>
    public string? FigureSeed => _coverMissing ? Folder : null;

    private Brush? _ring;

    /// <summary>The record's own colour, read from its sleeve. It is the hairline under the cover, so each
    /// block carries a trace of its album rather than the same grey as every other block. This is null
    /// until the sleeve has been read, and then nothing is painted. The rule keeps its 2 px height either
    /// way, so the block does not change height when the colour arrives.</summary>
    public Brush? Ring
    {
        get => _ring;
        set { _ring = value; OnChanged(); }
    }

    /// <summary>Set the moment the block is first realized. A second pass over the same block, such as a
    /// recycled container rebinding or a scroll back up, does not queue the reads again.</summary>
    public bool Requested { get; set; }

    /// <summary>The cloud changed while this record was off screen, so the marks on its songs are out of
    /// date. They are read again when the record comes back into view. The read is not done when the cloud
    /// changes. Re-badging a record reads its whole folder, and a shelf that has been browsed through holds
    /// hundreds of records that nobody is looking at.</summary>
    public bool TrackMarksStale { get; set; }

    /// <summary>Whether the cloud mark has been read for this record. It has its own flag, not
    /// <see cref="Requested"/>. The mark is a database query. Without a guard it ran again on every rebind.
    /// During a fast fling that is once per record per block passed, all on the thread pool, behind the
    /// reads that are actually wanted.</summary>
    public bool BadgeRead { get; set; }

    // ── the cloud, on the record itself ──
    //
    // The same mark the wall draws in the corner of a tile, in the same five states, beside the record's
    // name. The list is another view of the same shelf, so the mark means the same thing here.

    private RPM.Core.Library.AlbumCloudBadge _cloudBadge;

    /// <summary>How much of this record the cloud is holding. It is read when the block is realized, and
    /// read again whenever the cloud changes. The read is never done on the UI thread, because it is one
    /// query per record.</summary>
    public RPM.Core.Library.AlbumCloudBadge CloudBadge
    {
        get => _cloudBadge;
        set
        {
            if (_cloudBadge == value) return;
            _cloudBadge = value;
            OnChanged();
            OnChanged(nameof(ShowCloudBadge));
            OnChanged(nameof(IsCloudBusy));
            if (!IsCloudBusy)
                CloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;   // so no mark is left mid-breath
        }
    }

    /// <summary>The mark is shown only when the cloud is holding some of the record. Unlike a track line's
    /// mark, it is never hidden for any other reason, because the record's name is not a place the player
    /// marks.</summary>
    public bool ShowCloudBadge => _cloudBadge != RPM.Core.Library.AlbumCloudBadge.None;

    /// <summary>True while the mark is uploading or removing, which are the states that pulse.</summary>
    public bool IsCloudBusy =>
        _cloudBadge is RPM.Core.Library.AlbumCloudBadge.Working or RPM.Core.Library.AlbumCloudBadge.Leaving;

    private double _cloudPulse = RPM.Core.Cloud.CloudBadgePulse.Rest;

    /// <summary>The mark's opacity. The shared badge pulse writes it. The same clock drives the wall's
    /// tiles, the playlist rows and this record's own songs.</summary>
    public double CloudPulse
    {
        get => _cloudPulse;
        set { if (_cloudPulse == value) return; _cloudPulse = value; OnChanged(); }
    }

    private bool _loading;

    public bool Loading
    {
        get => _loading;
        set { if (_loading == value) return; _loading = value; OnChanged(); OnChanged(nameof(Ready)); }
    }

    public bool Ready => !_loading;

    private Common.AlbumTrackItemList _left = [];
    private Common.AlbumTrackItemList _right = [];

    /// <summary>The first column of lines, for the list's repeater.</summary>
    /// <remarks>
    /// <b>This has its own list type, not an IReadOnlyList.</b> Both columns are bound to a repeater's
    /// ItemsSource. Under Native AOT, CsWinRT can give WinRT list interfaces only to a collection type
    /// that it generated itself. A collection expression assigned to an IReadOnlyList is a type the
    /// compiler makes up. With that type the columns were passed to the repeater as something it could
    /// not read, and they stayed blank. See <see cref="Common.AlbumTrackItemList"/>.
    /// </remarks>
    public Common.AlbumTrackItemList Left
    {
        get => _left;
        private set { _left = value; OnChanged(); }
    }

    /// <summary>The second column. Its type has the reason given on <see cref="Left"/>.</summary>
    public Common.AlbumTrackItemList Right
    {
        get => _right;
        private set { _right = value; OnChanged(); OnChanged(nameof(LeftSpan)); }
    }

    /// <summary>How many columns the first one spans out of the tracklist's two. It spans both when the
    /// second is empty. That happens only for a record with a single line. If the first column kept one
    /// column's width, that line would get half the width, and its title would be cut short beside an
    /// empty column.</summary>
    public int LeftSpan => LeftSpanFor(_right.Count);

    /// <summary>The rule behind <see cref="LeftSpan"/>. It is kept apart from the entry so it can be tested
    /// without <see cref="SetLines"/>, whose summary needs the string table.</summary>
    internal static int LeftSpanFor(int secondColumnLines) => secondColumnLines == 0 ? 2 : 1;

    /// <summary>Every track on the record, in reading order. Play and Shuffle act on these.</summary>
    public IReadOnlyList<AlbumTrackItem> AllTracks => [.. _left.Where(i => i.IsTrack), .. _right.Where(i => i.IsTrack)];

    private IReadOnlyList<AlbumTrackItem> _lines = [];

    /// <summary>Every line in reading order, dividers included. Code that re-reads the lines in place
    /// uses this. The cloud marks are given to the list the entry already has, so a re-badge never
    /// re-cuts the two columns.</summary>
    public IReadOnlyList<AlbumTrackItem> Lines => _lines;

    private string _summary = "";

    /// <summary>A summary such as "13 songs, 48 minutes". It is shown under the cover, as the record's own
    /// sleeve states it.</summary>
    public string Summary
    {
        get => _summary;
        private set { if (_summary == value) return; _summary = value; OnChanged(); }
    }

    /// <summary>Lays the record's lines out in two columns, cut by height (see
    /// <see cref="AlbumTracklist.ColumnCut"/>), and sets the summary of the total.</summary>
    public void SetLines(IReadOnlyList<AlbumTrackItem> lines, string? genre, string year)
    {
        _lines = lines;
        var cut = AlbumTracklist.ColumnCut([.. lines.Select(l => l.LineUnits)]);
        Left = [.. lines.Take(cut)];
        Right = [.. lines.Skip(cut)];

        var tracks = lines.Where(l => l.IsTrack).ToList();
        var seconds = tracks.Sum(t => t.Info?.DurationSeconds ?? 0);
        var minutes = Common.PaneStats.WholeMinutes(seconds);
        Summary = tracks.Count == 1
            ? Loc.Format("Library_SongsAndTimeOne", tracks.Count, minutes)
            : Loc.Format("Library_SongsAndTime", tracks.Count, minutes);

        // The genre belongs to the record, not to each track. It is shown as a fact about the album only
        // when the tracks agree on it. A compilation of five genres therefore shows none of them.
        Subtitle = Line(ArtistName, genre, year);
        OnChanged(nameof(AllTracks));
    }

    /// <summary>The record's line: who made it, what it is, and when, in that order. Only the values that
    /// are known are shown. Empty fields are dropped, so no separators are left behind.</summary>
    private static string Line(string artist, string? genre, string year)
        => string.Join("  •  ", new[] { artist, genre, year }.Where(s => !string.IsNullOrWhiteSpace(s)));

    private void OnChanged([CallerMemberName] string? name = null)
        => PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));

    public event PropertyChangedEventHandler? PropertyChanged;
}

/// <summary>
/// One rung of the album list's letter rail: a letter, and the record it jumps to.
///
/// <para>Every letter is always drawn, whether or not the collection has any record under it. A rail that
/// shows only the letters in use would move its rungs each time the search box changed. An alphabet down
/// the side works only if M is always in the same place.</para>
/// </summary>
[RpmBindable]
public sealed partial class AlbumListLetter
{
    public AlbumListLetter(string text, int index)
    {
        Text = text;
        Index = index;
    }

    public string Text { get; }

    /// <summary>The first record filed under this letter, or -1 when the collection has none.</summary>
    public int Index { get; }

    public bool HasRecords => Index >= 0;
}
