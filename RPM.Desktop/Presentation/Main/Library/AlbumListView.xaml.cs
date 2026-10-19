using System.Numerics;
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
using RPM.Core.Cloud;
using RPM.Core.Library;
using RPM.Core.Models;
using RPM.Desktop.Services;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using Microsoft.UI.Xaml.Media.Imaging;

using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The album list: the collection shown as records, not as files. Each record has its sleeve beside its
/// tracks in two columns, and its artist and year on the line under its name. The records form one
/// continuous column down the panel, in artist order. This is the browsing view. It does the job of the
/// folder tree grid, but it orders the music the way a listener thinks about it, not the way a file
/// system stores it.
///
/// <para>Each record fills itself in the first time it comes on screen. A cover and a tracklist are each
/// a disk read, and ten thousand records must not make twenty thousand reads to paint the first screen.
/// Until they arrive, the block shows only its name.</para>
/// </summary>
public sealed partial class AlbumListView : UserControl
{
    private readonly List<AlbumListEntry> _entries = new();
    private readonly Dictionary<string, AlbumListEntry> _byFolder = new(StringComparer.OrdinalIgnoreCase);

    private string? _playingPath;

    /// <summary>The records handed to the list last time, in the order they were handed over. This is what is
    /// actually on screen, as opposed to what has just been built. It is compared with the next feed, to tell a
    /// re-feed of the same shelf from a shelf that has been replaced. See <see cref="Publish"/>.</summary>
    private List<string> _published = new();

    public AlbumListView()
    {
        InitializeComponent();
        // One shared timer for the records and their songs. Both are on screen together and can change at
        // the same time. A second timer would repaint the window twice per beat, and a record could then show
        // a mark out of step with its own track.
        _lineBreath = new Common.CloudLineBreath(
            () => _byFolder.Values.SelectMany(
                e => e.Lines.Cast<Common.ICloudBadged>().Append(e)));

        // A list that is not on screen must not keep a timer running for a badge.
        Unloaded += (_, _) => _lineBreath.Stop();

        // Measures how often the tree is laid out, and how long the layout pass after a tracklist is
        // published takes. That time is the cost of building the tracklist. This is only done when the
        // diagnostic is on. A handler on every layout pass costs time on every pass, and a tool for
        // measuring stalls must not add that cost.
        if (ListDiag.On)
        {
            LayoutUpdated += (_, _) => ListDiag.Laid();
            Loaded += (_, _) => ListDiag.Start("albums", DiagState);
        }
    }

    private bool _diagWatchingView;

    /// <summary>The scroller belongs to the ListView's template. It does not exist until the template has
    /// been applied, and Loaded does not promise that. It is wired the first time anything asks where a
    /// block is. By then there is certainly one.</summary>
    private void WatchViewForDiag()
    {
        if (_diagWatchingView || !ListDiag.On || ScrollHost is not { } scroller)
            return;

        _diagWatchingView = true;
        scroller.ViewChanged += (_, _) => ListDiag.View(scroller.VerticalOffset, scroller.ExtentHeight);
    }

    /// <summary>What the list reports about itself when a diagnostic line is written. The block heights
    /// answer the estimate question. A virtualizing stack places the records it has not realized from the
    /// mean of the ones it has. So two heights in one list make an extent that moves during a fling. One
    /// height lets a fling carry through.</summary>
    private string DiagState()
    {
        var heights = _shown.Keys
            .Where(b => b.ActualHeight > 0)
            .Select(b => Math.Round(b.ActualHeight))
            .Distinct()
            .OrderBy(h => h)
            .ToList();

        return $"blocks {_shown.Count} ({heights.Count} heights: {string.Join(", ", heights.Take(8))})" +
               $" | pending {_pending.Count}, readers {_readers}" +
               $" | read {_byFolder.Values.Count(e => e.Requested)} of {_byFolder.Count}";
    }

    // ── what the page supplies (the list does not depend on the library) ──

    /// <summary>An album folder's cover bytes, no larger than the pixel edge asked for.</summary>
    public Func<string, int, CancellationToken, Task<byte[]?>>? CoverLoader { get; set; }

    /// <summary>An album folder's tracks, in album order.</summary>
    public Func<string, CancellationToken, Task<IReadOnlyList<TrackInfo>>>? TracksLoader { get; set; }

    /// <summary>An album folder's own answer to whether it is classical, or null where it has no answer.
    /// This is the same question the album page asks. So one record does not count as two different
    /// things in two views of the same shelf.</summary>
    public Func<string, bool>? ClassicalLoader { get; set; }

    /// <summary>Plays one track. Clicking the track that is playing stops it, as on the album page.</summary>
    public Action<TrackInfo>? PlayTrack { get; set; }

    /// <summary>Plays a whole record, in order or shuffled.</summary>
    public Action<IReadOnlyList<TrackInfo>, bool>? PlayTracks { get; set; }

    /// <summary>Hands a whole record to the DJ. The queue becomes the record, and the mix plays it.</summary>
    public Action<IReadOnlyList<TrackInfo>>? StartDj { get; set; }

    /// <summary>Folder → how much of the record is in the cloud, for the mark beside its name. The page reads
    /// it from the library, and the list only shows the answer. The wall's tiles use the same hook, because
    /// it is the same fact about the same record.</summary>
    public Func<string, RPM.Core.Library.AlbumCloudBadge>? CloudBadgeLoader { get; set; }

    /// <summary>The same answer for the whole shelf at once, for <see cref="RefreshBadges"/>. It uses one
    /// query and one take of the library's reading gate, instead of one of each per record.</summary>
    public Func<IReadOnlyList<string>, IReadOnlyDictionary<string, RPM.Core.Library.AlbumCloudBadge>>?
        CloudBadgesLoader { get; set; }

    /// <summary>Whether the cloud engine is sending nothing at all. See the wall's property of the same name.
    /// The two views show the same marks on the same records, so they must agree on this.</summary>
    public Func<bool>? CloudUploadsStopped { get; set; }

    /// <summary>Whose collection is in the list. See the wall's property of the same name. The two views show
    /// the same marks, so they must agree on this too.</summary>
    public Func<RPM.Core.Cloud.ShelfReading>? ShelfReading { get; set; }

    /// <summary>Everything the album right-click menu needs. It is the same menu as on the wall, built by the
    /// same factory. It is the same record, so a listener sees the same menu in either view.</summary>
    public AlbumMenuHooks? MenuHooks { get; set; }

    /// <summary>Everything the TRACK right-click menu needs, for the songs under an expanded record. It is a
    /// separate set. A menu for a song and a menu for a record are menus for different things, so the entries
    /// of one do not belong in the other.</summary>
    public TrackMenuHooks? TrackMenuHooks { get; set; }

    /// <summary>Marks a song as a favourite, or clears the mark. This is the heart at the end of a line. It
    /// returns whether the library accepted the change. See the wall's property of the same name.</summary>
    public Func<TrackInfo, bool, bool>? SetTrackFavorite { get; set; }

    /// <summary>The list's own scroller. The page pins it while the art drawer slides. It belongs to the
    /// ListView's template, so it is found by searching the visual tree, not by name. That only works after
    /// the template has been applied. Before then there is nothing to pin.</summary>
    public ScrollViewer? ScrollHost => _scrollHost ??= FindScroller(EntriesList);

    private ScrollViewer? _scrollHost;

    private static ScrollViewer? FindScroller(DependencyObject? root)
    {
        if (root is null)
            return null;
        if (root is ScrollViewer found)
            return found;
        var count = VisualTreeHelper.GetChildrenCount(root);
        for (var i = 0; i < count; i++)
        {
            if (FindScroller(VisualTreeHelper.GetChild(root, i)) is { } scroller)
                return scroller;
        }
        return null;
    }

    private AlbumGrouping _grouping = AlbumGrouping.None;

    /// <summary>
    /// What the list gathers its records under: nothing, their artist, their genre or their year. The
    /// The grouping is also the ORDER. A group is only a group when its records sit next to each other. So
    /// changing it rebuilds the list from the albums it was last given.
    /// </summary>
    public AlbumGrouping Grouping
    {
        get => _grouping;
        set
        {
            if (_grouping == value)
                return;
            _grouping = value;
            // A new grouping is a new shelf, and every record is somewhere else. Rebuild reads that from the order
            // itself, so it takes the list back to its top like any other replacement.
            Rebuild();
        }
    }

    // The last set of albums the list was given. It is kept so that a change of grouping can lay out the
    // same albums again, without the page reading the library again. It is the same collection, grouped
    // in a different way.
    private IReadOnlyList<string> _folders = [];
    private IReadOnlyDictionary<string, AlbumFacts> _facts = new Dictionary<string, AlbumFacts>();

    /// <summary>The folders that hold an explicit track. This is the E on a record's line.</summary>
    private IReadOnlySet<string> _explicitAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>The folders the listener has marked as favourites. This is the sticker on a sleeve.</summary>
    private IReadOnlySet<string> _favoriteAlbums = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
    private IReadOnlyDictionary<string, string> _genres = new Dictionary<string, string>();
    private IReadOnlyDictionary<string, int> _trackCounts = new Dictionary<string, int>();
    private IReadOnlyDictionary<string, int> _headerLines = new Dictionary<string, int>();
    private string _libraryName = "";

