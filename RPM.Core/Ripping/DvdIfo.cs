using System.Buffers.Binary;

namespace RPM.Core.Ripping;

/// <summary>
/// Reads the chapter structure out of a DVD title set's IFO file (<c>VTS_nn_0.IFO</c>).
///
/// This is a separate implementation, not a libdvdread dependency. libdvdread is GPL, and a commercial
/// app cannot link it. All we need is the program-chain table, which is a handful of offsets, not the
/// whole library.
///
/// The layout, as given in the DVD-Video specification:
/// <list type="bullet">
/// <item>The IFO opens with "DVDVIDEO-VTS" and a table of 4-byte SECTOR pointers; the program chain
/// table (VTS_PGCIT) is at byte 0xCC.</item>
/// <item>VTS_PGCIT lists N program chains (PGCs). A PGC is a title.</item>
/// <item>A PGC holds CELLS (the physical pieces) and PROGRAMS (the chapters). The program map says
/// which cell each chapter starts at. The cell table holds each cell's duration. A chapter's
/// duration is the sum of its cells' durations.</item>
/// <item>Times are BCD: hours, minutes, seconds, frames. The frame rate is in the top two bits of
/// the frame byte.</item>
/// </list>
/// </summary>
public static class DvdIfo
{
    private const int SectorSize = 2048;

    // Offsets within VTSI_MAT (the IFO header).
    private const int VtsPgcitPointer = 0xCC;

    // Offsets within a PGC.
    private const int PgcProgramCount = 0x02;
    private const int PgcCellCount = 0x03;
    private const int PgcPlaybackTime = 0x04;
    private const int PgcAudioControl = 0x0C;   // 8 entries, 2 bytes each: is this audio slot present?
    private const int PgcProgramMapOffset = 0xE6;
    private const int PgcCellPlaybackOffset = 0xE8;

    private const int CellPlaybackEntrySize = 0x18; // 24 bytes
    private const int CellPlaybackTime = 0x04;      // within a cell entry
    private const int CellFirstSector = 0x08;
    private const int CellLastSector = 0x14;

    // Offsets within VTSI_MAT for the audio attribute table.
    private const int VtsAudioStreamCount = 0x0202; // uint16
    private const int VtsAudioAttrTable = 0x0204;   // 8 bytes per stream, up to 8 streams
    private const int MaxAudioStreams = 8;

    /// <summary>True when the bytes look like a title-set IFO at all.</summary>
    public static bool IsTitleSetIfo(ReadOnlySpan<byte> ifo) =>
        ifo.Length >= 12 && ifo[..12].SequenceEqual("DVDVIDEO-VTS"u8);

    /// <summary>
    /// The title set's audio streams, read from the IFO's attribute table.
    ///
    /// This comes from the IFO on purpose: the IFO is never scrambled, so the audio can be
    /// listed even on a CSS disc whose VOBs ffprobe cannot read. The Nth stream here is
    /// ffmpeg's <c>0:a:N</c>, because DVD authoring and ffmpeg both order the substreams the same way.
    ///
    /// <para>
    /// The attribute table alone is NOT the answer. Trusting it puts streams in the picker that do not
    /// exist. The table has eight fixed slots. The count beside them (<c>VTS_AST_Ns</c>) is what the
    /// title set <i>declares</i>, not what is multiplexed. Some discs declare 8 streams but carry fewer. Their
    /// unused slots are left as identical filler, which the authoring tool never cleared. Choosing
    /// one of those would send ffmpeg after a stream that is not in the file. What decides is the
    /// program chain's audio-control table. Each of its eight slots says whether that stream is present.
    /// So the declared attributes are filtered through that table.
    /// </para>
    /// </summary>
    public static IReadOnlyList<DvdAudioStream> ReadAudioStreams(ReadOnlySpan<byte> ifo)
    {
        if (!IsTitleSetIfo(ifo) || ifo.Length < VtsAudioAttrTable)
            return [];

        var count = Math.Min((int)ReadUInt16(ifo, VtsAudioStreamCount), MaxAudioStreams);
        var present = ReadPresentAudioSlots(ifo);
        var streams = new List<DvdAudioStream>(count);

        for (var i = 0; i < count; i++)
        {
            var offset = VtsAudioAttrTable + i * 8;
            if (offset + 8 > ifo.Length)
                break;

            // The chain shows which slots are really in the multiplex. When no chain can be read, fall back
            // to the declared attributes, so the user is offered too many streams rather than none.
            if (present is not null && !present.Contains(i))
                continue;

            // Byte 0, bits 7 to 5: coding mode. Byte 1, bits 2 to 0: channel count, less one.
            var codec = ((ifo[offset] >> 5) & 0x7) switch
            {
                0 => "ac3",
                2 => "mpeg1",
                3 => "mpeg2",
                4 => "lpcm",
                6 => "dts",
                _ => "audio",
            };
            var channels = (ifo[offset + 1] & 0x7) + 1;

            // The 2-character ISO language code follows, if present. It is printable letters, or absent.
            string? language = null;
            if (IsLower(ifo[offset + 2]) && IsLower(ifo[offset + 3]))
                language = $"{(char)ifo[offset + 2]}{(char)ifo[offset + 3]}";

            // ffmpeg numbers the audio streams it FINDS, in order. So a stream's 0:a:N is its position
            // among the streams actually present, not its slot among the IFO's eight. The two match only
            // when no slot is skipped. Skipped slots must be handled, or the numbers go wrong.
            streams.Add(new DvdAudioStream(streams.Count, codec, channels, language));
        }

        return streams;
    }

