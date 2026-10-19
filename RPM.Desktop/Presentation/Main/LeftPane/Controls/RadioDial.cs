using RPM.Core.Radio;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using Windows.Foundation;
using Windows.UI;
using RPM.Desktop.Presentation.Common;

namespace RPM.Desktop.Presentation.Main.LeftPane.Controls;

/// <summary>
/// The tuning dial: a printed scale behind a lit glass, with a needle that moves across it.
/// Stations are printed from left to right. Each one is its three or four letter call sign,
/// followed by a solid square. The entries are stacked into as many rows as the scale needs.
/// The square is the station's place on the band, and the needle tunes onto it.
/// Dragging tunes. On release, the needle settles on the nearest station. It does not stop
/// between two stations.
///
/// <para>
/// The scale is a strip, not a viewport. It can be longer than the glass and slide under it.
/// The needle sits at the station's real place on the scale. The scale is never re-centred on
/// the tuned station. If it were, every station would look like it is in the middle of the band,
/// and the reader would lose the position of the needle between the names. The scale moves only
/// when the needle would otherwise be off the glass.
/// </para>
///
/// <para>Drawn in code rather than from a template. The length of the scale, the number of rows
/// of names, which names clear their neighbours and where the needle lands all depend on the
/// stations shown and on the live width of the pane. A fixed template cannot express that.
/// Everything is a <see cref="Canvas"/> child positioned in <see cref="Layout"/>. The needle and
/// the scale move on <see cref="TranslateTransform"/>s, so tuning changes two animated
/// properties and does not need a re-layout.</para>
/// </summary>
public sealed partial class RadioDial : BorderBase
{
    // The scale needs room for several rows of names above the graduations.
    private const double DialHeight = 176;
    private const double ReadoutBottom = 54; // the bottom of the readout panel; the scale is drawn below it
    private const double ScaleSpan = 16;     // how tall the graduations themselves stand
    private const double PointerRise = 13;   // how far the needle's chevron stands above them
    private const double BadgeSize = 26;     // the station logo in the readout's corner

    // Stations are printed flat and upright, with a solid square after each name. The whole list
    // is stacked into as many rows as it needs. Rows make a crowded scale readable. A name only has
    // to clear the name to its left on its own row. So a band with thirty places still spells out
    // every one of them, instead of showing only every fourth.
    private const double NameSize = 9;
    private const double NameLine = 11;      // one row of names, baseline to baseline
    private const double NameGap = 7;        // clear space between two names sharing a row

    // The square marks the station's exact position, and the needle tunes onto it. The square has
    // no lead line. A lead would carry a name across to a mark somewhere else. There is no room for
    // that when the mark sits right against the last letter.
    private const double SquareSize = 6.5;   // as tall as the name's capitals, so the two read as one
    private const double SquareGap = 4;      // between the last letter and the square

    /// <summary>What a name has to keep clear of on its row, past its own width.</summary>
    private const double SquareSlot = SquareGap + SquareSize;

    /// <summary>Most rows of names the card will print, however tall it gets. Past six rows the
    /// names become hard to read against the scale.</summary>
    private const int MaxNameRows = 6;

    /// <summary>
    /// Rough width of one character at <see cref="NameSize"/>. This is an estimate, not a
    /// measurement. Laying out the scale means asking about every station, and each measurement of
    /// a TextBlock is a layout pass.
    ///
    /// <para>
    /// The estimate is set high on purpose. It decides the width given to each name, so a low guess
    /// clips the last letter off long names. A high guess only leaves a little more space between
    /// neighbours. Capitals and the wide letter spacing put the real width near this value.
    /// </para>
    /// </summary>
    private const double NameCharWidth = NameSize * 0.7;

    /// <summary>
    /// How many pane-widths of scale the dial will print before it starts leaving call signs off again.
    ///
    /// <para>
    /// The scale can be longer than the glass and slide under it, so a crowded list keeps every call
    /// sign instead of dropping four out of five. The length has a limit. Without one, a search with
    /// a thousand stations would lay out a thousand text blocks across seventeen thousand pixels just
    /// to show twenty of them. Past this limit the dial leaves some call signs off. A scale that
    /// nobody can drag to the end is not more readable than a sparse one.
    /// </para>
    /// </summary>
    private const double MaxScaleScreens = 6;

    private static readonly Color Cyan = Color.FromArgb(0xFF, 0x04, 0xC8, 0xFF);
    private static readonly Color Amber = Color.FromArgb(0xFF, 0xFF, 0xA8, 0x2B);

