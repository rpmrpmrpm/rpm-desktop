using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>The styles that hang off the shelves: that the table is well formed, and that looking a
/// style up from a tag gives what a listener would expect.</summary>
public class SubgenresTests
{
    /// <summary>A style whose shelf is not a shelf could never be browsed to. The table is generated
    /// from one file, so this checks that the generator and the app agree.</summary>
    [Fact]
    public void Every_style_hangs_off_a_shelf_that_exists()
    {
        var shelves = Genres.All.ToHashSet(StringComparer.Ordinal);
        foreach (var style in Subgenres.All)
            Assert.Contains(style.Genre, shelves);
    }

    [Fact]
    public void The_shelves_are_the_genre_list() => Assert.Equal(Genres.All, Subgenres.Shelves);

    /// <summary>The catch-all does not divide: a record no shelf describes has no corner of one.</summary>
    [Fact]
    public void The_catch_all_has_no_styles() => Assert.Empty(Subgenres.Under(Genres.Unknown));

    [Fact]
    public void Nothing_is_offered_under_a_name_that_is_not_a_shelf()
    {
        Assert.Empty(Subgenres.Under("Skiffle"));
        Assert.Empty(Subgenres.Under(""));
        Assert.Empty(Subgenres.Under(null));
    }

    /// <summary>Everything offered under a shelf says that shelf is its own, in the order the list
    /// gives. The order is what a picker shows, so it is not decoration.</summary>
    [Fact]
    public void What_is_offered_under_a_shelf_belongs_to_it()
    {
        foreach (var genre in Genres.All)
            foreach (var style in Subgenres.Under(genre))
                Assert.Equal(genre, style.Genre);
    }

    [Fact]
    public void A_shelf_that_divides_offers_its_styles()
    {
        var house = Subgenres.Under("House").Select(s => s.Name).ToList();
        Assert.Contains("Deep House", house);
        Assert.Contains("Acid House", house);
        Assert.DoesNotContain("Post-Punk", house);
    }

    [Theory]
    [InlineData("Deep House", "Deep House", "House")]
    [InlineData("deep house", "Deep House", "House")]
    [InlineData("DEEP-HOUSE", "Deep House", "House")]
    [InlineData("Post Punk", "Post-Punk", "Punk")]
    [InlineData("Pop Rap", "Pop Rap", "Rap")]
    [InlineData("Singspiel", "Singspiel", "Opera")]
    [InlineData("Opera Comique", "Opéra Comique", "Opera")]
    [InlineData("opéra-comique", "Opéra Comique", "Opera")]
    [InlineData("Marchenoper", "Märchenoper", "Opera")]
    [InlineData("String Quartet", "String Quartet", "Classical")]
    public void A_style_is_found_however_it_is_spelled(string tag, string name, string genre)
    {
        var style = Subgenres.Match(tag);
        Assert.NotNull(style);
        Assert.Equal(name, style.Name);
        Assert.Equal(genre, style.Genre);
    }

    /// <summary>The opera list follows Wikipedia's list of opera genres (the forms a recording is filed
    /// under, not every alternative name), and the classical list has forms beside its periods.</summary>
    [Theory]
    [InlineData("Opera", "Singspiel")]
    [InlineData("Opera", "Opera Seria")]
    [InlineData("Opera", "Zarzuela")]
    [InlineData("Classical", "Symphony")]
    [InlineData("Classical", "Art Song")]
    public void Opera_and_classical_offer_their_forms(string shelf, string style)
        => Assert.Contains(Subgenres.Under(shelf), s => s.Name == style);

    /// <summary>A main style is a style on the same shelf, and it comes before everything under it, so
    /// a picker read top to bottom shows each style beneath the one it belongs to.</summary>
    [Fact]
    public void Every_main_style_is_on_the_same_shelf_and_comes_first()
    {
        foreach (var genre in Genres.All)
        {
            var under = Subgenres.Under(genre).Select(s => s.Name).ToList();
            foreach (var style in Subgenres.Under(genre))
                if (style.Parent is { } parent)
                {
                    Assert.Contains(parent, under);
                    Assert.True(under.IndexOf(parent) < under.IndexOf(style.Name),
                        $"{style.Name} is offered before {parent}, the style it sits under");
                }
        }
    }

    /// <summary>Electronic / Dance follows Wikipedia's list of electronic music genres: each main
    /// style with its own styles under it, as deep as the article goes.</summary>
    [Theory]
    [InlineData("Psytrance", "Trance")]
    [InlineData("Dark Psytrance", "Psytrance")]
    [InlineData("Neurofunk", "Drum and Bass")]
    [InlineData("Brostep", "Dubstep")]
    [InlineData("Dubstep", "Bass Music")]
    [InlineData("Aggrotech", "Dark Electro")]
    [InlineData("Future Funk", "Vaporwave")]
    [InlineData("Gabber", "Hardcore")]
    public void Electronic_styles_sit_under_their_main_style(string style, string parent)
    {
        Assert.Equal(parent, Subgenres.ParentOf(style));
        Assert.Contains(Subgenres.Under("Electronic / Dance"), s => s.Name == style);
    }

    /// <summary>House and Techno follow the same article's House and Techno families.</summary>
    [Theory]
    [InlineData("House", "Amapiano", "Afro House")]
    [InlineData("House", "Complextro", "Electro House")]
    [InlineData("House", "Hardbass", "Pumping House")]
    [InlineData("House", "Pumping House", "UK Hard House")]
    [InlineData("Techno", "Raggatek", "Free Tekno")]
    [InlineData("Techno", "Free Tekno", "Hard Techno")]
    public void House_and_techno_styles_sit_under_their_main_style(string shelf, string style, string parent)
    {
        Assert.Equal(parent, Subgenres.ParentOf(style));
        Assert.Contains(Subgenres.Under(shelf), s => s.Name == style);
        Assert.Equal(shelf, Subgenres.Match(style)?.Genre);
    }

