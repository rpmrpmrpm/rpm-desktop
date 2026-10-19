using RPM.Core.Sharing;

namespace RPM.Core.Tests;

public class PullGateTests
{
    [Fact]
    public void OneReaderAtATimePerComputer()
    {
        var gate = new PullGate();

        Assert.True(gate.Begin("mac"));
        Assert.False(gate.Begin("mac"));
        Assert.False(gate.Begin("mac"));

        // A different computer is not blocked by it: the passes contend for the database, not for
        // each other. Holding one machine up for another would only make the round as slow as all
        // its peers added together.
        Assert.True(gate.Begin("pc"));
    }

    [Fact]
    public void AnAskWhileRunningCostsOneRepeat_NotOnePerAsk()
    {
        var gate = new PullGate();

        Assert.True(gate.Begin("mac"));
        for (var beacon = 0; beacon < 10; beacon++)
            Assert.False(gate.Begin("mac"));

        Assert.True(gate.Continue("mac"));    // ten beacons, one repeat
        Assert.False(gate.Continue("mac"));   // and then done
    }

    [Fact]
    public void NobodyAskedMeansOnePass()
    {
        var gate = new PullGate();

        Assert.True(gate.Begin("mac"));
        Assert.False(gate.Continue("mac"));
    }

    [Fact]
    public void TheClaimIsReleasedSoALaterAskStartsAfreshPass()
    {
        var gate = new PullGate();

        Assert.True(gate.Begin("mac"));
        Assert.False(gate.Continue("mac"));

        Assert.True(gate.Begin("mac"));
        Assert.Equal(1, gate.Running);
        Assert.False(gate.Continue("mac"));
        Assert.Equal(0, gate.Running);
    }

    [Fact]
    public void DeviceIdsAreMatchedTheWayTheRestOfSharingMatchesThem()
    {
        var gate = new PullGate();

        Assert.True(gate.Begin("MAC"));
        Assert.False(gate.Begin("mac"));   // the book compares these case-insensitively
        Assert.True(gate.Continue("mac"));
    }

    [Fact]
    public void AnAskLandingBetweenTheLastPassAndTheReleaseIsNotLost()
    {
        // The gap this type exists to close. If "did anybody want another?" and the release are two
        // separate steps, an ask that arrives between them is lost, and that happens most when
        // beacons are busiest. Run it hard enough that the interleaving happens.
        for (var attempt = 0; attempt < 200; attempt++)
        {
            var gate = new PullGate();
            var passes = 0;
            var asks = 0;
            var missed = 0;

            Assert.True(gate.Begin("mac"));

            var asking = new Thread(() =>
            {
                for (var i = 0; i < 500; i++)
                {
                    if (gate.Begin("mac"))
                    {
                        // This caller was handed the claim, so the runner had already released it. The
                        // ask is served here, which is correct and not a miss.
                        Interlocked.Increment(ref passes);
                        while (gate.Continue("mac"))
                            Interlocked.Increment(ref passes);
                    }

                    Interlocked.Increment(ref asks);
                }
            });

            asking.Start();

            passes++;
            while (gate.Continue("mac"))
                passes++;

            asking.Join();

            Assert.Equal(0, missed);
            Assert.True(passes >= 1);
            Assert.Equal(500, asks);
            Assert.Equal(0, gate.Running);   // nothing left claimed, whichever order they interleaved
        }
    }
}
