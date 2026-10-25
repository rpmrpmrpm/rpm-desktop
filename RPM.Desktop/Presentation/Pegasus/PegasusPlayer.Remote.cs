using System.Numerics;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Hosting;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;
using VirtualKey = Windows.System.VirtualKey;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Pegasus with a TV remote, on the host's side. It covers whether the listener is using keys, the look
/// that shows where the focus is, and where the focus goes when it is lost.
///
/// <para>The view is meant to be used with a TV remote: arrow keys, Back and the other buttons, with no
/// clicking and no swiping. A focused item is enlarged and drawn at full brightness while every other
/// item on the screen is dimmed, so it can be seen from across a room. The keys arrive through the shell
/// (ShellPage.OnPreviewKeyDown). That is the one handler every press passes through, wherever the focus
/// is. The arrows then move the focus by XY navigation, which is switched on at <c>Root</c>.</para>
///
/// <para><b>Keys and the pointer take turns.</b> A key puts the view in keyboard mode. The focus shows,
/// the faded keys come up, and the chrome moves the focus when a screen changes. A pointer move takes the
/// view out of keyboard mode, and everything is drawn at full brightness and size again. The first key
/// after the pointer only shows where the focus is, without moving it, so the listener sees where they are
/// before they go anywhere.</para>
///
/// <para><b>The look is the Android TV app's</b> (see <see cref="FocusLift"/>): the focused item grows,
/// the rest dim, and there is no glow ring, only a soft disc behind a focused icon key. The focus tick
/// (<see cref="OnRingFrame"/>) grows the focused item by its composition visual's scale and dims the
/// others by their opacity, neither of which lays anything out or builds anything again. The code still
/// calls it the ring.</para>
/// </summary>
public sealed partial class PegasusPlayer
{
    /// <summary>Whether the listener is driving the view with keys. See the class notes.</summary>
    public bool KeyboardMode { get; private set; }

    /// <summary>Where an arrow moves the focus from <paramref name="from"/>, or null where nothing lies in
    /// that direction. The chrome decides this on the screen in front (see PegasusChrome.NextFocus). Where
    /// there is no chrome yet, the whole view is searched.</summary>
    public Control? NextFocus(FrameworkElement? from, FocusNavigationDirection direction) =>
        _chrome is { } chrome
            ? chrome.NextFocus(from, direction, Root)
            : RemoteSearch.Next(from, direction, new RemoteScreen(Root, notDrawn: []));

    /// <summary>An arrow that found nothing to move to. Returns true when the chrome handled it (Right in
    /// the side menu closes it, Left at the edge of home, the wall, the lists and radio opens it; see
    /// PegasusChrome.TakeEdgeKey). Returns false when the host should bounce.</summary>
    public bool TakeEdgeKey(FocusNavigationDirection direction)
    {
        if (_chrome?.TakeEdgeKey(direction) != true)
            return false;
        if (direction == FocusNavigationDirection.Left && _chrome.LibraryMenuOpen)
            _swallowLeft = true;
        return true;
    }

    /// <summary>A Left that opened the side menu (at the edge, or on the mini player) is still down. Its
    /// repeats are swallowed, as the TV swallows the rest of that press, so a held key does not go on to
    /// bounce in the menu it opened. Cleared by the key's release or the next fresh press.</summary>
    private bool _swallowLeft;

    /// <summary>Whether the screen shown while a collection is switched to is up. The shell takes the
    /// remote's keys and does nothing with them while it is up, so nothing moves or is pressed behind
    /// it.</summary>
    public bool SwitchingCollection => _chrome?.SwitchingCollection == true;

    /// <summary>The bounce in progress: its direction, and when it started (Environment.TickCount64). Zero
    /// when no bounce is running.</summary>
    private Vector2 _bounceWay;
    private long _bounceAt;

    /// <summary>How far the bounce goes, before the view's scale, and how long the out and back
    /// take.</summary>
    private const double BounceReach = 10;
    private const double BounceMs = 220;

    /// <summary>
    /// The focus has nowhere to go in that direction. The ring and the item move a little toward the press,
    /// then come back. At the end of a list the view bounces back rather than continuing.
    /// </summary>
    public void Bounce(FocusNavigationDirection direction)
    {
        _bounceWay = direction switch
        {
            FocusNavigationDirection.Left => new Vector2(-1, 0),
            FocusNavigationDirection.Right => new Vector2(1, 0),
            FocusNavigationDirection.Up => new Vector2(0, -1),
            FocusNavigationDirection.Down => new Vector2(0, 1),
            _ => Vector2.Zero,
        };
        _bounceAt = Environment.TickCount64;
    }

    /// <summary>Where the bounce moves the ring and the item this frame. The movement goes out along the
    /// way pressed and back, on one half of a sine wave. It is zero once the bounce is over.</summary>
    private Vector2 BounceNow(double scale)
    {
        if (_bounceWay == Vector2.Zero)
            return Vector2.Zero;

        var t = (Environment.TickCount64 - _bounceAt) / BounceMs;
        if (t >= 1)
        {
            _bounceWay = Vector2.Zero;
            return Vector2.Zero;
        }

        return _bounceWay * (float)(Math.Sin(Math.PI * t) * BounceReach * scale);
    }

    /// <summary>The stop that has the focus, the element that shows it (its face, see FocusLift.Face: the
    /// stop itself, or what an empty stop is laid over), and how much that element is enlarged.</summary>
    private FrameworkElement? _ringTarget;
    private FrameworkElement? _ringFace;
    private float _ringGrow = 1;
    private bool _ringTicking;

    /// <summary>How far a focused key or row grows on each side, before the view's scale, and the most it
    /// can grow by however small it is (the TV's deskFocus). It grows by a fixed number of points, not a
    /// share of its size. A share that suits a cover would push a full-width track line past both edges of
    /// its list. A card grows by a share, FocusLift.CardGrow.</summary>
    private const double RingGrowPoints = 6;
    private const float RingGrowMost = 1.12f;

    /// <summary>How much of the way to where it is going the grow, a dim and the glow move each frame. At
    /// this value they arrive in about a tenth of a second at sixty frames per second, near the TV's
    /// 150 ms.</summary>
    private const double RingEase = 0.35;

