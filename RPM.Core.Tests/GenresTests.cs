using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>The app's own genre list, and how a catalogue's answer is translated into it. The purpose
/// of the list is that a library has one shelf per kind of music: "Hip Hop" and "hip-hop" are one
/// shelf, not two, and a record that nothing on the list describes keeps its own genre.</summary>
public class GenresTests
{
    [Theory]
    [InlineData("Rock", "Rock")]
    [InlineData("rock", "Rock")]
    [InlineData("Hip Hop", "Hip-Hop / Rap")]
    [InlineData("hip-hop", "Hip-Hop / Rap")]
    // Rap and funk have shelves of their own, so they do not merge into the compound ones. See
    // Genres.All for the ordering that decides which of two labels owns a word.
    [InlineData("Rap", "Rap")]
    [InlineData("R&B", "R&B")]
    [InlineData("RnB", "R&B")]
    [InlineData("Funk / Soul", "Funk")]
    [InlineData("Soul", "Soul")]
    [InlineData("Electronic", "Electronic / Dance")]
    [InlineData("House", "House")]
    [InlineData("Deep House", "House")]
    [InlineData("Techno", "Techno")]
    [InlineData("Indie Rock", "Indie")]
    [InlineData("Game", "Video Game")]
    // Filed under Soul. No alias is needed: the hyphen splits the phrase and the last word decides,
    // which is the simple case the phrase rule exists for.
    [InlineData("Neo-Soul", "Soul")]
    [InlineData("neo soul", "Soul")]
    // A style that belongs to three shelves at once names none of them, so the word is skipped and the
    // rest of the tag decides. Both of these are real tags from a collection. The first is filed under
    // Indie, not Folk, because "lo-fi" is a style the app knows and it comes before folk: the list is
    // read left to right, and the first entry the app recognises wins.
    [InlineData("singer-songwriter, lo-fi, folk, bedroom pop, indie pop, indie rock", "Indie")]
    [InlineData("singer-songwriter, pop, pop rock, baroque pop, soft rock, rock", "Pop")]
    [InlineData("Stage & Screen", "Soundtrack")]
    [InlineData("Folk, World, & Country", "Folk")]
    // Shelves with one-word names, each also found by the other word of its two-word label. Without
    // these aliases, "Gospel" and "Score" name nothing and "Video Game Soundtrack" is read from the
    // last word onto the Soundtrack shelf.
    [InlineData("Gospel", "Religious")]
    [InlineData("Religious", "Religious")]
    [InlineData("Score", "Soundtrack")]
    [InlineData("International", "World")]
    [InlineData("Video Game Soundtrack", "Video Game")]
    [InlineData("Video Game", "Video Game")]
    public void A_catalogues_word_for_it_lands_on_the_apps_shelf(string provider, string expected) =>
        Assert.Equal(expected, Genres.Match(provider));

    /// <summary>Four two-part labels that are not on the list are still in many files, and a library
    /// that did not recognise them would show those records as having no genre at all.</summary>
    [Theory]
    [InlineData("R&B / Soul", "R&B")]
    [InlineData("World / International", "World")]
    [InlineData("Gospel / Religious", "Religious")]
    [InlineData("Soundtrack / Score", "Soundtrack")]
    public void A_shelf_the_list_no_longer_has_still_reads(string retired, string expected) =>
        Assert.Equal(expected, Genres.Match(retired));

    /// <summary>A style is a phrase, and the word in it that names a genre is the genre. This is how a
    /// release Discogs filed under a style alone still gets a shelf.</summary>
    [Theory]
    [InlineData("Post-Punk", "Punk")]
    [InlineData("Hard Rock", "Rock")]
    [InlineData("Synth-pop", "Pop")]
    [InlineData("Death Metal", "Metal")]
    [InlineData("Contemporary Jazz", "Jazz")]
    public void A_styles_own_words_are_read_for_a_genre(string style, string expected) =>
        Assert.Equal(expected, Genres.Match(style));

