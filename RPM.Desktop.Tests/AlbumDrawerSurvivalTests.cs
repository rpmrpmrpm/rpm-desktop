using RPM.Desktop.Presentation.Main.Library.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// Whether the open drawer on the album wall stays open through an album feed or closes.
///
/// <para>The wall is fed again very often and almost every feed returns the same records, so a drawer
/// that closed on each one would close while the user types a key or a cloud badge changes. It must
/// close in two cases: the record leaves the wall, or the wall becomes a different collection. The
/// drawer band is attached to the ROW, and a row that is about to be refilled would show the old
/// drawer under whichever cover arrives in that column next.</para>
/// </summary>
public class AlbumDrawerSurvivalTests
{
    private static bool Survives(string? open, bool sameLibrary = true, params string[] wall)
        => AlbumDrawerSurvival.Survives(
            open, sameLibrary, f => wall.Contains(f, StringComparer.OrdinalIgnoreCase));

    [Fact]
    public void A_record_still_on_the_wall_keeps_its_drawer_open()
        => Assert.True(Survives("Talking Heads/Remain in Light",
            wall: ["ABBA/Gold", "Talking Heads/Remain in Light"]));

    /// <summary>The search box, the favourites heart, an offload, a delete: each of these can remove a
    /// record from the wall while its drawer is open.</summary>
    [Fact]
    public void A_record_narrowed_off_the_wall_takes_its_drawer_with_it()
        => Assert.False(Survives("Talking Heads/Remain in Light", wall: ["ABBA/Gold"]));

    /// <summary>Going back to your own collection from a friend's shelf: the folder can exist in both,
    /// but it is not the same card or the same record.</summary>
    [Fact]
    public void A_different_collection_shuts_the_drawer_even_where_the_folder_appears_in_both()
        => Assert.False(Survives("Talking Heads/Remain in Light", sameLibrary: false,
            wall: ["Talking Heads/Remain in Light"]));

    [Fact]
    public void Nothing_open_survives_nothing()
        => Assert.False(Survives(null, wall: ["ABBA/Gold"]));
}
