using System.Buffers.Binary;

namespace RPM.Core.Ripping;

/// <summary>
/// A CD track's audio as a WAV file the player can open: 16-bit stereo at 44.1 kHz, little-endian.
///
/// <para>Playing a CD reads each track off the disc into one of these. The player opens the file while the
/// audio is still arriving, so the music starts a few seconds into the read and not at the end of it. For
/// that to work the file is laid out first, at its full length and with its true sizes (see
/// <see cref="Begin"/>), and the audio is then written into it in place (see <see cref="FillAsync"/>). The
/// decoder sees an ordinary finished file. A drive reads several times faster than the music plays, so the
/// decoder stays behind the writing.</para>
///
/// <para>The trap: the part of the file not written yet reads as silence, not as the end of the file. So
/// nothing may play audio that has not arrived (see <see cref="CdTrackFill"/>).</para>
///
/// <para>A file that the player has open cannot be renamed or rewritten on Windows, because the decoder
/// does not share it for that. This is why there is no temporary name, and why tags go in before the
/// audio. They also stand in front of the audio in the file, which RIFF allows. A tag written behind a
/// laid-out file makes Windows fill all the space before it, and that takes seconds on a long track.</para>
/// </summary>
public static class CdWav
{
    public const int SampleRate = 44100;
    public const int Channels = 2;
    public const int BitsPerSample = 16;
    public const int HeaderBytes = 44;

    /// <summary>Where the audio chunk's own eight bytes start in a header, after the format chunk.</summary>
    private const int DataChunkAt = 36;

    /// <summary>The audio in one CD sector.</summary>
    public const int BytesPerSector = 2352;

    /// <summary>Lays the file out: its header and tags, and room for <paramref name="audioBytes"/> of audio
    /// behind them. A small file beside it says the track is not finished, until <see cref="FillAsync"/> has
    /// filled it. An existing file is replaced.</summary>
    /// <param name="tag">Writes the tags, into the file at the path it is given. That file has no audio in
    /// it, so this is quick. The tags it writes are moved in front of the audio. Null leaves the file
    /// untagged.</param>
    /// <returns>Where the audio starts in the file. <see cref="FillAsync"/> needs it.</returns>
    public static long Begin(string path, long audioBytes, Action<string>? tag = null)
    {
        File.WriteAllBytes(UnfinishedMark(path), []);

        File.WriteAllBytes(path, Header(0));
        tag?.Invoke(path);
        var written = File.ReadAllBytes(path);

        // The audio chunk must start on an even byte, and the tag writer does not promise to end on one.
        var tagBytes = written.Length - HeaderBytes;
        var padded = tagBytes + tagBytes % 2;
        long audioStart = HeaderBytes + padded;

        using var file = new FileStream(path, FileMode.Create, FileAccess.Write, FileShare.ReadWrite);
        file.Write(written, 0, DataChunkAt);
        file.Write(written, HeaderBytes, tagBytes);
        file.Write(new byte[padded - tagBytes]);
        file.Write(written, DataChunkAt, HeaderBytes - DataChunkAt);
        file.SetLength(audioStart + audioBytes);
        WriteSizes(file, audioStart, audioBytes);
        return audioStart;
    }

    /// <summary>Whether the file holds its whole track. False for a file that a run stopped filling,
    /// including a run that ended with the app.</summary>
    public static bool IsWhole(string path) => File.Exists(path) && !File.Exists(UnfinishedMark(path));

    /// <summary>Writes the audio into a file that <see cref="Begin"/> laid out. Other programs may read the
    /// file while this runs. The audio need not arrive in order: <paramref name="read"/> gets the file itself
    /// and can move about in it, starting at <paramref name="audioStart"/>. The file keeps the length it was
    /// laid out with.
    ///
    /// <para>If <paramref name="read"/> fails or is cancelled, the file is cut where the stream's position is
    /// at that moment and its sizes are made to agree. A player already in the file then plays what there is
    /// and reaches a true end, not a stretch of silence. A read that moved about must put the position after
    /// the last audio it wrote before it throws. The file stays marked as unfinished.</para>
    /// </summary>
    /// <param name="audioStart">What <see cref="Begin"/> returned.</param>
    /// <param name="audioBytes">The length given to <see cref="Begin"/>.</param>
    /// <param name="read">Writes the track's raw audio to the stream it is given, and returns when all of it
    /// is in.</param>
    /// <param name="bigEndian">Whether <paramref name="read"/> writes its samples big-endian. They are
    /// turned round when all of them are in, because WAV is little-endian, so such a file cannot be played
    /// until this returns.</param>
    public static async Task FillAsync(string path, long audioStart, long audioBytes, Func<Stream, Task> read,
        bool bigEndian, CancellationToken ct = default)
    {
        // No buffer: a reader of the file must see each block as soon as the caller reports it written.
        await using var file = new FileStream(path, FileMode.Open, FileAccess.ReadWrite, FileShare.ReadWrite,
            bufferSize: 0);
        file.Position = audioStart;

        try
        {
            await read(file);
        }
        catch
        {
            Cut(file, audioStart, file.Position - audioStart);
            throw;
        }

        if (bigEndian)
            await SwapAsync(file, audioStart, audioBytes, ct);

        File.Delete(UnfinishedMark(path));
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

    private static string UnfinishedMark(string path) => path + ".reading";

    /// <summary>Ends the file after <paramref name="audioBytes"/> of audio.</summary>
    private static void Cut(FileStream file, long audioStart, long audioBytes)
    {
        file.SetLength(audioStart + audioBytes);
        WriteSizes(file, audioStart, audioBytes);
    }

    /// <summary>Writes the two sizes a WAV carries: the RIFF chunk's, which is everything after its first
    /// eight bytes, and the audio chunk's, which stands just before the audio.</summary>
    private static void WriteSizes(FileStream file, long audioStart, long audioBytes)
    {
        Span<byte> size = stackalloc byte[4];

        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)(audioStart - 8 + audioBytes));
        file.Position = 4;
        file.Write(size);

        BinaryPrimitives.WriteUInt32LittleEndian(size, (uint)audioBytes);
        file.Position = audioStart - 4;
        file.Write(size);
    }

    /// <summary>Turns the 16-bit samples of the audio round, in place.</summary>
    private static async Task SwapAsync(FileStream file, long audioStart, long audioBytes, CancellationToken ct)
    {
        var buffer = new byte[1 << 16];
        var end = audioStart + audioBytes;
        file.Position = audioStart;
        while (file.Position < end)
        {
            var at = file.Position;
            var got = await file.ReadAsync(buffer.AsMemory(0, (int)Math.Min(buffer.Length, end - at)), ct);
            if (got <= 0)
                break;
            for (var i = 0; i + 1 < got; i += 2)
                (buffer[i], buffer[i + 1]) = (buffer[i + 1], buffer[i]);
            file.Position = at;
            await file.WriteAsync(buffer.AsMemory(0, got), ct);
        }
    }
}
