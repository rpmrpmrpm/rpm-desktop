using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Automation.Peers;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;
using RPM.Desktop.Presentation.Common;
using Windows.Foundation;
using Color = Windows.UI.Color;
using Path = Microsoft.UI.Xaml.Shapes.Path;
#if RPM_WINUI
using Microsoft.UI.Composition;
using Microsoft.UI.Xaml.Hosting;
#endif

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// A row's menu in Pegasus, drawn as the Android TV app draws it (TrackMenu.kt, RowMenu; FOBO 651449165
/// and 366b379d4, ANDR 3190fcf and 6112324): the screen goes soft under a light blur and a fifth of
/// black, the row the menu was opened on stays sharp and at full brightness in a hole in that blur, and
/// beside the row stands a card of the light glass with the menu's lines on it, at the size of a track
/// line. The card fades in, and fades out when it closes.
///
/// <para><b>Every Pegasus menu is shown here.</b> The menus are still built as the desktop builds them, as
/// MenuFlyouts: the song's menu out of the main window's own factory (TrackMenuFactory), the record's, the
/// station's, the queue row's, the sort menus, the cast menu and the tuner's. The card reads a menu's
/// lines and draws them itself, so the main window's menus stay exactly as they are. What a line reads:
/// its words, its icon (a FontIcon's glyph and face), a tick on a ToggleMenuFlyoutItem that is checked, a
/// chevron on a MenuFlyoutSubItem, a rule for a MenuFlyoutSeparator, and a disabled line drawn dim, which
/// the remote passes over. A line that changes while the card is up (a tick that a read sets late, the
/// record's favourite corrected from its stored row) is drawn again; a menu whose lines are built again
/// in place (the cast menu, as speakers answer) is read again through <see cref="Refresh"/>.</para>
///
/// <para><b>Choosing.</b> A line is chosen through its own automation peer, which raises the line's Click
/// (and toggles a ToggleMenuFlyoutItem) exactly as a press on it in a MenuFlyout would, so every builder's
/// handler runs unchanged. The card closes first, giving the focus back to the row, and then the line does
/// its work, as on the TV: an entry that opens a dialog or another screen finds the focus where it
/// expects it. A submenu (Add to playlist, Rate album, the tuner's filters, Delete) opens in the card's
/// place, and Back, or Left, comes back to the menu it was opened from.</para>
///
/// <para><b>The keys</b> are not the card's own: the shell hands them over while it is open (see
/// PegasusPlayer.LineMenuKey), Up and Down move the lit line and scroll a long menu to it, OK chooses,
/// Right opens a submenu and Left leaves one. Back goes through the host (PegasusPlayer.TakeBack). The
/// card still takes the focus while it is up, so nothing under it is focused, grown or typed into. The
/// mouse: a line lights under the pointer and a click chooses it; a click or a right-click beside the
/// card closes it; the wheel scrolls a long menu.</para>
///
/// <para><b>Where the card stands</b> (the TV's spotBeside): under the row where it fits, else over it,
/// starting at the row's left edge, or ending at its right edge where it would otherwise run past the side
/// of the screen. It is never nearer either side than the screen's inset. It is never taller than the
/// larger of the room under the row and the room over it, and a longer menu (the tuner's countries)
/// scrolls inside it. A menu opened by a right-click stands at the pointer instead.</para>
///
/// <para><b>The blur</b> is the app's live GlassBrush at the TV's menu radius (MENU_RADIUS, about 14
/// points), over the whole view but for a hole the shape of the row, so the row is the screen itself and
/// not a picture of it. The TV takes one still picture of the screen because its boxes cannot blur live;
/// a live blur needs no capture and shows the room still moving. The card itself is the light glass with
/// the TV's dimming (LightSmokeDimmedBrush) and its rim, over that same blur. On the Skia head a
/// full-screen blur thins out at the screen's edges (as radio's search and Choose library do).</para>
///
/// <para><b>The fade</b> is the compositor's on the WinUI head (the visual's opacity, 160 ms in and 110 ms
/// out, the TV's MENU_FADE_MS and MENU_OUT_MS), and the render tick's on the Skia head (see
/// <see cref="Dissolve"/>), whose composition is Uno's own.</para>
/// </summary>
internal sealed partial class PegasusMenuCard : Grid
{
    // ── the TV's sizes (TrackMenu.kt), in its scaled points: times the view's text scale here ──

    /// <summary>The words of a line: the size of a track line, the TV's PAGE_TEXT.</summary>
    private const double WordsSize = 19;

    /// <summary>A line's padding, and the corner of its lit box (MenuEntryRow).</summary>
    private const double LineAcross = 16, LineDown = 9, LineCorner = 8;

