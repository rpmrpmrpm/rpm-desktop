using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Windows.Foundation;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The screen that the arrow keys search: the element that holds it, and the parts inside it that it does
/// not draw (see PegasusChrome.FocusScreen). Both are stated. A screen that draws all of itself gives an
/// empty list.
///
/// <para>Parts that are not drawn are left out of the search. They still stand in the tree, laid out and
/// focusable, so the search could reach them. For example, the record turning page holds the lyric view's
/// own seek bar and keys at opacity 0, under every other view. Down from the transport went to that seek
/// bar, which is a glow along the bottom of the screen over nothing. These parts cannot be clicked, so they
/// must not be stops.</para>
/// </summary>
public sealed class RemoteScreen(UIElement area, IReadOnlyList<UIElement> notDrawn)
{
    /// <summary>The element that holds the screen in front.</summary>
    public UIElement Area { get; } = area;

    /// <summary>The parts of <see cref="Area"/> this screen does not draw. Nothing inside them is a
    /// stop.</summary>
    public IReadOnlyList<UIElement> NotDrawn { get; } = notDrawn;

    /// <summary>Whether an element is one of the parts not drawn.</summary>
    public bool LeavesOut(DependencyObject node)
    {
        foreach (var part in NotDrawn)
            if (ReferenceEquals(part, node))
                return true;
        return false;
    }
}

/// <summary>
/// Where an arrow on the TV remote moves the focus in Pegasus (see ShellPage.TakePegasusKey). Null means
/// there is nowhere to go in that direction, and the caller bounces.
///
/// <para><b>Found by layout position, not by the framework's search.</b> FocusManager.FindNextElement leaves
/// out any element that another element overlaps. The mini player floats over the top row of the wall and
/// of home. So Right from the wall's first cover went into the mini player, past the two covers beside it,
/// and the covers under its edge could not be reached at all. WinUI 3 has no option to turn that off. Its
/// choice of nearest element is also too loose. Down from a record page's play key went across to the
/// eighteenth line of the track list in the other column, and its Projection strategy did not stop that.
/// Nothing in Pegasus sets an XYFocus target, so nothing the framework would have honoured is lost.</para>
///
/// <para><b>In the row or the column, inside the list first.</b> Left and Right go only to an element that
/// shares some height with the focused element. Up and Down go only to one that shares some width. When the
/// focused element is in a scroller inside the screen in front (the wall, a row on home, a page's track
/// list, the drawer's lists), that scroller's realized elements are searched first. Then the whole screen
/// in front is searched. Anything else is in another row or another column. That is the end of this one:
/// the focus bounces back and does not continue into the next row or column.</para>
///
/// <para><b>Then, for Up and Down only, the list's nearest row.</b> If nothing in the column is that way,
/// a list still goes to its nearest row. Inside that row it goes to the element nearest across. For
/// example, Down from the end of the wall's next-to-last row reaches the last record of a short last
/// row.</para>
/// </summary>
public static class RemoteSearch
{
    /// <summary>Two candidates whose gaps differ by no more than this many points count as equally near. It
    /// is used for Up and Down. Within that range, the candidate nearer across wins.</summary>
    private const double RowSlack = 12;

    /// <summary>How far outside a scroller's content, in points, an element may stand and still count as
    /// in it: the scroller's own padding, and a little more.</summary>
    private const double ContentSlack = 100;

    /// <summary>
    /// Gives the remote's focus to a control. It first turns off the framework's own focus rectangle on
    /// that control. Pegasus draws its own glow, and the framework's rectangle inside it was a second
    /// outline, partly cut off on home's cards. Focusing with FocusState.Programmatic instead of Keyboard
    /// did not stop the rectangle, because the framework still draws it when the last input was a key.
    /// Every focus the remote moves goes through here.
    /// </summary>
    public static bool Give(Control target)
    {
        target.UseSystemFocusVisuals = false;
        return target.Focus(FocusState.Programmatic);
    }

