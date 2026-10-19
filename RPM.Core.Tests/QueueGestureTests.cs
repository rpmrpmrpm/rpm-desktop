using RPM.Core.Playback;

namespace RPM.Core.Tests;

/// <summary>
/// Pulling the queue apart with a finger. None of this throws when it is wrong. Instead the list
/// will not scroll, a row seems stuck, or a drag cannot reach the end of a long queue.
/// </summary>
public class QueueGestureTests
{
    private const double Slop = 12;
    private const double Lead = 1.4;
    private const double ActionWidth = 96;

    /// <summary>Nothing is decided under the slop, whichever way the finger has moved. A row that
    /// claims the gesture on the first point of sideways movement would stop the list every time
    /// somebody swipes down the screen.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(4, 3)]
    [InlineData(-11.9, 11.9)]
    public void Nothing_is_decided_inside_the_slop(double sideways, double downwards)
        => Assert.Equal(QueueGesture.Way.Undecided,
                        QueueGesture.Decide(sideways, downwards, Slop, Lead));

    /// <summary>A scroll is never perfectly vertical. Sideways movement has to beat downwards
    /// movement by a clear margin to count as a swipe, so a finger going mostly down is a scroll
    /// however far it drifts sideways.</summary>
    [Theory]
    [InlineData(-40, 4, QueueGesture.Way.Sideways)]
    [InlineData(40, 4, QueueGesture.Way.Sideways)]
    [InlineData(-8, 40, QueueGesture.Way.Downwards)]
    [InlineData(-30, 30, QueueGesture.Way.Downwards)]
    [InlineData(-45, 30, QueueGesture.Way.Sideways)]
    public void The_steeper_axis_takes_the_gesture(double sideways, double downwards, QueueGesture.Way way)
        => Assert.Equal(way, QueueGesture.Decide(sideways, downwards, Slop, Lead));

    /// <summary>Up to the width of the action the row goes exactly where the finger does.</summary>
    [Theory]
    [InlineData(-1)]
    [InlineData(-50)]
    [InlineData(-96)]
    public void The_row_follows_the_finger_as_far_as_the_action(double sideways)
        => Assert.Equal(sideways, QueueGesture.Reach(sideways, ActionWidth), 3);

    /// <summary>Past it the row keeps moving, by less and less. A row that stops suddenly looks
    /// stuck.</summary>
    [Fact]
    public void Past_the_action_it_gives_rather_than_stops()
    {
        var held = QueueGesture.Reach(-200, ActionWidth);

        Assert.True(held < -ActionWidth, "the row must keep moving past the action");
        Assert.True(held > -200, "and it must not keep up with the finger");
    }

    /// <summary>In the other direction there is nothing to reveal, so the row barely moves, and never
    /// far enough to open a gap at its left edge.</summary>
    [Theory]
    [InlineData(20)]
    [InlineData(400)]
    public void The_other_way_it_barely_moves(double sideways)
    {
        var held = QueueGesture.Reach(sideways, ActionWidth);

        Assert.InRange(held, 0, 16);
        Assert.True(held < sideways);
    }

    /// <summary>Pushed the whole way, or thrown. Either one is enough. Requiring both would make a
    /// swipe hard work, and a short fast flick is how most people swipe.</summary>
    [Theory]
    [InlineData(-80, 0, true)]          // far enough, standing still
    [InlineData(-40, 0, false)]         // half a swipe, let go: a look, not a commitment
    [InlineData(-40, -1.2, true)]       // flicked
    [InlineData(-10, -3, false)]        // flicked, but barely moved
    [InlineData(-40, 1.2, false)]       // thrown back the other way
    public void Distance_or_speed_removes_the_track(double travelled, double speed, bool commits)
        => Assert.Equal(commits, QueueGesture.Commits(travelled, speed, 72, 0.6, 24));

    /// <summary>Half a row is a place: the order changes when the middle of the carried row passes
    /// the boundary, and not a whole row later.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(28, 0)]
    [InlineData(30, 1)]
    [InlineData(-30, -1)]
    [InlineData(300, 1)]
    public void A_place_is_half_a_row(double held, int step)
        => Assert.Equal(step, QueueGesture.Step(held, 58));

    /// <summary>A row in the middle of the list moves nothing.</summary>
    [Fact]
    public void The_list_stays_put_away_from_the_edges()
        => Assert.Equal(0, QueueGesture.EdgeStep(top: 300, rowHeight: 58, floor: 700,
                                                 edgeZone: 72, maxStep: 13));