    /// <summary>The soft glow behind a focused icon key (FocusLift.GlowMost): one disc in the ring's grid,
    /// lit from its middle and fading to nothing FocusLift.GlowReach past the key's edge, the TV's radial
    /// gradient. <see cref="_glowEdge"/> is the stop at the key's own edge, which moves with the key's size.
    /// Built once (see <see cref="BuildGlow"/>). <see cref="_glowNow"/> is how lit it is, eased in.</summary>
    private Microsoft.UI.Xaml.Shapes.Ellipse? _glow;
    private GradientStop? _glowEdge;
    private double _glowNow;

    /// <summary>Builds the glow's disc into the ring's grid: white at 30% in the middle, 16% at the key's
    /// edge, nothing at the disc's own edge (the TV's FocusSpot).</summary>
    private void BuildGlow()
    {
        if (_glow is not null)
            return;
        _glowEdge = new GradientStop { Offset = 0.5, Color = Windows.UI.Color.FromArgb(41, 0xFF, 0xFF, 0xFF) };
        _glow = new Microsoft.UI.Xaml.Shapes.Ellipse
        {
            IsHitTestVisible = false,
            Fill = new RadialGradientBrush
            {
                GradientStops =
                {
                    new GradientStop { Offset = 0, Color = Windows.UI.Color.FromArgb(77, 0xFF, 0xFF, 0xFF) },
                    _glowEdge,
                    new GradientStop { Offset = 1, Color = Windows.UI.Color.FromArgb(0, 0xFF, 0xFF, 0xFF) },
                },
            },
        };
        FocusRing.Children.Add(_glow);
    }

    /// <summary>How strong a focused key's glow is, 1 at full: 70% on the tuner and none on a record's
    /// page, as on the TV (29be467c5 for the tuner; TrackPage gives its keys none, where it stood out hard
    /// beside the play key).</summary>
    private double GlowStrength => _chrome switch
    {
        { RecordUp: true } => 0,
        { TunerUp: true } => 0.7,
        _ => 1,
    };

    /// <summary>An element the focus look has dimmed, or is lighting again: how bright it is drawn now,
    /// where it is going, and the opacity last written to it. An element whose opacity is found to be other
    /// than the one written has been faded by its own code since, and is let go: the look never writes over
    /// a fade it did not make, and only dims an element from full opacity.</summary>
    private sealed class Lit(FrameworkElement element)
    {
        public FrameworkElement Element { get; } = element;
        public double Now = 1, Goal = 1, Wrote = 1;
        public bool Moving;
    }

    /// <summary>Every element dimmed now, and those of them easing toward a new brightness.</summary>
    private readonly Dictionary<FrameworkElement, Lit> _lit = new();
    private readonly List<Lit> _moving = [];

    /// <summary>Frames since the focus moved. The screen's stops are dimmed again at <see cref="RescanAt"/>
    /// of them, and then every <see cref="RescanEvery"/> up to <see cref="RescanFor"/>, for the cards a
    /// scroll or a late list brought in after the move: a cover a repeater realizes is drawn at full
    /// brightness until then. After that the screen is left alone until the next move, so a player left
    /// on screen for an evening walks nothing.</summary>
    private int _framesSinceMove;
    private static readonly int[] RescanAt = [3, 10, 24, 48];
    private const int RescanEvery = 90, RescanFor = 900;

    /// <summary>
    /// A key the shell handed over. This turns on keyboard mode, brings up the faded keys, and starts the
    /// ring's per-frame updates. The keys that only the mouse needs are hidden (see <see cref="MouseKeys"/>).
    /// </summary>
    /// <param name="wake">False for a key that acts without bringing anything up: Left and Right seeking
    /// with the player's controls hidden (see <see cref="TakeHiddenControlsKey"/>).</param>
    public void KeysUsed(bool wake = true)
    {
        KeyboardMode = true;
        MouseKeys.RemoteInUse(true);
        if (_chrome is { } chrome)
        {
            chrome.KeyboardDriven = true;
            if (wake)
                chrome.WakeForKeys();
        }

        if (wake)
        {
            ShowKeys();
            RestartIdle();
        }

        if (!_ringTicking)
        {
            _ringTicking = true;
            CompositionTarget.Rendering += OnRingFrame;
            Say("ring: keys in use");
        }
    }

    /// <summary>Writes one line to the window channel of the diagnostics, so the ring's movements can be
    /// read back afterwards.</summary>
    private static void Say(string line) => RPM.Core.Diagnostics.Scope.Said("window", line);

    /// <summary>Describes an element for the ring's log lines: its type, its name if it has one, the first
    /// named element above it, its size, and its position in the view. The position lets two cards from one
    /// list be told apart.</summary>
    private string Describe(FrameworkElement element)
    {
        var named = "";
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is FrameworkElement { Name: { Length: > 0 } name })
            {
                named = " in " + name;
                break;
            }

