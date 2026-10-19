using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// What a column filter asks the database. The filter is a way of matching and a piece of text, not
/// a set of ticked values: a list of every distinct artist in a million-row library is a scan, and
/// the user is looking for a word.
/// </summary>
public class TrackTableFilterTests : IDisposable
{
    private readonly string _root;
    private readonly LibraryDatabase _db;

    public TrackTableFilterTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpm-filter-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
        _db = new LibraryDatabase(MirrorLocation.InFolder(_root));
    }

    public void Dispose()
    {
        _db.Dispose();
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private void Add(string title, string artist = "Fleetwood Mac")
    {
        _db.Upsert(new LibraryTrack
        {
            RelativePath = $"{artist}/{title}.flac",
            Folder = artist,
            FileName = $"{title}.flac",
            Extension = ".flac",
            Artist = artist,
            AlbumArtist = artist,
            Album = "Rumours",
            Title = title,
            ModifiedUtcTicks = 1,
        });
    }

    private List<string> Titles(TextMatchMode mode, string text, string column = "title")
    {
        var match = TextMatch.Of(mode, text);
        Assert.NotNull(match);

        var query = new TrackTableQuery(Filters: [new TrackTableFilter(column, match)]);
        return [.. _db.QueryTracksByIds(_db.QueryTrackIds(query)).Select(t => t.Title ?? "")];
    }

    [Fact]
    public void Starts_with_matches_only_the_beginning()
    {
        Add("Go Your Own Way");
        Add("Never Going Back Again");

        Assert.Equal(["Go Your Own Way"], Titles(TextMatchMode.StartsWith, "Go"));
    }

    [Fact]
    public void Contains_matches_anywhere()
    {
        Add("Go Your Own Way");
        Add("Never Going Back Again");
        Add("The Chain");

        Assert.Equal(["Go Your Own Way", "Never Going Back Again"], Titles(TextMatchMode.Contains, "Go"));
    }

    [Fact]
    public void Ends_with_matches_only_the_end()
    {
        Add("Go Your Own Way");
        Add("Wayfarer");

        Assert.Equal(["Go Your Own Way"], Titles(TextMatchMode.EndsWith, "Way"));
    }

    [Fact]
    public void The_match_ignores_case()
    {
        Add("Dreams");

        Assert.Equal(["Dreams"], Titles(TextMatchMode.Contains, "DREAM"));
    }

    /// <summary>A % or an _ in the text is a character the user typed, not a wildcard.</summary>
    [Fact]
    public void Wildcards_in_the_text_are_matched_literally()
    {
        Add("100% Pure Love");
        Add("Dreams");

        Assert.Equal(["100% Pure Love"], Titles(TextMatchMode.Contains, "0% P"));
        Assert.Empty(Titles(TextMatchMode.Contains, "Dre_ms"));
    }

    [Fact]
    public void A_filter_on_another_column_narrows_the_same_way()
    {
        Add("Dreams");
        Add("Blue Monday", artist: "New Order");

        Assert.Equal(["Blue Monday"], Titles(TextMatchMode.StartsWith, "New", column: "artist"));
    }

    /// <summary>Nothing typed is not a filter that matches nothing. It is no filter, and there is no
    /// way to build one: the flyout's Clear and an empty box are the same answer.</summary>
    [Fact]
    public void There_is_no_match_for_empty_text()
    {
        Assert.Null(TextMatch.Of(TextMatchMode.Contains, null));
        Assert.Null(TextMatch.Of(TextMatchMode.Contains, ""));
        Assert.Null(TextMatch.Of(TextMatchMode.Contains, "   "));
    }

    [Fact]
    public void The_text_is_trimmed()
    {
        Assert.Equal("Go", TextMatch.Of(TextMatchMode.StartsWith, "  Go  ")!.Text);
    }

    /// <summary>A name the whitelist does not know reaches no SQL, so the table is not narrowed by
    /// it and the query still answers.</summary>
    [Fact]
    public void A_column_the_table_may_not_touch_filters_nothing()
    {
        Add("Dreams");

        Assert.Equal(["Dreams"], Titles(TextMatchMode.Contains, "zzz", column: "drop table tracks"));
    }
}
