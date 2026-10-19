using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Shell;

/// <summary>
/// The one thing that says what the whole window is standing on: the app's own backdrop, a record's
/// sleeve, or an artist's photograph. It is also the only way any of them gets onto the screen.
///
/// <para><b>Why this is one control and not a stack.</b> If a page's colours were laid over the
/// backdrop, the page would be a sheet over a photograph. Whenever the sheet was not fully opaque, the
/// photograph would show: between one record and the next, at the end of a fade, or on a ground with
/// no colour. Here the backdrop is not underneath anything. It is one of the things this control can
/// show, and it is shown only when asked for.</para>
///
/// <para><b>What arrives when.</b> Two things call <see cref="Show"/>, and they arrive on their own
/// timing. The first is the shell, when the listener changes the backdrop in Settings. The second is
/// the shell again, when the page on screen sets its look or is left (a sleeve read from disk, a photo
/// from the network). Both run on the dispatcher, and the last call wins. The most recent answer about
/// what the window is standing on is the correct one. The only state kept between calls is which face
/// is up and whose ground it holds. Both are written before <see cref="Show"/> returns.</para>
///
/// <para>The third arrival is the removal of the outgoing face. It is a timer this control starts
/// itself. The timer checks a turn number, not the state of the faces. A move started during the fade
/// owns the faces from that moment. An older timer that hid one of them would remove the ground from
/// the screen.</para>
/// </summary>
public sealed partial class ShellGround : UserControl
{
    /// <summary>How long a new ground takes to fade in over the one it replaces. It matches the album
    /// page's own fade, so the window and the card on it move together.</summary>
    private const double CrossFadeMs = 260;

    public ShellGround() => InitializeComponent();

    /// <summary>Which face is the one on screen.</summary>
    private bool _onA;

    /// <summary>Whose ground that face holds, or null before the first has ever been shown. Compared by
    /// <see cref="object.Equals(object?, object?)"/> rather than by reference, so a page is itself and a
    /// backdrop is the picture it names.</summary>
    private object? _owner;

    /// <summary>Whose ground is on screen. The shell reads it to tell a backdrop it has just changed from
    /// one it has not.</summary>
    public object? Owner => _owner;

    private Grid Face => _onA ? FaceA : FaceB;

    private Grid Spare => _onA ? FaceB : FaceA;

    /// <summary>One fade per face, kept between calls. A fade that is redirected part way through must
    /// carry on from where it was. A new fade each time would restart it (see <see
    /// cref="ChromeFade"/>).</summary>
    private readonly Dictionary<Grid, ChromeFade> _fades = [];

    private ChromeFade Fading(Grid face)
    {
        if (!_fades.TryGetValue(face, out var fade))
            _fades[face] = fade = new ChromeFade(face);
        return fade;
    }

    private int _turn;
    private DispatcherTimer? _wait;

    /// <summary>
    /// Puts <paramref name="ground"/> on the window, as belonging to <paramref name="owner"/>.
    ///
    /// <para>The owner tells the two kinds of change apart, and they are drawn differently. The SAME
    /// owner dressed differently is one surface getting closer to its final look. An example is an
    /// artist's photograph arriving a moment after the colour it was read beside. That change is
    /// written straight onto the current face, the same way a page writes its own layers. A NEW owner
    /// is a different room. It is faded in: painted on the idle face, raised above, and eased up over
    /// what is there.</para>
    ///
    /// <para>The first ground of a session is written with no fade. There is nothing behind it to fade
    /// from, so the window would otherwise fade up out of its own fill.</para>
    /// </summary>
    public void Show(object owner, PageBackdrop.Ground ground)
    {
        if (Equals(owner, _owner))
        {
            Dress(Face, ground);
            Fading(Face).To(1, CrossFadeMs);
            return;
        }

        if (_owner is null)
        {
            Dress(Face, ground);
            _owner = owner;
            Fading(Face).To(1, 0);
            return;
        }

        var turn = ++_turn;
        _wait?.Stop();

        var incoming = Spare;
        var outgoing = Face;

        // Start from nothing, wherever an earlier move left the fade.
        Fading(incoming).To(0, 0);
        Dress(incoming, ground);
        Canvas.SetZIndex(outgoing, 0);
        Canvas.SetZIndex(incoming, 1);

        // Kept at full strength underneath for the whole fade. This also covers a face that was already
        // fading out when this call came in. That happens when a page is left and another is opened in
        // the same dispatcher turn.
        Fading(outgoing).To(1, 0);
        Fading(incoming).To(1, CrossFadeMs);

        _onA = !_onA;
        _owner = owner;

        _wait = new DispatcherTimer { Interval = TimeSpan.FromMilliseconds(CrossFadeMs) };
        _wait.Tick += (_, _) =>
        {
            _wait?.Stop();
            if (turn != _turn)
                return;   // A newer move owns the faces now

            // A hidden face is empty. Nothing shows it, so its picture is released.
            Fading(outgoing).To(0, 0);
            WashArt(outgoing).ImageSource = null;
        };
        _wait.Start();
    }

    /// <summary>One face is set up with a ground. All of it is written before the face fades in, so the
    /// colour and the picture do not appear one at a time.</summary>
    private void Dress(Grid face, PageBackdrop.Ground ground)
    {
        var a = ReferenceEquals(face, FaceA);

        (a ? ColourA : ColourB).Fill = ground.Colour;
        WashArt(face).ImageSource = ground.Wash;
        (a ? WashA : WashB).Opacity = ground.Wash is null ? 0 : ground.WashStrength;
        (a ? VeilA : VeilB).Opacity = ground.Veil;
        (a ? WeightA : WeightB).Fill = ground.Weight;
    }

    private ImageBrush WashArt(Grid face) => ReferenceEquals(face, FaceA) ? WashArtA : WashArtB;
}
