using RPM.Core.Models;
using RPM.Core.Import;

namespace RPM.Core.Tests;

public sealed class StagingLayoutTests : IDisposable
{
    private readonly string _root =
        Path.Combine(Path.GetTempPath(), "rpm-staging-" + Guid.NewGuid().ToString("N"));

    public StagingLayoutTests() => Directory.CreateDirectory(_root);

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { /* best-effort cleanup */ }
    }

    private static TrackInfo Track(string title = "Airbag", uint number = 1, string ext = ".flac") => new()
    {
        FilePath = "",
        Extension = ext,
        Artist = "Radiohead",
        AlbumArtist = "Radiohead",
        Album = "OK Computer",
        Title = title,
        TrackNumber = number,
    };

    [Fact]
    public void Builds_the_template_folder_path_under_the_root()
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = StagingLayout.ResolvePath(_root, NamingTemplate.ArtistAlbumNumberedTitle, Track(), claimed);
        Assert.Equal(
            Path.GetFullPath(Path.Combine(_root, "Radiohead", "OK Computer", "01 - Airbag.flac")),
            path);
    }

    [Fact]
    public void Second_identical_render_is_de_duplicated()
    {
        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var first = StagingLayout.ResolvePath(_root, NamingTemplate.ArtistAlbumNumberedTitle, Track(), claimed);
        var second = StagingLayout.ResolvePath(_root, NamingTemplate.ArtistAlbumNumberedTitle, Track(), claimed);
        Assert.NotEqual(first, second);
        Assert.EndsWith("01 - Airbag (2).flac", second);
    }

    [Fact]
    public void Avoids_a_name_already_on_disk()
    {
        var folder = Path.Combine(_root, "Radiohead", "OK Computer");
        Directory.CreateDirectory(folder);
        File.WriteAllText(Path.Combine(folder, "01 - Airbag.flac"), "occupied");

        var claimed = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        var path = StagingLayout.ResolvePath(_root, NamingTemplate.ArtistAlbumNumberedTitle, Track(), claimed);
        Assert.EndsWith("01 - Airbag (2).flac", path);
    }
}
