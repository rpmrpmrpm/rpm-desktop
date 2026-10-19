using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

public class GenreWallTests
{
    private static ArtistWallItem Artist(string name, params string[] albums)
        => new(name, albums, [], [], albums.Length);

    private static Dictionary<string, string> Tagged(params (string Folder, string Genre)[] rows)
        => rows.ToDictionary(r => r.Folder, r => r.Genre, StringComparer.OrdinalIgnoreCase);

    private static GenreWallItem Find(IReadOnlyList<GenreWallItem> wall, string name)
        => wall.Single(g => g.Name == name);

    [Fact]
    public void A_tagged_record_puts_its_artist_on_that_genre()
    {
        var wall = GenreWall.Build(
            [Artist("Nirvana", "Nirvana/Nevermind")],
            Tagged(("Nirvana/Nevermind", "Rock")));

        var rock = Assert.Single(wall);
        Assert.Equal("Rock", rock.Name);
        Assert.Equal(["Nirvana"], rock.Artists.Select(a => a.Name));
    }

    /// <summary>This is why the wall is not built on the tags as typed: four spellings of one kind of
    /// music are one card.</summary>
    [Fact]
    public void The_catalogues_many_spellings_land_on_one_card()
    {
        var wall = GenreWall.Build(
            [
                Artist("Nas", "Nas/Illmatic"),
                Artist("Dr. Dre", "Dr. Dre/The Chronic"),
                Artist("Outkast", "Outkast/Stankonia"),
            ],
            Tagged(
                ("Nas/Illmatic", "Hip Hop"),
                ("Dr. Dre/The Chronic", "hip-hop"),
                ("Outkast/Stankonia", "HipHop")));

        // Three spellings of the one shelf. "Rap" is not among them: it has a shelf of its own (see
        // Genres.All), so it is correct that it is not on this card.
        var card = Assert.Single(wall);
        Assert.Equal("Hip-Hop / Rap", card.Name);
        Assert.Equal(3, card.Artists.Count);
    }

    [Fact]
    public void An_untagged_record_lands_on_Other()
    {
        var wall = GenreWall.Build(
            [Artist("A Local Band", "A Local Band/Demos")],
            Tagged());

        var other = Assert.Single(wall);
        Assert.Equal("Other", other.Name);
        Assert.Equal(["A Local Band"], other.Artists.Select(a => a.Name));
    }

    /// <summary>A tag the list has no room for is the same answer as no tag at all: the app cannot say
    /// what kind of music this is.</summary>
    [Fact]
    public void A_genre_the_app_does_not_know_lands_on_Other()
    {
        var wall = GenreWall.Build(
            [Artist("Somebody", "Somebody/Record")],
            Tagged(("Somebody/Record", "Norwegian Sea Shanties")));

        Assert.Equal("Other", Assert.Single(wall).Name);
    }

    /// <summary>The list's own last entry is a value a person can pick in a tag box, and it means the
    /// same as nothing picked. One card, not two.</summary>
    [Fact]
    public void The_lists_own_Unknown_and_an_untagged_record_share_one_card()
    {
        var wall = GenreWall.Build(
            [Artist("Picked", "Picked/Record"), Artist("Untagged", "Untagged/Record")],
            Tagged(("Picked/Record", Genres.Unknown)));

        var other = Assert.Single(wall);
        Assert.Equal("Other", other.Name);
        Assert.Equal(2, other.Artists.Count);
    }

    /// <summary>The cards follow the app's own order, and the unfiled card is last, however early its
    /// music appears.</summary>
    [Fact]
    public void The_cards_read_in_the_apps_own_order_with_Other_last()
    {
        var wall = GenreWall.Build(
            [
                Artist("Unfiled", "Unfiled/Record"),
                Artist("Miles Davis", "Miles Davis/Kind of Blue"),
                Artist("The Beatles", "The Beatles/Revolver"),
            ],
            Tagged(
                ("Miles Davis/Kind of Blue", "Jazz"),
                ("The Beatles/Revolver", "Rock")));

        Assert.Equal(["Rock", "Jazz", "Other"], wall.Select(g => g.Name));
    }

    /// <summary>A genre nobody has music on is not a card. The wall is the collection, not the list.</summary>
    [Fact]
    public void A_genre_with_no_music_is_not_on_the_wall()
    {
        var wall = GenreWall.Build(
            [Artist("The Beatles", "The Beatles/Revolver")],
            Tagged(("The Beatles/Revolver", "Rock")));

        Assert.Equal(["Rock"], wall.Select(g => g.Name));
    }

