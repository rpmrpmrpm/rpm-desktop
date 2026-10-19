using RPM.Desktop.Services.Shell;

namespace RPM.Desktop.Tests;

/// <summary>
/// The condition the splash waits for, besides a free drawing thread: whether anything is on screen.
///
/// <para>This is a latch, not a flag. Once it is set it stays set. The gate reads it on every pulse, from a
/// different thread than the one that sets it. Neither side may depend on arriving first.</para>
/// </summary>
public class FirstScreenTests
{
    [Fact]
    public void NothingIsUpUntilSomethingSaysSo()
    {
        var screen = new FirstScreen();

        Assert.False(screen.IsUp);
        Assert.Null(screen.UpAt);
    }

    [Fact]
    public void ScreenStaysUpOnceItIsUp()
    {
        var screen = new FirstScreen();

        screen.Up();
        Assert.True(screen.IsUp);
        Assert.NotNull(screen.UpAt);

        // The home screen calls this on every way out of its reading, so a run with a second reading
        // reaches it more than once. There is no way back down. A reveal that has opened cannot be closed
        // again, and a gate that is still waiting must not be made to wait again.
        screen.Up();
        Assert.True(screen.IsUp);
    }

    /// <summary>The time kept is the time of the first call. The reveal's patience is counted from it. A
    /// later call that moved it forward would delay a gate that is already waiting.</summary>
    [Fact]
    public void TheMomentKeptIsTheFirstOne()
    {
        var screen = new FirstScreen();

        screen.Up();
        var first = screen.UpAt;

        for (var i = 0; i < 1000; i++)
            screen.Up();

        Assert.Equal(first, screen.UpAt);
    }
}
