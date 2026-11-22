using RPM.Core.Import;
using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

/// <summary>
/// The picture of a record's disc that came with its files: a scan beside them under a ripper's name, or a picture
/// inside them typed "Media". And the import's carry, which saves it as the album folder's disc.png, yielding to one
/// already there.
/// </summary>
public class DiscArtFinderTests : IDisposable
{
    private readonly string _dir;

    public DiscArtFinderTests()
    {
        _dir = Path.Combine(Path.GetTempPath(), $"discfind-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dir);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    private string Put(string relative, params byte[] bytes)
    {
        var path = Path.Combine(_dir, relative.Replace('/', Path.DirectorySeparatorChar));
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        File.WriteAllBytes(path, bytes.Length == 0 ? [1] : bytes);
        return path;
    }

    private string Mp3(string relative, byte[]? discPicture = null)
    {
        var path = Put(relative);
        var frame = new byte[417];
        frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
        File.WriteAllBytes(path, frame);
        if (discPicture is not null)
        {
            using var file = TagLib.File.Create(path);
            file.Tag.Pictures =
            [
                new TagLib.Picture(new TagLib.ByteVector([9, 9, 9])) { Type = TagLib.PictureType.FrontCover, MimeType = "image/jpeg" },
                new TagLib.Picture(new TagLib.ByteVector(discPicture)) { Type = TagLib.PictureType.Media, MimeType = "image/png" },
            ];
            file.Save();
        }
        return path;
    }

    [Theory]
    [InlineData("cd.jpg")]
    [InlineData("CD1.png")]
    [InlineData("Disc 1.jpg")]
    [InlineData("disk-2.jpeg")]
    [InlineData("cdart.png")]
    [InlineData("CD Art.jpg")]
    [InlineData("disc_label.png")]
    [InlineData("media.jpg")]
    public void A_scan_under_a_rippers_name_is_found(string name)
    {
        var scan = Put("Album/" + name);
        Assert.Equal(scan, DiscArtFinder.FindScan(Path.Combine(_dir, "Album")));
    }

    [Theory]
    [InlineData("cover.jpg")]
    [InlineData("back.jpg")]
    [InlineData("cd front.jpg")]
    [InlineData("discography.jpg")]
    [InlineData("cd.txt")]
    public void Other_pictures_are_not_taken_for_the_disc(string name)
    {
        Put("Album/" + name);
        Assert.Null(DiscArtFinder.FindScan(Path.Combine(_dir, "Album")));
    }

    [Fact]
    public void A_name_with_no_number_wins_then_the_lowest_number()
    {
        Put("A/cd2.jpg");
        var one = Put("A/cd1.jpg");
        Assert.Equal(one, DiscArtFinder.FindScan(Path.Combine(_dir, "A")));

        var bare = Put("A/cd.jpg");
        Assert.Equal(bare, DiscArtFinder.FindScan(Path.Combine(_dir, "A")));
    }

    [Fact]
    public void A_scans_folder_one_level_down_is_looked_in()
    {
        var scan = Put("Album/Scans/CD.jpg");
        Assert.Equal(scan, DiscArtFinder.FindScan(Path.Combine(_dir, "Album")));
    }

    [Fact]
    public void A_picture_inside_the_files_typed_media_is_the_disc_and_the_cover_never_is()
    {
        var plain = Mp3("A/01.mp3");
        var withDisc = Mp3("A/02.mp3", discPicture: [7, 7, 7]);

        Assert.Null(TagReader.LoadDiscArt(plain));
        Assert.Equal([7, 7, 7], TagReader.LoadDiscArt(withDisc)!.Value.Bytes);

        var found = DiscArtFinder.ForRecord([Path.Combine(_dir, "A")], [plain, withDisc]);
        Assert.NotNull(found);
        Assert.Equal(ArtOrigin.Files, found!.Origin);
        Assert.Equal([7, 7, 7], found.Bytes);
    }

    [Fact]
    public void A_scan_beside_the_files_comes_before_a_picture_inside_them()
    {
        Put("A/cd.jpg", 5, 5);
        var track = Mp3("A/01.mp3", discPicture: [7]);

        var found = DiscArtFinder.ForRecord([Path.Combine(_dir, "A")], [track]);
        Assert.Equal(ArtOrigin.Folder, found!.Origin);
        Assert.Equal([5, 5], found.Bytes);
    }

    private static ApplyResult Landed(string source, string destination)
        => new() { SourcePath = source, DestinationPath = destination, Success = true };

    [Fact]
    public void The_carry_saves_the_scan_as_the_album_folders_disc_png_through_the_cut()
    {
        Put("in/Album/cd.jpg", 1, 2, 3);
        var dest = Mp3("lib/Artist/Album/01.mp3");

        var written = DiscArtFinder.Carry(
            [Landed(Path.Combine(_dir, "in", "Album", "01.mp3"), dest)],
            bytes => [.. bytes, 4]);

        var folder = Path.Combine(_dir, "lib", "Artist", "Album");
        Assert.Equal([folder], written);
        Assert.Equal([1, 2, 3, 4], File.ReadAllBytes(Path.Combine(folder, "disc.png")));
    }

    [Fact]
    public void The_carry_yields_to_a_disc_already_there_and_keeps_a_picture_the_cut_cannot_read()
    {
        Put("in/A/cd.jpg", 1);
        var kept = Mp3("lib/A/01.mp3");
        Put("lib/A/disc.png", 8);

        Put("in/B/cd.jpg", 2);
        var fresh = Mp3("lib/B/01.mp3");

        var written = DiscArtFinder.Carry(
            [Landed(Path.Combine(_dir, "in", "A", "01.mp3"), kept),
             Landed(Path.Combine(_dir, "in", "B", "01.mp3"), fresh)],
            _ => throw new InvalidDataException("not a picture"));

        Assert.Equal([Path.Combine(_dir, "lib", "B")], written);
        Assert.Equal([8], File.ReadAllBytes(Path.Combine(_dir, "lib", "A", "disc.png")));
        Assert.Equal([2], File.ReadAllBytes(Path.Combine(_dir, "lib", "B", "disc.png")));
    }

    [Fact]
    public void A_found_disc_never_replaces_one_the_album_has()
    {
        Put("A/disc.jpg", 3);
        Assert.False(FolderArtWriter.WriteFoundDisc(Path.Combine(_dir, "A"), [4]));
        Assert.False(File.Exists(Path.Combine(_dir, "A", "disc.png")));

        Directory.CreateDirectory(Path.Combine(_dir, "B"));
        Assert.True(FolderArtWriter.WriteFoundDisc(Path.Combine(_dir, "B"), [4]));
        Assert.Equal([4], File.ReadAllBytes(Path.Combine(_dir, "B", "disc.png")));
    }
}
