using System.Buffers.Binary;
using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The IFO is the only place a DVD says where its chapters are, so getting its offsets wrong means
/// silently cutting songs in the wrong places. These build title sets byte by byte to the DVD-Video
/// spec and check the parser reads back what was written.
/// </summary>
public class DvdIfoTests
{
    private const int SectorSize = 2048;
    private const int PgcitSector = 1;
    private const int PgcOffsetInPgcit = 16;

    /// <summary>BCD time, as a DVD stores it: hours, minutes, seconds, then frames with the frame
    /// rate in the top two bits (0b11 = 30 fps, 0b10 = 25 fps).</summary>
    private static byte[] BcdTime(int hours, int minutes, int seconds, int frames = 0, int rateBits = 0b11) =>
    [
        ToBcd(hours), ToBcd(minutes), ToBcd(seconds), (byte)((rateBits << 6) | ToBcd(frames)),
    ];

    private static byte ToBcd(int value) => (byte)(((value / 10) << 4) | (value % 10));

    /// <summary>Builds a title set holding one program chain: <paramref name="cellSeconds"/> cells, and
    /// a program map saying which cell each chapter starts at.</summary>
    private static byte[] BuildIfo(int[] cellSeconds, int[] chapterFirstCells, int totalSeconds)
    {
        const int programMapOffset = 0xF0;
        const int cellTableOffset = 0x100;
        const int cellEntrySize = 0x18;

        var pgcLength = cellTableOffset + cellSeconds.Length * cellEntrySize;
        var pgc = new byte[pgcLength];

        pgc[0x02] = (byte)chapterFirstCells.Length;   // programs = chapters
        pgc[0x03] = (byte)cellSeconds.Length;         // cells
        BcdTime(totalSeconds / 3600, totalSeconds % 3600 / 60, totalSeconds % 60).CopyTo(pgc, 0x04);

        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xE6), programMapOffset);
        BinaryPrimitives.WriteUInt16BigEndian(pgc.AsSpan(0xE8), cellTableOffset);

        for (var i = 0; i < chapterFirstCells.Length; i++)
            pgc[programMapOffset + i] = (byte)chapterFirstCells[i];

        for (var i = 0; i < cellSeconds.Length; i++)
        {
            var entry = cellTableOffset + i * cellEntrySize;
            var seconds = cellSeconds[i];
            BcdTime(seconds / 3600, seconds % 3600 / 60, seconds % 60).CopyTo(pgc, entry + 0x04);

            // Each cell gets its own run of 10 sectors, so cell N sits at sectors [10N, 10N+9].
            BinaryPrimitives.WriteUInt32BigEndian(pgc.AsSpan(entry + 0x08), (uint)(i * 10));       // first
            BinaryPrimitives.WriteUInt32BigEndian(pgc.AsSpan(entry + 0x14), (uint)(i * 10 + 9));   // last
        }

        // The program chain table: a header, one search pointer, then the chain itself.
        var pgcitLength = PgcOffsetInPgcit + pgcLength;
        var pgcit = new byte[pgcitLength];
        BinaryPrimitives.WriteUInt16BigEndian(pgcit.AsSpan(0), 1);                       // one chain
        BinaryPrimitives.WriteUInt32BigEndian(pgcit.AsSpan(4), (uint)pgcitLength - 1);   // last byte
        pgcit[8] = 1;                                                                    // entry id
        BinaryPrimitives.WriteUInt32BigEndian(pgcit.AsSpan(8 + 4), PgcOffsetInPgcit);    // where the chain is
        pgc.CopyTo(pgcit, PgcOffsetInPgcit);

        var ifo = new byte[PgcitSector * SectorSize + pgcitLength];
        "DVDVIDEO-VTS"u8.CopyTo(ifo);
        BinaryPrimitives.WriteUInt32BigEndian(ifo.AsSpan(0xCC), PgcitSector);            // pointer to the table
        pgcit.CopyTo(ifo, PgcitSector * SectorSize);

        return ifo;
    }

    [Fact]
    public void Reads_one_chapter_per_program_with_its_start_and_length()
    {
        // Three songs, one cell each, a minute apiece.
        var ifo = BuildIfo(cellSeconds: [60, 60, 60], chapterFirstCells: [1, 2, 3], totalSeconds: 180);

        var titles = DvdIfo.ReadTitles(ifo);

        var title = Assert.Single(titles);
        Assert.Equal(TimeSpan.FromMinutes(3), title.Duration);
        Assert.Equal(3, title.Chapters.Count);

        Assert.Equal(TimeSpan.Zero, title.Chapters[0].Start);
        Assert.Equal(TimeSpan.FromSeconds(60), title.Chapters[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(120), title.Chapters[2].Start);
        Assert.All(title.Chapters, c => Assert.Equal(TimeSpan.FromSeconds(60), c.Duration));

        // Chapters are numbered 1..n; that number becomes the track number.
        Assert.Equal([1, 2, 3], title.Chapters.Select(c => c.Number));
    }

    [Fact]
    public void A_chapter_spanning_several_cells_sums_them()
    {
        // Two songs across three cells: the first song is cells 1-2, the second is cell 3. A parser
        // that assumed one cell per chapter would report the first song as half its real length.
        var ifo = BuildIfo(cellSeconds: [30, 30, 90], chapterFirstCells: [1, 3], totalSeconds: 150);

        var title = Assert.Single(DvdIfo.ReadTitles(ifo));

        Assert.Equal(2, title.Chapters.Count);
        Assert.Equal(TimeSpan.FromSeconds(60), title.Chapters[0].Duration);
        Assert.Equal(TimeSpan.FromSeconds(60), title.Chapters[1].Start);
        Assert.Equal(TimeSpan.FromSeconds(90), title.Chapters[1].Duration);
    }

    [Fact]
    public void A_chapter_carries_the_sector_runs_of_every_cell_it_spans()
    {
        // The ripper cuts on sectors: a DVD's timestamps restart per cell, so seeking by time goes
        // to the wrong song. Chapter 1 owns cells 1-2, chapter 2 owns cell 3.
        var ifo = BuildIfo(cellSeconds: [30, 30, 90], chapterFirstCells: [1, 3], totalSeconds: 150);

        var title = Assert.Single(DvdIfo.ReadTitles(ifo));

        Assert.Equal([new DvdCellRange(0, 9), new DvdCellRange(10, 19)], title.Chapters[0].Cells);
        Assert.Equal([new DvdCellRange(20, 29)], title.Chapters[1].Cells);

        // A cell's range is inclusive at both ends.
        Assert.Equal(10, title.Chapters[1].Cells[0].SectorCount);
    }

    [Fact]
    public void Falls_back_to_the_summed_cells_when_the_disc_declares_no_runtime()
    {
        var ifo = BuildIfo(cellSeconds: [45, 75], chapterFirstCells: [1, 2], totalSeconds: 0);

        var title = Assert.Single(DvdIfo.ReadTitles(ifo));

        Assert.Equal(TimeSpan.FromSeconds(120), title.Duration);
    }

    [Fact]
    public void Bcd_time_decodes_hours_minutes_seconds_and_the_frame_rate()
    {
        // 1:23:45 exactly, NTSC (30 fps), no frames.
        Assert.Equal(new TimeSpan(1, 23, 45), DvdIfo.ReadBcdTime(BcdTime(1, 23, 45)));

        // Frames are a fraction of a second, and the rate lives in the top two bits: 15 frames is
        // half a second at 30 fps but six tenths at 25.
        Assert.Equal(0.5, DvdIfo.ReadBcdTime(BcdTime(0, 0, 0, 15, 0b11)).TotalSeconds, precision: 3);
        Assert.Equal(0.6, DvdIfo.ReadBcdTime(BcdTime(0, 0, 0, 15, 0b10)).TotalSeconds, precision: 3);

        // BCD, not binary: 0x45 is forty-five seconds, not sixty-nine.
        Assert.Equal(45, DvdIfo.ReadBcdTime([0x00, 0x00, 0x45, 0xC0]).TotalSeconds, precision: 3);
    }

    [Fact]
    public void Reads_audio_streams_from_the_ifo_attribute_table()
    {
        // The attribute table lets a CSS disc (whose VOBs ffprobe cannot read) still show its audio
        // options. These two entries are the real bytes from a commercial disc: LPCM stereo
        // (0x8401 656e) and AC-3 5.1 (0x04c5 656e), both English.
        var ifo = new byte[0x220];
        "DVDVIDEO-VTS"u8.CopyTo(ifo);
        BinaryPrimitives.WriteUInt16BigEndian(ifo.AsSpan(0x0202), 2);       // two audio streams
        new byte[] { 0x84, 0x01, (byte)'e', (byte)'n' }.CopyTo(ifo, 0x0204);
        new byte[] { 0x04, 0xC5, (byte)'e', (byte)'n' }.CopyTo(ifo, 0x020C);

        var streams = DvdIfo.ReadAudioStreams(ifo);

        Assert.Equal(2, streams.Count);
        Assert.Equal("lpcm", streams[0].Codec);
        Assert.Equal(2, streams[0].Channels);
        Assert.Equal("en", streams[0].Language);
        Assert.Equal("ac3", streams[1].Codec);
        Assert.Equal(6, streams[1].Channels);
        // Stream index is ffmpeg's 0:a:N, which is why they are read in order.
        Assert.Equal([0, 1], streams.Select(s => s.Index));
        // ...and the display string is what the picker shows.
        Assert.Equal("AC3 5.1 (en)", streams[1].DisplayName);
    }

    [Fact]
    public void Rejects_bytes_that_are_not_a_title_set()
    {
        Assert.False(DvdIfo.IsTitleSetIfo("DVDVIDEO-VMG"u8));      // the top-level menu, not a title set
        Assert.Empty(DvdIfo.ReadTitles(new byte[4096]));           // zeroes
        Assert.Empty(DvdIfo.ReadTitles("DVDVIDEO-VTS"u8.ToArray())); // truncated: header and nothing else
    }
}
