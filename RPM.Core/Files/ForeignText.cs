using System.Text;
using System.Text.Unicode;

namespace RPM.Core.Files;

/// <summary>
/// Text that arrives as bytes from somewhere that does not say how it was written: a radio station's
/// track line, a cue sheet, a lyric file someone picked, a web page whose server names no charset.
///
/// <para>Bytes are read as UTF-8 only when they are valid UTF-8. Decoding everything as UTF-8 turns
/// each byte of an older single-byte encoding into U+FFFD. For example, a radio station can send
/// "Raphaël" with the "ë" as the single byte 0xEB. So a byte-order mark is obeyed, then UTF-8 is tried
/// strictly, and bytes that are not UTF-8 are read as Windows-1252. That is what such text is in almost
/// every case: Latin-1 with the curly quotes, the dashes and the euro sign in 0x80 to 0x9F, which is also
/// what the HTML standard reads "iso-8859-1" as.</para>
///
/// <para>Windows-1252 is decoded from the table below rather than through <c>Encoding.GetEncoding(1252)</c>.
/// That needs the code page provider registered. The desktop registers it at startup. Nothing else that
/// links this assembly does so.</para>
/// </summary>
public static class ForeignText
{
    /// <summary>
    /// Whether the bytes are all of the text, or a read that a size limit may have stopped part way
    /// through a character. This is stated at every call. Forgiving a broken last character in text
    /// that is whole would drop a real one, because a Windows-1252 "é" at the end of a title is the byte
    /// 0xE9, which is also how a cut-off three-byte UTF-8 character begins.
    /// </summary>
    public enum Ending
    {
        /// <summary>Everything there is: a file, a radio station's metadata block.</summary>
        Whole,

        /// <summary>The first part of something longer, read up to a limit.</summary>
        MayBeCut,
    }

    private delegate string Decoder(ReadOnlySpan<byte> bytes);

