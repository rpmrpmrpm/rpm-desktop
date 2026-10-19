using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Dialogs;

/// <summary>
/// Shows dialogs, one at a time.
///
/// <para><b>Why this exists.</b> WinUI allows exactly one open <see cref="ContentDialog"/> per window.
/// A second <c>ShowAsync</c> throws "Only a single ContentDialog can be open at any time", and the
/// throw lands in the caller's <c>async void</c> click handler. Dialogs are often opened from inside
/// other dialogs. The album editor asks for a release and for cover art. The rip and split windows ask
/// for a release. Settings asks for a sign-in. The supporter window is opened without waiting, by
/// services that cannot see which dialogs are already open. The Skia head allows several dialogs at
/// once. WinUI does not.
/// </para>
///
/// <para><b>What happens instead.</b> A dialog opened while another is up makes the one underneath
/// stand aside: it is hidden, the new dialog runs, and it is shown again afterwards with its content
/// untouched. On Skia nothing stands aside, because nesting works there.</para>
///
/// <para><b>Standing aside fires Closed.</b> There is no way to hide a WinUI dialog without it, so a
/// dialog that tears anything down on close must hook through <see cref="WhenFinished"/>, and one that
/// starts work when it opens must hook through <see cref="WhenFirstOpened"/> or
/// <see cref="WhenFirstLoaded"/>. Hooking <c>Closed</c> or <c>Opened</c> directly means the teardown
/// runs while the dialog is only hidden, or the work restarts when it comes back.</para>
///
/// <para><b>Do not call <c>ContentDialog.ShowAsync</c> directly.</b> The compiler cannot enforce this,
/// so it is a rule rather than a type: every show in RPM.Desktop goes through
/// <see cref="ShowAsync"/>. A direct call causes the crash above, and it only appears when the user
/// happens to open the second window.</para>
///
/// <para>Everything here runs on the UI thread. The stack is a plain list for that reason: dialogs are
/// shown and hidden from dispatcher work, so there is no second thread to lock against.</para>
/// </summary>
public static class DialogHost
{
    private sealed class Frame(ContentDialog dialog)
    {
        public ContentDialog Dialog { get; } = dialog;

        /// <summary>How many times this dialog has been shown. Anything past the first is a resume.</summary>
        public int Shows;

#if RPM_WINUI
        /// <summary>Set while the dialog is hidden to make room for one opened on top of it.</summary>
        public bool StoodAside;

        /// <summary>Completes when the dialog has really closed after being hidden. The dialog on top
        /// may be shown only then.</summary>
        public TaskCompletionSource? Hidden;

        /// <summary>Completes when the dialog on top has closed and this one may come back.</summary>
        public TaskCompletionSource? Resumed;
#endif
    }

    private static readonly List<Frame> Stack = [];

    /// <summary>
    /// Shows a dialog and returns which button ended it. Whatever is already open stands aside first.
    /// The result comes from the real close, never from a stand-aside.
    /// </summary>
    public static async Task<ContentDialogResult> ShowAsync(ContentDialog dialog)
    {
        var frame = new Frame(dialog);

#if RPM_WINUI
        // The dialog underneath must be gone before this one can be shown. It is read before the new
        // frame is pushed, so a third dialog stands the second dialog aside, not the first.
        var below = Stack.Count > 0 ? Stack[^1] : null;
        if (below is not null)
            await StandAsideAsync(below);
#endif

        Stack.Add(frame);
        try
        {
            while (true)
            {
                frame.Shows++;
                var result = await dialog.ShowAsync();

#if RPM_WINUI
                // The close was ours, not the user's: let the dialog on top have its turn, then come back.
                if (frame.StoodAside)
                {
                    frame.Hidden!.TrySetResult();
                    await frame.Resumed!.Task;
                    frame.StoodAside = false;
                    frame.Hidden = frame.Resumed = null;
                    await NextTurnAsync();
                    continue;
                }
#endif
                return result;
            }
        }
        finally
        {
            Stack.Remove(frame);
#if RPM_WINUI
            below?.Resumed?.TrySetResult();
#endif
        }
    }

    /// <summary>
    /// Whether this dialog is being hidden to make room for one opened on top of it, rather than
    /// closed. True for as long as it is out of the way, including inside its own <c>Closed</c>
    /// handler.
    /// </summary>
    public static bool StandingAside(ContentDialog dialog)
#if RPM_WINUI
        => Find(dialog) is { StoodAside: true };
#else
        // Nothing stands aside on Skia: dialogs layer there, so a dialog that closes is finished with.
        => false;
#endif

    /// <summary>Whether this is the dialog's first time on screen, and not a return after a dialog on top
    /// of it closed.</summary>
    public static bool FirstShow(ContentDialog dialog) => Find(dialog) is not { Shows: > 1 };

    /// <summary>Hooks a handler to <c>Closed</c> that runs when the dialog is finished with, and not
    /// when it stands aside for a dialog opened on top of it.</summary>
    public static void WhenFinished(
        this ContentDialog dialog, TypedEventHandler<ContentDialog, ContentDialogClosedEventArgs> handler)
        => dialog.Closed += (sender, args) =>
        {
            if (!StandingAside(sender))
                handler(sender, args);
        };

    /// <summary>Hooks a handler to <c>Opened</c> that runs the first time the dialog is shown, and not
    /// when it comes back after standing aside.</summary>
    public static void WhenFirstOpened(
        this ContentDialog dialog, TypedEventHandler<ContentDialog, ContentDialogOpenedEventArgs> handler)
        => dialog.Opened += (sender, args) =>
        {
            if (FirstShow(sender))
                handler(sender, args);
        };

    /// <summary>The same for <c>Loaded</c>. It fires again on return, because a hidden dialog is
    /// removed from the tree.</summary>
    public static void WhenFirstLoaded(this ContentDialog dialog, RoutedEventHandler handler)
        => dialog.Loaded += (sender, args) =>
        {
            if (FirstShow(dialog))
                handler(sender, args);
        };

    private static Frame? Find(ContentDialog dialog)
    {
        foreach (var frame in Stack)
            if (ReferenceEquals(frame.Dialog, dialog))
                return frame;
        return null;
    }

#if RPM_WINUI
    /// <summary>Hides a dialog so another can be opened over it, and returns once it is really closed.</summary>
    private static async Task StandAsideAsync(Frame frame)
    {
        // Already standing aside: two dialogs asked to open over the same one. The second waits for the
        // first to finish, and then there is nothing left to hide.
        while (frame.StoodAside)
            await frame.Resumed!.Task;

        frame.StoodAside = true;
        frame.Hidden = new TaskCompletionSource();
        frame.Resumed = new TaskCompletionSource();
        frame.Dialog.Hide();

        await frame.Hidden.Task;

        // WinUI may still be finishing the close when the hidden dialog's ShowAsync completes. Showing
        // the next dialog in the same dispatcher turn throws the "single ContentDialog" error, so the
        // wait for the next turn is needed.
        await NextTurnAsync();
    }

    /// <summary>Waits for the next turn of the dispatcher.</summary>
    private static Task NextTurnAsync()
    {
        var done = new TaskCompletionSource();
        var queue = DispatcherQueue.GetForCurrentThread();
        if (queue is null || !queue.TryEnqueue(DispatcherQueuePriority.Normal, () => done.TrySetResult()))
            done.TrySetResult();
        return done.Task;
    }
#endif
}
