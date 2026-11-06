using RPM.Core.Import;

namespace RPM.Core.Tests;

public class StarterMusicTests : IDisposable
{
    private readonly string _root = Path.Combine(Path.GetTempPath(), "rpm-starter-tests-" + Guid.NewGuid().ToString("N"));

    private string App => Path.Combine(_root, "app");
    private string Unpacked => Path.Combine(_root, "unpacked");

    public void Dispose()
    {
        if (Directory.Exists(_root))
            Directory.Delete(_root, recursive: true);
    }

    private void Ship(string file, string bytes = "audio")
    {
        var sounds = Path.Combine(App, "Assets", "Sounds");
        Directory.CreateDirectory(sounds);
        File.WriteAllText(Path.Combine(sounds, file), bytes);
    }

    [Fact]
    public void A_build_without_the_files_offers_nothing()
    {
        Assert.False(StarterMusic.IsShipped(App));
        Assert.Equal(0, StarterMusic.Unpack(App, Unpacked));
    }

    [Fact]
    public void The_shipped_files_are_copied_and_left_where_they_ship()
    {
        Ship("starter-connect.mp3");
        Ship("starter-gymnopedie.ogg");
        Ship("starter-moonlight.ogg");

        Assert.True(StarterMusic.IsShipped(App));
        Assert.Equal(3, StarterMusic.Unpack(App, Unpacked));

        // One folder each, because the wizard's preview finds a sleeve per source folder.
        Assert.Equal(3, Directory.GetDirectories(Unpacked).Length);
        Assert.Equal(3, Directory.GetFiles(Unpacked, "*", SearchOption.AllDirectories).Length);
        Assert.Equal(3, Directory.GetFiles(Path.Combine(App, "Assets", "Sounds")).Length);
    }

    [Fact]
    public void A_record_with_disc_art_has_it_beside_the_track_under_the_name_the_import_carries()
    {
        Ship("starter-moonlight.ogg");
        Ship("starter-moonlight-disc.png", "picture");

        Assert.Equal(1, StarterMusic.Unpack(App, Unpacked));

        var disc = FolderArtWriter.FindDiscImage(Path.Combine(Unpacked, "starter-moonlight"));
        Assert.NotNull(disc);
        Assert.Equal("picture", File.ReadAllText(disc));
    }

    [Fact]
    public void Unpacking_again_replaces_what_an_earlier_try_left()
    {
        Ship("starter-connect.mp3", "fresh");
        var earlier = Path.Combine(Unpacked, "starter-connect", "starter-connect.mp3");
        Directory.CreateDirectory(Path.GetDirectoryName(earlier)!);
        File.WriteAllText(earlier, "tagged by an import");

        Assert.Equal(1, StarterMusic.Unpack(App, Unpacked));

        Assert.Equal("fresh", File.ReadAllText(earlier));
    }

    [Fact]
    public void Clearing_takes_the_copies_away()
    {
        Ship("starter-connect.mp3");
        StarterMusic.Unpack(App, Unpacked);

        StarterMusic.Clear(Unpacked);
        StarterMusic.Clear(Unpacked);

        Assert.False(Directory.Exists(Unpacked));
        Assert.True(StarterMusic.IsShipped(App));
    }
}
