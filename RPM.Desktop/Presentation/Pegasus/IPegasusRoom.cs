using RPM.Core.Viz;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// What Pegasus needs of the room behind it, over and above what any full-screen stage offers
/// (<see cref="IVizStage"/>): the colours of the record playing, which composition is up, and the low end
/// it is already measuring.
///
/// <para><b>Why an interface at all</b>, when there is exactly one implementation.
/// <see cref="PegasusStage"/> derives from <c>GLCanvasElement</c>, which lives in a separate assembly
/// (Uno.WinUI.Graphics3DGL). The runtime resolves a method's types when it is JIT-compiled, not when the
/// line runs. So any method that names the stage cannot start on a machine where GL is unavailable: it
/// throws a <c>FileNotFoundException</c> for the assembly before its first statement, and no try/catch
/// inside the method can catch that.</para>
///
/// <para>The host is written around exactly that failure, it catches a stage that will not stand up and
/// prints the reason on the black rather than taking the window down (see <c>PegasusPlayer.EnsureStage</c>
/// and the status line). Behind this interface, the concrete stage is named in ONE method, that method is
/// called from inside a try, and everything else, showing the view, sizing it, cycling the compositions,
/// dressing it as the record changes, is free of it. Without that, a failure the code goes to some trouble
/// to report politely would instead crash on the way in.</para>
/// </summary>
internal interface IPegasusRoom : IVizStage
{
    /// <summary>The low end, 0..1, as the room is lit by it. The chrome's lyric glow breathes on the same
    /// figure so the words and the room are answering one measurement rather than two.</summary>
    float Bass { get; }

    /// <summary>
    /// Whether the room has drawn a frame yet.
    ///
    /// <para>This is not the same as <see cref="IVizStage.RendererInfo"/> being set. The renderer string is
    /// read when the GL context is made, which is before the shaders are compiled and before any pixel
    /// exists. The black cover over the view comes off when this is true. If the cover came off on the
    /// renderer string, the view would show an empty surface for a frame or two.</para>
    /// </summary>
    bool HasPainted { get; }

    /// <summary>Eases the spectrum and the left-hand dim to suit the composition on screen.
    /// <paramref name="immediate"/> for a composition being SHOWN in this state rather than changed into
    /// it.</summary>
    void SetView(PegasusView view, bool immediate = false);

    /// <summary>The cover's own colours, eased toward over about a second. So a change of record fades the
    /// room rather than cutting it.</summary>
    void SetPalette(Color? tint, Color? glow, Color? shade);

    /// <summary>Hangs a mirror ball in the room, or takes it down. Only a few records call for one (see
    /// <see cref="MirrorBall"/>). It uses the same ease as the palette, so the ball arrives and leaves with
    /// the colours of the record that asked for it, not the previous record's.
    /// </summary>
    void SetMirrorBall(bool on);

    /// <summary>
    /// Where the chrome is writing: a box in fractions of the window, measured from its TOP-left corner,
    /// which is the way the composition over the room thinks about its own placing. A box with no width
    /// says nothing is written anywhere, which is the bare room.
    ///
    /// <para>The room keeps the mirror ball's light out of it. Everything else this stage draws is a soft
    /// gradient the words were dressed against, and the dim down the left was enough for those. A speck is
    /// a hard white light a few pixels across, and a title with one on it is too bright to read. The stage
    /// cannot work this out for itself: where the words land is decided by layout, and only the chrome
    /// knows it.</para>
    /// </summary>
    void SetWordBox(double left, double top, double right, double bottom);

    /// <summary>Which animation the room draws: the original or one of the TV's three others. The listener
    /// picks it in the settings panel. A change shows on the next frame, as a cut.</summary>
    void SetStyle(RoomStyle style);
}
