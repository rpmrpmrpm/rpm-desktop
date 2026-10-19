using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// The row under an album: other records on the same shelf, ranked on what the library actually knows
/// about them.
///
/// <para>The tests worth having here are about the RANKING, because the ranking is the part that can
/// be wrong without looking wrong. A row of nine records always looks like a row of nine records. A
/// row built by walking a dictionary in insertion order would look just as convincing, but would
/// show whatever sorts first in the collection.</para>
/// </summary>
public class SimilarAlbumsTests
{
    private const int Row = SimilarAlbums.DefaultCount;

    private static AlbumTraits Album(
        string artist, string album, string[]? genres = null, int year = 0, string? folder = null)
        => new(folder ?? $"{artist}/{album}", album, artist, genres ?? [], year);

    // ── what gets in at all ──

    [Fact]
    public void The_album_being_looked_at_is_not_suggested_back()
    {
        var subject = Album("Daniel Johnston", "Fun", ["Folk"], 1994);
        var picked = SimilarAlbums.Pick(subject, [subject, Album("Somebody Else", "A Record", ["Folk"], 1994)], Row);

        Assert.DoesNotContain(picked, p => p.Title == "Fun");
    }

    /// <summary>The same record ripped once and downloaded once is two folders and one record. They are
    /// matched on the folded artist and title, so it is one card.</summary>
    [Fact]
    public void The_same_record_in_two_folders_is_one_suggestion()
    {
        var picked = SimilarAlbums.Pick(
            Album("Daniel Johnston", "Fun"),
            [
                Album("Neutral Milk Hotel", "Aeroplane", folder: "ripped/aeroplane"),
                Album("The Neutral Milk Hotel", "aeroplane!", folder: "downloaded/aeroplane"),
            ],
            Row);

        Assert.Single(picked);
    }

    [Fact]
    public void A_row_in_the_mirror_with_no_title_is_not_an_album()
    {
        var picked = SimilarAlbums.Pick(
            Album("Daniel Johnston", "Fun"),
            [Album("Some Act", ""), Album("Another Act", "A Record")],
            Row);

        Assert.Single(picked);
    }

    [Fact]
    public void The_row_never_grows_past_what_was_asked_for()
    {
        var shelf = Enumerable.Range(0, 40).Select(i => Album($"Act {i}", $"Record {i}")).ToList();
        Assert.Equal(3, SimilarAlbums.Pick(Album("Somebody", "Something"), shelf, 3).Count);
    }

    [Fact]
    public void Asking_for_nothing_gives_nothing()
        => Assert.Empty(SimilarAlbums.Pick(
            Album("Daniel Johnston", "Fun"), [Album("Daniel Johnston", "Yip Jump Music")], 0));

    // ── the axes ──

    /// <summary>The row is somewhere ELSE to go. The rest of this act's shelf is one press away on
    /// their own page, and spending the row on it would use nine places on something that adds
    /// nothing.</summary>
    [Fact]
    public void The_acts_own_records_are_never_in_their_own_row()
    {
        var shelf = new List<AlbumTraits>();
        for (var i = 0; i < 20; i++)
            shelf.Add(Album("Daniel Johnston", $"Record {i}", ["Folk"], 1990));
        for (var i = 0; i < 20; i++)
            shelf.Add(Album($"Act {i}", $"Other {i}", ["Folk"], 1990));

        var picked = SimilarAlbums.Pick(Album("Daniel Johnston", "Fun", ["Folk"], 1990), shelf, Row);

        Assert.DoesNotContain(picked, p => p.Artist == "Daniel Johnston");
        Assert.Equal(Row, picked.Count);
    }

    /// <summary>An act whose name is spelled two ways is still one act. Without the folding the row
    /// would show "The Beatles" under a record by "Beatles".</summary>
    [Fact]
    public void The_acts_own_records_are_out_however_their_name_is_spelled()
    {
        var picked = SimilarAlbums.Pick(
            Album("The Beatles", "Revolver", ["Rock"], 1966),
            [Album("Beatles", "Rubber Soul", ["Rock"], 1965), Album("The Kinks", "Face to Face", ["Rock"], 1966)],
            Row);

        Assert.Equal("Face to Face", Assert.Single(picked).Title);
    }

    /// <summary>Nine cards, nine acts. The shelf has plenty of both, so there is no reason to show a
    /// second record by anybody.</summary>
    [Fact]
    public void Every_card_is_a_different_act_while_the_shelf_has_them()
    {
        var shelf = new List<AlbumTraits>();
        for (var i = 0; i < 20; i++)
            for (var n = 0; n < 3; n++)
                shelf.Add(Album($"Act {i}", $"Record {i}-{n}", ["Folk"], 1990));

        var picked = SimilarAlbums.Pick(Album("Subject", "Fun", ["Folk"], 1990), shelf, Row);

        Assert.Equal(Row, picked.Count);
        Assert.Equal(Row, picked.Select(p => p.Artist).Distinct().Count());
    }

