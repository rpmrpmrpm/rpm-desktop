using System.Diagnostics;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The file TagLib parses but cannot measure: Dolby Digital Plus inside an .m4a, which is the shape
/// a surround or Atmos download arrives in. Its tags read perfectly and it has no length, and a
/// track with no length cannot be laid out, queued or scrubbed. ffmpeg is the second opinion.
///
/// <para>Hand-built bytes could not prove any of this, so these encode a real file and are skipped
/// where ffmpeg is not on the machine.</para>
/// </summary>
public class MediaProbeTests : IDisposable
{
    private readonly string _root;

    public MediaProbeTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"probe-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>Two seconds of tone as Dolby Digital Plus in an .m4a, or null when this ffmpeg build
    /// cannot make one. <c>-f mp4</c> because the muxer .m4a would otherwise pick refuses the codec,
    /// the same refusal <see cref="Cloud.CanonicalAudio"/> has to work around.</summary>
    private string? EncodeDolby(int channels = 6)
    {
        var path = Path.Combine(_root, $"dolby-{channels}.m4a");
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardError = true,
            RedirectStandardOutput = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-y");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("lavfi");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add("sine=frequency=440:duration=2");
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("eac3");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add(channels.ToString());
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("48000");
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");
        psi.ArgumentList.Add(path);

        try
        {
            using var proc = Process.Start(psi)!;
            proc.WaitForExit();
            return proc.ExitCode == 0 && File.Exists(path) ? path : null;
        }
        catch { return null; }   // no ffmpeg here
    }

    [Fact]
    public void A_codec_taglib_does_not_know_is_still_measured()
    {
        var path = EncodeDolby();
        if (path is null) return;

        var facts = MediaProbe.Read(path);

        Assert.NotNull(facts);
        Assert.InRange(facts!.DurationSeconds, 1.8, 2.2);
        Assert.Equal("eac3", facts.Codec);
        Assert.Equal(48000, facts.SampleRate);
        Assert.Equal(6, facts.Channels);        // ffmpeg prints "5.1(side)", not "6"
        Assert.True(facts.BitrateKbps > 0);
    }

    [Fact]
    public void A_mono_stream_is_counted_as_one_channel()
    {
        var path = EncodeDolby(channels: 1);
        if (path is null) return;

        Assert.Equal(1, MediaProbe.Read(path)?.Channels);
    }

    [Fact]
    public void A_file_that_is_not_audio_probes_to_nothing()
    {
        var path = Path.Combine(_root, "sleeve.txt");
        File.WriteAllText(path, "not a record");

        Assert.Null(MediaProbe.Read(path));
    }

    [Fact]
    public void A_file_that_is_not_there_probes_to_nothing()
        => Assert.Null(MediaProbe.Read(Path.Combine(_root, "gone.m4a")));

    /// <summary>
    /// The bug as the user met it: tracks in the library with no length at all, playing perfectly.
    /// TagLib gives up on the file (here because the extension does not match what is inside, which
    /// reliably causes this), and ffmpeg, which plays the file, reads it without complaint.
    /// </summary>
    [Fact]
    public void A_file_taglib_refuses_still_gets_its_length()
    {
        var mp4 = EncodeDolby(channels: 2);
        if (mp4 is null) return;

        var path = Path.Combine(_root, "mislabelled.flac");
        File.Copy(mp4, path);
        Assert.Throws<TagLib.CorruptFileException>(() => TagLib.File.Create(path));

        var info = TagReader.Read(path);

        Assert.InRange(info.DurationSeconds, 1.8, 2.2);
        Assert.Equal(2, info.Channels);
        Assert.Equal(48000, info.SampleRate);
    }

    /// <summary>A file TagLib does read is not probed again. ffmpeg is asked second so
    /// that a scan of a whole library does not start a process for every file.</summary>
    [Fact]
    public void A_file_taglib_reads_is_left_alone()
    {
        var path = EncodeDolby(channels: 2);
        if (path is null) return;

        var was = MediaProbe.FfmpegPath;
        MediaProbe.FfmpegPath = Path.Combine(_root, "no-such-ffmpeg");
        try
        {
            var info = TagReader.Read(path);

            Assert.InRange(info.DurationSeconds, 1.8, 2.2);
            Assert.Equal("Dolby Digital Plus", AudioFormat.Codec(info.Extension, info.CodecDescription));
        }
        finally { MediaProbe.FfmpegPath = was; }
    }

    /// <summary>
    /// A file nothing can read is CORRUPT, and says so. This is the state the queue blocks from
    /// import. It is not the same as an untagged file, which is a good file waiting to be told what it is.
    /// </summary>
    [Fact]
    public void A_file_with_no_audio_in_it_comes_back_unreadable()
    {
        if (!File.Exists(MediaProbe.FfmpegPath)) return;   // the verdict needs a second reader

        var path = Path.Combine(_root, "truncated.mp3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 });

        Assert.Equal(FileAvailability.Unreadable, TagReader.Read(path).Availability);
    }

    /// <summary>
    /// And with no ffmpeg on the machine, the same file is NOT condemned. A missing tool is not
    /// evidence against a track: the only thing known then is that nobody looked.
    /// </summary>
    [Fact]
    public void Without_ffmpeg_nothing_is_called_corrupt()
    {
        var path = Path.Combine(_root, "truncated.mp3");
        File.WriteAllBytes(path, new byte[] { 0x00, 0x11, 0x22, 0x33, 0x44 });

        var was = MediaProbe.FfmpegPath;
        MediaProbe.FfmpegPath = Path.Combine(_root, "no-such-ffmpeg");
        try
        {
            Assert.Equal(FileAvailability.Local, TagReader.Read(path).Availability);
        }
        finally { MediaProbe.FfmpegPath = was; }
    }
}
