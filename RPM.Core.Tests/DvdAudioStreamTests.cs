using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// The IFO's audio attribute table has eight fixed slots, and the count beside it is what the title
/// set <em>declares</em>, not what is in the VOBs. A real disc (David Gray, "Live in Slow Motion")
/// declares 8 but has 2; its last six slots are identical filler an authoring tool never cleared.
/// Trusting the declaration puts six streams that do not exist in the picker, and choosing one makes
/// ffmpeg look for a stream that is not in the file.
///
/// <para>
/// The program chain's audio-control table is what actually says which slots are multiplexed, and
/// these tests check it, including the part that matters only when a slot is skipped: ffmpeg numbers
/// the streams it finds, so <c>0:a:N</c> is a stream's position among the ones present, not its slot.
/// </para>
/// </summary>
public class DvdAudioStreamTests
{
    private const int SectorSize = 2048;
    private const int VtsAudioStreamCount = 0x0202;
    private const int VtsAudioAttrTable = 0x0204;
    private const int VtsPgcitPointer = 0xCC;
    private const int PgcAudioControl = 0x0C;

    /// <summary>An audio attribute slot: coding mode in the top 3 bits, channels-less-one in byte 1.</summary>
    private static void WriteSlot(byte[] ifo, int slot, int coding, int channels, string language)
    {
        var at = VtsAudioAttrTable + slot * 8;
        ifo[at] = (byte)((coding & 0x7) << 5);
        ifo[at + 1] = (byte)((channels - 1) & 0x7);
        ifo[at + 2] = (byte)language[0];
        ifo[at + 3] = (byte)language[1];
    }

    /// <summary>
    /// A title-set IFO that declares <paramref name="declared"/> audio slots but whose program chain
    /// marks only <paramref name="present"/> as actually present.
    /// </summary>
    private static byte[] BuildIfo(int declared, params int[] present)
    {
        var ifo = new byte[SectorSize * 3];
        "DVDVIDEO-VTS"u8.CopyTo(ifo);

        ifo[VtsAudioStreamCount] = (byte)(declared >> 8);
        ifo[VtsAudioStreamCount + 1] = (byte)declared;

        // Slot 0 is AC-3 5-channel, slot 1 is LPCM stereo, and every slot after them is the same
        // filler the real disc had.
        WriteSlot(ifo, 0, coding: 0, channels: 5, "en");
        WriteSlot(ifo, 1, coding: 4, channels: 2, "en");
        for (var i = 2; i < 8; i++)
            WriteSlot(ifo, i, coding: 0, channels: 6, "en");

        // PGCIT one sector in: chain count, then one 8-byte search pointer, then the PGC.
        const int pgcitSector = 1;
        ifo[VtsPgcitPointer + 3] = pgcitSector;

        var pgcit = pgcitSector * SectorSize;
        ifo[pgcit + 1] = 1;             // one program chain
        const int pgcOffset = 16;       // past the 8-byte header + one 8-byte pointer
        ifo[pgcit + 8 + 7] = pgcOffset; // the chain's offset, in the pointer's last 4 bytes

        var pgc = pgcit + pgcOffset;
        ifo[pgc + 0x02] = 1;            // one program
        ifo[pgc + 0x03] = 1;            // one cell

        foreach (var slot in present)
            ifo[pgc + PgcAudioControl + slot * 2] = 0x80;   // top bit: this stream is present

        return ifo;
    }

    [Fact]
    public void A_disc_that_declares_eight_but_carries_two_offers_two()
    {
        // Exactly the David Gray disc: VTS_AST_Ns = 8, but the chain says only slots 0 and 1 are in
        // the multiplex. ffprobe finds two streams in the VOB, and so must the parser.
        var streams = DvdIfo.ReadAudioStreams(BuildIfo(declared: 8, present: [0, 1]));

        Assert.Equal(2, streams.Count);
        Assert.Equal("ac3", streams[0].Codec);
        Assert.Equal(5, streams[0].Channels);
        Assert.Equal("lpcm", streams[1].Codec);
        Assert.Equal(2, streams[1].Channels);
        Assert.Equal([0, 1], streams.Select(s => s.Index));
    }

    [Fact]
    public void A_skipped_slot_does_not_skip_an_ffmpeg_index()
    {
        // The chain carries slots 0 and 3. ffmpeg numbers what it finds, so those are 0:a:0 and
        // 0:a:1. Mapping the second one to 0:a:3 would ask for a stream that is not there.
        var streams = DvdIfo.ReadAudioStreams(BuildIfo(declared: 8, present: [0, 3]));

        Assert.Equal(2, streams.Count);
        Assert.Equal([0, 1], streams.Select(s => s.Index));
        Assert.Equal("ac3", streams[0].Codec);
        Assert.Equal(6, streams[1].Channels);   // slot 3's attributes, at ffmpeg index 1
    }

    [Fact]
    public void A_chain_that_marks_nothing_falls_back_to_what_is_declared()
    {
        // Offering too many streams is a bad picker; offering none is a disc that cannot be ripped.
        var streams = DvdIfo.ReadAudioStreams(BuildIfo(declared: 2));

        Assert.Equal(2, streams.Count);
        Assert.Equal([0, 1], streams.Select(s => s.Index));
    }

    [Fact]
    public void The_declared_count_still_caps_the_list()
    {
        // The chain claims four slots but the title set only declares two attribute entries; the
        // attributes are what describe a stream, so only two can be offered correctly.
        var streams = DvdIfo.ReadAudioStreams(BuildIfo(declared: 2, present: [0, 1, 2, 3]));

        Assert.Equal(2, streams.Count);
    }

    [Fact]
    public void Junk_is_not_a_title_set()
        => Assert.Empty(DvdIfo.ReadAudioStreams(new byte[SectorSize]));
}
