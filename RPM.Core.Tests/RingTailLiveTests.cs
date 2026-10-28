using RPM.Core.Crashes;
using RPM.Core.Diagnostics;

namespace RPM.Core.Tests;

/// <summary>
/// Reading the running app's own ring, for a report a user sends while the app is up. The crash
/// harvest reads rings of runs that have finished; this reads one that is still being written, and
/// must never make a report fail because of what it finds.
/// </summary>
[Collection(ScopeCollection.Name)]
public class RingTailLiveTests : IDisposable
{
    private readonly string _rings = Path.Combine(Path.GetTempPath(), $"live-ring-{Guid.NewGuid():N}");

    public RingTailLiveTests() => Directory.CreateDirectory(_rings);

    public void Dispose()
    {
        try { Directory.Delete(_rings, recursive: true); } catch { }
    }

    /// <summary>Writes lines into a ring and leaves it open, which is the state the running app's own
    /// ring is in when a report is written.</summary>
    private (ScopeNames Names, ScopeRing Ring) Open(int pid)
    {
        var names = ScopeNames.Fresh(Path.Combine(_rings, $"scope.{pid}.names"));
        var ring = ScopeRing.Create(Path.Combine(_rings, $"scope.{pid}.ring"), 4096);
        return (names, ring);
    }

    private static void Say(ScopeNames names, ScopeRing ring, string channel, string line) =>
        ring.Write(new ScopeRecord
        {
            Kind = (ushort)ScopeKind.Said,
            Nanos = ScopeClock.Nanos(),
            NameId = names.Id(channel),
            Detail = names.Id(line),
        });

    [Fact]
    public void A_ring_that_is_still_being_written_reads_back_its_lines()
    {
        var (names, ring) = Open(4242);
        using (names)
        using (ring)
        {
            Say(names, ring, "boot", "app ctor done");
            Say(names, ring, "library", "first load finished");

            var activity = RingTail.ActivityForThisRun(_rings, 4242);

            Assert.NotNull(activity);
            Assert.Contains("app ctor done", activity);
            Assert.Contains("first load finished", activity);
        }
    }

    /// <summary>A report with no activity log is still a report. Every way the ring can be unreadable
    /// answers null rather than throwing, because the user has already pressed Send.</summary>
    [Fact]
    public void A_run_with_no_ring_has_no_activity()
    {
        Assert.Null(RingTail.ActivityForThisRun(_rings, 5555));
    }

    [Fact]
    public void A_ring_of_zero_bytes_has_no_activity()
    {
        File.WriteAllBytes(Path.Combine(_rings, "scope.5556.ring"), []);

        Assert.Null(RingTail.ActivityForThisRun(_rings, 5556));
    }

    [Fact]
    public void A_ring_that_is_not_a_ring_has_no_activity()
    {
        File.WriteAllText(Path.Combine(_rings, "scope.5557.ring"), "this is not a ring at all");

        Assert.Null(RingTail.ActivityForThisRun(_rings, 5557));
    }

    /// <summary>A ring with nothing a person can read is the same as no ring: the report carries no
    /// activity rather than an empty block.</summary>
    [Fact]
    public void A_ring_with_no_readable_records_has_no_activity()
    {
        var (names, ring) = Open(4243);
        using (names)
        using (ring)
        {
            ring.Write(new ScopeRecord { Kind = (ushort)ScopeKind.ClockSync, Nanos = ScopeClock.Nanos() });

            Assert.Null(RingTail.ActivityForThisRun(_rings, 4243));
        }
    }

    /// <summary>The lines nearest the problem are the ones at the end, so a text over the cap keeps
    /// its last characters.</summary>
    [Fact]
    public void A_long_activity_log_keeps_its_last_characters()
    {
        var (names, ring) = Open(4244);
        using (names)
        using (ring)
        {
            // Each line is about 60 characters with its time stamp, so this is well over the cap.
            for (var i = 0; i < 2000; i++)
                Say(names, ring, "load", $"row {i} of the library came in with a long enough detail to count");
            Say(names, ring, "load", "the very last thing that happened");

            var activity = RingTail.ActivityForThisRun(_rings, 4244);

            Assert.NotNull(activity);
            Assert.True(activity.Length <= RingTail.MaxActivityChars);
            Assert.EndsWith("the very last thing that happened", activity);
        }
    }

    /// <summary>The ring holds a fixed number of records. A run that has said more than it holds
    /// reports the most recent ones, not a failure.</summary>
    [Fact]
    public void A_ring_that_has_wrapped_reports_what_is_left_in_it()
    {
        var (names, ring) = Open(4245);
        using (names)
        using (ring)
        {
            for (var i = 0; i < 5000; i++)
                Say(names, ring, "load", $"row {i}");

            var activity = RingTail.ActivityForThisRun(_rings, 4245);

            Assert.NotNull(activity);
            Assert.EndsWith("row 4999", activity);
            Assert.DoesNotContain("row 0\n", activity);
        }
    }
}
