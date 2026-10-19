using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>
/// The split screen's model. Everything the screen can do to a record is one of these calls, so this
/// is where "what happens when you delete the last two tracks" is settled, with numbers and not by
/// clicking around a dialog.
/// </summary>
public class SplitEditorStateTests
{
    private const double Total = 600;

    /// <summary>Five tracks, ten seconds of silence between each: a record, not a gapless rip.</summary>
    private static SplitEditorState FiveTracks()
    {
        var tracks = new List<SplitTrack>();
        for (int i = 0; i < 5; i++)
        {
            double start = i * 110;
            tracks.Add(new SplitTrack(start, start + 100, $"Track {i + 1}", "Artist",
                (uint)(i + 1), 1.0, BoundarySource.Silence));
        }
        var plan = new SplitPlan("/album.flac", ".flac", Total, "Album", "Artist", 1971, null, tracks);
        return SplitEditorState.Empty.FromPlan(plan);
    }

    [Fact]
    public void A_detected_plan_becomes_tracks_with_identities_of_their_own()
    {
        var state = FiveTracks();

        Assert.Equal(5, state.Count);
        Assert.Equal(5, state.Tracks.Select(t => t.Id).Distinct().Count());
        Assert.All(state.Tracks, t => Assert.NotEqual(0, t.Id));
        Assert.Equal("Track 1", state.Tracks[0].Title);
        Assert.Equal(0, state.CurrentId);   // nothing was being worked on, so nothing is
    }

    [Fact]
    public void Dropping_the_last_tracks_leaves_the_ones_before_them_exactly_as_they_were()
    {
        // Deleting the last couple of tracks on the waveform must not change a track near the top.
        // A track that was not touched must come out of the edit identical:
        // same identity, same name, same times, same tick.
        var state = FiveTracks().Tick(FiveTracks().Tracks[1].Id, true);
        var before = state.Tracks.Take(3).ToList();

        var after = state.Drop(4)!.Drop(3)!;

        Assert.Equal(3, after.Count);
        for (int i = 0; i < 3; i++)
        {
            Assert.Equal(before[i].Id, after.Tracks[i].Id);
            Assert.Equal(before[i].Title, after.Tracks[i].Title);
            Assert.Equal(before[i].Start, after.Tracks[i].Start);
            Assert.Equal(before[i].End, after.Tracks[i].End);
            Assert.Equal(before[i].Ticked, after.Tracks[i].Ticked);
            Assert.Equal(before[i].Source, after.Tracks[i].Source);
        }
    }

    [Fact]
    public void Dropping_the_track_being_worked_on_moves_the_work_to_what_took_its_place()
    {
        var state = FiveTracks();
        var after = state.WithCurrentIndex(2).Drop(2)!;

        Assert.Equal(4, after.Count);
        Assert.Equal(state.Tracks[3].Id, after.CurrentId);   // what is now in that place
    }

    [Fact]
    public void Dropping_the_last_track_of_all_leaves_the_work_on_the_new_last_one()
    {
        var state = FiveTracks();
        var after = state.WithCurrentIndex(4).Drop(4)!;

        Assert.Equal(state.Tracks[3].Id, after.CurrentId);
    }

    [Fact]
    public void Dropping_the_playing_track_stops_the_preview_belonging_to_it()
    {
        var state = FiveTracks();
        var playing = state.Tracks[2];
        var after = state.WithPlaying(playing.Id, (playing.Start, playing.End)).Drop(2)!;

        Assert.Equal(0, after.PlayingId);
        Assert.Null(after.PlayRegion);
    }

    [Fact]
    public void Dropping_a_track_leaves_a_preview_of_another_one_alone()
    {
        var state = FiveTracks();
        var playing = state.Tracks[0];
        var after = state.WithPlaying(playing.Id, (playing.Start, playing.End)).Drop(4)!;

        Assert.Equal(playing.Id, after.PlayingId);
    }

