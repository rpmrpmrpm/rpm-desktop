using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Adding up a listen across seeks. The failure these tests guard against: a favourite track played
/// through, with the position dragged back to the chorus twice, scores only its longest single
/// stretch and so counts as nothing at all.
/// </summary>
public class ListenTallyTests
{
    private const string Track = "/music/Celine Dion/01 - A New Day Has Come.flac";
    private const string Other = "/music/Celine Dion/02 - The Power Of Love.flac";
    private const double Length = 275;

    [Fact]
    public void A_straight_listen_is_what_was_heard()
    {
        var tally = new ListenTally();

        Assert.Null(tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 0, lengthSeconds: Length));
        Assert.Null(tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 120, lengthSeconds: Length));

        var finished = tally.Sample(Other, session: 2, startSeconds: 0, heardSeconds: 0, lengthSeconds: Length);

        Assert.NotNull(finished);
        Assert.Equal(Track, finished!.Value.Path);
        Assert.Equal(120, finished.Value.HeardSeconds);
        Assert.Equal(Length, finished.Value.LengthSeconds);
    }

    /// <summary>The whole point. Two minutes, a drag back to the chorus, two minutes more: four
    /// minutes of the track heard, and the counter underneath went back to zero in between.</summary>
    [Fact]
    public void Seeking_adds_up_rather_than_starting_over()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 120, lengthSeconds: Length);
        // The seek: same track, a new run of the pump, started well into the track.
        tally.Sample(Track, session: 2, startSeconds: 90, heardSeconds: 0, lengthSeconds: Length);
        tally.Sample(Track, session: 2, startSeconds: 90, heardSeconds: 120, lengthSeconds: Length);

        Assert.Equal(240, tally.HeardSeconds);
        Assert.True(Listen.Counts(tally.HeardSeconds, tally.LengthSeconds));
    }

    [Fact]
    public void Several_seeks_all_count()
    {
        var tally = new ListenTally();

        for (var pass = 1; pass <= 4; pass++)
        {
            tally.Sample(Track, session: pass, startSeconds: pass == 1 ? 0 : 60, heardSeconds: 0, lengthSeconds: Length);
            tally.Sample(Track, session: pass, startSeconds: pass == 1 ? 0 : 60, heardSeconds: 40, lengthSeconds: Length);
        }

        Assert.Equal(160, tally.HeardSeconds);
    }

    /// <summary>A track started again from its beginning is a second listen, not a longer one, or a
    /// track left on repeat would accumulate a single endless play out of a hundred passes.</summary>
    [Fact]
    public void Playing_it_again_from_the_top_settles_the_first_listen()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 200, lengthSeconds: Length);

        var finished = tally.Sample(Track, session: 2, startSeconds: 0, heardSeconds: 0, lengthSeconds: Length);

        Assert.NotNull(finished);
        Assert.Equal(200, finished!.Value.HeardSeconds);
        Assert.Equal(0, tally.HeardSeconds);
    }

    /// <summary>A low reading is not trusted as a rewind. A chained track's counter climbs from
    /// zero while the sink is still on the outgoing track's tail, and the session does not change at a
    /// gapless changeover, so a low reading is a short lag, never a rewind.</summary>
    [Fact]
    public void A_dip_inside_one_stretch_is_ignored()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 150, lengthSeconds: Length);
        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 2, lengthSeconds: Length);

        Assert.Equal(150, tally.HeardSeconds);
    }

    [Fact]
    public void Stopping_settles_what_was_playing()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 180, lengthSeconds: Length);

        var finished = tally.Sample(null, session: 2, startSeconds: 0, heardSeconds: 0, lengthSeconds: 0);

        Assert.NotNull(finished);
        Assert.Equal(Track, finished!.Value.Path);
        Assert.Equal(180, finished.Value.HeardSeconds);
        Assert.Null(tally.Path);
    }

    [Fact]
    public void Nothing_playing_settles_nothing()
    {
        var tally = new ListenTally();

        Assert.Null(tally.Sample(null, session: 0, startSeconds: 0, heardSeconds: 0, lengthSeconds: 0));
        Assert.Null(tally.Sample(null, session: 1, startSeconds: 0, heardSeconds: 0, lengthSeconds: 0));
    }

    /// <summary>A length the engine only reports once the file is open, after the first reading of a
    /// stretch has already been taken.</summary>
    [Fact]
    public void The_length_is_kept_once_something_says_what_it_is()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 0, lengthSeconds: 0);
        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 10, lengthSeconds: Length);
        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 20, lengthSeconds: 0);

        Assert.Equal(Length, tally.LengthSeconds);
    }

    [Fact]
    public void Closing_hands_back_the_record_still_on_the_player()
    {
        var tally = new ListenTally();

        tally.Sample(Track, session: 1, startSeconds: 0, heardSeconds: 140, lengthSeconds: Length);

        var finished = tally.Close();

        Assert.NotNull(finished);
        Assert.Equal(Track, finished!.Value.Path);
        Assert.Equal(140, finished.Value.HeardSeconds);

        // And only once: a shutdown that both closes and samples must not count it twice.
        Assert.Null(tally.Close());
    }
}
