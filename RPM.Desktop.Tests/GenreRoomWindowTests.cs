using RPM.Core.Viz;
using RPM.Desktop.Presentation.Common;
using RPM.Desktop.Presentation.Main.Genres;
using RPM.Desktop.Presentation.Main.Genres.Models;

namespace RPM.Desktop.Tests;

/// <summary>
/// Where each shelf looks on the room's own plane.
/// </summary>
/// <remarks>
/// <para>If two shelves land in the same place, every genre head looks alike. That is the fault this guards
/// against. If a shelf lands somewhere new between two runs, its card changes when the app restarts. That
/// is what <c>string.GetHashCode</c> would cause, because it is seeded per process. Neither fault shows up
/// as anything other than "the pictures look wrong".</para>
/// </remarks>
public class GenreRoomWindowTests
{
    private static readonly string[] Shelves =
    [
        "Rock", "Pop", "Jazz", "Funk", "Latin", "Soul", "Blues", "Metal", "Folk", "World",
    ];

    /// <summary>Every shelf gets its own place, and no two share one.</summary>
    [Fact]
    public void No_two_shelves_look_at_the_same_place()
    {
        var places = Shelves.Select(GenreTile.Scatter).ToList();

        Assert.Equal(places.Count, places.Distinct().Count());
    }

    /// <summary>
    /// No two shelves draw the same room.
    /// </summary>
    /// <remarks>
    /// <b>The test looks at the pictures, not at the places.</b> Being some distance apart on the plane has
    /// no meaning on its own. What matters is whether the two rooms look different. So the test measures
    /// that, at ONE moment, so that drift over time cannot be what separates them.
    ///
    /// <para>Drawn at the head's shape and zoom, which is where the rooms are seen.</para>
    /// </remarks>
    [Fact]
    public void No_two_shelves_draw_the_same_room()
    {
        var (w, h) = CardRoomArt.Small(1400, 142, 32, 3600);
        var drawn = new Dictionary<string, byte[]>();

        using (var pass = new CardRoomArt.Pass(w, h))
        {
            foreach (var shelf in Shelves)
            {
                var place = GenreTile.Scatter(shelf);
                Assert.True(pass.Draw(
                    RoomPalette.Fallback, 12.5f, zoom: 2.6f,
                    shift: ((float)place.X, (float)place.Y)));

                drawn[shelf] = pass.Pixels.ToArray();
            }
        }

        for (var i = 0; i < Shelves.Length; i++)
        {
            for (var j = i + 1; j < Shelves.Length; j++)
            {
                var one = drawn[Shelves[i]];
                var two = drawn[Shelves[j]];

                long off = 0;
                for (var at = 1; at < one.Length; at += 4)
                    off += Math.Abs(one[at] - two[at]);

                var each = off / (double)(one.Length / 4);
                Assert.True(
                    each > 4,
                    $"{Shelves[i]} and {Shelves[j]} differ by {each:F1} of 255 a pixel, which is one "
                    + "picture drawn twice");
            }
        }
    }

    /// <summary>The same shelf is in the same place every time it is asked. A stable hash gives this, and
    /// the runtime's own hash does not.</summary>
    [Fact]
    public void A_shelf_keeps_its_place()
    {
        foreach (var shelf in Shelves)
            Assert.Equal(GenreTile.Scatter(shelf), GenreTile.Scatter(shelf));
    }

    /// <summary>Both axes are used. If x and y came from one roll, they would be the same number, and every
    /// shelf would sit on the diagonal of the plane. That is one axis of variety, not two.</summary>
    [Fact]
    public void The_two_axes_are_not_the_same_number()
    {
        foreach (var shelf in Shelves)
        {
            var place = GenreTile.Scatter(shelf);
            Assert.True(
                Math.Abs(place.X - place.Y) > 0.01,
                $"{shelf} sits on the diagonal at {place.X:F2}, so x and y came off one roll");
        }
    }

    /// <summary>
    /// A shelf at the far corner of the scatter still has figures in its room.
    /// </summary>
    /// <remarks>
    /// <b>This guards against a reach that is too short.</b> The room is a cluster of lobes around the
    /// origin, not an endless field. A window moved past the lobes sees only the ground colour, which is a
    /// flat block. If the reach is too short, every genre head comes out as a flat block. Nothing throws
    /// and nothing is logged. The pictures are simply missing.
    ///
    /// <para>Drawn at the head's own shape and zoom. This is the harder case. A band that is short and a
    /// window wide sees a thin slice through the middle of the cluster, so it runs out of lobes in y sooner
    /// than a card does. The check uses the spread of the picture, not its average, because a flat block
    /// and a lit one can have the same average.</para>
    /// </remarks>
    [Theory]
    [InlineData(1, 1)]
    [InlineData(1, -1)]
    [InlineData(-1, 1)]
    [InlineData(-1, -1)]
    public void The_room_still_has_figures_at_the_edge_of_the_reach(int x, int y)
    {
        // The widest a shelf can be scattered, which is what Scatter's own reach is measured against.
        var far = Shelves.Select(GenreTile.Scatter)
                         .Select(p => Math.Max(Math.Abs(p.X), Math.Abs(p.Y)))
                         .Max();

        var (w, h) = CardRoomArt.Small(1400, 142, 32, 3600);
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(
            RoomPalette.Fallback, 12.5f, zoom: 2.6f, shift: ((float)(far * x), (float)(far * y))));

        int darkest = 255, brightest = 0;
        for (var at = 0; at + 3 < pass.Pixels.Length; at += 4)
        {
            // The pixels are premultiplied BGRA, and the room is opaque, so any channel shows the picture.
            // Green is used, because most of the perceived brightness comes from green.
            var g = pass.Pixels[at + 1];
            darkest = Math.Min(darkest, g);
            brightest = Math.Max(brightest, g);
        }

        Assert.True(
            brightest - darkest > 24,
            $"at ({far * x:F2}, {far * y:F2}) the room spans {brightest - darkest} of 255, which is a "
            + "flat block: the window has been moved off the lobes");
    }

    /// <summary>
    /// A shelf with no colour of its own does not draw a black card.
    /// </summary>
    /// <remarks>
    /// A genre that the wordmark table does not know (for example one from a borrowed library, or one added
    /// without a run of the generator) has no accent. The accent's default is a colour of all zeroes.
    /// Deepened, that is black. A black room is not a missing room. It is a card drawn as a hole. The wall
    /// does not show a room for a card that has no answer, so this should never happen. The test still
    /// checks it, because "this never happens" depends on how the callers behave.
    /// </remarks>
    [Fact]
    public void A_shelf_with_no_colour_does_not_get_a_black_room()
    {
        var room = GenreWordmarks.Room(default);

        Assert.NotEqual(default, room.Glow);
        Assert.True(
            room.Glow.X + room.Glow.Y + room.Glow.Z > 0.1f,
            "a shelf with no colour came back with a black room, which draws as a hole in the wall");
    }
}
