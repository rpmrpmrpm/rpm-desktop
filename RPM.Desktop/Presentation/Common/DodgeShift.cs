using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Media.Animation;
using RPM.Core.Layout;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// Keeps one group of buttons in a pane heading clear of the spinning record, by sliding it left as far as
/// <see cref="RecordDodge.Reserve"/> says. The X of <paramref name="shift"/> is written here and nowhere
/// else.
/// </summary>
/// <param name="group">The buttons that move together.</param>
/// <param name="shift">The group's own translate. Only its X is used.</param>
internal sealed class DodgeShift(FrameworkElement group, TranslateTransform shift)
{
    /// <summary>The slide now applied or being glided to, in px.</summary>
    private double _reserve;

    /// <summary>The glide that is running or holding its end value, if any.</summary>
    private Storyboard? _glide;

    /// <summary>Measures the group against the disc and moves it if the answer has changed.</summary>
    /// <param name="reach">The disc's leftmost window x at a window y (see
    /// <see cref="RecordDodge.Reserve"/>).</param>
    /// <param name="root">The window's content, the space the reach is measured in.</param>
    /// <param name="animate">Glide to the new place. False for a resize, which measures again on every
    /// frame of the drag: a glide started again on each frame would never arrive.</param>
    public void Update(Func<double, double> reach, UIElement root, bool animate)
    {
        if (group.ActualWidth <= 0)
            return;

        // The group's right edge and top in window coordinates. The transform includes the slide that is
        // applied now, so that slide is taken back out to get the resting edge. Otherwise each measure
        // would correct for the last one.
        var p = group.TransformToVisual(root).TransformPoint(new Windows.Foundation.Point(group.ActualWidth, 0));
        var now = shift.X;
        var reserve = RecordDodge.Reserve(reach, p.X - now, p.Y, group.ActualHeight);

        if (Math.Abs(reserve - _reserve) < 0.5)
            return;
        _reserve = reserve;

        // A finished glide still holds its end value above the local value. It is stopped before anything
        // else is set, or a later move without a glide would be set and never drawn.
        _glide?.Stop();
        _glide = null;

        if (!animate)
        {
            shift.X = -reserve;
            return;
        }

        // From where the group is drawn now, which is mid-glide when a glide was interrupted.
        var glide = new DoubleAnimation
        {
            From = now,
            To = -reserve,
            Duration = new Duration(TimeSpan.FromSeconds(ArtDisplay.TransitionSeconds)),
            EnableDependentAnimation = true,
            EasingFunction = new QuadraticEase { EasingMode = EasingMode.EaseInOut },
        };
        Storyboard.SetTarget(glide, shift);
        Storyboard.SetTargetProperty(glide, "X");
        _glide = new Storyboard();
        _glide.Children.Add(glide);
        _glide.Begin();
    }
}
