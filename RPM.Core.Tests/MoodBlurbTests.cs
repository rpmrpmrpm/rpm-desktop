using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// Which kinds of music a mood list says it contains.
///
/// <para>The wording belongs to the interface and is localised; this code decides which genres are
/// worth naming and what they are called in a sentence. A sentence that named a genre only one song
/// belongs to would describe the exception, not the playlist.</para>
/// </summary>
public class MoodBlurbTests
{
    private static IEnumerable<string?> Tags(params (string Tag, int Count)[] rows)
    {
        foreach (var (tag, count) in rows)
            for (var i = 0; i < count; i++)
                yield return tag;
    }

    /// <summary>Most common first: the list should first name what it mostly contains.</summary>
    [Fact]
    public void The_biggest_kinds_are_named_first()
    {
        var spoken = MoodBlurb.Spoken(Tags(("Jazz", 5), ("Rock", 20), ("Pop", 10)));

        Assert.Equal(["rock", "pop", "jazz"], spoken);
    }

    /// <summary>Three at most. Five is a list of genres inside a sentence, and the reader starts
    /// counting instead of reading.</summary>
    [Fact]
    public void No_more_than_three_are_named()
    {
        var spoken = MoodBlurb.Spoken(Tags(
            ("Rock", 20), ("Pop", 18), ("Jazz", 16), ("Blues", 14), ("Folk", 12)));

        Assert.Equal(MoodBlurb.MostGenres, spoken.Count);
        Assert.Equal(["rock", "pop", "jazz"], spoken);
    }

    /// <summary>A genre that one song in fifty belongs to is the exception, not the playlist. A
    /// reader who then cannot find it would think the sentence is wrong.</summary>
    [Fact]
    public void A_kind_barely_present_is_not_named()
    {
        var spoken = MoodBlurb.Spoken(Tags(("Rock", 49), ("Reggae", 1)));

        Assert.Equal(["rock"], spoken);
    }

    /// <summary>
    /// The tags are folded first. A list with "Deep House" and "Acid House" is one house list, not
    /// two rare ones that are both below the share.
    /// </summary>
    [Fact]
    public void The_spellings_of_one_kind_are_counted_together()
    {
        var spoken = MoodBlurb.Spoken(Tags(("Deep House", 8), ("Acid House", 7), ("Rock", 5)));

        Assert.Equal(["house", "rock"], spoken);
    }

    /// <summary>Nothing to say when nothing is tagged, so the view leaves out the sentence instead
    /// of printing one with a gap in it.</summary>
    [Fact]
    public void An_untagged_list_names_nothing()
    {
        Assert.Empty(MoodBlurb.Spoken(Tags(("", 0))));
        Assert.Empty(MoodBlurb.Spoken([null, null, null]));
        // A tag the app has no shelf for. It must be truly unknown: "Ambient" maps to
        // Electronic / Dance and "Field Recording" to Spoken Word, so neither works here.
        Assert.Empty(MoodBlurb.Spoken(Tags(("Norwegian Sea Shanties", 10))));
    }

    // ── how a genre is said in a sentence ──

    /// <summary>
    /// The shelf labels are made for sorting, so half of them are pairs with a slash. "The quiet
    /// end of your Electronic / Dance" puts a filing label inside a sentence.
    /// </summary>
    [Theory]
    [InlineData("Rock", "rock")]
    [InlineData("Electronic / Dance", "electronic")]
    [InlineData("Hip-Hop / Rap", "hip-hop")]
    [InlineData("Soundtrack", "soundtrack")]
    [InlineData("World", "world")]
    [InlineData("Religious", "religious")]
    public void A_shelf_label_is_said_as_one_word(string genre, string expected)
        => Assert.Equal(expected, Genres.Spoken(genre));

    /// <summary>The case a reader would notice: R&amp;B is an abbreviation and "r&amp;b" looks like a
    /// typo.</summary>
    [Fact]
    public void An_abbreviation_keeps_its_capitals()
        => Assert.Equal("R&B", Genres.Spoken("R&B"));

    [Fact]
    public void Nothing_is_said_for_nothing()
    {
        Assert.Equal("", Genres.Spoken(null));
        Assert.Equal("", Genres.Spoken("   "));
    }

    /// <summary>Every genre the app uses has a spoken form that works in a sentence: no slashes in
    /// it, and never empty.</summary>
    [Fact]
    public void Every_genre_can_be_said()
    {
        foreach (var genre in Genres.All)
        {
            var spoken = Genres.Spoken(genre);

            Assert.False(string.IsNullOrWhiteSpace(spoken));
            Assert.DoesNotContain('/', spoken);
        }
    }
}