    /// <summary>What an empty shelf would mean, from the last feed. It is kept rather than passed on, because
    /// the shelf is also rebuilt when the grouping changes, and the reason stays the same.</summary>
    private ShelfEmpty _empty = ShelfEmpty.Collection;

    /// <summary>
    /// Replaces the list with these album folders. They are gathered under the current
    /// <see cref="Grouping"/>, and sorted alphabetically within each group. This view is a shelf, and a
    /// shelf has one order.
    ///
    /// <para>A record that is still in the list keeps its existing entry, with its cover and tracklist.
    /// Each library change comes back through here with the same albums. Examples are adding one to the
    /// cloud, or marking one as a favourite. Rebuilding them all would drop every sleeve on screen and read
    /// them all again.</para>
    /// </summary>
    public void SetAlbums(
        IReadOnlyList<string> folders,
        IReadOnlyDictionary<string, AlbumFacts> facts,
        IReadOnlyDictionary<string, string> genres,
        IReadOnlyDictionary<string, int> trackCounts,
        IReadOnlyDictionary<string, int> headerLines,
        IReadOnlySet<string> explicitAlbums,
        IReadOnlySet<string> favoriteAlbums,
        string libraryName,
        ShelfEmpty empty)
    {
        _folders = folders;
        _facts = facts;
        _genres = genres;
        _trackCounts = trackCounts;
        _headerLines = headerLines;
        _explicitAlbums = explicitAlbums;
        _favoriteAlbums = favoriteAlbums;
        _libraryName = libraryName;
        _empty = empty;
        Rebuild();
    }

    /// <summary>One record on its way onto the shelf: what it is, and which group it falls in. The
    /// <paramref name="GroupKey"/> is what the shelf both orders and breaks by (see
    /// <see cref="AlbumGroupKey"/>). It is empty for a record that answers the grouping with nothing.
    /// <paramref name="Raw"/> is the record's own spelling of that answer. Only the heading uses it.</summary>
    private readonly record struct ShelfAlbum(string Folder, AlbumFacts Facts, string? Raw, string GroupKey);

    private void Rebuild()
    {
        var previous = new Dictionary<string, AlbumListEntry>(_byFolder, StringComparer.OrdinalIgnoreCase);

        _entries.Clear();
        _byFolder.Clear();

        var albums = _folders
            .Select(folder =>
            {
                var facts = FactsFor(folder, _facts, _libraryName);
                var raw = RawGroupOf(folder, facts);
                return new ShelfAlbum(folder, facts, raw, raw is null ? "" : AlbumGroupKey.Key(raw));
            })
            .ToList();

        // One heading per group, in the spelling most of its records use. So three records tagged "Hip Hop",
        // "hip-hop" and "HipHop" fall under one heading, not under three headings that look like the same word.
        var headings = AlbumGroupKey.Headings(albums.Select(a => a.Raw));

        var ordered = _grouping switch
        {
            // Newest first. Records with no year go at the end, not at year zero.
            AlbumGrouping.Year => albums
                .OrderBy(a => a.GroupKey.Length == 0 ? 1 : 0)
                .ThenByDescending(a => a.Facts.Year),

            // Grouped by nothing: sorted by the record's own TITLE. This mode has no headings. If the artist
            // were the main sort key, records by one band would sit together with no line to say why. The
            // rail's R would then land on R.E.M. under a heading the listener cannot see. Sorted by title, the
            // list is an A-to-Z of records, and the rail is its index.
            AlbumGrouping.None => albums.OrderBy(a => a.Facts.Album, StringComparer.CurrentCultureIgnoreCase),

            // Artist and genre alike: the GROUP comes first, always. Ordering by anything else would scatter a
            // group's records through the list. The shelf would then draw the same heading several times.
            //
            // Records that answer with nothing go at the end. Within the groups they are sorted by the heading
            // as it is printed, then by the key. So records that share a key sit next to each other, whatever the
            // collator does with their spellings. The break check below reads the same key back.
            _ => albums
                .OrderBy(a => a.GroupKey.Length == 0 ? 1 : 0)
                .ThenBy(a => HeadingOf(a, headings), StringComparer.CurrentCultureIgnoreCase)
                .ThenBy(a => a.GroupKey, StringComparer.Ordinal),
        };

        // The folder is the last tie-break in every grouping. Two untitled records by the same artist still
        // need a fixed place. Otherwise the list can reorder itself between two rebuilds that read the same rows.
        string? key = null;
        foreach (var album in ordered
            .ThenBy(a => a.Facts.Artist, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Facts.Album, StringComparer.CurrentCultureIgnoreCase)
            .ThenBy(a => a.Folder, StringComparer.OrdinalIgnoreCase))
        {
            var folder = album.Folder;
            var facts = album.Facts;
            _trackCounts.TryGetValue(folder, out var count);
            _headerLines.TryGetValue(folder, out var headerLines);
            if (!previous.TryGetValue(folder, out var entry)
                || entry.Title != facts.Album
                || entry.ArtistName != facts.Artist
                || entry.TrackCount != count
                || entry.HeaderLines != headerLines)
            {
                entry = AlbumListEntry.Album(
                    folder, facts.Album, facts.Year > 0 ? facts.Year.ToString() : "",
                    facts.Artist, count, headerLines);
            }

            // The list sets both of these, not the record. An entry can be reused, and it may have stood
            // somewhere else in the list before. Only the list knows where each record stands now.
            entry.IsExplicit = _explicitAlbums.Contains(folder);
            entry.IsFavorite = _favoriteAlbums.Contains(folder);

            // Compares with ordinal on the same key that the order was made from. This is the one comparison,
            // so a heading can only open a run of records that really are one group.
            var opensGroup = _grouping != AlbumGrouping.None
                && !string.Equals(key, album.GroupKey, StringComparison.Ordinal);
            entry.GroupHeading = opensGroup ? HeadingOf(album, headings) : null;

            // The rule is drawn UNDER a record (see the entry template), so it describes what FOLLOWS the record.
            // The record before this one draws its line, unless this one opens a group. Then the heading is the
            // divider, and a line above it would be a second divider. A record with nothing after it, the last
            // on the shelf, keeps its seam and draws nothing in it.
            if (_entries.Count > 0)
                _entries[^1].HasRule = !opensGroup;
            entry.HasRule = false;

            key = album.GroupKey;

            _entries.Add(entry);
            _byFolder[folder] = entry;
        }

        var saysEmpty = _byFolder.Count == 0 && _empty is not ShelfEmpty.Reading;
        if (saysEmpty)
            ShelfEmptyHint.Dress(_empty, EmptyIcon, EmptyText);
        EmptyHint.Visibility = saysEmpty ? Visibility.Visible : Visibility.Collapsed;
        BuildLetterRail();
        Publish();
    }

    /// <summary>What this record answers the grouping in force with, in its own spelling. It is null when the
    /// record answers with nothing: no genre its tracks agree on, no year tagged anywhere, and every spelling of
    /// "nothing" that a tagger writes into a field it left empty (see
    /// <see cref="AlbumGroupKey.Display"/>). A record with nothing to say gathers under one heading of
    /// its own rather than under a blank or an invented value.</summary>
    private string? RawGroupOf(string folder, AlbumFacts album) => _grouping switch
    {
        AlbumGrouping.Artist => AlbumGroupKey.Display(album.Artist),
        AlbumGrouping.Genre => _genres.TryGetValue(folder, out var genre) ? AlbumGroupKey.Display(genre) : null,
        AlbumGrouping.Year => album.Year > 0 ? album.Year.ToString() : null,
        _ => null,
    };

    /// <summary>The heading this record stands under: its group's canonical spelling, or the shelf's own
    /// heading for the records that answer with nothing. Null when the list is not gathered at all.</summary>
    private string? HeadingOf(ShelfAlbum album, IReadOnlyDictionary<string, string> headings) => _grouping switch
    {
        AlbumGrouping.None => null,
        AlbumGrouping.Year => album.GroupKey.Length == 0 ? Loc.Get("AlbumGroup_NoYear") : album.Raw,
        _ => album.GroupKey.Length == 0
            ? Loc.Get("AlbumGroup_Ungrouped")
            : headings.TryGetValue(album.GroupKey, out var heading) ? heading : album.Raw,
    };

    /// <summary>The rungs of the letter rail, in the order they are drawn. "#" is for everything the alphabet
    /// does not cover. That includes a band called 13th Floor Elevators, and a record whose tags say nothing
    /// at all, which is filed under the name of its folder.</summary>
    private const string RailLetters = "#ABCDEFGHIJKLMNOPQRSTUVWXYZ";