    /// <summary>It scrolls slowly at the inner boundary of the zone and fast at the edge itself. This
    /// lets a drag cross a long queue without overshooting the place it was aimed at.</summary>
    [Fact]
    public void It_runs_faster_the_closer_to_the_edge()
    {
        var creep = QueueGesture.EdgeStep(70, 58, 700, 72, 13);
        var run = QueueGesture.EdgeStep(0, 58, 700, 72, 13);

        Assert.InRange(creep, -13, 0);
        Assert.True(run < creep);
        Assert.Equal(-13, run, 3);
    }

    /// <summary>The floor is not the bottom of the list. The tab bar covers the end of the list, and
    /// an edge zone measured to the viewport's own bottom would put the trigger behind the tab bar,
    /// where the bar takes the touch and the finger cannot hold a row.</summary>
    [Fact]
    public void The_bottom_zone_is_measured_to_the_floor_not_the_viewport()
    {
        // A row sitting just above where a 90pt tab bar starts, in a 700pt viewport.
        const double Top = 700 - 90 - 58;

        Assert.True(QueueGesture.EdgeStep(Top, 58, floor: 700 - 90, 72, 13) > 0,
                    "a row against the tab bar has to run the list on");
        Assert.Equal(0, QueueGesture.EdgeStep(Top, 58, floor: 700, 72, 13));
    }

    /// <summary>The row being carried never steps aside: it is the one the others make room for. A
    /// list that moved it as well would draw it a row away from the pointer that holds it.</summary>
    [Fact]
    public void The_carried_row_does_not_step_aside()
    {
        Assert.Equal(0, QueueGesture.Displaced(3, start: 3, at: 7));
        Assert.Equal(0, QueueGesture.Displaced(3, start: 3, at: 0));
    }

    /// <summary>Carried DOWN the list, everything it has passed moves up one, and only what it has
    /// passed. The slot it is holding counts as passed, which leaves the hole it lands in.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(2, 0)]
    [InlineData(4, -1)]
    [InlineData(6, -1)]
    [InlineData(7, 0)]
    [InlineData(9, 0)]
    public void Rows_passed_on_the_way_down_come_up_one(int index, int expected)
        => Assert.Equal(expected, QueueGesture.Displaced(index, start: 3, at: 6));

    /// <summary>And the same going up, the other way round.</summary>
    [Theory]
    [InlineData(0, 0)]
    [InlineData(1, 0)]
    [InlineData(2, 1)]
    [InlineData(5, 1)]
    [InlineData(6, 0)]
    [InlineData(9, 0)]
    public void Rows_passed_on_the_way_up_go_down_one(int index, int expected)
        => Assert.Equal(expected, QueueGesture.Displaced(index, start: 6, at: 2));

    /// <summary>Held over the slot it came from, nothing has moved. The result is worked out from
    /// where the carried track is now, and not accumulated per crossing, so a boundary crossed and
    /// crossed back leaves nothing behind. Accumulating would leave a row stranded half a line
    /// off.</summary>
    [Fact]
    public void Carried_back_to_where_it_started_leaves_the_list_flat()
    {
        for (var i = 0; i < 10; i++)
            Assert.Equal(0, QueueGesture.Displaced(i, start: 4, at: 4));
    }

    // ── a row with something behind both edges ──
    //
    // The album's track list and the now playing queue: one action on each side. Everything here has
    // to mirror everything above it. A row that is easier to swipe one way than the other is wrong in
    // a way a finger notices at once but cannot explain.

    private const double CommitDistance = 64;
    private const double FlickSpeed = 0.6;
    private const double FlickDistance = 24;

    /// <summary>
    /// The one-sided call gives exactly the one-sided result.
    ///
    /// <para>Checked against the one-sided arithmetic written out here, not against the two-sided
    /// call. The one-sided overload forwards to the two-sided one, so comparing <c>Reach(s, w)</c> to
    /// <c>Reach(s, 0, w)</c> would compare a function to itself and could never fail. The playlist
    /// page still calls the short form, and its behaviour must not change by even a point.</para>
    /// </summary>
    [Theory]
    [InlineData(0)]
    [InlineData(30)]
    [InlineData(200)]
    [InlineData(-30)]
    [InlineData(-96)]
    [InlineData(-300)]
    public void The_one_sided_reach_is_unchanged(double sideways)
    {
        var expected =
            sideways >= 0 ? Math.Min(sideways * 0.18, 16)
            : sideways >= -ActionWidth ? sideways
            : -ActionWidth - ((-sideways - ActionWidth) * 0.35);

        Assert.Equal(expected, QueueGesture.Reach(sideways, ActionWidth), 9);
    }

