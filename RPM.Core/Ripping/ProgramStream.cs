namespace RPM.Core.Ripping;

/// <summary>
/// The little of MPEG program-stream framing that a DVD rip has to understand: whether the pack in a
/// sector carries media, and whether that media is CSS-scrambled.
///
/// <para>
/// CSS scrambles only the PES <b>payload</b>. Every pack header, PES header and NAV pack stays in the
/// clear. That makes the protection easy to get wrong without noticing. A scrambled VOB still starts with
/// <c>00 00 01 BA</c>, still demuxes, and still yields audio packets of the right length at the right
/// timestamps. Give the payload to a decoder and it produces confident, full-scale white noise. Nothing
/// reports an error. The only reliable signal is the two scrambling-control bits in each media PES
/// header, and this file reads those bits.
/// </para>
/// </summary>
public static class ProgramStream
{
    /// <summary>
    /// The PES scrambling-control value of the media stream in a program-stream pack. 0 means clear, and
    /// any other value means scrambled. Returns null when the sector has no media, such as a NAV or system
    /// pack. Those are never scrambled.
    /// </summary>
    public static int? ScramblingControl(ReadOnlySpan<byte> pack)
    {
        // Pack header: 00 00 01 BA, then an MPEG-2 header of 14 bytes. Its last 3 bits say how much stuffing
        // follows. The PES packet begins after that.
        if (pack.Length < 14 || pack[0] != 0 || pack[1] != 0 || pack[2] != 1 || pack[3] != 0xBA)
            return null;

        var pes = 14 + (pack[13] & 0x07);

        // A pack may start with a system header (BB) before the media PES. Skip over it.
        if (pes + 6 <= pack.Length
            && pack[pes] == 0 && pack[pes + 1] == 0 && pack[pes + 2] == 1 && pack[pes + 3] == 0xBB)
        {
            var systemLength = (pack[pes + 4] << 8) | pack[pes + 5];
            pes += 6 + systemLength;
        }

        if (pes + 7 > pack.Length || pack[pes] != 0 || pack[pes + 1] != 0 || pack[pes + 2] != 1)
            return null;

        var streamId = pack[pes + 3];

        // Media only: video (E0-EF), MPEG audio (C0-DF), or private_stream_1 (BD, used for AC-3, LPCM and DTS).
        // private_stream_2 (BF, the NAV pack) has no scrambled payload, so it never counts.
        var isMedia = streamId == 0xBD || streamId is >= 0xC0 and <= 0xEF;
        if (!isMedia)
            return null;

        // The PES header flags byte. Bits 5 and 4 are the scrambling control.
        return (pack[pes + 6] >> 4) & 0x03;
    }

    /// <summary>True when this sector holds a media pack whose payload is scrambled.</summary>
    public static bool IsScrambled(ReadOnlySpan<byte> pack) => ScramblingControl(pack) is > 0;

    /// <summary>True when this sector holds any media pack, scrambled or not. A scramble probe needs this to
    /// know that it has examined something. Without it, a run of NAV packs and padding could make a title
    /// look clear.</summary>
    public static bool IsMediaPack(ReadOnlySpan<byte> pack) => ScramblingControl(pack) is not null;
}
