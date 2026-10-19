using System.Diagnostics;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The extended tag surface: the fields TagLib# has no property for, across the containers that
/// carry them differently.
///
/// <para>MP3 always runs: it can be written by hand and exercises both ID3 paths, the registered
/// frame (<c>TMED</c>, <c>TSST</c>) and the <c>TXXX</c> user-text fallback that everything else
/// uses. FLAC and M4A need ffmpeg to produce a container that TagLib accepts, and are skipped
/// silently without it, the same arrangement as <see cref="AlbumFileSplitterTests"/>. M4A is the
/// one worth the subprocess: its atoms are the only carrier where the field name is raw bytes and
/// the numbers are integers and not digits, so it is the only place the encoding can go wrong.</para>
/// </summary>
public class TagCarrierTests : IDisposable
{
    private readonly string _root;

    public TagCarrierTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"carrier-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    // ── fixtures ──

    private static byte[] MinimalMp3()
    {
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        return frame;
    }

    private string Mp3(string name = "test.mp3")
    {
        var path = Path.Combine(_root, name);
        File.WriteAllBytes(path, MinimalMp3());
        return path;
    }

    private static bool HaveFfmpeg
    {
        get
        {
            try
            {
                using var p = Process.Start(new ProcessStartInfo("ffmpeg", "-version")
                {
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                });
                p!.WaitForExit(5000);
                return p.ExitCode == 0;
            }
            catch { return false; }
        }
    }

    /// <summary>A second of silence in whatever container the extension names.</summary>
    private string? Encoded(string name)
    {
        if (!HaveFfmpeg)
            return null;

        var path = Path.Combine(_root, name);
        using var p = Process.Start(new ProcessStartInfo("ffmpeg",
            $"-hide_banner -loglevel error -f lavfi -i anullsrc=r=44100:cl=stereo -t 1 -y \"{path}\"")
        {
            RedirectStandardOutput = true,
            RedirectStandardError = true,
        });
        p!.WaitForExit(30000);
        return p.ExitCode == 0 && File.Exists(path) ? path : null;
    }

    /// <summary>Every extended field set at once, so one write proves the whole block.</summary>
    private static TrackInfo Full(string path) => new()
    {
        FilePath = path,
        Extension = Path.GetExtension(path),
        Title = "Allegro con brio",
        Artist = "Berliner Philharmoniker",
        AlbumArtist = "Ludwig van Beethoven",
        Album = "Symphonies 1-9",

        TitleSort = "Allegro con brio",
        ArtistSort = "Berliner Philharmoniker",
        AlbumArtistSort = "Beethoven, Ludwig van",
        ComposerSort = "Beethoven, Ludwig van",
        Composers = "Ludwig van Beethoven",

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
        OriginalYear = 1963,
        MusicalKey = "Cm",
        Genre = "Classical",
        Subgenre = "Chamber Music",

        MusicBrainzRecordingId = "b1a9c0e6-0000-4000-8000-000000000001",
        MusicBrainzReleaseId = "b1a9c0e6-0000-4000-8000-000000000002",
        MusicBrainzReleaseGroupId = "b1a9c0e6-0000-4000-8000-000000000003",
        MusicBrainzArtistId = "b1a9c0e6-0000-4000-8000-000000000004",
        MusicBrainzReleaseArtistId = "b1a9c0e6-0000-4000-8000-000000000005",
    };

