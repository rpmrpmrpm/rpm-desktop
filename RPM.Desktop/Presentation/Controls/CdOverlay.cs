using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Imaging;

namespace RPM.Desktop.Presentation.Controls;

/// <summary>The two pictures drawn over a whole CD: a clean rim with the rpm hub, and a CD player's spindle
/// in the hole.
///
/// <para>The disc art for a CD is usually a photograph cut out by <see cref="Services.Art.DiscStamp"/>. Its
/// outer few percent is the disc's clear plastic rim. That rim shows the table and the shadow the disc was
/// photographed on. The face covers that band with an even ring, so every CD has the same smooth edge. It
/// covers the middle with the same hub. The spindle makes the disc look as if it sits in a player. Both
/// images are made from the source artwork by scripts/make_cd_overlays.py.</para>
///
/// <para>It is sized by its parent and is square. The face fills it. The spindle is centred at
/// <see cref="SpindleShare"/> of its edge. Draw it at exactly the disc's size, over the disc art and inside
/// the same rotating layer. Then the overlays turn with the disc, as a disc and a spindle do. Use it only
/// for a picture of the whole disc (<see cref="NowPlaying.ScannedDisc.CoversRecord"/>). A vinyl label sits
/// on the app's own pressing, which has its own hole.</para>
///
/// <para>Code-only, like <see cref="StepBadge"/>.</para>
/// </summary>
public sealed partial class CdOverlay : Grid
{
    /// <summary>The spindle's width as a share of the disc's diameter. In the source images the face is
    /// 1400 px across and the spindle is 176 px.</summary>
    private const double SpindleShare = 176.0 / 1400.0;

    /// <summary>The coordinate size that the two pictures are laid out in. A Viewbox scales it to
    /// fit.</summary>
    private const double Layout = 1000;

    public CdOverlay()
    {
        IsHitTestVisible = false;

        var face = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/cd-face.png")),
            Stretch = Stretch.Fill,
            Width = Layout,
            Height = Layout,
        };
        var spindle = new Image
        {
            Source = new BitmapImage(new Uri("ms-appx:///Assets/cd-spindle.png")),
            Stretch = Stretch.Fill,
            Width = Layout * SpindleShare,
            Height = Layout * SpindleShare,
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
        };

        var square = new Grid { Width = Layout, Height = Layout };
        square.Children.Add(face);
        square.Children.Add(spindle);
        Children.Add(new Viewbox { Stretch = Stretch.Fill, Child = square });
    }
}
