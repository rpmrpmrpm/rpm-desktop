using RPM.Core.Library;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// Reading a typed sentence as a generated list.
///
/// <para>The parse uses no model and no network: a phrase table, the app's genre matcher, and five
/// ordering rules. These tests check the rules, because each one is a decision that could reasonably
/// have been made the other way.</para>
/// </summary>
public class MoodQueryTests
{
    private static MoodQuery Read(string sentence) => MoodQuery.Read(sentence);

    // ── the plain readings ──

    [Theory]
    [InlineData("something mellow for a rainy sunday", SmartPlaylistKind.Calm)]
    [InlineData("chill", SmartPlaylistKind.Calm)]
    [InlineData("something for the gym", SmartPlaylistKind.Energetic)]
    [InlineData("music to study to", SmartPlaylistKind.Focus)]
    [InlineData("something to fall asleep to", SmartPlaylistKind.Sleep)]
    [InlineData("music for a party", SmartPlaylistKind.Danceable)]
    [InlineData("what have I never played", SmartPlaylistKind.NeverPlayed)]
    [InlineData("my favourites", SmartPlaylistKind.FavoriteTracks)]
    public void Reads_the_kind_a_sentence_names(string sentence, SmartPlaylistKind kind)
    {
        var query = Read(sentence);

        Assert.True(query.Understood);
        Assert.Equal(SmartPlaylistId.WholeShelf(kind), query.List);
    }

    [Fact]
    public void A_sentence_that_names_nothing_asks_for_no_list()
    {
        var query = Read("play something");

        Assert.False(query.Understood);
        Assert.Null(query.List);
        Assert.Null(query.Genre);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData("!?,.")]
    public void Nothing_typed_is_nothing_read(string? sentence)
    {
        Assert.Equal(MoodQuery.Nothing, MoodQuery.Read(sentence));
    }

    // ── the genre ──

    [Fact]
    public void A_mood_and_a_genre_make_the_genres_own_list()
    {
        var query = Read("calm rock");

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"), query.List);
        Assert.Equal("Rock", query.Genre);
        Assert.Empty(query.Dropped);
    }

    /// <summary>A style names its shelf, because the genre matcher reads styles that way: a record
    /// tagged "Deep House" is a House record, and so is a sentence asking for one.</summary>
    [Fact]
    public void A_style_names_the_shelf_it_sits_on()
    {
        Assert.Equal("House", Read("deep house for a party").Genre);
    }

    /// <summary>A genre on its own is not a mood question. The list is null and the genre is reported,
    /// so a surface can open the genre's own page instead of inventing a mood.</summary>
    [Fact]
    public void A_genre_alone_is_a_genre_and_not_a_mood()
    {
        var query = Read("hip hop");

        Assert.False(query.Understood);
        Assert.Equal("Hip-Hop / Rap", query.Genre);
    }

