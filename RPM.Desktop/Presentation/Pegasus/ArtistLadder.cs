using System.Diagnostics;
using Microsoft.UI;
using Microsoft.UI.Text;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Library;
using RPM.Desktop.Presentation.Main;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The alphabet down the right edge of Pegasus's artists list, in name order (see PegasusChrome.Artists).
/// It is the Android TV app's AlphabetLadder (FOBO c86660c2e, ANDR 1128831), at its sizes in desktop
/// units: a dark pill <see cref="NarrowWidth"/> wide holding the list's letters at <see cref="RestText"/>.
/// While the remote stands on it (<see cref="Active"/>) it grows to <see cref="WideWidth"/>, its letters to
/// <see cref="ActiveText"/>, and the letter in hand (<see cref="Current"/>) stands on a white disc.
///
/// <para><b>The focus look Pegasus is moving to.</b> The letter in hand grows by <see cref="LitGrow"/> and the
/// other letters dim to <see cref="OthersDim"/> (decision of 2026-10-24: grow 8 %, others at 75 %, no glow).
/// The ladder is one stop for the remote, not one per letter, as on the TV: Up and Down step the letter in
/// hand. The host leaves it to draw that itself: its stop is marked as one that draws its own focus (see
/// FocusLift, LiftPart.Own).</para>
///
/// <para><b>Moved on the frame tick.</b> The grow changes the letters' type size, which a clip or a scale on
/// the compositor cannot do without the letters swelling out of shape (the width grows by 1.87 and the type
/// by 1.57). So it is laid out, once a frame for <see cref="GrowMs"/>, over the app's one move curve
/// (<see cref="PaneGlide.EaseAt"/>). Twenty-seven rungs of one letter each. Settled, <see cref="Tick"/>
/// does nothing.</para>
///
/// <para>It scrolls itself so the letter in hand stands a third of the way down, which it needs once grown.
/// The pointer can turn it with the wheel and click a letter (<see cref="RungClicked"/>). The keys are the
/// host's: this draws, and says which letter was clicked.</para>
/// </summary>
internal sealed partial class ArtistLadder : Grid
{
    /// <summary>The pill's width at rest and in hand, in scaled units (the TV's 30.s and 56.s).</summary>
    public const double NarrowWidth = 30, WideWidth = 56;

    /// <summary>The letters' type size at rest and in hand, scaled (the TV's 14.ts and 22.ts).</summary>
    private const double RestText = 14, ActiveText = 22;

    /// <summary>A letter's disc, as a share of its type size (the TV's 1.7).</summary>
    private const double DiscShare = 1.7;

    /// <summary>The room above and below a letter, at rest and what it gains in hand, in fixed pixels (the
    /// TV's 2 + 4 dp, at two desktop pixels a dp).</summary>
    private const double RungAir = 4, RungAirGrown = 8;

    /// <summary>The pill's room above its first letter and below its last, scaled (the TV's 8.s).</summary>
    private const double PillAir = 8;

    /// <summary>How long the grow and a letter's lighting take, as a card's do on the TV (GROW_MS).</summary>
    private const double GrowMs = 150;

    /// <summary>How much the letter in hand grows, and how bright the others stand while it does.</summary>
    private const double LitGrow = 0.08, OthersDim = 0.75;

    /// <summary>The letters' brightness at rest: the TV's Ink.Dim, #99FFFFFF.</summary>
    private const double RestInk = 0x99 / 255.0;

    /// <summary>The pill's smoke: light at rest, darker in hand so its letters read over the cards it grows
    /// across (the TV's #33000000 and #99000000).</summary>
    private const byte RestFill = 0x33, ActiveFill = 0x99;

    /// <summary>The letter in hand, dark on its white disc (the TV's #FF111111).</summary>
    private static readonly Color LitInk = Color.FromArgb(0xFF, 0x11, 0x11, 0x11);

    private readonly Border _pill;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _column;
    private readonly SolidColorBrush _fill = new(Color.FromArgb(RestFill, 0, 0, 0));
    private readonly List<Rung> _rungs = [];
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private double _lastTick;

    private double _scale = 1;
    private bool _active;
    private int _current;

    /// <summary>The grow, 0 at rest and 1 in hand, and where its tween started and is going.</summary>
    private double _grow, _growFrom, _growTo;
    private double _growStartedAt;
    private bool _growLanded = true;

    /// <summary>Frames left in which the scroll is set again without animation, for a grow that has just
    /// landed: the scroller learns the ladder's new height a layout pass after it is set.</summary>
    private int _aimFrames;

    /// <summary>Whether anything was laid out at a size other than the settled one, so the next settled
    /// tick lays the ladder out once more.</summary>
    private bool _dirty = true;

