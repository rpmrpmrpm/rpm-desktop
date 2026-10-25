using System.ComponentModel;
using RPM.Core.Diagnostics;
using RPM.Desktop.Presentation.RPaMp;
using RPM.Desktop.Services.Cast;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Controls.Primitives;
using Microsoft.UI.Xaml.Input;
using Microsoft.UI.Xaml.Media;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Where the music plays, from the player: the cast key in its row, and the menu that key opens.
///
/// <para>The cast key stands at the right end of the row under the record,
/// and of the foot under the lyric (CastButton, LyricCastButton). It opens a menu over the player: this
/// computer first, then every speaker found on the network. The one the music goes to is ticked. Each line
/// has a mark for what it is: this computer, a speaker, a group of speakers, or a screen. The last line
/// looks for speakers again. The menu is the main window's cast menu (RPaMp's CastFlyoutContent) at this
/// view's size and under a remote. It runs on the same model and the same service (RPaMpModel's
/// CastTargets, CastToAsync, Services/Cast). Nothing here talks to a speaker itself.</para>
///
/// <para>While the music goes to a speaker, the line at the top of the screen says where it plays, and a
/// cast that could not start, or that ended by itself, is said there for six seconds (see
/// PegasusChrome.TopLine).</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>How long a cast's failure or end stays at the top, in milliseconds. It is the same as the
    /// TV's.</summary>
    private const long CastNoticeMs = 6000;

    /// <summary>The last failure or end of a cast, and when it stops being shown.</summary>
    private string? _castNotice;
    private long _castNoticeUntil;

    /// <summary>The speaker the music goes to, by name, or null while it plays here. It is read when the
    /// model says the cast changed, not on every frame.</summary>
    private string? _castingTo;

    /// <summary>The transport whose cast is being watched. It is watched from <see cref="SetTransport"/>
    /// on, for the life of the chrome.</summary>
    private RPaMpModel? _castWatched;

    /// <summary>The cast menu while it is open, and the key it was opened from.</summary>
    private MenuFlyout? _castMenu;
    private FrameworkElement? _castMenuAt;

    /// <summary>Whether the cast menu is open. The transport stays up while it is, so the key the menu
    /// returns to is still there when it closes.</summary>
    public bool CastMenuOpen => _castMenu is not null;

    /// <summary>The marks for the menu's lines, from the app's icon font (Font Awesome Pro): a laptop for
    /// this computer, a speaker, two speakers for a group or a stereo pair, a screen with a note for a
    /// device with a picture, and the reload arrows for looking again (as on the main window's menu).
    /// </summary>
    private const string GlyphComputer = "";
    private const string GlyphSpeaker = "";
    private const string GlyphSpeakers = "";
    private const string GlyphScreen = "";
    private const string GlyphAgain = "";

    /// <summary>Words in the model names of Cast devices with a screen, for a device that does not say
    /// whether it has a picture out. The TV app uses the same words (CastDevices.SCREEN_MODELS).</summary>
    private static readonly string[] ScreenModels = ["chromecast", "tv", "shield", "hub", "display"];

    /// <summary>Starts watching a transport's cast: whether it is on, what failed, and what was found. Called
    /// from <see cref="SetTransport"/>. The same transport is watched once.</summary>
    private void WatchCast(RPaMpModel amp)
    {
        if (ReferenceEquals(_castWatched, amp))
            return;
        if (_castWatched is { } old)
            old.PropertyChanged -= OnCastModelChanged;
        _castWatched = amp;
        amp.PropertyChanged += OnCastModelChanged;
        PaintCast();
    }

    private void OnCastModelChanged(object? sender, PropertyChangedEventArgs e)
    {
        // The model raises these on the UI thread. A change that arrives from anywhere else is moved there,
        // because everything below writes to the screen.
        if (DispatcherQueue is { HasThreadAccess: false } queue)
        {
            queue.TryEnqueue(() => OnCastModelChanged(sender, e));
            return;
        }

        switch (e.PropertyName)
        {
            case nameof(RPaMpModel.CastError):
                // A failure, or a cast that ended by itself (the speaker went away, or stopped playing). The
                // model keeps the line until the next cast; the top of the screen keeps it for six seconds.
                if (_amp?.CastError is { Length: > 0 } line)
                {
                    _castNotice = line;
                    _castNoticeUntil = Environment.TickCount64 + CastNoticeMs;
                }
                break;

            case nameof(RPaMpModel.IsCasting):
                PaintCast();
                RefreshCastMenu();
                break;

            case nameof(RPaMpModel.CastTargets):
            case nameof(RPaMpModel.IsScanningForCastDevices):
                RefreshCastMenu();
                break;
        }
    }

    /// <summary>Dresses both cast keys for where the music goes: the outline in the keys' white while it
    /// plays here, the filled screen in the cast cyan while it goes to a speaker. The tooltip names the
    /// speaker.</summary>
    private void PaintCast()
    {
        var casting = _amp is { IsCasting: true };
        _castingTo = casting
            ? App.Services.GetRequiredService<Services.Audio.PlaybackService>().CastDevice?.Name
            : null;

        var idle = casting ? Visibility.Collapsed : Visibility.Visible;
        var on = casting ? Visibility.Visible : Visibility.Collapsed;
        CastIdle.Visibility = LyricCastIdle.Visibility = idle;
        CastOn.Visibility = LyricCastOn.Visibility = on;

        var tip = _amp?.CastTooltip ?? Strings.Loc.Get("Rpm_Cast_PlayOn");
        IconName.Set(CastButton, tip);
        IconName.Set(LyricCastButton, tip);
    }

    // ── the menu ──

    private void Cast_Click(object sender, RoutedEventArgs e)
    {
        if (sender is FrameworkElement key)
            OpenCastMenu(key);
        WakeTransport();
    }

    /// <summary>
    /// Opens the cast menu over the key that was pressed, and looks for speakers.
    ///
    /// <para>The speakers are looked for every time the menu opens, as the main window's menu does. They
    /// come and go, and a list kept from before would offer a speaker that has been switched off. The look
    /// takes a few seconds. Until it is done the menu says so, and it fills in when the answer comes (see
    /// <see cref="RefreshCastMenu"/>).</para>
    /// </summary>
    private void OpenCastMenu(FrameworkElement at)
    {
        if (_amp is not { } amp)
            return;

        _castMenu?.Hide();

        // The look starts first, so the menu opens saying that it is looking.
        _ = amp.RefreshCastTargetsAsync();

        var menu = new MenuFlyout { Placement = FlyoutPlacementMode.Top };
        menu.MenuFlyoutPresenterStyle = new Style(typeof(MenuFlyoutPresenter))
        {
            Setters = { new Setter(FrameworkElement.MaxWidthProperty, MenuDesktopMaxWidth * MenuFontSize / 12) },
        };
        FillCastMenu(menu, amp, at);

        _castMenu = menu;
        _castMenuAt = at;
        ShowMenu(menu, at, closed: () =>
        {
            if (!ReferenceEquals(_castMenu, menu))
                return;
            _castMenu = null;
            _castMenuAt = null;
            WakeTransport();   // the five quiet seconds start again from here, with the key on screen
        });
    }

    /// <summary>
    /// Puts what was found into the open menu: a speaker that answered, the tick moving to the speaker now
    /// in use, or the end of the look. Does nothing while the menu is closed. The lines are built again in
    /// place, and the card reads them again (see PegasusMenuCard.Refresh). The lit line stays at the same
    /// place, or the nearest above it that can be lit: the menu may have become shorter, and a note or a
    /// divider cannot be lit.
    /// </summary>
    private void RefreshCastMenu()
    {
        if (_castMenu is not { } menu || _castMenuAt is not { } at || _amp is not { } amp)
            return;

        menu.Items.Clear();
        FillCastMenu(menu, amp, at);
        MenuCard?.Refresh(menu);
    }

    /// <summary>The menu's lines: this computer, the speakers found (or a note while there are none), a
    /// divider, and the line that looks again. <paramref name="at"/> is the key the menu was opened
    /// over, where Look again opens it again.</summary>
    private void FillCastMenu(MenuFlyout menu, RPaMpModel amp, FrameworkElement at)
    {
        menu.Items.Add(Choice(Strings.Loc.Get("Cast_ThisComputer"), GlyphComputer, ticked: !amp.IsCasting,
            () => _ = CastToAsync(amp, null)));

        if (amp.CastTargets.Count == 0)
        {
            // Looking for speakers, or none found. It is a line that cannot be pressed, so the remote passes
            // over it.
            menu.Items.Add(Line(amp.CastEmptyText, glyph: null, enabled: false));
        }
        else
        {
            foreach (var target in amp.CastTargets.ToList())
            {
                // A group is named as one, as the TV names it, because a group is what has to be chosen once
                // its speakers are paired.
                var name = target.Device.IsGroup
                    ? $"{target.Name} ({Strings.Loc.Get("Rpm_Cast_Group")})"
                    : target.Name;
                menu.Items.Add(Choice(name, GlyphFor(target.Device), ticked: target.IsCurrent,
                    () => _ = CastToAsync(amp, target)));
            }
        }

        menu.Items.Add(new MenuFlyoutSeparator());

        // A menu closes on any line, so Look again opens it again at once, over the same key. The look starts
        // with it (see OpenCastMenu).
        var again = Line(Strings.Loc.Get("Rpm_Cast_LookAgain"), GlyphAgain, enabled: true);
        again.Click += (_, _) => DispatcherQueue?.TryEnqueue(() => OpenCastMenu(at));
        menu.Items.Add(again);
    }

    /// <summary>
    /// Sends the music to a speaker, or back to this computer when <paramref name="target"/> is null. As
    /// the main window's menu does, it first asks to let rpm through Windows Firewall where the firewall
    /// blocks it. Without that, the speaker cannot fetch the stream. A cast that does not start says why
    /// through the model, and the line at the top shows it.
    /// </summary>
    private async Task CastToAsync(RPaMpModel amp, CastTarget? target)
    {
        try
        {
            if (target is null)
            {
                await amp.CastToAsync(null);
                return;
            }

            if (await Dialogs.FirewallDialog.BeforeCastingAsync(XamlRoot, target.Device.Name, amp.LastCastNeverFetched))
                await amp.CastToAsync(target);
        }
        catch (Exception ex)
        {
            // The model reports a cast that failed on its own (see RPaMpModel.CastToAsync). Anything that
            // still escapes is written down, and the music stays where it was.
            Faults.Note("cast", "casting from Pegasus", ex);
        }
    }

    /// <summary>A line that can be ticked, with its mark, at this view's menu size.</summary>
    private ToggleMenuFlyoutItem Choice(string text, string glyph, bool ticked, Action chosen)
    {
        var factor = MenuFontSize / 12;
        var item = new ToggleMenuFlyoutItem
        {
            Text = text,
            IsChecked = ticked,
            FontSize = 12 * factor,
            MinHeight = 26 * factor,
            Padding = new Thickness(11 * factor, 3 * factor, 11 * factor, 4 * factor),
            Icon = Mark(glyph, factor),
        };
        // The tick is the model's to set, not the click's. A press on the line in use must not take its
        // tick away, so the tick is put back before the menu closes.
        item.Click += (_, _) =>
        {
            item.IsChecked = ticked;
            chosen();
        };
        return item;
    }

    /// <summary>A plain line, with its mark where it has one, at this view's menu size.</summary>
    private MenuFlyoutItem Line(string text, string? glyph, bool enabled)
    {
        var factor = MenuFontSize / 12;
        return new MenuFlyoutItem
        {
            Text = text,
            IsEnabled = enabled,
            FontSize = 12 * factor,
            MinHeight = 26 * factor,
            Padding = new Thickness(11 * factor, 3 * factor, 11 * factor, 4 * factor),
            Icon = glyph is null ? null : Mark(glyph, factor),
        };
    }

    /// <summary>A line's mark, from the icon font, at the menu's size.</summary>
    private FontIcon Mark(string glyph, double factor) => new()
    {
        FontFamily = (FontFamily)Resources["PegasusFa"],
        FontSize = 12 * factor,
        Glyph = glyph,
    };

    /// <summary>
    /// What a device is, as a mark: a group or a stereo pair is two speakers, a device with a picture out
    /// (a Chromecast, a Google TV, a television with Cast built in) is a screen with a note on it, and
    /// anything else is a speaker. Whether it has a picture is read from what it advertises (the "ca"
    /// value's video bit), and by the words in its model's name where it advertises nothing, as on the TV.
    /// </summary>
    private static string GlyphFor(CastDevice device)
    {
        if (device.IsGroup)
            return GlyphSpeakers;
        return HasScreen(device) ? GlyphScreen : GlyphSpeaker;
    }

    private static bool HasScreen(CastDevice device)
    {
        if (device.Receiver?.ExtraInformation is { } said
            && said.TryGetValue("ca", out var ca)
            && int.TryParse(ca, out var bits) && bits != 0)
            return (bits & 1) != 0;

        return !device.HasNoScreen
               && ScreenModels.Any(word => device.Model.Contains(word, StringComparison.OrdinalIgnoreCase));
    }
}
