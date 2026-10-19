using System.Diagnostics;
using RPM.Core.Cloud;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// The renditions and the head clips. The rate arithmetic is pure and always runs. The encoding
/// tests make real files with ffmpeg and are skipped without it, like <c>CanonicalAudioTests</c>:
/// a fixture could not prove the one property that matters, which is that the index is at the
/// front of the file.
/// </summary>
public class TranscoderTests
{
    // ── what gets served, which has nothing to do with ffmpeg ──

    [Fact]
    public void Serves_a_free_account_one_rate_whatever_it_asks_for()
    {
        Assert.Equal(128, AudioLadder.Serve(320, mayHaveOriginal: false));
        Assert.Equal(128, AudioLadder.Serve(256, mayHaveOriginal: false));
        Assert.Equal(128, AudioLadder.Serve(128, mayHaveOriginal: false));

        // Including the original, which is the thing a plan actually sells.
        Assert.Equal(128, AudioLadder.Serve(AudioLadder.Original, mayHaveOriginal: false));
    }

    [Fact]
    public void Lets_a_free_account_ask_for_less()
    {
        // Below the cap is somebody on a bad connection choosing to spend less, which is never
        // something to override.
        Assert.Equal(96, AudioLadder.Serve(96, mayHaveOriginal: false));
    }

    [Fact]
    public void Gives_a_supporter_what_they_asked_for()
    {
        foreach (var rate in AudioLadder.Rates)
            Assert.Equal(rate, AudioLadder.Serve(rate, mayHaveOriginal: true));

        Assert.Equal(AudioLadder.Original, AudioLadder.Serve(AudioLadder.Original, mayHaveOriginal: true));
    }

    [Fact]
    public void Rounds_down_to_the_ladder_never_up()
    {
        // Rounding up would hand somebody a rendition they did not ask for and spend data they did not
        // agree to, and for a free account it would be one they are not entitled to.
        Assert.Equal(192, AudioLadder.Serve(200, mayHaveOriginal: true));
        Assert.Equal(320, AudioLadder.Serve(9999, mayHaveOriginal: true));
        Assert.Equal(96, AudioLadder.Serve(1, mayHaveOriginal: true));
    }

    [Fact]
    public void Knows_which_rates_it_makes()
    {
        Assert.True(AudioLadder.IsOnTheLadder(128));
        Assert.False(AudioLadder.IsOnTheLadder(160));
        Assert.False(AudioLadder.IsOnTheLadder(0));

        Assert.Throws<ArgumentOutOfRangeException>(
            () => Transcoding.Rendition("in.flac", "out.m4a", 160));
    }

    // ── reading an MP4's box list, which is how faststart is proven ──

    [Fact]
    public void Reads_the_index_as_being_at_the_front()
    {
        Assert.True(Transcoding.StartsFast(Mp4([("ftyp", 16), ("moov", 200), ("mdat", 900)])));
        Assert.False(Transcoding.StartsFast(Mp4([("ftyp", 16), ("mdat", 900), ("moov", 200)])));
    }

    [Fact]
    public void Does_not_walk_off_the_end_of_a_broken_file()
    {
        Assert.False(Transcoding.StartsFast([]));
        Assert.False(Transcoding.StartsFast([0, 0, 0]));

        // A box claiming a size smaller than its own header, which would loop for ever if believed.
        Assert.False(Transcoding.StartsFast([0, 0, 0, 2, (byte)'f', (byte)'t', (byte)'y', (byte)'p']));

        // A box claiming to run to the end of the file, which only the last one may do.
        Assert.False(Transcoding.StartsFast([0, 0, 0, 0, (byte)'f', (byte)'t', (byte)'y', (byte)'p']));
    }

    /// <summary>A file of top-level boxes with the given names and sizes, and nothing inside them.</summary>
    private static byte[] Mp4((string Name, int Size)[] boxes)
    {
        var file = new List<byte>();

        foreach (var (name, size) in boxes)
        {
            file.AddRange([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size]);
            file.AddRange(System.Text.Encoding.ASCII.GetBytes(name));
            file.AddRange(new byte[size - 8]);
        }

        return [.. file];
    }

    // ── the encoding itself ──

