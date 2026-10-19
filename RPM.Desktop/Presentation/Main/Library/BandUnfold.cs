using RPM.Core.Library;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Main.Library;

/// <summary>
/// The drawer's unfold and fold. One element's height and opacity are moved to a target one frame at a
/// time. At most one move is live at a time.
///
/// <para><b>Height, not a render transform.</b> A transform does not move other elements. A drawer that
/// scaled open would have its full room made in one frame and then filled. The view would jump, and the
/// unfold would play inside a gap that already existed. Growing the height pushes the content below the
/// drawer down as the drawer opens. This is the only way the space and the drawer arrive together. The
/// clip comes with it: the content is measured at its full length, and without a clip it would spill out
/// of a box that is still short.</para>
///
/// <para><b>Not a Storyboard.</b> On Uno-Skia, a finished Storyboard keeps its animated value. Every
/// later local write then loses to it. A recycled drawer would then stay invisible while its row's
/// space was still reserved. Plain local writes made on each render tick can always be overwritten by
/// whoever owns the element next.</para>
///
/// <para><b>One per surface, not one per process.</b> The wall has one open drawer, and an artist's page
/// has its own. They are different drawers. A single static walk would let the wall's fold cancel the
/// page's unfold. Whoever owns the drawers owns one of these.</para>
///
/// <para>If a walk is cancelled, the height stays where it is. The one who cancelled owns the element,
/// and uses that height as its own starting point.</para>
/// </summary>
public sealed class BandUnfold
{
    private sealed class Run
    {
        public required FrameworkElement Root;
        public bool Cancelled;
    }

    private Run? _live;

    /// <summary>Whether a walk is running on this element right now. Every realize-time visual sync must
    /// check this before it snaps the drawer open or shut. Snapping over a running unfold causes a flash:
    /// the drawer is drawn for a moment, and then it animates.</summary>
    public bool Owns(FrameworkElement root)
        => _live is { Cancelled: false } run && ReferenceEquals(run.Root, root);

    /// <summary>Stops whatever is running, wherever it has got to. The element keeps the height and
    /// opacity of the last frame written. The next owner starts from there.</summary>
    public void Stop()
    {
        if (_live is { } run)
            run.Cancelled = true;
        _live = null;
    }

    /// <summary>
    /// How tall a drawer wants to be. This does NOT run a layout pass to find out.
    ///
    /// <para><c>UpdateLayout</c> is not used here. It is a synchronous pass over the whole view. On the
    /// wall that pass does not settle. The rows are measured by AlbumGridLayout, and the drawer inside
    /// one of them is having its height written. Re-entering the pass from the middle of it trips Uno's
    /// iteration guard. The exception would be thrown wherever the call was made, even though the cause
    /// is the nested pass.</para>
    ///
    /// <para><c>Measure</c> answers the same question and does not change anything else. It sizes this
    /// element and its own children against the width it already has, and leaves <c>DesiredSize</c> set.
    /// The height is unbounded downwards, because the question is how tall this would be if nothing
    /// stopped it.</para>
    ///
    /// <para>The value returned is the height INSIDE the drawer's own margin, because the caller writes
    /// it to <c>Height</c>. A measure reports the room the element needs in its parent, margin included.
    /// See <see cref="AlbumGridLayout.DrawerLength"/>.</para>
    /// </summary>
    /// <param name="fallbackWidth">The width to measure against when the element has no width of its own
    /// yet. The wall passes its scroller's width. Zero is passed when the caller has nothing better. Zero
    /// comes back as zero, and is read as "nothing to unfold".</param>
    public static double Wanted(FrameworkElement root, double fallbackWidth)
    {
        var width = root.ActualWidth > 0 ? root.ActualWidth : fallbackWidth;
        if (width <= 0)
            return 0;

        root.Measure(new Windows.Foundation.Size(width, double.PositiveInfinity));
        return AlbumGridLayout.DrawerLength(root.DesiredSize.Height, root.Margin.Top, root.Margin.Bottom);
    }

    /// <summary>
    /// Walks <paramref name="root"/> from its current state to <paramref name="toHeight"/> and
    /// <paramref name="toOpacity"/>. The walk eases out for an unfold and eases in for a fold. A fold
    /// that starts during an unfold folds from the current, partly open state. It does not jump to full
    /// first. The task completes when the walk finishes, or when a new walk replaces it.
    /// </summary>
    /// <param name="releaseToAuto">Sets the height back to Auto at the end, for a drawer that has arrived
    /// and must lay out freely afterwards. Pass false for a fold. The caller of a fold resets the element
    /// itself, after it has detached what was in it.</param>
    public Task To(FrameworkElement root, double toHeight, double toOpacity,
                   double durMs, bool easeOut, bool releaseToAuto)
    {
        Stop();
        var run = new Run { Root = root };
        _live = run;

        // The parent's width is used when the element has no width of its own. This happens when a drawer
        // starts to open before any layout pass has run with it visible. Without a width there is no
        // clip. A drawer measured at its full length would then draw all of its content while its box is
        // still short. The content would appear at once, and only the gap under it would grow. A clip
        // wider than the element costs nothing, because its job is the cut across the bottom.
        var width = root.ActualWidth > 0 ? root.ActualWidth : (root.Parent as FrameworkElement)?.ActualWidth ?? 0;
        var fromHeight = double.IsNaN(root.Height) ? root.ActualHeight : root.Height;
        var fromOpacity = root.Opacity;
        var clip = width > 0
            ? new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, 0, width, fromHeight) }
            : null;
        root.Clip = clip;
        root.Height = fromHeight;

        var done = new TaskCompletionSource();
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var startMs = -1d;   // the clock starts on the first real frame, not on the subscribe
        EventHandler<object>? tick = null;
        tick = (_, _) =>
        {
            if (run.Cancelled)
            {
                CompositionTarget.Rendering -= tick;
                done.TrySetResult();
                return;
            }

            var nowMs = clock.Elapsed.TotalMilliseconds;
            if (startMs < 0)
                startMs = nowMs;

            var t = Math.Min(1, (nowMs - startMs) / durMs);
            var e = easeOut ? 1 - Math.Pow(1 - t, 3) : t * t * t;
            var h = fromHeight + ((toHeight - fromHeight) * e);
            root.Height = h;
            if (clip is not null)
                clip.Rect = new Windows.Foundation.Rect(0, 0, width, h);
            root.Opacity = fromOpacity + ((toOpacity - fromOpacity) * e);

            if (t < 1)
                return;

            CompositionTarget.Rendering -= tick;
            if (releaseToAuto)
            {
                root.Height = double.NaN;
                root.Clip = null;
            }
            if (ReferenceEquals(_live, run))
                _live = null;
            done.TrySetResult();
        };
        CompositionTarget.Rendering += tick;
        return done.Task;
    }
}
