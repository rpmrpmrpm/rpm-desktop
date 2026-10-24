using System.ComponentModel;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Microsoft.UI.Xaml.Shapes;
using RPM.Desktop.Presentation.Main.LeftPane.Models;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// The sizes, fonts and keys that the tuner's rows and the search's tiles need. The chrome gives this to
/// each row or tile when the repeater prepares it (see PegasusChrome.RadioTuner and RadioSearch). A row
/// that is realized after a resize is drawn at the new size. The chrome also updates the rows that are
/// already realized.
/// </summary>
/// <param name="Scale">The scale that the main window's radio pane sizes are multiplied by.</param>
/// <param name="Edge">A search tile's side, in points.</param>
/// <param name="Menu">A search tile's menu for a held OK or the Menu key, built unshown (see
/// PegasusChrome.StationMenu). Null where a held OK only presses, as on the tuner's rows.</param>
internal sealed record PegasusRadioLook(
    double Scale,
    double Edge,
    Style Plain,
    FontFamily Icons,
    FontFamily IconsRegular,
    Action<RadioStationItem> Press,
    Action<RadioStationItem> Heart,
    Func<RadioStationItem, MenuFlyout?>? Menu = null);

/// <summary>
/// One station in the tuner's list, drawn the way the main window's radio pane draws one (RadioView.xaml):
/// the logo in its well, the on-air lamp, the name with its call sign, where it broadcasts from, the heart
/// and the play key. It is drawn larger than in the pane, so it can be read from across a room.
///
/// <para><b>Reads the station it is handed when it is realized.</b> The repeater recycles rows, so a row
/// that scrolls off is handed another station. The row follows that station's changes (its logo arriving,
/// being tuned, being made a favourite) until it is handed the next one. It asks for the station's logo
/// only then. The row pulls its state when it is realized, so nothing has to push updates to it.</para>
///
/// <para>The row itself is a remote stop. OK on it plays or stops the station, as the pane's play key does.
/// The heart is a second stop beside it. The play key is for the pointer only. It is not a stop, because it
/// does the same thing as the row.</para>
/// </summary>
public sealed partial class PegasusStationRow : Grid
{
    private readonly Button _main = new() { HorizontalAlignment = HorizontalAlignment.Stretch, HorizontalContentAlignment = HorizontalAlignment.Stretch };
    private readonly Grid _inside = new() { Background = new SolidColorBrush(Microsoft.UI.Colors.Transparent) };
    private readonly Ellipse _lampGlow = new();
    private readonly Ellipse _lamp = new() { Fill = StationWell.Ink(0xFFFF4438) };
    private readonly Ellipse _lampOff = new() { Fill = StationWell.Ink(0x33FFFFFF) };
    private readonly Grid _lampBox = new() { VerticalAlignment = VerticalAlignment.Center };

    private readonly TextBlock _name = new()
    {
        Foreground = StationWell.Ink(0xEEFFFFFF),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly AdsBadge _ads = new()
    {
        LetterBrush = StationWell.Ink(0xFF0B0D10),
        VerticalAlignment = VerticalAlignment.Center,
        IsTabStop = false,
    };

    private readonly TextBlock _detail = new()
    {
        Foreground = StationWell.Ink(0x77FFFFFF),
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };

    private readonly TextBlock _sign = new()
    {
        Foreground = StationWell.Ink(0xAA04C8FF),
        CharacterSpacing = 60,
        VerticalAlignment = VerticalAlignment.Center,
    };

    private readonly Button _heart = new() { VerticalAlignment = VerticalAlignment.Center };
    private readonly TextBlock _heartMark = new()
    {
        Text = "",
        HorizontalAlignment = HorizontalAlignment.Center,
        VerticalAlignment = VerticalAlignment.Center,
    };
    private readonly Button _play = new() { VerticalAlignment = VerticalAlignment.Center, IsTabStop = false };
    private readonly TextBlock _playMark = new() { HorizontalAlignment = HorizontalAlignment.Center, VerticalAlignment = VerticalAlignment.Center };

    private StationWell? _well;
    private PegasusRadioLook? _look;
    private RadioStationItem? _item;

    public PegasusStationRow()
    {
        ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });

        _lampGlow.Fill = new RadialGradientBrush
        {
            GradientStops =
            {
                new GradientStop { Offset = 0.0, Color = Windows.UI.Color.FromArgb(0xB3, 0xFF, 0x3B, 0x30) },
                new GradientStop { Offset = 0.45, Color = Windows.UI.Color.FromArgb(0x59, 0xFF, 0x3B, 0x30) },
                new GradientStop { Offset = 1.0, Color = Windows.UI.Color.FromArgb(0x00, 0xFF, 0x3B, 0x30) },
            },
        };
        _lampBox.Children.Add(_lampGlow);
        _lampBox.Children.Add(_lamp);
        _lampBox.Children.Add(_lampOff);
        _lamp.HorizontalAlignment = _lampOff.HorizontalAlignment = HorizontalAlignment.Center;
        _lamp.VerticalAlignment = _lampOff.VerticalAlignment = VerticalAlignment.Center;