    [Fact]
    public void Splitting_keeps_the_name_on_the_front_half_and_works_on_the_new_back_half()
    {
        var state = FiveTracks();
        var after = state.SplitAt(50)!;

        Assert.Equal(6, after.Count);
        Assert.Equal(state.Tracks[0].Id, after.Tracks[0].Id);
        Assert.Equal("Track 1", after.Tracks[0].Title);
        Assert.Equal(50, after.Tracks[0].End, 6);
        Assert.Equal("", after.Tracks[1].Title);
        Assert.Equal(after.Tracks[1].Id, after.CurrentId);
        Assert.DoesNotContain(after.Tracks[1].Id, state.Tracks.Select(t => t.Id));
    }

    [Fact]
    public void There_is_nothing_to_split_in_the_silence_between_two_tracks()
    {
        Assert.Null(FiveTracks().SplitAt(105));
    }

    [Fact]
    public void There_is_nothing_to_split_a_hair_from_an_edge()
    {
        Assert.Null(FiveTracks().SplitAt(0.4));
    }

    [Fact]
    public void Combining_keeps_the_first_tracks_name_and_swallows_the_gap()
    {
        var state = FiveTracks();
        var after = state.Combine(1)!;

        Assert.Equal(4, after.Count);
        Assert.Equal(state.Tracks[1].Id, after.Tracks[1].Id);
        Assert.Equal("Track 2", after.Tracks[1].Title);
        Assert.Equal(state.Tracks[2].End, after.Tracks[1].End, 6);
        Assert.Equal(after.Tracks[1].Id, after.CurrentId);
        Assert.Equal(BoundarySource.Manual, after.Tracks[1].Source);
    }

    [Fact]
    public void The_last_track_has_nothing_to_combine_with()
    {
        Assert.Null(FiveTracks().Combine(4));
    }

    [Fact]
    public void Moving_an_edge_changes_where_a_track_is_not_which_track_it_is()
    {
        var state = FiveTracks();
        var after = state.MoveEdge(1, isEnd: true, 215)!;   // out into the silence after it

        Assert.Equal(state.Count, after.Count);
        Assert.Equal(state.Tracks[1].Id, after.Tracks[1].Id);
        Assert.Equal("Track 2", after.Tracks[1].Title);
        Assert.Equal(215, after.Tracks[1].End, 6);
        Assert.Equal(BoundarySource.Manual, after.Tracks[1].Source);
        // Its neighbour is a separate region with a real gap: it does not follow.
        Assert.Equal(state.Tracks[2].Start, after.Tracks[2].Start, 6);
        Assert.Equal(BoundarySource.Silence, after.Tracks[2].Source);
    }

    [Fact]
    public void Moving_a_shared_boundary_marks_both_sides_of_it_as_hand_made()
    {
        // A gapless rip: track 2 ends exactly where track 3 begins, so the two edges are one boundary.
        var tracks = new List<SplitTrack>
        {
            new(0, 100, "A", null, 1, 1.0, BoundarySource.CueSheet),
            new(100, 200, "B", null, 2, 1.0, BoundarySource.CueSheet),
            new(200, 300, "C", null, 3, 1.0, BoundarySource.CueSheet),
        };
        var state = SplitEditorState.Empty.FromPlan(
            new SplitPlan("/a.flac", ".flac", 300, null, null, 0, null, tracks));

        var after = state.MoveEdge(1, isEnd: true, 210)!;

        Assert.Equal(210, after.Tracks[1].End, 6);
        Assert.Equal(210, after.Tracks[2].Start, 6);
        Assert.Equal(BoundarySource.Manual, after.Tracks[1].Source);
        Assert.Equal(BoundarySource.Manual, after.Tracks[2].Source);
        Assert.Equal(BoundarySource.CueSheet, after.Tracks[0].Source);
    }

