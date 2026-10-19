using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Core.Metadata.Providers;

namespace RPM.Core.Tests;

/// <summary>
/// What a generated playlist goes looking for, and which of the answers it takes.
///
/// <para>The network part cannot be made deterministic, so the tests cover the part that decides:
/// which words are sent, and where in the results each list starts.</para>
/// </summary>
public class CoverLooksTests
{
    // ── what goes out ──

    /// <summary>Every pair the shelf can actually draw, which is what the vocabulary must cover.
    /// <see cref="Genres.Unknown"/> is left out: the shelf never breaks it out, because it is the
    /// answer for a record that matched no other genre, not a kind of music.</summary>
    public static TheoryData<SmartPlaylistKind, string> EveryPair()
    {
        var pairs = new TheoryData<SmartPlaylistKind, string>();
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
            foreach (var genre in Genres.All.Where(g => g != Genres.Unknown))
                pairs.Add(kind, genre);
        return pairs;
    }

    /// <summary>
    /// The trap the whole design avoids: every genre this app uses is also an ordinary noun. A search
    /// on the playlist's name would ask for "Rock" and get geology, "Metal" would get armour, and
    /// "Folk", "House" and "Garage" are no better.
    ///
    /// <para>Checked over EVERY pair, not a few, and against each half of a slashed label as well as
    /// the whole label: "Electronic / Dance" is two words that would each spoil a query on their own.
    /// The whole table follows this rule, so a test enforces it, not a comment.</para>
    /// </summary>
    /// <para>Checked only for the pairs that have words of their OWN. A pair that falls back uses its
    /// mood's words, and a mood's words can contain a genre's name by chance: "club dance floor
    /// lights" is a good query for the Dancefloor list, and there is a genre called Club. The rule is
    /// about what somebody wrote for a pair, not about the name of a shelf.</para>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void No_search_carries_its_own_genre_word(SmartPlaylistKind kind, string genre)
    {
        var look = CoverLooks.Look(SmartPlaylistId.Within(kind, genre));
        Assert.NotNull(look);

        if (look!.Value.Query == CoverLooks.Look(SmartPlaylistId.WholeShelf(kind))!.Value.Query)
            return;   // no line of its own; the fallback is not this rule's business

        foreach (var word in genre.Split('/', StringSplitOptions.TrimEntries).Append(genre))
            Assert.DoesNotContain(word, look.Value.Query, StringComparison.OrdinalIgnoreCase);
    }

