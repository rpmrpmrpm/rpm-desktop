#if DEBUG
#if RPM_SKIA
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RPM.Core.Audio;
using Windows.UI;

namespace RPM.Desktop.Presentation.MixLab;

/// <summary>
/// One deck's channel on the mixer: its fader and its three EQ knobs, as the set is running them.
///
/// <para>Everything here is a readout. Nothing is a control. The changeover turns these controls. The
/// fader moves across the crossfade, and the bass knob moves during the bass swap. A hand reaching in
/// from the screen would fight the set for the same knob. The strip is for watching. The bass swap is
/// the one move in a mix with nothing in the waveform to see, so it is easy to miss.</para>
///
/// <para>It uses bars and not only numbers. The question is rarely what the value is. It is which way
/// the value is moving, and whether the other deck is moving the opposite way.</para>
/// </summary>
public sealed partial class MixerStrip : StackPanel
{
    private static readonly Color Dim = Color.FromArgb(0xFF, 0x8A, 0x90, 0x9A);
    private static readonly Color Track = Color.FromArgb(0x30, 0xFF, 0xFF, 0xFF);
    private static readonly Color Volume = Color.FromArgb(0xFF, 0x4C, 0xD9, 0x8A);
    private static readonly Color Boost = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Color Cut = Color.FromArgb(0xFF, 0xF2, 0xC0, 0x4C);

    private readonly TextBlock _heading;
    private readonly Row _volume, _low, _mid, _high;

    public MixerStrip(string name)
    {
        Spacing = 4;

        _heading = new TextBlock
        {
            Text = name,
            FontSize = 12,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF)),
        };
        Children.Add(_heading);

        _volume = Add("Volume");
        _low = Add("Bass");
        _mid = Add("Mid");
        _high = Add("High");
    }

    /// <summary>What the deck is doing right now. Called from the render loop.</summary>
    /// <param name="fader">The deck's own fader, 0 to 1.</param>
    /// <param name="loaded">False when there is no record on the deck. This is not the same as a record
    /// sitting at zero. An empty deck shows nothing, and it does not show a fader turned down.</param>
    public void Show(bool loaded, double fader, double lowDb, double midDb, double highDb)
    {
        if (!loaded)
        {
            _volume.Clear();
            _low.Clear();
            _mid.Clear();
            _high.Clear();
            return;
        }

        // The fader fills from the left. It has no middle position: it is off or it is up.
        _volume.Show(Math.Clamp(fader, 0, 1), 0, $"{fader * 100:F0}%", Volume);

        Knob(_low, lowDb);
        Knob(_mid, midDb);
        Knob(_high, highDb);
    }

    /// <summary>
    /// One EQ band, drawn from the centre. The centre of the track is zero, with no boost and no cut. A
    /// boost grows to the right and a cut grows to the left. This is how the knob is read. So during a
    /// swap, the two decks' bass bars visibly move in opposite directions.
    /// </summary>
    private static void Knob(Row row, double db)
    {
        // Scaled to the travel of a hand on a real knob, not to the full kill range. A swap spends most
        // of its time below -26 dB. A bar scaled to -60 dB would sit at the far left for the whole swap
        // and show nothing moving. Past the hand's stop, the bar is simply full.
        double share = db >= 0
            ? db / DeckEq.MaxBoostDb
            : -Math.Min(1, db / DeckEq.MaxCutDb);

        string text = db <= DeckEq.MaxCutDb ? "kill" : $"{db:+0.0;-0.0} dB";
        row.Show(Math.Abs(share), Math.Sign(share), text, db >= 0 ? Boost : Cut);
    }

    private Row Add(string name)
    {
        var row = new Row(name);
        Children.Add(row.Panel);
        return row;
    }

    /// <summary>One labelled bar: the name, the track it moves in, and the value in words.</summary>
    private sealed class Row
    {
        private const double TrackWidth = 120, TrackHeight = 8;

        private readonly Rectangle _fill;
        private readonly TextBlock _value;
        private readonly Grid _track;

        public Grid Panel { get; }

        public Row(string name)
        {
            Panel = new Grid { ColumnSpacing = 8, VerticalAlignment = VerticalAlignment.Center };
            Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(46) });
            Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            Panel.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var label = new TextBlock
            {
                Text = name,
                FontSize = 11,
                Foreground = new SolidColorBrush(Dim),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(label, 0);
            Panel.Children.Add(label);

            _track = new Grid
            {
                Width = TrackWidth,
                Height = TrackHeight,
                Background = new SolidColorBrush(Track),
                VerticalAlignment = VerticalAlignment.Center,
            };

            _fill = new Rectangle
            {
                Height = TrackHeight,
                Width = 0,
                HorizontalAlignment = HorizontalAlignment.Left,
                Fill = new SolidColorBrush(Volume),
            };
            _track.Children.Add(_fill);

            Grid.SetColumn(_track, 1);
            Panel.Children.Add(_track);

            _value = new TextBlock
            {
                FontSize = 11,
                FontFamily = new FontFamily("Consolas"),
                Foreground = new SolidColorBrush(Dim),
                VerticalAlignment = VerticalAlignment.Center,
            };
            Grid.SetColumn(_value, 2);
            Panel.Children.Add(_value);
        }

        /// <param name="share">How much of the half-track (or whole track, when centred is 0) to fill,
        /// 0 to 1.</param>
        /// <param name="centred">0 fills from the left; -1 fills leftwards from the middle, +1
        /// rightwards from it.</param>
        public void Show(double share, int centred, string text, Color colour)
        {
            double width = centred == 0
                ? TrackWidth * Math.Clamp(share, 0, 1)
                : TrackWidth / 2 * Math.Clamp(share, 0, 1);

            _fill.Width = Math.Max(centred == 0 ? 0 : 1, width);
            _fill.Margin = centred switch
            {
                > 0 => new Thickness(TrackWidth / 2, 0, 0, 0),
                < 0 => new Thickness(TrackWidth / 2 - width, 0, 0, 0),
                _ => new Thickness(0),
            };

            ((SolidColorBrush)_fill.Fill).Color = colour;
            _value.Text = text;
        }

        public void Clear()
        {
            _fill.Width = 0;
            _value.Text = "";
        }
    }
}
#endif
#endif
