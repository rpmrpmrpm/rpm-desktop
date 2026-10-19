using RPM.Desktop.Services.Audio;

namespace RPM.Desktop.Tests;

/// <summary>
/// Reading the app's own sounds.
///
/// <para>Small enough to be obvious and worth pinning anyway, because the thing it reads is written
/// by whatever editor the sound was made in: a wav is a walk over chunks, and an editor that puts its
/// own notes between the format and the samples is the ordinary case, not the odd one. A reader that
/// assumed the samples start at byte 44 would play a chunk of somebody's metadata as audio.</para>
/// </summary>
public class WaveTests : IDisposable
{
    private readonly string _dir =
        Path.Combine(Path.GetTempPath(), $"wave-{Guid.NewGuid():N}");

    public WaveTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void The_samples_are_found_past_whatever_the_editor_left_in_the_middle()
    {
        // An odd-sized chunk between the format and the data. Chunks are word-aligned, so a reader that
        // walks by the declared size alone lands one byte short of the samples from then on.
        var path = Write(
            channels: 2, sampleRate: 44100,
            samples: [0, short.MaxValue, short.MinValue, 0],
            extra: ("LIST", [1, 2, 3]));

        var pcm = Wave.Read(path);

        Assert.NotNull(pcm);
        Assert.Equal(44100, pcm!.SampleRate);
        Assert.Equal(2, pcm.Channels);
        Assert.Equal(2, pcm.Frames);
        Assert.Equal(0f, pcm.Samples[0]);
        Assert.Equal(1f, pcm.Samples[1], 3);
        Assert.Equal(-1f, pcm.Samples[2], 3);
    }

    [Fact]
    public void Seconds_is_the_length_of_the_sound()
    {
        var path = Write(channels: 1, sampleRate: 8000, samples: new short[4000]);

        Assert.Equal(0.5, Wave.Read(path)!.Seconds, 3);
    }

    [Fact]
    public void Anything_that_is_not_a_wav_is_no_sound_rather_than_a_failure()
    {
        var path = Path.Combine(_dir, "not-a-wav.wav");
        File.WriteAllBytes(path, [0, 1, 2, 3, 4, 5, 6, 7, 8, 9, 10, 11]);

        Assert.Null(Wave.Read(path));
    }

    /// <summary>A RIFF/WAVE file, as an editor writes one: the format, then anything else it felt
    /// like keeping, then the samples.</summary>
    private string Write(
        int channels, int sampleRate, short[] samples, (string Id, byte[] Bytes)? extra = null)
    {
        var path = Path.Combine(_dir, $"sound-{samples.Length}-{channels}.wav");

        using var file = File.Create(path);
        using var writer = new BinaryWriter(file);

        var data = new byte[samples.Length * 2];
        Buffer.BlockCopy(samples, 0, data, 0, data.Length);

        writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF"));
        writer.Write(0);                       // the size nobody reads back
        writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));

        writer.Write(System.Text.Encoding.ASCII.GetBytes("fmt "));
        writer.Write(16);
        writer.Write((ushort)1);               // PCM
        writer.Write((ushort)channels);
        writer.Write(sampleRate);
        writer.Write(sampleRate * channels * 2);
        writer.Write((ushort)(channels * 2));
        writer.Write((ushort)16);              // bits

        if (extra is { } chunk)
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes(chunk.Id));
            writer.Write(chunk.Bytes.Length);
            writer.Write(chunk.Bytes);
            if (chunk.Bytes.Length % 2 == 1)
                writer.Write((byte)0);         // the pad byte a word-aligned chunk carries
        }

        writer.Write(System.Text.Encoding.ASCII.GetBytes("data"));
        writer.Write(data.Length);
        writer.Write(data);

        return path;
    }
}
