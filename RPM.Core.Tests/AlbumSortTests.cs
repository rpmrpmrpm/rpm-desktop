using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The orders a wall of albums can be put in. The important part is not the sorting itself but where
/// the ties and the missing values go, because that is the part that goes wrong without any error.
/// </summary>
public class AlbumSortTests
{
    private static AlbumFacts Facts(
        string album, string artist, long added = 0, int year = 0, int plays = 0)
        => new(album, artist, added, year, plays);

    private static IReadOnlyList<string> Names(IReadOnlyList<AlbumFacts> wall, AlbumOrdering by)
        => [.. AlbumSort.By(wall, by, f => f).Select(f => f.Album)];

    // ── artist, the default ──

    [Fact]
    public void By_artist_puts_a_shelf_in_order()
    {
        List<AlbumFacts> wall =
        [
            Facts("Kid A", "Radiohead"),
            Facts("Blue", "Joni Mitchell"),
            Facts("Amnesiac", "Radiohead"),
        ];

        Assert.Equal(["Blue", "Amnesiac", "Kid A"], Names(wall, AlbumOrdering.Artist));
    }

    [Fact]
    public void By_album_ignores_who_made_it()
    {
        List<AlbumFacts> wall =
        [
            Facts("Kid A", "Radiohead"),
            Facts("Blue", "Joni Mitchell"),
            Facts("Amnesiac", "Radiohead"),
        ];

        Assert.Equal(["Amnesiac", "Blue", "Kid A"], Names(wall, AlbumOrdering.Album));
    }

    // ── added ──

    [Fact]
    public void By_added_puts_the_newest_first()
    {
        List<AlbumFacts> wall =
        [
            Facts("Old", "A", added: 100),
            Facts("New", "B", added: 300),
            Facts("Middling", "C", added: 200),
        ];

        Assert.Equal(["New", "Middling", "Old"], Names(wall, AlbumOrdering.Added));
    }

    [Fact]
    public void Albums_added_together_fall_back_to_the_shelf_order()
    {
        // A library uploaded at once has one timestamp across half of it, so the order of the tie is
        // what the user actually sees.
        List<AlbumFacts> wall =
        [
            Facts("Zoo", "Wire", added: 100),
            Facts("Aja", "Steely Dan", added: 100),
        ];

        Assert.Equal(["Aja", "Zoo"], Names(wall, AlbumOrdering.Added));
    }

    // ── year ──

    [Fact]
    public void By_year_puts_the_newest_release_first()
    {
        List<AlbumFacts> wall =
        [
            Facts("Older", "A", year: 1971),
            Facts("Newer", "B", year: 1994),
        ];

        Assert.Equal(["Newer", "Older"], Names(wall, AlbumOrdering.Year));
    }

    /// <summary>The important case: half a library has no year tag, and sorting those as zero would
    /// open the wall on its own unlabelled half every time.</summary>
    [Fact]
    public void Albums_with_no_year_go_last_not_first()
    {
        List<AlbumFacts> wall =
        [
            Facts("Untagged", "A"),
            Facts("Tagged", "B", year: 1971),
        ];

        Assert.Equal(["Tagged", "Untagged"], Names(wall, AlbumOrdering.Year));
    }

    [Fact]
    public void Albums_of_the_same_year_fall_back_to_the_shelf_order()
    {
        List<AlbumFacts> wall =
        [
            Facts("Zoo", "Wire", year: 1977),
            Facts("Rumours", "Fleetwood Mac", year: 1977),
        ];

        Assert.Equal(["Rumours", "Zoo"], Names(wall, AlbumOrdering.Year));
    }

    // ── plays ──

    [Fact]
    public void By_plays_puts_the_most_played_first()
    {
        List<AlbumFacts> wall =
        [
            Facts("Once", "A", plays: 1),
            Facts("Often", "B", plays: 40),
            Facts("Never", "C"),
        ];

        Assert.Equal(["Often", "Once", "Never"], Names(wall, AlbumOrdering.Plays));
    }

    [Fact]
    public void Albums_never_played_keep_the_shelf_order_between_them()
    {
        List<AlbumFacts> wall =
        [
            Facts("Zoo", "Wire"),
            Facts("Aja", "Steely Dan"),
            Facts("Played", "Somebody", plays: 3),
        ];

        Assert.Equal(["Played", "Aja", "Zoo"], Names(wall, AlbumOrdering.Plays));
    }

    // ── the facts, read off the tracks ──

    private static CloudTrack Track(long added = 0, int year = 0)
        => new("id" + added + "-" + year, "sha", "a/b.flac", 0, "t", "a", "al", added,
               new CloudTrackTags(Year: year));

    [Fact]
    public void An_album_was_added_when_its_newest_track_was()
    {
        // Uploading takes hours, and a record still going up must not sit below one that finished
        // yesterday.
        Assert.Equal(300, AlbumSort.AddedOf([Track(added: 100), Track(added: 300), Track(added: 200)]));
    }

    [Fact]
    public void An_album_with_no_tracks_was_never_added()
    {
        Assert.Equal(0, AlbumSort.AddedOf([]));
    }

    [Fact]
    public void An_albums_year_is_the_one_most_of_its_tracks_agree_on()
    {
        // A compilation carries each recording's own year on the track it came from. The first row
        // and the highest row are both a year the record was not released in; the tag on most of the
        // rows is the reissue's.
        Assert.Equal(
            1994,
            AlbumSort.YearOf([Track(year: 1971), Track(year: 1994), Track(year: 1994)]));
    }

    [Fact]
    public void An_untagged_album_has_no_year()
    {
        Assert.Equal(0, AlbumSort.YearOf([Track(), Track()]));
    }

    [Fact]
    public void A_year_split_down_the_middle_takes_the_earlier_one()
    {
        // The record it was, not the pressing it came on.
        Assert.Equal(1971, AlbumSort.YearOf([Track(year: 1971), Track(year: 2011)]));
    }

    [Fact]
    public void Untagged_tracks_do_not_drag_an_albums_year_down()
    {
        Assert.Equal(1971, AlbumSort.YearOf([Track(), Track(), Track(year: 1971)]));
    }

    /// <summary>The desktop's mirror counts the years in SQL rather than handing over a million track
    /// rows to be re-counted. Same rule, same answers: one implementation, so the two walls cannot
    /// disagree about what year a record is from.</summary>
    [Fact]
    public void The_same_year_rule_applies_to_years_already_counted()
    {
        Assert.Equal(1994, AlbumSort.YearFrom([(1971, 1), (1994, 2)]));
        Assert.Equal(1971, AlbumSort.YearFrom([(1971, 1), (2011, 1)]));   // a tie goes to the earlier
        Assert.Equal(0, AlbumSort.YearFrom([]));
        Assert.Equal(1971, AlbumSort.YearFrom([(0, 5), (1971, 1)]));      // untagged is not a year
    }

    // ── everything else ──

    [Fact]
    public void An_empty_wall_sorts_to_an_empty_wall()
    {
        Assert.Empty(AlbumSort.By<AlbumFacts>([], AlbumOrdering.Plays, f => f));
    }

    [Fact]
    public void Two_albums_that_are_equal_in_every_way_both_survive_the_sort()
    {
        // A record type compares by value, so a wall holding two identical ones must not come back
        // one shorter.
        List<AlbumFacts> wall = [Facts("Same", "Same"), Facts("Same", "Same")];

        Assert.Equal(2, AlbumSort.By(wall, AlbumOrdering.Artist, f => f).Count);
    }
}
