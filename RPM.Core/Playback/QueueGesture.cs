namespace RPM.Core.Playback;

/// <summary>
/// The arithmetic behind pulling a queue apart with a finger: which way a gesture is going, how far a
/// swiped row actually travels, whether letting go acts, and when a list held near its edge starts running
/// on.
///
/// <para>
/// Pure, and separate from the page, because these are the numbers a touch list is judged on and every one
/// of them is wrong in a way nobody can see from the code. A row that claims the gesture too eagerly is a
/// list that cannot be scrolled. One that clamps rather than gives reads as stuck. A commit that asks for
/// distance alone ignores a flick, which is how most people swipe. And an edge zone measured against the
/// wrong floor puts the trigger behind the tab bar, where the finger cannot reach it.
/// </para>
/// </summary>
public static class QueueGesture
{
    /// <summary>Which way a gesture turned out to be going.</summary>
    public enum Way
    {
        /// <summary>Not far enough yet to say. Nothing moves, and the list is free to scroll.</summary>
        Undecided,

        /// <summary>A swipe. The row takes the gesture off the list.</summary>
        Sideways,

        /// <summary>A scroll. The row sits the rest of it out.</summary>
        Downwards,
    }

    /// <summary>
    /// What a finger that has gone <paramref name="sideways"/> and <paramref name="downwards"/> from where
    /// it went down is asking for.
    ///
    /// <para>Neither axis decides until one of them has passed <paramref name="slop"/>. A swipe down a
    /// phone screen is never perfectly vertical. A row that starts sliding on the first small movement
    /// would make the list impossible to scroll. Past that point, sideways must beat downwards by
    /// <paramref name="lead"/> to win. So anything within about forty degrees of vertical is a
    /// scroll.</para>
    /// </summary>
    public static Way Decide(double sideways, double downwards, double slop, double lead)
    {
        if (Math.Abs(sideways) < slop && Math.Abs(downwards) < slop) return Way.Undecided;

        return Math.Abs(sideways) >= Math.Abs(downwards) * lead ? Way.Sideways : Way.Downwards;
    }

    /// <summary>
    /// Where a row goes for a finger that has moved <paramref name="sideways"/>, with an action
    /// <paramref name="actionWidth"/> wide behind its right edge.
    ///
    /// <para>This is not a clamp. A row that stops dead at the width of its action looks stuck. A row that
    /// keeps giving, but less and less, looks held. A row with nothing behind it gives barely at all. That
    /// shows there is nothing there, without refusing to move.</para>
    /// </summary>
    public static double Reach(double sideways, double actionWidth)
        => Reach(sideways, behindLeft: 0, behindRight: actionWidth);

    /// <summary>
    /// The same, for a row with an action behind EACH edge.
    ///
    /// <para>Be careful with which edge is which, because they are easy to swap. Pushing a row to the RIGHT
    /// uncovers what lies behind its LEFT edge, and the reverse. Either width may be zero, which means
    /// nothing is behind that side. The row still gives a little in that direction, because a row that does
    /// not move at all looks stuck, not empty.</para>
    /// </summary>
    public static double Reach(double sideways, double behindLeft, double behindRight)
        => sideways < 0 ? -Give(-sideways, behindRight) : Give(sideways, behindLeft);

    /// <summary>How far a row travels for a push of <paramref name="far"/> towards an action
    /// <paramref name="width"/> wide. Both values are unsigned. The giving, rather than the clamping, is
    /// the point of this method. See <see cref="Reach(double, double)"/>.</summary>
    private static double Give(double far, double width)
        => width <= 0 ? Math.Min(far * 0.18, 16)
         : far <= width ? far
         : width + ((far - width) * 0.35);

    /// <summary>
    /// Whether letting go here takes the track off the list: pushed far enough, or thrown hard enough
    /// without being pushed far at all.
    ///
    /// <para>Use distance or speed, not both. A flick is as clear as a push. Requiring both makes a swipe
    /// feel heavy.</para>
    /// </summary>
    /// <param name="travelled">How far the finger went, negative being towards the action.</param>
    /// <param name="speed">How fast it was going when it left, in points per millisecond.</param>
    public static bool Commits(double travelled, double speed,
                               double commitDistance, double flickSpeed, double flickDistance)
        => travelled <= -commitDistance || (speed <= -flickSpeed && travelled <= -flickDistance);

