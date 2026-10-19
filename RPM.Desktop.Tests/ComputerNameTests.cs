using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// What this machine calls itself on the network and in the sharing settings.
///
/// <para>The hostname is not used. On macOS the hostname follows DHCP, and some routers hand out a bare
/// UUID as the hostname. The app would then show that UUID to every device on the network, while macOS
/// shows the friendly computer name everywhere else.</para>
/// </summary>
public class ComputerNameTests
{
    [Fact]
    public void This_machine_has_a_name()
    {
        var name = ComputerName.Mine;

        Assert.False(string.IsNullOrWhiteSpace(name));

        // The name must not be a DHCP hostname. On macOS the answer comes from the computer name,
        // which has no domain and is never a bare id.
        if (OperatingSystem.IsMacOS())
            Assert.DoesNotContain('.', name);
    }

    /// <summary>Read once and cached. It is a setting of the machine, so it does not change while the app
    /// runs.</summary>
    [Fact]
    public void It_is_the_same_answer_every_time() =>
        Assert.Same(ComputerName.Mine, ComputerName.Mine);
}
