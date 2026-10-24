using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using RPM.Core.Radio;
using RPM.Core.Viz;
using RPM.Desktop.Presentation.Main.LeftPane.Models;
using Windows.UI;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// One station as Pegasus's radio screen draws it, and what is known about it so far: the colours of its
/// room, its mark, the line saying what it is, and what it is playing. Each part is filled in by a
/// background task when its answer arrives (see PegasusChrome.Radio, LoadFaceAsync).
///
/// <para><b>Answers are written to the face, never to a host.</b> A banner host is handed another station
/// at each step. So an answer about the station that the host showed a moment ago must be stored on that
/// station's face, not on the host. The host reads the face whenever it paints. The radio card on home's
/// banner follows the same rule, because its host is also handed a new station at each step.</para>
/// </summary>
internal sealed class StationFace
{
    public StationFace(RadioStation station)
    {
        Station = station;

        // The room's colours until the station's own mark sets them (see LoadFaceAsync). The colour is taken
        // from the palette's hue ring by the station's name, the same way home's radio card does. So a
        // station has the same colour on every machine for the seconds before its mark arrives.
        Room = CardRoomArt.Palette(SoftFigure.RingColours(station.Display) is [var first, ..]
            ? first
            : Color.FromArgb(0xCC, 0xFF, 0xFF, 0xFF));
    }

    public RadioStation Station { get; }

    /// <summary>The colours its room is painted in.</summary>
    public RoomPalette Room { get; private set; }

    /// <summary>Its mark, with a ground made under it where it needs one. It has its default value until the
    /// lookup answers.</summary>
    public RadioStationItem.FoundLogo Logo { get; private set; }

    /// <summary>The lookup found no mark. The antenna is shown from then on.</summary>
    public bool LogoMissing { get; private set; }

    /// <summary>What the station says it is, or what Wikipedia says about it. Null for most
    /// stations.</summary>
    public string? Blurb { get; private set; }

    /// <summary>What the station announces while it is the tuned one. The tuner keeps it current (see
    /// PegasusChrome.ShowWhatRadioPlays).</summary>
    public string? Playing { get; private set; }

    /// <summary>What a peek found it playing while it is not tuned (see PegasusChrome.AskRadioOnAir).</summary>
    public string? OnAir { get; private set; }

    /// <summary>The line under the name: the live title while it is tuned, otherwise the peeked one.</summary>
    public string? NowPlaying => (Playing ?? OnAir) is { Length: > 0 } line
        ? Strings.Loc.Format("Radio_NowPlayingLine", line)
        : null;

    /// <summary>The pixel edge its mark is decoded at: the largest size that any surface showing it draws it
    /// at.</summary>
    public int Edge { get; set; }

    /// <summary>Set before the first await of the face's lookup, so a face asks only once.</summary>
    public bool FaceAsked { get; set; }

    /// <summary>The same, for the line under the name.</summary>
    public bool LineAsked { get; set; }

    /// <summary>Raised on the UI thread whenever anything above changes.</summary>
    public event Action? Changed;

    public void ShowRoom(RoomPalette room)
    {
        Room = room;
        Changed?.Invoke();
    }

    public void ShowLogo(RadioStationItem.FoundLogo logo)
    {
        Logo = logo;
        LogoMissing = logo.Picture is null;
        Changed?.Invoke();
    }

    public void ShowBlurb(string blurb)
    {
        Blurb = blurb;
        Changed?.Invoke();
    }

    /// <summary>The live title, or null where the station is not tuned or announces nothing. Nothing is
    /// written when the value has not changed, so a pass over every face reports only what moved.</summary>
    public void ShowPlaying(string? playing)
    {
        // The peek is dropped as soon as the station is the tuned one. The live title is the same fact, and it
        // is current. A stale peek would come back as soon as the live title went quiet between songs.
        var onAir = playing is null ? OnAir : null;
        if (playing == Playing && onAir == OnAir)
            return;
        Playing = playing;
        OnAir = onAir;
        Changed?.Invoke();
    }

    public void ShowOnAir(string? onAir)
    {
        if (onAir == OnAir)
            return;
        OnAir = onAir;
        Changed?.Invoke();
    }

    /// <summary>The line under the name in a row: where it broadcasts from and how it is served. The main
    /// window's radio pane shows the same line (RadioStationItem.Detail).</summary>
    public string Detail => new RadioStationItem(Station).Detail;
}

