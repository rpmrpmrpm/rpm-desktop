using RPM.Core.Diagnostics;
using RPM.Core.Library;

namespace RPM.Core.Tests;

/// <summary>
/// <c>Gate</c> computes its timing numbers and writes them to the console from whichever thread is
/// slow. These tests check that the same notes also reach the ring, and that the existing readers of
/// those notes still work.
/// </summary>
[Collection(ScopeCollection.Name)]
public class GateScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "gatescope-" + Guid.NewGuid().ToString("N"));

    public GateScopeTests()
    {
        Directory.CreateDirectory(_dir);
        Scope.StartForTest(_dir);
    }

    public void Dispose()
    {
        Scope.StopForTest();
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    private List<ScopeRecord> Drain()
    {
        using var reader = ScopeReader.Open(Path.Combine(_dir, "scope.ring"));
        var into = new ScopeRecord[256];
        var got = reader.Read(0, into);
        return into.Take(got).ToList();
    }

    [Fact]
    public void AHoldWorthNotingReachesTheRing()
    {
        var gate = new Gate("read");

        using (gate.Take("SlowThing"))
            Thread.Sleep(150);   // over Gate's hundred-millisecond threshold

        var hold = Assert.Single(Drain().Where(r => r.Kind == (ushort)ScopeKind.GateHold));
        Assert.Equal("SlowThing", Scope.Names!.Name(hold.NameId));
        Assert.InRange(hold.A / 1_000_000.0, 100, 2000);
    }

    [Fact]
    public void AWaitReachesTheRingNamingWhoItWasBehind()
    {
        var gate = new Gate("write");
        using var inside = new ManualResetEventSlim();
        using var letGo = new ManualResetEventSlim();

        var holder = new Thread(() =>
        {
            using (gate.Take("Holder"))
            {
                inside.Set();
                letGo.Wait();
            }
        });
        holder.Start();
        inside.Wait();

        var waiter = new Thread(() =>
        {
            using (gate.Take("Waiter")) { }
        });
        waiter.Start();
        Thread.Sleep(200);
        letGo.Set();
        waiter.Join();
        holder.Join();

        var wait = Assert.Single(Drain().Where(r => r.Kind == (ushort)ScopeKind.GateWait));
        Assert.Equal("Waiter", Scope.Names!.Name(wait.NameId));
        Assert.Equal("Holder", Scope.Names!.Name(wait.Detail));
    }

    [Fact]
    public void TheConsoleLineIsStillWrittenAndDrainStillWorks()
    {
        // The ring is a SECOND sink, not a replacement: WallDiag's stall line reads Gate.Line() and
        // must keep doing so until something else reads the ring instead.
        var gate = new Gate("read");
        using (gate.Take("StillCounted"))
            Thread.Sleep(150);

        Assert.Contains("StillCounted", Gate.Line());
    }
}
