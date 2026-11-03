using System.Buffers.Binary;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>A CD track read off the disc into a file the player opens: a RIFF header with the sizes filled in
/// once the audio is in, and the samples little-endian whichever way the reader gave them.</summary>
public class CdWavTests
{
    [Theory]
    [InlineData(false, new byte[] { 0x01, 0x02, 0x03, 0x04 })]
    [InlineData(true, new byte[] { 0x02, 0x01, 0x04, 0x03 })]
    public async Task The_file_is_a_wav_of_the_audio_little_endian(bool bigEndian, byte[] expected)
    {
        var path = Path.Combine(Path.GetTempPath(), $"cdwav-{Guid.NewGuid():N}.wav");
        try
        {
            await CdWav.WriteAsync(path, into => into.WriteAsync(new byte[] { 0x01, 0x02, 0x03, 0x04 }).AsTask(), bigEndian);

            var file = await File.ReadAllBytesAsync(path);
            Assert.Equal(CdWav.HeaderBytes + 4, file.Length);
            Assert.Equal("RIFF"u8.ToArray(), file[..4]);
            Assert.Equal(36u + 4, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(4)));
            Assert.Equal("data"u8.ToArray(), file[36..40]);
            Assert.Equal(4u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(40)));
            Assert.Equal(44100u, BinaryPrimitives.ReadUInt32LittleEndian(file.AsSpan(24)));
            Assert.Equal(expected, file[44..]);
        }
        finally
        {
            File.Delete(path);
        }
    }
}
