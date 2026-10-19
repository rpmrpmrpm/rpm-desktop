using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media.Animation;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// A slow, repeating fade for a control that cannot be used while something it started is still
/// running, such as the transport during a mix. A greyed button says "you cannot press this". A
/// pulsing button says "it is working". Together they tell the user why nothing happens when the
/// action takes several seconds.
/// </summary>
public static class Pulse
{
    /// <summary>How far the fade goes down, and how long each half of the cycle takes. The fade is
    /// slow on purpose: a quicker fade looks like an alert, not like work in progress.</summary>
    private const double Dimmest = 0.3;
    private static readonly TimeSpan Breath = TimeSpan.FromSeconds(0.9);

    private static readonly ConditionalWeakTable<FrameworkElement, Storyboard> Running = new();

    /// <summary>Starts or stops the pulse on <paramref name="element"/>. Safe to call with the state
    /// it is already in.</summary>
    public static void Set(FrameworkElement? element, bool on)
    {
        if (element is null)
            return;

        if (!on)
        {
            if (Running.TryGetValue(element, out var running))
            {
                running.Stop();
                Running.Remove(element);
            }
            // The animation can leave Opacity below 1 when it stops, so set it back to 1 directly.
            element.Opacity = 1;
            return;
        }

        if (Running.TryGetValue(element, out _))
            return;

        // The fade goes down and back up as three key frames of one animation. AutoReverse is not used
        // because Uno does not implement it. Without these key frames the fade would snap back to full
        // opacity at the start of each repeat, which looks like a blink.
        var fade = new DoubleAnimationUsingKeyFrames { RepeatBehavior = RepeatBehavior.Forever };
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(TimeSpan.Zero),
            Value = 1.0,
        });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(Breath),
            Value = Dimmest,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        fade.KeyFrames.Add(new EasingDoubleKeyFrame
        {
            KeyTime = KeyTime.FromTimeSpan(Breath + Breath),
            Value = 1.0,
            EasingFunction = new SineEase { EasingMode = EasingMode.EaseInOut },
        });
        Storyboard.SetTarget(fade, element);
        Storyboard.SetTargetProperty(fade, "Opacity");

        var board = new Storyboard();
        board.Children.Add(fade);
        Running.Add(element, board);
        Services.Shell.Storyboards.Play(board);
    }
}