    // Shared brushes. A scale can have a hundred graduations. Creating a brush for each tick on
    // every repaint would mean a hundred allocations each time the needle moves.
    private static readonly SolidColorBrush _scaleInk = new(Color.FromArgb(0xFF, 0xC8, 0xEE, 0xFF));
    private static readonly SolidColorBrush _labelInk = new(Color.FromArgb(0xFF, 0xDD, 0xF2, 0xFF));
    private static readonly SolidColorBrush _markInk = new(Color.FromArgb(0xFF, 0xA8, 0xD4, 0xEE));
    private static readonly SolidColorBrush _litInk = new(Cyan);

    private readonly Canvas _canvas = new();
    private readonly Rectangle _needle;
    private readonly Rectangle _needleGlow;
    private readonly Polyline _pointer;
    private readonly TextBlock _readout;
    private readonly TextBlock _subtitle;
    private readonly Border _badge;

    /// <summary>The space given to a mark inside the badge. A picture gets none. It is its own
    /// background, and the corner radius of the badge rounds it.</summary>
    private const double BadgePad = 2;
    private readonly Image _logo;

    /// <summary>The background behind the mark: the generated figure that the rows also draw behind
    /// the same mark. It is a separate layer under <see cref="_logo"/>. It is not inset with the
    /// mark, because it is the background.</summary>
    private readonly Image _logoGround;

    /// <summary>Holds the mark alone, and carries the inset. The badge itself cannot, because its
    /// padding would also push the background off the edges. A background held off the edges looks
    /// like a panel.</summary>
    private readonly Border _logoHost;
    private readonly TranslateTransform _slide = new();

    /// <summary>
    /// The graduations and their call signs, as one layer. It can be wider than the glass and slide
    /// under it. The readout panel and the needle stay on the glass. Only the printed scale moves.
    /// The needle does not follow the scale off the end of the glass.
    /// </summary>
    private readonly Canvas _scaleLayer;
    private readonly TranslateTransform _scroll = new();

    private readonly List<Rectangle> _ticks = [];
    private readonly List<TextBlock> _labels = [];

    /// <summary>The solid square that marks each printed station's place on the band. This is the
    /// mark the needle tunes onto. It is kept in step with <see cref="_labels"/>.</summary>
    private readonly List<Rectangle> _squares = [];

    /// <summary>A station whose name is printed: the station index, and the row it is on. Not every
    /// station has a printed name. A name that cannot clear its neighbours on any row keeps its
    /// graduation but has no caption.</summary>
    private readonly record struct Placed(int Station, int Row);

    private readonly List<Placed> _placed = [];

    /// <summary>How many rows of names are printed. This sets where the scale sits, because the
    /// whole block is centred in the room under the readout.</summary>
    private int _nameRows;

    private IReadOnlyList<RadioStation> _stations = [];
    private int _index = -1;
    private bool _dragging;
    private double _width;
    private bool _perStation;
    private int _tickCount;

    // The positions of the graduations. These are computed, not fixed. The needle, the scale and
    // the call signs are centred as one block in the room under the readout panel. So the room the
    // call signs take decides where the scale sits.
    private double _namesTop;
    private double _scaleTop;
    private double _scaleBottom;

    /// <summary>How wide the printed scale is. It equals the glass width until the call signs need
    /// more room. Then it is wider, and the scale can move.</summary>
    private double _scaleWidth;

