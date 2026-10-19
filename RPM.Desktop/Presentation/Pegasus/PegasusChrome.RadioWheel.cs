using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Input;
using Loc = RPM.Desktop.Strings.Loc;

namespace RPM.Desktop.Presentation.Pegasus;

/// <summary>
/// Radio's wheel: where radio can go, down the right of the screen over radio blurred behind it. It opens
/// when Right is held on the remote. It turns like a carousel, with a marker styled like the radio
/// tuner's (the blue arrow). Up and Down turn it, and nothing behind it is controlled.
///
/// <list type="bullet">
/// <item><b>Opened</b> by Right held for two seconds anywhere on radio (see PegasusPlayer.SideKey). The
/// press moves the focus one step as any Right does; while the key stays down nothing else moves, so the
/// row does not walk under the listener waiting for the wheel. Right at the end of a row only bounces,
/// and does not open the wheel: it waits for the two-second hold like any other Right.</item>
/// <item><b>While it is open</b> the shell gives it every arrow, OK and the Menu key (see
/// PegasusPlayer.RadioWheelKey), so the focus behind it never moves: Up and Down turn it, OK opens the
/// entry on the marker, Left closes it. Back closes it too, and so does a press on the ground.</item>
/// <item><b>The entries</b> are Tuner, Search, Music, News and Local. Search opens radio's
/// search (see PegasusChrome.RadioSearch); the other four open the tuner (see PegasusChrome.RadioTuner),
/// Music with the list of music genres open, News listing news stations and Local listing the stations of
/// the listener's country.</item>
/// </list>
///
/// <para>The wheel opens on the entry last opened, so a listener going back to the tuner finds it on the
/// marker.</para>
/// </summary>
public sealed partial class PegasusChrome
{
    /// <summary>What the wheel's entries open, top to bottom.</summary>
    private enum WheelEntry
    {
        Tuner,
        Search,
        Music,
        News,
        Local,
    }

    private static readonly WheelEntry[] WheelEntries =
        [WheelEntry.Tuner, WheelEntry.Search, WheelEntry.Music, WheelEntry.News, WheelEntry.Local];

    private RadioWheel _radioWheel = null!;
    private Fader _radioWheelFade = null!;

    /// <summary>The entry last opened, which the wheel opens on next time.</summary>
    private int _wheelPicked;

    /// <summary>How long the wheel takes to come up and to go: the side menu's figures.</summary>
    private const int WheelInMs = MenuInMs;
    private const int WheelOutMs = MenuOutMs;

    /// <summary>Whether the wheel is open, or opening. While it is, the shell hands it the keys.</summary>
    public bool RadioWheelOpen { get; private set; }

    /// <summary>Builds the wheel into its layer. Once, from BuildRadio.</summary>
    private void BuildRadioWheel()
    {
        _radioWheelFade = new Fader(RadioWheelLayer);
        _radioWheel = new RadioWheel();
        _radioWheel.SetEntries([.. WheelEntries.Select(NameOf)]);
        RadioWheelHost.Children.Add(_radioWheel);

        // The mouse wheel turns it as Up and Down do.
        RadioWheelLayer.PointerWheelChanged += (_, e) =>
        {
            e.Handled = true;
            var delta = e.GetCurrentPoint(RadioWheelLayer).Properties.MouseWheelDelta;
            if (delta != 0)
                TurnRadioWheel(delta > 0 ? -1 : 1);
        };

        static string NameOf(WheelEntry entry) => entry switch
        {
            WheelEntry.Tuner => Loc.Get("Viz_RadioTuner"),
            WheelEntry.Search => Loc.Get("Viz_MenuSearch"),
            WheelEntry.Music => Loc.Get("Viz_RadioMusic"),
            WheelEntry.News => Loc.Get("Viz_RadioNews"),
            _ => Loc.Get("Viz_RadioLocal"),
        };
    }

    /// <summary>
    /// Opens the wheel or closes it: a fade either way, out of reach of the pointer as it starts closing,
    /// and collapsed once the fade out lands if it has not been opened again meanwhile. Opened only over
    /// radio with nothing else over it. The focus is not moved either way: while the wheel is open nothing
    /// behind it takes a key, and when it closes the focus is where it was.
    /// </summary>
    public void ShowRadioWheel(bool open)
    {
        if (RadioWheelOpen == open || (open && !RadioHomeInFront))
            return;

        RadioWheelOpen = open;
        RadioWheelLayer.IsHitTestVisible = open;
        RPM.Core.Diagnostics.Scope.Said("radio", open ? "wheel opened" : "wheel closed");

        if (open)
        {
            _radioWheel.Jump(_wheelPicked);
            RadioWheelLayer.Visibility = Visibility.Visible;
            _radioWheelFade.To(1, WheelInMs, null);
            return;
        }

        _radioWheelFade.To(0, WheelOutMs, () =>
        {
            if (!RadioWheelOpen)
                RadioWheelLayer.Visibility = Visibility.Collapsed;
        });
    }

    /// <summary>Turns the wheel one entry up (-1) or down (1).</summary>
    public void TurnRadioWheel(int by)
    {
        if (RadioWheelOpen)
            _radioWheel.Turn(by);
    }

    /// <summary>OK on the wheel: it closes, and the entry on the marker opens.</summary>
    public void PickRadioWheel()
    {
        if (!RadioWheelOpen)
            return;

        var index = Math.Clamp(_radioWheel.Selected, 0, WheelEntries.Length - 1);
        _wheelPicked = index;
        var entry = WheelEntries[index];
        RPM.Core.Diagnostics.Scope.Said("radio", $"wheel: {entry}");

        ShowRadioWheel(false);
        switch (entry)
        {
            case WheelEntry.Search:
                ShowRadioSearch(true);
                break;
            case WheelEntry.Music:
                OpenTuner(TunerOpening.Music);
                break;
            case WheelEntry.News:
                OpenTuner(TunerOpening.News);
                break;
            case WheelEntry.Local:
                OpenTuner(TunerOpening.Local);
                break;
            default:
                OpenTuner(TunerOpening.AsLeft);
                break;
        }
    }

    /// <summary>A press on the blurred ground beside the wheel closes it.</summary>
    private void RadioWheelGround_Tapped(object sender, TappedRoutedEventArgs e)
    {
        e.Handled = true;
        ShowRadioWheel(false);
    }

    /// <summary>One frame of a turn. The wheel repaints only while it moves, which it can only do while it
    /// is open or fading out.</summary>
    private void TickRadioWheel(double dt) => _radioWheel?.Tick(dt);

    /// <summary>The wheel's strip down the right, at the collection's scale.</summary>
    private void SizeRadioWheel(double scale, double height)
    {
        if (_radioWheel is null)
            return;
        RadioWheelHost.Width = 520 * scale;
        RadioWheelHost.Height = height;
        _radioWheel.Sizing = scale;
    }
}
