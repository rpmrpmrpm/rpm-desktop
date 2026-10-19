using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// <see cref="SaliencyCrop.Fit"/>: the framing for a header with a foot, where nothing outside the
/// window is shown at all. The one promise it makes is that the subject is ALL THERE: the window is
/// sized by the content and only widened towards the slot's shape, never cut back to it.
/// </summary>
public class SaliencyFitTests
{
    /// <summary>A saliency map: flat background, with one salient blob painted into it.</summary>
    private static double[] Map(int width, int height, (int X, int Y, int W, int H)? subject = null,
                                double background = 0.02)
    {
        var map = new double[width * height];
        Array.Fill(map, background);

        if (subject is not { } blob)
            return map;

        for (var y = blob.Y; y < blob.Y + blob.H; y++)
            for (var x = blob.X; x < blob.X + blob.W; x++)
                if (x >= 0 && y >= 0 && x < width && y < height)
                    map[y * width + x] = 1.0;

        return map;
    }

    private static bool HoldsRows(CropRect frame, int top, int bottom)
        => frame.Y <= top && frame.Y + frame.Height >= bottom;

    private static bool Holds(CropRect frame, (int X, int Y, int W, int H) box)
        => frame.X <= box.X && frame.X + frame.Width >= box.X + box.W
        && frame.Y <= box.Y && frame.Y + frame.Height >= box.Y + box.H;

    [Fact]
    public void The_whole_subject_stands_in_the_frame()
    {
        var subject = (120, 90, 70, 80);

        var frame = SaliencyCrop.Fit(Map(320, 320, subject), 320, 320, 2.5).Rect;

        Assert.True(Holds(frame, subject),
                    $"the subject was cut: frame {frame}, subject x={subject.Item1} y={subject.Item2}");
    }

    [Fact]
    public void A_wide_slot_does_not_cut_a_slit_through_the_subject()
    {
        // A standing figure and an eight-to-one band. A crop window of the band's own shape would
        // leave only a strip of the figure's face. This framing exists to avoid that.
        var person = (130, 30, 60, 260);

        var frame = SaliencyCrop.Fit(Map(320, 320, person), 320, 320, 8.0).Rect;

        Assert.True(HoldsRows(frame, person.Item2, person.Item2 + person.Item4),
                    $"the figure was banded: frame y={frame.Y}..{frame.Y + frame.Height}");
    }

    [Fact]
    public void A_face_keeps_its_head_and_shoulders()
    {
        var person = (130, 30, 60, 260);
        var face = new FaceBox(140, 40, 40, 45);

        var frame = SaliencyCrop.Fit(Map(320, 320, person), 320, 320, 5.0, [face]).Rect;

        Assert.True(HoldsRows(frame, face.Y, face.Y + face.Height),
                    $"the face was cut: frame y={frame.Y}..{frame.Y + frame.Height}");
        Assert.True(frame.Y <= face.Y - face.Height * 0.4, "the hair above the face was cut");
    }

    [Fact]
    public void The_frame_takes_the_slots_shape_when_the_photo_has_the_room()
    {
        var subject = (200, 180, 60, 60);

        var frame = SaliencyCrop.Fit(Map(800, 600, subject), 800, 600, 2.5).Rect;

        Assert.Equal(2.5, (double)frame.Width / frame.Height, 1);
    }

    [Fact]
    public void A_photo_too_narrow_for_the_slot_gives_all_the_width_it_has()
    {
        // Nothing more to show sideways: the window is the photograph's full width, and the caller's
        // wash covers whatever part of the slot the photograph does not.
        var subject = (100, 100, 100, 120);

        var frame = SaliencyCrop.Fit(Map(300, 400, subject), 300, 400, 6.0).Rect;

        Assert.Equal(300, frame.Width);
        Assert.True(Holds(frame, subject), "the subject was cut to make the shape");
    }

    [Fact]
    public void No_subject_means_the_whole_picture()
    {
        // A texture: saliency spread over everything, nothing to frame. The picture IS the subject,
        // so all of it is shown and not a wide slice of it.
        var frame = SaliencyCrop.Fit(Map(400, 400, (0, 0, 400, 400)), 400, 400, 4.0).Rect;

        Assert.Equal(new CropRect(0, 0, 400, 400), frame);
    }

    [Fact]
    public void The_subject_is_placed_clear_of_the_cover()
    {
        // The strip's left is under the album cover and the title. Given slack across, the subject is
        // put right of the frame's centre.
        var subject = (240, 180, 60, 60);

        var frame = SaliencyCrop.Fit(Map(800, 600, subject), 800, 600, 2.5).Rect;

        var across = (subject.Item1 + subject.Item3 / 2.0 - frame.X) / frame.Width;
        Assert.True(across > 0.5, $"the subject sat at {across:F2} across the frame, left of centre");
    }