    /// <summary>Which way a released swipe landed, for a row that has something behind both edges. Named
    /// for the direction the FINGER went, which is the thing the person doing it is aware of.</summary>
    public enum Landing
    {
        /// <summary>Not far enough, and not thrown hard enough. The row goes home and nothing
        /// happens.</summary>
        None,

        /// <summary>Pushed left, which uncovers what lies behind the row's right edge.</summary>
        Leftwards,

        /// <summary>Pushed right, which uncovers what lies behind its left edge.</summary>
        Rightwards,
    }

    /// <summary>
    /// <see cref="Commits"/> in both directions at once: what a released swipe has actually asked for.
    ///
    /// <para>The two directions are mirror images and use the same figures. So a row cannot be easier to
    /// swipe one way than the other. A finger notices that difference at once, even when it cannot say
    /// why.</para>
    /// </summary>
    public static Landing Landed(double travelled, double speed,
                                 double commitDistance, double flickSpeed, double flickDistance)
        => Commits(travelled, speed, commitDistance, flickSpeed, flickDistance) ? Landing.Leftwards
         : Commits(-travelled, -speed, commitDistance, flickSpeed, flickDistance) ? Landing.Rightwards
         : Landing.None;

    /// <summary>
    /// Which way a carried row has to change places, given how far it is being held from the slot it
    /// currently occupies: one place down, one up, or none.
    ///
    /// <para>Half a row of travel. The change happens when the middle of the carried row passes the
    /// boundary, not a whole row later. Call this again until it returns none. A list that scrolls at its
    /// edge can cross several boundaries between two frames.</para>
    /// </summary>
    public static int Step(double held, double rowHeight)
    {
        if (held > rowHeight / 2) return 1;
        if (held < -rowHeight / 2) return -1;

        return 0;
    }

    /// <summary>
    /// Which way the row at <paramref name="index"/> has to step to make room, in rows: -1 up, +1 down, 0
    /// for one the carried track has not passed.
    ///
    /// <para>For a list that keeps its rows in place and moves them with a transform. This is the desktop
    /// drawer. Nothing is virtualized there, so rows do not have to be swapped one by one as on the phone
    /// list. The value is computed from where the carried track is now, not added up for each crossing. So
    /// a boundary that is crossed and then crossed back leaves nothing behind.</para>
    /// </summary>
    /// <param name="start">The slot the carried track was picked up from. Its row does not move, because it
    /// is the one being carried.</param>
    /// <param name="at">The slot it is holding now.</param>
    public static int Displaced(int index, int start, int at)
    {
        if (index > start && index <= at) return -1;
        if (index < start && index >= at) return 1;

        return 0;
    }

    /// <summary>
    /// How far the list scrolls this frame, with a row held at <paramref name="top"/>. It is zero unless
    /// the row is within <paramref name="edgeZone"/> of an end. The closer it gets, the faster the list
    /// scrolls. This is how dragging a file to the edge of a window scrolls it.
    /// </summary>
    /// <param name="floor">The last usable line of the list. This is not the same as its height. The tab
    /// bar sits over the foot of the list, not below it. An edge zone measured to the bottom of the
    /// viewport would start behind the tab bar.</param>
    public static double EdgeStep(double top, double rowHeight, double floor,
                                  double edgeZone, double maxStep)
    {
        if (top < edgeZone)
            return -maxStep * Depth(edgeZone - top, edgeZone);

        var bottom = top + rowHeight;
        var inner = floor - edgeZone;

        return bottom > inner ? maxStep * Depth(bottom - inner, edgeZone) : 0;
    }

    /// <summary>How far into the edge zone something is: 0 at the inner boundary and 1 at the edge itself.
    /// This makes the list move slowly at the boundary and quickly at the edge.</summary>
    private static double Depth(double into, double edgeZone)
        => Math.Clamp(into / edgeZone, 0, 1);
}