    /// <summary>The bytes as text, by the rule in the class remark.</summary>
    public static string Decode(ReadOnlySpan<byte> bytes, Ending ending)
    {
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xEF, 0xBB, 0xBF]))
            return Encoding.UTF8.GetString(bytes[3..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFF, 0xFE]))
            return Encoding.Unicode.GetString(bytes[2..]);
        if (bytes.StartsWith((ReadOnlySpan<byte>)[0xFE, 0xFF]))
            return Encoding.BigEndianUnicode.GetString(bytes[2..]);

        if (Utf8.IsValid(bytes))
            return Encoding.UTF8.GetString(bytes);

        // A read cut off by a size limit can end part way through a character. That is still UTF-8,
        // and reading the whole of it as Windows-1252 because of the last one to three bytes would turn
        // every accented letter before them into two wrong ones. The broken tail is dropped.
        if (ending is Ending.MayBeCut)
        {
            var whole = WithoutCutTail(bytes);
            if (whole.Length < bytes.Length && Utf8.IsValid(whole))
                return Encoding.UTF8.GetString(whole);
        }

        return Windows1252(bytes);
    }

    /// <summary>A file of text, read by <see cref="Decode"/>.</summary>
    public static string ReadFile(string path) => Decode(File.ReadAllBytes(path), Ending.Whole);

    /// <summary>A file of text, read by <see cref="Decode"/>.</summary>
    public static async Task<string> ReadFileAsync(string path, CancellationToken ct)
        => Decode(await File.ReadAllBytesAsync(path, ct).ConfigureAwait(false), Ending.Whole);

    /// <summary>
    /// A web page's bytes as text. The server's charset where it named one; otherwise the charset the
    /// page declares in its own <c>meta</c> tag near the top; otherwise <see cref="Decode"/>.
    /// </summary>
    /// <remarks>
    /// Without this step, a page in Latin-1 that names its charset only in its own head is read as
    /// UTF-8, and the text shows U+FFFD.
    /// </remarks>
    public static string DecodeWebPage(ReadOnlySpan<byte> bytes, string? serverCharset, Ending ending)
    {
        if (Named(serverCharset) is { } declared)
            return declared(bytes);

        // The standard's prescan looks at the first 1024 bytes. A head that puts its charset later than
        // that is not one any browser reads either.
        var head = Encoding.ASCII.GetString(bytes[..Math.Min(bytes.Length, 1024)]);
        if (Named(MetaCharset(head)) is { } meta)
            return meta(bytes);

        return Decode(bytes, ending);
    }

    /// <summary>
    /// A decoder for a charset label, or null for no label or one nothing here can decode. The labels
    /// the HTML standard maps to Windows-1252 are decoded by the table here, so they work without the
    /// code page provider. Anything else is asked of .NET.
    /// </summary>
    private static Decoder? Named(string? label)
    {
        if (string.IsNullOrWhiteSpace(label))
            return null;

        var name = label.Trim().Trim('"', '\'').ToLowerInvariant();
        switch (name)
        {
            case "windows-1252" or "cp1252" or "x-cp1252" or "iso-8859-1" or "iso8859-1" or "latin1"
                or "l1" or "us-ascii" or "ascii" or "iso_8859-1":
                return Windows1252;
            case "utf-8" or "utf8":
                return bytes => Encoding.UTF8.GetString(bytes);
        }

        try
        {
            var encoding = Encoding.GetEncoding(name);
            return bytes => encoding.GetString(bytes);
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>The charset a page names in <c>&lt;meta charset="..."&gt;</c> or in a
    /// <c>&lt;meta http-equiv="Content-Type" content="...; charset=..."&gt;</c>, or null.</summary>
    private static string? MetaCharset(string head)
    {
        var lower = head.ToLowerInvariant();
        for (var at = lower.IndexOf("<meta", StringComparison.Ordinal);
             at >= 0;
             at = lower.IndexOf("<meta", at + 5, StringComparison.Ordinal))
        {
            var end = lower.IndexOf('>', at);
            if (end < 0)
                break;

            var tag = lower[at..end];
            var key = tag.IndexOf("charset", StringComparison.Ordinal);
            if (key < 0)
                continue;

            var value = tag[(key + "charset".Length)..].TrimStart();
            if (!value.StartsWith('='))
                continue;

            value = value[1..].TrimStart().TrimStart('"', '\'');
            var stop = value.IndexOfAny(['"', '\'', ';', ' ', '/', '>']);
            var charset = stop < 0 ? value : value[..stop];
            if (charset.Length > 0)
                return charset;
        }
        return null;
    }

    /// <summary>The bytes less a last character that the end of the buffer cut short, if there is
    /// one. A UTF-8 lead byte says how long its character is. A lead byte with fewer bytes after it
    /// than that is the cut.</summary>
    private static ReadOnlySpan<byte> WithoutCutTail(ReadOnlySpan<byte> bytes)
    {
        for (var back = 1; back <= 3 && back <= bytes.Length; back++)
        {
            var b = bytes[^back];
            if ((b & 0xC0) == 0x80)
                continue;   // a continuation byte: keep walking back to the lead

            var length = (b & 0xE0) == 0xC0 ? 2 : (b & 0xF0) == 0xE0 ? 3 : (b & 0xF8) == 0xF0 ? 4 : 1;
            return length > back ? bytes[..^back] : bytes;
        }
        return bytes;
    }

    /// <summary>Windows-1252: each byte is the code point of the same number, except 0x80 to 0x9F,
    /// which are the table below. The five bytes the code page leaves undefined keep their own
    /// number, as Windows does.</summary>
    private static string Windows1252(ReadOnlySpan<byte> bytes)
    {
        return string.Create(bytes.Length, bytes.ToArray(), static (chars, source) =>
        {
            for (var i = 0; i < source.Length; i++)
            {
                var b = source[i];
                chars[i] = b is >= 0x80 and <= 0x9F ? High[b - 0x80] : (char)b;
            }
        });
    }

    private static readonly char[] High =
    [
        '\u20AC', '\u0081', '\u201A', '\u0192', '\u201E', '\u2026', '\u2020', '\u2021',
        '\u02C6', '\u2030', '\u0160', '\u2039', '\u0152', '\u008D', '\u017D', '\u008F',
        '\u0090', '\u2018', '\u2019', '\u201C', '\u201D', '\u2022', '\u2013', '\u2014',
        '\u02DC', '\u2122', '\u0161', '\u203A', '\u0153', '\u009D', '\u017E', '\u0178',
    ];
}
