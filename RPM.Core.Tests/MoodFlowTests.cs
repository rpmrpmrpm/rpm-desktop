using RPM.Core.Analysis;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// The order a generated list plays in, after its songs have been chosen.
///
/// <para>This is the only step in the mood path that changes the order. Everything before it keeps
/// ranked order, so these tests are just as much about what this step must NOT do: it must never change
/// which songs are in the list, and it must return the same order twice.</para>
/// </summary>
public class MoodFlowTests
{
    private static readonly IReadOnlyDictionary<string, MusicalKey> NoKeys =
        new Dictionary<string, MusicalKey>();

    /// <summary>One song, placed on the three axes. Nothing else about it matters here, so the rest
    /// is filled with the values of a song that no other narrowing affects.
    ///
    /// <para>Each song has its own record and its own act. A pool takes at most one song from any
    /// record and five from any act, so test songs that share a folder would give a list of one,
    /// however many there are.</para></summary>
    private static MoodList.Song Song(
        string path, double brightness, double onsets = 0, double dynamics = 0) =>
        new(path, "Rock", $"folder/{path}", $"act {path}", 1990, false, [],
            new MoodList.Sound(brightness, onsets, dynamics), WithinGenreBy: 0);

    private static MoodList.Song Unmeasured(string path) =>
        new(path, "Rock", $"folder/{path}", $"act {path}", 1990, false, [], MoodList.Sound.Nothing,
            WithinGenreBy: 0);

    private static IReadOnlyList<string> Order(
        IReadOnlyList<MoodList.Song> songs, IReadOnlyDictionary<string, MusicalKey>? keys = null) =>
        [.. MoodFlow.Ordered(songs, keys ?? NoKeys).Select(s => s.Path)];

    private static Dictionary<string, MusicalKey> Keys(params (string Path, string Camelot)[] said)
    {
        var keys = new Dictionary<string, MusicalKey>(StringComparer.OrdinalIgnoreCase);
        foreach (var (path, camelot) in said)
            keys[path] = MusicalKey.FromCamelot(camelot);
        return keys;
    }

    // ── what it must not do ──

    /// <summary>The window and the caps choose the songs. This step may only change the order, so the
    /// set it returns is always the set it was given.</summary>
    [Fact]
    public void The_same_songs_come_back()
    {
        List<MoodList.Song> songs =
            [Song("a", 0.1), Song("b", 0.9), Song("c", 0.5), Song("d", 0.2), Song("e", 0.7)];

        Assert.Equal(
            songs.Select(s => s.Path).OrderBy(p => p, StringComparer.Ordinal),
            Order(songs).OrderBy(p => p, StringComparer.Ordinal));
    }

    /// <summary>The list still starts on the song the ranking put first. That is the main purpose of
    /// the ranking.</summary>
    [Fact]
    public void The_first_song_does_not_move()
    {
        Assert.Equal("a", Order([Song("a", 0.9), Song("b", 0.0), Song("c", 0.1)])[0]);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(1)]
    [InlineData(2)]
    public void Too_short_to_order_comes_back_untouched(int count)
    {
        var songs = Enumerable.Range(0, count).Select(i => Song($"{i}", i * 0.3)).ToList();

        Assert.Equal(songs.Select(s => s.Path), Order(songs));
    }

    /// <summary>Two reads of one list must return the same order, so nothing here may depend on
    /// dictionary order or on a tie broken by chance.</summary>
    [Fact]
    public void Two_orderings_of_one_list_agree()
    {
        List<MoodList.Song> songs =
            [.. Enumerable.Range(0, 20).Select(i => Song($"{i}", (i * 7 % 20) / 20.0, i % 3, i % 5))];

        Assert.Equal(Order(songs), Order(songs));
    }

    /// <summary>Songs that fit equally well are taken in ranked order. That makes the test above
    /// always true, not only usually true.</summary>
    [Fact]
    public void A_tie_goes_to_the_better_ranked_song()
    {
        // b and c are the same distance from a on the only axis that has any range.
        Assert.Equal(["a", "b", "c"], Order([Song("a", 0.5), Song("b", 1.0), Song("c", 0.0)]));
    }

    // ── what it does ──

