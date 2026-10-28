using RPM.Core.Analysis;
using RPM.Desktop.Services;
using RPM.Desktop.Strings;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.UI.Dispatching;
using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;
using Microsoft.UI.Xaml.Media;
using Windows.UI;

namespace RPM.Desktop.Presentation.Settings;

/// <summary>
/// Which speakers the music comes out of.
///
/// <para>There are two answers. The first, and the default, is to follow the machine. If a Bluetooth
/// speaker is connected, or the output is changed in the system's own settings, rpm changes with it,
/// even in the middle of a track. The other answer pins the app to one named output, whatever the rest
/// of the system does. This is for someone whose audio interface is the one they mix on, while the
/// system default is a laptop speaker.</para>
///
/// <para>A pinned device that is not connected right now is not an error, and it is not forgotten. The
/// music plays on the system's output, and moves back as soon as the chosen device reappears.</para>
/// </summary>
public sealed partial class SoundSettingsPage : Page
{
    private readonly SettingsService _settings;
    private readonly PlaybackService _playback;
    private readonly EqService _eq;
    private readonly LoudnessService _loudness;
    private readonly DispatcherQueue _dispatcher = DispatcherQueue.GetForCurrentThread();

    /// <summary>The pre-amp comes first, then one slider for each band, in <see
    /// cref="PlaybackService.EqBands"/> order.</summary>
    private readonly List<Slider> _faders = [];

    /// <summary>Set while the picker is being filled, so seeding it does not read as a choice.</summary>
    private bool _loading;

    /// <summary>The device each row stands for, in the picker's order. Null means follow the system. It
    /// is the first row, because the names cannot carry that by themselves.</summary>
    private readonly List<string?> _devices = [];

    public SoundSettingsPage(IServiceProvider services)
    {
        _settings = services.GetRequiredService<SettingsService>();
        _playback = services.GetRequiredService<PlaybackService>();
        _eq = services.GetRequiredService<EqService>();
        _loudness = services.GetRequiredService<LoudnessService>();

        InitializeComponent();

        BuildFaders();

        // Filling a picker is not a choice. The handlers are attached before the rows exist, so the
        // guard must be up from the first row added, not from Render. A selection raised over an empty
        // picker reads as index 0. Index 0 of the levelling picker is Off. So this is the one silent
        // way this page could turn levelling off for someone who never touched it.
        _loading = true;

        foreach (var preset in EqService.Presets)
            PresetCombo.Items.Add(preset.Label);

        // In the enum's own order, so the index is the mode, and there is no table to keep in step.
        NormalizeCombo.Items.Add(Loc.Get("Settings_NormalizeOff"));
        NormalizeCombo.Items.Add(Loc.Get("Settings_NormalizeAlbum"));
        NormalizeCombo.Items.Add(Loc.Get("Settings_NormalizeTrack"));

        // Draws the controls from the current settings. Render lowers the guard again at the end.
        Render();

        // The measuring pass runs in the background. This page is where someone watches it work through
        // a library. The event is raised off the UI thread, so the handler goes back to the UI thread
        // first.
        _loudness.Progressed += OnMeasured;
        Unloaded += (_, _) => _loudness.Progressed -= OnMeasured;

        // The curve moves on its own. A change of speaker brings that endpoint's own settings, and the
        // player's skin turns the same knobs.
        _eq.Changed += OnEqChanged;
        Unloaded += (_, _) => _eq.Changed -= OnEqChanged;

        // The list is a snapshot of what the machine had when it was read. This page is opened because
        // someone has just plugged something in, so opening the picker reads the list again.
        DeviceCombo.DropDownOpened += (_, _) => Render();
    }

