using RPM.Desktop.Presentation.Import;

namespace RPM.Desktop.Tests;

/// <summary>
/// What one press of Import means while the tag editor is open. The middle case matters most: a genre
/// typed into the panel but not applied would be filed with its old value, into a row that is about to
/// leave the queue, and nothing on screen would show it.
/// </summary>
public class ImportGateTests
{
    [Fact]
    public void Nothing_to_file_means_the_press_does_nothing()
    {
        Assert.Equal(ImportPress.Nothing, ImportGate.For(canImport: false, panelOnScreen: true, panelHasEdits: true));
        Assert.Equal(ImportPress.Nothing, ImportGate.For(canImport: false, panelOnScreen: false, panelHasEdits: false));
    }

    [Fact]
    public void A_clean_panel_imports_straight_away()
        => Assert.Equal(ImportPress.Import, ImportGate.For(canImport: true, panelOnScreen: true, panelHasEdits: false));

    [Fact]
    public void Unapplied_edits_in_the_open_panel_are_asked_about_first()
        => Assert.Equal(ImportPress.AskAboutTags, ImportGate.For(canImport: true, panelOnScreen: true, panelHasEdits: true));

    /// <summary>A panel that is moved aside for a narrow pane can hold edits the user cannot see. Asking
    /// about boxes that are not on screen gives the user nothing to answer.</summary>
    [Fact]
    public void Edits_in_a_panel_that_is_away_are_not_asked_about()
        => Assert.Equal(ImportPress.Import, ImportGate.For(canImport: true, panelOnScreen: false, panelHasEdits: true));
}