    /// <summary>The tick's column, and the tick (TICK_WIDE).</summary>
    private const double TickWide = 30, TickSize = 15;

    /// <summary>The icons' column, the room after it, and the icon (ICON_WIDE, ICON_GAP, ICON_SIZE).</summary>
    private const double IconWide = 26, IconGap = 18, IconSize = 17;

    /// <summary>The room before a submenu's chevron, and the chevron.</summary>
    private const double MoreGap = 24, MoreSize = 13;

    /// <summary>The card's padding down (MENU_INSET) and across (MENU_SIDE), the room between lines, its
    /// corner, and the least and the most it is across (MENU_LEAST, MENU_MOST).</summary>
    private const double CardDown = 8, CardAcross = 14, LineSpacing = 2, CardCorner = 12, CardLeast = 240, CardMost = 640;

    /// <summary>A rule's room across and down.</summary>
    private const double RuleAcross = 10, RuleDown = 4;

    /// <summary>The room between the row and the card (MENU_GAP), and the least room the card keeps from
    /// either side of the screen (MENU_EDGE), in the TV's pixels on a screen 1080 high.</summary>
    private const double GapPixels = 8, EdgePixels = 34;

    /// <summary>The blur under the card, in points: the TV's menu blur, about a Gaussian of 14 (Glass.kt,
    /// MENU_RADIUS), lighter than the side menu's, so the screen under the menu is still the screen it was
    /// opened on.</summary>
    private const double BlurAmount = 14;

    /// <summary>The fade in and the fade out (MENU_FADE_MS, MENU_OUT_MS).</summary>
    private const int FadeInMs = 160, FadeOutMs = 110;

    private static readonly Color Words = Color.FromArgb(0xFF, 0xFF, 0xFF, 0xFF);
    private static readonly Color Dim = Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF);
    private static readonly Color LitColor = Color.FromArgb(0x33, 0xFF, 0xFF, 0xFF);
    private static readonly Color RuleColor = Color.FromArgb(0x26, 0xFF, 0xFF, 0xFF);
    private static readonly Color ShadeColor = Color.FromArgb(0x33, 0x00, 0x00, 0x00);

    private const string TickGlyph = "";
    private const string MoreGlyph = "";

    private readonly Path _blur;
    private readonly Path _shade;
    private readonly Canvas _stand;

    /// <summary>What holds the focus while the card is up. The keys come through the host, not through
    /// it.</summary>
    private readonly ContentControl _card;

    private readonly Border _glass;
    private readonly ScrollViewer _scroll;
    private readonly StackPanel _lines;
    private readonly FontFamily _fa;
    private readonly Func<double> _scale;
    private readonly Func<bool> _keys;
    private readonly Action _focusLost;
    private readonly SolidColorBrush _lit = new(LitColor);
    private readonly SolidColorBrush _unlit = new(Color.FromArgb(0, 0, 0, 0));
#if RPM_WINUI
    private int _fadeTurn;
#else
    private readonly Dissolve _fade;
