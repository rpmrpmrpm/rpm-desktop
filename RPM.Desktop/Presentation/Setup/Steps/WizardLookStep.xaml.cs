using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RPM.Core.Viz;
using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step four: the background, Pegasus's visualizer, the spinning record, and whether rpm opens by itself
/// for a CD or a music file.
///
/// <para><b>Written once, on Next.</b> Each setting that notifies wakes the shell's listeners, so the
/// choices are kept here while the step is up and written together when it is left, as the import step
/// does with its own (<c>PersistChoices</c>). The two Windows settings are read from Windows when the
/// step arrives, because Windows is where they live.</para>
/// </summary>
public sealed partial class WizardLookStep : UserControl
{
    /// <summary>Next was pressed and every choice is written.</summary>
    public event EventHandler? Finished;

    private readonly SettingsService _settings = App.Services.GetRequiredService<SettingsService>();
    private readonly DiscAutoPlayService _discs = App.Services.GetRequiredService<DiscAutoPlayService>();
    private readonly MusicFileService _files = App.Services.GetRequiredService<MusicFileService>();

    private readonly WizardChoices<string> _background;
    private readonly WizardChoices<RoomStyle> _room;

    /// <summary>What Windows said when the step arrived, so Next only writes what changed.</summary>
    private bool _discWas, _filesWas;

    public WizardLookStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(5);

        // The backdrops anyone can have. The supporters' one is on the Settings page, where the ask
        // that goes with it is.
        _background = new WizardChoices<string>(Backgrounds, _settings.BuiltInBackground ?? "");
        foreach (var preset in Backdrop.Presets.Where(p => !p.Supporters))
            _background.Add(preset.Id, Loc.Get(preset.NameKey), null, preset.Uri, 190, 112);

        _room = new WizardChoices<RoomStyle>(Rooms, RoomStyles.Named(_settings.PegasusRoomStyle));
        // Each with a still of itself, taken off Pegasus with the gallery's shoot (GalleryShoot).
        _room.Add(RoomStyle.Original, Loc.Get("Tv_RoomStyleOriginal"), Loc.Get("Wizard_RoomOriginalDesc"),
            "ms-appx:///Assets/Setup/room-original.jpg", 190, 112);
        _room.Add(RoomStyle.Yang, Loc.Get("Tv_RoomStyleYang"), Loc.Get("Wizard_RoomYangDesc"),
            "ms-appx:///Assets/Setup/room-yang.jpg", 190, 112);
        _room.Add(RoomStyle.Lava, Loc.Get("Tv_RoomStyleLava"), Loc.Get("Wizard_RoomLavaDesc"),
            "ms-appx:///Assets/Setup/room-lava.jpg", 190, 112);
        _room.Add(RoomStyle.Alchemy, Loc.Get("Tv_RoomStyleAlchemy"), Loc.Get("Wizard_RoomAlchemyDesc"),
            "ms-appx:///Assets/Setup/room-alchemy.jpg", 190, 112);

        RecordToggle.IsOn = _settings.HeaderRecord;

        DiscToggle.IsEnabled = _discs.IsSupported;
        FilesToggle.IsEnabled = _files.IsSupported;
    }

    /// <summary>Called by the host once this step is on screen: reads the two Windows settings.</summary>
    public async void Arrived()
    {
        if (_discs.IsSupported)
            DiscToggle.IsOn = _discWas = await _discs.IsDefaultAsync();
        if (_files.IsSupported)
            FilesToggle.IsOn = _filesWas = await _files.IsOfferedAsync();
    }

    private async void Next_Click(object sender, RoutedEventArgs e)
    {
        NextButton.IsEnabled = false;
        try
        {
            if ((_settings.BuiltInBackground ?? "") != _background.Picked || _settings.CustomBackgroundPath is not null)
            {
                _settings.CustomBackgroundPath = null;
                _settings.BuiltInBackground = _background.Picked.Length == 0 ? null : _background.Picked;
            }
            _settings.PegasusRoomStyle = _room.Picked.ToString();
            if (_settings.HeaderRecord != RecordToggle.IsOn)
                _settings.HeaderRecord = RecordToggle.IsOn;

            if (_discs.IsSupported && DiscToggle.IsOn != _discWas)
                await _discs.SetDefaultAsync(DiscToggle.IsOn);
            if (_files.IsSupported && FilesToggle.IsOn != _filesWas)
                await _files.SetAsync(FilesToggle.IsOn);
        }
        finally
        {
            NextButton.IsEnabled = true;
        }

        Finished?.Invoke(this, EventArgs.Empty);
    }
}
