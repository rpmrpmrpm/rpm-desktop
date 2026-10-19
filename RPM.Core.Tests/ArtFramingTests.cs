using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// The hand-made framing: a window into one photograph, cut to the header's shape, said in the
/// photograph's own units so it survives every size the header is laid out at.
/// </summary>
public class ArtFramingTests
{
    /// <summary>A header five times wider than it is tall: close to the real strip's room beside the
    /// record on an ordinary window.</summary>
    private const double Wide = 5.0;

    // ── the window is the header's shape, inside the photograph, always ──

    [Fact]
    public void A_covering_window_takes_the_headers_shape()
    {
        // 8000 wide so the photograph is not what runs out first.
        var window = new ArtFraming(0.5, 0.5, 0.5, 1).Window(8000, 1000, Wide);

        Assert.Equal(Wide, window.Width / window.Height, 3);
    }

    [Fact]
    public void A_window_that_covers_less_is_narrower_by_exactly_that_much()
    {
        var full = new ArtFraming(0.5, 0.5, 0.15, 1.0).Window(8000, 1000, Wide);
        var part = new ArtFraming(0.5, 0.5, 0.15, 0.6).Window(8000, 1000, Wide);

        Assert.Equal(full.Height, part.Height, 3);          // the same zoom…
        Assert.Equal(full.Width * 0.6, part.Width, 3);      // …and six tenths of the reach
    }

    [Fact]
    public void Window_never_leaves_the_photograph()
    {
        // Asked for a window hanging off every edge at once, at four zooms.
        foreach (var height in new[] { 0.1, 0.3, 0.7, 1.0 })
            foreach (var (cx, cy) in new[] { (-5.0, -5.0), (5.0, 5.0), (-5.0, 5.0), (0.5, -5.0) })
            {
                var window = new ArtFraming(cx, cy, height, 1).Window(1600, 1200, Wide);

                Assert.True(window.X >= 0);
                Assert.True(window.Y >= 0);
                Assert.True(window.X + window.Width <= 1600.001);
                Assert.True(window.Y + window.Height <= 1200.001);
            }
    }

    [Fact]
    public void A_photograph_that_runs_out_of_width_stops_covering_rather_than_being_zoomed_in()
    {
        // 1000 × 1000 asked to cover a 5:1 room at its full height: the window would have to be 5000
        // across and the picture has 1000. The ZOOM is kept (the whole height is shown) and the
        // cover falls away instead, which is what leaves the drawn picture short of the record.
        var framing = ArtFraming.Whole;
        var window = framing.Window(1000, 1000, Wide);

        Assert.Equal(1000, window.Height, 3);   // the whole picture, as asked
        Assert.Equal(1000, window.Width, 3);    // …and all the width there is
        Assert.Equal(0.2, framing.Settle(1000, 1000, Wide).Cover, 3);
    }

    [Fact]
    public void MaxCoverFor_says_how_much_is_reachable_at_this_zoom()
    {
        Assert.Equal(0.2, ArtFraming.Whole.MaxCoverFor(1000, 1000, Wide), 3);
        Assert.Equal(1.0, new ArtFraming(0.5, 0.5, 0.1, 1).MaxCoverFor(1000, 1000, Wide), 3);
    }

    [Fact]
    public void Window_is_not_zoomed_past_what_the_file_can_give()
    {
        // A small picture: the twentieth-of-the-height floor would draw 30px across, which is a blur.
        var window = new ArtFraming(0.5, 0.5, 0.01, 1).Window(600, 400, Wide);

        Assert.True(window.Width >= ArtFraming.MinSourcePixels - 0.001);
    }

    [Fact]
    public void Window_of_a_degenerate_source_is_the_whole_of_it()
    {
        var window = ArtFraming.Whole.Window(0, 0, Wide);

        Assert.Equal(1, window.Width, 3);
        Assert.Equal(1, window.Height, 3);
    }

    // ── reading a placement back as a framing, which is how the dialog opens on what is on screen ──

    [Theory]
    [InlineData(300, 120, 1000, 200)]   // exactly the header's shape
    [InlineData(300, 120, 700, 200)]    // narrower than it: what the chooser produces around a face
    [InlineData(0, 0, 1400, 200)]       // wider than the room can draw, so it comes back capped
    public void From_and_Window_round_trip(double x, double y, double w, double h)
    {
        var was = new FrameWindow(x, y, Math.Min(w, h * Wide), h);

        var framing = ArtFraming.From(was.X, was.Y, was.Width, was.Height, 2000, 1000, Wide);
        var now = framing.Window(2000, 1000, Wide);

        Assert.Equal(was.X, now.X, 3);
        Assert.Equal(was.Y, now.Y, 3);
        Assert.Equal(was.Width, now.Width, 3);
        Assert.Equal(was.Height, now.Height, 3);
    }

    [Fact]
    public void From_an_empty_window_is_the_whole_picture()
    {
        Assert.Equal(ArtFraming.Whole, ArtFraming.From(new CropRect(0, 0, 0, 0), 800, 600, Wide));
        Assert.Equal(ArtFraming.Whole, ArtFraming.From(new CropRect(0, 0, 10, 10), 0, 0, Wide));
    }