    /// <summary>
    /// Re-reads the rail against the list as it now stands. It holds every letter, and for each letter the
    /// first record filed under it. It is built from the entries, not from the library, so it matches the
    /// list the listener is looking at. A search that leaves four artists leaves four live rungs.
    /// </summary>
    private void BuildLetterRail()
    {
        var firstAt = new Dictionary<char, int>();
        string? group = null;
        for (var i = 0; i < _entries.Count; i++)
        {
            // The rail is keyed off the GROUP, not off the record. It is a way into the order the list is actually
            // in. Under a genre grouping, the R rung means Rock, not R.E.M. An ungrouped list has no headings and is
            // sorted by ALBUM TITLE, so the title is the key there. R then lands on the first record whose title
            // begins with R.
            group = _entries[i].GroupHeading ?? group;
            var letter = LetterOf(group ?? _entries[i].Title);
            if (!firstAt.ContainsKey(letter))
                firstAt[letter] = i;
        }

        LetterRail.ItemsSource = RailLetters
            .Select(c => new AlbumListLetter(c.ToString(), firstAt.TryGetValue(c, out var i) ? i : -1))
            .ToList();

        // No rail over an empty list, because there is nothing to jump to, and the empty hint should not have an
        // alphabet beside it. No rail over a list grouped by year either. Its headings are numbers, so every one
        // of them would land on the same rung.
        LetterRailBox.Visibility = _entries.Count > 0 && _grouping != AlbumGrouping.Year
            ? Visibility.Visible
            : Visibility.Collapsed;
    }

    /// <summary>
    /// Which rung a name is filed under. That is a group's heading, or a record's own title where the list
    /// is not grouped. Accents are removed first. Édith Piaf sorts among the E's, so the E rung has to find
    /// her. Filed under "#", she would be under a letter she does not sort near.
    /// </summary>
    private static char LetterOf(string name)
    {
        name = name.TrimStart();
        if (name.Length == 0)
            return '#';

        var folded = name[..1].Normalize(System.Text.NormalizationForm.FormD);
        foreach (var c in folded)
        {
            var upper = char.ToUpperInvariant(c);
            if (upper is >= 'A' and <= 'Z')
                return upper;
        }
        return '#';
    }

