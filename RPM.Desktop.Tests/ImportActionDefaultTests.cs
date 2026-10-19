using System.Xml.Linq;

namespace RPM.Desktop.Tests;

/// <summary>
/// A first run copies the person's files by default and does not move them. Two places set the
/// default: the radio that is checked when the setup wizard's import step opens, and the record default
/// in <c>SettingsService</c> for a settings file that has no ImportAction. Both are read as text,
/// because the record is private and the radio is markup.
/// </summary>
public class ImportActionDefaultTests
{
    private static readonly string Root = FindRoot();

    private static string FindRoot()
    {
        var dir = new DirectoryInfo(AppContext.BaseDirectory);
        while (dir is not null && !Directory.Exists(Path.Combine(dir.FullName, "RPM.Desktop")))
            dir = dir.Parent;
        return dir?.FullName ?? throw new DirectoryNotFoundException("repo root not found from the test binary");
    }

    private static string Read(params string[] parts) =>
        File.ReadAllText(Path.Combine([Root, "RPM.Desktop", .. parts]));

    [Fact]
    public void TheWizardOpensWithCopyChecked()
    {
        var page = XDocument.Parse(Read("Presentation", "Setup", "Steps", "WizardImportStep.xaml"));
        XNamespace x = "http://schemas.microsoft.com/winfx/2006/xaml";

        var radios = page.Descendants()
            .Where(e => e.Name.LocalName == "RadioButton" && (string?)e.Attribute("GroupName") == "Originals")
            .ToList();
        Assert.Equal(2, radios.Count);

        var isChecked = radios.Where(r => (string?)r.Attribute("IsChecked") == "True").ToList();
        Assert.Single(isChecked);
        Assert.Equal("CopyOption", (string?)isChecked[0].Attribute(x + "Name"));
    }

    [Fact]
    public void TheSettingsDefaultIsCopy()
    {
        var code = Read("Services", "Shell", "SettingsService.cs");

        Assert.Contains("ImportAction ImportAction = ImportAction.Copy,", code);
    }
}
