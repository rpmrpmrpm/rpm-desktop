using RPM.Desktop.Presentation.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// A zero-length move on <see cref="ChromeFade"/> means "be at this value now", and it must land even
/// while a fade is already moving to that value.
///
/// <para>The album page relies on this. A record left during a fade-out keeps that fade running towards
/// zero. When the next record is opened off screen, its layers are set to zero at once before the page comes
/// up. If that call were ignored because the target was already zero, the running fade would still be
/// drawing the last record's colours while the new page faded in.</para>
///
/// <para>The frames come from <see cref="Frames"/>, which draws none unless asked. So a fade with a length
/// stays where it started, and a test can tell a fade in flight from a value that has landed.</para>
/// </summary>
public class ChromeFadeTests
{
    /// <summary>A render tick that nothing drives. It only records whether a fade is listening.</summary>
    private sealed class Frames
    {
        private EventHandler<object>? _tick;

        public (Action<EventHandler<object>> Add, Action<EventHandler<object>> Remove) Source =>
            (tick => _tick += tick, tick => _tick -= tick);

        public bool Listened => _tick is not null;
    }

    [Fact]
    public void A_zero_length_move_lands_on_the_target_a_fade_is_already_moving_to()
    {
        var frames = new Frames();
        var value = 1.0;
        var fade = new ChromeFade(v => value = v, from: 1, frames.Source);

        fade.To(0, 120);
        Assert.True(frames.Listened);
        Assert.Equal(1, value);   // in flight: no frame has been drawn yet

        fade.To(0, 0);
        Assert.Equal(0, value);
        Assert.False(frames.Listened);   // landed, so it no longer asks for frames
    }

    [Fact]
    public void A_zero_length_move_to_where_it_already_is_writes_nothing()
    {
        var frames = new Frames();
        var writes = 0;
        var fade = new ChromeFade(_ => writes++, from: 0, frames.Source);

        fade.To(0, 0);

        Assert.Equal(0, writes);
        Assert.False(frames.Listened);
    }

    /// <summary>A fade asked again for the target it is moving to carries on. It does not restart, which is
    /// what keeps a pointer moving over chrome from restarting the chrome's fade on every move.</summary>
    [Fact]
    public void A_timed_move_to_the_target_in_flight_changes_nothing()
    {
        var frames = new Frames();
        var value = 0.0;
        var fade = new ChromeFade(v => value = v, from: 0, frames.Source);

        fade.To(1, 200);
        fade.To(1, 200);

        Assert.Equal(1, fade.Target);
        Assert.Equal(0, value);
        Assert.True(frames.Listened);

        fade.Stop();
        Assert.False(frames.Listened);
    }
}
