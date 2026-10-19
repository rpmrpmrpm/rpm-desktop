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
            nav.NavigateRoot(typeof(MainPage));
        else
            nav.NavigateRoot(typeof(WizardPage), route);

        return route;
    }
}
