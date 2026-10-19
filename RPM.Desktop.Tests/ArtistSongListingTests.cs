using RPM.Core.Models;
using RPM.Desktop.Presentation.Main.Artists.Models;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// The listing under All tracks on an artist's page: one list for up to twenty songs, and above that
/// grouped by the first letter of the title.
///
/// <para>The listing fills a record at a time while the page loads and is rebuilt whole after a search.
/// The two paths must give the same groups in the same order, or a search that is typed and cleared
/// would leave a different listing from the one the page opened with.</para>
/// </summary>
public class ArtistSongListingTests
{
    private static ArtistSong Song(string title, string folder = "Artist/Album", uint year = 0) =>
        new(new TrackInfo
            {
                FilePath = $"/music/{folder}/{title}.flac",
                Extension = ".flac",
                Title = title,
                Artist = "Artist",
                Year = year,
            },
            folder, subtitle: folder, duration: "3:00");

    private static ArtistSongListing ByLetter() => new(ArtistTrackGrouping.Letter, "No year");

    private static ArtistSongListing ByYear() => new(ArtistTrackGrouping.Year, "No year");

    private static List<ArtistSong> Songs(params string[] titles) => [.. titles.Select(t => Song(t))];

    /// <summary>The flat list read as groups: each header with the songs after it. A listing with
    /// no headers is one group whose letter is null.</summary>
    private static List<(string? Letter, List<ArtistSong> Songs)> Groups(ArtistSongListing listing)
    {
        var groups = new List<(string? Letter, List<ArtistSong> Songs)>();
        foreach (var item in listing.Items)
        {
            if (item is ArtistSongHeading header)
                groups.Add((header.Text, []));
            else
            {
                if (groups.Count == 0)
                    groups.Add((null, []));
                groups[^1].Songs.Add((ArtistSong)item);
            }
        }
        return groups;
    }

    private static List<string?> Letters(ArtistSongListing listing) => [.. Groups(listing).Select(g => g.Letter)];

    private static List<ArtistSong> Under(ArtistSongListing listing, string letter) =>
        Groups(listing).Single(g => g.Letter == letter).Songs;

    private static List<ArtistSong> Many(int count) =>
        [.. Enumerable.Range(0, count).Select(i => Song($"{(char)('Z' - i % 26)} song {i}"))];

    /// <summary>The songs a test put in, without the filler <see cref="Many"/> adds to cross the
    /// threshold.</summary>
    private static List<ArtistSong> Named(ArtistSongListing listing, string letter) =>
        [.. Under(listing, letter).Where(s => !s.Title.Contains(" song "))];

    [Fact]
    public void Twenty_songs_are_one_group_with_no_heading_in_the_order_given()
    {
        var songs = Many(20);
        var listing = ByLetter();
        listing.Show(songs);

        Assert.Equal(songs, listing.Items);
        Assert.False(listing.IsGrouped);
    }

    [Fact]
    public void Twenty_one_songs_are_grouped_by_letter_in_alphabet_order()
    {
        var listing = ByLetter();
        listing.Show(Many(21));

        Assert.True(listing.IsGrouped);
        Assert.IsType<ArtistSongHeading>(listing.Items[0]);
        Assert.Equal([.. Letters(listing).Order()], Letters(listing));
        Assert.Equal(21, listing.Items.OfType<ArtistSong>().Count());
        Assert.Equal(21, listing.Count);
    }

    [Fact]
    public void A_song_that_starts_with_a_digit_or_symbol_goes_under_hash_after_z()
    {
        var songs = Songs("99 Luftballons", "...Baby One More Time", "Zombie");
        songs.AddRange(Many(20));
        var listing = ByLetter();
        listing.Show(songs);

        Assert.Equal("#", Groups(listing)[^1].Letter);
        Assert.Equal(["99 Luftballons"], Groups(listing)[^1].Songs.Select(s => s.Title));
        // Leading punctuation is skipped, so "...Baby" is a B.
        Assert.Contains(Under(listing, "B"), s => s.Title == "...Baby One More Time");
    }

