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
/// short description, and what it is playing. Five hosts stand at offsets -2 to 2 from the card in front.
/// A step hands each host another face (see PegasusChrome.Radio, StepRadioBanner).
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

    /// <summary>The card's size and its writing, at the given scale. The writing uses home's radio card's sizes
    /// (22, 13 and 11.5 points). The mark takes the top two fifths of the card.</summary>
    public void Size(double width, double height, double scale)
    {
        Root.Width = width;
        Root.Height = height;
        _place.CenterX = width / 2;
        _place.CenterY = height / 2;

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
        _blurb.FontSize = 13 * scale;
        _blurb.LineHeight = 18 * scale;
        _blurb.MaxWidth = 300 * scale;
        _line.FontSize = 11.5 * scale;
        _line.MaxWidth = width * 0.7;
    }

    /// <summary>Where the host stands this frame: its middle, how far it is drawn down (shrunk), and how far
    /// it is dimmed. It is written on every frame of a step, from where the step has got to.</summary>
    public void Place(double left, double shrink, double dim)
    {
        _place.TranslateX = left;
        _place.ScaleX = _place.ScaleY = shrink;
        _dim.Opacity = dim;
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
