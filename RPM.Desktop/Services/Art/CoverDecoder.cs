using System.Runtime.InteropServices.WindowsRuntime;
using Microsoft.UI.Xaml.Media.Imaging;
using SkiaSharp;

namespace RPM.Desktop.Services.Art;

/// <summary>
/// Turns a cover's bytes into pixels at the size a square needs.
///
/// <para>ONE dimension is constrained, never both. Constraining both decodes to exactly that box and
/// stretches a cover that is not square. WHICH dimension depends on the picture. Every surface that draws a
/// cover in a square crops it with <c>UniformToFill</c>. So the SHORT side must reach the square's size. If
/// the width of a landscape sleeve were constrained, its height would fall short of the square, and the
/// whole picture would be scaled up to fill it. That is why only some covers looked soft. The square ones,
/// which are most of them, looked sharp.</para>
///
/// <para>The other half of that rule is on the way IN. See <see cref="AlbumCovers.LoadForSquareAsync"/>,
/// which makes sure the bytes have those pixels to give.</para>
///
/// <para><b>Why this decodes the picture itself, instead of giving the bytes to a <c>BitmapImage</c>.</b>
/// <c>BitmapSource.SetSourceAsync</c> cannot be called without throwing. Its <c>ForceLoad</c> subscribes,
/// which opens the source. Then it invalidates, which opens the source a second time.
/// <c>ImageSource._opening</c> is a <c>SerialDisposable</c>, so the second open cancels the first. That
/// gives one <c>TaskCanceledException</c> for every picture, always, even when nobody is scrolling and
/// nothing is wrong. This was checked in the Uno 6.7 source, not inferred.</para>
///
/// <para>Without a debugger this cost is small. With a debugger attached it is the whole problem. Every
/// managed throw goes through <c>Debugger::ManagedExceptionUnwindBegin</c>. That takes the global
/// thread-store lock, which a garbage collection needs in order to finish suspending the threads. So throws
/// serialise the process against each other and against every collection. In one measurement, a debug
/// launch spent 12,484 ms in 29 collections of a 184 MB heap, and was unusable for about twenty seconds. The
/// same build with no debugger spent 65 ms in 17 collections. The collections were the same. The suspensions
/// were not.</para>
///
/// <para><b><see cref="WriteableBitmap"/> cannot take that path.</b> Its <c>TryOpenSourceSync</c> returns
/// true. So <c>ImageSource.Open</c> takes the synchronous branch. It builds no <c>Task</c>, and it gives its
/// cancellation token to nobody. Nothing in this path can raise a cancellation. That is a stronger guarantee
/// than raising fewer of them.</para>
///
/// <para><b>What it costs.</b> The pixels are held twice while the picture is alive. Once in the bitmap's own
/// buffer, and once in the composition surface, which <c>CopyPixels</c> fills with
/// <c>SKImage.FromPixelCopy</c>. That is about 1.6 MB each at <see cref="ArtScaler.TileEdge"/>. This applies
/// only to the sharp tier, which is held for what is on screen. The cost was accepted on purpose.</para>
/// </summary>
public static class CoverDecoder
{
    /// <summary>What a decode cost, for the walls' scroll diagnostics. It is free to leave unset.</summary>
    public static Action<double>? Timed { get; set; }

    /// <summary>
    /// The cover at <paramref name="edge"/> on its short side, ready to draw.
    /// </summary>
    /// <remarks>
    /// <para>Synchronous, and returned as a completed task so that every caller can still await it. That
    /// matters for timing. <c>CoverStream.Decode</c> times this call and books the result against
    /// <see cref="FrameBudget"/>. If this were an <c>await</c> across <c>DispatcherQueue.TryEnqueue</c>, the
    /// timing would include the time spent waiting in the queue, and the budget would be charged for waiting.
    /// Keeping all the work here means the measurement covers only the work.</para>
    ///
    /// <para>The decode stays on the calling thread, which is the drawing thread. Moving it to a worker is
    /// the obvious next step. It is a separate change, because it alters what the budget measures, and the
    /// two should be measured apart.</para>
    /// </remarks>
    public static Task<WriteableBitmap> Async(byte[] bytes, int edge)
    {
        var clock = Timed is null ? null : System.Diagnostics.Stopwatch.StartNew();

        using var source = SKBitmap.Decode(bytes)
            ?? throw new ArgumentException("not a picture this decoder knows", nameof(bytes));

        var (width, height) = FitShortSide(source.Width, source.Height, edge);

        // Bgra8888 with Premul alpha. The bitmap's buffer holds that format, and CopyPixels reads it that way.
        // Any other format gives a picture with red and blue swapped, and no error is raised.
        var info = new SKImageInfo(width, height, SKColorType.Bgra8888, SKAlphaType.Premul);

        // The sampling is the app's own rule, not a quality setting chosen here. Cubic sampling is used while
        // the picture barely shrinks. Mipmaps are used past half size, because at that point sixteen source
        // pixels per destination pixel is a sample, not a filter. See ArtScaler.SamplingFor, which has the
        // measurements behind it.
        var shortest = Math.Min(source.Width, source.Height);
        using var scaled = source.Resize(info, ArtScaler.SamplingFor(shortest, Math.Min(edge, shortest)))
            ?? throw new ArgumentException("could not be resampled", nameof(bytes));

        var bitmap = new WriteableBitmap(width, height);
        using (var buffer = bitmap.PixelBuffer.AsStream())
            buffer.Write(scaled.GetPixelSpan());
        bitmap.Invalidate();

        if (clock is not null)
            Timed?.Invoke(clock.Elapsed.TotalMilliseconds);

        return Task.FromResult(bitmap);
    }

    /// <summary>
    /// The size to decode at: <paramref name="edge"/> on the SHORT side, the long side in proportion, and
    /// never larger than the picture actually is.
    /// </summary>
    /// <remarks>
    /// The clamp is the one behaviour that is not a copy of what <c>DecodePixelWidth</c> did. That method
    /// enlarges a small sleeve to fill the box. Enlarging during the decode and enlarging on the way to the
    /// screen look the same. But the decode holds four bytes for each invented pixel.
    /// </remarks>
    private static (int Width, int Height) FitShortSide(int width, int height, int edge)
    {
        if (width <= 0 || height <= 0)
            return (Math.Max(1, width), Math.Max(1, height));

        var shortest = Math.Min(width, height);
        if (shortest <= edge)
            return (width, height);

        var k = (double)edge / shortest;
        return (Math.Max(1, (int)Math.Round(width * k)), Math.Max(1, (int)Math.Round(height * k)));
    }
}
