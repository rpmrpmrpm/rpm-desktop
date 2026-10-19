using RPM.Core.Viz;

namespace RPM.Core.Tests;

/// <summary>
/// What the room hears. The measurement of the music lives in Core, apart from the fragment program,
/// so the phone and the desktop read the music with the same arithmetic and a test can check it.
/// </summary>
public class RoomBandsTests
{
    private const int Rate = 44100;

    /// <summary>Long enough for the longest window's delay line (three ticks) to have filled and for
    /// every envelope to have settled on what it is being fed.</summary>
    private const int SettleTicks = 40;

    /// <summary>A tap that hands back a pure tone, whatever length of frame is asked for.</summary>
    private static Func<float[], bool> Tone(double hz, float amplitude = 0.5f)
    {
        var at = 0L;
        return frame =>
        {
            for (int i = 0; i < frame.Length; i++)
                frame[i] = amplitude * MathF.Sin((float)(2 * Math.PI * hz * (at + i) / Rate));
            at += frame.Length;
            return true;
        };
    }

    private static RoomBands Settled(Func<float[], bool>? tap, int ticks = SettleTicks, bool playing = true)
    {
        var bands = new RoomBands(Rate) { Tap = tap };
        for (int i = 0; i < ticks; i++)
            bands.Update(i * 0.025, playing);
        return bands;
    }

    private static int Loudest(ReadOnlySpan<float> run)
    {
        int at = 0;
        for (int i = 1; i < run.Length; i++)
            if (run[i] > run[at]) at = i;
        return at;
    }

    [Fact]
    public void The_run_is_one_reading_per_band()
    {
        var bands = new RoomBands(Rate);
        Assert.Equal(RoomBands.Bands, bands.Run.Length);
    }

    [Fact]
    public void Nothing_playing_is_a_room_at_rest()
    {
        var bands = Settled(tap: null, playing: false);

        foreach (var level in bands.Run)
            Assert.Equal(0f, level, 3);

        Assert.Equal(0f, bands.Bass, 3);
        Assert.Equal(0f, bands.Mid, 3);
        Assert.Equal(0f, bands.Air, 3);
    }

    /// <summary>
    /// A bass note stands at the bottom of the run and a cymbal at the top. This is the one thing the
    /// fold has to get right, and the one thing that is invisible when it is wrong: the picture still
    /// moves, but in the wrong place.
    /// </summary>
    [Theory]
    [InlineData(60, 0.00, 0.45)]        // the kick, in the bottom range
    [InlineData(1000, 0.45, 0.80)]      // a voice, in the middle
    [InlineData(9000, 0.80, 1.00)]      // the air
    public void A_tone_stands_where_its_frequency_is_drawn(double hz, double from, double to)
    {
        var run = Settled(Tone(hz)).Run;
        var u = Loudest(run) / (double)(RoomBands.Bands - 1);

        Assert.InRange(u, from, to);
    }

    /// <summary>The bottom of the run is not allowed to be the whole of it. A straight log scale over
    /// these bins would spend half the width below 480 Hz and draw every kick as a solid block across
    /// the left of the screen. The ranges are laid out to prevent that.</summary>
    [Fact]
    public void A_bass_note_does_not_take_the_whole_run()
    {
        var run = Settled(Tone(60)).Run;

        // The top quarter of the run must not respond to a 60 Hz sine.
        for (int i = RoomBands.Bands * 3 / 4; i < RoomBands.Bands; i++)
            Assert.InRange(run[i], 0f, 0.35f);
    }

    [Fact]
    public void Every_reading_stays_inside_its_own_scale()
    {
        var run = Settled(Tone(200, amplitude: 1.0f)).Run;

        foreach (var level in run)
            Assert.InRange(level, 0f, 1f);
    }

    /// <summary>
    /// Silence eases the run down and does not cut it. A spectrum that falls to nothing the instant
    /// the music stops looks like the picture breaking, not like the room going quiet.
    /// </summary>
    [Fact]
    public void Silence_eases_the_run_down()
    {
        var bands = Settled(Tone(200));
        var loud = bands.Run.ToArray();
        Assert.True(loud.Max() > 0.2f, "the tone should be audible before the silence means anything");

        bands.Update(SettleTicks * 0.025, playing: false);
        Assert.True(bands.Run.ToArray().Max() > 0f, "the run should not have been cut to nothing in one tick");

        // The long window hears 186 ms back and also holds its answer through three ticks, so a
        // reading taken from it is still the music for a moment after the music has stopped. By the
        // time that has drained, the run is clearly on its way down.
        for (int i = 1; i < 8; i++)
            bands.Update((SettleTicks + i) * 0.025, playing: false);

        var falling = bands.Run.ToArray();
        Assert.True(falling.Max() < loud.Max(), $"the run should be coming down: {falling.Max()} of {loud.Max()}");
        Assert.True(falling.Max() > 0f, "and still be coming down rather than already gone");

        for (int i = 8; i < 80; i++)
            bands.Update((SettleTicks + i) * 0.025, playing: false);

        Assert.InRange(bands.Run.ToArray().Max(), 0f, 0.05f);
    }

    /// <summary>
    /// The low end is a held peak with a fast attack and a slow release. That makes it the right
    /// figure to light a room by: a kick pushes it up and lets it fall, and it does not flicker at
    /// whatever rate the drummer plays.
    /// </summary>
    [Fact]
    public void The_low_end_arrives_faster_than_it_leaves()
    {
        var bands = new RoomBands(Rate) { Tap = Tone(60) };

        // Up.
        for (int i = 0; i < 8; i++) bands.Update(i * 0.025, playing: true);
        var rising = bands.Bass;
        Assert.True(rising > 0.2f, $"a kick should have lit the room by now, not {rising}");

        // ...and down, over the same number of ticks with nothing playing.
        bands.Tap = null;
        for (int i = 8; i < 16; i++) bands.Update(i * 0.025, playing: false);

        Assert.True(bands.Bass > rising * 0.35f,
            $"the release should be far slower than the attack: {rising} to {bands.Bass}");
    }

    /// <summary>The three envelopes are driven by three different parts of the music and must not
    /// simply be copies of one another. Two families of figures moving with one measurement would move
    /// as one body.</summary>
    [Fact]
    public void The_three_envelopes_answer_to_three_different_things()
    {
        var low = Settled(Tone(60));
        var air = Settled(Tone(11000));

        Assert.True(low.Bass > low.Air, $"a bass note should not be lighting the air: {low.Bass} vs {low.Air}");
        Assert.True(air.Air > air.Bass, $"a cymbal should not be lighting the bass: {air.Air} vs {air.Bass}");
    }

    /// <summary>
    /// A tap that cannot fill the frame (the first fraction of a second of a track, where nothing is
    /// written ahead of the playhead yet) gives silence: not a crash and not a frozen picture.
    /// </summary>
    [Fact]
    public void A_tap_with_nothing_to_give_reads_as_silence()
    {
        var bands = Settled(_ => false);

        foreach (var level in bands.Run)
            Assert.Equal(0f, level, 3);
    }
}