    /// <summary>The main purpose: the list moves along the axis step by step instead of jumping across
    /// it.</summary>
    [Fact]
    public void Each_song_is_followed_by_the_one_nearest_it()
    {
        Assert.Equal(
            ["a", "c", "b"],
            Order([Song("a", 0.0), Song("b", 1.0), Song("c", 0.5)]));
    }

    /// <summary>An axis where every song has the same value says nothing about their order, and must
    /// not outweigh the axes that do. Dynamics are in decibels and have the largest numbers of the
    /// three, so without normalising, this axis would decide everything.</summary>
    [Fact]
    public void An_axis_with_no_range_has_no_opinion()
    {
        Assert.Equal(
            ["a", "c", "b"],
            Order([
                Song("a", 0.0, dynamics: 40),
                Song("b", 1.0, dynamics: 40),
                Song("c", 0.5, dynamics: 40)]));
    }

    /// <summary>A song with no reading cannot be placed by how it sounds. It is kept, and goes last in
    /// ranked order, instead of being guessed at or dropped.</summary>
    [Fact]
    public void A_song_with_no_reading_goes_last()
    {
        var order = Order([
            Song("a", 0.0), Unmeasured("x"), Song("b", 1.0), Unmeasured("y"), Song("c", 0.5)]);

        Assert.Equal(["a", "c", "b", "x", "y"], order);
    }

    /// <summary>
    /// A list whose songs lie on a line moves along that line, and turns back at most once.
    ///
    /// <para><b>Once is the minimum, not a failure.</b> The first song does not move, and a walk that
    /// starts in the middle of a line has to cover both sides of its start, so one jump back is
    /// necessary however the rest is arranged. What must not happen is a walk that keeps crossing
    /// itself, going above and below the start in turn. That is several jumps, and a listener hears
    /// it as the list changing direction again and again.</para>
    ///
    /// <para>The test data starts in the middle on purpose, because that is the normal case: the
    /// window moves through the ranked pool on every opening, so a list rarely starts at either end
    /// of the collection.</para>
    /// </summary>
    [Fact]
    public void A_list_in_a_line_turns_round_at_most_once()
    {
        var spaced = Enumerable.Range(0, 11).Select(i => i / 10.0).ToList();
        var songs = new List<MoodList.Song> { Song("start", spaced[5]) };
        songs.AddRange(spaced.Where((_, i) => i != 5).Select(at => Song($"{at:F1}", at)));

        var walked = Order(songs).Select(p => p == "start" ? 0.5 : double.Parse(p)).ToList();

        // One step of the ladder is 0.1. Anything longer is a jump.
        var jumps = Enumerable.Range(1, walked.Count - 1)
            .Where(i => Math.Abs(walked[i] - walked[i - 1]) > 0.1 + 1e-9)
            .Select(i => $"{walked[i - 1]:F1} to {walked[i]:F1}")
            .ToList();

        Assert.True(jumps.Count <= 1, $"the walk turned round {jumps.Count} times: {string.Join(", ", jumps)}");
    }

    // ── the key, which only some songs have ──

    /// <summary>When the sound is identical, the keys decide. This is harmonic mixing, and it is why
    /// the key term exists.</summary>
    [Fact]
    public void A_matching_key_is_preferred_to_a_clashing_one()
    {
        var order = Order(
            [Song("a", 0.5), Song("clash", 0.5), Song("match", 0.5)],
            Keys(("a", "8A"), ("clash", "2A"), ("match", "8A")));

        Assert.Equal(["a", "match", "clash"], order);
    }

    /// <summary>
    /// An unknown key is neither a match nor a clash, and both halves of that are checked.
    ///
    /// <para>The first half is the important one. If an unknown key scored as a match, two songs that
    /// were never analysed would beat two that were analysed and really fit, and the list would prefer
    /// missing data. The less the analyser had run, the worse this would be, and on a real shelf that
    /// is most of it.</para>
    /// </summary>
    [Fact]
    public void An_unknown_key_is_neither_a_match_nor_a_clash()
    {
        var beatenByAMatch = Order(
            [Song("a", 0.5), Song("unknown", 0.5), Song("match", 0.5)],
            Keys(("a", "8A"), ("match", "8A")));

        Assert.Equal(["a", "match", "unknown"], beatenByAMatch);

        var beatsAClash = Order(
            [Song("a", 0.5), Song("clash", 0.5), Song("unknown", 0.5)],
            Keys(("a", "8A"), ("clash", "2B")));

        Assert.Equal(["a", "unknown", "clash"], beatsAClash);
    }