    [Fact]
    public void An_edge_cannot_be_dragged_through_its_own_track()
    {
        var state = FiveTracks();
        var after = state.MoveEdge(0, isEnd: true, -50)!;

        Assert.True(after.Tracks[0].Length >= after.MinLength);
    }

    [Fact]
    public void An_edge_stops_at_the_neighbour_rather_than_shoving_it_along()
    {
        var state = FiveTracks();
        var after = state.MoveEdge(1, isEnd: true, 400)!;   // straight through tracks 3 and 4

        Assert.Equal(state.Tracks[2].Start, after.Tracks[1].End, 6);
        Assert.Equal(state.Tracks[2].Start, after.Tracks[2].Start, 6);
        Assert.Equal(state.Tracks[2].End, after.Tracks[2].End, 6);
    }

    [Fact]
    public void Carving_out_a_stretch_makes_a_new_nameless_track_and_trims_what_covered_it()
    {
        var state = FiveTracks();
        var after = state.Carve(40, 70)!;

        var carved = after.Tracks.First(t => Math.Abs(t.Start - 40) < 0.001);
        Assert.Equal(70, carved.End, 6);
        Assert.Equal("", carved.Title);
        Assert.Equal(carved.Id, after.CurrentId);
        Assert.DoesNotContain(carved.Id, state.Tracks.Select(t => t.Id));
        // The track it was cut out of keeps its name on the piece that still starts where it started.
        Assert.Equal("Track 1", after.Tracks[0].Title);
        Assert.Equal(40, after.Tracks[0].End, 6);
        Assert.Null(after.Selection);
    }

    [Fact]
    public void A_stretch_too_short_to_be_a_track_is_refused()
    {
        Assert.Null(FiveTracks().Carve(40, 40.2));
    }

    [Fact]
    public void Clearing_the_markers_in_a_selection_makes_one_track_of_what_it_covered()
    {
        var state = FiveTracks();
        var after = state.ClearBoundariesIn(50, 250)!;

        Assert.Equal(3, after.Count);
        Assert.Equal(state.Tracks[0].Id, after.Tracks[0].Id);
        Assert.Equal("Track 1", after.Tracks[0].Title);
        Assert.Equal(state.Tracks[2].End, after.Tracks[0].End, 6);
        Assert.Equal(after.Tracks[0].Id, after.CurrentId);
    }

    [Fact]
    public void A_selection_holding_no_marker_clears_nothing()
    {
        Assert.Null(FiveTracks().ClearBoundariesIn(20, 60));
    }

    [Fact]
    public void Ticks_and_names_belong_to_the_track_and_survive_its_neighbours_being_edited()
    {
        var state = FiveTracks();
        int id = state.Tracks[3].Id;
        state = state.Tick(id, true).Rename(id, "Kashmir", "Led Zeppelin");

        var after = state.Drop(0)!.Combine(0)!;

        var track = after.Track(id)!;
        Assert.True(track.Ticked);
        Assert.Equal("Kashmir", track.Title);
        Assert.Equal("Led Zeppelin", track.Artist);
    }

    [Fact]
    public void Ticking_them_all_is_one_edit_and_says_so()
    {
        var state = FiveTracks().TickAll(true);

        Assert.True(state.AllTicked);
        Assert.Equal(5, state.TickedTracks.Count);
        Assert.False(state.TickAll(false).AllTicked);
    }

    [Fact]
    public void The_cursor_stays_on_the_recording()
    {
        var state = FiveTracks();

        Assert.Equal(0, state.WithCursor(-10).Cursor);
        Assert.Equal(Total, state.WithCursor(Total + 500).Cursor);
    }

    [Fact]
    public void A_selection_marked_out_backwards_is_still_a_selection()
    {
        var state = FiveTracks().WithSelection((90, 30));

        Assert.Equal((30d, 90d), state.Selection);
        Assert.True(state.HasSelection);
        Assert.False(state.WithSelection(null).HasSelection);
    }