    [Fact]
    public void An_accented_title_goes_under_its_base_letter()
    {
        var songs = Songs("Éclair", "Ölmusik");
        songs.AddRange(Many(20));
        var listing = ByLetter();
        listing.Show(songs);

        Assert.Contains(Under(listing, "E"), s => s.Title == "Éclair");
        Assert.Contains(Under(listing, "O"), s => s.Title == "Ölmusik");
    }

    [Fact]
    public void Songs_in_a_group_are_sorted_by_title_ignoring_case_and_punctuation()
    {
        var songs = Songs("angie", "(I Can't Get No) Satisfaction", "Imagine", "Anarchy", "Idioteque");
        songs.AddRange(Many(20));
        var listing = ByLetter();
        listing.Show(songs);

        Assert.Equal(["Anarchy", "angie"], Named(listing, "A").Select(s => s.Title));
        Assert.Equal(
            ["(I Can't Get No) Satisfaction", "Idioteque", "Imagine"],
            Named(listing, "I").Select(s => s.Title));
    }

    [Fact]
    public void Two_songs_with_the_same_title_keep_the_order_they_were_given_in()
    {
        var first = Song("Hurt", "Artist/First");
        var second = Song("Hurt", "Artist/Second");
        var songs = new List<ArtistSong> { first };
        songs.AddRange(Many(20));
        songs.Add(second);

        var shown = ByLetter();
        shown.Show(songs);
        var added = ByLetter();
        foreach (var song in songs)
            added.Add(song);

        foreach (var listing in new[] { shown, added })
        {
            Assert.Equal([first, second], Named(listing, "H"));
        }
    }

    /// <summary>The load adds one song at a time and crosses the threshold on the way; a search rebuilds
    /// in one go. Every prefix of the load must look exactly like a rebuild of the same songs.</summary>
    [Fact]
    public void Adding_one_at_a_time_matches_a_rebuild_at_every_count()
    {
        var songs = Songs(
            "Zebra", "apple", "Mango", "banana", "99 Problems", "Éclair", "Apple", "kiwi", "Lemon", "Lime",
            "Cherry", "grape", "Fig", "Date", "Olive", "Peach", "Pear", "Plum", "Quince", "Raisin",
            "Satsuma", "Tangerine", "Ugli", "Vanilla", "Walnut", "Yam", "(Encore) Apple", "Banana");

        var added = ByLetter();
        for (var n = 1; n <= songs.Count; n++)
        {
            added.Add(songs[n - 1]);

            var rebuilt = ByLetter();
            rebuilt.Show(songs[..n]);

            Assert.Equal(n, added.Count);
            Assert.Equal(Letters(rebuilt), Letters(added));
            Assert.Equal(
                rebuilt.Items.Select(i => i is ArtistSongHeading h ? h.Text : i),
                added.Items.Select(i => i is ArtistSongHeading h ? h.Text : i));
        }
    }

    [Fact]
    public void A_search_that_narrows_below_the_threshold_goes_back_to_one_list_in_the_order_given()
    {
        var songs = Many(30);
        var listing = ByLetter();
        listing.Show(songs);
        Assert.True(listing.IsGrouped);

        listing.Show(songs[..5]);

        Assert.Equal(songs[..5], listing.Items);
    }

    [Fact]
    public void Clearing_empties_the_listing_and_forgets_the_pointer()
    {
        var songs = Many(25);
        var listing = ByLetter();
        listing.Show(songs);
        songs[3].IsHovered = true;

        listing.Clear();

        Assert.Empty(listing.Items);
        Assert.Equal(0, listing.Count);
        Assert.False(songs[3].IsHovered);
    }

    [Fact]
    public void An_empty_search_result_has_no_groups()
    {
        var listing = ByLetter();
        listing.Show(Many(25));
        listing.Show([]);

        Assert.Empty(listing.Items);
        Assert.Equal(0, listing.Count);
    }

    /// <summary>Songs from 2001, 1999, none and 2010, spread over records in that order, plus filler
    /// with no year to cross the threshold.</summary>
    private static List<ArtistSong> Dated()
    {
        var songs = new List<ArtistSong>
        {
            Song("Beta", "A/2001", 2001), Song("Alpha", "A/2001", 2001),
            Song("Old", "B/1999", 1999),
            Song("Undated", "C/None"),
            Song("Zed", "D/2010", 2010), Song("Aye", "D/2010", 2010),
        };
        songs.AddRange(Many(20));
        return songs;
    }