    /// <summary>
    /// The LAST word of a phrase is the genre, not the first: English puts the head of a compound name
    /// at the end, so these are all named after the music and not after the modifier in front of it.
    ///
    /// <para>Read from the first word, "electro house" would be Electronic / Dance and "trap latino"
    /// would be Trap. Every line here follows the same last-word rule.</para>
    /// </summary>
    [Theory]
    [InlineData("electro house", "House")]
    [InlineData("trap latino", "Latin")]
    [InlineData("pop rap", "Rap")]
    [InlineData("jazz rap", "Rap")]
    [InlineData("ambient techno", "Techno")]
    [InlineData("electro-disco", "Disco")]
    [InlineData("pop metal", "Metal")]
    [InlineData("reggaeton", "Latin")]
    public void The_last_word_of_a_phrase_is_the_genre(string phrase, string expected) =>
        Assert.Equal(expected, Genres.Match(phrase));

    /// <summary>
    /// A LIST is still read first to last, and this must not change.
    ///
    /// <para>A label with slashes or commas lists a record's genres in the catalogue's own order of
    /// importance, so the first one it names is what the record IS. Reading these from the end would
    /// file every funk record under soul and every folk record under country: the opposite mistake
    /// to the one the phrase rule fixes, on many more records.</para>
    /// </summary>
    [Theory]
    [InlineData("Funk / Soul", "Funk")]
    [InlineData("Folk, World, & Country", "Folk")]
    [InlineData("Rock | Pop | Folk Rock | Art Rock", "Rock")]
    [InlineData("Progressive House, Trance, Speed Garage", "House")]
    [InlineData("cloud rap, hip hop, trap", "Rap")]
    public void A_list_is_still_read_first_to_last(string label, string expected) =>
        Assert.Equal(expected, Genres.Match(label));

    /// <summary>
    /// The compounds where the last word names the music a style CAME FROM, not the shelf it belongs
    /// on. Each one is a whole-tag key in the alias table, so the last-word rule does not apply.
    ///
    /// <para>Tested here because nothing else would detect a missing one: the phrase rule would
    /// silently put the record on the wrong shelf and still look correct.</para>
    /// </summary>
    [Theory]
    [InlineData("Punk Rock", "Punk")]
    [InlineData("Lovers Rock", "Reggae")]
    [InlineData("Christian Rock", "Religious")]
    [InlineData("Gospel Soul", "Religious")]
    [InlineData("Gospel Blues", "Religious")]
    [InlineData("Latin Rock", "Latin")]
    [InlineData("Latin Pop", "Latin")]
    [InlineData("Country Pop", "Country")]
    [InlineData("Folk Pop", "Folk")]
    [InlineData("Jazz Pop", "Jazz")]
    [InlineData("Dream Pop", "Indie")]
    [InlineData("Bedroom Pop", "Indie")]
    [InlineData("Jangle Pop", "Indie")]
    [InlineData("Chamber Pop", "Indie")]
    [InlineData("Post-Punk Revival", "Indie")]
    [InlineData("Power Pop", "Rock")]
    [InlineData("Dance-Pop", "Pop")]
    [InlineData("Trap Soul", "R&B")]
    [InlineData("G-Funk", "Hip-Hop / Rap")]
    [InlineData("Witch House", "Electronic / Dance")]
    [InlineData("Future Funk", "Electronic / Dance")]
    [InlineData("Ambient Classical", "Neo-Classical")]
    [InlineData("Video Game Score", "Video Game")]
    // Named for where it is played, so it stays with the place: someone opening World looks for
    // Tinariwen and Mulatu Astatke and would not think to try Blues or Jazz.
    [InlineData("Desert Blues", "World")]
    [InlineData("Ethio-Jazz", "World")]
    public void A_compound_goes_to_the_shelf_it_belongs_on_not_to_its_last_word(string label, string expected)
        => Assert.Equal(expected, Genres.Match(label));

    /// <summary>The other half of the same rule, which the rule above must not override. A phrase that
    /// only CONTAINS a compound is read by what the app can name inside it.
    ///
    /// <para>For "Progressive Punk Rock", the alias table alone would read it by its last word and
    /// file it under Rock, but the style table finds the punk rock inside it first. That is the better
    /// answer (a progressive punk rock record is a punk record), and it is tested here so that a
    /// change would be noticed.</para></summary>
    [Theory]
    [InlineData("Progressive Punk Rock", "Punk")]
    [InlineData("Experimental Hip Hop", "Hip-Hop / Rap")]
    [InlineData("Experimental Rock", "Rock")]
    [InlineData("Cloud Rap", "Rap")]
    public void A_phrase_that_only_contains_one_is_still_read_by_its_last_word(string label, string expected)
        => Assert.Equal(expected, Genres.Match(label));

