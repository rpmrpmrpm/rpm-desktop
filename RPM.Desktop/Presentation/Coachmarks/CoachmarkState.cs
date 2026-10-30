using Microsoft.Extensions.DependencyInjection;

using RPM.Core.Library;
using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Presentation.Coachmarks;

/// <summary>
/// The first-run hints, as the rest of the app sees them: which one is due, and how to retire one.
///
/// <para><b>A hint can be retired from anywhere.</b> <see cref="Seen"/> is safe to call every time the
/// thing a hint is about happens. A second call does nothing. So the control that a hint points at can
/// say "this has been used" without checking whether a hint is on screen, whether it is this hint, or
/// whether the tour has already finished. The order of the hints, and when they stop, is decided by
/// <see cref="CoachTour"/>. That class has no side effects and has tests.</para>
///
/// <para>The shell is asked to redraw here, not by each caller. The control that has just retired a
/// hint is the one that knows the overlay is now out of date. If each caller had to do this, one of
/// them could forget, and draw an arrow at a button that has already been pressed.</para>
/// </summary>
internal static class CoachmarkState
{
    private static SettingsService Settings => App.Services.GetRequiredService<SettingsService>();

    /// <summary>The hints still to be shown, in order. The caller shows the first it can place; see
    /// <see cref="CoachTour.Remaining"/> for why it is the whole list and not just the first.</summary>
    public static IEnumerable<CoachMark> Remaining() => CoachTour.Remaining(Settings.CoachmarksSeen);

    /// <summary>Retire a hint, because it was dismissed or because its control was used. Does
    /// nothing if it was already retired.</summary>
    public static void Seen(CoachMark mark)
    {
        var key = CoachTour.Key(mark);
        if (Settings.CoachmarksSeen.Contains(key, StringComparer.Ordinal))
            return;

        Settings.MarkCoachmarkSeen(key);
        Shell.ShellPage.Current?.RefreshCoachmarks();
    }

    /// <summary>The key the tour's closing card is remembered under, with the hints' own. Putting the hints
    /// back puts it back too.</summary>
    private const string FinaleKey = "tour-done";

    /// <summary>Whether the closing card has been answered.</summary>
    public static bool FinaleSeen => Settings.CoachmarksSeen.Contains(FinaleKey, StringComparer.Ordinal);

    /// <summary>The closing card's button: the tour is over, and the app is the user's.</summary>
    public static void SeeFinale()
    {
        if (!FinaleSeen)
            Settings.MarkCoachmarkSeen(FinaleKey);
        Shell.ShellPage.Current?.RefreshCoachmarks();
    }

    /// <summary>Put every hint back, and show the first one again.</summary>
    public static void Reset()
    {
        Settings.ResetCoachmarks();
        Shell.ShellPage.Current?.RefreshCoachmarks();
    }
}
