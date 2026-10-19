using RPM.Core.Library;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class QueueFilterTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public QueueFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"mdfilter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        // The rule is a preference and lives in a static, so a test that changes it puts it back.
        QueueTags.Required = QueueTags.Default;
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    private static QueueTrack Row(
        string fileName, string? artist, string? title, string? album,
        MetadataSource src = MetadataSource.Tags, bool @checked = false, int splitHint = 0,
        FileAvailability availability = FileAvailability.Local)
    {
        var current = new TrackInfo
        {
            FilePath = $"/music/in/{fileName}",
            Extension = Path.GetExtension(fileName),
            DurationSeconds = 200,
            Artist = artist,
            AlbumArtist = artist,
            Title = title,
            Album = album,
            Availability = availability,
            Source = src,
        };
        return new QueueTrack
        {
            Source = "scan",
            Folder = "in",
            FileName = fileName,
            IsChecked = @checked,
            SplitHint = splitHint,
            Current = current,
            Wanted = current,
        };
    }

    private void Seed() => _db.QueueInsertMany(
    [
        Row("a.flac", "Enya", "Watermark", "Watermark", @checked: true),
        Row("b.flac", null, null, "Watermark"),                                 // not identified
        Row("c.mp3", "Enya", "Orinoco Flow", "Watermark", splitHint: 1),        // album in one file
        Row("d.flac", "Enya", "Storms", "Shepherd Moons",
            availability: FileAvailability.Unreadable),
        Row("e.flac", "Enya", "Ebudae", "Shepherd Moons",
            availability: FileAvailability.Dataless),
    ]);

    /// <summary>One record where each of the two tracks is in the queue twice, as the same track under
    /// two file names. This is what the duplicate pass judges on.</summary>
    private void SeedWithDuplicates() => _db.QueueInsertMany(
    [
        Row("a.flac", "Enya", "Orinoco Flow", "Watermark"),
        Row("b.flac", "Enya", "Cursum Perficio", "Watermark"),
        Row("a-copy.mp3", "Enya", "Orinoco Flow", "Watermark"),
        Row("b-copy.mp3", "Enya", "Cursum Perficio", "Watermark"),
    ]);

    private int Rows(QueueFilter f) => _db.Queue(f).Groups().Sum(g => g.TrackCount);

    [Fact]
    public void No_filter_shows_everything()
    {
        Seed();
        Assert.Equal(5, Rows(QueueFilter.None));
    }

    [Fact]
    public void Ticked_only_shows_the_ticked_rows()
    {
        Seed();
        Assert.Equal(1, Rows(new QueueFilter { TickedOnly = true }));
    }

    [Fact]
    public void Tags_missing_shows_rows_that_are_short_of_what_the_settings_ask_for()
    {
        // The seeded rows have an artist, a title and an album and nothing else. Under the default
        // rule, which asks for all seven tags, every readable row is missing something. The unreadable
        // and cloud-only rows are excluded: neither is a file the user can fix from here.
        Seed();
        Assert.Equal(3, Rows(new QueueFilter { NeedsRecognition = true }));
    }

    [Fact]
    public void The_rule_is_the_users_own()
    {
        Seed();

        // Asked only for the two that name a track, the one row with neither is the only one marked…
        QueueTags.Required = [QueueTag.Artist, QueueTag.Title];
        Assert.Equal(1, Rows(new QueueFilter { NeedsRecognition = true }));

        // …and asking for a genre nobody has tagged brings all of them back.
        QueueTags.Required = [QueueTag.Genre];
        Assert.Equal(3, Rows(new QueueFilter { NeedsRecognition = true }));
    }

    /// <summary>The style is the one tag not asked for by default. A style is narrower than most
    /// collections are tagged, so asking for it by default would mark half of a normal collection on
    /// the day the field appeared, and users would learn to ignore the mark.</summary>
    [Fact]
    public void The_style_is_offered_but_not_asked_for_by_default()
    {
        Assert.Contains(QueueTag.Subgenre, QueueTags.All);
        Assert.DoesNotContain(QueueTag.Subgenre, QueueTags.Default);
        Assert.Equal(QueueTags.All.Count - 1, QueueTags.Default.Count);
    }

    /// <summary>And it is one tick away. The clause it adds is its own. If MissingSql ended in a
    /// default arm that reads the genre's clause, a tag added to the enum would quietly count files
    /// missing a genre as files missing a style.</summary>
    [Fact]
    public void Asking_for_a_style_asks_about_the_style()
    {
        Seed();

        QueueTags.Required = [QueueTag.Subgenre];
        Assert.Contains("w_subgenre", QueueTags.MissingSql());
        Assert.DoesNotContain("w_genre", QueueTags.MissingSql());
        Assert.Equal(3, Rows(new QueueFilter { NeedsRecognition = true }));
    }

    [Fact]
    public void An_empty_rule_is_no_rule_at_all_and_is_refused()
    {
        // A set that nobody has to satisfy would mark every file complete and turn off the bench's
        // one warning without saying so.
        QueueTags.Required = [];
        Assert.Equal(QueueTags.Default, QueueTags.Required);
    }

    [Fact]
    public void Unreadable_and_no_data_are_different_chips()
    {
        Seed();
        Assert.Equal(1, Rows(new QueueFilter { Unreadable = true }));
        Assert.Equal(1, Rows(new QueueFilter { NoData = true }));
    }

    [Fact]
    public void Split_candidates_shows_rows_with_a_hint()
    {
        Seed();
        Assert.Equal(1, Rows(new QueueFilter { SplitCandidates = true }));
    }

    [Fact]
    public void Hide_duplicates_takes_away_exactly_what_the_clean_up_would_delete()
    {
        // Two copies of one track: the duplicate pass keeps the better one and marks the other. The
        // chip must hide that one and nothing else, because the clean-up next to it deletes by the
        // same rule. A chip that hid more would be the more dangerous of the two.
        _db.QueueInsertMany(
        [
            Row("keep.flac", "Enya", "Orinoco Flow", "Watermark"),
            Row("lesser.mp3", "Enya", "Orinoco Flow", "Watermark"),
            Row("other.flac", "Enya", "Storms", "Shepherd Moons"),
        ]);
        _db.QueueRefreshDuplicates();

        var marked = _db.QueueDuplicateCount();
        Assert.Equal(1, marked);
        Assert.Equal(3 - marked, Rows(new QueueFilter { HideDuplicates = true }));
    }

    [Fact]
    public void Select_all_ticks_what_the_filter_is_showing_and_nothing_else()
    {
        Seed();                     // five rows, one of them a .mp3, one already ticked
        _db.WholeQueue.CheckAll(false);   // from a clean slate, so what is counted is what the press did

        _db.Queue(new QueueFilter { Extension = ".mp3" }).CheckAll(true);

        Assert.Equal(1, _db.QueueTickedFacts().Ticked);
    }

    [Fact]
    public void Select_none_leaves_the_ticks_the_filter_is_not_showing()
    {
        // …which is only ever a temporary state: narrowing gives those ticks up (below). What is
        // checked here is that "none" means the rows in front of the user, the same as "all" does.
        Seed();
        _db.WholeQueue.CheckAll(true);
        _db.Queue(new QueueFilter { Extension = ".mp3" }).CheckAll(false);

        Assert.Equal(4, _db.QueueTickedFacts().Ticked);
    }

    [Fact]
    public void Narrowing_the_queue_gives_up_the_ticks_it_hides()
    {
        Seed();
        _db.WholeQueue.CheckAll(true);

        var dropped = _db.Queue(new QueueFilter { Extension = ".mp3" }).UncheckHidden();

        Assert.Equal(4, dropped);
        Assert.Equal(1, _db.QueueTickedFacts().Ticked);
    }

    [Fact]
    public void The_ticked_only_chip_does_not_untick_the_view_it_is_drawing()
    {
        // "Show me what is ticked" must not mean "untick what is not shown": that would shrink the
        // view row by row. The chip is left out of the hidden test.
        Seed();
        _db.WholeQueue.CheckAll(true);

        Assert.Equal(0, _db.Queue(new QueueFilter { TickedOnly = true }).UncheckHidden());
        Assert.Equal(5, _db.QueueTickedFacts().Ticked);
    }

    [Fact]
    public void An_unnarrowed_queue_hides_nothing_and_gives_up_nothing()
    {
        Seed();
        _db.WholeQueue.CheckAll(true);

        Assert.Equal(0, _db.Queue(QueueFilter.None).UncheckHidden());
        Assert.Equal(5, _db.QueueTickedFacts().Ticked);
    }

    [Fact]
    public void An_action_never_reaches_a_row_the_queue_is_hiding()
    {
        // Everything is ticked, then the queue is narrowed to one file kind. What an action receives
        // is the one row on screen. Numbering 1 to n over a set that has invisible rows would produce
        // numbers that skip, with nothing on screen to say why.
        Seed();
        _db.WholeQueue.CheckAll(true);

        Assert.Equal(5, _db.WholeQueue.CheckedRows().Count);
        Assert.Equal(
            ["c.mp3"],
            _db.Queue(new QueueFilter { Extension = ".mp3" }).CheckedRows().Select(r => r.FileName));
    }

    [Fact]
    public void Kind_narrows_to_one_extension()
    {
        Seed();
        Assert.Equal(1, Rows(new QueueFilter { Extension = ".mp3" }));
    }

    [Fact]
    public void Chips_and_search_narrow_together()
    {
        Seed();
        Assert.Equal(1, Rows(new QueueFilter { Search = "Orinoco" }));
        Assert.Equal(0, Rows(new QueueFilter { TickedOnly = true, Search = "Orinoco" }));
    }

    [Fact]
    public void A_group_emptied_by_a_filter_leaves_the_skeleton()
    {
        Seed();
        var groups = _db.Queue(new QueueFilter { SplitCandidates = true }).Groups();

        Assert.Single(groups);
        Assert.Equal("Watermark", groups[0].Album);
    }

    [Fact]
    public void A_page_reads_only_the_filtered_rows_of_its_group()
    {
        Seed();
        var page = _db.Queue(new QueueFilter { NoData = true }).GroupPage("Enya", "Shepherd Moons", 0, 50);

        Assert.Single(page);
        Assert.Equal("e.flac", page[0].FileName);
    }

    [Fact]
    public void Chip_counts_are_of_the_whole_queue()
    {
        Seed();
        var counts = _db.QueueChipCounts();

        Assert.Equal(5, counts.Total);
        Assert.Equal(1, counts.Ticked);
        Assert.Equal(3, counts.NeedsRecognition);   // every readable row is short of a tag or six
        Assert.Equal(1, counts.Unreadable);
        Assert.Equal(1, counts.NoData);
        Assert.Equal(1, counts.SplitCandidates);
    }

    [Fact]
    public void Distinct_extensions_are_offered_in_order()
    {
        Seed();
        Assert.Equal([".flac", ".mp3"], _db.QueueDistinctExtensions());
    }

    /// <summary>
    /// Tick a record, hide the duplicates, number the tracks: the rows the chip hides must not
    /// be numbered too.
    ///
    /// <para>Three separate promises, all about the same ticked set: hiding gives up the ticks it
    /// hides, an action reads the ticked set through the narrowing, and what comes back is only what
    /// is on screen. Each is checked here on the rows themselves. Together they mean that an action
    /// never touches what the user cannot see.</para>
    /// </summary>
    [Fact]
    public void Hiding_duplicates_gives_up_their_ticks_and_no_action_can_reach_them()
    {
        SeedWithDuplicates();
        _db.QueueRefreshDuplicates();

        var hidden = new QueueFilter { HideDuplicates = true };

        // The record is ticked whole, before anything is hidden.
        _db.WholeQueue.CheckAll(true);
        Assert.Equal(4, _db.WholeQueue.CheckedRows().Count);

        // The chip goes on: what it hides gives up its tick.
        _db.Queue(hidden).UncheckHidden();

        Assert.Equal(2, _db.QueueCheckedCount());
        Assert.Equal(
            ["a.flac", "b.flac"],
            _db.Queue(hidden).CheckedRows().Select(r => r.FileName).OrderBy(n => n));
        Assert.Equal(
            ["a.flac", "b.flac"],
            _db.WholeQueue.CheckedRows().Select(r => r.FileName).OrderBy(n => n));
        Assert.Equal(
            ["a.flac", "b.flac"],
            _db.Queue(hidden).RowsInGroup("Enya", "Watermark").Select(r => r.FileName).OrderBy(n => n));
    }

    /// <summary>
    /// A record's own tick box must tick only the rows that are shown. If it ticked every row
    /// under it, then hiding the duplicates and ticking the album would put the hidden copies into
    /// the selection, where the next action would find them.
    /// </summary>
    [Fact]
    public void A_records_tick_only_reaches_the_rows_it_is_standing_over()
    {
        SeedWithDuplicates();
        _db.QueueRefreshDuplicates();
        var hidden = new QueueFilter { HideDuplicates = true };

        _db.Queue(hidden).SetCheckedInGroup("Enya", "Watermark", true);

        Assert.Equal(2, _db.QueueCheckedCount());
        Assert.Equal(
            ["a.flac", "b.flac"],
            _db.WholeQueue.CheckedRows().Select(r => r.FileName).OrderBy(n => n));
    }

    /// <summary>And the box itself shows what it covers, so it does not look half-ticked over a
    /// record whose shown rows are all ticked.</summary>
    [Fact]
    public void A_records_box_counts_what_it_is_standing_over()
    {
        SeedWithDuplicates();
        _db.QueueRefreshDuplicates();
        var hidden = new QueueFilter { HideDuplicates = true };

        _db.Queue(hidden).SetCheckedInGroup("Enya", "Watermark", true);

        Assert.Equal((2, 2), _db.Queue(hidden).GroupCheckState("Enya", "Watermark"));
        Assert.Equal((2, 4), _db.WholeQueue.GroupCheckState("Enya", "Watermark"));
    }
}