    /// <summary>The card is drawn from the first name on it, so the order matters: most records in THIS
    /// genre first, then most songs anywhere, then by name.</summary>
    [Fact]
    public void The_biggest_act_on_the_genre_leads_it()
    {
        var wall = GenreWall.Build(
            [
                new ArtistWallItem("Prolific Elsewhere", ["Prolific/Jazz One"], [], [], 400),
                new ArtistWallItem("Two Records Here", ["Two/Rock One", "Two/Rock Two"], [], [], 20),
                new ArtistWallItem("One Record Here", ["One/Rock One"], [], [], 50),
            ],
            Tagged(
                ("Prolific/Jazz One", "Jazz"),
                ("Two/Rock One", "Rock"), ("Two/Rock Two", "Rock"),
                ("One/Rock One", "Rock")));

        Assert.Equal(
            ["Two Records Here", "One Record Here"],
            Find(wall, "Rock").Artists.Select(a => a.Name));
    }

    /// <summary>A guest appears on the genre of the record they are a guest on. A record with two names
    /// puts both of them on its card, which is what "the artists that appear in this genre" means.</summary>
    [Fact]
    public void A_guest_appearance_puts_that_artist_on_the_genre()
    {
        var wall = GenreWall.Build(
            [
                new ArtistWallItem("Calvin Harris", ["Calvin Harris/18 Months"], [], [], 12),
                new ArtistWallItem("Ellie Goulding", [], [], ["Calvin Harris/18 Months"], 1),
            ],
            Tagged(("Calvin Harris/18 Months", "Electronic / Dance")));

        var dance = Assert.Single(wall);
        Assert.Equal("Electronic / Dance", dance.Name);
        Assert.Equal(["Calvin Harris", "Ellie Goulding"], dance.Artists.Select(a => a.Name));
    }

    /// <summary>An act with records on two genres is on both cards, once each.</summary>
    [Fact]
    public void An_artist_on_two_genres_is_on_both_cards_once()
    {
        var wall = GenreWall.Build(
            [Artist("Johnny Cash", "Johnny Cash/At Folsom Prison", "Johnny Cash/American IV")],
            Tagged(
                ("Johnny Cash/At Folsom Prison", "Country"),
                ("Johnny Cash/American IV", "Folk")));

        Assert.Equal(["Country", "Folk"], wall.Select(g => g.Name).OrderBy(n => n));
        Assert.All(wall, g => Assert.Equal(["Johnny Cash"], g.Artists.Select(a => a.Name)));
    }

    [Fact]
    public void An_empty_collection_is_an_empty_wall()
        => Assert.Empty(GenreWall.Build([], Tagged()));

    // ── whether a rebuilt wall says anything new about one genre ──
    //
    // The open page is opened again on the new item after every library change, and opening it again
    // rebuilds its shelves. So a reading that describes an unchanged shelf must be recognisable as
    // one. See GenreWallItem.SameAs, and MainPage.FeedGenres.

    /// <summary>Two builds of an unchanged collection say the same thing, though the items are not
    /// equal. This is why the method exists.</summary>
    [Fact]
    public void A_rebuild_of_an_unchanged_collection_says_the_same_thing()
    {
        var artists = new[] { Artist("Nirvana", "Nirvana/Nevermind") };
        var tags = Tagged(("Nirvana/Nevermind", "Rock"));

        var before = Find(GenreWall.Build(artists, tags), "Rock");
        var after = Find(GenreWall.Build(artists, tags), "Rock");

        Assert.NotEqual(before, after);      // the lists are new objects, so the record's own == is false
        Assert.True(after.SameAs(before));
    }

    [Fact]
    public void A_record_added_to_the_genre_is_news()
    {
        var before = Find(
            GenreWall.Build(
                [Artist("Nirvana", "Nirvana/Nevermind")],
                Tagged(("Nirvana/Nevermind", "Rock"))),
            "Rock");

        var after = Find(
            GenreWall.Build(
                [Artist("Nirvana", "Nirvana/Nevermind", "Nirvana/In Utero")],
                Tagged(("Nirvana/Nevermind", "Rock"), ("Nirvana/In Utero", "Rock"))),
            "Rock");

        Assert.False(after.SameAs(before));
    }

