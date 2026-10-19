using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;

using RPM.Core.Library;
using RPM.Desktop.Presentation.Coachmarks;
using RPM.Desktop.Presentation.Main;
using RPM.Desktop.Strings;

using Windows.Foundation;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The first-run hints: which one is on screen, and where its arrow goes.
///
/// <para><b>The shell owns them, not the pages.</b> The three controls they point at are in three
/// different places. The Import button is in the library page's rail, the grip belongs to the artwork
/// band, and the view switch is in the library's own header. A hint drawn by each owner would be three
/// overlays that cannot see each other, so two could appear at once. One canvas over the whole window
/// can reach all three and show exactly one.</para>
///
/// <para><b>Nothing is stored about where anything was.</b> Each hint is redrawn from the live bounds of
/// its target. Any layout change that could move a target asks for a redraw. This is the only thing that
/// keeps an arrow on its control after the band opens, a pane folds, or the window resizes.</para>
/// </summary>
public sealed partial class ShellPage
{
    private CoachmarkPainter? _coach;

    /// <summary>True while a redraw is already queued, so a burst of layout events costs one.</summary>
    private bool _coachQueued;

    /// <summary>
    /// Redraw the hints once the current layout pass has finished.
    ///
    /// <para>Low priority and queued, both on purpose. The painter reads bounds, and bounds are only
    /// correct after the arrange pass. Called straight from a size or navigation event, it would place
    /// every arrow against the layout that is being replaced.</para>
    /// </summary>
    public void RefreshCoachmarks()
    {
        if (_coachQueued)
            return;

        _coachQueued = true;
        DispatcherQueue.TryEnqueue(DispatcherQueuePriority.Low, () =>
        {
            _coachQueued = false;
            DrawCoachmarks();
        });
    }

    /// <summary>The library page, or null when something else is up.</summary>
    private MainPage? LibraryPage => RootFrame.Content as MainPage;

    private void DrawCoachmarks()
    {
        if (CoachOverlay is null || CoachCanvas is null)
            return;

        _coach ??= new CoachmarkPainter(CoachCanvas, CoachOverlay);
        _coach.Clear();

        // Only over the library, and not while the splash is up. The wizard has the whole window and
        // its own three steps to explain it. The splash is a black sheet with nothing to point at.
        if (!OnLibrary || SplashOverlay.Visibility == Visibility.Visible)
        {
            HoldArtGrip(false);
            _coach.Hide();
            return;
        }

        var due = CoachmarkState.Remaining().ToList();

        // The grip comes up before the hint about it. The grip is drawn only while the pointer is on
        // its strip, so while it is hidden it cannot be measured, and the hint cannot be placed. If the
        // grip were held up only once the hint was on screen, the hint could never be placed. So the
        // grip is held for as long as the hint is DUE. That is a fact about the tour, not about what
        // was drawn last time.
        if (HoldArtGrip(due.Contains(CoachMark.ArtHeader)))
        {
            // It has no bounds until the layout pass that follows. Ask again after that one.
            RefreshCoachmarks();
            return;
        }

        foreach (var mark in due)
        {
            if (!Place(mark))
                continue;

            _coach.Show();
            return;
        }

        _coach.Hide();
    }

    /// <summary>
    /// Keeps the artwork grip on screen while the hint about it is up.
    ///
    /// <para>The grip is drawn only while the pointer is on its strip, so a hint saying "drag this" would
    /// otherwise point at nothing. The grip is held up for as long as the hint is shown, and let go as soon
    /// as the hint is answered.</para>
    /// </summary>
    /// <returns>True when this call CHANGED it, which is the caller's cue that the grip has not been
    /// laid out at its new visibility yet.</returns>
    private bool HoldArtGrip(bool hold)
    {
        if (_coachHoldsGrip == hold)
            return false;

        _coachHoldsGrip = hold;

        if (hold)
            ShowArtGrip(shown: true, lit: false);
        else if (!_artHovered && !_artDragging)
            ShowArtGrip(shown: false, lit: false);

        return true;
    }

    /// <summary>
    /// Draws one hint, and answers whether it could be drawn at all.
    ///
    /// <para>False for a target that is not on screen: folded away, scrolled out, or not yet laid out. The
    /// caller hides the overlay rather than showing a bubble whose arrow points at nothing. The next layout
    /// change asks again, so a hint about a control appears when its control does.</para>
    /// </summary>
    private bool Place(CoachMark mark) => mark switch
    {
        CoachMark.AddMusic => PlaceAddMusic(),
        CoachMark.ArtHeader => PlaceArtHeader(),
        CoachMark.Views => PlaceViews(),
        CoachMark.SignIn => PlaceSignIn(),
        _ => false,
    };