    private static void AssertFull(TrackInfo read)
    {
        Assert.Equal("Beethoven, Ludwig van", read.AlbumArtistSort);
        Assert.Equal("Beethoven, Ludwig van", read.ComposerSort);
        Assert.Equal("Berliner Philharmoniker", read.ArtistSort);
        Assert.Equal("Allegro con brio", read.TitleSort);

        Assert.Equal("Symphony No. 5 in C minor, Op. 67", read.Work);
        Assert.Equal("I. Allegro con brio", read.MovementName);
        Assert.Equal(1u, read.MovementNumber);
        Assert.Equal(4u, read.MovementCount);
        Assert.Equal("Friedrich Schiller", read.Lyricist);
        Assert.Equal("Gustav Mahler", read.Arranger);
        Assert.Equal("Berliner Philharmoniker", read.Ensemble);
        Assert.Equal("Martha Argerich (piano)", read.Soloists);

        Assert.Equal("0028947758815", read.Barcode);
        Assert.Equal("477 5881", read.CatalogNumber);
        Assert.Equal("xd_R.uPBg8Y0Cd0KAyaGrsJZ5c-", read.MusicBrainzDiscId);
        Assert.Equal("CD", read.Media);
        Assert.Equal(1963u, read.OriginalYear);
        Assert.Equal("Cm", read.MusicalKey);
        Assert.Equal("Chamber Music", read.Subgenre);

        Assert.Equal("b1a9c0e6-0000-4000-8000-000000000001", read.MusicBrainzRecordingId);
        Assert.Equal("b1a9c0e6-0000-4000-8000-000000000002", read.MusicBrainzReleaseId);
        Assert.Equal("b1a9c0e6-0000-4000-8000-000000000003", read.MusicBrainzReleaseGroupId);
        Assert.Equal("b1a9c0e6-0000-4000-8000-000000000004", read.MusicBrainzArtistId);
        Assert.Equal("b1a9c0e6-0000-4000-8000-000000000005", read.MusicBrainzReleaseArtistId);
    }

    // ── per container ──

    [Fact]
    public void Mp3_roundtrips_the_whole_extended_block()
    {
        var path = Mp3();
        TagReader.WriteAll(path, Full(path));
        AssertFull(TagReader.Read(path));
    }

    [Fact]
    public void Flac_roundtrips_the_whole_extended_block()
    {
        if (Encoded("test.flac") is not { } path)
            return;
        TagReader.WriteAll(path, Full(path));
        AssertFull(TagReader.Read(path));
    }

    [Fact]
    public void M4a_roundtrips_the_whole_extended_block()
    {
        if (Encoded("test.m4a") is not { } path)
            return;
        TagReader.WriteAll(path, Full(path));
        AssertFull(TagReader.Read(path));
    }

    // ── the parts that are easy to get wrong ──

