using System.Diagnostics;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Common;
using SkiaSharp;
using Xunit.Abstractions;

namespace RPM.Desktop.Tests;

/// <summary>
/// That the banner card's room compiles, draws, moves, and is cheap enough to draw on a timer.
///
/// <para>The first three fail silently. A shader that does not compile leaves the card with its flat wash
/// for ever. A room that compiles but comes out black is a card nobody can read.</para>
///
/// <para>The fourth checks cost. This room is drawn thirty-two times a second on the drawing thread, on
/// every card that has one, and on the head of an open playlist. That cost is the reason the room is drawn
/// at a tenth of each side. If the home screen or a tracklist ever feels slow, look at this figure
/// first.</para>
/// </summary>
public class CardRoomArtTests(ITestOutputHelper output)
{
    /// <summary>The room as it is tuned: no zoom and no shift. It is named here rather than written out at
    /// every call. These tests are about the room's own drawing. The window onto the room belongs to the
    /// card, see <c>CardRoom.Zoom</c> and <c>CardRoom.Shift</c>. <c>Draw</c> takes no defaults, so every
    /// call states its zoom and shift. A case that wants a different window must say so.</summary>
    private static (float Zoom, (float X, float Y) Shift) Straight => (1f, (0f, 0f));

    /// <summary>The plate at its widest, as CardRoom asks for it. See HomeBanner.Widest and
    /// HomeBanner.Height. There is one size for every card in the strip, whichever host it is in.</summary>
    private static (int Wide, int High) Plate => CardRoomArt.Small(760, 352);

    [Fact]
    public void The_room_compiles_as_SkSL()
    {
        var effect = SKRuntimeEffect.CreateShader(CardRoomArt.Sksl(), out var error);
        Assert.True(effect is not null, $"the card room did not compile: {error}");
        effect?.Dispose();
    }

