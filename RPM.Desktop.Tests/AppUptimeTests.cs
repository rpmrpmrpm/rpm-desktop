using System.Diagnostics;

namespace RPM.Desktop.Tests;

/// <summary>
/// What "since launch" is measured from.
///
/// <para>The reveal gate's rule has its own tests, but the rule is only correct if the number given to
/// it is correct, so this tests the number. Do not make <see cref="App.Uptime"/> a <c>Stopwatch</c>
/// in a static field: a class with static field initialisers and no static constructor is
/// <c>beforefieldinit</c>, so the runtime may delay the initialiser until the first read of a static
/// field. That first read can be the reveal gate's own first tick, AFTER the startup it should
/// measure, and the splash then stays up longer than it should.</para>
/// </summary>
public class AppUptimeTests
{
    /// <summary>
    /// The test host has been running for a while by the time this runs, and the app's uptime has to
    /// account for all of it. A clock that starts on its first read would answer nearly zero here.
    /// </summary>
    [Fact]
    public void UptimeIsMeasuredFromTheProcessStartRatherThanFromWhoeverAsksFirst()
    {
        var sinceStart = DateTime.UtcNow - Process.GetCurrentProcess().StartTime.ToUniversalTime();

        Assert.True(
            App.Uptime >= sinceStart - TimeSpan.FromMilliseconds(250),
            $"App.Uptime said {App.Uptime.TotalMilliseconds:F0}ms, but this process has been running "
            + $"for {sinceStart.TotalMilliseconds:F0}ms. A lazily started clock reports the age of the "
            + "question instead of the age of the run.");
    }

    /// <summary>And it still moves: a constant would pass the test above once and never again.</summary>
    [Fact]
    public void UptimeAdvances()
    {
        var first = App.Uptime;
        Thread.Sleep(30);

        Assert.True(App.Uptime > first);
    }
}
