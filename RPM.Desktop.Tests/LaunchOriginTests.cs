using RPM.Desktop.Services.Platform;

namespace RPM.Desktop.Tests;

/// <summary>
/// The startup line that says who started the process. Ring lines are sent with crash reports, so the
/// line must never contain the value of an argument.
/// </summary>
public class LaunchOriginTests
{
    [Fact]
    public void The_line_names_this_process_and_its_parent()
    {
        var line = LaunchOrigin.Describe();

        Assert.StartsWith($"process {Environment.ProcessId}, started by ", line);
        if (!OperatingSystem.IsWindows())
            Assert.DoesNotContain("started by unknown", line);
    }

    [Fact]
    public void No_argument_value_is_written()
    {
        var line = LaunchOrigin.Describe();

        // Numbers are skipped. The test host passes its parent's process id as an argument, and the
        // line names that process by its id on purpose.
        foreach (var argument in Environment.GetCommandLineArgs().Skip(1).Where(a => !a.All(char.IsDigit)))
        {
            if (argument.StartsWith("--", StringComparison.Ordinal))
            {
                if (argument.Split('=', 2) is [_, var value] && value.Length > 3 && !value.All(char.IsDigit))
                    Assert.DoesNotContain(value, line);
                continue;
            }
            if (argument.Length > 3)
                Assert.DoesNotContain(argument, line);
        }
    }
}
