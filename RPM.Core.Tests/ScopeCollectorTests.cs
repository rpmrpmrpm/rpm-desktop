using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// The collector's ring is the app's ring. There is one format and two producers, so anything that
/// can read one can read the other, and neither the viewer nor the chain has to know which half a
/// record came from. The collector itself is not tested here (it needs a live process to attach to);
/// what is pinned is the part the two halves have to agree about.
/// </summary>
[Collection(ScopeCollection.Name)]
public class ScopeCollectorTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scopecol-" + Guid.NewGuid().ToString("N"));

    public ScopeCollectorTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    [Fact]
    public void TwoRingsShareOneNameTableAndOneClock()
    {
        using var names = ScopeNames.Open(Path.Combine(_dir, "scope.names"));

        using (var app = ScopeRing.Create(Path.Combine(_dir, "scope.ring"), 64))
        using (var collector = ScopeRing.Create(Path.Combine(_dir, "scope.collector.ring"), 64))
        {
            app.Write(new ScopeRecord
            {
                Nanos = ScopeClock.Nanos(),
                Kind = (ushort)ScopeKind.GateHold,
                NameId = names.Id("GetAlbumCloudRollup"),
            });

            collector.Write(new ScopeRecord
            {
                Nanos = ScopeClock.Nanos(),
                Kind = (ushort)ScopeKind.GcSuspend,
                NameId = names.Id("Induced"),
            });
        }

        using var readApp = ScopeReader.Open(Path.Combine(_dir, "scope.ring"));
        using var readCollector = ScopeReader.Open(Path.Combine(_dir, "scope.collector.ring"));

        var a = new ScopeRecord[8];
        var b = new ScopeRecord[8];
        Assert.Equal(1, readApp.Read(0, a));
        Assert.Equal(1, readCollector.Read(0, b));

        // The clock belongs to neither, so the two are ordered against each other without anything
        // being negotiated between them.
        Assert.True(b[0].Nanos >= a[0].Nanos);
        Assert.Equal("GetAlbumCloudRollup", names.Name(a[0].NameId));
        Assert.Equal("Induced", names.Name(b[0].NameId));
    }

    [Fact]
    public void ARingLeftByAPreviousRunIsNotReadAsThisRun()
    {
        var path = Path.Combine(_dir, "scope.ring");

        using (var first = ScopeRing.Create(path, 64))
            for (var i = 0; i < 10; i++)
                first.Write(new ScopeRecord { Kind = (ushort)ScopeKind.Input });

        // Create truncates: a cursor describing records this run never wrote is worse than no ring,
        // because nothing downstream could tell the two apart.
        using (var second = ScopeRing.Create(path, 64))
            second.Write(new ScopeRecord { Kind = (ushort)ScopeKind.Input });

        using var reader = ScopeReader.Open(path);
        Assert.Equal(1, reader.Written);
    }
}