/// <summary>
/// A station's logo in a rounded well, drawn the way the main window's radio pane draws one. The mark sits
/// on a ground made for it where it has nothing behind it. A square picture fills the well. Where there is
/// no logo, the antenna is shown. The same rules as RadioView's row (see RadioStationItem.LogoStretch and
/// LogoPad) apply, drawn at any size.
/// </summary>
internal sealed class StationWell
{
    private readonly Border _base = new()
    {
        Background = Ink(0x22000000),
        BorderBrush = Ink(0x18FFFFFF),
        BorderThickness = new Thickness(1),
    };

    private readonly Border _groundClip = new();
    private readonly Image _ground = new() { Stretch = Stretch.UniformToFill, IsHitTestVisible = false };
    private readonly Border _logoHost = new();
    private readonly Image _logo = new() { IsHitTestVisible = false };

    private readonly TextBlock _glyph = new()
    {
        Text = "",   // the antenna, shown for a station with no logo, as the radio pane's rows show it
        Foreground = Ink(0x44FFFFFF),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private bool _fills;
    private double _edge;

    public StationWell(FontFamily icons)
    {
        _glyph.FontFamily = icons;
        _groundClip.Child = _ground;
        _logoHost.Child = _logo;
        Root.Children.Add(_base);
        Root.Children.Add(_groundClip);
        Root.Children.Add(_logoHost);
        Root.Children.Add(_glyph);
        Show(default);
    }

    public Grid Root { get; } = new() { IsHitTestVisible = false };

    /// <summary>The well's side and corner radius, in points. The corner and the inset are set in proportion to
    /// the 51 point size of the radio row (a corner of 6 and an inset of 4 at that size).</summary>
    public void Size(double edge)
    {
        _edge = edge;
        Root.Width = Root.Height = edge;
        var corner = new CornerRadius(edge * 6 / 51);
        _base.CornerRadius = _groundClip.CornerRadius = _logoHost.CornerRadius = corner;
        _glyph.FontSize = edge * 20 / 51;
        Fit();
    }

    /// <summary>Shows a station's logo, or the antenna where it has none.</summary>
    public void Show(RadioStationItem.FoundLogo found) => Show(found.Picture, found.Ground, found.Fills);

    /// <summary>The same, from the three facts that a radio row carries separately (RadioStationItem.Logo,
    /// LogoGround and LogoStretch). A row and the pane fit one logo the same way.</summary>
    /// <param name="fills">Whether the picture fills the well and is rounded by it, rather than being held
    /// off the edges on its ground. See RadioStationItem.FoundLogo.Fills.</param>
    public void Show(ImageSource? picture, ImageSource? ground, bool fills)
    {
        _fills = fills;
        _logo.Source = picture;
        _ground.Source = ground;
        _logoHost.Visibility = picture is null ? Visibility.Collapsed : Visibility.Visible;
        _groundClip.Visibility = ground is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Visibility = picture is null ? Visibility.Visible : Visibility.Collapsed;
        Fit();
    }

    private void Fit()
    {
        _logo.Stretch = _fills ? Stretch.UniformToFill : Stretch.Uniform;
        _logoHost.Padding = new Thickness(_fills ? 0 : _edge * 4 / 51);
    }

    internal static SolidColorBrush Ink(uint argb) => new(Color.FromArgb(
        (byte)(argb >> 24), (byte)(argb >> 16), (byte)(argb >> 8), (byte)argb));
}

/// <summary>
/// One card on radio's banner, drawn the way home's radio card is drawn (HomeView.xaml, "a radio station").
/// It shows the station's mark in the middle of a room in the mark's own colours, its name under it, a
/// short description, and what it is playing. Three hosts stand at the banner's three places: peeking at
/// the left, the plate in front, peeking at the right. A step hands each host another face (see
/// PegasusChrome.Radio, StepRadioBanner).
/// </summary>
internal sealed class RadioBannerHost
{
    private readonly Border _glass = new() { Background = StationWell.Ink(0x1AFFFFFF), IsHitTestVisible = false };
    private readonly Border _veil = new() { Background = StationWell.Ink(0x26000000), IsHitTestVisible = false };
    private readonly Border _dim = new() { Background = StationWell.Ink(0xFF000000), Opacity = 0, IsHitTestVisible = false };
    private readonly Border _rim = new() { BorderBrush = StationWell.Ink(0x33FFFFFF), BorderThickness = new Thickness(1), IsHitTestVisible = false };

