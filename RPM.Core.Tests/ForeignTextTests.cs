using System.Text;
using RPM.Core.Files;
using RPM.Core.Radio;

namespace RPM.Core.Tests;

public class ForeignTextTests
{
    /// <summary>The bytes of Windows-1252 text, written out by hand so the test does not depend on the
    /// code page provider it exists to avoid.</summary>
    private static byte[] Latin(string ascii, params (int At, byte Value)[] high)
    {
        var bytes = Encoding.ASCII.GetBytes(ascii);
        foreach (var (at, value) in high)
            bytes[at] = value;
        return bytes;
    }

    [Fact]
    public void Utf8_is_read_as_utf8()
    {
        var bytes = Encoding.UTF8.GetBytes("Raphaël Pichon – Motet");

        Assert.Equal("Raphaël Pichon – Motet", ForeignText.Decode(bytes, ForeignText.Ending.Whole));
    }

    [Fact]
    public void A_single_byte_letter_is_read_as_windows_1252_not_as_a_replacement_character()
    {
        // What NPO Klassiek sends: "Raphaël" with the ë as the one byte 0xEB.
        var bytes = Latin("Raphael Pichon", (5, 0xEB));

        var text = ForeignText.Decode(bytes, ForeignText.Ending.Whole);

        Assert.Equal("Raphaël Pichon", text);
        Assert.DoesNotContain('�', text);
    }

    [Fact]
    public void The_windows_1252_extras_are_the_quotes_dashes_and_euro_sign()
    {
        // 0x93 and 0x94 are curly double quotes, 0x96 a short dash, 0x80 the euro sign. Read as Latin-1
        // they would be invisible control characters.
        var bytes = Latin("xAx - x5", (0, 0x93), (2, 0x94), (4, 0x96), (6, 0x80));

        Assert.Equal("“A” – €5", ForeignText.Decode(bytes, ForeignText.Ending.Whole));
    }

    [Fact]
    public void A_byte_order_mark_is_obeyed_and_not_kept()
    {
        byte[] utf8 = [0xEF, 0xBB, 0xBF, .. Encoding.UTF8.GetBytes("Café")];
        byte[] utf16 = [0xFF, 0xFE, .. Encoding.Unicode.GetBytes("Café")];

        Assert.Equal("Café", ForeignText.Decode(utf8, ForeignText.Ending.Whole));
        Assert.Equal("Café", ForeignText.Decode(utf16, ForeignText.Ending.Whole));
    }

    [Fact]
    public void A_whole_windows_1252_text_keeps_a_letter_at_its_end()
    {
        // 0xE9 is "é" in Windows-1252 and also how a three-byte UTF-8 character begins. Text that is
        // whole must not lose it as a character cut off by a limit.
        var bytes = Latin("Cafe", (3, 0xE9));

        Assert.Equal("Café", ForeignText.Decode(bytes, ForeignText.Ending.Whole));
    }

    [Fact]
    public void A_utf8_read_cut_part_way_through_its_last_character_stays_utf8()
    {
        var full = Encoding.UTF8.GetBytes("Raphaël – x");
        var cut = full[..^3];   // " x" and the last of the dash's three bytes are gone

        Assert.Equal("Raphaël ", ForeignText.Decode(cut, ForeignText.Ending.MayBeCut));
    }

    [Fact]
    public void A_web_page_with_no_server_charset_is_read_in_the_charset_its_meta_tag_names()
    {
        var html = Latin("<html><head><meta charset=\"iso-8859-1\"></head><body>Raphael</body></html>");
        html[Array.IndexOf(html, (byte)'R') + 5] = 0xEB;

        var text = ForeignText.DecodeWebPage(html, serverCharset: null, ForeignText.Ending.Whole);

        Assert.Contains("Raphaël", text);
    }

    [Fact]
    public void A_web_page_reads_an_http_equiv_meta_tag_too()
    {
        var html = Latin("<meta http-equiv=\"Content-Type\" content=\"text/html; charset=windows-1252\">x");
        html[^1] = 0x93;

        Assert.EndsWith("“", ForeignText.DecodeWebPage(html, null, ForeignText.Ending.Whole));
    }

    [Fact]
    public void The_server_charset_wins_over_the_page()
    {
        var html = Encoding.UTF8.GetBytes("<meta charset=\"iso-8859-1\">Raphaël");

        Assert.EndsWith("Raphaël", ForeignText.DecodeWebPage(html, "utf-8", ForeignText.Ending.Whole));
    }

    [Fact]
    public void A_radio_station_announcing_in_windows_1252_is_read_right()
    {
        const int metaInt = 16;
        var block = Latin("StreamTitle='Raphael Pichon';", (18, 0xEB));
        int blocks = (block.Length + 15) / 16;

        byte[] raw =
        [
            .. Enumerable.Repeat((byte)1, metaInt),
            (byte)blocks, .. block, .. new byte[blocks * 16 - block.Length],
            .. Enumerable.Repeat((byte)2, metaInt),
            0,
        ];

        using var icy = new IcyStream(new MemoryStream(raw), metaInt);
        icy.CopyTo(Stream.Null);

        Assert.Equal("Raphaël Pichon", icy.Title);
    }
}