        _main.Content = _inside;
        _heart.Content = _heartMark;
        _play.Content = _playMark;
        Children.Add(_main);
        Children.Add(_heart);
        Children.Add(_play);
        SetColumn(_heart, 1);
        SetColumn(_play, 2);

        _main.Click += (_, _) => { if (_item is { } item) _look?.Press(item); };
        _heart.Click += (_, _) => { if (_item is { } item) _look?.Heart(item); };
        _play.Click += (_, _) => { if (_item is { } item) _look?.Press(item); };

        DataContextChanged += (_, _) => Follow(DataContext as RadioStationItem, again: false);
        Loaded += (_, _) => Follow(DataContext as RadioStationItem, again: true);
        Unloaded += (_, _) => Follow(null, again: false);
    }

    /// <summary>The main part of the row. It is the remote's stop that plays or stops the station.</summary>
    internal Button MainKey => _main;

    /// <summary>Gives the row its look. It is called again whenever the look changes. The parts are built
    /// the first time.</summary>
    internal void Wear(PegasusRadioLook look)
    {
        if (ReferenceEquals(look, _look))
            return;

        var first = _look is null;
        _look = look;
        if (first)
            Build(look);
        Lay(look.Scale);
        Paint();
    }

    private void Build(PegasusRadioLook look)
    {
        _well = new StationWell(look.Icons);
        _main.Style = look.Plain;
        _heart.Style = look.Plain;
        _play.Style = (Style)Application.Current.Resources["GlassPrimaryButtonStyle"];
        _playMark.FontFamily = look.Icons;

        var text = new StackPanel { VerticalAlignment = VerticalAlignment.Center };
        var nameLine = new StackPanel { Orientation = Orientation.Horizontal };
        nameLine.Children.Add(_lampBox);
        nameLine.Children.Add(_name);
        nameLine.Children.Add(_ads);
        text.Children.Add(nameLine);
        text.Children.Add(_detail);

        _inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _inside.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        _inside.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
        _inside.Children.Add(_well.Root);
        _inside.Children.Add(text);
        _inside.Children.Add(_sign);
        Grid.SetColumn(text, 1);
        Grid.SetColumn(_sign, 2);
        _well.Root.VerticalAlignment = VerticalAlignment.Center;
    }

    /// <summary>Sets the sizes at <paramref name="scale"/>. At a scale of 1 this matches the main window's
    /// radio row: a 51 point well, 13 and 11 point text lines, a 10 point call sign, a 34 point heart and a
    /// 45 by 39 point play key.</summary>
    private void Lay(double scale)
    {
        if (_well is null)
            return;

        _well.Size(51 * scale);
        _inside.ColumnSpacing = 10 * scale;
        _inside.Padding = new Thickness(6 * scale, 5 * scale, 6 * scale, 5 * scale);
        ColumnSpacing = 6 * scale;
        _main.CornerRadius = new CornerRadius(8 * scale);

        _lampBox.Width = _lampBox.Height = 12 * scale;
        _lampBox.Margin = new Thickness(0, 0, 5 * scale, 0);
        _lampGlow.Width = _lampGlow.Height = 12 * scale;
        _lamp.Width = _lamp.Height = 6 * scale;
        _lampOff.Width = _lampOff.Height = 6 * scale;

        _name.FontSize = 13 * scale;
        _ads.Height = 13 * scale;
        _ads.Width = 19 * scale;
        _ads.Margin = new Thickness(5 * scale, 0, 0, 0);
        _detail.FontSize = 11 * scale;
        _sign.FontSize = 10 * scale;

        _heart.Width = 34 * scale;
        _heart.Height = 34 * scale;
        _heart.CornerRadius = new CornerRadius(17 * scale);
        _heartMark.FontSize = 13 * scale;

        _play.Width = 45 * scale;
        _play.Height = 39 * scale;
        _play.MinHeight = 0;
        _play.MinWidth = 0;
        _play.Padding = new Thickness(0);
        _playMark.FontSize = 11 * scale;
    }

    /// <summary>Starts following another station, or none. <paramref name="again"/> re-subscribes to the same
    /// station. This is needed after the row was unloaded and loaded again.</summary>
    private void Follow(RadioStationItem? item, bool again)
    {
        if (ReferenceEquals(item, _item) && !again)
            return;

        if (_item is { } was)
            was.PropertyChanged -= Item_PropertyChanged;
        _item = item;
        if (item is not null)
            item.PropertyChanged += Item_PropertyChanged;
        Paint();
    }

    private void Item_PropertyChanged(object? sender, PropertyChangedEventArgs e)
    {
        if (ReferenceEquals(sender, _item))
            Paint();
    }

    /// <summary>Reads the station and writes what it says. Reading its logo starts the lookup for it, so a
    /// station is looked up only once a row showing it has been realized (see RadioStationItem.Logo).</summary>
    private void Paint()
    {
        if (_item is not { } item || _look is not { } look || _well is null)
            return;

        _name.Text = item.Name;
        _ads.HasAds = item.HasAds;
        _ads.Visibility = item.HasAds ? Visibility.Visible : Visibility.Collapsed;
        _detail.Text = item.Detail;
        _sign.Text = item.Sign;

        _lampGlow.Visibility = _lamp.Visibility = item.OnAir ? Visibility.Visible : Visibility.Collapsed;
        _lampOff.Visibility = item.OffAir ? Visibility.Visible : Visibility.Collapsed;

        _well.Show(item.Logo, item.LogoGround, item.LogoStretch == Stretch.UniformToFill);

        _heartMark.FontFamily = item.IsFavorite ? look.Icons : look.IconsRegular;
        _heartMark.Foreground = (Brush)Application.Current.Resources[
            item.IsFavorite ? "FavoriteMarkBrush" : "FavoriteMarkIdleBrush"];
        _playMark.Text = item.PlayGlyph;
    }
}