    private readonly StackPanel _body = new()
    {
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
        IsHitTestVisible = false,
    };

    private readonly Grid _markBox = new();
    private readonly Image _mark = new()
    {
        Stretch = Stretch.Uniform,
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _glyph = new()
    {
        Text = "",   // the radio icon, which home's radio card shows while a station's mark has not arrived
        Foreground = StationWell.Ink(0x59FFFFFF),
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly TextBlock _name = new()
    {
        FontWeight = Microsoft.UI.Text.FontWeights.Bold,
        Foreground = StationWell.Ink(0xFFFFFFFF),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _blurb = new()
    {
        Foreground = StationWell.Ink(0xD1FFFFFF),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextWrapping = TextWrapping.Wrap,
        MaxLines = 2,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _line = new()
    {
        Foreground = StationWell.Ink(0x8CFFFFFF),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        MaxLines = 1,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly CompositeTransform _place = new();

    public RadioBannerHost(FontFamily icons)
    {
        _glyph.FontFamily = icons;
        _markBox.Children.Add(_mark);
        _markBox.Children.Add(_glyph);
        _body.Children.Add(_markBox);
        _body.Children.Add(_name);
        _body.Children.Add(_blurb);
        _body.Children.Add(_line);

        Root.Children.Add(_glass);
        Root.Children.Add(Room);
        Root.Children.Add(_veil);
        Root.Children.Add(_body);
        Root.Children.Add(_dim);
        Root.Children.Add(_rim);
        Root.RenderTransform = _place;
    }

    /// <summary>The host. It is placed by its transform, so a step moves five transforms and lays nothing
    /// out.</summary>
    public Grid Root { get; } = new()
    {
        HorizontalAlignment = HorizontalAlignment.Left,
        VerticalAlignment = VerticalAlignment.Top,
        Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent),
    };

    /// <summary>The room behind the mark. Only the host in front drifts (see PegasusChrome.Radio).</summary>
    public CardRoom Room { get; } = new() { Drift = false };

    /// <summary>The station this host is drawing, or null.</summary>
    public StationFace? Face { get; private set; }

    /// <summary>Hands the host another station. The host follows the face's changes until it is handed the
    /// next one.</summary>
    public void Show(StationFace? face)
    {
        if (ReferenceEquals(face, Face))
            return;
        if (Face is { } was)
            was.Changed -= Paint;
        Face = face;
        if (face is not null)
            face.Changed += Paint;
        Root.Visibility = face is null ? Visibility.Collapsed : Visibility.Visible;
        Paint();
    }

    /// <summary>Reads the face and writes what it says. It runs on every change to the face, and on Show.</summary>
    public void Paint()
    {
        if (Face is not { } face)
            return;

        Room.Palette = face.Room;
        _name.Text = face.Station.Display;

        _mark.Source = face.Logo.Picture;
        _mark.Visibility = face.Logo.Picture is null ? Visibility.Collapsed : Visibility.Visible;
        _glyph.Visibility = face.Logo.Picture is null ? Visibility.Visible : Visibility.Collapsed;

        _blurb.Text = face.Blurb ?? "";
        _blurb.Visibility = face.Blurb is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        var now = face.NowPlaying;
        _line.Text = now ?? "";
        _line.Visibility = now is null ? Visibility.Collapsed : Visibility.Visible;
    }

    /// <summary>The card's size and its writing, at the given scale. The writing uses the TV's banner card's
    /// couch sizes (22, 18 on a 24 line, and 16 points), larger than home's radio card's 13 and 11.5 so it
    /// reads from across a room. The mark takes the top two fifths of the card.</summary>
    public void Size(double width, double height, double scale)
    {
        Root.Width = width;
        Root.Height = height;

        var corner = new CornerRadius(12 * scale);
        Root.CornerRadius = corner;
        Room.CornerRadius = corner;
        _glass.CornerRadius = _veil.CornerRadius = _dim.CornerRadius = _rim.CornerRadius = corner;

        _markBox.Height = height * 0.40;
        _mark.MaxHeight = height * 0.40;
        _mark.MaxWidth = width * 0.55;
        _glyph.FontSize = height * 0.20;

        _body.Spacing = 10 * scale;
        _name.FontSize = 22 * scale;
        _name.MaxWidth = width * 0.8;
        _blurb.FontSize = 18 * scale;
        _blurb.LineHeight = 24 * scale;
        _blurb.MaxWidth = Math.Min(420 * scale, width * 0.85);
        _line.FontSize = 16 * scale;
        _line.MaxWidth = width * 0.7;
    }

    /// <summary>Where the host stands this frame, its left and top edges in the banner, and how far it is
    /// dimmed. It is written on every frame of a step, from where the step has got to.</summary>
    public void Place(double left, double top, double dim)
    {
        _place.TranslateX = left;
        _place.TranslateY = top;
        _dim.Opacity = dim;
    }

    /// <summary>What of the card shows, in its own points: the part a peeking card shows at the strip's
    /// end, or null for the whole card.</summary>
    public void Cut(Windows.Foundation.Rect? shows)
    {
        if (shows is not { } rect)
        {
            if (Root.Clip is not null)
                Root.Clip = null;
            return;
        }

        if (Root.Clip is { Rect: var was } && was == rect)
            return;
        Root.Clip = new RectangleGeometry { Rect = rect };
    }
}

/// <summary>
/// One card in one of radio's rows: the station's logo well in a still room of the mark's colours, with the
/// station's name and where it broadcasts from under it. Essential stations stand in a wide room, favourite
/// stations in a square one.
///
/// <para>The remote stops on a line around a plain button (see RemoteLine): OK tunes the station, and a held
/// OK or the Menu key opens its menu (Play, and Add to or Remove from favourites; see
/// PegasusChrome.StationMenu). The button inside takes the pointer's press and is not a stop itself.</para>
/// </summary>
internal sealed class RadioRowCard
{
    private readonly Grid _art = new();
    private readonly StationWell _well;

    private readonly TextBlock _name = new()
    {
        FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
        Foreground = StationWell.Ink(0xF2FFFFFF),
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly TextBlock _detail = new()
    {
        Foreground = StationWell.Ink(0x99FFFFFF),
        TextWrapping = TextWrapping.NoWrap,
        TextTrimming = TextTrimming.CharacterEllipsis,
    };

    private readonly StackPanel _stack = new();
    private readonly bool _wide;

    public RadioRowCard(
        StationFace face, bool wide, Style plain, FontFamily icons,
        Action<RadioStation> tune, Func<RadioStation, MenuFlyout?> menu)
    {
        Face = face;
        _wide = wide;
        _well = new StationWell(icons);

        _art.Children.Add(Room);
        _art.Children.Add(_well.Root);
        _well.Root.HorizontalAlignment = HorizontalAlignment.Center;
        _well.Root.VerticalAlignment = VerticalAlignment.Center;

        _stack.Children.Add(_art);
        _stack.Children.Add(_name);
        _stack.Children.Add(_detail);

        _press = new Button
        {
            Style = plain,
            Content = _stack,
            IsTabStop = false,
        };
        _press.Click += (_, _) => tune(face.Station);

        Root = new RemoteLine(_press, () => tune(face.Station), () => menu(face.Station), turn: null)
        {
            VerticalAlignment = VerticalAlignment.Top,
        };

        _name.Text = face.Station.Display;
        _detail.Text = face.Detail;
        face.Changed += Paint;
        Paint();
    }

    /// <summary>The remote's stop: OK tunes, a held OK or the Menu key opens the station's menu.</summary>
    public RemoteLine Root { get; }

    /// <summary>The pointer's press, inside the stop.</summary>
    private readonly Button _press;

    /// <summary>A still room: it is drawn once, in the station's colours. Twenty drifting rooms on one screen
    /// would cost the drawing thread several milliseconds a frame each (see CardRoom).</summary>
    public CardRoom Room { get; } = new() { Drift = false, Fewest = 32 };

    public StationFace Face { get; }

    /// <summary>The card's width and its writing. A wide card's room is 16 by 9. A favourite's room is square.
    /// The well stands in the middle at half the room's height.</summary>
    public void Size(double width, double scale)
    {
        var height = _wide ? width * 9 / 16 : width;
        _art.Width = width;
        _art.Height = height;
        _stack.Width = width;
        _stack.Spacing = 7 * scale;

        var corner = new CornerRadius(10 * scale);
        Root.CornerRadius = _press.CornerRadius = corner;
        Room.CornerRadius = corner;
        _well.Size(height * 0.5);

        _name.FontSize = 14 * scale;
        _detail.FontSize = 12 * scale;
    }

    private void Paint()
    {
        Room.Palette = Face.Room;
        _well.Show(Face.Logo);
    }

    /// <summary>Stops following the face. Used when a row is built again.</summary>
    public void Drop() => Face.Changed -= Paint;
}

/// <summary>
/// Radio's five ways in (Tuner, Search, Music, News and Local, the wheel's entries) as a row of keys under
/// the banner that turns like a carousel: the TV's mode keys (Radio.kt, RadioModes), which came from the
/// Apple TV's, because a key held for two seconds is not easy to find.
///
/// <list type="bullet">
/// <item><b>Out of sight until the remote comes down to it.</b> Down from the banner gives its stop the
/// focus (see PegasusChrome.RadioOverBanner), and the row slides down into place from 26 above over 260 ms
/// as it fades in, taking its room as it comes, so the rows under it glide rather than jump. It goes the
/// same way once the focus moves on.</item>
/// <item><b>One stop for the remote</b>, as the banner over it is: the key in hand stands in the middle,
/// lit, with three keys at each side of it, dimmer the further out. Left and Right turn it, sliding the
/// keys along by one over 220 ms, and it wraps: with five ways in, the keys at the ends are the far side of
/// the round. OK opens the key in the middle.</item>
/// <item><b>Each key</b> is its glyph and its name in the menu key's glass (PegasusMenuButton: #14FFFFFF,
/// a 1 point #B3FFFFFF edge, corner 6), 196 wide and 52 high, 14 apart and 30 under the banner's dots; the
/// middle one #40FFFFFF with a 2 point white edge and 8% larger while the row has the remote.</item>
/// </list>
///
/// <para><b>The stop is a tab stop only while it is wanted.</b> It stands in a canvas at the middle key's
/// size whatever the row's height, so it can take the focus at once while the row is still folded away;
/// and while the row is folded it is no tab stop, so the search by layout from the rows under it cannot
/// land on a key that is not drawn. <see cref="Arm"/> makes it one just before the focus is given to it,
/// and losing the focus makes it none again.</para>
///
/// <para>Drawn and turned by transforms on the frame tick (see <see cref="Tick"/>), so a turn or the fold
/// lays out only the row's own height.</para>
/// </summary>
internal sealed class RadioModeRow
{
    /// <summary>How many keys stand at each side of the middle one, and how much dimmer each step out is.</summary>
    private const int Side = 3;
    private const double Fade = 0.2;

    /// <summary>A key's size and the room between two, how much the middle key grows while the row has the
    /// remote, and how far under the banner's dots the keys stand, before the view's scale.</summary>
    private const double KeyWidth = 196, KeyHeight = 52, Gap = 14, Grow = 1.08, Above = 30;

    /// <summary>How far above its place the row starts as it comes, before the view's scale, and how long it
    /// takes to come and go and to turn by one key.</summary>
    private const double Drop = 26;
    private const double InMs = 260, TurnMs = 220;

    private readonly (string Glyph, string Name)[] _entries;
    private readonly Action<int> _pick;

    /// <summary>The keys, one past each end of the ones shown as well, so a turn slides a key in from the
    /// edge rather than drawing it there. Key k stands at offset k - (Side + 1) from the middle.</summary>
    private readonly Border[] _keys = new Border[(2 * (Side + 1)) + 1];
    private readonly TextBlock[] _glyphs = new TextBlock[(2 * (Side + 1)) + 1];
    private readonly TextBlock[] _names = new TextBlock[(2 * (Side + 1)) + 1];
    private readonly CompositeTransform[] _places = new CompositeTransform[(2 * (Side + 1)) + 1];
    private readonly Canvas _canvas = new() { HorizontalAlignment = HorizontalAlignment.Left, VerticalAlignment = VerticalAlignment.Top };
    private readonly TranslateTransform _stopPlace = new();

    /// <summary>Which entry is in the middle, counted without wrapping, so a turn always slides the way it
    /// was made.</summary>
    private int _at;

    /// <summary>A turn under way: the keys stand <see cref="TurnNow"/> keys from their places, easing out
    /// to nought (a cubic ease out, the TV's BannerOut).</summary>
    private double _turnFrom, _turnT = 1;

    /// <summary>How far the row has come, 0 folded away to 1 in place, and where it is going.</summary>
    private double _shown, _shownFrom, _shownTo, _shownT = 1;

    private bool _focused;
    private double _scale = 1, _strip;

    public RadioModeRow(IReadOnlyList<(string Glyph, string Name)> entries, FontFamily icons, Action<int> pick)
    {
        _entries = [.. entries];
        _pick = pick;

        for (var k = 0; k < _keys.Length; k++)
        {
            _glyphs[k] = new TextBlock
            {
                FontFamily = icons,
                Foreground = StationWell.Ink(0xF2FFFFFF),
                VerticalAlignment = VerticalAlignment.Center,
            };
            _names[k] = new TextBlock
            {
                FontWeight = Microsoft.UI.Text.FontWeights.SemiBold,
                Foreground = StationWell.Ink(0xF2FFFFFF),
                VerticalAlignment = VerticalAlignment.Center,
                MaxLines = 1,
                TextTrimming = TextTrimming.CharacterEllipsis,
            };
            var words = new StackPanel
            {
                Orientation = Orientation.Horizontal,
                HorizontalAlignment = HorizontalAlignment.Center,
                VerticalAlignment = VerticalAlignment.Center,
            };
            words.Children.Add(_glyphs[k]);
            words.Children.Add(_names[k]);
            _places[k] = new CompositeTransform();
            _keys[k] = new Border
            {
                Child = words,
                RenderTransform = _places[k],
                Background = StationWell.Ink(0x14FFFFFF),
                BorderBrush = StationWell.Ink(0xB3FFFFFF),
                Opacity = 0,
            };

            // The pointer opens the key it presses, while the row is up.
            var offset = k - (Side + 1);
            _keys[k].Tapped += (_, e) =>
            {
                if (_shown < 0.5)
                    return;
                e.Handled = true;
                _pick(Mod(_at + offset));
            };
            _canvas.Children.Add(_keys[k]);
        }

        Stop = new RemoteLine(new Grid(), () => _pick(Selected), menu: null, turn: Turn)
        {
            IsHitTestVisible = false,
            IsTabStop = false,
            HorizontalAlignment = HorizontalAlignment.Left,
            VerticalAlignment = VerticalAlignment.Top,
            RenderTransform = _stopPlace,
        };
        Stop.GotFocus += (_, _) =>
        {
            _focused = true;
            ShowTo(1);
            Paint();
        };
        Stop.LostFocus += (_, _) =>
        {
            _focused = false;
            Stop.IsTabStop = false;
            ShowTo(0);
            Paint();
        };
        _canvas.Children.Add(Stop);

        Root.Children.Add(_canvas);
        Root.Height = 0;
        Paint();
    }

    /// <summary>The row, for the radio screen's column under the banner's dots.</summary>
    public Grid Root { get; } = new() { HorizontalAlignment = HorizontalAlignment.Stretch, VerticalAlignment = VerticalAlignment.Top };

    /// <summary>The remote's one stop over the middle key.</summary>
    public RemoteLine Stop { get; }

    /// <summary>The entry in the middle.</summary>
    public int Selected => Mod(_at);

    /// <summary>Makes the stop a tab stop, for the focus that is about to be given to it, and returns
    /// it.</summary>
    public RemoteLine Arm()
    {
        Stop.IsTabStop = true;
        return Stop;
    }

    /// <summary>Left (-1) or Right (1) on the stop: the next key comes into the middle, sliding from the side
    /// it was on. Always true: the row wraps.</summary>
    private bool Turn(int by)
    {
        var now = TurnNow;
        _at += by;
        _turnFrom = by + now;
        _turnT = 0;
        Paint();
        return true;
    }

    private double TurnNow => _turnFrom * Math.Pow(1 - _turnT, 3);

    private void ShowTo(double to)
    {
        if (Math.Abs(_shownTo - to) < 0.001)
            return;
        _shownFrom = _shown;
        _shownTo = to;
        _shownT = 0;
    }

    /// <summary>The row's sizes: the strip it spans and the view's scale.</summary>
    public void Size(double strip, double scale)
    {
        _strip = strip;
        _scale = scale;

        var corner = new CornerRadius(6 * scale);
        for (var k = 0; k < _keys.Length; k++)
        {
            _keys[k].Width = KeyWidth * scale;
            _keys[k].Height = KeyHeight * scale;
            _keys[k].CornerRadius = corner;
            _keys[k].Padding = new Thickness(16 * scale, 0, 16 * scale, 0);
            _places[k].CenterX = KeyWidth * scale / 2;
            _places[k].CenterY = KeyHeight * scale / 2;
            _glyphs[k].FontSize = 17 * scale;
            _glyphs[k].Margin = new Thickness(0, 0, 10 * scale, 0);
            _names[k].FontSize = 19 * scale;
            _names[k].MaxWidth = (KeyWidth - 32 - 27) * scale;
        }

        Stop.Width = KeyWidth * scale;
        Stop.Height = KeyHeight * scale;
        Stop.CornerRadius = corner;

        // Across, the row is cut at the strip's ends; down, it is not, so the middle key's growth and the
        // fold's slide are drawn whole.
        Root.Clip = new RectangleGeometry { Rect = new Windows.Foundation.Rect(0, -1000 * scale, strip, 3000 * scale) };
        Paint();
    }

    /// <summary>One frame of the fold and of a turn. Paints only while either moves.</summary>
    public void Tick(double dt)
    {
        var moving = false;
        if (_shownT < 1)
        {
            _shownT = Math.Min(1, _shownT + (dt * 1000 / InMs));
            var eased = 1 - Math.Pow(1 - _shownT, 2);   // an ease out quad, as the TV's
            _shown = _shownFrom + ((_shownTo - _shownFrom) * eased);
            moving = true;
        }
        if (_turnT < 1)
        {
            _turnT = Math.Min(1, _turnT + (dt * 1000 / TurnMs));
            moving = true;
        }
        if (moving)
            Paint();
    }

    /// <summary>
    /// Writes every key's entry, place, look and ink from where the fold and a turn have got to, and the
    /// row's height from the fold: the room it takes grows with it.
    /// </summary>
    private void Paint()
    {
        var s = _scale;
        var box = KeyHeight * Grow * s;
        Root.Height = ((Above * s) + box) * _shown;

        var left = (_strip / 2) - (KeyWidth * s / 2);
        var top = (Above * s) + ((box - (KeyHeight * s)) / 2);
        var lift = -(1 - _shown) * Drop * s;
        var pitch = (KeyWidth + Gap) * s;
        var turn = TurnNow;

        for (var k = 0; k < _keys.Length; k++)
        {
            var d = k - (Side + 1);
            var (glyph, name) = _entries[Mod(_at + d)];
            if (_glyphs[k].Text != glyph)
                _glyphs[k].Text = glyph;
            if (_names[k].Text != name)
                _names[k].Text = name;

            var place = d + turn;
            var far = Math.Abs(place);
            var lit = Math.Clamp(1 - far, 0, 1) * (_focused ? 1 : 0);
            var ink = far <= Side
                ? 1 - (far * Fade)
                : Math.Max(0, (Side + 1 - far) * (1 - (Side * Fade)));

            Canvas.SetLeft(_keys[k], left);
            Canvas.SetTop(_keys[k], top);
            _places[k].TranslateX = place * pitch;
            _places[k].TranslateY = lift;
            _places[k].ScaleX = _places[k].ScaleY = 1 + ((Grow - 1) * lit);
            _keys[k].Opacity = ink * _shown;

            var middle = d == 0 && _focused;
            _keys[k].Background = middle ? _middleFill : _keyFill;
            _keys[k].BorderBrush = middle ? _middleEdge : _keyEdge;
            _keys[k].BorderThickness = new Thickness(middle ? 2 : 1);
        }

        Canvas.SetLeft(Stop, left);
        Canvas.SetTop(Stop, top);
        _stopPlace.Y = lift;
    }

    private readonly SolidColorBrush _keyFill = StationWell.Ink(0x14FFFFFF);
    private readonly SolidColorBrush _keyEdge = StationWell.Ink(0xB3FFFFFF);
    private readonly SolidColorBrush _middleFill = StationWell.Ink(0x40FFFFFF);
    private readonly SolidColorBrush _middleEdge = StationWell.Ink(0xFFFFFFFF);

    private int Mod(int i) => ((i % _entries.Length) + _entries.Length) % _entries.Length;
}