    [Fact]
    public void It_is_not_a_black_plate()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(RoomPalette.Fallback, 3.4f, Straight.Zoom, Straight.Shift));

        long sum = 0;
        foreach (var b in pass.Pixels)
            sum += b;

        Assert.True(sum > 0, "the card room came out black");
    }

    /// <summary>The drift is the reason the room is drawn more than once.</summary>
    [Fact]
    public void Two_moments_are_two_rooms()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(RoomPalette.Fallback, 0.4f, Straight.Zoom, Straight.Shift));
        var one = pass.Pixels.ToArray();

        Assert.True(pass.Draw(RoomPalette.Fallback, 7.9f, Straight.Zoom, Straight.Shift));

        Assert.False(one.AsSpan().SequenceEqual(pass.Pixels), "the moment made no difference to the room");
    }

    /// <summary>
    /// Two shelves looking at different parts of the room are two pictures.
    /// </summary>
    /// <remarks>
    /// <b>Drift alone does not make shelves differ.</b> Each genre head can start at a different point
    /// along the drift, but the heads still look alike. The drift is slow by design. A long way along it,
    /// the figures are in nearly the same places. Moving the window is what makes the shelves look
    /// different. This test checks that it does, at ONE moment, so the drift cannot be what makes the
    /// difference.
    /// </remarks>
    [Fact]
    public void Two_places_on_the_plane_are_two_rooms()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, Straight.Zoom, (0f, 0f)));
        var here = pass.Pixels.ToArray();

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, Straight.Zoom, (4.3f, -2.1f)));

        Assert.False(
            here.AsSpan().SequenceEqual(pass.Pixels),
            "the shift made no difference to the room, so every card looking at it is the same picture");
    }

    /// <summary>The same place at the same moment gives the same picture. This lets a shelf's card and the
    /// head of its page show one room seen twice. It also stops a card from changing between two
    /// draws.</summary>
    [Fact]
    public void The_same_place_is_the_same_room()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, 1.8f, (4.3f, -2.1f)));
        var once = pass.Pixels.ToArray();

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, 1.8f, (4.3f, -2.1f)));

        Assert.True(once.AsSpan().SequenceEqual(pass.Pixels), "the room is not the same twice over");
    }

    /// <summary>Zooming in changes the picture too. It is tested separately from the shift, because they
    /// are different uniforms. A wiring mistake could keep one of them and drop the other.</summary>
    [Fact]
    public void Two_zooms_are_two_rooms()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, 1f, (0f, 0f)));
        var wide = pass.Pixels.ToArray();

        Assert.True(pass.Draw(RoomPalette.Fallback, 2.5f, 2.6f, (0f, 0f)));

        Assert.False(wide.AsSpan().SequenceEqual(pass.Pixels), "the zoom made no difference to the room");
    }

    /// <summary>
    /// The same arguments draw the same pixels. A table of still rooms depends on this.
    /// </summary>
    /// <remarks>
    /// <c>CardRoom</c> keeps a finished still room and gives it to the next card that matches on every
    /// argument (see <c>CardRoom.RoomKey</c>). That is only safe while the draw depends on its arguments
    /// and nothing else. A frame counter, a clock read inside the shader, or anything left over from the
    /// previous pass would break it. So this property is tested here, not assumed.
    /// </remarks>
    [Fact]
    public void One_set_of_arguments_is_one_picture()
    {
        var (w, h) = Plate;

        using var one = new CardRoomArt.Pass(w, h);
        Assert.True(one.Draw(RoomPalette.Fallback, 4.25f, 1.8f, (0.3f, -0.2f)));
        var first = one.Pixels.ToArray();

        // This is a second pass, not the first one drawn twice. A card that borrows another card's picture
        // uses a second surface. A draw that agrees with itself but not with another surface would still be
        // the wrong room on the wall.
        using var two = new CardRoomArt.Pass(w, h);
        Assert.True(two.Draw(RoomPalette.Fallback, 4.25f, 1.8f, (0.3f, -0.2f)));

        Assert.True(
            first.AsSpan().SequenceEqual(two.Pixels),
            "two passes given the same arguments drew different rooms, so a still room cannot be shared");
    }

    /// <summary>
    /// A frame costs less than a fifth of the 66 ms between two ticks.
    /// </summary>
    /// <remarks>
    /// The limit is loose on purpose. This runs on whatever machine the tests run on, and it guards against
    /// a large slowdown, not a small one. Raising <c>CardRoomArt.Coarse</c>, adding a blur, or driving the
    /// room from the player would show up here first.
    /// </remarks>
    [Fact]
    public void A_frame_is_cheap_enough_to_draw_on_a_timer()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);

        // Once to build the effect and warm the surface, which is not what is being measured.
        Assert.True(pass.Draw(RoomPalette.Fallback, 0f, Straight.Zoom, Straight.Shift));

        const int Frames = 20;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
            pass.Draw(RoomPalette.Fallback, i * 0.066f, Straight.Zoom, Straight.Shift);
        clock.Stop();

        var each = clock.Elapsed.TotalMilliseconds / Frames;
        output.WriteLine($"{w}x{h}: {each:0.00} ms a frame");
        Assert.True(each < 13, $"a card room frame took {each:0.00} ms, which is a quarter of its tick");
    }

    /// <summary>
    /// Three rooms drawn on one tick still leave most of the tick free.
    /// </summary>
    /// <remarks>
    /// The strip draws three cards, and all three animate at one size and one rate. The worst case is that
    /// all three are generated lists, and that is what this measures. Three frames must fit in the 31 ms
    /// that the clock actually delivers, with the drawing thread's other work still to do. The limit is
    /// half of that. A timer that uses its whole interval slips late, so a request for 15 frames a second
    /// delivers about 11.
    /// </remarks>
    [Fact]
    public void Three_rooms_fit_inside_one_tick()
    {
        var (w, h) = Plate;
        using var pass = new CardRoomArt.Pass(w, h);
        pass.Draw(RoomPalette.Fallback, 0f, Straight.Zoom, Straight.Shift);

        const int Frames = 20;
        var clock = Stopwatch.StartNew();
        for (var i = 0; i < Frames; i++)
            pass.Draw(RoomPalette.Fallback, i * 0.03f, Straight.Zoom, Straight.Shift);
        clock.Stop();

        var each = clock.Elapsed.TotalMilliseconds / Frames;
        var three = each * 3;
        output.WriteLine($"{w}x{h}: {each:0.00} ms a frame, {three:0.00} ms for three");
        Assert.True(three < 15, $"three rooms cost {three:0.00} ms of a 31 ms tick");
    }
}
