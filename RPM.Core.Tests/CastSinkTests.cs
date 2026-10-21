using System.Diagnostics;
using System.Net;
using RPM.Core.Audio;
using RPM.Core.Cloud;

namespace RPM.Core.Tests;

/// <summary>
/// The whole cast path, end to end, without a speaker in the room: a fake receiver fetches the URL
/// the sink publishes (which is exactly what a Chromecast does), and we decode what comes back.
///
/// <para>This is the test that matters for this feature. Everything else about casting can be
/// reasoned about, but "does the thing we serve actually decode as audio of the right length"
/// cannot, and it is the failure that a real device reports as silence and nothing else.</para>
/// </summary>
public class CastSinkTests
{
    private const int Rate = 44100;
    private const int Channels = 2;

    private static bool HasFfmpeg => FindFfmpeg();

    private static bool FindFfmpeg()
    {
        try
        {
            using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
            {
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            });
            p!.WaitForExit(5000);
            return p.ExitCode == 0;
        }
        catch { return false; }
    }

    private static SinkTrack Track(double seconds) =>
        // The path need not exist: the sink only reads it for embedded art, and finding none is a
        // normal outcome (plenty of files have no cover).
        new("/nonexistent/track.flac", "Test Track", "Test Artist", "Test Album", seconds, 0);

    /// <summary>One 50 ms block of a 440 Hz tone, as the engine's pump would hand it over.</summary>
    private static float[] ToneBlock(int frames, int startFrame)
    {
        var block = new float[frames * Channels];
        for (int i = 0; i < frames; i++)
        {
            var v = (float)(0.5 * Math.Sin(2 * Math.PI * 440 * (startFrame + i) / Rate));
            block[i * Channels] = v;
            block[i * Channels + 1] = v;
        }
        return block;
    }

    /// <summary>Feeds the sink like <c>AudioEngine.Pump</c> does (blocking Enqueue, one 50 ms block
    /// at a time), then drains it.</summary>
    private static void PlayTone(IAudioSink sink, double seconds)
    {
        const int FramesPerBlock = 2205;   // 50 ms, the engine's block size
        int blocks = (int)(seconds * Rate / FramesPerBlock);

        for (int b = 0; b < blocks; b++)
        {
            var block = ToneBlock(FramesPerBlock, b * FramesPerBlock);
            if (!sink.Enqueue(block, FramesPerBlock)) return;
        }
        sink.Drain();
    }

    /// <summary>Writes the bytes to a file and asks ffprobe what they are: codec name and duration,
    /// or null if it is not decodable audio at all. That is the only question that really matters about
    /// what we put on the wire, and the one a real speaker answers only as silence.</summary>
    private static (string Codec, double? Seconds)? Probe(byte[] audio)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cast-test-{Guid.NewGuid():n}");
        try
        {
            File.WriteAllBytes(file, audio);

            using var probe = Process.Start(new ProcessStartInfo("ffprobe")
            {
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=codec_name",
                    "-show_entries", "format=duration",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    file,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            var lines = probe.StandardOutput.ReadToEnd()
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            probe.WaitForExit(10_000);
            if (lines.Length == 0) return null;

            double? seconds = lines.Length > 1
                && double.TryParse(lines[1], System.Globalization.NumberStyles.Float,
                                   System.Globalization.CultureInfo.InvariantCulture, out var d)
                ? d
                : null;

            return (lines[0], seconds);
        }
        catch { return null; }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>Decodes the stream back to float PCM, so we can check that what came out the far end
    /// is the audio we put in.</summary>
    private static float[] DecodeToPcm(byte[] audio)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cast-test-{Guid.NewGuid():n}");
        try
        {
            File.WriteAllBytes(file, audio);

            using var dec = Process.Start(new ProcessStartInfo("ffmpeg")
            {
                ArgumentList =
                {
                    "-v", "error",
                    "-i", file,
                    "-f", "f32le", "-ac", "2", "-ar", "44100", "-",
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            using var buffer = new MemoryStream();
            dec.StandardOutput.BaseStream.CopyTo(buffer);
            dec.WaitForExit(20_000);

            var bytes = buffer.ToArray();
            var samples = new float[bytes.Length / sizeof(float)];
            Buffer.BlockCopy(bytes, 0, samples, 0, samples.Length * sizeof(float));
            return samples;
        }
        catch { return Array.Empty<float>(); }
        finally { try { File.Delete(file); } catch { } }
    }

    [Theory]
    [InlineData(CastAudioFormat.Mp3, "mp3")]
    [InlineData(CastAudioFormat.Flac, "flac")]
    public void Publishes_Audio_The_Receiver_Can_Actually_Decode(CastAudioFormat format, string codec)
    {
        if (!HasFfmpeg) return;   // ffmpeg is the app's decoder too; without it there is nothing to test

        using var receiver = new FakeReceiver();
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, format);

        Assert.True(sink.IsAvailable);

        sink.Begin(Track(2.0), Rate, Channels);
        PlayTone(sink, 2.0);

        var fetched = receiver.WaitForBody(TimeSpan.FromSeconds(30));
        Assert.NotNull(fetched);
        Assert.True(fetched!.Length > 0, "the receiver fetched an empty stream");

        var probed = Probe(fetched);
        Assert.NotNull(probed);
        Assert.Equal(codec, probed!.Value.Codec);

        // Measure the length by decoding, not by reading the container: a live Matroska stream
        // deliberately carries no duration (that is what "live" means: the length is not known
        // when the header goes out), so the FLAC stream reports none. That is why the
        // receiver is told the track's length in the LOAD instead.
        var decoded = DecodeToPcm(fetched!);
        var seconds = decoded.Length / (double)(Rate * Channels);

        // Encoders pad; two seconds in should be about two seconds out.
        Assert.InRange(seconds, 1.7, 2.4);
    }

    /// <summary>The receiver is told the right MIME type for what we are actually sending it. FLAC
    /// travels in Matroska (nothing else can carry it with no length), and calling that
    /// <c>audio/mpeg</c> would have the receiver hand it to an MP3 decoder.</summary>
    [Theory]
    [InlineData(CastAudioFormat.Mp3, "audio/mpeg")]
    [InlineData(CastAudioFormat.Flac, "audio/x-matroska")]
    [InlineData(CastAudioFormat.Ac3Surround, "audio/mp4; codecs=\"ac-3\"")]
    public void Declares_The_Mime_Type_Of_What_It_Is_Sending(CastAudioFormat format, string mime)
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, format);

        sink.Begin(Track(30), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        Assert.Equal(mime, receiver.Media.ContentType);
        sink.Stop();
    }

    /// <summary>
    /// Surround goes out with no duration, and it does so whoever is listening.
    ///
    /// <para>A duration is what makes a receiver load the stream BUFFERED, and a buffered
    /// fragmented-MP4 AC-3 stream never leaves "buffering" on the hardware. That is the box's own
    /// AC-3 pipeline, confirmed on the device itself with no receiver app of ours involved. LIVE
    /// plays it. This must not depend on whether the device answered the capability question (RPM's
    /// own receiver included): made conditional, 5.1 does not play at all. Every other format keeps
    /// its duration: only surround is affected, and only because the decoder is.</para>
    /// </summary>
    [Theory]
    [InlineData(CastAudioFormat.Mp3, 300.0)]
    [InlineData(CastAudioFormat.Flac, 300.0)]
    [InlineData(CastAudioFormat.Ac3Surround, null)]
    public void Surround_Loads_Live_Whichever_Receiver_Is_Listening(CastAudioFormat format, double? duration)
    {
        if (!HasFfmpeg) return;

        foreach (var capabilities in new CastCapabilities?[]
                 {
                     null,                                              // Google's stock receiver
                     new(Protocol: 1, Ac3: true, Eac3: false, Flac: true, HiResAudio: true,
                         Display: true, Ac3Playable: true, FlacPlayable: true),
                 })
        {
            using var receiver = new FakeReceiver(fetch: false) { Capabilities = capabilities };
            using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, format);

            sink.Begin(Track(300), Rate, Channels);
            Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

            Assert.Equal(duration, receiver.Media.DurationSeconds);
            sink.Stop();
        }
    }

    /// <summary>
    /// A box that can do Dolby but whose player will not take it right now gets stereo, not silence.
    ///
    /// <para>These are two different questions, and only the second one decides whether anything
    /// comes out of the speakers. A Shield is a Dolby box whatever else is true, but the decode is
    /// only enabled when there is something downstream to pass Dolby to, so an amplifier switched
    /// off, or an output that has renegotiated to the television's own speakers, turns the player's
    /// answer to no while the box's stays yes. This can change between two runs on the same
    /// hardware, and a listener hears it as "5.1 stopped working again". Deciding on the box's answer
    /// sends AC-3 into a player that refuses it, and nothing plays.</para>
    /// </summary>
    [Fact]
    public void A_Dolby_Box_Whose_Player_Refuses_Dolby_Gets_Stereo()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false)
        {
            // What the amplifier being off looks like from here.
            Capabilities = new CastCapabilities(
                Protocol: 1, Ac3: true, Eac3: false, Flac: false,
                HiResAudio: false, Display: true, Ac3Playable: false, FlacPlayable: false),
        };
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, CastAudioFormat.Ac3Surround);

        CastTrouble? notice = null;
        sink.Notice += reason => notice = reason;

        sink.Begin(Track(300), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        Assert.Equal("audio/mpeg", receiver.Media.ContentType);
        Assert.NotNull(notice);

        // And it says which of the two refused, because the listener can fix one of them.
        Assert.IsType<CastTrouble.DolbyOff>(notice);
        Assert.True(notice!.IsNotice);

        sink.Stop();
    }

    /// <summary>
    /// The surround stream really is Dolby Digital 5.1, all the way through the encoder, the small
    /// server and the pacer, and out the far end where a receiver would decode it.
    ///
    /// <para>This is the path nothing else covers: surround does not use the ring the other formats
    /// travel in (it re-encodes the source file), so it has its own encoder, its own container and
    /// its own pacing, and a mistake in any of them is heard only as silence.</para>
    /// </summary>
    [Fact]
    public void Surround_Sends_Dolby_Digital_Five_Point_One()
    {
        if (!HasFfmpeg) return;

        var source = ToneFile(3.0);
        try
        {
            using var receiver = new FakeReceiver();
            using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, CastAudioFormat.Ac3Surround);

            sink.Begin(Track(3.0) with { Path = source }, Rate, Channels);
            PlayTone(sink, 3.0);

            var fetched = receiver.WaitForBody(TimeSpan.FromSeconds(30));
            Assert.NotNull(fetched);
            Assert.True(fetched!.Length > 0, "the receiver fetched an empty stream");

            var stream = ProbeStream(fetched);
            Assert.NotNull(stream);
            Assert.Equal("ac3", stream!.Value.Codec);
            Assert.Equal(6, stream.Value.Channels);
        }
        finally { try { File.Delete(source); } catch { } }
    }

    /// <summary>
    /// A cover's URL names the picture, so an album's tracks share one and a change gets a new one.
    ///
    /// <para>Both halves matter and they pull against each other. A URL that never changes leaves the
    /// first track's sleeve on screen for the rest of the playlist, because the receiver has fetched
    /// it and has no reason to ask again. A URL that always changes sends the same picture across the
    /// network once per track and makes the screen cross-fade it into itself each time, which is a
    /// flicker with no change. Naming the URL after the contents satisfies both at once.</para>
    /// </summary>
    [Fact]
    public void A_Covers_Url_Names_The_Picture_Not_The_Track()
    {
        if (!HasFfmpeg) return;

        var folder = Path.Combine(Path.GetTempPath(), $"cast-art-{Guid.NewGuid():n}");
        Directory.CreateDirectory(folder);
        try
        {
            // Two tracks of one album, sharing its sleeve, and a third from a record with another.
            var first = Path.Combine(folder, "01.flac");
            var second = Path.Combine(folder, "02.flac");
            File.Move(ToneFile(1.0), first);
            File.Move(ToneFile(1.0), second);
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), OnePixelJpeg());

            var elsewhere = Path.Combine(Path.GetTempPath(), $"cast-art-{Guid.NewGuid():n}");
            Directory.CreateDirectory(elsewhere);
            var third = Path.Combine(elsewhere, "01.flac");
            File.Move(ToneFile(1.0), third);
            File.WriteAllBytes(Path.Combine(elsewhere, "cover.jpg"), AnotherPixelJpeg());

            using var receiver = new FakeReceiver(fetch: false);
            using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

            sink.Begin(Track(30) with { Path = first }, Rate, Channels);
            Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

            var sameAlbum = ArtUrlAfter(sink, receiver, second, "Second");
            var otherAlbum = ArtUrlAfter(sink, receiver, third, "Third");

            Assert.NotNull(sameAlbum);
            Assert.NotNull(otherAlbum);

            // The album's own sleeve, twice: one URL, so the receiver never asks twice and the screen
            // never cross-fades it into itself.
            var alsoSameAlbum = ArtUrlAfter(sink, receiver, first, "Fourth");
            Assert.Equal(sameAlbum, alsoSameAlbum);

            // A different picture is a different URL, or it would never arrive at all.
            Assert.NotEqual(sameAlbum, otherAlbum);

            sink.Stop();
            try { Directory.Delete(elsewhere, recursive: true); } catch { }
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    /// <summary>Chains a track into the stream and returns the cover URL the screen was given.</summary>
    private static string? ArtUrlAfter(CastSink sink, FakeReceiver receiver, string path, string title)
    {
        sink.TrackChanged(new SinkTrack(path, title, "Artist", "Album", 200, 0));
        Assert.True(SpinFor(() => receiver.NowPlaying.Title == title),
                    $"the screen was never told about {title}");
        return receiver.NowPlaying.Art;
    }

    /// <summary>A second one-pixel JPEG, of a different colour, so it hashes differently.</summary>
    private static byte[] AnotherPixelJpeg()
    {
        var jpeg = OnePixelJpeg();
        jpeg[^3] ^= 0x5A;          // one byte of image data, still a decodable file
        return jpeg;
    }

    /// <summary>
    /// A record whose cover is a file in its folder casts with that cover.
    ///
    /// <para>The library does not show only embedded art; it shows embedded art <i>or the album
    /// folder's image</i>, and most ripped albums have only the second. Reading the tags alone would
    /// mean a record with a sleeve on screen goes to the television with none, and nothing shows
    /// that: a missing cover looks the same as a record that never had one.</para>
    /// </summary>
    [Fact]
    public void A_Cover_In_The_Album_Folder_Is_Cast_Like_One_In_The_Tags()
    {
        if (!HasFfmpeg) return;

        var folder = Path.Combine(Path.GetTempPath(), $"cast-art-{Guid.NewGuid():n}");
        Directory.CreateDirectory(folder);
        try
        {
            // A ripped album: real audio with nothing in its tags, and the sleeve beside it. Real,
            // and not an empty file, because AlbumArt.ForFile reads the tags first: a file it cannot
            // open at all is a different case from one that simply has no picture in it.
            var track = Path.Combine(folder, "01 track.flac");
            var made = ToneFile(1.0);
            File.Move(made, track);
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), OnePixelJpeg());

            using var receiver = new FakeReceiver(fetch: false);
            using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

            sink.Begin(Track(30) with { Path = track }, Rate, Channels);
            Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

            receiver.RefuseArtwork();

            Assert.True(SpinFor(() => Volatile.Read(ref receiver.ArtworkAnswers) > 0),
                        "the receiver asked about the cover and was never answered");
            Assert.NotNull(receiver.ArtworkSent);
            Assert.True(receiver.ArtworkSent!.Length > 0,
                        "the folder's cover was not found — the sink is still only reading tags");

            sink.Stop();
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    /// <summary>
    /// A record with no file on this disk still casts its cover.
    ///
    /// <para>This is the borrowed shelf, and the offloaded album. Both play perfectly (one streams
    /// from the computer that has it, the other from the store), and neither has a file here to read
    /// a picture from: a borrowed track's path belongs to <i>that</i> machine, and an offloaded
    /// one's file is gone. A sink that looked only in the file would cast a blank square for both,
    /// which to the listener looks like a record with no sleeve, not a sleeve that was not
    /// fetched.</para>
    ///
    /// <para>What is asserted is only that the sink ASKS: where the answer comes from is the
    /// caller's business, and the desktop's answer reaches the peer and then the store (see
    /// PlaybackService.CastCoverAsync).</para>
    /// </summary>
    [Fact]
    public void A_Record_With_No_File_Here_Still_Casts_Its_Cover()
    {
        if (!HasFfmpeg) return;

        var sleeve = OnePixelJpeg();

        using var receiver = new FakeReceiver(fetch: false);

        // The path is a stranger's: nothing at it, and no folder beside it either. Exactly what a
        // borrowed row carries.
        using var sink = new CastSink(
            receiver,
            (_, _) => Task.FromResult<AlbumCover?>(new AlbumCover(sleeve, "image/jpeg", AlbumCover.SourceElsewhere)));

        sink.Begin(Track(30) with { Path = "/not/on/this/disk/01 track.flac" }, Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        receiver.RefuseArtwork();

        Assert.True(SpinFor(() => Volatile.Read(ref receiver.ArtworkAnswers) > 0),
                    "the receiver asked about the cover and was never answered");
        Assert.Equal(sleeve, receiver.ArtworkSent);

        sink.Stop();
    }

    /// <summary>
    /// The album folder's picture wins over the one in the tags.
    ///
    /// <para>A cover chosen in the album editor is written to the folder beside the tracks, and that
    /// is what this machine draws the record by; the tags keep whatever the rip put in them. Read in
    /// the other order the television shows the picture the user replaced. This is the same reason
    /// SharingService.CoverFor gives a borrower the folder's copy first.</para>
    /// </summary>
    [Fact]
    public async Task The_Folder_Cover_Is_Preferred_To_The_One_In_The_Tags()
    {
        if (!HasFfmpeg) return;

        var folder = Path.Combine(Path.GetTempPath(), $"cast-art-order-{Guid.NewGuid():n}");
        Directory.CreateDirectory(folder);
        try
        {
            var track = Path.Combine(folder, "01 track.flac");
            File.Move(ToneFile(1.0), track);

            var chosen = OnePixelJpeg();
            File.WriteAllBytes(Path.Combine(folder, "cover.jpg"), chosen);

            var cover = await SinkArt.FromTheFileAlone(
                Track(30) with { Path = track }, CancellationToken.None);

            Assert.NotNull(cover);
            Assert.Equal(AlbumCover.SourceFolder, cover!.Source);
            Assert.Equal(chosen, cover.Bytes);
        }
        finally { try { Directory.Delete(folder, recursive: true); } catch { } }
    }

    /// <summary>
    /// The heart pressed at the television reaches the player, and what the player writes reaches the
    /// television.
    ///
    /// <para>The sink only passes messages here, and that is intended: the mark belongs to
    /// the library, which the sink cannot see and has no business writing to. A press comes up as
    /// <see cref="CastSink.RemoteFavorite"/> for whoever owns the collection to act on, and the answer
    /// goes back down as a value, including the null that means "this record is not one we hold", so
    /// the screen can take the key away rather than offer one that cannot be pressed.</para>
    /// </summary>
    [Fact]
    public void The_Heart_Is_Carried_Up_And_The_Answer_Back_Down()
    {
        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        var pressed = 0;
        sink.RemoteFavorite += () => Interlocked.Increment(ref pressed);

        receiver.PressFavorite();
        Assert.Equal(1, Volatile.Read(ref pressed));

        // Nothing is claimed by the press itself: the sink does not change a mark by itself.
        Assert.Null(receiver.Loved);

        sink.SetFavoriteAsync(true).Wait();
        Assert.True(receiver.Loved);

        // …and "we do not hold this record" is an answer of its own, not a hollow heart.
        sink.SetFavoriteAsync(null).Wait();
        Assert.Null(receiver.Loved);
    }

    /// <summary>The smallest thing that is really a JPEG, for a test that only cares whether the
    /// bytes beside the music were found.</summary>
    private static byte[] OnePixelJpeg() => Convert.FromBase64String(
        "/9j/4AAQSkZJRgABAQEAYABgAAD/2wBDAAgGBgcGBQgHBwcJCQgKDBQNDAsLDBkSEw8UHRofHh0a" +
        "HBwgJC4nICIsIxwcKDcpLDAxNDQ0Hyc5PTgyPC4zNDL/wAALCAABAAEBAREA/8QAFAABAAAAAAAA" +
        "AAAAAAAAAAAACf/EABQQAQAAAAAAAAAAAAAAAAAAAAD/2gAIAQEAAD8AKp//2Q==");

    /// <summary>
    /// A track with no cover is answered as such, so the last one comes off the screen.
    ///
    /// <para>The cover's URL goes out in the LOAD before the tags have been read (it has to, or
    /// every track start waits on a picture), which means it is published for tracks that turn out to
    /// have no picture at all. Those serve a 404, the receiver asks about it, and the answer has to
    /// come even when it is "there is none". Saying nothing leaves the previous record's sleeve up:
    /// play a singer with no embedded art after one who has some, and his cover stays over her
    /// song.</para>
    /// </summary>
    [Fact]
    public void A_Track_With_No_Cover_Is_Answered_So_The_Last_One_Comes_Down()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        // Track() points at a path that does not exist, so there is no artwork to be had: exactly
        // the case, from the sink's point of view, as a file whose tags carry no picture.
        sink.Begin(Track(30), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        // The URL is published regardless, because at LOAD time nobody knows yet.
        Assert.NotNull(receiver.Media.ArtworkUrl);

        receiver.RefuseArtwork();

        // Answered, and answered with nothing, which is the part that clears the screen.
        Assert.True(SpinFor(() => Volatile.Read(ref receiver.ArtworkAnswers) > 0),
                    "the receiver asked about the cover and was never answered");
        Assert.Null(receiver.ArtworkSent);

        sink.Stop();
    }

    /// <summary>True when the task finished inside the window, without blocking the test thread.</summary>
    private static async Task<bool> Finishes(Task task, TimeSpan within)
        => await Task.WhenAny(task, Task.Delay(within)) == task;

    /// <summary>Waits briefly for something that happens on another thread.</summary>
    private static bool SpinFor(Func<bool> until)
    {
        for (int i = 0; i < 100; i++)
        {
            if (until()) return true;
            Thread.Sleep(20);
        }
        return false;
    }

    /// <summary>
    /// The speaker is given as much as it will take, and the transport decides how much that is.
    ///
    /// <para>There is no fixed lead here (a distance the stream is held in front of the receiver's
    /// reported position). A lead makes holes in the music: it refuses to send against a clock that
    /// has stopped, against a speaker that is refilling, and against one that has not started. The
    /// risk a lead would guard against, a receiver that is flooded, is handled better by the socket:
    /// it blocks when the far end stops reading, and that measurement is never stale.</para>
    ///
    /// <para>So this asserts that a receiver that keeps reading keeps being served, well past any
    /// fixed lead, even when its clock reports nothing at all.</para>
    /// </summary>
    [Fact]
    public void A_Speaker_Is_Given_As_Much_As_It_Will_Take()
    {
        if (!HasFfmpeg) return;

        const double BytesPerSecond = 640_000 / 8.0;

        var source = ToneFile(240.0);
        try
        {
            // Reading steadily, reporting nothing, and its clock stopped: together these are every
            // state that a lead-based pacer would mistake for a full buffer.
            using var receiver = new FakeReceiver(drink: true) { Pinned = 0 };
            using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, CastAudioFormat.Ac3Surround);

            Assert.Equal(CastPlayback.Unknown, receiver.Playback);

            sink.Begin(Track(240.0) with { Path = source }, Rate, Channels);
            Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

            // Far past a fixed lead of 25 s, or of 50 s. A stream held by a lead stops there and never
            // reaches 100 s. How fast it gets there depends only on how fast ffmpeg encodes on this
            // machine, so the test waits for the amount and not for a fixed time.
            double Given() => Interlocked.Read(ref receiver.BytesTaken) / BytesPerSecond;
            var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(60);
            while (Given() <= 100 && DateTime.UtcNow < deadline)
                Thread.Sleep(100);

            var given = Given();
            Assert.True(given > 100,
                        $"the stream stopped at {given:F1}s to a speaker that was still drinking");

            sink.Stop();
        }
        finally { try { File.Delete(source); } catch { } }
    }

    /// <summary>A real file for the surround encoder to re-encode. The other formats never need one
    /// (they encode the ring), so this is the only test that opens anything on disk.</summary>
    private static string ToneFile(double seconds)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cast-test-{Guid.NewGuid():n}.flac");

        using var ffmpeg = Process.Start(new ProcessStartInfo("ffmpeg")
        {
            ArgumentList =
            {
                "-hide_banner", "-loglevel", "error",
                "-f", "lavfi",
                "-i", $"sine=frequency=440:duration={seconds.ToString(System.Globalization.CultureInfo.InvariantCulture)}:sample_rate={Rate}",
                "-ac", "2", "-c:a", "flac", "-y", file,
            },
            RedirectStandardError = true,
            UseShellExecute = false,
        })!;
        ffmpeg.WaitForExit(30_000);
        return file;
    }

    /// <summary>Codec and channel count of what came off the wire. Channels are the main question for
    /// surround: an AC-3 stream folded to two is the failure that sounds like success.</summary>
    private static (string Codec, int Channels)? ProbeStream(byte[] audio)
    {
        var file = Path.Combine(Path.GetTempPath(), $"cast-test-{Guid.NewGuid():n}.mp4");
        try
        {
            File.WriteAllBytes(file, audio);

            using var probe = Process.Start(new ProcessStartInfo("ffprobe")
            {
                ArgumentList =
                {
                    "-v", "error",
                    "-select_streams", "a:0",
                    "-show_entries", "stream=codec_name,channels",
                    "-of", "default=noprint_wrappers=1:nokey=1",
                    file,
                },
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                UseShellExecute = false,
            })!;

            var lines = probe.StandardOutput.ReadToEnd()
                             .Split('\n', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries);
            probe.WaitForExit(10_000);

            return lines.Length >= 2 && int.TryParse(lines[1], out var channels)
                ? (lines[0], channels)
                : null;
        }
        catch { return null; }
        finally { try { File.Delete(file); } catch { } }
    }

    /// <summary>
    /// The purpose of the lossless option: the samples that come out of the far end are the samples
    /// that went in. If this ever regresses to a lossy path, nothing breaks and nobody notices,
    /// which is exactly why it is worth a test.
    /// </summary>
    [Fact]
    public void Lossless_Really_Is_Lossless()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver();
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone, CastAudioFormat.Flac);

        sink.Begin(Track(1.0), Rate, Channels);
        PlayTone(sink, 1.0);

        var fetched = receiver.WaitForBody(TimeSpan.FromSeconds(30));
        Assert.NotNull(fetched);

        var decoded = DecodeToPcm(fetched!);
        const int FramesPerBlock = 2205;
        int blocks = (int)(1.0 * Rate / FramesPerBlock);
        var sent = new List<float>();
        for (int b = 0; b < blocks; b++) sent.AddRange(ToneBlock(FramesPerBlock, b * FramesPerBlock));

        Assert.True(decoded.Length >= sent.Count, $"got {decoded.Length} samples back, sent {sent.Count}");

        // FLAC is an integer codec and our pipeline is float, so 24-bit quantisation is the only
        // difference permitted. One LSB of 24-bit is ~6e-8; allow slightly more and no more.
        for (int i = 0; i < sent.Count; i++)
            Assert.True(Math.Abs(decoded[i] - sent[i]) < 1e-6,
                        $"sample {i} came back as {decoded[i]}, sent {sent[i]} — this is not lossless");
    }

    /// <summary>The receiver is told to fetch the URL we are actually serving, with the track's
    /// metadata attached. That is what ends up on the television.</summary>
    [Fact]
    public void Hands_The_Receiver_Its_Own_Live_Url_And_The_Tags()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver();
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(1.0), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        var media = receiver.Media;
        Assert.Equal("audio/mpeg", media.ContentType);
        Assert.Equal("Test Track", media.Title);
        Assert.Equal("Test Artist", media.Artist);
        Assert.Equal("Test Album", media.Album);
        Assert.StartsWith("http://", media.Url);
        Assert.Contains("/stream/", media.Url);

        // The receiver is given the track's length, so it can show a running time rather than an
        // open-ended live stream. It is what lets the seek bar mean anything.
        Assert.Equal(1.0, media.DurationSeconds);

        sink.Stop();
    }

    /// <summary>
    /// Seeking while casting: the stream restarts at the new offset, so the receiver gets a brand new
    /// URL and its clock starts from zero again. The position the user sees is composed back together
    /// upstream (seek offset + receiver's time), which is why a live stream can carry a
    /// working seek bar at all. This is how VLC does it too.
    /// </summary>
    [Fact]
    public void Seeking_Hands_The_Receiver_A_New_Stream_Starting_At_The_Seek_Point()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(300) with { StartSeconds = 0 }, Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));
        var before = receiver.Media.Url;

        // What AudioEngine.SeekTo does: re-Begin the sink at the new offset.
        receiver.ResetLoad();
        sink.Begin(Track(300) with { StartSeconds = 120 }, Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        Assert.NotEqual(before, receiver.Media.Url);
        // Still the whole track's length, not the 180 s that remain: the receiver counts within the
        // stream, and the offset is added back on before the user sees a number.
        Assert.Equal(300, receiver.Media.DurationSeconds);

        // And the receiver's clock is back at zero for the new stream, so the composed position is
        // the seek point rather than the old one.
        Assert.Equal(0, sink.FramesPlayed);

        sink.Stop();
    }

    /// <summary>
    /// Changing track must not send the receiver a STOP.
    ///
    /// <para>The new LOAD replaces its media by itself. A STOP fired alongside that LOAD is a second
    /// message racing it across the network with nothing ordering the two, and when it loses, it
    /// arrives <i>after</i> the LOAD and kills the track that just started. This happens on every
    /// track change and every seek, which is the most common path there is.</para>
    /// </summary>
    [Fact]
    public void Changing_Track_Does_Not_Stop_The_Receiver()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver();
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(1.0), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        sink.Begin(Track(1.0), Rate, Channels);   // next track in the playlist

        Assert.Equal(0, receiver.StopMediaCount);

        // But a real stop still stops it.
        sink.Stop();
        Assert.Equal(1, receiver.StopMediaCount);
    }

    /// <summary>Each track gets its own stream id, so a receiver still holding the previous URL is
    /// told the track is gone rather than being handed the wrong song's audio.</summary>
    [Fact]
    public void Every_Track_Gets_A_Fresh_Stream_Url()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(1.0), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));
        var first = receiver.Media.Url;

        receiver.ResetLoad();
        sink.Begin(Track(1.0), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));
        var second = receiver.Media.Url;

        Assert.NotEqual(first, second);
        sink.Stop();
    }

    /// <summary>
    /// A Cast device does not push back. It has a deep buffer and pulls as fast as the network will
    /// carry it, so the pipeline never blocks on its own: the decoder will race to the end of a
    /// four-minute track in a couple of seconds while the speaker is still on the first verse.
    /// Everything downstream is then wrong: the position, the visualiser, and above all the end of the
    /// track, which arrives while the song is still playing and moves the playlist on.
    ///
    /// <para>The pacer is the only thing that prevents that, so this test uses exactly that case: a
    /// receiver that takes the stream as fast as it is served and reports its position in real time,
    /// like a real speaker.</para>
    /// </summary>
    [Fact]
    public void The_Decoder_Cannot_Run_Away_From_A_Greedy_Receiver()
    {
        if (!HasFfmpeg) return;

        // FakeReceiver reads the body as fast as the socket will give it. That is the problem, and
        // it is what a Shield does.
        using var receiver = new FakeReceiver();
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(300), Rate, Channels);

        const int FramesPerBlock = 2205;
        var blocksWritten = 0;
        var pump = new Thread(() =>
        {
            for (int b = 0; b < 300 * Rate / FramesPerBlock; b++)   // five minutes of audio, offered flat out
            {
                if (!sink.Enqueue(ToneBlock(FramesPerBlock, b * FramesPerBlock), FramesPerBlock)) return;
                Interlocked.Increment(ref blocksWritten);
            }
        })
        { IsBackground = true };
        pump.Start();

        Thread.Sleep(TimeSpan.FromSeconds(3));   // three seconds of real time

        var secondsPushed = Volatile.Read(ref blocksWritten) * FramesPerBlock / (double)Rate;

        // Three seconds of wall clock, plus what the speaker is allowed to buffer. Nowhere near
        // the whole track, which is where an unpaced pipeline would already be.
        Assert.True(secondsPushed < 20,
            $"pushed {secondsPushed:F1}s of audio in 3s of real time — the decoder is not being paced, "
            + "and the track will 'end' while the speaker is still playing it");

        sink.Stop();
        Assert.True(pump.Join(TimeSpan.FromSeconds(5)), "Stop did not release the paced pump");
    }

    /// <summary>
    /// The main purpose of the design: a receiver that stops reading must stop the decoder, rather
    /// than letting it race ahead into memory. If this regresses, casting still "works", but the
    /// position, the visualiser and the EQ latency all quietly become wrong.
    /// </summary>
    [Fact]
    public async Task A_Receiver_That_Stops_Pulling_Stops_The_Decoder()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);   // never comes to collect
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(60), Rate, Channels);

        // The ring holds ~2 s. Push well past that from another thread and it must park.
        var parked = new TaskCompletionSource<bool>();
        var pump = new Thread(() =>
        {
            const int FramesPerBlock = 2205;
            for (int b = 0; b < 400; b++)   // 20 s of audio into a 2 s ring
            {
                if (!sink.Enqueue(ToneBlock(FramesPerBlock, b * FramesPerBlock), FramesPerBlock))
                {
                    parked.TrySetResult(false);   // torn down, not parked
                    return;
                }
            }
            parked.TrySetResult(false);           // it swallowed the lot: no backpressure at all
        })
        { IsBackground = true };
        pump.Start();

        Assert.False(await Finishes(parked.Task, TimeSpan.FromSeconds(1)),
                     "the decoder ran ahead of a receiver that was not pulling — backpressure is broken");

        sink.Stop();   // must eject the parked pump rather than deadlock it
        Assert.True(await Finishes(parked.Task, TimeSpan.FromSeconds(5)), "Stop did not release the parked pump");
        Assert.False(await parked.Task);
    }

    [Fact]
    public void Reports_A_Receiver_That_Never_Fetches_Instead_Of_Hanging()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        CastTrouble? failure = null;
        var failed = new ManualResetEventSlim();
        sink.Failed += reason => { failure = reason; failed.Set(); };

        sink.Begin(Track(5), Rate, Channels);

        // The sink gives a receiver 20 s to come and get it before giving up.
        Assert.True(failed.Wait(TimeSpan.FromSeconds(40)), "a receiver that never fetched should have been reported");
        Assert.IsType<CastTrouble.NeverFetched>(failure);

        // And having failed, it refuses more audio, which is what releases the engine's pump.
        Assert.False(sink.Enqueue(new float[2 * Channels], 2));
    }

    /// <summary>
    /// Finishing with a speaker has to close the connection to it, not merely stop sending audio.
    ///
    /// <para>An open Cast connection is not inert: Sharpcaster keeps heart-beating on it, and a ping
    /// that meets a socket the speaker has closed throws from an <c>async void</c> timer handler,
    /// where nothing in the process can catch it. The sink owns the connection, so the sink closes
    /// it. See SharpcasterReceiver.</para>
    /// </summary>
    [Fact]
    public void Disposing_The_Sink_Closes_The_Connection()
    {
        using var receiver = new FakeReceiver(fetch: false);
        var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Dispose();

        Assert.True(receiver.WaitForDispose(TimeSpan.FromSeconds(5)),
                    "the sink was disposed and left the connection to the speaker open");

        // And the speaker was told to stop before the connection went, rather than being cut off.
        Assert.Equal(1, receiver.StopMediaCount);
    }

    /// <summary>The speaker went away mid-cast (power, network, another sender taking it over). The
    /// sink has to say so, because that is what brings the music back to this computer instead of
    /// leaving the player streaming into nothing.</summary>
    [Fact]
    public void A_Receiver_That_Disconnects_Is_Reported()
    {
        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        CastTrouble? failure = null;
        sink.Failed += reason => failure = reason;

        receiver.Disconnect();

        Assert.NotNull(failure);
        Assert.IsType<CastTrouble.Disconnected>(failure);
    }

    /// <summary>
    /// Tearing the sink down closes the connection itself, so the disconnect that follows is our own
    /// doing and must not be reported as a failure: that would tell the player to stop casting
    /// while it is already stopping, or, on a swap between speakers, while the next one is starting.
    /// </summary>
    [Fact]
    public void A_Disconnect_We_Caused_Is_Not_Reported()
    {
        using var receiver = new FakeReceiver(fetch: false);
        var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        CastTrouble? failure = null;
        sink.Failed += reason => failure = reason;

        sink.Dispose();
        receiver.Disconnect();

        Assert.Null(failure);
    }

    /// <summary>
    /// Pausing at the speaker (its touch surface, the Google Home app, a voice command) has to
    /// reach the player. Without this the app goes on saying it is playing while nothing is heard.
    /// </summary>
    [Fact]
    public void A_Pause_At_The_Speaker_Reaches_The_Player()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        int paused = 0, resumed = 0;
        sink.RemotePaused += () => paused++;
        sink.RemoteResumed += () => resumed++;

        receiver.Report(CastPlayback.Playing);
        receiver.Report(CastPlayback.Paused);
        Assert.Equal(1, paused);

        // And the player follows it, so the speaker starting again is not a surprise either.
        sink.Pause();
        receiver.Report(CastPlayback.Playing);
        Assert.Equal(1, resumed);
    }

    /// <summary>
    /// A record sent to a speaker while the player is paused stays paused.
    ///
    /// <para>The stream is loaded playing, and has to be (a receiver that is not playing never comes
    /// for it), so a speaker given a paused record always reports PLAYING once before the pause
    /// reaches it. Read as a press at the speaker, that report would start the music: the player
    /// would resume itself, because it cannot tell its own unanswered pause from a person's
    /// action.</para>
    /// </summary>
    [Fact]
    public void The_Load_Playing_Under_Our_Own_Pause_Is_Not_A_Press_In_The_Room()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        int resumed = 0;
        sink.RemoteResumed += () => resumed++;

        sink.Pause();                            // the player, carrying a pause onto this speaker
        receiver.Report(CastPlayback.Playing);   // the autoplaying LOAD, still crossing when we asked

        Assert.Equal(0, resumed);                    // the player does not start itself again…
        Assert.True(receiver.PauseCount >= 2,        // …and the speaker is asked again instead
                    $"the speaker was asked to stop {receiver.PauseCount} time(s)");

        // Once it has actually stopped, a PLAYING comes from a person, and the player follows them.
        receiver.Report(CastPlayback.Paused);
        receiver.Report(CastPlayback.Playing);
        Assert.Equal(1, resumed);
    }

    /// <summary>Our own pause is not news coming back. The receiver reports PAUSED after we ask it
    /// to pause, and reporting that to the player would be an echo, not a command.</summary>
    [Fact]
    public void Our_Own_Pause_Is_Not_Reported_Back()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        int paused = 0;
        sink.RemotePaused += () => paused++;

        sink.Pause();
        receiver.Report(CastPlayback.Paused);

        Assert.Equal(0, paused);
    }

    /// <summary>
    /// Stop at the speaker means stop. The receiver goes idle at the end of every track as well, so
    /// the deciding value is <c>idleReason</c>: FINISHED is the playlist moving on, CANCELLED is
    /// somebody asking for silence, and answering the second with the next song is the opposite of
    /// what they asked for.
    /// </summary>
    [Theory]
    [InlineData(CastIdle.Stopped, true)]
    [InlineData(CastIdle.Interrupted, true)]
    [InlineData(CastIdle.Finished, false)]
    public void A_Stop_At_The_Speaker_Is_Told_Apart_From_A_Track_Ending(CastIdle reason, bool expected)
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        var stopped = false;
        sink.RemoteStopped += () => stopped = true;

        receiver.Report(CastPlayback.Playing);
        receiver.Report(CastPlayback.Idle, reason);

        Assert.Equal(expected, stopped);
    }

    /// <summary>
    /// Next at the speaker is a skip, not a stop, which is why RPM has a receiver of its own. It
    /// reaches the player, and the stream it is playing is never touched.
    /// </summary>
    [Fact]
    public void Next_At_The_Speaker_Reaches_The_Player()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        int next = 0, previous = 0, stopped = 0;
        sink.RemoteNext += () => next++;
        sink.RemotePrevious += () => previous++;
        sink.RemoteStopped += () => stopped++;

        receiver.PressNext();
        receiver.PressPrevious();

        Assert.Equal(1, next);
        Assert.Equal(1, previous);

        // And definitely not reported as a stop, which is all the stock receiver can ever say.
        Assert.Equal(0, stopped);
    }

    /// <summary>Once the sink is finished with, a skip arriving late is nobody's business: the
    /// player has already moved on, and acting on it would skip a track it never asked to skip.</summary>
    [Fact]
    public void A_Skip_After_Teardown_Is_Ignored()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        var next = 0;
        sink.RemoteNext += () => next++;

        sink.Dispose();
        receiver.PressNext();

        Assert.Equal(0, next);
    }

    /// <summary>
    /// The gapless changeover retitles the screen instead of reloading the media.
    ///
    /// <para>The receiver is holding one endless stream, and the tracks inside it change without it
    /// noticing. That is why it is gapless, and why the television would otherwise show the first
    /// track of the playlist for as long as the stream plays.</para>
    /// </summary>
    [Fact]
    public void A_Chained_Track_Retitles_The_Screen_Without_Reloading()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.Begin(Track(30), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));
        receiver.ResetLoad();

        // The load carries its own now-playing update (the opening track's clock anchor), so this
        // waits for the change itself rather than for "something happened".
        sink.TrackChanged(new SinkTrack("/nonexistent/second.flac", "Second Track", "Second Artist",
                                        "Second Album", 200, 0));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (receiver.NowPlaying.Title != "Second Track" && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.Equal("Second Track", receiver.NowPlaying.Title);
        Assert.Equal("Second Artist", receiver.NowPlaying.Artist);

        // And it says where in the stream the change lands, so the screen can hold the new title
        // until the speaker actually reaches it rather than announcing it ten seconds early.
        Assert.Equal(0, receiver.LastAnchor.TrackAt);
        Assert.True(receiver.LastAnchor.StreamAt >= 0);

        // The media was never handed over again. A LOAD here is the couple of seconds of silence
        // that the chaining path exists to avoid.
        Assert.False(receiver.WaitForLoad(TimeSpan.FromMilliseconds(200)),
                     "a chained track reloaded the media — that is the gap back");
    }

    /// <summary>
    /// The player's slider turns the speaker up, not only down.
    ///
    /// <para>The obvious channel to send volume on is the media channel, and it is the wrong one: its
    /// volume is an attenuation applied underneath the device's own, so 100% means "however loud the
    /// speaker happens to be" and everything else is quieter. The knob has to be the device's.</para>
    /// </summary>
    [Fact]
    public void The_Volume_Is_The_Speakers_Own_Knob()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        // Sends are coalesced (see CastSink.SendVolumeAsync), so the level arrives a moment later
        // rather than on the call. That is intended: a drag is one send, not fifty.
        sink.SetVolume(100);
        Assert.True(receiver.WaitForVolume(1.0, TimeSpan.FromSeconds(5)), "the speaker was never turned up");

        sink.SetVolume(40);
        Assert.True(receiver.WaitForVolume(0.4, TimeSpan.FromSeconds(5)), "the speaker was never turned down");
    }

    /// <summary>
    /// Starting a cast adopts the volume of the room instead of imposing the player's.
    ///
    /// <para>Because the slider drives the speaker's own volume, sending the app's number to it on
    /// connect would set a speaker to whatever the last pair of headphones used: a speaker at 20%
    /// would jump to 100% because that is where the player was. So the player moves instead.</para>
    /// </summary>
    [Fact]
    public void Starting_A_Cast_Adopts_The_Rooms_Volume()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        sink.SetVolume(100);            // the player is loud; the room is not
        receiver.RestingVolume(0.2);

        var adopted = 0;
        sink.VolumeChanged += v => adopted = v;

        sink.Begin(Track(30), Rate, Channels);
        Assert.True(receiver.WaitForLoad(TimeSpan.FromSeconds(5)));

        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds(5);
        while (adopted == 0 && DateTime.UtcNow < deadline) Thread.Sleep(20);

        Assert.Equal(20, adopted);

        // And definitely not the other way round: the speaker was left where it was.
        Assert.Equal(0.2, receiver.DeviceVolume!.Value, 3);
    }

    /// <summary>
    /// A status that arrives while the slider is moving does not drag it backwards.
    ///
    /// <para>The speaker reports the level it had when it answered, so during a drag its statuses are
    /// stale by definition, and adopting them makes the slider keep jumping back to where it was a
    /// moment ago.</para>
    /// </summary>
    [Fact]
    public void A_Stale_Status_Does_Not_Drag_The_Slider_Back()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        var reported = new List<int>();
        sink.VolumeChanged += v => reported.Add(v);

        // A drag: several values in quick succession, ending where the user let go.
        sink.SetVolume(40);
        sink.SetVolume(60);
        sink.SetVolume(80);

        // ...and the speaker answering the first of them, late.
        receiver.TurnVolumeKnob(0.4);

        Assert.Empty(reported);
    }

    /// <summary>Turned at the speaker, followed by the player: while casting they are one knob.</summary>
    [Fact]
    public void The_Player_Follows_The_Volume_Turned_At_The_Speaker()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);
        sink.Begin(Track(30), Rate, Channels);

        var reported = 0;
        sink.VolumeChanged += v => reported = v;

        receiver.TurnVolumeKnob(0.65);

        Assert.Equal(65, reported);
    }

    /// <summary>
    /// Skipping does not stop the music.
    ///
    /// <para>Changing track sends a new stream while the speaker is still playing the old one, and
    /// the old one ends the only way it can: IDLE with CANCELLED, which is exactly what somebody
    /// pressing stop at the speaker looks like. Acting on that stops the music the listener just
    /// asked to skip, so next would sometimes stop playback. Until the new stream is the one playing,
    /// the speaker is reporting about the stream we took away.</para>
    /// </summary>
    [Fact]
    public void The_Old_Streams_Ending_Is_Not_A_Stop()
    {
        if (!HasFfmpeg) return;

        using var receiver = new FakeReceiver(fetch: false);
        using var sink = new CastSink(receiver, SinkArt.FromTheFileAlone);

        var stopped = 0;
        sink.RemoteStopped += () => stopped++;

        sink.Begin(Track(30), Rate, Channels);
        receiver.Report(CastPlayback.Playing);

        // The listener presses next: a new stream goes out while the old one is still playing...
        sink.Begin(Track(30), Rate, Channels);

        // ...and the old one ends, the only way a torn-down stream can.
        receiver.Report(CastPlayback.Idle, CastIdle.Stopped);
        Assert.Equal(0, stopped);

        // A stop pressed once the new stream is the one playing is still a stop.
        receiver.Report(CastPlayback.Playing);
        receiver.Report(CastPlayback.Idle, CastIdle.Stopped);
        Assert.Equal(1, stopped);
    }

    /// <summary>A Cast device, as far as the sink is concerned: it fetches the URL it is given and
    /// says how far it has got. Which is all a real one does.</summary>
    private sealed class FakeReceiver : ICastReceiver, IDisposable
    {
        private readonly bool _fetch;
        private readonly bool _drink;
        private readonly HttpClient _http = new() { Timeout = Timeout.InfiniteTimeSpan };
        private readonly ManualResetEventSlim _loaded = new();
        private readonly ManualResetEventSlim _bodyDone = new();
        private readonly ManualResetEventSlim _closed = new();
        private readonly Stopwatch _playing = new();

        private byte[]? _body;
        private volatile bool _idle;
        private volatile bool _connected = true;

        public FakeReceiver(bool fetch = true, bool drink = false)
        {
            _fetch = fetch;
            _drink = drink;
        }

        public IPAddress Address => IPAddress.Loopback;

        public string Name => "Fake Speaker";

        public bool IsConnected => _connected;

        /// <summary>The speaker goes away, as a real one does when it loses power or another sender
        /// takes it over, and as ours does when the connection is closed from this end.</summary>
        public void Disconnect()
        {
            _connected = false;
            StatusChanged?.Invoke();
        }

        /// <summary>True once the sink has closed the connection. The point of the test: an open Cast
        /// connection goes on heart-beating at a speaker nobody is talking to.</summary>
        public bool WaitForDispose(TimeSpan timeout) => _closed.Wait(timeout);

        public CastMedia Media { get; private set; }

        public double? PositionSeconds =>
            Pinned ?? (_playing.IsRunning || _idle ? _playing.Elapsed.TotalSeconds : null);

        public bool IsIdle => _idle;

        public CastPlayback Playback { get; private set; } = CastPlayback.Unknown;

        public CastIdle Idle { get; private set; } = CastIdle.None;

        public event Action? NextRequested;

        public event Action? PreviousRequested;

        public event Action? ArtworkRefused;

        /// <summary>The cover the sink handed over as data, when the URL was refused. Null is a real
        /// answer ("this track has none"), so <see cref="ArtworkAnswers"/> counts them separately
        /// from whether anything arrived.</summary>
        public byte[]? ArtworkSent { get; private set; }

        public int ArtworkAnswers;

        public Task SendArtworkAsync(byte[]? artwork, CancellationToken cancellation = default)
        {
            ArtworkSent = artwork;
            Interlocked.Increment(ref ArtworkAnswers);
            return Task.CompletedTask;
        }

        /// <summary>Stands in for a receiver that would not load the cover from our URL: mixed
        /// content, which is the normal outcome of an HTTPS page and a LAN HTTP image.</summary>
        public void RefuseArtwork() => ArtworkRefused?.Invoke();

        public CastCapabilities? Capabilities { get; set; }

        /// <summary>What the sink last put on the screen, and how many times it has done so: the
        /// receiver's side of a gapless changeover, which is the only signal a television gets.</summary>
        public (string? Title, string? Artist, string? Album, string? Art) NowPlaying { get; private set; }

        public int NowPlayingUpdates;

        public Task UpdateNowPlayingAsync(CastNowPlaying track, CancellationToken cancellation = default)
        {
            NowPlaying = (track.Title, track.Artist, track.Album, track.ArtworkUrl);
            LastAnchor = (track.TrackAtSeconds, track.StreamAtSeconds);
            Interlocked.Increment(ref NowPlayingUpdates);
            return Task.CompletedTask;
        }

        /// <summary>Where in the track and where in the stream the last update said it was: what
        /// lets the screen hold a new title until the speaker actually reaches the changeover.</summary>
        public (double TrackAt, double StreamAt) LastAnchor { get; private set; }

        /// <summary>Whether the television's picture is on. Nothing in the sink touches it (it is a
        /// setting about the television, not about the audio), and it is here because the interface carries
        /// it.</summary>
        public bool ScreenOn { get; private set; } = true;

        public Task SetScreenAsync(bool on, CancellationToken cancellation = default)
        {
            ScreenOn = on;
            return Task.CompletedTask;
        }

        /// <summary>The heart at the television. Nothing in the sink writes it: the mark is the
        /// library's, and the sink carries the press up and the answer back down.</summary>
        public event Action? FavoriteRequested;

        public bool? Loved { get; private set; }

        public Task SetFavoriteAsync(bool? loved, CancellationToken cancellation = default)
        {
            Loved = loved;
            return Task.CompletedTask;
        }

        /// <summary>The heart, pressed at the television.</summary>
        public void PressFavorite() => FavoriteRequested?.Invoke();

        /// <summary>Next or previous pressed at the speaker. Only RPM's own receiver can report
        /// this, and this fake is standing in for that one.</summary>
        public void PressNext() => NextRequested?.Invoke();

        public void PressPrevious() => PreviousRequested?.Invoke();

        /// <summary>Somebody at the speaker did something (pressed pause on it, told it to stop,
        /// cast something else to it). That is exactly how a real one reports those: a pushed status
        /// and nothing else.</summary>
        public void Report(CastPlayback playback, CastIdle idle = CastIdle.None)
        {
            Playback = playback;
            Idle = idle;
            _idle = playback == CastPlayback.Idle;
            StatusChanged?.Invoke();
        }

        public event Action? StatusChanged;

        /// <summary>
        /// Where this speaker says it has got to, when it is not to be believed to be moving.
        ///
        /// <para>A real one's clock runs; the pacer's job is to stay a set distance in front
        /// of it. Pinning it is how a test can ask "how far in front does it actually get?" without
        /// waiting out a song in real time.</para>
        /// </summary>
        public double? Pinned { get; set; }

        /// <summary>Bytes taken off the stream so far, while it is still being taken. Only counted in
        /// <c>drink</c> mode, where the body is read a chunk at a time rather than in one go.</summary>
        public long BytesTaken;

        /// <summary>The speaker ran dry, as ours reports when it re-enters BUFFERING mid-track.</summary>
        public void Starve() => Starved?.Invoke();

        public event Action? Starved;

        public Task LoadAsync(CastMedia media, CancellationToken cancellation = default)
        {
            Media = media;
            _loaded.Set();
            if (!_fetch) return Task.CompletedTask;

            _ = Task.Run(async () =>
            {
                try
                {
                    _playing.Restart();

                    if (_drink)
                    {
                        // A chunk at a time, so a test can watch the stream arrive instead of only
                        // seeing it once it has ended, which for a paced stream is the point.
                        await using var body = await _http.GetStreamAsync(media.Url);
                        var buffer = new byte[16 * 1024];
                        int n;
                        while ((n = await body.ReadAsync(buffer)) > 0) Interlocked.Add(ref BytesTaken, n);
                        _body = [];
                        return;
                    }

                    // Exactly what a Chromecast does: come and get it, and read until the body ends.
                    _body = await _http.GetByteArrayAsync(media.Url);
                }
                catch { _body = null; }
                finally
                {
                    _playing.Stop();
                    _idle = true;
                    _bodyDone.Set();
                    StatusChanged?.Invoke();
                }
            });
            return Task.CompletedTask;
        }

        public bool WaitForLoad(TimeSpan timeout) => _loaded.Wait(timeout);

        public void ResetLoad() => _loaded.Reset();

        public byte[]? WaitForBody(TimeSpan timeout) => _bodyDone.Wait(timeout) ? _body : null;

        public Task PlayAsync(CancellationToken cancellation = default) => Task.CompletedTask;

        /// <summary>How many times the sink has asked it to stop. More than one is not a fault: a
        /// pause sent beside the LOAD reaches a receiver that has no media session to pause yet, and
        /// asking again is how it lands at all. See CastSink's _awaitingPause.</summary>
        public int PauseCount;

        public Task PauseAsync(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref PauseCount);
            return Task.CompletedTask;
        }

        public int StopMediaCount;

        public Task StopMediaAsync(CancellationToken cancellation = default)
        {
            Interlocked.Increment(ref StopMediaCount);
            return Task.CompletedTask;
        }

        /// <summary>The speaker's own knob, as a real receiver treats it: what we set is where it
        /// goes, and where it goes is what it reports back.</summary>
        public double? DeviceVolume { get; private set; }

        public Task SetVolumeAsync(double level, CancellationToken cancellation = default)
        {
            DeviceVolume = level;
            return Task.CompletedTask;
        }

        /// <summary>Waits for the speaker to have been turned to <paramref name="level"/>.</summary>
        public bool WaitForVolume(double level, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (DeviceVolume is double at && Math.Abs(at - level) < 0.005) return true;
                Thread.Sleep(20);
            }
            return false;
        }

        /// <summary>Where the speaker's knob was sitting before anyone cast to it. Silent, because
        /// nothing happened: this is the state of the speaker, not an event.</summary>
        public void RestingVolume(double level) => DeviceVolume = level;

        /// <summary>The volume was turned somewhere else: the speaker's buttons, the Home app.</summary>
        public void TurnVolumeKnob(double level)
        {
            DeviceVolume = level;
            StatusChanged?.Invoke();
        }

        public void Dispose()
        {
            // Disposed twice on purpose in most tests: by the sink, which owns the connection, and
            // by the test's own `using`. Recording the close before tearing anything down keeps that
            // harmless, which is what a real receiver's Dispose is too.
            _connected = false;
            _closed.Set();

            _http.Dispose();
            _loaded.Dispose();
            _bodyDone.Dispose();
        }
    }
}