    /// <summary>The spread is a preference, not a rule: a shelf with three other acts on it cannot show
    /// nine, and a row three cards long helps nobody. It goes round again and takes second
    /// records.</summary>
    [Fact]
    public void A_thin_shelf_fills_the_row_with_seconds_rather_than_running_short()
    {
        var shelf = new List<AlbumTraits>();
        for (var i = 0; i < 3; i++)
            for (var n = 0; n < 5; n++)
                shelf.Add(Album($"Act {i}", $"Record {i}-{n}", ["Folk"], 1990));

        var picked = SimilarAlbums.Pick(Album("Subject", "Fun", ["Folk"], 1990), shelf, Row);

        Assert.Equal(Row, picked.Count);
        Assert.Equal(3, picked.Select(p => p.Artist).Distinct().Count());

        // Every act got its turn before any act got a second card.
        Assert.Equal(3, picked.Take(3).Select(p => p.Artist).Distinct().Count());
    }

    /// <summary>A shelf holding nothing but the act being looked at has nowhere else to send anybody.
    /// It says so by being empty, and does not suggest the act back to itself.</summary>
    [Fact]
    public void A_shelf_of_only_this_act_gives_an_empty_row()
        => Assert.Empty(SimilarAlbums.Pick(
            Album("Daniel Johnston", "Fun", ["Folk"], 1994),
            [Album("Daniel Johnston", "Yip Jump Music", ["Folk"], 1983)],
            Row));

