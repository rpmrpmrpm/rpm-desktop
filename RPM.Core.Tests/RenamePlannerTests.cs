using RPM.Core.Models;
using RPM.Core.Import;

namespace RPM.Core.Tests;

public class RenamePlannerTests : IDisposable
{
    private readonly string _dest;

    public RenamePlannerTests()
    {
        _dest = Path.Combine(Path.GetTempPath(), $"mdtest-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_dest);
    }

    public void Dispose()
    {
        try { Directory.Delete(_dest, recursive: true); } catch { }
    }

    private static TrackInfo Track(string path, string title, string artist = "Muse", string album = "Absolution", uint track = 1)
        => new()
        {
            FilePath = path,
            Extension = Path.GetExtension(path).ToLowerInvariant(),
            Title = title,
            Artist = artist,
            Album = album,
            TrackNumber = track,
            Source = MetadataSource.Tags,
        };

    [Fact]
    public void Plans_simple_rename()
    {
        var plan = RenamePlanner.Plan(
            [Track("/src/a.mp3", "Hysteria", track: 8)],
            NamingTemplate.ArtistAlbumNumberedTitle,
            _dest);

        var s = Assert.Single(plan);
        Assert.Equal("Muse/Absolution/08 - Hysteria.mp3", s.ProposedRelativePath);
        Assert.False(s.IsAlreadyInPlace);
    }

    [Fact]
    public void Duplicate_targets_within_plan_get_numbered()
    {
        var plan = RenamePlanner.Plan(
            [
                Track("/src/a.mp3", "Hysteria", track: 8),
                Track("/src/b.mp3", "Hysteria", track: 8),
            ],
            NamingTemplate.ArtistAlbumNumberedTitle,
            _dest);

        Assert.Equal("Muse/Absolution/08 - Hysteria.mp3", plan[0].ProposedRelativePath);
        Assert.Equal("Muse/Absolution/08 - Hysteria (2).mp3", plan[1].ProposedRelativePath);
    }

    [Fact]
    public void Existing_file_on_disk_forces_numbering()
    {
        string existing = Path.Combine(_dest, "Muse", "Absolution", "08 - Hysteria.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(existing)!);
        File.WriteAllText(existing, "x");

        var plan = RenamePlanner.Plan(
            [Track("/src/a.mp3", "Hysteria", track: 8)],
            NamingTemplate.ArtistAlbumNumberedTitle,
            _dest);

        Assert.Equal("Muse/Absolution/08 - Hysteria (2).mp3", Assert.Single(plan).ProposedRelativePath);
    }

    [Fact]
    public void File_already_at_target_is_marked_in_place()
    {
        string inPlace = Path.Combine(_dest, "Muse", "Absolution", "08 - Hysteria.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(inPlace)!);
        File.WriteAllText(inPlace, "x");

        var plan = RenamePlanner.Plan(
            [Track(inPlace, "Hysteria", track: 8)],
            NamingTemplate.ArtistAlbumNumberedTitle,
            _dest);

        var s = Assert.Single(plan);
        Assert.True(s.IsAlreadyInPlace);
        Assert.Equal("Muse/Absolution/08 - Hysteria.mp3", s.ProposedRelativePath);
    }
}
