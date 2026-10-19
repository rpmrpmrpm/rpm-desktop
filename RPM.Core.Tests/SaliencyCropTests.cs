using RPM.Core.Art;

namespace RPM.Core.Tests;

public class SaliencyCropTests
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

    private static bool Holds(CropRect frame, FaceBox face)
        => frame.Y <= face.Y && frame.Y + frame.Height >= face.Y + face.Height;

    // ── the faces decide the band ──

    [Fact]
    public void The_band_lands_on_the_face()
    {
        // A standing figure, face near the top. The band is a fifth of the photo's height: it cannot
        // hold the person, and the one thing it must hold is the face.
        var person = (130, 30, 60, 260);
        var face = new FaceBox(140, 40, 40, 45);

        var frame = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0, [face]).Rect;

        Assert.True(Holds(frame, face), $"the face was cut: frame y={frame.Y}..{frame.Y + frame.Height}, face y={face.Y}..{face.Y + face.Height}");
    }

    [Fact]
    public void The_band_takes_in_as_many_faces_as_it_can()
    {
        // Four people at four heights. No band of this shape holds all four, so it takes the three
        // that fit and not the one that happens to be tallest.
        var group = (20, 20, 280, 240);
        FaceBox[] faces =
        [
            new(30, 90, 34, 40),    // low
            new(100, 40, 34, 40),   // high
            new(170, 55, 34, 40),
            new(240, 60, 34, 40),
        ];

        var frame = SaliencyCrop.Choose(Map(320, 320, group), 320, 320, 5.0, faces).Rect;

        var held = faces.Count(f => Holds(frame, f));
        Assert.True(held >= 3, $"only {held} of 4 faces made it into the band");
    }

    [Fact]
    public void A_face_taller_than_the_band_keeps_its_eyes()
    {
        // A close-up: the face does not fit the band, so hair and chin have to go. The eyes may not.
        var person = (100, 10, 120, 300);
        var face = new FaceBox(110, 20, 100, 120);      // 120 tall; the band here is ~64
        var eyes = face.Y + face.Height * 0.42;

        var frame = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0, [face]).Rect;

        Assert.True(eyes >= frame.Y && eyes <= frame.Y + frame.Height,
                    $"the eyes at y={eyes:F0} fell outside the band y={frame.Y}..{frame.Y + frame.Height}");
    }

    [Fact]
    public void The_faces_beat_the_silhouette()
    {
        // The subject's box says "top of the blob"; the face says otherwise, and the face wins.
        // Without a detector, this is the crop that lands on somebody's scalp.
        var person = (120, 0, 80, 300);
        var face = new FaceBox(130, 120, 50, 55);       // face well down the frame (a hat, a hood, a hood ornament)

        var frame = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0, [face]).Rect;

        Assert.True(Holds(frame, face), "the band followed the silhouette instead of the face");
    }

    // ── no faces: the subject decides ──

    [Fact]
    public void With_no_faces_the_band_still_lands_on_the_top_of_the_subject()
    {
        // Nobody detectable: a figure with their back turned, a face behind hair. The band goes to
        // the top of them, not their middle.
        var person = (130, 40, 60, 240);

        var frame = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0).Rect;

        Assert.True(frame.Y < 40 + 240 / 2, $"the band sat at y={frame.Y}, on the body rather than the head");
    }

    [Fact]
    public void The_frame_takes_the_slots_shape()
    {
        var frame = SaliencyCrop.Choose(Map(320, 320, (130, 140, 60, 50)), 320, 320, 5.0).Rect;

        Assert.InRange(frame.Width / (double)frame.Height, 4.75, 5.25);
    }

    [Fact]
    public void A_small_subject_gets_zoomed_in_on()
    {
        var subject = (170, 130, 50, 50);
        var frame = SaliencyCrop.Choose(Map(500, 400, subject), 500, 400, 2.5).Rect;

        Assert.True(frame.Width < 500 * 0.9, $"no zoom: frame is {frame.Width}px of a 500px photo");
    }

    [Fact]
    public void A_textured_photo_with_no_subject_is_not_zoomed_into()
    {
        // A rock face, a landscape, a wall: saliency is spread over all of it and there is nothing to
        // zoom into. A density search would find the busiest patch of stone and zoom into it. This
        // must not happen.
        var frame = SaliencyCrop.Choose(Map(400, 400, (0, 0, 400, 400)), 400, 400, 4.0).Rect;

        Assert.Equal(400, frame.Width);
    }

    [Fact]
    public void A_speck_is_not_a_subject()
    {
        var frame = SaliencyCrop.Choose(Map(400, 400, (200, 200, 4, 4)), 400, 400, 4.0).Rect;

        Assert.Equal(400, frame.Width);
    }

    [Fact]
    public void The_frame_stays_inside_the_photo()
    {
        var frame = SaliencyCrop.Choose(Map(300, 200, (280, 180, 18, 18)), 300, 200, 2.0).Rect;

        Assert.True(frame.X >= 0 && frame.Y >= 0);
        Assert.True(frame.X + frame.Width <= 300);
        Assert.True(frame.Y + frame.Height <= 200);
    }

    [Fact]
    public void The_frame_never_zooms_past_the_floor()
    {
        var frame = SaliencyCrop.Choose(Map(400, 400, (198, 198, 10, 10)), 400, 400, 4.0).Rect;

        Assert.True(frame.Width >= 400 * SaliencyCrop.MinScale - 1,
                    $"zoomed to {frame.Width}px, past the {400 * SaliencyCrop.MinScale:F0}px floor");
    }

    [Fact]
    public void Degenerate_input_is_handled()
    {
        Assert.Equal(new CropRect(0, 0, 0, 0), SaliencyCrop.Choose([], 0, 0, 2.0).Rect);
        Assert.Equal(new CropRect(0, 0, 4, 4), SaliencyCrop.Choose(new double[16], 4, 4, 0).Rect);
    }

    // ── pillarbox: fit the picture, fill the sides ──

    [Fact]
    public void A_portrait_too_tall_for_the_strip_is_pillarboxed_rather_than_beheaded()
    {
        // A close-up in a 5:1 strip. The head cannot fit a frame of that shape, so the frame stops
        // being that shape: the caller fits the picture whole and fills the sides with a blurred
        // copy, as television does for vertical video.
        var person = (100, 10, 120, 300);
        var face = new FaceBox(110, 30, 100, 110);

        var choice = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0, [face]);

        Assert.False(choice.FillsTarget, "a portrait was squeezed into the strip's shape");
        Assert.True(choice.Rect.Width / (double)choice.Rect.Height < 5.0);
    }

    [Fact]
    public void A_pillarboxed_picture_is_never_a_postage_stamp()
    {
        // The fill is a border, not the main part: the picture still has to be most of what is shown.
        var person = (100, 10, 120, 300);
        var face = new FaceBox(110, 30, 100, 110);

        var choice = SaliencyCrop.Choose(Map(320, 320, person), 320, 320, 5.0, [face]);

        Assert.True(choice.Rect.Width / (double)choice.Rect.Height >= 2.7,
                    $"the picture would show at {choice.Rect.Width / (double)choice.Rect.Height:F1}:1 in a 5:1 strip");
    }

    [Fact]
    public void Faces_that_fit_still_fill_the_strip()
    {
        // A group shot far enough back that heads and shoulders fit the strip's own shape: no fill
        // needed, and none used.
        var group = (20, 60, 280, 120);
        FaceBox[] faces = [new(40, 70, 26, 30), new(140, 72, 26, 30), new(240, 70, 26, 30)];

        var choice = SaliencyCrop.Choose(Map(320, 320, group), 320, 320, 5.0, faces);

        Assert.True(choice.FillsTarget);
    }

    // ── scene: the crop places the subject where the panel leaves it in the clear ──

    [Fact]
    public void The_scene_moves_where_the_subject_is_placed()
    {
        // A subject with room to slide across the frame. A scene whose clear band is further right (a
        // narrower panel, where the cover hides more of the left) pulls the crop window left, so the
        // subject lands further right in the frame, clear of the cover.
        var map = Map(500, 300, (210, 120, 60, 60));
        var leansLeft = new ArtScene(0, 0, 0.10, 0.60);
        var leansRight = new ArtScene(0, 0, 0.55, 0.98);

        var left = SaliencyCrop.Choose(map, 500, 300, 2.5, null, leansLeft).Rect;
        var right = SaliencyCrop.Choose(map, 500, 300, 2.5, null, leansRight).Rect;

        Assert.True(right.X < left.X, $"placement did not follow the scene: left.X={left.X}, right.X={right.X}");
    }

    /// <summary>
    /// The phone's landscape stage: a frame twice as wide as it is tall, with the words along the
    /// bottom, so the scene asks for the subject high. It may only be given what the head leaves: a
    /// wide frame comes out at the head's own height, and moving it up any further cuts off the
    /// crown.
    /// </summary>
    [Fact]
    public void A_wide_frame_will_not_ride_the_head_out_of_the_top()
    {
        // A head near the top of a 16:9 photograph, and a stage-shaped scene: clear on the left, the
        // record from 0.75 across, the column along the bottom. FocusDown ends up at about a fifth.
        var face = new FaceBox(400, 90, 150, 170);
        var map = Map(1920, 1080, (380, 70, 190, 400));
        var stage = ArtScene.ForClearLeft(852, 393, clearRightFrac: 0.75, clearBottomFrac: 0.52);

        var frame = SaliencyCrop.Choose(map, 1920, 1080, targetAspect: 2.17, [face], stage).Rect;

        // The crown, as the crop itself reckons it: the face box grown upwards by the hair above it.
        var crown = face.Y - face.Height * 0.45;

        Assert.True(frame.Y <= crown + 1,
            $"the frame cut into the head: top={frame.Y}, crown={crown:0.#}");
        Assert.True(Holds(frame, face), "and it must still hold the face itself");
    }

    /// <summary>The same request in a TALL frame is met in full: there the window is several times the
    /// head's height, so lifting the subject uses spare room and not the crown. This stops the clamp
    /// above from quietly turning every scene's FocusDown into "centred".</summary>
    [Fact]
    public void A_tall_frame_still_lifts_the_subject()
    {
        var face = new FaceBox(800, 400, 150, 170);
        var map = Map(1920, 1080, (780, 380, 190, 420));
        var high = ArtScene.ForClearLeft(393, 852, clearRightFrac: 1, clearBottomFrac: 0.45);
        var centred = new ArtScene(393, 852, 0, 1);

        var lifted = SaliencyCrop.Choose(map, 1920, 1080, targetAspect: 0.46, [face], high).Rect;
        var middle = SaliencyCrop.Choose(map, 1920, 1080, targetAspect: 0.46, [face], centred).Rect;

        Assert.True(lifted.Y > middle.Y,
            $"a tall frame has the room, so the subject should ride up: lifted={lifted.Y}, middle={middle.Y}");
    }

    [Fact]
    public void The_classical_fallback_finds_detail()
    {
        // Not a subject detector, an edge detector. All it has to do is score a shape's edges above
        // flat background, so that a missing model degrades gracefully and does not fail.
        var rgba = new byte[80 * 40 * 4];
        for (var i = 0; i < 80 * 40; i++)
        {
            rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 20;
            rgba[i * 4 + 3] = 255;
        }
        for (var y = 10; y < 30; y++)
            for (var x = 10; x < 30; x++)
            {
                var i = y * 80 + x;
                rgba[i * 4] = rgba[i * 4 + 1] = rgba[i * 4 + 2] = 240;
            }

        var map = SaliencyCrop.ClassicalSaliency(rgba, 80, 40);

        Assert.True(map[20 * 80 + 10] > 0, "the block's edge scored nothing");
        Assert.Equal(0, map[5 * 80 + 60]);
    }
}
