using RPM.Core.Library;
using RPM.Desktop.Presentation.Common;
using Xunit;

namespace RPM.Desktop.Tests;

// Render dispatches into PlaylistArt.Figure and PlaylistArt.Made. Both draw in colours from RpmPalette,
// which needs a stand-in in this test host. See PaletteStandIn for why. See PaletteStandInCollection for
// why this class cannot run alongside another class that also holds one.
[Collection(PaletteStandInCollection.Name)]
public class PlaylistCoverDispatchTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    public void Dispose()
    {
        _palette.Dispose();
        GC.SuppressFinalize(this);
    }

    private static byte[] ASleeve() => PlaylistArt.Figure(0, 1u, CoverMark.None, mood: null).Cover!;

    [Fact]
    public void A_figure_choice_needs_no_sleeves_at_all()
    {
        var drawn = PlaylistCovers.Render(new PlaylistCover.Figure(2, 99u, CoverMark.None), [], "Rainy");
        Assert.NotNull(drawn.Cover);

        // The result is byte for byte the same as the figure drawn with no mood. So a playlist with a
        // chosen figure does NOT wear the app's own mark. If Render passed a mood, it would stamp one, and
        // this test would catch it.
        Assert.Equal(PlaylistArt.Figure(2, 99u, CoverMark.None, mood: null).Cover, drawn.Cover);
    }

    [Fact]
    public void An_automatic_choice_with_no_sleeves_draws_nothing()
        => Assert.Null(PlaylistCovers.Render(new PlaylistCover.Automatic(), [], "Rainy").Cover);

    [Fact]
    public void An_automatic_choice_with_sleeves_draws_the_quilt()
        => Assert.NotNull(PlaylistCovers.Render(new PlaylistCover.Automatic(), [ASleeve()], "Rainy").Cover);

    // A record the user chose can be retagged out of the list, or its folder can go. Falling back to
    // the quilt is right and falling back to nothing is not: they asked for a picture, and the list
    // still has records on it.
    [Fact]
    public void A_records_choice_whose_records_have_gone_falls_back_to_the_quilt()
    {
        var drawn = PlaylistCovers.Render(new PlaylistCover.Records(["/gone"]), [ASleeve()], "Rainy");
        Assert.NotNull(drawn.Cover);
        Assert.Equal(PlaylistCovers.Render(new PlaylistCover.Automatic(), [ASleeve()], "Rainy").Cover,
                     drawn.Cover);
    }
}
