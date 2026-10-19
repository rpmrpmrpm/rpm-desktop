using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// A run that begins to end normally says so on its ring, and the ring's header says when the run
/// began. The crash harvest reads both after the process has gone.
/// </summary>
[Collection(ScopeCollection.Name)]
public class ScopeClosingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), $"scope-closing-{Guid.NewGuid():N}");

    public ScopeClosingTests() => Scope.StartForTest(_dir);

    public void Dispose()
    {
        Scope.StopForTest();
        try { Directory.Delete(_dir, recursive: true); } catch { }
    }

    [Fact]
    public void Closing_is_written_as_its_own_kind()
    {
        Scope.Closing();

        using var reader = ScopeReader.Open(Path.Combine(_dir, "scope.ring"));
        var records = new ScopeRecord[64];
        var count = reader.Read(0, records);

        Assert.Contains(records[..count], r => r.Kind == (ushort)ScopeKind.Closing);
    }

    [Fact]
    public void The_start_time_is_the_same_from_the_writer_and_from_a_reader()
    {
        using var reader = ScopeReader.Open(Path.Combine(_dir, "scope.ring"));

        Assert.True(Scope.StartedUnixNanos > 0);
        Assert.Equal(Scope.StartedUnixNanos, reader.StartUnixNanos);
        Assert.True(reader.StartMonoNanos > 0);
    }

    [Fact]
    public void Without_a_ring_the_start_time_is_zero()
    {
        Scope.StopForTest();

        Assert.Equal(0, Scope.StartedUnixNanos);
    }
}
