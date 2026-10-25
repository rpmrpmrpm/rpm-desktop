using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The letter wheel of the TV screens' album carousel: which stops a wall runs through, and how far the
/// endless wall moves to reach one.
/// </summary>
public class AlbumLettersTests
{
    private static AlbumFacts Facts(string album, string artist, int year = 0)
        => new(album, artist, 0, year, 0);

    [Fact]
    public void By_artist_the_stops_are_the_artists_first_letters_in_wall_order()
    {
        List<AlbumFacts> wall =
        [
            Facts("Blue", "Joni Mitchell"),
            Facts("Court and Spark", "Joni Mitchell"),
            Facts("Amnesiac", "Radiohead"),
            Facts("Kid A", "Radiohead"),
            Facts("Ágætis byrjun", "Sigur Rós"),
        ];

        var stops = AlbumLetters.For(wall, AlbumOrdering.Artist, f => f);

        Assert.Equal([new WallLetter("J", 0), new WallLetter("R", 2), new WallLetter("S", 4)], stops);
    }

    [Fact]
    public void By_artist_a_name_that_starts_with_a_symbol_is_filed_under_hash_where_the_wall_sorts_it()
    {
        // The wall sorts by the whole name, so these four come before A. Filed by their first letter instead
        // (T, N, U), the wheel read "#, T, N, U, A, B ..." (seen in Pegasus, 2026-10-25).
        List<AlbumFacts> wall =
        [
            Facts("Wallop", "!!!"),
            Facts("The Rocky Horror Picture Show", "\"The Rocky Horror Picture Show\" Original Cast"),
            Facts("No Strings Attached", "*NSYNC"),
            Facts("Eternal Grey", "$uicideboy$"),
            Facts("Abbey Road", "Beatles"),
            Facts("Ágætis byrjun", "Ólafur Arnalds"),
        ];

        var stops = AlbumLetters.For(wall, AlbumOrdering.Artist, f => f);

        Assert.Equal([new WallLetter("#", 0), new WallLetter("B", 4), new WallLetter("O", 5)], stops);
    }

    [Fact]
    public void By_album_the_stops_are_the_titles_first_letters_with_accents_folded()
    {
        List<AlbumFacts> wall =
        [
            Facts("1999", "Prince"),
            Facts("Ágætis byrjun", "Sigur Rós"),
            Facts("Amnesiac", "Radiohead"),
            Facts("Blue", "Joni Mitchell"),
        ];

        var stops = AlbumLetters.For(wall, AlbumOrdering.Album, f => f);

        Assert.Equal([new WallLetter("#", 0), new WallLetter("A", 1), new WallLetter("B", 3)], stops);
    }

    [Fact]
    public void By_date_the_stops_are_decades_and_no_year_is_hash()
    {
        List<AlbumFacts> wall =
        [
            Facts("Blue", "Joni Mitchell", 1971),
            Facts("Court and Spark", "Joni Mitchell", 1974),
            Facts("Kid A", "Radiohead", 2000),
            Facts("Untitled", "Nobody"),
        ];

        var stops = AlbumLetters.For(wall, AlbumOrdering.Date, f => f);

        Assert.Equal([new WallLetter("1970", 0), new WallLetter("2000", 2), new WallLetter("#", 3)], stops);
    }

    [Theory]
    [InlineData(AlbumOrdering.Added)]
    [InlineData(AlbumOrdering.Plays)]
    [InlineData(AlbumOrdering.Year)]
    public void Other_orders_have_no_wheel(AlbumOrdering ordering)
    {
        List<AlbumFacts> wall = [Facts("Blue", "Joni Mitchell", 1971)];

        Assert.False(AlbumLetters.HasLetters(ordering));
        Assert.Empty(AlbumLetters.For(wall, ordering, f => f));
    }

    [Fact]
    public void A_letter_the_wall_reaches_twice_is_listed_once_at_its_first_run()
    {
        List<AlbumFacts> wall = [Facts("A", "x"), Facts("B", "x"), Facts("a again", "x")];

        var stops = AlbumLetters.For(wall, AlbumOrdering.Album, f => f);

        Assert.Equal([new WallLetter("A", 0), new WallLetter("B", 1)], stops);
    }

    [Theory]
    [InlineData(0, 3, 10, 3)]      // forwards is shorter
    [InlineData(0, 8, 10, -2)]     // backwards round the end is shorter
    [InlineData(12, 1, 10, -1)]    // the count has gone past the end once
    [InlineData(-3, 9, 10, 2)]     // …or before the start
    [InlineData(0, 5, 10, 5)]      // half way: forwards
    [InlineData(4, 4, 10, 0)]
    [InlineData(0, 0, 0, 0)]
    public void The_jump_goes_the_shorter_way_round(int from, int to, int count, int expected)
        => Assert.Equal(expected, AlbumLetters.ShorterWay(from, to, count));

    [Theory]
    [InlineData(-1, 5, 4)]
    [InlineData(5, 5, 0)]
    [InlineData(13, 5, 3)]
    [InlineData(3, 0, 0)]
    public void An_endless_count_is_taken_round_the_wall(int slot, int count, int expected)
        => Assert.Equal(expected, AlbumLetters.Wrap(slot, count));
}