    /// <summary>How wide a bubble is. All three hints use one width, because they are the same kind of
    /// hint. Three different bubble shapes would look like three different kinds of thing.</summary>
    private const double BubbleWidth = 270;

    /// <summary>The gap between a bubble and the control it points at.</summary>
    private const double Reach = 48;

    /// <summary>The Import button, bottom left. The bubble sits to its right, clear of the rail. The arrow
    /// runs back to the button's right edge.</summary>
    private bool PlaceAddMusic()
    {
        if (LibraryPage?.CoachAddMusicTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        _coach.AddCallout(
            Loc.Get("Coach_AddMusic"),
            bubbleAt: new Point(rect.Right + Reach, rect.Y - 30),
            arrowTo: new Point(rect.Right + 6, rect.Y + rect.Height / 2),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.AddMusic),
            anchor: CoachmarkPainter.ArrowAnchor.LeftCenter);
        return true;
    }

    /// <summary>The grip, at the top middle. The bubble hangs below and to the left, so it does not cover
    /// the artwork the grip controls. The arrow curls up to the grip.</summary>
    private bool PlaceArtHeader()
    {
        if (_coach!.TryRect(ArtGrip) is not { } rect)
            return false;

        _coach.AddCallout(
            Loc.Get("Coach_ArtHeader"),
            bubbleAt: new Point(rect.X - BubbleWidth - 40, rect.Bottom + Reach),
            arrowTo: new Point(rect.X + rect.Width / 2, rect.Bottom + 6),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.ArtHeader),
            flipArch: true);
        return true;
    }

    /// <summary>
    /// The deck's menu button, bottom right. Signing in is done from here. The bubble sits above it and to
    /// the left, since the button is in the corner.
    ///
    /// <para><b>Not shown to anybody already signed in.</b> This also means it is never retired for them.
    /// Someone who signs out later is shown it again, because the hint is true for them again. Signing in
    /// retires it, in <see cref="RefreshCoachmarksForAccount"/>. The user has then done what the hint asks,
    /// so a later hint would not help.</para>
    /// </summary>
    private bool PlaceSignIn()
    {
        if (Auth is not { IsLoggedIn: false }
            || LibraryPage?.CoachMenuTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        _coach.AddCallout(
            Loc.Get("Coach_SignIn"),
            bubbleAt: new Point(rect.X - BubbleWidth - Reach, rect.Y - 120),
            arrowTo: new Point(rect.X + rect.Width / 2, rect.Y - 6),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.SignIn),
            flipArch: true);
        return true;
    }

    /// <summary>The account changed. Signing in answers the sign-in hint. Signing out means the hints
    /// must be checked again, because one of them is about being signed out.
    /// </summary>
    private void RefreshCoachmarksForAccount()
    {
        if (Auth is { IsLoggedIn: true })
            CoachmarkState.Seen(CoachMark.SignIn);
        else
            RefreshCoachmarks();
    }

    /// <summary>The view switch, in the library header on the right. The bubble hangs under it, pulled left
    /// so that a narrow window cannot push it off the edge.</summary>
    private bool PlaceViews()
    {
        if (LibraryPage is not { } page)
            return false;

        // No switch on screen means the collection is not open. Open it (see OpenCollectionForCoach for why
        // the hint moves the listener rather than waiting), and return false for now. The move lands in
        // ApplyViews, which asks for this again.
        if (page.CoachViewsTarget is not { } target)
        {
            page.OpenCollectionForCoach();
            return false;
        }

        if (_coach!.TryRect(target) is not { } rect)
            return false;

        // The bubble is placed straight underneath and centred on the target. The arrow leaves the
        // nearest edge, which is the top, since the target is above. An arrow that started at the
        // bottom of the bubble would go downwards and then have to climb back around the bubble to
        // reach the switch.
        var middle = rect.X + rect.Width / 2;
        _coach.AddCallout(
            Loc.Get("Coach_Views"),
            bubbleAt: new Point(middle - BubbleWidth / 2, rect.Bottom + Reach),
            arrowTo: new Point(middle, rect.Bottom + 5),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.Views));
        return true;
    }
}