    [Fact]
    public void A_record_in_the_same_genre_beats_one_in_another()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Folk"], 1994),
            [Album("Act A", "Metal Record", ["Metal"], 1994), Album("Act B", "Folk Record", ["Folk"], 1994)],
            Row);

        Assert.Equal("Folk Record", picked[0].Title);
        Assert.Equal(SimilarBecause.SameGenre, picked[0].Why);
    }

    /// <summary>Sharing one of two labels says more than sharing one of nine. Without this a record
    /// filed under everything would be the best match for every record on the shelf.</summary>
    [Fact]
    public void Sharing_a_genre_is_worth_less_when_the_record_is_filed_under_everything()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Folk"], 1994),
            [
                Album("Act A", "Filed Under Everything",
                    ["Folk", "Metal", "Jazz", "Pop", "Classical", "Reggae"], 1994),
                Album("Act B", "A Folk Record", ["Folk"], 1994),
            ],
            Row);

        Assert.Equal("A Folk Record", picked[0].Title);
    }

    /// <summary>The "similar artists" axis. Neither record says what it is, but their MAKERS' other
    /// records do. This is the reason the profiles are pooled across a catalogue.</summary>
    [Fact]
    public void An_untagged_record_is_placed_by_the_company_its_maker_keeps()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Folk"], 1994),
            [
                // Says nothing itself; its maker is a metal act everywhere else on the shelf.
                Album("Metal Act", "Untagged Record", [], 1994),
                Album("Metal Act", "Their Loud One", ["Metal"], 1990),

                // Says nothing itself; its maker is a folk act everywhere else on the shelf.
                Album("Folk Act", "Also Untagged", [], 1994),
                Album("Folk Act", "Their Quiet One", ["Folk"], 1990),
            ],
            Row);

        var untagged = picked.First(p => p.Title is "Untagged Record" or "Also Untagged");
        Assert.Equal("Also Untagged", untagged.Title);
        Assert.Equal(SimilarBecause.LikeTheArtist, untagged.Why);
    }

    [Fact]
    public void A_record_from_the_same_years_beats_one_from_another_age()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Folk"], 1994),
            [Album("Act A", "Ancient", ["Folk"], 1930), Album("Act B", "Contemporary", ["Folk"], 1995)],
            Row);

        Assert.Equal("Contemporary", picked[0].Title);
    }

    /// <summary>Era is a distance, not a decade. 1979 and 1980 are the same music, and a rule that
    /// put them apart would measure the calendar and not the record.</summary>
    [Fact]
    public void The_turn_of_a_decade_is_not_a_change_of_era()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Rock"], 1979),
            [Album("Act A", "Nineteen Eighty", ["Rock"], 1980), Album("Act B", "Nineteen Ninety Five", ["Rock"], 1995)],
            Row);

        Assert.Equal("Nineteen Eighty", picked[0].Title);
    }

    /// <summary>Genre outranks era: two records twenty years apart in the same genre are more alike
    /// than two from the same year in different ones.</summary>
    [Fact]
    public void The_same_genre_two_decades_apart_beats_another_genre_this_year()
    {
        var picked = SimilarAlbums.Pick(
            Album("Subject", "Fun", ["Folk"], 1994),
            [Album("Act A", "Same Year Wrong Music", ["Metal"], 1994), Album("Act B", "Right Music Long Ago", ["Folk"], 1974)],
            Row);

        Assert.Equal("Right Music Long Ago", picked[0].Title);
    }

    // ── what the ranking must not do ──

    /// <summary>The most important one. A record with no genre and no year cannot answer the genre or
    /// the era question, and it must not be scored zero for it. If silence were scored, the row would
    /// become a ranking of how well the collection was tagged, which is a question nobody
    /// asked.</summary>
    [Fact]
    public void An_untagged_record_is_not_punished_for_what_its_tags_do_not_say()
    {
        var subject = Album("Subject", "Fun", ["Folk"], 1994);

        // Nothing about it is known; its maker has nothing else on the shelf to lend it either.
        var silent = Album("Silent Act", "Says Nothing");

        // Everything about it is known, and all of it is wrong: another genre, another age.
        var wrong = Album("Wrong Act", "Says The Wrong Thing", ["Classical"], 1890);

        var picked = SimilarAlbums.Pick(subject, [wrong, silent], Row);

        Assert.Equal("Says Nothing", picked[0].Title);
    }

    /// <summary>The row must not walk a dictionary in insertion order. That order is the mirror's
    /// <c>ORDER BY folder</c>, so every album in the collection would suggest whatever sorts first in
    /// it, and a library beginning with "!!!" would show "!!!" under every record in it.</summary>
    [Fact]
    public void The_head_of_the_collection_is_not_everybodys_suggestion()
    {
        var shelf = new List<AlbumTraits>
        {
            Album("!!!", "Sorts First", ["Electronic / Dance"], 2004, folder: "!!!/first"),
            Album("$uicideboy$", "Sorts Second", ["Hip-Hop / Rap"], 2016, folder: "$uicideboy$/second"),
            Album("Zebra Folk", "Sorts Last", ["Folk"], 1994, folder: "Zebra Folk/last"),
        };

        var picked = SimilarAlbums.Pick(Album("Subject", "Fun", ["Folk"], 1994, folder: "Subject/fun"), shelf, Row);

        Assert.Equal("Sorts Last", picked[0].Title);
    }

    /// <summary>The same album opened twice gives the same row. The enumeration order of a dictionary
    /// is not a documented guarantee, so a row that came from one could be reproduced only by
    /// luck.</summary>
    [Fact]
    public void The_same_shelf_in_a_different_order_gives_the_same_row()
    {
        var shelf = new List<AlbumTraits>();
        for (var i = 0; i < 30; i++)
            shelf.Add(Album($"Act {i}", $"Record {i}", ["Folk"], 1990 + i % 5, folder: $"act-{i}/record"));

        var subject = Album("Subject", "Fun", ["Folk"], 1992, folder: "subject/fun");

        var forwards = SimilarAlbums.Pick(subject, shelf, Row).Select(p => p.Folder);
        var backwards = SimilarAlbums.Pick(subject, Enumerable.Reverse(shelf).ToList(), Row).Select(p => p.Folder);

        Assert.Equal(forwards, backwards);
    }

    /// <summary>A shelf that says nothing about anything still fills the row, and says so. A short row
    /// would be worse than an honest one, but the reason has to be stated: the order must not be
    /// presented as if it meant something.</summary>
    [Fact]
    public void A_shelf_with_nothing_to_go_on_still_fills_the_row_and_admits_it()
    {
        var shelf = Enumerable.Range(0, 12).Select(i => Album($"Act {i}", $"Record {i}")).ToList();
        var picked = SimilarAlbums.Pick(Album("Subject", "Fun"), shelf, Row);

        Assert.Equal(Row, picked.Count);
        Assert.All(picked, p => Assert.Equal(SimilarBecause.JustOnTheShelf, p.Why));
    }

    /// <summary>Every card opens a record on this shelf. No suggestion leaves the app: that is
    /// what "similar albums" means.</summary>
    [Fact]
    public void Every_suggestion_is_a_record_on_the_shelf()
    {
        var shelf = Enumerable.Range(0, 12)
            .Select(i => Album($"Act {i}", $"Record {i}", ["Folk"], 1990, folder: $"act-{i}/record"))
            .ToList();

        var picked = SimilarAlbums.Pick(Album("Subject", "Fun", ["Folk"], 1990), shelf, Row);

        Assert.All(picked, p => Assert.False(string.IsNullOrWhiteSpace(p.Folder)));
    }
}