    public ArtistLadder()
    {
        HorizontalAlignment = HorizontalAlignment.Right;
        VerticalAlignment = VerticalAlignment.Top;

        _column = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _column,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            HorizontalScrollMode = ScrollMode.Disabled,
            ZoomMode = ZoomMode.Disabled,
            IsTabStop = false,
            BringIntoViewOnFocusChange = false,
        };
        _pill = new Border { Background = _fill, Child = _scroll };
        Children.Add(_pill);
    }

    /// <summary>A letter was clicked with the pointer: its place on the ladder.</summary>
    public event Action<int>? RungClicked;

    /// <summary>How many letters the ladder holds.</summary>
    public int Count => _rungs.Count;

    /// <summary>The letter in hand, as a place on the ladder.</summary>
    public int Current
    {
        get => _current;
        set
        {
            var to = Math.Clamp(value, 0, Math.Max(0, _rungs.Count - 1));
            if (to == _current)
                return;
            _current = to;
            Aim(animate: _growLanded);
        }
    }

    /// <summary>Whether the remote stands on the ladder: it grows, and the letter in hand is lit.</summary>
    public bool Active
    {
        get => _active;
        set
        {
            if (_active == value)
                return;
            _active = value;
            _growFrom = _grow;
            _growTo = value ? 1 : 0;
            _growStartedAt = Now;
            _growLanded = false;
            foreach (var rung in _rungs)
                rung.Weight(value);
        }
    }

    /// <summary>Pegasus's scale (QueueScale), which the scaled sizes are multiplied by.</summary>
    public double Sizing
    {
        get => _scale;
        set
        {
            if (value <= 0 || Math.Abs(_scale - value) < 0.001)
                return;
            _scale = value;
            LayOut();
            Aim(animate: false);
        }
    }

    /// <summary>The pill's width at rest, at this scale: the room the list keeps for it.</summary>
    public double RestWidth => NarrowWidth * _scale;

    /// <summary>The letters, A to Z and then "#". The letter in hand goes back to the first.</summary>
    public void SetLetters(IReadOnlyList<ArtistLetter> letters)
    {
        var same = letters.Count == _rungs.Count;
        for (var i = 0; same && i < letters.Count; i++)
            same = _rungs[i].Letter == letters[i].Letter;
        _current = 0;
        if (!same)
        {
            _column.Children.Clear();
            _rungs.Clear();
            for (var i = 0; i < letters.Count; i++)
            {
                var rung = new Rung(letters[i].Letter, i, this);
                rung.Weight(_active);
                _rungs.Add(rung);
                _column.Children.Add(rung.Root);
            }
            LayOut();
        }
        _scroll.ChangeView(null, 0, null, disableAnimation: true);
    }

    private double Now => _clock.Elapsed.TotalMilliseconds;

    /// <summary>
    /// One frame: the grow's tween, each letter's lighting easing to where it belongs, and the scroll kept
    /// on the letter in hand while the ladder changes height. Nothing at all once everything has landed.
    /// From the chrome's frame tick while the artists list is up.
    /// </summary>
    public void Tick()
    {
        var now = Now;
        var dt = Math.Min(100, now - _lastTick);
        _lastTick = now;

        var moving = false;

        if (!_growLanded)
        {
            var t = (now - _growStartedAt) / GrowMs;
            _grow = t >= 1 ? _growTo : _growFrom + ((_growTo - _growFrom) * PaneGlide.EaseAt(t));
            _growLanded = t >= 1;
            moving = true;

            // While the ladder changes height the letter in hand is held where it stands, and once more
            // for a few frames after, until the scroller has measured the height it landed at.
            Aim(animate: false);
            if (_growLanded)
                _aimFrames = 3;
        }

        // Each letter's light walks to its goal at the grow's pace: one GrowMs from dark to lit.
        var step = dt / GrowMs;
        for (var i = 0; i < _rungs.Count; i++)
        {
            var goal = _active && i == _current ? 1.0 : 0.0;
            var rung = _rungs[i];
            if (rung.Light == goal)
                continue;
            rung.Light = goal > rung.Light ? Math.Min(goal, rung.Light + step) : Math.Max(goal, rung.Light - step);
            moving = true;
        }

        if (moving || _dirty)
        {
            _dirty = moving;
            LayOut();
        }

        if (_aimFrames > 0)
        {
            _aimFrames--;
            Aim(animate: false);
        }
    }

    /// <summary>The ladder at the grow and the lighting it stands at now.</summary>
    private void LayOut()
    {
        var g = _grow;
        var width = (NarrowWidth + ((WideWidth - NarrowWidth) * g)) * _scale;
        _pill.Width = width;
        _pill.CornerRadius = new CornerRadius(width / 2);
        _column.Padding = new Thickness(0, PillAir * _scale, 0, PillAir * _scale);
        _fill.Color = Color.FromArgb((byte)Math.Round(RestFill + ((ActiveFill - RestFill) * g)), 0, 0, 0);

        var type = (RestText + ((ActiveText - RestText) * g)) * _scale;
        var air = RungAir + (RungAirGrown * g);

        // The others stand at the TV's Ink.Dim at rest and dim to OthersDim of white in hand.
        var ink = RestInk + ((OthersDim - RestInk) * g);
        foreach (var rung in _rungs)
            rung.Lay(type, air, ink);
    }

    /// <summary>Scrolls the ladder so the letter in hand stands a third of the way down it.</summary>
    private void Aim(bool animate)
    {
        if (_rungs.Count == 0 || _scroll.ViewportHeight <= 0)
            return;
        var type = (RestText + ((ActiveText - RestText) * _grow)) * _scale;
        var air = RungAir + (RungAirGrown * _grow);
        var top = (PillAir * _scale) + (_current * ((type * DiscShare) + (air * 2)));
        var to = Math.Clamp(top - (_scroll.ViewportHeight / 3), 0, Math.Max(0, _scroll.ScrollableHeight));
        if (Math.Abs(to - _scroll.VerticalOffset) > 0.5)
            _scroll.ChangeView(null, to, null, disableAnimation: !animate);
    }

    /// <summary>One letter: a disc with the letter on it twice, light for the ladder and dark for the white
    /// disc, the one fading as the other comes up. Two lines rather than one recoloured, so the letter never
    /// flips from light to dark halfway through its lighting.</summary>
    private sealed class Rung
    {
        private readonly Grid _holder;
        private readonly Border _disc;
        private readonly TextBlock _light;
        private readonly TextBlock _dark;
        private readonly ScaleTransform _grow = new();
        private bool _hovered;

        public Rung(char letter, int place, ArtistLadder ladder)
        {
            Letter = letter;
            var text = letter.ToString();
            _disc = new Border
            {
                Background = new SolidColorBrush(Colors.White),
                Opacity = 0,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            _light = Line(text, Colors.White);
            _dark = Line(text, LitInk);
            _dark.Opacity = 0;
            _dark.FontWeight = FontWeights.SemiBold;

            _holder = new Grid
            {
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
                RenderTransform = _grow,
                RenderTransformOrigin = new Windows.Foundation.Point(0.5, 0.5),
            };
            _holder.Children.Add(_disc);
            _holder.Children.Add(_light);
            _holder.Children.Add(_dark);

            // The whole width of the pill takes the pointer, so a letter is easy to hit on a narrow ladder.
            Root = new Grid { Background = new SolidColorBrush(Colors.Transparent) };
            Root.Children.Add(_holder);
            Root.Tapped += (_, e) =>
            {
                e.Handled = true;
                ladder.RungClicked?.Invoke(place);
            };
            Root.PointerEntered += (_, _) => Hover(true);
            Root.PointerExited += (_, _) => Hover(false);
            Root.PointerCanceled += (_, _) => Hover(false);
            Root.PointerCaptureLost += (_, _) => Hover(false);

            void Hover(bool on)
            {
                _hovered = on;
                ladder._dirty = true;
            }
        }

        public char Letter { get; }

        public Grid Root { get; }

        /// <summary>How lit the letter is: 0 like the others, 1 on its disc and grown.</summary>
        public double Light { get; set; }

        /// <summary>SemiBold in hand, as the TV writes every letter while the ladder is active.</summary>
        public void Weight(bool active) =>
            _light.FontWeight = active ? FontWeights.SemiBold : FontWeights.Normal;

        public void Lay(double type, double air, double ink)
        {
            var disc = type * DiscShare;
            if (Math.Abs(_light.FontSize - type) > 0.01)
            {
                _light.FontSize = type;
                _dark.FontSize = type;
            }
            _holder.Width = disc;
            _holder.Height = disc;
            _disc.Width = disc;
            _disc.Height = disc;
            _disc.CornerRadius = new CornerRadius(disc / 2);
            Root.Padding = new Thickness(0, air, 0, air);

            var lit = PaneGlide.EaseAt(Light);
            _disc.Opacity = lit;
            _dark.Opacity = lit;
            _light.Opacity = (_hovered ? 1 : ink) * (1 - lit);
            _grow.ScaleX = _grow.ScaleY = 1 + (LitGrow * lit);
        }

        private static TextBlock Line(string text, Color ink) => new()
        {
            Text = text,
            Foreground = new SolidColorBrush(ink),
            HorizontalAlignment = HorizontalAlignment.Center,
            VerticalAlignment = VerticalAlignment.Center,
            TextAlignment = TextAlignment.Center,
            IsHitTestVisible = false,
        };
    }
}
