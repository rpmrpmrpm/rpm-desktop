using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

[Collection(ScopeCollection.Name)]
public class ScopeNamesTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scopenames-" + Guid.NewGuid().ToString("N"));

    public ScopeNamesTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    private string Path0 => Path.Combine(_dir, "scope.names");

    [Fact]
    public void TheSameNameIsTheSameId()
    {
        using var names = ScopeNames.Open(Path0);
        var a = names.Id("RefreshBadges");
        var b = names.Id("RefreshBadges");
        var c = names.Id("CloudBadgeOf");

        Assert.Equal(a, b);
        Assert.NotEqual(a, c);
        Assert.True(a > 0, "zero means no name, so no name may be given it");
    }

    [Fact]
    public void ANullOrEmptyNameIsNoName()
    {
        using var names = ScopeNames.Open(Path0);
        Assert.Equal(0, names.Id(null));
        Assert.Equal(0, names.Id(""));
        Assert.Null(names.Name(0));
    }

    [Fact]
    public void ALateReaderResolvesEveryNameItSees()
    {
        int id;
        using (var writing = ScopeNames.Open(Path0))
        {
            writing.Id("first");
            id = writing.Id("GetAlbumCloudRollup");
            writing.Id("last");
        }

        using var reading = ScopeNames.Open(Path0);
        Assert.Equal("GetAlbumCloudRollup", reading.Name(id));
        Assert.Equal(3, reading.All().Count);

        // And a reopened table returns the same id and does not append a duplicate.
        Assert.Equal(id, reading.Id("GetAlbumCloudRollup"));
    }

    [Fact]
    public void AFreshTableKeepsNothingFromTheRunBefore()
    {
        // The table's lifetime is the ring's. A fresh ring holds no record that refers to an old id,
        // and an id means nothing except relative to the table that issued it. A table carried over
        // from a run that shared its file with another writer would hold two producers' words mixed
        // together, and `rpmscope throws` would name exception types after a performance counter.
        using (var first = ScopeNames.Open(Path0))
        {
            first.Id("from the run before");
            first.Id("and another");
        }

        using var second = ScopeNames.Fresh(Path0);
        Assert.Empty(second.All());
        Assert.Equal(1, second.Id("the first name of this run"));
        Assert.Null(second.Name(2));
    }

    [Fact]
    public void ItSurvivesTwoThreadsNamingAtOnce()
    {
        using var names = ScopeNames.Open(Path0);
        var ids = new System.Collections.Concurrent.ConcurrentBag<int>();

        Parallel.For(0, 8, _ =>
        {
            for (var i = 0; i < 200; i++)
                ids.Add(names.Id("name-" + (i % 50)));
        });

        Assert.Equal(50, ids.Distinct().Count());
        Assert.Equal(50, names.All().Count);
    }
}