    /// <summary>Pushed either way, the row follows the finger exactly until the action behind that
    /// edge is fully uncovered. Any lag here would make the row feel heavy.</summary>
    [Theory]
    [InlineData(40)]
    [InlineData(96)]
    public void A_row_follows_the_finger_until_its_action_is_uncovered(double far)
    {
        Assert.Equal(far, QueueGesture.Reach(far, ActionWidth, ActionWidth), 9);
        Assert.Equal(-far, QueueGesture.Reach(-far, ActionWidth, ActionWidth), 9);
    }

    /// <summary>Past that it keeps moving by less and less instead of stopping, the same on both
    /// sides.</summary>
    [Theory]
    [InlineData(150)]
    [InlineData(400)]
    public void Past_the_action_it_gives_the_same_either_way(double far)
    {
        var right = QueueGesture.Reach(far, ActionWidth, ActionWidth);
        var left = QueueGesture.Reach(-far, ActionWidth, ActionWidth);

        Assert.Equal(-left, right, 9);
        Assert.True(right > ActionWidth, $"{right} should be past the action");
        Assert.True(right < far, "it should not be following the finger any more");
    }

    /// <summary>A side with nothing behind it still moves, but only a little: this shows there is
    /// nothing on that side without the row seeming broken. Sixteen points, whatever is asked
    /// for.</summary>
    [Theory]
    [InlineData(100)]
    [InlineData(500)]
    public void A_side_with_nothing_behind_it_barely_moves(double far)
    {
        Assert.Equal(16, QueueGesture.Reach(far, behindLeft: 0, behindRight: ActionWidth), 9);
        Assert.Equal(-16, QueueGesture.Reach(-far, behindLeft: ActionWidth, behindRight: 0), 9);
    }

    /// <summary>A row let go where it started asks for nothing.</summary>
    [Fact]
    public void A_row_let_go_at_rest_asks_for_nothing()
        => Assert.Equal(QueueGesture.Landing.None,
                        QueueGesture.Landed(0, 0, CommitDistance, FlickSpeed, FlickDistance));

    /// <summary>Pushed far enough, each direction lands on its own side and not the other's. The
    /// direction is named for the FINGER: pushing left uncovers what is behind the row's right
    /// edge.</summary>
    [Fact]
    public void A_push_past_the_commit_lands_on_the_side_it_was_pushed_towards()
    {
        Assert.Equal(QueueGesture.Landing.Leftwards,
                     QueueGesture.Landed(-80, 0, CommitDistance, FlickSpeed, FlickDistance));

        Assert.Equal(QueueGesture.Landing.Rightwards,
                     QueueGesture.Landed(80, 0, CommitDistance, FlickSpeed, FlickDistance));
    }

    /// <summary>Short of the threshold nothing happens: the row goes back and the track is untouched.
    /// This is what makes a swipe recoverable.</summary>
    [Theory]
    [InlineData(-63)]
    [InlineData(63)]
    public void A_push_short_of_the_commit_asks_for_nothing(double travelled)
        => Assert.Equal(QueueGesture.Landing.None,
                        QueueGesture.Landed(travelled, 0, CommitDistance, FlickSpeed, FlickDistance));

    /// <summary>A flick counts on both sides too. Most people swipe and do not push, and a gesture that
    /// also asked for distance would feel heavy in whichever direction was not tested.</summary>
    [Fact]
    public void A_flick_lands_either_way_without_the_distance()
    {
        Assert.Equal(QueueGesture.Landing.Leftwards,
                     QueueGesture.Landed(-30, -0.9, CommitDistance, FlickSpeed, FlickDistance));

        Assert.Equal(QueueGesture.Landing.Rightwards,
                     QueueGesture.Landed(30, 0.9, CommitDistance, FlickSpeed, FlickDistance));
    }

    /// <summary>A flick back the way it came is not a flick: thrown right from twenty points left of
    /// home asks for nothing. Otherwise a row that was nudged and abandoned would act on its own.</summary>
    [Fact]
    public void A_throw_back_towards_home_asks_for_nothing()
        => Assert.Equal(QueueGesture.Landing.None,
                        QueueGesture.Landed(-20, 0.9, CommitDistance, FlickSpeed, FlickDistance));
}
