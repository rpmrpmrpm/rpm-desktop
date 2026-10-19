using RPM.Core.Library;
using Microsoft.UI.Xaml;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The problem screen over the running library. It shows when a read of the open collection fails,
/// and it is taken down when the collection has no trouble.
///
/// <para>This is an overlay, not a navigation in the shell's frame. The library page is never torn down
/// and rebuilt during one run, and several singletons hold handlers on it. The Settings folder change
/// swaps the mirror while the page stays up, and this screen uses that same path. Try again and Choose
/// another folder both go that way.</para>
///
/// <para>Both directions read <see cref="LibraryService.Trouble"/> on the interface thread, rather than
/// acting on the event that asked. So the order the two events arrive in does not matter. A Try again
/// that succeeds before a stale refusal is handled leaves nothing to show for it. An open whose own
/// reads fail again keeps the screen up.</para>
/// </summary>
public sealed partial class ShellPage
{
    /// <summary>Subscribes once, from the constructor. The shell lives for the whole run.</summary>
    private void WatchLibraryTrouble()
    {
        var library = App.Services.GetRequiredService<LibraryService>();

        // Troubled arrives on the thread of the read that failed, Opened on the thread that opened.
        library.Troubled += () => DispatcherQueue.TryEnqueue(() => Queued("show", ShowLibraryTrouble));
        library.Opened += _ => DispatcherQueue.TryEnqueue(() => Queued("take down", TakeDownLibraryTrouble));

        // A refusal that arrives while setup or the landing's problem screen is up is shown later. The
        // check runs when the library page arrives, not when the refusal is raised.
        RootFrame.Navigated += (_, _) => ShowLibraryTrouble();
    }

    /// <summary>
    /// Runs a callback from the dispatcher queue. It writes down any exception before letting the
    /// exception continue. On WinUI, an exception thrown here ends the process as a stowed exception
    /// with no message. It does not pass through the synchronization context (RefusalGuardContext does
    /// not see it), and it never reaches <c>App.UnhandledException</c>.
    ///
    /// <para>On Windows, a throw here once ended the process with a stowed XAML load failure (WER P7
    /// 802b000a). The cause was <c>LibraryProblemPage</c> giving a <c>RowDefinition</c> a double, which
    /// WinUI refuses. XamlResourceTypeTests fails on that.</para>
    /// </summary>
    private static void Queued(string what, Action callback)
    {
        try { callback(); }
        catch (Exception error) when (WrittenDown(what, error)) { }
    }

    /// <summary>The filter's side: always false, so the exception continues unchanged from where it was
    /// thrown.</summary>
    private static bool WrittenDown(string what, Exception error)
    {
        try { RPM.Core.Diagnostics.Said.Line("library", $"the problem screen failed to {what}: {error}"); } catch { }
#if RPM_WINUI
        // A crash record is written on WinUI only, where this throw ends the process. The Uno Skia
        // dispatcher logs the throw and carries on, so a record there would report a crash that did not
        // happen.
        try { App.Crashes.Record(error, "library trouble"); } catch { }
#endif
        return false;
    }

    private void ShowLibraryTrouble()
    {
        if (App.Services.GetRequiredService<LibraryService>().Trouble is not { } trouble)
            return;

        // Already shown. Its own Try again button reads the collection again and shows the result.
        if (ProblemFrame.Visibility == Visibility.Visible)
            return;

        // Setup, or the landing's problem screen: both replace the library and own the open. The
        // navigation handler above checks again when the library page replaces them.
        if (RootFrame.Content is not MainPage)
            return;

        PutUpLibraryTrouble(trouble);
    }

    /// <summary>Shows the problem screen over the library for <paramref name="trouble"/>. The gallery
    /// also calls this to show a fake trouble, without breaking a database.</summary>
    internal void PutUpLibraryTrouble(LibraryOpening.Trouble trouble)
    {
        // Switch to the library view, so the screen is not shown under Pegasus or squeezed into the
        // skin's window. The switch is queued, so for one turn the screen is shown under whichever view
        // is leaving.
        App.Services.GetRequiredService<ViewModeService>().Switch(AppView.Library);
        if (VizOverlay.IsShowing)
            VizOverlay.Hide();

        ProblemFrame.Navigate(typeof(LibraryProblemPage), new LibraryProblem(trouble, ProblemPlace.OverLibrary));
        ProblemFrame.BackStack.Clear();
        ProblemFrame.Visibility = Visibility.Visible;
    }

    private void TakeDownLibraryTrouble()
    {
        if (ProblemFrame.Visibility != Visibility.Visible)
            return;

        // The open that raised this failed its own reads too. The screen stays, showing what its Try
        // again found.
        if (App.Services.GetRequiredService<LibraryService>().Trouble is not null)
            return;

        ProblemFrame.Visibility = Visibility.Collapsed;
        ProblemFrame.Content = null;
        ProblemFrame.BackStack.Clear();
    }
}