    [Fact]
    public void A_kind_with_no_per_genre_form_says_so_rather_than_dropping_it_quietly()
    {
        var query = Read("rock to fall asleep to");

        Assert.Equal(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Sleep), query.List);
        Assert.Equal("Rock", query.Genre);
        Assert.Contains(MoodQueryDrop.KindTakesNoGenre, query.Dropped);
    }

    // ── classical, which is a shelf and a kind ──

    [Fact]
    public void Classical_alone_is_the_classical_list()
    {
        Assert.Equal(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Classical), Read("classical").List);
    }

    [Theory]
    [InlineData("calm classical")]
    [InlineData("classical, something calm")]
    public void Classical_becomes_the_shelf_as_soon_as_another_word_supplies_the_mood(string sentence)
    {
        var query = Read(sentence);

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Classical"), query.List);
        Assert.Empty(query.Dropped);
    }

    // ── the decade ──

    [Fact]
    public void A_decade_alone_is_the_decades_chart()
    {
        Assert.Equal(SmartPlaylistId.In(1970), Read("the seventies").List);
        Assert.Equal(SmartPlaylistId.In(1980), Read("80s").List);
    }

    /// <summary>A bare year is read as its decade by arithmetic, so the table needs no line for it.
    /// </summary>
    [Fact]
    public void A_bare_year_is_read_as_its_decade()
    {
        Assert.Equal(SmartPlaylistId.In(1970), Read("1975").List);
    }

    [Fact]
    public void A_year_outside_the_six_decades_is_refused_out_loud()
    {
        var query = Read("1955");

        Assert.False(query.Understood);
        Assert.Equal(1950, query.Decade);
        Assert.Contains(MoodQueryDrop.DecadeNotOffered, query.Dropped);
    }

    [Fact]
    public void A_mood_in_a_decade_is_a_list_of_its_own()
    {
        Assert.Equal(
            SmartPlaylistId.InEra(SmartPlaylistKind.Calm, 1970),
            Read("something calm from the seventies").List);
    }

    /// <summary>A list is narrowed by a genre or by a decade, never by both, so one is dropped. The
    /// genre stays, and the dropped decade is reported, not hidden.</summary>
    [Fact]
    public void A_genre_beats_a_decade_and_the_decade_is_reported_lost()
    {
        var query = Read("calm rock from the seventies");

        Assert.Equal(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"), query.List);
        Assert.Equal(1970, query.Decade);
        Assert.Contains(MoodQueryDrop.DecadeLostToGenre, query.Dropped);
    }

    [Fact]
    public void A_kind_with_no_per_decade_form_says_so()
    {
        var query = Read("something to fall asleep to from the seventies");

        Assert.Equal(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Sleep), query.List);
        Assert.Contains(MoodQueryDrop.KindTakesNoDecade, query.Dropped);
    }

    // ── refusals ──

    /// <summary>The failure this rule prevents: "loud" is in the table, so a parse that ignored the
    /// "nothing" before it would answer a request for quiet with the loudest list there
    /// is.</summary>
    [Fact]
    public void A_refused_mood_is_dropped_and_never_inverted()
    {
        var query = Read("nothing too loud");

        Assert.False(query.Understood);
        Assert.Contains(MoodQueryDrop.NegatedMood, query.Dropped);
    }

    [Fact]
    public void A_refusal_stops_at_the_end_of_its_clause()
    {
        var query = Read("no metal, something calm");

        Assert.Equal(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm), query.List);
        Assert.Null(query.Genre);
        Assert.Contains(MoodQueryDrop.NegatedGenre, query.Dropped);
    }

    /// <summary>A refusal applies to the first meaningful word after it, however many filler words
    /// come between. A fixed window of words would miss it.</summary>
    [Fact]
    public void A_refusal_reaches_past_filler()
    {
        Assert.Contains(MoodQueryDrop.NegatedMood, Read("nothing that is too loud").Dropped);
    }

    /// <summary>"Never played" is one phrase and is matched as a whole, so the "never" in it is not left
    /// over to negate the "played" next to it. Phrases are matched before refusals for exactly this
    /// reason.</summary>
    [Fact]
    public void A_negating_word_inside_a_phrase_does_not_negate_the_phrase()
    {
        Assert.Equal(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed), Read("never played").List);
    }

    // ── two moods ──

    [Fact]
    public void The_longest_phrase_wins_among_kinds()
    {
        // "Late night" is two words and beats the one-word "chill" next to it.
        Assert.Equal(SmartPlaylistKind.LateNight, Read("chill late night").List?.Kind);
    }

    [Fact]
    public void A_second_mood_is_reported_rather_than_merged()
    {
        var query = Read("calm party");

        Assert.Equal(SmartPlaylistKind.Calm, query.List?.Kind);
        Assert.Contains(MoodQueryDrop.SecondMood, query.Dropped);
    }

    /// <summary>Two words naming the SAME kind are a normal sentence, not a conflict.</summary>
    [Fact]
    public void Two_words_for_one_mood_drop_nothing()
    {
        Assert.Empty(Read("something quiet and mellow").Dropped);
    }

    // ── the table itself ──

    /// <summary>Every kind the table names must be a list that can actually be shown. A phrase that
    /// points at a kind no folder offers would be a sentence that parses and then opens nothing.
    /// </summary>
    [Fact]
    public void Every_phrase_names_a_kind_the_app_offers()
    {
        var offered = SmartPlaylists.All.Concat(SmartPlaylists.InTheirOwnFolder).ToHashSet();

        foreach (var (kind, phrase) in MoodWords.EveryPhrase)
            Assert.True(offered.Contains(kind), $"\"{phrase}\" names {kind}, which nothing offers.");
    }

    /// <summary>
    /// A phrase may name a genre only where it names the classical list, which is the one kind that is
    /// also a shelf.
    ///
    /// <para>A table with "disco" would answer a question about the disco shelf with a dance-floor
    /// mood list, and nothing in the parse could tell the two apart. The classical pair can be told
    /// apart, by the rule in <see cref="MoodQuery.Read"/>: the word is the kind where nothing else
    /// gives a mood, and the shelf where something does. That rule works for any word on the
    /// Classical shelf, which is why "chamber music" is allowed here and why the exception is written
    /// as the shelf and not as the single word "classical".</para>
    /// </summary>
    [Fact]
    public void A_phrase_is_a_genre_only_where_it_is_the_classical_shelf()
    {
        foreach (var (kind, phrase) in MoodWords.EveryPhrase)
        {
            if (Genres.Match(GenreText.Fold(phrase)) is not { } genre)
                continue;

            Assert.True(
                genre == "Classical" && kind == SmartPlaylistKind.Classical,
                $"\"{phrase}\" names {kind} and the {genre} shelf, and nothing can tell those apart.");
        }
    }
}
