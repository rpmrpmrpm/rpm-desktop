using RPM.Core.Audio;

namespace RPM.Core.Tests;

public class ShaderAudioTextureTests
{
    private const int Rate = 44100;
    private const int N = 2048;

    private static float[] Sine(double hz, float amp = 0.9f)
    {
        var f = new float[N];
        for (int i = 0; i < N; i++)
            f[i] = amp * (float)Math.Sin(2 * Math.PI * hz * i / Rate);
        return f;
    }

    [Fact]
    public void Layout_is_512_by_2_single_channel()
    {
        var tex = new ShaderAudioTexture();
        Assert.Equal(512, ShaderAudioTexture.Width);
        Assert.Equal(2, ShaderAudioTexture.Height);
        Assert.Equal(512 * 2, tex.Data.Length);
    }

    [Fact]
    public void Silence_rests_the_spectrum_at_the_floor_and_centres_the_wave()
    {
        var tex = new ShaderAudioTexture();
        tex.Update(new float[N]);

        // Spectrum row (0..511) all at the floor.
        for (int x = 0; x < ShaderAudioTexture.Width; x++)
            Assert.Equal(0, tex.Data[x]);

        // Waveform row centred on 128 (silence maps to mid-grey).
        for (int x = 0; x < ShaderAudioTexture.Width; x++)
            Assert.InRange(tex.Data[ShaderAudioTexture.Width + x], 127, 129);
    }

    [Fact]
    public void A_tone_lights_its_own_bin_and_leaves_a_distant_one_dark()
    {
        var tex = new ShaderAudioTexture();
        // Feed the tone twice: the smoothing means one frame is enough to rise, but two is unambiguous.
        var tone = Sine(1000);
        tex.Update(tone);
        tex.Update(tone);

        int bin = (int)Math.Round(1000.0 * N / Rate);   // ~46
        byte atTone = tex.Data[bin];
        byte farAway = tex.Data[500];                    // ~10.7 kHz, nothing there

        Assert.True(atTone > 180, $"tone bin should be bright, was {atTone}");
        Assert.True(farAway < 40, $"silent bin should be dark, was {farAway}");
    }

    [Fact]
    public void Waveform_row_tracks_the_signal_away_from_centre()
    {
        var tex = new ShaderAudioTexture();
        tex.Update(Sine(1000));

        bool anyHigh = false, anyLow = false;
        for (int x = 0; x < ShaderAudioTexture.Width; x++)
        {
            byte v = tex.Data[ShaderAudioTexture.Width + x];
            if (v > 170) anyHigh = true;
            if (v < 85) anyLow = true;
        }
        Assert.True(anyHigh && anyLow, "a loud tone should swing the waveform row both sides of centre");
    }

    /// <summary>
    /// An empty frame is what every visualiser feeds while nothing is playing, so it skips the
    /// transform entirely. The picture it produces still has to be the one the arithmetic would give:
    /// bins easing down towards the floor, and the waveform back at centre.
    /// </summary>
    [Fact]
    public void An_empty_frame_eases_the_spectrum_down_rather_than_freezing_or_dropping_it()
    {
        var tex = new ShaderAudioTexture();

        var tone = Sine(1000);
        tex.Update(tone);
        tex.Update(tone);

        int bin = (int)Math.Round(1000.0 * N / Rate);
        byte lit = tex.Data[bin];
        Assert.True(lit > 180, $"the tone should have lit its bin first, was {lit}");

        tex.Update(ReadOnlySpan<float>.Empty);
        byte once = tex.Data[bin];

        Assert.True(once < lit, $"an empty frame should fall from {lit}, stayed at {once}");
        Assert.True(once > 0, $"and not drop straight to the floor, went to {once}");

        // Half of what is left each time, so a handful of frames is silence.
        for (int i = 0; i < 12; i++) tex.Update(ReadOnlySpan<float>.Empty);
        Assert.Equal(0, tex.Data[bin]);

        // And the wave row is centred throughout, exactly as a frame of zeros would leave it.
        for (int x = 0; x < ShaderAudioTexture.Width; x++)
            Assert.InRange(tex.Data[ShaderAudioTexture.Width + x], 127, 129);
    }

    /// <summary>The skipped transform must not give a different picture from the one it replaced: a
    /// frame of zeros and no frame at all are the same silence, and both rest at the floor.</summary>
    [Fact]
    public void An_empty_frame_and_a_frame_of_zeros_rest_at_the_same_place()
    {
        var empty = new ShaderAudioTexture();
        var zeros = new ShaderAudioTexture();

        for (int i = 0; i < 20; i++)
        {
            empty.Update(ReadOnlySpan<float>.Empty);
            zeros.Update(new float[N]);
        }

        Assert.Equal(zeros.Data, empty.Data);
    }

    [Fact]
    public void Constructor_rejects_a_non_power_of_two_or_too_small_frame()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShaderAudioTexture(1000));
        Assert.Throws<ArgumentOutOfRangeException>(() => new ShaderAudioTexture(512));
    }
}
