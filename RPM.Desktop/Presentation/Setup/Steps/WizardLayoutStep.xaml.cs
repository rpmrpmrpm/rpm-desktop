using Microsoft.UI.Xaml;
using Microsoft.UI.Xaml.Controls;

using RPM.Desktop.Strings;

namespace RPM.Desktop.Presentation.Setup.Steps;

/// <summary>
/// Step five: the view the library opens in, picked from a picture of each. The library header's three
/// view keys switch between them later, and the Settings page holds the same choice.
/// </summary>
public sealed partial class WizardLayoutStep : UserControl
{
    /// <summary>Next was pressed and the view is written.</summary>
    public event EventHandler? Finished;

    private readonly SettingsService _settings = App.Services.GetRequiredService<SettingsService>();
    private readonly WizardChoices<LibraryViewMode> _layout;

    public WizardLayoutStep()
    {
        InitializeComponent();
        Kicker.Text = WizardSteps.Kicker(6);

        _layout = new WizardChoices<LibraryViewMode>(Layouts, _settings.LibraryView);
        _layout.Add(LibraryViewMode.Wall, Loc.Get("Library_ViewGrid"), Loc.Get("Wizard_LayoutWallDesc"),
            "ms-appx:///Assets/Setup/layout-wall.jpg", 440, 265);
        _layout.Add(LibraryViewMode.List, Loc.Get("Library_ViewAlbums"), Loc.Get("Wizard_LayoutListDesc"),
            "ms-appx:///Assets/Setup/layout-list.jpg", 440, 265);
        _layout.Add(LibraryViewMode.Table, Loc.Get("Library_ViewTable"), Loc.Get("Wizard_LayoutTableDesc"),
            "ms-appx:///Assets/Setup/layout-table.jpg", 440, 265);
    }

    private void Next_Click(object sender, RoutedEventArgs e)
    {
        if (_settings.LibraryView != _layout.Picked)
            _settings.LibraryView = _layout.Picked;
        Finished?.Invoke(this, EventArgs.Empty);
    }
}
