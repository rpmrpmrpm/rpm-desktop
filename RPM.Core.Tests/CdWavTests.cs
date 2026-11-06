using System.Buffers.Binary;
using System.Diagnostics;
using RPM.Core.Audio;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>A CD track read off the disc into a file the player opens while the audio is still arriving: laid
/// out at its full length with its true sizes and its tags first, and filled in place, little-endian whichever
/// way the reader gave the samples.</summary>
public sealed class CdWavTests : IDisposable
{
    private const int BytesPerSecond = CdWav.SampleRate * CdWav.Channels * CdWav.BitsPerSample / 8;

    private static readonly bool FfmpegAvailable = ProbeFfmpeg();

    private readonly string _root = Path.Combine(Path.GetTempPath(), $"cdwav-{Guid.NewGuid():N}");
    private string Wav => Path.Combine(_root, "01.wav");

    public CdWavTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool ProbeFfmpeg()
    {
        try
        {
            using var process = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            return process is not null;
        }
        catch
        {
            return false;
        }
    }

    [Theory]
    [InlineData(false, new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(true, new byte[] { 0x02, 0x01, 0x04, 0x03 })]
    public async Task The_file_is_a_wav_of_the_audio_little_endian(bool bigEndian, byte[] expected)
    {
        var start = CdWav.Begin(Wav, 4);
        await CdWav.FillAsync(Wav, start, 4, into => into.WriteAsync(new byte[] { 0x01, 0x02, 0x03, 0x04 }).AsTask(), bigEndian);

        var file = await File.ReadAllBytesAsync(Wav);
        Assert.Equal(CdWav.HeaderBytes + 4, file.Length);
        Assert.Equal("RIFF"u8.ToArray(), file[..4]);
        Assert.Equal(36u + 4, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal("data"u8.ToArray(), file[36..40]);
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)));
        Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));
        Assert.Equal(expected, file[44..]);
    }

    [Fact]
    public void A_file_just_laid_out_has_its_full_length_and_true_header_but_is_not_whole()
    {
        CdWav.Begin(Wav, 1000);

        var file = File.ReadAllBytes(Wav);
        Assert.Equal(CdWav.HeaderBytes + 1000, file.Length);
        Assert.Equal(1000u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)));
        Assert.False(CdWav.IsWhole(Wav));
    }

    [Fact]
    public async Task A_filled_file_is_whole()
    {
        var start = CdWav.Begin(Wav, 4);
        await CdWav.FillAsync(Wav, start, 4, into => into.WriteAsync(new byte[4]).AsTask(), bigEndian: false);

        Assert.True(CdWav.IsWhole(Wav));
    }

    [Fact]
    public void A_file_that_is_not_there_is_not_whole() => Assert.False(CdWav.IsWhole(Wav));

    [Fact]
    public async Task A_read_that_fails_cuts_the_file_at_what_arrived_and_it_stays_unfinished()
    {
        var start = CdWav.Begin(Wav, 1000);

        await Assert.ThrowsAsync<DiscReadException>(() => CdWav.FillAsync(Wav, start, 1000, async into =>
        {
            await into.WriteAsync(new byte[] { 1, 2, 3, 4 });
            throw new DiscReadException("a scratch");
        }, bigEndian: false));

        var file = File.ReadAllBytes(Wav);
        Assert.Equal(CdWav.HeaderBytes + 4, file.Length);
        Assert.Equal(36u + 4, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
        Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)));
        Assert.False(CdWav.IsWhole(Wav));
    }

    [Fact]
    public async Task A_cancelled_read_cuts_the_file_at_what_arrived()
    {
        var start = CdWav.Begin(Wav, 1000);
        using var stop = new CancellationTokenSource();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => CdWav.FillAsync(Wav, start, 1000, async into =>
        {
            await into.WriteAsync(new byte[8]);
            await stop.CancelAsync();
            stop.Token.ThrowIfCancellationRequested();
        }, bigEndian: false, stop.Token));

        Assert.Equal(CdWav.HeaderBytes + 8, new FileInfo(Wav).Length);
    }

    /// <summary>A listener jumps ahead of the read: the end of the track goes in first, then the gap.</summary>
    [Fact]
    public async Task Audio_that_arrives_out_of_order_lands_in_its_place()
    {
        var start = CdWav.Begin(Wav, 8);
        await CdWav.FillAsync(Wav, start, 8, async into =>
        {
            into.Position = start + 4;
            await into.WriteAsync(new byte[] { 5, 6, 7, 8 });
            into.Position = start;
            await into.WriteAsync(new byte[] { 1, 2, 3, 4 });
        }, bigEndian: false);

        var file = File.ReadAllBytes(Wav);
        Assert.Equal(CdWav.HeaderBytes + 8, file.Length);
        Assert.Equal(new byte[] { 1, 2, 3, 4, 5, 6, 7, 8 }, file[44..]);
        Assert.True(CdWav.IsWhole(Wav));
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public async Task Tags_stand_in_front_of_the_audio_and_survive_it(bool bigEndian)
    {
        const int audio = BytesPerSecond;
        var start = CdWav.Begin(Wav, audio, Tag);
        Assert.True(start > CdWav.HeaderBytes);
        Assert.Equal(0, start % 2);

        await CdWav.FillAsync(Wav, start, audio, into => into.WriteAsync(Ramp(0, audio / 4)).AsTask(), bigEndian);

        var tags = TagReader.Read(Wav);
        Assert.Equal("The first track", tags.Title);
        Assert.Equal("A disc", tags.Album);
        Assert.Equal(start + audio, new FileInfo(Wav).Length);
        Assert.True(CdWav.IsWhole(Wav));
    }

    [Fact]
    public async Task Tags_survive_a_read_that_stops_short()
    {
        var start = CdWav.Begin(Wav, BytesPerSecond, Tag);

        await Assert.ThrowsAsync<DiscReadException>(() => CdWav.FillAsync(Wav, start, BytesPerSecond, async into =>
        {
            await into.WriteAsync(new byte[400]);
            throw new DiscReadException("a scratch");
        }, bigEndian: false));

        Assert.Equal(start + 400, new FileInfo(Wav).Length);
        Assert.Equal("The first track", TagReader.Read(Wav).Title);
    }

    [Fact]
    public void Laying_out_a_long_tagged_track_takes_no_time_to_speak_of()
    {
        var clock = Stopwatch.StartNew();
        CdWav.Begin(Wav, 70L * 60 * BytesPerSecond, Tag);

        // The bound is loose so that a busy machine does not fail it. Writing the tags behind the audio takes
        // about thirteen seconds here.
        Assert.InRange(clock.ElapsedMilliseconds, 0, 2000);
    }

    /// <summary>The whole idea of the class, with the real decoder. On Windows this also proves the share
    /// modes: ffmpeg opens the file while it is held for writing, and the writing goes on under it.</summary>
    [Fact]
    public async Task The_decoder_plays_a_track_that_is_still_arriving_through_to_its_true_end()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test

        const int seconds = 10;
        const int frames = seconds * CdWav.SampleRate;
        const int audio = seconds * BytesPerSecond;

        var start = CdWav.Begin(Wav, audio, Tag);

        var firstHalfIn = new TaskCompletionSource();
        var carryOn = new TaskCompletionSource();
        var fill = CdWav.FillAsync(Wav, start, audio, async into =>
        {
            await into.WriteAsync(Ramp(0, frames / 2));
            firstHalfIn.SetResult();
            await carryOn.Task;
            await into.WriteAsync(Ramp(frames / 2, frames - frames / 2));
        }, bigEndian: false);

        await firstHalfIn.Task;

        using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options { Source = Wav });

        // The start of the track, with the end of it not on the disk yet.
        var buffer = new float[CdWav.SampleRate * 2];
        Assert.Equal(CdWav.SampleRate, ReadAll(source, buffer, CdWav.SampleRate));
        Assert.Equal(Expected(0), buffer[0], 3);
        Assert.Equal(Expected(CdWav.SampleRate - 1), buffer[(CdWav.SampleRate - 1) * 2], 3);

        carryOn.SetResult();
        await fill;

        // The rest of it comes through, right up to the length the file gives and no further.
        var rest = new float[frames * 2];
        var got = ReadAll(source, rest, frames);
        Assert.Equal(frames - CdWav.SampleRate, got);
        Assert.Equal(Expected(frames - 1), rest[(got - 1) * 2], 3);

        Assert.True(CdWav.IsWhole(Wav));
        Assert.Equal("The first track", TagReader.Read(Wav).Title);
    }

    [Fact]
    public async Task A_jump_into_audio_that_has_arrived_lands_on_the_right_sample()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test

        const int seconds = 10;
        const int audio = seconds * BytesPerSecond;
        const int jumpTo = 3 * CdWav.SampleRate;

        var start = CdWav.Begin(Wav, audio, Tag);

        var someIn = new TaskCompletionSource();
        var carryOn = new TaskCompletionSource();
        var fill = CdWav.FillAsync(Wav, start, audio, async into =>
        {
            await into.WriteAsync(Ramp(0, 6 * CdWav.SampleRate));
            someIn.SetResult();
            await carryOn.Task;
        }, bigEndian: false);

        await someIn.Task;

        // The jump has to find the audio behind the tags. The sample is compared, not only the count.
        using (var source = new FfmpegPcmSource(new FfmpegPcmSource.Options { Source = Wav, StartSeconds = 3 }))
        {
            var buffer = new float[2048];
            Assert.Equal(512, ReadAll(source, buffer, 512));
            Assert.Equal(Expected(jumpTo), buffer[0], 3);
        }

        carryOn.SetResult();
        await fill;
    }

    [Fact]
    public async Task A_decoder_in_the_file_when_the_read_fails_stops_at_the_audio_that_arrived()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test

        const int seconds = 10;
        const int arrived = 4 * CdWav.SampleRate;
        const int audio = seconds * BytesPerSecond;

        var start = CdWav.Begin(Wav, audio);

        var someIn = new TaskCompletionSource();
        var fail = new TaskCompletionSource();
        var fill = CdWav.FillAsync(Wav, start, audio, async into =>
        {
            await into.WriteAsync(Ramp(0, arrived));
            someIn.SetResult();
            await fail.Task;
            throw new DiscReadException("the disc was taken out");
        }, bigEndian: false);

        await someIn.Task;
        using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options { Source = Wav });
        var buffer = new float[CdWav.SampleRate * 2];
        Assert.Equal(CdWav.SampleRate / 2, ReadAll(source, buffer, CdWav.SampleRate / 2));

        fail.SetResult();
        await Assert.ThrowsAsync<DiscReadException>(() => fill);

        // The decoder may already hold the audio just past the cut as silence, but not much of it, and it
        // never runs on to the length the header first promised.
        var rest = new float[seconds * CdWav.SampleRate * 2];
        var got = ReadAll(source, rest, seconds * CdWav.SampleRate);
        Assert.InRange(CdWav.SampleRate / 2 + got, arrived, seconds * CdWav.SampleRate - 1);
    }

    /// <summary>The panes that show a track read its tags from the file as soon as it plays, and that is
    /// while the audio is still being written.</summary>
    [Fact]
    public async Task Tags_can_be_read_while_the_audio_is_arriving()
    {
        var start = CdWav.Begin(Wav, BytesPerSecond, Tag);

        var someIn = new TaskCompletionSource();
        var carryOn = new TaskCompletionSource();
        var fill = CdWav.FillAsync(Wav, start, BytesPerSecond, async into =>
        {
            await into.WriteAsync(new byte[400]);
            someIn.SetResult();
            await carryOn.Task;
        }, bigEndian: false);

        await someIn.Task;
        var tags = TagReader.Read(Wav);

        carryOn.SetResult();
        await fill;

        Assert.Equal("The first track", tags.Title);
        Assert.Equal("A disc", tags.Album);
    }

    [Fact]
    public async Task A_file_can_be_laid_out_again_while_a_decoder_still_has_it_open()
    {
        if (!FfmpegAvailable) return; // no ffmpeg here, so nothing to test

        const int audio = 4 * BytesPerSecond;
        var start = CdWav.Begin(Wav, audio);
        await CdWav.FillAsync(Wav, start, audio, into => into.WriteAsync(Ramp(0, CdWav.SampleRate)).AsTask(), bigEndian: false);

        using var source = new FfmpegPcmSource(new FfmpegPcmSource.Options { Source = Wav });
        var buffer = new float[2048];
        Assert.True(source.Read(buffer, 512) > 0);

        CdWav.Begin(Wav, audio);

        Assert.False(CdWav.IsWhole(Wav));
    }

    private static void Tag(string path) =>
        TagReader.Write(path, new TrackInfo { FilePath = path, Extension = ".wav", Title = "The first track", Album = "A disc" });

    private static int ReadAll(FfmpegPcmSource source, float[] buffer, int frames)
    {
        var chunk = new float[4096 * 2];
        var done = 0;
        while (done < frames)
        {
            var got = source.Read(chunk, Math.Min(4096, frames - done));
            if (got <= 0)
                break;
            Array.Copy(chunk, 0, buffer, done * 2, got * 2);
            done += got;
        }
        return done;
    }

    /// <summary>A sample that says where in the track it came from, never zero, so audio cannot be mistaken
    /// for the silence of a part not written yet.</summary>
    private static short SampleAt(int frame) => (short)(1000 + frame % 20000);

    private static float Expected(int frame) => SampleAt(frame) / 32768f;

    private static byte[] Ramp(int firstFrame, int frames)
    {
        var bytes = new byte[frames * 4];
        for (var i = 0; i < frames; i++)
        {
            var sample = SampleAt(firstFrame + i);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4), sample);
            BinaryPrimitives.WriteInt16LittleEndian(bytes.AsSpan(i * 4 + 2), sample);
        }
        return bytes;
    }
}
