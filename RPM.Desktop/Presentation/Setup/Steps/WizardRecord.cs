using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

using RPM.Desktop.Presentation.Controls;
using RPM.Desktop.Presentation.NowPlaying;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Dresses a <see cref="RecordDisc"/> for the setup wizard, where it stands on its own.
///
/// <para><b>Why it needs dressing at all.</b> <c>RecordDisc</c> is built for the shell. Its viewbox and
/// its shadow are aligned to the RIGHT, because in the shell the record hangs half off the window's
/// edge. Both are sized in code by <c>ArtDisplay.Layout</c>. The vinyl image has no source, because
/// <c>ArtDisplay</c> presses it once a cover has landed. Used as it is on a page, <c>RecordDisc</c>
/// draws a small dark centre and no record at all.</para>
///
/// <para>The wizard has no album and no cover, so the pressing is the plain black one and the label
/// keeps the rpm stamp underneath it.</para>
/// </summary>
internal static class WizardRecord
{
    /// <summary>How much wider than the record its shadow is drawn. It matches
    /// <c>ArtDisplay.RecordHaloScale</c>, so the disc looks on this page as it does in the
    /// shell.</summary>
    private const double HaloShare = 1.08;

    /// <summary>
    /// How far the shadow is biased DOWN, as a fraction of the diameter.
    ///
    /// <para>The same value as <c>ArtDisplay.ShadowDropFraction</c>. The shadow is moved straight down,
    /// for the same reason as there. The vinyl's own sheen is at the upper left, so the halo looks thin
    /// along the top and heavy along the foot. The value is repeated because the two are private
    /// constants in a control that this file cannot see. If either changes, change both.</para>
    /// </summary>
    private const double DropShare = 0.03;

    /// <summary>How fast the label turns, in degrees a second.
    ///
    /// <para>Twelve degrees a second is about a sixteenth of the speed of a real record. A real record
    /// at 33 1/3 rpm makes a full turn in under two seconds, which looks like a spinning wheel. At this
    /// speed the turn is noticed only when someone watches it.</para></summary>
    private const double DegreesPerSecond = 12;

    /// <summary>How often the angle is written. This is not the display rate. On the Skia head every
    /// write repaints the window, and 12 degrees a second looks smooth at one write every 50 ms. The
    /// same reasoning applies as for <see cref="Common.BadgeBreath"/>.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>Centres the disc at <paramref name="size"/>, presses a black record into it, and
    /// optionally starts it turning.
    /// </summary>
    /// <param name="turn">Driven by a timer, not by a looping storyboard. Under Uno,
    /// <c>Storyboard.Stop()</c> does not reliably halt a <c>RepeatBehavior.Forever</c> animation on an
    /// element that stays in the tree. A record started that way would keep turning behind the next
    /// step.</param>
    public static async void Dress(RecordDisc disc, double size, bool turn = false)
    {
        ArgumentNullException.ThrowIfNull(disc);

        double halo = size * HaloShare;
        double drop = size * DropShare;

        // The control is sized to the shadow, not to the record. Sized to the disc's own diameter, it
        // clipped the halo at its bounds. The soft glow then ended on four straight edges, which made a
        // rectangle around the record. A shadow must never look like that. The extra space is the
        // halo's overhang plus the whole downward bias, so no part of the shadow reaches an edge.
        double bounds = halo + drop * 2;
        disc.Width = bounds;
        disc.Height = bounds;

        disc.Box.HorizontalAlignment = HorizontalAlignment.Center;
        disc.Box.VerticalAlignment = VerticalAlignment.Center;
        disc.Box.Width = size;
        disc.Box.Height = size;
        disc.Box.Margin = new Thickness(0);

        disc.ShadowHalo.HorizontalAlignment = HorizontalAlignment.Center;
        disc.ShadowHalo.VerticalAlignment = VerticalAlignment.Center;
        disc.ShadowHalo.Width = halo;
        disc.ShadowHalo.Height = halo;

        // The shadow is biased down. For a centred element that means twice the offset on the top
        // margin. A centred child splits its margins, so a top margin of 2d and a bottom margin of 0
        // moves it down by d.
        disc.ShadowHalo.Margin = new Thickness(0, drop * 2, 0, 0);

        if (turn)
            Turn(disc);

        // Last, and awaited. The pressing is rendered once and cached, but the first caller on a cold
        // start waits for it. Everything above is laid out before this, so the disc is the right size
        // and in the right place when the picture arrives, rather than jumping into place.
        if (await VinylPressing.PressBlackAsync() is { } black)
            disc.Stand.Vinyl.Source = black;
    }

    /// <summary>
    /// Starts the label turning, and stops when the disc leaves the tree.
    /// </summary>
    /// <remarks>Only the label turns. The vinyl ring is held still by <c>RecordDisc</c> itself,
    /// because real grooves are concentric. Turning them would be invisible, and it would cost a
    /// repaint.
    /// </remarks>
    private static void Turn(RecordDisc disc)
    {
        var clock = System.Diagnostics.Stopwatch.StartNew();
        var timer = new DispatcherTimer { Interval = Beat };

        timer.Tick += (_, _) => disc.Rotate.Angle = clock.Elapsed.TotalSeconds * DegreesPerSecond % 360;

        // A halt that cannot be skipped. Unloaded fires when the wizard leaves. A timer left running
        // would keep a reference to a page that is gone, and it would repaint a window the user is no
        // longer on.
        disc.Unloaded += (_, _) =>
        {
            timer.Stop();
            clock.Stop();
        };

        timer.Start();
    }
}