    /// <summary>Ballroom is the house music of ball culture, and a tag that says it is a House record.
    /// The waltzes and foxtrots of the dance hall are Ballroom Dance, a different name on Classical, so
    /// neither shelf loses its word to the other.</summary>
    [Theory]
    [InlineData("ballroom", "House", "Ballroom")]
    [InlineData("Ballroom Dance", "Classical", "Ballroom Dance")]
    public void Ballroom_and_ballroom_dance_are_two_styles(string tag, string genre, string style)
    {
        var choice = Genres.Read(tag);
        Assert.Equal(genre, choice.Genre);
        Assert.Equal(style, choice.Subgenre);
    }

    [Theory]
    [InlineData("Trance")]
    [InlineData("Ambient")]
    [InlineData("Drum and Bass")]
    [InlineData("Deep House")]
    [InlineData("Not a style")]
    [InlineData(null)]
    public void A_main_style_and_anything_unknown_sit_under_nothing(string? style)
        => Assert.Null(Subgenres.ParentOf(style));

    /// <summary>The nesting orders the list and never files a record: a Psytrance tag is a Psytrance
    /// record on Electronic / Dance, not a Trance one.</summary>
    [Theory]
    [InlineData("psytrance", "Psytrance")]
    [InlineData("Liquid Funk", "Liquid Funk")]
    [InlineData("hard trance, techno", "Hard Trance")]
    public void A_nested_style_is_read_as_itself(string tag, string style)
    {
        var choice = Genres.Read(tag);
        Assert.Equal("Electronic / Dance", choice.Genre);
        Assert.Equal(style, choice.Subgenre);
    }

    /// <summary>Hardcore is an electronic main style, but most records tagged "hardcore" are punk. It
    /// is offered and never read out of a tag, so "hardcore, punk" is still a punk record. The same
    /// for Wave, whose one word is inside every "something wave" tag.</summary>
    [Theory]
    [InlineData("Hardcore")]
    [InlineData("Wave")]
    public void A_main_style_named_by_one_common_word_is_offered_but_not_read(string style)
    {
        Assert.Contains(Subgenres.Under("Electronic / Dance"), s => s.Name == style);
        Assert.Null(Subgenres.Match(style));
    }

    [Fact]
    public void A_hardcore_punk_tag_stays_punk() => Assert.Equal("Punk", Genres.Match("hardcore, punk"));

    /// <summary>"Organ" and "Mass" are offered, but a loose word is not read as either: "Organ Jazz" is
    /// a jazz record and a gospel "Mass Choir" is not a classical one.</summary>
    [Theory]
    [InlineData("Organ Jazz")]
    [InlineData("Mass Choir")]
    public void A_classical_form_named_by_one_common_word_is_not_read_out_of_a_tag(string tag)
        => Assert.NotEqual("Classical", Subgenres.Match(tag)?.Genre);

    /// <summary>A style under three shelves names none of them, so it is matched to nothing and the
    /// rest of the tag decides. It is still offered under each of its shelves, because somebody
    /// picking by hand knows which one they mean.</summary>
    [Fact]
    public void A_style_belonging_to_several_shelves_is_offered_but_never_matched()
    {
        Assert.Null(Subgenres.Match("Singer-Songwriter"));

        var shelves = Subgenres.All
            .Where(s => s.Name == "Singer-Songwriter")
            .Select(s => s.Genre)
            .ToList();

        Assert.True(shelves.Count > 1);
        foreach (var genre in shelves)
            Assert.Contains(Subgenres.Under(genre), s => s.Name == "Singer-Songwriter");
    }

    /// <summary>Words that fall out of splitting a tag are never styles: a shelf label cut in half,
    /// a bare modifier, a fact about a recording. Whatever reads a tag has to be TOLD to skip these
    /// and not left to find them.</summary>
    [Theory]
    [InlineData("world")]
    [InlineData("international")]
    [InlineData("progressive")]
    [InlineData("psychedelic")]
    [InlineData("instrumental")]
    [InlineData("glam")]
    [InlineData("experimental")]
    [InlineData("dance")]
    [InlineData("edm")]
    public void A_word_that_is_not_a_style_never_becomes_one(string word) =>
        Assert.Null(Subgenres.Match(word));

    /// <summary>Blocked for the STYLE lookup only. Several of those words name a shelf perfectly
    /// well, and blocking them everywhere would lose the records that carry them.</summary>
    [Theory]
    [InlineData("world", "World")]
    [InlineData("dance", "Electronic / Dance")]
    [InlineData("edm", "Electronic / Dance")]
    public void A_blocked_word_can_still_name_a_shelf(string word, string genre) =>
        Assert.Equal(genre, Genres.Match(word));

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(null)]
    [InlineData("Shellac")]
    [InlineData("Test Pressing")]
    public void Anything_else_is_no_style(string? value)
    {
        Assert.Null(Subgenres.Match(value));
        Assert.False(Subgenres.Knows(value));
    }

    /// <summary>Folded to letters, a style and a shelf spelled the same could never be told apart. The
    /// generator refuses to write such a table; this is the same check from the app's side.</summary>
    [Fact]
    public void No_style_is_spelled_like_a_shelf()
    {
        var shelves = Genres.All
            .Select(g => new string([.. g.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]))
            .ToHashSet(StringComparer.Ordinal);

        foreach (var style in Subgenres.All)
        {
            var folded = new string([.. style.Name.Where(char.IsLetterOrDigit).Select(char.ToLowerInvariant)]);
            Assert.DoesNotContain(folded, shelves);
        }
    }
}