    [Fact]
    public void A_re_detection_replaces_the_tracks_and_keeps_the_place_in_the_record()
    {
        var state = FiveTracks().WithCurrentIndex(3).Tick(FiveTracks().Tracks[0].Id, true);
        var old = state.Tracks.Select(t => t.Id).ToHashSet();

        var plan = new SplitPlan("/album.flac", ".flac", Total, null, null, 0, null,
            Enumerable.Range(0, 5)
                .Select(i => new SplitTrack(i * 120, i * 120 + 110, null, null, (uint)(i + 1), 1.0,
                    BoundarySource.Silence))
                .ToList());
        var after = state.FromPlan(plan);

        // Nothing from the previous reading survives as an identity, so a recycled row cannot end up
        // showing a track it no longer holds.
        Assert.All(after.Tracks, t => Assert.DoesNotContain(t.Id, old));
        Assert.Equal(3, after.CurrentIndex);
        Assert.Equal(0, after.PlayingId);
    }

    [Fact]
    public void A_re_detection_that_finds_fewer_tracks_keeps_the_place_inside_them()
    {
        var state = FiveTracks().WithCurrentIndex(4);
        var plan = new SplitPlan("/album.flac", ".flac", Total, null, null, 0, null,
            new List<SplitTrack> { new(0, 200, null, null, 1, 1.0, BoundarySource.Silence) });

        var after = state.FromPlan(plan);

        Assert.Equal(0, after.CurrentIndex);
    }

    [Fact]
    public void The_plan_it_gives_back_is_numbered_by_position_and_drops_slivers()
    {
        // A detection that produced a sliver: an edit cannot make one (an edge stops a second short
        // of its own track), but a plan can arrive holding one, and a sliver is not a track.
        var tracks = new List<SplitTrack>
        {
            new(0, 100, "A", null, 1, 1.0, BoundarySource.Silence),
            new(110, 110.4, "sliver", null, 2, 1.0, BoundarySource.Silence),
            new(120, 220, "B", null, 3, 1.0, BoundarySource.Silence),
        };
        var state = SplitEditorState.Empty.FromPlan(
            new SplitPlan("/a.flac", ".flac", 300, null, null, 0, null, tracks));

        var plan = state.ToPlanTracks();

        Assert.Equal(2, plan.Count);
        Assert.Equal(new uint[] { 1, 2 }, plan.Select(t => t.TrackNumber).ToArray());
        Assert.DoesNotContain(plan, t => t.Title == "sliver");
    }

    [Fact]
    public void An_untitled_track_goes_into_the_plan_as_untitled_not_as_empty_text()
    {
        var state = FiveTracks().Rename(FiveTracks().Tracks[0].Id, "   ", "");
        var plan = state.ToPlanTracks();

        Assert.Null(plan[0].Title);
        Assert.Null(plan[0].Artist);
    }

    [Fact]
    public void Where_a_time_falls_is_asked_of_the_state_not_worked_out_by_the_caller()
    {
        var state = FiveTracks();

        Assert.Equal(0, state.IndexAt(50));
        Assert.Equal(-1, state.IndexAt(105));    // the gap belongs to nobody
        Assert.Equal(state.Tracks[2].Id, state.IdAt(2));
        Assert.Equal(0, state.IdAt(99));
    }

    [Fact]
    public void An_edge_moved_by_an_album_time_says_that_is_where_it_came_from()
    {
        // Adopting a release never moves a cut. The per-row apply-timing button does, deliberately,
        // and the dot on that row has to say so and not call it a hand edit.
        var state = FiveTracks();
        var after = state.MoveEdge(0, isEnd: true, 95, BoundarySource.DurationAligned)!;

        Assert.Equal(BoundarySource.DurationAligned, after.Tracks[0].Source);
        Assert.Equal(BoundarySource.Manual, state.MoveEdge(0, isEnd: true, 95)!.Tracks[0].Source);
    }