    /// <summary>The element the focus moves to from <paramref name="from"/>, searching the screen in
    /// front, or null where there is nothing in that direction.</summary>
    public static Control? Next(FrameworkElement? from, FocusNavigationDirection direction, RemoteScreen screen)
    {
        if (from is null)
            return null;

        var list = ScrollerOf(from, screen.Area);

        // In the row or the column, inside the list first and then anywhere on the screen in front.
        if (list is not null && Nearest(from, direction, list, ContentOf(list), screen, acrossFree: false) is { } inLine)
            return inLine;
        if (screen.Area is FrameworkElement area
            && Nearest(from, direction, area, AroundOf(area), screen, acrossFree: false) is { } onScreen)
            return onScreen;

        // Only then, for Up and Down, the list's nearest row, wherever it is across. This runs after the
        // screen pass. Otherwise Up from home's banner dots could go to the banner's far arrow, past the
        // mini player that stands right above them.
        var sideways = direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right;
        return list is not null && !sideways
            ? Nearest(from, direction, list, ContentOf(list), screen, acrossFree: true)
            : null;
    }

    /// <summary>
    /// For Up and Down only: the nearest row in that direction inside <paramref name="page"/>, and in it the
    /// element nearest across, whether or not it shares any width with <paramref name="from"/>. This is
    /// for a page of shelves of different lengths. Down from the eighth card of a long shelf may have
    /// nothing under it in a short shelf. Null for Left and Right, and where there is no row in that
    /// direction.
    /// </summary>
    public static Control? NearestRow(
        FrameworkElement from, FocusNavigationDirection direction, ScrollViewer page, RemoteScreen screen)
    {
        if (direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right)
            return null;
        return Nearest(from, direction, page, ContentOf(page), screen, acrossFree: true);
    }

    /// <summary>
    /// The nearest focusable element in that direction under <paramref name="root"/>, measured in its
    /// points. It considers only elements inside <paramref name="within"/>, and outside the parts that
    /// <paramref name="screen"/> does not draw. Which one is chosen is decided by <see cref="Pick"/>.
    /// </summary>
    private static Control? Nearest(
        FrameworkElement from, FocusNavigationDirection direction, FrameworkElement root, Rect within,
        RemoteScreen screen, bool acrossFree)
    {
        if (BoundsIn(from, root) is not { } at)
            return null;

        var controls = new List<Control>();
        var places = new List<Rect>();

        var stack = new Stack<DependencyObject>();
        stack.Push(root);
        while (stack.Count > 0)
        {
            var node = stack.Pop();
            if (node is UIElement { Visibility: Visibility.Collapsed } || screen.LeavesOut(node))
                continue;

            if (!ReferenceEquals(node, from) && !ReferenceEquals(node, root)
                && node is Control { IsTabStop: true, IsEnabled: true, IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } control
                && BoundsIn(control, root) is { } r
                && Intersects(r, within))
            {
                controls.Add(control);
                places.Add(r);
            }

            for (var i = VisualTreeHelper.GetChildrenCount(node) - 1; i >= 0; i--)
                stack.Push(VisualTreeHelper.GetChild(node, i));
        }

        var picked = Pick(at, places, direction, acrossFree);
        return picked < 0 ? null : controls[picked];
    }

