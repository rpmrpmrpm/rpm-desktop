using System.Diagnostics;
using System.Security.Cryptography;
using RPM.Core.Cloud;
using RPM.Core.Metadata;
using RPM.Core.Models;
using RPM.Fingerprint;

namespace RPM.Core.Tests;

/// <summary>
/// The whole cloud store rests on one claim: two users holding the same encoding of a track, tagged
/// differently, produce the same canonical bytes, and the audio survives the trip. Hand-built
/// fixtures cannot prove either, so these encode real files and are skipped without ffmpeg.
/// </summary>
public class CanonicalAudioTests : IDisposable
{
    private readonly string _root;

    public CanonicalAudioTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"canon-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    private static bool FfmpegAvailable
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("ffmpeg")
                {
                    RedirectStandardError = true,
                    RedirectStandardOutput = true,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                })!;
                p.WaitForExit();
                return true;
            }
            catch { return false; }
        }
    }

    /// <summary>A real encoded tone in the given container, or null when this ffmpeg cannot make one.</summary>
    private string? Encode(string name, string extension, string? codec = null, int seconds = 2)
    {
        var path = Path.Combine(_root, name + extension);
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
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add($"sine=frequency=440:duration={seconds}");
        if (codec is not null) { psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add(codec); }
        psi.ArgumentList.Add(path);

        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>The decoded samples, so "the audio survived" is asserted on the audio itself.</summary>
    private static byte[] DecodePcm(string path)
    {
        var psi = new ProcessStartInfo("ffmpeg")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
            CreateNoWindow = true,
        };
        psi.ArgumentList.Add("-loglevel"); psi.ArgumentList.Add("error");
        psi.ArgumentList.Add("-i"); psi.ArgumentList.Add(path);
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("s16le");
        psi.ArgumentList.Add("-ar"); psi.ArgumentList.Add("44100");
        psi.ArgumentList.Add("-ac"); psi.ArgumentList.Add("2");
        psi.ArgumentList.Add("-");

        using var proc = Process.Start(psi)!;
        using var mem = new MemoryStream();
        proc.StandardOutput.BaseStream.CopyTo(mem);
        _ = proc.StandardError.ReadToEnd();
        proc.WaitForExit();
        return mem.ToArray();
    }

    [Theory]
    [InlineData(".mp3", "libmp3lame")]
    [InlineData(".flac", "flac")]
    [InlineData(".m4a", "aac")]
    [InlineData(".wav", "pcm_s16le")]
    public async Task Two_users_tagging_the_same_encoding_differently_get_one_blob(string ext, string codec)
    {
        if (!FfmpegAvailable) return;
        var source = Encode("source", ext, codec);
        if (source is null) return; // this ffmpeg build cannot make the container

        // Two people, one download. Each library stamps its own id and each user tagged it their way.
        var userA = Path.Combine(_root, "a" + ext);
        var userB = Path.Combine(_root, "b" + ext);
        File.Copy(source, userA);
        File.Copy(source, userB);

        TagReader.WriteAll(userA, new TrackInfo
        {
            FilePath = userA, Extension = ext,
            Title = "Song", Artist = "Band", Album = "Record",
            Comment = "ripped by A", RpmId = RpmTag.New(),
        });
        TagReader.WriteAll(userB, new TrackInfo
        {
            FilePath = userB, Extension = ext,
            Title = "song (remaster)", Artist = "The Band", Album = "Record",
            Comment = "B's copy", Year = 1998, RpmId = RpmTag.New(),
        });

        // The naive design (hash the file) would store these twice.
        Assert.NotEqual(await CanonicalAudio.HashAsync(userA), await CanonicalAudio.HashAsync(userB));

        var a = await CanonicalAudio.CreateAsync(userA, _root);
        var b = await CanonicalAudio.CreateAsync(userB, _root);

        Assert.Equal(a.Sha256, b.Sha256);
        Assert.Equal(a.SizeBytes, b.SizeBytes);
    }

    /// <summary>
    /// A surround or Atmos download is Dolby Digital Plus inside an .m4a, and the muxer that
    /// extension picks (ffmpeg's "ipod") takes AAC, ALAC and AC-3 and nothing else. With that muxer,
    /// every track of such an album fails to sync with "Could not write header". The generic mp4 muxer
    /// takes it, so the second attempt is made with that one.
    /// </summary>
    [Fact]
    public async Task An_m4a_the_ipod_muxer_refuses_is_still_canonicalized()
    {
        if (!FfmpegAvailable) return;

        var source = Path.Combine(_root, "dolby.m4a");
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
        psi.ArgumentList.Add("-f"); psi.ArgumentList.Add("mp4");   // .m4a alone would be refused here too
        psi.ArgumentList.Add(source);
        using (var enc = Process.Start(psi)!)
        {
            enc.WaitForExit();
            if (enc.ExitCode != 0) return;   // this ffmpeg build has no E-AC-3 encoder
        }

        var canonical = await CanonicalAudio.CreateAsync(source, _root);

        Assert.True(new FileInfo(canonical.Path).Length > 0);
        Assert.Equal(DecodePcm(source), DecodePcm(canonical.Path));   // still a stream copy
    }

    /// <summary>The fallback is for the files that need it and nothing else: an ordinary AAC .m4a
    /// keeps the bytes (and so the hash, and so its place in the store) it has always had.</summary>
    [Fact]
    public async Task An_ordinary_m4a_still_hashes_the_way_it_always_did()
    {
        if (!FfmpegAvailable) return;
        var source = Encode("source", ".m4a", "aac");
        if (source is null) return;

        var canonical = await CanonicalAudio.CreateAsync(source, _root);

        // What the ipod muxer writes and what the mp4 muxer writes are different bytes; the brand in
        // the header alone differs. This asserts the file went the first way round.
        var head = new byte[12];
        using (var f = File.OpenRead(canonical.Path))
            f.ReadExactly(head);
        Assert.Equal("M4A ", System.Text.Encoding.ASCII.GetString(head, 8, 4));
    }

    [Fact]
    public async Task Canonicalizing_is_deterministic()
    {
        if (!FfmpegAvailable) return;
        var source = Encode("source", ".mp3", "libmp3lame")!;

        var first = await CanonicalAudio.CreateAsync(source, _root);
        var second = await CanonicalAudio.CreateAsync(source, _root);

        Assert.Equal(first.Sha256, second.Sha256);
        Assert.NotEqual(first.Path, second.Path);   // distinct temp files, same content
    }

    [Fact]
    public async Task The_audio_survives_canonicalization_untouched()
    {
        if (!FfmpegAvailable) return;
        var source = Encode("source", ".mp3", "libmp3lame")!;
        TagReader.WriteAll(source, new TrackInfo
        {
            FilePath = source, Extension = ".mp3",
            Title = "Song", Artist = "Band",
            PendingArt = FakeJpeg(), PendingArtMime = "image/jpeg",
            RpmId = RpmTag.New(),
        });

        var canonical = await CanonicalAudio.CreateAsync(source, _root);

        // Stream copy, not a re-encode: the samples must come back identical.
        Assert.Equal(DecodePcm(source), DecodePcm(canonical.Path));

        // ...and the tags, art included, are gone from the shared blob.
        var stripped = TagReader.Read(canonical.Path);
        Assert.Null(stripped.Title);
        Assert.Null(stripped.Artist);
        Assert.Null(stripped.RpmId);
        Assert.False(stripped.HasArt);
    }

    [Fact]
    public async Task Tagging_a_file_does_not_change_its_blob()
    {
        if (!FfmpegAvailable) return;
        var path = Encode("source", ".mp3", "libmp3lame")!;

        var before = await CanonicalAudio.CreateAsync(path, _root);

        // The user renames the track and drops a cover on it. The audio did not change, so the
        // upload must not either: only the sidecar needs rewriting.
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3",
            Title = "A new title", Artist = "Someone Else",
            PendingArt = FakeJpeg(), PendingArtMime = "image/jpeg",
            RpmId = RpmTag.New(),
        });

        var after = await CanonicalAudio.CreateAsync(path, _root);
        Assert.Equal(before.Sha256, after.Sha256);
    }

    [Fact]
    public async Task A_challenge_is_answerable_only_by_holding_the_bytes()
    {
        if (!FfmpegAvailable) return;
        var mine = Encode("mine", ".mp3", "libmp3lame")!;
        var theirs = Encode("theirs", ".mp3", "libmp3lame", seconds: 3)!;

        var nonce = RandomNumberGenerator.GetBytes(16);
        var ranges = new[] { (0L, 64), (128L, 256) };

        var expected = await CanonicalAudio.AnswerChallengeAsync(mine, nonce, ranges);
        var again = await CanonicalAudio.AnswerChallengeAsync(mine, nonce, ranges);
        var impostor = await CanonicalAudio.AnswerChallengeAsync(theirs, nonce, ranges);

        Assert.Equal(expected, again);        // the holder answers, repeatably
        Assert.NotEqual(expected, impostor);  // someone with a different file cannot

        // A different nonce gives a different answer, so a harvested one is worth nothing.
        var replayed = await CanonicalAudio.AnswerChallengeAsync(
            mine, RandomNumberGenerator.GetBytes(16), ranges);
        Assert.NotEqual(expected, replayed);
    }

    [Fact]
    public async Task A_range_past_the_end_is_refused_rather_than_silently_short()
    {
        if (!FfmpegAvailable) return;
        var path = Encode("source", ".mp3", "libmp3lame")!;
        long size = new FileInfo(path).Length;

        await Assert.ThrowsAsync<ArgumentOutOfRangeException>(() =>
            CanonicalAudio.AnswerChallengeAsync(path, [1, 2, 3], new[] { (size - 4, 64) }));
    }

    [Fact]
    public async Task A_file_ffmpeg_cannot_read_fails_loudly()
    {
        if (!FfmpegAvailable) return;
        var junk = Path.Combine(_root, "not-audio.mp3");
        await File.WriteAllTextAsync(junk, "this is not an mp3");

        var refused = await Assert.ThrowsAsync<FfmpegFailedException>(() =>
            CanonicalAudio.CreateAsync(junk, _root));

        // All of what ffmpeg said travels with it, for the log book and a report; the message keeps
        // only the short reading.
        Assert.NotEmpty(refused.Output);
        Assert.Contains("ffmpeg could not canonicalize 'not-audio.mp3'", refused.Message);
    }

    /// <summary>Smallest bytes TagLib will accept as a picture. The content is never decoded.</summary>
    private static byte[] FakeJpeg()
    {
        var bytes = new byte[512];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }
}