    [Fact]
    public void An_act_added_to_the_genre_is_news()
    {
        var before = Find(
            GenreWall.Build(
                [Artist("Nirvana", "Nirvana/Nevermind")],
                Tagged(("Nirvana/Nevermind", "Rock"))),
            "Rock");

        var after = Find(
            GenreWall.Build(
                [Artist("Nirvana", "Nirvana/Nevermind"), Artist("Pixies", "Pixies/Doolittle")],
                Tagged(("Nirvana/Nevermind", "Rock"), ("Pixies/Doolittle", "Rock"))),
            "Rock");

        Assert.False(after.SameAs(before));
    }

    /// <summary>The acts are ordered biggest first, and the first act a picture is found for is the
    /// card's face, so their order is part of the genre's identity.</summary>
    [Fact]
    public void The_same_acts_in_another_order_are_news()
    {
        var one = new GenreWallItem("Rock", [Artist("Nirvana"), Artist("Pixies")], ["a", "b"]);
        var other = new GenreWallItem("Rock", [Artist("Pixies"), Artist("Nirvana")], ["a", "b"]);

        Assert.False(one.SameAs(other));
    }

    /// <summary>The folders have no meaningful order, so two readings that list them differently are
    /// the same shelf.</summary>
    [Fact]
    public void The_same_folders_in_another_order_are_not_news()
    {
        var one = new GenreWallItem("Rock", [Artist("Nirvana")], ["a", "b"]);
        var other = new GenreWallItem("Rock", [Artist("Nirvana")], ["b", "a"]);

        Assert.True(one.SameAs(other));
    }
    // ── whether a reading is new, and whether the card's face is still right ───────────────────────

    [Fact]
    public void A_shelf_that_lost_an_act_is_not_the_same_acts()
    {
        // Signing out: the catalogue's artists are removed and this disk's stay. The card's face was
        // one of the artists removed.
        var withCloud = Find(GenreWall.Build(
            [Artist("Sting", "Sting/Fields of Gold"), Artist("Pink Floyd", "Pink Floyd/The Wall")],
            Tagged(("Sting/Fields of Gold", "Rock"), ("Pink Floyd/The Wall", "Rock"))), "Rock");

        var localOnly = Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall")],
            Tagged(("Pink Floyd/The Wall", "Rock"))), "Rock");

        Assert.False(withCloud.SameActsAs(localOnly));
        Assert.False(withCloud.SameAs(localOnly));
    }

    [Fact]
    public void A_shelf_that_only_gained_a_record_keeps_its_acts()
    {
        // An import by an act already on the shelf. The face is still one of these artists, so the
        // card must not discard its photograph and fetch it again from the network.
        var before = Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall")],
            Tagged(("Pink Floyd/The Wall", "Rock"))), "Rock");

        var after = Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall", "Pink Floyd/Animals")],
            Tagged(("Pink Floyd/The Wall", "Rock"), ("Pink Floyd/Animals", "Rock"))), "Rock");

        Assert.True(before.SameActsAs(after));
        Assert.False(before.SameAs(after));   // the records changed, so the page does open again
    }

    [Fact]
    public void The_order_of_the_acts_is_part_of_what_the_acts_are()
    {
        // The card shows the first act a picture is found for, so two shelves holding the same
        // artists in a different order do not have the same face.
        var floydFirst = Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall", "Pink Floyd/Animals"),
             Artist("Sting", "Sting/Fields of Gold")],
            Tagged(("Pink Floyd/The Wall", "Rock"), ("Pink Floyd/Animals", "Rock"),
                   ("Sting/Fields of Gold", "Rock"))), "Rock");

        var stingFirst = Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall"),
             Artist("Sting", "Sting/Fields of Gold", "Sting/Ten Summoner's Tales")],
            Tagged(("Pink Floyd/The Wall", "Rock"), ("Sting/Fields of Gold", "Rock"),
                   ("Sting/Ten Summoner\'s Tales", "Rock"))), "Rock");

        Assert.Equal("Pink Floyd", floydFirst.Artists[0].Name);
        Assert.Equal("Sting", stingFirst.Artists[0].Name);
        Assert.False(floydFirst.SameActsAs(stingFirst));
    }

    [Fact]
    public void A_reading_of_an_untouched_shelf_is_the_same_shelf()
    {
        var read = () => Find(GenreWall.Build(
            [Artist("Pink Floyd", "Pink Floyd/The Wall")],
            Tagged(("Pink Floyd/The Wall", "Rock"))), "Rock");

        Assert.True(read().SameActsAs(read()));
        Assert.True(read().SameAs(read()));
    }
}
