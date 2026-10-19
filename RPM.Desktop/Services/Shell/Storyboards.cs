using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Starts a storyboard and keeps it alive until it has finished.
///
/// <para><b>The same defect as <see cref="Ticker"/>, in the other animation mechanism.</b> A storyboard
/// that is created in a local variable, started, and then left alone is not referred to by anything. Uno
/// keeps its own timelines reachable, so on the Skia head this has always worked. On WinUI the storyboard
/// is a projection over a native object. The managed peer can be collected as soon as Begin returns. The
/// animation then stops partway, or never starts, and nothing throws.</para>
///
/// <para>On the WinUI head the record is not visible at all in one case. Its motion is running, but the
/// record is INVISIBLE. The tween that flies it in from the corner and fades it up from nothing is one of
/// these storyboards. The disc stays where the turntable's constructor left it, at <c>ExitScale</c> with
/// zero opacity.</para>
///
/// <para>A storyboard is held until Completed, and only until Completed. A storyboard that is stopped
/// rather than finished stays held. The alternative is to poll every one of them. There are fourteen, and
/// all are short.</para>
/// </summary>
internal static class Storyboards
{
    private static readonly HashSet<Storyboard> Running = [];

    /// <summary>Begins <paramref name="board"/> and holds it for as long as it is running. A Completed
    /// handler that the caller has already attached still runs. This method adds its own.</summary>
    public static void Play(Storyboard board)
    {
        ArgumentNullException.ThrowIfNull(board);

        lock (Running)
            Running.Add(board);

        board.Completed += (_, _) =>
        {
            lock (Running)
                Running.Remove(board);
        };

        board.Begin();
    }
}
