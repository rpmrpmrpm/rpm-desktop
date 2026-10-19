using RPM.Core.Metadata;
using RPM.Core.Models;

namespace RPM.Core.Tests;

public class FilenameGuesserTests
{
    private static TrackInfo Untagged(string fileName)
        => new() { FilePath = $"/music/{fileName}", Extension = Path.GetExtension(fileName).ToLowerInvariant() };

    [Fact]
    public void Guesses_artist_dash_title()
    {
        var t = FilenameGuesser.Enrich(Untagged("Muse - Hysteria.mp3"));
        Assert.Equal("Muse", t.Artist);
        Assert.Equal("Hysteria", t.Title);
        Assert.Equal(MetadataSource.FilenameGuess, t.Source);
    }

    [Fact]
    public void Guesses_track_artist_title()
    {
        var t = FilenameGuesser.Enrich(Untagged("08 - Muse - Hysteria.mp3"));
        Assert.Equal("Muse", t.Artist);
        Assert.Equal("Hysteria", t.Title);
        Assert.Equal(8u, t.TrackNumber);
    }

    [Fact]
    public void Guesses_track_dot_title_without_artist()
    {
        var t = FilenameGuesser.Enrich(Untagged("03. Starlight.mp3"));
        Assert.Null(t.Artist);
        Assert.Equal("Starlight", t.Title);
        Assert.Equal(3u, t.TrackNumber);
    }

    [Fact]
    public void Underscores_become_spaces()
    {
        var t = FilenameGuesser.Enrich(Untagged("Muse_-_Hysteria.mp3"));
        Assert.Equal("Muse", t.Artist);
        Assert.Equal("Hysteria", t.Title);
    }

    [Fact]
    public void Tagged_files_pass_through_unchanged()
    {
        var tagged = Untagged("random.mp3") with { Title = "Hysteria", Artist = "Muse", Source = MetadataSource.Tags };
        Assert.Same(tagged, FilenameGuesser.Enrich(tagged));
    }

    [Fact]
    public void Unparseable_names_stay_unknown()
    {
        var t = FilenameGuesser.Enrich(Untagged("AUD_20240101.mp3"));
        Assert.Equal(MetadataSource.Unknown, t.Source);
        Assert.False(t.HasUsableMetadata);
    }
}
