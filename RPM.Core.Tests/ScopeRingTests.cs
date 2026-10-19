using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

[Collection(ScopeCollection.Name)]
public class ScopeRingTests : IDisposable
{
    private readonly string _dir = Path.Combine(Path.GetTempPath(), "scope-" + Guid.NewGuid().ToString("N"));

    public ScopeRingTests() => Directory.CreateDirectory(_dir);

    public void Dispose()
    {
        try { Directory.Delete(_dir, recursive: true); } catch { /* a temp directory is not worth failing over */ }
        GC.SuppressFinalize(this);
    }

    private string Path0 => Path.Combine(_dir, "scope.ring");

    private static ScopeRecord Rec(ScopeKind kind, long jobId) =>
        new() { Kind = (ushort)kind, JobId = jobId, Nanos = ScopeClock.Nanos() };

    [Fact]
    public void WhatGoesInComesOut()
    {
        using (var ring = ScopeRing.Create(Path0, capacity: 64))
        {
            for (var i = 1; i <= 5; i++)
                ring.Write(Rec(ScopeKind.JobStart, i));
        }

        using var reader = ScopeReader.Open(Path0);
        var into = new ScopeRecord[16];
        var got = reader.Read(0, into);

        Assert.Equal(5, got);
        Assert.Equal(new long[] { 1, 2, 3, 4, 5 }, into.Take(5).Select(r => r.JobId));
        Assert.All(into.Take(5), r => Assert.Equal((ushort)ScopeKind.JobStart, r.Kind));
    }

    [Fact]
    public void OldestIsOverwrittenAndTheReaderIsToldHowManyItMissed()
    {
        using (var ring = ScopeRing.Create(Path0, capacity: 8))
        {
            for (var i = 1; i <= 20; i++)
                ring.Write(Rec(ScopeKind.JobStart, i));
        }

        using var reader = ScopeReader.Open(Path0);
        Assert.Equal(20, reader.Written);
        Assert.Equal(12, reader.Oldest);   // 20 written, 8 slots, so 0..11 are gone

        var into = new ScopeRecord[8];
        var got = reader.Read(0, into);    // asked from the beginning, which no longer exists
        Assert.Equal(8, got);
        Assert.Equal(12, reader.Dropped);
        Assert.Equal(new long[] { 13, 14, 15, 16, 17, 18, 19, 20 }, into.Select(r => r.JobId));
    }

    [Fact]
    public void ReadingResumesWhereItStopped()
    {
        using var ring = ScopeRing.Create(Path0, capacity: 64);
        using var reader = ScopeReader.Open(Path0);
        var into = new ScopeRecord[64];

        ring.Write(Rec(ScopeKind.JobStart, 1));
        Assert.Equal(1, reader.Read(0, into));

        ring.Write(Rec(ScopeKind.JobStart, 2));
        ring.Write(Rec(ScopeKind.JobStart, 3));
        Assert.Equal(2, reader.Read(1, into));
        Assert.Equal(2, into[0].JobId);
        Assert.Equal(3, into[1].JobId);

        Assert.Equal(0, reader.Read(3, into));   // nothing new
    }

    [Fact]
    public void ARingWithNoReaderIsStillAFile()
    {
        using (var ring = ScopeRing.Create(Path0, capacity: 64))
            ring.Write(Rec(ScopeKind.Input, 0));

        Assert.True(File.Exists(Path0));
        Assert.Equal(128 + 64 * 64, new FileInfo(Path0).Length);
    }

    [Fact]
    public void CapacityIsRoundedUpToAPowerOfTwo()
    {
        using var ring = ScopeRing.Create(Path0, capacity: 100);
        Assert.Equal(128, ring.Capacity);
    }

    [Fact]
    public void AReaderSpinningWhileAWriterWrapsNeverSeesHalfARecord()
    {
        // The one that matters. Every record carries its own job id in three places; a torn read
        // shows up as a record whose fields disagree with each other.
        using var ring = ScopeRing.Create(Path0, capacity: 256);
        using var reader = ScopeReader.Open(Path0);

        var stop = false;
        var torn = 0;
        var seen = 0;

        var readerThread = new Thread(() =>
        {
            var into = new ScopeRecord[256];
            var from = 0L;
            while (!Volatile.Read(ref stop))
            {
                var got = reader.Read(from, into);
                for (var i = 0; i < got; i++)
                {
                    var r = into[i];
                    if (r.JobId != r.A || r.JobId != r.B || r.NameId != (int)(r.JobId & 0xFFFF))
                        Interlocked.Increment(ref torn);
                    Interlocked.Increment(ref seen);
                }
                from = reader.Written;
            }
        });
        readerThread.Start();

        for (long i = 1; i <= 200_000; i++)
        {
            ring.Write(new ScopeRecord
            {
                Kind = (ushort)ScopeKind.JobStart,
                Nanos = ScopeClock.Nanos(),
                JobId = i,
                A = i,
                B = i,
                NameId = (int)(i & 0xFFFF),
            });
        }

        Volatile.Write(ref stop, true);
        readerThread.Join();

        Assert.True(seen > 0, "the reader saw nothing, so this proves nothing");
        Assert.Equal(0, torn);
    }
}
