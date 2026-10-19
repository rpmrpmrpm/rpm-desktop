using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Common;

/// <summary>
/// One line of chrome, and the state of its travel.
///
/// <para>A line short enough for its column never moves, and it is drawn plainly. A line that is too long
/// rests at its start. It walks left at reading pace until its tail shows. It holds there long enough to
/// be read, runs back, and rests again. <see cref="ScrollStartPause"/> at the start is the pause that
/// matters most, because that is where the title is, and the title is what a glance needs.</para>
///
/// <para><b>Why the edge is a sharp edge and not a fade.</b> A fade here needs one of three things, and this
/// toolkit has none of them. The first is a gradient FOREGROUND. Uno does not paint text with gradient
/// brushes, so the words would not draw at all. The second is an OPACITY MASK. There is no such property on
/// FrameworkElement in WinUI 3. The third is a veil laid over the top. That veil would need a colour, and
/// the words sit on a live picture that changes colour every second. So any colour chosen is wrong as soon
/// as the figures drift. The line is therefore clipped at the column's edge. The rest at the start gives the
/// reader time to read the title.</para>
///
/// <para>Written for Pegasus and lifted out of it. The phone's player scrolls a title that does not fit by
/// the same rule, rather than by a second rule that could drift from it.</para>
/// </summary>
internal sealed class Marquee
{
    /// <summary>How long a line rests at its starting position before it sets off again.</summary>
    private const double ScrollStartPause = 10.0;

    /// <summary>How long the line holds at the far end, so the tail can be read.</summary>
    private const double ScrollEndPause = 2.0;

    /// <summary>Reading pace, and the quicker pace of the run back. Both are in points a second. So a long
    /// title takes longer than a short one. If every line took the same time, a long line would be too fast
    /// to read.</summary>
    private const double ScrollSpeed = 42.0;
    private const double ReturnSpeed = 130.0;

    /// <summary>How far PAST its tail a line walks before it holds. If the last glyph stopped exactly on the
    /// clip's edge, it would look like a cut. A few points of space after it show that the line has really
    /// ended, and did not just run out of room.</summary>
    private const double TailSlack = 10.0;

    /// <param name="text">The words. They live inside <paramref name="host"/>, which must be a Canvas. A
    /// Canvas gives its children infinite space and lays them out at the size they ask for. That lets the
    /// whole line draw, tail included, so the clip decides what is seen, not the layout.</param>
    /// <param name="host">The column, clipped to what may be read.</param>
    /// <param name="shift">What the travel is written to.</param>
    /// <param name="colour">The ink, where the caller would rather say it here than in its markup. Left
    /// out, the line keeps whatever it was given.</param>
    public Marquee(TextBlock text, FrameworkElement host, TranslateTransform shift,
                   Windows.UI.Color? colour = null)
    {
        ArgumentNullException.ThrowIfNull(text);
        ArgumentNullException.ThrowIfNull(host);
        ArgumentNullException.ThrowIfNull(shift);

        Text = text;
        Host = host;
        Shift = shift;

        if (colour is { } ink) text.Foreground = new SolidColorBrush(ink);
    }

    public TextBlock Text { get; }
    public FrameworkElement Host { get; }
    public TranslateTransform Shift { get; }

    /// <summary>The line's own width, measured without any constraint. It is the width the line WOULD take
    /// if nothing clipped it. It is recomputed when the words or the font size change, never per
    /// frame.</summary>
    public double Natural { get; private set; }

    /// <summary>
    /// The column the line is read through: the width of the host, the width of its clip, and the width
    /// the travel is worked out against.
    ///
    /// <para>ONE number is used for all three. It is held here, rather than read back off the host on each
    /// frame. If the travel were measured against the host's <c>ActualWidth</c>, and the clip came from a
    /// SizeChanged handler, the two could differ by a couple of hundred points at the moment it matters. A
    /// line that walks further than its clip is wide would then stop with its tail already past the edge.
    /// That looks exactly like a tail that was never drawn.</para>
    /// </summary>
    public double Room { get; private set; }

    private double _height;

    /// <summary>Whether the line is read centred in its column or from its left edge.</summary>
    public bool Centred { get; set; }

