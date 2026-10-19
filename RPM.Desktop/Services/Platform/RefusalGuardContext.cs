namespace RPM.Desktop.Services.Platform;

/// <summary>
/// The interface thread's synchronization context on the WinUI head, wrapped so a callback it runs
/// cannot end the process with an exception nobody records.
///
/// <para><b>Why the wrap is needed.</b> An exception that an <c>async void</c> handler throws after an
/// <c>await</c> is rethrown inside a callback that this context posts to the dispatcher queue. On WinUI
/// that path does not reach <c>Application.UnhandledException</c>. The process ends with 0xc000027b, a
/// stowed exception, in <c>CoreMessagingXP.dll</c>, and no managed handler runs. This happened when a new
/// playlist was created over a library database whose playlist table was missing. The refusal was reported,
/// and the app ended a millisecond later.</para>
///
/// <para><b>What it does with one.</b> A database refusal that the library has already put on screen
/// (<see cref="LibraryService.Reported"/>) is stopped here and written to the ring. The problem screen
/// covers it,
/// and ending the process would take that screen down. Any other exception is recorded as a crash and then
/// rethrown. The app still fails as it did, but the managed exception is written down, not only Windows'
/// report of the stowed one.</para>
///
/// <para><b>Not covered:</b> a callback handed straight to <c>DispatcherQueue.TryEnqueue</c> does not go
/// through any synchronization context. Two Windows crashes were of this kind. The problem screen failed to
/// load inside the shell's queued callback (a XamlParseException) before the async void handler's exception
/// was ever posted. So the guard does not see those callbacks.</para>
/// </summary>
/// <param name="inner">The context the callbacks really run on: the dispatcher queue's own.</param>
/// <param name="onScreen">Whether an exception is a refusal already on screen.</param>
/// <param name="crashed">Records an exception that is about to end the process.</param>
public sealed class RefusalGuardContext(
    SynchronizationContext inner, Func<Exception, bool> onScreen, Action<Exception> crashed)
    : SynchronizationContext
{
    public override void Post(SendOrPostCallback d, object? state) => inner.Post(s => Run(d, s), state);

    public override void Send(SendOrPostCallback d, object? state) => inner.Send(s => Run(d, s), state);

    public override SynchronizationContext CreateCopy()
        => new RefusalGuardContext(inner.CreateCopy(), onScreen, crashed);

    // An async void method tells its context when it starts and ends; the dispatcher's context is told
    // the same, as it would be without the wrap.
    public override void OperationStarted()
    {
        // Logged once per run, as proof that an async void handler captured the guard.
        if (Interlocked.Exchange(ref _firstHandlerSaid, 1) == 0)
            RPM.Core.Diagnostics.Said.Line("library", "an async void handler started under the guard");

        inner.OperationStarted();
    }

    private static int _firstHandlerSaid;

    /// <summary>Makes the guard the calling thread's context, over <paramref name="inner"/>. Called once,
    /// on the interface thread, inside <c>Application.Start</c>.</summary>
    public static void Install(SynchronizationContext inner, Func<Exception, bool> onScreen, Action<Exception> crashed)
    {
        SetSynchronizationContext(new RefusalGuardContext(inner, onScreen, crashed));
        RPM.Core.Diagnostics.Said.Line("library", $"interface thread guarded, over {inner.GetType().Name}");
    }

    public override void OperationCompleted() => inner.OperationCompleted();

    private void Run(SendOrPostCallback d, object? state)
    {
        try
        {
            d(state);
        }
        catch (Exception error) when (onScreen(error))
        {
            RPM.Core.Diagnostics.Said.Line("library",
                $"a refusal already on screen reached the dispatcher ({error.GetType().Name})");
        }
        catch (Exception error) when (Crashed(error))
        {
            // Never entered: the filter records the crash and answers false, so the exception leaves
            // with its stack as it was.
        }
    }

    private bool Crashed(Exception error)
    {
        crashed(error);
        return false;
    }
}
