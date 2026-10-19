using System.Diagnostics;
using System.Runtime.CompilerServices;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

namespace RPM.Desktop.Presentation.Common;

/// <summary>The one clock that every cover's focus pull runs on: the sharp picture coming up over the
/// resident one, for however many tiles are loading at once.
///
/// <para>The clock is shared, and it ticks on a timer. On Skia a write marks the window as dirty, so
/// the cost of a fade is the repaint it asks for, not the arithmetic. One clock writes every live fade
/// in one pass, so the window repaints once per beat, whether one tile or twenty are fading. A separate
/// tween for each tile would repaint the window once per tile per write.</para>
///
/// <para>Do not drive fades with a loop that awaits <c>Task.Yield()</c>. Yield does not wait for a
/// frame. It posts the continuation straight back to the dispatcher. So the loop runs tens of thousands
/// of times a second, and each write asks for a full-window repaint. The loop also keeps the dispatcher
/// busy at normal priority. The cover loader works at low priority, behind layout and input, so it
/// would be starved.</para>
///
/// <para>Four steps over the fade at this beat are enough. The fade crosses the same sleeve, small and
/// soft, coming into focus under a sharp copy of itself. It is a focus pull, not something appearing
/// from nothing, so there is no hard edge for a step to show up on.</para>
/// </summary>
internal static class SharpFade
{
    /// <summary>How often the live fades are written. Each write repaints the whole window. This is the
    /// same rate that the badges and the playing mark use, for the same reason.</summary>
    private static readonly TimeSpan Beat = TimeSpan.FromMilliseconds(50);

    /// <summary>How long a cover takes to come into focus. Long enough not to look like a switch, and short
    /// enough that a tile the user scrolls to is sharp by the time they look at it.</summary>
    private static readonly TimeSpan Length = TimeSpan.FromMilliseconds(200);

    /// <summary>One fade in flight: the layer being written, where it started, and when.</summary>
    private readonly record struct Pull(FrameworkElement Layer, double From, long StartedAt);

    private static readonly List<Pull> Live = [];
    private static readonly Stopwatch Clock = new();
    private static DispatcherTimer? _timer;

    /// <summary>How many fades are live, and how many opacity writes have happened since the app started.
    /// The wall's scroll diagnostic reports these two numbers. Reading them costs nothing.</summary>
    public static int Count => Live.Count;

    public static long Writes { get; private set; }

    /// <summary>The opacity of a pull that started at <paramref name="from"/>, after
    /// <paramref name="elapsedMs"/>. It is eased out, and clamped at both ends. So a beat that lands late
    /// finishes the fade rather than overshooting it.</summary>
    public static double At(double from, double elapsedMs)
    {
        var t = Math.Clamp(elapsedMs / Length.TotalMilliseconds, 0, 1);
        return from + (1 - from) * (1 - Math.Pow(1 - t, 3));
    }

    /// <summary>The cost of one fade, from start to finish, in writes. That is also its cost in
    /// full-window repaints. The point of the clock is that this number is small, and it does not grow
    /// with the number of fades running at once.</summary>
    public static int WritesPerFade => (int)Math.Ceiling(Length.TotalMilliseconds / Beat.TotalMilliseconds);

    /// <summary>Fades <paramref name="layer"/> up whenever it is given a picture, for as long as the
    /// element lives. Call it once per element. A second call for the same element does nothing.
    ///
    /// <para><b>The walls use this instead of <c>ImageOpened</c>. The difference depends on the
    /// head.</b> Uno raises <c>ImageOpened</c> for a <see
    /// cref="Microsoft.UI.Xaml.Media.Imaging.WriteableBitmap"/>. WinUI does not. On WinUI that event is
    /// only raised for an image that the framework had to download and decode. A bitmap given with its
    /// pixels already in it raises neither. If <c>ImageOpened</c> were used there, the sharp layers
    /// would stay at zero opacity, and the wall would never sharpen past its resident 128px
    /// tier.</para>
    ///
    /// <para>A property-changed callback on <c>Source</c> is raised by both heads, because it comes
    /// from the framework's dependency-property system, not from the image pipeline. It fires when the
    /// picture is ASSIGNED, not when the element has uploaded it, which is a frame or so earlier. That
    /// costs nothing here, because the resident picture is the layer directly underneath. A sharp layer
    /// at full opacity with no pixels yet is transparent, not blank. What shows through is the small
    /// cover, which was already showing.</para>
    /// </summary>
    public static void Watch(Image layer)
    {
        if (Watched.TryGetValue(layer, out _))
            return;
        Watched.Add(layer, Watched);

        layer.RegisterPropertyChangedCallback(Image.SourceProperty, (element, _) =>
        {
            // A null source means the tile has been given a record with no sharp picture. The caller's own
            // rebind handler clears the layer there. This method only ever fades up.
            if (element is Image { Source: not null } picture)
                Start(picture);
        });

        // The binding may already have set a picture before this element was watched. This happens for
        // a tile that is realized onto a record the loader has already finished with. The callback
        // cannot report a change that has already happened, so the current state is read once here.
        if (layer.Source is not null)
            Start(layer);
    }

    /// <summary>Which elements are already watched. The table is weak, so when a wall is torn down its
    /// tiles go with it. The value is a placeholder and is never read.</summary>
    private static readonly ConditionalWeakTable<Image, object> Watched = new();

    /// <summary>Starts <paramref name="layer"/> fading up to full from its current opacity. A layer
    /// that is already fading is restarted, not added twice. So a tile given a second picture during a
    /// fade does not get two clocks writing to it.</summary>
    public static void Start(FrameworkElement layer)
    {
        if (!Clock.IsRunning)
            Clock.Restart();

        var from = layer.Opacity;
        var index = IndexOf(layer);
        var pull = new Pull(layer, from, Clock.ElapsedMilliseconds);
        if (index >= 0)
            Live[index] = pull;
        else
            Live.Add(pull);

        if (_timer is not null)
            return;

        _timer = new DispatcherTimer { Interval = Beat };
        _timer.Tick += OnBeat;
        _timer.Start();
    }

    /// <summary>Stops writing to <paramref name="layer"/>. The tile has been given another record, or
    /// has given back its sharp picture. The caller sets the layer's final value itself. This method
    /// does not know what it should be left at.</summary>
    public static void Stop(FrameworkElement layer)
    {
        var index = IndexOf(layer);
        if (index >= 0)
            Live.RemoveAt(index);
        StopIfIdle();
    }

    private static int IndexOf(FrameworkElement layer)
    {
        for (var i = 0; i < Live.Count; i++)
            if (ReferenceEquals(Live[i].Layer, layer))
                return i;
        return -1;
    }

    private static void OnBeat(object? sender, object e)
    {
        var now = Clock.ElapsedMilliseconds;

        // Backwards, so removing a finished fade cannot skip the one behind it.
        for (var i = Live.Count - 1; i >= 0; i--)
        {
            var pull = Live[i];
            var elapsed = now - pull.StartedAt;
            pull.Layer.Opacity = At(pull.From, elapsed);
            Writes++;
            if (elapsed >= Length.TotalMilliseconds)
                Live.RemoveAt(i);
        }

        StopIfIdle();
    }

    /// <summary>Nothing is left to write, so the timer stops. Stopping the timer always halts the
    /// writes. A looping storyboard does not reliably do that under Uno, for elements that stay in the
    /// tree.</summary>
    private static void StopIfIdle()
    {
        if (Live.Count > 0 || _timer is not { } timer)
            return;

        _timer = null;
        timer.Stop();
        timer.Tick -= OnBeat;
        Clock.Stop();
    }
}
