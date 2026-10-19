using System.Xml.Linq;
using RPM.Core.Diagnostics;

namespace RPM.Desktop.Tests;

/// <summary>
/// A Release build of the app reads no RPM_* environment switch. The gate is an AppContext switch that
/// the Release configuration sets. These tests check both the setting and what it does.
/// </summary>
public class DevSwitchTests
{
    [Fact]
    public void The_Release_configuration_of_the_app_turns_the_switches_off()
    {
        var csproj = XDocument.Load(Path.Combine(ReleaseToolsTests.RepoRoot(), "RPM.Desktop", "RPM.Desktop.csproj"));

        var option = csproj.Descendants("RuntimeHostConfigurationOption")
            .SingleOrDefault(o => (string?)o.Attribute("Include") == DevSwitch.AppContextName);

        Assert.NotNull(option);
        Assert.Equal("false", (string?)option.Attribute("Value"));
        Assert.Equal("'$(Configuration)' == 'Release'", (string?)option.Parent?.Attribute("Condition"));
    }

    [Fact]
    public void A_switch_reads_as_unset_once_the_app_turns_them_off_and_as_set_otherwise()
    {
        const string name = "RPM_DEVSWITCH_TEST";
        Environment.SetEnvironmentVariable(name, "1");
        try
        {
            AppContext.SetSwitch(DevSwitch.AppContextName, false);
            Assert.Null(DevSwitch.Read(name));
            Assert.False(DevSwitch.IsOn(name));

            AppContext.SetSwitch(DevSwitch.AppContextName, true);
            Assert.Equal("1", DevSwitch.Read(name));
            Assert.True(DevSwitch.IsOn(name));
        }
        finally
        {
            AppContext.SetSwitch(DevSwitch.AppContextName, true);
            Environment.SetEnvironmentVariable(name, null);
        }
    }
}
