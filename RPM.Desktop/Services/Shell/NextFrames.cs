using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Services.Shell;

/// <summary>Waits for the window to have DRAWN, rather than for a length of time.
/// <para><b>What goes wrong without it.</b> Layout and rendering are different turns. Code that sets a
/// property and then acts "a few milliseconds later" is guessing how many turns the renderer needs. The
/// guess is wrong on machines it was not tested on. Two places in the startup depend on the answer. The
/// splash card must not fade in before there is a card to fade. The main window must not be uncloaked
/// before it has drawn the interface. If it is uncloaked 64 ms after the reveal, it shows its previous
/// frame, the black splash, for about 300 ms.</para>
/// <para><b>The backstop is not optional.</b> Both callers are in a startup that holds the drawing
/// thread for hundreds of milliseconds at a time, and a cloaked window may be drawn at a rate of the
/// compositor's choosing rather than the display's. So the wait is bounded and says which way it ended.
/// A window that appears late without its first frame is better than one that never appears.</para>
/// <para><b>The event is raised on the dispatcher's thread.</b> The callback reports
/// <c>DispatcherQueue.HasThreadAccess</c> as true. So the promise that <see cref="After"/> makes, that
/// <c>then</c> runs on the dispatcher's thread, holds on both paths. What <c>then</c> does, uncloaking
/// one window and closing another, must happen on that thread.</para>
/// </summary>
internal static class NextFrames
{
    /// <summary>
    /// Runs <paramref name="then"/> once <paramref name="frames"/> frames have been drawn, or once
    /// <paramref name="backstop"/> has passed, whichever comes first. Called on the dispatcher's thread;
    /// <paramref name="then"/> runs on it too, exactly once.
    /// </summary>
    /// <param name="drawn">True when the frames arrived, false when the backstop fired. The two cases
    /// mean different things, so callers should record which one happened.</param>
    public static void After(
        DispatcherQueue? queue, int frames, TimeSpan backstop, Action<bool> then)
    {
        ArgumentNullException.ThrowIfNull(then);

        var done = false;
        var seen = 0;

        void Finish(bool drawn)
        {
            if (done)
                return;
            done = true;
            then(drawn);
        }

        void OnFrame(object? sender, object e)
        {
            if (++seen < frames)
                return;

            CompositionTarget.Rendering -= OnFrame;
            Finish(drawn: true);
        }

        CompositionTarget.Rendering += OnFrame;

        Ticker.Once(queue, backstop, () =>
        {
            CompositionTarget.Rendering -= OnFrame;
            Finish(drawn: false);
        });
    }
}
