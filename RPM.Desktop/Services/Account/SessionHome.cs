namespace RPM.Desktop.Services.Account;

/// <summary>
/// Whose sign-in a run may read: the one kept on this computer, or none at all.
///
/// <para><b>A design-harness run reads none.</b> <c>--gallery</c> and every flag that starts with it
/// run the whole app against a demo library. If a harness run were signed in, every service that acts
/// for the account would act on that demo library. Listening lists would go to the owner's phone and
/// TV, and the cloud upload would queue the demo tracks. A Debug build on Windows reads the same DPAPI
/// session as the installed app, so this really happens. A harness run therefore gets an in-memory
/// store that is empty at launch (<see cref="SessionStore.For"/>), and nothing under the app's local
/// data folder is read. A service that checks <c>IsLoggedIn</c>, including one written later, is
/// covered without knowing that the harness exists.</para>
///
/// <para><b>It is decided once, from the command line, by <see cref="For"/>.</b> A <see cref="Harness"/>
/// can be made only this way, and starting the gallery needs one
/// (<see cref="Presentation.Gallery.GalleryMode.Begin"/>). So a gallery run that uses this computer's
/// sign-in does not compile. Release builds have no harness. <see cref="For"/> always returns
/// <see cref="ThisMachine"/> there.</para>
/// </summary>
public abstract record SessionHome
{
    private SessionHome() { }

    /// <summary>The sign-in kept on this computer: the keychain, DPAPI or the sealed file, whichever
    /// the build can use. Used on every ordinary launch.</summary>
    public sealed record ThisMachine : SessionHome;

    /// <summary>No stored sign-in from anywhere. The store is in memory: it is empty at launch and
    /// gone at exit.</summary>
    public sealed record Harness : SessionHome
    {
        private Harness(string flag) => Flag = flag;

        /// <summary>The flag that made this run a harness, for the log.</summary>
        public string Flag { get; }

        /// <summary>The harness flag on the command line, or null. Every flag that starts with
        /// <c>--gallery</c> counts, so a variant added later (<c>--gallery-shelf</c>) is a harness
        /// without anyone having to list it here.</summary>
        internal static Harness? In(IReadOnlyList<string> commandLine)
        {
#if DEBUG
            foreach (var arg in commandLine)
                if (arg.StartsWith("--gallery", StringComparison.Ordinal))
                    return new Harness(arg);
#endif
            return null;
        }
    }

    /// <summary>Which sign-in the run may read, given the command line it started with.</summary>
    public static SessionHome For(IReadOnlyList<string> commandLine) =>
        Harness.In(commandLine) is { } harness ? harness : new ThisMachine();
}