    /// <summary>Jumps to the first record under a letter. The list is asked to bring that record to the top
    /// of itself. Where the record is in the collection is the list's own business. It is not calculated here
    /// from heights that nobody has measured yet.</summary>
    private void Letter_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumListLetter { HasRecords: true } letter
            && letter.Index >= 0 && letter.Index < _entries.Count)
        {
            EntriesList.ScrollIntoView(_entries[letter.Index], ScrollIntoViewAlignment.Leading);
        }
    }

    /// <summary>An album's facts, with the folder name used wherever the tags say nothing. This is the same
    /// rule the wall's cards use for their names, so the two views never disagree about what a record is
    /// called.</summary>
    private static AlbumFacts FactsFor(
        string folder, IReadOnlyDictionary<string, AlbumFacts> facts, string libraryName)
    {
        if (facts.TryGetValue(folder, out var known)
            && !string.IsNullOrWhiteSpace(known.Album)
            && !string.IsNullOrWhiteSpace(known.Artist))
            return known;

        var parts = folder.Split('/', StringSplitOptions.RemoveEmptyEntries);
        var album = known.Album is { Length: > 0 } a ? a
            : parts.Length > 0 ? parts[^1] : libraryName;
        var artist = known.Artist is { Length: > 0 } r ? r
            : parts.Length >= 2 ? parts[^2] : libraryName;
        return known with { Album = album, Artist = artist };
    }

    /// <summary>
    /// Hands the entries to the list.
    ///
    /// <para>A shelf that holds the same records in the same order is the shelf already on screen. It has
    /// been told something new about itself, such as a cover arriving or a record being marked, and the reader
    /// keeps their place. Any other shelf (a search, a change of grouping, an import) is read from its top,
    /// because the record they were looking at is not in it.</para>
    /// </summary>
    private void Publish()
    {
        var shelf = _entries.Select(e => e.Folder).ToList();
        var replaced = RPM.Core.Library.AlbumGridLayout.ShelfReplaced(_published, shelf);

        // The same entry objects in the same order are the shelf already on screen. The entries have
        // already been told what changed, such as a heart, an E or a heading. Handing the list a new source
        // anyway would drop every realized block and build them again. For a collection of records that is a
        // screenful of sleeves and a few thousand track lines, rebuilt because one record was marked as a
        // favourite. Rebuild makes a NEW entry wherever a record's own facts changed, so reference equality
        // is the right test.
        if (!_entries.SequenceEqual(_shelfOnScreen))
        {
            EntriesList.ItemsSource = _entries.ToList();
            _shelfOnScreen = [.. _entries];
        }

        _published = shelf;

        if (replaced && _entries.Count > 0)
            EntriesList.ScrollIntoView(_entries[0], ScrollIntoViewAlignment.Leading);
    }

    /// <summary>The entries the list was last actually given. See <see cref="Publish"/>.</summary>
    private List<AlbumListEntry> _shelfOnScreen = [];

    /// <summary>The now-playing file, so its line marks itself wherever it is on the list.</summary>
    public void SetPlaying(string? filePath)
    {
        _playingPath = filePath;
        foreach (var entry in _byFolder.Values)
            ApplyPlaying(entry);
    }

    /// <summary>Works over the record's LINES, not its AllTracks. AllTracks is worked out fresh on every
    /// read: two LINQ passes and a new list. This runs over every record the list is holding, each time a
    /// track starts.</summary>
    private void ApplyPlaying(AlbumListEntry entry)
    {
        foreach (var line in entry.Lines)
            if (line.IsTrack)
                line.IsPlaying = _playingPath is { } path
                    && string.Equals(line.FilePath, path, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// Re-reads the cloud mark on every record the list is holding. This is the list's half of the wall's
    /// corner clouds, on the same signal.
    ///
    /// <para>It runs off the UI thread, in ONE query for the whole shelf. The answers are applied back on the
    /// UI thread in one loop. One query per record would mean a thousand takes of the gate that every library
    /// read goes through, on a shelf of a thousand records. See the wall's own <c>RefreshBadges</c> for the
    /// cost that was measured.</para>
    /// </summary>
    public async void RefreshBadges()
    {
        if (CloudBadgesLoader is not { } load)
            return;

        var entries = _entries.ToList();
        List<(AlbumListEntry Entry, RPM.Core.Library.AlbumCloudBadge Badge)> badges;
        try
        {
            badges = await Task.Run(() =>
            {
                var marks = load([.. entries.Select(e => e.Folder)]);
                return entries
                    .Select(e => (e, marks.TryGetValue(e.Folder, out var b)
                        ? b
                        : RPM.Core.Library.AlbumCloudBadge.None))
                    .ToList();
            });
        }
        catch
        {
            return;   // Marks stay stale until the next cloud change. This is not an error.
        }

        foreach (var (entry, badge) in badges)
            entry.CloudBadge = badge;

        _lineBreath.Sync();
    }

    /// <summary>The badge read is a database query, so it never runs on the realize path. A screenful of
    /// blocks coming in would be a screenful of queries on the UI thread. The record shows without its cloud
    /// badge for a moment instead. The entry is the model, not the recycled container, so the answer cannot
    /// land on the wrong record.</summary>
    private async Task LoadBadgeAsync(AlbumListEntry entry)
    {
        if (CloudBadgeLoader is not { } load)
            return;
        try
        {
            entry.CloudBadge = await ListDiag.TimeAsync("badge", () => Task.Run(() => load(entry.Folder)));

            // A record that scrolls in may be the first busy one on screen. Only then is the sync run. The sync
            // reads every mark the view holds, and every record that was scrolled past is one of them.
            if (entry.IsCloudBusy)
                _lineBreath.Sync();
        }
        catch { /* No mark is shown as a plain heading. This is not an error. */ }
    }

    /// <summary>
    /// Re-reads the cloud mark on every line of every record ON SCREEN.
    ///
    /// <para>The lines are re-badged where they stand. A rebuilt list would make a shelf that somebody is
    /// scrolling blink, because one song finished uploading. Records that have not been reached yet are left
    /// alone. They get their marks from <see cref="AlbumTrackItem.Build"/> when they are read.</para>
    ///
    /// <para>Records that have been read but are now scrolled away are not re-badged here. Re-badging one
    /// takes a full read of its folder, and this runs on every change the cloud makes. A shelf that somebody
    /// had browsed for a while would turn each change into a few hundred database queries, for marks on records
    /// nobody can see. Such records are marked as out of date instead. They are read again when they come back
    /// into view (see <see cref="Entry_DataContextChanged"/>).</para>
    /// </summary>
    public async void RefreshTrackBadges()
    {
        if (TracksLoader is not { } load)
            return;

        // Read once for the whole sweep, not per record. It is one fact about the engine, and every line on the
        // shelf must be told the same thing. The same applies to whose shelf it is. One collection is on screen,
        // not a different one per record.
        var stopped = CloudUploadsStopped?.Invoke() == true;
        var shelf = ShelfReading?.Invoke() ?? RPM.Core.Cloud.ShelfReading.Own;

        // Which records are on screen, read once. The blocks that exist are the only records that can be on
        // screen. There are a dozen of them, against a shelf of thousands.
        var visible = new HashSet<AlbumListEntry>();
        foreach (var (block, entry) in _shown)
            if (PlaceOf(block).Rank == 0)
                visible.Add(entry);

        var onScreen = new List<AlbumListEntry>();
        foreach (var entry in _byFolder.Values)
        {
            if (entry.Lines.Count == 0 || _pending.ContainsKey(entry))
                continue;   // never read, or already queued for a read that will badge it anyway

            if (visible.Contains(entry))
                onScreen.Add(entry);
            else
                entry.TrackMarksStale = true;
        }

        foreach (var entry in onScreen)
        {
            IReadOnlyList<TrackInfo> tracks;
            try
            {
                tracks = await load(entry.Folder, CancellationToken.None);
            }
            catch
            {
                continue;   // Marks on that one record stay stale. This is not an error.
            }

            // The shelf may have been re-fed while the folder was being read.
            if (!_byFolder.TryGetValue(entry.Folder, out var live) || !ReferenceEquals(live, entry))
                continue;

            AlbumTrackItem.ApplyCloud(entry.Lines, tracks, stopped, shelf);
        }

        _lineBreath.Sync();
    }

    /// <summary>The timer that animates the cloud marks on every line the list shows. It is one timer for the
    /// shelf, shared with the wall's tiles and the playlist rows.</summary>
    private readonly Common.CloudLineBreath _lineBreath;

    /// <summary>One record changed on disk (a retag or a new cover). It is read again. The rest of the list is
    /// not touched. An edit to one album is not a reason to read the whole shelf again.</summary>
    public void RefreshAlbum(string folder)
    {
        if (!_byFolder.TryGetValue(folder, out var entry))
            return;
        entry.Requested = false;
        _ = LoadAsync(entry, force: true);
    }

    // ── lazy fill: the record you are looking at first, paced against the frame ──
    //
    // Work is pulled, not pushed, as the wall's loader does it. A block coming into view only says that its
    // record WANTS reading. Pushing a read at once would start one for every block that a fling passes. The
    // sleeves that had already left the screen would then be read before the records on screen. The pull
    // version decides what to read next from where the blocks are at that moment. So a fling re-orders the
    // loader without cancelling anything, and a record that has scrolled away stops being the best job. Each
    // read is booked against the frame it was taken from, so filling the list in cannot take the frames the
    // scroll needs. See FrameBudget.

    /// <summary>Which record each live block is showing. A ListView recycles its blocks, so this is also how a
    /// record leaves the queue. The block that was going to be read for it has been given a different record.
    /// That means the first record is no longer on screen.</summary>
    private readonly Dictionary<FrameworkElement, AlbumListEntry> _shown = new();

    /// <summary>The records with a block of their own that still want reading, and the block each one stands
    /// in. This is not a queue. See <see cref="NextRecord"/>.</summary>
    private readonly Dictionary<AlbumListEntry, FrameworkElement> _pending = new();

    private void Entry_DataContextChanged(FrameworkElement sender, DataContextChangedEventArgs args)
    {
        if (_shown.TryGetValue(sender, out var before))
        {
            _pending.Remove(before);
            ListDiag.Cleared();
        }

        if (args.NewValue is not AlbumListEntry entry)
        {
            _shown.Remove(sender);
            return;
        }

        _shown[sender] = entry;
        ListDiag.Realized();

        if (entry.Requested && entry.BadgeRead && !entry.TrackMarksStale)
            return;

        _pending[entry] = sender;
        Pump();
    }

    /// <summary>
    /// The record most worth reading now: the highest one on screen. Once everything on screen has been read,
    /// it is the nearest one in the band either side of the screen. Null when nothing is left.
    ///
    /// <para>It is answered from where the blocks are at this instant. That lets a scroll re-order the work
    /// without a queue to rebuild.</para>
    /// </summary>
    private AlbumListEntry? NextRecord()
    {
        AlbumListEntry? best = null;
        var bestPlace = (Rank: int.MaxValue, Key: double.MaxValue);

        foreach (var (entry, block) in _pending)
        {
            var place = PlaceOf(block);
            if (place.Rank < bestPlace.Rank || (place.Rank == bestPlace.Rank && place.Key < bestPlace.Key))
            {
                bestPlace = place;
                best = entry;
            }
        }

        return best;
    }

    /// <summary>Where a block stands relative to the part of the list that can be seen. Rank 0 is on screen,
    /// and reads from top to bottom. Rank 1 is the cache band either side, nearest first. Rank 2 is a block
    /// that cannot be placed yet, because it has not been laid out.</summary>
    private (int Rank, double Key) PlaceOf(FrameworkElement block)
    {
        if (ScrollHost is not { } scroller || block.ActualHeight <= 0)
            return (2, 0);

        try
        {
            var top = block.TransformToVisual(scroller).TransformPoint(default).Y;
            var bottom = top + block.ActualHeight;
            if (bottom < 0)
                return (1, -bottom);
            if (top > scroller.ViewportHeight)
                return (1, top - scroller.ViewportHeight);
            return (0, Math.Max(0, top));
        }
        catch
        {
            return (2, 0);
        }
    }

    /// <summary>How many records may be read at once. More workers are not the answer to a fling. The answer
    /// is choosing again between every job, which <see cref="NextRecord"/> does. There are two readers, so one
    /// slow disk read does not hold up the record beside it.</summary>
    private const int Readers = 2;

    private int _readers;

    /// <summary>How much of each frame the reading may take, on the thread that draws. Both readers share it.
    /// It is one thread's budget, however many readers are spending it.</summary>
    private readonly FrameBudget _budget = new();

    /// <summary>The clock the budget is measured against. There is one for the class. A Stopwatch per view
    /// would measure the same frames from different starts.</summary>
    private static readonly System.Diagnostics.Stopwatch PaceClock = System.Diagnostics.Stopwatch.StartNew();

    private void Pump()
    {
        WatchViewForDiag();
        for (var spare = _readers; spare < Readers; spare++)
            _ = ReadAsync();
    }

    private async Task ReadAsync()
    {
        _readers++;
        try
        {
            while (NextRecord() is { } entry)
            {
                _pending.Remove(entry);

                var startedAt = PaceClock.Elapsed.TotalMilliseconds;

                // The record is read first, then its mark. The mark is a database query. It was measured at about
                // 80 ms per record on a busy mirror. If it were read first, the block on screen would have no sleeve
                // and no songs for those 80 ms, while a small cloud badge in the corner was fetched. The record is
                // what the reader is waiting for.
                if (!entry.Requested)
                    await LoadAsync(entry);
                else if (entry.TrackMarksStale)
                    await RemarkAsync(entry);

                if (!entry.BadgeRead)
                {
                    entry.BadgeRead = true;
                    await LoadBadgeAsync(entry);
                }

                // The time this record cost the drawing thread, booked against the frame it was taken from. If the
                // frame's share is used up, the reader waits out the rest of the frame. A record's tracklist is a few
                // hundred elements built in one go, and two of those in one frame drop the frame. See FrameBudget for
                // why giving the thread back is not enough on its own.
                var now = PaceClock.Elapsed.TotalMilliseconds;
                if (_budget.Spend(now, now - startedAt) is > 0 and var wait)
                    await Task.Delay(TimeSpan.FromMilliseconds(wait));
            }
        }
        finally
        {
            _readers--;
        }
    }

    /// <summary>A record was read, and then the cloud changed while it was off screen. It is now coming back
    /// into view. Only the marks are read again. The sleeve and the lines are already right, and building them
    /// again would make a record that has not changed blink.</summary>
    private async Task RemarkAsync(AlbumListEntry entry)
    {
        entry.TrackMarksStale = false;
        if (TracksLoader is not { } load)
            return;

        try
        {
            var tracks = await load(entry.Folder, CancellationToken.None);

            // The shelf may have been re-fed while the folder was being read.
            if (!_byFolder.TryGetValue(entry.Folder, out var live) || !ReferenceEquals(live, entry))
                return;

            AlbumTrackItem.ApplyCloud(
                entry.Lines, tracks, CloudUploadsStopped?.Invoke() == true,
                ShelfReading?.Invoke() ?? RPM.Core.Cloud.ShelfReading.Own);

            if (entry.Lines.Any(l => l.IsCloudBusy))
                _lineBreath.Sync();
        }
        catch
        {
            // Marks on that one record stay stale until the next cloud change. This is not an error.
        }
    }

    private async Task LoadAsync(AlbumListEntry entry, bool force = false)
    {
        if (entry.Requested && !force)
            return;
        entry.Requested = true;
        entry.Loading = true;

        try
        {
            // The sleeve is a fixed 188 points in the markup, so the edge it needs is fixed too. It is asked for
            // at that size, not at the wall's size, and it is decoded to that size, not to whatever size the file
            // has. The loader answers within the edge, so nothing is left to shrink here. The colour is read from
            // the same bytes on a worker thread.
            var sleeve = ArtScaler.DecodeEdgeFor(188, XamlRoot?.RasterizationScale ?? 1);
            if (CoverLoader is { } loadCover)
            {
                if (await ListDiag.TimeAsync("cover", () => loadCover(entry.Folder, sleeve, CancellationToken.None))
                    is { Length: > 0 } thumb)
                {
                    var tint = await ListDiag.TimeAsync("tint", () => Task.Run(() => CoverColor.Tint(thumb)));
                    entry.Cover = await ListDiag.TimeAsync("decode", () => DecodeAsync(thumb, sleeve));
                    entry.CoverMissing = false;
                    if (tint is { } colour)
                        entry.Ring = new SolidColorBrush(CoverColor.Dim(colour, 0.62));
                }
                else
                {
                    // No cover exists anywhere for this record. This is the one state that gets a drawn placeholder.
                    // It is set here, not in the markup. The block is drawn long before this read answers, and a
                    // placeholder bound any earlier would be drawn for every record on the shelf.
                    entry.CoverMissing = true;
                }
            }

            var tracks = TracksLoader is { } loadTracks
                ? await ListDiag.TimeAsync("tracks", () => loadTracks(entry.Folder, CancellationToken.None))
                : (IReadOnlyList<TrackInfo>)Array.Empty<TrackInfo>();

            // Disc order, with a divider for each disc (multi-disc records only). This is the same layout the
            // wall's drawer uses, because it is the same tracklist.
            var lines = ListDiag.Time("build", () => AlbumTrackItem.Build(
                tracks, ClassicalLoader?.Invoke(entry.Folder) == true, CloudUploadsStopped?.Invoke() == true,
                ShelfReading?.Invoke() ?? RPM.Core.Cloud.ShelfReading.Own));

            // A genre is only a fact about the RECORD when all its tracks agree. A compilation with five genres
            // has no genre. Taking the first track's genre would be the list inventing a value.
            var genres = tracks
                .Select(t => t.Genre)
                .Where(g => !string.IsNullOrWhiteSpace(g))
                .Distinct(StringComparer.CurrentCultureIgnoreCase)
                .Take(2)
                .ToList();
            var year = tracks.FirstOrDefault(t => t.Year > 0)?.Year.ToString() ?? "";

            ListDiag.Time<object?>("setlines", () =>
            {
                entry.SetLines(lines, genres.Count == 1 ? genres[0] : null, year);
                return null;
            });

            // The time SetLines itself takes is the time to cut the two columns. What it causes is a few hundred
            // elements built in the layout pass after it, and nothing above can see that. The clock for that pass
            // starts here. See ListDiag.Laid.
            ListDiag.LinesPublished(lines.Count);
            ListDiag.Record();

            // A record that is read while some of its tracks are uploading starts its timer straight away. Its lines
            // were given their marks as they were built. The sync runs only when a line has a busy mark. The sync
            // reads every mark the view holds, and a shelf that has been scrolled through holds thousands. Running
            // it after every record read would make the list slower the longer it is used.
            if (lines.Any(l => l.IsCloudBusy))
                _lineBreath.Sync();

            ApplyPlaying(entry);
        }
        catch
        {
            // A locked or half-written folder shows as an empty block. This is not an error.
        }
        finally
        {
            entry.Loading = false;
        }
    }

    private static async Task<BitmapImage> DecodeAsync(byte[] bytes, int edge)
    {
        // Never larger than the picture is. A cover smaller than the sleeve's edge, such as a 300 pixel scan or
        // an embedded thumbnail, would become a bitmap with more pixels than the file has. The memory is spent
        // and no sharpness is gained, because the same picture is drawn either way. The header alone gives the
        // size, so nothing is decoded to find it out.
        var (width, _) = ArtScaler.BoundsOf(bytes);

        var stream = Presentation.Common.BitmapBytes.Stream(bytes);
        // Width only. Setting both edges decodes to exactly that box, and stretches a cover that is not square.
        // The sleeve crops with UniformToFill, so the height follows the picture's own ratio.
        var bitmap = new BitmapImage { DecodePixelWidth = width > 0 ? Math.Min(edge, width) : edge };
        await bitmap.SetSourceAsync(stream);
        return bitmap;
    }

    // ── the tracklist's own scroller, and handing the wheel back ──

    /// <summary>The tracklist scrollers that already have the handler below. Blocks are recycled, so Loaded
    /// fires again on an element that was wired the first time it was used. A handler added twice would run
    /// twice, and that would give the shelf two wheel steps for one notch.</summary>
    private readonly HashSet<ScrollViewer> _wheelWired = new();

    private void Tracks_Loaded(object sender, RoutedEventArgs e)
    {
        if (sender is not ScrollViewer tracks)
            return;

        // The hems show what is true of whichever record this block is showing NOW. So they are read again on
        // every load. A recycled block is a different tracklist in the same scroller.
        ShowTrackHems(tracks);

        // Any stretch left on the block belongs to the record that has just left. A block that is recycled in the
        // middle of a stretch would otherwise pass its offset to the next record's songs and hold it there. Only
        // another stretch at the same end would put it back.
        if (tracks.Content is FrameworkElement held && held.RenderTransform is TranslateTransform slide)
            slide.Y = 0;
        if (ReferenceEquals(_stretched, tracks))
        {
            _stretched = null;
            _stretchRaw = 0;
            _stretchSpent = false;   // Reset, so the next record's first push at its end is not swallowed
            _stretchHeld.Reset();    // Reset the held state for the same reason
            _stretchIdle?.Stop();
        }

        if (!_wheelWired.Add(tracks))
            return;

        // This handler must run BEFORE Uno's own wheel handler. See Tracks_PointerWheel, which does the scrolling
        // itself for that reason. Placing it after Uno's handler does not work. Uno's ScrollContentPresenter
        // unhooks and re-hooks its handler on every OnLoaded. These blocks are recycled all the time, so each time
        // a block comes back, Uno joins the end of the handler list again. So a handler that is wired once cannot
        // stay behind Uno's.
        tracks.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler(Tracks_PointerWheel),
            handledEventsToo: true);

        tracks.ViewChanged += (s, _) => ShowTrackHems((ScrollViewer)s!);

        // The tracklist's own size changes when a record's lines land. The scroller's view has not changed at that
        // point, so ViewChanged says nothing. The scroller's own size changes too. The hems are as wide as the
        // tracks they copy, and the panel can be resized.
        if (tracks.Content is FrameworkElement lines)
            lines.SizeChanged += (_, _) => ShowTrackHems(tracks);
        tracks.SizeChanged += (_, _) => ShowTrackHems(tracks);

        WatchShelfWheel();
    }

    /// <summary>Whether the shelf's own wheel probe is up. One for the whole list, not one per block.
    /// </summary>
    private bool _shelfWheelWatched;

    /// <summary>
    /// The other end of the nested-scroll question. A tracklist's handler cannot see whether the notch
    /// reaches the shelf, or in what state. This probe answers that.
    ///
    /// <para>A tracklist's handler can say it marked a notch handled. It cannot say whether that stopped
    /// anything. So this probe sits on the shelf's own scroller, with <c>handledEventsToo</c>, and reports
    /// where the shelf ended up. The offset pair is the field to read. The "handled" value is not the notch's
    /// state on arrival. This probe is wired long after the shelf's own presenter, so it reports what that
    /// presenter left behind. A moved offset beside "handled yes" means the shelf took the notch before this
    /// probe saw it. It does not mean that handling failed to stop anything.</para>
    ///
    /// <para>It runs only under <c>RPM_LIST_DIAG</c>, because it is a measurement, and it runs on every notch
    /// anywhere in the list.</para>
    /// </summary>
    private void WatchShelfWheel()
    {
        if (!ListDiag.On || _shelfWheelWatched || ScrollHost is not { } shelf)
            return;
        _shelfWheelWatched = true;

        shelf.AddHandler(
            UIElement.PointerWheelChangedEvent,
            new PointerEventHandler((_, e) =>
            {
                var before = shelf.VerticalOffset;
                var handled = e.Handled;

                // Where the shelf ENDS UP, which is the only thing that settles it. Read on the turn
                // after the notch, so whatever the outer presenter did with it has already been done.
                shelf.DispatcherQueue.TryEnqueue(() =>
                    ListDiag.ShelfWheel(handled, before, shelf.VerticalOffset));
            }),
            handledEventsToo: true);
    }

    /// <summary>
    /// The two hems of one block, each as deep as the scroll leaves at its end. The foot shows that there are
    /// songs below, and it goes when the last of them arrives. The head appears once the tracklist has been
    /// scrolled off its first line. A record whose tracks all fit inside the block shows neither, which is what
    /// makes the hems mean something. The machinery is <see cref="ScrollHem"/>. The album page hems its own
    /// tracklist with the same one.
    /// </summary>
    private void ShowTrackHems(ScrollViewer tracks) => TrackHemsFor(tracks)?.Show();

    /// <summary>The hems of each block, kept against the scroller they hem. Blocks are recycled, and their
    /// scrollers come back with them. So this is built once per scroller, not once per record.</summary>
    private readonly Dictionary<ScrollViewer, ScrollHem> _trackHems = new();

    /// <summary>Set once the composition stack has said that it cannot do this. That stops the other blocks
    /// each trying it again. Their tracklists keep hard edges, as they would if none of this existed.</summary>
    private bool _noTrackHems;

    /// <summary>The hems of one block, built on the first request. The three elements they are made of live in
    /// a DataTemplate. So from here, the only way to find them is through the block they are in.</summary>
    private ScrollHem? TrackHemsFor(ScrollViewer tracks)
    {
        if (_trackHems.TryGetValue(tracks, out var made))
            return made;
        if (_noTrackHems)
            return null;

        if (tracks.Parent is not Grid cut || cut.Parent is not Grid block)
            return null;
        if (Hem(block, "head") is not { } head || Hem(block, "foot") is not { } foot)
            return null;

        if (ScrollHem.Attach(tracks, cut, head, foot, Orientation.Vertical, ScrollHem.Depth) is not { } hems)
        {
            _noTrackHems = true;
            return null;
        }

        _trackHems[tracks] = hems;
        return hems;
    }

    /// <summary>One of the two hems of a block. They are tagged rather than named, because they live in a
    /// DataTemplate. From here, the only way to find them is through the block they are in.</summary>
    private static FrameworkElement? Hem(Grid block, string tag)
    {
        foreach (var child in block.Children)
        {
            if (child is FrameworkElement hem && hem.Tag as string == tag)
                return hem;
        }

        return null;
    }

    /// <summary>
    /// A record's tracks scroll inside the block, which is what lets every block be one height (see
    /// AlbumListTrackAreaHeight). One wheel notch moves one scroller. This method decides which one. A
    /// tracklist that can scroll keeps every notch given to it, and a record whose songs all fit passes them
    /// to the shelf.
    ///
    /// <para>The pointer decides. A wheel over a tracklist is a request to read that record's songs. If the
    /// shelf jumped in the middle of that, the reader would lose the record. So the tracklist holds the wheel
    /// at its ends too, and the shelf is reached by moving the pointer off the tracklist. A record with no songs
    /// to hide has no scroller worth aiming at, so there the notch belongs to the shelf.</para>
    ///
    /// <para>So the tracklist's wheel is scrolled HERE, by hand. Uno's presenter never sees the notch at all.
    /// Two measured facts make this necessary. Both rule out working with that presenter:</para>
    ///
    /// <list type="number">
    /// <item>This handler always runs FIRST. So Uno's verdict on a notch cannot be read, because it has not
    /// been reached yet. Running a turn later, to get behind Uno, does not help. The presenter unhooks and
    /// re-hooks from every <c>OnLoaded</c>. A recycled block reloads all the time, so Uno joins the end of the
    /// list again each time.</item>
    /// <item>A clamped notch still MOVES the scroller. <c>Set</c> clamps to the end and goes there, and it
    /// reports false. That leaves <c>e.Handled</c> false. So the one notch that reaches the last song snaps the
    /// tracklist to its end, and also hands the same notch to the shelf.</item>
    /// </list>
    ///
    /// <para>Marking every notch over a scrolling tracklist as handled settles it. Uno's presenter is subscribed
    /// without <c>handledEventsToo</c>, so a handled notch never reaches it, and never reaches the shelf behind
    /// it either. One notch, one scroller, with no verdict to read and no order to depend on. The offsets are
    /// ours too, which is what makes them reliable. The lag from Uno's one-second animation would make the ends
    /// read wrongly. There is no such animation here.</para>
    /// </summary>
    private void Tracks_PointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (sender is not ScrollViewer tracks)
            return;

        var delta = e.GetCurrentPoint(tracks).Properties.MouseWheelDelta;
        if (delta == 0)
            return;

        var room = tracks.ScrollableHeight;

        // A record whose songs all fit has no scroller to aim at, so the notch is left to bubble up. The shelf
        // scrolls it with the platform's own step and easing, the same as anywhere else in the list. This code
        // does not move the shelf by hand. A hand-moved shelf scaled with the delta, so one wheel notch of -1080
        // threw the shelf about 1500 points.
        if (room <= 0.5)
        {
            ListDiag.Wheel(
                delta, room, tracks.VerticalOffset, e.Handled, ScrollHost?.VerticalOffset ?? -1,
                "tracklist does not scroll, shelf takes it");
            return;
        }

        // Already answered by something ahead of this handler. There is nothing to add, and nothing will chain either.
        if (e.Handled)
        {
            // This is logged because it is the one exit that leaves no trace. A notch marked handled before this
            // handler sees it takes the stretch with it, and the tracklist then sits at its end with no give
            // and nothing to say why.
            ListDiag.Wheel(
                delta, room, tracks.VerticalOffset, true, ScrollHost?.VerticalOffset ?? -1,
                "answered before this handler, no stretch");
            return;
        }

        var was = tracks.VerticalOffset;
        var target = WheelTarget(delta, was, room, tracks.ViewportHeight, Wheel);
        var moved = Math.Abs(target - was) > 0.5;

        if (moved)
        {
            tracks.ChangeView(null, target, null, disableAnimation: true);

            // Scrolling again lets go of whatever stretch was being held. A tracklist cannot be moving and
            // pressed against an end at the same time.
            if (ReferenceEquals(_stretched, tracks))
                ReleaseStretch(spent: false);
        }
        else
        {
            StretchTracklist(tracks, delta);
        }

        // Set on every path, and at the ends in particular. This is what keeps the notch off the shelf.
        e.Handled = true;

        ListDiag.Wheel(
            delta, room, target, false, ScrollHost?.VerticalOffset ?? -1,
            moved
                ? "tracklist scrolled by hand"
                : _stretchSpent
                    ? $"band already given, coasting ({_stretchHeld.ElapsedMilliseconds}ms)"
                    : $"tracklist held past its end {_stretchHeld.ElapsedMilliseconds}ms:"
                        + $" pushed {_stretchRaw:0.#}, songs give {StretchGive(_stretchRaw):0.##}");
    }

    // ── the stretch at the end of a tracklist ──

    /// <summary>
    /// A tracklist pressed past its end STRETCHES. It holds where the wheel left it, and springs back when the
    /// wheel stops. Nothing else in the window moves.
    ///
    /// <para>The wheel handler cannot say this in words. The notch was taken on purpose, and the shelf was left
    /// alone on purpose. Without the stretch, a tracklist at its end looks the same as a dead one. The wheel
    /// turns and the whole window sits still, which looks broken, not finished. A give in the direction you are
    /// pushing shows that this is the end of this list, and that the way on is the pointer, not more wheel.
    /// Phones show the end this way, so people already understand it.</para>
    ///
    /// <para>The stretch is written as resistance, not as a bounce for each notch. A bounce per notch is the
    /// difference between a rubber band and a twitch. The first version fired a fixed give-and-return on every
    /// notch and restarted it on the next. A spin at the end became a queue of identical 260 ms pulses, each
    /// cutting the one before it off. That read as jitter, correctly. Here a notch only adds to how far the
    /// tracklist is being pressed. The songs are put straight where that says, with no animation of their own.
    /// So they follow the wheel exactly as they do when the tracklist is really scrolling. The spring is the
    /// release, and it happens once, when the pushing stops.</para>
    ///
    /// <para>The give per notch falls away as the stretch grows (see <see cref="StretchGive"/>). So the first
    /// notch past the end moves plainly, and the tenth barely moves. That taper is what makes a limit feel
    /// right. A linear give reads as scrolling that has gone wrong, and a fixed give reads as a click.</para>
    ///
    /// <para>The songs move, not the block. The transform is on the scroller's content, so the sleeve, the
    /// record's name and the two hems stay still while the tracklist gives under them.</para>
    /// </summary>
    private void StretchTracklist(ScrollViewer tracks, int delta)
    {
        if (tracks.Content is not FrameworkElement lines)
            return;

        // The gesture has already given and sprung back, and events are still arriving. This is the tail of a
        // gesture that is over. Stretching again now would make the band pump itself. So the events are
        // swallowed, and the idle timer stays armed. The end of the tail is what clears this.
        if (_stretchSpent && ReferenceEquals(_stretched, tracks))
        {
            ArmStretchIdle();
            return;
        }

        // The pointer is over one tracklist at a time, so there is one stretch at a time. Arriving at a different
        // tracklist releases the last one, rather than leaving it held open.
        if (!ReferenceEquals(_stretched, tracks))
        {
            ReleaseStretch(spent: false);
            _stretched = tracks;
            _stretchRaw = 0;
            _stretchHeld.Restart();
        }

        var slide = StretchTransform(lines);

        // A release already under way is abandoned. The wheel is pushing again, so the stretch carries on from
        // where the spring had got to, not from where it started.
        _stretchRelease?.Stop();

        // How hard it is being pushed, in the units the tracklist would have scrolled by. It is signed. Down the
        // page is a negative delta, and a stretch lifts the songs.
        var push = Math.Clamp(WheelStep(delta, Wheel), -tracks.ViewportHeight, tracks.ViewportHeight);

        // Reversing the wheel against a held stretch relaxes it. It does not stretch the other way. The far end
        // has its own stretch, and the tracklist would have to travel the whole record to reach it. In practice
        // the reversal is caught earlier, where the tracklist scrolls again and the band is let go. This check
        // only keeps the arithmetic from crossing zero if it ever gets here.
        var next = _stretchRaw + push;
        _stretchRaw = _stretchRaw != 0 && Math.Sign(next) != Math.Sign(_stretchRaw) ? 0 : next;

        // Down the page is a negative delta. The offset rises, and the songs travel UP. So the give carries the
        // sign of the push it came from, and it needs no turning around here.
        slide.Y = StretchGive(_stretchRaw);

        // Nothing more has arrived for a moment, so the wheel has stopped and the band is let go.
        ArmStretchIdle();

        // The band is also released when it has been held long enough. On a trackpad, the wheel never reports
        // that it has stopped. macOS keeps sending scroll events for a second or two after the fingers lift. The
        // Skia host's event has no momentum phase (NativeMouseEventData has no such field), so a coasting event
        // looks the same as a finger that is still pushing. Each one re-armed the idle timer. So the band stayed
        // stretched for the whole coast, which lasts about two seconds before it springs back.
        //
        // A ceiling on the hold does the same job as the idle timer, and it works even when the gesture does not
        // stop. The band has had its say. It then STAYS let go until the events really stop. Otherwise the next
        // coasting event would simply stretch it again.
        if (_stretchHeld.Elapsed > StretchHoldMax)
            ReleaseStretch(spent: true);
    }

    private void ArmStretchIdle()
    {
        _stretchIdle ??= BuildStretchIdle();
        _stretchIdle.Stop();
        _stretchIdle.Start();
    }

    /// <summary>
    /// How far the songs actually move, given how hard the wheel has pushed past the end.
    ///
    /// <para>This is the rubber-band curve that every touch platform uses. It is proportional at first, and it
    /// levels off after that. So the band can always be pushed a little further, and it never reaches an end of
    /// its own. <c>Ceiling * raw / (raw + Slack)</c> is the same curve as Apple's
    /// <c>(1 - 1/(raw * c / d + 1)) * d</c>, written in a shorter form. Slack is the point where the give reaches
    /// half its ceiling. It is the one number worth tuning by feel.</para>
    ///
    /// <para>It is pure and internal, so the shape can be tested without a scroller. See
    /// AlbumListStretchTests.</para>
    /// </summary>
    internal static double StretchGive(double raw)
    {
        var pushed = Math.Abs(raw);
        var give = StretchCeiling * pushed / (pushed + StretchSlack);
        return raw < 0 ? -give : give;
    }

    /// <summary>The furthest the songs will ever give, however long the wheel is held. That is a track line and
    /// a half. It is far enough to be clearly a movement, and never far enough to look like scrolling.
    /// </summary>
    private const double StretchCeiling = 33;

    /// <summary>The push at which the give reaches half its ceiling, in the same units the tracklist scrolls in.
    /// A hundred is about a notch and a half. So the first notch past the end gives around 12 points, and the
    /// third around 20. The first is plain, and each one after is plainly harder.</summary>
    private const double StretchSlack = 100;

    /// <summary>The tracklist currently being held past one of its ends, if any.</summary>
    private ScrollViewer? _stretched;

    /// <summary>How hard that tracklist is being pushed, in the units it would otherwise have scrolled by. It is
    /// signed. It is not the same as how far the songs have moved. See <see cref="StretchGive"/>.</summary>
    private double _stretchRaw;

    private DispatcherTimer? _stretchIdle;
    private Storyboard? _stretchRelease;

    /// <summary>How long the band has been held. Together with <see cref="StretchHoldMax"/>, this is the only
    /// thing that ends a stretch on a trackpad. There the events never stop by themselves. See the note in
    /// <see cref="StretchTracklist"/>.</summary>
    private readonly System.Diagnostics.Stopwatch _stretchHeld = new();

    /// <summary>The band gave and sprang back, but the gesture that pushed it is still sending events. This stays
    /// set until the events stop, so a coast cannot stretch the same tracklist again and again.</summary>
    private bool _stretchSpent;

    /// <summary>The longest a band is held while events keep arriving. It is long enough that a push and its
    /// give read as one movement. It is short enough that letting go is felt as the answer to the push, not as
    /// something that happened later.</summary>
    private static readonly TimeSpan StretchHoldMax = TimeSpan.FromMilliseconds(400);

    /// <summary>The transform the stretch moves. It is made once per tracklist and left on it. The content may
    /// already carry a transform from something else, so an existing one is reused rather than replaced.</summary>
    private static TranslateTransform StretchTransform(FrameworkElement lines)
    {
        if (lines.RenderTransform is TranslateTransform existing)
            return existing;

        var slide = new TranslateTransform();
        lines.RenderTransform = slide;
        return slide;
    }

    /// <summary>How long after the last notch the band is let go. It is short enough that the release follows
    /// the wheel stopping, not trailing it. It is long enough that the gaps inside one spin of the wheel do not
    /// each look like the end of it.</summary>
    private static readonly TimeSpan StretchHold = TimeSpan.FromMilliseconds(120);

    private DispatcherTimer BuildStretchIdle()
    {
        var timer = new DispatcherTimer { Interval = StretchHold };
        timer.Tick += (_, _) =>
        {
            timer.Stop();
            ReleaseStretch(spent: false);   // The events have stopped. Let go, and forget the tracklist.
        };
        return timer;
    }

    /// <summary>
    /// The band let go: the songs travel back to where they belong, once, and the stretch is forgotten.
    ///
    /// <para>An exponential ease-out is the closest thing to a settling spring that can be had here. Uno's
    /// compositor has no spring animation, so there is no stiffness or damping value to set. The curve leaves
    /// at speed and arrives with almost none, which is what a released band looks like. A cubic ease looks like
    /// a plain slide by comparison. An elastic ease wobbles like a cartoon.</para>
    /// </summary>
    /// <param name="spent">The band was let go while its gesture was still sending events, as with a trackpad's
    /// coast. The tracklist is kept as the spent one, so the rest of that coast is swallowed and does not pump the
    /// band again. The idle timer clears it. False is the ordinary release. The events have stopped, so there is
    /// nothing left to swallow.</param>
    private void ReleaseStretch(bool spent)
    {
        var stretched = _stretched;

        if (spent)
        {
            _stretchSpent = true;
            ArmStretchIdle();   // The end of the coast is what clears this flag.
        }
        else
        {
            _stretchIdle?.Stop();
            _stretchSpent = false;
            _stretched = null;
        }

        _stretchHeld.Reset();
        _stretchRaw = 0;

        if (stretched?.Content is not FrameworkElement lines
            || lines.RenderTransform is not TranslateTransform slide
            || Math.Abs(slide.Y) < 0.5)
        {
            return;
        }

        var animation = new DoubleAnimation
        {
            From = slide.Y,
            To = 0,
            Duration = new Duration(StretchReturn),
            EasingFunction = new ExponentialEase { EasingMode = EasingMode.EaseOut, Exponent = 5 },
            // Stops at its own value instead of holding the last frame. The transform is then left at rest, and
            // a new stretch does not fight a finished animation.
            FillBehavior = FillBehavior.Stop,
            EnableDependentAnimation = true,
        };

        Storyboard.SetTarget(animation, slide);
        Storyboard.SetTargetProperty(animation, "Y");

        var board = new Storyboard();
        board.Children.Add(animation);
        // The storyboard animates the transform and then stops controlling it. So the value the transform should
        // hold afterwards is written directly. Without this, the songs jump back to the stretch for one frame.
        board.Completed += (_, _) => slide.Y = 0;

        _stretchRelease = board;
        board.Begin();
    }

    /// <summary>How long the band takes to come back. It is longer than the give, because a release is watched
    /// and a stretch is felt.</summary>
    private static readonly TimeSpan StretchReturn = TimeSpan.FromMilliseconds(420);

    /// <summary>
    /// Where one notch puts a tracklist: three song lines per notch, proportional to the delta, clamped to
    /// the scroller's own ends. Up the page is a positive delta and a falling offset.
    ///
    /// <para>It is proportional, because this wheel reports notches of 120, 360 and 1080. A fast spin arrives as
    /// one big delta, not as several small ones. It is capped at one viewport per notch, for the same reason. A
    /// tracklist that crosses its whole length on one notch is not scrolling. It is teleporting.</para>
    ///
    /// <para>It is separate and pure, so the ends can be tested without a scroller. See AlbumListWheelStepTests.
    /// The ends are the part that is easiest to get wrong, so they are tested.</para>
    /// </summary>
    internal static double WheelTarget(int delta, double offset, double room, double viewport, WheelUnit unit)
    {
        var step = Math.Clamp(WheelStep(delta, unit), -viewport, viewport);
        return Math.Clamp(offset - step, 0, Math.Max(0, room));
    }

    /// <summary>
    /// What a wheel event's delta MEANS. It is not the same on two platforms, and nothing in the framework
    /// normalises it. Reading one platform's numbers as the other's is silent. The arithmetic still runs, and it
    /// comes out fifty times too small.
    ///
    /// <para>Measured in the app: a push over the library on macOS arrives as −1, −4, −8, −18, −23, −14, −13,
    /// −7, −5. That is the shape of a trackpad's ramp, in points. The shelf's own scroller moves by exactly those
    /// numbers. The same push on Windows arrives as multiples of 120. If the macOS numbers go through the Windows
    /// conversion, a tracklist travels 0.55 of the distance that the shelf beside it travels for the same gesture.
    /// So the same push carries a different distance, depending on what the pointer is over. Users feel that
    /// before they notice it.</para>
    ///
    /// <para>The size of the difference matters. The two readings differ by 1.82 times. A whole push gives 11
    /// points of band when read as notches, and 16 when read as points. Both are visible. This is a consistency
    /// fault, not a missing effect.</para>
    ///
    /// <para>The unit is passed in rather than read from a static field. A test can then hold each platform to
    /// its own arithmetic, on whichever machine it runs. The compiler also names every caller when a third
    /// platform disagrees.</para>
    /// </summary>
    internal enum WheelUnit
    {
        /// <summary>Windows: <c>WM_MOUSEWHEEL</c>'s <c>GET_WHEEL_DELTA_WPARAM</c>. One detent of the wheel is 120,
        /// and a fast spin is a multiple of it.</summary>
        Notch,

        /// <summary>macOS: <c>NSEvent.scrollingDeltaY</c>, passed through unchanged by the Skia host. It is already
        /// the distance to travel. The shelf's own scroller beside this one moves by the same amount, one for one.
        /// So the same gesture carries the same distance, whether the pointer is over a record's songs or over the
        /// shelf behind them.</summary>
        Point,
    }

    /// <summary>Which of the two units this machine sends. This is the one place that asks the question.</summary>
    private static readonly WheelUnit Wheel =
        OperatingSystem.IsMacOS() ? WheelUnit.Point : WheelUnit.Notch;

    /// <summary>How far a delta asks the songs to travel, in layout points. It is not capped. The viewport cap
    /// belongs to whoever is scrolling. The stretch caps itself against its own ceiling.</summary>
    internal static double WheelStep(int delta, WheelUnit unit) => unit switch
    {
        WheelUnit.Point => delta,
        _ => delta / 120.0 * LinesPerNotch * TrackLine,
    };

    /// <summary>How far one wheel notch moves a tracklist, in track lines. Three lines is how far a wheel notch
    /// scrolls a list. On this list a line is also a song. The tracklist is read as songs, so it moves by songs.</summary>
    private const double LinesPerNotch = 3;

    /// <summary>One track line, the same 22 as <c>AlbumTrackLineHeight</c> in Generic.xaml. It is held here as a
    /// constant, because this runs on every notch. The same figure is repeated for the same reason in
    /// AlbumListBlockHeightTests.</summary>
    private const double TrackLine = 22;

    // ── playing and menus (delegated to the page) ──

    private void Track_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem { Info: { } info })
            PlayTrack?.Invoke(info);
    }

    /// <summary>A right-click on one song under an expanded record. The song's menu is separate from the
    /// record's menu, which is on the record's row. Neither belongs in the other.</summary>
    private void Track_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumTrackItem { Info: { } info })
            return;

        var menu = TrackMenuFactory.Build(
            info, TrackMenuHooks ?? new TrackMenuHooks(null, null, null, null),
            (FontFamily)Resources["Fa"], (FontFamily)Resources["FaRegular"]);
        if (args.TryGetPosition(sender, out var point))
            menu.ShowAt(sender, new FlyoutShowOptions { Position = point });
        else if (sender is FrameworkElement fe)
            menu.ShowAt(fe);
        args.Handled = true;
    }

    // ── the heart at the end of a track line ──

    /// <summary>The pointer is on a line, so its empty heart appears. The mark can then be made without a menu.
    /// See the wall's tracklist, which this is a second view of.</summary>
    private void Track_PointerEntered(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem line)
            line.IsHovered = true;
    }

    private void Track_PointerExited(object sender, PointerRoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumTrackItem line)
            line.IsHovered = false;
    }

    private void TrackFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumTrackItem { Info: { } info } line)
            return;
        var next = !line.IsFavorite;
        if (SetTrackFavorite?.Invoke(info, next) == true)
            line.IsFavorite = next;
    }

    /// <summary>One song's heart, re-marked on every record the list is holding. The favourite entry in the
    /// song's right-click menu lands here.</summary>
    public void ApplyTrackFavorite(string filePath, bool favorite)
    {
        foreach (var entry in _byFolder.Values)
            foreach (var line in entry.Lines)
                if (line.IsTrack && string.Equals(line.FilePath, filePath, StringComparison.OrdinalIgnoreCase))
                    line.IsFavorite = favorite;
    }

    /// <summary>One record's sticker, put on or taken off where it stands. The shelf is not rebuilt for a
    /// mark on one sleeve.</summary>
    public void ApplyAlbumFavorite(string folder, bool favorite)
    {
        if (_byFolder.TryGetValue(folder, out var entry))
            entry.IsFavorite = favorite;
    }

    private void PlayAll_Click(object sender, RoutedEventArgs e) => Play(sender, shuffle: false);
    private void Shuffle_Click(object sender, RoutedEventArgs e) => Play(sender, shuffle: true);

    private async void Play(object sender, bool shuffle)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumListEntry entry)
            return;

        // A record whose read is still in progress, or one whose read came back empty because the folder was
        // locked at the time, has no lines to play. It is read again, rather than letting the button do nothing.
        // The click means the listener wants THIS record now.
        if (entry.AllTracks.Count == 0)
            await LoadAsync(entry, force: true);

        var tracks = entry.AllTracks.Select(t => t.Info!).ToList();
        if (tracks.Count > 0)
            PlayTracks?.Invoke(tracks, shuffle);
    }

    /// <summary>The record becomes the DJ's set. It uses the same lazy re-read as the play buttons. The click is
    /// the listener asking for THIS record, so a record whose read never landed is read again first.</summary>
    private async void Dj_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is not AlbumListEntry entry)
            return;

        if (entry.AllTracks.Count == 0)
            await LoadAsync(entry, force: true);

        var tracks = entry.AllTracks.Select(t => t.Info!).ToList();
        if (tracks.Count > 0)
            StartDj?.Invoke(tracks);
    }

    /// <summary>The heart under a record's sleeve. This method decides nothing. The page stores the mark and
    /// tells every view that shows it (see MainPage.AlbumMenu). The mark comes back to this entry through
    /// <see cref="ApplyAlbumFavorite"/>, the same as a mark made from the menu.</summary>
    private void AlbumFavorite_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumListEntry entry)
            MenuHooks?.Invoked?.Invoke(AlbumMenuCommand.ToggleFavorite, entry.Folder);
    }

    private void Menu_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement { Tag: AlbumListEntry entry } anchor)
            MenuFor(entry).ShowAt(anchor);
    }

    /// <summary>A right-click anywhere on the record that is not one of its songs. It uses ContextRequested, not
    /// RightTapped. A song's own menu marks ContextRequested as handled, so only that event stops bubbling here.
    /// On RightTapped, the record would open its menu under the song's menu.</summary>
    private void Album_ContextRequested(UIElement sender, ContextRequestedEventArgs args)
    {
        if (sender is not FrameworkElement { Tag: AlbumListEntry entry } block)
            return;
        var menu = MenuFor(entry);
        if (args.TryGetPosition(block, out var point))
            menu.ShowAt(block, new FlyoutShowOptions { Position = point });
        else
            menu.ShowAt(block);
        args.Handled = true;
    }

    private MenuFlyout MenuFor(AlbumListEntry entry) => AlbumMenuFactory.Build(
        entry.Folder,
        MenuHooks ?? new AlbumMenuHooks(null, null, null, null, null),
        (FontFamily)Resources["Fa"],
        (FontFamily)Resources["FaRegular"],
        AlbumMenuPlace.Collection);

    // ── the key on the sleeve ──

    /// <summary>The pointer enters a record's sleeve. This is the only time the way into its own page is drawn.
    /// The list is read as a shelf of covers, and a key on every cover all the time would be a column of keys.
    /// The key is found by name inside the row's own template, not held in a field, because the list recycles
    /// its rows.</summary>
    private void Art_PointerEntered(object sender, PointerRoutedEventArgs e) => ShowOpenKey(sender, true);

    private void Art_PointerExited(object sender, PointerRoutedEventArgs e) => ShowOpenKey(sender, false);

    private static void ShowOpenKey(object sender, bool show)
    {
        if ((sender as FrameworkElement)?.FindName("OpenSlot") is not FrameworkElement key)
            return;

        key.Opacity = show ? 1 : 0;
        key.IsHitTestVisible = show;
    }

    /// <summary>Handled here, so that a press on the key is not also a press on the record's block.</summary>
    private void OpenPage_PointerPressed(object sender, PointerRoutedEventArgs e) => e.Handled = true;

    private void OpenPage_Click(object sender, RoutedEventArgs e)
    {
        if ((sender as FrameworkElement)?.Tag is AlbumListEntry entry)
            MenuHooks?.Invoked?.Invoke(AlbumMenuCommand.AlbumPage, entry.Folder);
    }
}