#endif

    /// <summary>The menu shown, the row it was opened on (the card stands beside it and the blur leaves it
    /// sharp), where a right-click was in that row's points, the stop the focus goes back to, and what the
    /// opener wants to hear when the card closes.</summary>
    private MenuFlyout? _menu;
    private FrameworkElement? _at;
    private Point? _pointer;
    private Control? _returnTo;
    private Action? _closed;

    /// <summary>The menu or submenu in the card now.</summary>
    private Level? _level;

    /// <summary>The property watches on the lines in the card, taken off when it changes.</summary>
    private readonly List<(DependencyObject Item, DependencyProperty Property, long Token)> _watches = [];

    /// <summary>A menu or a submenu: its lines, the one lit (-1 for none), and the menu it was opened from,
    /// with the submenu's own line there, for Back.</summary>
    private sealed class Level(IList<MenuFlyoutItemBase> items, Level? parent, MenuFlyoutSubItem? from = null)
    {
        public IList<MenuFlyoutItemBase> Items { get; } = items;
        public Level? Parent { get; } = parent;
        public MenuFlyoutSubItem? From { get; } = from;
        public List<Line> Lines { get; } = [];
        public int Lit = -1;
    }

    /// <summary>One line in the card and what draws it.</summary>
    private sealed class Line(MenuFlyoutItemBase item, Border box)
    {
        public MenuFlyoutItemBase Item { get; } = item;
        public Border Box { get; } = box;
        public TextBlock? Words;
        public TextBlock? Tick;
        public TextBlock? Icon;

        /// <summary>Whether the remote can stop on it: an enabled line or submenu, not a rule.</summary>
        public bool IsEntry => Item is MenuFlyoutItem or MenuFlyoutSubItem && Item.IsEnabled;
    }

    /// <param name="resources">The chrome's resources: the icon font (PegasusFa), the light glass with the
    /// TV's dimming (LightSmokeDimmedBrush) and its rim (LightSmokeRimBrush).</param>
    /// <param name="scale">The view's text scale, read at each opening.</param>
    /// <param name="keys">Whether the remote is in use. A menu opened by keys starts with its first line
    /// lit, as the TV's does; one opened with the mouse starts with none.</param>
    /// <param name="focusLost">Puts the focus on the screen's default, for a card that closes after its row
    /// has gone.</param>
    public PegasusMenuCard(ResourceDictionary resources, Func<double> scale, Func<bool> keys, Action focusLost)
    {
        _scale = scale;
        _keys = keys;
        _focusLost = focusLost;
        _fa = resources.TryGetValue("PegasusFa", out var fa) && fa is FontFamily family
            ? family
            : new FontFamily("ms-appx:///Assets/Fonts/fa-solid-900.ttf#Font Awesome 5 Pro Solid");

        Visibility = Visibility.Collapsed;
        IsHitTestVisible = false;
        // Transparent, not empty: a press beside the card lands here and closes it, and nothing under the
        // menu is pressed.
        Background = _unlit;

        _blur = new Path { Fill = new GlassBrush { BlurAmount = BlurAmount }, IsHitTestVisible = false };
        _shade = new Path { Fill = new SolidColorBrush(ShadeColor), IsHitTestVisible = false };

        _lines = new StackPanel();
        _scroll = new ScrollViewer
        {
            Content = _lines,
            HorizontalScrollMode = ScrollMode.Disabled,
            HorizontalScrollBarVisibility = ScrollBarVisibility.Disabled,
            VerticalScrollMode = ScrollMode.Auto,
            VerticalScrollBarVisibility = ScrollBarVisibility.Hidden,
            IsTabStop = false,
        };
        _glass = new Border
        {
            Background = resources.TryGetValue("LightSmokeDimmedBrush", out var glass) && glass is Brush smoke
                ? smoke
                : new SolidColorBrush(Color.FromArgb(0x5C, 0x4F, 0x4F, 0x4F)),
            BorderBrush = resources.TryGetValue("LightSmokeRimBrush", out var rim) && rim is Brush edge
                ? edge
                : new SolidColorBrush(RuleColor),
            BorderThickness = new Thickness(1),
            Child = _scroll,
        };
        _card = new ContentControl
        {
            Content = _glass,
            // A stop only while a menu is open (see Show and Close): the remote's search and the focus look
            // walk the view, and must not find the card while it fades out.
            IsTabStop = false,
            UseSystemFocusVisuals = false,
            HorizontalContentAlignment = HorizontalAlignment.Stretch,
            VerticalContentAlignment = VerticalAlignment.Stretch,
        };
        _stand = new Canvas();
        _stand.Children.Add(_card);

        Children.Add(_blur);
        Children.Add(_shade);
        Children.Add(_stand);

        // A press on the card is the card's: only a press beside it closes the menu.
        _card.Tapped += (_, e) => e.Handled = true;
        _card.RightTapped += (_, e) => e.Handled = true;
        Tapped += (_, e) =>
        {
            e.Handled = true;
            Close();
        };
        RightTapped += (_, e) =>
        {
            e.Handled = true;
            Close();
        };

        SizeChanged += (_, _) => Place();
        _card.SizeChanged += (_, _) => Place();

#if RPM_WINUI
        ElementCompositionPreview.GetElementVisual(this).Opacity = 0;
#else
        Opacity = 0;
        _fade = new Dissolve(this, "pegasus menu");
#endif
    }

    /// <summary>Whether a menu is open: from its opening until it starts to fade out.</summary>
    public bool IsOpen { get; private set; }

    /// <summary>The menu open now, or null.</summary>
    public MenuFlyout? Menu => IsOpen ? _menu : null;

    /// <summary>Raised when a menu opens, before anything else is done with it.</summary>
    public event Action? Opening;

    /// <summary>
    /// Opens <paramref name="menu"/> beside <paramref name="at"/>, the row or the key it is about. A menu
    /// open already is closed first, and told so. <paramref name="pointer"/> is where a right-click was, in
    /// the row's points: the card stands there instead of beside the row. <paramref name="closed"/> is
    /// called once, as the card starts to close, whether a line was chosen or not.
    /// </summary>
    public void Show(MenuFlyout menu, FrameworkElement? at, Point? pointer = null, Action? closed = null)
    {
        if (IsOpen)
        {
            var before = _closed;
            _closed = null;
            Unwatch();
            before?.Invoke();
        }

        Opening?.Invoke();

        _menu = menu;
        _at = at;
        _pointer = pointer;
        _closed = closed;
        _returnTo = StopOf(at);
        IsOpen = true;
        IsHitTestVisible = true;
        Visibility = Visibility.Visible;
        _card.IsTabStop = true;

        ShowLevel(new Level(menu.Items, parent: null), lit: _keys() ? 0 : -1, startAt: true);
        Place();
        SettleSoon();
        FadeTo(1, FadeInMs, landed: null);

        RemoteSearch.Give(_card);
        RPM.Core.Diagnostics.Scope.Said("window", $"menu card: opened with {_level?.Lines.Count ?? 0} lines");
    }

    /// <summary>
    /// Back: from a submenu, the menu it was opened from, with the submenu's line lit; from a menu, the card
    /// closes. Returns false where nothing was open.
    /// </summary>
    public bool Back()
    {
        if (!IsOpen || _level is not { } level)
            return false;

        if (level.Parent is { } parent)
        {
            var from = parent.Lines.FindIndex(l => ReferenceEquals(l.Item, level.From));
            ShowLevel(parent, from, startAt: false);
            return true;
        }

        Close();
        return true;
    }

    /// <summary>
    /// Closes the card: it fades out, and the focus goes back to the row it was opened on (or, where that
    /// row has gone, to the screen's default) as the fade starts, as on the TV. Returns false where nothing
    /// was open.
    /// </summary>
    public bool Close()
    {
        if (!IsOpen)
            return false;

        IsOpen = false;
        IsHitTestVisible = false;
        Unwatch();

        // Only a focus the card still holds is given back. One the opener has moved since is left alone.
        if (_card.FocusState != FocusState.Unfocused)
        {
            if (_returnTo is not { IsLoaded: true, IsEnabled: true, Visibility: Visibility.Visible } row
                || !RemoteSearch.Give(row))
                _focusLost();
        }
        _card.IsTabStop = false;

        var closed = _closed;
        _closed = null;

        FadeTo(0, FadeOutMs, landed: () =>
        {
            if (IsOpen)
                return;   // opened again during the fade
            Visibility = Visibility.Collapsed;
            _lines.Children.Clear();
            _level = null;
            _menu = null;
            _at = null;
        });

        closed?.Invoke();
        return true;
    }

    /// <summary>Takes the card away at once, with no fade and no focus given back, for the view being
    /// put away.</summary>
    public void Dismiss()
    {
        if (!IsOpen && Visibility == Visibility.Collapsed)
            return;

        var closed = IsOpen ? _closed : null;
        IsOpen = false;
        IsHitTestVisible = false;
        Unwatch();
        _closed = null;
        _card.IsTabStop = false;
        FadeTo(0, 0, landed: null);
        Visibility = Visibility.Collapsed;
        _lines.Children.Clear();
        _level = null;
        _menu = null;
        _at = null;
        closed?.Invoke();
    }

    /// <summary>Reads <paramref name="menu"/>'s lines again, where it is the menu open and its own lines are
    /// in the card (not a submenu), for a menu built again in place while it is up (the cast menu). The lit
    /// line stays where it was, or moves to the nearest line above it that can be lit.</summary>
    public void Refresh(MenuFlyout menu)
    {
        if (!IsOpen || !ReferenceEquals(menu, _menu) || _level is not { Parent: null } level)
            return;

        var lit = level.Lit;
        ShowLevel(new Level(menu.Items, parent: null), lit, startAt: false);
    }

    /// <summary>Moves the lit line one way (-1 up, 1 down), past rules and dim lines, and brings it into
    /// view. A card with nothing lit lights its first line. At an end it stays.</summary>
    public void Step(int way)
    {
        if (!IsOpen || _level is not { } level)
            return;

        if (level.Lit < 0)
        {
            Light(level, Next(level, -1, 1));
            return;
        }

        var next = Next(level, level.Lit, way);
        if (next >= 0)
            Light(level, next);
    }

    /// <summary>OK: chooses the lit line. Returns false where nothing is lit.</summary>
    public bool Choose()
    {
        if (!IsOpen || _level is not { Lit: >= 0 } level || level.Lit >= level.Lines.Count)
            return false;
        Choose(level.Lines[level.Lit]);
        return true;
    }

    /// <summary>Right: opens the lit line where it is a submenu.</summary>
    public void Enter()
    {
        if (IsOpen && _level is { Lit: >= 0 } level && level.Lit < level.Lines.Count
            && level.Lines[level.Lit].Item is MenuFlyoutSubItem { IsEnabled: true } sub)
            OpenSub(sub);
    }

    /// <summary>Left: leaves a submenu for the menu it was opened from. A menu itself stays open.</summary>
    public void Leave()
    {
        if (IsOpen && _level is { Parent: not null })
            Back();
    }

    // ── lines ──

    private void Choose(Line line)
    {
        switch (line.Item)
        {
            case MenuFlyoutSubItem { IsEnabled: true } sub:
                OpenSub(sub);
                return;

            case MenuFlyoutItem { IsEnabled: true } item:
                Close();
                Invoke(item);
                return;
        }
    }

    /// <summary>
    /// Does what a press on the line does in a MenuFlyout: its automation peer raises its Click, runs its
    /// Command, and toggles a ToggleMenuFlyoutItem first. The peers do the same on both heads (Uno's
    /// MenuFlyoutItemAutomationPeer.Invoke and ToggleMenuFlyoutItemAutomationPeer.Toggle call the item's
    /// own Invoke, as WinUI's do). A line that fails is noted, and the menu is closed already.
    /// </summary>
    private static void Invoke(MenuFlyoutItem item)
    {
        try
        {
            if (item is ToggleMenuFlyoutItem toggle)
                new ToggleMenuFlyoutItemAutomationPeer(toggle).Toggle();
            else
                new MenuFlyoutItemAutomationPeer(item).Invoke();
        }
        catch (Exception ex)
        {
            RPM.Core.Diagnostics.Faults.Note("pegasus", "a menu card's line: " + item.Text, ex);
        }
    }

    private void OpenSub(MenuFlyoutSubItem sub)
    {
        if (_level is not { } level)
            return;
        ShowLevel(new Level(sub.Items, level, sub), lit: _keys() ? 0 : -1, startAt: true);
    }

    /// <summary>Draws a menu or a submenu in the card. <paramref name="lit"/> is the line to light, the
    /// nearest that can be lit at or above it (with <paramref name="startAt"/>, at or below it), or none
    /// for -1.</summary>
    private void ShowLevel(Level level, int lit, bool startAt)
    {
        Unwatch();
        _lines.Children.Clear();

        var s = _scale();
        _lines.Padding = new Thickness(CardAcross * s, CardDown * s, CardAcross * s, CardDown * s);
        _lines.Spacing = LineSpacing * s;
        _glass.CornerRadius = new CornerRadius(CardCorner * s);

        var shown = level.Items.Where(i => i.Visibility == Visibility.Visible).ToList();
        var ticks = shown.Any(i => i is ToggleMenuFlyoutItem);
        var icons = shown.Any(i => IconOf(i) is not null);

        foreach (var item in shown)
        {
            var line = item is MenuFlyoutSeparator ? Rule(item, s) : Entry(item, s, ticks, icons);
            var at = level.Lines.Count;
            level.Lines.Add(line);
            _lines.Children.Add(line.Box);
            if (item is MenuFlyoutSeparator)
                continue;

            line.Box.PointerEntered += (_, _) =>
            {
                if (line.IsEntry)
                    Light(level, at);
            };
            line.Box.Tapped += (_, e) =>
            {
                e.Handled = true;
                if (line.IsEntry)
                    Choose(line);
            };
            Watch(line);
        }

        _level = level;
        level.Lit = -1;
        if (lit >= 0 && level.Lines.Count > 0)
        {
            var from = Math.Min(lit, level.Lines.Count - 1);
            var found = level.Lines[from].IsEntry ? from : Next(level, from, startAt ? 1 : -1);
            if (found < 0)
                found = Next(level, from, startAt ? -1 : 1);
            Light(level, found);
        }

        _scroll.ChangeView(null, 0, null, disableAnimation: true);
        if (level.Lit >= 0)
            DispatcherQueue?.TryEnqueue(() =>
            {
                if (ReferenceEquals(_level, level) && level.Lit >= 0)
                    Reveal(level.Lines[level.Lit]);
            });
        Place();
    }

    /// <summary>A rule between lines.</summary>
    private Line Rule(MenuFlyoutItemBase item, double s) =>
        new(item, new Border
        {
            Height = 1,
            Margin = new Thickness(RuleAcross * s, RuleDown * s, RuleAcross * s, RuleDown * s),
            Background = new SolidColorBrush(RuleColor),
            IsHitTestVisible = false,
        });

    /// <summary>One line: the tick's column where the menu ticks a line, the icons' column where any line
    /// has an icon, the words, and a chevron on a submenu.</summary>
    private Line Entry(MenuFlyoutItemBase item, double s, bool ticks, bool icons)
    {
        var row = new Grid();
        var line = new Line(item, new Border
        {
            Child = row,
            Background = _unlit,
            CornerRadius = new CornerRadius(LineCorner * s),
            Padding = new Thickness(LineAcross * s, LineDown * s, LineAcross * s, LineDown * s),
        });

        var column = 0;
        if (ticks)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(TickWide * s) });
            line.Tick = Glyph(TickGlyph, _fa, TickSize * s, HorizontalAlignment.Left);
            Grid.SetColumn(line.Tick, column++);
            row.Children.Add(line.Tick);
        }

        if (icons)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength((IconWide + IconGap) * s) });
            line.Icon = Glyph("", _fa, IconSize * s, HorizontalAlignment.Left);
            line.Icon.Width = IconWide * s;
            line.Icon.TextAlignment = TextAlignment.Center;
            Grid.SetColumn(line.Icon, column++);
            row.Children.Add(line.Icon);
        }

        row.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });
        line.Words = new TextBlock
        {
            FontSize = WordsSize * s,
            TextWrapping = TextWrapping.NoWrap,
            TextTrimming = TextTrimming.CharacterEllipsis,
            MaxLines = 1,
            VerticalAlignment = VerticalAlignment.Center,
        };
        Grid.SetColumn(line.Words, column++);
        row.Children.Add(line.Words);

        if (item is MenuFlyoutSubItem)
        {
            row.ColumnDefinitions.Add(new ColumnDefinition { Width = GridLength.Auto });
            var more = Glyph(MoreGlyph, _fa, MoreSize * s, HorizontalAlignment.Right);
            more.Margin = new Thickness(MoreGap * s, 0, 0, 0);
            more.Foreground = new SolidColorBrush(Dim);
            Grid.SetColumn(more, column);
            row.Children.Add(more);
        }

        Paint(line);
        return line;
    }

    private static TextBlock Glyph(string glyph, FontFamily face, double size, HorizontalAlignment along) => new()
    {
        Text = glyph,
        FontFamily = face,
        FontSize = size,
        HorizontalAlignment = along,
        VerticalAlignment = VerticalAlignment.Center,
        Foreground = new SolidColorBrush(Words),
    };

    /// <summary>Writes a line's words, tick, icon and dimming from its item, as it is now.</summary>
    private static void Paint(Line line)
    {
        var item = line.Item;
        if (line.Words is { } words)
        {
            words.Text = item switch
            {
                MenuFlyoutItem one => one.Text,
                MenuFlyoutSubItem sub => sub.Text,
                _ => "",
            };
            words.Foreground = new SolidColorBrush(item.IsEnabled ? Words : Dim);
        }

        if (line.Tick is { } tick)
            tick.Opacity = item is ToggleMenuFlyoutItem { IsChecked: true } ? 1 : 0;

        if (line.Icon is { } icon)
        {
            if (IconOf(item) is { } font)
            {
                icon.Text = font.Glyph;
                icon.FontFamily = font.FontFamily;
                icon.Visibility = Visibility.Visible;
            }
            else
            {
                icon.Visibility = Visibility.Collapsed;
            }
            icon.Foreground = new SolidColorBrush(item.IsEnabled ? Words : Dim);
        }
    }

    /// <summary>A line's icon where it is a glyph of a font, the only kind the menus here use.</summary>
    private static FontIcon? IconOf(MenuFlyoutItemBase item) => item switch
    {
        MenuFlyoutItem { Icon: FontIcon { Glyph.Length: > 0 } icon } => icon,
        MenuFlyoutSubItem { Icon: FontIcon { Glyph.Length: > 0 } icon } => icon,
        _ => null,
    };

    /// <summary>Draws a line again whenever its words, its tick, its icon or whether it can be chosen
    /// change while it is in the card.</summary>
    private void Watch(Line line)
    {
        var item = line.Item;
        Add(Control.IsEnabledProperty);
        switch (item)
        {
            case ToggleMenuFlyoutItem:
                Add(MenuFlyoutItem.TextProperty);
                Add(MenuFlyoutItem.IconProperty);
                Add(ToggleMenuFlyoutItem.IsCheckedProperty);
                break;
            case MenuFlyoutItem:
                Add(MenuFlyoutItem.TextProperty);
                Add(MenuFlyoutItem.IconProperty);
                break;
            case MenuFlyoutSubItem:
                Add(MenuFlyoutSubItem.TextProperty);
                Add(MenuFlyoutSubItem.IconProperty);
                break;
        }

        void Add(DependencyProperty property) =>
            _watches.Add((item, property, item.RegisterPropertyChangedCallback(property, (_, _) => Paint(line))));
    }

    private void Unwatch()
    {
        foreach (var (item, property, token) in _watches)
            item.UnregisterPropertyChangedCallback(property, token);
        _watches.Clear();
    }

    /// <summary>The next line that can be lit from <paramref name="from"/> one way, or -1.</summary>
    private static int Next(Level level, int from, int way)
    {
        for (var i = from + way; i >= 0 && i < level.Lines.Count; i += way)
            if (level.Lines[i].IsEntry)
                return i;
        return -1;
    }

    private void Light(Level level, int at)
    {
        if (at == level.Lit)
            return;
        if (level.Lit >= 0 && level.Lit < level.Lines.Count)
            level.Lines[level.Lit].Box.Background = _unlit;
        level.Lit = at;
        if (at < 0 || at >= level.Lines.Count)
            return;
        level.Lines[at].Box.Background = _lit;
        if (ReferenceEquals(level, _level))
            Reveal(level.Lines[at]);
    }

    /// <summary>Scrolls a long menu so the line is in view, with the card's padding round it.</summary>
    private void Reveal(Line line)
    {
        if (_scroll.ViewportHeight <= 0 || line.Box.ActualHeight <= 0)
            return;

        double top;
        try
        {
            top = line.Box.TransformToVisual(_lines).TransformPoint(new Point(0, 0)).Y;
        }
        catch (ArgumentException)
        {
            return;   // not laid out yet
        }

        var room = CardDown * _scale();
        var bottom = top + line.Box.ActualHeight;
        if (top - room < _scroll.VerticalOffset)
            _scroll.ChangeView(null, Math.Max(0, top - room), null);
        else if (bottom + room > _scroll.VerticalOffset + _scroll.ViewportHeight)
            _scroll.ChangeView(null, bottom + room - _scroll.ViewportHeight, null);
    }

    // ── where it stands ──

    /// <summary>The stop the focus goes back to: the row itself, or the nearest stop holding it (a track
    /// line's button, right-clicked, is inside its line).</summary>
    private static Control? StopOf(FrameworkElement? at)
    {
        for (DependencyObject? node = at; node is not null; node = VisualTreeHelper.GetParent(node))
            if (node is Control { IsTabStop: true } stop)
                return stop;
        return null;
    }

    /// <summary>The row's bounds in the card's layer, or null where it is not on screen.</summary>
    private Rect? RowRect()
    {
        if (_at is not { IsLoaded: true, ActualWidth: > 0, ActualHeight: > 0 } at)
            return null;
        try
        {
            return at.TransformToVisual(this).TransformBounds(new Rect(0, 0, at.ActualWidth, at.ActualHeight));
        }
        catch (ArgumentException)
        {
            return null;   // not in this tree
        }
    }

    /// <summary>
    /// Puts the card beside the row (or at the pointer) and cuts the row out of the blur and the shade.
    /// Called on opening, when the card's size changes, and when the view's does.
    /// </summary>
    private void Place()
    {
        var width = ActualWidth;
        var height = ActualHeight;
        if (_level is null || width <= 0 || height <= 0)
            return;

        var s = _scale();
        var px = height / 1080;
        var gap = GapPixels * px;
        var edge = EdgePixels * px;
        var row = RowRect();

        // The row's own corner, so the hole is the row's shape.
        var corner = _at switch
        {
            Control control => control.CornerRadius.TopLeft,
            Border border => border.CornerRadius.TopLeft,
            _ => 0,
        };
        _blur.Data = Holed(width, height, row, corner);
        _shade.Data = Holed(width, height, row, corner);

        // A right-click: the card stands at the pointer, as a point of a row.
        Rect? beside = row;
        if (_pointer is { } point && _at is { } at)
        {
            try
            {
                var p = at.TransformToVisual(this).TransformPoint(point);
                beside = new Rect(p.X, p.Y, 0, 0);
            }
            catch (ArgumentException)
            {
                // The row left the tree. The card stands beside where it was.
            }
        }

        _glass.MinWidth = Math.Min(CardLeast * s, Math.Max(0, width - (2 * edge)));
        _glass.MaxWidth = Math.Max(_glass.MinWidth, Math.Min(CardMost * s, width - (2 * edge)));

        // The larger of the room under the row and the room over it, so a long menu scrolls there instead
        // of covering the row it belongs to.
        var room = beside is { } r
            ? Math.Max(height - r.Bottom - (gap * 2), r.Top - (gap * 2))
            : height - (gap * 2);
        _card.MaxHeight = Math.Max(0, room);

        _card.Measure(new Size(double.PositiveInfinity, double.PositiveInfinity));
        var w = _card.DesiredSize.Width;
        var h = _card.DesiredSize.Height;

        double x, y;
        if (beside is { } b)
        {
            x = b.Left + w <= width - edge ? b.Left : b.Right - w;
            y = b.Bottom + gap + h <= height - gap ? b.Bottom + gap : Math.Max(gap, b.Top - gap - h);
        }
        else
        {
            x = (width - w) / 2;
            y = (height - h) / 2;
        }
        x = Math.Clamp(x, edge, Math.Max(edge, width - w - edge));

        Canvas.SetLeft(_card, x);
        Canvas.SetTop(_card, y);
    }

    /// <summary>How many frames are left in which the card is placed again after opening.</summary>
    private int _settleFrames;

    /// <summary>
    /// Places the card again on the next few frames. The row the menu opened on was grown by the focus look
    /// as it opened, and the look puts it back to its own size on its next frame (PegasusPlayer.OnRingFrame:
    /// nothing grows while a menu is open). The hole in the blur is then cut to the row's own size, as the
    /// TV cuts it round the row drawn bare. The focus is asked for again on those frames until the card
    /// holds it, as the TV asks (FOCUS_ASKS): a card that has only just been shown is not laid out yet,
    /// and may not take it at once.
    /// </summary>
    private void SettleSoon()
    {
        if (_settleFrames <= 0)
            CompositionTarget.Rendering += Settle;
        _settleFrames = 3;
    }

    private void Settle(object? sender, object e)
    {
        Place();
        if (IsOpen && _card.FocusState == FocusState.Unfocused)
            RemoteSearch.Give(_card);
        if (--_settleFrames <= 0)
            CompositionTarget.Rendering -= Settle;
    }

    /// <summary>The whole layer less a hole the shape of the row: the row's bounds with its corners. Even
    /// odd, so the second figure cuts the first.</summary>
    private static Geometry Holed(double width, double height, Rect? hole, double corner)
    {
        var geometry = new PathGeometry { FillRule = FillRule.EvenOdd };
        geometry.Figures.Add(Box(new Rect(0, 0, width, height), 0));
        if (hole is { Width: > 0, Height: > 0 } cut)
            geometry.Figures.Add(Box(cut, Math.Min(corner, Math.Min(cut.Width, cut.Height) / 2)));
        return geometry;
    }

    /// <summary>A closed figure round a rectangle, with round corners of <paramref name="r"/>.</summary>
    private static PathFigure Box(Rect rect, double r)
    {
        var figure = new PathFigure { StartPoint = new Point(rect.Left + r, rect.Top), IsClosed = true, IsFilled = true };
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Right - r, rect.Top) });
        Corner(rect.Right, rect.Top + r);
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Right, rect.Bottom - r) });
        Corner(rect.Right - r, rect.Bottom);
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Left + r, rect.Bottom) });
        Corner(rect.Left, rect.Bottom - r);
        figure.Segments.Add(new LineSegment { Point = new Point(rect.Left, rect.Top + r) });
        Corner(rect.Left + r, rect.Top);
        return figure;

        void Corner(double x, double y)
        {
            if (r > 0)
                figure.Segments.Add(new ArcSegment
                {
                    Point = new Point(x, y),
                    Size = new Size(r, r),
                    SweepDirection = SweepDirection.Clockwise,
                });
        }
    }

    // ── the fade ──

    /// <summary>Fades the whole layer to <paramref name="to"/>, from wherever it is, and calls
    /// <paramref name="landed"/> when it gets there, unless another fade has started since.</summary>
    private void FadeTo(double to, int ms, Action? landed)
    {
#if RPM_WINUI
        var visual = ElementCompositionPreview.GetElementVisual(this);
        var turn = ++_fadeTurn;
        if (ms <= 0)
        {
            visual.StopAnimation("Opacity");
            visual.Opacity = (float)to;
            landed?.Invoke();
            return;
        }

        var compositor = visual.Compositor;
        var ramp = compositor.CreateScalarKeyFrameAnimation();
        ramp.InsertExpressionKeyFrame(0f, "this.StartingValue");
        ramp.InsertKeyFrame(1f, (float)to);
        ramp.Duration = TimeSpan.FromMilliseconds(ms);

        // Wrapped in a batch, so the end of the fade is heard on this thread.
        var batch = compositor.CreateScopedBatch(CompositionBatchTypes.Animation);
        visual.StartAnimation("Opacity", ramp);
        batch.End();
        batch.Completed += (_, _) =>
        {
            if (turn == _fadeTurn)
                landed?.Invoke();
        };
#else
        if (ms <= 0)
        {
            _fade.Set(to);
            landed?.Invoke();
            return;
        }
        _fade.To(to, TimeSpan.FromMilliseconds(ms), landed);
#endif
    }
}
