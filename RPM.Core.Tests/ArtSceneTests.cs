using RPM.Core.Art;

namespace RPM.Core.Tests;

public class ArtSceneTests
{
    // A wide strip and a narrow one, built the way the stage builds them (cover square = height - 24 at
    // margin 16 + 14 gap; record 1.6× the height with its centre on the right edge; slot = width - inset).
    private static ArtScene Panel(double width, double height)
    {
        var side = height - 24;
        var clearLeft = 16 + side + 14;
        var recordLeft = width - height * 1.6 / 2;
        var radius = height * 1.6 / 2;
        var half = height / 2;
        var inset = radius > half ? Math.Sqrt(radius * radius - half * half) : 0;
        return ArtScene.FromPanel(width, height, clearLeft, recordLeft, width - inset);
    }

    [Fact]
    public void A_wide_panel_leaves_a_wider_clear_band_than_a_narrow_one()
    {
        // The cover and the record are fixed multiples of the height, so on a narrow panel they take a
        // bigger fraction of the strip, and the clear band shrinks and moves right.
        var wide = Panel(1200, 180);
        var narrow = Panel(520, 200);

        Assert.True(wide.ClearLeftFrac < narrow.ClearLeftFrac, "the cover should be a smaller fraction of a wide panel");
        var wideBand = wide.ClearRightFrac - wide.ClearLeftFrac;
        var narrowBand = narrow.ClearRightFrac - narrow.ClearLeftFrac;
        Assert.True(wideBand > narrowBand, $"clear band wide={wideBand:F2} narrow={narrowBand:F2}");
    }

    [Fact]
    public void The_focus_leans_right_of_the_clear_bands_centre_and_stays_in_range()
    {
        var scene = Panel(1200, 180);
        var centre = (scene.FaceHardLeft + scene.FaceHardRight) / 2;

        Assert.True(scene.FocusAcross >= centre, "the subject should lean towards the record, clear of the text");
        Assert.InRange(scene.FocusAcross, 0.5, 0.75);
    }

    [Fact]
    public void A_face_past_the_clear_band_is_obstructed()
    {
        var scene = Panel(1200, 180);

        Assert.True(scene.Obstructs(scene.FaceHardLeft - 0.01), "just behind the cover");
        Assert.True(scene.Obstructs(scene.FaceHardRight + 0.01), "just behind the record");
        Assert.False(scene.Obstructs((scene.FaceHardLeft + scene.FaceHardRight) / 2), "dead centre is in the clear");
    }

    [Fact]
    public void Face_position_peaks_in_the_sweet_spot_and_falls_to_the_edges()
    {
        var scene = Panel(1200, 180);
        var centre = (scene.FaceIdealLeft + scene.FaceIdealRight) / 2;

        Assert.Equal(1.0, scene.FacePosition(centre), 6);
        Assert.Equal(0.0, scene.FacePosition(scene.FaceHardLeft), 6);
        Assert.Equal(0.0, scene.FacePosition(scene.FaceHardRight), 6);
        Assert.True(scene.FacePosition(centre) > scene.FacePosition(scene.FaceIdealLeft - (centre - scene.FaceIdealLeft)));
    }

    [Fact]
    public void The_safe_zone_is_floored_so_a_face_must_clear_the_text_and_the_narrow_cover()
    {
        // The measured clear band starts at the cover's own edge (~0.23 on a wide strip), but the title
        // text runs well right of it and the cover grows into the strip as it narrows. So the face
        // safe-zone is floored at centre and capped short of the record: a face has to be at least
        // centred to count as clear.
        var wide = Panel(1900, 380);
        Assert.True(wide.ClearLeftFrac < 0.3, "the raw cover edge is well left");
        Assert.Equal(0.50, wide.FaceHardLeft, 6);          // floored, not the 0.23 cover edge
        Assert.True(wide.FaceHardRight <= 0.90 + 1e-9);

        // A face at 0.47 (under the title text on a wide strip) is outside the safe-zone; one at 0.63 is in.
        Assert.True(wide.Obstructs(0.47));
        Assert.False(wide.Obstructs(0.63));
    }

    /// <summary>The phone's landscape stage: an iPhone on its side, the record 1.08× the height with its
    /// centre on the right edge, the words along the foot of the left half.</summary>
    private static ArtScene Stage(double width = 852, double height = 393)
    {
        var clearRight = (width - height * 1.08 / 2) / width;
        var clearBottom = (height - 190) / height;   // the column: sleeve, title, artist, seek, keys
        return ArtScene.ForClearLeft(width, height, clearRight, clearBottom);
    }

    [Fact]
    public void The_stage_keeps_a_face_clear_of_the_record()
    {
        var stage = Stage();

        // The record's leftmost reach, as a fraction of the frame. Everything from here right is vinyl
        // for as long as the record is on screen.
        var recordReach = (852 - 393 * 1.08 / 2) / 852;

        Assert.True(stage.FaceHardRight <= recordReach + 1e-9, "the safe zone must stop at the record");
        Assert.True(stage.Obstructs(recordReach + 0.02), "a face under the record is hidden");
        Assert.True(stage.FocusAcross < recordReach - 0.1, "and the subject is aimed well clear of it");
    }

    [Fact]
    public void The_stage_puts_the_subject_left_of_centre_and_high()
    {
        var stage = Stage();

        // Left, because the record covers the right; high, because the text covers the bottom. The
        // desktop strip's measurement gets both wrong for this stage (see ArtScene.ForClearLeft).
        Assert.InRange(stage.FocusAcross, 0.2, 0.55);
        Assert.InRange(stage.FocusDown, 0.15, 0.35);
        Assert.False(stage.Obstructs(stage.FocusAcross), "the place it aims for cannot be a hidden one");
    }

    [Fact]
    public void The_desktop_strips_measurement_would_squeeze_the_stage_against_the_record()
    {
        // Why the stage does not use FromPanel: read the left-hand column as an obstruction the full
        // height of the frame and the safe zone collapses to a slot beside the record, which is the one
        // place a face must not be. This pins the difference rather than the workaround.
        var asStrip = ArtScene.FromPanel(852, 393, clearLeftPx: 568, clearRightPx: 640, slotWidth: 852);
        var asStage = Stage();

        Assert.True(asStrip.FaceHardRight - asStrip.FaceHardLeft < 0.1, "a slot, not a band");
        Assert.True(asStage.FaceHardRight - asStage.FaceHardLeft > 0.5, "the whole clear left instead");
        Assert.True(asStage.FocusAcross < asStrip.FocusAcross - 0.15, "and it aims a long way further from the record");
    }
}
