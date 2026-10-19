using RPM.Core.Splitting;

namespace RPM.Core.Tests;

/// <summary>The waveform viewport's promises: a zoom keeps what was under the pointer under the
/// pointer, the view never leaves the recording, and the ruler steps in round numbers.</summary>
public class WaveTimelineTests
{
    private const double Total = 1200;   // a 20-minute side

    [Fact]
    public void Fitting_shows_the_whole_recording()
    {
        var view = WaveTimeline.Fit(Total);
        Assert.Equal(0, view.Start);
        Assert.Equal(Total, view.Span);
    }

    [Fact]
    public void Zooming_holds_the_anchor_where_it_was_on_screen()
    {
        var view = new WaveView(0, Total);
        const double width = 1000;
        const double anchor = 300;                       // a quarter across
        double before = WaveTimeline.TimeToX(view, anchor, width);

        var zoomed = WaveTimeline.ZoomAt(view, anchor, 4, Total);

        Assert.Equal(Total / 4, zoomed.Span, 6);
        Assert.Equal(before, WaveTimeline.TimeToX(zoomed, anchor, width), 6);
    }

    [Fact]
    public void Zooming_out_at_the_edge_stays_inside_the_recording()
    {
        var view = new WaveView(1150, 50);               // parked at the very end
        var zoomed = WaveTimeline.ZoomAt(view, 1195, 0.25, Total);

        Assert.Equal(200, zoomed.Span, 6);
        Assert.Equal(Total - 200, zoomed.Start, 6);      // pushed back rather than hanging off the end
        Assert.True(zoomed.End <= Total + 1e-9);
    }

    [Fact]
    public void The_view_never_gets_tighter_than_the_floor_or_wider_than_the_side()
    {
        var tight = WaveTimeline.ZoomAt(new WaveView(0, Total), 600, 1e6, Total);
        Assert.Equal(WaveTimeline.MinSpanSeconds, tight.Span, 6);

        var wide = WaveTimeline.ZoomAt(new WaveView(0, Total), 600, 1e-6, Total);
        Assert.Equal(Total, wide.Span, 6);
        Assert.Equal(0, wide.Start);
    }

    [Fact]
    public void Panning_clamps_at_both_ends()
    {
        var view = new WaveView(100, 200);
        Assert.Equal(0, WaveTimeline.Pan(view, -500, Total).Start);
        Assert.Equal(Total - 200, WaveTimeline.Pan(view, 5000, Total).Start, 6);
    }

    [Fact]
    public void Zooming_to_a_selection_frames_it_with_air_either_side()
    {
        var view = WaveTimeline.Around(400, 500, Total);

        Assert.True(view.Start < 400);
        Assert.True(view.End > 500);
        Assert.True(view.Span < 130);   // 100 s plus a little, not a lot
    }

    [Fact]
    public void Following_a_playhead_leaves_the_view_alone_until_it_reaches_the_edge()
    {
        var view = new WaveView(0, 100);

        Assert.Equal(view, WaveTimeline.Follow(view, 50, Total));   // comfortably inside
        var moved = WaveTimeline.Follow(view, 95, Total);
        Assert.True(moved.Start > 0);
        Assert.True(moved.Contains(95));
    }

    [Fact]
    public void Following_a_jump_somewhere_else_recentres()
    {
        var view = new WaveView(0, 100);
        var moved = WaveTimeline.Follow(view, 600, Total);

        Assert.Equal(550, moved.Start, 6);
        Assert.Equal(100, moved.Span, 6);
    }

    [Fact]
    public void Times_and_pixels_round_trip()
    {
        var view = new WaveView(120, 60);
        double x = WaveTimeline.TimeToX(view, 150, 900);

        Assert.Equal(450, x, 6);
        Assert.Equal(150, WaveTimeline.XToTime(view, x, 900), 6);
    }

    [Fact]
    public void Ruler_steps_are_round_and_never_crowd()
    {
        double step = WaveTimeline.TickSeconds(new WaveView(0, 1200), 900);
        Assert.Contains(step, new double[] { 60, 120, 300 });
        Assert.True(step / 1200 * 900 >= 74);

        double zoomed = WaveTimeline.TickSeconds(new WaveView(0, 10), 900);
        Assert.True(zoomed <= 1);
    }
}