    /// <summary>
    /// Which of the <paramref name="candidates"/> the focus moves to from <paramref name="at"/>, all in one
    /// element's points. Returns -1 for none. A candidate counts when it is ahead of the focused element.
    /// Ahead means its near edge is past the focused element's middle, so neighbours that overlap a little
    /// still count. For Left and Right it must also share some height. For Up and Down it must share some
    /// width, unless <paramref name="acrossFree"/> is true. Of those, the nearest row is chosen (or, sideways,
    /// the nearest along the row). Ties go to the candidate whose middle is nearest the focused element's,
    /// across the move.
    /// </summary>
    internal static int Pick(Rect at, IReadOnlyList<Rect> candidates, FocusNavigationDirection direction, bool acrossFree)
    {
        var sideways = direction is FocusNavigationDirection.Left or FocusNavigationDirection.Right;
        var found = new List<(int Index, double Gap, double Across)>();

        for (var i = 0; i < candidates.Count; i++)
        {
            var r = candidates[i];

            // Ahead is measured from the focused element's middle, not its edge. An edge test with a little
            // slack missed a card in home's quick selection when its neighbours touched it, so Up and Down
            // went past it.
            var ahead = direction switch
            {
                FocusNavigationDirection.Right => r.Left > (at.Left + at.Right) / 2,
                FocusNavigationDirection.Left => r.Right < (at.Left + at.Right) / 2,
                FocusNavigationDirection.Down => r.Top > (at.Top + at.Bottom) / 2,
                _ => r.Bottom < (at.Top + at.Bottom) / 2,
            };
            var gap = Math.Max(0, direction switch
            {
                FocusNavigationDirection.Right => r.Left - at.Right,
                FocusNavigationDirection.Left => at.Left - r.Right,
                FocusNavigationDirection.Down => r.Top - at.Bottom,
                _ => at.Top - r.Bottom,
            });
            var shares = sideways
                ? Math.Min(at.Bottom, r.Bottom) - Math.Max(at.Top, r.Top)
                : Math.Min(at.Right, r.Right) - Math.Max(at.Left, r.Left);

            if (!ahead || (shares <= 1 && (sideways || !acrossFree)))
                continue;

            // The distance between the middles, across the move. It is measured even where the two share some
            // height. Two cards in the next column can both share some height with the focused card, and the
            // one that overlaps by a few points is not always the one beside it. Without this, Right from a
            // card in home's quick selection can land on the card above the one beside it.
            var across = sideways
                ? Math.Abs((r.Top + r.Bottom - at.Top - at.Bottom) / 2)
                : Math.Abs((r.Left + r.Right - at.Left - at.Right) / 2);
            found.Add((i, gap, across));
        }

        if (found.Count == 0)
            return -1;

        var nearest = found.Min(f => f.Gap);
        var slack = sideways ? 1 : RowSlack;
        return found.Where(f => f.Gap <= nearest + slack)
                    .OrderBy(f => f.Across)
                    .ThenBy(f => f.Gap)
                    .First().Index;
    }

    /// <summary>
    /// Where a scroller's content is, in its own points, with room for its padding. An ItemsRepeater keeps
    /// the elements it has recycled in the tree, arranged far outside its content (at -10000). Those are not
    /// there to go to. Without this check, Up from home's banner went to a card that was not on screen.
    /// </summary>
    private static Rect ContentOf(ScrollViewer list) => new(
        -list.HorizontalOffset - ContentSlack,
        -list.VerticalOffset - ContentSlack,
        Math.Max(list.ExtentWidth, list.ViewportWidth) + (2 * ContentSlack),
        Math.Max(list.ExtentHeight, list.ViewportHeight) + (2 * ContentSlack));

    /// <summary>The screen in front, and two of its sizes on every side, in its own points. Realized
    /// elements of a list scrolled past the edge are inside it. Recycled ones parked at -10000 are
    /// not.</summary>
    private static Rect AroundOf(FrameworkElement area) => new(
        -2 * area.ActualWidth,
        -2 * area.ActualHeight,
        5 * area.ActualWidth,
        5 * area.ActualHeight);

    /// <summary>Whether two rectangles share any area.</summary>
    private static bool Intersects(Rect a, Rect b) =>
        a.Left < b.Right && b.Left < a.Right && a.Top < b.Bottom && b.Top < a.Bottom;

    /// <summary>An element's bounds in another element's points, or null when either is out of the
    /// tree.</summary>
    private static Rect? BoundsIn(FrameworkElement element, UIElement other)
    {
        try
        {
            return element.TransformToVisual(other)
                          .TransformBounds(new Rect(0, 0, element.ActualWidth, element.ActualHeight));
        }
        catch (ArgumentException)
        {
            return null;
        }
    }

    /// <summary>
    /// The nearest ScrollViewer above an element and inside the screen in front, or null if there is none
    /// before the scope, or if the element is not inside the scope. Pegasus itself stands in a scroller
    /// further up the window. If that scroller were taken as the list of an element that has no scroller of
    /// its own (the corner key, the mini player), the search would go through the whole window. For example,
    /// Down from the corner key went to the mini player, and from there into the main window behind the view.
    /// </summary>
    private static ScrollViewer? ScrollerOf(DependencyObject element, UIElement scope)
    {
        ScrollViewer? nearest = null;
        for (var node = VisualTreeHelper.GetParent(element); node is not null; node = VisualTreeHelper.GetParent(node))
        {
            if (ReferenceEquals(node, scope))
                return nearest;
            nearest ??= node as ScrollViewer;
        }
        return null;   // the element is not inside the scope at all
    }
}
