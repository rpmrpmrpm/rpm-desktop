using System.Collections.ObjectModel;
using System.Globalization;
using RPM.Core.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Main.Artists.Models;

/// <summary>
/// The songs shown under All tracks on an artist's page. This class is the only way to change them.
///
/// <para>Up to <see cref="GroupAbove"/> songs are one list, in the order they were given, with no
/// headings. Above that number, the songs are grouped by <see cref="Grouping"/>. Each group is an
/// <see cref="ArtistSongHeading"/> followed by its songs.</para>
///
/// <list type="bullet">
/// <item><see cref="ArtistTrackGrouping.Letter"/>: the first letter of the title, A to Z and then #
/// (the order the artists wall uses). Each group is sorted by title.</item>
/// <item><see cref="ArtistTrackGrouping.Year"/>: the track's year, newest first. Songs with no year
/// come last. Each group keeps the order the songs were given in, which is record by record in track
/// order.</item>
/// </list>
///
/// <para><see cref="Items"/> is ONE flat list of headings and songs, drawn by one repeater with
/// <see cref="ArtistTrackLayout"/>. Do not use one repeater per group. The repeaters would share one
/// recycle pool through the song template, and each would take the pool's only element from the
/// others on every pass. The layout then loops forever.</para>
///
/// <para>The shape depends only on which songs are in the listing, the order they were given in, and
/// the grouping. Adding songs one at a time while the page loads, rebuilding the whole listing after a
/// search, and switching the grouping all end in the same items in the same order. See
/// <see cref="Add"/>.</para>
/// </summary>
public sealed class ArtistSongListing
{
    /// <summary>More songs than this are grouped.</summary>
    public const int GroupAbove = 20;

    private readonly ObservableCollection<object> _items = [];

    /// <summary>Every song shown, in the order it was given. A regroup is rebuilt from this list,
    /// because a grouped <see cref="Items"/> does not keep that order.</summary>
    private readonly List<ArtistSong> _given = [];

    /// <summary>The headings in <see cref="Items"/>, in order. Empty while the listing is not grouped.</summary>
    private readonly List<ArtistSongHeading> _headings = [];

    /// <summary>The heading over songs with no year, localized by the caller.</summary>
    private readonly string _noYear;

    public ArtistSongListing(ArtistTrackGrouping grouping, string noYear)
    {
        Grouping = grouping;
        _noYear = noYear;
        Items = new(_items);
    }

    /// <summary>What the page binds to. Songs only while the listing is short. Headings and songs once
    /// it is long.</summary>
    public ReadOnlyObservableCollection<object> Items { get; }

    public ArtistTrackGrouping Grouping { get; private set; }

    /// <summary>How many songs are shown. Headings are not counted.</summary>
    public int Count => _given.Count;

    public bool IsGrouped => Count > GroupAbove;

    /// <summary>Raised after every change. Anything that depends on <see cref="IsGrouped"/> reads it
    /// again when this is raised, rather than being told by each caller.</summary>
    public event Action? Changed;

    /// <summary>Regroups what is shown. Nothing changes on screen while the listing is short, but the
    /// grouping is kept for when it grows.</summary>
    public void GroupBy(ArtistTrackGrouping grouping)
    {
        if (grouping == Grouping)
            return;

        Grouping = grouping;
        Rebuild();
        Changed?.Invoke();
    }

    /// <summary>
    /// Adds one song after the ones already shown.
    ///
    /// <para>Below the threshold, the song goes at the end. The song that crosses the threshold causes a
    /// rebuild in groups. Above it, the song is inserted after every song in its group that sorts the
    /// same or earlier. For years, it goes after every song in the group. This is the position that a
    /// stable sort of the whole listing gives it. That is why this method and <see cref="Show"/> agree.
    /// </para>
    /// </summary>
    public void Add(ArtistSong song)
    {
        _given.Add(song);

        if (Count == GroupAbove + 1)
            Rebuild();
        else if (IsGrouped)
            File(song);
        else
            _items.Add(song);

        Changed?.Invoke();
    }

    /// <summary>Replaces everything shown with these songs, in this order.</summary>
    public void Show(IReadOnlyList<ArtistSong> songs)
    {
        _given.Clear();
        _given.AddRange(songs);
        Rebuild();
        Changed?.Invoke();
    }

    public void Clear() => Show([]);