    public RadioDial()
    {
        Height = DialHeight;
        CornerRadius = new CornerRadius(10);
        BorderThickness = new Thickness(1);
        BorderBrush = new SolidColorBrush(Color.FromArgb(0x22, 0xFF, 0xFF, 0xFF));

        // The glass: a dark pane, lit from behind and brighter along the scale line. Two gradient
        // stops, not one flat fill, stop it looking like another card.
        Background = new LinearGradientBrush
        {
            StartPoint = new Point(0, 0),
            EndPoint = new Point(0, 1),
            GradientStops =
            {
                new GradientStop { Color = Color.FromArgb(0x38, 0x00, 0x14, 0x22), Offset = 0 },
                new GradientStop { Color = Color.FromArgb(0x30, 0x04, 0x50, 0x70), Offset = 0.55 },
                new GradientStop { Color = Color.FromArgb(0x44, 0x00, 0x0A, 0x14), Offset = 1 },
            },
        };

        _readout = new TextBlock
        {
            FontSize = 14,
            FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
            Foreground = new SolidColorBrush(Color.FromArgb(0xF2, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };
        _subtitle = new TextBlock
        {
            FontSize = 11.5,
            Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
            TextTrimming = TextTrimming.CharacterEllipsis,
        };

        // The station's own logo, in the top left corner of the glass. It is shown only when the
        // station has a logo that can be drawn. The readout moves across to make room, so there is
        // no empty gap for stations that have no logo.
        _logo = new Image { Stretch = Stretch.Uniform };
        // If the logo link does not load, the badge would be an empty box on the glass. So the badge
        // is hidden and the station's name gets the room back.
        _logo.ImageFailed += (_, _) => SetLogo(null, null, fills: false);

        // The generated figure fills the badge under the mark, and the corner radius clips it. The
        // rows draw the same arrangement: the ground first, then the mark held off the edges. So the
        // badge on the glass and the badge in a row look the same.
        _logoGround = new Image { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
        _logoHost = new Border { Padding = new Thickness(BadgePad), Child = _logo };

        // The badge has no padding of its own. The padding is on _logoHost instead. Padding on the
        // badge would also inset the ground, and a background with a ring of glass around it looks
        // like a panel.
        _badge = new Border
        {
            Width = BadgeSize,
            Height = BadgeSize,
            CornerRadius = new CornerRadius(5),
            Background = new SolidColorBrush(Color.FromArgb(0x26, 0x00, 0x00, 0x00)),
            BorderBrush = new SolidColorBrush(Color.FromArgb(0x1A, 0xFF, 0xFF, 0xFF)),
            BorderThickness = new Thickness(1),
            Visibility = Visibility.Collapsed,
            IsHitTestVisible = false,
            Child = new Grid
            {
                Children =
                {
                    new Border { CornerRadius = new CornerRadius(5), Child = _logoGround },
                    _logoHost,
                },
            },
        };

        // Needle: a thin line, with a wider, dimmer copy behind it that stands for the light spilling
        // from the lamp. A chevron at the head makes it point, not just mark a place.
        _needleGlow = new Rectangle { Width = 9, Fill = new SolidColorBrush(Color.FromArgb(0x33, 0x04, 0xC8, 0xFF)), RadiusX = 4, RadiusY = 4 };
        _needle = new Rectangle { Width = 2, Fill = new SolidColorBrush(Cyan), RadiusX = 1, RadiusY = 1 };
        // Use a Polyline in a Canvas, not a Path. In Uno a Path tries to stretch to its parent, and
        // then it is placed in the wrong position inside a Canvas.
        _pointer = new Polyline
        {
            Points = { new Point(-5, 0), new Point(0, 7), new Point(5, 0) },
            Stroke = new SolidColorBrush(Cyan),
            StrokeThickness = 2,
            StrokeLineJoin = PenLineJoin.Round,
        };

        foreach (var element in new UIElement[] { _needleGlow, _needle, _pointer })
            element.RenderTransform = _slide;

        // The scale is drawn under everything. It has its own canvas, so one transform moves every
        // graduation and call sign together. The canvas also keeps the ticks below the needle.
        _scaleLayer = new Canvas { IsHitTestVisible = false, RenderTransform = _scroll };

        _canvas.Children.Add(_scaleLayer);
        _canvas.Children.Add(_badge);
        _canvas.Children.Add(_readout);
        _canvas.Children.Add(_subtitle);
        _canvas.Children.Add(_needleGlow);
        _canvas.Children.Add(_needle);
        _canvas.Children.Add(_pointer);
        Child = _canvas;

        SizeChanged += (_, e) => { _width = e.NewSize.Width; Layout(); };
        PointerPressed += OnPointerPressed;
        PointerMoved += OnPointerMoved;
        PointerReleased += OnPointerReleased;
        PointerCaptureLost += OnPointerReleased;
        PointerWheelChanged += OnPointerWheel;
        Unloaded += (_, _) => StopGlide();   // a rendering handler outliving the dial that owns it
    }

    /// <summary>Raised when the dial settles on a station: after a click or the end of a drag.</summary>
    public event Action<RadioStation>? Tuned;

    /// <summary>The station under the needle, or null when the dial is empty.
    ///
    /// <para>Internal, not public. This is required for the WinUI head. XAML registers every PUBLIC
    /// property of a control it can place. Registering this one would add RadioStation to the
    /// generated type table. The generator then writes <c>new RadioStation()</c>, which does not
    /// compile, because that record has required members. Nothing outside this assembly reads it.</para>
    /// </summary>
    internal RadioStation? Selected => _index >= 0 && _index < _stations.Count ? _stations[_index] : null;

    /// <summary>The stations the dial can tune to. Tuning is limited to these, so the pane passes
    /// whichever list is on screen: the presets or the search results.</summary>
    public void SetStations(IReadOnlyList<RadioStation> stations)
    {
        stations ??= [];

        // The pane calls this every time the live title changes, and it builds a new list each time.
        // So most calls pass the stations that are already on the card. A new layout would cost a full
        // pass over every name, and it would undo where the user had moved the scale.
        if (SameStations(stations))
            return;

        _stations = stations;
        if (_index >= _stations.Count)
            _index = _stations.Count - 1;

        // A different list starts at its left edge, not where the scale was moved to for the last
        // list.
        _scroll.X = 0;
        Layout();
    }

    private bool SameStations(IReadOnlyList<RadioStation> stations)
    {
        if (stations.Count != _stations.Count)
            return false;
        for (int i = 0; i < stations.Count; i++)
        {
            if (!string.Equals(stations[i].Uuid, _stations[i].Uuid, StringComparison.OrdinalIgnoreCase))
                return false;
        }
        return true;
    }

    /// <summary>Points the needle at a station by id, without raising <see cref="Tuned"/>. Used when
    /// a station is tuned from the list rather than from the dial.</summary>
    public void PointAt(string? uuid)
    {
        int at = -1;
        for (int i = 0; i < _stations.Count && uuid is not null; i++)
        {
            if (string.Equals(_stations[i].Uuid, uuid, StringComparison.OrdinalIgnoreCase))
            {
                at = i;
                break;
            }
        }
        if (at == _index)
            return;
        _index = at;
        // This is the only case that may move the scale. The user tuned from the list, so the needle
        // must be somewhere they can see it.
        MoveNeedle(animate: true, reveal: true);
        UpdateReadout();
    }

    /// <summary>The line under the station's name: the live ICY title, "reconnecting", or nothing.
    /// It is kept apart from the station, so a title change only sets one text value.</summary>
    public void SetStatus([Shown] string? text)
    {
        var status = text ?? "";
        if (_subtitle.Text != status)
            _subtitle.Text = status;
    }

    /// <summary>The station's logo, or null to show the name alone. Many stations publish a logo, but
    /// many logo links are broken, so both cases are normal.</summary>
    /// <param name="ground">What a mark with nothing behind it stands on: the generated figure from
    /// the station's own <c>FoundLogo</c>. Null for a logo that is already a solid picture, because
    /// it is its own background. Also null when there is no logo at all.</param>
    /// <param name="fills">True when the logo is a picture with its own background. It fills the badge,
    /// and the corner radius of the badge clips it. False when the logo is a mark with nothing behind
    /// it. Then it is held off the edges. A logo that floats inside its badge, with a ring of glass
    /// around it, looks like a badge that failed to load. A logo that runs to the corners looks like
    /// the station's own. Rows use the same rule. See RadioStationItem.LogoStretch.</param>
    /// <remarks>All three values are given at every call, with no defaults. They describe one station.
    /// A caller that passed only some of them would put a new station's mark on the previous
    /// station's ground.</remarks>
    public void SetLogo(ImageSource? logo, ImageSource? ground, bool fills)
    {
        bool show = logo is not null;
        _logo.Source = logo;
        _logo.Stretch = fills ? Stretch.UniformToFill : Stretch.Uniform;
        _logoGround.Source = ground;
        _logoHost.Padding = new Thickness(fills ? 0 : BadgePad);
        var visibility = show ? Visibility.Visible : Visibility.Collapsed;
        if (_badge.Visibility == visibility)
            return;
        _badge.Visibility = visibility;
        Layout();                            // the readout starts further in when the badge shows
    }

    /// <summary>Amber needle while the station cannot be reached. Colour is the only signal for
    /// this state.</summary>
    public void SetReconnecting(bool reconnecting)
    {
        var color = reconnecting ? Amber : Cyan;
        _needle.Fill = new SolidColorBrush(color);
        _pointer.Stroke = new SolidColorBrush(color);
        _needleGlow.Fill = new SolidColorBrush(Color.FromArgb(0x33, color.R, color.G, color.B));
    }

    /// <summary>
    /// Rebuilds the scale for the current width: a tick per station where they are far enough apart
    /// to tell one from another, otherwise an even ruler that reads as a scale without pretending
    /// each line is a station.
    /// </summary>
    private void Layout()
    {
        if (_width <= 0)
            return;

        // The scale can be longer than the glass, so the glass must act as a window. A Border does not
        // clip its child here. Without this clip, the graduations would show outside the dial on both
        // sides.
        _canvas.Clip = new RectangleGeometry { Rect = new Rect(0, 0, _width, DialHeight) };

        // The badge sits at the left edge of the readout. The readout lines start to its right when
        // the badge is shown. With no logo, the text starts at the normal left position.
        bool badge = _badge.Visibility == Visibility.Visible;
        double textLeft = badge ? 12 + BadgeSize + 8 : 12;
        Canvas.SetLeft(_badge, 12);
        Canvas.SetTop(_badge, 8);

        // Both readout lines sit at the top, one under the other. So the second line does not land on
        // the station names on the scale.
        _readout.Width = Math.Max(0, _width - textLeft - 12);
        _subtitle.Width = Math.Max(0, _width - textLeft - 12);
        Canvas.SetLeft(_readout, textLeft);
        Canvas.SetLeft(_subtitle, textLeft);
        PlaceReadoutLines();

        int count = _stations.Count;

        // The number of rows that fit. This is calculated, not fixed. The scale is centred in the room
        // that the readout leaves, so the row count follows the height of the control.
        double room = Math.Max(0, DialHeight - 8 - ReadoutBottom);
        int maxRows = Math.Clamp((int)((room - PointerRise - ScaleSpan) / NameLine), 1, MaxNameRows);

        // Each caption is built once. The width total, the row packing and the placement all use the
        // same strings and widths.
        var captions = new string[count];
        var widths = new double[count];

        // The length the scale needs. A name printed flat takes its own width, plus the square and a
        // gap. Stacked into rows, the scale needs only a fraction of that total. The length comes from
        // the total width of the names, not from a fixed pitch. So six short names fit the glass, and a
        // hundred long names make the scale grow, with no hand tuning.
        double ink = 0;
        for (int i = 0; i < count; i++)
        {
            captions[i] = Caption(i);
            widths[i] = NameWidth(captions[i]);
            ink += widths[i] + SquareSlot + NameGap;
        }

        double wanted = ink / maxRows + 32;
        _scaleWidth = Math.Clamp(wanted, _width, Math.Max(_width, _width * MaxScaleScreens));

        double pitch = count > 0 ? ScaleInset / count : 0;
        _perStation = count > 0 && pitch >= 4;
        _tickCount = _perStation ? count : Math.Max(8, (int)(ScaleInset / 11));

        // Lay the entries out in rows, from left to right. An entry is a name followed by its square.
        // The square sits on the station's position, so the name is to the left of that position. Each
        // entry goes on the topmost row whose last entry it clears. An entry that clears no row keeps
        // its graduation but has no caption. That is better than printing it over a neighbour.
        _placed.Clear();
        _nameRows = 0;
        if (_perStation)
        {
            Span<double> rowEnd = stackalloc double[maxRows];
            rowEnd.Fill(double.NegativeInfinity);

            for (int i = 0; i < count; i++)
            {
                double mark = At(Fraction(i, count));
                double left = mark - SquareSize / 2 - SquareGap - widths[i];

                for (int row = 0; row < rowEnd.Length; row++)
                {
                    if (left < rowEnd[row])
                        continue;
                    rowEnd[row] = mark + SquareSize / 2 + NameGap;
                    _placed.Add(new Placed(i, row));
                    _nameRows = Math.Max(_nameRows, row + 1);
                    break;
                }
            }
        }

        // Centre the needle head, the rows of names and the graduations as one block, in the room
        // below the readout. A fixed top position would leave the lower half of the dial empty.
        double names = _nameRows * NameLine;
        double block = PointerRise + names + ScaleSpan;
        double top = ReadoutBottom + Math.Max(0, (room - block) / 2) + PointerRise;

        _namesTop = top;
        _scaleTop = top + names;
        _scaleBottom = _scaleTop + ScaleSpan;

        // The needle is one thin line down the whole scale. It is not a short mark beside the
        // graduations.
        double needleTop = _namesTop - 3;
        double needleHeight = _scaleBottom - needleTop + 2;
        _needleGlow.Height = needleHeight + 4;
        Canvas.SetTop(_needleGlow, needleTop - 2);
        Canvas.SetLeft(_needleGlow, -4.5);
        _needle.Height = needleHeight;
        Canvas.SetTop(_needle, needleTop);
        Canvas.SetLeft(_needle, -1);
        Canvas.SetTop(_pointer, needleTop - PointerRise);
        Canvas.SetLeft(_pointer, 0);

        EnsureCount(_ticks, _tickCount, MakeTick);
        EnsureCount(_labels, _placed.Count, MakeLabel);
        EnsureCount(_squares, _placed.Count, MakeSquare);

        for (int i = 0; i < _tickCount; i++)
        {
            var tick = _ticks[i];
            Canvas.SetLeft(tick, At(_perStation ? Fraction(i, _tickCount) : (i + 0.5) / _tickCount) - 0.5);
        }

        // Use _placed.Count, not _labels.Count. The label list is a reuse pool that only grows. After
        // a search that returns fewer stations, the pool still holds extra labels. Looping over the
        // pool would index past the end of the station list.
        for (int i = 0; i < _placed.Count; i++)
        {
            var (station, row) = _placed[i];
            double markX = At(Fraction(station, count));
            double width = widths[station];
            double rowTop = _namesTop + row * NameLine;

            // The square is centred on the station's position, so the needle crosses its middle when
            // tuned. The name is placed directly to its left, touching the square.
            var square = _squares[i];
            Canvas.SetLeft(square, markX - SquareSize / 2);
            Canvas.SetTop(square, rowTop + (NameLine - SquareSize) / 2);

            var label = _labels[i];
            label.Text = captions[station];
            label.Width = width;
            Canvas.SetLeft(label, markX - SquareSize / 2 - SquareGap - width);
            Canvas.SetTop(label, rowTop);
        }

        MoveNeedle(animate: false);
        UpdateReadout();
    }

    /// <summary>The letters printed on the band for a station: its call sign, three or four letters.
    /// They are built from the printed name, so the mark on the dial and the name in the list use the
    /// same words. See RadioStation.Display. The full name is in the readout above, so the scale does
    /// not have to carry it.</summary>
    private string Caption(int station) => StationSign.For(_stations[station].Display);

    private static double NameWidth(string caption) => Math.Max(8, caption.Length * NameCharWidth);

    /// <summary>Usable width of the scale. The needle never reaches the glass edge.</summary>
    private static double Inset(double width) => Math.Max(1, width - 32);

    /// <summary>Usable width of the printed scale. It is the glass width or wider.</summary>
    private double ScaleInset => Inset(_scaleWidth);

    /// <summary>How far the scale can move. It is zero when the scale fits the glass, and the code
    /// below then works with no special case.</summary>
    private double ScrollRange => Math.Max(0, _scaleWidth - _width);

    /// <summary>Where station <paramref name="i"/> of <paramref name="count"/> sits, 0..1. It is
    /// centred in its own slice. So the first and last stations sit inside the scale.</summary>
    private static double Fraction(int i, int count) => count <= 0 ? 0.5 : (i + 0.5) / count;

    /// <summary>Converts a fraction to an x position on the printed scale. This is not an x position
    /// on the glass when the scale is longer than the glass.</summary>
    private double At(double fraction) => 16 + Math.Clamp(fraction, 0, 1) * ScaleInset;

    /// <summary>
    /// How far the scale must move so that <paramref name="scaleX"/> sits at
    /// <paramref name="glassX"/>, within the range the scale can move. The clamp stops the scale
    /// from being dragged past its ends, which would leave blank glass.
    /// </summary>
    private double ScrollFor(double scaleX, double glassX) =>
        Math.Clamp(glassX - scaleX, -ScrollRange, 0);

    /// <summary>How close to the glass edge the needle may get before the scale moves.</summary>
    private const double ScrollMargin = 56;

    /// <summary>
    /// Where the scale should sit for <paramref name="scaleX"/> to be visible. This is usually
    /// exactly where it already is.
    ///
    /// <para>
    /// The scale is not re-centred on the tuned station. The needle moves along the scale, and its
    /// position between the names on each side is what the reader sees. Re-centring would hide that,
    /// because every station would look like it is in the middle of the band. So the scale moves only
    /// when the needle would be off the glass, and then only as far as needed to bring it back.
    /// </para>
    /// </summary>
    private double ScrollToShow(double scaleX)
    {
        double current = Math.Clamp(_scroll.X, -ScrollRange, 0);
        if (ScrollRange <= 0)
            return 0;

        double margin = Math.Min(ScrollMargin, _width / 3);
        double onGlass = scaleX + current;

        if (onGlass < margin)
            return ScrollFor(scaleX, margin);
        if (onGlass > _width - margin)
            return ScrollFor(scaleX, _width - margin);

        return current;
    }

    /// <param name="reveal">
    /// Whether the scale may move to bring the needle onto the glass. Only an explicit tune does
    /// this. Everything else leaves the scale where the user left it, including a re-layout. A
    /// re-layout happens every time the live title changes. If it revealed the needle, a scale that
    /// the user had moved away from the tuned station would jump back.
    /// </param>
    private void MoveNeedle(bool animate, bool reveal = false)
    {
        // Two positions are needed: where the station sits on the printed scale, and where that lands
        // on the glass after the scale has moved.
        double scaleX = _index >= 0 ? At(Fraction(_index, _stations.Count)) : At(0);
        double scroll = reveal ? ScrollToShow(scaleX) : Math.Clamp(_scroll.X, -ScrollRange, 0);
        double glassX = scaleX + scroll;

        if (!animate || (Math.Abs(_slide.X - glassX) < 0.5 && Math.Abs(_scroll.X - scroll) < 0.5))
        {
            Place(glassX, scroll);
            return;
        }

        // The move is eased and short, so the needle does not jump.
        _glideFromSlide = _slide.X;
        _glideFromScroll = _scroll.X;
        _glideToSlide = glassX;
        _glideToScroll = scroll;

        if (!_gliding)
        {
            _gliding = true;
            CompositionTarget.Rendering += OnGlideTick;
        }
        _glideClock.Restart();
    }

    // ── the needle's travel ──
    //
    // This is a per-frame tween, not a Storyboard. A DoubleAnimation keeps its property after it
    // ends, and a held animation overrides direct writes. So the mouse wheel could move the scale and
    // the needle, and the animation left over from the last tune would move them both back. Nothing
    // here holds a value. The tick sets the values while it runs, and releases them when it stops.

    private const double GlideMs = 260;

    private readonly System.Diagnostics.Stopwatch _glideClock = new();
    private bool _gliding;
    private double _glideFromSlide, _glideFromScroll, _glideToSlide, _glideToScroll;

    private void OnGlideTick(object? sender, object e)
    {
        double t = Math.Clamp(_glideClock.Elapsed.TotalMilliseconds / GlideMs, 0, 1);
        double eased = 1 - Math.Pow(1 - t, 3);   // cubic ease-out

        Place(
            _glideFromSlide + (_glideToSlide - _glideFromSlide) * eased,
            _glideFromScroll + (_glideToScroll - _glideFromScroll) * eased);

        if (t >= 1)
            StopGlide();
    }

    /// <summary>Ends any travel in progress. The scale and the needle stay where they are now. Every
    /// direct move calls this first, so nothing else changes a property that the tick sets.</summary>
    private void StopGlide()
    {
        if (!_gliding)
            return;
        _gliding = false;
        CompositionTarget.Rendering -= OnGlideTick;
        _glideClock.Stop();
    }

    private void Place(double glassX, double scroll)
    {
        _slide.X = glassX;
        _scroll.X = scroll;
    }

    private void UpdateReadout()
    {
        // The printed name, the same as the rows print it. The name from the stream's own
        // specification is not used. See RadioStation.Display.
        var name = Selected?.Display ?? "";
        if (_readout.Text != name)
        {
            _readout.Text = name;
            PlaceReadoutLines();
        }
        PaintScale();
    }

    /// <summary>
    /// Places the two readout lines at the top of the glass.
    ///
    /// <para>
    /// With a station tuned there are two lines: the name, and under it the connection status. With
    /// no station there is only the status. If the status stayed on the second line, text such as
    /// "Not tuned in" would sit under an empty first line. So when the name is empty, the status
    /// moves to the top line.
    /// </para>
    /// </summary>
    private void PlaceReadoutLines()
    {
        const double FirstLine = 11;
        const double SecondLine = 32;

        bool named = _readout.Text is { Length: > 0 };
        _readout.Visibility = named ? Visibility.Visible : Visibility.Collapsed;

        Canvas.SetTop(_readout, FirstLine);
        Canvas.SetTop(_subtitle, named ? SecondLine : FirstLine);
    }

    /// <summary>
    /// Paints the graduations and the names for the station under the needle. This is separate from
    /// <see cref="Layout"/>. Tuning changes which line is lit, but not where any line is. A full
    /// re-layout to move a highlight would also make the needle jump during its travel.
    /// </summary>
    private void PaintScale()
    {
        for (int i = 0; i < _tickCount && i < _ticks.Count; i++)
        {
            var tick = _ticks[i];
            bool tuned = _perStation && i == _index;
            // Every fifth line is a major graduation. This makes the scale look measured, not striped.
            bool major = i % 5 == 0;

            // Set as a share of the scale height. The graduations keep their size relative to each
            // other if the scale height changes.
            tick.Height = ScaleSpan * (tuned ? 1 : major ? 0.62 : 0.36);
            tick.Opacity = tuned ? 1 : major ? 0.55 : 0.28;
            tick.Fill = tuned ? _litInk : _scaleInk;
            Canvas.SetTop(tick, _scaleBottom - tick.Height);
        }

        // The whole entry of the tuned station lights up: its name and the square under the needle.
        // If only one of them lit, the reader would have to work out which of the six rows holds
        // the needle.
        for (int i = 0; i < _placed.Count && i < _labels.Count; i++)
        {
            bool tuned = _placed[i].Station == _index;

            _labels[i].Opacity = tuned ? 1 : 0.62;
            _labels[i].Foreground = tuned ? _litInk : _labelInk;

            _squares[i].Opacity = tuned ? 1 : 0.7;
            _squares[i].Fill = tuned ? _litInk : _markInk;
        }
    }

    // ── Dragging ──
    // The needle follows the pointer during a drag. Nothing is tuned until the pointer is released.
    // Otherwise a drag across forty stations would open forty connections.

    private void OnPointerPressed(object sender, PointerRoutedEventArgs e)
    {
        if (_stations.Count == 0)
            return;
        StopGlide();                        // the pointer takes over from any move still in progress
        _dragging = true;
        CapturePointer(e.Pointer);
        DragTo(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    private void OnPointerMoved(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        DragTo(e.GetCurrentPoint(this).Position.X);
        e.Handled = true;
    }

    /// <summary>The mouse wheel moves the scale without changing the tuned station. Reading the rest
    /// of a long dial should not retune the radio at each step.</summary>
    private void OnPointerWheel(object sender, PointerRoutedEventArgs e)
    {
        if (ScrollRange <= 0)
            return;

        // Stop any move from the last tune, so that it cannot finish on top of this one.
        StopGlide();

        int delta = e.GetCurrentPoint(this).Properties.MouseWheelDelta;
        double scroll = Math.Clamp(_scroll.X + delta * 0.4, -ScrollRange, 0);

        // The needle moves with the scale. It may move off the glass and out of sight if the scale has
        // moved that far.
        double scaleX = _index >= 0 ? At(Fraction(_index, _stations.Count)) : At(0);
        Place(scaleX + scroll, scroll);

        e.Handled = true;
    }

    private void OnPointerReleased(object sender, PointerRoutedEventArgs e)
    {
        if (!_dragging)
            return;
        _dragging = false;
        ReleasePointerCaptures();
        MoveNeedle(animate: true);          // settle onto the nearest station
        if (Selected is { } station)
            Tuned?.Invoke(station);
    }

    /// <summary>
    /// Puts the needle where the pointer is on the scale, and reads out the station that is there.
    ///
    /// <para>
    /// The needle maps one to one onto the scale, not onto the list. The place on the printed scale
    /// is the place it means. So a pointer between two names puts the needle between those names. If
    /// the glass mapped to the whole list instead, each drag would act like a scrub bar with the scale
    /// printed below it.
    /// </para>
    ///
    /// <para>
    /// The rest of a long scale is reached by holding the pointer near either edge. The scale then
    /// moves along, as it does when a drag goes past the end of a list.
    /// </para>
    /// </summary>
    private void DragTo(double x)
    {
        WindAtEdge(x);

        double fraction = Math.Clamp((x - _scroll.X - 16) / ScaleInset, 0, 1);
        int at = Math.Clamp((int)(fraction * _stations.Count), 0, _stations.Count - 1);
        if (at != _index)
        {
            _index = at;
            UpdateReadout();
        }

        Place(Math.Clamp(x, 0, _width), _scroll.X);   // under the pointer, not on the nearest station
    }

    /// <summary>A drag that reaches the edge of the glass moves the scale along. A scale that is longer
    /// than the window can still be crossed in one drag.</summary>
    private void WindAtEdge(double x)
    {
        if (ScrollRange <= 0)
            return;

        double margin = Math.Min(ScrollMargin, _width / 4);
        double over = x < margin ? margin - x
            : x > _width - margin ? _width - margin - x
            : 0;

        if (over != 0)
            _scroll.X = Math.Clamp(_scroll.X + over * 0.25, -ScrollRange, 0);
    }

    private static void EnsureCount<T>(List<T> pool, int wanted, Func<T> make) where T : FrameworkElement
    {
        while (pool.Count < wanted) pool.Add(make());
        for (int i = 0; i < pool.Count; i++)
            pool[i].Visibility = i < wanted ? Visibility.Visible : Visibility.Collapsed;
    }

    private Rectangle MakeTick()
    {
        var tick = new Rectangle
        {
            Width = 1,
            Fill = _scaleInk,
            IsHitTestVisible = false,
        };
        _scaleLayer.Children.Add(tick);
        return tick;
    }

    private TextBlock MakeLabel()
    {
        var label = new TextBlock
        {
            FontSize = NameSize,
            LineHeight = NameLine,
            // Right-aligned, so the last letter of the name stays next to its square, whatever the
            // length of the name. Left-aligned, the gap would change as the name got shorter.
            TextAlignment = TextAlignment.Right,
            TextTrimming = TextTrimming.Clip,
            // Wide letter spacing, so the name stays readable at this small size.
            CharacterSpacing = 60,
            Foreground = _labelInk,
            IsHitTestVisible = false,
        };
        _scaleLayer.Children.Add(label);
        return label;
    }

    /// <summary>
    /// The mark itself: a solid square, as tall as the name's capitals, so the two read as one entry.
    /// This is a Rectangle, not a glyph. The needle must land on it exactly, and the size of a font's
    /// square depends on the font.
    /// </summary>
    private Rectangle MakeSquare()
    {
        var square = new Rectangle
        {
            Width = SquareSize,
            Height = SquareSize,
            Fill = _markInk,
            IsHitTestVisible = false,
        };
        _scaleLayer.Children.Add(square);
        return square;
    }
}