    /// <summary>A single letter is not a genre. "R&amp;B" splits into "R" and "B", and treating
    /// either as a key would file half the catalogue under it.</summary>
    [Theory]
    [InlineData("R")]
    [InlineData("B")]
    [InlineData("Non-Music")]
    [InlineData("Shellac")]
    [InlineData("Test Pressing")]
    [InlineData("")]
    [InlineData(null)]
    public void Anything_else_matches_nothing(string? value) => Assert.Null(Genres.Match(value));

    /// <summary>"Other" is a value a person picks when nothing fits. A lookup never puts a record
    /// into it: a catalogue that says "unknown" gives no information.</summary>
    [Fact]
    public void The_catch_all_is_never_matched_into()
    {
        Assert.Null(Genres.Match("Unknown"));
        Assert.Null(Genres.Match("Other"));
        Assert.Null(Genres.Match("Other / Unknown"));
        Assert.Contains("Other / Unknown", Genres.All);
    }

    /// <summary>The catch-all keeps a longer name than the list's own word for it, because
    /// <see cref="RPM.Core.Library.GenreWall.Unclassified"/> is the literal "Other" and means the
    /// absence of a shelf. A shelf somebody chose and the absence of one cannot be one string.</summary>
    [Fact]
    public void The_catch_all_is_not_spelled_like_the_walls_word_for_no_shelf()
    {
        Assert.Equal("Other / Unknown", Genres.Unknown);
        Assert.NotEqual(RPM.Core.Library.GenreWall.Unclassified, Genres.Unknown);
    }

    [Fact]
    public void The_first_answer_the_app_knows_wins_over_the_ones_it_does_not() =>
        Assert.Equal("Reggae", Genres.Pick(["Non-Music", "Reggae", "Jazz"]));

    /// <summary>A style is read after every genre, not alongside them: a record whose genre the app
    /// knows is filed under that, whatever its style says.</summary>
    [Fact]
    public void A_genre_is_preferred_over_a_style_that_would_also_match() =>
        Assert.Equal("Electronic / Dance", Genres.Pick(["Electronic"], ["Synth-pop", "Italo-Disco"]));

    [Fact]
    public void A_style_is_read_when_no_genre_matches() =>
        Assert.Equal("Punk", Genres.Pick(["Non-Music"], ["Post-Punk"]));

    /// <summary>Nothing on either side is on the app's list, so the record keeps the catalogue's own
    /// genre. Losing it would be worse: an unusual shelf is better than no shelf.</summary>
    [Fact]
    public void What_the_catalogue_said_is_kept_when_nothing_matches() =>
        Assert.Equal("Non-Music", Genres.Pick(["Non-Music"], ["Shellac"]));

    [Fact]
    public void A_style_is_kept_unmatched_when_there_is_no_genre_at_all() =>
        Assert.Equal("Test Pressing", Genres.Pick([], ["Test Pressing"]));

    [Fact]
    public void Nothing_at_all_is_no_genre()
    {
        Assert.Null(Genres.Pick([], []));
        Assert.Null(Genres.Pick(null, null));
        Assert.Null(Genres.Pick(["", "   "]));
    }

    // ── the pair ──

    /// <summary>The purpose of <see cref="Genres.Read"/>: a file with one word gives both halves of the
    /// answer, because a style knows the shelf it belongs to.</summary>
    [Theory]
    [InlineData("Deep House", "House", "Deep House")]
    [InlineData("deep house", "House", "Deep House")]
    [InlineData("Post-Punk", "Punk", "Post-Punk")]
    [InlineData("Indie Rock", "Indie", "Indie Rock")]
    [InlineData("Trip Hop", "Electronic / Dance", "Trip Hop")]
    public void A_style_names_its_own_shelf(string tag, string genre, string subgenre)
    {
        var choice = Genres.Read(tag);
        Assert.Equal(genre, choice.Genre);
        Assert.Equal(subgenre, choice.Subgenre);
        Assert.True(choice.HasSubgenre);
    }