    // ── the same framing at another size: the point of storing it in the picture's units ──

    [Fact]
    public void The_same_framing_holds_its_subject_when_the_header_widens()
    {
        // Wide enough that neither header shape runs out of photograph. This is about the window
        // following the room, not about a small picture hitting its own edges.
        var framing = new ArtFraming(0.5, 0.4, 0.5, 1);

        var narrow = framing.Window(8000, 1000, 4.0);
        var wide = framing.Window(8000, 1000, 8.0);

        // The zoom is stated down the picture, so the height is untouched and the width follows the room:
        // a wider header shows MORE of the photograph sideways rather than a closer crop.
        Assert.Equal(narrow.Height, wide.Height, 3);
        Assert.True(wide.Width > narrow.Width);

        // …and it is still centred on the same point down the picture.
        Assert.Equal(narrow.Y + (narrow.Height / 2), wide.Y + (wide.Height / 2), 3);
    }

    // ── settling: what the dialog's readings have to show ──

    [Fact]
    public void Settle_brings_a_framing_dragged_off_the_edge_back()
    {
        var settled = new ArtFraming(2.0, -1.0, 0.5, 1).Settle(2000, 1000, Wide);

        Assert.InRange(settled.CentreX, 0, 1);
        Assert.InRange(settled.CentreY, 0, 1);
        Assert.True(settled.Matches(settled.Settle(2000, 1000, Wide)));   // and stays there
    }

    [Fact]
    public void Settle_reports_the_cover_the_photograph_could_actually_reach()
    {
        // Asked to cover the header at the whole height of a picture too narrow to do it.
        var settled = ArtFraming.Whole.Settle(1000, 1000, Wide);

        Assert.Equal(1.0, settled.Height, 3);   // the zoom stands
        Assert.Equal(0.2, settled.Cover, 3);    // the reach is what gives way
    }

    // ── moving ──

    [Fact]
    public void Moved_shifts_the_window_by_the_fraction_asked_for()
    {
        var window = new ArtFraming(0.5, 0.5, 0.4, 0.5).Moved(0.1, 0).Window(6000, 1000, Wide);

        Assert.Equal((0.6 * 6000) - (window.Width / 2), window.X, 3);
    }

    [Fact]
    public void Moved_keeps_its_place_past_the_edge_so_a_drag_can_come_back()
    {
        // Unclamped ON PURPOSE: clamp here and a drag that ran off the left would have thrown away how
        // far past the edge it went, so dragging back would start moving immediately instead of after
        // the same distance, and the picture would follow the pointer in one direction only.
        var out_ = new ArtFraming(0.5, 0.5, 0.4, 1).Moved(-3, 0);
        Assert.Equal(-2.5, out_.CentreX, 6);

        var back = out_.Moved(3, 0);
        Assert.Equal(0.5, back.CentreX, 6);
    }

    // ── zooming about a held point ──

    [Fact]
    public void Zoomed_about_the_middle_keeps_the_middle()
    {
        var was = new ArtFraming(0.4, 0.6, 0.8, 1);
        var now = was.Zoomed(0.5, 0.5, 0.5, 2000, 1000, Wide);

        var wasWindow = was.Window(2000, 1000, Wide);
        var nowWindow = now.Window(2000, 1000, Wide);

        Assert.Equal(wasWindow.X + (wasWindow.Width / 2), nowWindow.X + (nowWindow.Width / 2), 3);
        Assert.Equal(wasWindow.Y + (wasWindow.Height / 2), nowWindow.Y + (nowWindow.Height / 2), 3);
        Assert.Equal(wasWindow.Height / 2, nowWindow.Height, 3);
    }

    [Fact]
    public void Zoomed_holds_the_pixel_under_the_pointer()
    {
        var was = new ArtFraming(0.5, 0.5, 0.8, 1);
        var wasWindow = was.Window(3000, 2000, Wide);

        // A quarter across and three-quarters down the window: the pixel the wheel is over.
        var heldX = wasWindow.X + (wasWindow.Width * 0.25);
        var heldY = wasWindow.Y + (wasWindow.Height * 0.75);

        var nowWindow = was.Zoomed(0.6, 0.25, 0.75, 3000, 2000, Wide).Window(3000, 2000, Wide);

        Assert.Equal(heldX, nowWindow.X + (nowWindow.Width * 0.25), 2);
        Assert.Equal(heldY, nowWindow.Y + (nowWindow.Height * 0.75), 2);
    }

    [Fact]
    public void Zoomed_out_past_the_whole_picture_stops_at_it()
    {
        var now = new ArtFraming(0.5, 0.5, 0.9, 1).Zoomed(100, 0.5, 0.5, 2000, 1000, Wide);

        Assert.Equal(1.0, now.Height, 6);
    }

    [Fact]
    public void Zooming_leaves_how_far_the_picture_reaches_alone()
    {
        var was = new ArtFraming(0.5, 0.5, 0.12, 0.7);
        var now = was.Zoomed(0.5, 0.5, 0.5, 8000, 1000, Wide);

        Assert.Equal(0.7, now.Cover, 6);
    }

