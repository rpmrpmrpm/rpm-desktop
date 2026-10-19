using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// <see cref="Wave.WriteScaled"/>: the opening sound written out at the level it should be heard, for
/// the machine's own player to be pointed at. Nothing in the app reads this file back (afplay,
/// SoundPlayer and paplay do). So what is tested is that it is a wav by the book: read it with the
/// reader that knows nothing about how it was made, and it has to come back the same sound, quieter.
/// </summary>
public class WaveWriteTests
{
    private static Wave.Pcm Ramp(int frames = 400, int channels = 2, int rate = 44100)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++)
            samples[i] = (i % 200) / 100f - 1f;      // -1 to 1 and round again
        return new Wave.Pcm(samples, frames, rate, channels);
    }

    [Fact]
    public void What_is_written_reads_back_as_the_same_sound()
    {
        var sound = Ramp();
        var path = Wave.WriteScaled(sound, 1.0);
        Assert.NotNull(path);

        try
        {
            var read = Wave.Read(path!);
            Assert.NotNull(read);
            Assert.Equal(sound.SampleRate, read!.SampleRate);
            Assert.Equal(sound.Channels, read.Channels);
            Assert.Equal(sound.Frames, read.Frames);

            // Within one step of 16-bit, which is what the trip through it costs.
            for (int i = 0; i < sound.Samples.Length; i++)
                Assert.True(Math.Abs(sound.Samples[i] - read.Samples[i]) < 1e-4,
                            $"sample {i}: {sound.Samples[i]} came back as {read.Samples[i]}");
        }
        finally { File.Delete(path!); }
    }

    [Fact]
    public void The_level_is_in_the_file_rather_than_asked_of_the_player()
    {
        var sound = Ramp();
        var path = Wave.WriteScaled(sound, 0.25);
        Assert.NotNull(path);

        try
        {
            var read = Wave.Read(path!);
            Assert.NotNull(read);

            for (int i = 0; i < sound.Samples.Length; i++)
                Assert.True(Math.Abs(sound.Samples[i] * 0.25f - read!.Samples[i]) < 1e-4,
                            $"sample {i}: {sound.Samples[i]} at a quarter came back as {read!.Samples[i]}");
        }
        finally { File.Delete(path!); }
    }

    [Fact]
    public void A_sound_louder_than_the_format_holds_is_clipped_rather_than_wrapped()
    {
        var loud = new Wave.Pcm([0.9f, -0.9f, 0.9f, -0.9f], 2, 44100, 2);
        var path = Wave.WriteScaled(loud, 4.0);
        Assert.NotNull(path);

        try
        {
            var read = Wave.Read(path!);
            Assert.NotNull(read);
            Assert.All(read!.Samples, s => Assert.True(Math.Abs(s) <= 1f, $"{s} is outside the format"));
            Assert.True(read.Samples[0] > 0.99f, "a loud positive sample should sit at the top, not wrap");
            Assert.True(read.Samples[1] < -0.99f, "a loud negative sample should sit at the bottom");
        }
        finally { File.Delete(path!); }
    }
}