    /// <summary>ID3 packs the movement and its total into one <c>MVIN</c> frame, the way it packs a
    /// track number into <c>TRCK</c>. Writing the two halves through separate carriers would let the
    /// second erase the first, which MovementTag exists to prevent.</summary>
    [Fact]
    public void Id3_keeps_both_halves_of_the_movement()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            Work = "Die Kunst der Fuge", MovementName = "Contrapunctus I",
            MovementNumber = 3, MovementCount = 14,
        });

        var read = TagReader.Read(path);
        Assert.Equal(3u, read.MovementNumber);
        Assert.Equal(14u, read.MovementCount);

        using var file = TagLib.File.Create(path);
        Assert.Equal("3/14", ExtendedTags.MovementNumber.Read(file));
    }

    /// <summary>A movement with no known total is "3", not "3/0".</summary>
    [Fact]
    public void Id3_movement_without_a_total_is_a_bare_number()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            Work = "W", MovementName = "M", MovementNumber = 2,
        });

        using var file = TagLib.File.Create(path);
        Assert.Equal("2", ExtendedTags.MovementNumber.Read(file));
        Assert.Equal(2u, TagReader.Read(path).MovementNumber);
    }

    /// <summary>The show-movement flag is derived, never edited: a file naming both a work and a
    /// movement gets it, and one that stops naming them loses it.</summary>
    [Fact]
    public void Show_movement_follows_the_work_and_movement()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            Work = "W", MovementName = "M",
        });
        using (var file = TagLib.File.Create(path))
            Assert.Equal("1", ExtendedTags.ShowMovement.Read(file));

        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
        });
        using (var file = TagLib.File.Create(path))
            Assert.Null(ExtendedTags.ShowMovement.Read(file));
    }

    /// <summary>A work with no movement is a whole piece in one file: a real title, and not a "Work: "
    /// prefix with nothing after the colon.</summary>
    [Fact]
    public void A_work_without_a_movement_does_not_claim_to_show_one()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
            Work = "Boléro",
        });

        Assert.Equal("Boléro", TagReader.Read(path).Work);
        using var file = TagLib.File.Create(path);
        Assert.Null(ExtendedTags.ShowMovement.Read(file));
    }

    /// <summary>Every extended field clears when the user empties it. WriteAll states the whole wanted
    /// side, so a field left blank means blank, not "leave whatever was there".</summary>
    [Fact]
    public void WriteAll_clears_every_extended_field()
    {
        var path = Mp3();
        TagReader.WriteAll(path, Full(path));
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "T", Artist = "A",
        });

        var read = TagReader.Read(path);
        Assert.Null(read.Work);
        Assert.Null(read.MovementName);
        Assert.Equal(0u, read.MovementNumber);
        Assert.Equal(0u, read.MovementCount);
        Assert.Null(read.Lyricist);
        Assert.Null(read.Arranger);
        Assert.Null(read.Ensemble);
        Assert.Null(read.Soloists);
        Assert.Null(read.Barcode);
        Assert.Null(read.CatalogNumber);
        Assert.Null(read.MusicBrainzDiscId);
        Assert.Null(read.Media);
        Assert.Equal(0u, read.OriginalYear);
        Assert.Null(read.AlbumArtistSort);
        Assert.Null(read.MusicBrainzRecordingId);
    }

    /// <summary>A partial write (what recognition does) must not wipe the extended fields it says
    /// nothing about. This is the whole difference between Write and WriteAll, and the extended block
    /// has to implement it separately because it does not go through TagLib's properties.</summary>
    [Fact]
    public void Merge_write_leaves_extended_fields_alone()
    {
        var path = Mp3();
        TagReader.WriteAll(path, Full(path));

        TagReader.Write(path, new TrackInfo
        {
            FilePath = path, Extension = ".mp3", Title = "Corrected", Artist = "Corrected",
        });

        var read = TagReader.Read(path);
        Assert.Equal("Corrected", read.Title);
        AssertFull(read);
    }

    // ── the style ──

    /// <summary>The style is its own field, so the genre tag stays exactly one word. Writing it as a
    /// second entry in the genre list is the cheap way to do this, and it makes every other player
    /// show the record as "Classical; Chamber Music".</summary>
    [Fact]
    public void The_style_does_not_land_in_the_genre_tag()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo
        {
            FilePath = path,
            Extension = ".mp3",
            Genre = "House",
            Subgenre = "Deep House",
        });

        using var file = TagLib.File.Create(path);
        Assert.Equal(["House"], file.Tag.Genres);
        Assert.Equal("Deep House", ExtendedTags.Subgenre.Read(file));

        var read = TagReader.Read(path);
        Assert.Equal("House", read.Genre);
        Assert.Equal("Deep House", read.Subgenre);
    }

    /// <summary>The taggers that pull from Discogs write styles into a STYLE field, and a library that
    /// arrives carrying one already has the answer in it. So it is read. This is worth a test,
    /// because nothing else would notice if it stopped working.</summary>
    [Fact]
    public void A_style_another_tagger_wrote_is_read()
    {
        var path = Mp3();
        using (var file = TagLib.File.Create(path))
        {
            ExtendedTags.Style.Write(file, "Deep House");
            file.Save();
        }

        Assert.Equal("Deep House", TagReader.Read(path).Subgenre);
    }

    /// <summary>RPM's own field wins when a file carries both: it is the one RPM wrote, so it is the
    /// one that was edited.</summary>
    [Fact]
    public void Our_own_field_wins_over_the_other_tools()
    {
        var path = Mp3();
        using (var file = TagLib.File.Create(path))
        {
            ExtendedTags.Style.Write(file, "Tech House");
            ExtendedTags.Subgenre.Write(file, "Deep House");
            file.Save();
        }

        Assert.Equal("Deep House", TagReader.Read(path).Subgenre);
    }

    /// <summary>And the other tool's field is never written, because RPM's list is its own: what RPM
    /// means by a style is one of 394 names with a shelf attached, and writing that into somebody
    /// else's field would mean RPM deciding what that field means.</summary>
    [Fact]
    public void The_other_tools_field_is_never_written()
    {
        var path = Mp3();
        TagReader.WriteAll(path, new TrackInfo { FilePath = path, Extension = ".mp3", Subgenre = "Deep House" });

        using var file = TagLib.File.Create(path);
        Assert.Equal("Deep House", ExtendedTags.Subgenre.Read(file));
        Assert.Null(ExtendedTags.Style.Read(file));
    }
}