    /// <summary>Where the line sits before any travel. If it is centred and fits, this is the middle of its
    /// column. Otherwise it is the left edge. A line with somewhere to go always starts where reading
    /// starts, whichever way it is set.</summary>
    public double Home => Centred && Natural < Room ? (Room - Natural) / 2 : 0;

    public double Offset { get; set; }
    public double Wait { get; set; } = ScrollStartPause;
    public Leg Where { get; set; } = Leg.Resting;

    public enum Leg { Resting, Running, Holding, Returning }

    /// <summary>Gives the line its column. The host is held to that width and clipped to it. So the visible
    /// area is exactly the column, and the travel below uses the same edge.</summary>
    public void Fit(double room)
    {
        Room = Math.Max(0, room);
        Host.Width = Room;
        Clip();
    }

    /// <summary>
    /// Measures the line as if it had the whole world to sit in.
    ///
    /// <para>Width is cleared before measuring. Otherwise the measure would only return the width that was
    /// set the last time.</para>
    /// </summary>
    public void Remeasure()
    {
        Text.Width = double.NaN;
        Text.Measure(new Windows.Foundation.Size(double.PositiveInfinity, double.PositiveInfinity));
        Natural = Text.DesiredSize.Width;
        Text.Width = Natural;

        // A Canvas has no size of its own. Nothing inside one counts toward what its parent gives it. So
        // the row's height must be set explicitly. Otherwise the lines stack on top of one another at zero
        // height, and the words disappear.
        //
        // The measured height is preferred, and the font's own height is the fallback. A measure taken by
        // hand, outside a layout pass, is not always answered. An element that the framework has not laid
        // out yet returns a desired size of zero, and zero is exactly the height that loses the line. The
        // font size is always known, is always sensible, and cannot be zero.
        var measured = Text.DesiredSize.Height;
        _height = measured > 1 ? measured : Text.FontSize * 1.4;
        Host.Height = _height;
        Clip();
    }

    /// <summary>Cuts the host to its column. Words that do not fit are cut off at the column's edge, instead
    /// of running out across the picture. There is a little slack above and below, because a descender sits
    /// slightly outside the line's own box.</summary>
    private void Clip() => Host.Clip = new RectangleGeometry
    {
        Rect = new Windows.Foundation.Rect(0, -3, Room, _height + 6),
    };

    /// <summary>Back to the start, and resting there. Called whenever the words change. Otherwise a new title
    /// would start partway through the old title's travel.</summary>
    public void Rewind()
    {
        Offset = 0;
        Wait = ScrollStartPause;
        Where = Leg.Resting;
        Shift.X = Home;
    }

    /// <summary>Moves every line that has somewhere to go. It sets one translate per moving line, and nothing
    /// else. It only touches the composition layer, so a scrolling title costs the screen behind it no
    /// layout work.</summary>
    public static void Walk(IEnumerable<Marquee> lines, double dt)
    {
        ArgumentNullException.ThrowIfNull(lines);

        foreach (var line in lines)
        {
            double room = line.Room;
            double over = line.Natural - room;

            if (room <= 0 || over <= 0.5)
            {
                // It fits, so nothing travels. Home is still written on every frame. The arrangement can
                // change under a line that is standing still, and a centred line moves when it does.
                if (line.Offset != 0) line.Rewind();
                line.Shift.X = line.Home;
                continue;
            }

            switch (line.Where)
            {
                case Leg.Resting:
                    line.Wait -= dt;
                    if (line.Wait <= 0) line.Where = Leg.Running;
                    break;

                case Leg.Running:
                    line.Offset += ScrollSpeed * dt;
                    if (line.Offset >= over + TailSlack)
                    {
                        line.Offset = over + TailSlack;
                        line.Wait = ScrollEndPause;
                        line.Where = Leg.Holding;
                    }
                    break;

                case Leg.Holding:
                    line.Wait -= dt;
                    if (line.Wait <= 0) line.Where = Leg.Returning;
                    break;

                case Leg.Returning:
                    line.Offset -= ReturnSpeed * dt;
                    if (line.Offset <= 0)
                    {
                        line.Offset = 0;
                        line.Wait = ScrollStartPause;
                        line.Where = Leg.Resting;
                    }
                    break;
            }

            line.Shift.X = line.Home - line.Offset;
        }
    }
}
