namespace RPM.Core.Library;

/// <summary>One first-run hint, in the order they are shown.</summary>
public enum CoachMark
{
    /// <summary>Where music comes in. Points at the Import tab of the rail, and asks for it to be
    /// pressed.</summary>
    AddMusic,

    /// <summary>What the import pane does, once it is open: the sources, and finding the release. Asks for
    /// the pane to be closed again, which retires it.</summary>
    ImportSources,

    /// <summary>The rail's other side screens: track info, the visualizers and the radio.</summary>
    SideScreens,

    /// <summary>That the artwork band opens and closes. Points at its grip.</summary>
    ArtHeader,

    /// <summary>That the library has three views. Points at the view switch.</summary>
    Views,

    /// <summary>That rpm itself has views: the deck's eye button, which is to be pressed.</summary>
    AppViews,

    /// <summary>What each of those views is, beside the picker the eye button opened. Closing the picker
    /// retires it.</summary>
    AppViewsOpen,

    /// <summary>That there is an account and what it is for. Points at the menu the sign-in is in. Shown
    /// only while nobody is signed in.</summary>
    SignIn,
}

/// <summary>Which first-run hint to show, if any.
///
/// <para><b>One at a time, and always the earliest one left.</b> Three arrows on a window at once look like
/// a diagram of the app, not a hint about it. They also leave only dismiss buttons to press. So the tour is
/// a queue. It offers the first mark that nobody has seen yet, and nothing while the queue is empty.</para>
///
/// <para><b>Seen, not dismissed.</b> A mark leaves the queue when its own button dismisses it, and also
/// when the user does the thing it points at. For the tour these are the same event: the user now knows
/// about that control. Keeping them apart would show someone a hint about the import tab they had just
/// used.</para>
/// </summary>
public static class CoachTour
{
    /// <summary>The marks in the order they are offered. Add music is first, because the collection is
    /// what every other hint is about. The header has no artwork, and the views have nothing to switch
    /// between, until there is music.
    ///
    /// <para>Two pairs lead into each other: the Import press opens the pane its second hint is about,
    /// and the eye button opens the picker its second hint is about. The second of each pair can only be
    /// placed while the first has opened what it points at, so it follows on its own.</para>
    ///
    /// <para>Sign-in is last. It is the only mark that is an invitation rather than an explanation. The
    /// other three describe the app in front of the user, and this one asks them to go and get something.
    /// It is shown after they have been told how the app works.</para>
    /// </summary>
    public static readonly IReadOnlyList<CoachMark> Order =
        [CoachMark.AddMusic, CoachMark.ImportSources, CoachMark.SideScreens, CoachMark.ArtHeader,
         CoachMark.Views, CoachMark.AppViews, CoachMark.AppViewsOpen, CoachMark.SignIn];

    /// <summary>The mark's key in the settings file. The keys are written out here, not taken from the
    /// enum's names. Renaming the enum must not silently show a dismissed hint again on everybody's
    /// machine.
    /// </summary>
    public static string Key(CoachMark mark) => mark switch
    {
        CoachMark.AddMusic => "add-music",
        CoachMark.ImportSources => "import-sources",
        CoachMark.SideScreens => "side-screens",
        CoachMark.ArtHeader => "art-header",
        CoachMark.Views => "views",
        CoachMark.AppViews => "app-views",
        CoachMark.AppViewsOpen => "app-views-open",
        CoachMark.SignIn => "sign-in",
        _ => throw new ArgumentOutOfRangeException(nameof(mark)),
    };

    /// <summary>The mark to show now, or null when the tour is over.
    /// </summary>
    /// <param name="seen">The keys already seen. Anything in here is skipped.</param>
    public static CoachMark? Next(IReadOnlyCollection<string> seen)
    {
        foreach (var mark in Remaining(seen))
            return mark;

        return null;
    }

    /// <summary>Every mark still to be shown, in order.
    ///
    /// <para><b>Why the caller wants the whole list, not just the first.</b> A mark can be due but
    /// impossible to show, because its control is not on screen. The view switch is not drawn on the home
    /// screen. The artwork grip is not drawn at all until the pointer is near it. If only the first mark
    /// were offered, a tour stuck on one of those would stop and never reach the marks after it. So the
    /// caller walks this list and shows the first mark it can actually point at.</para>
    ///
    /// <para>A mark passed over this way is not seen. It comes back as soon as its control is on
    /// screen.</para>
    /// </summary>
    public static IEnumerable<CoachMark> Remaining(IReadOnlyCollection<string> seen)
        => Order.Where(mark => !seen.Contains(Key(mark)));

    /// <summary>Whether every mark has been seen. A caller that is watching for layout changes can stop
    /// once this is true, because it has no further use for them.
    /// </summary>
    public static bool Over(IReadOnlyCollection<string> seen) => Next(seen) is null;
}