    [Fact]
    public void By_year_the_newest_year_comes_first_and_songs_with_no_year_last()
    {
        var listing = ByYear();
        listing.Show(Dated());

        Assert.Equal(["2010", "2001", "1999", "No year"], Letters(listing));
    }

    [Fact]
    public void By_year_a_group_keeps_the_order_the_songs_were_given_in()
    {
        var listing = ByYear();
        listing.Show(Dated());

        // Record order, not title order: Zed before Aye, Beta before Alpha.
        Assert.Equal(["Zed", "Aye"], Under(listing, "2010").Select(s => s.Title));
        Assert.Equal(["Beta", "Alpha"], Under(listing, "2001").Select(s => s.Title));
        Assert.Equal("Undated", Under(listing, "No year")[0].Title);
    }

    [Fact]
    public void By_year_adding_one_at_a_time_matches_a_rebuild_at_every_count()
    {
        var songs = Dated();
        songs.Insert(3, Song("Later", "B/1999", 1999));
        songs.Add(Song("Last", "E/2001", 2001));

        var added = ByYear();
        for (var n = 1; n <= songs.Count; n++)
        {
            added.Add(songs[n - 1]);

            var rebuilt = ByYear();
            rebuilt.Show(songs[..n]);

            Assert.Equal(
                rebuilt.Items.Select(i => i is ArtistSongHeading h ? h.Text : i),
                added.Items.Select(i => i is ArtistSongHeading h ? h.Text : i));
        }
    }

    /// <summary>Switching and switching back gives the listing the page opened with, because both
    /// groupings are built from the order the songs were given in and not from each other.</summary>
    [Fact]
    public void Switching_to_year_and_back_gives_the_same_listing()
    {
        var songs = Dated();
        var listing = ByLetter();
        listing.Show(songs);
        var before = listing.Items.Select(i => i is ArtistSongHeading h ? h.Text : i).ToList();

        listing.GroupBy(ArtistTrackGrouping.Year);
        Assert.Equal(ArtistTrackGrouping.Year, listing.Grouping);
        Assert.Equal("2010", Letters(listing)[0]);

        listing.GroupBy(ArtistTrackGrouping.Letter);
        Assert.Equal(before, listing.Items.Select(i => i is ArtistSongHeading h ? h.Text : i));
    }

    /// <summary>The switch can be pressed while the page is still filling: songs that arrive after it
    /// go under the new grouping, and the result is the same as if that grouping had been used from
    /// the start.</summary>
    [Fact]
    public void Switching_in_the_middle_of_a_load_matches_a_listing_that_had_that_grouping_all_along()
    {
        var songs = Dated();
        var switched = ByLetter();
        for (var n = 0; n < songs.Count; n++)
        {
            if (n == 24)
                switched.GroupBy(ArtistTrackGrouping.Year);
            switched.Add(songs[n]);
        }

        var fromStart = ByYear();
        fromStart.Show(songs);

        Assert.Equal(
            fromStart.Items.Select(i => i is ArtistSongHeading h ? h.Text : i),
            switched.Items.Select(i => i is ArtistSongHeading h ? h.Text : i));
    }

    [Fact]
    public void A_short_listing_keeps_the_grouping_for_when_it_grows_and_shows_no_headings()
    {
        var listing = ByLetter();
        listing.Show(Many(5));
        listing.GroupBy(ArtistTrackGrouping.Year);

        Assert.DoesNotContain(listing.Items, i => i is ArtistSongHeading);

        foreach (var song in Many(20))
            listing.Add(song);
        Assert.Equal(["No year"], Letters(listing));
    }

    [Fact]
    public void Changed_is_raised_on_every_change_so_the_switch_can_follow_IsGrouped()
    {
        var listing = ByLetter();
        var seen = new List<bool>();
        listing.Changed += () => seen.Add(listing.IsGrouped);

        foreach (var song in Many(21))
            listing.Add(song);
        listing.Show(Many(3));
        listing.Clear();

        Assert.Equal(23, seen.Count);
        Assert.False(seen[19]);
        Assert.True(seen[20]);
        Assert.False(seen[21]);
        Assert.False(seen[22]);
    }
}