    /// <summary>The pre-amp and the ten bands, built in code rather than written out: one column each,
    /// with the slider above its label. All of them share one handler.</summary>
    private void BuildFaders()
    {
        var labels = new List<string> { Loc.Get("Settings_EqPre") };
        labels.AddRange(PlaybackService.EqBands.Select(hz => hz >= 1000 ? $"{hz / 1000}K" : hz.ToString()));

        for (int i = 0; i < labels.Count; i++)
        {
            BandGrid.ColumnDefinitions.Add(new ColumnDefinition { Width = new GridLength(1, GridUnitType.Star) });

            var index = i;
            var fader = new Slider
            {
                Orientation = Orientation.Vertical,
                Minimum = -12,
                Maximum = 12,
                StepFrequency = 1,
                Height = 110,
                HorizontalAlignment = HorizontalAlignment.Center,
                IsThumbToolTipEnabled = false,
            };
            fader.ValueChanged += (_, e) => OnFaderMoved(index, e.NewValue);

            var cell = new StackPanel { HorizontalAlignment = HorizontalAlignment.Center };
            cell.Children.Add(fader);
            cell.Children.Add(new TextBlock
            {
                Text = labels[i],
                FontSize = 9,
                Foreground = new SolidColorBrush(Color.FromArgb(0x99, 0xFF, 0xFF, 0xFF)),
                HorizontalAlignment = HorizontalAlignment.Center,
                Margin = new Thickness(0, 2, 0, 0),
            });

            Grid.SetColumn(cell, i);
            BandGrid.Children.Add(cell);
            _faders.Add(fader);
        }
    }

    private void Render()
    {
        _loading = true;

        var chosen = _settings.AudioDevice;
        var available = OpenAlSink.AvailableDevices();

        _devices.Clear();
        DeviceCombo.Items.Clear();

        _devices.Add(null);
        DeviceCombo.Items.Add(Loc.Get("Settings_OutputSystem"));

        foreach (var device in available)
        {
            _devices.Add(device);
            DeviceCombo.Items.Add(device);
        }

        // A chosen device that is not here right now is still shown as chosen. Otherwise the picker
        // would quietly show "follow the system" for a setting that says otherwise.
        var missing = chosen is { Length: > 0 } && !available.Contains(chosen);
        if (missing)
        {
            _devices.Add(chosen);
            DeviceCombo.Items.Add(chosen);
        }

        DeviceCombo.SelectedIndex = Math.Max(0, _devices.IndexOf(chosen));
        MissingText.Visibility = missing ? Visibility.Visible : Visibility.Collapsed;

        var playing = _playback.OutputDeviceName;
        CurrentText.Text = playing is { Length: > 0 } ? Loc.Format("Settings_OutputNow", playing) : "";
        CurrentText.Visibility = playing is { Length: > 0 } ? Visibility.Visible : Visibility.Collapsed;

        RenderEq();
        RenderNormalize();
        MixtapeSwitch.IsOn = _settings.MixtapeAfter;

        _loading = false;
    }

