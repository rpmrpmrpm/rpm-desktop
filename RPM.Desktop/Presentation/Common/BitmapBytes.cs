using Windows.Storage.Streams;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Bytes as a stream a <c>BitmapImage</c> can be given, in the form the head in use needs.
///
/// <para><b>The two heads need opposite things.</b> On Uno, the answer is a MemoryStream behind
/// <c>AsRandomAccessStream</c>. InMemoryRandomAccessStream was tried on Uno and did not work there. On
/// WinUI, the MemoryStream shape is the wrong one. The adapter wraps a managed stream, which is not
/// thread-safe. WinUI decodes images through WIC on its own threads, and it opens the stream more than
/// once.</para>
///
/// <para>On WinUI, a managed stream is never usable here. The record does not appear at all. The
/// <c>VinylPressing</c> method awaits <c>SetSourceAsync(stream.AsRandomAccessStream())</c>, and with a
/// managed stream that await never returns. The record then never drops onto the turntable.</para>
///
/// <para>InMemoryRandomAccessStream is a real WinRT stream. It is a native buffer that can be cloned
/// correctly, and it can be opened from more than one thread at a time. This method fills it
/// synchronously, so it can replace the existing call sites. Most of those use <c>using var stream =
/// ...</c> followed by an awaited SetSourceAsync.</para>
/// </summary>
internal static class BitmapBytes
{
    /// <summary>
    /// The bytes as a stream, positioned at the start.
    ///
    /// <para><b>Do not dispose the returned stream, and do not put it in a using.</b> A BitmapImage keeps
    /// its stream and opens it again on every draw. It does not read the stream once and let go. If the
    /// stream is disposed at the end of the statement that created it, the next open fails. On Uno that
    /// is a cancelled read and three first-chance exceptions for each picture. On WinUI it is an
    /// ObjectDisposedException, thrown into a task that nobody awaits.</para>
    ///
    /// <para>Nothing is leaked. The bytes belong to the caller, the stream holds no file handle, and the
    /// stream is collected together with the bitmap that keeps it.</para>
    /// </summary>
    public static IRandomAccessStream Stream(byte[] bytes)
    {
        ArgumentNullException.ThrowIfNull(bytes);

#if RPM_WINUI
        var stream = new InMemoryRandomAccessStream();

        // The writer is NOT disposed, on purpose. AsStreamForWrite returns a wrapper over this same
        // stream. Disposing the wrapper closes the stream underneath it. A using here would close the
        // stream before it is returned, and every later read would throw ObjectDisposedException. The
        // writer is flushed instead. The wrapper owns nothing else, so the GC collects it.
        var writing = stream.AsStreamForWrite();
        writing.Write(bytes, 0, bytes.Length);
        writing.Flush();

        stream.Seek(0);
        return stream;
#else
        return new MemoryStream(bytes).AsRandomAccessStream();
#endif
    }
}
