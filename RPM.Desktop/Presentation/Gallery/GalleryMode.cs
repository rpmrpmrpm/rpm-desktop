namespace RPM.Desktop.Presentation.Gallery;

/// <summary> Runtime switch for the <c>--gallery</c> design harness (DEBUG launches only). When it is
/// on, the app runs against a seeded demo library instead of the user's own. Mock stand-ins take the
/// place of the services that would otherwise need a network or a sign-in, so every screen can be
/// opened and driven with something to look at.
///
/// <para>One thing has to be turned off: the library sync. The demo rows name files that are not on
/// disk. A sync reconciles the mirror against the disk, so it would read every demo track as deleted
/// and empty the library. <see cref="LibraryService.SyncAsync"/> checks <see cref="Active"/> and does
/// nothing while the harness is up.</para>
///
/// <para><b>The run is signed out.</b> <see cref="Begin"/> takes the run's <see
/// cref="Services.Account.SessionHome.Harness"/>. Only the command line can produce that, and its
/// session store is in memory. So the gallery cannot start in a run that read the sign-in kept on this
/// computer. Nothing here has to be checked by a service that acts for the account.</para> </summary>
public static class GalleryMode
{
    /// <summary>True for the lifetime of a <c>--gallery</c> run. It defaults to false, so a normal
    /// launch and every Release build are unaffected.</summary>
    public static bool Active { get; private set; }

    /// <summary>The temp library that the demo catalogue was seeded into. The shell opens it on launch,
    /// instead of the configured library or a missing one.</summary>
    public static string? LibraryPath { get; private set; }

    /// <summary>Turns the harness on for <paramref name="libraryPath"/>.</summary>
    /// <param name="harness">The run's session home. It is asked for, not read, so a gallery run is one
    /// whose session store was built for a harness.</param>
    public static void Begin(Services.Account.SessionHome.Harness harness, string libraryPath)
    {
        Active = true;
        LibraryPath = libraryPath;
        RPM.Core.Diagnostics.Said.Line("auth", $"harness run ({harness.Flag}): the session store is in memory");
    }
}

/// <summary> "Open the workbench on this record." The navigation parameter that the gallery sends to
/// <see cref="Main.MainPage"/>, so it lands on one album's page rather than on the grid.
///
/// <para>This is a parameter, not a call. The album page is a view inside the library card, and the
/// card does not exist until the page it is on has been navigated to. The app itself never needs this,
/// because a record is reached by clicking one. The gallery is the only caller that must arrive already
/// looking at something.</para> </summary>
/// <param name="Folder">Library-relative album folder, forward slashes.</param>
public sealed record GalleryAlbumRequest(string Folder);
