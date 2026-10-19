using System.Diagnostics;
using RPM.Core.Cloud;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The blob is shared and therefore anonymous, so everything that makes a track *this user's* copy
/// lives in the sidecar. What has to hold: upload a tagged file, throw the file away, download the
/// anonymous blob, apply the sidecar, and get the same track back, art and id included.
/// </summary>
public class TagSidecarTests : IDisposable
{
    private readonly string _root;

    public TagSidecarTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"sidecar-{Guid.NewGuid():N}");
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

    private string? Encode(string relativePath)
    {
        var path = Path.Combine(_root, relativePath);
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
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
        psi.ArgumentList.Add("-c:a"); psi.ArgumentList.Add("libmp3lame");
        psi.ArgumentList.Add(path);
        using var proc = Process.Start(psi)!;
        proc.WaitForExit();
        return proc.ExitCode == 0 ? path : null;
    }

    private static TrackInfo FullyTagged(string path, string id) => new()
    {
        FilePath = path,
        Extension = ".mp3",
        Title = "Bitter Sweet Symphony",
        Artist = "The Verve",
        AlbumArtist = "The Verve",
        Album = "Urban Hymns",
        Genre = "Britpop",
        Subgenre = "Shoegaze",
        TrackNumber = 1,
        TrackCount = 13,
        DiscNumber = 1,
        DiscCount = 1,
        Year = 1997,
        Composers = "Richard Ashcroft",
        Comment = "the one with the strings",
        Grouping = "90s",
        Bpm = 86,
        Copyright = "Hut Records",
        Publisher = "Virgin",
        Isrc = "GBAAA9700123",
        PendingArt = FakeJpeg(),
        PendingArtMime = "image/jpeg",
        RpmId = id,
    };

    /// <summary>
    /// Every tag field TrackInfo has, set to something distinguishable. Deliberately not a subset: the
    /// parity test below reflects over TrackInfo and fails if any property here is left at its
    /// default. So adding a field to the model and forgetting this fixture is caught here and not
    /// later by a user whose downloaded record comes back without its composer.
    /// </summary>
    private static TrackInfo EveryField(string path, string id) => new()
    {
        FilePath = path,
        Extension = ".mp3",
        Title = "I. Allegro con brio",
        Artist = "Berliner Philharmoniker",
        AlbumArtist = "Ludwig van Beethoven",
        Album = "Symphonies 1-9",
        Genre = "Classical",
        Subgenre = "Chamber Music",
        TrackNumber = 5,
        TrackCount = 37,
        DiscNumber = 2,
        DiscCount = 5,
        DiscSubtitle = "Symphony No. 5",
        Year = 1963,
        Composers = "Ludwig van Beethoven",
        Conductor = "Herbert von Karajan",
        Comment = "the 1963 cycle",
        // One line: an mp3's lyrics frame normalises newlines, and that is not what this is testing.
        Lyrics = "no words",
        Grouping = "Symphony cycle",
        Bpm = 108,
        Copyright = "1963 Deutsche Grammophon",
        Publisher = "Deutsche Grammophon",
        RemixedBy = "Nobody",
        Isrc = "DEA016300123",
        MusicalKey = "Cm",
        ReleaseCountry = "DE",
        Advisory = ContentAdvisory.Clean,

        TitleSort = "Allegro con brio",
        ArtistSort = "Berliner Philharmoniker",
        AlbumArtistSort = "Beethoven, Ludwig van",
        ComposerSort = "Beethoven, Ludwig van",

        Work = "Symphony No. 5 in C minor, Op. 67",
        MovementName = "I. Allegro con brio",
        MovementNumber = 1,
        MovementCount = 4,
        Lyricist = "Friedrich Schiller",
        Arranger = "Gustav Mahler",
        Ensemble = "Berliner Philharmoniker",
        Soloists = "Martha Argerich (piano)",

        Barcode = "0028947758815",
        CatalogNumber = "477 5881",
        MusicBrainzDiscId = "xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-",
        Media = "CD",
        ReleaseType = RPM.Core.Library.ReleaseType.Live,
        OriginalYear = 1963,

        MusicBrainzRecordingId = "aaaaaaaa-0000-4000-8000-000000000001",
        MusicBrainzReleaseId = "aaaaaaaa-0000-4000-8000-000000000002",
        MusicBrainzReleaseGroupId = "aaaaaaaa-0000-4000-8000-000000000003",
        MusicBrainzArtistId = "aaaaaaaa-0000-4000-8000-000000000004",
        MusicBrainzReleaseArtistId = "aaaaaaaa-0000-4000-8000-000000000005",

        PendingArt = FakeJpeg(),
        PendingArtMime = "image/jpeg",
        RpmId = id,
    };

    /// <summary>
    /// What is deliberately not the sidecar's business.
    ///
    /// <para>The path and extension belong to the restore, and the stream facts (channels, sample
    /// rate, bit depth, bitrate, codec) are read back from the downloaded blob, which has the same
    /// bytes. Carrying them would store an answer the file already gives. The rest is runtime state
    /// or derived from a field that IS carried.</para>
    /// </summary>
    private static readonly HashSet<string> NotCarried =
    [
        nameof(TrackInfo.FilePath), nameof(TrackInfo.Extension),
        nameof(TrackInfo.Channels), nameof(TrackInfo.SampleRate), nameof(TrackInfo.BitsPerSample),
        nameof(TrackInfo.BitrateKbps), nameof(TrackInfo.CodecDescription),
        nameof(TrackInfo.Availability), nameof(TrackInfo.Source), nameof(TrackInfo.IdentificationScore),
        nameof(TrackInfo.TagsEdited), nameof(TrackInfo.PendingArt), nameof(TrackInfo.PendingArtMime),
        nameof(TrackInfo.HasArt), nameof(TrackInfo.IsExplicit), nameof(TrackInfo.HasUsableMetadata),
        // Carried, but not a tag: it comes off the stream, so the fixture cannot state it and the
        // sweep below would read it as an unpopulated field. Asserted on its own instead.
        nameof(TrackInfo.DurationSeconds),
        // How loud the recording is. Not carried, for the same reason as the stream facts above: it
        // is measured FROM the blob, and the blob is what a restore downloads, so carrying it would
        // store an answer the bytes already give. A restored track is measured again by the
        // background pass, or arrives with the reading the catalogue already holds.
        nameof(TrackInfo.LoudnessLufs), nameof(TrackInfo.TruePeak),
        // Where the bytes are: this is the library's own bookkeeping and not anything the file says
        // about itself. The sidecar travels WITH the upload, so carrying it would write down an
        // answer that restoring changes anyway. A restored track's state is whatever the catalogue
        // row says once it arrives.
        nameof(TrackInfo.CloudState), nameof(TrackInfo.CloudSelected), nameof(TrackInfo.CloudInSet),
        // And what went wrong putting it there: bookkeeping about THIS machine's attempt and nothing
        // about the recording. A track restored onto another computer starts that process from
        // scratch, with its own ffmpeg and its own result.
        nameof(TrackInfo.CloudPartialError), nameof(TrackInfo.CloudAttempts),
        // The listener's own mark. Carried by the sidecar (v5) but never read from the FILE: no
        // container has a field for it, and writing one person's taste into a shared encoding is the
        // reason it lives on the row. It is put on the sidecar from the library row on the way up, and
        // is asserted by its own test below and not by this sweep, which reads a written file back.
        nameof(TrackInfo.IsFavorite),
    ];

    /// <summary>The mark the file cannot carry: it goes on the sidecar from the row, and comes back off
    /// it when a restore rebuilds the track.</summary>
    [Fact]
    public void The_sidecar_carries_the_listeners_favorite_mark()
    {
        var sidecar = new TagSidecar { RelativePath = "A/One/01.flac", Extension = ".flac" }
            .WithFavorite(favorite: true, favoritedUtcTicks: 4242);

        var back = TagSidecar.FromJson(sidecar.ToJson())!;

        Assert.True(back.Favorite);
        Assert.Equal(4242, back.FavoritedUtcTicks);
        Assert.True(back.ToTrackInfo("/tmp/01.flac").IsFavorite);
    }

    /// <summary>
    /// Parity, checked by reflection and not by memory.
    ///
    /// <para>The sidecar is the only record of a cloud-only track's tags, and it is rebuilt by hand in
    /// three places: the record's own properties, <c>FromFile</c> and <c>ToTrackInfo</c>. A field
    /// added to TrackInfo and missed in any one of them is silently dropped. So the test asks the
    /// model what its fields are and does not list them: add a property to TrackInfo, and either
    /// carry it or say here why it is not carried.</para>
    /// </summary>
    [Fact]
    public void The_sidecar_carries_every_tag_field_TrackInfo_has()
    {
        if (!FfmpegAvailable) return;

        var path = Encode("Karajan/Beethoven/05 Allegro.mp3")!;
        var wanted = EveryField(path, RpmTag.New());
        TagReader.WriteAll(path, wanted);

        var back = TagSidecar.FromFile(path, _root).ToTrackInfo(path);

        // The one carried field the fixture cannot state, because the audio decides it.
        Assert.Equal(TagReader.Read(path).DurationSeconds, back.DurationSeconds, precision: 3);

        var missing = new List<string>();
        var unset = new List<string>();
        foreach (var property in typeof(TrackInfo).GetProperties())
        {
            if (NotCarried.Contains(property.Name) || !property.CanRead)
                continue;

            var expected = property.GetValue(wanted);
            // A field the fixture forgot to populate would pass this test for the wrong reason. Null
            // is the default for every reference type; a value type's default has to be constructed,
            // and only a value type can be (string has no parameterless constructor).
            var unpopulated = property.PropertyType.IsValueType
                ? Activator.CreateInstance(property.PropertyType)
                : null;
            if (expected is null || Equals(expected, unpopulated))
            {
                unset.Add(property.Name);
                continue;
            }
            if (!Equals(expected, property.GetValue(back)))
                missing.Add($"{property.Name}: expected {expected}, got {property.GetValue(back) ?? "null"}");
        }

        Assert.True(unset.Count == 0,
            $"EveryField does not set: {string.Join(", ", unset)} — populate it or add it to NotCarried");
        Assert.True(missing.Count == 0,
            $"the sidecar drops {missing.Count} field(s):{Environment.NewLine}{string.Join(Environment.NewLine, missing)}");
    }

    [Fact]
    public async Task A_track_survives_the_round_trip_through_the_cloud()
    {
        if (!FfmpegAvailable) return;

        // What the user has.
        var original = Encode("The Verve/Urban Hymns/01 Bitter Sweet Symphony.mp3")!;
        var id = RpmTag.New();
        TagReader.WriteAll(original, FullyTagged(original, id));

        // Upload: the anonymous blob plus the private sidecar.
        var sidecar = TagSidecar.FromFile(original, _root);
        var canonical = await CanonicalAudio.CreateAsync(original, _root);

        // The two halves cross the wire as bytes and JSON.
        var blobBytes = await File.ReadAllBytesAsync(canonical.Path);
        var sidecarJson = sidecar.ToJson();

        // The local file is offloaded, gone.
        File.Delete(original);
        Assert.False(File.Exists(original));

        // Restore: write the blob back where the row says it belongs, then re-apply the tags.
        var restoredSidecar = TagSidecar.FromJson(sidecarJson)!;
        var target = Path.Combine(_root, restoredSidecar.RelativePath.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(target)!);
        await File.WriteAllBytesAsync(target, blobBytes);
        restoredSidecar.ApplyTo(target);

        var back = TagReader.Read(target);
        Assert.Equal("Bitter Sweet Symphony", back.Title);
        Assert.Equal("The Verve", back.Artist);
        Assert.Equal("Urban Hymns", back.Album);
        Assert.Equal("Britpop", back.Genre);
        Assert.Equal(1u, back.TrackNumber);
        Assert.Equal(13u, back.TrackCount);
        Assert.Equal(1997u, back.Year);
        Assert.Equal("Richard Ashcroft", back.Composers);
        Assert.Equal("the one with the strings", back.Comment);
        Assert.Equal(86u, back.Bpm);
        Assert.Equal("GBAAA9700123", back.Isrc);
        Assert.True(back.HasArt);

        // The id is the row's identity: a restore that minted a new one would orphan the row.
        Assert.Equal(id, back.RpmId);

        // And it lands back where it was, not in some flat dump.
        Assert.Equal(
            "The Verve/Urban Hymns/01 Bitter Sweet Symphony.mp3",
            restoredSidecar.RelativePath);
    }

    [Fact]
    public async Task The_restored_audio_is_bit_identical_to_what_was_uploaded()
    {
        if (!FfmpegAvailable) return;
        var original = Encode("a/song.mp3")!;
        TagReader.WriteAll(original, FullyTagged(original, RpmTag.New()));

        var canonical = await CanonicalAudio.CreateAsync(original, _root);

        // Re-tagging the downloaded blob must not disturb the audio it carries: canonicalising the
        // restored file has to give the same hash the blob was stored under.
        var restored = Path.Combine(_root, "restored.mp3");
        File.Copy(canonical.Path, restored);
        TagSidecar.FromFile(original, _root).ApplyTo(restored);

        var again = await CanonicalAudio.CreateAsync(restored, _root);
        Assert.Equal(canonical.Sha256, again.Sha256);
    }

    [Fact]
    public void Art_survives_the_json()
    {
        var art = FakeJpeg();
        var sidecar = new TagSidecar
        {
            RelativePath = "a/b.mp3",
            Extension = ".mp3",
            Art = art,
            ArtMime = "image/jpeg",
            Title = "x",
        };

        var back = TagSidecar.FromJson(sidecar.ToJson())!;
        Assert.Equal(art, back.Art);
        Assert.Equal("image/jpeg", back.ArtMime);
    }

    [Fact]
    public void A_file_with_no_art_carries_none()
    {
        if (!FfmpegAvailable) return;
        var path = Encode("plain.mp3")!;
        var sidecar = TagSidecar.FromFile(path, _root);
        Assert.Null(sidecar.Art);
        Assert.Null(sidecar.ArtMime);
    }

    [Fact]
    public void Corrupt_json_is_null_not_an_exception()
        => Assert.Null(TagSidecar.FromJson("{ not json"));

    [Fact]
    public void What_the_file_is_survives_the_json()
    {
        // The catalogue's format line is built from these, and a phone has no file to read them off.
        var sidecar = new TagSidecar
        {
            RelativePath = "a/b.flac",
            Extension = ".flac",
            Channels = 6,
            SampleRate = 96000,
            BitsPerSample = 24,
            BitrateKbps = 4608,
            CodecDescription = "Flac Audio",
        };

        var back = TagSidecar.FromJson(sidecar.ToJson())!;

        Assert.Equal(6, back.Channels);
        Assert.Equal(96000, back.SampleRate);
        Assert.Equal(24, back.BitsPerSample);
        Assert.Equal(4608, back.BitrateKbps);
        Assert.Equal("Flac Audio", back.CodecDescription);
    }

    [Fact]
    public void A_v2_sidecar_still_reads_and_simply_knows_no_format()
    {
        var back = TagSidecar.FromJson(
            """{"Version":2,"RelativePath":"a/b.mp3","Extension":".mp3","Title":"x"}""")!;

        Assert.Equal(2, back.Version);
        Assert.Equal(0, back.Channels);
        Assert.Null(back.CodecDescription);
    }

    // ── an edit to a track whose file is not on this machine ──

    private static TagSidecar Stored() => new()
    {
        RelativePath = "Old Artist/Old Album/01 - old.flac",
        Extension = ".flac",
        Title = "Old title",
        Artist = "Old Artist",
        AlbumArtist = "Old Artist",
        Album = "Old Album",
        TrackNumber = 1,
        Year = 1999,
        Lyrics = "kept",
        Isrc = "GBAAA0000001",
        Art = FakeJpeg(),
        ArtMime = "image/jpeg",
    };

    private static RPM.Core.Library.LibraryTrack Row(
        string relativePath, string? title, string? artist, string? album, uint year = 0)
    {
        var cut = relativePath.LastIndexOf('/');
        return new RPM.Core.Library.LibraryTrack
        {
            RelativePath = relativePath,
            Folder = cut < 0 ? "" : relativePath[..cut],
            FileName = cut < 0 ? relativePath : relativePath[(cut + 1)..],
            Extension = ".flac",
            Title = title,
            Artist = artist,
            AlbumArtist = artist,
            Album = album,
            Year = year,
        };
    }

    [Fact]
    public void A_correction_reaches_the_sidecar_and_leaves_the_rest_alone()
    {
        var patched = Stored().With(Row("New Artist/New Album/01 - new.flac", "New title", "New Artist", "New Album"));

        Assert.Equal("New title", patched.Title);
        Assert.Equal("New Artist", patched.Artist);
        Assert.Equal("New Artist", patched.AlbumArtist);
        Assert.Equal("New Album", patched.Album);
        Assert.Equal("New Artist/New Album/01 - new.flac", patched.RelativePath);

        // A correction, not a rewrite from a thinner source: what the mirror does not carry survives.
        Assert.Equal("kept", patched.Lyrics);
        Assert.Equal("GBAAA0000001", patched.Isrc);
        Assert.NotNull(patched.Art);
        Assert.Equal(1999u, patched.Year);     // the row says nothing about the year
        Assert.Equal(1u, patched.TrackNumber);
    }

    [Fact]
    public void A_sidecar_that_already_agrees_is_not_rewritten()
    {
        var sidecar = Stored();

        Assert.True(sidecar.Matches(
            Row("Old Artist/Old Album/01 - old.flac", "Old title", "Old Artist", "Old Album", 1999)));

        Assert.False(sidecar.Matches(
            Row("Old Artist/Old Album/01 - old.flac", "New title", "Old Artist", "Old Album", 1999)));

        // A re-file with the same tags is a change too: it is where a download would put the file.
        Assert.False(sidecar.Matches(
            Row("Old Artist/Old Album (1999)/01 - old.flac", "Old title", "Old Artist", "Old Album", 1999)));
    }

    [Fact]
    public void An_explicit_mark_made_on_a_cloud_only_row_reaches_its_sidecar()
    {
        var row = Row("Old Artist/Old Album/01 - old.flac", "Old title", "Old Artist", "Old Album", 1999)
            with { Advisory = ContentAdvisory.Explicit };

        Assert.False(Stored().Matches(row));
        Assert.Equal(ContentAdvisory.Explicit, Stored().With(row).Advisory);
    }

    [Fact]
    public void A_row_that_says_nothing_about_its_lyrics_keeps_the_sidecars_flag()
    {
        var stored = Stored() with { Advisory = ContentAdvisory.Explicit };
        var silent = Row("Old Artist/Old Album/01 - old.flac", "Old title", "Old Artist", "Old Album", 1999);

        Assert.True(stored.Matches(silent));
        Assert.True(stored.Matches(silent with { Advisory = ContentAdvisory.Unknown }));
        Assert.Equal(ContentAdvisory.Explicit, stored.With(silent with { Advisory = ContentAdvisory.Unknown }).Advisory);
    }

    [Fact]
    public void A_classical_edit_made_on_a_cloud_only_row_reaches_its_sidecar()
    {
        var row = Row("Old Artist/Old Album/01 - old.flac", "Old title", "Old Artist", "Old Album", 1999) with
        {
            Composer = "Ludwig van Beethoven",
            Work = "Symphony No. 7 in A major, Op. 92",
            MovementName = "Allegretto",
            MovementNumber = 2,
        };

        Assert.False(Stored().Matches(row));
        var patched = Stored().With(row);
        Assert.Equal("Ludwig van Beethoven", patched.Composers);
        Assert.Equal("Symphony No. 7 in A major, Op. 92", patched.Work);
        Assert.Equal("Allegretto", patched.MovementName);
        Assert.Equal(2u, patched.MovementNumber);
        Assert.True(patched.Matches(row));
    }

    private static byte[] FakeJpeg()
    {
        var bytes = new byte[512];
        bytes[0] = 0xFF; bytes[1] = 0xD8; bytes[2] = 0xFF; bytes[3] = 0xE0;
        bytes[^2] = 0xFF; bytes[^1] = 0xD9;
        return bytes;
    }
}
