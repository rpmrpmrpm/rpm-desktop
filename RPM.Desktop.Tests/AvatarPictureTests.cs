using RPM.Desktop.Presentation.Controls;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where a user's picture is placed in its round frame.
///
/// <para>The frame is covered completely, so a picture that is not square always loses some part. With
/// UniformToFill the loss is always on the right and the bottom, because it draws from the top-left
/// corner, so a face in the middle of a wide photograph ends up against the side of the circle. These
/// tests check that the loss is shared: half from each edge, whichever way the picture is not square,
/// and nothing lost at all when it is square.</para>
/// </summary>
public class AvatarPictureTests
{
    private const double Box = 72;

    [Fact]
    public void A_wide_picture_loses_the_same_from_both_sides()
    {
        var drawn = AvatarPicture.Cover(pictureWidth: 400, pictureHeight: 200, Box, Box);

        // Scaled by its short edge (the height), so the frame is covered.
        Assert.Equal(Box, drawn.Height, 3);
        Assert.Equal(144, drawn.Width, 3);

        // ...and the overhang split: as much cut from the left as from the right.
        Assert.Equal(-36, drawn.X, 3);
        Assert.Equal(0, drawn.Y, 3);
        Assert.Equal(-drawn.X, drawn.X + drawn.Width - Box, 3);   // off the left, off the right
    }

    [Fact]
    public void And_a_tall_one_the_same_from_top_and_foot()
    {
        var drawn = AvatarPicture.Cover(pictureWidth: 200, pictureHeight: 400, Box, Box);

        Assert.Equal(Box, drawn.Width, 3);
        Assert.Equal(144, drawn.Height, 3);
        Assert.Equal(0, drawn.X, 3);
        Assert.Equal(-36, drawn.Y, 3);
    }

    [Fact]
    public void A_square_picture_is_not_moved_at_all()
    {
        var drawn = AvatarPicture.Cover(pictureWidth: 512, pictureHeight: 512, Box, Box);

        Assert.Equal(0, drawn.X, 3);
        Assert.Equal(0, drawn.Y, 3);
        Assert.Equal(Box, drawn.Width, 3);
        Assert.Equal(Box, drawn.Height, 3);
    }

    [Fact]
    public void The_frame_is_always_covered_whatever_the_shape()
    {
        // A frame with a gap is worse than an off-centre crop: the card's fill shows through the circle.
        // Both edges reach the frame, and the picture is never scaled smaller than it.
        foreach (var (w, h) in new[] { (1920, 1080), (300, 1200), (73, 71), (1, 4000) })
        {
            var drawn = AvatarPicture.Cover(w, h, Box, Box);

            Assert.True(drawn.Width >= Box - 0.001, $"{w}x{h} left a gap across");
            Assert.True(drawn.Height >= Box - 0.001, $"{w}x{h} left a gap down");
            Assert.True(drawn.X <= 0.001 && drawn.Y <= 0.001, $"{w}x{h} was hung inside the frame");
        }
    }
    /// <summary>
    /// Which addresses the frame pulls the bytes for itself.
    ///
    /// <para>The crop above does nothing unless it knows the picture's size, and under Uno a bitmap
    /// only knows that when it was set from a stream: <c>new BitmapImage(url)</c>, which fetches its
    /// own address, leaves the size at zero. So every address that can be fetched here is fetched
    /// here, and the others are passed to the bitmap directly.</para>
    /// </summary>
    [Theory]
    [InlineData("https://api.rpmrpmrpm.com/avatar/abc.jpg", true)]
    [InlineData("http://192.168.1.9:5000/avatar/abc.jpg", true)]
    [InlineData("file:///Users/me/Pictures/face.png", true)]
    [InlineData("ms-appx:///Assets/nobody.png", false)]
    [InlineData("ms-resource:///Files/Assets/nobody.png", false)]
    public void An_address_is_fetched_here_unless_it_is_the_frameworks_own(string address, bool fetched)
    {
        Assert.Equal(fetched, AvatarPicture.Fetchable(new Uri(address)));
    }
}
