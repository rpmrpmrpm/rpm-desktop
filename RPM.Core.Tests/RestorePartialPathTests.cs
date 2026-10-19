using RPM.Core.Cloud;
using RPM.Core.Scanning;

namespace RPM.Core.Tests;

/// <summary>
/// The name a download has while it is still arriving.
///
/// <para>
/// The user's tags are written onto the partial file before it is moved into place, and TagLib
/// chooses its reader by extension. A partial called "…flac.part" would fail with "(taglib/part)"
/// after the whole transfer, so the partial must keep the real extension. That has a second trap:
/// a file called "29 - Song.flac" in an album folder is a track to anything that scans the library,
/// half-downloaded or not.
/// </para>
/// </summary>
public class RestorePartialPathTests
{
    private const string Target = "/music/ABBA/The Definitive Collection/29 - The Day Before You Came.flac";

    [Fact]
    public void The_partial_keeps_the_real_extension()
    {
        // What the tag writer needs: the extension it reads the container from must be the last thing
        // on the name.
        Assert.Equal(".flac", Path.GetExtension(RpmCloudClient.PartialPathFor(Target)));
    }

    [Fact]
    public void The_partial_is_hidden_from_the_scanner()
    {
        var name = Path.GetFileName(RpmCloudClient.PartialPathFor(Target));

        // The scanner skips dot-prefixed files. Without that, keeping the extension would mean that a
        // half-downloaded album imports itself while it is still arriving.
        Assert.StartsWith(".", name);
        Assert.Contains(Path.GetExtension(Target), AudioFileScanner.AudioExtensions);
    }

    [Fact]
    public void The_partial_sits_beside_its_destination()
    {
        // Same folder, so putting it in place at the end is a rename and not a copy across volumes.
        // This is what makes an interrupted download leave nothing behind.
        Assert.Equal(
            Path.GetDirectoryName(Target),
            Path.GetDirectoryName(RpmCloudClient.PartialPathFor(Target)));
    }

    [Fact]
    public void Two_tracks_get_two_partials()
    {
        var one = RpmCloudClient.PartialPathFor("/music/Band/Record/01 One.flac");
        var two = RpmCloudClient.PartialPathFor("/music/Band/Record/02 Two.flac");

        Assert.NotEqual(one, two);
    }

    [Theory]
    [InlineData("/music/Band/Record/01 One.mp3", ".mp3")]
    [InlineData("/music/Band/Record/01 One.m4a", ".m4a")]
    [InlineData("/music/Band/Record/01 One.ogg", ".ogg")]
    public void Every_format_keeps_the_extension_its_reader_is_chosen_by(string target, string extension)
        => Assert.Equal(extension, Path.GetExtension(RpmCloudClient.PartialPathFor(target)));
}