    /// <summary>A shelf named on its own is a complete answer with no style in it. Most records are
    /// like this and it is not a failure.</summary>
    [Theory]
    [InlineData("Rock", "Rock")]
    [InlineData("Jazz", "Jazz")]
    [InlineData("Electronic", "Electronic / Dance")]
    public void A_shelf_on_its_own_has_no_style(string tag, string genre)
    {
        var choice = Genres.Read(tag);
        Assert.Equal(genre, choice.Genre);
        Assert.Null(choice.Subgenre);
        Assert.True(choice.IsKnown);
        Assert.False(choice.HasSubgenre);
    }

    /// <summary>Both tables are checked for each key in turn; one table is not run to the end first.
    /// Checking the shelves first files a rap record under Pop, because "Pop Rap" reaches the word
    /// "pop" before the phrase is checked.</summary>
    [Theory]
    [InlineData("Pop Rap", "Rap")]
    [InlineData("Cloud Rap", "Rap")]
    [InlineData("Christian Rock", "Religious")]
    [InlineData("Lovers Rock", "Reggae")]
    public void A_compound_style_beats_the_shelf_its_last_word_names(string tag, string genre) =>
        Assert.Equal(genre, Genres.Read(tag).Genre);

    /// <summary>A tag is often a list, and the phrase inside it carries the style. If the tag is split
    /// on the commas AND the spaces at once, "cloud rap" is never formed, and the record gets no
    /// style.</summary>
    [Fact]
    public void A_phrase_inside_a_list_is_read_whole()
    {
        var choice = Genres.Read("cloud rap, hip hop, trap");
        Assert.Equal("Rap", choice.Genre);
        Assert.Equal("Cloud Rap", choice.Subgenre);
    }

    /// <summary>Left to right, because the first value a tagger wrote is what the record is. Lo-fi is a
    /// style the app knows and it comes before folk.</summary>
    [Fact]
    public void The_earliest_entry_the_app_knows_decides() =>
        Assert.Equal("Indie", Genres.Read("singer-songwriter, lo-fi, folk, bedroom pop").Genre);

    [Theory]
    [InlineData("Non-Music")]
    [InlineData("Shellac")]
    [InlineData("")]
    [InlineData(null)]
    public void A_tag_naming_neither_is_kept_as_it_was_written(string? tag)
    {
        var choice = Genres.Read(tag);
        Assert.Equal(GenreChoice.None, choice);
        Assert.False(choice.IsKnown);
        Assert.False(choice.HasSubgenre);
    }

    /// <summary>A style is never returned without the shelf it belongs to.</summary>
    [Fact]
    public void There_is_no_style_without_a_shelf()
    {
        foreach (var tag in new[] { "Deep House", "Rock", "Non-Music", "", "Pop Rap" })
        {
            var choice = Genres.Read(tag);
            if (choice.HasSubgenre)
                Assert.True(choice.IsKnown);
        }
    }

    // ── what a catalogue gives ──

    /// <summary>Discogs sends styles together with genres. This is the pair they are read into.</summary>
    [Fact]
    public void A_providers_style_becomes_the_style_and_its_genre_stays_the_shelf()
    {
        var choice = Genres.PickPair(["Electronic"], ["Deep House"]);
        Assert.Equal("Electronic / Dance", choice.Genre);
        Assert.Equal("Deep House", choice.Subgenre);
    }

    /// <summary>MusicBrainz has one list and its top vote is often a style. Ignoring it would leave the
    /// record on no shelf at all, so the style names the shelf.</summary>
    [Fact]
    public void A_style_names_the_shelf_when_the_genres_could_not()
    {
        var choice = Genres.PickPair(["deep house"], ["deep house"]);
        Assert.Equal("House", choice.Genre);
        Assert.Equal("Deep House", choice.Subgenre);
    }

