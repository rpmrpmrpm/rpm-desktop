using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>A CD track being read and played at once: when the player may start, and what happens to the read
/// when the listener jumps past it. A second of audio is 75 sectors.</summary>
public class CdTrackFillTests
{
    private const int Second = 75;
    private const int Track = 300 * Second;

    private static CdTrackFill Fill() => new(Track, startLeadSeconds: 5, jumpLeadSeconds: 3);

    private static (int From, int Count) Next(CdTrackFill fill, CancellationTokenSource? run = null) =>
        fill.Next(run ?? new CancellationTokenSource()) ?? throw new Xunit.Sdk.XunitException("the track is whole");

    [Fact]
    public void A_new_track_is_read_from_its_start_to_its_end()
    {
        Assert.Equal((0, Track), Next(Fill()));
    }

    [Fact]
    public void The_player_waits_for_the_lead()
    {
        var fill = Fill();

        fill.Arrived(0, 4 * Second);
        Assert.False(fill.Enough.IsCompleted);

        fill.Arrived(0, 5 * Second);
        Assert.True(fill.Enough.IsCompleted);
    }

    [Fact]
    public void A_track_shorter_than_the_lead_never_has_enough_so_the_caller_watches_the_read()
    {
        var fill = new CdTrackFill(3 * Second, startLeadSeconds: 5, jumpLeadSeconds: 3);

        fill.Arrived(0, 3 * Second);

        // The whole track is in, which is as much as there will ever be.
        Assert.True(fill.Enough.IsCompleted);
    }

    [Fact]
    public void A_reader_that_cannot_be_played_early_has_enough_only_when_the_track_is_whole()
    {
        var fill = new CdTrackFill(Track, startLeadSeconds: double.PositiveInfinity, jumpLeadSeconds: 3);

        fill.Arrived(0, Track - 1);
        Assert.False(fill.Enough.IsCompleted);

        fill.Arrived(Track - 1, 1);
        Assert.True(fill.Enough.IsCompleted);
    }

    [Fact]
    public void A_jump_into_audio_that_has_arrived_lands_at_once_and_leaves_the_read_alone()
    {
        var fill = Fill();
        using var run = new CancellationTokenSource();
        Next(fill, run);
        fill.Arrived(0, 100 * Second);

        var landed = fill.Jump(50);

        Assert.True(landed.IsCompleted);
        Assert.False(run.IsCancellationRequested);
    }

    [Fact]
    public void A_jump_past_the_read_stops_it_and_sends_it_there()
    {
        var fill = Fill();
        using var run = new CancellationTokenSource();
        Next(fill, run);
        fill.Arrived(0, 20 * Second);

        var landed = fill.Jump(200);

        Assert.False(landed.IsCompleted);
        Assert.True(run.IsCancellationRequested);
        Assert.Equal((200 * Second, Track - 200 * Second), Next(fill));
    }

    [Fact]
    public void A_jump_lands_when_its_lead_has_arrived()
    {
        var fill = Fill();
        Next(fill);
        var landed = fill.Jump(200);
        Next(fill);

        fill.Arrived(200 * Second, 2 * Second);
        Assert.False(landed.IsCompleted);

        fill.Arrived(200 * Second, 3 * Second);
        Assert.True(landed.IsCompleted);
    }

    [Fact]
    public void A_jump_just_ahead_of_the_read_lets_it_arrive()
    {
        var fill = Fill();
        using var run = new CancellationTokenSource();
        Next(fill, run);
        fill.Arrived(0, 20 * Second);

        var landed = fill.Jump(21);

        Assert.False(run.IsCancellationRequested);
        fill.Arrived(0, 24 * Second);
        Assert.True(landed.IsCompleted);
    }

    [Fact]
    public void The_read_goes_back_for_the_gap_it_left()
    {
        var fill = Fill();
        Next(fill);
        fill.Arrived(0, 20 * Second);
        fill.Jump(200);

        var (from, count) = Next(fill);
        fill.Arrived(from, count);

        Assert.Equal((20 * Second, 180 * Second), Next(fill));
    }

    [Fact]
    public void A_track_with_every_gap_filled_is_whole()
    {
        var fill = Fill();
        Next(fill);
        fill.Arrived(0, 20 * Second);
        fill.Jump(200);
        var (from, count) = Next(fill);
        fill.Arrived(from, count);
        (from, count) = Next(fill);
        fill.Arrived(from, count);

        Assert.Null(fill.Next(new CancellationTokenSource()));
    }

    /// <summary>The listener jumps back into audio that has arrived, and is then playing towards the gap left
    /// by an earlier jump. That gap must come before the end of the track.</summary>
    [Fact]
    public void A_jump_back_turns_the_read_towards_the_gap_the_listener_will_reach()
    {
        var fill = Fill();
        Next(fill);
        fill.Arrived(0, 70 * Second);
        fill.Jump(200);
        using var run = new CancellationTokenSource();
        Next(fill, run);
        fill.Arrived(200 * Second, 30 * Second);

        fill.Jump(60);

        Assert.True(run.IsCancellationRequested);
        Assert.Equal((70 * Second, 130 * Second), Next(fill));
    }

    [Fact]
    public void A_later_jump_lets_the_earlier_one_go()
    {
        var fill = Fill();
        Next(fill);
        var first = fill.Jump(100);
        var second = fill.Jump(200);

        Assert.True(first.IsCompleted);
        Assert.False(second.IsCompleted);
    }

    [Fact]
    public void A_read_that_stops_lets_a_waiting_jump_go()
    {
        var fill = Fill();
        Next(fill);
        var landed = fill.Jump(200);

        fill.Stop();

        Assert.True(landed.IsCompleted);
    }

    [Fact]
    public void A_jump_after_the_read_stopped_does_not_wait()
    {
        var fill = Fill();
        fill.Stop();

        Assert.True(fill.Jump(200).IsCompleted);
    }

    [Fact]
    public void A_jump_to_the_end_of_the_track_waits_only_for_what_is_there()
    {
        var fill = Fill();
        Next(fill);
        var landed = fill.Jump(299);
        var (from, count) = Next(fill);

        fill.Arrived(from, count);

        Assert.True(landed.IsCompleted);
    }

    [Fact]
    public void Furthest_is_the_end_of_the_last_audio_to_arrive_even_with_a_gap_before_it()
    {
        var fill = Fill();
        fill.Arrived(0, 20 * Second);
        fill.Arrived(200 * Second, 5 * Second);

        Assert.Equal(205 * Second, fill.Furthest);
    }

    [Fact]
    public void A_reader_reports_the_part_of_its_own_read()
    {
        var fill = Fill();
        var progress = fill.ProgressOf(200 * Second, 100 * Second);

        progress.Report(0.5);

        Assert.Equal(250 * Second, fill.Furthest);
    }

    [Fact]
    public void Ranges_that_touch_are_one_range()
    {
        var fill = Fill();
        fill.Arrived(10 * Second, 10 * Second);
        fill.Arrived(0, 10 * Second);
        fill.Arrived(20 * Second, 10 * Second);

        Assert.Equal((30 * Second, Track - 30 * Second), Next(fill));
    }
}
