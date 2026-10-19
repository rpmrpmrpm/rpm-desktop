using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// The split editor's region arithmetic: coincident edges are one boundary and move together,
/// separated edges (the silence a record leaves between tracks) move on their own, and nothing a
/// drag does may run into the neighbouring track.
/// </summary>
public class TrackRegionsTests
{
    private const double Min = 1.0;
    private const double Total = 300;

    /// <summary>An album file: three tracks, no gaps.</summary>
    private static List<TrackSpan> Gapless() =>
        [new(0, 100), new(100, 200), new(200, 300)];

    /// <summary>A record: three tracks with two seconds of groove between them.</summary>
    private static List<TrackSpan> WithGaps() =>
        [new(4, 100), new(102, 200), new(202, 295)];

    [Fact]
    public void A_shared_boundary_moves_both_tracks()
    {
        var t = Gapless();
        var landed = TrackRegions.MoveEdge(t, 0, isEnd: true, 120, Min, Total);

        Assert.Equal(120, landed);
        Assert.Equal(new TrackSpan(0, 120), t[0]);
        Assert.Equal(new TrackSpan(120, 200), t[1]);
        Assert.Equal(new TrackSpan(200, 300), t[2]);
    }

    [Fact]
    public void A_separated_edge_moves_alone_and_eats_into_the_gap()
    {
        var t = WithGaps();
        TrackRegions.MoveEdge(t, 0, isEnd: true, 101, Min, Total);

        Assert.Equal(new TrackSpan(4, 101), t[0]);
        Assert.Equal(new TrackSpan(102, 200), t[1]);   // its start did not budge
    }

    [Fact]
    public void A_separated_edge_stops_at_the_next_track()
    {
        var t = WithGaps();
        TrackRegions.MoveEdge(t, 0, isEnd: true, 150, Min, Total);

        Assert.Equal(102, t[0].End);                   // clamped to where track 2 begins
        Assert.Equal(new TrackSpan(102, 200), t[1]);
    }

    [Fact]
    public void A_shared_boundary_stops_short_of_collapsing_the_next_track()
    {
        var t = Gapless();
        TrackRegions.MoveEdge(t, 0, isEnd: true, 500, Min, Total);

        Assert.Equal(199, t[0].End);                   // track 1 keeps its minimum length
        Assert.Equal(new TrackSpan(199, 200), t[1]);
        Assert.Equal(new TrackSpan(200, 300), t[2]);   // and nothing beyond it moved
    }

    [Fact]
    public void An_edge_never_crosses_its_own_track()
    {
        var t = Gapless();
        TrackRegions.MoveEdge(t, 1, isEnd: false, 199.5, Min, Total);
        Assert.Equal(199, t[1].Start);

        t = Gapless();
        TrackRegions.MoveEdge(t, 1, isEnd: true, 100.2, Min, Total);
        Assert.Equal(101, t[1].End);
    }

    [Fact]
    public void The_outer_edges_clamp_to_the_recording()
    {
        var t = WithGaps();
        TrackRegions.MoveEdge(t, 0, isEnd: false, -30, Min, Total);
        Assert.Equal(0, t[0].Start);

        TrackRegions.MoveEdge(t, 2, isEnd: true, 9999, Min, Total);
        Assert.Equal(Total, t[2].End);
    }

    [Fact]
    public void Splitting_cuts_one_track_into_two_that_meet()
    {
        var t = WithGaps();
        int at = TrackRegions.SplitAt(t, 60, Min);

        Assert.Equal(1, at);
        Assert.Equal(4, t.Count);
        Assert.Equal(new TrackSpan(4, 60), t[0]);
        Assert.Equal(new TrackSpan(60, 100), t[1]);
        Assert.True(TrackRegions.EndIsLinked(t, 0));
    }

