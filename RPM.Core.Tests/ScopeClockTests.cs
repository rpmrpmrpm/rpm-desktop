using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

public class ScopeClockTests
{
    [Fact]
    public void Nanos_MovesForwardAndIsNotCoarse()
    {
        var a = ScopeClock.Nanos();
        var b = ScopeClock.Nanos();
        Assert.True(b >= a, $"went backwards: {a} then {b}");

        // A hundred reads must span less than a millisecond, or this is not a clock a tracer can use.
        var start = ScopeClock.Nanos();
        for (var i = 0; i < 100; i++)
            _ = ScopeClock.Nanos();
        Assert.True(ScopeClock.Nanos() - start < 1_000_000);
    }

    [Fact]
    public void Nanos_AgreesWithStopwatchOverASleep()
    {
        var n0 = ScopeClock.Nanos();
        Thread.Sleep(50);
        var elapsed = (ScopeClock.Nanos() - n0) / 1_000_000.0;
        Assert.InRange(elapsed, 40, 200);
    }

    [Fact]
    public void UnixNanos_IsNearNow()
    {
        var fromClock = ScopeClock.UnixNanos() / 1_000_000_000.0;
        var fromFramework = DateTimeOffset.UtcNow.ToUnixTimeMilliseconds() / 1000.0;
        Assert.InRange(fromClock - fromFramework, -2, 2);
    }
}
