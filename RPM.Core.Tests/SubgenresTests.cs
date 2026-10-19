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
    public void A_style_is_found_however_it_is_spelled(string tag, string name, string genre)
    {
        var style = Subgenres.Match(tag);
        Assert.NotNull(style);
        Assert.Equal(name, style.Name);
        Assert.Equal(genre, style.Genre);
    }

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
