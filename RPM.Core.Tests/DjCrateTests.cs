using RPM.Core.Analysis;
using RPM.Core.Mixing;

namespace RPM.Core.Tests;

/// <summary>
/// The crate, and the one decision it can be told not to make.
///
/// <para>The DJ normally picks what comes next from what is left, by tempo, key and energy. A
/// playlist mixed on the phone is played in its own order instead: the mixing is the same, but the
/// DJ does not choose. That switch is what is tested here.</para>
/// </summary>
public class DjCrateTests
{
    private static MixTrack Track(string name, double bpm, MusicalKey key)
    {
        var path = $"/library/{name}.flac";

        return new MixTrack
        {
            Source = path,
            Path = path,
            Title = name,
            TaggedDuration = 200,
            Analysis = new TrackAnalysis
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
            },
        };
    }

    private static readonly MusicalKey AMinor = new(9, true);

    /// <summary>Slow, then a record that matches what is playing, then one that does not. When it
    /// plans, the DJ skips the mismatch and picks the match.</summary>
    private static List<MixTrack> Crate() =>
    [
        Track("opener", 124, AMinor),
        Track("wrong-tempo", 92, AMinor),
        Track("right-tempo", 125, AMinor),
    ];

    [Fact]
    public void The_dj_picks_the_record_that_follows_best()
    {
        var records = Crate();
        var crate = new DjCrate(records);

        var playing = crate.TakeNext(null, 0);
        var next = crate.TakeNext(playing, playing!.Bpm);

        Assert.Equal("right-tempo", next!.Title);
    }

    /// <summary>When told not to plan, the DJ plays them in the order given, including the mismatch.
    /// Somebody chose that order.</summary>
    [Fact]
    public void An_unplanned_crate_keeps_the_order_it_was_given()
    {
        var crate = new DjCrate(Crate(), plan: false);

        var playing = crate.TakeNext(null, 0);
        Assert.Equal("opener", playing!.Title);

        Assert.Equal("wrong-tempo", crate.TakeNext(playing, playing.Bpm)!.Title);
        Assert.Equal("right-tempo", crate.TakeNext(playing, playing.Bpm)!.Title);
        Assert.Null(crate.TakeNext(playing, playing.Bpm));
    }

    /// <summary>Order does not replace analysis: a record analysed after the set started still
    /// replaces its entry in the queue, so its changeover is a beat-match and not a fade.</summary>
    [Fact]
    public void An_unplanned_crate_still_takes_a_late_analysis()
    {
        var crate = new DjCrate(
            [Track("first", 120, AMinor), new MixTrack { Source = "/library/late.flac", Path = "/library/late.flac" }],
            plan: false);

        crate.Update(Track("late", 121, AMinor) with { Path = "/library/late.flac" });

        crate.TakeNext(null, 0);
        var second = crate.TakeNext(null, 120);

        Assert.NotNull(second!.Analysis);
        Assert.Equal(121, second.Bpm);
    }

    /// <summary>A crate that fills up while the set plays (a phone's crate does, because its records
    /// arrive over the network) keeps giving them out as they arrive.</summary>
    [Fact]
    public void A_record_added_after_the_set_started_is_played()
    {
        var crate = new DjCrate([Track("first", 120, AMinor)], plan: false);

        var playing = crate.TakeNext(null, 0);
        Assert.Null(crate.TakeNext(playing, 120));      // the set would end here

        crate.Add(Track("late-arrival", 121, AMinor));

        Assert.Equal("late-arrival", crate.TakeNext(playing, 120)!.Title);
    }

    /// <summary>Offering the same file twice does not put it in the set twice: a fetcher that retries
    /// must not make a record play again.</summary>
    [Fact]
    public void A_record_offered_twice_is_added_once()
    {
        var crate = new DjCrate([], plan: false);

        crate.Add(Track("only", 120, AMinor));
        crate.Add(Track("only", 120, AMinor));

        Assert.Equal(1, crate.RemainingCount);
    }

    /// <summary>A record already played is not put back by a late offer either: the fetcher does not
    /// know what the DJ has played.</summary>
    [Fact]
    public void A_record_already_played_is_not_added_again()
    {
        var crate = new DjCrate([Track("gone", 120, AMinor)], plan: false);

        crate.TakeNext(null, 0);
        crate.Add(Track("gone", 120, AMinor));

        Assert.True(crate.IsEmpty);
    }

    /// <summary>A record the DJ cued and then put back goes back to the front, so an unplanned crate
    /// plays it next and it keeps its place in the order.</summary>
    [Fact]
    public void A_record_put_back_is_next_out_again()
    {
        var crate = new DjCrate(Crate(), plan: false);

        var first = crate.TakeNext(null, 0);
        var second = crate.TakeNext(first, 124);
        crate.PutBack(second!);

        Assert.Equal(second!.Title, crate.TakeNext(first, 124)!.Title);
    }
}