    [Fact]
    public void Naming_a_track_never_moves_it()
    {
        var state = FiveTracks();
        var after = state.Rename(state.Tracks[2].Id, "Achilles Last Stand", "Led Zeppelin");

        for (int i = 0; i < state.Count; i++)
        {
            Assert.Equal(state.Tracks[i].Start, after.Tracks[i].Start);
            Assert.Equal(state.Tracks[i].End, after.Tracks[i].End);
            Assert.Equal(state.Tracks[i].Source, after.Tracks[i].Source);
        }
    }

    [Fact]
    public void An_albums_times_become_guide_marks_laid_from_the_first_track()
    {
        // The record starts 110 s in here, so the sleeve's ruler starts there too: a recorded side
        // does not begin where the music does.
        var state = FiveTracks().Drop(0)!;
        var guides = state.AlbumGuides(new double[] { 100, 200, 50 });

        Assert.Equal(new double[] { 110, 210, 410, 460 }, guides);
    }

    [Fact]
    public void The_guides_stop_at_a_track_the_listing_gives_no_time_for()
    {
        var guides = FiveTracks().AlbumGuides(new double[] { 100, 0, 200 });

        Assert.Equal(new double[] { 0, 100 }, guides);
    }

    [Fact]
    public void The_guides_stop_at_the_end_of_the_recording()
    {
        var guides = FiveTracks().AlbumGuides(new double[] { 300, 400, 200 });

        Assert.Equal(new double[] { 0, 300 }, guides);
    }

    [Fact]
    public void No_release_adopted_means_no_guides()
    {
        Assert.Empty(FiveTracks().AlbumGuides(null));
        Assert.Empty(FiveTracks().AlbumGuides([]));
        Assert.Empty(SplitEditorState.Empty.AlbumGuides(new double[] { 100 }));
    }

    [Fact]
    public void Setting_what_is_already_set_hands_back_the_very_same_state()
    {
        // The screen redraws when it is handed a state that is not the one it has. Repeats (a scrub
        // reporting the position it already reported) must be the SAME object, or every one of them
        // is a redraw of rows nobody touched.
        var state = FiveTracks().WithCursor(42).WithSelection((10, 20)).WithCurrentIndex(1);

        Assert.Same(state, state.WithCursor(42));
        Assert.Same(state, state.WithSelection((10, 20)));
        Assert.Same(state, state.WithCurrent(state.CurrentId));
        Assert.Same(state, state.NotPlaying);
        Assert.Same(state, state.Rename(state.Tracks[0].Id, state.Tracks[0].Title, state.Tracks[0].Artist));
        Assert.Same(state, state.Tick(state.Tracks[0].Id, false));
        Assert.NotSame(state, state.WithCursor(43));
    }

    [Fact]
    public void A_state_that_only_moved_the_cursor_still_holds_the_same_track_list()
    {
        // What tells the screen it can skip redrawing the rows: an edit that did not touch the tracks
        // shares the list it was given, so a reference check is enough to know.
        var state = FiveTracks();

        Assert.Same(state.Tracks, state.WithCursor(120).Tracks);
        Assert.Same(state.Tracks, state.WithSelection((5, 15)).Tracks);
        Assert.Same(state.Tracks, state.WithCurrentIndex(2).Tracks);
        Assert.NotSame(state.Tracks, state.Drop(0)!.Tracks);
    }

    [Fact]
    public void An_edit_that_does_not_apply_says_so_rather_than_half_happening()
    {
        var state = FiveTracks();

        Assert.Null(state.Drop(-1));
        Assert.Null(state.Drop(99));
        Assert.Null(state.MoveEdge(99, isEnd: true, 10));
        Assert.Null(state.Combine(-1));
        Assert.Equal(state, state.Rename(9999, "nope", null));
        Assert.Equal(state, state.Tick(9999, true));
    }
}
