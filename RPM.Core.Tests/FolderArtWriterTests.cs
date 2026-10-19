using RPM.Core.Import;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class FolderArtWriterTests : IDisposable
{
    private readonly string _dir;

    public FolderArtWriterTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"mdart-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void FindFolderImage_returns_null_when_none()
    {
        Assert.Null(FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindFolderImage_finds_any_known_name()
    {
        var path = Path.Combine(_dir, "folder.png");
        File.WriteAllBytes(path, [1, 2, 3]);

        Assert.Equal(path, FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindFolderImage_prefers_cover_over_folder()
    {
        File.WriteAllBytes(Path.Combine(_dir, "folder.jpg"), [1]);
        var cover = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(cover, [2]);

        Assert.Equal(cover, FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindDiscImage_returns_null_when_none()
    {
        Assert.Null(FolderArtWriter.FindDiscImage(_dir));
    }

    [Fact]
    public void WriteDisc_writes_disc_png_and_find_locates_it()
    {
        FolderArtWriter.WriteDisc(_dir, [4, 5, 6]);

        var disc = Path.Combine(_dir, "disc.png");
        Assert.True(File.Exists(disc));
        Assert.Equal(disc, FolderArtWriter.FindDiscImage(_dir));
    }

    [Fact]
    public void WriteDisc_does_not_overwrite_existing_disc()
    {
        var disc = Path.Combine(_dir, "disc.png");
        File.WriteAllBytes(disc, [1]);

        FolderArtWriter.WriteDisc(_dir, [9, 9, 9]);

        Assert.Equal([1], File.ReadAllBytes(disc));   // the existing disc art is left alone
    }

    [Fact]
    public void ReplaceDisc_overwrites_an_existing_disc_png()
    {
        var disc = Path.Combine(_dir, "disc.png");
        File.WriteAllBytes(disc, [1]);

        Assert.True(FolderArtWriter.ReplaceDisc(_dir, [9, 9, 9]));

        // Unlike WriteDisc, this is the user explicitly putting this picture on this record.
        Assert.Equal([9, 9, 9], File.ReadAllBytes(disc));
    }

    [Fact]
    public void ReplaceDisc_clears_a_disc_jpg_that_would_otherwise_win_the_lookup()
    {
        var stale = Path.Combine(_dir, "disc.jpg");
        File.WriteAllBytes(stale, [1]);

        Assert.True(FolderArtWriter.ReplaceDisc(_dir, [9]));

        Assert.False(File.Exists(stale));
        Assert.Equal(Path.Combine(_dir, "disc.png"), FolderArtWriter.FindDiscImage(_dir));
    }

    [Fact]
    public void ReplaceDisc_writes_nothing_for_empty_bytes()
    {
        Assert.False(FolderArtWriter.ReplaceDisc(_dir, []));
        Assert.Null(FolderArtWriter.FindDiscImage(_dir));
    }

    [Fact]
    public void DeleteCover_takes_every_spelling_of_the_folder_image()
    {
        File.WriteAllBytes(Path.Combine(_dir, "cover.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "folder.jpg"), [2]);

        Assert.True(FolderArtWriter.DeleteCover(_dir));

        // Not just the first one found: the next name down the order would otherwise become the cover.
        Assert.Null(FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void DeleteCover_reports_nothing_removed_when_there_was_none()
    {
        Assert.False(FolderArtWriter.DeleteCover(_dir));
    }

    [Fact]
    public void DeleteDisc_takes_every_spelling_of_the_disc_image()
    {
        File.WriteAllBytes(Path.Combine(_dir, "disc.png"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "disc.jpg"), [2]);

        Assert.True(FolderArtWriter.DeleteDisc(_dir));

        // Not just the first one found: the next name down the order would otherwise become the art.
        Assert.Null(FolderArtWriter.FindDiscImage(_dir));
    }

    [Fact]
    public void DeleteDisc_reports_nothing_removed_when_there_was_none()
    {
        Assert.False(FolderArtWriter.DeleteDisc(_dir));
    }

    [Fact]
    public void CarryDiscImages_copies_disc_from_staging_to_final_folder()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "disc.png"), [7]);

        FolderArtWriter.CarryDiscImages([
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = true,
            },
        ]);

        Assert.Equal([7], File.ReadAllBytes(Path.Combine(dest, "disc.png")));
    }

    [Fact]
    public void CarryDiscImages_does_not_overwrite_a_disc_already_in_the_destination()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "disc.png"), [7]);
        File.WriteAllBytes(Path.Combine(dest, "disc.png"), [1]);

        FolderArtWriter.CarryDiscImages([
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = true,
            },
        ]);

        Assert.Equal([1], File.ReadAllBytes(Path.Combine(dest, "disc.png")));
    }

    // ── the full-size folder cover: the tier the track files deliberately do not carry ──

    [Fact]
    public void WriteFolderCover_writes_cover_jpg_and_find_locates_it()
    {
        FolderArtWriter.WriteFolderCover(_dir, [4, 5, 6], "image/jpeg");

        var cover = Path.Combine(_dir, "cover.jpg");
        Assert.True(File.Exists(cover));
        Assert.Equal(cover, FolderArtWriter.FindFolderImage(_dir));
    }

    /// <summary>A PNG scan keeps its extension. Naming it .jpg would leave a file whose bytes and name
    /// disagree, and every reader of the file would have to guess the real format.</summary>
    [Fact]
    public void WriteFolderCover_keeps_a_png_a_png()
    {
        FolderArtWriter.WriteFolderCover(_dir, [4, 5, 6], "image/png");

        Assert.True(File.Exists(Path.Combine(_dir, "cover.png")));
        Assert.False(File.Exists(Path.Combine(_dir, "cover.jpg")));
    }

    /// <summary>An unknown or missing mime falls back to jpg, which is what the archives serve.</summary>
    [Fact]
    public void WriteFolderCover_falls_back_to_jpg_without_a_mime()
    {
        FolderArtWriter.WriteFolderCover(_dir, [4, 5, 6], null);

        Assert.True(File.Exists(Path.Combine(_dir, "cover.jpg")));
    }

    /// <summary>Unlike the album-menu action, this one is automatic. A cover already in the folder is
    /// one somebody chose, and a rip must not overwrite it.</summary>
    [Fact]
    public void WriteFolderCover_does_not_overwrite_an_existing_folder_image()
    {
        var existing = Path.Combine(_dir, "folder.jpg");
        File.WriteAllBytes(existing, [1]);

        FolderArtWriter.WriteFolderCover(_dir, [9, 9, 9], "image/jpeg");

        Assert.Equal([1], File.ReadAllBytes(existing));
        Assert.False(File.Exists(Path.Combine(_dir, "cover.jpg")));
    }

    [Fact]
    public void WriteFolderCover_ignores_empty_bytes()
    {
        FolderArtWriter.WriteFolderCover(_dir, [], "image/jpeg");

        Assert.Null(FolderArtWriter.FindFolderImage(_dir));
    }

    /// <summary>The scan is staged next to the ripped tracks and must move with them at import: the
    /// organiser moves only audio files, and would leave it behind.</summary>
    [Fact]
    public void CarryCoverImages_copies_the_staged_scan_to_the_final_folder()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "cover.jpg"), [7]);

        FolderArtWriter.CarryCoverImages([
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = true,
            },
        ]);

        Assert.Equal([7], File.ReadAllBytes(Path.Combine(dest, "cover.jpg")));
    }

    [Fact]
    public void CarryCoverImages_does_not_overwrite_a_cover_already_in_the_destination()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "cover.jpg"), [7]);
        File.WriteAllBytes(Path.Combine(dest, "cover.jpg"), [1]);

        FolderArtWriter.CarryCoverImages([
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = true,
            },
        ]);

        Assert.Equal([1], File.ReadAllBytes(Path.Combine(dest, "cover.jpg")));
    }

    /// <summary>A failed move must not move its folder image: the tracks it belongs to are not
    /// there.</summary>
    [Fact]
    public void CarryCoverImages_ignores_failed_results()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "cover.jpg"), [7]);

        FolderArtWriter.CarryCoverImages([
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = false,
            },
        ]);

        Assert.Null(FolderArtWriter.FindFolderImage(dest));
    }

    /// <summary>The order the importer runs them in, in one place: the moved scan is written first, and
    /// WriteCovers (which fills a folder from a track's embedded copy) then finds one already there
    /// and leaves it. In the other order, every ripped album would get the smaller tier on disk.</summary>
    [Fact]
    public void A_carried_scan_survives_the_embedded_art_pass()
    {
        var src = Path.Combine(_dir, "staging");
        var dest = Path.Combine(_dir, "library");
        Directory.CreateDirectory(src);
        Directory.CreateDirectory(dest);
        File.WriteAllBytes(Path.Combine(src, "cover.jpg"), [7]);

        ApplyResult[] results = [
            new ApplyResult
            {
                SourcePath = Path.Combine(src, "01.flac"),
                DestinationPath = Path.Combine(dest, "01.flac"),
                Success = true,
            },
        ];

        FolderArtWriter.CarryCoverImages(results);
        FolderArtWriter.WriteCovers(results);

        Assert.Equal([7], File.ReadAllBytes(Path.Combine(dest, "cover.jpg")));
    }

    [Fact]
    public void FindFolderImage_knows_the_names_other_rippers_wrote()
    {
        var front = Path.Combine(_dir, "front.jpg");
        File.WriteAllBytes(front, [1]);

        Assert.Equal(front, FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindFolderImage_takes_the_one_picture_in_the_folder_whatever_it_is_called()
    {
        // A collection from another source often has these: the sleeve is next to the tracks under a
        // non-standard name.
        var scan = Path.Combine(_dir, "Dark Side of the Moon.jpeg");
        File.WriteAllBytes(scan, [1]);

        Assert.Equal(scan, FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindFolderImage_will_not_guess_between_two_unnamed_pictures()
    {
        File.WriteAllBytes(Path.Combine(_dir, "scan1.jpg"), [1]);
        File.WriteAllBytes(Path.Combine(_dir, "scan2.jpg"), [2]);

        Assert.Null(FolderArtWriter.FindFolderImage(_dir));
    }

    [Fact]
    public void FindFolderImage_does_not_mistake_the_record_for_its_sleeve()
    {
        // disc.png is the label on the record itself, drawn on the turntable. A folder holding only
        // that has no cover.
        File.WriteAllBytes(Path.Combine(_dir, "disc.png"), [1]);

        Assert.Null(FolderArtWriter.FindFolderImage(_dir));
    }

    /// <summary>
    /// An offloaded or adopted record is a row with no folder behind it, and the album wall asks for
    /// its cover like any other. The answer is null, and (the main check here) it is reached without
    /// throwing. Enumerating the missing folder and catching the error would cost a first-chance
    /// exception per cloud record every time the shelf draws.
    /// </summary>
    [Fact]
    public void FindFolderImage_asks_nothing_of_a_folder_that_is_not_there()
    {
        var missing = Path.Combine(_dir, "gone");
        var thrown = 0;

        void Count(object? _, System.Runtime.ExceptionServices.FirstChanceExceptionEventArgs e)
        {
            if (e.Exception is DirectoryNotFoundException && e.Exception.Message.Contains("gone"))
                Interlocked.Increment(ref thrown);
        }

        AppDomain.CurrentDomain.FirstChanceException += Count;
        try
        {
            Assert.Null(FolderArtWriter.FindFolderImage(missing));
        }
        finally
        {
            AppDomain.CurrentDomain.FirstChanceException -= Count;
        }

        Assert.Equal(0, thrown);
    }

    [Fact]
    public void FindFolderImage_prefers_a_named_cover_to_a_lone_scan()
    {
        var cover = Path.Combine(_dir, "cover.jpg");
        File.WriteAllBytes(Path.Combine(_dir, "back scan.png"), [1]);
        File.WriteAllBytes(cover, [2]);

        Assert.Equal(cover, FolderArtWriter.FindFolderImage(_dir));
    }
}
