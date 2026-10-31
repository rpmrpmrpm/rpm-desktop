using RPM.Core.Library;
using RPM.Core.Metadata;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Xunit;

namespace RPM.Desktop.Tests;

/// <summary>
/// The picture a generated list wears, and the colour it wears it in.
///
/// <para>These check the CHOICE rather than the drawing: which family, and which hue from the app's ring,
/// each list gets. "Calm" must be blue and "Energetic" must be red, and neither may drift there by
/// accident. Covers are not hashed from the list's name, because a hash gives no sense of colour.</para>
///
/// <para>The hue is an index into <see cref="RpmPalette"/>. RpmPalette reads its asset through a resource
/// dictionary that this test host has no dispatcher for. So the ten colours are stood in for the length
/// of each test. The stand-in is one process-global static, so this class belongs in the collection with
/// every other class that holds one. See <see cref="PaletteStandInCollection"/>, where the failure it
/// prevents is written out. A class that set and cleared the static on its own, outside the collection,
/// could clear it in the middle of another class's draw.</para>
/// </summary>
[Collection(PaletteStandInCollection.Name)]
public class SmartPlaylistFaceTests : IDisposable
{
    private readonly PaletteStandIn _palette = new();

    public void Dispose()
    {
        _palette.Dispose();
        GC.SuppressFinalize(this);
    }

    /// <summary>The colourway anchors on <c>seed % palette.Count</c>. This is the colour the cover is
    /// actually drawn in: the same arithmetic, read back off the palette the stand-in installed. A second
    /// copy of the ring could drift from it.</summary>
    private static int HueOf(SmartPlaylistId id) =>
        (int)(SmartPlaylistFace.Cover(id).Seed % (uint)RpmPalette.Colours.Count);

    /// <summary>Every kind has a picture. The switch throws rather than defaulting, so a kind added
    /// tomorrow fails here rather than drawing the favourites' cover under somebody else's name.</summary>
    [Fact]
    public void Every_generated_list_has_a_picture()
    {
        foreach (var kind in SmartPlaylists.All)
        {
            var (style, _) = SmartPlaylistFace.Cover(SmartPlaylistId.WholeShelf(kind));
            Assert.InRange(style, 0, PlaylistArt.Styles - 1);
        }
    }