    [Fact]
    public void Zoomed_by_nothing_changes_nothing()
    {
        var was = new ArtFraming(0.3, 0.7, 0.5, 1);

        Assert.Equal(was, was.Zoomed(0, 0.5, 0.5, 2000, 1000, Wide));
        Assert.Equal(was, was.Zoomed(double.NaN, 0.5, 0.5, 2000, 1000, Wide));
        Assert.Equal(was, was.Zoomed(2, 0.5, 0.5, 0, 0, Wide));
    }

    // ── the stamp, which is what keeps a re-framed picture out of the baked-artwork cache ──

    [Fact]
    public void Stamp_separates_framings_that_draw_differently()
    {
        Assert.NotEqual(new ArtFraming(0.5, 0.5, 0.5, 1).Stamp(), new ArtFraming(0.6, 0.5, 0.5, 1).Stamp());
        Assert.NotEqual(new ArtFraming(0.5, 0.5, 0.5, 1).Stamp(), new ArtFraming(0.5, 0.5, 0.4, 1).Stamp());
        Assert.NotEqual(new ArtFraming(0.5, 0.5, 0.5, 1).Stamp(), new ArtFraming(0.5, 0.5, 0.5, 0.8).Stamp());
        Assert.Equal(new ArtFraming(0.5, 0.5, 0.5, 1).Stamp(), new ArtFraming(0.50001, 0.5, 0.5, 1).Stamp());
    }

    [Fact]
    public void Matches_is_the_no_op_test_an_apply_is_skipped_on()
    {
        var was = new ArtFraming(0.5, 0.5, 0.5, 1);

        Assert.True(was.Matches(new ArtFraming(0.5001, 0.4999, 0.5, 1)));
        Assert.False(was.Matches(new ArtFraming(0.52, 0.5, 0.5, 1)));
        Assert.False(was.Matches(new ArtFraming(0.5, 0.5, 0.5, 0.9)));
    }

    // ── the limits a slider's ends have to be ──

    [Fact]
    public void The_reachable_range_across_is_the_window_standing_against_each_edge()
    {
        // A window a third of the picture wide can put its middle anywhere from a sixth in to five
        // sixths across, and nowhere else. Those two are what the ends of a slider must mean.
        var framing = new ArtFraming(0.5, 0.5, 0.1, 0.6);
        var window = framing.Window(6000, 1000, Wide);
        var half = window.Width / (2.0 * 6000);

        var (min, max) = framing.AcrossRange(6000, 1000, Wide);

        Assert.Equal(half, min, 6);
        Assert.Equal(1 - half, max, 6);

        // …and the ends really are reachable: asked for exactly them, the window lands on the edges.
        Assert.Equal(0, (framing with { CentreX = min }).Window(6000, 1000, Wide).X, 3);
        Assert.Equal(6000 - window.Width, (framing with { CentreX = max }).Window(6000, 1000, Wide).X, 3);
    }

    [Fact]
    public void A_window_as_wide_as_the_picture_has_no_range_to_offer()
    {
        var (min, max) = ArtFraming.Whole.AcrossRange(1000, 1000, Wide);

        Assert.Equal(min, max, 9);   // collapsed: the slider is disabled rather than moving nothing
    }

    [Fact]
    public void The_reachable_range_down_is_the_same_rule_stood_on_end()
    {
        var framing = new ArtFraming(0.5, 0.5, 0.25, 1);
        var (min, max) = framing.DownRange(8000, 1000, Wide);

        Assert.Equal(0.125, min, 6);
        Assert.Equal(0.875, max, 6);
    }

    [Fact]
    public void The_widest_zoom_is_where_the_picture_stops_carrying_the_header()
    {
        // Out past here the photograph, taken as wide as it goes, covers less than MinCover of the
        // header: a tiny picture beside the record. A zoom slider that went on to the whole picture would
        // spend its last stretch of travel making the artwork disappear.
        var widest = ArtFraming.WidestZoomFor(1200, 1080, Wide);

        var atLimit = new ArtFraming(0.5, 0.5, widest, 1).Settle(1200, 1080, Wide);
        Assert.Equal(ArtFraming.MinCover, atLimit.Cover, 3);

        // …and it never goes past the whole picture, whatever shape the photograph. A wide one runs out
        // of picture before it runs out of cover, and stops there instead.
        Assert.Equal(1.0, ArtFraming.WidestZoomFor(4000, 400, Wide), 6);
        Assert.Equal(1.0, ArtFraming.WidestZoomFor(1920, 1080, Wide), 6);
    }

    [Fact]
    public void The_widest_zoom_is_never_below_the_closest()
    {
        // A tiny picture can hit both bounds at once; the range has to stay ordered rather than invert.
        foreach (var (w, h) in new[] { (200, 900), (100, 100), (4000, 300) })
            Assert.True(ArtFraming.WidestZoomFor(w, h, Wide) >= ArtFraming.MinHeightFor(w, h, Wide));
    }
}
