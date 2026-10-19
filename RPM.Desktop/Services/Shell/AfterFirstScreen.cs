namespace RPM.Desktop.Services.Shell;

/// <summary>Work that must not run before the first screen is shown.</summary>
/// <remarks>
/// <para><b>What this is for.</b> The splash comes off when home has something on it, and home gets it
/// by reading the collection. The library's reads are serialised: one gate, one reader at a time. So
/// anything else that reads the collection while home is reading is not merely sharing the machine with
/// it. It delays home. In one measurement on a 13,000-track collection, home's listening
/// profile waited 1,015 ms behind <c>LibraryAlbumIndex</c>'s artist credits. That read was for a view
/// that home was covering.</para>
/// <para><b>Why not the governor.</b> <see cref="Governor"/> answers for work that runs in a loop and
/// can be told to go away and come back. This work happens once, on the drawing thread, and it must
/// happen. The album views have to be fed and the playlist tree has to exist, whether or not anybody
/// opens either. There is nothing to refuse here, only something to order.</para>
/// <para><b>Asked, not told.</b> <see cref="FirstScreen"/> is a latch with no event, on purpose, so
/// that a screen which came up before anybody subscribed to it cannot be missed. This keeps that rule.
/// It reads the latch on each turn that the drawing thread already reports through
/// <see cref="UiPulse"/>. That is the same beat the governor's waves use. There is no second timer, no
/// event to miss, and no gap to fall into. <see cref="Run"/> called after the screen is already up runs
/// its work straight away.</para>
/// <para><b>And a ceiling, because the screen is not a promise.</b> A collection that cannot be opened,
/// a read that throws before <c>HomeView.ShowScreen</c>, or a pulse that stops: any of these leaves the
/// latch false for the rest of the run. The work here is not optional, so it runs anyway at
/// <see cref="AtLatest"/>. This is the same design as the reveal's own backstop, for the same
/// reason.</para>
/// </remarks>
internal static class AfterFirstScreen
{
    /// <summary>How long work waits for the first screen before it runs regardless. Long enough that
    /// a slow collection still gets the order right, short enough that a startup which never reports
    /// a screen is not a session with half a page unwired.</summary>
    private static readonly TimeSpan AtLatest = TimeSpan.FromSeconds(20);

    /// <summary>
    /// Runs <paramref name="work"/> on the drawing thread once the first screen has its content, or
    /// straight away if it already has.
    /// </summary>
    /// <param name="work">What to do. It runs exactly once, on the thread the pulse beats on, which
    /// is the drawing thread.</param>
    public static void Run(Action work)
    {
        ArgumentNullException.ThrowIfNull(work);

        var screen = App.Services.GetService<FirstScreen>();
        var pulse = App.Services.GetService<Governor>()?.Pulse;

        // No pulse and no latch is the headless host and the design harness: there is no first screen
        // to be in front of, so there is nothing to wait for.
        if (screen is null || pulse is null || screen.IsUp)
        {
            work();
            return;
        }

        var from = App.Uptime;
        pulse.Landed += Turn;

        void Turn(Turn _)
        {
            if (!screen.IsUp && App.Uptime - from < AtLatest)
                return;

            pulse.Landed -= Turn;
            work();
        }
    }
}