    /// <summary>A genre the app recognises is never overridden by a style: Discogs files many rock
    /// records with an electronic style on them.</summary>
    [Fact]
    public void A_recognised_genre_is_not_overruled_by_a_style()
    {
        var choice = Genres.PickPair(["Rock"], ["Synth-pop"]);
        Assert.Equal("Rock", choice.Genre);
        Assert.Equal("Synth-Pop", choice.Subgenre);
    }

    [Fact]
    public void A_provider_that_named_no_style_gives_a_shelf_alone()
    {
        var choice = Genres.PickPair(["Jazz"], []);
        Assert.Equal("Jazz", choice.Genre);
        Assert.Null(choice.Subgenre);
    }

    /// <summary>Nothing recognised on either side, so the catalogue's own word is kept: the same
    /// answer <see cref="Genres.Pick"/> gives, for the same reason.</summary>
    [Fact]
    public void What_the_catalogue_said_is_kept_by_the_pair_too()
    {
        var choice = Genres.PickPair(["Non-Music"], ["Shellac"]);
        Assert.Equal("Non-Music", choice.Genre);
        Assert.Null(choice.Subgenre);
    }

    [Fact]
    public void A_provider_that_said_nothing_gives_nothing() =>
        Assert.Equal(GenreChoice.None, Genres.PickPair(null, null));
    // ── which genres stand next to which ──
    //
    // What a genre's page offers a reader NEXT, in the card under the article. These tests are about
    // the ordering: heads before kinds, because going up is a bigger step than going across, and a
    // genre that belongs to two families gets both its heads before either family's siblings.

    [Fact]
    public void A_head_lists_its_kinds_and_nothing_above_it()
        => Assert.Equal(["Indie", "Punk", "Metal", "Noise"], Genres.Related("Rock"));

    [Fact]
    public void A_kind_lists_the_head_first_and_then_its_siblings()
        => Assert.Equal(["Rock", "Indie", "Metal", "Noise"], Genres.Related("Punk"));

    /// <summary>Disco is a kind of pop and a kind of R&amp;B, and both are true. Both heads come before
    /// either family's siblings. Otherwise the second head is hidden among the first family's siblings,
    /// and a reader cannot find it quickly.</summary>
    [Fact]
    public void A_genre_in_two_families_names_both_heads_before_any_sibling()
        => Assert.Equal(["Pop", "R&B", "Indie", "Club", "Soul", "Funk"], Genres.Related("Disco"));

    /// <summary>Club is in the pop family and the dance family, and Indie is in the rock family and the
    /// pop one. A name reached twice is listed once: a card that said "Indie" twice would look like a
    /// bug.</summary>
    [Fact]
    public void A_genre_reached_through_two_families_is_listed_once()
    {
        var related = Genres.Related("Club");
        Assert.Equal(related.Distinct(StringComparer.OrdinalIgnoreCase).Count(), related.Count);
    }

    /// <summary>Most of the list is in no family, and that is correct, not a gap: RPM does not file a
    /// kind of reggae separately, so there is nothing to offer and the card is not drawn at all.</summary>
    [Theory]
    [InlineData("Reggae")]
    [InlineData("Latin")]
    [InlineData("Spoken Word")]
    [InlineData(Genres.Unknown)]
    [InlineData("Something nobody files under")]
    [InlineData("")]
    [InlineData(null)]
    public void A_genre_in_no_family_has_no_relations(string? genre)
        => Assert.Empty(Genres.Related(genre));

    /// <summary>Every name a family mentions has to be a shelf the app actually offers, or a page would
    /// send a reader to a genre that does not exist. This rule keeps the table correct when
    /// <see cref="Genres.All"/> is edited.</summary>
    [Fact]
    public void Every_relation_is_one_of_the_apps_own_genres()
    {
        foreach (var genre in Genres.All)
            foreach (var related in Genres.Related(genre))
                Assert.Contains(related, Genres.All);
    }

    /// <summary>It works both ways: if Rock offers Punk, Punk offers Rock. Otherwise a reader could go
    /// to a genre and not come back.</summary>
    [Fact]
    public void Standing_next_to_something_is_mutual()
    {
        foreach (var genre in Genres.All)
            foreach (var related in Genres.Related(genre))
                Assert.Contains(genre, Genres.Related(related));
    }
}
