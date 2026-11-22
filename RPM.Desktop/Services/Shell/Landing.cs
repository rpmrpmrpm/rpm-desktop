using RPM.Core.Library;

namespace RPM.Desktop.Services.Shell;

/// <summary>
/// Opening the saved collection, and the screen that goes up for each outcome. One place, because
/// two things do it: the shell at launch, and the problem screen's Try again.
///
/// <para>Both halves are here so they cannot drift apart. The decision itself is
/// <see cref="OpeningScreen.Choose"/>, which is pure and tested; what reaches the disk is
/// <see cref="LibraryService.OpenForLanding"/>, which names every way the open can fail.</para>
/// </summary>
internal static class Landing
{
    /// <summary>
    /// Opens the saved collection and says what happened. Nothing is thrown: a failure is one of the
    /// <see cref="LibraryOpening.Trouble"/> answers.
    /// </summary>
    public static LibraryOpening Attempt()
    {
        // The harness opens the seeded demo library and never the user's own, so Try again on the
        // gallery's problem screens lands on the demo collection. GalleryMode.Active is false in
        // every real launch.
        if (GalleryMode.Active && GalleryMode.LibraryPath is { } galleryLibrary)
            return App.Services.GetRequiredService<LibraryService>().OpenForLanding(galleryLibrary);

        var settings = App.Services.GetRequiredService<SettingsService>();
#if DEBUG
        // Dev shortcut: `dotnet run -- --library <path>` opens that library for this run without
        // saving it. Here rather than in the shell, so Try again retries the same folder.
        var args = Environment.GetCommandLineArgs();
        var idx = Array.IndexOf(args, "--library");
        var path = idx >= 0 && idx + 1 < args.Length ? args[idx + 1] : settings.LibraryPath;
#else
        var path = settings.LibraryPath;
#endif
        // A library on a NAS: mount the share first. A NAS that cannot be reached, or a mount that failed,
        // gives the folder-missing screen, never an open of the empty mount folder.
        if (settings.LibraryNas is { } nas)
        {
            path = Nas.NasStartup.Prepare(
                settings, App.Services.GetRequiredService<Account.AuthService>(), Nas.NasMount.ForThisSystem());
            if (path is null)
                return new LibraryOpening.Trouble.FolderMissing(settings.LibraryPath ?? nas.Name);
        }

        return App.Services.GetRequiredService<LibraryService>().OpenForLanding(path);
    }

    /// <summary>
    /// Puts the screen for an opening up in the shell's frame, and says which one it was so the
    /// caller can decide what it waits for. The history is dropped: Back from the library must not
    /// walk into the setup wizard or the problem screen.
    /// </summary>
    public static OpeningRoute Show(LibraryOpening opening)
    {
        var route = OpeningScreen.Choose(opening);
        var nav = App.Services.GetRequiredService<NavigationService>();

        // The trouble object is the parameter, not the route: LibraryProblemPage takes the failure
        // itself, so it cannot be shown without one.
        if (opening is LibraryOpening.Trouble trouble)
            nav.NavigateRoot(typeof(LibraryProblemPage), new LibraryProblem(trouble, ProblemPlace.Landing));
        else if (route is OpeningRoute.Library)
        {
            // Before the page and not after it: the page is built on this thread and reads almost
            // nothing from the library, so the worker has the read gate to itself for that time.
            BuildSessionCutsEarly(App.Services.GetRequiredService<LibraryService>().Db);
            nav.NavigateRoot(typeof(MainPage));
        }
        else
            nav.NavigateRoot(typeof(WizardPage), route);

        return route;
    }

    /// <summary>
    /// Starts building the listening profile and the mood cut that home reads first, on a worker, while
    /// the main page is being built.
    /// </summary>
    /// <remarks>
    /// <para><b>Why.</b> Home cannot show anything before it has both, and building them is most of its
    /// reading, about a second on a large library. Home asks only after the main page is built and
    /// attached, which takes about as long again on the drawing thread. Started here, the two overlap.
    /// See <see cref="LibraryDatabase.BuildSessionCuts"/>.</para>
    ///
    /// <para><b>Safe in any order.</b> Whichever thread asks first builds, and a thread that asks during
    /// the build waits for it. If home asks before the worker starts, home builds and the worker finds the
    /// answers made. A build that fails is not kept: home then builds for itself and meets the failure
    /// through its own read, where the problem screen is put up.</para>
    /// </remarks>
    private static void BuildSessionCutsEarly(LibraryDatabase db)
    {
        _ = Task.Run(() =>
        {
            var clock = System.Diagnostics.Stopwatch.StartNew();
            try
            {
                db.BuildSessionCuts();
                RPM.Core.Diagnostics.Scope.Said("boot", $"session cuts built early in {clock.ElapsedMilliseconds}ms");
            }
            catch (Exception ex)
            {
                RPM.Core.Diagnostics.Scope.Said("boot", $"session cuts not built early: {ex.GetType().Name}");
            }
        });
    }
}
