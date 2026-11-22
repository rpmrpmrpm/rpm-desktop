using System.Numerics;
using RPM.Core.Library;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using SkiaSharp;

namespace RPM.Desktop.Tests;

/// <summary>
/// That a room in the library is lit with <see cref="RoomLight.Shelf"/>, and that a room in Pegasus keeps
/// <see cref="RoomLight.Stage"/>.
///
/// <para>The two lights are both valid palettes, so a caller that passes the wrong one compiles and draws.
/// The mistake is only a card that is too bright, or a Pegasus room that is too dull. These tests check the
/// two paths that choose a light inside <see cref="CoverRoom"/> rather than at the caller.</para>
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class CoverRoomTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    public void Dispose()
    {
        _palette.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>A generated list's room is its cover, and its cover is drawn for the library. So it is lit
    /// for the library, whichever surface asks.</summary>
    [Theory]
    [InlineData(SmartPlaylistKind.RightNow)]
    [InlineData(SmartPlaylistKind.FavoriteTracks)]
    [InlineData(SmartPlaylistKind.OnRepeat)]
    public void A_generated_list_s_room_is_lit_for_the_library(SmartPlaylistKind kind)
    {
        var id = SmartPlaylistId.WholeShelf(kind);
        var ring = RpmPalette.Colours.Select(c => new SKColor(c.R, c.G, c.B)).ToList();
        var ink = PlaylistFigure.Colourway.From(ring, SmartPlaylistFace.Cover(id).Seed);
        Assert.NotNull(ink);

        var expected = RoomPalette.From(Vec(ink.Value.Mid), Vec(ink.Value.Hot), Vec(ink.Value.Deep), RoomLight.Shelf);

        Assert.Equal(expected, CoverRoom.Of(id).Colours);
    }

    /// <summary>A cover that is missing gives the empty room of the light that was asked for: the app's blue
    /// at that strength.</summary>
    [Fact]
    public void A_missing_cover_gives_the_empty_room_of_the_light_asked_for()
    {
        Assert.Equal(RoomLight.Shelf.Empty, CoverRoom.Of(null, RoomLight.Shelf));
        Assert.Equal(RoomLight.Stage.Empty, CoverRoom.Of(null, RoomLight.Stage));
        Assert.Equal(RoomPalette.Fallback, CoverRoom.Of([], RoomLight.Stage));
    }

    /// <summary>A picture that cannot be decoded is treated as a missing one, in the same light.</summary>
    [Fact]
    public void A_cover_that_does_not_decode_gives_the_empty_room_of_the_light_asked_for()
    {
        byte[] noise = [1, 2, 3, 4, 5, 6, 7, 8];

        Assert.Equal(RoomLight.Shelf.Empty, CoverRoom.Of(noise, RoomLight.Shelf));
        Assert.Equal(RoomLight.Stage.Empty, CoverRoom.Of(noise, RoomLight.Stage));
    }

    private static Vector3 Vec(SKColor c) => new(c.Red / 255f, c.Green / 255f, c.Blue / 255f);
}