        var own = element.Name is { Length: > 0 } n ? $" '{n}'" : "";
        var place = "";
        try
        {
            var at = element.TransformToVisual(Root).TransformPoint(new Point(0, 0));
            place = $" at {at.X:0},{at.Y:0}";
        }
        catch (ArgumentException)
        {
            // The element is not connected to Root, so there is no position. The line is written without
            // one.
        }
        return $"{element.GetType().Name}{own}{named} {element.ActualWidth:0}x{element.ActualHeight:0}{place}";
    }

    /// <summary>Where the pointer was at its last event, in the root's points. NaN until the first
    /// event.</summary>
    private Point _pointerAt = new(double.NaN, double.NaN);

    /// <summary>How far the pointer has to move, in points, before the movement counts as the pointer being
    /// used.</summary>
    private const double PointerMoveCounts = 4;

    /// <summary>
    /// Whether a pointer event means that somebody is using the pointer. That is a button pressed, or a
    /// move of more than a few points since the last event. Content that scrolls under a cursor which has
    /// not moved also raises pointer events, at the same place, and these do not count. The first event
    /// only records where the pointer is.
    /// </summary>
    private bool PointerReallyUsed(PointerRoutedEventArgs e)
    {
        var point = e.GetCurrentPoint(Root);
        var at = point.Position;
        var was = _pointerAt;
        _pointerAt = at;

        if (point.Properties.IsLeftButtonPressed || point.Properties.IsRightButtonPressed
            || point.Properties.IsMiddleButtonPressed)
            return true;
        if (double.IsNaN(was.X))
            return false;

        var dx = at.X - was.X;
        var dy = at.Y - was.Y;
        return (dx * dx) + (dy * dy) > PointerMoveCounts * PointerMoveCounts;
    }

    /// <summary>The pointer was used. Keyboard mode turns off, and the ring stands down.</summary>
    private void PointerUsed()
    {
        if (!KeyboardMode)
            return;

        Say("ring: the pointer took over");
        KeyboardMode = false;
        MouseKeys.RemoteInUse(false);
        if (_chrome is { } chrome)
            chrome.KeyboardDriven = false;
        EndHold();
        EndSideHold();
        StopRing();
    }

    /// <summary>Takes the focus look away: the enlarged item goes back to its normal size, every dimmed item
    /// to full brightness, and the glow goes. This runs when the pointer is used, and when the view is put
    /// away.</summary>
    private void StopRing()
    {
        if (_ringTicking)
        {
            _ringTicking = false;
            CompositionTarget.Rendering -= OnRingFrame;
        }

        Grow(_ringFace, 1);
        Nudge(_ringFace, Vector2.Zero);
        _ringTarget = null;
        _ringFace = null;
        _ringGrow = 1;
        _bounceWay = Vector2.Zero;
        foreach (var lit in _lit.Values)
            if (Math.Abs(lit.Element.Opacity - lit.Wrote) < 0.002)
                lit.Element.Opacity = 1;
        _lit.Clear();
        _moving.Clear();
        HideGlow();
    }

    /// <summary>
    /// Whether the focus is nowhere a key can move from. That is the case when the focus is on nothing,
    /// outside this view, or on the view's own root. The root stands for the whole screen, not for an item
    /// on it.
    /// </summary>
    public bool FocusIsLost() => FocusedInView() is null;

    /// <summary>Puts the focus on the screen's default target (see
    /// PegasusChrome.DefaultFocusTarget).</summary>
    public void FocusDefault()
    {
        if (_greeting)
            return;   // The greeting has nothing to press. Back removes it.
        if (_chrome?.DefaultFocusTarget() is { } target)
            RemoteSearch.Give(target);
    }

    /// <summary>The focused element, if it is inside this view, is not the root itself, and is not on a
    /// part the view does not draw (see PegasusChrome.FocusOnNotDrawn). Otherwise this returns null. The
    /// next key then places the focus on the screen's default, rather than moving or pressing something
    /// nobody can see. The ring shows nothing until that happens.</summary>
    private FrameworkElement? FocusedInView()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not FrameworkElement focused
            || ReferenceEquals(focused, Root) || _chrome?.FocusOnNotDrawn() == true)
            return null;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
            if (ReferenceEquals(node, Root))
                return focused;

        return null;
    }

    /// <summary>
    /// One frame of the focus look. It moves onto whatever has the focus, enlarges that item (its face, see
    /// FocusLift.Face), dims every other item on the screen in front, and eases the glow in behind an icon
    /// key. A new target is also scrolled into view (see RemoteScroll). Three of this view's scrollers do
    /// not follow the focus on their own. They were turned off so a recycled element cannot drag the wall to
    /// itself (see LibraryScroll in the chrome's XAML). The rest only scroll as far as the edge.
    /// </summary>
    private void OnRingFrame(object? sender, object e)
    {
        var target = KeyboardMode ? FocusedInView() : null;

        if (!ReferenceEquals(target, _ringTarget))
        {
            var was = _ringTarget;
            Grow(_ringFace, 1);
            Nudge(_ringFace, Vector2.Zero);
            _bounceWay = Vector2.Zero;   // A bounce belongs to the item it started on.
            _ringTarget = target;
            _ringFace = target is null ? null : FocusLift.GetFace(target) ?? target;
            _ringGrow = 1;
            _glowNow = 0;
            _framesSinceMove = 0;
            Say(target is null ? "ring: on nothing" : "ring: on " + Describe(target));

            // Brings the target into view in every scroller above it. It is centred down the screen, unless
            // the move stayed within its row (see RemoteScroll). Then the item left dims with the rest of the
            // screen and the new one lights. A focus that went to nothing (a row's menu, which is a popup
            // outside this view) leaves the item it was on at full brightness and its own size, as the TV
            // draws the item a menu is open on.
            if (target is not null)
            {
                RemoteScroll.IntoView(target, Root, keepHeight: was is not null && SameHeight(was, target));
                LightStops(was);
            }
        }
        else if (target is not null)
        {
            var frames = _framesSinceMove = Math.Min(_framesSinceMove + 1, RescanFor + 1);
            if (Array.IndexOf(RescanAt, frames) >= 0
                || (frames > RescanAt[^1] && frames <= RescanFor && frames % RescanEvery == 0))
                LightStops(null);
        }

        EaseLights();

        // Nothing grows on nothing, nor on the transport while it is faded away. A key growing alone where
        // the keys were looks like a fault.
        if (target is not { ActualWidth: > 0, ActualHeight: > 0 } || _ringFace is not { ActualWidth: > 0, ActualHeight: > 0 } face
            || FocusOnFadedKeys)
        {
            if (_ringGrow != 1)
            {
                _ringGrow = 1;
                Grow(_ringFace, 1);
            }
            HideGlow();
            return;
        }

        var scale = _chrome?.TextScale ?? 1;
        var part = FocusLift.GetPart(target);

        // A stop that draws its own focus (the carousel, the ladder, radio's modes) only bounces.
        if (part == LiftPart.Own)
        {
            Nudge(face, BounceNow(scale));
            HideGlow();
            return;
        }

        // The middle of the face, which a grow about it does not move, and its size before the grow.
        Point middle;
        try
        {
            middle = face.TransformToVisual(FocusLayer).TransformPoint(new Point(face.ActualWidth / 2, face.ActualHeight / 2));
        }
        catch (ArgumentException)
        {
            // The element was removed from the tree since the last frame.
            HideGlow();
            return;
        }

        // A card grows by 8%, a key or a row by a few points on each side, eased over a few frames.
        var longest = Math.Max(face.ActualWidth, face.ActualHeight);
        var want = part == LiftPart.Card
            ? FocusLift.CardGrow
            : (float)Math.Min(RingGrowMost, 1 + (2 * RingGrowPoints * scale / Math.Max(1, longest)));
        _ringGrow += (float)((want - _ringGrow) * RingEase);
        Grow(face, _ringGrow);

        // Then moved by the bounce. The glow moves with it.
        var bounce = BounceNow(scale);
        Nudge(face, bounce);

        // The glow, behind an icon key only: a card, a row or a wide key shows the focus by its grow and the
        // dim alone.
        var strength = GlowStrength;
        var most = FocusLift.GlowMost * scale;
        if (part == LiftPart.Card || strength <= 0 || face.ActualWidth > most || face.ActualHeight > most)
        {
            HideGlow();
            return;
        }

        BuildGlow();
        var half = longest * _ringGrow / 2;
        var radius = half + (FocusLift.GlowReach * scale);
        FocusRingShift.X = middle.X + bounce.X - radius;
        FocusRingShift.Y = middle.Y + bounce.Y - radius;
        // A size that was never set is NaN, and every comparison with NaN is false. If the check only asked
        // "has it changed", the glow would never get a size and would stay a dot.
        if (double.IsNaN(FocusRing.Width) || Math.Abs(FocusRing.Width - (2 * radius)) > 0.5)
            FocusRing.Width = FocusRing.Height = 2 * radius;
        if (_glowEdge is { } edge && Math.Abs(edge.Offset - (half / radius)) > 0.01)
            edge.Offset = half / radius;

        _glowNow += (strength - _glowNow) * RingEase;
        FocusRing.Opacity = _glowNow;
    }

    /// <summary>Puts the glow out at once.</summary>
    private void HideGlow()
    {
        _glowNow = 0;
        FocusRing.Opacity = 0;
    }

    /// <summary>
    /// Dims every stop on the screen in front but the focused one, and lights the focused one, the TV's
    /// look: a card to FocusLift.CardLight, a key or a row to FocusLift.KeyLight, each through its face.
    /// What holds the focused stop or its face is not dimmed, or it would dim them with it. A stop that
    /// draws its own focus is left alone. The stops are found as the arrow keys find them (RemoteSearch),
    /// so a part the screen does not draw is not walked.
    ///
    /// <para>What was dimmed and is no longer on the screen in front (the collection under the side menu
    /// or a record's page) stays dimmed, as every item without the focus is on the TV. An element that has
    /// left the window is put back to full brightness and forgotten.</para>
    /// </summary>
    /// <param name="left">The stop the focus has just left, dimmed even where it is not on the screen in
    /// front now (a card on home, after Left opened the side menu). Null on a later pass.</param>
    private void LightStops(FrameworkElement? left)
    {
        if (_ringTarget is not { } target || _ringFace is not { } face)
            return;

        var holding = new HashSet<DependencyObject>();
        for (DependencyObject? node = target; node is not null && !ReferenceEquals(node, Root); node = VisualTreeHelper.GetParent(node))
            holding.Add(node);
        for (DependencyObject? node = face; node is not null && !ReferenceEquals(node, Root); node = VisualTreeHelper.GetParent(node))
            holding.Add(node);

        LightTo(face, 1, start: false);
        if (left is Control leftStop && !holding.Contains(leftStop))
            DimStop(leftStop, holding);

        var screen = _chrome?.ScreenInFront(Root) ?? new RemoteScreen(Root, notDrawn: []);
        var open = new Stack<DependencyObject>();
        open.Push(screen.Area);
        while (open.Count > 0)
        {
            var node = open.Pop();
            if (node is UIElement { Visibility: Visibility.Collapsed } || screen.LeavesOut(node))
                continue;

            if (node is Control { IsTabStop: true, IsEnabled: true, IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } stop
                && !holding.Contains(stop))
                DimStop(stop, holding);

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                open.Push(VisualTreeHelper.GetChild(node, i));
        }

        List<Lit>? gone = null;
        foreach (var lit in _lit.Values)
            if (!lit.Element.IsLoaded)
                (gone ??= []).Add(lit);
        if (gone is null)
            return;
        foreach (var lit in gone)
        {
            if (Math.Abs(lit.Element.Opacity - lit.Wrote) < 0.002)
                lit.Element.Opacity = 1;
            Forget(lit);
        }
    }

    /// <summary>Dims one stop without the focus, through its face, unless it draws its own focus or its
    /// face holds the focused one.</summary>
    private void DimStop(Control stop, HashSet<DependencyObject> holding)
    {
        var part = FocusLift.GetPart(stop);
        if (part == LiftPart.Own)
            return;
        var face = FocusLift.GetFace(stop) ?? stop;
        if (holding.Contains(face))
            return;
        LightTo(face, part == LiftPart.Card ? FocusLift.CardLight : FocusLift.KeyLight, start: true);
    }

    /// <summary>Sends an element toward a brightness, eased by <see cref="EaseLights"/>. An element not
    /// dimmed yet is taken on only when <paramref name="start"/> is set and it stands at full opacity; any
    /// other opacity is a fade of its own, which the look leaves alone.</summary>
    private void LightTo(FrameworkElement element, double goal, bool start)
    {
        if (_lit.TryGetValue(element, out var lit) && Math.Abs(element.Opacity - lit.Wrote) >= 0.002)
        {
            Forget(lit);   // faded by its own code since it was dimmed
            lit = null;
        }

        if (lit is null)
        {
            if (!start || Math.Abs(element.Opacity - 1) >= 0.002)
                return;
            lit = new Lit(element);
            _lit[element] = lit;
        }

        if (lit.Goal == goal)
            return;
        lit.Goal = goal;
        if (!lit.Moving)
        {
            lit.Moving = true;
            _moving.Add(lit);
        }
    }

    /// <summary>One frame of every dim and every light under way. An element back at full brightness is
    /// forgotten. Only the elements that are changing are written, two on most moves: the one left and the
    /// one arrived at.</summary>
    private void EaseLights()
    {
        for (var i = _moving.Count - 1; i >= 0; i--)
        {
            var lit = _moving[i];
            var element = lit.Element;
            if (Math.Abs(element.Opacity - lit.Wrote) >= 0.002)
            {
                Forget(lit);   // faded by its own code meanwhile
                continue;
            }

            lit.Now += (lit.Goal - lit.Now) * RingEase;
            if (Math.Abs(lit.Goal - lit.Now) < 0.004)
                lit.Now = lit.Goal;
            element.Opacity = lit.Wrote = lit.Now;

            if (lit.Now == lit.Goal)
            {
                lit.Moving = false;
                _moving.RemoveAt(i);
                if (lit.Goal >= 1)
                    _lit.Remove(element);
            }
        }
    }

    /// <summary>Stops following an element's brightness, leaving its opacity as it is.</summary>
    private void Forget(Lit lit)
    {
        _lit.Remove(lit.Element);
        if (lit.Moving)
        {
            lit.Moving = false;
            _moving.Remove(lit);
        }
    }

    /// <summary>Whether two elements stand in one row in this view. They share at least half the height of
    /// the smaller one, so a move from one to the other stays in its row. Half is used rather than any
    /// overlap, because home's quick cards overlap the card under them by a point or so. With any overlap,
    /// every Down would read as a move along the row and would not be centred. If either element's place is
    /// not known, the answer is no.</summary>
    private bool SameHeight(FrameworkElement a, FrameworkElement b)
    {
        try
        {
            var ra = a.TransformToVisual(Root).TransformBounds(new Rect(0, 0, a.ActualWidth, a.ActualHeight));
            var rb = b.TransformToVisual(Root).TransformBounds(new Rect(0, 0, b.ActualWidth, b.ActualHeight));
            var shared = Math.Min(ra.Bottom, rb.Bottom) - Math.Max(ra.Top, rb.Top);
            return shared > Math.Min(ra.Height, rb.Height) / 2;
        }
        catch (ArgumentException)
        {
            return false;   // One of them is not in the tree.
        }
    }

    /// <summary>Moves an item by its composition visual's Translation, for the bounce. Nothing in the
    /// layout moves. Translation must be switched on for the element before it is written. Otherwise the
    /// write is ignored, with no error.</summary>
    private static void Nudge(FrameworkElement? element, Vector2 by)
    {
        if (element is null)
            return;

        ElementCompositionPreview.SetIsTranslationEnabled(element, true);
        ElementCompositionPreview.GetElementVisual(element).Properties
            .InsertVector3("Translation", new Vector3(by.X, by.Y, 0));
    }

    /// <summary>Enlarges an item by its composition visual's scale, about its middle. Nothing in the layout
    /// moves.</summary>
    private static void Grow(FrameworkElement? element, float by)
    {
        if (element is null)
            return;

        var visual = ElementCompositionPreview.GetElementVisual(element);
        visual.CenterPoint = new Vector3((float)element.ActualWidth / 2, (float)element.ActualHeight / 2, 0);
        visual.Scale = new Vector3(by, by, 1);
    }

    // OK, a held OK, and the Menu key on a line
    //
    // Holding OK, or the Menu key, opens a track's menu. A line says what both keys do (see RemoteLine). OK
    // on a line with a menu is delayed as it goes down. If it comes up within HoldMs, the line is pressed.
    // If it is still down after HoldMs, the menu opens. The menu takes the focus while the key is still
    // down, so the menu receives the key's repeats and its release is hidden from it (see SwallowHeldKey).
    // Without this, the release would choose the menu's first entry, which is Play.

    /// <summary>How long OK must be held on a line with a menu before the menu opens, in
    /// milliseconds.</summary>
    private const int HoldMs = 500;

    /// <summary>One press of OK on a line with a menu, from the key going down until it comes up or the
    /// menu opens. There is one object per press, so the timer can tell whether the press it was started
    /// for is still the one being held.</summary>
    private sealed class OkHold(RemoteLine line, VirtualKey key)
    {
        public RemoteLine Line { get; } = line;
        public VirtualKey Key { get; } = key;
    }

    private OkHold? _okHold;
    private DispatcherQueueTimer? _okTimer;

    /// <summary>The menu a line opened, while it is open. Back closes it before anything else.</summary>
    private MenuFlyout? _lineMenu;

    /// <summary>Whether a line's menu is open. While it is, the shell passes every key except Back to
    /// it.</summary>
    public bool LineMenuOpen => _lineMenu is not null;

    /// <summary>
    /// Whether the focus is inside an open popup over this view. This covers any menu, not only a line's
    /// menu. The wall's sort menu is one example. While a popup is open, the shell passes every key except
    /// Back to it. Without this check, arrow keys pressed in the wall's sort menu found the focus "lost",
    /// because the focus was in the popup and outside the view. The view then put the focus back on the
    /// wall, which closed the menu.
    /// </summary>
    public bool FocusInPopup => PopupWithFocus() is not null;

    /// <summary>
    /// Closes the open popup that holds the focus, as a press outside it would. Returns true if there was
    /// one. This is Back's first step, after a line's own menu.
    ///
    /// <para>A dialog is hidden through the dialog itself, not through its popup. Closing only the popup of
    /// the supporter dialog (opened from the settings panel) left the dialog's dark layer over the whole
    /// screen. The dialog still counted as open, and the focus was on nothing. The dialog does not receive
    /// Back itself, because the shell takes the key on its way down.</para>
    /// </summary>
    public bool ClosePopupWithFocus()
    {
        if (PopupWithFocus() is not { } popup)
            return false;

        if (DialogWithFocus(popup) is { } dialog)
            dialog.Hide();
        else
            popup.IsOpen = false;
        return true;
    }

    /// <summary>The dialog in <paramref name="popup"/> that holds the focused element, or null when the
    /// popup is not a dialog's.</summary>
    private ContentDialog? DialogWithFocus(Microsoft.UI.Xaml.Controls.Primitives.Popup popup)
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return null;

        for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (node is ContentDialog dialog)
                return dialog;
            if (ReferenceEquals(node, popup.Child))
                return popup.Child as ContentDialog;
        }
        return null;
    }

    /// <summary>The open popup that holds the focus. Tooltips are not counted. Null where the focus is in
    /// no popup.</summary>
    private Microsoft.UI.Xaml.Controls.Primitives.Popup? PopupWithFocus()
    {
        if (XamlRoot is null || FocusManager.GetFocusedElement(XamlRoot) is not DependencyObject focused)
            return null;

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is null or ToolTip)
                continue;
            for (DependencyObject? node = focused; node is not null; node = VisualTreeHelper.GetParent(node))
                if (ReferenceEquals(node, popup.Child))
                    return popup;
        }
        return null;
    }

    /// <summary>Whether the focus is on the transport while it is faded away (see
    /// PegasusChrome.FocusOnFadedKeys).</summary>
    public bool FocusOnFadedKeys => _chrome?.FocusOnFadedKeys() == true;

    /// <summary>
    /// OK went down, handed over by the shell. Returns true when it was dealt with here. Returns false to
    /// let it go on to the focused control, which is how a plain key presses itself.
    /// <list type="bullet">
    /// <item>A repeat does nothing, on a line or on a key. If it were not ignored, it would press whatever
    /// the first press moved the focus to. For example, a shelf row opens a record, and the focus then goes
    /// to its play key.</item>
    /// <item>On a line with no menu, the line is pressed now.</item>
    /// <item>On a line with a menu, the press waits for the key to come up, or for HoldMs to pass.</item>
    /// </list>
    /// </summary>
    public bool OkDown(object? focused, VirtualKey key, bool repeat)
    {
        if (repeat)
            return true;

        // A new press ends a hold whose release never came here. That happens when the release went to the
        // menu the hold opened, or when the window lost the keyboard while the key was down. It also ends the
        // wait for the release of the OK that brought the controls up, in case that release was lost.
        EndHold();
        _raisedBy = null;

        if (focused is not RemoteLine line)
            return false;

        if (!line.HasMenu)
        {
            line.Press();
            return true;
        }

        _okHold = new OkHold(line, key);
        if (_okTimer is null && DispatcherQueue is { } queue)
        {
            _okTimer = queue.CreateTimer();
            _okTimer.Interval = TimeSpan.FromMilliseconds(HoldMs);
            _okTimer.IsRepeating = false;
            _okTimer.Tick += (_, _) => HoldReached();
        }
        _okTimer?.Start();
        return true;
    }

    /// <summary>
    /// A key came up, handed over by the shell. The release of a held OK presses its line, if the line
    /// still has the focus. Left and Right coming up commit what they did to a seek bar. Returns true when
    /// the key was dealt with here.
    /// </summary>
    public bool KeyReleased(VirtualKey key, bool sideways)
    {
        if (sideways)
            _swallowLeft = false;

        // Left or Right was released before its menu opened, so nothing opens.
        if (_sideHeld?.Pressed == key)
            EndSideHold();

        // The release of the OK that brought the controls up. It is kept from the play key, which has the
        // focus by now. A button that saw the key go down would press itself as it comes up.
        if (_raisedBy == key)
        {
            _raisedBy = null;
            return true;
        }

        if (_okHold is { } hold && hold.Key == key)
        {
            EndHold();
            if (ReferenceEquals(FocusedInView(), hold.Line))
                hold.Line.Press();
            return true;
        }

        if (sideways)
            _chrome?.AdjustDone();
        return false;
    }

    /// <summary>
    /// The Menu key, handed over by the shell. On home, the wall and the artists and genres lists it opens
    /// the side menu, as the TV's Menu key does (see PegasusChrome.OpenMenuByMenuKey). It does so there
    /// even on a line with a menu of its own, such as a quick song on home or a record on the wall, whose
    /// menu a held OK opens. Elsewhere it opens the focused line's menu, if the line has one: a track, a
    /// popular song on an artist's or a genre's page, a station on radio. Returns false to let the key go
    /// on to the focused control.
    /// </summary>
    public bool MenuKey(object? focused, VirtualKey key, bool repeat)
    {
        // The rest of a press that opened a menu.
        if (repeat)
            return _chrome?.LibraryMenuOpen == true || focused is RemoteLine { HasMenu: true };

        if (_chrome?.OpenMenuByMenuKey() == true)
        {
            EndHold();
            Say("menu key: opened the side menu");
            return true;
        }

        if (focused is not RemoteLine { HasMenu: true } line)
            return false;

        EndHold();
        OpenLineMenu(line, key);
        return true;
    }

    /// <summary>The OK that brought the player's controls up, until it comes up again. Its release is kept
    /// from the play key (see <see cref="KeyReleased"/>). Null at other times.</summary>
    private VirtualKey? _raisedBy;

    /// <summary>
    /// An arrow or OK on the player with its controls hidden, handed over by the shell before anything
    /// moves. Returns true when it was dealt with here. Returns false where the controls are not hidden, and
    /// then the key goes on as usual.
    /// <list type="bullet">
    /// <item>Left and Right move the playhead 10 seconds back or on (a held key runs on at the seek bar's
    /// held rate), and nothing comes up (see PegasusChrome.SeekHidden). The seek is made when the key comes
    /// up.</item>
    /// <item>OK, Up and Down bring the controls up with the play key focused, and do nothing else. OK does
    /// not pause: a press made to see the keys must never stop the music. The rest of that OK, its repeats
    /// and its release, is kept from the play key (see <see cref="OkDown"/> and <see cref="KeyReleased"/>).
    /// </item>
    /// </list>
    /// </summary>
    public bool TakeHiddenControlsKey(VirtualKey key, FocusNavigationDirection? direction, bool ok, bool repeat)
    {
        if (_chrome is not { ControlsHidden: true } chrome)
            return false;

        if (direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
        {
            KeysUsed(wake: false);
            chrome.SeekHidden(direction == FocusNavigationDirection.Left ? -1 : 1, repeat);
            if (!repeat)
                Say($"keys: controls hidden, {direction} seeks");
            return true;
        }

        if (!ok && direction is not (FocusNavigationDirection.Up or FocusNavigationDirection.Down))
            return false;

        KeysUsed();
        FocusDefault();
        if (ok)
            _raisedBy = key;
        Say("keys: controls hidden, the key brought them up on the play key");
        return true;
    }

    /// <summary>Whether a queue row is being carried with the keys (see PegasusChrome.QueueRemote). While
    /// it is, the shell gives the arrows and OK to <see cref="QueueMoveKey"/>.</summary>
    public bool QueueMoving => _chrome?.QueueMoving == true;

    /// <summary>
    /// A key while a queue row is carried. Up and Down move it one slot. At an end of the queue they
    /// bounce. Left and Right bounce. OK puts the row down. A repeat of OK does nothing, so the key that
    /// chose "Move" cannot also put the row down.
    /// </summary>
    public void QueueMoveKey(FocusNavigationDirection? direction, bool ok, bool repeat)
    {
        if (_chrome is not { } chrome)
            return;

        switch (direction)
        {
            case FocusNavigationDirection.Up or FocusNavigationDirection.Down:
                if (!chrome.StepQueueMove(direction == FocusNavigationDirection.Up ? -1 : 1))
                    Bounce(direction.Value);
                return;
            case FocusNavigationDirection.Left or FocusNavigationDirection.Right:
                Bounce(direction.Value);
                return;
        }

        if (ok && !repeat)
        {
            chrome.EndQueueMove(place: true);
            Say("queue: carried row put down");
        }
    }

    /// <summary>Whether the radio wheel is open (see PegasusChrome.RadioWheel). While it is, the shell
    /// gives the arrows, OK and the Menu key to <see cref="RadioWheelKey"/>, so nothing behind the wheel
    /// moves.</summary>
    public bool RadioWheelOpen => _chrome?.RadioWheelOpen == true;

    /// <summary>How long Left or Right has to be held before the menu on that side opens. For Left, that is
    /// the side menu on home and radio. For Right, it is radio's wheel.</summary>
    private const int SideHoldMs = 2000;

    private DispatcherQueueTimer? _sideTimer;

    /// <summary>A hold of Left or Right that is being counted: the key it started with, and the direction
    /// it points. Null while no hold is being counted.</summary>
    private SideHold? _sideHeld;

    /// <param name="Unflagged">A repeat of this hold arrived without the platform's repeat flag. This is
    /// noted on the ring once per hold.</param>
    private sealed record SideHold(VirtualKey Pressed, FocusNavigationDirection Way, bool Unflagged);

    /// <summary>
    /// Left or Right, handed over by the shell before anything moves. A hold of that direction opens a menu
    /// on the screen in front (see PegasusChrome.HoldOpens). The first press moves the focus, as any arrow
    /// does, and returns false. The hold then starts counting. The keyboard's repeats move nothing and
    /// return true, so a held key does not walk along the row. <see cref="SideHoldMs"/> after the press,
    /// with the key still down, the menu opens (see <see cref="SideHoldReached"/>). Where no menu opens
    /// that way, this returns false and nothing is counted.
    /// </summary>
    public bool SideKey(VirtualKey key, FocusNavigationDirection way, bool repeat)
    {
        // The rest of a Left that opened the side menu.
        if (_swallowLeft && repeat && way == FocusNavigationDirection.Left)
            return true;
        _swallowLeft = false;

        // Left on the mini player opens the side menu, whatever lies to the card's left.
        if (!repeat && way == FocusNavigationDirection.Left && _chrome?.OpenMenuFromMiniPlayer() == true)
        {
            EndSideHold();
            _swallowLeft = true;
            Say("left on the mini player: opened the side menu");
            return true;
        }

        if (_chrome?.HoldOpens(way) != true)
        {
            EndSideHold();
            return false;
        }

        // A key-down for the key whose hold is being counted, with no key-up between, is a repeat. This is
        // true whatever the platform's repeat flag says. The flag is not trusted alone. On the Skia head, a
        // held key's repeats did not open the menu, and each one was read as a new press, which restarted
        // the two seconds.
        if (!repeat && _sideHeld is { } counting && counting.Pressed == key)
        {
            if (!counting.Unflagged)
            {
                _sideHeld = counting with { Unflagged = true };
                Say($"held {way}: a repeat came without the platform's repeat flag");
            }
            return true;
        }

        // A repeat with no hold being counted is a hold whose first press did not reach this view. For
        // example, the first key after the pointer only shows where the focus is. The hold starts counting
        // from this repeat.
        if (repeat)
        {
            if (_sideHeld is null)
                StartSideHold(key, way);
            return true;
        }

        StartSideHold(key, way);
        return false;
    }

    private void StartSideHold(VirtualKey key, FocusNavigationDirection way)
    {
        EndSideHold();
        _sideHeld = new SideHold(key, way, Unflagged: false);
        if (_sideTimer is null && DispatcherQueue is { } queue)
        {
            _sideTimer = queue.CreateTimer();
            _sideTimer.Interval = TimeSpan.FromMilliseconds(SideHoldMs);
            _sideTimer.IsRepeating = false;
            _sideTimer.Tick += (_, _) => SideHoldReached();
        }
        _sideTimer?.Start();
    }

    /// <summary>Left or Right has been held for <see cref="SideHoldMs"/>. The menu on that side opens, if a
    /// hold of that direction still opens one on the screen in front. The key's later repeats land in the
    /// menu. There, Left finds nothing further left and bounces. In the wheel, Right is ignored.</summary>
    private void SideHoldReached()
    {
        _sideTimer?.Stop();
        if (_sideHeld is not { } hold)
            return;
        _sideHeld = null;

        // This is asked of the keyboard, not trusted. A release that went to another window (the view lost
        // the keyboard while the key was down) never reached KeyReleased. Without this check, the hold
        // would open a menu for a key that nobody is holding. This applies only to the arrow keys. A
        // gamepad's pad and a navigation remote's keys are not keys the keyboard has a state for, and
        // asking about them would answer "up".
        if (hold.Pressed is VirtualKey.Left or VirtualKey.Right && !Services.Platform.HeldKeys.IsDown(hold.Pressed))
        {
            Say($"held {hold.Way}: let go outside the view, nothing opened");
            return;
        }

        if (_chrome?.OpenHeldMenu(hold.Way) == true)
            Say($"held {hold.Way} for two seconds: its menu opened");
    }

    /// <summary>Ends a hold of Left or Right without opening anything. This happens when the key comes up,
    /// another key goes down, or the pointer takes over.</summary>
    public void EndSideHold()
    {
        _sideTimer?.Stop();
        _sideHeld = null;
    }

    /// <summary>A key went down, so a hold of any other key is over. The held key's own repeats leave its
    /// hold counting. The shell asks before it routes the key.</summary>
    public void SideHoldSees(VirtualKey key)
    {
        if (_sideHeld is { } hold && hold.Pressed != key)
            EndSideHold();
    }

    /// <summary>
    /// A key while the radio wheel is open. Up and Down turn the wheel, held keys included. OK opens the
    /// entry on the marker. Left closes the wheel, and Right does nothing. Up and Down control the
    /// scrolling, and they do not act on anything behind the wheel.
    /// </summary>
    public void RadioWheelKey(FocusNavigationDirection? direction, bool ok, bool repeat)
    {
        if (_chrome is not { } chrome)
            return;

        switch (direction)
        {
            case FocusNavigationDirection.Up or FocusNavigationDirection.Down:
                chrome.TurnRadioWheel(direction == FocusNavigationDirection.Up ? -1 : 1);
                return;
            case FocusNavigationDirection.Left when !repeat:
                chrome.ShowRadioWheel(false);
                Say("radio wheel: closed with Left");
                return;
            case FocusNavigationDirection.Left or FocusNavigationDirection.Right:
                // A held Right is what opened the wheel, and its repeats keep arriving here.
                return;
        }

        if (ok && !repeat)
            chrome.PickRadioWheel();
    }

    /// <summary>An arrow moved the focus. A placement that a screen change asked for, and that the chrome
    /// has not made yet, is dropped. This stops it from taking the focus back from where the arrow put
    /// it.</summary>
    public void FocusMovedByKey() => _chrome?.DropFocusRequest();

    /// <summary>Left or Right on the focused control, handed over by the shell (see
    /// PegasusChrome.Adjust).</summary>
    public KeyTurn Adjust(object? focused, int way, bool repeat) =>
        _chrome?.Adjust(focused, way, repeat) ?? KeyTurn.NotTurnable;

    /// <summary>HoldMs after OK went down on a line with a menu, with the key still down. The menu opens on
    /// the line the press was made on, if that line still has the focus. An arrow pressed while OK is down,
    /// or Back, moves on from that line, and the menu does not open.</summary>
    private void HoldReached()
    {
        _okTimer?.Stop();
        if (_okHold is not { } hold)
            return;

        // The press is over as far as this host is concerned. Its release goes to the menu, which keeps it
        // (see SwallowHeldKey). If no menu opens, the release comes back here and finds nothing held.
        _okHold = null;

        if (ReferenceEquals(FocusedInView(), hold.Line))
            OpenLineMenu(hold.Line, hold.Key);
    }

    /// <summary>Forgets a held OK and stops its timer.</summary>
    private void EndHold()
    {
        _okTimer?.Stop();
        _okHold = null;
    }

    /// <summary>Opens a line's menu at the line. <paramref name="heldKey"/> is the key that opened it, and
    /// it is still down.</summary>
    private void OpenLineMenu(RemoteLine line, VirtualKey heldKey)
    {
        if (line.BuildMenu() is not { } menu)
            return;

        CloseLineMenu();
        _lineMenu = menu;
        menu.Opened += (_, _) => SwallowHeldKey(menu, heldKey);
        menu.Closed += (_, _) =>
        {
            if (ReferenceEquals(_lineMenu, menu))
                _lineMenu = null;
        };
        menu.ShowAt(line);
        Say("menu: opened on " + Describe(line));
    }

    /// <summary>Closes a line's menu. True if one was open.</summary>
    public bool CloseLineMenu()
    {
        if (_lineMenu is not { } menu)
            return false;
        _lineMenu = null;
        menu.Hide();
        return true;
    }

    /// <summary>
    /// The key that opened a menu is still down when the menu takes the focus. Its repeats and its release
    /// are kept from the menu until the key has come up once. A press of the key after that goes through,
    /// and that is how an entry is chosen. The remote's Back also closes the menu. The menu answers Escape
    /// on its own, but not every remote sends Escape.
    ///
    /// <para>It is attached to the menu's presenter, which is found among the open popups. The shell leaves
    /// the menu's keys alone while the menu is open (see LineMenuOpen), so the presenter is the first place
    /// that can keep them.</para>
    /// </summary>
    private void SwallowHeldKey(MenuFlyout menu, VirtualKey key)
    {
        if (XamlRoot is null)
            return;

        foreach (var popup in VisualTreeHelper.GetOpenPopupsForXamlRoot(XamlRoot))
        {
            if (popup.Child is not MenuFlyoutPresenter presenter)
                continue;

            var holding = true;

            void Down(object sender, KeyRoutedEventArgs e)
            {
                if (e.Key is VirtualKey.GoBack or VirtualKey.NavigationCancel)
                {
                    e.Handled = true;
                    menu.Hide();
                    return;
                }
                if (!holding || e.Key != key)
                    return;
                if (HeldKeys.IsRepeat(e))
                    e.Handled = true;    // This is a repeat of the held press.
                else
                    holding = false;     // A new press. The release was missed, and this press is meant.
            }

            void Up(object sender, KeyRoutedEventArgs e)
            {
                if (!holding || e.Key != key)
                    return;
                e.Handled = true;
                holding = false;
            }

            presenter.PreviewKeyDown += Down;
            presenter.PreviewKeyUp += Up;
            menu.Closed += (_, _) =>
            {
                presenter.PreviewKeyDown -= Down;
                presenter.PreviewKeyUp -= Up;
            };
            Say("menu: holding the key that opened it");
        }
    }
}
