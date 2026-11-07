using System.Text;
using RPM.Core.Metadata;

namespace RPM.Core.Tests;

/// <summary>
/// ID3v2.3 uses a slash to separate the values of a genre frame, so a shelf label with a slash in it comes
/// back from TagLib as two genres. These files are written by hand because a file RPM saves may land as
/// ID3v2.4, which has no such rule, and would pass without the reader doing anything.
/// </summary>
public class TagReaderSlashedGenreTests : IDisposable
{
    private readonly string _root;

    public TagReaderSlashedGenreTests()
    {
        _root = Path.Combine(Path.GetTempPath(), $"rpmslash-{Guid.NewGuid():N}");
        Directory.CreateDirectory(_root);
    }

    public void Dispose()
    {
        try { Directory.Delete(_root, recursive: true); } catch { }
    }

    /// <summary>An MP3 with an ID3v2.3 tag that holds one genre frame.</summary>
    private string Mp3WithGenreFrame(string genre)
    {
        var text = Encoding.Latin1.GetBytes(genre);
        var size = text.Length + 1;   // the encoding byte

        using var tag = new MemoryStream();
        tag.Write("TCON"u8);
        tag.Write([(byte)(size >> 24), (byte)(size >> 16), (byte)(size >> 8), (byte)size, 0, 0, 0]);
        tag.Write(text);

        var body = (int)tag.Length;
        var path = Path.Combine(_root, $"{Guid.NewGuid():N}.mp3");
        using (var file = File.Create(path))
        {
            file.Write("ID3"u8);
            file.Write([3, 0, 0,
                (byte)((body >> 21) & 0x7F), (byte)((body >> 14) & 0x7F), (byte)((body >> 7) & 0x7F), (byte)(body & 0x7F)]);
            file.Write(tag.ToArray());

            var frame = new byte[417];
            frame[0] = 0xFF; frame[1] = 0xFB; frame[2] = 0x90; frame[3] = 0x00;
            file.Write(frame);
        }
        return path;
    }

    [Theory]
    [InlineData("Hip-Hop / Rap")]
    [InlineData("Electronic / Dance")]
    [InlineData("Other / Unknown")]
    public void A_shelf_with_a_slash_survives_an_id3v23_file(string shelf)
        => Assert.Equal(shelf, TagReader.Read(Mp3WithGenreFrame(shelf)).Genre);

    [Fact]
    public void Two_real_genres_in_an_id3v23_file_still_read_as_the_first()
        => Assert.Equal("Rock", TagReader.Read(Mp3WithGenreFrame("Rock/Pop")).Genre);

    [Fact]
    public void A_compound_label_that_is_not_a_shelf_is_not_put_back_together()
        => Assert.Equal("Funk", TagReader.Read(Mp3WithGenreFrame("Funk / Soul")).Genre);
}