    [Fact]
    public void Makes_a_head_clip_that_can_be_decoded_from_its_first_bytes()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 12);
        if (source is null) return;

        try
        {
            var clip = Transcoding.Head(source);

            Assert.NotEmpty(clip);

            // The property the whole feature rests on: the index is at the front, so a decoder can start on
            // the first bytes rather than after the last.
            Assert.True(Transcoding.StartsFast(clip), "the clip's moov atom is not at the front");

            // And it is small. Three seconds at 96 k is about 36 KB; the assertion is loose enough
            // for container overhead and tight enough to catch a whole track being encoded.
            Assert.InRange(clip.Length, 10_000, 120_000);
        }
        finally
        {
            File.Delete(source);
        }
    }

    [Fact]
    public void Makes_a_rendition_of_the_whole_track()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 12);
        if (source is null) return;

        var to = Path.Combine(Path.GetTempPath(), $"gr-test-{Guid.NewGuid():N}.m4a");

        try
        {
            Transcoding.Rendition(source, to, 128);

            var made = File.ReadAllBytes(to);

            Assert.True(Transcoding.StartsFast(made));

            // Twelve seconds at 128 k is about 190 KB, several times the head clip. This is the
            // point: this one is the whole track.
            Assert.InRange(made.Length, 100_000, 400_000);
        }
        finally
        {
            File.Delete(source);
            if (File.Exists(to)) File.Delete(to);
        }
    }

    [Fact]
    public void Fails_loudly_on_something_that_is_not_audio()
    {
        if (!FfmpegAvailable) return;

        var nonsense = Path.Combine(Path.GetTempPath(), $"gr-test-{Guid.NewGuid():N}.flac");
        File.WriteAllText(nonsense, "this is not a song");

        try
        {
            // Silently producing nothing would leave a library with tracks that will not play and no record
            // of why.
            Assert.ThrowsAny<InvalidOperationException>(() => Transcoding.Head(nonsense));
        }
        finally
        {
            File.Delete(nonsense);
        }
    }

    // ── the same encode, served as it is made ──

    /// <summary>
    /// The property the live path rests on, and the reason it is not the faststart MP4 that
    /// everything else is: ADTS carries its format in every frame, so a decoder can start on the first
    /// bytes of the pipe. An MP4 with <c>+faststart</c> cannot be written to a pipe at all (ffmpeg
    /// seeks back to put the index at the front), and one without it is undecodable until its last
    /// byte.
    /// </summary>
    [Fact]
    public async Task Serves_a_rendition_that_can_be_decoded_from_its_first_bytes()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 30);
        if (source is null) return;

        var served = new Recording();
        string? filed = null;

        try
        {
            filed = await Transcoding.LiveAsync(source, served, 128, startingAt: 0, CancellationToken.None);

            var sent = served.ToArray();

            // An ADTS frame opens with twelve set bits. On the first byte of the stream, which is
            // what "decodable from the first bytes" means here.
            Assert.True(sent.Length > 2, "nothing was served");
            Assert.Equal(0xFF, sent[0]);
            Assert.Equal(0xF0, sent[1] & 0xF0);
        }
        finally
        {
            File.Delete(source);
            if (filed is not null && File.Exists(filed)) File.Delete(filed);
        }
    }

    /// <summary>
    /// The whole feature: the listener hears the beginning while the end is still being encoded. A
    /// pass that handed over one buffer at the end would satisfy every other assertion here and would
    /// be no better than a plain encode.
    /// </summary>
    [Fact]
    public async Task Serves_the_beginning_before_the_end_has_been_encoded()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 30);
        if (source is null) return;

        var served = new Recording();
        string? filed = null;

        try
        {
            filed = await Transcoding.LiveAsync(source, served, 128, startingAt: 0, CancellationToken.None);

            Assert.True(served.Writes > 1,
                $"the whole rendition arrived in {served.Writes} write(s), which is not streaming");
        }
        finally
        {
            File.Delete(source);
            if (filed is not null && File.Exists(filed)) File.Delete(filed);
        }
    }

    /// <summary>What is kept is what every later play is served, so it has to be the same file the
    /// desktop and the backfill make: a faststart MP4, not the ADTS that went down the wire.</summary>
    [Fact]
    public async Task Keeps_a_faststart_copy_of_what_it_served()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 30);
        if (source is null) return;

        var served = new Recording();
        string? filed = null;

        try
        {
            filed = await Transcoding.LiveAsync(source, served, 128, startingAt: 0, CancellationToken.None);

            Assert.True(File.Exists(filed), "nothing was kept");
            Assert.True(Transcoding.StartsFast(File.ReadAllBytes(filed)),
                "the kept copy's moov atom is not at the front");

            // The same audio, so the same order of size. The container differs, not the encode.
            Assert.InRange(new FileInfo(filed).Length, served.Length / 2, served.Length * 2);
        }
        finally
        {
            File.Delete(source);
            if (filed is not null && File.Exists(filed)) File.Delete(filed);
        }
    }

    /// <summary>
    /// A rendition is only ever filed whole.
    ///
    /// <para>The store's rule is that a rate which is present is complete: the API reads the rate set
    /// and signs a URL for it without looking. A partial file kept here would be a rendition that
    /// exists as far as everything else is concerned, and stops after ninety seconds, for ever, for
    /// everybody who dedups onto that blob.</para>
    /// </summary>
    [Fact]
    public async Task Keeps_nothing_when_the_encode_fails()
    {
        if (!FfmpegAvailable) return;

        var nonsense = Path.Combine(Path.GetTempPath(), $"gr-test-{Guid.NewGuid():N}.flac");
        File.WriteAllText(nonsense, "this is not a song");

        var before = TempM4as();

        try
        {
            await Assert.ThrowsAnyAsync<InvalidOperationException>(
                () => Transcoding.LiveAsync(nonsense, new Recording(), 128, startingAt: 0, CancellationToken.None));

            Assert.Equal(before, TempM4as());
        }
        finally
        {
            File.Delete(nonsense);
        }
    }

    [Fact]
    public async Task Will_not_serve_a_rate_that_is_not_on_the_ladder()
    {
        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(
            () => Transcoding.LiveAsync("in.flac", new Recording(), 160, startingAt: 0, CancellationToken.None));
    }

    /// <summary>
    /// A seek during the first play. The encoder can start anywhere, which is why a seek on a live
    /// stream is a second and not a byte range: a byte offset could only be turned back into a second
    /// by assuming a constant rate, which AAC does not promise.
    /// </summary>
    [Fact]
    public async Task Serves_the_record_from_the_second_it_was_asked_for()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 30);
        if (source is null) return;

        var whole = new Recording();
        var tail = new Recording();
        string? filed = null;

        try
        {
            filed = await Transcoding.LiveAsync(source, whole, 128, startingAt: 0, CancellationToken.None);
            await Transcoding.LiveAsync(source, tail, 128, startingAt: 20, CancellationToken.None);

            // The last third of a thirty second tone, so about a third of the bytes. Loose, because an
            // encoder's output is not exactly linear in its input, but tight enough to tell a seek from a
            // whole track served again.
            Assert.InRange(tail.Length, whole.Length / 6, whole.Length / 2);
        }
        finally
        {
            File.Delete(source);
            if (filed is not null && File.Exists(filed)) File.Delete(filed);
        }
    }

    /// <summary>
    /// A seek makes no rendition.
    ///
    /// <para>The store's rule is that a rate which is present is complete. What comes out of a seek
    /// is the end of a record with its beginning missing. Filed under the rate it was encoded at, it
    /// would be a rendition that starts in the middle, for ever, for everybody who dedups onto that
    /// blob.</para>
    /// </summary>
    [Fact]
    public async Task Keeps_nothing_of_a_record_it_joined_partway_through()
    {
        if (!FfmpegAvailable) return;

        var source = Tone(seconds: 30);
        if (source is null) return;

        var before = TempM4as();

        try
        {
            var filed = await Transcoding.LiveAsync(
                source, new Recording(), 128, startingAt: 20, CancellationToken.None);

            Assert.Null(filed);
            Assert.Equal(before, TempM4as());
        }
        finally
        {
            File.Delete(source);
        }
    }

    /// <summary>What the temp directory holds of ours, so a failed encode can be shown to have left
    /// nothing behind, not merely to have thrown.</summary>
    private static HashSet<string> TempM4as() =>
        [.. Directory.EnumerateFiles(Path.GetTempPath(), "gr-live-*")];

    /// <summary>A stream that remembers what it was given and how many times. The count is the
    /// assertion: one write is a buffer handed over at the end, several is a stream.</summary>
    private sealed class Recording : Stream
    {
        private readonly MemoryStream _held = new();

        public int Writes { get; private set; }

        public byte[] ToArray() => _held.ToArray();

        public override bool CanRead => false;
        public override bool CanSeek => false;
        public override bool CanWrite => true;
        public override long Length => _held.Length;

        public override long Position
        {
            get => _held.Position;
            set => throw new NotSupportedException();
        }

        public override void Write(byte[] buffer, int offset, int count)
        {
            Writes++;
            _held.Write(buffer, offset, count);
        }

        public override void Write(ReadOnlySpan<byte> buffer)
        {
            Writes++;
            _held.Write(buffer);
        }

        public override ValueTask WriteAsync(
            ReadOnlyMemory<byte> buffer, CancellationToken cancellationToken = default)
        {
            Writes++;
            return _held.WriteAsync(buffer, cancellationToken);
        }

        public override void Flush() => _held.Flush();
        public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();
        public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
        public override void SetLength(long value) => throw new NotSupportedException();
    }

    private static bool FfmpegAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("ffmpeg")
                {
                    ArgumentList = { "-version" },
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                });

                p!.WaitForExit();
                return p.ExitCode == 0;
            }
            catch
            {
                return false;
            }
        }
    }

    /// <summary>A real FLAC of a sine tone, or null when this ffmpeg cannot make one.</summary>
    private static string? Tone(double seconds)
    {
        var path = Path.Combine(Path.GetTempPath(), $"gr-test-{Guid.NewGuid():N}.flac");

        var psi = new ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error", "-y",
                "-f", "lavfi",
                "-i", $"sine=frequency=440:sample_rate=44100:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}",
                "-ac", "2",
                path,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };

        using var p = Process.Start(psi);
        p!.StandardError.ReadToEnd();
        p.WaitForExit();

        return p.ExitCode == 0 && File.Exists(path) ? path : null;
    }
}