/// <summary>
/// One station in radio's search results: its logo in a well, and its name under it. The results are laid
/// out as a grid of squares, each with the station's name under it.
///
/// <para>The remote stops on its line (see <see cref="Key"/>, a RemoteLine): OK tunes the station and
/// closes the search (see PegasusChrome.RadioSearch), and a held OK or the Menu key opens the station's
/// menu. Inside the line a plain button takes the pointer's press; it is not a stop itself. The tile is a
/// panel around the line because a RemoteLine is given what OK does when it is made, and the repeater
/// makes a tile with nothing: the line asks the tile's look and station each time instead.</para>
///
/// <para>It reads the station it is handed when it is realized, as a tuner row does. It asks for its logo at
/// the tile's own size, not at the pane's 102 pixels. At this tile size, a 102 pixel logo would be drawn
/// soft. An answer for a station that the tile has since been handed away from is dropped.</para>
/// </summary>
public sealed partial class PegasusStationTile : Grid
{
    private readonly StackPanel _stack = new();
    private readonly Button _press = new() { IsTabStop = false };

    private readonly TextBlock _name = new()
    {
        Foreground = StationWell.Ink(0xF2FFFFFF),
        TextAlignment = TextAlignment.Center,
        HorizontalAlignment = HorizontalAlignment.Center,
        TextTrimming = TextTrimming.CharacterEllipsis,
        MaxLines = 1,
    };

    private StationWell? _well;
    private PegasusRadioLook? _look;
    private RadioStationItem? _item;

    public PegasusStationTile()
    {
        _press.Content = _stack;
        _press.Click += (_, _) => Press();
        Key = new RemoteLine(
            _press,
            Press,
            () => _item is { } item ? _look?.Menu?.Invoke(item) : null,
            turn: null);
        Children.Add(Key);
        VerticalAlignment = VerticalAlignment.Top;
        DataContextChanged += (_, _) => Follow(DataContext as RadioStationItem);
    }

    /// <summary>The remote's stop: OK tunes the station, a held OK or the Menu key opens its menu.</summary>
    internal RemoteLine Key { get; }

    private void Press()
    {
        if (_item is { } item)
            _look?.Press(item);
    }

    internal void Wear(PegasusRadioLook look)
    {
        if (ReferenceEquals(look, _look))
            return;

        if (_look is null)
        {
            _press.Style = look.Plain;
            _well = new StationWell(look.Icons);
            _stack.Children.Add(_well.Root);
            _stack.Children.Add(_name);
        }
        _look = look;

        _well!.Size(look.Edge);
        _stack.Width = look.Edge;
        _stack.Spacing = 10 * look.Scale;
        _name.FontSize = 15 * look.Scale;
        _name.MaxWidth = look.Edge;
        Key.CornerRadius = _press.CornerRadius = new CornerRadius(look.Edge * 6 / 51);

        // The tile was realized before its look arrived, so its station is followed again now that the look is set.
        var item = _item;
        _item = null;
        Follow(item);
    }

    private void Follow(RadioStationItem? item)
    {
        if (ReferenceEquals(item, _item))
            return;

        _item = item;
        if (item is null || _well is null || _look is not { } look)
            return;

        _name.Text = item.Name;
        _well.Show(default);
        _ = AskLogoAsync(item, look);
    }

    /// <summary>The station's logo at the tile's size, in device pixels. The result is dropped if the tile has
    /// been handed another station in the meantime.</summary>
    private async Task AskLogoAsync(RadioStationItem item, PegasusRadioLook look)
    {
        var raster = XamlRoot?.RasterizationScale is > 0 and var s ? s : 1;
        var edge = (int)Math.Ceiling(look.Edge * raster);

        RadioStationItem.FoundLogo found;
        try
        {
            found = await RadioStationItem.LogoForAsync(item.Station, edge);
        }
        catch
        {
            return;   // a failed request (for example, the logo site is down) leaves the placeholder in place
        }

        if (ReferenceEquals(item, _item))
            _well?.Show(found);
    }
}
