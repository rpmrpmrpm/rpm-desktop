using System.IO;
using System.Linq;
using System.Threading;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Services.Art;
using SkiaSharp;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The cover a generated playlist wears, composed from the photograph that ships with the app.
///
/// <para><b>The whole path is run here.</b> <c>CoverAsync</c> is the entry point that all the surfaces use.
/// The decode, the tint and the mark are all run from the tests, not only by the app itself. The whole path
/// can be reached from a test.</para>
///
/// <para>These tests check that a shipped photograph comes out as a cover, not as the file that went in.
/// The cover must have the right size, decode correctly, and be tinted. Without the tint, the covers would
/// look like stock photographs with a logo on them.</para>
/// </summary>
public class GeneratedCoverTests
{
    /// <summary>A list that has a photograph, and a colour that is not grey so the tint can be seen. This
    /// is the whole Chill shelf. It is the first row in the table and the least likely to be removed from
    /// it.</summary>
    private static readonly SmartPlaylistId Chill =
        SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);

    private static byte[] Photograph()
    {
        var path = PlaylistPhotos.PathFor(Chill);
        Assert.NotNull(path);
        return File.ReadAllBytes(path!);
    }

    /// <summary>
    /// The whole path the app takes, from a list id to the bytes a surface draws. The result is a square
    /// at cover size that decodes.
    ///
    /// <para>The test goes through <c>CoverAsync</c>, not only the drawing. That is the entry point the
    /// four surfaces use. Everything before the drawing is also checked: which file is used, whether it
    /// exists, and whether it can be read.</para>
    /// </summary>
    [Fact]
    public async Task A_shipped_photograph_composes_into_a_cover()
    {
        var cover = await GeneratedCoverService.CoverAsync(
            Chill, Windows.UI.Color.FromArgb(255, 40, 90, 200), CancellationToken.None);
        Assert.NotNull(cover);

        using var drawn = SKBitmap.Decode(cover);
        Assert.NotNull(drawn);
        Assert.Equal(640, drawn!.Width);
        Assert.Equal(640, drawn.Height);
    }

    /// <summary>A list with no photograph of its own gives null here, and the surface draws a figure
    /// instead. This is the normal answer for most generated lists.</summary>
    [Fact]
    public async Task A_list_with_no_photograph_asks_for_the_drawn_figure()
        => Assert.Null(await GeneratedCoverService.CoverAsync(
            SmartPlaylistId.WholeShelf(SmartPlaylistKind.NeverPlayed),
            Windows.UI.Color.FromArgb(255, 40, 90, 200),
            CancellationToken.None));

    /// <summary>
    /// The mood's colour is actually laid over the picture.
    ///
    /// <para>The tint puts a photograph and a drawn figure on one shelf. It is applied with
    /// <see cref="SKBlendMode.Color"/>, which takes hue and saturation from the paint and lightness from
    /// the picture underneath. So the check is that the hue MOVED towards the colour asked for. It does not
    /// check that the pixels have that colour. If a photograph came out the colour of the paint, the blend
    /// mode would have been changed to one that throws the picture away.</para>
    /// </summary>
    [Fact]
    public void The_mood_colour_reaches_the_picture()
    {
        var photo = Photograph();

        var cover = GeneratedCoverService.Compose(photo, Windows.UI.Color.FromArgb(255, 40, 90, 200));
        Assert.NotNull(cover);

        using var before = SKBitmap.Decode(photo);
        using var after = SKBitmap.Decode(cover);
        Assert.NotNull(before);
        Assert.NotNull(after);

        // Read off the middle, away from the corner the mark is stamped in.
        var was = before!.GetPixel(320, 320);
        var now = after!.GetPixel(320, 320);
        Assert.NotEqual(was, now);

        // Blue was asked for, so the tinted pixel must lean further towards blue than the source pixel. The
        // check is a comparison, not a fixed value, because the content of the photograph does not matter
        // here.
        Assert.True(now.Blue - now.Red > was.Blue - was.Red,
            $"the tint did not reach the picture: {was} became {now}");
    }

    /// <summary>Two lists with different colours get two different covers from one photograph. Each list
    /// has its own tint, and that is the point of the tint.</summary>
    [Fact]
    public void Two_colours_make_two_covers()
    {
        var photo = Photograph();

        var blue = GeneratedCoverService.Compose(photo, Windows.UI.Color.FromArgb(255, 40, 90, 200));
        var red = GeneratedCoverService.Compose(photo, Windows.UI.Color.FromArgb(255, 200, 50, 40));

        Assert.NotNull(blue);
        Assert.NotNull(red);
        Assert.False(blue!.SequenceEqual(red!));
    }

    /// <summary>Bytes that are not a picture give no cover, so the list draws its figure instead of
    /// throwing. This happens when an asset folder is pruned or only partly copied.</summary>
    [Fact]
    public void Bytes_that_are_not_a_picture_compose_to_nothing()
        => Assert.Null(GeneratedCoverService.Compose(
            [1, 2, 3, 4], Windows.UI.Color.FromArgb(255, 40, 90, 200)));
}
