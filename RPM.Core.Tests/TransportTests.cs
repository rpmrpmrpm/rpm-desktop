using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Play and pause pressed while a track is still opening: the stretch where the engine holds
/// nothing and every honest answer to "is it playing?" is no. Getting this wrong does not throw:
/// the press is dropped, and the track everybody expected silence from starts playing a second
/// later.
/// </summary>
public class TransportTests
{
    // ── the window while a track is opening ──

    [Fact]
    public void Pause_pressed_while_a_track_is_opening_is_remembered_not_dropped()
    {
        var transport = new Transport();
        transport.Starting();

        Assert.Equal(TransportMove.Remember, transport.Pause(starting: true, Loaded.Nothing));
        Assert.True(transport.PausePending);
    }

    [Fact]
    public void A_remembered_pause_is_applied_when_the_source_arrives()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        Assert.True(transport.Arrives());
    }

    [Fact]
    public void A_remembered_pause_is_applied_once_and_not_to_the_track_after_it()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        Assert.True(transport.Arrives());
        Assert.False(transport.Arrives());
    }

    [Fact]
    public void Play_pressed_while_a_track_is_opening_withdraws_a_remembered_pause()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        Assert.Equal(TransportMove.Forget, transport.Play(starting: true, Loaded.Nothing));
        Assert.False(transport.Arrives());
    }

    /// <summary>The lock screen sends play and pause as separate commands. If the transport ignores
    /// the load in progress, pressing play during a load comes out as pause.</summary>
    [Fact]
    public void Play_pressed_while_a_track_is_opening_never_pauses_it()
    {
        var transport = new Transport();
        transport.Starting();

        Assert.Equal(TransportMove.Nothing, transport.Play(starting: true, Loaded.Nothing));
        Assert.False(transport.PausePending);
        Assert.False(transport.Arrives());
    }

    [Fact]
    public void Pause_pressed_twice_while_opening_stays_paused()
    {
        var transport = new Transport();
        transport.Starting();

        Assert.Equal(TransportMove.Remember, transport.Pause(starting: true, Loaded.Nothing));
        Assert.Equal(TransportMove.Nothing, transport.Pause(starting: true, Loaded.Nothing));
        Assert.True(transport.Arrives());
    }

    [Fact]
    public void Choosing_another_track_forgets_a_pause_pressed_against_the_last_one()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        transport.Starting();   // next was pressed: somebody asking to hear this one

        Assert.False(transport.PausePending);
        Assert.False(transport.Arrives());
    }

    [Fact]
    public void Stopping_forgets_a_remembered_pause()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        transport.Forgotten();

        Assert.False(transport.Arrives());
    }

    [Fact]
    public void A_pause_with_nothing_playing_and_nothing_coming_is_not_held_against_the_next_track()
    {
        var transport = new Transport();

        Assert.Equal(TransportMove.Nothing, transport.Pause(starting: false, Loaded.Nothing));
        Assert.False(transport.PausePending);
    }

    // ── the ordinary cases, which have to keep working ──

    [Fact]
    public void Pause_on_a_playing_track_pauses_it()
    {
        Assert.Equal(TransportMove.Pause, new Transport().Pause(starting: false, Loaded.Playing));
    }

    [Fact]
    public void Play_on_a_paused_track_resumes_it()
    {
        Assert.Equal(TransportMove.Resume, new Transport().Play(starting: false, Loaded.Paused));
    }

    [Fact]
    public void Play_on_a_playing_track_does_nothing()
    {
        Assert.Equal(TransportMove.Nothing, new Transport().Play(starting: false, Loaded.Playing));
    }

    [Fact]
    public void Pause_on_a_paused_track_does_nothing()
    {
        Assert.Equal(TransportMove.Nothing, new Transport().Pause(starting: false, Loaded.Paused));
    }

    [Fact]
    public void The_engine_wins_over_a_stale_start_flag()
    {
        var transport = new Transport();
        transport.Starting();

        // The source has been handed over but the load has not finished tidying up after itself. What is
        // coming out of the speaker is the answer.
        Assert.Equal(TransportMove.Pause, transport.Pause(starting: true, Loaded.Playing));
        Assert.True(transport.WantsToPlay(starting: true, Loaded.Playing));
    }

    // ── what the key draws ──

    [Fact]
    public void An_opening_track_shows_a_pause_key_because_that_is_what_was_asked_for()
    {
        var transport = new Transport();
        transport.Starting();

        Assert.True(transport.WantsToPlay(starting: true, Loaded.Nothing));
    }

    [Fact]
    public void An_opening_track_shows_a_play_key_once_pause_has_been_pressed()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Pause(starting: true, Loaded.Nothing);

        Assert.False(transport.WantsToPlay(starting: true, Loaded.Nothing));
    }

    [Fact]
    public void An_idle_player_shows_a_play_key()
    {
        Assert.False(new Transport().WantsToPlay(starting: false, Loaded.Nothing));
    }

    // ── the toggle ──

    [Fact]
    public void The_toggle_pauses_a_track_that_is_playing()
    {
        Assert.Equal(TransportMove.Pause, new Transport().Toggle(starting: false, Loaded.Playing));
    }

    [Fact]
    public void The_toggle_resumes_a_track_that_is_paused()
    {
        Assert.Equal(TransportMove.Resume, new Transport().Toggle(starting: false, Loaded.Paused));
    }

    [Fact]
    public void The_toggle_remembers_a_pause_while_a_track_is_opening()
    {
        var transport = new Transport();
        transport.Starting();

        Assert.Equal(TransportMove.Remember, transport.Toggle(starting: true, Loaded.Nothing));
    }

    [Fact]
    public void The_toggle_takes_a_remembered_pause_back()
    {
        var transport = new Transport();
        transport.Starting();
        transport.Toggle(starting: true, Loaded.Nothing);

        Assert.Equal(TransportMove.Forget, transport.Toggle(starting: true, Loaded.Nothing));
        Assert.False(transport.Arrives());
    }

    [Fact]
    public void The_toggle_does_nothing_when_there_is_nothing_loaded_and_nothing_coming()
    {
        Assert.Equal(TransportMove.Nothing, new Transport().Toggle(starting: false, Loaded.Nothing));
    }
}
