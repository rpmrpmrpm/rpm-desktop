using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The next key during a set, and the order the queue shows.
///
/// <para>The key is enabled whenever there is a record left: next plays the next record without
/// mixing into it, so no record needs a reading for it to work. A reading still decides the order
/// (a record that has not been read cannot be placed against the one playing), so the queue moves
/// into the real play order as the readings arrive.</para>
/// </summary>
public class DjNextReadyTests
{
    private static MixTrack Track(string name, double bpm, MusicalKey key, bool analyzed = true)
    {
        var path = $"/library/{name}.flac";

        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = 200,
            Analysis = analyzed
                ? new TrackAnalysis
                {
                    FilePath = path,
                    DurationSeconds = 200,
                    Bpm = bpm,
                    BpmConfidence = 0.9,
                    BeatTimes = [.. Enumerable.Range(0, 64).Select(i => i * 60.0 / bpm)],
                    Key = key,
                    KeyConfidence = 0.8,
                    Intensity = 0.5,
                    OutroStartSeconds = 180,
                }
                : null,
        };
    }

    private static readonly MusicalKey AMinor = new(9, true);

    [Fact]
    public void A_crate_counts_the_records_that_have_been_read()
    {
        var crate = new DjCrate([
            Track("read", 124, AMinor),
            Track("unread", 0, MusicalKey.Unknown, analyzed: false),
        ]);

        Assert.Equal(2, crate.RemainingCount);
        Assert.Equal(1, crate.AnalyzedRemaining);
    }

    /// <summary>A playlist that has never been played: the set opens, and until the first reading
    /// arrives there is nothing the next key could bring in.</summary>
    [Fact]
    public void A_set_of_unread_records_has_nothing_to_mix_into()
    {
        var crate = new DjCrate([
            Track("a", 0, MusicalKey.Unknown, analyzed: false),
            Track("b", 0, MusicalKey.Unknown, analyzed: false),
        ]);

        Assert.Equal(0, crate.AnalyzedRemaining);

        crate.Update(Track("b", 126, AMinor) with { Path = "/library/b.flac" });
        Assert.Equal(1, crate.AnalyzedRemaining);
    }

    /// <summary>What the transport is bound to. The DJ raises a change notification, so the interface
    /// does not have to poll a crate.</summary>
    [Fact]
    public void The_dj_says_when_the_next_key_comes_back_to_life()
    {
        var crate = new DjCrate([
            Track("opener", 124, AMinor),
            Track("late", 0, MusicalKey.Unknown, analyzed: false),
        ]);

        using var dj = new Dj(crate, 44100, 2);

        var said = new List<bool>();
        dj.NextReadyChanged += ready => said.Add(ready);

        Assert.True(dj.IsNextReady);         // the opener has been read and is still in the crate

        // The analyser reads the record that had no reading. The answer has not changed (there was
        // already a read record), so nothing is announced twice.
        dj.NoteAnalysis(Track("late", 125, AMinor) with { Path = "/library/late.flac" });
        Assert.Empty(said);
        Assert.True(dj.IsNextReady);
    }

    [Fact]
    public void The_next_key_is_live_on_a_set_nobody_has_ever_read()
    {
        // Next plays the next record without mixing into it, and playing one needs no grid. So the
        // next key works in a newly opened playlist before the first reading arrives, which on a slow
        // machine can take most of the opening track.
        var crate = new DjCrate([
            Track("a", 0, MusicalKey.Unknown, analyzed: false),
            Track("b", 0, MusicalKey.Unknown, analyzed: false),
        ]);

        using var dj = new Dj(crate, 44100, 2);

        var said = new List<bool>();
        dj.NextReadyChanged += ready => said.Add(ready);

        Assert.True(dj.IsNextReady);

        // And a reading that arrives does not change the answer, so nothing is announced.
        dj.NoteAnalysis(Track("b", 126, AMinor) with { Path = "/library/b.flac" });

        Assert.True(dj.IsNextReady);
        Assert.Empty(said);
    }

    /// <summary>The purpose of the tier: a record that has been read is taken before one that has not,
    /// however well the unread one scores on the little that is known about it.</summary>
    [Fact]
    public void An_unread_record_is_not_taken_while_a_read_one_is_left()
    {
        var playing = Track("playing", 128, AMinor);
        var unread = Track("unread", 0, MusicalKey.Unknown, analyzed: false);

        // A poor next record on purpose: a clash on the wheel and 1.5 times the tempo. It is still
        // taken, because its behaviour at the changeover is known and the other's is not.
        var awkward = Track("awkward", 175, new MusicalKey(3, false));

        Assert.Same(awkward, DjSetPlanner.Next(playing, 128, [unread, awkward]));
    }

    /// <summary>With nothing read at all, the set must still play: it plays in the order it was given,
    /// which is the playlist's own.</summary>
    [Fact]
    public void With_nothing_read_the_playlist_order_stands()
    {
        var playing = Track("playing", 128, AMinor);
        var first = Track("first", 0, MusicalKey.Unknown, analyzed: false);
        var second = Track("second", 0, MusicalKey.Unknown, analyzed: false);

        Assert.Same(first, DjSetPlanner.Next(playing, 128, [first, second]));
    }

    /// <summary>What the queue shows: the records played, in the order they were played, then the rest
    /// as the DJ would take them.</summary>
    [Fact]
    public void The_running_order_is_what_has_been_played_then_what_follows_best()
    {
        var crate = new DjCrate([
            Track("opener", 124, AMinor),
            Track("wrong-tempo", 92, AMinor),
            Track("right-tempo", 125, AMinor),
        ]);

        using var dj = new Dj(crate, 44100, 2);

        // Nothing has played yet, so the projection starts with the crate's own first record (where a
        // set begins is the listener's choice, not the planner's), and the rest are chosen against it:
        // the record that matches its tempo comes next, and the one that does not comes last.
        Assert.Equal(
            ["/library/opener.flac", "/library/right-tempo.flac", "/library/wrong-tempo.flac"],
            dj.RunningOrder.Select(t => t.Path));

        crate.TakeNext(playing: null, playingBpm: 0);

        Assert.Equal(
            ["/library/opener.flac", "/library/right-tempo.flac", "/library/wrong-tempo.flac"],
            dj.RunningOrder.Select(t => t.Path));
    }

    /// <summary>A projection, not a guarantee: a record that could not be placed moves into its place
    /// as soon as it has been read.</summary>
    [Fact]
    public void The_running_order_settles_as_the_readings_land()
    {
        var crate = new DjCrate([
            Track("opener", 124, AMinor),
            Track("late", 0, MusicalKey.Unknown, analyzed: false),
            Track("mismatch", 92, AMinor),
        ]);

        using var dj = new Dj(crate, 44100, 2);
        crate.TakeNext(playing: null, playingBpm: 0);

        // Unread, so it cannot be chosen while a read record is left, however well it would fit.
        Assert.Equal(
            ["/library/opener.flac", "/library/mismatch.flac", "/library/late.flac"],
            dj.RunningOrder.Select(t => t.Path));

        dj.NoteAnalysis(Track("late", 125, AMinor) with { Path = "/library/late.flac" });

        Assert.Equal(
            ["/library/opener.flac", "/library/late.flac", "/library/mismatch.flac"],
            dj.RunningOrder.Select(t => t.Path));
    }
}