    /// <summary>
    /// Which of the eight audio slots the title's program chain says are really in the multiplex.
    /// Returns null when no chain can be read. The caller then keeps the declared attributes, because
    /// offering too many streams is better than offering none.
    ///
    /// <para>
    /// The chain's audio-control table has one 2-byte entry per slot. The top bit of the first byte
    /// means "this stream is present". The rest of the entry is the stream number the player uses. We
    /// do not need it, because ffmpeg finds the streams itself.
    /// </para>
    /// </summary>
    private static HashSet<int>? ReadPresentAudioSlots(ReadOnlySpan<byte> ifo)
    {
        var pgcitSector = ReadUInt32(ifo, VtsPgcitPointer);
        if (pgcitSector == 0)
            return null;

        var pgcitStart = (int)(pgcitSector * SectorSize);
        if (pgcitStart < 0 || pgcitStart + 8 > ifo.Length)
            return null;

        var pgcit = ifo[pgcitStart..];
        int chainCount = ReadUInt16(pgcit, 0);

        // Union across chains. All chains in a title set play from the same VOBs, so a stream that any
        // of them uses is a stream that is there.
        var present = new HashSet<int>();
        var sawChain = false;

        for (var i = 0; i < chainCount; i++)
        {
            var pointer = 8 + i * 8;
            if (pointer + 8 > pgcit.Length)
                break;

            var pgcOffset = (int)ReadUInt32(pgcit, pointer + 4);
            if (pgcOffset <= 0 || pgcOffset + PgcAudioControl + MaxAudioStreams * 2 > pgcit.Length)
                continue;

            sawChain = true;
            var pgc = pgcit[pgcOffset..];

            for (var slot = 0; slot < MaxAudioStreams; slot++)
            {
                if ((pgc[PgcAudioControl + slot * 2] & 0x80) != 0)
                    present.Add(slot);
            }
        }

        if (!sawChain || present.Count == 0)
            return null;

        return present;
    }

    private static bool IsLower(byte b) => b is >= (byte)'a' and <= (byte)'z';

    /// <summary>
    /// Parses every program chain in a title set into a title with its chapters. Titles come back in
    /// file order, and the caller sorts them. Returns an empty list for an IFO with no usable program chain.
    /// </summary>
    public static IReadOnlyList<(TimeSpan Duration, IReadOnlyList<DvdChapter> Chapters)> ReadTitles(
        ReadOnlySpan<byte> ifo)
    {
        var titles = new List<(TimeSpan, IReadOnlyList<DvdChapter>)>();

        if (!IsTitleSetIfo(ifo))
            return titles;

        // The pointer is a sector number, relative to the start of the IFO.
        var pgcitSector = ReadUInt32(ifo, VtsPgcitPointer);
        if (pgcitSector == 0)
            return titles;

        var pgcitStart = (int)(pgcitSector * SectorSize);
        if (pgcitStart < 0 || pgcitStart + 8 > ifo.Length)
            return titles;

        var pgcit = ifo[pgcitStart..];
        int chainCount = ReadUInt16(pgcit, 0);

        for (var i = 0; i < chainCount; i++)
        {
            // The search pointer table follows the 8-byte header. Each chain has 8 bytes, and the PGC's
            // own offset (from the start of the table) is in its last 4 bytes.
            var pointer = 8 + i * 8;
            if (pointer + 8 > pgcit.Length)
                break;

            var pgcOffset = (int)ReadUInt32(pgcit, pointer + 4);
            if (pgcOffset <= 0 || pgcOffset >= pgcit.Length)
                continue;

            if (ReadProgramChain(pgcit[pgcOffset..]) is { } title)
                titles.Add(title);
        }

        return titles;
    }