    /// <summary>Lays <see cref="Items"/> out again from <see cref="_given"/>.</summary>
    private void Rebuild()
    {
        ForgetPointer();
        _items.Clear();
        _headings.Clear();

        if (!IsGrouped)
        {
            foreach (var song in _given)
                _items.Add(song);
            return;
        }

        // OrderBy is stable, so songs that compare equal keep the order they were given in.
        foreach (var group in _given.GroupBy(Rank).OrderBy(group => group.Key))
        {
            var heading = new ArtistSongHeading(group.Key, HeadingText(group.First()));
            _headings.Add(heading);
            _items.Add(heading);
            foreach (var song in group.OrderBy(song => song, Within))
            {
                _items.Add(song);
                heading.Count++;
            }
        }
    }

    /// <summary>
    /// Rebuilding removes every row. It is not known whether a row removed under the pointer still gets
    /// its PointerExited. A song left marked as hovered would keep its empty heart on the row that
    /// replaces it. So the mark is cleared here.
    /// </summary>
    private void ForgetPointer()
    {
        foreach (var song in _items.OfType<ArtistSong>())
            song.IsHovered = false;
    }

    private void File(ArtistSong song)
    {
        var rank = Rank(song);

        // Where this group's heading is in Items: past every earlier group's heading and songs.
        var at = 0;
        var g = 0;
        for (; g < _headings.Count && _headings[g].Rank < rank; g++)
            at += 1 + _headings[g].Count;

        if (g == _headings.Count || _headings[g].Rank != rank)
        {
            var created = new ArtistSongHeading(rank, HeadingText(song));
            _headings.Insert(g, created);
            _items.Insert(at, created);
        }

        var heading = _headings[g];
        int low = 0, high = heading.Count;
        while (low < high)
        {
            var mid = (low + high) / 2;
            if (Within.Compare((ArtistSong)_items[at + 1 + mid], song) <= 0)
                low = mid + 1;
            else
                high = mid;
        }

        _items.Insert(at + 1 + low, song);
        heading.Count++;
    }

    /// <summary>Where a song's group stands among the others: lower is earlier.</summary>
    private long Rank(ArtistSong song) => Grouping switch
    {
        ArtistTrackGrouping.Year => song.Track.Year > 0 ? -(long)song.Track.Year : long.MaxValue,
        _ => LetterRank(ArtistWall.Initial(song.Title)),
    };

    private string HeadingText(ArtistSong song) => Grouping switch
    {
        ArtistTrackGrouping.Year => song.Track.Year > 0
            ? song.Track.Year.ToString(CultureInfo.InvariantCulture)
            : _noYear,
        _ => ArtistWall.Initial(song.Title).ToString(),
    };

    /// <summary>A letter's place in <see cref="ArtistWall.Alphabet"/>.</summary>
    private static long LetterRank(char letter)
    {
        for (var at = 0; at < ArtistWall.Alphabet.Count; at++)
            if (ArtistWall.Alphabet[at] == letter)
                return at;
        return ArtistWall.Alphabet.Count;
    }

    /// <summary>The order inside a group: by title for letters, the order given for years.</summary>
    private IComparer<ArtistSong> Within => Grouping == ArtistTrackGrouping.Letter
        ? ByTitle.Instance
        : AsGiven.Instance;

    /// <summary>Title order as a reader expects it. Case, accents and punctuation are ignored. So
    /// "(I Can't Get No) Satisfaction" sorts among the I's, which is the group that
    /// <see cref="ArtistWall.Initial"/> puts it in.</summary>
    private sealed class ByTitle : IComparer<ArtistSong>
    {
        public static readonly ByTitle Instance = new();

        public int Compare(ArtistSong? x, ArtistSong? y) => CultureInfo.CurrentCulture.CompareInfo.Compare(
            x?.Title, y?.Title,
            CompareOptions.IgnoreCase | CompareOptions.IgnoreNonSpace | CompareOptions.IgnoreSymbols);
    }

    /// <summary>Every song compares equal. So a stable sort, and an insert after equal songs, both keep
    /// the order given.</summary>
    private sealed class AsGiven : IComparer<ArtistSong>
    {
        public static readonly AsGiven Instance = new();

        public int Compare(ArtistSong? x, ArtistSong? y) => 0;
    }
}

/// <summary>The heading over one group of songs under All tracks: a letter or a year. Only
/// <see cref="ArtistSongListing"/> builds these.</summary>
[RpmBindable]
public sealed partial class ArtistSongHeading
{
    internal ArtistSongHeading(long rank, string text)
    {
        Rank = rank;
        Text = text;
    }

    internal long Rank { get; }

    public string Text { get; }

    /// <summary>How many songs follow this heading in <see cref="ArtistSongListing.Items"/>.</summary>
    internal int Count { get; set; }
}
