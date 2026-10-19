using RPM.Core.Ripping;

namespace RPM.Core.Tests;

/// <summary>
/// CSS fails quietly when it is wrong. It scrambles the PES payload and leaves every header in the
/// clear, so a scrambled VOB still demuxes, still gives audio packets of the right length and still
/// decodes, into full-scale white noise, with no error anywhere. The two scrambling-control bits
/// are the only reliable signal, so reading them has to be right.
/// </summary>
public class ProgramStreamTests
{
    private const int SectorSize = 2048;

    /// <summary>A program-stream pack: pack header, optional system header, then a PES packet.</summary>
    private static byte[] Pack(byte streamId, int scrambling, int stuffing = 0, bool systemHeader = false)
    {
        var sector = new byte[SectorSize];

        // Pack header: 00 00 01 BA + 14 bytes, the last 3 bits of byte 13 being the stuffing count.
        sector[0] = 0x00; sector[1] = 0x00; sector[2] = 0x01; sector[3] = 0xBA;
        sector[13] = (byte)(0xF8 | (stuffing & 0x07));

        var at = 14 + stuffing;

        if (systemHeader)
        {
            // 00 00 01 BB + 2-byte length + body.
            const int bodyLength = 12;
            sector[at] = 0x00; sector[at + 1] = 0x00; sector[at + 2] = 0x01; sector[at + 3] = 0xBB;
            sector[at + 4] = (byte)(bodyLength >> 8);
            sector[at + 5] = (byte)(bodyLength & 0xFF);
            at += 6 + bodyLength;
        }

        // The media PES: 00 00 01 <id>, 2-byte length, flags byte carrying the scrambling control.
        sector[at] = 0x00; sector[at + 1] = 0x00; sector[at + 2] = 0x01; sector[at + 3] = streamId;
        sector[at + 4] = 0x07; sector[at + 5] = 0xEC;
        sector[at + 6] = (byte)(0x80 | ((scrambling & 0x03) << 4));

        return sector;
    }

    [Fact]
    public void A_clear_media_pack_reads_as_clear()
    {
        Assert.Equal(0, ProgramStream.ScramblingControl(Pack(0xBD, scrambling: 0)));
        Assert.False(ProgramStream.IsScrambled(Pack(0xBD, scrambling: 0)));
        Assert.False(ProgramStream.IsScrambled(Pack(0xE0, scrambling: 0)));
    }

    [Theory]
    [InlineData(0xBD)]   // private_stream_1, AC-3 / LPCM / DTS, the audio we are after
    [InlineData(0xE0)]   // video
    [InlineData(0xC0)]   // MPEG audio
    public void A_scrambled_media_pack_is_caught(byte streamId)
    {
        Assert.True(ProgramStream.IsScrambled(Pack(streamId, scrambling: 1)));
        Assert.True(ProgramStream.IsScrambled(Pack(streamId, scrambling: 3)));
    }

    [Fact]
    public void A_nav_pack_carries_no_verdict()
    {
        // private_stream_2 (BF) is the NAV pack that starts every VOBU. It is never scrambled. If it
        // is counted as "clear", the wrong conclusion is that a protected disc is not protected.
        Assert.Null(ProgramStream.ScramblingControl(Pack(0xBF, scrambling: 0)));
        Assert.False(ProgramStream.IsScrambled(Pack(0xBF, scrambling: 0)));
    }

    [Fact]
    public void Stuffing_bytes_are_stepped_over()
    {
        for (var stuffing = 0; stuffing <= 7; stuffing++)
            Assert.True(ProgramStream.IsScrambled(Pack(0xBD, scrambling: 1, stuffing)),
                $"a pack with {stuffing} stuffing bytes was misread");
    }

    [Fact]
    public void A_system_header_before_the_media_pes_is_stepped_over()
    {
        // The first pack of a VOB starts with a system header. A reader that looks only at the first
        // packet would stop there and never reach the media PES that carries the flag.
        Assert.True(ProgramStream.IsScrambled(Pack(0xBD, scrambling: 1, systemHeader: true)));
        Assert.False(ProgramStream.IsScrambled(Pack(0xBD, scrambling: 0, systemHeader: true)));
    }

    [Fact]
    public void Anything_that_is_not_a_pack_is_not_a_verdict()
    {
        Assert.Null(ProgramStream.ScramblingControl(new byte[SectorSize]));       // zeroes
        Assert.Null(ProgramStream.ScramblingControl(new byte[] { 1, 2, 3 }));     // too short
        Assert.Null(ProgramStream.ScramblingControl("not a dvd sector at all"u8)); // junk
    }

    /// <summary>
    /// A scramble probe has to tell apart "I looked at content and it was clear" and "I never saw
    /// any content". Only the first is grounds to call a title rippable. The second is what a run of
    /// NAV packs, padding or an unreadable stretch looks like.
    /// </summary>
    [Fact]
    public void A_media_pack_is_told_apart_from_everything_that_is_not_one()
    {
        Assert.True(ProgramStream.IsMediaPack(Pack(0xBD, scrambling: 0)));   // AC-3 audio, clear
        Assert.True(ProgramStream.IsMediaPack(Pack(0xBD, scrambling: 1)));   // …and scrambled
        Assert.True(ProgramStream.IsMediaPack(Pack(0xE0, scrambling: 0)));   // video

        // The NAV pack is private_stream_2: it is never scrambled and carries no content, so counting
        // it as something examined would let a probe decide "clear" without having read any audio.
        Assert.False(ProgramStream.IsMediaPack(Pack(0xBF, scrambling: 0)));

        Assert.False(ProgramStream.IsMediaPack(new byte[SectorSize]));
        Assert.False(ProgramStream.IsMediaPack("not a dvd sector at all"u8));
    }
}