    // ── the content box: where the dissolve may not go ──

    [Fact]
    public void The_content_box_holds_every_face_in_a_group()
    {
        // Eight people across a photograph. The box that the caller must not dissolve through covers
        // all of them. The two on the ends are exactly the ones that would otherwise fade out.
        FaceBox[] band =
        [
            new(40, 120, 40, 46), new(150, 110, 38, 44), new(260, 115, 40, 46), new(370, 108, 38, 44),
            new(480, 118, 40, 46), new(590, 112, 38, 44), new(700, 116, 40, 46),
        ];

        var fit = SaliencyCrop.Fit(Map(900, 500, (30, 100, 720, 300)), 900, 500, 5.0, band);

        foreach (var face in band)
            Assert.True(fit.Content.X <= face.X && fit.Content.X + fit.Content.Width >= face.X + face.Width,
                        $"a face at x={face.X} fell outside the content box {fit.Content}");
    }

    [Fact]
    public void The_content_box_never_leaves_the_frame()
    {
        var subject = (10, 10, 60, 60);

        var fit = SaliencyCrop.Fit(Map(320, 320, subject), 320, 320, 2.5);

        Assert.True(fit.Content.X >= fit.Rect.X && fit.Content.Y >= fit.Rect.Y);
        Assert.True(fit.Content.X + fit.Content.Width <= fit.Rect.X + fit.Rect.Width);
        Assert.True(fit.Content.Y + fit.Content.Height <= fit.Rect.Y + fit.Rect.Height);
    }

    // ── the keep box: what a display may not zoom away ──

    [Fact]
    public void The_keep_box_is_faces_only()
    {
        // Head and shoulders is what the frame is BUILT around; the faces alone are what must never be
        // lost. A header seven times wider than it is tall spends the difference on filling itself:
        // the shoulders, the chest and the top of the hair. This is a normal crop in television.
        var person = (130, 30, 60, 260);
        var face = new FaceBox(140, 40, 40, 45);

        var fit = SaliencyCrop.Fit(Map(320, 320, person), 320, 320, 5.0, [face]);

        Assert.True(fit.Keep.Height < fit.Content.Height, "the keep box kept the shoulders");
        Assert.True(fit.Keep.Y + fit.Keep.Height >= face.Y + face.Height, "the chin is not in the keep box");
        Assert.True(fit.Keep.Y > face.Y - (face.Height * 0.3), "the keep box is still holding the hair");
        Assert.True(fit.Keep.Y <= face.Y, "the brow was cut");
    }

    [Fact]
    public void The_eye_line_is_reported_for_a_face()
    {
        // What a display needs once it has zoomed: the eyes go a little above the middle of the
        // frame, so it has to be told where they are.
        var person = (130, 30, 60, 260);
        var face = new FaceBox(140, 40, 40, 45);

        var fit = SaliencyCrop.Fit(Map(320, 320, person), 320, 320, 5.0, [face]);

        Assert.InRange(fit.EyeLine, face.Y, face.Y + face.Height);
    }

    [Fact]
    public void There_is_no_eye_line_without_a_face()
    {
        var fit = SaliencyCrop.Fit(Map(320, 320, (120, 90, 70, 80)), 320, 320, 2.5);

        Assert.Equal(0, fit.EyeLine);
    }

    [Fact]
    public void Every_head_in_a_group_is_in_the_keep_box()
    {
        FaceBox[] band =
        [
            new(40, 120, 40, 46), new(370, 108, 38, 44), new(700, 116, 40, 46),
        ];

        var fit = SaliencyCrop.Fit(Map(900, 500, (30, 100, 720, 300)), 900, 500, 5.0, band);

        foreach (var face in band)
            Assert.True(fit.Keep.X <= face.X && fit.Keep.X + fit.Keep.Width >= face.X + face.Width,
                        $"a head at x={face.X} fell outside the keep box {fit.Keep}");
    }

    [Fact]
    public void With_no_faces_the_subject_itself_is_kept()
    {
        var subject = (120, 90, 70, 80);

        var fit = SaliencyCrop.Fit(Map(320, 320, subject), 320, 320, 2.5);

        Assert.True(Holds(fit.Keep, subject), "the subject is not wholly in the keep box");
        Assert.True(fit.Keep.Height <= fit.Content.Height, "the keep box is bigger than the content box");
    }

    [Fact]
    public void Degenerate_input_is_the_whole_picture()
    {
        Assert.Equal(new CropRect(0, 0, 0, 0), SaliencyCrop.Fit([], 0, 0, 2.0).Rect);
        Assert.Equal(new CropRect(0, 0, 4, 4), SaliencyCrop.Fit(new double[16], 4, 4, 0).Rect);
    }
}