    /// <summary>Counts from the next list that ends: the transport reads the setting when a list's last song
    /// starts.</summary>
    private void OnMixtapeToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;
        _settings.MixtapeAfter = MixtapeSwitch.IsOn;
    }

    /// <summary>
    /// The levelling controls, and how far the measuring pass has got.
    ///
    /// <para>The classical exception is hidden, not disabled, when levelling is off entirely. It is a
    /// rule about which records the mode reaches. A rule about nothing is not worth showing, even
    /// greyed out.</para>
    /// </summary>
    private void RenderNormalize()
    {
        var was = _loading;
        _loading = true;

        NormalizeCombo.SelectedIndex = (int)_settings.NormalizeMode;

        bool on = _settings.NormalizeMode is not NormalizeMode.Off;
        ClassicalCheck.IsChecked = _settings.NormalizeClassical;
        ClassicalRow.Visibility = on ? Visibility.Visible : Visibility.Collapsed;

        // Shown only while there is something left to measure. A finished shelf has nothing to report,
        // and a line that says "0 remaining" is about nothing. If the count is unanswered, the line is
        // hidden. The problem screen is then shown over the app.
        int pending = on ? _loudness.Pending.Match(waiting => waiting, _ => 0) : 0;
        MeasuringText.Text = pending > 0 ? Loc.Format("Settings_NormalizeMeasuring", pending) : "";
        MeasuringText.Visibility = pending > 0 ? Visibility.Visible : Visibility.Collapsed;

        _loading = was;
    }

    private void OnMeasured() => _dispatcher.TryEnqueue(RenderNormalize);

    private void OnNormalizeModeSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || NormalizeCombo.SelectedIndex < 0)
            return;

        _settings.NormalizeMode = (NormalizeMode)NormalizeCombo.SelectedIndex;
        RenderNormalize();
    }

    private void OnNormalizeClassicalToggled(object sender, RoutedEventArgs e)
    {
        if (_loading)
            return;

        _settings.NormalizeClassical = ClassicalCheck.IsChecked == true;
    }

    /// <summary>The knobs, read from the service. The whole method runs under <see cref="_loading"/>,
    /// so filling them is not mistaken for the user turning them.</summary>
    private void RenderEq()
    {
        var was = _loading;
        _loading = true;

        EqSwitch.IsOn = _eq.On;

        _faders[0].Value = _eq.Preamp;
        for (int i = 0; i < _eq.Bands.Count && i + 1 < _faders.Count; i++)
            _faders[i + 1].Value = _eq.Bands[i];

        // The preset picker shows nothing once a slider has been moved. A preset is a starting point,
        // not a state. Leaving "Rock" selected over a curve that nobody would call Rock would be a
        // small lie.
        PresetCombo.SelectedIndex = MatchingPreset();

        CurveForText.Text = _eq.Device is { Length: > 0 } device
            ? Loc.Format("Settings_EqFor", device)
            : Loc.Get("Settings_EqForCast");

        _loading = was;
    }

    /// <summary>Which preset the sliders currently match, or -1 for a custom curve.</summary>
    private int MatchingPreset()
    {
        for (int p = 0; p < EqService.Presets.Length; p++)
        {
            var gains = EqService.Presets[p].Gains;
            var same = true;

            for (int i = 0; i < _eq.Bands.Count && same; i++)
                same = Math.Abs(_eq.Bands[i] - (i < gains.Length ? gains[i] : 0)) < 0.001;

            if (same) return p;
        }

        return -1;
    }

    private void OnEqChanged() => _dispatcher.TryEnqueue(RenderEq);

    private void OnFaderMoved(int index, double db)
    {
        if (_loading) return;

        if (index == 0) _eq.Preamp = db;
        else _eq.SetBand(index - 1, db);
    }

    private void OnEqToggled(object sender, RoutedEventArgs e)
    {
        if (_loading) return;
        _eq.On = EqSwitch.IsOn;
    }

    private void OnPresetSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || PresetCombo.SelectedIndex < 0) return;

        _eq.ApplyPreset(EqService.Presets[PresetCombo.SelectedIndex].Gains);

        // A preset is a curve worth hearing, so choosing one turns the equaliser on. Picking "Bass
        // Boost" and hearing no change would look like a broken control.
        _eq.On = true;
    }

    private void OnEqReset(object sender, RoutedEventArgs e) => _eq.Reset();

    private void OnDeviceSelected(object sender, SelectionChangedEventArgs e)
    {
        if (_loading || DeviceCombo.SelectedIndex < 0 || DeviceCombo.SelectedIndex >= _devices.Count)
            return;

        var device = _devices[DeviceCombo.SelectedIndex];

        _settings.AudioDevice = device;
        _playback.SetOutputDevice(device);

        // The sink takes a moment to reflect the new device. The pump moves it on its next block. So
        // the line below is left until the next time the page is drawn, rather than showing a state
        // that is not yet true.
        MissingText.Visibility = Visibility.Collapsed;
    }
}
