using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step one: the language and the country.
///
/// <para><b>The language is applied as soon as it is picked.</b> The wizard is rebuilt in it straight
/// away (<see cref="LanguagePicked"/>), so the rest of setup is read in the language just chosen. The
/// country is written when the step is left. It is only read by the radio, and nothing on screen
/// changes with it.</para>
/// </summary>
public sealed partial class WizardRegionStep : UserControl
{
    /// <summary>A language other than the one on screen was picked, by tag. It is already stored.</summary>
    public event EventHandler<string>? LanguagePicked;

    /// <summary>Next was pressed. The country is stored.</summary>
    public event EventHandler? Finished;

    private readonly SettingsService _settings = App.Services.GetRequiredService<SettingsService>();

    /// <summary>The language on screen, so picking it again does not rebuild the wizard.</summary>
    private readonly string _speaking;

    /// <summary>False while the boxes are filled, so filling them is not read as a pick.</summary>
    private bool _ready;

    public WizardRegionStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(1);

        _speaking = AppLanguage.Current(_settings);
        foreach (var tag in AppLanguage.Offered)
        {
            LanguageBox.Items.Add(new ComboBoxItem { Content = AppLanguage.NativeName(tag), Tag = tag });
            if (tag == _speaking)
                LanguageBox.SelectedIndex = LanguageBox.Items.Count - 1;
        }

        var home = AppCountry.Code(_settings);
        foreach (var country in AppCountry.All())
        {
            CountryBox.Items.Add(new ComboBoxItem { Content = country.Name, Tag = country.Code });
            if (country.Code == home)
                CountryBox.SelectedIndex = CountryBox.Items.Count - 1;
        }

        _ready = true;
    }

    private void Language_Changed(object sender, SelectionChangedEventArgs e)
    {
        if (!_ready || LanguageBox.SelectedItem is not ComboBoxItem { Tag: string tag } || tag == _speaking)
            return;

        // The country picked so far goes with the rebuild, or it would be lost with this step.
        StoreCountry();
        _settings.Language = tag;
        AppLanguage.Apply(tag);
        LanguagePicked?.Invoke(this, tag);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        // The language is not written here. One that was never changed keeps following Windows.
        StoreCountry();
        Finished?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Stores the picked country, unless it is the one Windows would give anyway. Leaving it
    /// unset keeps following Windows, which is what someone who did not change it chose.</summary>
    private void StoreCountry()
    {
        if (CountryBox.SelectedItem is not ComboBoxItem { Tag: string code })
            return;
        _settings.Country = code == AppCountry.SystemCode() && _settings.Country is null ? null : code;
    }
}
