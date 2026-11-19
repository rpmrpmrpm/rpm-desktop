using RPM.Core.Library;
using RPM.Desktop.Presentation.Main.Search;

namespace RPM.Desktop.Tests;

/// <summary>
/// The library search with no window: what it finds, and that a stopped or failed search answers null
/// instead of throwing, because the view awaits it on the UI thread.
/// </summary>
public class LibrarySearchQueryTests
{
    private static ArtistWallItem Act(string name) => new(name, ["folder"], [], [], 3);

    private static LibrarySearchSources Sources(Func<string, int, IReadOnlyList<SearchSong>>? songs = null) => new(
        [new SearchRecord("Beyoncé/Lemonade", "Lemonade", "Beyoncé", 12), new SearchRecord("AC-DC/Back in Black", "Back in Black", "AC/DC", 10)],
        [Act("Beyoncé"), Act("AC/DC"), Act("Radiohead")],
        [new GenreWallItem("Rock", [], ["AC-DC/Back in Black"])],
        [],
        songs ?? ((_, _) => []));

    /// <summary>Names (artists, genres, playlists) are folded, so "beyonce" finds Beyoncé and "acdc" finds
    /// AC/DC. Records and songs match the words as typed, ignoring case only, as on the phones.</summary>
    [Fact]
    public void Names_are_matched_folded_and_records_as_typed()
    {
        var plain = LibrarySearchQuery.Run(Sources(), "beyonce", CancellationToken.None);
        Assert.NotNull(plain);
        Assert.Equal(["Beyoncé"], plain.Artists.Select(a => a.Name));
        Assert.Empty(plain.Records);

        var accented = LibrarySearchQuery.Run(Sources(), "BEYONCÉ", CancellationToken.None);
        Assert.NotNull(accented);
        Assert.Equal(["Lemonade"], accented.Records.Select(r => r.Album));

        var squashed = LibrarySearchQuery.Run(Sources(), "acdc", CancellationToken.None);
        Assert.NotNull(squashed);
        Assert.Equal(["AC/DC"], squashed.Artists.Select(a => a.Name));
    }

    [Fact]
    public void A_record_is_found_by_its_name_or_who_made_it()
    {
        var found = LibrarySearchQuery.Run(Sources(), "black", CancellationToken.None);

        Assert.NotNull(found);
        Assert.Equal(["Back in Black"], found.Records.Select(r => r.Album));
        Assert.Empty(found.Artists);
    }

    [Fact]
    public void The_songs_are_asked_with_the_words_and_the_ceiling()
    {
        (string Asked, int Most)? asked = null;
        LibrarySearchQuery.Run(Sources((words, most) =>
        {
            asked = (words, most);
            return [];
        }), "acdc", CancellationToken.None);

        Assert.Equal(("acdc", LibrarySearchQuery.SongCeiling), asked);
    }

    [Fact]
    public void Nothing_found_says_so()
    {
        var found = LibrarySearchQuery.Run(Sources(), "zzqx", CancellationToken.None);

        Assert.NotNull(found);
        Assert.True(found.Nothing);
    }

    /// <summary>A search stopped by a newer one answers null and does not throw: every keystroke can stop
    /// the search before it, and a throw would land on the UI thread.</summary>
    [Fact]
    public void A_stopped_search_answers_null_without_throwing()
    {
        using var stop = new CancellationTokenSource();
        stop.Cancel();

        Assert.Null(LibrarySearchQuery.Run(Sources(), "beyonce", stop.Token));
    }

    /// <summary>Stopped while the songs are read: the answer is dropped, not shown half done.</summary>
    [Fact]
    public void A_search_stopped_during_the_songs_answers_null()
    {
        using var stop = new CancellationTokenSource();

        Assert.Null(LibrarySearchQuery.Run(Sources((_, _) =>
        {
            stop.Cancel();
            return [];
        }), "beyonce", stop.Token));
    }

    [Fact]
    public void A_failed_read_answers_null_without_throwing()
        => Assert.Null(LibrarySearchQuery.Run(
            Sources((_, _) => throw new InvalidOperationException("the database is closed")),
            "beyonce",
            CancellationToken.None));
}