    private static (TimeSpan Duration, IReadOnlyList<DvdChapter> Chapters)? ReadProgramChain(ReadOnlySpan<byte> pgc)
    {
        if (pgc.Length < 0xEC)
            return null;

        int programCount = pgc[PgcProgramCount];
        int cellCount = pgc[PgcCellCount];
        if (programCount == 0 || cellCount == 0)
            return null;

        var totalDuration = ReadBcdTime(pgc[PgcPlaybackTime..]);

        int programMapOffset = ReadUInt16(pgc, PgcProgramMapOffset);
        int cellTableOffset = ReadUInt16(pgc, PgcCellPlaybackOffset);
        if (programMapOffset == 0 || cellTableOffset == 0)
            return null;

        if (programMapOffset + programCount > pgc.Length ||
            cellTableOffset + cellCount * CellPlaybackEntrySize > pgc.Length)
            return null;

        // Each cell's duration and its sector run. The sectors matter most. They say where the cell's
        // bytes are on the disc, and this is the only reliable way to cut a chapter out.
        var cellDurations = new TimeSpan[cellCount];
        var cellRanges = new DvdCellRange[cellCount];
        for (var cell = 0; cell < cellCount; cell++)
        {
            var entry = cellTableOffset + cell * CellPlaybackEntrySize;
            cellDurations[cell] = ReadBcdTime(pgc[(entry + CellPlaybackTime)..]);
            cellRanges[cell] = new DvdCellRange(
                (int)ReadUInt32(pgc, entry + CellFirstSector),
                (int)ReadUInt32(pgc, entry + CellLastSector));
        }

        // The program map gives each chapter's FIRST cell (1-based). A chapter runs from its own first
        // cell up to the next chapter's first cell. The last chapter runs to the end.
        var firstCells = new int[programCount];
        for (var program = 0; program < programCount; program++)
            firstCells[program] = pgc[programMapOffset + program];

        var chapters = new List<DvdChapter>(programCount);
        var elapsed = TimeSpan.Zero;

        for (var program = 0; program < programCount; program++)
        {
            var startCell = Math.Clamp(firstCells[program] - 1, 0, cellCount - 1);
            var endCell = program + 1 < programCount
                ? Math.Clamp(firstCells[program + 1] - 1, 0, cellCount)
                : cellCount;

            var duration = TimeSpan.Zero;
            var cells = new List<DvdCellRange>(endCell - startCell);
            for (var cell = startCell; cell < endCell; cell++)
            {
                duration += cellDurations[cell];
                cells.Add(cellRanges[cell]);
            }

            chapters.Add(new DvdChapter(program + 1, elapsed, duration, cells));
            elapsed += duration;
        }

        // The PGC's own stated runtime is the authority. Fall back to the sum of the cells when the disc
        // does not give one, because some authoring tools leave it at zero.
        return (totalDuration > TimeSpan.Zero ? totalDuration : elapsed, chapters);
    }

    /// <summary>
    /// DVD times are BCD: hours, minutes, seconds, then frames. The frame rate is in the top two bits
    /// of the frame byte: 0b11 is 30 fps (NTSC), and 0b10 is 25 fps (PAL).
    /// </summary>
    public static TimeSpan ReadBcdTime(ReadOnlySpan<byte> time)
    {
        if (time.Length < 4)
            return TimeSpan.Zero;

        var hours = FromBcd(time[0]);
        var minutes = FromBcd(time[1]);
        var seconds = FromBcd(time[2]);

        var rateBits = time[3] >> 6;
        var frames = FromBcd((byte)(time[3] & 0x3F));
        double fps = rateBits switch
        {
            0b11 => 30.0,
            0b10 => 25.0,
            _ => 0.0, // No rate is declared, so the frame count has no meaning. Ignore it.
        };

        var total = hours * 3600.0 + minutes * 60.0 + seconds;
        if (fps > 0)
            total += frames / fps;

        return TimeSpan.FromSeconds(total);
    }

    private static int FromBcd(byte value) => ((value >> 4) & 0x0F) * 10 + (value & 0x0F);

    // Every multi-byte field in an IFO is big-endian.
    private static uint ReadUInt32(ReadOnlySpan<byte> data, int offset) =>
        offset + 4 > data.Length ? 0 : BinaryPrimitives.ReadUInt32BigEndian(data[offset..]);

    private static ushort ReadUInt16(ReadOnlySpan<byte> data, int offset) =>
        offset + 2 > data.Length ? (ushort)0 : BinaryPrimitives.ReadUInt16BigEndian(data[offset..]);
}