    [Fact]
    public void Splitting_refuses_a_gap_and_refuses_slivers()
    {
        var t = WithGaps();
        Assert.Equal(-1, TrackRegions.SplitAt(t, 101, Min));      // between two tracks
        Assert.Equal(-1, TrackRegions.SplitAt(t, 4.5, Min));      // would leave half a second
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void Combining_swallows_the_gap_between_two_tracks()
    {
        var t = WithGaps();
        Assert.True(TrackRegions.Combine(t, 0));

        Assert.Equal(2, t.Count);
        Assert.Equal(new TrackSpan(4, 200), t[0]);
        Assert.Equal(new TrackSpan(202, 295), t[1]);
    }

    [Fact]
    public void Combining_the_last_track_with_nothing_does_nothing()
    {
        var t = WithGaps();
        Assert.False(TrackRegions.Combine(t, 2));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void Removing_the_boundaries_in_a_selection_makes_one_track_of_everything_it_touches()
    {
        var t = WithGaps();
        Assert.True(TrackRegions.RemoveBoundariesIn(t, 90, 210));

        Assert.Single(t);
        Assert.Equal(new TrackSpan(4, 295), t[0]);
    }

    [Fact]
    public void A_selection_inside_one_track_has_no_boundary_to_remove()
    {
        var t = WithGaps();
        Assert.False(TrackRegions.RemoveBoundariesIn(t, 20, 60));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void A_stretch_of_nobodys_audio_becomes_a_track_in_order()
    {
        var t = new List<TrackSpan> { new(4, 100), new(202, 295) };
        int at = TrackRegions.Insert(t, 110, 190, Min);

        Assert.Equal(1, at);
        Assert.Equal(new TrackSpan(110, 190), t[1]);
        Assert.Equal(new TrackSpan(202, 295), t[2]);
    }

    [Fact]
    public void Inserting_over_an_existing_track_is_refused()
    {
        var t = WithGaps();
        Assert.Equal(-1, TrackRegions.Insert(t, 90, 150, Min));
        Assert.Equal(3, t.Count);
    }

    [Fact]
    public void Carving_takes_a_stretch_out_of_the_track_it_sits_in()
    {
        var t = new List<TrackSpan> { new(0, 300) };
        int at = TrackRegions.Carve(t, 100, 160, Min);

        Assert.Equal(1, at);
        Assert.Equal(3, t.Count);
        Assert.Equal(new TrackSpan(0, 100), t[0]);
        Assert.Equal(new TrackSpan(100, 160), t[1]);
        Assert.Equal(new TrackSpan(160, 300), t[2]);
    }

    [Fact]
    public void Carving_drops_the_slivers_it_would_have_left_behind()
    {
        var t = new List<TrackSpan> { new(0, 300) };
        TrackRegions.Carve(t, 0.5, 200, Min);   // half a second would be left in front

        Assert.Equal(2, t.Count);
        Assert.Equal(new TrackSpan(0.5, 200), t[0]);
        Assert.Equal(new TrackSpan(200, 300), t[1]);
    }

    [Fact]
    public void A_name_follows_the_track_that_still_starts_where_it_started()
    {
        var before = WithGaps();

        // Joined: the pair becomes one track that starts where the first one did.
        var joined = WithGaps();
        TrackRegions.Combine(joined, 0);
        Assert.Equal([0, 2], TrackRegions.MatchByStart(before, joined));

        // Split: the front half is still that track, the back half is new.
        var split = WithGaps();
        TrackRegions.SplitAt(split, 60, Min);
        Assert.Equal([0, -1, 1, 2], TrackRegions.MatchByStart(before, split));

        // Carved out of the middle: front half keeps its name, the carve and the tail are new.
        var carved = WithGaps();
        TrackRegions.Carve(carved, 40, 70, Min);
        Assert.Equal([0, -1, -1, 1, 2], TrackRegions.MatchByStart(before, carved));

        // Dropped: everything that is left is exactly where it was.
        var dropped = WithGaps();
        TrackRegions.Remove(dropped, 1);
        Assert.Equal([0, 2], TrackRegions.MatchByStart(before, dropped));
    }

    [Fact]
    public void The_track_at_a_time_is_the_one_containing_it_and_gaps_belong_to_nobody()
    {
        var t = WithGaps();
        Assert.Equal(0, TrackRegions.IndexAt(t, 50));
        Assert.Equal(-1, TrackRegions.IndexAt(t, 101));
        Assert.Equal(-1, TrackRegions.IndexAt(t, 1));

        Assert.Equal(0, TrackRegions.NearestIndex(t, 100.5));
        Assert.Equal(1, TrackRegions.NearestIndex(t, 101.7));
    }
}
