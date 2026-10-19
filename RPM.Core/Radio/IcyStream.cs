using System.Text;

namespace RPM.Core.Radio;

/// <summary>
/// Removes the metadata from a SHOUTcast or Icecast stream.
///
/// <para>A station that is asked for metadata (<c>Icy-MetaData: 1</c>) answers with an <c>icy-metaint</c>
/// header and then mixes the two. After every <c>metaint</c> bytes of audio comes one length byte. That is
/// followed by the given number of sixteen-byte blocks of text. The text is usually <c>StreamTitle='Artist
/// - Title';</c>. It is usually unchanged, and most of the time it is empty (length zero). A decoder given
/// the raw stream would try to decode that text as audio. So this class removes the text, passes the audio
/// on untouched, and reports each new title as it passes.</para>
///
/// <para>The stream is read-only and forward-only, which is what a live stream is.</para>
/// </summary>
public sealed class IcyStream : Stream
{
    private readonly Stream _inner;
    private readonly int _metaInt;
    private int _untilMeta;
    private byte[] _meta = new byte[16 * 255];

    /// <param name="metaInt">Bytes of audio between metadata blocks, from the <c>icy-metaint</c> response
    /// header.</param>
    public IcyStream(Stream inner, int metaInt)
    {
        ArgumentNullException.ThrowIfNull(inner);
        ArgumentOutOfRangeException.ThrowIfLessThan(metaInt, 1);
        _inner = inner;
        _metaInt = metaInt;
        _untilMeta = metaInt;
    }

    /// <summary>The track the station says is playing, or null before it has said. Raised on the thread
    /// doing the reading, so a listener that updates the UI must switch to the UI thread itself.</summary>
    public event Action<string>? TitleChanged;

    public string? Title { get; private set; }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (count <= 0)
            return 0;

        if (_untilMeta == 0)
        {
            ReadMetadata();
            _untilMeta = _metaInt;
        }

        // Never read past the next metadata block. The boundary is counted in bytes of audio, so reading
        // even one byte too far puts every later block out of step, and the text starts reaching the
        // decoder.
        int n = _inner.Read(buffer, offset, Math.Min(count, _untilMeta));
        if (n > 0)
            _untilMeta -= n;
        return n;
    }

    /// <summary>Reads one metadata block: a length byte, then that many sixteen-byte chunks. A length of
    /// zero is the common case, because stations only send new text when the title changes. It costs one
    /// byte.</summary>
    private void ReadMetadata()
    {
        int length = _inner.ReadByte();
        if (length <= 0)
            return;                       // 0 = unchanged, -1 = the stream ended (the next Read reports it)

        int bytes = length * 16;
        if (_meta.Length < bytes)
            _meta = new byte[bytes];
        _inner.ReadExactly(_meta, 0, bytes);

        // The protocol does not name an encoding. Stations use both UTF-8 and Windows-1252. ForeignText
        // reads the bytes as UTF-8 when they are valid UTF-8, and as Windows-1252 otherwise. Reading
        // everything as UTF-8 would show U+FFFD where a letter such as the "ë" in "Raphaël" is a single
        // byte.
        var title = ParseTitle(RPM.Core.Files.ForeignText.Decode(_meta.AsSpan(0, bytes),
                                                                RPM.Core.Files.ForeignText.Ending.Whole));
        if (title is null || string.Equals(title, Title, StringComparison.Ordinal))
            return;

        Title = title;
        TitleChanged?.Invoke(title);
    }

    /// <summary>
    /// Pulls the title out of a metadata block: <c>StreamTitle='…';StreamUrl='…';</c>, padded with nulls to
    /// a multiple of sixteen. Titles can contain apostrophes, so the value ends at the first <c>';</c>, not
    /// at the first quote. It also ends at the end of the text when the station leaves out the terminator,
    /// which some do.
    /// </summary>
    public static string? ParseTitle(string block)
    {
        const string Key = "StreamTitle='";
        int start = block.IndexOf(Key, StringComparison.Ordinal);
        if (start < 0)
            return null;
        start += Key.Length;

        int end = block.IndexOf("';", start, StringComparison.Ordinal);
        if (end < 0)
            end = block.IndexOf('\0', start);
        if (end < 0)
            end = block.Length;

        var title = block[start..end].Trim();
        return title.Length == 0 ? null : title;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
            _inner.Dispose();
        base.Dispose(disposing);
    }

    public override bool CanRead => true;
    public override bool CanSeek => false;
    public override bool CanWrite => false;
    public override long Length => throw new NotSupportedException();
    public override long Position { get => throw new NotSupportedException(); set => throw new NotSupportedException(); }
    public override void Flush() { }
    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();
    public override void SetLength(long value) => throw new NotSupportedException();
    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
