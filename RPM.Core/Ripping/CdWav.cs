using System.Buffers.Binary;

namespace RPM.Core.Ripping;

/// <summary>
/// A CD track's audio as a WAV file the player can open: 16-bit stereo at 44.1 kHz, little-endian.
///
/// <para>Playing a CD reads each track off the disc into one of these, a track ahead of the music, and plays
/// the file. A drive reads many times faster than it plays, so the next track is ready long before it is
/// wanted, and the player needs nothing it does not already have.</para>
/// </summary>
public static class CdWav
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int HeaderBytes = 44;

    /// <summary>Writes the file: a header, the audio <paramref name="read"/> puts after it, and the sizes the
    /// header could not know until the audio was in.</summary>
    /// <param name="read">Writes the track's raw audio to the stream it is given.</param>
    /// <param name="bigEndian">Whether <paramref name="read"/> writes its samples big-endian, as a Mac's
    /// reader does. They are turned round, because WAV is little-endian.</param>
    public static async Task WriteAsync(string path, Func<Stream, Task> read, bool bigEndian, CancellationToken ct = default)
    {
        await using (var file = new FileStream(path, FileMode.Create, FileAccess.ReadWrite, FileShare.Read))
        {
            await file.WriteAsync(Header(0), ct);
            await read(file);

            var audio = file.Length - HeaderBytes;
            if (bigEndian)
                await SwapAsync(file, ct);

            file.Position = 0;
            await file.WriteAsync(Header(audio), ct);
        }
    }

    /// <summary>The 44-byte RIFF header for <paramref name="audioBytes"/> of audio.</summary>
    public static byte[] Header(long audioBytes)
    {
        var data = (uint)Math.Clamp(audioBytes, 0, uint.MaxValue - 36);
        var h = new byte[HeaderBytes];
        "RIFF"u8.CopyTo(h);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(4), 36 + data);
        "WAVEfmt "u8.CopyTo(h.AsSpan(8));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(16), 16);                 // the fmt chunk's size
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(20), 1);                  // PCM
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(22), Channels);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(24), SampleRate);
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(28), SampleRate * Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(32), Channels * BitsPerSample / 8);
        BinaryPrimitives.WriteUInt16LittleEndian(h.AsSpan(34), BitsPerSample);
        "data"u8.CopyTo(h.AsSpan(36));
        BinaryPrimitives.WriteUInt32LittleEndian(h.AsSpan(40), data);
        return h;
    }

    /// <summary>Turns every 16-bit sample after the header round, in place.</summary>
    private static async Task SwapAsync(FileStream file, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        file.Position = HeaderBytes;
        while (true)
        {
            var at = file.Position;
            var got = await file.ReadAsync(buffer, ct);
            if (got <= 0)
                break;
            for (var i = 0; i + 1 < got; i += 2)
                (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
            file.Position = at;
            await file.WriteAsync(buffer.AsMemory(0, got), ct);
        }
    }
}