    /// <summary>Blue slows a reader down and red speeds them up. This is the whole point of choosing
    /// the colour rather than hashing it.</summary>
    [Fact]
    public void Calm_is_blue_and_energetic_is_red()
    {
        Assert.Equal(5, HueOf(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm)));        // Azure
        Assert.Equal(9, HueOf(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Energetic)));   // Red
    }

    /// <summary>Sleep and Late night are both the night colour, which is the one place two lists share
    /// a hue on purpose: they are the same time of day asked about two ways.</summary>
    [Fact]
    public void The_night_lists_wear_the_night_colour()
    {
        Assert.Equal(6, HueOf(SmartPlaylistId.WholeShelf(SmartPlaylistKind.Sleep)));       // Indigo
        Assert.Equal(6, HueOf(SmartPlaylistId.WholeShelf(SmartPlaylistKind.LateNight)));   // Indigo
    }

    /// <summary>
    /// A genre row is a VARIATION of its parent: the same family and the same colour, arranged
    /// differently. The tree says the two belong together by nesting them and the picture has to agree.
    /// </summary>
    [Fact]
    public void A_genre_row_is_a_variation_of_its_parent()
    {
        var parent = SmartPlaylistId.WholeShelf(SmartPlaylistKind.Calm);
        var rock = SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock");

        Assert.Equal(SmartPlaylistFace.Cover(parent).Style, SmartPlaylistFace.Cover(rock).Style);
        Assert.Equal(HueOf(parent), HueOf(rock));
        Assert.NotEqual(SmartPlaylistFace.Cover(parent).Seed, SmartPlaylistFace.Cover(rock).Seed);
    }

    /// <summary>Two genres under one mood are told apart, or every row under "Calm" would be the same
    /// picture.</summary>
    [Fact]
    public void Two_genres_under_one_mood_are_different_pictures()
    {
        var rock = SmartPlaylistFace.Cover(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Rock"));
        var jazz = SmartPlaylistFace.Cover(SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Jazz"));

        Assert.NotEqual(rock.Seed, jazz.Seed);
    }

    /// <summary>
    /// The same list is the same picture on every launch. The genre variation is an FNV hash written out
    /// rather than <see cref="string.GetHashCode()"/>, which is randomised per process. With that one,
    /// every genre cover would be redrawn in a different arrangement on each launch.
    /// </summary>
    [Fact]
    public void One_list_is_one_picture_for_ever()
    {
        var id = SmartPlaylistId.Within(SmartPlaylistKind.Danceable, "Electronic / Dance");

        Assert.Equal(SmartPlaylistFace.Cover(id), SmartPlaylistFace.Cover(id));
        Assert.Equal(SmartPlaylistFace.Cover(id).Seed, SmartPlaylistFace.Cover(id).Seed);
    }

    /// <summary>The variation reaches the bit the colourway reads to decide how far apart its three
    /// colours sit. A variation that only moved the low bits would be the same picture in the same
    /// spread, which is not a variation.</summary>
    [Fact]
    public void The_variation_reaches_the_bit_that_widens_the_colourway()
    {
        var seeds = new[] { "Rock", "Jazz", "Pop", "Classical", "Folk", "Punk", "Latin", "Metal" }
            .Select(g => SmartPlaylistFace.Cover(SmartPlaylistId.Within(SmartPlaylistKind.Calm, g)).Seed)
            .ToList();

        // Both spans occur across a handful of real genres, so the set does not all sit at one width.
        Assert.Contains(seeds, s => (s >> 11) % 2 == 0);
        Assert.Contains(seeds, s => (s >> 11) % 2 == 1);
    }

    // ── what a row is called ──

    /// <summary>
    /// A genre row wears a title, not the genre it stands for. "Chill" above "Electronic / Dance" put a
    /// shelf label where a playlist name goes. That is why the title table exists.
    /// </summary>
    [Theory]
    [InlineData(SmartPlaylistKind.Calm, "Electronic / Dance", "Ambient Drift")]
    [InlineData(SmartPlaylistKind.Calm, "Rock", "Slow Burn")]
    [InlineData(SmartPlaylistKind.Energetic, "Metal", "Wall of Sound")]
    [InlineData(SmartPlaylistKind.Danceable, "Electronic / Dance", "Four to the Floor")]
    public void A_genre_row_wears_a_title(SmartPlaylistKind kind, string genre, string expected)
        => Assert.Equal(expected, SmartPlaylistFace.Name(SmartPlaylistId.Within(kind, genre)));

    /// <summary>
    /// The same name, asked with the kind spelled as it travels. Both wires carry the kind as a string: the
    /// peer catalogue and the cloud cut. Both name their lists through this one door. So a borrowed "Slow
    /// Burn" and a phone's "Slow Burn" are the same list the owner sees.
    /// </summary>
    [Theory]
    [InlineData("Calm", "Rock", 0, "Slow Burn")]
    [InlineData("Decade", null, 1970, "1970s")]
    public void The_same_name_answers_a_kind_spelled_as_it_travels(
        string kind, string? genre, int decade, string expected)
        => Assert.Equal(expected, SmartPlaylistFace.Name(kind, genre, decade));

    /// <summary>A kind this build does not know, the far end is ahead, reads as what it called itself
    /// rather than being dropped, because a list with an unfamiliar name still plays.</summary>
    [Fact]
    public void A_kind_this_build_does_not_know_is_called_what_it_called_itself()
        => Assert.Equal("Windswept", SmartPlaylistFace.Name("Windswept", null, 0));

    /// <summary>Every genre the app files under has a title under each mood that is broken out, so no
    /// real row falls back. The fallback exists for a genre added later, not for today.
    ///
    /// <para>Except the squares another list has taken over: see <see cref="MoodSets"/>. Those are
    /// not rows, so they are not asked: a name on one would be a list this app does not offer.</para>
    /// </summary>
    [Fact]
    public void Every_genre_has_a_title_under_every_mood_that_is_broken_out()
    {
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
            foreach (var genre in Genres.All.Where(g => g != Genres.Unknown))
            {
                if (!MoodSets.IsOffered(kind, genre))
                    continue;

                var name = SmartPlaylistFace.Name(SmartPlaylistId.Within(kind, genre));
                Assert.NotEqual(genre, name);
                Assert.False(string.IsNullOrWhiteSpace(name));
            }
    }

    /// <summary>A square another list has taken over has NO title of its own. It falls back to the genre,
    /// which is what an unnamed pair does. The two tables are written apart, and this is what stops them
    /// drifting. A name left behind here would be reachable through a shared cut and would read as a list
    /// this app does not have.</summary>
    [Fact]
    public void An_absorbed_square_has_no_title_left_behind()
    {
        foreach (var (cell, _) in MoodSets.EveryAbsorbed)
            Assert.Equal(
                cell.Genre, SmartPlaylistFace.Name(SmartPlaylistId.Within(cell.Kind, cell.Genre)));
    }

    /// <summary>A dropped square has no title left behind either, for the same reason, and is not
    /// offered.</summary>
    [Fact]
    public void A_dropped_square_has_no_title_left_behind()
    {
        Assert.NotEmpty(MoodSets.EveryDropped);
        foreach (var cell in MoodSets.EveryDropped)
        {
            Assert.Equal(
                cell.Genre, SmartPlaylistFace.Name(SmartPlaylistId.Within(cell.Kind, cell.Genre)));
            Assert.False(MoodSets.IsOffered(cell.Kind, cell.Genre));
        }
    }

    /// <summary>And the list that took it over really does draw from it. Absorption and drawing are
    /// separate decisions (see <see cref="MoodSets"/>), so nothing but this says that the songs of an
    /// absorbed square actually went somewhere.</summary>
    [Fact]
    public void Every_absorbed_square_is_drawn_by_the_list_that_took_it()
    {
        foreach (var (cell, taker) in MoodSets.EveryAbsorbed)
        {
            Assert.Contains(cell, MoodSets.CellsFor(taker.Kind, taker.Genre));
            Assert.False(MoodSets.IsAbsorbed(taker.Kind, taker.Genre));
        }
    }

    /// <summary>A genre with no title reads as itself rather than as nothing, which is what every row
    /// showed before the table existed.</summary>
    [Fact]
    public void A_genre_with_no_title_falls_back_to_itself()
    {
        var id = SmartPlaylistId.Within(SmartPlaylistKind.Calm, "Sea Shanties");

        Assert.Equal("Sea Shanties", SmartPlaylistFace.Name(id));
    }

    /// <summary>Titles are distinct: two rows under one mood must not read the same, and a title used
    /// twice across the whole table would make two lists look like one.</summary>
    [Fact]
    public void No_two_lists_share_a_title()
    {
        var names = new List<string>();
        foreach (var kind in SmartPlaylists.BrokenOutByGenre)
            foreach (var genre in Genres.All.Where(g => g != Genres.Unknown))
                if (MoodSets.IsOffered(kind, genre))
                    names.Add(SmartPlaylistFace.Name(SmartPlaylistId.Within(kind, genre)));

        Assert.Equal(names.Count, names.Distinct(StringComparer.OrdinalIgnoreCase).Count());
    }
}
