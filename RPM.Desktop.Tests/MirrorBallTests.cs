using RPM.Desktop.Presentation.Pegasus;

namespace RPM.Desktop.Tests;

/// <summary>
/// Which records the Pegasus room hangs a mirror ball for.
///
/// <para>The rule reads two tags the way a tagger writes them, so most of these tests cover the
/// shapes that real files come in: a genre with a slash in it, an artist with a guest credit, a
/// field left empty. They also cover near misses that the rule must not match.</para>
/// </summary>
public class MirrorBallTests
{
    /// <summary>The named artist, however the file spells it. Letter case does not matter.</summary>
    [Theory]
    [InlineData("ABBA")]
    [InlineData("abba")]
    [InlineData("Abba")]
    [InlineData("  ABBA  ")]
    public void The_named_artist_hangs_the_ball_however_it_is_spelt(string artist)
        => Assert.True(MirrorBall.HangsFor(artist, genre: "Pop"));

    /// <summary>The artist name may appear anywhere in the field. A guest credit or a second name
    /// in the artist tag still counts as that record.</summary>
    [Theory]
    [InlineData("ABBA feat. Frida")]
    [InlineData("Agnetha & ABBA")]
    [InlineData("ABBA/Various Artists")]
    public void A_shared_credit_is_still_that_artist(string artist)
        => Assert.True(MirrorBall.HangsFor(artist, genre: null));

    /// <summary>A genre match is enough on its own, and this half covers most records. It includes
    /// all the usual ways of writing the two words, including pairs with a slash or a comma between
    /// them.</summary>
    [Theory]
    [InlineData("Disco")]
    [InlineData("disco")]
    [InlineData("Italo Disco")]
    [InlineData("Nu-Disco")]
    [InlineData("Disco/Funk")]
    [InlineData("Funk")]
    [InlineData("funk")]
    [InlineData("Funk/Soul")]
    [InlineData("Soul, Funk")]
    [InlineData("P-Funk")]
    public void Anything_filed_under_one_of_the_genres_hangs_the_ball(string genre)
        => Assert.True(MirrorBall.HangsFor(artist: "Chic", genre: genre));

    /// <summary>The pair needs its funk half. The other half is not a word on the list, so a record
    /// filed under soul alone is an ordinary record. This is how the rule is meant to work.</summary>
    [Fact]
    public void The_other_half_of_a_pair_does_not_count_on_its_own()
    {
        Assert.True(MirrorBall.HangsFor("Marvin Gaye", "Funk/Soul"));
        Assert.False(MirrorBall.HangsFor("Marvin Gaye", "Soul"));
    }

    /// <summary>Either tag is enough by itself: ABBA filed under pop is still ABBA, and a disco record by
    /// somebody else is still disco.</summary>
    [Fact]
    public void Either_tag_alone_is_enough()
    {
        Assert.True(MirrorBall.HangsFor("ABBA", "Pop"));
        Assert.True(MirrorBall.HangsFor("Sister Sledge", "Disco"));
    }

    /// <summary>The rule matches whole words, not substrings. An artist whose name only starts the
    /// same way, and a genre that only contains the letters, are both ordinary records.</summary>
    [Theory]
    [InlineData("Abbath", "Black Metal")]
    [InlineData("The Abbey Road Ensemble", "Classical")]
    [InlineData("Daft Punk", "Discovery")]
    [InlineData("Pink Floyd", "Progressive Rock")]
    public void A_name_that_only_looks_like_one_of_them_is_left_alone(string artist, string genre)
        => Assert.False(MirrorBall.HangsFor(artist, genre));

    /// <summary>Untagged files are the common case in any real library, and the room simply carries on.
    /// Nothing here may throw on a field that was never filled in.</summary>
    [Theory]
    [InlineData(null, null)]
    [InlineData("", "")]
    [InlineData("   ", "   ")]
    [InlineData("Miles Davis", null)]
    [InlineData(null, "Jazz")]
    public void An_untagged_record_hangs_nothing(string? artist, string? genre)
        => Assert.False(MirrorBall.HangsFor(artist, genre));

    /// <summary>The named tags are what the room is documented to answer to. The tests check them
    /// against the rule itself, not against a copy of the words, so a genre added to the list is
    /// covered by this test too.</summary>
    [Fact]
    public void The_rule_answers_to_every_name_it_publishes()
    {
        Assert.True(MirrorBall.HangsFor(MirrorBall.Artist, genre: null));

        Assert.NotEmpty(MirrorBall.Genres);
        foreach (var genre in MirrorBall.Genres)
            Assert.True(MirrorBall.HangsFor(artist: null, genre: genre), $"{genre} should hang the ball");
    }
}
