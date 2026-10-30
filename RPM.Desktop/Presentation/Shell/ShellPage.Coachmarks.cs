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

    /// <summary>True while something on screen has said the tour comes after it: the beta welcome that
    /// follows setup. The hints are drawn once it is put back to false and refreshed.</summary>
    public static bool CoachmarksHeld { get; set; }

    /// <summary>The library page, or null when something else is up.</summary>
    private MainPage? LibraryPage => RootFrame.Content as MainPage;

    private void DrawCoachmarks()
    {
        if (CoachOverlay is null || CoachCanvas is null)
            return;

        _coach ??= new CoachmarkPainter(CoachCanvas, CoachOverlay);
        _coach.Clear();

        // Only over the library, and not while the splash is up. The wizard has the whole window and
        // its own steps to explain it. The splash is a black sheet with nothing to point at. Nor while
        // the beta welcome is up, which says the tour comes next (CoachmarksHeld).
        if (!OnLibrary || SplashOverlay.Visibility == Visibility.Visible || CoachmarksHeld)
        {
            HoldArtGrip(false);
            _coach.Hide();
            return;
        }

        var due = CoachmarkState.Remaining().ToList();

        // Signed in already: the sign-in hint has nothing left to ask, and must not hold the tour open.
        if (Auth is { IsLoggedIn: true } && due.Contains(CoachMark.SignIn))
        {
            CoachmarkState.Seen(CoachMark.SignIn);
            return;   // Seen asks for the next draw
        }

        // The import pane's hint asks for the pane to be closed again. Once it has been on screen and the
        // pane is gone, it has been answered.
        if (_importCoachShown && due.Contains(CoachMark.ImportSources) && LibraryPage?.CoachImportTarget is null)
        {
            _importCoachShown = false;
            CoachmarkState.Seen(CoachMark.ImportSources);
            return;   // Seen asks for the next draw
        }

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

            // A step of the tour that is not the one on screen fades in, rather than appearing in one
            // frame where the last one was.
            if (_lastMark != mark)
                _coach.FadeIn();
            _lastMark = mark;

            _tourShown = true;
            _coach.Show();
            return;
        }

        // The last hint answered, in a run that showed the tour: the card that ends it, once.
        if (due.Count == 0 && _tourShown && !CoachmarkState.FinaleSeen)
        {
            _coach.AddFinale(
                Loc.Get("Coach_DoneTitle"), Loc.Get("Coach_DoneBody"), Loc.Get("Coach_DoneButton"),
                CoachmarkState.SeeFinale);
            if (_lastMark is not null)
                _coach.FadeIn();
            _lastMark = null;
            _coach.Show();
            return;
        }

        _lastMark = null;
        _coach.Hide();
    }

    /// <summary>Set once a hint has been drawn in this run, so the end of the tour can be marked: a run that
    /// starts with the tour already over shows no finale.</summary>
    private bool _tourShown;

    /// <summary>The hint on screen, so a redraw of the same one does not fade it in again.</summary>
    private CoachMark? _lastMark;

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
        CoachMark.ImportSources => PlaceImportSources(),
        CoachMark.SideScreens => PlaceSideScreens(),
        CoachMark.ArtHeader => PlaceArtHeader(),
        CoachMark.Views => PlaceViews(),
        CoachMark.AppViews => PlaceAppViews(),
        CoachMark.AppViewsOpen => PlaceAppViewsOpen(),
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

        // No Next: the hint is answered by pressing Import. Closing it instead skips the pane's own hint
        // too, which could otherwise only come up when the pane is opened.
        _coach.AddSpotlight(rect);
        _coach.AddCallout(
            Loc.Get("Coach_AddMusic"),
            bubbleAt: new Point(rect.Right + Reach, rect.Y - 30),
            arrowTo: new Point(rect.Right + 6, rect.Y + rect.Height / 2),
            BubbleWidth,
            onDismiss: () =>
            {
                CoachmarkState.Seen(CoachMark.ImportSources);
                CoachmarkState.Seen(CoachMark.AddMusic);
            },
            anchor: CoachmarkPainter.ArrowAnchor.LeftCenter);
        return true;
    }

    /// <summary>Set once the import pane's hint has been drawn, so its pane closing can retire it. A pane
    /// that was never shown the hint closing says nothing.</summary>
    private bool _importCoachShown;

    /// <summary>The import pane, once its button has been pressed. The bubble sits over the pane's right
    /// edge, beside what it describes, with the arrow reaching back into the pane.</summary>
    private bool PlaceImportSources()
    {
        if (LibraryPage?.CoachImportTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        // Next does what the hint asks: closes the pane.
        _coach.AddSpotlight(rect, pad: 2);
        _coach.AddCallout(
            Loc.Get("Coach_ImportSources"),
            bubbleAt: new Point(rect.Right + Reach, rect.Y + rect.Height / 2 - 70),
            arrowTo: new Point(rect.Right - 12, rect.Y + rect.Height / 2),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.ImportSources),
            anchor: CoachmarkPainter.ArrowAnchor.LeftCenter,
            onNext: () =>
            {
                _importCoachShown = false;
                LibraryPage?.CloseImportForCoach();
                CoachmarkState.Seen(CoachMark.ImportSources);
            });
        _importCoachShown = true;
        return true;
    }

    /// <summary>The rail's side screens. The bubble sits to the right of the rail, level with the middle
    /// one of the three, like the import hint.</summary>
    private bool PlaceSideScreens()
    {
        if (LibraryPage?.CoachSideScreensTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        // All three are lit, not just the middle one the arrow points at.
        var lit = rect;
        foreach (var part in LibraryPage.CoachSideScreensParts)
            if (_coach.TryRect(part) is { } r)
                lit.Union(r);

        _coach.AddSpotlight(lit);
        _coach.AddCallout(
            Loc.Get("Coach_SideScreens"),
            bubbleAt: new Point(rect.Right + Reach, rect.Y - 30),
            arrowTo: new Point(rect.Right + 6, rect.Y + rect.Height / 2),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.SideScreens),
            anchor: CoachmarkPainter.ArrowAnchor.LeftCenter,
            onNext: () => CoachmarkState.Seen(CoachMark.SideScreens));
        return true;
    }

    /// <summary>The deck's eye button, bottom right. The bubble sits above it and to the left, as the
    /// sign-in hint does for the menu beside it.</summary>
    private bool PlaceAppViews()
    {
        if (LibraryPage?.CoachAppViewsTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        // No Next: the hint is answered by pressing the eye button. Closing it instead skips the picker's
        // own hint too, which could otherwise only come up when the picker is opened.
        _coach.AddSpotlight(rect);
        _coach.AddCallout(
            Loc.Get("Coach_AppViews"),
            bubbleAt: new Point(rect.X - BubbleWidth - Reach, rect.Y - 120),
            arrowTo: new Point(rect.X + rect.Width / 2, rect.Y - 6),
            BubbleWidth,
            onDismiss: () =>
            {
                CoachmarkState.Seen(CoachMark.AppViewsOpen);
                CoachmarkState.Seen(CoachMark.AppViews);
            },
            flipArch: true);
        return true;
    }

    /// <summary>Beside the views picker while it is open: to its left, level with it, so the bubble covers
    /// none of the three cards it is about.</summary>
    private bool PlaceAppViewsOpen()
    {
        if (LibraryPage?.CoachAppViewsPickerTarget is not { } target
            || _coach!.TryRect(target) is not { } rect)
            return false;

        // Next closes the picker, which is what answers this hint. Its cards are held still meanwhile: a
        // view picked here would leave the tour pointing into a window that has gone.
        LibraryPage.HoldViewPickerForCoach();
        _coach.AddSpotlight(rect, pad: 2);
        _coach.AddCallout(
            Loc.Get("Coach_AppViewsOpen"),
            bubbleAt: new Point(rect.X - BubbleWidth - Reach, rect.Y + 10),
            arrowTo: new Point(rect.X - 6, rect.Y + rect.Height / 2),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.AppViewsOpen),
            onNext: () =>
            {
                LibraryPage?.CloseViewPickerForCoach();
                CoachmarkState.Seen(CoachMark.AppViewsOpen);
            });
        return true;
    }

    /// <summary>The grip, at the top middle. The bubble hangs below and to the left, so it does not cover
    /// the artwork the grip controls. The arrow curls up to the grip.</summary>
    private bool PlaceArtHeader()
    {
        if (_coach!.TryRect(ArtGrip) is not { } rect)
            return false;

        _coach.AddSpotlight(rect, pad: 14);
        _coach.AddCallout(
            Loc.Get("Coach_ArtHeader"),
            bubbleAt: new Point(rect.X - BubbleWidth - 40, rect.Bottom + Reach),
            arrowTo: new Point(rect.X + rect.Width / 2, rect.Bottom + 6),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.ArtHeader),
            onNext: () => CoachmarkState.Seen(CoachMark.ArtHeader),
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

        _coach.AddSpotlight(rect);
        _coach.AddCallout(
            Loc.Get("Coach_SignIn"),
            bubbleAt: new Point(rect.X - BubbleWidth - Reach, rect.Y - 120),
            arrowTo: new Point(rect.X + rect.Width / 2, rect.Y - 6),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.SignIn),
            onNext: () => CoachmarkState.Seen(CoachMark.SignIn),
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
        _coach.AddSpotlight(rect);
        _coach.AddCallout(
            Loc.Get("Coach_Views"),
            bubbleAt: new Point(middle - BubbleWidth / 2, rect.Bottom + Reach),
            arrowTo: new Point(middle, rect.Bottom + 5),
            BubbleWidth,
            onDismiss: () => CoachmarkState.Seen(CoachMark.Views),
            onNext: () => CoachmarkState.Seen(CoachMark.Views));
        return true;
    }
}