    /// <summary>
    /// A genre changes the WORDS, which is the purpose of the table.
    ///
    /// <para>Without this, every genre row under one mood runs the same search and differs only in
    /// which of the same thirty results it takes. Then a chill hip-hop list can get a picture of a
    /// British seaside.</para>
    /// </summary>
    [Fact]
    public void A_genre_changes_the_words()
    {
        var whole = CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm))!.Value;
        var classical = CoverLooks.Look(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Classical"))!.Value;
        var hiphop = CoverLooks.Look(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Hip-Hop / Rap"))!.Value;

        Assert.NotEqual(whole.Query, classical.Query);
        Assert.NotEqual(whole.Query, hiphop.Query);
        Assert.NotEqual(classical.Query, hiphop.Query);
    }

    /// <summary>
    /// Every pair the shelf can draw asks for SOMETHING.
    ///
    /// <para>Not "words of its own": <c>CoverLooks.Within</c> is a table of the pairs somebody has
    /// written words for, and a pair with none falls back to its mood. What must hold for all of them
    /// is that a cover can be searched for at all.</para>
    /// </summary>
    [Theory]
    [MemberData(nameof(EveryPair))]
    public void Every_pair_the_shelf_can_draw_asks_for_something(SmartPlaylistKind kind, string genre)
    {
        var look = CoverLooks.Look(SmartPlaylistId.Within(kind, genre));

        Assert.NotNull(look);
        Assert.False(string.IsNullOrWhiteSpace(look!.Value.Query));
    }

    /// <summary>The pairs somebody HAS written words for do not just repeat their mood. Otherwise
    /// there is no reason to write a line for a pair.</summary>
    [Fact]
    public void A_pair_with_words_of_its_own_does_not_repeat_its_mood()
    {
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
        {
            var mood = CoverLooks.Look(SmartPlaylistId.WholeShelf(kind))!.Value.Query;

            foreach (var genre in Genres.All.Where(g => g != Genres.Unknown))
            {
                var pair = CoverLooks.Look(SmartPlaylistId.Within(kind, genre))!.Value.Query;
                if (pair == mood)
                    continue;   // no line of its own, and the fallback is the documented answer

                Assert.NotEqual(mood, pair);
            }
        }
    }

    /// <summary>
    /// And no two of them ask the same thing.
    ///
    /// <para>Two pairs that share a query compete for the same thirty results. The claims file stops
    /// them from getting the same picture; it cannot stop them from getting two pictures of the same
    /// thing.</para>
    /// </summary>
    [Fact]
    public void No_two_lists_ask_for_the_same_thing()
    {
        var asked = new List<string>();
        var moods = new List<string>();

        foreach (var kind in new[] { SmartPlaylistKind.Calm, SmartPlaylistKind.Energetic,
                                     SmartPlaylistKind.Danceable, SmartPlaylistKind.Focus,
                                     SmartPlaylistKind.Sleep })
        {
            var mood = CoverLooks.Look(SmartPlaylistId.WholeShelf(kind))!.Value.Query;
            moods.Add(mood);
            asked.Add(mood);
        }

        // The written lines only. Every pair without one uses its mood's words, so dozens of pairs
        // sharing one query is the fallback working as intended, not two lists in conflict.
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
            foreach (var genre in Genres.All.Where(g => g != Genres.Unknown))
            {
                var pair = CoverLooks.Look(SmartPlaylistId.Within(kind, genre))!.Value.Query;
                if (!moods.Contains(pair))
                    asked.Add(pair);
            }

        Assert.Equal(asked.Count, asked.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }

    /// <summary>A genre nobody has written a line for uses its mood's words, not nothing.</summary>
    [Fact]
    public void A_genre_with_no_line_falls_back_to_its_mood()
    {
        var mood = CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm))!.Value;
        var invented = CoverLooks.Look(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Sea Shanty"))!.Value;

        Assert.Equal(mood.Query, invented.Query);
    }

    /// <summary>Lights out is cut by an absolute level, not within a genre, so it has no genre
    /// rows and no lines. It uses its own words whatever it is given.</summary>
    [Fact]
    public void Lights_out_has_no_genre_words_because_it_has_no_genre_rows()
    {
        Assert.DoesNotContain(SmartPlaylistKind.Sleep, SmartPlaylists.BrokenOutByGenre);

        var whole = CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Sleep))!.Value;
        var narrowed = CoverLooks.Look(SmartPlaylistId.Within(SmartPlaylistKind.Sleep, "Jazz"))!.Value;

        Assert.Equal(whole.Query, narrowed.Query);
    }

    /// <summary>Each mood asks for something of its own, so five lists are not one search five
    /// times.</summary>
    [Fact]
    public void Each_mood_asks_for_something_different()
    {
        var moods = new[]
        {
            SmartPlaylistKind.Calm, SmartPlaylistKind.Energetic,
            SmartPlaylistKind.Danceable, SmartPlaylistKind.Focus, SmartPlaylistKind.Sleep,
        };

        var asked = moods
            .Select(k => CoverLooks.Look(SmartPlaylistId.WholeShelf(k))!.Value.Query)
            .ToList();

        Assert.Equal(moods.Length, asked.Distinct(StringComparer.OrdinalIgnoreCase).Count());
        Assert.DoesNotContain(asked, q => string.IsNullOrWhiteSpace(q));
    }

    /// <summary>Only the moods have a look. There is no sensible picture search for "songs you have
    /// not played", and making one up would put a photograph on a list that is not about a mood.</summary>
    [Fact]
    public void Only_the_moods_go_looking()
    {
        Assert.NotNull(CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Sleep)));
        Assert.Null(CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed)));
        Assert.Null(CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.MostPlayed)));
        Assert.Null(CoverLooks.Look(SmartPlaylistId.WholeShelf(SmartPlaylistKind.FavoriteTracks)));
    }
}
