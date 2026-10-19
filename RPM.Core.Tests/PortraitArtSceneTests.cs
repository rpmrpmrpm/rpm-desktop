using RPM.Core.Art;

namespace RPM.Core.Tests;

/// <summary>
/// The phone's now-playing screen: the same art geometry turned to portrait. The record covers the
/// right side, and the cover, title and transport cover the bottom. The subject belongs in the
/// clear upper-left, the opposite corner from the desktop strip, and both come out of the same
/// arithmetic.
/// </summary>
public class PortraitArtSceneTests
{
    /// <summary>A 6.1-inch phone laid out as the now-playing screen does it: the record is 1.05 times
    /// the screen width and placed half off the right edge, so it reaches in to just under half the
    /// width. The cover tile, the two lines of text and the transport take the bottom third and a
    /// little more.</summary>
    private static ArtScene Phone() => ArtScene.ForPortrait(390, 760, clearRightFrac: 0.475, clearBottomFrac: 0.62);

    [Fact]
    public void The_subject_goes_left_on_a_phone_and_right_on_the_strip()
    {
        // The one thing that must not be shared is which side is safe. The strip's focus is clamped
        // to the right of centre because its cover and title are on the left. The phone's record is
        // on the right, so the same derivation must be able to land on the other half of the frame.
        Assert.True(Phone().FocusAcross < 0.45, $"phone focus {Phone().FocusAcross:F2} should be left of centre");
        Assert.True(ArtScene.Default.FocusAcross > 0.55, $"strip focus {ArtScene.Default.FocusAcross:F2} should be right of centre");
    }

    [Fact]
    public void The_subject_goes_high_on_a_phone_and_centred_on_the_strip()
    {
        // The strip is barely taller than a face and nothing covers it vertically, so the exact
        // centre is right there. A tall frame with controls along the bottom is different.
        Assert.True(Phone().FocusDown < 0.35, $"phone focus down {Phone().FocusDown:F2} should be high in the frame");
        Assert.Equal(0.5, ArtScene.Default.FocusDown);
    }

    [Fact]
    public void A_face_behind_the_record_is_obstructed_and_one_on_the_left_is_not()
    {
        var scene = Phone();

        Assert.True(scene.Obstructs(0.80), "a face at 0.80 across is behind the record");
        Assert.False(scene.Obstructs(0.28), "a face at 0.28 across is in the clear");

        // And the strip rules out exactly the opposite one. This is why the scene is a value and not
        // a set of constants.
        Assert.True(ArtScene.Default.Obstructs(0.28));
        Assert.False(ArtScene.Default.Obstructs(0.80));
    }

    [Fact]
    public void The_clear_zone_follows_the_record_when_it_moves()
    {
        // A bigger record reaches further in, so the safe zone and the focus both have to follow it.
        // They must not stay fixed to numbers tuned for one phone.
        var small = ArtScene.ForPortrait(390, 760, 0.60, 0.62);
        var large = ArtScene.ForPortrait(390, 760, 0.40, 0.62);

        Assert.True(large.FaceHardRight < small.FaceHardRight);
        Assert.True(large.FocusAcross < small.FocusAcross);
    }

    [Fact]
    public void Taller_chrome_pushes_the_subject_higher()
    {
        var shallow = ArtScene.ForPortrait(390, 760, 0.475, 0.80);
        var deep = ArtScene.ForPortrait(390, 760, 0.475, 0.45);

        Assert.True(deep.FocusDown < shallow.FocusDown);
    }

    // ── the flip between Vision's coordinates and the crop's ──

    [Fact]
    public void A_face_high_in_visions_frame_is_a_face_high_in_the_crops()
    {
        // Vision measures from the BOTTOM. A face in the top quarter of the photo therefore arrives
        // with a large y and must come out with a small one. If this is the wrong way round, the
        // crop is wrong with no sign of it. That is why it is a Core function with a test.
        var face = FaceBox.FromNormalizedBottomLeft(0.4, 0.75, 0.1, 0.15, 1000, 1000);

        Assert.Equal(400, face.X);
        Assert.Equal(100, face.Y);      // 1 - 0.75 - 0.15 = 0.10 down from the top
        Assert.Equal(100, face.Width);
        Assert.Equal(150, face.Height);
    }

    [Fact]
    public void A_face_at_the_foot_of_the_photo_lands_at_the_foot()
    {
        var face = FaceBox.FromNormalizedBottomLeft(0.1, 0.0, 0.2, 0.2, 500, 400);

        Assert.Equal(320, face.Y);      // flush with the bottom: 400 - 80
        Assert.Equal(80, face.Height);
    }

    // ── and the crop actually puts the face there ──

    /// <summary>A saliency map with one salient blob, as SaliencyCropTests builds them.</summary>
    private static double[] Map(int width, int height, (int X, int Y, int W, int H) subject)
    {
        var map = new double[width * height];
        Array.Fill(map, 0.02);

        for (var y = subject.Y; y < subject.Y + subject.H; y++)
            for (var x = subject.X; x < subject.X + subject.W; x++)
                if (x >= 0 && y >= 0 && x < width && y < height)
                    map[y * width + x] = 1.0;

        return map;
    }

    [Fact]
    public void The_crop_puts_the_face_in_the_upper_left_of_a_portrait_frame()
    {
        // A press photo: 16:9, one person right of centre with the face high. Cropped for the
        // phone's portrait screen, the face has to come out upper-LEFT. So the crop window is placed
        // to the RIGHT of the face and BELOW it.
        var scene = Phone();
        var aspect = 390.0 / 760.0;

        var person = (1000, 120, 300, 800);
        var face = new FaceBox(1080, 160, 140, 170);

        var frame = SaliencyCrop.Choose(Map(1920, 1080, person), 1920, 1080, aspect, [face], scene).Rect;

        var faceAcross = (face.X + face.Width / 2.0 - frame.X) / frame.Width;
        var faceDown = (face.Y + face.Height / 2.0 - frame.Y) / frame.Height;

        Assert.InRange(faceAcross, 0.10, 0.45);
        Assert.InRange(faceDown, 0.10, 0.42);
    }

    [Fact]
    public void The_same_photo_on_the_strip_still_comes_out_right_of_centre()
    {
        // The regression that matters: the desktop crop must not change.
        var person = (1000, 120, 300, 800);
        var face = new FaceBox(1080, 160, 140, 170);

        var frame = SaliencyCrop.Choose(Map(1920, 1080, person), 1920, 1080, 5.0, [face], ArtScene.Default).Rect;

        var faceAcross = (face.X + face.Width / 2.0 - frame.X) / frame.Width;
        Assert.InRange(faceAcross, 0.50, 0.80);
    }

    [Fact]
    public void A_faceless_photo_is_still_framed_high_on_a_phone()
    {
        // With no face, the crop falls back to where a head sits on a body. The vertical shift has
        // to apply there too. Otherwise a photo where the model finds no face gets a centred crop.
        var scene = Phone();
        var aspect = 390.0 / 760.0;
        var subject = (700, 200, 500, 700);

        var phone = SaliencyCrop.Choose(Map(1920, 1080, subject), 1920, 1080, aspect, null, scene).Rect;
        var centred = SaliencyCrop.Choose(Map(1920, 1080, subject), 1920, 1080, aspect, null,
            ArtScene.ForPortrait(390, 760, 0.475, 0.62) with { FocusDown = 0.5 }).Rect;

        Assert.True(phone.Y > centred.Y, $"the phone frame should sit lower in the photo (so the subject rides higher): {phone.Y} vs {centred.Y}");
    }
}
