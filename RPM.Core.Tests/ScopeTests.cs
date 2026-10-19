using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

[Collection(ScopeCollection.Name)]
public class ScopeTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scopef-" + Guid.NewGuid().ToString("N"));

    public ScopeTests()
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
        var into = new ScopeRecord[4096];
        var got = reader.Read(0, into);
        return into.Take(got).ToList();
    }

    [Fact]
    public void AGateNoteBecomesARecordWithBothNames()
    {
        Scope.Gate("read", "held", "GetAlbumCloudRollup", behind: null, ms: 19371, cpuMs: 4);

        var record = Assert.Single(Drain(), r => r.Kind == (ushort)ScopeKind.GateHold);
        Assert.Equal(19_371_000_000, record.A);   // nanoseconds
        Assert.Equal(4_000_000, record.B);
        Assert.Equal("GetAlbumCloudRollup", Scope.Names!.Name(record.NameId));
        Assert.Equal("read", Scope.Names!.Name(record.Detail));
    }

    [Fact]
    public void AWaitRecordsWhoItWasBehind()
    {
        Scope.Gate("write", "waited", "GenreWall", behind: "MirrorPeerCatalog", ms: 24102, cpuMs: 0);

        var record = Assert.Single(Drain(), r => r.Kind == (ushort)ScopeKind.GateWait);
        Assert.Equal("GenreWall", Scope.Names!.Name(record.NameId));
        Assert.Equal("MirrorPeerCatalog", Scope.Names!.Name(record.Detail));
    }

    [Fact]
    public void WorkStartedInsideWorkRecordsItsParent()
    {
        var outer = Scope.NewJobId();
        long inner;

        using (Scope.Under(outer))
        {
            Scope.Say(ScopeKind.JobStart, "OnSharedShelfChanged", jobId: outer);
            inner = Scope.NewJobId();
            using (Scope.Under(inner))
                Scope.Say(ScopeKind.JobStart, "RefreshBadges", jobId: inner);
        }

        var records = Drain();
        var child = Assert.Single(records, r => Scope.Names!.Name(r.NameId) == "RefreshBadges");
        Assert.Equal(outer, child.ParentId);
        Assert.Equal(inner, child.JobId);
    }

    [Fact]
    public async Task ParentageSurvivesAnAwait()
    {
        var outer = Scope.NewJobId();
        using (Scope.Under(outer))
        {
            await Task.Yield();
            await Task.Run(async () =>
            {
                await Task.Delay(5);
                Scope.Say(ScopeKind.JobEnd, "deep");
            });
        }

        var record = Assert.Single(Drain(), r => Scope.Names!.Name(r.NameId) == "deep");
        Assert.Equal(outer, record.ParentId);
    }

    [Fact]
    public void LeavingAJobPutsTheParentBack()
    {
        var outer = Scope.NewJobId();
        using (Scope.Under(outer))
        {
            using (Scope.Under(Scope.NewJobId())) { }
            Scope.Say(ScopeKind.JobEnd, "after");
        }

        var record = Assert.Single(Drain(), r => Scope.Names!.Name(r.NameId) == "after");
        Assert.Equal(outer, record.ParentId);
    }

    [Fact]
    public void SayingSomethingWithTheInstrumentOffIsNotAnError()
    {
        Scope.StopForTest();
        Scope.Gate("read", "held", "nobody", null, 1, 0);   // must not throw
        Scope.Say(ScopeKind.Input, "click");
    }
}