    /// <summary>The key is measured for only a few percent of a real shelf and the sound for all of it,
    /// so a key must never outweigh the sound. Here the harmonic match is at the far end of the sound
    /// axis and loses to the near song that has no key.</summary>
    [Fact]
    public void The_sound_outweighs_the_key()
    {
        var order = Order(
            [Song("a", 0.0), Song("far match", 1.0), Song("near", 0.05)],
            Keys(("a", "8A"), ("far match", "8A")));

        Assert.Equal("near", order[1]);
    }

    // ── through the pool, which is how the app uses it ──

    /// <summary>The two entry points on <see cref="MoodPool"/> pick the same songs and differ only in
    /// order. The pane that counts a window and the rule that builds the list must never disagree about
    /// which songs a list contains.</summary>
    [Fact]
    public void The_pool_orders_without_changing_what_it_picked()
    {
        var pool = new MoodPool(
            [.. Enumerable.Range(0, 12).Select(i => Song($"{i}", (i * 5 % 12) / 12.0))]);

        var ranked = pool.Window(8, 0);
        var flowed = pool.InFlow(8, readings: 1, 0, _ => NoKeys);

        Assert.Equal(8, flowed.Count);
        Assert.Equal(
            ranked.OrderBy(p => p, StringComparer.Ordinal),
            flowed.OrderBy(p => p, StringComparer.Ordinal));
        Assert.NotEqual(ranked, flowed);
    }

    /// <summary>
    /// A run is ordered one reading at a time, so its first reading is the list that one reading gives.
    ///
    /// <para><see cref="MoodFlow.Ordered"/> walks nearest-sounding-first and normalises its distances
    /// over whatever it is given. If a run sent to the phone were ordered as a whole, the first fifty
    /// of a run of two hundred would be fifty songs taken from all over the run, in an order that
    /// belongs to the two hundred. The phone and the computer would then show different songs under one
    /// name, which is exactly what the cut must prevent.</para>
    ///
    /// <para>The pool here is deliberately not a ramp (a song's sound does not follow its rank), so the
    /// flow ordering really moves songs. Over a ramp the two orderings agree by chance and the test
    /// would pass either way.</para>
    /// </summary>
    [Fact]
    public void A_run_is_ordered_one_reading_at_a_time()
    {
        // Twelve records, one song each, whose brightness is in a different order from their rank.
        var pool = new MoodPool(
            [.. Enumerable.Range(0, 12).Select(i => Song($"{i:00}", (i * 5 % 12) / 12.0))]);

        var oneList = pool.InFlow(4, readings: 1, 0, _ => NoKeys);
        var run = pool.InFlow(4, readings: 3, 0, _ => NoKeys);

        // The run is three lists long, and its first list is the single list: the same songs in the
        // same order. This is all that the phone and the computer have to agree on.
        Assert.Equal(12, run.Count);
        Assert.Equal(oneList, run.Take(4));

        // It really is a reordering, not the ranked order passed through. Otherwise the line above
        // would pass whatever this does.
        Assert.NotEqual(pool.Window(4, 0), oneList);

        // Each reading in the run is a separate list: no record appears twice across the run, so no
        // two readings are the same list.
        Assert.Equal(run.Count, run.Distinct(StringComparer.Ordinal).Count());
    }

    /// <summary>The keys are requested once, for the songs the window actually chose. Requesting them
    /// for any other songs would mean picking twice.</summary>
    [Fact]
    public void The_pool_asks_for_the_keys_of_exactly_the_songs_it_picked()
    {
        var pool = new MoodPool([.. Enumerable.Range(0, 12).Select(i => Song($"{i}", i / 12.0))]);

        var asked = new List<IReadOnlyList<string>>();
        var flowed = pool.InFlow(5, readings: 1, 0, paths =>
        {
            asked.Add(paths);
            return NoKeys;
        });

        var once = Assert.Single(asked);
        Assert.Equal(
            flowed.OrderBy(p => p, StringComparer.Ordinal),
            once.OrderBy(p => p, StringComparer.Ordinal));
    }
}
